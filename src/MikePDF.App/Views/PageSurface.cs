using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using MikePDF.App.Services;
using MikePDF.Core.Colour;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Reading;
using MikePDF.Core.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MikePDF.App.Views;

/// <summary>
/// The MikePDF-owned control that hosts the pages (S7, ADR-0006 decision 1). The drawing element lives inside it and is hidden from
/// accessibility; this control owns focus, input and the automation peer, which exposes the text of the pages in view through the UI
/// Automation Text pattern. Pages are drawn by an <see cref="Core.Tiles.ITilePresenter"/> (Win2D now); the selection, the search matches and the
/// current match are drawn over them as shapes (ADR-0006: overlays live on separate layers above the tiles).
/// <para>
/// Input: wheel scrolls, Shift+wheel or a horizontal wheel scrolls sideways, Ctrl+wheel zooms about the pointer, touch drags and pinches (with
/// inertia). The mouse and the pen select text: drag, double click for a word, triple click for a line, and a click on a link follows it.
/// Page Up, Page Down, Home, End, the arrow keys and Space navigate, and Ctrl+A and Ctrl+C select and copy, as commands the window's dispatcher runs;
/// Shift with the arrow keys (and Ctrl for words) moves the selection and Esc clears it, here.
/// </para>
/// </summary>
public sealed partial class PageSurface : Control
{
    /// <summary>Scroll per wheel notch, in device-independent pixels.</summary>
    private const double WheelStepDip = 100;
    private const double ZoomPerNotch = 1.1;

    /// <summary>The pointer must move this far (device-independent pixels) between press and release for a click to become a drag.</summary>
    private const double DragThresholdDip = 4;
    private const int MaxOverlayShapes = 5000;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly InputCursor _arrow = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor _beam = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private Grid? _host;
    private ShapePath? _selectionShapes;
    private ShapePath? _findShapes;
    private ShapePath? _currentMatchShapes;
    private DocumentSession? _session;
    private bool _overlayPending;
    private bool _pressed;
    private bool _dragged;
    private PointD _pressPoint;
    private PageLink? _pressedLink;
    private int _pressedLinkPage = -1;
    private int _clickCount;
    private long _lastClickTick;
    private PointD _lastClickPoint;
    private PointD _lastPointer;
    private DispatcherQueueTimer? _autoScroll;
    private PageColourMode _pageColour = PageColourMode.Normal;
    private TextModel _accessibleModel = TextModel.Empty;
    private string _accessiblePages = string.Empty;
    private bool _presentation;
    private bool _pointerHidden;
    private DispatcherQueueTimer? _pointerIdle;

    /// <summary>The pointer is hidden after it has rested this long in presentation mode (design 03: 2 s; the feature spec says 3 s, the longer is kept).</summary>
    private static readonly TimeSpan PointerIdle = TimeSpan.FromSeconds(3);

    public PageSurface()
    {
        DefaultStyleKey = typeof(PageSurface);
        Presenter = new Win2DTilePresenter();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.TranslateInertia | ManipulationModes.Scale;
        SizeChanged += (_, _) => PushViewport();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
            ShowPointer();
        };
        ActualThemeChanged += (_, _) => ApplyColors();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, _) => EndPress();
        PointerCaptureLost += (_, _) => EndPress();
        PointerExited += (_, _) => ProtectedCursor = _arrow;
        PointerWheelChanged += OnPointerWheelChanged;
        ManipulationDelta += OnManipulationDelta;
        KeyDown += OnKeyDown;
        Tapped += OnTapped;
        ManipulationCompleted += OnManipulationCompleted;
        ProtectedCursor = _arrow;
    }

    internal Win2DTilePresenter Presenter { get; }

    /// <summary>A link was clicked.</summary>
    internal event Action<int, PageLink>? LinkActivated;

    /// <summary>A copy finished: true when the text was cut because the selection was very large.</summary>
    internal event Action<bool>? Copied;

    /// <summary>The person tried to copy, and the document's owner has not allowed it.</summary>
    internal event Action? CopyBlocked;

    /// <summary>
    /// How the pages are coloured. A high-contrast theme overrides it: the system's colours win, so the setting is kept but not applied while
    /// high contrast is on.
    /// </summary>
    internal PageColourMode PageColour
    {
        get => _pageColour;
        set
        {
            _pageColour = value;
            ApplyColors();
        }
    }

    /// <summary>
    /// Presentation mode is on (docs/features/page-view-modes.md 3.6): a black surround, no selecting, a click turns the page, and the pointer hides itself when it rests. The window
    /// turns it on and off with the session's own presentation state.
    /// </summary>
    internal bool Presentation
    {
        get => _presentation;
        set
        {
            if (_presentation == value)
            {
                return;
            }

            _presentation = value;
            EndPress();
            if (value)
            {
                _session?.Selection?.Clear();
                RestartPointerIdle();
            }
            else
            {
                _pointerIdle?.Stop();
                ShowPointer();
            }

            ApplyColors();
        }
    }

    /// <summary>Hides the pointer now (a page was turned from the keyboard); it comes back when it moves.</summary>
    internal void HidePointer()
    {
        if (_presentation && !_pointerHidden)
        {
            _pointerHidden = true;
            _ = ShowCursor(false);
        }
    }

    private void ShowPointer()
    {
        if (_pointerHidden)
        {
            _pointerHidden = false;
            _ = ShowCursor(true);
        }
    }

    private void RestartPointerIdle()
    {
        ShowPointer();
        _pointerIdle ??= DispatcherQueue.CreateTimer();
        _pointerIdle.Interval = PointerIdle;
        _pointerIdle.IsRepeating = false;
        _pointerIdle.Tick -= OnPointerIdle;
        _pointerIdle.Tick += OnPointerIdle;
        _pointerIdle.Stop();
        _pointerIdle.Start();
    }

    private void OnPointerIdle(DispatcherQueueTimer sender, object args)
    {
        if (_presentation)
        {
            HidePointer();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool show);

    internal DocumentSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                PushViewport();
                return;
            }

            if (_session is { } old)
            {
                old.ViewChanged -= OnSessionViewChanged;
                old.OverlaysChanged -= OnSessionOverlaysChanged;
                old.SelectionChanged -= OnSessionSelectionChanged;
            }

            EndPress();
            _session = value;
            if (value is not null)
            {
                value.ViewChanged += OnSessionViewChanged;
                value.OverlaysChanged += OnSessionOverlaysChanged;
                value.SelectionChanged += OnSessionSelectionChanged;
            }

            ScheduleOverlays();
            RefreshAccessibleText();
            value?.SetDarkPages(!_accessibility.HighContrast && _pageColour == PageColourMode.Inverted); // a tab shown later takes on the page colour now chosen
            PushViewport();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild("PART_Host") as Grid;
        _selectionShapes = GetTemplateChild("PART_SelectionShapes") as ShapePath;
        _findShapes = GetTemplateChild("PART_FindShapes") as ShapePath;
        _currentMatchShapes = GetTemplateChild("PART_CurrentMatchShapes") as ShapePath;
        if (_host is not null && !_host.Children.Contains(Presenter.Element))
        {
            _host.Children.Insert(0, Presenter.Element); // under the overlay
        }

        ApplyColors();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PageSurfaceAutomationPeer(this);

    /// <summary>Gives the keyboard focus to the pages.</summary>
    public bool FocusPages() => Focus(FocusState.Programmatic);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // System colours change with the theme and with high contrast; AccessibilitySettings.HighContrastChanged is not available to desktop apps.
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        if (XamlRoot is { } root)
        {
            root.Changed += (_, _) => PushViewport();
        }

        ApplyColors();
        PushViewport();
    }

    private void OnColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyColors);

    private void ApplyColors()
    {
        Color background;
        Color paper = Color.FromArgb(255, 255, 255, 255);
        Color border;
        if (_accessibility.HighContrast)
        {
            // High contrast: the system colours, and a visible page edge.
            background = _uiSettings.GetColorValue(UIColorType.Background);
            border = _uiSettings.GetColorValue(UIColorType.Foreground);
            paper = background;
        }
        else if (_presentation)
        {
            // Presentation: the page on black, with nothing round it (the surround is the room's dark, not the app's grey).
            background = Color.FromArgb(255, 0, 0, 0);
            border = Color.FromArgb(0, 0, 0, 0);
        }
        else if (ActualTheme == ElementTheme.Dark)
        {
            background = Color.FromArgb(255, 0x2B, 0x2B, 0x2B);
            border = Color.FromArgb(90, 255, 255, 255);
        }
        else
        {
            background = Color.FromArgb(255, 0xE9, 0xE9, 0xE9);
            border = Color.FromArgb(40, 0, 0, 0);
        }

        Presenter.SetColors(background, paper, border);
        // Dark pages are drawn by the engine (photographs stay as they are), so the tiles are already dark and the GPU must not invert them again. Sepia stays on the GPU.
        var engineDark = !_accessibility.HighContrast && _pageColour == PageColourMode.Inverted;
        Presenter.ColourMode = _accessibility.HighContrast || engineDark ? PageColourMode.Normal : _pageColour;
        _session?.SetDarkPages(engineDark);
        _session?.RequestFrame();
    }

    private void PushViewport()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        Presenter.RasterScale = scale;
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _session?.SetViewport(ActualWidth * scale, ActualHeight * scale, scale);
        }
    }

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1.0;

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private PointD ToPixels(Point dip) => new(dip.X * RasterScale, dip.Y * RasterScale);

    // ---- the session's news --------------------------------------------------------------------------------------------------------

    private void OnSessionViewChanged()
    {
        ScheduleOverlays();
        RefreshAccessibleText();
    }

    private void OnSessionOverlaysChanged()
    {
        ScheduleOverlays();
        RefreshAccessibleText();
    }

    private void OnSessionSelectionChanged()
    {
        ScheduleOverlays();
        (FrameworkElementAutomationPeer.FromElement(this) as PageSurfaceAutomationPeer)?.RaiseSelectionChanged();
    }

    // ---- overlays ----------------------------------------------------------------------------------------------------------------------

    private void ScheduleOverlays()
    {
        if (_overlayPending)
        {
            return;
        }

        _overlayPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, UpdateOverlays);
    }

    private void UpdateOverlays()
    {
        _overlayPending = false;
        if (_selectionShapes is null || _findShapes is null || _currentMatchShapes is null)
        {
            return;
        }

        if (_session is not { IsOpen: true } session)
        {
            _selectionShapes.Data = null;
            _findShapes.Data = null;
            _currentMatchShapes.Data = null;
            return;
        }

        var view = session.Controller.Snapshot();
        var scale = RasterScale;
        var selection = new GeometryGroup();
        var find = new GeometryGroup();
        var current = new GeometryGroup();
        var budget = MaxOverlayShapes;
        foreach (var page in session.VisiblePages())
        {
            if (PageProjection.For(view, page) is not { } projection)
            {
                continue;
            }

            budget -= Add(selection, session.SelectionRects(page), projection, scale, budget);
            budget -= Add(find, session.FindRects(page), projection, scale, budget);
            Add(current, session.CurrentMatchRects(page), projection, scale, MaxOverlayShapes);
        }

        _selectionShapes.Data = selection.Children.Count == 0 ? null : selection;
        _findShapes.Data = find.Children.Count == 0 ? null : find;
        _currentMatchShapes.Data = current.Children.Count == 0 ? null : current;
    }

    private static int Add(GeometryGroup group, IReadOnlyList<RectD> rects, PageProjection projection, double scale, int budget)
    {
        var added = 0;
        foreach (var rect in rects)
        {
            if (added >= budget)
            {
                break;
            }

            var r = projection.PageToViewport(rect);
            group.Children.Add(new RectangleGeometry { Rect = new Rect(r.X / scale, r.Y / scale, Math.Max(1, r.Width / scale), Math.Max(1, r.Height / scale)) });
            added++;
        }

        return added;
    }

    // ---- wheel and touch -----------------------------------------------------------------------------------------------------------

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var notches = point.Properties.MouseWheelDelta / 120.0;
        var scale = RasterScale;
        if (_session.Presenting)
        {
            // A talk: a notch down is the next page, up the one before (once per debounce, so one flick is one page). Nothing zooms.
            if (notches != 0)
            {
                _session.WheelNotch(-Math.Sign(notches) * WheelStepDip * scale, Environment.TickCount64);
            }
        }
        else if (IsDown(VirtualKey.Control))
        {
            _session.ZoomBy(Math.Pow(ZoomPerNotch, notches), new PointD(point.Position.X * scale, point.Position.Y * scale));
        }
        else if (point.Properties.IsHorizontalMouseWheel)
        {
            _session.ScrollBy(notches * WheelStepDip * scale, 0);
        }
        else if (IsDown(VirtualKey.Shift))
        {
            _session.ScrollBy(-notches * WheelStepDip * scale, 0);
        }
        else
        {
            _session.WheelNotch(-notches * WheelStepDip * scale, Environment.TickCount64);
        }

        e.Handled = true;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_session is null || _session.Presenting)
        {
            return; // in a talk a drag or a pinch moves nothing; a swipe turns the page when it ends
        }

        var scale = RasterScale;
        if (Math.Abs(e.Delta.Scale - 1.0) > 0.001)
        {
            _session.ZoomBy(e.Delta.Scale, new PointD(e.Position.X * scale, e.Position.Y * scale));
        }

        _session.ScrollBy(-e.Delta.Translation.X * scale, -e.Delta.Translation.Y * scale);
        e.Handled = true;
    }

    /// <summary>A swipe is this far (device-independent pixels) before it turns a page.</summary>
    private const double SwipeDip = 80;

    // A swipe across a page turns it: in presentation always, in the paged flow when the spread fits the window (otherwise the same movement is a pan).
    private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (_session is not { IsOpen: true } session || !(session.Presenting || (session.Mode.IsPaged && session.Controller.SpreadFitsWindow)))
        {
            return;
        }

        var dx = e.Cumulative.Translation.X;
        var dy = e.Cumulative.Translation.Y;
        if (Math.Abs(dx) >= SwipeDip && Math.Abs(dx) > Math.Abs(dy) && Math.Abs(e.Cumulative.Scale - 1.0) < 0.05)
        {
            var forward = dx < 0 != (session.Controller.RightToLeft && !session.Presenting);
            session.Turn(forward ? +1 : -1);
            e.Handled = true;
        }
    }

    // A tap by finger or pen in presentation: the right two-thirds of the page well go forward, the left third back.
    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_session is not { IsOpen: true } session || !session.Presenting || e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        session.Turn(x < ActualWidth / 3 ? -1 : +1);
        e.Handled = true;
    }

    // ---- the mouse: selecting and following links --------------------------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        if (_session is not { IsOpen: true } session || e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (session.Presenting)
        {
            OnPresentationPressed(session, e, point);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pixels = ToPixels(point.Position);
        var hit = session.HitTest(pixels);
        if (hit is null)
        {
            return;
        }

        session.PrefetchVisible();
        CapturePointer(e.Pointer);
        _pressed = true;
        _dragged = false;
        _pressPoint = pixels;
        _lastPointer = pixels;
        _pressedLink = hit.Value.Link;
        _pressedLinkPage = hit.Value.Page;

        var now = Environment.TickCount64;
        var near = Math.Abs(pixels.X - _lastClickPoint.X) < 8 * RasterScale && Math.Abs(pixels.Y - _lastClickPoint.Y) < 8 * RasterScale;
        _clickCount = near && now - _lastClickTick <= GetDoubleClickTime() ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickTick = now;
        _lastClickPoint = pixels;

        var selection = session.Selection;
        if (selection is null || hit.Value.Atom < 0)
        {
            selection?.Clear();
            e.Handled = true;
            return;
        }

        var position = new TextPosition(hit.Value.Page, hit.Value.CaretAtom);
        if (_clickCount == 1)
        {
            if (IsDown(VirtualKey.Shift) && selection.HasSelection)
            {
                selection.DragTo(position);
            }
            else
            {
                selection.Begin(position);
            }
        }
        else
        {
            selection.SelectUnitAt(new TextPosition(hit.Value.Page, hit.Value.Atom), _clickCount == 2 ? TextUnitKind.Word : TextUnitKind.Line);
        }

        e.Handled = true;
    }

    // Presentation: the left button follows a link it pressed on (and lets go), anything else it presses turns the page forward when released; the right button goes back at once.
    private void OnPresentationPressed(DocumentSession session, PointerRoutedEventArgs e, PointerPoint point)
    {
        if (point.Properties.IsRightButtonPressed)
        {
            session.Turn(-1);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hit = session.HitTest(ToPixels(point.Position));
        CapturePointer(e.Pointer);
        _pressed = true;
        _dragged = false;
        _pressedLink = hit?.Link;
        _pressedLinkPage = hit?.Page ?? -1;
        e.Handled = true;
    }

    private void OnPresentationReleased(DocumentSession session, PointerRoutedEventArgs e)
    {
        var wasPressed = _pressed;
        var link = _pressedLink;
        var page = _pressedLinkPage;
        var releasedAt = ToPixels(e.GetCurrentPoint(this).Position);
        EndPress();
        ReleasePointerCaptures();
        if (!wasPressed)
        {
            return;
        }

        if (link is not null && session.HitTest(releasedAt) is { Link: { } under } && under.Index == link.Index)
        {
            LinkActivated?.Invoke(page, link);
        }
        else
        {
            session.Turn(+1);
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        if (session.Presenting)
        {
            RestartPointerIdle(); // the pointer is back; it hides again after it has rested
            return;
        }

        var point = e.GetCurrentPoint(this);
        var pixels = ToPixels(point.Position);
        _lastPointer = pixels;
        if (!_pressed)
        {
            UpdateCursor(session, pixels);
            return;
        }

        if (!_dragged && (Math.Abs(pixels.X - _pressPoint.X) > DragThresholdDip * RasterScale || Math.Abs(pixels.Y - _pressPoint.Y) > DragThresholdDip * RasterScale))
        {
            _dragged = true;
            StartAutoScroll();
        }

        if (_dragged && _clickCount == 1)
        {
            DragSelectionTo(session, pixels);
        }
    }

    private static void DragSelectionTo(DocumentSession session, PointD pixels)
    {
        if (session.HitTest(pixels) is { } hit && hit.Atom >= 0)
        {
            session.Selection?.DragTo(new TextPosition(hit.Page, hit.CaretAtom));
        }
        else if (session.HitTest(pixels) is { } unloaded)
        {
            session.PrefetchVisible(); // the page under the pointer has no text yet: the next move finds it
            _ = unloaded;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_session is { IsOpen: true, Presenting: true } presenting)
        {
            OnPresentationReleased(presenting, e);
            return;
        }

        var wasClick = _pressed && !_dragged;
        var link = _pressedLink;
        var page = _pressedLinkPage;
        var session = _session;
        var releasedAt = ToPixels(e.GetCurrentPoint(this).Position);
        EndPress();
        ReleasePointerCaptures();
        if (wasClick && link is not null && _clickCount == 1 && session is { IsOpen: true }
            && session.HitTest(releasedAt) is { Link: { } under } && under.Index == link.Index)
        {
            LinkActivated?.Invoke(page, link);
        }
    }

    private void EndPress()
    {
        _pressed = false;
        _dragged = false;
        _pressedLink = null;
        _autoScroll?.Stop();
    }

    private void UpdateCursor(DocumentSession session, PointD pixels)
    {
        var hit = session.HitTest(pixels);
        if (hit is null)
        {
            ProtectedCursor = _arrow;
            return;
        }

        if (hit.Value.Link is not null)
        {
            ProtectedCursor = _hand;
            return;
        }

        // The I-beam over text: the pointer is inside a box of a character of the page under it.
        ProtectedCursor = OverText(session, hit.Value) ? _beam : _arrow;
    }

    private static bool OverText(DocumentSession session, TextHit hit)
    {
        if (hit.Atom < 0 || session.Store?.GetLoaded(hit.Page) is not { } model || hit.Atom >= model.Length)
        {
            return false;
        }

        return model.Atoms[hit.Atom].Rect is { } rect && rect.Inflate(2).Contains(hit.PagePoint);
    }

    // Dragging above or below the pages scrolls them, faster the further the pointer is.
    private void StartAutoScroll()
    {
        _autoScroll ??= DispatcherQueue.CreateTimer();
        _autoScroll.Interval = TimeSpan.FromMilliseconds(30);
        _autoScroll.IsRepeating = true;
        _autoScroll.Tick -= OnAutoScroll;
        _autoScroll.Tick += OnAutoScroll;
        _autoScroll.Start();
    }

    private void OnAutoScroll(DispatcherQueueTimer sender, object args)
    {
        if (!_pressed || !_dragged || _session is not { IsOpen: true } session)
        {
            sender.Stop();
            return;
        }

        var height = ActualHeight * RasterScale;
        var distance = _lastPointer.Y < 0 ? _lastPointer.Y : _lastPointer.Y > height ? _lastPointer.Y - height : 0;
        if (distance != 0)
        {
            session.ScrollBy(0, Math.Clamp(distance * 0.3, -60 * RasterScale, 60 * RasterScale));
            if (_clickCount == 1)
            {
                DragSelectionTo(session, new PointD(_lastPointer.X, Math.Clamp(_lastPointer.Y, 0, height)));
            }
        }
    }

    // ---- the keyboard ---------------------------------------------------------------------------------------------------------------

    // The keys of the page (Page Up and Down, Home, End, the arrows, Space, Ctrl+A, Ctrl+C) are commands, run by the window's dispatcher from the registry, so that remapping them
    // works and there is one list of them. What is left here is what is not a command: Esc clears the selection, and Shift with the arrows (and Ctrl for words) moves it.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null || _session.Presenting || IsDown(VirtualKey.Menu))
        {
            return; // Alt+Left and Alt+Right are Back and Forward (commands), not Left and Right; a talk selects nothing
        }

        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (e.Key == VirtualKey.Escape && _session.Selection is { HasSelection: true } active)
        {
            active.Clear();
            e.Handled = true;
            return;
        }

        if (shift && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var kind = e.Key is VirtualKey.Up or VirtualKey.Down ? ExtendKind.Line : ctrl ? ExtendKind.Word : ExtendKind.Character;
            var direction = e.Key is VirtualKey.Right or VirtualKey.Down ? +1 : -1;
            _ = _session.ExtendSelectionAsync(kind, direction);
            e.Handled = true;
        }
    }

    /// <summary>Selects all the text of the document. The pages in view are read first so there is something to see.</summary>
    internal void SelectAll()
    {
        _session?.PrefetchVisible();
        _session?.Selection?.SelectAll();
    }

    /// <summary>Copies the selection to the clipboard.</summary>
    internal async Task CopyAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (!session.CanCopy)
        {
            // The owner's restriction (P0: owner-restricted permissions are respected). Only copying is stopped: reading and screen readers are not.
            CopyBlocked?.Invoke();
            return;
        }

        try
        {
            var (text, truncated) = await session.CopySelectionAsync();
            if (text.Length == 0)
            {
                return;
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Copied?.Invoke(truncated);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or OperationCanceledException or UnauthorizedAccessException)
        {
            // The clipboard is held by another program, or the document was closed meanwhile: the copy is simply not made.
        }
    }

    // ---- accessibility support (the peer reads these) ----------------------------------------------------------------------------

    /// <summary>The text of the pages in view, as one run of atoms in reading order. Replaced when the pages in view or their text change.</summary>
    internal TextModel AccessibleModel => _accessibleModel;

    private void RefreshAccessibleText()
    {
        TextModel combined;
        string signature;
        if (_session is { IsOpen: true, Store: { } store } session)
        {
            var models = session.VisiblePages().Select(page => (Page: page, Model: store.GetLoaded(page))).Where(p => p.Model is { Length: > 0 }).ToList();
            signature = string.Join(',', models.Select(p => p.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Model)));
            if (signature == _accessiblePages)
            {
                return;
            }

            combined = TextModel.Combine([.. models.Select(p => p.Model!)]);
        }
        else
        {
            combined = TextModel.Empty;
            signature = string.Empty;
            if (_accessiblePages.Length == 0)
            {
                return;
            }
        }

        _accessiblePages = signature;
        _accessibleModel = combined;
        (FrameworkElementAutomationPeer.FromElement(this) as PageSurfaceAutomationPeer)?.RaiseTextChanged();
    }

    /// <summary>The selection as atoms of <see cref="AccessibleModel"/>.</summary>
    internal (int Start, int End) AccessibleSelection() =>
        _session?.Selection is { HasSelection: true } selection ? AccessibleSelectionMap.ToAtoms(_accessibleModel, selection.Selection) : (0, 0);

    internal void SelectAccessibleRange(int start, int end)
    {
        if (_session?.Selection is not { } selection)
        {
            return;
        }

        var range = AccessibleSelectionMap.ToSelection(_accessibleModel, start, end);
        if (range.IsEmpty)
        {
            selection.Begin(range.Start);
        }
        else
        {
            selection.Select(range.Start, range.End);
        }
    }

    /// <summary>The boxes of atoms <c>[start, end)</c> in the coordinates UI Automation wants: device-independent pixels from the top left of the window's content.</summary>
    internal IReadOnlyList<Rect> AccessibleBounds(int start, int end)
    {
        if (_session is not { IsOpen: true } session || XamlRoot is null)
        {
            return [];
        }

        var view = session.Controller.Snapshot();
        var scale = RasterScale;
        var toRoot = TransformToVisual(null);
        var visible = new Rect(0, 0, ActualWidth, ActualHeight);
        var rects = new List<Rect>();
        foreach (var (page, rect) in _accessibleModel.LineRects(start, end))
        {
            if (PageProjection.For(view, page) is not { } projection)
            {
                continue;
            }

            // Only what can be seen: text scrolled out of view has no rectangle (UI Automation: an empty array), so nobody clicks outside the window.
            var v = projection.PageToViewport(rect);
            var local = new Rect(v.X / scale, v.Y / scale, v.Width / scale, v.Height / scale);
            local.Intersect(visible);
            if (!local.IsEmpty && local.Width > 0 && local.Height > 0)
            {
                rects.Add(toRoot.TransformBounds(local));
            }
        }

        return rects;
    }

    /// <summary>The atoms of <see cref="AccessibleModel"/> that are on screen: from the first atom with a visible box to the last.</summary>
    internal (int Start, int End) AccessibleVisibleAtoms()
    {
        if (_session is not { IsOpen: true } session)
        {
            return (0, 0);
        }

        var view = session.Controller.Snapshot();
        var viewport = new RectD(0, 0, view.ViewportWidth, view.ViewportHeight);
        var first = -1;
        var last = -1;
        var atoms = _accessibleModel.Atoms;
        var projections = new Dictionary<int, PageProjection?>();
        for (var i = 0; i < atoms.Count; i++)
        {
            if (atoms[i].Rect is not { } rect)
            {
                continue;
            }

            if (!projections.TryGetValue(atoms[i].Page, out var projection))
            {
                projection = PageProjection.For(view, atoms[i].Page);
                projections[atoms[i].Page] = projection;
            }

            if (projection is not null && projection.PageToViewport(rect).Intersects(viewport))
            {
                first = first < 0 ? i : first;
                last = i;
            }
        }

        return first < 0 ? (0, 0) : (first, last + 1);
    }

    /// <summary>The atom of <see cref="AccessibleModel"/> under a point given as UI Automation gives it (device-independent pixels from the window's content); -1 if none.</summary>
    internal int AccessibleAtomAt(Point root)
    {
        if (_session is not { IsOpen: true } session || TransformToVisual(null).Inverse is not { } toLocal)
        {
            return -1;
        }

        var local = toLocal.TransformPoint(root);
        if (session.HitTest(ToPixels(local)) is not { Atom: >= 0 } hit)
        {
            return -1;
        }

        var (start, end) = _accessibleModel.PageRange(hit.Page);
        return end > start ? Math.Min(start + hit.Atom, end - 1) : -1;
    }

    /// <summary>Scrolls atoms <c>[start, end)</c> into view.</summary>
    internal void RevealAccessibleRange(int start, int end)
    {
        if (_session is null)
        {
            return;
        }

        foreach (var (page, rect) in _accessibleModel.LineRects(start, end))
        {
            _session.RevealPageRect(page, rect);
            return;
        }
    }

    internal bool HasFocusForAccessibility => FocusState != FocusState.Unfocused;
}
