#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;
using static MikePDF.Integration.Tests.Saving.SaveCoordinatorTests;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// The flows the shell strings together, end to end below the window (work package W3-save-app): edits applied through the model and saved by <see cref="DocumentSaver"/> through the real sandboxed writer host and a
/// fresh sandboxed engine, then validated; a crash and the restore that follows; "Don't save". SP-25, SP-27, SP-28, SP-29 and SP-30 (journey 3) as far as they can be checked without a window.
/// </summary>
public sealed class SaveAppFlowTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private static DocumentSaver SaverOver(WriterHostClient host, SandboxFixture sandbox) =>
        new(new SaveCoordinator(new WriterHostSaveHost(host), new EngineSaveCheck(() => sandbox.Options()), new FileReplacer(5, TimeSpan.FromMilliseconds(50))));

    private static int Annotations(byte[] bytes, int page = 0) => InProcessSaveHost.Count(bytes, Rig.PagesOf(bytes), [(uint)page])![0];

    // ---- journey 3 below the window: highlight, note, save, reopen: intact -------------------------------------------------------------------------------

    [Fact]
    public async Task EditsAppliedThroughTheModelAreSavedByTheSandboxedHostAndTheFileReopensInAFreshEngineWithThemIntact()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(doc);
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = new EditSession(new JournalStore(workspace.Journals), doc, "1.0.0.0");
        session.Model.Apply(Commands.Highlight("a"));
        session.Model.Apply(Commands.Note("b", page: 1));
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var stages = new List<SaveProgressStage>();

        var flow = await SaverOver(host, sandbox).RunAsync(
            new SaveJob { Session = session, DocumentPath = doc, PageCount = 2, Held = held, Mode = SaveMode.Save, Progress = new ListProgress(stages) },
            new CancellationTokenSource(Wait).Token);

        Assert.True(flow.IsSaved, flow.Result.Reason + " " + flow.Result.Detail);
        Assert.Null(flow.Notice);
        var saved = File.ReadAllBytes(doc);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Equal(1, Annotations(saved, 0));
        Assert.Equal(1, Annotations(saved, 1));
        Assert.True(Soundness.NoNewErrors(saved, original));
        Assert.False(session.Model.IsDirty);
        Assert.False(new JournalStore(workspace.Journals).Exists(doc)); // deleted after the replace, not before
        Assert.Equal(["doc.pdf"], workspace.Listing());
        Assert.Equal([SaveProgressStage.Saving, SaveProgressStage.Checking, SaveProgressStage.Replacing], stages);

        await using var engine = await sandbox.StartAsync();
        var reopened = await engine.OpenDocumentAsync(doc);
        Assert.Equal(Status.Ok, reopened.Status);
        await using (reopened.Value!)
        {
            Assert.Equal(2u, reopened.Value!.PageCount);
        }
    }

    private sealed class ListProgress(List<SaveProgressStage> into) : IProgress<SaveProgressStage>
    {
        public void Report(SaveProgressStage value) => into.Add(value);
    }

    [Fact]
    public async Task SaveAsThroughTheSandboxedHostWritesTheCopyLeavesTheOriginalAndTheTabKeepsEditing()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(doc);
        var copy = workspace.Combine("docs\\copy.pdf");
        var store = new JournalStore(workspace.Journals);
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = new EditSession(store, doc, "1.0.0.0");
        session.Model.Apply(Commands.Highlight("a"));
        var saver = SaverOver(host, sandbox);

        var as1 = await saver.RunAsync(new SaveJob { Session = session, DocumentPath = doc, PageCount = 2, Mode = SaveMode.SaveAs, TargetPath = copy }, new CancellationTokenSource(Wait).Token);

        Assert.True(as1.IsSaved && as1.SwitchedToTarget, as1.Result.Reason + " " + as1.Result.Detail);
        Assert.Equal(original, File.ReadAllBytes(doc));
        Assert.Equal(1, Annotations(File.ReadAllBytes(copy)));
        Assert.False(session.Model.IsDirty);
        Assert.Empty(store.List());

        // The tab now shows the copy: the next edit is journalled against it, and saving it appends to the copy.
        session.Model.Apply(Commands.Note("b"));
        Assert.True(store.Exists(copy));
        using var heldCopy = File.OpenHandle(copy, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var second = await saver.RunAsync(new SaveJob { Session = session, DocumentPath = copy, PageCount = 2, Held = heldCopy, Mode = SaveMode.Save }, new CancellationTokenSource(Wait).Token);

        Assert.True(second.IsSaved, second.Result.Reason + " " + second.Result.Detail);
        Assert.Equal(2, Annotations(File.ReadAllBytes(copy)));
        Assert.Equal(original, File.ReadAllBytes(doc));
        Assert.Empty(store.List());
    }

    // ---- journey 8 below the window: the app dies with unsaved edits; the next start offers them back (SP-28, SP-29) -----------------------------------------

    [Fact]
    public async Task AfterACrashTheJournalIsOfferedWithAnAccurateSummaryRestoredAsDirtyAndSavedFromThere()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(doc);
        var store = new JournalStore(workspace.Journals);
        var session = new EditSession(store, doc, "1.0.0.0");
        session.Model.Apply(Commands.Highlight("a"));
        session.Model.Apply(Commands.Note("b"));
        session.Model.Apply(Commands.Note("c", page: 1));
        session.Model.Undo(); // the last note was undone before the crash
        session.Dispose(); // the process dies: nothing saved, nothing cleared

        var recovery = new RecoveryService(store);
        var found = Assert.Single(recovery.Scan());
        var row = RecoveryPrompt.Row(found);

        Assert.Equal(("doc.pdf", RecoveryRowState.Ready, true), (row.Name, row.State, row.DefaultChecked));
        Assert.Equal(2, found.Summary.AnnotationChanges);
        Assert.Equal("2 markup changes", RecoveryPrompt.Counts(found.Summary, key => key switch
        {
            "Recover_Join" => ", ",
            "Recover_Part_Markup_Other" => "{0} markup changes",
            _ => key,
        }));

        var (restored, report) = recovery.Restore(found);
        using (restored)
        {
            Assert.Equal(3, report.Commands);
            Assert.Equal(3, report.Applied);
            Assert.True(restored.Model.IsDirty); // nothing is saved by recovery
            Assert.True(restored.Model.CanRedo); // the undo stack came back too

            await using var host = await sandbox.StartWriterHostAsync();
            using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
            var flow = await SaverOver(host, sandbox).RunAsync(new SaveJob { Session = restored, DocumentPath = doc, PageCount = 2, Held = held, Mode = SaveMode.Save }, new CancellationTokenSource(Wait).Token);

            Assert.True(flow.IsSaved, flow.Result.Reason + " " + flow.Result.Detail);
            var saved = File.ReadAllBytes(doc);
            Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
            Assert.Equal(2, Annotations(saved, 0));
            Assert.Equal(0, Annotations(saved, 1)); // the undone note is not in the file
        }

        Assert.Empty(store.List());
    }

    [Fact]
    public void ADocumentWhoseFileChangedSinceTheEditsAreOfferedButNotCheckedAndOneThatWentIsNotOfferedAtAll()
    {
        using var workspace = new SaveWorkspace();
        var changed = workspace.Copy("hello_world_2_pages.pdf", "changed.pdf");
        var gone = workspace.Copy("hello_world_2_pages.pdf", "gone.pdf");
        var store = new JournalStore(workspace.Journals);
        foreach (var path in new[] { changed, gone })
        {
            var session = new EditSession(store, path, "1.0.0.0");
            session.Model.Apply(Commands.Highlight());
            session.Dispose();
        }

        File.AppendAllText(changed, "\n% another program\n");
        File.Delete(gone);

        var rows = RecoveryPrompt.Rows(new RecoveryService(store).Scan());

        Assert.Equal(2, rows.Count);
        var changedRow = rows.Single(r => r.Name == "changed.pdf");
        Assert.Equal((RecoveryRowState.FileChanged, false, true), (changedRow.State, changedRow.DefaultChecked, changedRow.CanRestore));
        var goneRow = rows.Single(r => r.Name == "gone.pdf");
        Assert.Equal((RecoveryRowState.FileMissing, false), (goneRow.State, goneRow.CanRestore));
    }

    [Fact]
    public void AJournalAnotherInstanceHasOpenIsLeftAloneAndNotOffered()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var store = new JournalStore(workspace.Journals);
        using var live = new EditSession(store, doc, "1.0.0.0"); // another instance of the app is editing this document right now
        live.Model.Apply(Commands.Highlight());

        var candidates = new RecoveryService(new JournalStore(workspace.Journals)).Scan();

        Assert.Empty(candidates);
        Assert.True(store.Exists(doc)); // not deleted, not offered for discard
        Assert.True(live.Model.IsDirty);
    }

    // ---- "Don't save" ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DontSaveDeletesTheJournalAndLeavesTheFile()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(doc);
        var store = new JournalStore(workspace.Journals);
        var session = new EditSession(store, doc, "1.0.0.0");
        session.Model.Apply(Commands.Highlight());
        Assert.True(store.Exists(doc));

        session.ClearJournal(); // what closing the tab with "Don't save" does
        session.Dispose();

        Assert.False(store.Exists(doc));
        Assert.Empty(new RecoveryService(store).Scan());
        Assert.Equal(original, File.ReadAllBytes(doc));
    }

    [Fact]
    public void AWindowThatClosesBecauseWindowsEndsTheSessionKeepsTheJournalForTheNextStart()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var store = new JournalStore(workspace.Journals);
        var session = new EditSession(store, doc, "1.0.0.0");
        session.Model.Apply(Commands.Highlight());

        session.Dispose(); // released without a discard: the journal is flushed already (TAB-12)

        Assert.True(store.Exists(doc));
        var found = Assert.Single(new RecoveryService(store).Scan());
        Assert.Equal(RecoveryAction.Replay, found.Action);
    }

    // ---- "Changes can't be backed up right now" (OQ-SP-1, degraded by default) -----------------------------------------------------------------------------

    [Fact]
    public void AJournalThatCannotBeStartedLeavesEditingOnAndSaysChangesCannotBeBackedUp()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var store = new JournalStore(workspace.Journals);
        using var first = new EditSession(store, doc, "1.0.0.0");
        first.Model.Apply(Commands.Highlight("x")); // this journal is open and exclusive
        using var second = new EditSession(store, doc, "1.0.0.0"); // a second session for the same document cannot start its own

        second.Model.Apply(Commands.Highlight("y"));

        Assert.True(second.Model.IsDirty); // the command applied
        Assert.False(second.BackedUp); // and the chip says it is not safe
        Assert.True(first.BackedUp);
    }

    [Fact]
    public void ASessionWhoseJournalFolderCannotBeMadeStartsUnbacked()
    {
        using var session = new EditSession(null);

        session.MarkBackupUnavailable();
        session.Model.Apply(Commands.Highlight());

        Assert.False(session.BackedUp);
        Assert.True(session.Model.IsDirty);
    }
}
