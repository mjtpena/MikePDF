using System.Security.Cryptography;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;

namespace MikePDF.Core.Tests;

/// <summary>SP-06, SP-07 and SP-29 (save-pipeline 14), and the interrupted-save cases of 3.5 item 5.</summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string Document(string name = "a.pdf", int size = 2000)
    {
        var data = new byte[size];
        new Random(name.GetHashCode(StringComparison.Ordinal)).NextBytes(data);
        return _scratch.Make(name, data);
    }

    private JournalStore Store() => new(_scratch.Combine("journals"));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (EditSession Session, string Journal) Start(JournalStore store, string document)
    {
        var writer = store.Create(document, JournalStore.HeaderFor(document, "1.0.0.0"));
        return (new EditSession(writer), store.PathFor(document));
    }

    [Fact]
    public void ReplayRebuildsTheModelAndTheUndoAndRedoStacksForAScriptedSequence()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        var model = session.Model;
        model.Apply(Edits.Add(name: "a"));
        model.Apply(Edits.Add(name: "b"));
        model.Apply(Edits.Add(name: "c"));
        model.Undo(); // c
        model.Undo(); // b
        model.Redo(); // b
        model.Apply(Edits.Add(name: "d")); // clears the redo stack
        model.Undo(); // d
        var expected = model.PendingCommands.Select(static c => c.Payload.GetProperty("name").GetString()).ToList();
        session.Dispose();

        var read = store.Read(journalPath);
        var rebuilt = new EditModel();
        var report = RecoveryService.Replay(read, rebuilt, canApply: null);

        Assert.Equal(["a", "b"], expected);
        Assert.Equal(expected, rebuilt.PendingCommands.Select(static c => c.Payload.GetProperty("name").GetString()));
        Assert.Equal(4, report.Commands);
        Assert.Equal(0, report.Skipped);
        Assert.True(rebuilt.CanUndo);
        Assert.True(rebuilt.CanRedo); // d can be redone
        Assert.Equal(UndoOutcome.Done, rebuilt.Redo());
        Assert.Equal("d", rebuilt.PendingCommands[^1].Payload.GetProperty("name").GetString());
        Assert.Equal(UndoOutcome.Done, rebuilt.Undo());
        Assert.Equal(UndoOutcome.Done, rebuilt.Undo());
        Assert.Equal(UndoOutcome.Done, rebuilt.Undo());
        Assert.False(rebuilt.IsDirty);
        Assert.Equal(UndoOutcome.None, rebuilt.Undo());
    }

    [Fact]
    public void CommandsThatNoLongerFitAreSkippedAndCounted()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add(page: 0, name: "a"));
        session.Model.Apply(Edits.Add(page: 9, name: "gone")); // a page that no longer exists
        session.Model.Apply(Edits.Add(page: 1, name: "c"));
        session.Dispose();

        var model = new EditModel();
        var report = RecoveryService.Replay(store.Read(journalPath), model, c => c.Payload.GetProperty("page").GetInt32() < 5);

        Assert.Equal((3, 2, 1), (report.Commands, report.Applied, report.Skipped));
        Assert.Equal(2, model.PendingCount);
    }

    [Fact]
    public void AnUndoWhoseCommandWasSkippedIsSkippedToo()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add(page: 9, name: "gone"));
        session.Model.Undo();
        session.Dispose();

        var model = new EditModel();
        var report = RecoveryService.Replay(store.Read(journalPath), model, _ => false);

        Assert.Equal(2, report.Skipped); // the command, and the undo that named it
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void TheSummaryCountsChangesByKindAndNamesTheLastOne()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add(name: "a"));
        session.Model.Apply(Edits.Add(name: "b"));
        session.Model.Apply(Edits.Field());
        session.Model.Apply(Edits.Rotate());
        session.Dispose();

        var summary = RecoveryService.Summarise(store.Read(journalPath));

        Assert.Equal((2, 1, 1, 4), (summary.AnnotationChanges, summary.FormChanges, summary.PageChanges, summary.Total));
        Assert.NotNull(summary.LastChange);
    }

    [Fact]
    public void ARecoveredSessionContinuesTheSameJournalAfterATornLine()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add(name: "a"));
        session.Model.Apply(Edits.Add(name: "b"));
        session.Dispose();
        var whole = File.ReadAllBytes(journalPath);
        File.WriteAllBytes(journalPath, whole[..^15]); // the crash cut the last record

        var service = new RecoveryService(store);
        var candidate = Assert.Single(service.Scan());
        var (restored, report) = service.Restore(candidate);
        restored.Model.Apply(Edits.Add(name: "c"));
        restored.Dispose();

        Assert.Equal(RecoveryAction.Replay, candidate.Action);
        Assert.Equal(JournalReadStatus.TornTail, candidate.Read.Status);
        Assert.Equal(1, report.Applied);
        var after = store.Read(journalPath);
        Assert.Equal(JournalReadStatus.Ok, after.Status); // the torn half-record was cut away before the new one was appended
        Assert.Equal(["a", "c"], after.Entries.OfType<CommandEntry>().Select(static c => c.Command.Payload.GetProperty("name").GetString()));
    }

    [Fact]
    public void ACorruptMiddleStillOffersTheValidPrefix()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add(name: "a"));
        session.Model.Apply(Edits.Add(name: "b"));
        session.Model.Apply(Edits.Add(name: "c"));
        session.Dispose();
        var bytes = File.ReadAllBytes(journalPath);
        var lines = Array.IndexOf(bytes, (byte)'\n');
        var second = Array.IndexOf(bytes, (byte)'\n', lines + 1) + 1;
        bytes[second + 8] ^= 0x20;
        File.WriteAllBytes(journalPath, bytes);

        var candidate = Assert.Single(new RecoveryService(store).Scan());

        Assert.True(candidate.PartiallyReadable);
        Assert.Equal(1, candidate.Summary.Total);
    }

    [Fact]
    public void AFileThatEqualsASaveBeginOutputMeansTheSaveFinishedAndTheJournalIsClearedSilently()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add());
        var saved = Enumerable.Range(0, 3000).Select(static i => (byte)(i * 7)).ToArray();
        session.Journal!.SaveIntent(@"C:\elsewhere\.a.pdf.mikepdf-00000000.tmp", null, document);
        session.Journal.SaveBegin(Sha(saved), saved.Length);
        session.Dispose();
        File.WriteAllBytes(document, saved); // the replace happened; the crash came before the cleanup

        var found = new RecoveryService(store).Scan();

        Assert.Empty(found);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void AFileThatMatchesTheHeaderFingerprintIsReplayed()
    {
        var store = Store();
        var document = Document();
        var (session, _) = Start(store, document);
        session.Model.Apply(Edits.Add());
        session.Dispose();

        var candidate = Assert.Single(new RecoveryService(store).Scan());

        Assert.Equal(RecoveryAction.Replay, candidate.Action);
        Assert.Equal(document, candidate.DocumentPath);
    }

    [Fact]
    public void AFileThatMatchesNeitherIsAWarningAndAMissingFileIsAnError()
    {
        var store = Store();
        var changed = Document("changed.pdf");
        var missing = Document("missing.pdf");
        foreach (var path in new[] { changed, missing })
        {
            var (session, _) = Start(store, path);
            session.Model.Apply(Edits.Add());
            session.Dispose();
        }

        File.AppendAllText(changed, "someone else edited this");
        File.Delete(missing);

        var found = new RecoveryService(store).Scan().ToDictionary(static c => Path.GetFileName(c.DocumentPath!));

        Assert.Equal(RecoveryAction.WarnFileChanged, found["changed.pdf"].Action);
        Assert.Equal(RecoveryAction.FileMissing, found["missing.pdf"].Action);
    }

    [Fact]
    public void AnEmptyJournalIsRemovedAndAnUnreadableOneIsOfferedForDiscard()
    {
        var store = Store();
        File.WriteAllBytes(Path.Combine(store.Directory, new string('1', 32) + ".jsonl"), []);
        var bad = Path.Combine(store.Directory, new string('2', 32) + ".jsonl");
        File.WriteAllText(bad, "garbage that is not a journal\n");

        var service = new RecoveryService(store);
        var found = Assert.Single(service.Scan());

        Assert.Equal(RecoveryAction.Unreadable, found.Action);
        Assert.False(File.Exists(Path.Combine(store.Directory, new string('1', 32) + ".jsonl")));
        service.Discard(found);
        Assert.False(File.Exists(bad));
    }

    [Fact]
    public void ADocumentWhoseEditsWereAllUndoneHasNothingToRestore()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add());
        session.Model.Undo();
        session.Dispose();

        Assert.Empty(new RecoveryService(store).Scan());
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void ClearAllRemovesEveryJournalAndItsBlobsAndNothingElse()
    {
        var store = Store();
        var a = Document("a.pdf");
        var b = Document("b.pdf");
        Start(store, a).Session.Dispose();
        Start(store, b).Session.Dispose();
        Directory.CreateDirectory(store.BlobDirectoryFor(a));
        File.WriteAllText(Path.Combine(store.BlobDirectoryFor(a), "x"), "blob");
        File.WriteAllText(Path.Combine(store.Directory, "keep.txt"), "not ours");

        Assert.Equal(2, store.ClearAll());

        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.BlobDirectoryFor(a)));
        Assert.True(File.Exists(Path.Combine(store.Directory, "keep.txt")));
        Assert.Equal(0, store.Budget.Used);
    }

    // ---- interrupted saves (3.5 item 5) ----

    private sealed record Interrupted(string Document, string Temp, string Backup, byte[] Old, byte[] New, JournalReadResult Journal);

    private Interrupted Arrange(bool begin, string? documentName = null, string? target = null, bool backupInIntent = true)
    {
        var name = documentName ?? "a.pdf";
        var document = Document(name, 4000);
        var old = File.ReadAllBytes(document);
        var next = new byte[5000];
        new Random(99).NextBytes(next);
        var saveTarget = target ?? document;
        var temp = Path.Combine(Path.GetDirectoryName(saveTarget)!, SaveRecovery.TempNameFor(Path.GetFileName(saveTarget), "0a1b2c3d"));
        var backup = Path.Combine(Path.GetDirectoryName(saveTarget)!, SaveRecovery.BackupNameFor(Path.GetFileName(saveTarget), "0a1b2c3d"));
        var store = Store();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add());
        session.Journal!.SaveIntent(temp, backupInIntent ? backup : null, saveTarget);
        if (begin)
        {
            session.Journal.SaveBegin(Sha(next), next.Length);
        }

        session.Dispose();
        return new Interrupted(document, temp, backup, old, next, store.Read(journalPath));
    }

    [Fact]
    public void ASaveThatDiedBeforeItsOutputWasCompleteLosesOnlyItsTemporaryFile()
    {
        var s = Arrange(begin: false);
        File.WriteAllBytes(s.Temp, s.New[..100]);
        File.SetAttributes(s.Temp, FileAttributes.Hidden);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.RolledBack, report.Outcome);
        Assert.False(File.Exists(s.Temp));
        Assert.Equal(s.Old, File.ReadAllBytes(s.Document));
    }

    [Fact]
    public void ASaveThatDiedBeforeItsTemporaryFileWasCreatedHasNothingToRemove()
    {
        var s = Arrange(begin: false);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.RolledBack, report.Outcome);
        Assert.Empty(report.RemovedFiles);
    }

    [Fact]
    public void ASaveThatDiedAfterValidationButBeforeTheReplaceRollsBackAndKeepsTheOldFile()
    {
        var s = Arrange(begin: true);
        File.WriteAllBytes(s.Temp, s.New);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.RolledBack, report.Outcome);
        Assert.False(File.Exists(s.Temp));
        Assert.Equal(s.Old, File.ReadAllBytes(s.Document));
    }

    [Fact]
    public void ASaveThatDiedAfterTheReplaceIsRecognisedByTheHashAndCleanedUp()
    {
        var s = Arrange(begin: true);
        File.WriteAllBytes(s.Document, s.New);
        File.WriteAllBytes(s.Backup, s.Old);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.AlreadySaved, report.Outcome);
        Assert.False(File.Exists(s.Backup));
        Assert.Equal(s.New, File.ReadAllBytes(s.Document));
    }

    [Fact]
    public void AReplaceFileFallbackKilledBetweenItsRenamesGetsItsTargetBack()
    {
        var s = Arrange(begin: true);
        File.Move(s.Document, s.Backup); // ReplaceFileW had renamed the original away
        File.WriteAllBytes(s.Temp, s.New); // and had not yet moved the new file in

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.TargetRestored, report.Outcome);
        Assert.Equal(s.Old, File.ReadAllBytes(s.Document));
        Assert.False(File.Exists(s.Backup));
        Assert.False(File.Exists(s.Temp));
    }

    [Fact]
    public void ATargetThatIsMissingWithNoBackupButTheCompleteOutputIsRolledForward()
    {
        var s = Arrange(begin: true, backupInIntent: false);
        File.Delete(s.Document);
        File.WriteAllBytes(s.Temp, s.New);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.RolledForward, report.Outcome);
        Assert.Equal(s.New, File.ReadAllBytes(s.Document));
        Assert.False(File.Exists(s.Temp));
    }

    [Fact]
    public void ATargetThatIsMissingWithNothingToRestoreItFromDeletesNothing()
    {
        var s = Arrange(begin: false);
        File.Delete(s.Document);
        File.WriteAllBytes(s.Temp, s.New[..50]);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.TargetMissing, report.Outcome);
        Assert.True(File.Exists(s.Temp)); // it may be all there is
    }

    [Fact]
    public void AnOldFileLeftByReplaceFileWithoutABackupNameIsFoundByItsPattern()
    {
        var s = Arrange(begin: true, backupInIntent: false);
        var leftover = s.Document + "~RF1a2b3c4d.TMP";
        File.Move(s.Document, leftover);
        File.WriteAllBytes(s.Temp, s.New);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.TargetRestored, report.Outcome);
        Assert.Equal(s.Old, File.ReadAllBytes(s.Document));
    }

    [Fact]
    public void ASaveAsThatDiedLeavesNoFileAtTheNewNameAndTheOriginalAlone()
    {
        var folder = _scratch.Combine("elsewhere");
        Directory.CreateDirectory(folder);
        var newTarget = Path.Combine(folder, "copy.pdf");
        var s = Arrange(begin: true, target: newTarget);
        File.WriteAllBytes(s.Temp, s.New);

        var report = SaveRecovery.Run(s.Journal);

        Assert.Equal(InterruptedSave.RolledBack, report.Outcome);
        Assert.False(File.Exists(newTarget));
        Assert.False(File.Exists(s.Temp));
        Assert.Equal(s.Old, File.ReadAllBytes(s.Document));
    }

    [Fact]
    public void ADamagedJournalCannotMakeRecoveryDeleteAFileThatIsNotAPipelineFile()
    {
        var s = Arrange(begin: false);
        var victim = _scratch.Make("important.docx", [1, 2, 3]);
        var elsewhere = _scratch.Make(".a.pdf.mikepdf-0a1b2c3d.tmp.keep", [4]);
        var forged = new JournalReadResult(JournalReadStatus.Ok, s.Journal.Header, [new SaveIntentEntry(1, DateTimeOffset.UtcNow, victim, elsewhere, s.Document)], 0, 0, null);

        var report = SaveRecovery.Run(forged);

        Assert.Empty(report.RemovedFiles);
        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void RecoveryWillNotTouchATemporaryFileInAnotherDirectory()
    {
        var s = Arrange(begin: false);
        var other = _scratch.Combine("other");
        Directory.CreateDirectory(other);
        var sameNameElsewhere = Path.Combine(other, Path.GetFileName(s.Temp));
        File.WriteAllBytes(sameNameElsewhere, [1]);
        var forged = new JournalReadResult(JournalReadStatus.Ok, s.Journal.Header, [new SaveIntentEntry(1, DateTimeOffset.UtcNow, sameNameElsewhere, null, s.Document)], 0, 0, null);

        SaveRecovery.Run(forged);

        Assert.True(File.Exists(sameNameElsewhere));
    }

    [Fact]
    public void ASaveThatIsAlreadyCommittedIsNotRolledBackAgain()
    {
        var store = Store();
        var document = Document();
        var (session, journalPath) = Start(store, document);
        session.Model.Apply(Edits.Add());
        var temp = Path.Combine(Path.GetDirectoryName(document)!, SaveRecovery.TempNameFor("a.pdf", "0a1b2c3d"));
        session.Journal!.SaveIntent(temp, null, document);
        session.Journal.SaveBegin(new string('c', 64), 1);
        session.Journal.SaveCommitted();
        session.Dispose();
        File.WriteAllBytes(temp, [1]); // not ours any more: the commit said the save is over

        var report = SaveRecovery.Run(store.Read(journalPath));

        Assert.Equal(InterruptedSave.None, report.Outcome);
        Assert.True(File.Exists(temp));
    }
}
