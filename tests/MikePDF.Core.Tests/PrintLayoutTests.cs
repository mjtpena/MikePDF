using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;

namespace MikePDF.Core.Tests;

/// <summary>print.md 3.4 and criteria PRINT-03, PRINT-04, PRINT-18: page to paper geometry, band planning, N-up, copies.</summary>
public sealed class PrintLayoutTests
{
    // A4 at 600 dpi with a 4 mm unprintable margin on every side, as a printer reports it: 4,960 x 7,016 px less 2 x 94.
    private static readonly PaperArea A4At600 = new(4772, 6828, 600, 600);
    private static readonly PaperArea Letter300 = new(2400, 3200, 300, 300);

    private static PagePlacement Place(double w, double h, PaperArea area, PrintScaling scaling = PrintScaling.Shrink, double percent = 100, bool autoRotate = true) =>
        PrintLayout.Place(new SizeD(w, h), area, scaling, percent, autoRotate);

    // ---- the four modes ----

    [Fact]
    public void ShrinkLeavesASmallPageAtActualSizeAndCentresIt()
    {
        var p = Place(300, 400, A4At600);
        Assert.Equal(1.0, p.Scale);
        Assert.Equal(2500, p.Width); // 300 pt at 600 dpi
        Assert.Equal(3333, p.Height); // 400 pt = 3333.33 px
        Assert.Equal((4772 - 2500) / 2, p.X);
        Assert.Equal((int)Math.Floor((6828 - 3333) / 2.0), p.Y);
        Assert.False(p.Clipped);
        Assert.False(p.Rotated);
    }

    [Fact]
    public void ShrinkScalesALargePageDownToFitTheLimitingSide()
    {
        var p = Place(1224, 1584, Letter300); // a tabloid-sized page on Letter paper: the width is the tight side
        var fit = 2400 / (1224 * 300 / 72.0);
        Assert.Equal(fit, p.Scale, 6);
        Assert.Equal(2400, p.Width);
        Assert.True(p.Height <= 3200);
        Assert.False(p.Clipped);
    }

    [Fact]
    public void FitScalesASmallPageUpToTheArea()
    {
        var p = Place(300, 400, Letter300, PrintScaling.Fit);
        Assert.True(p.Scale > 1);
        Assert.Equal(2400, p.Width); // the width is the tight side: 300 x 400 is 3:4 and the area 3:4
        Assert.Equal(3200, p.Height);
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void ActualSizeIsOnePointPerSeventySecondOfAnInchAndMayBeCutOff()
    {
        var small = Place(612, 792, A4At600, PrintScaling.Actual);
        Assert.Equal(1.0, small.Scale);
        Assert.Equal(5100, small.Width);
        Assert.Equal(6600, small.Height);
        Assert.True(small.Clipped); // Letter is 5,100 px wide, the A4 area 4,772
        Assert.True(small.X < 0);

        var a4 = Place(595.2756, 841.8898, new PaperArea(5000, 7100, 600, 600), PrintScaling.Actual);
        Assert.False(a4.Clipped);
        Assert.Equal(4961, a4.Width);
        Assert.Equal(7016, a4.Height);
    }

    [Fact]
    public void CustomScaleIsAPercentageOfActualSize()
    {
        var p = Place(612, 792, A4At600, PrintScaling.Custom, 50);
        Assert.Equal(0.5, p.Scale);
        Assert.Equal(2550, p.Width);
        Assert.Equal(3300, p.Height);
        Assert.False(p.Clipped);
        var big = Place(612, 792, A4At600, PrintScaling.Custom, 400);
        Assert.Equal(4.0, big.Scale);
        Assert.True(big.Clipped);
        // The percentage is clamped to the sheet's 10 to 400 range, and NaN is 100%.
        Assert.Equal(0.1, Place(612, 792, A4At600, PrintScaling.Custom, 1).Scale);
        Assert.Equal(4.0, Place(612, 792, A4At600, PrintScaling.Custom, 4000).Scale);
        Assert.Equal(1.0, Place(612, 792, A4At600, PrintScaling.Custom, double.NaN).Scale);
    }

    // ---- auto-rotate (PRINT-18) ----

    [Fact]
    public void AutoRotateTurnsExactlyThePagesWhoseOrientationDiffersFromThePaper()
    {
        var portraitPaper = Letter300;
        var landscapePaper = new PaperArea(3200, 2400, 300, 300);
        Assert.False(Place(612, 792, portraitPaper).Rotated);
        Assert.True(Place(792, 612, portraitPaper).Rotated);
        Assert.True(Place(612, 792, landscapePaper).Rotated);
        Assert.False(Place(792, 612, landscapePaper).Rotated);
        Assert.False(Place(500, 500, portraitPaper).Rotated); // a square page has no orientation
        Assert.False(Place(612, 792, new PaperArea(2400, 2400, 300, 300)).Rotated); // nor has a square area
        Assert.Equal(1, Place(792, 612, portraitPaper).Rotation);
        Assert.Equal(0, Place(612, 792, portraitPaper).Rotation);
    }

    [Fact]
    public void AMixedDocumentRotatesOnlyItsLandscapePagesOnPortraitPaper()
    {
        SizeD[] pages = [new(612, 792), new(792, 612), new(595, 842), new(842, 595), new(612, 612)];
        var on = pages.Select(p => PrintLayout.Place(p, Letter300, PrintScaling.Shrink, 100, autoRotate: true).Rotated).ToArray();
        var off = pages.Select(p => PrintLayout.Place(p, Letter300, PrintScaling.Shrink, 100, autoRotate: false).Rotated).ToArray();
        Assert.Equal([false, true, false, true, false], on);
        Assert.Equal([false, false, false, false, false], off);
    }

    [Fact]
    public void RotationMakesALandscapePageFitLargerOnPortraitPaper()
    {
        var on = Place(792, 612, Letter300, PrintScaling.Fit, autoRotate: true);
        var off = Place(792, 612, Letter300, PrintScaling.Fit, autoRotate: false);
        Assert.True(on.Scale > off.Scale);
        Assert.True(on.Height > on.Width && off.Width > off.Height); // the rotated page is the other way round
    }

    // ---- the four rotations and a CropBox away from the origin ----

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ThePageIsItsDisplayedBoxWhateverItsRotateAndCropBoxOrigin(int rotate)
    {
        var geometry = new PageGeometry(new PdfRect(0, 0, 612, 792), new PdfRect(50, 30, 450, 330), rotate); // a 400 x 300 CropBox that does not start at (0, 0)
        var placement = PrintLayout.Place(geometry.Size, Letter300, PrintScaling.Actual, 100, autoRotate: false);
        var turned = rotate is 90 or 270;
        Assert.Equal(turned ? 300 : 400, geometry.Size.Width);
        Assert.Equal((int)Math.Round(geometry.Size.Width * 300 / 72), placement.Width);
        Assert.Equal((int)Math.Round(geometry.Size.Height * 300 / 72), placement.Height);
        Assert.True(Math.Abs((placement.X * 2) + placement.Width - Letter300.WidthPx) <= 1);
        Assert.True(Math.Abs((placement.Y * 2) + placement.Height - Letter300.HeightPx) <= 1);
    }

    [Fact]
    public void AutoRotateNormalisesAPageThatIsDisplayedLandscapeBecauseOfItsRotate()
    {
        // Portrait MediaBox with /Rotate 90 is displayed landscape: on portrait paper it is turned back by the layout, not twice.
        var geometry = new PageGeometry(new PdfRect(0, 0, 612, 792), null, 90);
        Assert.True(PrintLayout.Place(geometry.Size, Letter300, PrintScaling.Shrink, 100, true).Rotated);
        var upright = new PageGeometry(new PdfRect(0, 0, 612, 792), null, 0);
        Assert.False(PrintLayout.Place(upright.Size, Letter300, PrintScaling.Shrink, 100, true).Rotated);
    }

    // ---- extremes and the property (PRINT-03) ----

    [Theory]
    [InlineData(1, 1, 72)]
    [InlineData(1, 1, 1200)]
    [InlineData(14400, 14400, 72)]
    [InlineData(14400, 14400, 1200)]
    [InlineData(1, 14400, 600)]
    [InlineData(14400, 1, 600)]
    public void ExtremePagesAndResolutionsStayInsideTheAreaForFitAndShrink(double w, double h, int dpi)
    {
        var area = new PaperArea((int)(8.0 * dpi), (int)(10.5 * dpi), dpi, dpi);
        foreach (var mode in new[] { PrintScaling.Fit, PrintScaling.Shrink })
        {
            var p = Place(w, h, area, mode);
            Assert.InRange(p.Width, 1, area.WidthPx);
            Assert.InRange(p.Height, 1, area.HeightPx);
            Assert.False(p.Clipped);
            Assert.True(p.X >= 0 && p.X + p.Width <= area.WidthPx);
            Assert.True(p.Y >= 0 && p.Y + p.Height <= area.HeightPx);
        }
    }

    [Fact]
    public void PropertyThePlacedPageNeverExceedsTheAreaInFitAndShrinkAndIsCentred()
    {
        var random = new Random(20261102);
        for (var i = 0; i < 2000; i++)
        {
            var dpi = random.Next(72, 1201);
            var dpiY = random.Next(5) == 0 ? random.Next(72, 1201) : dpi; // an occasional printer with unequal resolutions
            var area = new PaperArea(random.Next(100, 12000), random.Next(100, 12000), dpi, dpiY);
            var w = Math.Exp(random.NextDouble() * Math.Log(14400)); // 1 to 14,400 pt, log-uniform
            var h = Math.Exp(random.NextDouble() * Math.Log(14400));
            var auto = random.Next(2) == 0;
            foreach (var mode in new[] { PrintScaling.Fit, PrintScaling.Shrink })
            {
                var p = Place(w, h, area, mode, 100, auto);
                Assert.True(p.Width >= 1 && p.Width <= area.WidthPx && p.Height >= 1 && p.Height <= area.HeightPx, $"{w} x {h} on {area} {mode}: {p}");
                Assert.True(p.X >= 0 && p.Y >= 0 && p.X + p.Width <= area.WidthPx && p.Y + p.Height <= area.HeightPx);
                Assert.True(Math.Abs((p.X * 2) + p.Width - area.WidthPx) <= 1, "centred horizontally");
                Assert.True(Math.Abs((p.Y * 2) + p.Height - area.HeightPx) <= 1, "centred vertically");
                // The shape is kept to within a pixel of rounding on each side (a very thin page can round to one pixel).
                var rotated = p.Rotated;
                var expectedRatio = (rotated ? h : w) / (rotated ? w : h) * (dpi / (double)dpiY);
                if (p.Width > 50 && p.Height > 50)
                {
                    Assert.Equal(expectedRatio, p.Width / (double)p.Height, expectedRatio * 0.05);
                }
            }
        }
    }

    [Fact]
    public void ADamagedPageSizeIsTreatedAsOnePoint()
    {
        var p = Place(0, double.NaN, Letter300);
        Assert.True(p.Width >= 1 && p.Height >= 1);
        var huge = Place(1e12, -5, Letter300, PrintScaling.Actual);
        Assert.True(huge.Width >= 1 && huge.Height >= 1);
    }

    // ---- N-up ----

    [Theory]
    [InlineData(1, true, 1, 1)]
    [InlineData(2, true, 2, 1)]
    [InlineData(2, false, 1, 2)]
    [InlineData(4, false, 2, 2)]
    [InlineData(6, true, 3, 2)]
    [InlineData(6, false, 2, 3)]
    [InlineData(9, false, 3, 3)]
    [InlineData(16, true, 4, 4)]
    public void TheGridFollowsThePaperOrientation(int perSheet, bool landscape, int columns, int rows) => Assert.Equal((columns, rows), SheetLayout.Grid(perSheet, landscape));

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(16)]
    public void TheCellsOfASheetStayInsideTheAreaAndDoNotOverlap(int perSheet)
    {
        foreach (var order in new[] { SheetOrder.Horizontal, SheetOrder.Vertical })
        {
            var cells = SheetLayout.Cells(A4At600, perSheet, order);
            Assert.Equal(perSheet, cells.Count);
            foreach (var (x, y, area) in cells)
            {
                Assert.True(x >= 0 && y >= 0 && x + area.WidthPx <= A4At600.WidthPx && y + area.HeightPx <= A4At600.HeightPx);
            }

            for (var i = 0; i < cells.Count; i++)
            {
                for (var j = i + 1; j < cells.Count; j++)
                {
                    var a = cells[i];
                    var b = cells[j];
                    var apart = a.X + a.Area.WidthPx <= b.X || b.X + b.Area.WidthPx <= a.X || a.Y + a.Area.HeightPx <= b.Y || b.Y + b.Area.HeightPx <= a.Y;
                    Assert.True(apart, $"cells {i} and {j} overlap");
                }
            }
        }
    }

    [Fact]
    public void FillOrderRunsAcrossThenDownOrDownThenAcross()
    {
        var across = SheetLayout.Cells(A4At600, 4, SheetOrder.Horizontal);
        Assert.True(across[1].X > across[0].X && across[1].Y == across[0].Y);
        Assert.True(across[2].Y > across[0].Y && across[2].X == across[0].X);
        var down = SheetLayout.Cells(A4At600, 4, SheetOrder.Vertical);
        Assert.True(down[1].Y > down[0].Y && down[1].X == down[0].X);
        Assert.True(down[2].X > down[0].X && down[2].Y == down[0].Y);
    }

    [Fact]
    public void ASingleCellIsTheWholeAreaAndNUpAlwaysFitsWithoutRotating()
    {
        var one = SheetLayout.Cells(A4At600, 1, SheetOrder.Horizontal);
        Assert.Single(one);
        Assert.Equal(A4At600, one[0].Area);
        var planner = new PrintPlanner(new PrintOptions { PagesPerSheet = 4, Scaling = PrintScaling.Actual, AutoRotate = true }, A4At600, new RenderLimits());
        var sheet = planner.PlanSheet([(1, new SizeD(612, 792)), (2, new SizeD(792, 612)), (3, new SizeD(612, 792)), (4, new SizeD(612, 792))]);
        Assert.Equal(4, sheet.Pages.Count);
        Assert.All(sheet.Pages, p => Assert.False(p.Placement.Clipped));
        Assert.All(sheet.Pages, p => Assert.False(p.Placement.Rotated));
        Assert.Equal(1, sheet.Pages[0].Page);
        Assert.True(planner.SheetCount(9) == 3 && planner.SheetCount(8) == 2 && planner.PagesPerSheet == 4);
    }

    [Fact]
    public void AutoPaperOrientationFollowsTheMajorityAndTurnsOverForTwoAndSixUp()
    {
        SizeD[] portrait = [new(612, 792), new(612, 792), new(792, 612)];
        SizeD[] landscape = [new(792, 612), new(792, 612), new(612, 792)];
        Assert.False(SheetLayout.ChooseLandscape(PaperOrientation.Auto, portrait, 1));
        Assert.True(SheetLayout.ChooseLandscape(PaperOrientation.Auto, landscape, 1));
        Assert.True(SheetLayout.ChooseLandscape(PaperOrientation.Auto, portrait, 2));
        Assert.False(SheetLayout.ChooseLandscape(PaperOrientation.Auto, landscape, 2));
        Assert.False(SheetLayout.ChooseLandscape(PaperOrientation.Auto, [new SizeD(100, 100)], 1)); // a tie is portrait
        Assert.True(SheetLayout.ChooseLandscape(PaperOrientation.Landscape, portrait, 1));
        Assert.False(SheetLayout.ChooseLandscape(PaperOrientation.Portrait, landscape, 1));
    }

    // ---- bands (PRINT-04) ----

    [Fact]
    public void AnA4PageAt600DpiIsEightOrNineBandsOf16MiB()
    {
        // 4,960 x 7,016 px (the ADR measured 8 to 9 bands per page).
        var bands = RenderPlanner.Tile(4960, 7016, 16 << 20);
        Assert.InRange(bands.Count, 8, 9);
        Assert.Equal(7016, bands.Sum(b => b.Height));
        Assert.All(bands, b => Assert.True(4L * 4960 * b.Height <= 16 << 20));
    }

    [Fact]
    public void PropertyBandsTileThePageExactlyAndStayUnderTheByteCap()
    {
        var random = new Random(34);
        for (var i = 0; i < 400; i++)
        {
            var dpi = random.Next(72, 1201);
            var widthPt = random.Next(1, 14401);
            var heightPt = random.Next(1, 14401);
            var width = (int)Math.Clamp(Math.Round(widthPt * dpi / 72.0), 1, RenderLimits.ProtocolMaxPageSide);
            var height = (int)Math.Clamp(Math.Round(heightPt * dpi / 72.0), 1, RenderLimits.ProtocolMaxPageSide);
            var cap = random.Next(3) switch { 0 => 4 << 20, 1 => 16 << 20, _ => (1 << 20) + random.Next(1 << 20) };
            var bands = RenderPlanner.Tile(width, height, cap);
            var next = 0;
            foreach (var (top, rows) in bands)
            {
                Assert.Equal(next, top); // no gap, no overlap
                Assert.InRange(rows, 1, RenderLimits.ProtocolMaxBandHeight);
                Assert.True(4L * width * rows <= cap || rows == 1, $"{width}x{height} band of {rows} rows is over {cap}");
                next = top + rows;
            }

            Assert.Equal(height, next); // the heights add up to the page
            Assert.True(bands.Take(bands.Count - 1).All(b => b.Height == bands[0].Height)); // only the last one is short
        }
    }

    [Fact]
    public void OneBandPerPageIsTheWholePage()
    {
        var bands = RenderPlanner.Tile(2480, 3508, 64 << 20, singleBand: true);
        Assert.Equal([(0, 3508)], bands);
    }

    [Fact]
    public void AFullyVisiblePageGetsBandsWhoseDestinationsTileItsRectangleExactly()
    {
        var placement = Place(595, 842, A4At600, PrintScaling.Fit);
        var plan = RenderPlanner.Plan(placement, A4At600, new RenderLimits());
        Assert.False(plan.Stretched);
        Assert.Equal(placement.Width, plan.WidthPx);
        Assert.Equal(placement.Height, plan.HeightPx);
        Assert.Equal(600u, plan.Dpi);
        var rows = placement.Y;
        foreach (var band in plan.Bands)
        {
            Assert.Equal(rows, band.DestTop);
            Assert.Equal(band.Height, band.DestHeight); // one render row is one paper row: a blit with no stretching
            rows += band.DestHeight;
        }

        Assert.Equal(placement.Y + placement.Height, rows);
    }

    [Fact]
    public void ADeviceAbove600DpiIsRenderedAt600AndStretched()
    {
        var area = new PaperArea(10400, 13600, 1200, 1200);
        var placement = Place(612, 792, area, PrintScaling.Actual);
        Assert.False(placement.Clipped);
        var plan = RenderPlanner.Plan(placement, area, new RenderLimits());
        Assert.True(plan.Stretched);
        Assert.Equal(600u, plan.Dpi);
        Assert.Equal(placement.Width / 2, plan.WidthPx);
        var rows = placement.Y;
        foreach (var band in plan.Bands)
        {
            Assert.Equal(rows, band.DestTop); // neighbouring bands meet on the paper even when the scale is not a whole number
            rows += band.DestHeight;
        }

        Assert.Equal(placement.Y + placement.Height, rows);
    }

    [Fact]
    public void TheDpiCapsAreAppliedAs300ForImageModeAnd150ForLowQuality()
    {
        var placement = Place(612, 792, A4At600, PrintScaling.Actual);
        var image = RenderLimits.For(new PrintOptions { AsImage = true }, lowQualityOnly: false);
        var imagePlan = RenderPlanner.Plan(placement, A4At600, image);
        Assert.Equal(300u, imagePlan.Dpi);
        Assert.Single(imagePlan.Bands); // one bitmap for the page
        Assert.Equal(imagePlan.HeightPx, imagePlan.Bands[0].Height);

        var low = RenderLimits.For(new PrintOptions(), lowQualityOnly: true);
        Assert.Equal(150u, RenderPlanner.Plan(placement, A4At600, low).Dpi);
        var both = RenderLimits.For(new PrintOptions { AsImage = true }, lowQualityOnly: true);
        Assert.Equal(150u, RenderPlanner.Plan(placement, A4At600, both).Dpi);
        Assert.Equal(600, RenderLimits.For(new PrintOptions(), lowQualityOnly: false).MaxDpi);
    }

    [Fact]
    public void ABandThatLiesOffThePaperIsNotRequested()
    {
        // Actual size on a page twice as tall as the paper: the lower half is cut off and never rendered.
        var paper = new PaperArea(2400, 3000, 300, 300);
        var placement = Place(612, 2400, paper, PrintScaling.Actual, autoRotate: false); // 10,000 px tall, centred: it starts above the paper
        Assert.True(placement.Y < 0 && placement.Y + placement.Height > paper.HeightPx);
        var plan = RenderPlanner.Plan(placement, paper, new RenderLimits { BandBytes = 4 << 20 });
        Assert.NotEmpty(plan.Bands);
        Assert.True(plan.Bands.Count < RenderPlanner.Tile(plan.WidthPx, plan.HeightPx, 4 << 20).Count);
        Assert.All(plan.Bands, b => Assert.True(b.DestTop + b.DestHeight > 0 && b.DestTop < paper.HeightPx));
    }

    [Fact]
    public void APageEntirelyOffThePaperHasNothingToRender()
    {
        var paper = new PaperArea(1000, 1000, 300, 300);
        Assert.Empty(RenderPlanner.Plan(new PagePlacement(false, 1, 1000, 0, 500, 500, true), paper, new RenderLimits()).Bands);
        Assert.Empty(RenderPlanner.Plan(new PagePlacement(false, 1, -500, 0, 500, 500, true), paper, new RenderLimits()).Bands);
    }

    // ---- hostile pages (PRINT-24, the part that needs no engine) ----

    [Theory]
    [InlineData(14400.0, 14400.0, 600, PrintScaling.Actual)]
    [InlineData(14400.0, 14400.0, 1200, PrintScaling.Custom)]
    [InlineData(1.0, 14400.0, 1200, PrintScaling.Custom)]
    [InlineData(14400.0, 14400.0, 600, PrintScaling.Shrink)]
    [InlineData(14400.0, 14400.0, 600, PrintScaling.Fit)]
    public void AHugePageIsRenderedWithinThePixelCapTheProtocolAndTheBandCap(double w, double h, int dpi, PrintScaling scaling)
    {
        var paper = new PaperArea(5000, 7000, dpi, dpi);
        var placement = Place(w, h, paper, scaling, 400);
        foreach (var limits in new[] { new RenderLimits(), RenderLimits.For(new PrintOptions { AsImage = true }, false) })
        {
            var plan = RenderPlanner.Plan(placement, paper, limits);
            Assert.True((long)plan.WidthPx * plan.HeightPx <= limits.MaxPagePixels);
            Assert.InRange(plan.WidthPx, 1, RenderLimits.ProtocolMaxPageSide);
            Assert.InRange(plan.HeightPx, 1, RenderLimits.ProtocolMaxPageSide);
            Assert.InRange(plan.Dpi, 72u, 1200u);
            Assert.All(plan.Bands, b => Assert.True(4L * plan.WidthPx * b.Height <= Math.Max(limits.BandBytes, 4L * plan.WidthPx)));
            if (limits.SingleBand)
            {
                Assert.True(4L * plan.WidthPx * plan.HeightPx <= limits.SlotBytes);
                Assert.True(plan.HeightPx <= RenderLimits.ProtocolMaxBandHeight);
            }
        }
    }

    // ---- copies ----

    [Theory]
    [InlineData(1, true, true, true, 1, false, 1, 1)]
    [InlineData(3, true, true, true, 3, true, 1, 1)] // the driver keeps both: one job with dmCopies 3
    [InlineData(3, false, true, true, 3, false, 1, 1)]
    [InlineData(3, true, true, false, 1, false, 3, 1)] // the driver clears collate: we repeat the whole run
    [InlineData(3, true, false, false, 1, false, 3, 1)] // the driver clears copies: collated, we repeat the whole run
    [InlineData(3, false, false, false, 1, false, 1, 3)] // uncollated: each sheet 3 times in a row
    [InlineData(500, true, true, true, 99, true, 1, 1)] // clamped to 99
    [InlineData(0, true, true, true, 1, false, 1, 1)]
    public void TheCopiesAreMadeByTheDriverWhenItKeptThemAndByUsWhenItDidNot(int requested, bool collate, bool keepsCopies, bool keepsCollate, int deviceCopies, bool deviceCollate, int repeats, int each)
    {
        var plan = CopyPlan.Resolve(requested, collate, keepsCopies, keepsCollate);
        Assert.Equal(new CopyPlan(deviceCopies, deviceCollate, repeats, each), plan);
        Assert.Equal(Math.Clamp(requested, 1, 99), plan.Multiplier);
    }
}
