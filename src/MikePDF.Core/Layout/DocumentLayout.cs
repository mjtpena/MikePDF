using MikePDF.Core.Geometry;

namespace MikePDF.Core.Layout;

/// <summary>A point of the document remembered across a zoom or a resize: a page and a fraction of the size of its spread (of the page, when it is alone in its spread).</summary>
public readonly record struct LayoutAnchor(int Page, double FractionX, double FractionY);

/// <summary>
/// The pages of a document stacked in one column (continuous scroll), or in spreads of one or two pages side by side, in layout pixels (device pixels at the
/// current zoom). Immutable. Page sizes are the displayed sizes in points (after /Rotate); the view rotation is applied here.
/// <para>
/// Layout pixels and render pixels differ only above <see cref="ZoomMath.MaxEngineScale"/>, where tiles are rendered at the engine's limit and
/// stretched: <see cref="RenderToLayout"/> is then above 1.
/// </para>
/// <para>
/// A spread (docs/features/page-view-modes.md 3.2) is a row of one or two pages. Spreads are stacked with the gap between them; the pages of a spread are
/// centred on each other vertically and a gutter apart. With single-page spreads (the default) every result is the same as for a plain column of pages.
/// </para>
/// </summary>
public sealed class DocumentLayout
{
    private readonly double[] _top;
    private readonly double[] _xInSpread;
    private readonly long[] _width;
    private readonly long[] _height;
    private readonly SizeD[] _sizePt;
    private readonly double[] _spreadTop;
    private readonly long[] _spreadWidth;
    private readonly long[] _spreadHeight;

    public DocumentLayout(IReadOnlyList<SizeD> pageSizes, int viewTurns, double layoutScale, double gapPx, double marginPx, SpreadMap? spreads = null, double gutterPx = 0)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (!(layoutScale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(layoutScale));
        }

        PageCount = pageSizes.Count;
        Spreads = spreads is not null && spreads.PageCount == PageCount ? spreads : SpreadMap.Column(PageCount);
        GutterPx = Math.Max(0, gutterPx);
        ViewTurns = QuarterTurns.Normalize(viewTurns);
        LayoutScale = layoutScale;
        GapPx = gapPx;
        MarginPx = marginPx;
        RenderScale = ZoomMath.EngineScale(layoutScale);
        _top = new double[PageCount];
        _xInSpread = new double[PageCount];
        _width = new long[PageCount];
        _height = new long[PageCount];
        _sizePt = new SizeD[PageCount];
        for (var i = 0; i < PageCount; i++)
        {
            var size = QuarterTurns.Rotate(pageSizes[i], ViewTurns);
            _sizePt[i] = size;
            _width[i] = Math.Max(1, PageTransform.EngineRounding(size.Width * layoutScale));
            _height[i] = Math.Max(1, PageTransform.EngineRounding(size.Height * layoutScale));
        }

        var count = Spreads.SpreadCount;
        _spreadTop = new double[count];
        _spreadWidth = new long[count];
        _spreadHeight = new long[count];
        var gutter = (long)Math.Round(GutterPx);
        var y = marginPx;
        long maxWidth = 0;
        for (var s = 0; s < count; s++)
        {
            var (left, right) = Spreads.Slots(s);
            long spreadWidth;
            long spreadHeight;
            if (Spreads.IsSingle)
            {
                spreadWidth = _width[left];
                spreadHeight = _height[left];
            }
            else
            {
                // A lone page keeps its half of the spread; the other half is blank and as wide as the page.
                var leftWidth = left >= 0 ? _width[left] : _width[right];
                var rightWidth = right >= 0 ? _width[right] : _width[left];
                spreadWidth = leftWidth + gutter + rightWidth;
                spreadHeight = Math.Max(left >= 0 ? _height[left] : 0, right >= 0 ? _height[right] : 0);
                if (left >= 0)
                {
                    _xInSpread[left] = 0;
                }

                if (right >= 0)
                {
                    _xInSpread[right] = leftWidth + gutter;
                }
            }

            _spreadTop[s] = y;
            _spreadWidth[s] = spreadWidth;
            _spreadHeight[s] = spreadHeight;
            if (left >= 0)
            {
                _top[left] = y + ((spreadHeight - _height[left]) / 2.0);
            }

            if (right >= 0)
            {
                _top[right] = y + ((spreadHeight - _height[right]) / 2.0);
            }

            y += spreadHeight + gapPx;
            maxWidth = Math.Max(maxWidth, spreadWidth);
        }

        ContentWidth = maxWidth + (2 * marginPx);
        ContentHeight = count == 0 ? 2 * marginPx : y - gapPx + marginPx;
    }

    public int PageCount { get; }

    /// <summary>Which pages share a row.</summary>
    public SpreadMap Spreads { get; }

    public int SpreadCount => Spreads.SpreadCount;

    public int ViewTurns { get; }

    /// <summary>Layout pixels per point.</summary>
    public double LayoutScale { get; }

    /// <summary>The scale tiles are requested at (device pixels per point, at most the engine's limit).</summary>
    public double RenderScale { get; }

    public double RenderToLayout => LayoutScale / RenderScale;

    /// <summary>Space between spreads.</summary>
    public double GapPx { get; }

    /// <summary>Space between the two pages of a spread.</summary>
    public double GutterPx { get; }

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

    public int SpreadOf(int page) => Spreads.SpreadOf(page);

    /// <summary>The first and last page of a spread.</summary>
    public (int First, int Last) PagesOf(int spread) => Spreads.PagesOf(spread);

    public double SpreadTop(int spread) => _spreadTop[spread];

    public long SpreadHeight(int spread) => _spreadHeight[spread];

    public long SpreadWidth(int spread) => _spreadWidth[spread];

    /// <summary>X of the spread's left edge in document space for a viewport of the given width: spreads are centred, or left-aligned at the margin when wider than the viewport.</summary>
    public double SpreadLeft(int spread, double viewportWidth) => (Math.Max(viewportWidth, ContentWidth) - _spreadWidth[spread]) / 2;

    /// <summary>X of the page's left edge in document space for a viewport of the given width: pages are centred, or left-aligned at the margin when wider than the viewport.</summary>
    public double Left(int page, double viewportWidth) => SpreadLeft(Spreads.SpreadOf(page), viewportWidth) + _xInSpread[page];

    /// <summary>The spread containing document y, or the nearest one when y is in a gap or outside; -1 for an empty document.</summary>
    public int SpreadAtY(double y)
    {
        if (SpreadCount == 0)
        {
            return -1;
        }

        var lo = 0;
        var hi = SpreadCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_spreadTop[mid] <= y)
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

    /// <summary>The page containing document y, or the nearest one when y is in a gap or outside. In a spread of two, the first of its pages.</summary>
    public int PageAt(double y)
    {
        var spread = SpreadAtY(y);
        return spread < 0 ? -1 : Spreads.FirstPage(spread);
    }

    /// <summary>The page under (or nearest to) a document point: the spread by y, then the page of the spread by x.</summary>
    public int PageAt(double docX, double docY, double viewportWidth)
    {
        var spread = SpreadAtY(docY);
        if (spread < 0)
        {
            return -1;
        }

        var (first, last) = Spreads.PagesOf(spread);
        if (first == last)
        {
            return first;
        }

        // The two pages of a two-page spread: the one whose half the point is in (the gutter's middle divides them).
        var (left, right) = Spreads.Slots(spread);
        var middle = Left(right, viewportWidth) - (GutterPx / 2);
        return docX < middle ? left : right;
    }

    /// <summary>The pages in the spreads intersecting the document y range [y0, y1) (the first page of the first spread to the last page of the last). Empty when there are none.</summary>
    public (int First, int Last) VisiblePages(double y0, double y1)
    {
        if (PageCount == 0 || y1 <= 0 || y0 >= ContentHeight)
        {
            return (0, -1);
        }

        var first = SpreadAtY(Math.Max(0, y0));
        if (_spreadTop[first] + _spreadHeight[first] <= y0 && first + 1 < SpreadCount)
        {
            first++;
        }

        var last = SpreadAtY(y1 - 1e-9);
        return last < first ? (0, -1) : (Spreads.FirstPage(first), Spreads.PagesOf(last).Last);
    }

    /// <summary>The first page of the spread with the most of its area inside [y0, y1) (the page indicator). Ties go to the earlier spread.</summary>
    public int MostVisiblePage(double y0, double y1)
    {
        var spread = MostVisibleSpread(y0, y1);
        return spread < 0 ? -1 : Spreads.FirstPage(spread);
    }

    /// <summary>The spread with the most of its area inside [y0, y1). Ties go to the earlier spread. -1 for an empty document.</summary>
    public int MostVisibleSpread(double y0, double y1)
    {
        var (firstPage, lastPage) = VisiblePages(y0, y1);
        if (lastPage < firstPage)
        {
            return PageCount == 0 ? -1 : (y0 <= 0 ? 0 : SpreadCount - 1);
        }

        var first = Spreads.SpreadOf(firstPage);
        var last = Spreads.SpreadOf(lastPage);
        var best = first;
        var bestOverlap = -1.0;
        for (var i = first; i <= last; i++)
        {
            var overlap = Math.Min(y1, _spreadTop[i] + _spreadHeight[i]) - Math.Max(y0, _spreadTop[i]);
            if (overlap > bestOverlap + 1e-9)
            {
                best = i;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    /// <summary>The anchor for a document-space point: the page under it, and the point as fractions of that page's spread (a page alone in its spread: of the page), so a spread whose pages change size does not move the anchor.</summary>
    public LayoutAnchor AnchorAt(double docX, double docY, double viewportWidth)
    {
        var page = PageAt(docX, docY, viewportWidth);
        if (page < 0)
        {
            return new LayoutAnchor(0, 0, 0);
        }

        var spread = Spreads.SpreadOf(page);
        return new LayoutAnchor(page, (docX - SpreadLeft(spread, viewportWidth)) / _spreadWidth[spread], (docY - _spreadTop[spread]) / _spreadHeight[spread]);
    }

    /// <summary>The document-space point of an anchor.</summary>
    public PointD PointOf(LayoutAnchor anchor, double viewportWidth)
    {
        if (PageCount == 0)
        {
            return new PointD(0, 0);
        }

        var page = Math.Clamp(anchor.Page, 0, PageCount - 1);
        var spread = Spreads.SpreadOf(page);
        return new PointD(SpreadLeft(spread, viewportWidth) + (anchor.FractionX * _spreadWidth[spread]), _spreadTop[spread] + (anchor.FractionY * _spreadHeight[spread]));
    }
}
