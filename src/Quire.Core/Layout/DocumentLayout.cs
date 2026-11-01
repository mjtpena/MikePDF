using Quire.Core.Geometry;

namespace Quire.Core.Layout;

/// <summary>A point of the document remembered across a zoom or a resize: a page and a fraction of its size.</summary>
public readonly record struct LayoutAnchor(int Page, double FractionX, double FractionY);

/// <summary>
/// The pages of a document stacked in one column (continuous scroll), in layout pixels (device pixels at the current zoom).
/// Immutable. Page sizes are the displayed sizes in points (after /Rotate); the view rotation is applied here.
/// <para>
/// Layout pixels and render pixels differ only above <see cref="ZoomMath.MaxEngineScale"/>, where tiles are rendered at the engine's limit and
/// stretched: <see cref="RenderToLayout"/> is then above 1.
/// </para>
/// </summary>
public sealed class DocumentLayout
{
    private readonly double[] _top;
    private readonly long[] _width;
    private readonly long[] _height;
    private readonly SizeD[] _sizePt;

    public DocumentLayout(IReadOnlyList<SizeD> pageSizes, int viewTurns, double layoutScale, double gapPx, double marginPx)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (!(layoutScale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(layoutScale));
        }

        PageCount = pageSizes.Count;
        ViewTurns = QuarterTurns.Normalize(viewTurns);
        LayoutScale = layoutScale;
        GapPx = gapPx;
        MarginPx = marginPx;
        RenderScale = ZoomMath.EngineScale(layoutScale);
        _top = new double[PageCount];
        _width = new long[PageCount];
        _height = new long[PageCount];
        _sizePt = new SizeD[PageCount];
        var y = marginPx;
        long maxWidth = 0;
        for (var i = 0; i < PageCount; i++)
        {
            var size = QuarterTurns.Rotate(pageSizes[i], ViewTurns);
            _sizePt[i] = size;
            _width[i] = Math.Max(1, PageTransform.EngineRounding(size.Width * layoutScale));
            _height[i] = Math.Max(1, PageTransform.EngineRounding(size.Height * layoutScale));
            _top[i] = y;
            y += _height[i] + gapPx;
            maxWidth = Math.Max(maxWidth, _width[i]);
        }

        ContentWidth = maxWidth + (2 * marginPx);
        ContentHeight = PageCount == 0 ? 2 * marginPx : y - gapPx + marginPx;
    }

    public int PageCount { get; }

    public int ViewTurns { get; }

    /// <summary>Layout pixels per point.</summary>
    public double LayoutScale { get; }

    /// <summary>The scale tiles are requested at (device pixels per point, at most the engine's limit).</summary>
    public double RenderScale { get; }

    public double RenderToLayout => LayoutScale / RenderScale;

    public double GapPx { get; }

    public double MarginPx { get; }

    public double ContentWidth { get; }

    public double ContentHeight { get; }

    public double Top(int page) => _top[page];

    public long Width(int page) => _width[page];

    public long Height(int page) => _height[page];

    /// <summary>The displayed size in points after the view rotation.</summary>
    public SizeD SizePt(int page) => _sizePt[page];

    /// <summary>The page size in render pixels (what the engine computes for the tile grid).</summary>
    public (long Width, long Height) RenderSize(int page, double? renderScale = null)
    {
        var scale = renderScale ?? RenderScale;
        return (Math.Max(1, PageTransform.EngineRounding(_sizePt[page].Width * scale)), Math.Max(1, PageTransform.EngineRounding(_sizePt[page].Height * scale)));
    }

    /// <summary>X of the page's left edge in document space for a viewport of the given width: pages are centred, or left-aligned at the margin when wider than the viewport.</summary>
    public double Left(int page, double viewportWidth) => (Math.Max(viewportWidth, ContentWidth) - _width[page]) / 2;

    /// <summary>The page containing document y, or the nearest one when y is in a gap or outside.</summary>
    public int PageAt(double y)
    {
        if (PageCount == 0)
        {
            return -1;
        }

        var lo = 0;
        var hi = PageCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_top[mid] <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>The pages intersecting document y range [y0, y1). Empty when there are none.</summary>
    public (int First, int Last) VisiblePages(double y0, double y1)
    {
        if (PageCount == 0 || y1 <= 0 || y0 >= ContentHeight)
        {
            return (0, -1);
        }

        var first = PageAt(Math.Max(0, y0));
        if (_top[first] + _height[first] <= y0 && first + 1 < PageCount)
        {
            first++;
        }

        var last = PageAt(y1 - 1e-9);
        return last < first ? (0, -1) : (first, last);
    }

    /// <summary>The page with the most of its area inside [y0, y1) (the page indicator). Ties go to the earlier page.</summary>
    public int MostVisiblePage(double y0, double y1)
    {
        var (first, last) = VisiblePages(y0, y1);
        if (last < first)
        {
            return PageCount == 0 ? -1 : (y0 <= 0 ? 0 : PageCount - 1);
        }

        var best = first;
        var bestOverlap = -1.0;
        for (var i = first; i <= last; i++)
        {
            var overlap = Math.Min(y1, _top[i] + _height[i]) - Math.Max(y0, _top[i]);
            if (overlap > bestOverlap + 1e-9)
            {
                best = i;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    /// <summary>The anchor for a document-space point.</summary>
    public LayoutAnchor AnchorAt(double docX, double docY, double viewportWidth)
    {
        var page = PageAt(docY);
        if (page < 0)
        {
            return new LayoutAnchor(0, 0, 0);
        }

        return new LayoutAnchor(page, (docX - Left(page, viewportWidth)) / _width[page], (docY - _top[page]) / _height[page]);
    }

    /// <summary>The document-space point of an anchor.</summary>
    public PointD PointOf(LayoutAnchor anchor, double viewportWidth)
    {
        if (PageCount == 0)
        {
            return new PointD(0, 0);
        }

        var page = Math.Clamp(anchor.Page, 0, PageCount - 1);
        return new PointD(Left(page, viewportWidth) + (anchor.FractionX * _width[page]), _top[page] + (anchor.FractionY * _height[page]));
    }
}
