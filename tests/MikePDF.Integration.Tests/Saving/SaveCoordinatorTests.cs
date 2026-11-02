using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;
using MikePDF.PdfWriter;
using MikePDF.PdfWriter.Editing;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// The save pipeline end to end on a real NTFS volume, with the writer host's operations run in this process (the sandboxed host has its own
/// tests): SP-01, SP-14, SP-15, SP-18 to SP-27, SP-31, SP-32 and the refusals of R-39 (save-pipeline 14).
/// </summary>
public sealed class SaveCoordinatorTests
{
    internal sealed class Rig : IDisposable
    {
        public Rig(string fixture = "hello_world_2_pages.pdf", ISaveFaults? faults = null, IReplaceNative? native = null)
        {
            Workspace = new SaveWorkspace();
            Doc = Workspace.Copy(fixture);
            Original = File.ReadAllBytes(Doc);
            PageCount = PagesOf(Original);
            Store = new JournalStore(Workspace.Journals);
            Session = new EditSession(Store, Doc, "1.0.0.0");
            Held = File.OpenHandle(Doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
            var replacer = native is null ? new FileReplacer(5, TimeSpan.FromMilliseconds(5)) : new FileReplacer(native, 5, TimeSpan.FromMilliseconds(5));
            Coordinator = new SaveCoordinator(Host, Engine, replacer, faults);
        }

        public SaveWorkspace Workspace { get; }

        public string Doc { get; }

        public byte[] Original { get; }

        public int PageCount { get; }

        public JournalStore Store { get; }

        public EditSession Session { get; }

        public SafeFileHandle Held { get; }

        public InProcessSaveHost Host { get; } = new();

        public OkEngineCheck Engine { get; } = new();

        public SaveCoordinator Coordinator { get; }

        public Task<SaveResult> SaveAsync(SaveMode mode = SaveMode.Save, string? target = null, bool rewrite = false, CancellationToken cancellationToken = default) =>
            Coordinator.SaveSessionAsync(Session, Doc, target ?? Doc, mode, PageCount, Held, rewrite, cancellationToken);

        public static int PagesOf(byte[] bytes)
        {
            var opened = StrictDocument.Open(bytes);
            return opened.IsSuccess && PageTree.Enumerate(opened.Value) is { IsSuccess: true } tree ? tree.Value.Count : 1;
        }

        public void Dispose()
        {
            Held.Dispose();
            Session.Dispose();
            Workspace.Dispose();
        }
    }

    private static int Annotations(byte[] bytes, int page = 0)
    {
        var counted = InProcessSaveHost.Count(bytes, PagesOf(bytes), [(uint)page]);
        return counted is null ? -1 : counted[0];
    }

    private static int PagesOf(byte[] bytes) => Rig.PagesOf(bytes);

    private static void AssertUntouched(Rig rig)
    {
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
    }

    // ---- SP-14 ----

    [Fact]
    public async Task ASaveWithNoCommandsWritesNothingAndDoesNotTouchTheFile()
    {
        using var rig = new Rig();
        var stamp = File.GetLastWriteTimeUtc(rig.Doc);
        await Task.Delay(30);

        var result = await rig.SaveAsync();

        Assert.Equal(SaveOutcome.NothingToSave, result.Outcome);
        Assert.Empty(rig.Host.Calls); // no engine save, no writer host call, nothing
        Assert.Equal(0, rig.Engine.Calls);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(rig.Doc));
        AssertUntouched(rig);
        Assert.False(rig.Session.Model.IsSaving);
        Assert.Empty(rig.Store.List()); // not even a journal was started
    }

    // ---- the whole pipeline ----

    [Fact]
    public async Task EditsAreSavedWithTheOriginalAsAPrefixAndTheJournalAndTheTemporaryFilesAreGone()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight("h1"));
        rig.Session.Model.Apply(Commands.Note("n1"));
        Assert.Single(rig.Store.List());

        var result = await rig.SaveAsync();

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(ReplaceMechanism.PosixRename, result.Replace!.Mechanism);
        var saved = File.ReadAllBytes(rig.Doc);
        Assert.True(saved.AsSpan(0, rig.Original.Length).SequenceEqual(rig.Original), "the original bytes are an exact prefix");
        Assert.Equal(2, Annotations(saved));
        Assert.True(Soundness.NoNewErrors(saved, rig.Original));
        Assert.True(StructureValidator.Validate(saved, options: new ValidationOptions { DanglingReferencesAreErrors = true }).IsValid); // this fixture is clean to begin with
        Assert.False(rig.Session.Model.IsDirty);
        Assert.True(rig.Session.Model.CanUndo); // the stack is kept
        Assert.Empty(rig.Store.List());
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.Equal(["append", "validate", "verify"], rig.Host.Calls);
        Assert.Equal(1, rig.Engine.Calls);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(saved)), result.OutputSha256);
        Assert.Equal(saved.Length, result.OutputLength);
    }

    [Fact]
    public async Task TheJournalIsDeletedOnlyAfterTheReplaceAndTheNextEditStartsAFreshJournalAgainstTheSavedFile()
    {
        var faults = new RecordingFaults();
        using var rig = new Rig(faults: faults);
        var journal = new RecordingJournal { Inner = new JournalSaveSink(rig.Session) };
        var targetAtStep = new Dictionary<SaveStep, bool>();
        var clearedAtStep = new Dictionary<SaveStep, bool>();
        faults.Observe = step =>
        {
            targetAtStep[step] = File.ReadAllBytes(rig.Doc).Length > rig.Original.Length;
            clearedAtStep[step] = journal.Events.Contains("clear");
        };
        rig.Session.Model.Apply(Commands.Highlight());
        var snapshot = rig.Session.Model.BeginSave();

        var result = await rig.Coordinator.SaveAsync(
            new SaveRequest { DocumentPath = rig.Doc, TargetPath = rig.Doc, Mode = SaveMode.Save, Snapshot = snapshot, PageCount = rig.PageCount, HeldOriginal = rig.Held },
            journal);
        rig.Session.Model.MarkSaved(snapshot);

        Assert.True(result.IsSaved);
        Assert.Equal(["intent", "begin", "committed", "clear"], journal.Events);
        Assert.Equal(
            [SaveStep.Start, SaveStep.IntentWritten, SaveStep.TempCreated, SaveStep.OutputWritten, SaveStep.BeginWritten, SaveStep.Validated, SaveStep.Replaced, SaveStep.CommittedWritten, SaveStep.Done],
            faults.Reached);
        Assert.False(targetAtStep[SaveStep.Validated], "the original is untouched until the replace");
        Assert.True(targetAtStep[SaveStep.Replaced]);
        Assert.False(clearedAtStep[SaveStep.Replaced], "the journal is still there when the replace has just happened");
        Assert.False(clearedAtStep[SaveStep.CommittedWritten]);
        Assert.True(clearedAtStep[SaveStep.Done]);

        rig.Session.Model.Apply(Commands.Note());
        var fresh = Assert.Single(rig.Store.List());
        rig.Session.Dispose(); // the journal is held exclusively while the document is being edited
        Assert.Equal(JournalKey.Fingerprint(rig.Doc), rig.Store.Read(fresh.Path).Header!.Fingerprint);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("clear")]
    public async Task AJournalThatCannotBeWrittenAfterTheReplaceDoesNotTurnTheSaveIntoAFailure(string failingRecord)
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var journal = new RecordingJournal { Inner = new JournalSaveSink(rig.Session), Observe = name => { if (name == failingRecord) { throw new IOException("the journal's disk went away"); } } };
        var snapshot = rig.Session.Model.BeginSave();

        var result = await rig.Coordinator.SaveAsync(
            new SaveRequest { DocumentPath = rig.Doc, TargetPath = rig.Doc, Mode = SaveMode.Save, Snapshot = snapshot, PageCount = rig.PageCount, HeldOriginal = rig.Held },
            journal);
        rig.Session.Model.MarkSaved(snapshot);

        Assert.True(result.IsSaved); // the file was replaced; what the journal could not say, the next start's recovery finishes
        Assert.Equal(1, Annotations(File.ReadAllBytes(rig.Doc)));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        rig.Session.Dispose();
        Assert.Empty(new MikePDF.Core.Editing.Recovery.RecoveryService(rig.Store).Scan());
    }

    // ---- SP-15: validation catches faults ----

    [Theory]
    [InlineData("drop-an-annotation")]
    [InlineData("corrupt-an-xref-offset")]
    [InlineData("wrong-size")]
    [InlineData("leave-a-pdfium-key")]
    [InlineData("damage-the-prefix")]
    [InlineData("engine-says-no")]
    public async Task AFaultyOutputFailsStageFiveLeavesTheOriginalByteIdenticalAndRemovesTheTemporaryFiles(string fault)
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight("a"));
        rig.Session.Model.Apply(Commands.Highlight("b"));
        switch (fault)
        {
            case "drop-an-annotation":
                rig.Host.TamperEdits = edits => [.. edits.Take(edits.Count - 1)];
                break;
            case "corrupt-an-xref-offset":
                rig.Host.Corrupt = bytes => Replace(bytes, "startxref\n", "startxref\n9", fromEnd: true);
                break;
            case "wrong-size":
                rig.Host.Corrupt = bytes => SetLastSize(bytes, 2);
                break;
            case "leave-a-pdfium-key":
                rig.Host.SkipValidate = true; // reach the check behind the structure walk
                rig.Host.Corrupt = bytes => [.. bytes[..rig.Original.Length], .. "%/PDFIUM_HasGeneratedAP\n"u8, .. bytes[rig.Original.Length..]];
                break;
            case "damage-the-prefix":
                rig.Host.SkipValidate = true;
                rig.Host.Corrupt = bytes =>
                {
                    var copy = (byte[])bytes.Clone();
                    copy[20] ^= 0x01;
                    return copy;
                };
                break;
            default:
                rig.Engine.Verdict = (_, _) => "page-count";
                break;
        }

        var result = await rig.SaveAsync();

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(SaveReason.ValidationFailed, result.Reason);
        Assert.NotNull(result.Detail);
        AssertUntouched(rig);
        Assert.True(rig.Session.Model.IsDirty); // "Your changes are still here."
        Assert.False(rig.Session.Model.IsSaving);
        rig.Session.Model.Apply(Commands.Note()); // and editing works again
        rig.Host.Corrupt = null;
        rig.Host.TamperEdits = null;
        rig.Host.SkipValidate = false;
        rig.Engine.Verdict = null;
        var retry = await rig.SaveAsync();
        Assert.True(retry.IsSaved, retry.Reason + " " + retry.Detail); // and so does the next save
    }

    private static byte[] SetLastSize(byte[] bytes, int size)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var at = text.LastIndexOf("/Size ", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var end = at + 6;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return System.Text.Encoding.Latin1.GetBytes(text[..(at + 6)] + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + text[end..]);
    }

    private static byte[] Replace(byte[] bytes, string find, string with, bool fromEnd)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var at = fromEnd ? text.LastIndexOf(find, StringComparison.Ordinal) : text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, find);
        return System.Text.Encoding.Latin1.GetBytes(text[..at] + with + text[(at + find.Length)..]);
    }

    // ---- SP-18, SP-24: refusals before anything is written ----

    [Fact]
    public async Task AReadOnlyFileIsRefusedWithSaveAsToOfferAndNothingIsWritten()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        File.SetAttributes(rig.Doc, FileAttributes.ReadOnly);
        var journal = new RecordingJournal();

        var result = await rig.Coordinator.SaveAsync(Request(rig), journal);
        rig.Session.Model.AbortSave();

        Assert.Equal((SaveOutcome.Refused, SaveReason.ReadOnly), (result.Outcome, result.Reason));
        Assert.Empty(journal.Events); // not even a SaveIntent
        Assert.Empty(rig.Host.Calls);
        File.SetAttributes(rig.Doc, FileAttributes.Normal);
        AssertUntouched(rig);
    }

    private static SaveRequest Request(Rig rig, SaveMode mode = SaveMode.Save, string? target = null) => new()
    {
        DocumentPath = rig.Doc,
        TargetPath = target ?? rig.Doc,
        Mode = mode,
        Snapshot = rig.Session.Model.BeginSave(),
        PageCount = rig.PageCount,
        HeldOriginal = rig.Held,
    };

    [Fact]
    public async Task AFileWithAnotherNameIsRefusedBecauseTheOtherNameWouldKeepTheOldContent()
    {
        using var rig = new Rig();
        Native.HardLink(rig.Workspace.Combine("docs\\other-name.pdf"), rig.Doc);
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.Coordinator.SaveAsync(Request(rig), null);

        Assert.Equal((SaveOutcome.Refused, SaveReason.HardLinks), (result.Outcome, result.Reason));
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf", "other-name.pdf"], rig.Workspace.Listing());
    }

    [Fact]
    public async Task ADirectoryThatCannotTakeANewFileTurnsSaveIntoSaveAs()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var directory = new DirectoryInfo(rig.Workspace.Documents);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            var result = await rig.Coordinator.SaveAsync(Request(rig), null);

            Assert.Equal((SaveOutcome.Refused, SaveReason.DirectoryNotWritable), (result.Outcome, result.Reason));
        }
        finally
        {
            security = directory.GetAccessControl();
            security.RemoveAccessRule(deny);
            directory.SetAccessControl(security);
        }

        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
    }

    [Fact]
    public async Task AFileOverOneGibibyteIsRefusedUpFrontWithNoPartialFiles()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var big = rig.Workspace.Write("big.pdf", rig.Original);
        MakeSparse(big, (1L << 30) + 4096);
        using var held = File.OpenHandle(big, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var request = new SaveRequest { DocumentPath = big, TargetPath = big, Mode = SaveMode.Save, Snapshot = rig.Session.Model.BeginSave(), PageCount = 1, HeldOriginal = held };

        var result = await rig.Coordinator.SaveAsync(request, new RecordingJournal());

        Assert.Equal((SaveOutcome.Refused, SaveReason.TooLarge), (result.Outcome, result.Reason));
        Assert.Empty(rig.Host.Calls);
        Assert.Equal(["big.pdf", "doc.pdf"], rig.Workspace.Listing());
    }

    private static void MakeSparse(string path, long length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var returned = 0u;
        var ok = DeviceIoControl(file.SafeFileHandle, 0x900C4, 0, 0, 0, 0, ref returned, 0); // FSCTL_SET_SPARSE
        Assert.True(ok, "the volume cannot make a sparse file");
        file.SetLength(length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, nint input, uint inputSize, nint output, uint outputSize, ref uint returned, nint overlapped);

    [Fact]
    public async Task FormAndPageChangesAreRefusedBecauseThisBuildHasNoEngineSaveYet()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        rig.Session.Model.Apply(EditCommand.Create("SetFieldValue", new { field = "f1", value = "v" }));

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Refused, SaveReason.UnsupportedEdits), (result.Outcome, result.Reason));
        AssertUntouched(rig);
        Assert.True(rig.Session.Model.IsDirty);
    }

    [Fact]
    public async Task ACommandNoGeneratorExistsForIsRefusedByNameAndNothingIsLeftBehind()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(EditCommand.Create("AddReply", new { page = 0 }));

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Refused, SaveReason.UnsupportedEdits, "AddReply"), (result.Outcome, result.Reason, result.Detail));
        AssertUntouched(rig);
    }

    // ---- SP-23: encrypted, and R-39: files the strict parser refuses ----

    [Fact]
    public async Task APasswordProtectedDocumentIsNeverReplacedByAnUnprotectedOne()
    {
        using var rig = new Rig("encrypted_hello_world_r3.pdf");
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Refused, SaveReason.PasswordProtected), (result.Outcome, result.Reason));
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
    }

    public static TheoryData<string> FixtureNames
    {
        get
        {
            var directory = Path.GetDirectoryName(SaveWorkspace.Fixture("hello_world.pdf"))!;
            var root = directory;
            while (!File.Exists(Path.Combine(root, "MikePDF.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("MikePDF.slnx not found");
            }

            var fixtures = Path.Combine(root, "tests", "MikePDF.PdfWriter.Tests", "Fixtures", "pdfium");
            return [.. Directory.GetFiles(fixtures, "*.pdf").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task EveryRealFileIsEitherSavedSoundlyOrRefusedWithAReasonAndLeftAlone(string fixture)
    {
        using var rig = new Rig(fixture);
        var strictlyReadable = StrictDocument.Open(rig.Original).IsSuccess;
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync();

        var name = Path.GetFileName(rig.Doc);
        if (result.IsSaved)
        {
            Assert.True(strictlyReadable);
            var saved = File.ReadAllBytes(rig.Doc);
            Assert.True(saved.AsSpan(0, rig.Original.Length).SequenceEqual(rig.Original), "the original bytes are an exact prefix");
            Assert.True(Soundness.NoNewErrors(saved, rig.Original));
            Assert.Equal(Annotations(rig.Original) + 1, Annotations(saved));
        }
        else
        {
            // R-39: PDFium opens it, the strict parser does not (or the page tree is not one a writer may trust): explain and refuse.
            Assert.NotEqual(SaveOutcome.NothingToSave, result.Outcome);
            Assert.False(string.IsNullOrEmpty(result.Detail), "a refusal says why");
            Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
            Assert.Equal([name], rig.Workspace.Listing());
            if (!strictlyReadable)
            {
                Assert.Equal(SaveOutcome.Refused, result.Outcome);
                Assert.True(result.Reason is SaveReason.Structure or SaveReason.PasswordProtected, result.Reason.ToString());
            }
        }
    }

    // ---- SP-21 and SP-39: the path changed under the open document ----

    [Fact]
    public async Task APathReplacedByAnotherFileAsksBeforeOverwritingAndWritesNothing()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        File.Move(rig.Doc, rig.Workspace.Combine("docs\\aside.pdf"));
        File.WriteAllBytes(rig.Doc, "another program saved here"u8.ToArray());

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.NeedsConfirmation, SaveReason.ChangedOnDisk), (result.Outcome, result.Reason));
        Assert.Equal("another program saved here"u8.ToArray(), File.ReadAllBytes(rig.Doc));
        Assert.Empty(rig.Host.Calls);
        Assert.True(rig.Session.Model.IsDirty);
    }

    [Fact]
    public async Task ADocumentThatWasDeletedIsReportedGone()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        File.Delete(rig.Doc);

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Refused, SaveReason.TargetGone), (result.Outcome, result.Reason));
        Assert.False(File.Exists(rig.Doc));
    }

    [Fact]
    public async Task ARaceBetweenThePreflightAndTheReplaceIsCaughtByTheSecondIdentityCheck()
    {
        var faults = new RecordingFaults();
        using var rig = new Rig(faults: faults);
        rig.Session.Model.Apply(Commands.Highlight());
        faults.Observe = step =>
        {
            if (step == SaveStep.Validated)
            {
                File.Move(rig.Doc, rig.Workspace.Combine("docs\\aside.pdf"));
                File.WriteAllBytes(rig.Doc, "swapped in at the last moment"u8.ToArray());
            }
        };

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.NeedsConfirmation, SaveReason.ChangedOnDisk), (result.Outcome, result.Reason));
        Assert.Equal("swapped in at the last moment"u8.ToArray(), File.ReadAllBytes(rig.Doc));
        Assert.DoesNotContain(rig.Workspace.Listing(), static n => n.Contains(".mikepdf-", StringComparison.Ordinal));
    }

    // ---- SP-20, SP-41: locks and shares ----

    [Fact]
    public async Task ALockThatClearsInTimeIsRetriedAndTheSaveSucceeds()
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        native.PosixAnswers.Enqueue(SaveNative.ErrorAccessDenied);
        using var rig = new Rig(native: native);
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync();

        Assert.True(result.IsSaved);
        Assert.Equal(3, result.Replace!.Attempts);
        AssertCleanSave(rig);
    }

    [Fact]
    public async Task ALockThatDoesNotClearFailsCleanlyTheOriginalIsIntactAndTheTemporaryFileIsGone()
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        for (var i = 0; i < 50; i++)
        {
            native.PosixAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        }

        using var rig = new Rig(native: native);
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Failed, SaveReason.InUse), (result.Outcome, result.Reason));
        AssertUntouched(rig);
        Assert.True(rig.Session.Model.IsDirty);
    }

    [Fact]
    public async Task WhereTheRenameIsNotSupportedTheFallbackRunsAndNothingIsLeftOver()
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(87);
        using var rig = new Rig(native: native);
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync();

        Assert.True(result.IsSaved);
        Assert.Equal(ReplaceMechanism.ReplaceFile, result.Replace!.Mechanism);
        Assert.Equal(1, native.ReplaceFileCalls);
        AssertCleanSave(rig);
    }

    private static void AssertCleanSave(Rig rig)
    {
        var saved = File.ReadAllBytes(rig.Doc);
        Assert.True(saved.AsSpan(0, rig.Original.Length).SequenceEqual(rig.Original));
        Assert.Equal(1, Annotations(saved));
        Assert.Equal(["doc.pdf"], rig.Workspace.Listing());
        Assert.Empty(rig.Store.List());
    }

    // ---- SP-19, SP-31: failures in the host and on the disk ----

    [Fact]
    public async Task AFullDiskIsReportedAsNoSpaceTheOriginalIsIntactAndTheTemporaryFileIsGone()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        rig.Host.Override = () => throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));

        var result = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Failed, SaveReason.NoSpace), (result.Outcome, result.Reason));
        AssertUntouched(rig);
        Assert.True(rig.Session.Model.IsDirty);
    }

    [Theory]
    [InlineData("Timeout", "timeout")]
    [InlineData("HostCrashed", "host-crashed")]
    public async Task AHostThatTimesOutOrDiesFailsTheSaveOnceAndTheNextSaveWorks(string code, string detail)
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        rig.Host.Override = () => new HostResult(false, code);

        var failed = await rig.SaveAsync();
        rig.Host.Override = null;
        var retried = await rig.SaveAsync();

        Assert.Equal((SaveOutcome.Failed, SaveReason.HostFailed, detail), (failed.Outcome, failed.Reason, failed.Detail));
        Assert.True(retried.IsSaved);
        AssertCleanSave(rig);
    }

    [Fact]
    public async Task CancellingBeforeTheReplaceStopsTheSaveAndRemovesTheTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        var faults = new RecordingFaults { Observe = step => { if (step == SaveStep.Validated) { cancellation.Cancel(); } } };
        using var rig = new Rig(faults: faults);
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync(cancellationToken: cancellation.Token);

        Assert.Equal((SaveOutcome.Failed, SaveReason.Cancelled), (result.Outcome, result.Reason));
        AssertUntouched(rig);
        Assert.True(rig.Session.Model.IsDirty);
    }

    // ---- SP-32: one save at a time, edits blocked while it runs ----

    [Fact]
    public async Task ASecondSaveDoesNotStartAPipelineAndEditsAreBlockedUntilTheFirstEnds()
    {
        using var rig = new Rig();
        var release = new TaskCompletionSource();
        rig.Host.Gate = release.Task;
        rig.Session.Model.Apply(Commands.Highlight());

        var first = rig.SaveAsync();
        await Task.Delay(100);

        Assert.True(rig.Session.Model.IsSaving);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.SaveAsync());
        Assert.Throws<EditsBlockedException>(() => rig.Session.Model.Apply(Commands.Note()));
        Assert.Single(rig.Host.Calls);

        release.SetResult();
        Assert.True((await first).IsSaved);
        rig.Session.Model.Apply(Commands.Note()); // editing is back
        Assert.True(rig.Session.Model.IsDirty);
    }

    // ---- SP-25, SP-26: Save As, the rewrite and the flattened copy ----

    [Fact]
    public async Task SaveAsWritesTheNewFileLeavesTheOriginalByteIdenticalAndClearsTheJournal()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var copy = rig.Workspace.Combine("docs\\copy.pdf");

        var result = await rig.SaveAsync(SaveMode.SaveAs, copy);

        Assert.True(result.IsSaved);
        Assert.Equal(copy, result.TargetPath, ignoreCase: true);
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
        var saved = File.ReadAllBytes(copy);
        Assert.True(saved.AsSpan(0, rig.Original.Length).SequenceEqual(rig.Original));
        Assert.Equal(1, Annotations(saved));
        Assert.Equal(["copy.pdf", "doc.pdf"], rig.Workspace.Listing());
        Assert.Empty(rig.Store.List());
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task SaveAsToTheSamePathIsASave()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());

        var result = await rig.SaveAsync(SaveMode.SaveAs, rig.Doc);

        Assert.True(result.IsSaved);
        AssertCleanSave(rig);
    }

    [Fact]
    public async Task SaveAsWithNothingToAddIsAByteForByteCopyThatIsStillValidated()
    {
        using var rig = new Rig();
        var copy = rig.Workspace.Combine("docs\\copy.pdf");

        var result = await rig.SaveAsync(SaveMode.SaveAs, copy);

        Assert.True(result.IsSaved);
        Assert.Equal(rig.Original, File.ReadAllBytes(copy));
        Assert.Equal(["validate"], rig.Host.Calls);
        Assert.Equal(1, rig.Engine.Calls);
    }

    [Fact]
    public async Task SaveAsToAFileThatExistsReplacesItAfterThePickerConfirmedIt()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var existing = rig.Workspace.Write("existing.pdf", "an older copy"u8.ToArray());

        var result = await rig.SaveAsync(SaveMode.SaveAs, existing);

        Assert.True(result.IsSaved);
        Assert.Equal(1, Annotations(File.ReadAllBytes(existing)));
        Assert.Equal(["doc.pdf", "existing.pdf"], rig.Workspace.Listing());
    }

    [Fact]
    public async Task TheWholeFileRewriteHasNoEditHistoryAndStillHasTheAnnotations()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        var copy = rig.Workspace.Combine("docs\\whole.pdf");

        var result = await rig.SaveAsync(SaveMode.SaveAs, copy, rewrite: true);

        Assert.True(result.IsSaved);
        var saved = File.ReadAllBytes(copy);
        var document = StrictDocument.Open(saved).Value;
        Assert.Single(document.Revisions);
        Assert.Equal(1, Annotations(saved));
        Assert.Equal(["append", "rewrite", "validate"], rig.Host.Calls);
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc));
    }

    [Fact]
    public async Task AFlattenedCopyHasNoAnnotationsLeavesTheDocumentAndItsJournalAndEditsAlone()
    {
        using var rig = new Rig();
        rig.Session.Model.Apply(Commands.Highlight());
        rig.Session.Model.Apply(Commands.Note());
        var copy = rig.Workspace.Combine("docs\\flat.pdf");

        var result = await rig.SaveAsync(SaveMode.FlattenedCopy, copy);

        Assert.True(result.IsSaved);
        var flat = File.ReadAllBytes(copy);
        Assert.Equal(0, Annotations(flat));
        Assert.Equal(rig.PageCount, PagesOf(flat));
        Assert.Single(StrictDocument.Open(flat).Value.Revisions);
        Assert.Equal(rig.Original, File.ReadAllBytes(rig.Doc)); // the working file is untouched
        Assert.True(rig.Session.Model.IsDirty); // and still has its edits
        Assert.Single(rig.Store.List()); // and its journal
        Assert.Equal(["doc.pdf", "flat.pdf"], rig.Workspace.Listing());
        Assert.False(rig.Session.Model.IsSaving);
    }
}
