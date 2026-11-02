using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Tiles;

/// <summary>
/// Builds what the presenter draws for one frame (master prompt 7.1 rule 3: never show blank space). For every visible page, back to front:
/// the low-res placeholder, then any cached tiles of the page at <i>another</i> scale (stretched, so a zoom change keeps the old picture until
/// the new one is ready), then the sharp tiles at the current scale. Everything drawn is marked as used in the cache for this frame.
/// </summary>
public static class FrameComposer
{
    public static FrameComposition Compose(
        ViewSnapshot view,
        TileCache cache,
        double gestureScale = 1.0,
        PointD gestureOrigin = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(cache);
        var layout = view.Layout;
        var frame = new FrameComposition
        {
            ViewportWidth = view.ViewportWidth,
            ViewportHeight = view.ViewportHeight,
            GestureScale = gestureScale,
            GestureOrigin = gestureOrigin,
        };
        cache.BeginFrame();
        if (layout.PageCount == 0)
        {
            return frame;
        }

        var viewport = new RectD(0, 0, view.ViewportWidth, view.ViewportHeight);
        var (first, last) = layout.VisiblePages(view.ScrollY, view.ScrollY + view.ViewportHeight);
        var currentBits = BitConverter.SingleToUInt32Bits((float)layout.RenderScale);
        var k = layout.RenderToLayout;
        for (var p = first; p <= last; p++)
        {
            var pageRect = new RectD(layout.Left(p, view.ViewportWidth) - view.ScrollX, layout.Top(p) - view.ScrollY, layout.Width(p), layout.Height(p));
            var draw = new PageDraw(p, pageRect);
            frame.Pages.Add(draw);

            var visiblePart = pageRect.Intersect(viewport);
            var pageArea = visiblePart.Width * visiblePart.Height;

            // 1. The placeholder, stretched over the whole page.
            var lowKey = TileKey.LowRes(p, layout.ViewTurns);
            var haveLowRes = cache.Touch(lowKey, out var lowSurface);
            if (haveLowRes)
            {
                draw.Layers.Add(new SurfaceDraw(lowSurface, pageRect));
            }

            // 2. Tiles of other scales, farthest scale first, so the closest one is on top.
            var others = cache.KeysForPage(p)
                .Where(key => !key.IsLowRes && key.Turns == layout.ViewTurns && key.ScaleBits != currentBits)
                .OrderByDescending(key => Math.Abs(Math.Log(key.Scale / layout.RenderScale)))
                .ToList();
            foreach (var key in others)
            {
                var (rw, rh) = layout.RenderSize(p, key.Scale);
                var rect = TileGrid.Rect(key.Tx, key.Ty, rw, rh);
                var destination = new RectD(pageRect.X + ((double)rect.X / rw * pageRect.Width), pageRect.Y + ((double)rect.Y / rh * pageRect.Height), (double)rect.Width / rw * pageRect.Width, (double)rect.Height / rh * pageRect.Height);
                if (destination.Intersects(viewport) && cache.Touch(key, out var stale))
                {
                    draw.Layers.Add(new SurfaceDraw(stale, destination));
                }
            }

            // 3. Sharp tiles at the current scale.
            var (rw0, rh0) = layout.RenderSize(p);
            var local = new RectD(-pageRect.X / k, -pageRect.Y / k, view.ViewportWidth / k, view.ViewportHeight / k);
            var (fx, lx, fy, ly) = TileGrid.Covering(local, rw0, rh0);
            double sharp = 0;
            for (var ty = fy; ty <= ly; ty++)
            {
                for (var tx = fx; tx <= lx; tx++)
                {
                    var key = TileKey.ForTile(p, (float)layout.RenderScale, layout.ViewTurns, tx, ty);
                    var rect = TileGrid.Rect(tx, ty, rw0, rh0);
                    var destination = new RectD(pageRect.X + (rect.X * k), pageRect.Y + (rect.Y * k), rect.Width * k, rect.Height * k);
                    if (cache.Touch(key, out var surface))
                    {
                        draw.Layers.Add(new SurfaceDraw(surface, destination));
                        var inView = destination.Intersect(viewport);
                        sharp += inView.Width * inView.Height;
                    }
                }
            }

            frame.SharpArea += sharp;
            var rest = Math.Max(0, pageArea - sharp);
            if (haveLowRes || others.Count > 0)
            {
                frame.FallbackArea += rest;
            }
            else
            {
                frame.BlankArea += rest;
            }
        }

        return frame;
    }
}
