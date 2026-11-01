using Quire.Core.Geometry;
using Quire.Core.Layout;
using Quire.Core.Tiles;

namespace Quire.Core.Tests;

public sealed class TileGridTests
{
    [Theory]
    [InlineData(512, 1)]
    [InlineData(513, 2)]
    [InlineData(1024, 2)]
    [InlineData(1, 1)]
    public void ColumnsRoundUp(long width, int expected) => Assert.Equal(expected, TileGrid.Columns(width));

    [Fact]
    public void EdgeTilesAreSmaller()
    {
        Assert.Equal(new TileRect(0, 0, 512, 512), TileGrid.Rect(0, 0, 1000, 700));
        Assert.Equal(new TileRect(512, 0, 488, 512), TileGrid.Rect(1, 0, 1000, 700));
        Assert.Equal(new TileRect(512, 512, 488, 188), TileGrid.Rect(1, 1, 1000, 700));
    }

    [Fact]
    public void CoveringGivesTheTilesThatIntersect()
    {
        Assert.Equal((0, 1, 0, 0), TileGrid.Covering(new RectD(0, 0, 600, 100), 1000, 700));
        Assert.Equal((1, 1, 1, 1), TileGrid.Covering(new RectD(512, 512, 10, 10), 1000, 700));
        Assert.Equal((0, 0, 0, 0), TileGrid.Covering(new RectD(-100, -100, 200, 200), 1000, 700)); // clipped to the page
        Assert.Equal((0, 0, 0, 0), TileGrid.Covering(new RectD(0, 0, 512, 512), 1000, 700));        // exactly one tile: not two
        var (fx, lx, _, _) = TileGrid.Covering(new RectD(2000, 0, 10, 10), 1000, 700);
        Assert.True(lx < fx);
    }

    [Fact]
    public void KeysCompareByValueAndCarryTheirScale()
    {
        var a = TileKey.ForTile(3, 1.5f, 1, 2, 4);
        var b = TileKey.ForTile(3, 1.5f, 1, 2, 4);
        Assert.Equal(a, b);
        Assert.Equal(1.5f, a.Scale);
        Assert.NotEqual(a, TileKey.ForTile(3, 1.5f, 2, 2, 4));
        Assert.True(TileKey.LowRes(3, 5).IsLowRes);
        Assert.Equal(1, TileKey.LowRes(3, 5).Turns);
        Assert.False(a.IsLowRes);
    }
}

public sealed class TilePlannerTests
{
    private static readonly SizeD Letter = new(612, 792);

    private static (ViewSnapshot View, TileCache Cache) Setup(double scrollY = 0, double velocity = 0, int pages = 10, double scale = 1.0, int turns = 0, double vw = 800, double vh = 600)
    {
        var layout = new DocumentLayout([.. Enumerable.Repeat(Letter, pages)], turns, scale, 8, 12);
        return (new ViewSnapshot(layout, 0, scrollY, vw, vh, velocity), new TileCache(1L << 30));
    }

    [Fact]
    public void VisibleTilesAreThoseUnderTheViewport()
    {
        var (view, cache) = Setup(); // 612 x 792 page: 2 x 2 tiles; the 800 x 600 viewport shows tiles of page 0 (and the top of page 1)
        var plan = TilePlanner.Plan(view, cache);

        Assert.All(plan.VisibleTiles, t => Assert.False(t.Key.IsLowRes));
        Assert.Contains(plan.VisibleTiles, t => t.Key.Page == 0 && t.Key.Tx == 0 && t.Key.Ty == 0);
        Assert.Contains(plan.VisibleTiles, t => t.Key.Page == 0 && t.Key.Tx == 1 && t.Key.Ty == 0);
        Assert.Contains(plan.VisibleTiles, t => t.Key.Page == 0 && t.Key.Ty == 1);
        Assert.DoesNotContain(plan.VisibleTiles, t => t.Key.Page >= 2);
    }

    [Fact]
    public void ThePlanAsksForEdgeTilesAtTheirTrueSize()
    {
        var (view, cache) = Setup();
        var plan = TilePlanner.Plan(view, cache);
        var edge = plan.VisibleTiles.Single(t => t.Key is { Page: 0, Tx: 1, Ty: 0 });
        Assert.Equal(new PlannedItem(edge.Key, 1.0f, 0, 512, 0, 100, 512), edge);
        var bottom = plan.VisibleTiles.Single(t => t.Key is { Page: 0, Tx: 0, Ty: 1 });
        Assert.Equal(280, bottom.Height); // 792 - 512
    }

    [Fact]
    public void TilesComeFromTheCentreOutward()
    {
        var (view, cache) = Setup(scrollY: 0);
        var plan = TilePlanner.Plan(view, cache);
        var centre = view.ScrollY + (view.ViewportHeight / 2);
        var distances = plan.VisibleTiles.Select(t => Math.Abs(view.Layout.Top(t.Key.Page) + t.Y + (t.Height / 2.0) - centre)).ToList();
        Assert.Equal(distances.OrderBy(d => d), distances);
    }

    [Fact]
    public void PlaceholdersOfTheVisiblePagesComeFirstAndCachedItemsAreLeftOut()
    {
        var (view, cache) = Setup(scrollY: 700); // the bottom of page 0 and the top of page 1 are in view
        cache.Add(TileKey.LowRes(0, 0), new FakeSurface(160, 207));
        cache.Add(TileKey.ForTile(0, 1.0f, 0, 0, 1), new FakeSurface());

        var plan = TilePlanner.Plan(view, cache);
        Assert.DoesNotContain(plan.VisibleLowRes, i => i.Key.Page == 0);
        Assert.Contains(plan.VisibleLowRes, i => i.Key.Page == 1);
        Assert.DoesNotContain(plan.VisibleTiles, i => i.Key is { Page: 0, Tx: 0, Ty: 1 });
        Assert.Contains(plan.VisibleTiles, i => i.Key is { Page: 0, Tx: 1, Ty: 1 });

        // ...but they are still wanted, so they are not cancelled or evicted early.
        Assert.Contains(TileKey.LowRes(0, 0), plan.Wanted);
        Assert.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 1), plan.Wanted);
    }

    [Fact]
    public void ThePlaceholderFitsItsBoxWhateverTheAspectRatio()
    {
        var options = new TilePlannerOptions();
        var (scale, w, h) = TilePlanner.LowResSize(new SizeD(612, 792), options);
        Assert.InRange(w, 159, 161);
        Assert.True(h <= 512);
        Assert.Equal(612.0 * scale, (double)w, 0);

        var (_, tallW, tallH) = TilePlanner.LowResSize(new SizeD(100, 5000), options);
        Assert.True(tallH <= 512);
        Assert.True(tallW >= 1);
    }

    [Fact]
    public void PlaceholdersAheadFollowTheDirectionOfTravel()
    {
        var (down, cache) = Setup(scrollY: 3000, velocity: 800);
        var planDown = TilePlanner.Plan(down, cache);
        var visible = down.Layout.VisiblePages(down.ScrollY, down.ScrollY + down.ViewportHeight);
        Assert.Contains(planDown.AheadLowRes, i => i.Key.Page == visible.Last + 1);

        var (up, cache2) = Setup(scrollY: 3000, velocity: -800);
        var planUp = TilePlanner.Plan(up, cache2);
        var visibleUp = up.Layout.VisiblePages(up.ScrollY, up.ScrollY + up.ViewportHeight);
        Assert.Contains(planUp.AheadLowRes, i => i.Key.Page == visibleUp.First - 1);
        Assert.True(planUp.AheadLowRes.First().Key.Page < visibleUp.First);
    }

    [Fact]
    public void PrefetchStopsWhenScrollingFasterThanTwoAndAHalfScreensPerSecond()
    {
        var (slow, cache) = Setup(scrollY: 3000, velocity: 100);
        Assert.NotEmpty(TilePlanner.Plan(slow, cache).Prefetch);

        var (fast, cache2) = Setup(scrollY: 3000, velocity: 600 * 2.6);
        var plan = TilePlanner.Plan(fast, cache2);
        Assert.Empty(plan.Prefetch);
        Assert.NotEmpty(plan.VisibleTiles); // the visible tiles are still wanted
    }

    [Fact]
    public void PrefetchDoesNotRepeatVisibleTiles()
    {
        var (view, cache) = Setup(scrollY: 3000);
        var plan = TilePlanner.Plan(view, cache);
        var visible = plan.VisibleTiles.Select(t => t.Key).ToHashSet();
        Assert.All(plan.Prefetch, t => Assert.DoesNotContain(t.Key, visible));
    }

    [Fact]
    public void AGiantPageAtHighZoomOnlyAsksForTheTilesInView()
    {
        // 6400% of a 612 x 792 pt page is far beyond one screen; only the viewport's tiles (and a screen of prefetch) are requested.
        var layout = new DocumentLayout([Letter], 0, ZoomMath.PixelsPerPoint(64, 1.0), 8, 12);
        var view = new ViewSnapshot(layout, 20000, 30000, 1200, 800, 0);
        var plan = TilePlanner.Plan(view, new TileCache(1L << 30));
        Assert.InRange(plan.VisibleTiles.Count, 1, 12);
        Assert.All(plan.VisibleTiles, t => Assert.Equal(64.0f, t.Scale)); // the engine's limit; the tiles are stretched 1.33 times
    }

    [Fact]
    public void RotationIsPartOfTheKey()
    {
        var (view, cache) = Setup(turns: 1);
        var plan = TilePlanner.Plan(view, cache);
        Assert.All(plan.VisibleTiles, t => Assert.Equal(1, t.Turns));
        Assert.All(plan.VisibleLowRes, t => Assert.Equal(1, t.Key.Turns));
    }

    [Fact]
    public void AnEmptyDocumentPlansNothing()
    {
        var plan = TilePlanner.Plan(new ViewSnapshot(new DocumentLayout([], 0, 1, 0, 0), 0, 0, 800, 600, 0), new TileCache(1 << 20));
        Assert.Empty(plan.VisibleTiles);
        Assert.Empty(plan.Wanted);
    }
}

public sealed class TileCacheTests
{
    private static TileKey Key(int page, int tx = 0) => TileKey.ForTile(page, 1.0f, 0, tx, 0);

    [Fact]
    public void TheDefaultBudgetIsTwoPercentOfRamBetween96And256MB()
    {
        Assert.Equal(96L << 20, TileCache.DefaultBudget(2L << 30));
        Assert.Equal(160L << 20, TileCache.DefaultBudget(8000L << 20));
        Assert.Equal(256L << 20, TileCache.DefaultBudget(64L << 30));
    }

    [Fact]
    public void CostIsNominalSizeTimesTheFactor()
    {
        var cache = new TileCache(1L << 30, costFactor: 2.0);
        cache.Add(Key(0), new FakeSurface(512, 512));
        Assert.Equal(512L * 512 * 4 * 2, cache.Bytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TheLeastRecentlyUsedTileIsEvictedFirstDownToNinetyPercent()
    {
        var tileCost = 512L * 512 * 4 * 2; // 2 MiB
        var cache = new TileCache(tileCost * 10);
        var surfaces = Enumerable.Range(0, 10).Select(_ => new FakeSurface()).ToList();
        for (var i = 0; i < 10; i++)
        {
            cache.Add(Key(i), surfaces[i]);
        }

        Assert.Equal(10, cache.Count);
        cache.TryGet(Key(0), out _); // the oldest is used again: page 1 is now the oldest

        cache.Add(Key(10), new FakeSurface()); // 11 tiles: over budget, evict to 90% = 9 tiles
        Assert.True(cache.Bytes <= tileCost * 9);
        Assert.False(cache.Contains(Key(1)));
        Assert.True(cache.Contains(Key(0)));
        Assert.True(cache.Contains(Key(10)));
    }

    [Fact]
    public void WhatIsOnScreenInTheCurrentFrameIsNeverEvicted()
    {
        var tileCost = 512L * 512 * 4 * 2;
        var cache = new TileCache(tileCost * 3);
        cache.BeginFrame();
        for (var i = 0; i < 3; i++)
        {
            cache.Add(Key(i), new FakeSurface());
            Assert.True(cache.Touch(Key(i), out _));
        }

        cache.Add(Key(3), new FakeSurface()); // over budget, but all of the first three are pinned: only the new one can go
        Assert.True(cache.Contains(Key(0)) && cache.Contains(Key(1)) && cache.Contains(Key(2)));

        cache.BeginFrame(); // a new frame: they are no longer pinned
        cache.Add(Key(4), new FakeSurface());
        Assert.True(cache.Bytes <= tileCost * 3);
    }

    [Fact]
    public void PlaceholdersAreEvictedLast()
    {
        var tileCost = 512L * 512 * 4 * 2;
        var cache = new TileCache(tileCost * 3);
        cache.Add(TileKey.LowRes(0, 0), new FakeSurface(512, 512)); // the oldest, but a placeholder
        cache.Add(Key(1), new FakeSurface());
        cache.Add(Key(2), new FakeSurface());
        cache.Add(Key(3), new FakeSurface()); // four tiles in a budget of three: evict to 90%
        Assert.True(cache.Contains(TileKey.LowRes(0, 0)));
        Assert.False(cache.Contains(Key(1)));
        Assert.False(cache.Contains(Key(2)));
        Assert.True(cache.Contains(Key(3)));
    }

    [Fact]
    public void EvictedSurfacesAreDisposedOnlyWhenTheOwnerSaysSo()
    {
        var tileCost = 512L * 512 * 4 * 2;
        var cache = new TileCache(tileCost);
        var first = new FakeSurface();
        cache.Add(Key(0), first);
        cache.Add(Key(1), new FakeSurface());

        Assert.False(cache.Contains(Key(0)));
        Assert.False(first.Disposed); // a frame may still be drawing it
        Assert.Equal(1, cache.DisposeEvicted());
        Assert.True(first.Disposed);
        Assert.Equal(0, cache.DisposeEvicted());
    }

    [Fact]
    public void ReplacingAKeyDisposesTheOldSurfaceLaterAndKeepsTheAccountRight()
    {
        var cache = new TileCache(1L << 30);
        var old = new FakeSurface(512, 512);
        cache.Add(Key(0), old);
        cache.Add(Key(0), new FakeSurface(256, 256));
        Assert.Equal(1, cache.Count);
        Assert.Equal(256L * 256 * 4 * 2, cache.Bytes);
        cache.DisposeEvicted();
        Assert.True(old.Disposed);
    }

    [Fact]
    public void KeysAreListedPerPageAndTrimToVisibleKeepsOnlyTheFrame()
    {
        var cache = new TileCache(1L << 30);
        cache.Add(Key(1, 0), new FakeSurface());
        cache.Add(Key(1, 1), new FakeSurface());
        cache.Add(Key(2, 0), new FakeSurface());
        Assert.Equal(2, cache.KeysForPage(1).Count);
        Assert.Empty(cache.KeysForPage(9));

        cache.BeginFrame();
        cache.Touch(Key(2, 0), out _);
        cache.TrimToVisible();
        Assert.Equal(1, cache.Count);
        Assert.Empty(cache.KeysForPage(1));
        Assert.Equal(2, cache.DisposeEvicted());
    }

    [Fact]
    public void ABudgetChangeEvictsAndClearEmpties()
    {
        var tileCost = 512L * 512 * 4 * 2;
        var cache = new TileCache(tileCost * 10);
        for (var i = 0; i < 10; i++)
        {
            cache.Add(Key(i), new FakeSurface());
        }

        cache.SetBudget(tileCost * 2);
        Assert.True(cache.Bytes <= tileCost * 2);
        Assert.Equal(tileCost * 2, cache.BudgetBytes);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
        Assert.True(cache.EvictedCount >= 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetBudget(0));
    }
}
