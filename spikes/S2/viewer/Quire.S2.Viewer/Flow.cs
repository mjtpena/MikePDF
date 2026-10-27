using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Quire.S2;

namespace Quire.S2.Viewer;

public static class Flow
{
    static string S2Root => AppContext.BaseDirectory[..AppContext.BaseDirectory.IndexOf(@"\out\bin\", StringComparison.OrdinalIgnoreCase)];
    static string Fx(string n) => Path.Combine(S2Root, "out", "fixtures", n);

    public static async Task Main2(MainWindow w, Options o)
    {
        var r = new Runner(w, o);
        try { await Run(r, w, o); }
        catch (Exception ex)
        {
            Directory.CreateDirectory(r.OutDir == "" ? "." : r.OutDir);
            File.WriteAllText(Path.Combine(r.OutDir == "" ? "." : r.OutDir, "ERROR.txt"), ex.ToString());
            r.Exit(1);
        }
    }

    static async Task Run(Runner r, MainWindow w, Options o)
    {
        string mode = o.Str("mode", "fling");
        if (o.Int("affinity", 0) != 0) Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)o.Int("affinity", 0);   // decimal mask; the engine child inherits it
        string root = Path.Combine(S2Root, "out", "results", "viewer");
        r.Label = o.Str("label", $"{o.Str("presenter", "win2d")}-{mode}");
        r.OutDir = Path.Combine(o.Str("out", root), r.Label);
        Directory.CreateDirectory(r.OutDir);
        r.DocPath = Path.GetFullPath(o.Str("doc", Fx("text-book.pdf")));
        r.UiUpload = o.Str("upload", "thread") == "ui";

        // ---- window: fixed client size, topmost so the compositor never treats it as occluded
        int cw = o.Int("vw", 1280), ch = o.Int("vh", 1000);
        w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(40, 40, cw + 16, ch + 39));
        if (w.AppWindow.Presenter is OverlappedPresenter op) { op.IsAlwaysOnTop = true; op.IsResizable = false; }
        w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(cw, ch));
        w.Activate();

        // ---- presenter
        string pn = o.Str("presenter", "win2d");
        // rasterisation scale comes from the XamlRoot once the window content is loaded
        await Task.Delay(400);
        r.RasterScale = w.Content.XamlRoot?.RasterizationScale ?? 1.0;
        IPresenter pres = pn == "comp" ? new CompPresenter(r.RasterScale) : new Win2DPresenter(r.RasterScale);
        r.Presenter = pres;
        var host = w.HostGrid;
        host.Children.Add(new PageSurface(pres.Element));
        await Task.Delay(300);
        if (pres is CompPresenter cp) cp.Initialize();
        r.VW = Math.Round(host.ActualWidth * r.RasterScale); r.VH = Math.Round(host.ActualHeight * r.RasterScale);
        r.SetMargin(pres is CompPresenter ? r.VH : 0);

        // ---- environment
        var memBefore = Dxgi.Query();
        r.Note("env", Env(r, o, mode));
        // ---- engine
        bool sandbox = o.Flag("sandbox", true);
        string exe = Path.Combine(AppContext.BaseDirectory, "Engine", "engine.exe");
        long tSpawn = Clock2.Now();
        r.Engine = EngineClient.Start(exe, sandbox ? Isolation.AppContainer : Isolation.None, o.Int("pagecache", 8));
        r.Note("engine", new { sandbox, spawnToHelloMs = Clock2.Ms(r.Engine.THello - tSpawn), pdfiumInitMs = r.Engine.PdfiumInitMs, pid = r.Engine.Pid, pingMs = Enumerable.Range(0, 5).Select(_ => r.Engine.PingMs()).Min() });
        r.Engine.SetOptions(o.Int("pagecache", 8), o.Flag("progressive", true));
        r.P = new Pipeline(r.Engine, pres, o, r.RasterScale);
        r.P.Start();
        r.StartFrameLoop();
        using var mem = new MemSampler(o.Int("memperiod", 250));

        if (mode == "ttft") await Modes2.Ttft(r, o);
        else
        {
            var (pages, openMs) = r.Engine.Open(r.DocPath);
            r.P.Sizes = r.Engine.PageSizes(); r.P.ClearLayouts();
            r.Note("doc", new { path = Path.GetFileName(r.DocPath), pages, openMs });
            switch (mode)
            {
                case "fling": await Modes2.Fling(r, o, mem); break;
                case "zoom": await Modes2.Zoom(r, o); break;
                case "upload": await Modes2.Upload(r, o, memBefore); break;
                case "idle": await Modes2.Idle(r, o, mem); break;
                default: throw new Exception("unknown mode " + mode);
            }
        }
        r.Note("mem", new { peakWsMB = mem.PeakWs, peakPrivMB = mem.PeakPriv, gpuPeak = mem.Peak.ToDictionary(k => k.Key, k => new { localMB = k.Value.local, nonLocalMB = k.Value.nonlocal }), gpuLast = mem.Last.ToDictionary(k => k.Key, k => new { localMB = k.Value.local, nonLocalMB = k.Value.nonlocal }) });
        if (pres is CompPresenter cp2) r.Note("compCreateDrawingSessionRetries", cp2.Retries);
        r.Note("engineStderr", r.Engine.StdErr);
        r.WriteJson("summary.json", r.Res);
        r.Exit(0);
    }

    static object Env(Runner r, Options o, string mode)
    {
        var ad = Dxgi.Adapters();
        var (rw, rh) = Sys.PrimaryRes();
        return new
        {
            args = o.ToString(), mode, presenter = r.Presenter.Name, os = Environment.OSVersion.VersionString, runtime = RuntimeInformation.FrameworkDescription, arch = RuntimeInformation.ProcessArchitecture.ToString(),
            rasterScale = r.RasterScale, viewportPx = new { r.VW, r.VH }, primaryRes = new { rw, rh }, refreshHz = Sys.PrimaryRefreshHz(), dwm = Dwm.Get(r.Hwnd),
            adapters = ad.Select(a => new { a.Index, a.Name, a.VendorId, a.DedicatedVideoMB, a.SharedSystemMB, a.Flags }).ToList(),
            gc = new { server = GCSettings.IsServerGC, latency = GCSettings.LatencyMode.ToString() }, tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            cores = Environment.ProcessorCount, doc = Path.GetFileName(r.DocPath), tile = o.Int("tile", 512), custom = (r.Presenter as Win2DPresenter)?.CustomDeviceNote,
            time = DateTime.Now.ToString("s"),
        };
    }
}
