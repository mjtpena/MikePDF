using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using S7.Core.Engine;
using S7.Core.Geometry;
using S7.Core.Text;
using Windows.Foundation;
using WinRT.Interop;

namespace PageA11y;

/// <summary>
/// The custom page control. In production this would be the Win2D / Composition tile surface; here it is a ContentControl
/// that shows one PDFium-rendered bitmap plus a debug overlay. ContentControl (a Control) is used rather than a bare
/// Grid because only Controls take keyboard focus, which a screen-reader caret needs.
/// All text knowledge comes from <see cref="TextModel"/>, which was built from plain engine data.
/// </summary>
public sealed class PageSurface : ContentControl
{
    private readonly Grid _root = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private PdfiumEngine? _engine;
    private bool _dragging;
    private int _anchor;

    public TextModel? Model { get; private set; }
    public PageTextData? Data { get; private set; }
    public int ViewRotation { get; private set; }
    public double Zoom { get; set; } = 1.0;
    public OverlayMode Overlay { get; set; } = OverlayMode.None;
    public int SelStart { get; private set; }
    public int SelEnd { get; private set; }
    public string? FixturePath { get; private set; }

    public event Action? SelectionChanged;
    public event Action? TextChanged;

    public PageSurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _root.Children.Add(_image);
        _root.Children.Add(_overlay);
        Content = _root;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => { _dragging = false; ReleasePointerCaptures(); };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PageSurfaceAutomationPeer(this);

    // ---------------------------------------------------------------------------------------------------------------
    public void Load(string path)
    {
        _engine?.Dispose();
        _engine = PdfiumEngine.Open(path);
        FixturePath = path;
        Data = _engine.ExtractPage(0);
        Model = TextModelBuilder.Build(Data);
        SelStart = SelEnd = 0;
        Refresh();
        TextChanged?.Invoke();
    }

    public void SetViewRotation(int quarterTurns)
    {
        ViewRotation = ((quarterTurns % 4) + 4) % 4;
        Refresh();
    }

    public void RotateClockwise() => SetViewRotation(ViewRotation + 1);

    public void SetOverlay(OverlayMode m) { Overlay = m; RedrawOverlay(); }

    public void Refresh()
    {
        if (_engine == null || Model == null) return;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        var page = _engine.Render(0, Zoom * scale, ViewRotation);
        var wb = new WriteableBitmap(page.Width, page.Height);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(page.Bgra, 0, page.Bgra.Length);
        _image.Source = wb;
        var (vw, vh) = Model.ViewSize(ViewRotation);
        _root.Width = _image.Width = _overlay.Width = vw * Zoom;
        _root.Height = _image.Height = _overlay.Height = vh * Zoom;
        RedrawOverlay();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Selection (shared by pointer input, the UIA provider and the overlay)
    public void SetSelection(int start, int end)
    {
        if (Model == null) return;
        start = Math.Clamp(start, 0, Model.Length);
        end = Math.Clamp(end, start, Model.Length);
        if (start == SelStart && end == SelEnd) return;
        SelStart = start; SelEnd = end;
        RedrawOverlay();
        SelectionChanged?.Invoke();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Model == null) return;
        Focus(FocusState.Pointer);
        var p = e.GetCurrentPoint(_root).Position;
        _anchor = Math.Max(0, Model.HitTest(p.X / Zoom, p.Y / Zoom, ViewRotation));
        _dragging = true;
        CapturePointer(e.Pointer);
        SetSelection(_anchor, _anchor);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Model == null) return;
        var p = e.GetCurrentPoint(_root).Position;
        int i = Model.HitTest(p.X / Zoom, p.Y / Zoom, ViewRotation);
        if (i < 0) return;
        SetSelection(Math.Min(_anchor, i), Math.Max(_anchor, i) + (i >= _anchor ? 1 : 0));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Geometry: model (view page space, points) -> local DIPs -> screen physical pixels

    /// <summary>One rectangle per line for [start,end), in DIPs relative to the page content origin. This is what the UIA provider and the overlay both use.</summary>
    public IReadOnlyList<Rect> RangeRectsLocal(int start, int end)
    {
        if (Model == null) return Array.Empty<Rect>();
        return Model.BoundingRects(start, end, ViewRotation)
            .Select(r => new Rect(r.X * Zoom, r.Y * Zoom, r.Width * Zoom, r.Height * Zoom)).ToList();
    }

    public double DpiScale => XamlRoot?.RasterizationScale ?? 1.0;

    private IntPtr Hwnd => (App.Current as App)?.WindowHandle ?? IntPtr.Zero;

    // What WinUI 3 expects from ITextRangeProvider.GetBoundingRectangles / RangeFromPoint is NOT the screen-pixel contract of
    // native UIA: the XAML wrapper adds the window client origin and applies the DPI scale itself, so providers work in DIPs
    // relative to the XAML root (measured, see docs/spikes/S7.md "Surprises"). These helpers convert accordingly.

    /// <summary>DIPs relative to the XAML root (window content) of the top-left corner of the page content.</summary>
    public Point ContentOriginInRoot()
    {
        var t = _root.TransformToVisual(null);
        return t.TransformPoint(new Point(0, 0));
    }

    public Rect LocalToRoot(Rect local)
    {
        var o = ContentOriginInRoot();
        return new Rect(o.X + local.X, o.Y + local.Y, local.Width, local.Height);
    }

    public Point RootToLocal(Point root)
    {
        var o = ContentOriginInRoot();
        return new Point(root.X - o.X, root.Y - o.Y);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Overlay (S7.3). Draws exactly the rectangles GetBoundingRectangles would hand to UIA (before the screen transform).
    public void RedrawOverlay()
    {
        _overlay.Children.Clear();
        if (Model == null) return;
        int n = Model.Length;
        void Add(Rect r, Windows.UI.Color stroke, Windows.UI.Color fill, double th)
        {
            var rect = new Rectangle { Width = Math.Max(0.5, r.Width), Height = Math.Max(0.5, r.Height), Stroke = new SolidColorBrush(stroke), StrokeThickness = th, Fill = new SolidColorBrush(fill) };
            Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y);
            _overlay.Children.Add(rect);
        }
        var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        if (Overlay.HasFlag(OverlayMode.Lines))
        {
            var b = Model.Boundaries(TextUnitKind.Line);
            for (int i = 0; i + 1 < b.Length; i++)
                foreach (var r in RangeRectsLocal(b[i], b[i + 1])) Add(r, Windows.UI.Color.FromArgb(220, 0, 90, 255), Windows.UI.Color.FromArgb(25, 0, 90, 255), 1.5);
        }
        if (Overlay.HasFlag(OverlayMode.Words))
        {
            var b = Model.Boundaries(TextUnitKind.Word);
            for (int i = 0; i + 1 < b.Length; i++)
                foreach (var r in RangeRectsLocal(b[i], b[i + 1])) Add(r, Windows.UI.Color.FromArgb(200, 0, 160, 60), clear, 1.0);
        }
        if (Overlay.HasFlag(OverlayMode.Chars))
        {
            for (int i = 0; i < n; i++)
                foreach (var r in RangeRectsLocal(i, i + 1)) Add(r, Windows.UI.Color.FromArgb(230, 230, 0, 0), clear, 0.6);
        }
        if (SelEnd > SelStart)
            foreach (var r in RangeRectsLocal(SelStart, SelEnd)) Add(r, Windows.UI.Color.FromArgb(255, 200, 150, 0), Windows.UI.Color.FromArgb(90, 255, 200, 0), 1.0);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
}
