using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Tiles;

namespace MikePDF.Core.Tests;

/// <summary>A presentation has the pages beside the one shown drawn in full ahead of the turn (docs/features/page-view-modes.md 3.6, PV-17).</summary>
public sealed class PresentationPrefetchTests
{
    private static ViewportController Presenting(int pages = 12, int startPage = 4, bool prefetch = true)
    {
        var c = new ViewportController { PrefetchAdjacent = prefetch };
        c.SetViewport(1920, 1080, 1.0);
        c.SetPageSizes([.. Enumerable.Repeat(new SizeD(612, 792), pages)]);
        c.GoToPage(startPage);
        c.EnterPresentation();
        return c;
    }

    private static HashSet<int> PagesOf(IEnumerable<PlannedItem> items) => [.. items.Select(i => i.Key.Page)];

    private static int TilesOf(DocumentLayout layout, int page)
    {
        var (w, h) = layout.RenderSize(page);
        return TileGrid.Columns(w) * TileGrid.Rows(h);
    }

    [Fact]
    public void TheNextPageTheOneBeforeAndTheOneAfterTheNextAreDrawnInFullAheadOfTheTurn()
    {
        var c = Presenting();
        var plan = TilePlanner.Plan(c.Snapshot(), new TileCache(1L << 30));
        var layout = c.Layout;
        var sharp = plan.VisibleTiles.Concat(plan.Prefetch).Where(i => !i.Key.IsLowRes).ToList();

        Assert.Equal([3, 4, 5, 6], PagesOf(sharp).Order());
        foreach (var page in new[] { 4, 5, 3, 6 })
        {
            var tiles = sharp.Where(i => i.Key.Page == page).Select(i => i.Key).Distinct().Count();
            Assert.Equal(TilesOf(layout, page), tiles); // every tile of the page, not the sliver the planner's own look-ahead reaches
        }

        // The page on screen comes first, then the next, then the one before, then the one after the next.
        Assert.All(plan.VisibleTiles, i => Assert.Equal(4, i.Key.Page));
        var order = plan.Prefetch.Select(i => i.Key.Page).Distinct().ToList();
        Assert.True(order.IndexOf(5) < order.IndexOf(6), "the next page before the one after it"); // (the sliver of the page before comes first: the planner reaches it by itself)
    }

    [Fact]
    public void WithoutItOnlyTheSliverOfTheNextPageThePlannersOwnLookAheadReachesIsPlanned()
    {
        var c = Presenting(prefetch: false);
        var plan = TilePlanner.Plan(c.Snapshot(), new TileCache(1L << 30));
        var next = plan.Prefetch.Count(i => i.Key.Page == 5 && !i.Key.IsLowRes);
        Assert.True(next < TilesOf(c.Layout, 5), $"{next} of {TilesOf(c.Layout, 5)} tiles of the next page");
    }

    [Fact]
    public void ItIsLeftOutWhenTheCacheHasNoRoomForIt()
    {
        var c = Presenting();
        var small = new TileCache(2L << 20); // two megabytes: not a page and a half at 1080p
        var plan = TilePlanner.Plan(c.Snapshot(), small);
        var adjacentTiles = plan.Prefetch.Count(i => i.Key.Page != 4 && !i.Key.IsLowRes);
        var all = TilesOf(c.Layout, 5) + TilesOf(c.Layout, 3) + TilesOf(c.Layout, 6);
        Assert.True(adjacentTiles < all, $"{adjacentTiles} of {all}");

        var none = TilePlanner.Plan(c.Snapshot(), new TileCache(1L << 20));
        var without = TilePlanner.Plan(Presenting(prefetch: false).Snapshot(), new TileCache(1L << 20));
        Assert.Equal(without.Prefetch.Count, none.Prefetch.Count); // a cache that small gets what the planner always asked for, and no more
    }

    [Fact]
    public void ThePagesAreAskedForAtTheScaleTheyWillBeShownAt()
    {
        var c = Presenting();
        var plan = TilePlanner.Plan(c.Snapshot(), new TileCache(1L << 30));
        var shown = plan.VisibleTiles.Select(i => i.Scale).Distinct().Single();
        Assert.All(plan.Prefetch.Where(i => !i.Key.IsLowRes && i.Key.Page != 4), i => Assert.Equal(shown, i.Scale));
    }

    [Fact]
    public void AtTheEndsThereIsNothingBeyondAndOutsideAPresentationNothingIsAdded()
    {
        var first = Presenting(startPage: 0);
        var plan = TilePlanner.Plan(first.Snapshot(), new TileCache(1L << 30));
        Assert.DoesNotContain(plan.Prefetch, i => i.Key.Page < 0);
        Assert.Equal([0, 1, 2], PagesOf(plan.VisibleTiles.Concat(plan.Prefetch).Where(i => !i.Key.IsLowRes)).Order());

        var last = Presenting(pages: 12, startPage: 11);
        Assert.Equal([10, 11], PagesOf(TilePlanner.Plan(last.Snapshot(), new TileCache(1L << 30)).Prefetch.Concat(TilePlanner.Plan(last.Snapshot(), new TileCache(1L << 30)).VisibleTiles).Where(i => !i.Key.IsLowRes)).Order());

        var reading = new ViewportController();
        reading.SetViewport(1920, 1080, 1.0);
        reading.SetPageSizes([.. Enumerable.Repeat(new SizeD(612, 792), 12)]);
        reading.SetMode(new ViewMode(PageArrangement.OnePage, PageFlow.Paged));
        Assert.False(reading.Snapshot().PrefetchAdjacentSpreads);
    }
}
