using MikePDF.Core.Geometry;

namespace MikePDF.Core.Layout;

/// <summary>Everything a renderer needs to know about the current view. With <c>PrefetchAdjacentSpreads</c> the whole spreads before and after the one shown are also drawn ahead (a presentation: the next page must be there the moment it is asked for).</summary>
public sealed record ViewSnapshot(DocumentLayout Layout, double ScrollX, double ScrollY, double ViewportWidth, double ViewportHeight, double VelocityPxPerSecond, bool PrefetchAdjacentSpreads = false);

/// <summary>
/// The scroll, zoom, fit and rotation state of one open document, and the rules that change it (continuous scroll, master prompt 2.1; the page view modes of
/// docs/features/page-view-modes.md: single pages or spreads, continuous or one at a time, presentation). No UI types: the window feeds it viewport sizes,
/// wheel and key commands and reads the layout back. Not thread-safe; use it on the UI thread.
/// <para>
/// Zooming keeps the document point under the anchor (the pointer, or the viewport centre) where it was. Rotating and resizing keep the
/// page at the top of the viewport. Fit modes follow the window until the user picks a number.
/// </para>
/// <para>
/// In the paged flow the layout puts a gap of most of a viewport between spreads and the scroll is held inside the spread being shown, so nothing of the next
/// one is ever on screen; a spread that fits the window is centred in it (the scroll can then be negative), and turning shows the next spread.
/// </para>
/// </summary>
public sealed class ViewportController
{
    /// <summary>Space between spreads and around the column, in device-independent pixels.</summary>
    public const double GapDip = 8;
    public const double MarginDip = 12;

    /// <summary>Space between the two pages of a spread (design 04: a 2 epx gutter).</summary>
    public const double GutterDip = 2;

    /// <summary>The wheel turns a page at most this often (design 03: a 250 ms debounce).</summary>
    public const long WheelTurnDebounceMs = 250;

    private IReadOnlyList<SizeD> _sizes = [];
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private double _dpiScale = 1.0;
    private DocumentLayout _layout = new([], 0, 1, 0, 0);
    private SpreadMap _map = SpreadMap.Column(0);
    private ReadingDirection _documentDirection = ReadingDirection.Auto;
    private int _pagedSpread;
    private long _lastTurnTick = long.MinValue / 2;
    private PresentationBackup? _backup;
    private int _modePage = -1;
    private double _modeScrollX;
    private double _modeScrollY;

    private sealed record PresentationBackup(ViewMode Mode, double Zoom, FitMode Fit, int Page, double Fraction);

    public double Zoom { get; private set; } = 1.0;

    public FitMode Fit { get; private set; } = FitMode.Width;

    public int ViewTurns { get; private set; }

    public double ScrollX { get; private set; }

    public double ScrollY { get; private set; }

    public double ViewportWidth => _viewportWidth;

    public double ViewportHeight => _viewportHeight;

    public double DpiScale => _dpiScale;

    public DocumentLayout Layout => _layout;

    /// <summary>How the pages are arranged and turned, and the user's reading-direction choice.</summary>
    public ViewMode Mode { get; private set; }

    /// <summary>Presentation mode is on: one page at a time, fit to the window, no margin.</summary>
    public bool Presenting { get; private set; }

    /// <summary>The binding side the document asks for (<c>/ViewerPreferences /Direction</c>); <c>Auto</c> when it says nothing.</summary>
    public ReadingDirection DocumentDirection => _documentDirection;

    /// <summary>Spreads run right to left: the user said so, or left it to the document and the document says so.</summary>
    public bool RightToLeft => Mode.Direction == ReadingDirection.RightToLeft || (Mode.Direction == ReadingDirection.Auto && _documentDirection == ReadingDirection.RightToLeft);

    /// <summary>Incremented whenever anything a presenter or the page indicator shows may have changed.</summary>
    public long Version { get; private set; }

    public int PageCount => _layout.PageCount;

    public int SpreadCount => _layout.SpreadCount;

    /// <summary>The page with most of its area in view, zero-based; -1 for an empty document. In a spread, the first page of the one with the most in view.</summary>
    public int CurrentPage => Mode.IsPaged && _layout.SpreadCount > 0 ? _map.FirstPage(_pagedSpread) : _layout.MostVisiblePage(ScrollY, ScrollY + _viewportHeight);

    /// <summary>The spread with most of its area in view (the one shown, when paged); -1 for an empty document.</summary>
    public int CurrentSpread => _layout.SpreadCount == 0 ? -1 : Mode.IsPaged ? Math.Clamp(_pagedSpread, 0, _layout.SpreadCount - 1) : _layout.MostVisibleSpread(ScrollY, ScrollY + _viewportHeight);

    /// <summary>The first and last page of the current spread (equal for one page); (0, -1) for an empty document.</summary>
    public (int First, int Last) CurrentPages => _layout.SpreadCount == 0 ? (0, -1) : _map.PagesOf(CurrentSpread);

    /// <summary>Where the viewport top is within the page it falls on, as a fraction of the page height. For "reopen at the last position".</summary>
    public (int Page, double Fraction) TopPosition
    {
        get
        {
            var anchor = TopAnchor();
            return (anchor.Page, Math.Clamp(anchor.FractionY, 0, 1));
        }
    }

    /// <summary>
    /// The zoom, fit and mode to remember for this document (a position, the session file). Presentation is never remembered: while it is on, these are the ones it will go back to.
    /// </summary>
    public (double Zoom, FitMode Fit, ViewMode Mode) PersistedView => Presenting && _backup is { } back ? (back.Zoom, back.Fit, back.Mode) : (Zoom, Fit, Mode);

    /// <summary>Whether the spreads beside the one shown are drawn ahead in full while presenting (the default). A test turns it off to measure what it buys.</summary>
    public bool PrefetchAdjacent { get; set; } = true;

    public ViewSnapshot Snapshot(double velocityPxPerSecond = 0) => new(_layout, ScrollX, ScrollY, _viewportWidth, _viewportHeight, velocityPxPerSecond, Presenting && PrefetchAdjacent);

    private double MarginPx => Presenting ? 0 : MarginDip * _dpiScale;

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
        RefreshMap();
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

    // ---- view modes ------------------------------------------------------------------------------

    /// <summary>
    /// Changes how pages are arranged and turned (single, facing, facing with a cover; continuous or paged; the reading direction). The first page in view stays in view.
    /// Does nothing while presenting (the mode is then fixed; leave presentation first).
    /// </summary>
    public void SetMode(ViewMode mode)
    {
        if (mode == Mode || Presenting)
        {
            return;
        }

        Rearrange(() => Mode = mode);
    }

    /// <summary>The document's own binding side, from its viewer preferences. Only the two directions mean anything: anything else is automatic.</summary>
    public void SetDocumentDirection(ReadingDirection direction)
    {
        direction = direction is ReadingDirection.LeftToRight or ReadingDirection.RightToLeft ? direction : ReadingDirection.Auto;
        if (direction == _documentDirection)
        {
            return;
        }

        Rearrange(() => _documentDirection = direction);
    }

    /// <summary>
    /// The first page of the first spread that is really in view: a sliver of the spread above (the margin is deeper than the gap, so after a jump a few pixels of it show) does not count, or
    /// every change of mode would step one spread back.
    /// </summary>
    private int FirstSubstantialPage()
    {
        var (firstPage, lastPage) = _layout.VisiblePages(ScrollY, ScrollY + _viewportHeight);
        if (lastPage < firstPage)
        {
            return Math.Max(0, TopAnchor().Page);
        }

        var first = _layout.SpreadOf(firstPage);
        var last = _layout.SpreadOf(lastPage);
        var sliver = _layout.MarginPx + _layout.GapPx + 2;
        for (var s = first; s < last; s++)
        {
            var visible = Math.Min(ScrollY + _viewportHeight, _layout.SpreadTop(s) + _layout.SpreadHeight(s)) - Math.Max(ScrollY, _layout.SpreadTop(s));
            if (visible > sliver)
            {
                return _map.FirstPage(s);
            }
        }

        return _map.FirstPage(last);
    }

    // Changes what is in a spread and keeps the first visible page in view.
    private void Rearrange(Action change)
    {
        // Where the view is, by page: the page that was kept by the last change of mode, if nothing has moved the view since (facing and cover pair the pages differently, so the first page of a
        // spread is not a stable thing to hold on to when the mode changes back and forth); otherwise the first page really in view.
        var page = _layout.PageCount > 0 ? (_modePage >= 0 && ScrollX == _modeScrollX && ScrollY == _modeScrollY ? _modePage : FirstSubstantialPage()) : -1;
        change();
        RefreshMap();
        ApplyFit(page >= 0 ? page : null);
        Rebuild(null, 0, 0);
        if (page >= 0)
        {
            GoToPage(page);
            (_modePage, _modeScrollX, _modeScrollY) = (page, ScrollX, ScrollY);
        }
    }

    /// <summary>
    /// Presentation: one page at a time, fit to the window, no margin (docs/features/page-view-modes.md 3.6). The mode, zoom and place are kept for
    /// <see cref="ExitPresentation"/>.
    /// </summary>
    public void EnterPresentation()
    {
        if (Presenting || _layout.PageCount == 0)
        {
            return;
        }

        var page = Math.Max(0, CurrentPage);
        var (anchorPage, anchorFraction) = TopPosition;
        var fraction = anchorPage >= 0 && _layout.SpreadOf(anchorPage) == _layout.SpreadOf(page) ? anchorFraction : 0; // the offset is of the page shown, not of a sliver of the one above
        _backup = new PresentationBackup(Mode, Zoom, Fit, page, fraction);
        Presenting = true;
        Mode = new ViewMode(PageArrangement.OnePage, PageFlow.Paged, Mode.Direction);
        Fit = FitMode.Page;
        RefreshMap();
        ApplyFit(_backup.Page);
        Rebuild(null, 0, 0);
        GoToPage(_backup.Page);
    }

    /// <summary>Leaves presentation: the mode, zoom and place from before.</summary>
    public void ExitPresentation()
    {
        if (!Presenting || _backup is not { } back)
        {
            return;
        }

        var page = Math.Max(0, CurrentPage); // the page the talk ended on is the page to come back to
        Presenting = false;
        _backup = null;
        Mode = back.Mode;
        Zoom = back.Zoom;
        Fit = back.Fit;
        RefreshMap();
        ApplyFit(page);
        Rebuild(null, 0, 0);
        GoToPage(page, page == back.Page ? back.Fraction : 0);
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

    /// <summary>100%: 96 device-independent pixels to the inch (Ctrl+0).</summary>
    public void ActualSize(PointD? viewportAnchor = null) => ZoomTo(1.0, viewportAnchor);

    /// <summary>The zoom at which a point of the page is a 72nd of an inch on the monitor, given the monitor's physical density (Ctrl+Alt+0).</summary>
    public void PhysicalSize(double rawMonitorDpi, PointD? viewportAnchor = null) => ZoomTo(ZoomMath.ActualSizeZoom(rawMonitorDpi, _dpiScale), viewportAnchor);

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

    /// <summary>
    /// Scrolls so the page's spread is at the top of the viewport (centred when the whole spread fits). In the paged flow the spread is shown whole or from its top,
    /// and the fit modes are applied to it. With a <paramref name="fractionOfPage"/> the point of the page itself is brought to the top.
    /// </summary>
    public void GoToPage(int page, double fractionOfPage = 0)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        page = Math.Clamp(page, 0, _layout.PageCount - 1);
        if (Mode.IsPaged)
        {
            EnterSpread(_map.SpreadOf(page));
        }

        var spread = _layout.SpreadOf(page);
        var fraction = Math.Clamp(fractionOfPage, 0, 1);
        var top = _layout.SpreadTop(spread) + (fraction * _layout.SpreadHeight(spread));
        var spare = _viewportHeight - _layout.SpreadHeight(spread);
        var y = fraction == 0 && spare > 2 * _layout.MarginPx ? top - (spare / 2) : top - (fraction == 0 ? _layout.MarginPx : 0);
        ScrollTo(ScrollX, y);
    }

    /// <summary>
    /// Scrolls so a point of a page is at the top of the viewport (a bookmark or link with a position): <paramref name="pagePoint"/> is in page
    /// space, the page's own /Rotate applied, in points. With <paramref name="useX"/> the point is also brought to the left edge when the page
    /// is wider than the viewport. The view rotation and the zoom are accounted for.
    /// </summary>
    public void GoToPoint(int page, PointD pagePoint, bool useX)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        page = Math.Clamp(page, 0, _layout.PageCount - 1);
        var index = Math.Min(page, _sizes.Count - 1);
        if (index < 0)
        {
            GoToPage(page);
            return;
        }

        if (Mode.IsPaged)
        {
            EnterSpread(_map.SpreadOf(page));
        }

        var projection = PageProjection.For(Snapshot(), page);
        if (projection is null)
        {
            GoToPage(page);
            return;
        }

        var inPage = new PageTransform(projection.PageSize, ViewTurns, _layout.LayoutScale).PageToDevice(pagePoint);
        var y = _layout.Top(page) + Math.Clamp(inPage.Y, 0, _layout.Height(page)) - _layout.MarginPx;
        var x = ScrollX;
        if (useX && _layout.ContentWidth > _viewportWidth)
        {
            x = _layout.Left(page, _viewportWidth) + Math.Clamp(inPage.X, 0, _layout.Width(page)) - _layout.MarginPx;
        }

        ScrollTo(x, y);
    }

    /// <summary>
    /// Whether a box of a page (page space) needs the view to move to be seen: it is off the window, or on a spread that the paged flow is not showing. A page that is on
    /// the other half of the spread shown needs nothing.
    /// </summary>
    public bool NeedsReveal(int page, RectD pageRect)
    {
        if (_layout.PageCount == 0)
        {
            return false;
        }

        page = Math.Clamp(page, 0, _layout.PageCount - 1);
        if (Mode.IsPaged && _layout.SpreadOf(page) != CurrentSpread)
        {
            return true;
        }

        if (PageProjection.For(Snapshot(), page) is not { } projection)
        {
            return false;
        }

        var r = projection.PageToViewport(pageRect);
        return r.Y < 0 || r.Bottom > _viewportHeight || r.X < 0 || r.Right > _viewportWidth;
    }

    /// <summary>
    /// Scrolls just enough to bring a box of a page (page space) into view: not at all if it is already there, otherwise to the middle of the viewport. In the paged flow a box on
    /// another spread turns to that spread first (a search match, a selection moved by the keyboard). Works for either page of a spread.
    /// </summary>
    public void RevealPageRect(int page, RectD pageRect)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        page = Math.Clamp(page, 0, _layout.PageCount - 1);
        if (Mode.IsPaged && _layout.SpreadOf(page) != CurrentSpread)
        {
            GoToPage(page);
        }

        if (PageProjection.For(Snapshot(), page) is not { } projection)
        {
            return;
        }

        var r = projection.PageToViewport(pageRect);
        double dy = 0;
        double dx = 0;
        if (r.Y < 0 || r.Bottom > _viewportHeight)
        {
            dy = r.Y + (r.Height / 2) - (_viewportHeight / 2);
        }

        if (r.X < 0 || r.Right > _viewportWidth)
        {
            dx = r.X + (r.Width / 2) - (_viewportWidth / 2);
        }

        if (dx != 0 || dy != 0)
        {
            ScrollBy(dx, dy);
        }
    }

    /// <summary>The first page of the next spread (the next page, for single pages).</summary>
    public void NextPage() => GoToSpread(CurrentSpread + 1);

    public void PreviousPage() => GoToSpread(CurrentSpread - 1);

    public void FirstPage() => GoToPage(0);

    public void LastPage() => GoToPage(PageCount - 1);

    private void GoToSpread(int spread)
    {
        if (_layout.SpreadCount == 0)
        {
            return;
        }

        GoToPage(_map.FirstPage(Math.Clamp(spread, 0, _layout.SpreadCount - 1)));
    }

    /// <summary>
    /// Page Down: when the spread in view fits the window whole (the view then shows one at a time) the next spread, otherwise one screen further down it. In
    /// the paged flow the spread scrolls to its end first and the next one follows.
    /// </summary>
    public void PageDown() => PageStep(+1);

    /// <summary>Page Up: the previous spread when the spread in view fits the window whole, otherwise one screen up.</summary>
    public void PageUp() => PageStep(-1);

    private void PageStep(int direction)
    {
        var spread = CurrentSpread;
        if (spread < 0)
        {
            return;
        }

        if (Mode.IsPaged)
        {
            var (lo, hi) = PagedRange();
            if (direction > 0 ? ScrollY >= hi - 1 : ScrollY <= lo + 1)
            {
                Turn(direction);
            }
            else
            {
                ScrollScreen(direction);
            }

            return;
        }

        if (_layout.SpreadHeight(spread) + (2 * _layout.MarginPx) <= _viewportHeight + 2)
        {
            GoToSpread(spread + direction);
        }
        else
        {
            ScrollScreen(direction);
        }
    }

    /// <summary>One viewport height less a little overlap, for Page Down and Space when pages are taller than the window.</summary>
    public void ScrollScreen(int direction) => ScrollBy(0, direction * Math.Max(1, _viewportHeight * 0.9));

    /// <summary>
    /// The Down and Up arrows: scroll a step. In the paged flow a spread that fits the window is turned instead (nothing to scroll), the next one when going down.
    /// </summary>
    public void ArrowVertical(int direction, double stepPx)
    {
        if (Mode.IsPaged)
        {
            var (lo, hi) = PagedRange();
            if (hi - lo < 1)
            {
                Turn(direction);
                return;
            }
        }

        ScrollBy(0, direction * stepPx);
    }

    /// <summary>
    /// The Right and Left arrows. Continuous: the next or previous spread. Paged: pan sideways when the spread is wider than the window and there is more to see that way,
    /// otherwise turn.
    /// </summary>
    public void ArrowHorizontal(int direction, double stepPx)
    {
        if (!Mode.IsPaged)
        {
            GoToSpread(CurrentSpread + direction);
            return;
        }

        var maxX = Math.Max(0, _layout.ContentWidth - _viewportWidth);
        if (maxX > 1 && (direction > 0 ? ScrollX < maxX - 1 : ScrollX > 1))
        {
            ScrollBy(direction * stepPx, 0);
        }
        else
        {
            Turn(direction);
        }
    }

    /// <summary>Whether the arrow keys should be read the other way: a right-to-left binding turned one spread at a time (docs/features/page-view-modes.md 3.7). Presentation is not mirrored.</summary>
    public bool ArrowsAreMirrored => Mode.IsPaged && !Presenting && RightToLeft && !_map.IsSingle;

    /// <summary>Shows the next (<paramref name="direction"/> above 0) or the previous spread in the paged flow; goes to its page in the continuous flow. False at either end.</summary>
    public bool Turn(int direction)
    {
        var from = CurrentSpread;
        var to = from + (direction > 0 ? 1 : -1);
        if (from < 0 || to < 0 || to >= _layout.SpreadCount)
        {
            return false;
        }

        if (!Mode.IsPaged)
        {
            GoToSpread(to);
            return true;
        }

        EnterSpread(to);
        var (lo, hi) = PagedRange();
        ScrollTo(direction > 0 ? 0 : ScrollX, direction > 0 ? lo : hi); // forwards from the top, backwards from the bottom of the spread
        return true;
    }

    /// <summary>
    /// One notch of the mouse wheel. Continuous: scrolls. Paged: scrolls inside the spread, and one notch further, once the end is reached, turns the page (so a
    /// stray notch never does), at most once every <see cref="WheelTurnDebounceMs"/>. Returns true when it turned.
    /// </summary>
    /// <param name="dyPx">Pixels down (positive) or up.</param>
    /// <param name="tickMs">A monotonic clock in milliseconds.</param>
    public bool WheelNotch(double dyPx, long tickMs)
    {
        if (!Mode.IsPaged || _layout.SpreadCount == 0 || dyPx == 0)
        {
            ScrollBy(0, dyPx);
            return false;
        }

        var (lo, hi) = PagedRange();
        var atEnd = dyPx > 0 ? ScrollY >= hi - 1 : ScrollY <= lo + 1;
        if (!atEnd)
        {
            ScrollBy(0, dyPx);
            return false;
        }

        if (tickMs - _lastTurnTick < WheelTurnDebounceMs)
        {
            return false;
        }

        if (Turn(dyPx > 0 ? 1 : -1))
        {
            _lastTurnTick = tickMs;
            return true;
        }

        return false;
    }

    /// <summary>The whole current spread is inside the window, so there is nothing to scroll or pan (a swipe then turns the page).</summary>
    public bool SpreadFitsWindow
    {
        get
        {
            if (_layout.SpreadCount == 0)
            {
                return true;
            }

            var spread = CurrentSpread;
            return _layout.SpreadHeight(spread) + (2 * _layout.MarginPx) <= _viewportHeight + 2 && _layout.ContentWidth <= _viewportWidth + 2;
        }
    }

    /// <summary>
    /// Restores a saved view (the zoom is applied as a number; fit mode as saved). The view mode comes with it; a restored presentation is never saved, so
    /// the mode here is never presentation.
    /// </summary>
    public void Restore(int page, double fraction, double zoom, FitMode fit, int viewTurns, ViewMode mode = default)
    {
        Presenting = false;
        _backup = null;
        Mode = mode;
        ViewTurns = QuarterTurns.Normalize(viewTurns);
        Zoom = ZoomMath.Clamp(zoom);
        Fit = fit;
        RefreshMap();
        ApplyFit(page);
        Rebuild(null, 0, 0);
        GoToPage(page, fraction);
    }

    // ---- internals -------------------------------------------------------------------------------

    private void RefreshMap() => _map = new SpreadMap(_sizes.Count, Mode.Arrangement, RightToLeft);

    /// <summary>The scroll range, in document y, that the paged flow allows inside the spread being shown: one value when the spread fits the window (it is centred).</summary>
    private (double Lo, double Hi) PagedRange()
    {
        var spread = Math.Clamp(_pagedSpread, 0, Math.Max(0, _layout.SpreadCount - 1));
        var top = _layout.SpreadTop(spread);
        var height = _layout.SpreadHeight(spread);
        var margin = _layout.MarginPx;
        if (height + (2 * margin) <= _viewportHeight)
        {
            var centred = top - ((_viewportHeight - height) / 2);
            return (centred, centred);
        }

        return (top - margin, top + height + margin - _viewportHeight);
    }

    /// <summary>Makes a spread the one the paged flow shows, and applies the fit modes to it (the next spread may be another size).</summary>
    private void EnterSpread(int spread)
    {
        _pagedSpread = Math.Clamp(spread, 0, Math.Max(0, _map.SpreadCount - 1));
        if (Fit == FitMode.None)
        {
            return;
        }

        var before = Zoom;
        ApplyFit(_map.FirstPage(_pagedSpread));
        if (Zoom != before)
        {
            Rebuild(null, 0, 0);
        }
    }

    /// <summary>The point at the top of the viewport, relative to the first page that shows there (the fraction is negative while only the gap above it is in view).</summary>
    private LayoutAnchor TopAnchor()
    {
        var (first, last) = _layout.VisiblePages(ScrollY, ScrollY + _viewportHeight);
        if (last < first)
        {
            return _layout.AnchorAt(ScrollX, ScrollY, _viewportWidth);
        }

        var spread = _layout.SpreadOf(first);
        var x = (ScrollX - _layout.SpreadLeft(spread, _viewportWidth)) / _layout.SpreadWidth(spread);
        return new LayoutAnchor(first, x, (ScrollY - _layout.SpreadTop(spread)) / _layout.SpreadHeight(spread));
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

    /// <summary>The page, rotated by the view, in points.</summary>
    private SizeD Rotated(int page) => QuarterTurns.Rotate(_sizes[page], ViewTurns);

    /// <summary>The size of a spread in points (a lone page of a facing arrangement counts twice: the other half is blank), and whether it has a gutter.</summary>
    private (SizeD Size, bool HasGutter) SpreadSizePt(int spread)
    {
        var (left, right) = _map.Slots(spread);
        if (_map.IsSingle)
        {
            return (Rotated(left), false);
        }

        var l = left >= 0 ? Rotated(left) : Rotated(right);
        var r = right >= 0 ? Rotated(right) : Rotated(left);
        var height = Math.Max(left >= 0 ? Rotated(left).Height : 0, right >= 0 ? Rotated(right).Height : 0);
        return (new SizeD(l.Width + r.Width, height), true);
    }

    /// <summary>
    /// Recomputes Zoom for the fit modes from the spread at the top of the viewport (or the spread of the given page): its width, or the whole of it.
    /// </summary>
    private void ApplyFit(int? ofPage = null)
    {
        if (Fit == FitMode.None || _sizes.Count == 0)
        {
            return;
        }

        var page = ofPage ?? (_layout.PageCount > 0 ? Math.Clamp(CurrentPage, 0, _sizes.Count - 1) : 0);
        page = Math.Clamp(page, 0, _sizes.Count - 1);
        var margin = MarginPx;
        var (size, hasGutter) = SpreadSizePt(_map.SpreadOf(page));
        if (!hasGutter)
        {
            Zoom = Fit == FitMode.Width
                ? ZoomMath.FitWidth(_viewportWidth, size.Width, _dpiScale, margin)
                : ZoomMath.FitPage(_viewportWidth, _viewportHeight, size, _dpiScale, margin);
            return;
        }

        var gutter = GutterDip * _dpiScale;
        Zoom = Fit == FitMode.Width
            ? ZoomMath.FitSpreadWidth(_viewportWidth, size.Width, gutter, _dpiScale, margin)
            : ZoomMath.FitSpreadPage(_viewportWidth, _viewportHeight, size, gutter, _dpiScale, margin);
    }

    /// <summary>Builds the layout for the current zoom and puts the anchor back at the given viewport point.</summary>
    private void Rebuild(LayoutAnchor? anchor, double viewportX, double viewportY)
    {
        var scale = ZoomMath.PixelsPerPoint(Zoom, _dpiScale);
        var gap = GapDip * _dpiScale;
        if (Mode.IsPaged)
        {
            gap = Math.Max(gap, _viewportHeight * 0.75); // so much between spreads that the next is never on screen beside the one shown
        }

        var gutter = _map.IsSingle ? 0 : GutterDip * _dpiScale;
        _layout = new DocumentLayout(_sizes, ViewTurns, scale, gap, MarginPx, _map, gutter);
        if (anchor is { } a && _layout.PageCount > 0)
        {
            if (Mode.IsPaged)
            {
                _pagedSpread = _layout.SpreadOf(Math.Clamp(a.Page, 0, _layout.PageCount - 1));
            }

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

    private double ClampY(double y)
    {
        if (Mode.IsPaged && _layout.SpreadCount > 0)
        {
            var (lo, hi) = PagedRange();
            return Math.Clamp(y, lo, Math.Max(lo, hi));
        }

        return Math.Clamp(y, 0, Math.Max(0, _layout.ContentHeight - _viewportHeight));
    }
}
