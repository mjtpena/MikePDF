using MikePDF.Core.Commands;
using MikePDF.Core.Windowing;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Editing.Recovery;

namespace MikePDF.Core.Tests;

/// <summary>
/// The parts of wiring the save core into the app that are plain code (work package W3-save-app): the commands and their enabled state, the model's change news that the tab marker hangs on, the
/// journal re-keyed by Save As, and the rows and words of the recovery dialog. SP-02, SP-25 (the journal part), SP-29 (the dialog part), SP-35 (the command state).
/// </summary>
public sealed class SaveAppTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static CommandContext Open(bool edits = false, bool saving = false) => new() { HasDocument = true, HasPath = true, HasEdits = edits, SaveRunning = saving };

    // ---- the commands (SP-35: dirty state is in command state, not by a dot alone) ---------------------------------------------------------------------------

    [Fact]
    public void SaveNeedsADocumentOnDiskAndUnsavedEditsAndNoSaveRunning()
    {
        var save = CommandCatalog.ById["file.save"];

        Assert.Equal(Reasons.NoDocument, save.CanExecute(CommandContext.Empty).Reason);
        Assert.Equal(Reasons.NoPath, save.CanExecute(new CommandContext { HasDocument = true, HasEdits = true }).Reason);
        Assert.Equal(Reasons.NoEdits, save.CanExecute(Open()).Reason);
        Assert.Equal(Reasons.SaveBusy, save.CanExecute(Open(edits: true, saving: true)).Reason);
        Assert.True(save.CanExecute(Open(edits: true)).IsEnabled);
    }

    [Theory]
    [InlineData("file.saveAs")]
    [InlineData("file.saveFlattenedCopy")]
    public void ACopyCanBeSavedWithOrWithoutEditsButNotWhileASaveRuns(string id)
    {
        var command = CommandCatalog.ById[id];

        Assert.False(command.CanExecute(CommandContext.Empty).IsEnabled);
        Assert.True(command.CanExecute(Open()).IsEnabled);
        Assert.True(command.CanExecute(Open(edits: true)).IsEnabled);
        Assert.Equal(Reasons.SaveBusy, command.CanExecute(Open(edits: true, saving: true)).Reason);
    }

    [Fact]
    public void TheDefaultChordsAreTheSpecifiedOnesAndTheFlattenedCopyHasNone()
    {
        Assert.Equal(["Ctrl+S"], CommandCatalog.ById["file.save"].DefaultChords.Select(static c => c.ToString()));
        Assert.Equal(["Ctrl+Shift+S"], CommandCatalog.ById["file.saveAs"].DefaultChords.Select(static c => c.ToString()));
        Assert.Empty(CommandCatalog.ById["file.saveFlattenedCopy"].DefaultChords);
    }

    [Theory]
    [InlineData(Reasons.NoEdits)]
    [InlineData(Reasons.SaveBusy)]
    public void EveryNewDisabledReasonHasWords(string reason) => Assert.True(ResourceText.Has(Reasons.ResourceKey(reason)));

    [Fact]
    public void EveryNewCommandHasANameAndKeywordsInTheResources()
    {
        foreach (var id in new[] { "file.save", "file.saveAs", "file.saveFlattenedCopy" })
        {
            var command = CommandCatalog.ById[id];
            Assert.True(ResourceText.Has(command.NameKey), command.NameKey);
            Assert.True(ResourceText.Has(command.NameKey.Replace("_Name", "_Keywords", StringComparison.Ordinal)), id + " keywords");
        }
    }

    // ---- the model's news (the marker on the tab, the title, the Save button) ---------------------------------------------------------------------------------------

    [Fact]
    public void TheModelSaysWhenAnythingThatChangesTheDirtyStateHappened()
    {
        var model = new EditModel();
        var news = 0;
        model.Changed += () => news++;

        model.Apply(Edits.Add(name: "a"));
        model.Apply(Edits.Add(name: "b"));
        Assert.Equal(2, news);

        model.Undo();
        model.Redo();
        Assert.Equal(4, news);

        var snapshot = model.BeginSave();
        Assert.Equal(5, news); // the document is read-only now: the Save command goes off
        model.MarkSaved(snapshot);
        Assert.Equal(6, news);
        Assert.False(model.IsDirty);

        model.Apply(Edits.Add(name: "c"));
        var again = model.BeginSave();
        model.AbortSave();
        Assert.Equal(9, news);
        Assert.True(model.IsDirty);
        Assert.Single(again.Pending);
        model.AbortSave(); // nothing was running: no news
        Assert.Equal(9, news);
    }

    [Fact]
    public void ACommandThatCannotBeJournalledSaysNothingBecauseNothingChanged()
    {
        var model = new EditModel(new RefusingJournal());
        var news = 0;
        model.Changed += () => news++;

        Assert.Throws<JournalUnavailableException>(() => model.Apply(Edits.Add()));

        Assert.Equal(0, news);
        Assert.False(model.IsDirty);
    }

    private sealed class RefusingJournal : IEditJournal
    {
        public long Command(EditCommand command) => throw new JournalUnavailableException("no");

        public void Undo(long target)
        {
        }

        public void Redo(long target)
        {
        }
    }

    // ---- Save As re-keys the journal (SP-25) -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AfterSaveAsTheNextCommandStartsAJournalForTheNewFileAndTheOldOneStaysGone()
    {
        var store = new JournalStore(_scratch.Combine("journals"));
        var original = _scratch.Make("a.pdf", new byte[3000]);
        var copy = _scratch.Make("b.pdf", new byte[3500]);
        using var session = new EditSession(store, original, "1.0.0.0");
        session.Model.Apply(Edits.Add(name: "a"));
        Assert.True(store.Exists(original));

        session.ClearJournal(); // what a successful save does after the replace
        session.Rekey(copy);
        session.Model.Apply(Edits.Add(name: "b"));

        Assert.False(store.Exists(original));
        Assert.True(store.Exists(copy));
        session.Dispose(); // the live journal is open exclusively
        var read = store.Read(store.PathFor(copy));
        Assert.Equal(Path.GetFullPath(copy), read.Header!.DocPath);
        Assert.Equal(JournalKey.Fingerprint(copy), read.Header.Fingerprint); // against the saved file, as save-pipeline 3.7 says
        Assert.Equal(copy, session.DocumentPath);
    }

    [Fact]
    public void RekeyingASessionWithNoStoreChangesNothing()
    {
        using var session = new EditSession(null);

        session.Rekey(@"C:\x\b.pdf");

        Assert.Null(session.DocumentPath);
    }

    // ---- the recovery dialog (SP-29, design 14 2.7) ----------------------------------------------------------------------------------------------------------

    private static RecoveryCandidate Candidate(string? path, RecoveryAction action, int markup = 1, int forms = 0, int pages = 0, JournalReadStatus status = JournalReadStatus.Ok, DateTimeOffset? last = null) =>
        new(@"C:\j\k.jsonl", path, action, new JournalReadResult(status, null, [], 0, 0, null), new RecoverySummary(markup, forms, pages, last), new InterruptedSaveReport(InterruptedSave.None, []));

    [Fact]
    public void ADocumentWhoseFileIsAsTheEditsWereMadeAgainstIsCheckedAndOneThatChangedIsNot()
    {
        var ready = RecoveryPrompt.Row(Candidate(@"C:\docs\Contract v3.pdf", RecoveryAction.Replay));
        var changed = RecoveryPrompt.Row(Candidate(@"C:\docs\Permit.pdf", RecoveryAction.WarnFileChanged));
        var missing = RecoveryPrompt.Row(Candidate(@"C:\docs\Lecture 4.pdf", RecoveryAction.FileMissing));
        var unreadable = RecoveryPrompt.Row(Candidate(null, RecoveryAction.Unreadable));

        Assert.Equal(("Contract v3.pdf", RecoveryRowState.Ready, true, true), (ready.Name, ready.State, ready.CanRestore, ready.DefaultChecked));
        Assert.Equal((RecoveryRowState.FileChanged, true, false), (changed.State, changed.CanRestore, changed.DefaultChecked)); // may be restored, but not by default
        Assert.Equal((RecoveryRowState.FileMissing, false, false), (missing.State, missing.CanRestore, missing.DefaultChecked));
        Assert.Equal((string.Empty, RecoveryRowState.Unreadable, false), (unreadable.Name, unreadable.State, unreadable.CanRestore));
    }

    [Fact]
    public void TheRowsNameFilesButNeverFoldersAndTheMostRecentComesFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = RecoveryPrompt.Rows(
        [
            Candidate(@"C:\secret folder\old.pdf", RecoveryAction.Replay, last: now.AddHours(-5)),
            Candidate(@"C:\secret folder\new.pdf", RecoveryAction.Replay, last: now),
        ]);

        Assert.Equal(["new.pdf", "old.pdf"], rows.Select(static r => r.Name));
        Assert.DoesNotContain(rows, static r => r.Name.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void ADamagedMiddleRecordIsSaidToBePartial()
    {
        Assert.True(RecoveryPrompt.Row(Candidate(@"C:\a.pdf", RecoveryAction.Replay, status: JournalReadStatus.Corrupt)).Partial);
        Assert.False(RecoveryPrompt.Row(Candidate(@"C:\a.pdf", RecoveryAction.Replay, status: JournalReadStatus.TornTail)).Partial); // a torn last line is normal and silent
    }

    [Fact]
    public void TheSummaryCountsWhatWasAddedInWordsAndSaysWhenTheLastChangeWas()
    {
        var last = new DateTimeOffset(2026, 11, 4, 10, 31, 0, TimeSpan.Zero);

        var text = RecoveryPrompt.Summary(new RecoverySummary(8, 3, 1, last), ResourceText.Get);

        Assert.StartsWith("8 markup changes, 3 form changes, 1 page change. Last change ", text, StringComparison.Ordinal);
        Assert.Equal("1 markup change", RecoveryPrompt.Counts(new RecoverySummary(1, 0, 0, null), ResourceText.Get));
        Assert.Equal("2 page changes", RecoveryPrompt.Summary(new RecoverySummary(0, 0, 2, null), ResourceText.Get));
    }

    [Fact]
    public void EveryStringTheRecoveryDialogUsesIsInTheResources()
    {
        foreach (var key in RecoveryPrompt.ResourceKeys)
        {
            Assert.True(ResourceText.Has(key), key);
        }
    }

    // ---- the suggested name of a copy (SaveMode=AlwaysSaveAsCopy) ---------------------------------------------------------------------------------------

    [Fact]
    public void ACopySuggestsTheEditedSuffixOnceAndNeverTwice()
    {
        var format = ResourceText.Get("Save_SuggestedName");

        var first = SaveNames.WithSuffix("Annual report", format);
        var second = SaveNames.WithSuffix(first, format);

        Assert.Equal("Annual report (edited)", first);
        Assert.Equal(first, second); // saving a copy of the copy does not give "(edited) (edited)"
        Assert.Equal("Annual report (EDITED)", SaveNames.WithSuffix("Annual report (EDITED)", format)); // case does not matter to a file name
    }

    [Fact]
    public void ANameThatOnlyMentionsTheSuffixInTheMiddleStillGetsIt()
    {
        var format = ResourceText.Get("Save_SuggestedName");

        Assert.Equal("Notes (edited) v2 (edited)", SaveNames.WithSuffix("Notes (edited) v2", format));
    }

    [Fact]
    public void TheFlattenedSuffixIsKeptOnceToo()
    {
        var format = ResourceText.Get("Save_SuggestedFlatName");

        var flat = SaveNames.WithSuffix("Form", format);

        Assert.Equal("Form (flattened)", flat);
        Assert.Equal(flat, SaveNames.WithSuffix(flat, format));
    }

    [Theory]
    [InlineData("{0} (copy)", "a (copy)", "a (copy)")]
    [InlineData("{0} (copy)", "a", "a (copy)")]
    [InlineData("Copy of {0}", "Copy of a", "Copy of Copy of a")] // a prefix format has no suffix to look for: applied as it is
    [InlineData("{0}", "a", "a")]
    public void AnyFormatTheTranslationsUseIsApplied(string format, string name, string expected) => Assert.Equal(expected, SaveNames.WithSuffix(name, format));

    // ---- one dialog after a crash, not two ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreAction.AskThenRestore, true, RestoreAction.Restore)] // the box was left checked: no second question
    [InlineData(RestoreAction.AskThenRestore, false, RestoreAction.StartFresh)] // unchecked: start fresh, no second question
    [InlineData(RestoreAction.AskThenRestore, null, RestoreAction.AskThenRestore)] // no recovery dialog was shown (no journal): the session question is asked on its own, as before
    [InlineData(RestoreAction.Restore, false, RestoreAction.Restore)] // the setting says always: nothing was asked, so there is no answer to obey
    [InlineData(RestoreAction.StartFresh, true, RestoreAction.StartFresh)]
    [InlineData(RestoreAction.StartFresh, null, RestoreAction.StartFresh)]
    public void TheSessionQuestionRidesInTheRecoveryDialogWhenThereIsOne(RestoreAction decided, bool? answered, RestoreAction expected) =>
        Assert.Equal(expected, SessionPolicy.AfterRecovery(decided, answered));

    [Fact]
    public void WithRestoreSetToNeverTheSessionIsNotAskedAboutButUnsavedWorkStillIs()
    {
        var unclean = new SessionFile(false, DateTimeOffset.UtcNow, 0, [new SessionWindow(new StoredBounds(0, 0, 800, 600, 96, "d"), false, 0, false, "thumbnails", [new SessionTab(TabKind.Document, @"C:.pdf", null, null, TabViewState.Start, true)])]);

        Assert.Equal(RestoreAction.StartFresh, SessionPolicy.Decide(RestoreMode.Never, unclean)); // no session question, so none rides anywhere
        Assert.Equal(RestoreAction.AskThenRestore, SessionPolicy.Decide(RestoreMode.Ask, unclean)); // and with Ask it is the one that rides in the recovery dialog
        // The recovery dialog itself does not consult the setting: PromptStartupRecoveryAsync takes no restore mode (MainWindow.Recovery.cs).
    }
}
