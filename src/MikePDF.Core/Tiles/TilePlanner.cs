using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Tiles;

/// <summary>One image the planner wants, and how to ask for it.</summary>
public readonly record struct PlannedItem(TileKey Key, float Scale, int Turns, int X, int Y, int Width, int Height);

/// <summary>What the view needs, in the priority order of ADR-0006 decision 4. Items already cached are left out.</summary>
public sealed class TilePlan
{
    /// <summary>1. Low-res placeholders of the visible pages.</summary>
    public List<PlannedItem> VisibleLowRes { get; } = [];

    /// <summary>2. Sharp tiles of the viewport, from its centre outward.</summary>
    public List<PlannedItem> VisibleTiles { get; } = [];

    /// <summary>3. Low-res placeholders ahead (max(1 screen, 0.6 s of velocity), at most 8 screens) and one screen behind.</summary>
    public List<PlannedItem> AheadLowRes { get; } = [];

    /// <summary>4. Tile prefetch: one screen ahead, half a screen behind; none while scrolling faster than 2.5 screens/s.</summary>
    public List<PlannedItem> Prefetch { get; } = [];

    /// <summary>Every key still worth having, cached or not. In-flight work outside this set is cancelled.</summary>
    public HashSet<TileKey> Wanted { get; } = [];
}

public sealed record TilePlannerOptions
{
    /// <summary>Width of the low-res placeholder (ADR-0006 decision 3: 128 px thumbnails; a little more here, for a calmer fallback).</summary>
    public int LowResWidth { get; init; } = 160;

    public int LowResMaxHeight { get; init; } = 512;

    public double FastScrollScreensPerSecond { get; init; } = 2.5;

    public double AheadSecondsOfVelocity { get; init; } = 0.6;

    public double MaxAheadScreens { get; init; } = 8;

    /// <summary>A hard cap on the plan so a huge viewport at a tiny zoom cannot flood the scheduler.</summary>
    public int MaxItems { get; init; } = 400;
}

/// <summary>Turns a view into a prioritised list of tiles (ADR-0006 decision 4). Pure: no threads, no I/O.</summary>
public static class TilePlanner
{
    public static TilePlan Plan(ViewSnapshot view, TileCache cache, TilePlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cache);
        options ??= new TilePlannerOptions();
        var plan = new TilePlan();
        var layout = view.Layout;
        if (layout.PageCount == 0)
        {
            return plan;
        }

        var vw = view.ViewportWidth;
        var vh = view.ViewportHeight;
        var y0 = view.ScrollY;
        var y1 = y0 + vh;
        var velocity = view.VelocityPxPerSecond;
        var fast = Math.Abs(velocity) > vh * options.FastScrollScreensPerSecond;
        var direction = velocity > 40 ? 1 : velocity < -40 ? -1 : 0;
        var centreY = y0 + (vh / 2);
        var (first, last) = layout.VisiblePages(y0, y1);
        var scale = (float)layout.RenderScale;
        var turns = layout.ViewTurns;
        var budget = options.MaxItems;

        // 1. Placeholders of the visible pages.
        var visibleLowRes = new List<(double Distance, PlannedItem Item)>();
        for (var p = first; p <= last; p++)
        {
            AddLowRes(plan, cache, layout, p, options, visibleLowRes, centreY);
        }

        plan.VisibleLowRes.AddRange(visibleLowRes.OrderBy(t => t.Distance).Select(t => t.Item));

        // 2. Sharp tiles of the viewport.
        var visible = new List<(double Distance, PlannedItem Item)>();
        var visibleKeys = new HashSet<TileKey>();
        for (var p = first; p <= last; p++)
        {
            CollectTiles(plan, cache, layout, p, view.ScrollX, view.ScrollX + vw, y0, y1, scale, turns, centreY, visible, visibleKeys, exclude: null);
        }

        plan.VisibleTiles.AddRange(visible.OrderBy(t => t.Distance).Select(t => t.Item).Take(budget));

        // 3. Placeholders ahead and behind.
        var aheadPx = Math.Min(Math.Max(vh, Math.Abs(velocity) * options.AheadSecondsOfVelocity), vh * options.MaxAheadScreens);
        var behindPx = vh;
        var from = y0 - (direction < 0 ? aheadPx : behindPx);
        var to = y1 + (direction > 0 ? aheadPx : behindPx);
        var (aFirst, aLast) = layout.VisiblePages(Math.Max(0, from), to);
        var aheadLowRes = new List<(double Distance, PlannedItem Item)>();
        for (var p = aFirst; p <= aLast; p++)
        {
            if (p < first || p > last)
            {
                AddLowRes(plan, cache, layout, p, options, aheadLowRes, centreY);
            }
        }

        plan.AheadLowRes.AddRange(aheadLowRes
            .OrderBy(t => (direction >= 0 ? t.Item.Key.Page > last : t.Item.Key.Page < first) ? 0 : 1)
            .ThenBy(t => t.Distance)
            .Select(t => t.Item)
            .Take(budget));

        // 4. Prefetch of sharp tiles.
        if (!fast)
        {
            var up = (direction >= 0 ? 0.5 : 1.0) * vh;
            var down = (direction <= 0 ? 0.5 : 1.0) * vh;
            if (direction == 0)
            {
                up = down = vh;
            }

            var (pFirst, pLast) = layout.VisiblePages(Math.Max(0, y0 - up), y1 + down);
            var prefetch = new List<(double Distance, PlannedItem Item)>();
            var prefetchKeys = new HashSet<TileKey>();
            for (var p = pFirst; p <= pLast; p++)
            {
                CollectTiles(plan, cache, layout, p, view.ScrollX, view.ScrollX + vw, y0 - up, y1 + down, scale, turns, centreY, prefetch, prefetchKeys, exclude: visibleKeys);
            }

            plan.Prefetch.AddRange(prefetch.OrderBy(t => t.Distance).Select(t => t.Item).Take(budget));
        }

        // 5. A presentation: the whole of the next spread, the one before, and the one after the next, each page at the scale it will be shown, so that a turn finds its tiles drawn. It waits
        // for the viewport's own tiles (the lanes serve the earlier lists first), and it is left out when it would not fit in the cache beside what is already there.
        if (view.PrefetchAdjacentSpreads && !fast)
        {
            PrefetchAdjacentSpreads(plan, cache, view, scale, turns, centreY, visibleKeys, budget);
        }

        return plan;
    }

    // The share of the cache's budget that prefetching a presentation may fill; the rest is for the page on screen and for the way back.
    private const double AdjacentBudgetShare = 0.7;

    private static void PrefetchAdjacentSpreads(TilePlan plan, TileCache cache, ViewSnapshot view, float scale, int turns, double centreY, HashSet<TileKey> visibleKeys, int budget)
    {
        var layout = view.Layout;
        var current = layout.MostVisibleSpread(view.ScrollY, view.ScrollY + view.ViewportHeight);
        if (current < 0)
        {
            return;
        }

        var already = new HashSet<TileKey>(plan.Prefetch.Select(item => item.Key));
        var candidates = new List<PlannedItem>();
        foreach (var spread in new[] { current + 1, current - 1, current + 2 })
        {
            if (spread < 0 || spread >= layout.SpreadCount)
            {
                continue;
            }

            var (first, last) = layout.PagesOf(spread);
            for (var page = first; page <= last; page++)
            {
                var found = new List<(double Distance, PlannedItem Item)>();
                CollectTiles(plan, cache, layout, page, view.ScrollX, view.ScrollX + view.ViewportWidth, layout.Top(page), layout.Top(page) + layout.Height(page), scale, turns, centreY, found, already, exclude: visibleKeys);
                candidates.AddRange(found.OrderBy(t => t.Distance).Select(t => t.Item));
            }
        }

        var room = (long)(cache.BudgetBytes * AdjacentBudgetShare) - cache.Bytes;
        foreach (var item in candidates.Take(budget))
        {
            room -= (long)item.Width * item.Height * 4 * 2; // as the cache costs a tile: its pixels, and the surface made from them
            if (room < 0)
            {
                break;
            }

            plan.Prefetch.Add(item);
        }
    }

    /// <summary>The scale at which a page's placeholder is rendered so it fits the placeholder box.</summary>
    public static (float Scale, int Width, int Height) LowResSize(SizeD pageSizePt, TilePlannerOptions options)
    {
        var scale = Math.Min(options.LowResWidth / pageSizePt.Width, options.LowResMaxHeight / pageSizePt.Height);
        scale = Math.Clamp(scale, 0.01, ZoomMath.MaxEngineScale);
        var w = (int)Math.Max(1, PageTransform.EngineRounding(pageSizePt.Width * scale));
        var h = (int)Math.Max(1, PageTransform.EngineRounding(pageSizePt.Height * scale));
        return ((float)scale, w, h);
    }

    private static void AddLowRes(TilePlan plan, TileCache cache, DocumentLayout layout, int page, TilePlannerOptions options, List<(double, PlannedItem)> into, double centreY)
    {
        var key = TileKey.LowRes(page, layout.ViewTurns);
        plan.Wanted.Add(key);
        if (cache.Contains(key))
        {
            return;
        }

        // The page size here is already rotated by the view; the request carries the view rotation to the engine, which rotates the page
        // itself, so the size to fit is the rotated one.
        var (scale, w, h) = LowResSize(layout.SizePt(page), options);
        var distance = Math.Abs(layout.Top(page) + (layout.Height(page) / 2.0) - centreY);
        into.Add((distance, new PlannedItem(key, scale, layout.ViewTurns, 0, 0, w, h)));
    }

    private static void CollectTiles(
        TilePlan plan,
        TileCache cache,
        DocumentLayout layout,
        int page,
        double x0,
        double x1,
        double y0,
        double y1,
        float scale,
        int turns,
        double centreY,
        List<(double, PlannedItem)> into,
        HashSet<TileKey> seen,
        HashSet<TileKey>? exclude)
    {
        var left = layout.Left(page, x1 - x0);
        var top = layout.Top(page);
        var k = layout.RenderToLayout;
        var (rw, rh) = layout.RenderSize(page);

        // The viewport region in the page's render pixels.
        var region = new RectD((x0 - left) / k, (y0 - top) / k, (x1 - x0) / k, (y1 - y0) / k);
        var (fx, lx, fy, ly) = TileGrid.Covering(region, rw, rh);
        for (var ty = fy; ty <= ly; ty++)
        {
            for (var tx = fx; tx <= lx; tx++)
            {
                var key = TileKey.ForTile(page, scale, turns, tx, ty);
                if (exclude is not null && exclude.Contains(key))
                {
                    continue;
                }

                plan.Wanted.Add(key);
                if (!seen.Add(key) || cache.Contains(key))
                {
                    continue;
                }

                var rect = TileGrid.Rect(tx, ty, rw, rh);
                var distance = Math.Abs(top + ((rect.Y + (rect.Height / 2.0)) * k) - centreY);
                into.Add((distance, new PlannedItem(key, scale, turns, rect.X, rect.Y, rect.Width, rect.Height)));
            }
        }
    }
}
