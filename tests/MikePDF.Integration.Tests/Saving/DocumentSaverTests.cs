#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Xml.Linq;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using static MikePDF.Integration.Tests.Saving.SaveCoordinatorTests;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// What the shell calls (work package W3-save-app): <see cref="DocumentSaver"/> deciding the mode and turning a result into what a person is told, the model's news that the tab marker hangs on, the progress
/// the strip shows, Overwrite, Save As switching the tab's file, and the "changed on disk" check. SP-02, SP-21, SP-23, SP-25, SP-27, SP-32 at the level the shell works.
/// </summary>
public sealed class DocumentSaverTests
{
    private static DocumentSaver SaverFor(Rig rig) => new(rig.Coordinator);

    private static SaveJob JobFor(Rig rig, SaveMode mode = SaveMode.Save, string? target = null, bool overwrite = false, bool encrypted = false, IProgress<SaveProgressStage>? progress = null) => new()
    {
        Session = rig.Session,
        DocumentPath = rig.Doc,
        PageCount = rig.PageCount,
        Held = rig.Held,
        Mode = mode,
        TargetPath = target,
        OverwriteChangedFile = overwrite,
        PasswordProtected = encrypted,
        Progress = progress,
    };

    private static int Annotations(byte[] bytes, int page = 0) => InProcessSaveHost.Count(bytes, Rig.PagesOf(bytes), [(uint)page])![0];

    private sealed class Steps : IProgress<SaveProgressStage>
    {
        public List<SaveProgressStage> Seen { get; } = [];

        public void Report(SaveProgressStage value) => Seen.Add(value);
    }

    // ---- the marker, the title and the Save command follow the model (SP-02, SP-35) ----------------------------------------------------------------------

    [Fact]
    public async Task TheModelTellsTheShellWhenItBecomesDirtyWhenASaveBeginsAndWhenItIsClean()
    {
        using var rig = new Rig();
        var dirty = new List<(bool Dirty, bool Saving)>();
        rig.Session.Model.Changed += () => dirty.Add((rig.Session.Model.IsDirty, rig.Session.Model.IsSaving));

        rig.Session.Model.Apply(Commands.Highlight());
        var flow = await SaverFor(rig).RunAsync(JobFor(rig));

        Assert.True(flow.IsSaved);
        Assert.Equal([(true, false), (true, true), (false, false)], dirty); // edited; the save begins (the document is read-only); saved and clean
        Assert.False(rig.Session.Model.IsDirty);
        Assert.True(flow.FileChanged);
        Assert.Null(flow.Notice);
    }

    [Fact]
    public async Task EditsAreBlockedWhileASaveRunsAndAllowedAgainAfterIt()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var gate = new TaskCompletionSource();
        rig.Host.Gate = gate.Task;

        var running = SaverFor(rig).RunAsync(JobFor(rig));
        Assert.True(rig.Session.Model.IsSaving);
        Assert.Throws<EditsBlockedException>(() => rig.Session.Model.Apply(Commands.Note()));
        gate.SetResult();
        var flow = await running;

        Assert.True(flow.IsSaved);
        Assert.False(rig.Session.Model.IsSaving);
        rig.Session.Model.Apply(Commands.Note()); // allowed again
        Assert.True(rig.Session.Model.IsDirty);
    }

    [Fact]
    public async Task TheStripIsToldWhereTheSaveIsInOrder()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var steps = new Steps();

        await SaverFor(rig).RunAsync(JobFor(rig, progress: steps));

        Assert.Equal([SaveProgressStage.Saving, SaveProgressStage.Checking, SaveProgressStage.Replacing], steps.Seen);
    }

    [Fact]
    public async Task ACancelBeforeTheReplaceLeavesTheFileAndTheEditsAndSaysSo()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        using var cancel = new CancellationTokenSource();
        rig.Host.Gate = Task.Run(() => cancel.Cancel()); // the person pressed Cancel while the host was working

        var flow = await SaverFor(rig).RunAsync(JobFor(rig), cancel.Token);

        Assert.Equal((SaveOutcome.Failed, SaveReason.Cancelled), (flow.Result.Outcome, flow.Result.Reason));
        Assert.Equal("Save_Cancelled", flow.Notice!.BodyKey);
        Assert.Equal(SaveNoticeSeverity.Informational, flow.Notice.Severity);
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.True(rig.Session.Model.IsDirty);
        Assert.False(rig.Session.Model.IsSaving);
    }

    // ---- Save As (SP-25) --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsSwitchesTheTabToTheNewFileTheOriginalIsUntouchedAndTheNextEditStartsAJournalForTheNewFile()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var copy = rig.Workspace.Combine("docs\\copy.pdf");

        var flow = await SaverFor(rig).RunAsync(JobFor(rig, SaveMode.SaveAs, copy));

        Assert.True(flow.IsSaved);
        Assert.True(flow.SwitchedToTarget);
        Assert.False(flow.FileChanged); // the document's own file was not written
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(1, Annotations(File.ReadAllBytes(copy)));
        Assert.False(rig.Session.Model.IsDirty); // the edits are in the file the tab shows now
        Assert.Equal(copy, rig.Session.DocumentPath);
        Assert.Empty(rig.Store.List()); // the old journal is gone and none exists for the new file yet

        rig.Session.Model.Apply(Commands.Note());
        Assert.False(rig.Store.Exists(rig.Doc));
        Assert.True(rig.Store.Exists(copy));
    }

    [Fact]
    public async Task SaveAsToTheDocumentsOwnPathIsASave()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());

        var flow = await SaverFor(rig).RunAsync(JobFor(rig, SaveMode.SaveAs, rig.Doc));

        Assert.True(flow.IsSaved);
        Assert.True(flow.FileChanged);
        Assert.False(flow.SwitchedToTarget);
        Assert.Equal(rig.Doc, rig.Session.DocumentPath);
        Assert.Equal(1, Annotations(File.ReadAllBytes(rig.Doc)));
    }

    [Fact]
    public async Task ASaveAsOfADocumentWithNoEditsMakesAnIdenticalCopyAndSwitchesToIt()
    {
        using var rig = new Rig();
        var copy = rig.Workspace.Combine("docs\\copy.pdf");

        var flow = await SaverFor(rig).RunAsync(JobFor(rig, SaveMode.SaveAs, copy));

        Assert.True(flow.IsSaved);
        Assert.True(flow.SwitchedToTarget);
        Assert.Equal(rig.Original, File.ReadAllBytes(copy));
        Assert.Equal(copy, rig.Session.DocumentPath);
    }

    [Fact]
    public async Task AFlattenedCopyLeavesTheWorkingDocumentItsEditsAndItsJournalAlone()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var flat = rig.Workspace.Combine("docs\\flat.pdf");

        var flow = await SaverFor(rig).RunAsync(JobFor(rig, SaveMode.FlattenedCopy, flat));

        Assert.True(flow.IsSaved);
        Assert.False(flow.SwitchedToTarget);
        Assert.False(flow.FileChanged);
        Assert.True(rig.Session.Model.IsDirty);
        Assert.Equal(rig.Doc, rig.Session.DocumentPath);
        Assert.True(rig.Store.Exists(rig.Doc));
        Assert.Equal(0, Annotations(File.ReadAllBytes(flat)));
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
    }

    // ---- what is known to be impossible is refused before anything is made (SP-23) ----------------------------------------------------------------------

    [Fact]
    public async Task ADocumentOpenedWithAPasswordIsRefusedWithoutAJournalRecordOrATemporaryFileAndNoCopyIsOffered()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());

        var flow = await SaverFor(rig).RunAsync(JobFor(rig, encrypted: true));

        Assert.Equal((SaveOutcome.Refused, SaveReason.PasswordProtected), (flow.Result.Outcome, flow.Result.Reason));
        Assert.Empty(rig.Host.Calls); // the writer host was never asked
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.True(rig.Session.Model.IsDirty);
        Assert.Equal("Save_Encrypted", flow.Notice!.BodyKey);
        Assert.DoesNotContain(SaveAction.SaveAs, flow.Notice.Actions); // a copy would be refused for the same reason
        var journal = rig.Store.Read(rig.Store.PathFor(rig.Doc));
        Assert.DoesNotContain(journal.Entries, static e => e is SaveIntentEntry);
    }

    // ---- the file changed under the document: Overwrite, Save a copy, Cancel (SP-21, SP-39) --------------------------------------------------------------

    [Fact]
    public async Task APathReplacedByAnotherFileAsksFirstAndOverwriteWritesTheEditsOntoWhatThePersonSawNotWhatAnotherProgramPut()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        File.Move(rig.Doc, rig.Workspace.Combine("docs\\aside.pdf"));
        File.Copy(SaveWorkspace.Fixture("hello_world.pdf"), rig.Doc); // another program saved a different file here: a different file ID at the same path
        var theirs = File.ReadAllBytes(rig.Doc);

        var first = await SaverFor(rig).RunAsync(JobFor(rig));

        Assert.True(first.NeedsConfirmation);
        Assert.Equal(SaveReason.ChangedOnDisk, first.Result.Reason);
        Assert.Equal(theirs, File.ReadAllBytes(rig.Doc)); // nothing was written
        Assert.Equal([SaveAction.Overwrite, SaveAction.SaveAs], first.Notice!.Actions);
        Assert.True(rig.Session.Model.IsDirty);

        var second = await SaverFor(rig).RunAsync(JobFor(rig, overwrite: true));

        Assert.True(second.IsSaved);
        var saved = File.ReadAllBytes(rig.Doc);
        Assert.True(saved.AsSpan(0, rig.Original.Length).SequenceEqual(rig.Original)); // the bytes the edits were made against, plus the update
        Assert.Equal(1, Annotations(saved));
        Assert.False(rig.Session.Model.IsDirty);
    }

    [Fact]
    public async Task ADeletedPathIsReportedWithSaveAsAndNothingIsWritten()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        File.Delete(rig.Doc);

        var flow = await SaverFor(rig).RunAsync(JobFor(rig));

        Assert.Equal(SaveReason.TargetGone, flow.Result.Reason);
        Assert.Equal([SaveAction.SaveAs], flow.Notice!.Actions);
        Assert.Equal([], rig.Workspace.Listing());
        Assert.True(rig.Session.Model.IsDirty);
    }

    // ---- the words (SP-23, SP-35, save-pipeline 6) ----------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Resources
    {
        get
        {
            var root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "MikePDF.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("MikePDF.slnx not found");
            }

            var document = XDocument.Load(Path.Combine(root, "src", "MikePDF.App", "Strings", "en-US", "Resources.resw"));
            return document.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void EveryReasonASaveCanGiveHasWordsAndEveryKeyThePresentationUsesIsInTheResources()
    {
        var resources = Resources;
        foreach (var key in SavePresentation.ResourceKeys)
        {
            Assert.True(resources.ContainsKey(key), key);
        }

        foreach (var reason in Enum.GetValues<SaveReason>().Where(static r => r is not (SaveReason.None or SaveReason.NothingToSave)))
        {
            var notice = SavePresentation.Describe(new SaveResult(SaveOutcome.Failed, reason, "x") { OriginalAt = @"C:\docs\.a.pdf.mikepdf-1234abcd.bak" });

            Assert.NotNull(notice);
            Assert.True(resources.ContainsKey(notice.TitleKey), reason + " title " + notice.TitleKey);
            Assert.True(resources.ContainsKey(notice.BodyKey), reason + " body " + notice.BodyKey);
            Assert.DoesNotContain(@":\", notice.DetailCode ?? string.Empty, StringComparison.Ordinal); // Details never carry a path
        }
    }

    [Theory]
    [InlineData(SaveReason.ReadOnly)]
    [InlineData(SaveReason.HardLinks)]
    [InlineData(SaveReason.EncryptedFile)]
    [InlineData(SaveReason.DirectoryNotWritable)]
    [InlineData(SaveReason.TargetGone)]
    [InlineData(SaveReason.InUse)]
    [InlineData(SaveReason.NoSpace)]
    [InlineData(SaveReason.ValidationFailed)]
    [InlineData(SaveReason.HostFailed)]
    [InlineData(SaveReason.Io)]
    public void EveryReasonThatBlocksSavingInPlaceOffersASaveAs(SaveReason reason)
    {
        var notice = SavePresentation.Describe(new SaveResult(reason is SaveReason.ReadOnly or SaveReason.HardLinks or SaveReason.EncryptedFile or SaveReason.DirectoryNotWritable or SaveReason.TargetGone ? SaveOutcome.Refused : SaveOutcome.Failed, reason));

        Assert.Contains(SaveAction.SaveAs, notice!.Actions);
    }

    [Fact]
    public void ARestorePendingSaysWhereTheOriginalIsAndOffersToShowIt()
    {
        var original = @"C:\docs\.contract.pdf.mikepdf-1234abcd.bak";

        var notice = SavePresentation.Describe(new SaveResult(SaveOutcome.Failed, SaveReason.RestorePending, "win32-1177") { OriginalAt = original })!;

        Assert.Equal(SaveNoticeSeverity.Error, notice.Severity);
        Assert.Equal(SaveNoticeArgument.OriginalPath, notice.BodyArgument);
        Assert.Equal([SaveAction.ShowOriginal, SaveAction.Details], notice.Actions);
        Assert.Contains("{0}", Resources[notice.BodyKey], StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedEditsAreKeptAndSaidToBe()
    {
        var notice = SavePresentation.Describe(new SaveResult(SaveOutcome.Refused, SaveReason.UnsupportedEdits, "SetFieldValue"))!;

        Assert.Equal("Save_Unsupported", notice.BodyKey);
        Assert.Contains("kept", Resources[notice.BodyKey], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SaveOutcome.Saved, SaveReason.None)]
    [InlineData(SaveOutcome.NothingToSave, SaveReason.NothingToSave)]
    public void ASavedOrEmptySaveHasNothingToTellExceptByAnnouncement(SaveOutcome outcome, SaveReason reason) => Assert.Null(SavePresentation.Describe(new SaveResult(outcome, reason)));

    // ---- changed on disk (SP-21) ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFileThatIsAsItWasIsUnchangedAndOneAnotherProgramRewroteIsChanged()
    {
        using var workspace = new SaveWorkspace();
        var path = workspace.Copy("hello_world.pdf");
        var before = FileStamp.Take(path)!.Value;

        Assert.Equal(ExternalChange.None, ExternalChanges.Check(before, path));

        File.AppendAllText(path, "\n% another program was here");
        Assert.Equal(ExternalChange.Changed, ExternalChanges.Check(before, path));
    }

    [Fact]
    public void ARewriteOfTheSameLengthWithADifferentTimeIsChanged()
    {
        using var workspace = new SaveWorkspace();
        var path = workspace.Copy("hello_world.pdf");
        var before = FileStamp.Take(path)!.Value;

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        Assert.Equal(ExternalChange.Changed, ExternalChanges.Check(before, path));
    }

    [Fact]
    public void AFileReplacedByAnotherFileIsChangedEvenWithTheSameBytesAndSize()
    {
        using var workspace = new SaveWorkspace();
        var path = workspace.Copy("hello_world.pdf");
        var before = FileStamp.Take(path)!.Value;
        var stamp = File.GetLastWriteTimeUtc(path);
        File.Move(path, workspace.Combine("docs\\aside.pdf"));
        File.Copy(workspace.Combine("docs\\aside.pdf"), path);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(ExternalChange.Changed, ExternalChanges.Check(before, path)); // another file ID at the path
    }

    [Fact]
    public void AMovedOrDeletedFileIsGoneAndAFolderThatCannotBeSeenIsNot()
    {
        using var workspace = new SaveWorkspace();
        var path = workspace.Copy("hello_world.pdf");
        var before = FileStamp.Take(path)!.Value;

        File.Delete(path);
        Assert.Equal(ExternalChange.Gone, ExternalChanges.Check(before, path));

        Directory.Delete(workspace.Documents, recursive: true);
        Assert.Equal(ExternalChange.None, ExternalChanges.Check(before, path)); // the whole folder is away (a share): not "moved or deleted"
    }

    [Fact]
    public async Task AFileTheAppSavedIsNotChangedOnceItsStampIsTakenAgain()
    {
        using var rig = new Rig();
        var before = FileStamp.Take(rig.Doc)!.Value;
        rig.Session.Model.Apply(Commands.Highlight());

        await SaverFor(rig).RunAsync(JobFor(rig));

        Assert.Equal(ExternalChange.Changed, ExternalChanges.Check(before, rig.Doc)); // the save is a change to the path...
        Assert.Equal(ExternalChange.None, ExternalChanges.Check(FileStamp.Take(rig.Doc)!.Value, rig.Doc)); // ...that the shell takes as the new stamp
    }
}
