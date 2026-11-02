using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quire.S2;

namespace Quire.S2.Viewer;

/// <summary>Hosts the presenter inside a XAML control (S7: the page surface must be a Control, not a bare element).</summary>
public sealed class PageSurface : UserControl { public PageSurface(FrameworkElement presenter) { Content = presenter; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; } }

public abstract class Scenario
{
    public readonly TaskCompletionSource<bool> Tcs = new();
    public bool Record = true;
    public virtual void Begin(Runner r, long now, ViewState v) { }
    public abstract void Update(Runner r, long now, ViewState v);       // set scroll/zoom for this frame; call Finish() when done
    public virtual void OnFrame(Runner r, long now, FrameModel m) { }
    public void Finish() => Tcs.TrySetResult(true);
}

public sealed class FrameLog
{
    public readonly List<double> T = new(8192), Interval = new(8192), Work = new(8192), Sharp = new(8192), Blank = new(8192), LowRes = new(8192), ScrollY = new(8192), UpdateMs = new(8192);
    public readonly List<int> BlankCells = new(8192), LowResCells = new(8192), InFlight = new(8192), Draws = new(8192), VisibleCells = new(8192);
    public void Clear() { T.Clear(); Interval.Clear(); Work.Clear(); Sharp.Clear(); Blank.Clear(); LowRes.Clear(); ScrollY.Clear(); UpdateMs.Clear(); BlankCells.Clear(); LowResCells.Clear(); InFlight.Clear(); Draws.Clear(); VisibleCells.Clear(); }
}

public sealed class Runner
{
    public readonly MainWindow Win; public readonly Options Opt; public readonly DispatcherQueue Dq;
    public Pipeline P = null!; public IPresenter Presenter = null!; public EngineClient Engine = null!;
    public double RasterScale = 1; public double VW, VH;
    public readonly ViewState View = new();
    public readonly FrameModel Model = new();
    public readonly FrameLog Log = new();
    public readonly Dictionary<string, object?> Res = new();
    Scenario? _sc; long _frame; long _prevTick; bool _recording; long _recStart;
    double _margin;
    public string OutDir = "", Label = "";
    public string DocPath = "";
    public bool UiUpload;
    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(Win);
    public double ObservedRenderIntervalMs;

    public Runner(MainWindow win, Options opt) { Win = win; Opt = opt; Dq = win.DispatcherQueue; }

    public void Note(string k, object? v) { Res[k] = v; }

    // ------------------------------------------------------------------------------------------------ frame loop
    public void StartFrameLoop() { CompositionTarget.Rendering += OnRender; }
    public void StopFrameLoop() { CompositionTarget.Rendering -= OnRender; }

    void OnRender(object? sender, object e)
    {
        long now = Clock2.Now();
        double interval = _prevTick == 0 ? 0 : Clock2.Ms(now - _prevTick); _prevTick = now;
        if (P == null) return;
        _frame++; View.Frame = _frame; View.VW = VW; View.VH = VH;
        var sc = _sc;
        if (sc != null) sc.Update(this, now, View);
        if (UiUpload) P.DrainOnUi(Opt.Dbl("uibudget", 3.0));
        P.Publish(View);
        P.BuildFrame(View, Model, _margin);
        P.EvictIfNeeded(_frame);
        Presenter.Update(Model);
        double work = Clock2.MsSince(now);
        if (_recording && interval > 0)
        {
            double tot = Model.SharpPx + Model.LowResPx + Model.BlankPx; if (tot <= 0) tot = 1;
            Log.T.Add(Clock2.Ms(now - _recStart)); Log.Interval.Add(interval); Log.Work.Add(work); Log.Sharp.Add(Model.SharpPx / tot); Log.Blank.Add(Model.BlankPx); Log.LowRes.Add(Model.LowResPx);
            Log.ScrollY.Add(View.ScrollYDisp); Log.BlankCells.Add(Model.BlankCells); Log.LowResCells.Add(Model.LowResCells); Log.VisibleCells.Add(Model.VisibleCells); Log.InFlight.Add(P.InFlight); Log.Draws.Add(Presenter.DrawCount);
        }
        sc?.OnFrame(this, now, Model);
    }

    public async Task Run(Scenario sc)
    {
        sc.Begin(this, Clock2.Now(), View);
        _sc = sc;
        await sc.Tcs.Task;
        _sc = null;
    }

    public void BeginRecording() { Log.Clear(); _recStart = Clock2.Now(); _recording = true; }
    public void EndRecording() { _recording = false; }
    public void SetMargin(double m) => _margin = m;
    public long Frame => _frame;

    // ------------------------------------------------------------------------------------------------ helpers
    public async Task WaitUntil(Func<bool> cond, int timeoutMs, int pollMs = 5)
    {
        long t0 = Clock2.Now();
        while (!cond() && Clock2.MsSince(t0) < timeoutMs) await Task.Delay(pollMs);
    }

    public async Task Sleep(int ms) => await Task.Delay(ms);

    /// <summary>Run on the UI thread and wait (composition objects must be disposed on their owning thread: COMException 0x8001010E otherwise).</summary>
    public void OnUiSync(Action a)
    {
        if (Dq.HasThreadAccess) { a(); return; }
        using var done = new ManualResetEventSlim(); Exception? ex = null;
        Dq.TryEnqueue(() => { try { a(); } catch (Exception e) { ex = e; } finally { done.Set(); } });
        done.Wait(); if (ex != null) throw ex;
    }

    public static JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public void WriteJson(string name, object o) { Directory.CreateDirectory(OutDir); File.WriteAllText(Path.Combine(OutDir, name), JsonSerializer.Serialize(o, Json)); }

    public void Exit(int code = 0)
    {
        try { StopFrameLoop(); } catch { }
        try { P?.Dispose(); } catch { }
        try { Engine?.Dispose(); } catch { }
        try { int hr = AppContainerProfile.Delete(); } catch { }
        Environment.Exit(code);
    }
}
