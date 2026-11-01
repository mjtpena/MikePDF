using Quire.Core.Geometry;

namespace Quire.Core.Layout;

/// <summary>Everything a renderer needs to know about the current view.</summary>
public sealed record ViewSnapshot(DocumentLayout Layout, double ScrollX, double ScrollY, double ViewportWidth, double ViewportHeight, double VelocityPxPerSecond);

/// <summary>
/// The scroll, zoom, fit and rotation state of one open document, and the rules that change it (continuous scroll, master prompt 2.1).
/// No UI types: the window feeds it viewport sizes, wheel and key commands and reads the layout back. Not thread-safe; use it on the UI thread.
/// <para>
/// Zooming keeps the document point under the anchor (the pointer, or the viewport centre) where it was. Rotating and resizing keep the
/// page at the top of the viewport. Fit modes follow the window until the user picks a number.
/// </para>
/// </summary>
public sealed class ViewportController
{
    /// <summary>Space between pages and around the column, in device-independent pixels.</summary>
    public const double GapDip = 8;
    public const double MarginDip = 12;

    private IReadOnlyList<SizeD> _sizes = [];
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private double _dpiScale = 1.0;
    private DocumentLayout _layout = new([], 0, 1, 0, 0);

    public double Zoom { get; private set; } = 1.0;

    public FitMode Fit { get; private set; } = FitMode.Width;

    public int ViewTurns { get; private set; }

    public double ScrollX { get; private set; }

    public double ScrollY { get; private set; }

    public double ViewportWidth => _viewportWidth;

    public double ViewportHeight => _viewportHeight;

    public double DpiScale => _dpiScale;

    public DocumentLayout Layout => _layout;

    /// <summary>Incremented whenever anything a presenter or the page indicator shows may have changed.</summary>
    public long Version { get; private set; }

    public int PageCount => _layout.PageCount;

    /// <summary>The page with most of its area in view, zero-based; -1 for an empty document.</summary>
    public int CurrentPage => _layout.MostVisiblePage(ScrollY, ScrollY + _viewportHeight);

    /// <summary>Where the viewport top is within the page it falls on, as a fraction of the page height. For "reopen at the last position".</summary>
    public (int Page, double Fraction) TopPosition
    {
        get
        {
            var anchor = TopAnchor();
            return (anchor.Page, Math.Clamp(anchor.FractionY, 0, 1));
        }
    }

    public ViewSnapshot Snapshot(double velocityPxPerSecond = 0) => new(_layout, ScrollX, ScrollY, _viewportWidth, _viewportHeight, velocityPxPerSecond);

    // ---- inputs ----------------------------------------------------------------------------------

    /// <summary>
    /// Sets the page sizes (displayed size in points, after /Rotate). Called once when the document opens and again as more sizes are
    /// learned; the page at the top of the viewport stays put.
    /// </summary>
    public void SetPageSizes(IReadOnlyList<SizeD> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var hadPages = _layout.PageCount > 0;
        var top = hadPages ? TopAnchor() : default;
        _sizes = sizes;
        ApplyFit();
        Rebuild(hadPages ? top : null, 0, 0);
    }

    public void SetViewport(double width, double height, double dpiScale)
    {
        if (!(width > 0) || !(height > 0) || !(dpiScale > 0))
        {
            return;
        }

        if (width == _viewportWidth && height == _viewportHeight && dpiScale == _dpiScale)
        {
            return;
        }

        var top = _layout.PageCount > 0 ? TopAnchor() : (LayoutAnchor?)null;
        _viewportWidth = width;
        _viewportHeight = height;
        _dpiScale = dpiScale;
        ApplyFit();
        Rebuild(top, 0, 0);
    }

    // ---- commands --------------------------------------------------------------------------------

    /// <summary>Sets an exact zoom (clamped to 10% to 6400%) and leaves fit mode. The anchor is a viewport point (default: the viewport centre).</summary>
    public void ZoomTo(double zoom, PointD? viewportAnchor = null)
    {
        Fit = FitMode.None;
        SetZoom(zoom, viewportAnchor);
    }

    public void ZoomBy(double factor, PointD? viewportAnchor = null) => ZoomTo(Zoom * factor, viewportAnchor);

    public void ZoomStep(int direction, PointD? viewportAnchor = null) => ZoomTo(ZoomMath.NextStep(Zoom, direction), viewportAnchor);

    public void ActualSize(PointD? viewportAnchor = null) => ZoomTo(1.0, viewportAnchor);

    public void FitWidth() => SetFit(FitMode.Width);

    public void FitPage() => SetFit(FitMode.Page);

    /// <summary>Turns the view (not the file) by quarter turns clockwise.</summary>
    public void Rotate(int quarterTurns)
    {
        // Turning the view changes which edge of a page is "top", so keep the page in view, not the point.
        var page = _layout.PageCount > 0 ? CurrentPage : -1;
        ViewTurns = QuarterTurns.Normalize(ViewTurns + quarterTurns);
        ApplyFit();
        Rebuild(page >= 0 ? new LayoutAnchor(page, 0, 0) : null, 0, 0);
    }

    public void ScrollBy(double dx, double dy) => ScrollTo(ScrollX + dx, ScrollY + dy);

    public void ScrollTo(double x, double y)
    {
        var cx = ClampX(x);
        var cy = ClampY(y);
        if (cx != ScrollX || cy != ScrollY)
        {
            ScrollX = cx;
            ScrollY = cy;
            Version++;
        }
    }

    /// <summary>Scrolls so the page's top is at the top of the viewport (centred when the whole page fits).</summary>
    public void GoToPage(int page, double fractionOfPage = 0)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        page = Math.Clamp(page, 0, _layout.PageCount - 1);
        var top = _layout.Top(page) + (Math.Clamp(fractionOfPage, 0, 1) * _layout.Height(page));
        var spare = _viewportHeight - _layout.Height(page);
        var y = fractionOfPage == 0 && spare > 2 * _layout.MarginPx ? top - (spare / 2) : top - (fractionOfPage == 0 ? _layout.MarginPx : 0);
        ScrollTo(ScrollX, y);
    }

    public void NextPage() => GoToPage(Math.Min(CurrentPage + 1, Math.Max(0, PageCount - 1)));

    public void PreviousPage() => GoToPage(Math.Max(CurrentPage - 1, 0));

    public void FirstPage() => GoToPage(0);

    public void LastPage() => GoToPage(PageCount - 1);

    /// <summary>One viewport height less a little overlap, for Page Down and Space when pages are taller than the window.</summary>
    public void ScrollScreen(int direction) => ScrollBy(0, direction * Math.Max(1, _viewportHeight * 0.9));

    /// <summary>Restores a saved view (the zoom is applied as a number; fit mode as saved).</summary>
    public void Restore(int page, double fraction, double zoom, FitMode fit, int viewTurns)
    {
        ViewTurns = QuarterTurns.Normalize(viewTurns);
        Zoom = ZoomMath.Clamp(zoom);
        Fit = fit;
        ApplyFit();
        Rebuild(null, 0, 0);
        GoToPage(page, fraction);
    }

    // ---- internals -------------------------------------------------------------------------------

    /// <summary>The point at the top of the viewport, relative to the first page that shows there (the fraction is negative while only the gap above it is in view).</summary>
    private LayoutAnchor TopAnchor()
    {
        var (first, last) = _layout.VisiblePages(ScrollY, ScrollY + _viewportHeight);
        if (last < first)
        {
            return _layout.AnchorAt(ScrollX, ScrollY, _viewportWidth);
        }

        var x = (ScrollX - _layout.Left(first, _viewportWidth)) / _layout.Width(first);
        return new LayoutAnchor(first, x, (ScrollY - _layout.Top(first)) / _layout.Height(first));
    }

    private void SetFit(FitMode mode)
    {
        var top = _layout.PageCount > 0 ? TopAnchor() : (LayoutAnchor?)null;
        Fit = mode;
        ApplyFit();
        Rebuild(top is { } a ? new LayoutAnchor(a.Page, 0, a.FractionY) : null, 0, 0);
    }

    private void SetZoom(double zoom, PointD? viewportAnchor)
    {
        var target = ZoomMath.Clamp(zoom);
        if (_layout.PageCount == 0)
        {
            Zoom = target;
            Rebuild(null, 0, 0);
            return;
        }

        var anchorPoint = viewportAnchor ?? new PointD(_viewportWidth / 2, _viewportHeight / 2);
        var anchor = _layout.AnchorAt(ScrollX + anchorPoint.X, ScrollY + anchorPoint.Y, _viewportWidth);
        Zoom = target;
        Rebuild(anchor, anchorPoint.X, anchorPoint.Y);
    }

    /// <summary>Recomputes Zoom for the fit modes from the page at the top of the viewport.</summary>
    private void ApplyFit()
    {
        if (Fit == FitMode.None || _sizes.Count == 0)
        {
            return;
        }

        var page = _layout.PageCount > 0 ? Math.Clamp(CurrentPage, 0, _sizes.Count - 1) : 0;
        var size = QuarterTurns.Rotate(_sizes[page], ViewTurns);
        var margin = MarginDip * _dpiScale;
        Zoom = Fit == FitMode.Width
            ? ZoomMath.FitWidth(_viewportWidth, size.Width, _dpiScale, margin)
            : ZoomMath.FitPage(_viewportWidth, _viewportHeight, size, _dpiScale, margin);
    }

    /// <summary>Builds the layout for the current zoom and puts the anchor back at the given viewport point.</summary>
    private void Rebuild(LayoutAnchor? anchor, double viewportX, double viewportY)
    {
        var scale = ZoomMath.PixelsPerPoint(Zoom, _dpiScale);
        _layout = new DocumentLayout(_sizes, ViewTurns, scale, GapDip * _dpiScale, MarginDip * _dpiScale);
        if (anchor is { } a && _layout.PageCount > 0)
        {
            var point = _layout.PointOf(a, _viewportWidth);
            ScrollX = ClampX(point.X - viewportX);
            ScrollY = ClampY(point.Y - viewportY);
        }
        else
        {
            ScrollX = ClampX(ScrollX);
            ScrollY = ClampY(ScrollY);
        }

        Version++;
    }

    private double ClampX(double x) => Math.Clamp(x, 0, Math.Max(0, _layout.ContentWidth - _viewportWidth));

    private double ClampY(double y) => Math.Clamp(y, 0, Math.Max(0, _layout.ContentHeight - _viewportHeight));
}
