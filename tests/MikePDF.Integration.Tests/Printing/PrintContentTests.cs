using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// The pipeline with the real sandboxed engine and a printer that keeps pixels in memory: what is drawn, where, with which flags. No printer or driver is involved, so these
/// always run. Criteria PRINT-06, -14, -15, -16, -17, -18 and -24.
/// </summary>
public sealed class PrintContentTests(SandboxFixture sandbox)
{
    private static readonly PaperArea LetterAt100 = new(850, 1100, 100, 100);
    private static readonly PaperArea A4At150 = new(1240, 1754, 150, 150);

    private static PrintJobRequest Request(int pageCount, PrintOptions options) => new()
    {
        PrinterName = "Memory",
        DocumentName = "Print content test",
        Pages = PageSelection.Resolve(PrintScope.AllPages, pageCount, 1, null, null, PageSubset.All).Pages,
        Options = options,
    };

    private static bool IsWhite((byte Blue, byte Green, byte Red) pixel) => pixel is { Blue: 255, Green: 255, Red: 255 };

    /// <summary>The pixel of a Letter page's point (x, y in PDF points, origin bottom left) on a sheet that has the page at <paramref name="placement"/>.</summary>
    private static (int X, int Y) At(PagePlacement placement, double x, double y) =>
        (placement.X + (int)(x * placement.Width / 612), placement.Y + (int)((792 - y) * placement.Height / 792));

    // ---- PRINT-06: bands are a partition of the page ----
    // The criterion says "pixel for pixel". With PDFium that holds for solid content (the engine's own test) and does NOT hold for text, hairlines and the edges of filled rectangles:
    // the page's matrix is computed in floats with the band's offset in it, so an edge that falls on a half pixel can round to the other side in one render and not in the other
    // (measured on these fixtures: 0.1% of the pixels, on a handful of edge rows, never near a band boundary). The tests below pin what is true: bands are exactly reproducible, they
    // agree with the whole-page render except for those edge pixels, and no seam appears at a boundary.

    private static (double Percent, int BoundaryDiffs, int MaxLevel) CompareTilings(byte[] whole, byte[] tiled, int width, IReadOnlyList<(int Top, int Height)> bands)
    {
        var different = 0;
        var boundaryDiffs = 0;
        var maxLevel = 0;
        var boundaries = bands.Skip(1).Select(b => b.Top).ToHashSet();
        for (var i = 0; i < whole.Length; i += 4)
        {
            var level = Math.Max(Math.Abs(whole[i] - tiled[i]), Math.Max(Math.Abs(whole[i + 1] - tiled[i + 1]), Math.Abs(whole[i + 2] - tiled[i + 2])));
            if (level > 8)
            {
                different++;
                var row = i / 4 / width;
                if (boundaries.Contains(row) || boundaries.Contains(row + 1))
                {
                    boundaryDiffs++;
                }
            }

            maxLevel = Math.Max(maxLevel, level);
        }

        return (100.0 * different / (whole.Length / 4), boundaryDiffs, maxLevel);
    }

    [Fact]
    public async Task TheBandsOfAPageAgreeWithTheWholePageRenderAt150DpiExceptForEdgePixelsAndShowNoSeam()
    {
        await using var harness = new PrintHarness(sandbox);
        await using var session = await harness.OpenAsync(PrintFixtures.Mix(withPoster: false), new PrintOptions { AsImage = true }); // 64 MiB slots: a whole page fits one
        for (var page = 0; page < 5; page++)
        {
            var size = (await session.Source.GetPageSizesAsync(page, 1, default)).Sizes[0];
            var width = (int)Math.Round(size.Width * 150 / 72);
            var height = (int)Math.Round(size.Height * 150 / 72);
            var whole = await RenderRowsAsync(session.Source, page, width, height, 0, height);
            var again = await RenderRowsAsync(session.Source, page, width, height, 0, height);
            Assert.True(whole.AsSpan().SequenceEqual(again), "the same band twice is the same pixels");
            var tiling = RenderPlanner.Tile(width, height, 1 << 20);
            var tiled = new List<byte>(whole.Length);
            foreach (var (top, rows) in tiling)
            {
                tiled.AddRange(await RenderRowsAsync(session.Source, page, width, height, top, rows));
            }

            var (percent, seams, _) = CompareTilings(whole, tiled.ToArray(), width, tiling);
            Assert.True(percent < 0.3, $"page {page + 1}: {percent:F3}% of the pixels differ from the whole-page render by more than 8 levels");
            Assert.Equal(0, seams);
        }
    }

    [Fact]
    public async Task TwoDifferentTilingsOfAPageAt600DpiAgreeExceptForEdgePixels()
    {
        await using var harness = new PrintHarness(sandbox);
        await using var session = await harness.OpenAsync(PrintFixtures.Mix(withPoster: false));
        const int width = 5100;
        const int height = 6600; // Letter at 600 dpi
        const int from = 2400;
        const int rows = 600;
        var oneBand = await RenderRowsAsync(session.Source, 0, width, height, from, rows);
        var pieces = new List<byte>(oneBand.Length);
        var tiling = new List<(int Top, int Height)>();
        for (var top = from; top < from + rows; top += 150)
        {
            pieces.AddRange(await RenderRowsAsync(session.Source, 0, width, height, top, 150));
            tiling.Add((top - from, 150));
        }

        var (percent, seams, _) = CompareTilings(oneBand, pieces.ToArray(), width, tiling);
        Assert.True(percent < 0.3, $"{percent:F3}% of the pixels differ");
        Assert.Equal(0, seams);
        Assert.Contains(oneBand, b => b != 255); // the rows were not all white: this compared something
    }

    private static async Task<byte[]> RenderRowsAsync(IPrintSource source, int page, int width, int height, int top, int rows)
    {
        var args = new RenderBandArgs((uint)page, 150, 0, (uint)width, (uint)height, (uint)top, (uint)rows, 5);
        var result = await source.RenderBandAsync(args, 1, TimeSpan.FromSeconds(60), default);
        Assert.True(result.Status == Status.Ok, $"{result.Status} {result.Detail}");
        using var band = result.Band!;
        return band.Pixels.ToArray();
    }

    // ---- PRINT-14, PRINT-15: what the annotation and form options do ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnnotationsPrintOnlyWhenOnAndOnlyThoseWithThePrintFlag(bool annotations)
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { Annotations = annotations, Scaling = PrintScaling.Fit };
        await using var session = await harness.OpenAsync(PrintFixtures.Annotated(), options);
        var device = new BitmapDevice(LetterAt100);
        var result = await PrintJob.RunAsync(Request(1, options), session.Source, new FixedDeviceFactory(device));

        Assert.True(result.Completed, result.Detail);
        var placement = PrintLayout.Place(new SizeD(612, 792), LetterAt100, PrintScaling.Fit, 100, true);
        var (rx, ry) = At(placement, 150, 650); // the middle of the red, print-flagged square
        var (bx, by) = At(placement, 350, 650); // the middle of the blue square that is shown on screen only
        var red = device.Pixel(0, rx, ry);
        Assert.Equal(annotations, red.Red > 200 && red.Green < 60 && red.Blue < 60);
        Assert.True(IsWhite(device.Pixel(0, bx, by)), "an annotation without the Print flag is never printed");
        if (!annotations)
        {
            Assert.True(IsWhite(red));
        }

        // The page's own content is there either way (the grey bar).
        var (gx, gy) = At(placement, 300, 70);
        Assert.False(IsWhite(device.Pixel(0, gx, gy)));
    }

    [Fact]
    public async Task NoFormFieldHighlightEverPrints()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { FormFields = true, Annotations = true, Scaling = PrintScaling.Fit };
        await using var session = await harness.OpenAsync(PrintFixtures.Form(), options);
        var device = new BitmapDevice(LetterAt100);
        var result = await PrintJob.RunAsync(Request(1, options), session.Source, new FixedDeviceFactory(device));

        Assert.True(result.Completed, result.Detail);
        var placement = PrintLayout.Place(new SizeD(612, 792), LetterAt100, PrintScaling.Fit, 100, true);
        var (rx, ry) = At(placement, 250, 625); // inside the field, where its appearance is empty: this is where an editing highlight would be
        Assert.True(IsWhite(device.Pixel(0, rx, ry)), "the editing highlight never prints");
        // The engine draws the form layer, so the pipeline did not have to fall back to printing without it.
        Assert.False(result.FormLayerUnavailable);
    }

    [Fact]
    public async Task AFilledFormPrintsItsContents()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { FormFields = true, Annotations = true, Scaling = PrintScaling.Fit };
        await using var session = await harness.OpenAsync(PrintFixtures.Form(), options);
        var device = new BitmapDevice(LetterAt100);
        var result = await PrintJob.RunAsync(Request(1, options), session.Source, new FixedDeviceFactory(device));

        Assert.True(result.Completed, result.Detail);
        // PDFium draws a widget only through its form-fill environment (FPDF_FFLDraw): FPDF_ANNOT never shows one. The engine makes one when RenderBand carries flag bit 1, so the field's
        // appearance is on the paper. (Until then the pipeline fell back to printing without the layer and this test skipped itself.)
        Assert.False(result.FormLayerUnavailable, "PRINT-15: the engine must accept RenderBand's form flag");
        var placement = PrintLayout.Place(new SizeD(612, 792), LetterAt100, PrintScaling.Fit, 100, true);
        var (lx, ly) = At(placement, 150, 625); // where the field's appearance is a green block
        var content = device.Pixel(0, lx, ly);
        Assert.True(content.Green > 120 && content.Red < 40 && content.Blue < 40, $"the field's appearance is printed ({content})");
    }

    // ---- PRINT-16: print as image ----

    [Fact]
    public async Task PrintAsImageSendsOneBitmapPerPageAtNoMoreThan300DpiAndTheSheetCountIsUnchanged()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { AsImage = true, Scaling = PrintScaling.Fit };
        await using var session = await harness.OpenAsync(PrintFixtures.Text(3), options);
        var source = new RecordingSource(session.Source);
        var device = new BitmapDevice(new PaperArea(2400, 3600, 600, 600)); // a 4 x 6 inch sheet on a 600 dpi printer
        var result = await PrintJob.RunAsync(Request(3, options), source, new FixedDeviceFactory(device));

        Assert.True(result.Completed, result.Detail);
        Assert.Equal(3, device.Sheets.Count);
        Assert.Equal(3, device.Draws); // one bitmap per page
        var bands = source.Requests.ToList();
        Assert.Equal(3, bands.Count); // one request, so one bitmap, per page
        Assert.All(bands, r =>
        {
            Assert.Equal(r.PageHeightPx, r.BandHeight);
            Assert.InRange(r.Dpi, 72u, 300u);
        });
        Assert.All(device.Blits, b => Assert.True(b.DestWidth >= 2 * b.SrcWidth - 1, "the 300 dpi bitmap is stretched to the 600 dpi sheet"));
    }

    // ---- PRINT-17, PRINT-18: rotated, cropped and mixed pages land where the viewer would put them ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryPageOfTheMixedDocumentIsPlacedAndRotatedAsTheLayoutSaysAndEqualsTheEngineRenderUnderTheSameTransform(bool autoRotate)
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { Scaling = PrintScaling.Fit, AutoRotate = autoRotate, Orientation = PaperOrientation.Portrait, Annotations = true, FormFields = false };
        await using var session = await harness.OpenAsync(PrintFixtures.Mix(withPoster: false), options);
        var recording = new RecordingSource(session.Source);
        var device = new BitmapDevice(A4At150);
        var result = await PrintJob.RunAsync(Request(5, options), recording, new FixedDeviceFactory(device));
        Assert.True(result.Completed, result.Detail);
        Assert.Equal(5, device.Sheets.Count);

        // PRINT-18: the displayed sizes are portrait, landscape, landscape-after-/Rotate and so on; auto-rotate turns exactly those that differ from the portrait paper.
        var sizes = (await session.Source.GetPageSizesAsync(0, 5, default)).Sizes;
        var rotatedPages = recording.Requests.Where(r => r.Rotation == 1).Select(r => (int)r.Page).Distinct().Order().ToArray();
        var landscapePages = Enumerable.Range(0, 5).Where(i => sizes[i].Width > sizes[i].Height).ToArray();
        Assert.Equal(autoRotate ? landscapePages : [], rotatedPages);
        Assert.Contains(PrintFixtures.MixLandscape, landscapePages);
        Assert.Contains(PrintFixtures.MixRotated, landscapePages); // /Rotate 90 makes a portrait MediaBox a landscape page

        for (var page = 0; page < 5; page++)
        {
            var placement = PrintLayout.Place(sizes[page], A4At150, PrintScaling.Fit, 100, autoRotate);
            var reference = await RenderRowsAsync(session.Source, page, placement.Width, placement.Height, placement.Rotation, 0, placement.Height);
            var sheet = device.Sheets[page];
            for (var row = 0; row < placement.Height; row++)
            {
                var printed = sheet.AsSpan((((placement.Y + row) * A4At150.WidthPx) + placement.X) * 4, placement.Width * 4);
                if (!printed.SequenceEqual(reference.AsSpan(row * placement.Width * 4, placement.Width * 4)))
                {
                    Assert.Fail($"page {page + 1} (autoRotate {autoRotate}): row {row} of the placed page differs from the engine's render under the same transform");
                }
            }

            Assert.True(IsWhite(device.Pixel(page, 0, 0)) || placement.X == 0 && placement.Y == 0); // outside the placed page the paper is blank
        }
    }

    private static async Task<byte[]> RenderRowsAsync(IPrintSource source, int page, int width, int height, ushort rotation, int top, int rows)
    {
        var args = new RenderBandArgs((uint)page, 150, rotation, (uint)width, (uint)height, (uint)top, (uint)rows, 5);
        var result = await source.RenderBandAsync(args, 1, TimeSpan.FromSeconds(60), default);
        Assert.True(result.Status == Status.Ok, $"{result.Status} {result.Detail}");
        using var band = result.Band!;
        return band.Pixels.ToArray();
    }

    // ---- PRINT-24: a hostile document ----

    [Fact]
    public async Task APoster14400PointsWideIsPrintedWithinThePixelCapAndTheProtocolLimitsWithTheRealEngine()
    {
        await using var harness = new PrintHarness(sandbox);
        foreach (var scaling in new[] { PrintScaling.Actual, PrintScaling.Custom, PrintScaling.Fit })
        {
            var options = new PrintOptions { Scaling = scaling, CustomPercent = 400, Copies = int.MaxValue };
            await using var session = await harness.OpenAsync(PrintFixtures.OnePage(14400, 14400), options);
            var source = new RecordingSource(session.Source);
            var device = new NullDevice(new PaperArea(2480, 3508, 300, 300));
            var clock = Stopwatch.StartNew();
            var result = await PrintJob.RunAsync(Request(1, options), source, new FixedDeviceFactory(device));
            clock.Stop();

            Assert.True(result.Completed, $"{scaling}: {result.EngineStatus} {result.Detail}");
            Assert.NotEmpty(source.Requests);
            Assert.All(source.Requests, r =>
            {
                Assert.True((long)r.PageWidthPx * r.PageHeightPx <= RenderLimits.DefaultMaxPagePixels, $"{scaling}: {r.PageWidthPx} x {r.PageHeightPx}");
                Assert.True(4L * r.PageWidthPx * r.BandHeight <= PrintSession.BandSlotBytes);
            });
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"{scaling} took {clock.Elapsed}");
        }
    }

    [Fact]
    public async Task HostilePrintPreferencesAreClampedOrIgnoredBeforeTheyBecomeDefaults()
    {
        await using var harness = new PrintHarness(sandbox);
        await using var session = await harness.OpenAsync(PrintFixtures.HostilePreferences());
        var preferences = await session.Source.GetViewerPreferencesAsync(default);
        Assert.NotNull(preferences);
        Assert.InRange((int)preferences.NumCopies, 0, 99);
        var defaults = PrintDefaults.FromViewerPreferences(preferences.PrintScaling, preferences.NumCopies, preferences.Duplex, preferences.RangeFirsts, preferences.RangeLasts, session.PageCount);
        Assert.InRange(defaults.Copies, 1, 99);
        Assert.Equal(PrintScaling.Actual, defaults.Scaling); // /PrintScaling /None is a default, not a command
        Assert.Equal(PrintDuplex.None, defaults.Duplex); // an unknown name is "absent"
        Assert.True(defaults.RangeText is null || PageRangeParser.Parse(defaults.RangeText, session.PageCount).IsValid);
    }
}
