using MikePDF.Render.Cli;
using MikePDF.Render.Engine;

namespace MikePDF.Render.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("all", 5, new[] { 1, 2, 3, 4, 5 })]
    [InlineData("", 3, new[] { 1, 2, 3 })]
    [InlineData("2", 5, new[] { 2 })]
    [InlineData("2-4", 5, new[] { 2, 3, 4 })]
    [InlineData("4-", 6, new[] { 4, 5, 6 })]
    [InlineData("1,3,5-6", 10, new[] { 1, 3, 5, 6 })]
    [InlineData("3,1,2,2-3", 10, new[] { 1, 2, 3 })]
    [InlineData("4-9", 6, new[] { 4, 5, 6 })] // a range that runs past the end is clipped
    public void PageSelectionsResolveAgainstTheDocument(string text, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PageSpec.Parse(text).Resolve(pageCount, out var outside));
        Assert.Empty(outside);
    }

    [Fact]
    public void ASinglePagePastTheEndIsReportedNotDropped()
    {
        var pages = PageSpec.Parse("1,9,12-").Resolve(5, out var outside);
        Assert.Equal([1], pages);
        Assert.Equal([9], outside); // "12-" is an open range, not a named page
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("a")]
    [InlineData("5-2")]
    [InlineData("1,,x")]
    [InlineData("1.5")]
    public void ANonsensePageSelectionIsRefusedWithAMessage(string text) =>
        Assert.Throws<FormatException>(() => PageSpec.Parse(text));

    [Fact]
    public void TheDefaultsAreOneTimesScaleAllPagesAndTheReferenceOn()
    {
        var options = RenderOptions.Parse(["a.pdf"]);
        Assert.Equal(["a.pdf"], options.Inputs);
        Assert.Equal([1.0], options.Scales);
        Assert.Equal("all", options.Pages.Text);
        Assert.True(options.Reference);
        Assert.Equal(0.98, options.SsimThreshold);
        Assert.Equal(98, options.SsimTarget);
        Assert.Equal(1, options.Retries);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TileDeadline);
    }

    [Fact]
    public void ScalesAndDpiBothSetTheScaleList()
    {
        Assert.Equal([1.0, 2.0, 1.5], RenderOptions.Parse(["--scale", "1,2,1.5,2", "x.pdf"]).Scales);
        Assert.Equal([1.0, 2.0, 96.0 / 72.0], RenderOptions.Parse(["--dpi", "72,144,96", "x.pdf"]).Scales);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("90", 1)]
    [InlineData("180", 2)]
    [InlineData("270", 3)]
    public void RotationIsGivenInDegreesAndHeldInQuarterTurns(string degrees, int quarterTurns) =>
        Assert.Equal(quarterTurns, RenderOptions.Parse(["--rotation", degrees, "x.pdf"]).Rotation);

    [Fact]
    public void EveryOptionIsReadAndUnknownOnesAreRefused()
    {
        var options = RenderOptions.Parse([
            "--tier", "smoke", "--manifest", "m.json", "--corpus-dir", "c", "--filter", "pdfium", "--pages", "1-3", "--password", "pw", "--max-pixels", "2000000",
            "--out", "o", "--png", "--no-reference", "--ssim-threshold", "0.95", "--ssim-target", "90", "--baseline", "b.json", "--write-baseline", "w.json", "--no-gate",
            "--tile-deadline-ms", "500", "--open-deadline-ms", "700", "--file-timeout-s", "9", "--retries", "3", "--engine-dir", "e", "--pdfium-dir", "p", "--work-dir", "w", "--quiet"]);
        Assert.Equal(("smoke", "m.json", "c", "pdfium"), (options.Tier, options.ManifestPath, options.CorpusDirectory, options.Filter));
        Assert.Equal(("1-3", "pw", 2_000_000L, "o"), (options.Pages.Text, options.Password, options.MaxPixels, options.OutDirectory));
        Assert.True(options.WritePng);
        Assert.False(options.Reference);
        Assert.Equal((0.95, 90.0), (options.SsimThreshold, options.SsimTarget));
        Assert.Equal(("b.json", "w.json", true), (options.BaselinePath, options.WriteBaselinePath, options.NoGate));
        Assert.Equal((TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(9), 3), (options.TileDeadline, options.OpenDeadline, options.FileTimeout, options.Retries));
        Assert.Equal(("e", "p", "w", true), (options.EngineDirectory, options.PdfiumDirectory, options.WorkDirectory, options.Quiet));

        Assert.Throws<FormatException>(() => RenderOptions.Parse(["--bogus", "x.pdf"]));
    }

    [Theory]
    [InlineData("--scale", "0")]
    [InlineData("--scale", "100")]
    [InlineData("--scale", "x")]
    [InlineData("--dpi", "0")]
    [InlineData("--rotation", "45")]
    [InlineData("--max-pixels", "5")]
    [InlineData("--ssim-threshold", "2")]
    [InlineData("--retries", "99")]
    [InlineData("--tile-deadline-ms", "-1")]
    [InlineData("--pages", "0")]
    public void BadValuesAreRefusedWithAMessage(string option, string value) =>
        Assert.Throws<FormatException>(() => RenderOptions.Parse([option, value, "x.pdf"]));

    [Fact]
    public void AnOptionWithoutItsValueAndAnEmptyRunAreRefused()
    {
        Assert.Throws<FormatException>(() => RenderOptions.Parse(["x.pdf", "--out"]));
        Assert.Throws<FormatException>(() => RenderOptions.Parse([]));
        Assert.Throws<FormatException>(() => RenderOptions.Parse(["--png"]));
        Assert.True(RenderOptions.Parse(["--help"]).Help);
    }

    [Theory]
    [InlineData(200f, 300f, 1.0, 0, 200, 300)]
    [InlineData(200f, 300f, 2.0, 0, 400, 600)]
    [InlineData(200f, 300f, 1.0, 1, 300, 200)] // a quarter turn swaps the sides
    [InlineData(612f, 792f, 96.0 / 72.0, 0, 816, 1056)]
    [InlineData(100.5f, 100.5f, 1.0, 0, 101, 101)] // half away from zero, as std::llround does in the engine
    [InlineData(0f, 0f, 1.0, 0, 1, 1)] // a degenerate page is clamped to one point
    public void ThePlannedDeviceSizeFollowsTheEnginesArithmetic(float width, float height, double scale, int rotation, int expectedWidth, int expectedHeight)
    {
        var plan = PagePlan.For(width, height, scale, rotation, 100_000_000);
        Assert.Equal((expectedWidth, expectedHeight), (plan.Width, plan.Height));
    }

    [Fact]
    public void APageOverThePixelBudgetIsRenderedAtASmallerScale()
    {
        var plan = PagePlan.For(14400f, 14400f, 1.0, 0, 50_000_000);
        Assert.True((long)plan.Width * plan.Height <= 50_000_000);
        Assert.True(plan.Scale < 1f);
        Assert.True((long)plan.Width * plan.Height > 40_000_000, "the reduction should use most of the budget, not shrink the page far below it");
        var untouched = PagePlan.For(612f, 792f, 2.0, 0, 50_000_000);
        Assert.Equal(2f, untouched.Scale);
    }
}
