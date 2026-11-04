using System.Text;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Saving;
using MikePDF.Integration.Tests.Support;
using MikePDF.PdfWriter;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// Schema 7 through the real sandboxed engine and the real sandboxed writer host (criteria SP-11, SP-12, FM-05, FM-06, FM-19 in part, AC-14, AC-15): forms edited through PDFium's
/// form-fill events, saved by the engine into a handle the broker made, repaired by the host's NormalizeTail, and read back by a fresh engine. Nothing here parses a PDF in the
/// test process except to judge the result.
/// </summary>
public sealed class Schema7EndToEndTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Directory = Path.Combine(Path.GetTempPath(), "mikepdf-s7-" + Guid.NewGuid().ToString("N")[..10]);
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string Copy(string source, string name = "doc.pdf")
        {
            var path = Path.Combine(Directory, name);
            File.Copy(source, path);
            return path;
        }

        public string Path_(string name) => Path.Combine(Directory, name);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Where each fixture of the theory ended up, for the test report: set S7_NOTES to a file name to collect it.
    private static void Note(string name, string text)
    {
        if (Environment.GetEnvironmentVariable("S7_NOTES") is { Length: > 0 } file)
        {
            File.AppendAllText(file, name + ": " + text + "\n");
        }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("MikePDF.slnx not found");
    }

    private static string WriterFixture(string name) => Path.Combine(Root(), "tests", "MikePDF.PdfWriter.Tests", "Fixtures", "pdfium", name);

    public static TheoryData<string> WriterFixtures
    {
        get
        {
            var directory = Path.Combine(Root(), "tests", "MikePDF.PdfWriter.Tests", "Fixtures", "pdfium");
            return [.. Directory.GetFiles(directory, "*.pdf").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>PDFium's save of the document the engine holds: the incremental save and the number of bytes the engine says it wrote.</summary>
    private static async Task<byte[]> SaveAsync(SupervisedDocument document, string output, ushort mode = 0)
    {
        SaveDocumentReply reply;
        using (var handle = File.OpenHandle(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var saved = await document.SaveDocumentAsync(mode, handle);
            Assert.True(saved.Status == Status.Ok, "SaveDocument: " + saved.Status + " " + saved.Detail);
            reply = saved.Value!;
        }

        var bytes = File.ReadAllBytes(output);
        Assert.Equal((ulong)bytes.Length, reply.BytesWritten);
        Assert.Equal(document.PageCount, reply.PageCount);
        return bytes;
    }

    private static async Task<(NormalizeTailReply Reply, byte[] Output)> NormalizeAsync(WriterHostClient host, string original, string pdfium, string output, uint flags = 3)
    {
        NormalizeTailReply reply;
        using (var originalHandle = File.OpenHandle(original, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare))
        using (var pdfiumHandle = File.OpenHandle(pdfium, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare))
        using (var outputHandle = File.OpenHandle(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var result = await host.NormalizeTailAsync(originalHandle, pdfiumHandle, outputHandle, flags, Wait);
            Assert.Equal(Status.Ok, result.Status);
            reply = result.Value!;
        }

        return (reply, File.Exists(output) ? File.ReadAllBytes(output) : []);
    }

    private static async Task<(uint Total, GetFormFieldsReply Fields)> FieldsAsync(SupervisedDocument document)
    {
        var fields = await document.GetFormFieldsAsync(0, 64);
        Assert.Equal(Status.Ok, fields.Status);
        return (fields.Value!.Total, fields.Value);
    }

    [Fact]
    public async Task AFilledFormIsSavedRepairedAndReadBackWithItsValuesAndNothingOfPDFiumsOwn()
    {
        using var workspace = new Workspace();
        var source = workspace.Copy(SandboxFixture.FixturePath("acroform_filled.pdf"));
        var original = File.ReadAllBytes(source);
        await using var engine = await sandbox.StartAsync();
        await using var host = await sandbox.StartWriterHostAsync();
        var opened = await engine.OpenDocumentAsync(source);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;

        var (total, fields) = await FieldsAsync(document);
        Assert.Equal(4u, total);
        Assert.Equal([(ushort)6, (ushort)2, (ushort)4, (ushort)6], fields.Types);
        var set = await document.SetFormFieldValueAsync(fields.FieldIds[0], 0, "World", false, null);
        Assert.Equal(Status.Ok, set.Status);
        Assert.True(set.Value!.Accepted);
        Assert.Equal("World", set.Value.Value);
        Assert.Equal(Status.Ok, (await document.SetFormFieldValueAsync(fields.FieldIds[1], 1, null, false, null)).Status); // the box is on: off
        Assert.Equal(Status.Ok, (await document.SetFormFieldValueAsync(fields.FieldIds[2], 2, null, false, [2])).Status);  // "Three"

        var pdfium = workspace.Path_("pdfium.pdf");
        var pdfiumBytes = await SaveAsync(document, pdfium);
        Assert.True(pdfiumBytes.AsSpan(0, original.Length).SequenceEqual(original), "an incremental save keeps the original as a prefix");
        Assert.True(pdfiumBytes.Length > original.Length);

        var output = workspace.Path_("normalized.pdf");
        var (reply, normalized) = await NormalizeAsync(host, source, pdfium, output);

        Assert.True(reply.Ok, string.Join("; ", reply.IssueMessages));
        Assert.Equal((ulong)normalized.Length, reply.BytesWritten);
        Assert.True(normalized.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.True(Soundness.NoNewErrors(normalized, original));
        Assert.DoesNotContain("PDFIUM_", Encoding.Latin1.GetString(normalized.AsSpan(original.Length)));
        Assert.Equal(0, host.RestartCount);

        // A fresh engine reads what was entered (an engine holds one document).
        await using var fresh = await sandbox.StartAsync();
        var reopened = await fresh.OpenDocumentAsync(output);
        Assert.Equal(Status.Ok, reopened.Status);
        await using var again = reopened.Value!;
        var (_, after) = await FieldsAsync(again);
        Assert.Equal("World", after.Values[0]);
        Assert.False((after.States[1] & 1) != 0, "the check box is off");
        var combo = await again.GetFormFieldDetailAsync(after.FieldIds[2], 0);
        Assert.Equal(Status.Ok, combo.Status);
        Assert.Equal([2u], combo.Value!.SelectedIndices);
    }

    [Theory]
    [MemberData(nameof(WriterFixtures))]
    public async Task APDFiumSaveOfEveryFixtureIsRepairedToAFileThatAddsNoError(string name)
    {
        using var workspace = new Workspace();
        var source = workspace.Copy(WriterFixture(name));
        var original = File.ReadAllBytes(source);
        await using var engine = await sandbox.StartAsync();
        await using var host = await sandbox.StartWriterHostAsync();
        var opened = await engine.OpenDocumentAsync(source);
        if (opened.Status != Status.Ok)
        {
            Note(name, "not opened: " + opened.Status);
            return; // encrypted with a password, or too damaged for PDFium to open: nothing to save
        }

        await using var document = opened.Value!;
        var pdfium = workspace.Path_("pdfium.pdf");
        var pdfiumBytes = await SaveAsync(document, pdfium); // a save with no edit at all: S4 found it is not a no-op, which is what stage 1 of the pipeline is for
        if (!pdfiumBytes.AsSpan(0, Math.Min(original.Length, pdfiumBytes.Length)).SequenceEqual(original))
        {
            Note(name, "PDFium's save does not begin with the original");
            return; // PDFium shifted the offsets (a header not at byte 0, a linearized file): not an extension, and the host says so (the test below)
        }

        var strict = StrictDocument.Open(original).IsSuccess;
        var (reply, normalized) = await NormalizeAsync(host, source, pdfium, workspace.Path_("normalized.pdf"));
        Note(name, (strict ? "strict" : "not strict") + ", normalised " + reply.Ok + ", PDFium's own file sound: " + Soundness.NoNewErrors(pdfiumBytes, original) + " " + string.Join("; ", reply.IssueMessages));

        if (!strict)
        {
            Assert.False(reply.Ok, name + ": the strict parser refuses the original, so the host must not normalise (R-39)");
            Assert.NotEmpty(reply.IssueCodes);
            return;
        }

        Assert.True(reply.Ok, name + ": " + string.Join("; ", reply.IssueMessages));
        Assert.True(normalized.AsSpan(0, original.Length).SequenceEqual(original), name);
        Assert.True(Soundness.NoNewErrors(normalized, original), name + ": the repaired file has an error the original did not have");
        Assert.DoesNotContain("PDFIUM_", Encoding.Latin1.GetString(normalized.AsSpan(original.Length)));
        await using var fresh = await sandbox.StartAsync();
        var reopened = await fresh.OpenDocumentAsync(workspace.Path_("normalized.pdf"));
        Assert.Equal(Status.Ok, reopened.Status);
        await using (reopened.Value!)
        {
            Assert.Equal(document.PageCount, reopened.Value!.PageCount);
        }
    }

    [Fact]
    public async Task AFileThatIsNotPDFiumsSaveOfTheOriginalIsRefusedWithAReason()
    {
        using var workspace = new Workspace();
        var one = workspace.Copy(SandboxFixture.FixturePath("hello_world.pdf"), "one.pdf");
        var two = workspace.Copy(SandboxFixture.FixturePath("rectangles.pdf"), "two.pdf");
        await using var engine = await sandbox.StartAsync();
        await using var host = await sandbox.StartWriterHostAsync();
        var opened = await engine.OpenDocumentAsync(two);
        await using var document = opened.Value!;
        await SaveAsync(document, workspace.Path_("pdfium-of-two.pdf"));

        var (reply, output) = await NormalizeAsync(host, one, workspace.Path_("pdfium-of-two.pdf"), workspace.Path_("out.pdf"));

        Assert.False(reply.Ok);
        Assert.Single(reply.IssueCodes);
        Assert.Contains("begin", reply.IssueMessages[0], StringComparison.Ordinal);
        Assert.Empty(output); // nothing was written
        Assert.Equal(0, host.RestartCount);
    }

    [Fact]
    public async Task AnAnnotationThatWasHiddenForTheScreenHasItsOwnFlagsInTheSavedFile()
    {
        // AC-14 through the whole stack: hide, save in the engine, repair in the host, read the file back in a fresh engine.
        using var workspace = new Workspace();
        var source = workspace.Copy(WriterFixture("annots.pdf"));
        var original = File.ReadAllBytes(source);
        await using var engine = await sandbox.StartAsync();
        await using var host = await sandbox.StartWriterHostAsync();
        var opened = await engine.OpenDocumentAsync(source);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;
        var before = await document.ListAnnotationsAsync(0, 0, 64);
        Assert.Equal(Status.Ok, before.Status);
        var count = (int)Math.Min(before.Value!.Total, 64);
        Assert.True(count > 0, "the fixture has annotations on its first page");
        for (uint i = 0; i < count; i++)
        {
            Assert.Equal(Status.Ok, (await document.SetAnnotationHiddenAsync(0, i, true)).Status);
        }

        var pdfium = workspace.Path_("pdfium.pdf");
        await SaveAsync(document, pdfium);
        var (reply, normalized) = await NormalizeAsync(host, source, pdfium, workspace.Path_("normalized.pdf"));
        Assert.True(reply.Ok, string.Join("; ", reply.IssueMessages));
        Assert.True(normalized.AsSpan(0, original.Length).SequenceEqual(original));

        await using var fresh = await sandbox.StartAsync();
        var reopened = await fresh.OpenDocumentAsync(workspace.Path_("normalized.pdf"));
        Assert.Equal(Status.Ok, reopened.Status);
        await using var again = reopened.Value!;
        var after = await again.ListAnnotationsAsync(0, 0, 64);
        Assert.Equal(Status.Ok, after.Status);
        Assert.Equal(before.Value.Flags, after.Value!.Flags);
        Assert.Equal(before.Value.Subtypes, after.Value.Subtypes);

        // And the document in the first engine still shows them hidden and still reports the file's flags (the mirror was put back after the save).
        var stillThere = await document.ListAnnotationsAsync(0, 0, 64);
        Assert.Equal(before.Value.Flags, stillThere.Value!.Flags);
    }

    [Fact]
    public async Task TheReadSideAnswersThroughTheSandboxForTextAnnotationsAndForms()
    {
        // TM-01 and AC-02 (the reads), FM-01 (the list and the detail), FM-19 (the highlight) over the real pipe, with the broker's reply checks in the path.
        using var workspace = new Workspace();
        await using var engine = await sandbox.StartAsync();

        var hello = await engine.OpenDocumentAsync(workspace.Copy(SandboxFixture.FixturePath("hello_world.pdf"), "hello.pdf"));
        Assert.Equal(Status.Ok, hello.Status);
        await using (var document = hello.Value!)
        {
            var text = await document.GetPageTextAsync(0, 0, 2048);
            Assert.Equal(Status.Ok, text.Status);
            Assert.True(text.Value!.Total > 0);
            var quads = await document.GetTextQuadsAsync(0, 0, Math.Min(text.Value.Total, 200000), 0);
            Assert.Equal(Status.Ok, quads.Status);
            Assert.True(quads.Value!.TotalQuads >= 1);
            Assert.Equal(quads.Value.Xs.Length, quads.Value.Ys.Length);
            Assert.Equal(0, quads.Value.Xs.Length % 4);
            Assert.Equal(Status.InvalidArgument, (await document.GetTextQuadsAsync(5, 0, 10, 0)).Status); // a page the document does not have
            Assert.NotEqual(Status.Ok, (await document.GetFormFieldDetailAsync(99999, 0)).Status);
        }

        var annots = await engine.OpenDocumentAsync(workspace.Copy(WriterFixture("annots.pdf"), "annots.pdf"));
        Assert.Equal(Status.Ok, annots.Status);
        await using (var document = annots.Value!)
        {
            var listed = await document.ListAnnotationsAsync(0, 0, 64);
            Assert.Equal(Status.Ok, listed.Status);
            Assert.True(listed.Value!.Subtypes.Length > 0);
            var one = await document.GetAnnotationAsync(0, 0);
            Assert.Equal(Status.Ok, one.Status);
            Assert.Equal(listed.Value.Subtypes[0], one.Value!.Subtype);
            var counts = await document.CountAnnotationsAsync(0, document.PageCount);
            Assert.Equal(Status.Ok, counts.Status);
            Assert.Equal((int)document.PageCount, counts.Value!.Counts.Length);
            Assert.NotEqual(Status.Ok, (await document.GetAnnotationAsync(0, 99999)).Status);
        }

        var form = await engine.OpenDocumentAsync(workspace.Copy(SandboxFixture.FixturePath("acroform_filled.pdf"), "form.pdf"));
        Assert.Equal(Status.Ok, form.Status);
        await using (var document = form.Value!)
        {
            var (total, fields) = await FieldsAsync(document);
            Assert.True(total > 0);
            var detail = await document.GetFormFieldDetailAsync(fields.FieldIds[0], 0);
            Assert.Equal(Status.Ok, detail.Status);
            Assert.Equal(fields.Types[0], detail.Value!.Type);

            // The highlight changes the pixels over a widget and only when asked for, and the next tile without it is as before.
            async Task<byte[]> Tile(uint flags)
            {
                var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 640, 800, flags), 1);
                Assert.Equal(Status.Ok, result.Status);
                using var lease = result.Value!;
                var bytes = new byte[640 * 800 * 4];
                for (var y = 0; y < 800; y++)
                {
                    lease.Pixels.Slice(y * lease.Stride, 640 * 4).CopyTo(bytes.AsSpan(y * 640 * 4, 640 * 4));
                }

                return bytes;
            }

            var plain = await Tile(RenderTileArgs.Annotations | RenderTileArgs.FormFields);
            var lit = await Tile(RenderTileArgs.Annotations | RenderTileArgs.FormFields | RenderTileArgs.FieldHighlight);
            var plainAgain = await Tile(RenderTileArgs.Annotations | RenderTileArgs.FormFields);
            Assert.False(plain.AsSpan().SequenceEqual(lit), "the field highlight changed nothing");
            Assert.True(plain.AsSpan().SequenceEqual(plainAgain), "the highlight stayed on after it was switched off");
        }
    }

    [Fact]
    public async Task TheEngineRefusesWhatItDoesNotServeAndTheHandleIsGoneAfterwards()
    {
        using var workspace = new Workspace();
        var source = workspace.Copy(SandboxFixture.FixturePath("hello_world.pdf"));
        await using var engine = await sandbox.StartAsync();
        var opened = await engine.OpenDocumentAsync(source);
        await using var document = opened.Value!;

        var output = workspace.Path_("flattened.pdf");
        using (var handle = File.OpenHandle(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var refused = await document.SaveDocumentAsync(2, handle); // the flattened copy is the writer host's
            Assert.Equal(Status.Unsupported, refused.Status);
        }

        Assert.Empty(File.ReadAllBytes(output));
        // The engine is still the same engine and still answers.
        Assert.Equal(Status.Ok, (await document.GetInfoAsync()).Status);
        Assert.Equal(0, engine.RestartCount);
    }
}
