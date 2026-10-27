using System.Runtime;
using Quire.S2;

namespace Quire.S2.Viewer;

/// <summary>Background sampler of per-process GPU memory (DXGI QueryVideoMemoryInfo) and CPU memory; keeps peaks.</summary>
public sealed class MemSampler : IDisposable
{
    readonly Thread _t; volatile bool _stop;
    public double PeakWs, PeakPriv; public Dictionary<string, (double local, double nonlocal)> Peak = new(); public Dictionary<string, (double local, double nonlocal)> Last = new();
    public MemSampler(int periodMs = 250) { _t = new Thread(() => { while (!_stop) { Sample(); Thread.Sleep(periodMs); } }) { IsBackground = true, Name = "mem-sampler" }; _t.Start(); }
    public void Sample()
    {
        var (ws, priv) = Sys.ProcessMem(); PeakWs = Math.Max(PeakWs, ws); PeakPriv = Math.Max(PeakPriv, priv);
        foreach (var m in Dxgi.Query())
        {
            Last[m.Name] = (m.LocalUsageMB, m.NonLocalUsageMB);
            Peak[m.Name] = Peak.TryGetValue(m.Name, out var p) ? (Math.Max(p.local, m.LocalUsageMB), Math.Max(p.nonlocal, m.NonLocalUsageMB)) : (m.LocalUsageMB, m.NonLocalUsageMB);
        }
    }
    public void Dispose() { _stop = true; }
}

public sealed class SettleScenario : Scenario
{
    readonly double _y, _x; readonly int _z; readonly int _timeoutMs; long _t0; public double SettleMs;
    public SettleScenario(int z, double y, double x = 0, int timeoutMs = 8000) { _z = z; _y = y; _x = x; _timeoutMs = timeoutMs; }
    public override void Begin(Runner r, long now, ViewState v) { _t0 = now; Record = false; }
    public override void Update(Runner r, long now, ViewState v)
    {
        v.ZDisp = v.ZTgt = _z; v.ScrollYDisp = v.ScrollYTgt = _y; v.ScrollXDisp = v.ScrollXTgt = _x; v.VelPxPerSec = 0; v.Gesture = 1;
    }
    public override void OnFrame(Runner r, long now, FrameModel m)
    {
        if (m.VisibleCells > 0 && m.BlankCells == 0 && m.LowResCells == 0 && r.P.InFlight == 0) { SettleMs = Clock2.MsSince(_t0); Finish(); }
        else if (Clock2.MsSince(_t0) > _timeoutMs) { SettleMs = -1; Finish(); }
    }
}

public sealed class FlingScenario : Scenario
{
    readonly double _v0, _hold, _tau, _dur, _startY; readonly int _z; long _t0; double _maxY; bool _afterEnd; long _tEnd;
    public double SharpAfterMs = -1; public double EndY;
    public FlingScenario(int z, double startY, double v0, double holdSec, double tauSec, double durSec, bool record = true) { _z = z; _startY = startY; _v0 = v0; _hold = holdSec; _tau = tauSec; _dur = durSec; Record = record; }
    double Offset(double t) => t <= _hold ? _v0 * t : _v0 * _hold + _v0 * _tau * (1 - Math.Exp(-(t - _hold) / _tau));
    double Vel(double t) => t <= _hold ? _v0 : _v0 * Math.Exp(-(t - _hold) / _tau);
    public override void Begin(Runner r, long now, ViewState v) { _t0 = now; var L = r.P.Layout(_z); _maxY = Math.Max(0, L.Total - r.VH); if (Record) r.BeginRecording(); }
    public override void Update(Runner r, long now, ViewState v)
    {
        double t = Clock2.Ms(now - _t0) / 1000.0;
        v.ZDisp = v.ZTgt = _z; v.Gesture = 1;
        if (t < _dur)
        {
            double y = Math.Min(_maxY, _startY + Offset(t));
            v.ScrollYDisp = v.ScrollYTgt = y; v.VelPxPerSec = y >= _maxY ? 0 : Vel(t); EndY = y;
        }
        else
        {
            if (!_afterEnd) { _afterEnd = true; _tEnd = now; if (Record) r.EndRecording(); }
            v.VelPxPerSec = 0;
        }
    }
    public override void OnFrame(Runner r, long now, FrameModel m)
    {
        if (!_afterEnd) return;
        double since = Clock2.MsSince(_tEnd);
        if (m.VisibleCells > 0 && m.BlankCells == 0 && m.LowResCells == 0) { SharpAfterMs = since; Finish(); }
        else if (since > 4000) { SharpAfterMs = -1; Finish(); }
    }
}

/// <summary>Zoom gesture: GPU-scale of the existing tiles for N frames, wait the debounce, request tiles at the new zoom, swap when every visible cell is sharp.</summary>
public sealed class ZoomScenario : Scenario
{
    readonly int _z0, _z1, _gestureFrames, _debounceMs; readonly double _y0;
    int _phase; int _gf; long _tStart, _tGestureEnd, _tCommit; double _yTgt;
    public double GestureStartToFirstFrameMs, SettleToSharpMs, DebounceToSharpMs, TotalMs; public bool TimedOut;
    public readonly List<double> GestureFrameIntervals = new(), GestureFrameWork = new(), WaitFrameIntervals = new();
    long _prevT;
    public ZoomScenario(int z0, int z1, double y0, int gestureFrames = 12, int debounceMs = 120) { _z0 = z0; _z1 = z1; _y0 = y0; _gestureFrames = gestureFrames; _debounceMs = debounceMs; }
    public override void Begin(Runner r, long now, ViewState v) { _tStart = now; Record = false; }
    public override void Update(Runner r, long now, ViewState v)
    {
        double ratio = (double)_z1 / _z0; double vh = r.VH;
        if (_phase == 0)
        {   // gesture: Gesture goes 1 -> ratio exponentially across the frames (display layout stays at z0)
            v.ZDisp = v.ZTgt = _z0; v.ScrollYDisp = v.ScrollYTgt = _y0; v.VelPxPerSec = 0;
            _gf++; v.Gesture = (float)Math.Pow(ratio, Math.Min(1.0, _gf / (double)_gestureFrames));
            if (_gf == 1) GestureStartToFirstFrameMs = 0;
            if (_gf >= _gestureFrames) { _phase = 1; _tGestureEnd = now; }
        }
        else if (_phase == 1)
        {   // settle: hold the scaled image for the debounce
            v.Gesture = (float)ratio;
            if (Clock2.Ms(now - _tGestureEnd) >= _debounceMs)
            {
                _phase = 2; _tCommit = now;
                // anchor the document point under the viewport centre
                _yTgt = (_y0 + vh / 2) * ratio - vh / 2; var L1 = r.P.Layout(_z1); _yTgt = Math.Max(0, Math.Min(_yTgt, L1.Total - vh));
                v.ZTgt = _z1; v.ScrollYTgt = _yTgt; v.ScrollXTgt = 0;
            }
        }
        else
        {   // waiting for sharp tiles at the new zoom; display stays the scaled old tiles until they are all there
            v.Gesture = (float)ratio;
        }
    }
    public override void OnFrame(Runner r, long now, FrameModel m)
    {
        if (_prevT != 0) { double iv = Clock2.Ms(now - _prevT); if (_phase == 0) GestureFrameIntervals.Add(iv); else WaitFrameIntervals.Add(iv); }
        _prevT = now;
        if (_phase == 0 && _gf == 1) GestureStartToFirstFrameMs = Clock2.MsSince(_tStart);
        if (_phase == 2)
        {
            if (r.P.AllVisibleSharp(_z1, _yTgt, 0, r.VW, r.VH, out _) && AllThumbsOrSharp(r))
            {
                // swap the displayed layout to the new zoom: next frame draws sharp tiles at gesture 1
                r.View.ZDisp = _z1; r.View.ScrollYDisp = _yTgt; r.View.ScrollXDisp = 0; r.View.Gesture = 1;
                DebounceToSharpMs = Clock2.Ms(now - _tCommit); SettleToSharpMs = Clock2.Ms(now - _tGestureEnd); TotalMs = Clock2.Ms(now - _tStart);
                _phase = 3; Finish();
            }
            else if (Clock2.Ms(now - _tCommit) > 5000) { TimedOut = true; DebounceToSharpMs = SettleToSharpMs = -1; Finish(); }
        }
    }
    static bool AllThumbsOrSharp(Runner r) => true;
}
