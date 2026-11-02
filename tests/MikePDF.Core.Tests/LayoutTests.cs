using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Tests;

public sealed class ZoomMathTests
{
    [Theory]
    [InlineData(0.01, 0.10)]
    [InlineData(0.10, 0.10)]
    [InlineData(1.0, 1.0)]
    [InlineData(64.0, 64.0)]
    [InlineData(1000.0, 64.0)]
    [InlineData(double.NaN, 1.0)]
    public void ZoomIsClampedToTenPercentAndSixtyFourHundredPercent(double input, double expected) => Assert.Equal(expected, ZoomMath.Clamp(input));

    [Fact]
    public void StepsGoUpAndDownAndStopAtTheEnds()
    {
        Assert.Equal(1.25, ZoomMath.NextStep(1.0, +1));
        Assert.Equal(0.75, ZoomMath.NextStep(1.0, -1));
        Assert.Equal(1.0, ZoomMath.NextStep(0.8, +1));  // between stops: the next one up
        Assert.Equal(0.75, ZoomMath.NextStep(0.8, -1)); // and down
        Assert.Equal(64.0, ZoomMath.NextStep(64.0, +1));
        Assert.Equal(0.10, ZoomMath.NextStep(0.10, -1));

        var z = 1.0;
        for (var i = 0; i < 40; i++)
        {
            z = ZoomMath.NextStep(z, +1);
        }

        Assert.Equal(64.0, z);
    }

    [Fact]
    public void PixelsPerPointFollowsZoomAndDisplayScale()
    {
        Assert.Equal(96.0 / 72.0, ZoomMath.PixelsPerPoint(1.0, 1.0), 12);
        Assert.Equal(2 * 96.0 / 72.0 * 1.5, ZoomMath.PixelsPerPoint(2.0, 1.5), 12);
    }

    [Fact]
    public void TheEngineScaleIsCappedAtTheProtocolLimit()
    {
        Assert.Equal(10.0, ZoomMath.EngineScale(10.0));
        Assert.Equal(64.0, ZoomMath.EngineScale(85.33));
        // 6400% at 100% display scale needs 85.3 device pixels per point: more than the engine accepts, so tiles are stretched.
        Assert.True(ZoomMath.PixelsPerPoint(64.0, 1.0) > ZoomMath.MaxEngineScale);
    }

    [Fact]
    public void FitWidthAndFitPageMatchTheViewport()
    {
        // A 612 x 792 pt page in a 1000 x 800 px window at 100% display scale with 12 px margins.
        var width = ZoomMath.FitWidth(1000, 612, 1.0, 12);
        Assert.Equal((1000 - 24) / (612 * ZoomMath.DipPerPoint), width, 9);

        var page = ZoomMath.FitPage(1000, 800, new SizeD(612, 792), 1.0, 12);
        Assert.Equal((800 - 24) / (792 * ZoomMath.DipPerPoint), page, 9);
        Assert.True(page < width);
    }
}

public sealed class DocumentLayoutTests
{
    private static readonly SizeD[] Letter3 = [new(612, 792), new(612, 792), new(792, 612)];

    private static DocumentLayout Layout(int turns = 0, double scale = 1.0) => new(Letter3, turns, scale, gapPx: 10, marginPx: 20);

    [Fact]
    public void PagesStackWithGapsAndMargins()
    {
        var l = Layout();
        Assert.Equal(20, l.Top(0));
        Assert.Equal(20 + 792 + 10, l.Top(1));
        Assert.Equal(20 + ((792 + 10) * 2), l.Top(2));
        Assert.Equal(792, l.Width(2));
        Assert.Equal(612, l.Height(2));
        Assert.Equal(l.Top(2) + 612 + 20, l.ContentHeight);
        Assert.Equal(792 + 40, l.ContentWidth);
    }

    [Fact]
    public void ViewRotationSwapsPageSidesAndRebuildsThePositions()
    {
        var l = Layout(turns: 1);
        Assert.Equal(792, l.Width(0));
        Assert.Equal(612, l.Height(0));
        Assert.Equal(612, l.Width(2));
        Assert.Equal(792, l.Height(2));
        Assert.Equal(new SizeD(792, 612), l.SizePt(0));
    }

    [Fact]
    public void PagesAreCentredAndWidePagesStartAtTheLeft()
    {
        var l = Layout();
        Assert.Equal((1000 - 612) / 2.0, l.Left(0, 1000));
        Assert.Equal((792 + 40 - 612) / 2.0, l.Left(0, 300)); // content wider than the viewport: centred in the content
        Assert.Equal(20, l.Left(2, 300));
    }

    [Fact]
    public void PageAtFindsThePageAndTheNearestOneInGaps()
    {
        var l = Layout();
        Assert.Equal(0, l.PageAt(0));
        Assert.Equal(0, l.PageAt(400));
        Assert.Equal(0, l.PageAt(l.Top(0) + 792 + 5)); // in the gap: the page above
        Assert.Equal(1, l.PageAt(l.Top(1)));
        Assert.Equal(2, l.PageAt(1e9));
        Assert.Equal(-1, new DocumentLayout([], 0, 1, 0, 0).PageAt(5));
    }

    [Fact]
    public void VisiblePagesAreThoseIntersectingTheRange()
    {
        var l = Layout();
        Assert.Equal((0, 0), l.VisiblePages(0, 100));
        Assert.Equal((0, 1), l.VisiblePages(700, 900));
        Assert.Equal((1, 1), l.VisiblePages(l.Top(0) + 792 + 1, l.Top(1) + 100)); // starts in the gap
        Assert.Equal((0, 2), l.VisiblePages(0, 1e9));
        var (first, last) = l.VisiblePages(5000, 6000);
        Assert.True(last < first);
        Assert.Equal((0, -1), new DocumentLayout([], 0, 1, 0, 0).VisiblePages(0, 100));
    }

    [Fact]
    public void TheMostVisiblePageIsTheOneWithMostAreaInView()
    {
        var l = Layout();
        Assert.Equal(0, l.MostVisiblePage(0, 800));
        Assert.Equal(1, l.MostVisiblePage(l.Top(0) + 500, l.Top(0) + 500 + 800)); // 292 of page 0, then page 1
        Assert.Equal(2, l.MostVisiblePage(l.Top(2) - 10, l.Top(2) + 600));
        Assert.Equal(-1, new DocumentLayout([], 0, 1, 0, 0).MostVisiblePage(0, 100));
    }

    [Fact]
    public void AnchorsSurviveAChangeOfScale()
    {
        var small = Layout(scale: 1.0);
        var big = Layout(scale: 3.0);
        var anchor = small.AnchorAt(small.Left(1, 1000) + 153, small.Top(1) + 396, 1000);
        Assert.Equal(1, anchor.Page);
        Assert.Equal(0.5, anchor.FractionY, 9);

        var point = big.PointOf(anchor, 1000);
        Assert.Equal(big.Top(1) + (396 * 3), point.Y, 6);
        Assert.Equal(big.Left(1, 1000) + (153 * 3), point.X, 6);
    }

    [Fact]
    public void TilesAboveTheEngineLimitAreRenderedAtTheLimitAndStretched()
    {
        var l = new DocumentLayout([new SizeD(100, 100)], 0, layoutScale: 128, gapPx: 0, marginPx: 0);
        Assert.Equal(64, l.RenderScale);
        Assert.Equal(2.0, l.RenderToLayout);
        Assert.Equal(12800, l.Width(0));
        Assert.Equal((6400L, 6400L), l.RenderSize(0));
    }

    [Fact]
    public void RejectsANonPositiveScale() => Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLayout(Letter3, 0, 0, 0, 0));
}

public sealed class ViewportControllerTests
{
    private static readonly SizeD[] Pages = [.. Enumerable.Repeat(new SizeD(612, 792), 10)];

    private static ViewportController Controller(double width = 1000, double height = 700, double dpi = 1.0)
    {
        var c = new ViewportController();
        c.SetViewport(width, height, dpi);
        c.SetPageSizes(Pages);
        return c;
    }

    [Fact]
    public void ADocumentOpensFitToWidthAtTheTop()
    {
        var c = Controller();
        Assert.Equal(FitMode.Width, c.Fit);
        Assert.Equal(ZoomMath.FitWidth(1000, 612, 1.0, ViewportController.MarginDip), c.Zoom, 9);
        Assert.Equal(0, c.ScrollY);
        Assert.Equal(0, c.CurrentPage);
        Assert.Equal(10, c.PageCount);
    }

    [Fact]
    public void ZoomingKeepsThePointUnderThePointerWhereItWas()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        c.ScrollTo(0, 2000);
        var pointer = new PointD(400, 300);
        var before = c.Layout.AnchorAt(c.ScrollX + pointer.X, c.ScrollY + pointer.Y, c.ViewportWidth);

        c.ZoomBy(2.0, pointer);

        var after = c.Layout.PointOf(before, c.ViewportWidth);
        Assert.Equal(c.ScrollX + pointer.X, after.X, 3);
        Assert.Equal(c.ScrollY + pointer.Y, after.Y, 3);
        Assert.Equal(FitMode.None, c.Fit);
        Assert.Equal(2.0, c.Zoom, 9);
    }

    [Fact]
    public void ZoomIsLimitedToTheSupportedRange()
    {
        var c = Controller();
        c.ZoomTo(0.001);
        Assert.Equal(0.10, c.Zoom);
        c.ZoomTo(1e6);
        Assert.Equal(64.0, c.Zoom);
        c.ZoomStep(+1);
        Assert.Equal(64.0, c.Zoom);
        c.ActualSize();
        Assert.Equal(1.0, c.Zoom);
    }

    [Fact]
    public void ScrollIsClampedToTheContent()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        c.ScrollTo(-50, -50);
        Assert.Equal(0, c.ScrollY);
        c.ScrollTo(0, 1e9);
        Assert.Equal(c.Layout.ContentHeight - c.ViewportHeight, c.ScrollY, 6);
        c.ScrollTo(1e9, 0);
        Assert.Equal(Math.Max(0, c.Layout.ContentWidth - c.ViewportWidth), c.ScrollX, 6);
    }

    [Fact]
    public void FitWidthFollowsTheWindowButAnExactZoomDoesNot()
    {
        var c = Controller();
        var zoom = c.Zoom;
        c.SetViewport(2000, 700, 1.0);
        Assert.True(c.Zoom > zoom);
        Assert.Equal(ZoomMath.FitWidth(2000, 612, 1.0, ViewportController.MarginDip), c.Zoom, 9);

        c.ZoomTo(1.5);
        c.SetViewport(500, 700, 1.0);
        Assert.Equal(1.5, c.Zoom);
    }

    [Fact]
    public void FitPageShowsTheWholePage()
    {
        var c = Controller();
        c.FitPage();
        Assert.Equal(FitMode.Page, c.Fit);
        Assert.True(c.Layout.Height(0) <= c.ViewportHeight);
        c.GoToPage(3);
        Assert.Equal(3, c.CurrentPage);
        Assert.True(c.ScrollY >= 0);
    }

    [Fact]
    public void ARetinaDisplayDoublesThePixelsNotTheLayoutInDips()
    {
        var one = Controller(1000, 700, 1.0);
        var two = Controller(2000, 1400, 2.0);
        one.ZoomTo(1.0);
        two.ZoomTo(1.0);
        Assert.Equal(one.Layout.Width(0) * 2, two.Layout.Width(0));
        Assert.Equal(one.Layout.GapPx * 2, two.Layout.GapPx);
    }

    [Fact]
    public void PageNavigationMovesThePageIndicator()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        Assert.Equal(0, c.CurrentPage);
        c.NextPage();
        Assert.Equal(1, c.CurrentPage);
        c.LastPage();
        Assert.Equal(9, c.CurrentPage);
        c.NextPage();
        Assert.Equal(9, c.CurrentPage);
        c.FirstPage();
        Assert.Equal(0, c.CurrentPage);
        c.PreviousPage();
        Assert.Equal(0, c.CurrentPage);
        c.GoToPage(7);
        Assert.Equal(7, c.CurrentPage);
        c.GoToPage(99);
        Assert.Equal(9, c.CurrentPage);
    }

    [Fact]
    public void PageDownScrollsAScreenAtATime()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        c.ScrollScreen(+1);
        Assert.Equal(700 * 0.9, c.ScrollY, 6);
        c.ScrollScreen(-1);
        Assert.Equal(0, c.ScrollY, 6);
    }

    [Fact]
    public void RotatingKeepsThePageAtTheTop()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        c.GoToPage(4);
        var page = c.CurrentPage;
        c.Rotate(1);
        Assert.Equal(1, c.ViewTurns);
        Assert.Equal(page, c.CurrentPage);
        Assert.Equal(1056, c.Layout.Width(0)); // 792 pt at 100%
        c.Rotate(-1);
        Assert.Equal(0, c.ViewTurns);
        Assert.Equal(page, c.CurrentPage);
        c.Rotate(4);
        Assert.Equal(0, c.ViewTurns);
    }

    [Fact]
    public void LearningMorePageSizesKeepsThePageAtTheTop()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        c.GoToPage(5);
        var fraction = c.TopPosition;
        // Pages before the current one turn out to be smaller than assumed.
        var sizes = Pages.ToArray();
        for (var i = 0; i < 5; i++)
        {
            sizes[i] = new SizeD(300, 400);
        }

        c.SetPageSizes(sizes);
        Assert.Equal(fraction.Page, c.TopPosition.Page);
        Assert.Equal(fraction.Fraction, c.TopPosition.Fraction, 3);
    }

    [Fact]
    public void ASavedViewIsRestored()
    {
        var c = Controller();
        c.Restore(page: 6, fraction: 0.5, zoom: 2.0, FitMode.None, viewTurns: 2);
        Assert.Equal(2.0, c.Zoom);
        Assert.Equal(2, c.ViewTurns);
        Assert.Equal(FitMode.None, c.Fit);
        var (page, fraction) = c.TopPosition;
        Assert.Equal(6, page);
        Assert.Equal(0.5, fraction, 3);
    }

    [Fact]
    public void NothingHappensForAnEmptyDocumentOrAnImpossibleViewport()
    {
        var c = new ViewportController();
        c.SetPageSizes([]);
        c.GoToPage(3);
        c.NextPage();
        c.ZoomBy(2);
        c.SetViewport(0, 0, 1);
        Assert.Equal(-1, c.CurrentPage);
        Assert.Equal(0, c.PageCount);
    }

    [Fact]
    public void TheVersionAdvancesWhenTheViewChanges()
    {
        var c = Controller();
        c.ZoomTo(1.0);
        var v = c.Version;
        c.ScrollBy(0, 10);
        Assert.True(c.Version > v);
        v = c.Version;
        c.ScrollBy(0, 0);
        Assert.Equal(v, c.Version);
    }
}
