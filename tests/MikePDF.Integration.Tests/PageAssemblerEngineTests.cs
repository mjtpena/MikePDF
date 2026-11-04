using System.Text;
using MikePDF.Integration.Tests.Support;
using MikePDF.PdfWriter.Assembly;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// The page assembler's output through the real sandboxed engine (page-organiser PO-15: opens and reads in a fresh engine): the library builds the file from
/// plain bytes, the engine, PDFium and the schema-3 codecs read it back. The finer checks (every link, name and label) are in <c>MikePDF.PdfWriter.Tests</c>
/// against PDFium in process; this is the one test that goes through the sandbox.
/// </summary>
public sealed class PageAssemblerEngineTests(SandboxFixture sandbox)
{
    private static string Letters(GetPageTextReply reply)
    {
        var text = new StringBuilder();
        for (var i = 0; i < reply.Units.Length; i++)
        {
            if ((reply.Flags[i] & 1) == 0)
            {
                text.Append((char)reply.Units[i]);
            }
        }

        return text.ToString();
    }

    [Fact]
    [Trait("Spec", "PO-09")]
    [Trait("Spec", "PO-15")]
    public async Task AnExtractOpensInTheSandboxedEngineWithItsPagesTextBoxesLinksAndBookmarks()
    {
        // The reader document has three pages. Page 2 is the target of a link on page 1 and of two bookmarks; taking pages 3 and 1 (in that order) leaves it out.
        var assembled = PageAssembler.Assemble([new AssemblerSource("reader", ReaderPdf.Build())], AssemblyPlan.Pick(0, [2, 0]));
        Assert.True(assembled.IsSuccess, assembled.IsSuccess ? "" : assembled.Refusal.ToString());
        using var file = new TempFile("extract.pdf", assembled.Output.Bytes.ToArray());

        await using var supervisor = await sandbox.StartAsync();
        var opened = await supervisor.OpenDocumentAsync(file.Path);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;
        Assert.Equal(2u, document.PageCount);

        // Page 1 of the extract is the rotated, cropped page 3 of the source: the boxes and /Rotate are exactly as they were.
        var boxes = await document.GetPageBoxesAsync(0);
        Assert.Equal(Status.Ok, boxes.Status);
        Assert.True(boxes.Value!.HasCropBox);
        Assert.Equal((100f, 150f, 500f, 650f), (boxes.Value.CropLeft, boxes.Value.CropBottom, boxes.Value.CropRight, boxes.Value.CropTop));
        Assert.Equal(1, boxes.Value.Rotation);

        var text = await document.GetPageTextAsync(1, 0, 2048);
        Assert.Equal(Status.Ok, text.Status);
        Assert.Contains(ReaderPdf.FirstLine, Letters(text.Value!), StringComparison.Ordinal);

        // The five links of page 1 were: a GoTo to page 2, a URI, a destination on page 3, a Launch and a destination on page 2. The two that led to the page left out are gone.
        var links = await document.GetPageLinksAsync(1, 0, 512);
        Assert.Equal(Status.Ok, links.Status);
        Assert.Equal(3u, links.Value!.Total);

        // The bookmarks that led to page 2 are gone and the ones that did not are kept, retargeted: "Chapter One" is on what is now page 2, its child on page 1.
        var top = await document.GetOutlineAsync([], 0, 64);
        Assert.Equal(Status.Ok, top.Status);
        Assert.Equal(["Chapter One", "Web"], top.Value!.Titles);
        Assert.Equal(1u, top.Value.Pages[0]);
        var children = await document.GetOutlineAsync([0], 0, 64);
        Assert.Equal(["Section 1.2"], children.Value!.Titles);
        Assert.Equal<uint[]>([0], children.Value.Pages);
        Assert.Equal(0, supervisor.RestartCount);
    }
}
