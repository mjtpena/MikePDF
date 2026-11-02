using MikePDF.Core.Commands;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Persistence;
using MikePDF.Core.Windowing;

namespace MikePDF.Core.Tests;

/// <summary>The page view modes (docs/features/page-view-modes.md): spreads, layout, fit, mode changes, paged navigation, persistence. The numbers are the criteria's ids (PV-nn).</summary>
public sealed class PageViewModeTests
{
    private static readonly SizeD Letter = new(612, 792);

    private static IReadOnlyList<SizeD> Pages(int count, SizeD? size = null) => [.. Enumerable.Repeat(size ?? Letter, count)];

    private static ViewportController Controller(int pages, ViewMode mode = default, double width = 1400, double height = 900, double dpi = 1.0, IReadOnlyList<SizeD>? sizes = null)
    {
        var c = new ViewportController();
        c.SetViewport(width, height, dpi);
        c.SetPageSizes(sizes ?? Pages(pages));
        c.SetMode(mode);
        return c;
    }

    // ---- PV-01: spread arithmetic ------------------------------------------------------------------------------------------------------

    public static TheoryData<int> Counts => new() { 0, 1, 2, 3, 10, 11 };

    private static List<int[]> SpreadLists(SpreadMap map) =>
        [.. Enumerable.Range(0, map.SpreadCount).Select(s => { var (a, b) = map.PagesOf(s); return Enumerable.Range(a, b - a + 1).ToArray(); })];

    [Theory]
    [MemberData(nameof(Counts))]
    public void PV01_SinglePagesAreOneSpreadEach(int count)
    {
        var map = new SpreadMap(count, PageArrangement.OnePage, false);
        Assert.Equal(count, map.SpreadCount);
        Assert.Equal(Enumerable.Range(0, count).Select(i => new[] { i }), SpreadLists(map));
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void PV01_FacingPairsOneAndTwoThreeAndFour(int count)
    {
        var map = new SpreadMap(count, PageArrangement.Facing, false);
        var expected = count <= 1 ? Enumerable.Range(0, count).Select(i => new[] { i }).ToList() : Enumerable.Range(0, (count + 1) / 2).Select(s => s * 2 + 1 < count ? new[] { s * 2, s * 2 + 1 } : new[] { s * 2 }).ToList();
        Assert.Equal(expected, SpreadLists(map));
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void PV01_ACoverIsAloneAndTheRestPairFromPageTwo(int count)
    {
        var map = new SpreadMap(count, PageArrangement.FacingWithCover, false);
        var expected = new List<int[]>();
        if (count > 0)
        {
            expected.Add([0]);
            for (var p = 1; p < count; p += 2)
            {
                expected.Add(p + 1 < count ? [p, p + 1] : [p]);
            }
        }

        if (count <= 1)
        {
            expected = [.. Enumerable.Range(0, count).Select(i => new[] { i })];
        }

        Assert.Equal(expected, SpreadLists(map));
        for (var page = 0; page < count; page++)
        {
            var spread = map.SpreadOf(page);
            var (first, last) = map.PagesOf(spread);
            Assert.InRange(page, first, last);
        }
    }

    [Fact]
    public void PV01_TheSevenPageExamplesOfTheDesignAreRight()
    {
        Assert.Equal([[0, 1], [2, 3], [4, 5], [6]], SpreadLists(new SpreadMap(7, PageArrangement.Facing, false)));
        Assert.Equal([[0], [1, 2], [3, 4], [5, 6]], SpreadLists(new SpreadMap(7, PageArrangement.FacingWithCover, false)));
    }

    [Fact]
    public void PV01_ARightToLeftBindingPutsTheLowerPageOnTheRightAndTheCoverOnTheLeft()
    {
        var map = new SpreadMap(7, PageArrangement.FacingWithCover, rightToLeft: true);
        Assert.Equal((-1, 0), new SpreadMap(7, PageArrangement.FacingWithCover, false).Slots(0)); // LTR: the cover is on the right
        Assert.Equal((0, -1), map.Slots(0));                                                     // RTL: on the left
        Assert.Equal((2, 1), map.Slots(1));                                                      // pages 2-3 read 3-2
        Assert.Equal((6, 5), map.Slots(3));
        var facing = new SpreadMap(7, PageArrangement.Facing, rightToLeft: true);
        Assert.Equal((-1, 6), facing.Slots(3));                                                  // the lone last page on the right
    }

    [Fact]
    public void PV01_ADocumentOfOnePageIsAlwaysOneCentredPage()
    {
        foreach (var arrangement in Enum.GetValues<PageArrangement>())
        {
            var map = new SpreadMap(1, arrangement, false);
            Assert.True(map.IsSingle);
            Assert.Equal(1, map.SpreadCount);
            Assert.Equal((0, -1), map.Slots(0));
        }
    }

    // ---- PV-02: nothing changed for single continuous pages ----------------------------------------------------------------------------

    [Fact]
    public void PV02_SingleContinuousRectanglesAreTheOldLayoutsForAThousandRandomSizeSets()
    {
        var random = new Random(20261103);
        for (var trial = 0; trial < 1000; trial++)
        {
            var sizes = Enumerable.Range(0, random.Next(1, 9)).Select(_ => new SizeD(100 + random.Next(900), 100 + random.Next(900))).ToList();
            var turns = random.Next(4);
            var scale = 0.2 + (random.NextDouble() * 3);
            var gap = random.Next(0, 20);
            var margin = random.Next(0, 30);
            var plain = new DocumentLayout(sizes, turns, scale, gap, margin);
            var explicitSingle = new DocumentLayout(sizes, turns, scale, gap, margin, new SpreadMap(sizes.Count, PageArrangement.OnePage, false), 0);
            double y = margin;
            long maxWidth = 0;
            for (var i = 0; i < sizes.Count; i++)
            {
                var size = QuarterTurns.Rotate(sizes[i], turns);
                var w = Math.Max(1, PageTransform.EngineRounding(size.Width * scale));
                var h = Math.Max(1, PageTransform.EngineRounding(size.Height * scale));
                foreach (var layout in new[] { plain, explicitSingle })
                {
                    Assert.Equal(y, layout.Top(i));
                    Assert.Equal(w, layout.Width(i));
                    Assert.Equal(h, layout.Height(i));
                    Assert.Equal(i, layout.PageAt(y + (h / 2.0)));
                }

                y += h + gap;
                maxWidth = Math.Max(maxWidth, w);
            }

            foreach (var layout in new[] { plain, explicitSingle })
            {
                Assert.Equal(maxWidth + (2.0 * margin), layout.ContentWidth);
                Assert.Equal(y - gap + margin, layout.ContentHeight);
                for (var i = 0; i < sizes.Count; i++)
                {
                    Assert.Equal((Math.Max(900.0, layout.ContentWidth) - layout.Width(i)) / 2, layout.Left(i, 900));
                }
            }
        }
    }

    // ---- PV-03: spread geometry --------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV03_ThePagesOfASpreadAreCentredOnEachOtherAndAGutterApart()
    {
        // A tall page next to a short one: the short one is centred vertically.
        var sizes = new SizeD[] { new(500, 800), new(500, 400), new(500, 800), new(500, 800) };
        var layout = new DocumentLayout(sizes, 0, 1.0, gapPx: 10, marginPx: 20, new SpreadMap(4, PageArrangement.Facing, false), gutterPx: 6);
        Assert.Equal(2, layout.SpreadCount);
        Assert.Equal(800, layout.SpreadHeight(0));
        Assert.Equal(1006, layout.SpreadWidth(0));
        Assert.Equal(20, layout.Top(0));
        Assert.Equal(20 + 200, layout.Top(1));
        Assert.Equal(20 + 800 + 10, layout.SpreadTop(1));
        var left0 = layout.Left(0, 1500);
        Assert.Equal(left0 + 506, layout.Left(1, 1500));
        Assert.Equal((1500 - 1006) / 2.0, left0);
    }

    [Fact]
    public void PV03_ALonePageKeepsItsHalfSoACoverDoesNotJumpToTheMiddle()
    {
        var layout = new DocumentLayout(Pages(3), 0, 1.0, 10, 20, new SpreadMap(3, PageArrangement.FacingWithCover, false), 4);
        // Spread 0 is the cover alone, on the right half; spread 1 is pages 2 and 3.
        Assert.Equal(612 + 4 + 612, layout.SpreadWidth(0));
        Assert.Equal(layout.SpreadWidth(1), layout.SpreadWidth(0));
        Assert.Equal(layout.Left(1, 2000) + 616, layout.Left(0, 2000)); // the cover sits where page 3 does, not centred
        Assert.Equal(layout.Left(0, 2000), layout.Left(2, 2000));
    }

    [Fact]
    public void PV03_InARightToLeftBindingTheCoverIsOnTheLeftAndPagesMirror()
    {
        var layout = new DocumentLayout(Pages(3), 0, 1.0, 10, 20, new SpreadMap(3, PageArrangement.FacingWithCover, rightToLeft: true), 4);
        Assert.Equal(layout.Left(2, 2000), layout.Left(0, 2000));                  // the cover on the left half, above page 3 (index 2)
        Assert.True(layout.Left(2, 2000) < layout.Left(1, 2000));                  // page 3 is left of page 2
    }

    [Fact]
    public void PV03_PageAtPicksThePageOfASpreadByX()
    {
        var layout = new DocumentLayout(Pages(4), 0, 1.0, 10, 20, new SpreadMap(4, PageArrangement.Facing, false), 4);
        var y = layout.Top(0) + 100;
        Assert.Equal(0, layout.PageAt(layout.Left(0, 1600) + 100, y, 1600));
        Assert.Equal(1, layout.PageAt(layout.Left(1, 1600) + 100, y, 1600));
        Assert.Equal(0, layout.PageAt(layout.Left(0, 1600) - 500, y, 1600)); // outside, left: the nearest page
        Assert.Equal(1, layout.PageAt(layout.Left(1, 1600) + 9999, y, 1600));
        Assert.Equal(3, layout.PageAt(layout.Left(3, 1600) + 5, layout.Top(3) + 5, 1600));
    }

    // ---- PV-04: rotation and the projection --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PV04_APagePointRoundTripsThroughTheViewportOnBothPagesOfASpread(int turns)
    {
        // Page sizes are the displayed ones, so a page with /Rotate 90 is simply landscape here; the view turn is applied on top.
        var sizes = new SizeD[] { new(612, 792), new(792, 612), new(300, 500), new(500, 300) };
        var c = Controller(4, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1500, 900, sizes: sizes);
        c.Rotate(turns);
        foreach (var page in new[] { 0, 1, 2, 3 })
        {
            c.GoToPage(page);
            var view = c.Snapshot();
            var projection = PageProjection.For(view, page)!;
            var size = projection.PageSize;
            foreach (var point in new[] { new PointD(10, 20), new PointD(size.Width / 2, size.Height / 2), new PointD(size.Width - 3, size.Height - 4) })
            {
                var back = projection.ViewportToPage(projection.PageToViewport(point));
                Assert.Equal(point.X, back.X, 2);
                Assert.Equal(point.Y, back.Y, 2);
            }

            // And the page under the viewport point of the page's middle is the page itself.
            var middle = projection.PageToViewport(new PointD(size.Width / 2, size.Height / 2));
            Assert.Equal(page, PageProjection.At(view, middle)!.Page);
        }
    }

    // ---- PV-05: fit --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV05_FitWidthOfASpreadFitsBothPagesAndTheGutterToTheWindow()
    {
        var c = Controller(10, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1400, 900);
        c.FitWidth();
        var expected = ZoomMath.FitSpreadWidth(1400, 612 * 2, ViewportController.GutterDip, 1.0, ViewportController.MarginDip);
        Assert.Equal(expected, c.Zoom, 9);
        Assert.True(c.Layout.SpreadWidth(0) + (2 * c.Layout.MarginPx) <= 1400 + 2);
        Assert.True(c.Zoom < ZoomMath.FitWidth(1400, 612, 1.0, ViewportController.MarginDip)); // two pages are smaller than one
    }

    [Fact]
    public void PV05_FitPageOfASpreadFitsItsWidthAndItsHeight()
    {
        var c = Controller(10, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1400, 600);
        c.FitPage();
        Assert.Equal(ZoomMath.FitSpreadPage(1400, 600, new SizeD(1224, 792), ViewportController.GutterDip, 1.0, ViewportController.MarginDip), c.Zoom, 9);
        Assert.True(c.Layout.SpreadHeight(0) + (2 * c.Layout.MarginPx) <= 600 + 2);
    }

    [Fact]
    public void PV05_ACoverIsFittedAsHalfASpread()
    {
        var c = Controller(10, new ViewMode(PageArrangement.FacingWithCover, PageFlow.Continuous), 1400, 900);
        c.FitWidth();
        c.GoToPage(0);
        Assert.Equal(ZoomMath.FitSpreadWidth(1400, 612 * 2, ViewportController.GutterDip, 1.0, ViewportController.MarginDip), c.Zoom, 9);
    }

    [Fact]
    public void PV05_InThePagedFlowFitPageIsDoneAgainWhenTheSpreadChanges()
    {
        // The second spread is a different size, so fit page gives a different zoom there.
        var sizes = new SizeD[] { new(612, 792), new(612, 792), new(300, 300), new(300, 300) };
        var c = Controller(4, new ViewMode(PageArrangement.Facing, PageFlow.Paged), 1200, 700, sizes: sizes);
        c.FitPage();
        var first = c.Zoom;
        Assert.True(c.Turn(+1));
        Assert.NotEqual(first, c.Zoom);
        Assert.Equal(ZoomMath.FitSpreadPage(1200, 700, new SizeD(600, 300), ViewportController.GutterDip, 1.0, ViewportController.MarginDip), c.Zoom, 9);
        Assert.True(c.Turn(-1));
        Assert.Equal(first, c.Zoom, 9);
    }

    [Fact]
    public void PV05_InTheContinuousFlowFitPageHoldsItsZoomWhileScrolling()
    {
        var sizes = new SizeD[] { new(612, 792), new(612, 792), new(300, 300), new(300, 300) };
        var c = Controller(4, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1200, 700, sizes: sizes);
        c.FitPage();
        var zoom = c.Zoom;
        c.ScrollTo(0, c.Layout.SpreadTop(1));
        Assert.Equal(zoom, c.Zoom);
    }

    // ---- PV-06: a mode change keeps the first visible page in view ---------------------------------------------------------------------

    public static TheoryData<int, int> ModePairs()
    {
        var data = new TheoryData<int, int>();
        for (var from = 0; from < 6; from++)
        {
            for (var to = 0; to < 6; to++)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    private static ViewMode ModeNumber(int n) => new((PageArrangement)(n % 3), n < 3 ? PageFlow.Continuous : PageFlow.Paged);

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void PV06_TheFirstVisiblePageStaysInViewForEveryPairOfModes(int fromMode, int toMode)
    {
        foreach (var startPage in new[] { 0, 1, 7, 18, 39 })
        {
            var c = Controller(40, ModeNumber(fromMode));
            c.GoToPage(startPage);
            var first = c.CurrentPage;
            c.SetMode(ModeNumber(toMode));
            var view = c.Snapshot();
            var (a, b) = view.Layout.VisiblePages(view.ScrollY, view.ScrollY + view.ViewportHeight);
            Assert.InRange(first, a, b);
            var rect = PageProjection.For(view, first)!.PageRect;
            Assert.True(rect.Intersects(new RectD(0, 0, view.ViewportWidth, view.ViewportHeight)), $"page {first} left the view going from mode {fromMode} to {toMode} (start {startPage})");
        }
    }

    [Fact]
    public void PV06_ChangingModeOverAndOverNeverWalksBackThroughTheDocument()
    {
        // The margin is deeper than the gap between spreads, so after a jump a sliver of the spread above shows; going by it, every change used to step one spread back.
        var c = Controller(40, default, 1400, 900);
        c.GoToPage(20);
        var start = c.CurrentPage;
        for (var round = 0; round < 12; round++)
        {
            foreach (var n in new[] { 1, 2, 5, 4, 0, 3 })
            {
                c.SetMode(ModeNumber(n));
                var (first, last) = c.CurrentPages;
                Assert.True(start >= first && start <= last, $"round {round}, mode {n}: the page we began on, {start}, is not in {first}-{last}");
            }
        }
    }

    // ---- PV-07: remembered per document and in the session ------------------------------------------------------------------------------

    [Fact]
    public void PV07_ADocumentPositionRoundTripsTheViewMode()
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-pv07-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var mode = new ViewMode(PageArrangement.FacingWithCover, PageFlow.Paged, ReadingDirection.RightToLeft);
            var store = new DocumentStore(path);
            store.SavePosition(@"C:\docs\book.pdf", 4, 0.25, 1.5, FitMode.Page, 0, mode);
            var again = new DocumentStore(path);
            Assert.Equal(mode, again.GetPosition(@"C:\docs\book.pdf")!.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PV07_ARecordWithoutAModeLoadsAsSinglePagesContinuous()
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-pv07-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Recent":[],"Positions":[{"Path":"C:\\docs\\old.pdf","Page":3,"PageFraction":0.5,"Zoom":1.25,"Fit":"None","ViewTurns":1,"Saved":"2026-10-01T10:00:00+00:00"}],"PageColour":"Normal"}""");
            var position = new DocumentStore(path).GetPosition(@"C:\docs\old.pdf")!;
            Assert.Equal(3, position.Page);
            Assert.Equal(ViewMode.Default, position.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PV07_ANameThatIsNotOursIsTheDefaultAndDoesNotSpoilTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-pv07-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Recent":[],"Positions":[{"Path":"C:\\docs\\x.pdf","Page":1,"PageFraction":0,"Zoom":1,"Fit":"None","ViewTurns":0,"Saved":"2026-10-01T10:00:00+00:00","Arrangement":"Triple","Flow":"Paged","Direction":"Sideways"}],"PageColour":"Normal"}""");
            var position = new DocumentStore(path).GetPosition(@"C:\docs\x.pdf")!;
            Assert.Equal(new ViewMode(PageArrangement.OnePage, PageFlow.Paged, ReadingDirection.Auto), position.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PV07_TheSessionFileCarriesTheViewModeAndAnOldFileStillLoads()
    {
        var mode = new ViewMode(PageArrangement.Facing, PageFlow.Continuous, ReadingDirection.LeftToRight);
        var tab = new SessionTab(TabKind.Document, @"C:\Docs\a.pdf", "1:abc", null, new TabViewState(2, 0.5, 1.25, FitMode.Page, 1, mode), false);
        var window = new SessionWindow(new StoredBounds(0, 0, 1100, 820, 96, "d"), false, 0, false, "thumbnails", [tab]);
        var bytes = SessionCodec.Write(new SessionFile(true, DateTimeOffset.UnixEpoch, 0, [window]));
        var read = SessionCodec.Read(bytes);
        Assert.True(read.IsOk, read.Problem.ToString());
        Assert.Equal(mode, read.Session!.Windows[0].Tabs[0].View.Mode);

        // A file written before the mode existed: the three names are simply not there.
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var old = System.Text.RegularExpressions.Regex.Replace(text, "\"(arrangement|flow|direction)\":\"[A-Za-z]+\",?", string.Empty).Replace(",}", "}");
        Assert.DoesNotContain("arrangement", old, StringComparison.Ordinal);
        var oldRead = SessionCodec.Read(System.Text.Encoding.UTF8.GetBytes(old));
        Assert.True(oldRead.IsOk, oldRead.Problem.ToString());
        Assert.Equal(ViewMode.Default, oldRead.Session!.Windows[0].Tabs[0].View.Mode);

        // And a name that is junk is the default, not a rejected file.
        var junk = text.Replace("\"arrangement\":\"Facing\"", "\"arrangement\":\"Quadruple\"", StringComparison.Ordinal);
        Assert.True(SessionCodec.Read(System.Text.Encoding.UTF8.GetBytes(junk)).IsOk);
    }

    // ---- PV-08: paged navigation -------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV08_PageDownScrollsATallSpreadAndThenTurns()
    {
        var c = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 600);
        c.ZoomTo(1.0); // a page 1056 px tall in a 600 px window: it scrolls
        Assert.Equal(0, c.CurrentPage);
        var turns = 0;
        for (var i = 0; i < 6 && c.CurrentPage == 0; i++)
        {
            var before = c.ScrollY;
            c.PageDown();
            if (c.CurrentPage == 0)
            {
                Assert.True(c.ScrollY > before, "Page Down scrolled");
                Assert.True(c.ScrollY - before <= 600 * 0.9 + 0.01); // a window less 10%
                turns++;
            }
        }

        Assert.True(turns >= 1);
        Assert.Equal(1, c.CurrentPage);
        Assert.Equal(c.Layout.Top(1) - c.Layout.MarginPx, c.ScrollY, 3); // the next page from its top
    }

    [Fact]
    public void PV08_AFittedSpreadTurnsAtOnceAndPageUpComesBackToTheEndOfThePrevious()
    {
        var c = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 700);
        c.FitPage();
        c.PageDown();
        Assert.Equal(1, c.CurrentPage);
        c.PageUp();
        Assert.Equal(0, c.CurrentPage);
        c.PageUp();
        Assert.Equal(0, c.CurrentPage); // the first page: nowhere to go
        c.LastPage();
        Assert.Equal(9, c.CurrentPage);
        c.PageDown();
        Assert.Equal(9, c.CurrentPage);
        c.FirstPage();
        Assert.Equal(0, c.CurrentPage);
    }

    [Fact]
    public void PV08_TheWheelScrollsInsideASpreadAndNeedsOneMoreNotchToTurn()
    {
        var c = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 600);
        c.ZoomTo(1.0);
        long now = 10_000;
        var notches = 0;
        while (c.CurrentPage == 0 && notches < 40)
        {
            var before = c.ScrollY;
            var (_, hi) = (0.0, c.Layout.Top(0) + c.Layout.Height(0) + c.Layout.MarginPx - c.ViewportHeight);
            var turned = c.WheelNotch(100, now);
            now += 400;
            notches++;
            if (before < hi - 1)
            {
                Assert.False(turned); // scrolling inside the spread never turns
            }
        }

        Assert.Equal(1, c.CurrentPage);
        // The notch that turned came after one that reached the end: the page was shown to its bottom first.
        Assert.True(notches >= 3);
    }

    [Fact]
    public void PV08_TheWheelTurnsAtMostOncePerDebounceAndNeverPastTheEnds()
    {
        var c = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 700);
        c.FitPage();
        Assert.True(c.WheelNotch(100, 5_000));
        Assert.Equal(1, c.CurrentPage);
        Assert.False(c.WheelNotch(100, 5_100)); // a flick: the second notch within 250 ms is ignored
        Assert.Equal(1, c.CurrentPage);
        Assert.True(c.WheelNotch(100, 5_400));
        Assert.Equal(2, c.CurrentPage);
        Assert.True(c.WheelNotch(-100, 6_000));
        Assert.Equal(1, c.CurrentPage);
        c.FirstPage();
        Assert.False(c.WheelNotch(-100, 9_000)); // before the first page there is nothing
        Assert.Equal(0, c.CurrentPage);
    }

    [Fact]
    public void PV08_TheArrowsTurnWhenTheSpreadFitsAndPanOrScrollWhenItDoesNot()
    {
        var fit = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 700);
        fit.FitPage();
        fit.ArrowVertical(+1, 48);
        Assert.Equal(1, fit.CurrentPage);
        fit.ArrowHorizontal(-1, 48);
        Assert.Equal(0, fit.CurrentPage);

        var zoomed = Controller(10, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 700);
        zoomed.ZoomTo(2.0);
        var y = zoomed.ScrollY;
        zoomed.ArrowVertical(+1, 48);
        Assert.Equal(0, zoomed.CurrentPage);
        Assert.Equal(y + 48, zoomed.ScrollY, 3);
        var x = zoomed.ScrollX;
        zoomed.ArrowHorizontal(+1, 48);
        Assert.Equal(0, zoomed.CurrentPage);
        Assert.True(zoomed.ScrollX > x);
    }

    [Fact]
    public void PV08_ContinuousFacingPageDownAndTheArrowsMoveBySpread()
    {
        var c = Controller(10, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1400, 900);
        c.FitPage();
        Assert.Equal(0, c.CurrentPage);
        c.NextPage();
        Assert.Equal(2, c.CurrentPage);
        c.PageDown();
        Assert.Equal(4, c.CurrentPage);
        c.PreviousPage();
        Assert.Equal(2, c.CurrentPage);
        c.LastPage();
        Assert.Equal(8, c.CurrentPage); // pages 9-10 are the last spread; its first page is the indicator's
        Assert.Equal((8, 9), c.CurrentPages);
    }

    [Fact]
    public void PV08_ASpreadThatFitsTheWindowIsCentredAndNothingOfTheNextOneShows()
    {
        var c = Controller(6, new ViewMode(PageArrangement.OnePage, PageFlow.Paged), 1000, 900);
        c.FitPage();
        c.Turn(+1);
        var view = c.Snapshot();
        var (first, last) = view.Layout.VisiblePages(view.ScrollY, view.ScrollY + view.ViewportHeight);
        Assert.Equal((1, 1), (first, last));
        var rect = PageProjection.For(view, 1)!.PageRect;
        Assert.Equal((view.ViewportHeight - rect.Height) / 2, rect.Y, 3);
    }

    // ---- PV-13: late page sizes ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV13_LateArrivingPageSizesDoNotMoveTheAnchorSpread()
    {
        var assumed = new SizeD[40];
        Array.Fill(assumed, Letter);
        var c = Controller(40, new ViewMode(PageArrangement.FacingWithCover, PageFlow.Continuous), 1400, 900, sizes: assumed);
        c.ZoomTo(1.0);
        c.GoToPage(21);
        var (page, fraction) = c.TopPosition;
        double SpreadY() => c.Layout.SpreadTop(c.Layout.SpreadOf(page)) - c.ScrollY;
        var top = SpreadY();
        var learned = assumed.ToArray();
        for (var i = 0; i < 20; i++)
        {
            learned[i] = new SizeD(300 + i, 400 + i);
        }

        c.SetPageSizes(learned);
        Assert.Equal(page, c.TopPosition.Page);
        Assert.Equal(fraction, c.TopPosition.Fraction, 3);
        Assert.InRange(Math.Abs(SpreadY() - top), 0, 1.0); // the spread did not move, though a page inside it, now smaller, is centred lower
    }

    // ---- PV-22: hostile input ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV22_JunkNamesAndEdgePageSizesNeverThrow()
    {
        Assert.Equal(ViewMode.Default, ViewMode.Parse(null, "", "\0"));
        Assert.Equal(ViewMode.Default, ViewMode.Parse("5", "-1", "Left"));
        Assert.Equal(new ViewMode((PageArrangement)0, PageFlow.Paged), ViewMode.Parse("OnePage", "Paged", null));
        var c = Controller(0);
        c.Turn(+1);
        c.WheelNotch(100, 1);
        c.PageDown();
        c.SetMode(new ViewMode(PageArrangement.Facing, PageFlow.Paged));
        c.EnterPresentation();
        c.SetDocumentDirection((ReadingDirection)99);
        Assert.Equal(ReadingDirection.Auto, c.DocumentDirection);

        var extreme = new SizeD[] { new(0, 0), new(15000, 15000), new(0.001, 14400), new(14400, 0.001) };
        foreach (var arrangement in Enum.GetValues<PageArrangement>())
        {
            var odd = Controller(4, new ViewMode(arrangement, PageFlow.Paged), 1000, 700, sizes: extreme);
            odd.FitPage();
            odd.Turn(+1);
            odd.EnterPresentation();
            odd.ExitPresentation();
            Assert.True(double.IsFinite(odd.Zoom));
        }
    }

    [Fact]
    public void PV22_SpreadArithmeticIsConstantSpaceForTenMillionPages()
    {
        var map = new SpreadMap(10_000_000, PageArrangement.FacingWithCover, rightToLeft: true);
        Assert.Equal(5_000_001, map.SpreadCount);
        Assert.Equal(map.SpreadCount - 1, map.SpreadOf(9_999_999));
        Assert.Equal((9_999_999, 9_999_999), map.PagesOf(map.SpreadCount - 1));
        Assert.Equal(0, new SpreadMap(0, PageArrangement.Facing, false).SpreadCount);
    }

    [Fact]
    public void PV22_AHundredThousandPagesLayOutInFacingContinuousWithoutTrouble()
    {
        var sizes = Pages(100_000);
        var layout = new DocumentLayout(sizes, 0, 1.0, 8, 12, new SpreadMap(sizes.Count, PageArrangement.Facing, false), 2);
        Assert.Equal(50_000, layout.SpreadCount);
        Assert.Equal(sizes.Count - 2, layout.VisiblePages(layout.ContentHeight - 100, layout.ContentHeight).First);
    }

    // ---- presentation ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Presentation_IsOnePageAtATimeFitToTheWindowAndGivesBackTheModeZoomAndPlace()
    {
        var c = Controller(20, new ViewMode(PageArrangement.FacingWithCover, PageFlow.Continuous), 1600, 900);
        c.ZoomTo(1.3);
        c.GoToPage(9);
        var mode = c.Mode;
        var zoom = c.Zoom;
        var page = c.CurrentPage;
        c.EnterPresentation();
        Assert.True(c.Presenting);
        Assert.Equal(new ViewMode(PageArrangement.OnePage, PageFlow.Paged), c.Mode);
        Assert.Equal(FitMode.Page, c.Fit);
        Assert.Equal(page, c.CurrentPage);
        Assert.Equal(0, c.Layout.MarginPx);
        var rect = PageProjection.For(c.Snapshot(), c.CurrentPage)!.PageRect;
        Assert.True(rect.Height <= 900 + 1 && rect.Width <= 1600 + 1);
        Assert.True(Math.Abs(rect.Height - 900) <= 1 || Math.Abs(rect.Width - 1600) <= 1); // as big as fits
        Assert.Equal((1600 - rect.Width) / 2, rect.X, 1);

        c.Turn(+1);
        c.Turn(+1);
        c.SetMode(new ViewMode(PageArrangement.Facing, PageFlow.Continuous)); // not while presenting
        Assert.Equal(PageFlow.Paged, c.Mode.Flow);
        var endedOn = c.CurrentPage;
        c.ExitPresentation();
        Assert.False(c.Presenting);
        Assert.Equal(mode, c.Mode);
        Assert.Equal(zoom, c.Zoom, 9);
        Assert.InRange(endedOn, c.CurrentPages.First, c.CurrentPages.Last);
        Assert.Equal(FitMode.None, c.Fit);
    }

    [Fact]
    public void Presentation_ComingBackToTheSamePageKeepsTheOffsetInIt()
    {
        var c = Controller(6, new ViewMode(PageArrangement.OnePage, PageFlow.Continuous), 1000, 600);
        c.ZoomTo(1.0);
        c.GoToPage(2, 0.4);
        var before = c.TopPosition;
        c.EnterPresentation();
        c.ExitPresentation();
        Assert.Equal(before.Page, c.TopPosition.Page);
        Assert.Equal(before.Fraction, c.TopPosition.Fraction, 3);
    }

    // ---- reading direction ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PV18_TheDocumentsDirectionArrangesRightToLeftAndTheUsersChoiceBeatsIt()
    {
        var c = Controller(6, new ViewMode(PageArrangement.Facing, PageFlow.Continuous), 1600, 900);
        Assert.False(c.RightToLeft);
        var ltrLeft = c.Layout.Left(0, 1600);
        c.SetDocumentDirection(ReadingDirection.RightToLeft);
        Assert.True(c.RightToLeft);
        Assert.True(c.Layout.Left(0, 1600) > c.Layout.Left(1, 1600)); // page 1 is on the right
        Assert.NotEqual(ltrLeft, c.Layout.Left(0, 1600));

        c.SetMode(c.Mode.With(ReadingDirection.LeftToRight)); // the override wins
        Assert.False(c.RightToLeft);
        Assert.True(c.Layout.Left(0, 1600) < c.Layout.Left(1, 1600));

        c.SetMode(c.Mode.With(ReadingDirection.Auto));
        Assert.True(c.RightToLeft);
        Assert.Equal((0, 1), c.CurrentPages);
    }

    [Fact]
    public void PV18_ThePagedArrowsAreMirroredOnlyForARightToLeftSpreadBindingAndNeverInPresentation()
    {
        var c = Controller(6, new ViewMode(PageArrangement.Facing, PageFlow.Paged, ReadingDirection.RightToLeft), 1600, 900);
        Assert.True(c.ArrowsAreMirrored);
        c.EnterPresentation();
        Assert.False(c.ArrowsAreMirrored);
        c.ExitPresentation();
        c.SetMode(new ViewMode(PageArrangement.OnePage, PageFlow.Paged, ReadingDirection.RightToLeft));
        Assert.False(c.ArrowsAreMirrored); // single pages have no facing side to mirror
    }

    // ---- commands ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheViewModeCommandsNeedADocumentAndThePresentationOnesNeedAPresentation()
    {
        var none = CommandContext.Empty;
        var doc = new CommandContext { HasDocument = true, PageCount = 10 };
        foreach (var id in new[] { "view.layoutSingle", "view.layoutTwo", "view.layoutTwoCover", "view.continuous", "view.presentation" })
        {
            Assert.False(CommandCatalog.ById[id].CanExecute(none).IsEnabled, id);
            Assert.True(CommandCatalog.ById[id].CanExecute(doc).IsEnabled, id);
        }

        Assert.Equal(Reasons.NotFacing, CommandCatalog.ById["view.coverPage"].CanExecute(doc).Reason);
        Assert.True(CommandCatalog.ById["view.coverPage"].CanExecute(doc with { ViewMode = new ViewMode(PageArrangement.Facing) }).IsEnabled);
        Assert.False(CommandCatalog.ById["view.presentation"].CanExecute(doc with { ModalOpen = true }).IsEnabled);
        foreach (var id in new[] { "present.next", "present.previous", "present.black", "present.white" })
        {
            Assert.Equal(Reasons.NotPresenting, CommandCatalog.ById[id].CanExecute(doc).Reason);
            Assert.True(CommandCatalog.ById[id].CanExecute(doc with { Presentation = true }).IsEnabled, id);
        }
    }

    [Fact]
    public void ThePresentationKeysAreCommandsOfThePresentationScopeAndTheNavigationOnesStillWorkThere()
    {
        var registry = new CommandRegistry();
        var dispatcher = new ChordDispatcher(registry);
        var presenting = new CommandContext { HasDocument = true, PageCount = 10, Presentation = true };
        var reading = presenting with { Presentation = false };

        DispatchResult Press(string chord, CommandContext context) =>
            dispatcher.Dispatch(ChordParser.Parse(chord), new DispatchInput { Context = context, SurfaceFocused = true });

        foreach (var (chord, id) in new[]
        {
            ("Right", "nav.nextPage"), ("Down", "nav.scrollDown"), ("PageDown", "nav.scrollScreenDown"), ("Space", "nav.scrollScreenDown"), ("Enter", "present.next"), ("N", "present.next"),
            ("Left", "nav.previousPage"), ("Up", "nav.scrollUp"), ("PageUp", "nav.scrollScreenUp"), ("Shift+Space", "nav.scrollScreenUp"), ("Backspace", "present.previous"), ("P", "present.previous"),
            ("Home", "nav.firstPage"), ("End", "nav.lastPage"), ("B", "present.black"), ("Period", "present.black"), ("W", "present.white"), ("Comma", "present.white"),
            ("Ctrl+L", "view.presentation"),
        })
        {
            var result = Press(chord, presenting);
            Assert.Equal(DispatchKind.Execute, result.Kind);
            Assert.Equal(id, result.Command!.Id);
        }

        // Outside presentation the letters and Enter are not ours: nothing happens when reading.
        foreach (var chord in new[] { "Enter", "N", "B", "W", "Period", "Comma", "P", "Backspace" })
        {
            Assert.Equal(DispatchKind.NotHandled, Press(chord, reading).Kind);
        }

        // Presentation shows, does not edit: the zoom and the layout commands are off in it.
        Assert.Equal(DispatchKind.Disabled, Press("Ctrl+4", presenting).Kind);
        Assert.Equal(DispatchKind.Disabled, Press("Ctrl+Plus", presenting).Kind);
    }

    [Fact]
    public void TheLayoutShortcutsAreCtrlFourToSevenAndPresentationIsCtrlL()
    {
        var chords = new[] { ("view.layoutSingle", "Ctrl+4"), ("view.layoutTwo", "Ctrl+5"), ("view.layoutTwoCover", "Ctrl+6"), ("view.continuous", "Ctrl+7"), ("view.presentation", "Ctrl+L") };
        foreach (var (id, chord) in chords)
        {
            Assert.Equal([ChordParser.Parse(chord)], CommandCatalog.ById[id].DefaultChords);
        }
    }
}
