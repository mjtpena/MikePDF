using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PageA11y;

public sealed class MainWindow : Window
{
    private readonly Options _opt;
    private readonly PageSurface _page = new();
    private readonly ComboBox _fixtures = new() { Width = 320 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ToggleButton _overlayToggle = new() { Content = "Overlay" };
    private string[] _fixturePaths = Array.Empty<string>();

    public IntPtr Hwnd { get; }

    public MainWindow(Options opt)
    {
        _opt = opt;
        Title = "S7 PageA11y";
        Hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1000));
        (Application.Current as App)!.SetWindowHandle(Hwnd);

        var rotate = new Button { Content = "Rotate 90 CW" };
        AutomationProperties.SetAutomationId(rotate, "RotateButton");
        AutomationProperties.SetAutomationId(_overlayToggle, "OverlayToggle");
        AutomationProperties.SetAutomationId(_fixtures, "FixtureCombo");
        AutomationProperties.SetName(_fixtures, "Fixture");
        AutomationProperties.SetAutomationId(_status, "StatusText");
        AutomationProperties.SetAutomationId(_page, "PageSurface");
        rotate.Click += (_, _) => { _page.RotateClockwise(); UpdateStatus(); };
        _overlayToggle.Click += (_, _) =>
        {
            _page.SetOverlay(_overlayToggle.IsChecked == true ? OverlayMode.Lines | OverlayMode.Chars : OverlayMode.None);
        };
        _fixtures.SelectionChanged += (_, _) =>
        {
            if (_fixtures.SelectedIndex >= 0) { _page.Load(_fixturePaths[_fixtures.SelectedIndex]); _page.SetViewRotation(_page.ViewRotation); UpdateStatus(); }
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(8) };
        bar.Children.Add(_fixtures);
        bar.Children.Add(rotate);
        bar.Children.Add(_overlayToggle);
        bar.Children.Add(_status);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _page,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroller, 1);
        grid.Children.Add(bar);
        grid.Children.Add(scroller);
        Content = grid;

        _page.Zoom = opt.Zoom;
        grid.Loaded += async (_, _) => await OnLoadedAsync();
    }

    private async Task OnLoadedAsync()
    {
        try
        {
            string dir = _opt.FixturesDir ?? FindFixturesDir();
            _fixturePaths = Directory.GetFiles(dir, "*.pdf").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (var p in _fixturePaths) _fixtures.Items.Add(Path.GetFileName(p));
            _page.SetOverlay(_opt.Overlay);
            _overlayToggle.IsChecked = _opt.Overlay != OverlayMode.None;
            string first = _opt.Fixture ?? _fixturePaths[0];
            _page.SetViewRotation(_opt.Rotation);
            _page.Load(first);
            int idx = Array.FindIndex(_fixturePaths, p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _fixtures.SelectedIndex = idx;
            UpdateStatus();
            Log.Write($"ready fixture={first} rot={_opt.Rotation} zoom={_opt.Zoom} scale={_page.DpiScale} order={_page.Model?.OrderSource}");

            if (_opt.ShotsDir != null) await TakeAllShotsAsync(_opt.ShotsDir);
        }
        catch (Exception ex)
        {
            Log.Write("OnLoaded failed: " + ex);
        }
    }

    private void UpdateStatus()
        => _status.Text = $"rotation {(_page.ViewRotation * 90)} deg | order: {_page.Model?.OrderSource} | {(_page.Model?.Length ?? 0)} atoms";

    private static string FindFixturesDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !(d.Name == "S7" && d.Parent?.Name == "spikes")) d = d.Parent;
        return Path.Combine(d!.FullName, "fixtures");
    }

    private async Task TakeAllShotsAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        _overlayToggle.IsChecked = true;
        _page.SetOverlay(OverlayMode.Lines | OverlayMode.Chars);
        foreach (var f in _fixturePaths)
        {
            _page.Load(f);
            for (int rot = 0; rot < 4; rot++)
            {
                _page.SetViewRotation(rot);
                UpdateStatus();
                await Task.Delay(400);
                string name = Path.GetFileNameWithoutExtension(f) + $"-view{rot * 90}.png";
                await SaveShotAsync(_page, Path.Combine(outDir, name));
                Log.Write("shot " + name);
                if (rot == 1) await SaveShotAsync((UIElement)Content, Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "-WINDOW-view90.png"));
            }
        }
        Log.Write("shots done");
        Application.Current.Exit();
    }

    public static async Task SaveShotAsync(UIElement el, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(el);
        var pixels = await rtb.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var r = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) r.ReadBytes(bytes);
        using var stream = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, bytes);
        await enc.FlushAsync();
        stream.Seek(0);
        var data = new byte[stream.Size];
        using (var r = new DataReader(stream)) { await r.LoadAsync((uint)stream.Size); r.ReadBytes(data); }
        await File.WriteAllBytesAsync(path, data);
    }
}
