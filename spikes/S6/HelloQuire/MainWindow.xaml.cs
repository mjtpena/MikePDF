using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.DirectX;

namespace HelloQuire;

public sealed partial class MainWindow : Window
{
    private CanvasBitmap? _pageBitmap;
    private int _clicks;
    private bool _firstRender;
    private bool _firstDraw;
    private string _pdfStatus = "";

    public MainWindow()
    {
        Timing.Mark("MainWindow.ctor.enter");
        InitializeComponent();
        Timing.Mark("MainWindow.InitializeComponent.done");
        Activated += OnActivated;
        CompositionTarget.Rendering += OnRendering;
        Root.Loaded += async (s, e) =>
        {
            Timing.Mark("Root.Loaded");
            var probeOut = Log.Arg("--probe");
            if (probeOut != null)
            {
                string report;
                try { report = await Probes.RunAllAsync(this); } catch (Exception ex) { report = "PROBE CRASH: " + ex; }
                File.WriteAllText(probeOut, report);
                Environment.Exit(0);
            }
            if (Log.Arg("--launch-file") is string lf)
            {
                // Double-click equivalent with an explicit handler: ask the shell to open a .pdf in THIS package (no default-app change).
                if (!File.Exists(lf)) File.WriteAllBytes(lf, Pdfium.MakeSamplePdf());
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(lf);
                var opts = new Windows.System.LauncherOptions { TargetApplicationPackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName };
                bool ok = await Windows.System.Launcher.LaunchFileAsync(file, opts);
                Log.Write("LaunchFileAsync(" + lf + ") with target PFN -> " + ok);
                await Task.Delay(3000);
                Environment.Exit(0);
            }
            if (Log.Flag("--engine-test"))
            {
                var r = LaunchEngine();
                Log.Write("engine-test: " + r);
                Environment.Exit(0);
            }
            if (Log.Arg("--run-seconds") is string secs && double.TryParse(secs, out var s2))
            {
                await Task.Delay(TimeSpan.FromSeconds(s2));
                Environment.Exit(0);
            }
        };
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!Timing.Enabled) return;
        Timing.Mark("Window.Activated." + args.WindowActivationState);
    }

    private void OnRendering(object? sender, object e)
    {
        if (_firstRender) return;
        _firstRender = true;
        Timing.Mark("CompositionTarget.Rendering.first");
        var timingOut = Environment.GetEnvironmentVariable("QUIRE_TIMING_OUT");
        if (!string.IsNullOrEmpty(timingOut) && Timing.Enabled && Log.Flag("--bench"))
        {
            // Defer one dispatcher turn so the window has really been shown, then record and exit.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                Timing.Mark("Dispatcher.idle.afterFirstRender");
                Timing.WriteTo(timingOut);
                Environment.Exit(0);
            });
        }
    }

    private void Canvas_CreateResources(CanvasVirtualControl sender, CanvasCreateResourcesEventArgs args)
    {
        try
        {
            var (bgra, w, h) = Pdfium.RenderSample(1.5);
            _pageBitmap = CanvasBitmap.CreateFromBytes(sender, bgra, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _pdfStatus = $"PDFium bitmap {w}x{h} OK";
        }
        catch (Exception ex)
        {
            _pdfStatus = "PDFium FAILED: " + ex.GetType().Name + ": " + ex.Message;
            Log.Write(_pdfStatus);
        }
        Timing.Mark("Win2D.CreateResources.done");
    }

    private void Canvas_RegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            DrawScene(ds, sender.ActualWidth, sender.ActualHeight, _pageBitmap, _pdfStatus);
        }
        if (!_firstDraw)
        {
            _firstDraw = true;
            Timing.Mark("Win2D.firstDraw.done");
            Log.Write("Win2D first draw done; " + _pdfStatus + "; device=" + sender.Device.GetType().Name);
        }
    }

    internal static void DrawScene(CanvasDrawingSession ds, double width, double height, CanvasBitmap? page, string status)
    {
        ds.Clear(Windows.UI.Color.FromArgb(255, 32, 36, 40));
        for (int y = 0; y < height; y += 40)
            for (int x = 0; x < width; x += 40)
                if (((x / 40) + (y / 40)) % 2 == 0) ds.FillRectangle(x, y, 40, 40, Windows.UI.Color.FromArgb(255, 40, 45, 50));
        if (page != null)
            ds.DrawImage(page, new Windows.Foundation.Rect(20, 20, page.SizeInPixels.Width, page.SizeInPixels.Height));
        // vector path (cubic Beziers) + gradient disc
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(40, 520);
        pb.AddCubicBezier(new(160, 420), new(260, 620), new(380, 520));
        pb.AddCubicBezier(new(480, 440), new(560, 600), new(700, 520));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, Colors.Orange, 6);
        using var grad = new CanvasRadialGradientBrush(ds, Colors.Yellow, Colors.OrangeRed) { Center = new(780, 480), RadiusX = 60, RadiusY = 60 };
        ds.FillCircle(780, 480, 60, grad);
        using var fmt = new CanvasTextFormat { FontSize = 20, FontFamily = "Segoe UI" };
        ds.DrawText("Win2D CanvasVirtualControl + WinAppSDK 2.5.1 | " + status, 20, 610, Colors.White, fmt);
        ds.DrawText("BGRA tile from PDFium P/Invoke -> CanvasBitmap.CreateFromBytes -> DrawImage", 20, 640, Colors.LightGray, fmt);
    }

    private void ClickButton_Click(object sender, RoutedEventArgs e)
    {
        _clicks++;
        StatusText.Text = "Clicked " + _clicks;
    }

    private void LaunchEngineButton_Click(object sender, RoutedEventArgs e) => EngineText.Text = LaunchEngine();

    internal static string LaunchEngine()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Engine", "EngineStub.exe");
            if (!File.Exists(exe)) return "ENGINE MISSING: " + exe;
            var psi = new ProcessStartInfo(exe, "--hello") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine() ?? "(no output)";
            p.WaitForExit(5000);
            return line + " exit=" + p.ExitCode;
        }
        catch (Exception ex) { return "ENGINE LAUNCH FAILED: " + ex.GetType().Name + ": " + ex.Message; }
    }
}
