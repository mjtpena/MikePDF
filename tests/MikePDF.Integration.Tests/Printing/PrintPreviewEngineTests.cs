using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>The sheet's preview drawn by the real sandboxed engine: it shows what the options do (annotations, form contents, grey scale, pages per sheet), as the job will print them.</summary>
public sealed class PrintPreviewEngineTests(SandboxFixture sandbox)
{
    private static readonly PaperArea Printable = new(4760, 6816, 600, 600);
    private static readonly PhysicalPaper Physical = new(4960, 7016, 100, 100);

    private static async Task<PreviewImage> RenderAsync(PrintHarness harness, byte[] pdf, PrintOptions options, params int[] pages)
    {
        await using var session = await harness.OpenAsync(pdf, options);
        return await PrintPreview.RenderAsync(session.Source, new PreviewRequest(options, pages, Printable, Physical, LongEdgePx: 700), CancellationToken.None);
    }

    private static int Differing(PreviewImage a, PreviewImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var count = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4)
        {
            if (a.Bgra[i] != b.Bgra[i] || a.Bgra[i + 1] != b.Bgra[i + 1] || a.Bgra[i + 2] != b.Bgra[i + 2])
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task ThePreviewOfARealPageHasInkInTheMiddleAndWhiteMarginsAround()
    {
        await using var harness = new PrintHarness(sandbox);
        var image = await RenderAsync(harness, PrintFixtures.Text(2), new PrintOptions { Scaling = PrintScaling.Fit, FormFields = false }, 1);
        Assert.False(image.AnyFailed);
        var dark = 0;
        for (var y = image.Height / 4; y < image.Height * 3 / 4; y++)
        {
            for (var x = image.Width / 6; x < image.Width * 5 / 6; x++)
            {
                if (image.Bgra[((y * image.Width) + x) * 4] < 100)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > 200, $"{dark} dark pixels where the text is");
        for (var x = 0; x < image.Width; x++)
        {
            Assert.True(image.Bgra[x * 4] >= 190, "the top edge is paper (or the dashed margin guide)");
        }
    }

    [Fact]
    public async Task TurningAnnotationsOffChangesThePreviewOfAnAnnotatedPage()
    {
        await using var harness = new PrintHarness(sandbox);
        var on = await RenderAsync(harness, PrintFixtures.Annotated(), new PrintOptions { Annotations = true, Scaling = PrintScaling.Fit }, 1);
        var off = await RenderAsync(harness, PrintFixtures.Annotated(), new PrintOptions { Annotations = false, Scaling = PrintScaling.Fit }, 1);
        Assert.True(Differing(on, off) > 50, "the annotation that carries the print flag is in one picture and not in the other");
    }

    [Fact]
    public async Task TurningFormFieldsOffChangesThePreviewOfAFilledForm()
    {
        await using var harness = new PrintHarness(sandbox);
        var on = await RenderAsync(harness, PrintFixtures.Form(), new PrintOptions { FormFields = true, Scaling = PrintScaling.Fit }, 1);
        var off = await RenderAsync(harness, PrintFixtures.Form(), new PrintOptions { FormFields = false, Scaling = PrintScaling.Fit }, 1);
        Assert.True(Differing(on, off) > 20, "the field's contents are in one picture and not in the other");
    }

    [Fact]
    public async Task GreyScaleAndPagesPerSheetShowInThePreviewOfRealPages()
    {
        await using var harness = new PrintHarness(sandbox);
        var colour = await RenderAsync(harness, PrintFixtures.Annotated(), new PrintOptions { Annotations = true, Scaling = PrintScaling.Fit }, 1);
        var grey = await RenderAsync(harness, PrintFixtures.Annotated(), new PrintOptions { Annotations = true, Scaling = PrintScaling.Fit, Grayscale = true }, 1);
        var anyColour = false;
        for (var i = 0; i < colour.Bgra.Length; i += 4)
        {
            anyColour |= colour.Bgra[i] != colour.Bgra[i + 2];
            Assert.True(grey.Bgra[i] == grey.Bgra[i + 1] && grey.Bgra[i + 1] == grey.Bgra[i + 2], "grey scale has equal channels");
        }

        Assert.True(anyColour, "the annotated fixture has colour in it");

        var four = await RenderAsync(harness, PrintFixtures.Text(4), new PrintOptions { PagesPerSheet = 4 }, 1, 2, 3, 4);
        Assert.Equal(4, four.Pages.Count);
        Assert.All(four.Pages, p => Assert.False(p.Failed));
    }
}
