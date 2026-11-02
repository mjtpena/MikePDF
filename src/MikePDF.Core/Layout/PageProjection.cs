using MikePDF.Core.Geometry;

namespace MikePDF.Core.Layout;

/// <summary>
/// Where one page is in the viewport and how to get from the page's own space (points, the page's /Rotate applied, origin top-left) to the
/// viewport's pixels and back: the page's place in the layout, the scroll position, the view rotation and the zoom. It is what selection,
/// search highlights, link hit-testing and the accessibility rectangles all use, so they agree with what is drawn.
/// </summary>
public sealed class PageProjection
{
    private readonly PageTransform _transform;

    private PageProjection(int page, RectD pageRect, PageTransform transform)
    {
        Page = page;
        PageRect = pageRect;
        _transform = transform;
    }

    public int Page { get; }

    /// <summary>The page in viewport pixels.</summary>
    public RectD PageRect { get; }

    /// <summary>The page in points before the view rotation.</summary>
    public SizeD PageSize => _transform.PageSize;

    /// <summary>The projection of one page in a view, or null if the page does not exist.</summary>
    public static PageProjection? For(ViewSnapshot view, int page)
    {
        ArgumentNullException.ThrowIfNull(view);
        var layout = view.Layout;
        if (page < 0 || page >= layout.PageCount)
        {
            return null;
        }

        var unrotated = QuarterTurns.Rotate(layout.SizePt(page), 4 - layout.ViewTurns);
        var transform = new PageTransform(unrotated, layout.ViewTurns, layout.LayoutScale);
        var left = layout.Left(page, view.ViewportWidth) - view.ScrollX;
        var top = layout.Top(page) - view.ScrollY;
        return new PageProjection(page, new RectD(left, top, layout.Width(page), layout.Height(page)), transform);
    }

    /// <summary>The page under (or nearest to) a viewport point, with its projection; null for an empty document.</summary>
    public static PageProjection? At(ViewSnapshot view, PointD viewportPoint)
    {
        ArgumentNullException.ThrowIfNull(view);
        var page = view.Layout.PageAt(viewportPoint.Y + view.ScrollY);
        return For(view, page);
    }

    public PointD PageToViewport(PointD pagePoint)
    {
        var device = _transform.PageToDevice(pagePoint);
        return new PointD(PageRect.X + device.X, PageRect.Y + device.Y);
    }

    public PointD ViewportToPage(PointD viewportPoint) =>
        _transform.DeviceToPage(new PointD(viewportPoint.X - PageRect.X, viewportPoint.Y - PageRect.Y));

    /// <summary>The viewport box of a page-space rectangle (the bounding box of its corners, whatever the view rotation).</summary>
    public RectD PageToViewport(RectD pageRect)
    {
        var a = PageToViewport(new PointD(pageRect.X, pageRect.Y));
        var b = PageToViewport(new PointD(pageRect.Right, pageRect.Bottom));
        return new RectD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
