using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Quire.App.Services;
using Quire.Core.Geometry;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Quire.App.Views;

/// <summary>
/// The Quire-owned control that hosts the pages (S7, ADR-0006 decision 1). The drawing element lives inside it and is hidden from
/// accessibility; this control owns focus, input and the automation peer, so that the UI Automation Text pattern can arrive in the next work
/// package without restructuring anything. Pages are drawn by an <see cref="Core.Tiles.ITilePresenter"/> (Win2D now).
/// <para>
/// Input: wheel scrolls, Shift+wheel or a horizontal wheel scrolls sideways, Ctrl+wheel zooms about the pointer, touch drags and pinches (with
/// inertia), and Page Up, Page Down, Home, End, the arrow keys and Space navigate. Window-level shortcuts (Ctrl+O, Ctrl+G, zoom keys) are
/// accelerators on the window.
/// </para>
/// </summary>
public sealed class PageSurface : Control
{
    /// <summary>Scroll per wheel notch, in device-independent pixels.</summary>
    private const double WheelStepDip = 100;
    private const double ArrowStepDip = 48;
    private const double ZoomPerNotch = 1.1;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Grid? _host;
    private DocumentSession? _session;

    public PageSurface()
    {
        DefaultStyleKey = typeof(PageSurface);
        Presenter = new Win2DTilePresenter();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.TranslateInertia | ManipulationModes.Scale;
        SizeChanged += (_, _) => PushViewport();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        ActualThemeChanged += (_, _) => ApplyColors();
        PointerPressed += (_, _) => Focus(FocusState.Pointer);
        PointerWheelChanged += OnPointerWheelChanged;
        ManipulationDelta += OnManipulationDelta;
        KeyDown += OnKeyDown;
    }

    internal Win2DTilePresenter Presenter { get; }

    internal DocumentSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            PushViewport();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild("PART_Host") as Grid;
        if (_host is not null && !_host.Children.Contains(Presenter.Element))
        {
            _host.Children.Add(Presenter.Element);
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

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var notches = point.Properties.MouseWheelDelta / 120.0;
        var scale = RasterScale;
        if (IsDown(VirtualKey.Control))
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
            _session.ScrollBy(0, -notches * WheelStepDip * scale);
        }

        e.Handled = true;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var scale = RasterScale;
        if (Math.Abs(e.Delta.Scale - 1.0) > 0.001)
        {
            _session.ZoomBy(e.Delta.Scale, new PointD(e.Position.X * scale, e.Position.Y * scale));
        }

        _session.ScrollBy(-e.Delta.Translation.X * scale, -e.Delta.Translation.Y * scale);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var scale = RasterScale;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.PageDown:
                _session.ScrollScreen(+1);
                break;
            case VirtualKey.PageUp:
                _session.ScrollScreen(-1);
                break;
            case VirtualKey.Space:
                _session.ScrollScreen(shift ? -1 : +1);
                break;
            case VirtualKey.Home:
                _session.FirstPage();
                break;
            case VirtualKey.End:
                _session.LastPage();
                break;
            case VirtualKey.Down when !ctrl:
                _session.ScrollBy(0, ArrowStepDip * scale);
                break;
            case VirtualKey.Up when !ctrl:
                _session.ScrollBy(0, -ArrowStepDip * scale);
                break;
            case VirtualKey.Right when !ctrl:
                _session.NextPage();
                break;
            case VirtualKey.Left when !ctrl:
                _session.PreviousPage();
                break;
            default:
                return; // not ours: accelerators and the system get it
        }

        e.Handled = true;
    }
}

/// <summary>
/// The automation peer of the page surface: a document, named after the open file. The Text pattern (S7) is added here in the next work
/// package; keeping the peer in Quire's own control is what makes that possible.
/// </summary>
internal sealed class PageSurfaceAutomationPeer(PageSurface owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(PageSurface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
