#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Integration.Tests.Support;
using MikePDF.PdfWriter;
using MikePDF.Protocol;
using MikePDF.Protocol.Edits;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// The save pipeline against the real sandboxed writer host and a real sandboxed engine for stage 5's fresh-engine check (save-pipeline 14: SP-11,
/// SP-12, SP-13, SP-26, SP-31): handles in, bytes out, nothing else crosses the pipe.
/// </summary>
public sealed class RealWriterHostSaveTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static SaveCoordinator Coordinator(WriterHostClient host, SandboxFixture sandbox, ISaveFaults? faults = null) =>
        new(new WriterHostSaveHost(host), new EngineSaveCheck(() => sandbox.Options()), new FileReplacer(5, TimeSpan.FromMilliseconds(50)), faults);

    private static EditSession Session(SaveWorkspace workspace, string document) => new(new JournalStore(workspace.Journals), document, "1.0.0.0");

    private static int Annotations(byte[] bytes, int page = 0) => InProcessSaveHost.Count(bytes, SaveCoordinatorTests.Rig.PagesOf(bytes), [(uint)page])![0];

    [Fact]
    public async Task ASaveThroughTheSandboxedHostAndAFreshEngineWritesAFileEveryReaderOpens()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        var original = File.ReadAllBytes(doc);
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = Session(workspace, doc);
        session.Model.Apply(Commands.Highlight("a"));
        session.Model.Apply(Commands.Note("b", page: 1));
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);

        var result = await Coordinator(host, sandbox).SaveSessionAsync(session, doc, doc, SaveMode.Save, 2, held, cancellationToken: new CancellationTokenSource(Wait).Token);

        Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
        var saved = File.ReadAllBytes(doc);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Equal(1, Annotations(saved, 0));
        Assert.Equal(1, Annotations(saved, 1));
        Assert.True(Soundness.NoNewErrors(saved, original));
        Assert.Equal(0, host.RestartCount);
        Assert.Equal(["doc.pdf"], workspace.Listing());

        // The result opens in a new sandboxed engine and shows the same two pages.
        await using var engine = await sandbox.StartAsync();
        var opened = await engine.OpenDocumentAsync(doc);
        Assert.Equal(Status.Ok, opened.Status);
        await using (opened.Value!)
        {
            Assert.Equal(2u, opened.Value!.PageCount);
        }
    }

    [Fact]
    public async Task APasswordProtectedFileIsRefusedByTheHostAndNeverReplaced()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("encrypted.pdf");
        var original = File.ReadAllBytes(doc);
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = Session(workspace, doc);
        session.Model.Apply(Commands.Highlight());

        var result = await Coordinator(host, sandbox).SaveSessionAsync(session, doc, doc, SaveMode.Save, 1, held: null);

        Assert.Equal((SaveOutcome.Refused, SaveReason.PasswordProtected), (result.Outcome, result.Reason));
        Assert.Equal(original, File.ReadAllBytes(doc));
        Assert.Equal(["doc.pdf"], workspace.Listing());
        Assert.Equal(0, host.RestartCount);
    }

    [Fact]
    public async Task AnEditThatExpectsWhatIsNotThereFailsTheWholeSaveAndTheHostSurvives()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = Session(workspace, doc);
        session.Model.Apply(Commands.Highlight("a"));
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var coordinator = Coordinator(host, sandbox);
        Assert.True((await coordinator.SaveSessionAsync(session, doc, doc, SaveMode.Save, 2, held)).IsSaved);
        var afterFirstSave = File.ReadAllBytes(doc);
        using var second = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        session.Model.Apply(Commands.Note("fine"));
        session.Model.Apply(MikePDF.Core.Editing.EditCommand.Create("DeleteAnnotation", new { page = 0, name = "a", subtype = "Underline", rect = new[] { 10f, 20f, 110f, 40f } }));

        var result = await coordinator.SaveSessionAsync(session, doc, doc, SaveMode.Save, 2, second);

        Assert.Equal((SaveOutcome.Failed, SaveReason.ValidationFailed, "ExpectationMismatch"), (result.Outcome, result.Reason, result.Detail));
        Assert.Equal(afterFirstSave, File.ReadAllBytes(doc));
        Assert.Equal(["doc.pdf"], workspace.Listing());
        Assert.Equal(0, host.RestartCount);
        Assert.True(session.Model.IsDirty);
    }

    [Fact]
    public async Task SaveAsRewriteAndAFlattenedCopyGoThroughTheHostToo()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = Session(workspace, doc);
        session.Model.Apply(Commands.Highlight("a"));
        var coordinator = Coordinator(host, sandbox);
        var whole = workspace.Combine("docs\\whole.pdf");
        var flat = workspace.Combine("docs\\flat.pdf");

        var rewritten = await coordinator.SaveSessionAsync(session, doc, whole, SaveMode.SaveAs, 2, held: null, rewriteWholeFile: true);
        var flattened = await coordinator.SaveSessionAsync(session, doc, flat, SaveMode.FlattenedCopy, 2, held: null);

        Assert.True(rewritten.IsSaved, rewritten.Reason + " " + rewritten.Detail);
        Assert.True(flattened.IsSaved, flattened.Reason + " " + flattened.Detail);
        Assert.Single(StrictDocument.Open(File.ReadAllBytes(whole)).Value.Revisions);
        Assert.Equal(1, Annotations(File.ReadAllBytes(whole)));
        Assert.Equal(0, Annotations(File.ReadAllBytes(flat)));
        Assert.Equal(2, SaveCoordinatorTests.Rig.PagesOf(File.ReadAllBytes(flat)));
        Assert.Equal(0, host.RestartCount);
        Assert.Equal(["doc.pdf", "flat.pdf", "whole.pdf"], workspace.Listing());
    }

    [Fact]
    public async Task AHostThatIsKilledFailsTheSaveInFlightAtMostOnceAndTheNextSaveWorks()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        await using var host = await sandbox.StartWriterHostAsync();
        using var session = Session(workspace, doc);
        session.Model.Apply(Commands.Highlight("a"));
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var coordinator = Coordinator(host, sandbox);
        var original = File.ReadAllBytes(doc);

        using (var process = Process.GetProcessById((int)host.ProcessId))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        var first = await coordinator.SaveSessionAsync(session, doc, doc, SaveMode.Save, 2, held);
        if (!first.IsSaved)
        {
            Assert.Equal((SaveOutcome.Failed, SaveReason.HostFailed), (first.Outcome, first.Reason));
            Assert.Equal(original, File.ReadAllBytes(doc));
            Assert.Equal(["doc.pdf"], workspace.Listing());
        }

        var retry = first.IsSaved ? first : await coordinator.SaveSessionAsync(session, doc, doc, SaveMode.Save, 2, held);

        Assert.True(retry.IsSaved, retry.Reason + " " + retry.Detail);
        Assert.Equal(["doc.pdf"], workspace.Listing());
        Assert.True(host.RestartCount >= 1);
    }

    // ---- the messages themselves ----

    private static SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);

    private static SafeFileHandle Scratch(string path, byte[]? content = null)
    {
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (content is not null)
        {
            RandomAccess.Write(handle, content, 0);
        }

        return handle;
    }

    private static byte[] OneHighlight() => EditBlobCodec.Encode(new EditBlob
    {
        PageCount = 2,
        Edits = [new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Highlight, Rect = [10, 20, 110, 40], Color = [1, 1, 0], Name = "x", QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20] }],
    });

    [Fact]
    public async Task TheHostRefusesWhatItCannotUseWithoutDyingAndConsumesEveryHandleItWasGiven()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        await using var host = await sandbox.StartWriterHostAsync();
        var process = host.ProcessId;
        var deadline = TimeSpan.FromSeconds(30);

        // A blob that is not a blob.
        using (var baseFile = Open(doc))
        using (var edits = Scratch(workspace.Combine("e1"), [1, 2, 3, 4]))
        using (var output = Scratch(workspace.Combine("o1")))
        {
            var garbage = await host.AppendAnnotationsAsync(baseFile, edits, 4, output, 2, deadline);
            Assert.Equal(Status.InvalidArgument, garbage.Status);
            Assert.Equal(0, RandomAccess.GetLength(output));
        }

        // A length that is not the file's.
        var blob = OneHighlight();
        using (var baseFile = Open(doc))
        using (var edits = Scratch(workspace.Combine("e2"), blob))
        using (var output = Scratch(workspace.Combine("o2")))
        {
            Assert.Equal(Status.InvalidArgument, (await host.AppendAnnotationsAsync(baseFile, edits, blob.Length + 1, output, 2, deadline)).Status);
        }

        // An output that is not empty.
        using (var baseFile = Open(doc))
        using (var edits = Scratch(workspace.Combine("e3"), blob))
        using (var output = Scratch(workspace.Combine("o3"), [9]))
        {
            Assert.Equal(Status.InvalidArgument, (await host.AppendAnnotationsAsync(baseFile, edits, blob.Length, output, 2, deadline)).Status);
            Assert.Equal(1, RandomAccess.GetLength(output));
        }

        // A page count that is not the file's: a refusal with a reason, not an error.
        using (var baseFile = Open(doc))
        using (var edits = Scratch(workspace.Combine("e4"), blob))
        using (var output = Scratch(workspace.Combine("o4")))
        {
            var wrongPages = await host.AppendAnnotationsAsync(baseFile, edits, blob.Length, output, 7, deadline);
            Assert.Equal(Status.Ok, wrongPages.Status);
            Assert.False(wrongPages.Value!.Ok);
            Assert.Equal(WriterErrorCodes.PageCountMismatch, wrongPages.Value.IssueCodes[0]);
        }

        // The host is the same process, and the good case still works.
        using (var baseFile = Open(doc))
        using (var edits = Scratch(workspace.Combine("e5"), blob))
        using (var output = Scratch(workspace.Combine("o5")))
        {
            var good = await host.AppendAnnotationsAsync(baseFile, edits, blob.Length, output, 2, deadline);
            Assert.Equal(Status.Ok, good.Status);
            Assert.True(good.Value!.Ok);
            Assert.Equal((1u, 0u), (good.Value.AnnotationsAdded, good.Value.AnnotationsRemoved));
            Assert.Equal((ulong)RandomAccess.GetLength(output), good.Value.BytesWritten);
        }

        Assert.Equal(process, host.ProcessId);
        Assert.Equal(0, host.RestartCount);
    }

    [Fact]
    public async Task VerifyAnnotationsComparesTwoIndependentParsesAndNamesTheMismatchedPages()
    {
        using var workspace = new SaveWorkspace();
        var doc = workspace.Copy("hello_world_2_pages.pdf");
        await using var host = await sandbox.StartWriterHostAsync();
        var blob = OneHighlight();
        using var baseFile = Open(doc);
        using var edits = Scratch(workspace.Combine("e"), blob);
        using var output = Scratch(workspace.Combine("o"));
        Assert.True((await host.AppendAnnotationsAsync(baseFile, edits, blob.Length, output, 2, TimeSpan.FromSeconds(30))).Value!.Ok);

        var right = await host.VerifyAnnotationsAsync(baseFile, output, 2, [0, 1], [1, 0], [0, 0], TimeSpan.FromSeconds(30));
        var wrong = await host.VerifyAnnotationsAsync(baseFile, output, 2, [0, 1], [2, 1], [0, 0], TimeSpan.FromSeconds(30));
        var badCount = await host.VerifyAnnotationsAsync(baseFile, output, 3, [0], [1], [0], TimeSpan.FromSeconds(30));

        Assert.True(right.Value!.Ok);
        Assert.Empty(right.Value.Mismatches);
        Assert.True(wrong.Value!.Ok);
        Assert.Equal([0u, 1u], wrong.Value.Mismatches);
        Assert.False(badCount.Value!.Ok); // the page count is not the file's
    }

    [Fact]
    public void TheErrorCodesTheBrokerNamesAreTheParsersOwn()
    {
        Assert.Equal((uint)PdfErrorCode.LimitExceeded, WriterErrorCodes.LimitExceeded);
        Assert.Equal((uint)PdfErrorCode.UnsupportedEncryption, WriterErrorCodes.UnsupportedEncryption);
        Assert.Equal((uint)PdfErrorCode.InvalidEdit, WriterErrorCodes.InvalidEdit);
        Assert.Equal((uint)PdfErrorCode.ExpectationMismatch, WriterErrorCodes.ExpectationMismatch);
        Assert.Equal((uint)PdfErrorCode.PageCountMismatch, WriterErrorCodes.PageCountMismatch);
    }
}
