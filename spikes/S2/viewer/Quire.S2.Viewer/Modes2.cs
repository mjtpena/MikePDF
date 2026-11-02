using System.Runtime;
using Microsoft.Graphics.Canvas;
using Quire.S2;

namespace Quire.S2.Viewer;

public sealed class TtftScenario : Scenario
{
    readonly int _z; readonly double _y; public long T0;
    public double TThumbReady = -1, TThumbSubmitted = -1, TThumbComposed = -1, TFirstSharpReady = -1, TFirstSharpSubmitted = -1, TFirstSharpComposed = -1, TAllReady = -1, TAllSubmitted = -1, TAllComposed = -1;
    readonly List<long> _ticks = new(256); long _rThumb = -1, _rFirst = -1, _rAll = -1; bool _first = true;
    public TtftScenario(int z, double y, long t0) { _z = z; _y = y; T0 = t0; }
    public override void Begin(Runner r, long now, ViewState v)
    {
        Record = false;
        // publish the view immediately (an application opens the document and asks for tiles; it does not wait for the next render tick)
        v.ZDisp = v.ZTgt = _z; v.ScrollYDisp = v.ScrollYTgt = _y; v.ScrollXDisp = v.ScrollXTgt = 0; v.VelPxPerSec = 0; v.Gesture = 1; v.VW = r.VW; v.VH = r.VH; v.Frame = r.Frame;
        r.P.Publish(v);
    }
    public override void Update(Runner r, long now, ViewState v) { v.ZDisp = v.ZTgt = _z; v.ScrollYDisp = v.ScrollYTgt = _y; v.ScrollXDisp = v.ScrollXTgt = 0; v.VelPxPerSec = 0; v.Gesture = 1; _ticks.Add(now); }
    double Ms(long t) => Clock2.Ms(t - T0);
    /// <summary>First tick whose start is at or after the ready time = the tick that attaches/draws it ("submitted"); the next tick start = "composed" (the compositor consumed that frame).</summary>
    void Resolve(long ready, out double submitted, out double composed)
    {
        submitted = composed = -1; if (ready < 0) return;
        for (int i = 0; i < _ticks.Count; i++) if (_ticks[i] >= ready) { submitted = Ms(_ticks[i]); if (i + 1 < _ticks.Count) composed = Ms(_ticks[i + 1]); return; }
    }
    public override void OnFrame(Runner r, long now, FrameModel m)
    {
        if (_rThumb < 0)
        {
            long best = long.MaxValue; foreach (var p in m.Pages) if (p.VisiblePage && p.Thumb != null) best = Math.Min(best, p.Thumb.ReadyAt);
            if (best != long.MaxValue) _rThumb = best;
        }
        if (_rFirst < 0 && m.SharpCells > 0) { long best = long.MaxValue; foreach (var p in m.Pages) foreach (var t in p.Tiles) if (t.Visible) best = Math.Min(best, t.E.ReadyAt); if (best != long.MaxValue) _rFirst = best; }
        if (_rAll < 0 && m.VisibleCells > 0 && m.SharpCells == m.VisibleCells) { long worst = 0; foreach (var p in m.Pages) foreach (var t in p.Tiles) if (t.Visible) worst = Math.Max(worst, t.E.ReadyAt); _rAll = worst; }
        bool timeout = Clock2.MsSince(T0) > 8000;
        bool haveAll = _rAll >= 0 && _rThumb >= 0 && _rFirst >= 0;
        if (haveAll)
        {
            long maxReady = Math.Max(_rAll, Math.Max(_rFirst, _rThumb)); int after = 0;
            for (int i = _ticks.Count - 1; i >= 0 && _ticks[i] >= maxReady; i--) after++;
            if (after < 2 && !timeout) return;     // two ticks after the last ready event: one submits it, the next one is "composed"
        }
        if (haveAll || timeout)
        {
            TThumbReady = _rThumb < 0 ? -1 : Ms(_rThumb); TFirstSharpReady = _rFirst < 0 ? -1 : Ms(_rFirst); TAllReady = _rAll < 0 ? -1 : Ms(_rAll);
            Resolve(_rThumb, out TThumbSubmitted, out TThumbComposed); Resolve(_rFirst, out TFirstSharpSubmitted, out TFirstSharpComposed); Resolve(_rAll, out TAllSubmitted, out TAllComposed);
            Finish();
        }
    }
}

public static class Modes2
{
    static object FrameStats(FrameLog L, double vsyncMs, double medianTickMs)
    {
        var iv = L.Interval; int n = iv.Count;
        int Over(double x) => iv.Count(v => v > x);
        var st = Stat.Of(iv); var wk = Stat.Of(L.Work);
        int blankFrames = L.BlankCells.Count(c => c > 0);
        int lowFrames = 0; for (int i = 0; i < n; i++) if (L.BlankCells[i] == 0 && L.LowResCells[i] > 0) lowFrames++;
        int sharpFrames = n - blankFrames - lowFrames;
        double maxBlank = L.Blank.Count == 0 ? 0 : L.Blank.Max();
        double avgSharp = L.Sharp.Count == 0 ? 0 : L.Sharp.Average();
        double vis = L.VisibleCells.Count == 0 ? 1 : L.VisibleCells.Average();
        return new
        {
            frames = n, intervalMs = st, workMs = wk, vsyncMs, over16_7 = Over(16.7), over20 = Over(20.0), over25 = Over(25.0), over33_4 = Over(33.4), over50 = Over(50.0), droppedTicks1_5xMedian = Over(1.5 * medianTickMs), medianTickMs, longestRunOver16_7 = LongestRun(iv, 16.7),
            blankFrames, lowResOnlyFrames = lowFrames, fullySharpFrames = sharpFrames, maxBlankPx = maxBlank, meanSharpPxFraction = avgSharp, meanVisibleCells = vis,
            totalMs = L.T.Count == 0 ? 0 : L.T[^1],
        };
    }
    static int LongestRun(List<double> v, double th) { int best = 0, cur = 0; foreach (var x in v) { if (x > th) { cur++; best = Math.Max(best, cur); } else cur = 0; } return best; }

    static object PipelineStats(Pipeline p) => new
    {
        requests = p.Requests, tilesReady = p.TilesReady, thumbsReady = p.ThumbsReady, cancelledByEngine = p.Cancelled, cancelCalls = p.CancelCalls, errors = p.Errors, lastError = p.LastError, evicted = p.Evicted, cachedEntries = p.Store.Count, cacheNominalMB = p.Store.GpuBytes / 1048576.0,
        maxInflightSeen = p.MaxInflightSeen, minFreeSlots = p.MinFreeSlots, resources = p.Presenter.ResourceCount,
        tileRequestToReadyMs = p.TileTotalMs.Stat(), thumbRequestToReadyMs = p.ThumbTotalMs.Stat(), requestTransitMs = p.ReqTransitMs.Stat(), engineQueueMs = p.EngQueueMs.Stat(), engineLoadPageMs = p.EngLoadMs.Stat(), engineRenderMs = p.EngRenderMs.Stat(),
        responseTransitMs = p.RespTransitMs.Stat(), slotCopyMs = p.CopyMs.Stat(), uploadTileMs = p.UploadMs.Stat(), uploadThumbMs = p.UploadThumbMs.Stat(), uiQueueMs = p.UiQueueMs.Stat(), presenterMs = p.Presenter.UpdateMs.Stat(),
    };

    static void DumpFrames(Runner r)
    {
        var L = r.Log; var lines = new List<string> { "t_ms,interval_ms,work_ms,sharp_frac,blank_px,lowres_px,blank_cells,lowres_cells,visible_cells,inflight,scroll_y,draws" };
        for (int i = 0; i < L.T.Count; i++) lines.Add($"{Csv.F(L.T[i])},{Csv.F(L.Interval[i])},{Csv.F(L.Work[i])},{Csv.F(L.Sharp[i])},{Csv.F(L.Blank[i])},{Csv.F(L.LowRes[i])},{L.BlankCells[i]},{L.LowResCells[i]},{L.VisibleCells[i]},{L.InFlight[i]},{Csv.F(L.ScrollY[i])},{L.Draws[i]}");
        Csv.Write(Path.Combine(r.OutDir, "frames.csv"), lines);
    }

    // ------------------------------------------------------------------------------------------------ S2.5 fling
    public static async Task Fling(Runner r, Options o, MemSampler mem)
    {
        int z = o.Int("zoom", 100); double v0 = o.Dbl("v0", 15000), hold = o.Dbl("hold", 1.5), tau = o.Dbl("tau", 2.5), dur = o.Dbl("dur", 10), startY = o.Dbl("starty", 0);
        var L = r.P.Layout(z);
        var st = new SettleScenario(z, startY); await r.Run(st);
        r.Note("initialSettleMs", st.SettleMs);
        if (o.Flag("warmup", true))
        {   // JIT + driver warm-up: fling a different part of the document, then drop every cached tile
            double wy = Math.Min(L.Total * 0.4, Math.Max(0, L.Total - r.VH - 60000));
            await r.Run(new SettleScenario(z, wy, timeoutMs: 6000));
            await r.Run(new FlingScenario(z, wy, v0, hold, tau, 4.0, record: false));
            r.P.Paused = true; await r.Sleep(400); r.P.FreeAll(); r.P.ResetStats(); await r.Sleep(100); r.P.Paused = false;
            await r.Run(new SettleScenario(z, startY, timeoutMs: 8000));
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); await r.Sleep(500);
        mem.Sample();
        r.P.ResetStats();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2); var gcPause0 = GC.GetTotalPauseDuration();
        Sys.CpuBegin();
        var memBefore = new { ws = Sys.ProcessMem(), gpu = Dxgi.Query() };
        var dwm0 = Dwm.Get(r.Hwnd);
        var fs = new FlingScenario(z, startY, v0, hold, tau, dur);
        await r.Run(fs);
        var dwm1 = Dwm.Get(r.Hwnd);
        double sysCpu = Sys.CpuPercentSinceBegin();
        var gcPause = GC.GetTotalPauseDuration() - gcPause0;
        mem.Sample();
        double vsync = Sys.PrimaryRefreshHz() > 0 ? 1000.0 / Sys.PrimaryRefreshHz() : 16.7;
        var ivSorted = r.Log.Interval.OrderBy(x => x).ToArray(); double medIv = ivSorted.Length > 0 ? ivSorted[ivSorted.Length / 2] : 0;
        r.Note("fling", new { z, v0, hold, tau, dur, startY, endY = fs.EndY, distancePx = fs.EndY - startY, docTotalPx = L.Total, pages = L.N, sharpAfterEndMs = fs.SharpAfterMs, sysCpuPercentDuring = sysCpu, observedMedianFrameIntervalMs = medIv });
        r.Note("frames", FrameStats(r.Log, vsync, medIv));
        r.Note("pipeline", PipelineStats(r.P));
        r.Note("dwm", new { before = dwm0, after = dwm1, lateDelta = (long)dwm1.Late - (long)dwm0.Late, droppedDelta = (long)dwm1.Dropped - (long)dwm0.Dropped, missedDelta = (long)dwm1.Missed - (long)dwm0.Missed, displayedDelta = (long)dwm1.Displayed - (long)dwm0.Displayed, refreshDelta = (long)dwm1.Refreshes - (long)dwm0.Refreshes });
        r.Note("gcDuringFling", new { gen0 = GC.CollectionCount(0) - g0, gen1 = GC.CollectionCount(1) - g1, gen2 = GC.CollectionCount(2) - g2, pauseMs = gcPause.TotalMilliseconds });
        r.Note("memAroundFling", new { before = memBefore, afterWsPriv = Sys.ProcessMem(), afterGpu = Dxgi.Query() });
        DumpFrames(r);
    }

    // ------------------------------------------------------------------------------------------------ S2.5 zoom
    public static async Task Zoom(Runner r, Options o)
    {
        int cycles = o.Int("cycles", 4); int page = Math.Min(o.Int("page", 10), r.P.Pages - 1);
        var steps = new List<(int a, int b)> { (100, 200), (200, 400), (400, 200), (200, 100) };
        var L100 = r.P.Layout(100);
        double y = L100.Top[page] + 200;
        var rows = new List<object>(); var settle = new Samples(); var deb = new Samples(); var first = new Samples(); var gestIv = new Samples();
        var gestWork = new List<double>();
        // warm-up transition (JIT)
        for (int cyc = -1; cyc < cycles; cyc++)
            foreach (var (a, b) in steps)
            {
                r.P.Paused = true; await r.Sleep(80); r.P.FreeAll(); r.P.Paused = false;
                await r.Run(new SettleScenario(a, y, timeoutMs: 6000));
                var zs = new ZoomScenario(a, b, y, gestureFrames: o.Int("gframes", 12), debounceMs: o.Int("debounce", 120));
                r.P.ResetStats();
                await r.Run(zs);
                // anchored position for the next step
                var Lb = r.P.Layout(b); double yb = (y + r.VH / 2) * ((double)b / a) - r.VH / 2; y = Math.Max(0, Math.Min(yb, Lb.Total - r.VH));
                if (cyc < 0) continue;
                if (zs.TimedOut) { rows.Add(new { a, b, timedOut = true }); continue; }
                settle.Add(zs.SettleToSharpMs); deb.Add(zs.DebounceToSharpMs); first.Add(zs.GestureStartToFirstFrameMs); foreach (var v in zs.GestureFrameIntervals) gestIv.Add(v);
                rows.Add(new { a, b, debounceToSharpMs = zs.DebounceToSharpMs, gestureEndToSharpMs = zs.SettleToSharpMs, firstGestureFrameMs = zs.GestureStartToFirstFrameMs, gestureFrameIntervalMaxMs = zs.GestureFrameIntervals.Count == 0 ? 0 : zs.GestureFrameIntervals.Max(), waitFrameIntervalMaxMs = zs.WaitFrameIntervals.Count == 0 ? 0 : zs.WaitFrameIntervals.Max(), tilesReady = r.P.TilesReady, thumbsReady = r.P.ThumbsReady });
            }
        r.Note("zoom", new { page, debounceMs = o.Int("debounce", 120), gestureFrames = o.Int("gframes", 12), transitions = rows.Count, debounceExpiryToAllSharpMs = deb.Stat(), gestureEndToAllSharpMs = settle.Stat(), firstGestureFrameMs = first.Stat(), gestureFrameIntervalMs = gestIv.Stat(), rows });
    }

    // ------------------------------------------------------------------------------------------------ S2.1 TTFT end to end
    public static async Task Ttft(Runner r, Options o)
    {
        int iters = o.Int("iters", 30); int z = o.Int("zoom", 100); int startPage = o.Int("startpage", 5);
        var open = new Samples(); var sizes = new Samples(); var thumbR = new Samples(); var thumbC = new Samples(); var sharpR = new Samples(); var sharpC = new Samples(); var allR = new Samples(); var allC = new Samples();
        var rows = new List<object>();
        int warm = o.Flag("cold", false) ? 0 : o.Int("warmiters", 3); r.P.Paused = true;
        for (int it = -warm; it < iters; it++)
        {
            long t0 = Clock2.Now();
            var (pages, openMs) = r.Engine.Open(r.DocPath);
            long t1 = Clock2.Now();
            r.P.Sizes = r.Engine.PageSizes(); r.P.ClearLayouts();
            long t2 = Clock2.Now();
            var L = r.P.Layout(z);
            int pg = Math.Min(startPage, pages - 1);
            double y = pg == 0 ? 0 : Math.Max(0, L.Top[pg] - L.Gap);
            var sc = new TtftScenario(z, y, t0);
            r.P.ResetStats(); r.P.Paused = false;
            await r.Run(sc);
            if (it >= 0)
            {
                open.Add(Clock2.Ms(t1 - t0)); sizes.Add(Clock2.Ms(t2 - t1)); thumbR.Add(sc.TThumbReady); thumbC.Add(sc.TThumbComposed); sharpR.Add(sc.TFirstSharpReady); sharpC.Add(sc.TFirstSharpComposed); allR.Add(sc.TAllReady); allC.Add(sc.TAllComposed);
                rows.Add(new { it, pages, openRpcMs = Clock2.Ms(t1 - t0), pageSizesMs = Clock2.Ms(t2 - t1), thumbReadyMs = sc.TThumbReady, thumbSubmittedMs = sc.TThumbSubmitted, thumbComposedMs = sc.TThumbComposed, firstSharpReadyMs = sc.TFirstSharpReady, firstSharpSubmittedMs = sc.TFirstSharpSubmitted, firstSharpComposedMs = sc.TFirstSharpComposed, allSharpReadyMs = sc.TAllReady, allSharpSubmittedMs = sc.TAllSubmitted, allSharpComposedMs = sc.TAllComposed });
            }
            r.P.Paused = true; await r.Sleep(120); r.Engine.Call(Msg.CloseDocument, null); r.P.FreeAll(); await r.Sleep(230);   // scheduler stays paused until the next document is open
        }
        r.Note("ttft", new { z, startPage, iters, cold = o.Flag("cold", false), openRpcMs = open.Stat(), pageSizesMs = sizes.Stat(), openToThumbReadyMs = thumbR.Stat(), openToThumbComposedMs = thumbC.Stat(), openToFirstSharpReadyMs = sharpR.Stat(), openToFirstSharpComposedMs = sharpC.Stat(), openToAllSharpReadyMs = allR.Stat(), openToAllSharpComposedMs = allC.Stat(), rows });
        r.Note("pipelineLastIter", PipelineStats(r.P));
    }

    // ------------------------------------------------------------------------------------------------ S2.3/S2.4 upload cost + memory per tile
    public static async Task Upload(Runner r, Options o, List<Dxgi.MemInfo> baseline)
    {
        int T = o.Int("tile", 512); int N = o.Int("n", 200); int pageStart = o.Int("startpage", 1);
        var dev = (r.Presenter as Win2DPresenter)?.Device ?? (r.Presenter as CompPresenter)!.Device;
        var res = await Task.Run(() =>
        {
            var L = r.P.Layout(100);
            var copy = new Samples(); var up = new Samples(); var upSync = new Samples(); var render = new Samples(); var total = new Samples();
            var kept = new List<TileEntry>();
            (int page, int tx, int ty) Pick(int i)
            {
                int page = pageStart + (i % Math.Max(1, Math.Min(30, r.P.Pages - pageStart))); int cols = L.Cols(page, T), rows = L.Rows(page, T);
                int tx = (cols / 4 + i / 7) % cols, ty = (rows / 6 + i / 3) % rows; return (page, tx, ty);
            }
            void FreeOnUi(TileEntry e) => r.OnUiSync(() => r.Presenter.FreeResource(e));
            // one tile through engine + upload
            unsafe double[] One(int i, bool keep)
            {
                var (page, tx, ty) = Pick(i);
                int slot = r.Engine.AcquireSlot(); if (slot < 0) throw new Exception("no slot");
                int w = (int)Math.Min(T, L.W[page] - tx * T), h = (int)Math.Min(T, L.H[page] - ty * T);
                using var done = new ManualResetEventSlim(); RenderDone d = default;
                long t0 = Clock2.Now();
                r.Engine.SubmitRender(page, (float)L.Scale, tx * T, ty * T, w, h, slot, 0, 1, 0, x => { d = x; done.Set(); });
                done.Wait(10000);
                double engMs = Clock2.Ms(d.TUiRecv - t0);
                long u0 = Clock2.Now();
                object gpu = r.Presenter.Upload(r.Engine.SlotPtr(slot), w, h, false, out double cp, out double upl);
                long u1 = Clock2.Now();
                if (!o.Flag("nosync", false)) GpuSync.Sync(dev);
                long u2 = Clock2.Now();
                r.Engine.ReleaseSlot(slot);
                var e = new TileEntry { Gpu = gpu, W = w, H = h };
                if (keep) kept.Add(e); else FreeOnUi(e);
                return [engMs, cp, upl, Clock2.Ms(u1 - u0), Clock2.Ms(u2 - u0)];
            }
            for (int i = 0; i < 30; i++) { try { One(i + 1000, false); } catch (Exception ex) { throw new Exception($"warm-up iteration {i} failed", ex); } }     // warm-up (JIT, device, driver)
            GC.Collect(); Thread.Sleep(300);
            var memBefore = new { ws = Sys.ProcessMem(), gpu = Dxgi.Query() };
            for (int i = 0; i < N; i++) { var m = One(i, true); render.Add(m[0]); copy.Add(m[1]); up.Add(m[3]); upSync.Add(m[4]); }
            if (r.Presenter is CompPresenter cpx) { r.OnUiSync(() => cpx.AttachForTest(kept)); Thread.Sleep(1500); }   // composition surfaces are only fully realised once they are in the tree
            Thread.Sleep(500);
            var memAfter = new { ws = Sys.ProcessMem(), gpu = Dxgi.Query() };
            long bytes = 0; foreach (var e in kept) bytes += (long)e.W * e.H * 4;
            foreach (var e in kept) FreeOnUi(e);
            kept.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); dev.Trim(); GpuSync.Sync(dev); Thread.Sleep(800);
            var memFreed = new { ws = Sys.ProcessMem(), gpu = Dxgi.Query() };
            // per-adapter deltas
            var perAdapter = memAfter.gpu.Select(a => { var b = memBefore.gpu.First(x => x.Index == a.Index); var f = memFreed.gpu.First(x => x.Index == a.Index); return new { a.Name, localDeltaMB = a.LocalUsageMB - b.LocalUsageMB, nonLocalDeltaMB = a.NonLocalUsageMB - b.NonLocalUsageMB, localBytesPerTile = (a.LocalUsageMB - b.LocalUsageMB) * 1048576.0 / N, nonLocalBytesPerTile = (a.NonLocalUsageMB - b.NonLocalUsageMB) * 1048576.0 / N, localAfterFreeDeltaMB = f.LocalUsageMB - b.LocalUsageMB, nonLocalAfterFreeDeltaMB = f.NonLocalUsageMB - b.NonLocalUsageMB }; }).ToList();
            return new
            {
                tile = T, n = N, nominalBytesPerTile = bytes / (double)N,
                engineRequestToSlotReadyMs = render.Stat(), slotToManagedCopyMs = copy.Stat(), uploadCallMs = up.Stat(), uploadPlusGpuSyncMs = upSync.Stat(),
                cpuBytesPerTile = new { workingSetDeltaMB = memAfter.ws.ws - memBefore.ws.ws, privateDeltaMB = memAfter.ws.priv - memBefore.ws.priv, privatePerTileBytes = (memAfter.ws.priv - memBefore.ws.priv) * 1048576.0 / N, workingSetPerTileBytes = (memAfter.ws.ws - memBefore.ws.ws) * 1048576.0 / N, privateAfterFreeDeltaMB = memFreed.ws.priv - memBefore.ws.priv },
                perAdapter,
            };
        });
        r.Note("upload", res);
        await Task.CompletedTask;
    }

    public static async Task Idle(Runner r, Options o, MemSampler mem)
    {
        int z = o.Int("zoom", 100); var st = new SettleScenario(z, 0); await r.Run(st);
        await r.Sleep(o.Int("idlems", 3000)); mem.Sample();
        r.Note("idle", new { settleMs = st.SettleMs, ws = Sys.ProcessMem() });
    }
}
