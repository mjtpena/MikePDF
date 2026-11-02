using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Core.Text;
using static MikePDF.Core.Tests.TextFixtures;

namespace MikePDF.Core.Tests;

/// <summary>
/// What works on a page works on both pages of a spread (docs/features/page-view-modes.md 3.5): selection that starts on one page and ends on the other, search highlights and next match across
/// the boundary of spreads, and a link on the right-hand page. All of it goes through <see cref="PageProjection"/>, so the tests run the projection over facing layouts, left to right and right to left.
/// </summary>
public sealed class SpreadInteractionTests
{
    private static readonly SizeD Page = new(612, 792);

    private static PageCharacters CharsOf(int page) => Chars(new Run("left" + page, 72, 72), new Run("quokka" + page, 72, 200), new Run("tail" + page, 72, 400));

    private static TextModel ModelOf(int page) => TextModelBuilder.Build(CharsOf(page), Letter, page);

    private static LoadedPages Pages(int count) => new([.. Enumerable.Range(0, count).Select(ModelOf)]);

    private static ViewportController Facing(bool rightToLeft, PageFlow flow = PageFlow.Continuous, int pages = 8)
    {
        var c = new ViewportController();
        c.SetViewport(1800, 1000, 1.0);
        c.SetPageSizes([.. Enumerable.Repeat(Page, pages)]);
        c.SetMode(new ViewMode(PageArrangement.Facing, flow, rightToLeft ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight));
        c.ZoomTo(0.75);
        c.GoToPage(0);
        return c;
    }

    /// <summary>The viewport point at the middle of the first character of a run of text (the atom of the model at <paramref name="atom"/>).</summary>
    private static PointD ViewportPointOf(ViewSnapshot view, int page, int atom)
    {
        var model = ModelOf(page);
        var rect = model.Atoms[atom].Rect!.Value;
        return PageProjection.For(view, page)!.PageToViewport(new PointD(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));
    }

    // The atom a pointer at a viewport point lands on, the way DocumentSession.HitTest does it.
    private static TextPosition PositionAt(ViewSnapshot view, PointD point)
    {
        var projection = PageProjection.At(view, point)!;
        var model = ModelOf(projection.Page);
        return new TextPosition(projection.Page, model.HitTest(projection.Page, projection.ViewportToPage(point)));
    }

    // ---- PV-09: selection across the spread --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PV09_ADragFromOnePageOfASpreadToTheOtherSelectsInPageOrderWhicheverWayItGoes(bool rightToLeft, bool backwards)
    {
        var c = Facing(rightToLeft);
        var view = c.Snapshot();
        var onZero = ViewportPointOf(view, 0, 0);          // the "l" of "left0"
        var onOne = ViewportPointOf(view, 1, ModelOf(1).Length - 2);

        // The pages are where the binding says: page 0 is on the right for a right-to-left document.
        Assert.Equal(rightToLeft, onZero.X > onOne.X);
        Assert.Equal(0, PageProjection.At(view, onZero)!.Page);
        Assert.Equal(1, PageProjection.At(view, onOne)!.Page);

        var pages = Pages(8);
        var selection = new SelectionController(pages);
        var (from, to) = backwards ? (onOne, onZero) : (onZero, onOne);
        selection.Begin(PositionAt(view, from));
        selection.DragTo(PositionAt(view, to));

        Assert.True(selection.HasSelection);
        Assert.Equal(new TextPosition(0, 0), selection.Selection.Start);
        Assert.Equal(1, selection.Selection.End.Page);
        var (text, _) = await SelectionText.CollectAsync(selection.Selection, (page, _) => Task.FromResult(pages.GetLoaded(page)), "\n", CancellationToken.None);
        Assert.StartsWith("left0", text, StringComparison.Ordinal);
        Assert.Contains("tail0", text, StringComparison.Ordinal); // the rest of the first page
        Assert.True(text.IndexOf("left1", StringComparison.Ordinal) > text.IndexOf("tail0", StringComparison.Ordinal), "page 2 comes after page 1 in the text, left to right or not");
        Assert.DoesNotContain("tail1 ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PV09_APointInTheGutterOrBesideTheSpreadLandsOnTheNearestPage(bool rightToLeft)
    {
        var c = Facing(rightToLeft);
        var view = c.Snapshot();
        var first = PageProjection.For(view, 0)!.PageRect;
        var second = PageProjection.For(view, 1)!.PageRect;
        var y = first.Y + 100;
        var (left, right) = first.X < second.X ? (0, 1) : (1, 0);

        Assert.Equal(left, PageProjection.At(view, new PointD(Math.Min(first.X, second.X) - 200, y))!.Page);
        Assert.Equal(right, PageProjection.At(view, new PointD(Math.Max(first.Right, second.Right) + 200, y))!.Page);
        var gutter = new PointD((Math.Min(first.Right, second.Right) + Math.Max(first.X, second.X)) / 2, y);
        Assert.NotNull(PageProjection.At(view, gutter));
        Assert.True(PageProjection.At(view, gutter)!.Page is 0 or 1);
        Assert.False(first.Contains(gutter) || second.Contains(gutter)); // and a gutter click does not follow a link either
    }

    [Fact]
    public void PV09_APagePointRoundTripsOnBothPagesOfASpreadAtAnyZoomAndTurn()
    {
        foreach (var rtl in new[] { false, true })
        {
            foreach (var turns in new[] { 0, 1, 2, 3 })
            {
                var c = Facing(rtl);
                c.Rotate(turns);
                c.GoToPage(2);
                var view = c.Snapshot();
                foreach (var page in new[] { 2, 3 })
                {
                    var projection = PageProjection.For(view, page)!;
                    var point = new PointD(100.5, 200.25);
                    var back = projection.ViewportToPage(projection.PageToViewport(point));
                    Assert.Equal(point.X, back.X, 2);
                    Assert.Equal(point.Y, back.Y, 2);
                }
            }
        }
    }

    // ---- PV-10: search highlights and next match across spreads ---------------------------------------------------------------------------

    private static IReadOnlyList<RectD> MatchRects(int page)
    {
        var chars = CharsOf(page);
        const int start = 5; // after "left" and the page digit
        return MatchGeometry.RectsFor(chars, Letter, start, "quokka".Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PV10_AMatchOnTheRightHandPageIsHighlightedInsideThatPageOnItsSide(bool rightToLeft)
    {
        var c = Facing(rightToLeft);
        var view = c.Snapshot();
        foreach (var page in new[] { 0, 1 })
        {
            var projection = PageProjection.For(view, page)!;
            var rects = MatchRects(page);
            Assert.NotEmpty(rects);
            var box = projection.PageToViewport(rects[0]);
            Assert.True(box.X >= projection.PageRect.X - 0.5 && box.Right <= projection.PageRect.Right + 0.5 && box.Y >= projection.PageRect.Y && box.Bottom <= projection.PageRect.Bottom, $"match on page {page} is inside its page");
            var other = PageProjection.For(view, 1 - page)!.PageRect;
            Assert.False(box.Intersects(other), "and not over the other page");
        }

        var firstOnLeft = PageProjection.For(view, 0)!.PageRect.X < PageProjection.For(view, 1)!.PageRect.X;
        Assert.Equal(!rightToLeft, firstOnLeft);
    }

    [Theory]
    [InlineData(PageFlow.Continuous, false)]
    [InlineData(PageFlow.Paged, false)]
    [InlineData(PageFlow.Continuous, true)]
    [InlineData(PageFlow.Paged, true)]
    public async Task PV10_NextMatchWalksBothHalvesOfASpreadAndOnToTheNextSpreadsAndShowsEach(PageFlow flow, bool rightToLeft)
    {
        var source = new ScriptedSearch();
        foreach (var page in new[] { 0, 1, 2, 5 })
        {
            source.Matches[page] = [new PageMatch(5, 6)];
        }

        using var session = new FindSession(source, 8, new SearchOptions("quokka"), 0) { RetryDelay = TimeSpan.FromMilliseconds(1) };
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var c = Facing(rightToLeft, flow);

        var visited = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var found = await session.NextAsync() ?? throw new InvalidOperationException("no match");
            visited.Add(found.Page);
            var rect = MatchRects(found.Page)[0];
            c.RevealPageRect(found.Page, rect);

            // The match is on the screen, whichever spread it is on and whichever half of it.
            var box = PageProjection.For(c.Snapshot(), found.Page)!.PageToViewport(rect);
            Assert.True(box.Y >= 0 && box.Bottom <= c.ViewportHeight && box.X >= 0 && box.Right <= c.ViewportWidth, $"match {i} on page {found.Page} is in the window");
            Assert.InRange(found.Page, c.CurrentPages.First, c.CurrentPages.Last);
        }

        Assert.Equal([0, 1, 2, 5], visited); // page 0 (left of spread 1), page 1 (its right half), then the next spread, then two spreads on
    }

    [Fact]
    public void PV10_InThePagedFlowAMatchOnAnotherSpreadTurnsToItAndOneOnTheOtherHalfDoesNot()
    {
        var c = Facing(false, PageFlow.Paged);
        Assert.False(c.NeedsReveal(1, MatchRects(1)[0]));          // the other half of the spread shown
        Assert.True(c.NeedsReveal(2, MatchRects(2)[0]));           // the next spread
        c.RevealPageRect(2, MatchRects(2)[0]);
        Assert.Equal((2, 3), c.CurrentPages);
        Assert.False(c.NeedsReveal(3, MatchRects(3)[0]));
        c.RevealPageRect(7, MatchRects(7)[0]);
        Assert.Equal((6, 7), c.CurrentPages);
    }

    // ---- PV-11: a link on the right-hand page ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PV11_ALinkOnTheRightHandPageIsHitThroughTheProjectionAndTheGutterIsNot(bool rightToLeft)
    {
        var c = Facing(rightToLeft);
        var view = c.Snapshot();
        var link = new RectD(72, 190, 200, 24); // page space, as PdfToPage gives it
        foreach (var page in new[] { 0, 1 })
        {
            var projection = PageProjection.For(view, page)!;
            var centre = projection.PageToViewport(new PointD(link.X + (link.Width / 2), link.Y + (link.Height / 2)));
            var hit = PageProjection.At(view, centre)!;
            Assert.Equal(page, hit.Page);
            Assert.True(hit.PageRect.Contains(centre));
            var inPage = hit.ViewportToPage(centre);
            Assert.True(link.Contains(inPage), $"the point lands inside the link on page {page}: {inPage}");
            Assert.Equal(link.X + (link.Width / 2), inPage.X, 2);
            Assert.Equal(link.Y + (link.Height / 2), inPage.Y, 2);

            // A pixel outside the link's box on the same page is not in it.
            var beside = projection.PageToViewport(new PointD(link.Right + 30, link.Y + 5));
            Assert.False(link.Contains(PageProjection.At(view, beside)!.ViewportToPage(beside)));
        }
    }
}
