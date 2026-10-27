using System.Collections.Concurrent;
using Quire.S2;

namespace Quire.S2.Viewer;

public interface IPresenter : IDisposable
{
    string Name { get; }
    Microsoft.UI.Xaml.FrameworkElement Element { get; }
    /// <summary>Create the GPU resource for one BGRA tile. May run on the uploader thread (default) or on the UI thread.</summary>
    unsafe object Upload(byte* bgra, int w, int h, bool thumb, out double copyMs, out double uploadMs);
    /// <summary>UI thread: release the GPU resource (and detach its visual).</summary>
    void FreeResource(TileEntry e);
    /// <summary>UI thread, once per frame, after the frame model was built for the displayed layout.</summary>
    void Update(FrameModel m);
    Samples UpdateMs { get; }
    /// <summary>Number of frames the presenter actually composed since the last call (A: RegionsInvalidated draws).</summary>
    int DrawCount { get; }
    long ResourceCount { get; }
}

/// <summary>
/// Tile cache + scheduler + engine plumbing. Threads: UI (Publish, BuildFrame, Evict), scheduler (plans and submits to the engine),
/// engine reader (completions), uploader (GPU upload; optional, otherwise the UI thread drains completions with a per-frame budget).
/// </summary>
public sealed unsafe class Pipeline : IDisposable
{
    public readonly EngineClient Engine;
    public readonly IPresenter Presenter;
    public readonly TileStore Store = new();
    public readonly Options Opt;
    public readonly int Tile;
    public readonly double RasterScale;
    public readonly int Gap;
    public readonly int ThumbW;
    public (float w, float h)[] Sizes = [];
    public int Pages => Sizes.Length;

    readonly Dictionary<int, DocLayout> _layouts = new();
    volatile ViewState? _target;
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEventSlim _uplWake = new(false);
    readonly ConcurrentQueue<(TileEntry e, RenderDone d)> _completions = new();
    readonly HashSet<TileKey> _inflightKeys = new();
    int _inflight;
    uint _gen = 1;
    long _lastCancelTick;
    volatile bool _stop;
    public volatile bool Paused;
    Thread? _sched, _upl;
    readonly int _cap, _inflightCap;
    readonly bool _uiUpload, _lowres;
    readonly double _prefetchScreens;
    public long CacheBudgetBytes;

    // measurements (all QPC-derived, ms)
    public readonly Samples TileTotalMs = new(), ThumbTotalMs = new(), ReqTransitMs = new(), EngQueueMs = new(), EngLoadMs = new(), EngRenderMs = new(), RespTransitMs = new(), CopyMs = new(), UploadMs = new(), UploadThumbMs = new(), UiQueueMs = new();
    public long TilesReady, ThumbsReady, Cancelled, Errors, Evicted, Requests, CancelCalls;
    public int MaxInflightSeen, MinFreeSlots = 99;

    public Pipeline(EngineClient engine, IPresenter presenter, Options opt, double rasterScale)
    {
        Engine = engine; Presenter = presenter; Opt = opt; RasterScale = rasterScale;
        Tile = opt.Int("tile", 512); Gap = (int)Math.Round(8 * rasterScale); ThumbW = opt.Int("thumbw", 128);
        _inflightCap = opt.Int("inflight", 3); _uiUpload = opt.Str("upload", "thread") == "ui"; _lowres = opt.Flag("lowres", true);
        _prefetchScreens = opt.Dbl("prefetch", 1.0); _cap = opt.Int("maxwanted", 400);
        CacheBudgetBytes = (long)opt.Int("cachemb", 256) * 1024 * 1024;
    }

    public DocLayout Layout(int z)
    {
        lock (_layouts)
        {
            if (!_layouts.TryGetValue(z, out var l)) _layouts[z] = l = new DocLayout(z, Sizes, z / 100.0 * (96.0 / 72.0) * RasterScale, Gap);
            return l;
        }
    }

    public void ClearLayouts() { lock (_layouts) _layouts.Clear(); }

    public void Start()
    {
        _sched = new Thread(SchedulerLoop) { IsBackground = true, Name = "tile-scheduler", Priority = ThreadPriority.AboveNormal };
        _sched.Start();
        if (!_uiUpload) { _upl = new Thread(UploaderLoop) { IsBackground = true, Name = "tile-uploader" }; _upl.Start(); }
    }

    public void Publish(ViewState v) { _target = v.Clone(); _wake.Set(); }

    // ------------------------------------------------------------------------------------------------ frame model (UI thread)
    /// <summary>Builds the draw list and the blank-area probe for the displayed layout. <paramref name="margin"/> extends the list beyond the viewport (presenter B keeps those sprites attached).</summary>
    public void BuildFrame(ViewState v, FrameModel m, double margin)
    {
        m.Reset(); m.Frame = v.Frame; m.ScrollY = v.ScrollYDisp; m.ScrollX = v.ScrollXDisp; m.Gesture = v.Gesture; m.VW = v.VW; m.VH = v.VH;
        var L = Layout(v.ZDisp);
        float g = v.Gesture <= 0 ? 1 : v.Gesture;
        double cx = v.VW / 2, cy = v.VH / 2;
        // visible region in layout screen coordinates (gesture scales about the viewport centre)
        double vx0 = cx - cx / g, vx1 = cx + cx / g, vy0 = cy - cy / g, vy1 = cy + cy / g;
        double docY0 = v.ScrollYDisp + vy0 - margin, docY1 = v.ScrollYDisp + vy1 + margin;
        int first = L.PageAt(Math.Max(0, docY0));
        for (int i = first; i < L.N && L.Top[i] < docY1; i++)
        {
            double px = L.PageX(i, v.VW, v.ScrollXDisp), py = L.Top[i] - v.ScrollYDisp;
            var pi = m.NewPage(); pi.Page = i; pi.X = (float)px; pi.Y = (float)py; pi.W = (float)L.W[i]; pi.H = (float)L.H[i];
            bool pageVisible = py + L.H[i] > vy0 && py < vy1 && px + L.W[i] > vx0 && px < vx1;
            pi.VisiblePage = pageVisible;
            if (pageVisible) m.VisiblePages++;
            if (Store.TryGet(TileKey.Thumb(i), out var th) && th.State == 2) { th.LastUsed = v.Frame; th.ModelStamp = v.Frame; pi.Thumb = th; }
            int cols = L.Cols(i, Tile), rows = L.Rows(i, Tile);
            int tx0 = Math.Max(0, (int)Math.Floor((vx0 - margin - px) / Tile)), tx1 = Math.Min(cols - 1, (int)Math.Floor((vx1 + margin - px) / Tile));
            int ty0 = Math.Max(0, (int)Math.Floor((vy0 - margin - py) / Tile)), ty1 = Math.Min(rows - 1, (int)Math.Floor((vy1 + margin - py) / Tile));
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    double tw = Math.Min(Tile, L.W[i] - tx * Tile), th2 = Math.Min(Tile, L.H[i] - ty * Tile);
                    double sx = px + tx * Tile, sy = py + ty * Tile;
                    bool vis = sx + tw > vx0 && sx < vx1 && sy + th2 > vy0 && sy < vy1;
                    bool ready = Store.TryGet(new TileKey(L.Z, i, tx, ty), out var e) && e.State == 2;
                    if (ready) { e!.LastUsed = v.Frame; e.ModelStamp = v.Frame; var ti = m.NewTile(); ti.E = e; ti.X = (float)sx; ti.Y = (float)sy; ti.W = (float)tw; ti.H = (float)th2; ti.Visible = vis; pi.Tiles.Add(ti); }
                    if (vis)
                    {
                        double ax = Math.Min(sx + tw, vx1) - Math.Max(sx, vx0), ay = Math.Min(sy + th2, vy1) - Math.Max(sy, vy0);
                        double area = Math.Max(0, ax) * Math.Max(0, ay);
                        m.VisibleCells++; if (!ready) pi.MissingCells++;
                        if (ready) { m.SharpPx += area; m.SharpCells++; }
                        else if (pi.Thumb != null) { m.LowResPx += area; m.LowResCells++; }
                        else { m.BlankPx += area; m.BlankCells++; }
                    }
                }
        }
    }

    /// <summary>True when every visible cell of the target layout has a sharp tile (used to swap the display zoom after a gesture settles, and for time-to-sharp).</summary>
    public bool AllVisibleSharp(int z, double scrollY, double scrollX, double vw, double vh, out int missing)
    {
        var L = Layout(z); missing = 0;
        int first = L.PageAt(Math.Max(0, scrollY));
        for (int i = first; i < L.N && L.Top[i] < scrollY + vh; i++)
        {
            double px = L.PageX(i, vw, scrollX), py = L.Top[i] - scrollY;
            int cols = L.Cols(i, Tile), rows = L.Rows(i, Tile);
            for (int ty = Math.Max(0, (int)Math.Floor(-py / Tile)); ty < rows && py + ty * Tile < vh; ty++)
                for (int tx = Math.Max(0, (int)Math.Floor(-px / Tile)); tx < cols && px + tx * Tile < vw; tx++)
                    if (!(Store.TryGet(new TileKey(z, i, tx, ty), out var e) && e.State == 2)) missing++;
        }
        return missing == 0;
    }

    // ------------------------------------------------------------------------------------------------ planner (scheduler thread)
    List<TileKey> Plan(ViewState v)
    {
        var L = Layout(v.ZTgt);
        var res = new List<TileKey>(128);
        double vw = v.VW, vh = v.VH, y0 = v.ScrollYTgt, y1 = y0 + vh;
        double vel = v.VelPxPerSec; bool fast = Math.Abs(vel) > vh * 2.5;               // > 2.5 screens per second: no tile prefetch beyond the viewport
        int dir = vel > 40 ? 1 : vel < -40 ? -1 : 0;
        int firstVis = L.PageAt(Math.Max(0, y0)), lastVis = L.PageAt(Math.Max(0, y1));
        var thumbs = new List<(double d, TileKey k)>(); var vis = new List<(double d, TileKey k)>(); var pre = new List<(double d, TileKey k)>(); var ahead = new List<(double d, TileKey k)>();
        double cy = y0 + vh / 2;

        void AddThumb(List<(double, TileKey)> into, int p) { if (p < 0 || p >= L.N) return; var k = TileKey.Thumb(p); if (!Store.TryGet(k, out var e) || e.State == 0) into.Add((Math.Abs(L.Top[p] + L.H[p] / 2 - cy), k)); else e.LastUsed = v.Frame; }
        void AddTiles(List<(double, TileKey)> into, double ya, double yb, bool markOnly)
        {
            int pf = L.PageAt(Math.Max(0, ya));
            for (int i = pf; i < L.N && L.Top[i] < yb; i++)
            {
                double px = L.PageX(i, vw, v.ScrollXTgt), py = L.Top[i];
                int cols = L.Cols(i, Tile), rows = L.Rows(i, Tile);
                int ty0 = Math.Max(0, (int)Math.Floor((ya - py) / Tile)), ty1 = Math.Min(rows - 1, (int)Math.Floor((yb - py) / Tile));
                int tx0 = Math.Max(0, (int)Math.Floor(-px / Tile)), tx1 = Math.Min(cols - 1, (int)Math.Floor((vw - px) / Tile));
                for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        var k = new TileKey(L.Z, i, tx, ty);
                        if (Store.TryGet(k, out var e) && e.State != 0) { e.LastUsed = v.Frame; continue; }
                        double d = Math.Abs(py + ty * Tile + Tile / 2.0 - cy);
                        into.Add((d, k));
                    }
            }
        }
        // 1. thumbs for visible pages, 2. visible tiles, 3. thumbs ahead, 4. prefetch tiles
        for (int p = firstVis; p <= lastVis; p++) AddThumb(thumbs, p);
        AddTiles(vis, y0, y1, false);
        if (_lowres)
        {
            double aheadPx = Math.Min(Math.Max(vh, Math.Abs(vel) * 0.6), vh * 8);
            double behindPx = vh;
            double ta0 = y0 - (dir <= 0 ? aheadPx : behindPx), ta1 = y1 + (dir >= 0 ? aheadPx : behindPx);
            int pa = L.PageAt(Math.Max(0, ta0)), pb = L.PageAt(Math.Max(0, ta1));
            for (int p = pa; p <= pb && p < L.N; p++) if (p < firstVis || p > lastVis) AddThumb(ahead, p);
            // direction-first: nearest page in the direction of travel first
            ahead.Sort((a, b) => a.d.CompareTo(b.d));
        }
        if (!fast && _prefetchScreens > 0)
        {
            double up = (dir >= 0 ? 0.5 : _prefetchScreens) * vh, down = (dir <= 0 ? 0.5 : _prefetchScreens) * vh;
            if (dir == 0) { up = down = _prefetchScreens * vh; }
            AddTiles(pre, y0 - up, y1 + down, false);
            pre.RemoveAll(t => { var L2 = L; double top = L2.Top[t.k.Page] + t.k.Ty * Tile; return top + Tile > y0 && top < y1; });   // visible ones were handled by group 2
        }
        thumbs.Sort((a, b) => a.d.CompareTo(b.d)); vis.Sort((a, b) => a.d.CompareTo(b.d)); pre.Sort((a, b) => a.d.CompareTo(b.d));
        // visible tiles that are not yet cached were added by AddTiles(vis...) including partially visible rows: fine.
        foreach (var t in thumbs) res.Add(t.k);
        foreach (var t in vis) res.Add(t.k);
        foreach (var t in ahead) res.Add(t.k);
        foreach (var t in pre) res.Add(t.k);
        if (res.Count > _cap) res.RemoveRange(_cap, res.Count - _cap);
        return res;
    }

    void SchedulerLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne(4);
            var v = _target; if (v == null || Pages == 0 || Paused) continue;
            try { ScheduleOnce(v); } catch (Exception ex) { Errors++; LastError = ex.ToString(); }
        }
    }
    public string LastError = "";

    void ScheduleOnce(ViewState v)
    {
        var want = Plan(v);
        // obsolete in-flight work? (requested but no longer wanted) -> cancel everything older than a new generation, then re-request what is still wanted
        bool obsolete = false;
        lock (_inflightKeys)
        {
            if (_inflightKeys.Count > 0)
            {
                var ws = new HashSet<TileKey>(want);
                foreach (var k in _inflightKeys) if (!ws.Contains(k) && !(Store.TryGet(k, out var e) && e.LastUsed >= v.Frame - 1)) { obsolete = true; break; }
            }
        }
        if (obsolete && Clock2.MsSince(_lastCancelTick) > 6)
        {
            _lastCancelTick = Clock2.Now(); _gen++; Engine.CancelBefore(_gen); Interlocked.Increment(ref CancelCalls);
        }
        foreach (var k in want)
        {
            if (Volatile.Read(ref _inflight) >= _inflightCap) break;
            var e = Store.GetOrAdd(k);
            if (e.State != 0) continue;
            int slot = Engine.AcquireSlot();
            if (slot < 0) break;
            MinFreeSlots = Math.Min(MinFreeSlots, Engine.FreeSlots);
            var L = Layout(k.IsThumb ? 100 : k.Z);
            int page = k.Page; float scale; int ox, oy, w, h, flags;
            if (k.IsThumb)
            {
                var Lc = Layout(100);   // page sizes in pt come from any layout
                scale = (float)(ThumbW / Lc.WPt[page]);
                w = ThumbW; h = Math.Min(512, (int)Math.Ceiling(Lc.HPt[page] * scale)); ox = oy = 0;
                var Lt = Layout(v.ZTgt); bool visiblePage = page >= Lt.PageAt(Math.Max(0, v.ScrollYTgt)) && page <= Lt.PageAt(Math.Max(0, v.ScrollYTgt + v.VH));
                flags = visiblePage ? 0 : 0x100;   // keep the parsed page for visible pages (the sharp tiles of the same page reuse it); do not cache prefetch pages
                if (w > 512) w = 512;
            }
            else
            {
                scale = (float)L.Scale; ox = k.Tx * Tile; oy = k.Ty * Tile;
                w = (int)Math.Min(Tile, L.W[page] - ox); h = (int)Math.Min(Tile, L.H[page] - oy); flags = 0;
            }
            e.W = w; e.H = h; e.Gen = _gen; e.RequestedAt = Clock2.Now(); e.State = 1;
            lock (_inflightKeys) _inflightKeys.Add(k);
            int n = Interlocked.Increment(ref _inflight); if (n > MaxInflightSeen) MaxInflightSeen = n;
            Interlocked.Increment(ref Requests);
            try { Engine.SubmitRender(page, scale, ox, oy, w, h, slot, flags, _gen, 0, d => OnDone(e, d)); }
            catch { e.State = 0; Engine.ReleaseSlot(slot); Interlocked.Decrement(ref _inflight); lock (_inflightKeys) _inflightKeys.Remove(k); throw; }
        }
    }

    void OnDone(TileEntry e, RenderDone d)
    {
        Interlocked.Decrement(ref _inflight);
        lock (_inflightKeys) _inflightKeys.Remove(e.Key);
        _completions.Enqueue((e, d));
        _uplWake.Set(); _wake.Set();
    }

    // ------------------------------------------------------------------------------------------------ upload
    void UploaderLoop()
    {
        while (!_stop)
        {
            _uplWake.Wait(20); _uplWake.Reset();
            while (_completions.TryDequeue(out var c)) { try { Process(c.e, c.d); } catch (Exception ex) { Errors++; LastError = ex.ToString(); c.e.State = 0; Engine.ReleaseSlot(c.d.Slot); } }
        }
    }

    /// <summary>UI-thread upload mode: drain with a time budget per frame.</summary>
    public int DrainOnUi(double budgetMs)
    {
        long t0 = Clock2.Now(); int n = 0;
        while (Clock2.MsSince(t0) < budgetMs && _completions.TryDequeue(out var c)) { Process(c.e, c.d); n++; }
        return n;
    }

    void Process(TileEntry e, RenderDone d)
    {
        if (d.Status != 0)
        {
            if (d.Status == 1) Interlocked.Increment(ref Cancelled); else { Interlocked.Increment(ref Errors); LastError = $"engine status {d.Status} for {e.Key}"; }
            if (d.Slot >= 0) Engine.ReleaseSlot(d.Slot);
            e.State = 0; _wake.Set(); return;
        }
        long tProc = Clock2.Now();
        UiQueueMs.Add(Clock2.Ms(tProc - d.TUiRecv));
        ReqTransitMs.Add(Clock2.Ms(d.TEngRecv - d.TSubmit)); EngQueueMs.Add(Clock2.Ms(d.TEngDeq - d.TEngRecv)); EngLoadMs.Add(Clock2.Ms(d.TLoaded - d.TEngDeq));
        EngRenderMs.Add(Clock2.Ms(d.TRendered - d.TLoaded)); RespTransitMs.Add(Clock2.Ms(d.TUiRecv - d.TEngSend));
        object gpu = Presenter.Upload(Engine.SlotPtr(d.Slot), e.W, e.H, e.Key.IsThumb, out double copy, out double up);
        Engine.ReleaseSlot(d.Slot);
        CopyMs.Add(copy); (e.Key.IsThumb ? UploadThumbMs : UploadMs).Add(up);
        e.Gpu = gpu; e.Bytes = (long)e.W * e.H * 4; Store.AddBytes(e.Bytes);
        e.ReadyAt = Clock2.Now();
        (e.Key.IsThumb ? ThumbTotalMs : TileTotalMs).Add(Clock2.Ms(e.ReadyAt - e.RequestedAt));
        if (e.Key.IsThumb) Interlocked.Increment(ref ThumbsReady); else Interlocked.Increment(ref TilesReady);
        e.State = 2;
        _wake.Set();
    }

    // ------------------------------------------------------------------------------------------------ eviction (UI thread)
    public void EvictIfNeeded(long frame)
    {
        if (Store.GpuBytes <= CacheBudgetBytes) return;
        long target = (long)(CacheBudgetBytes * 0.9);
        var cands = new List<TileEntry>();
        foreach (var kv in Store.D) { var e = kv.Value; if (e.State == 2 && e.LastUsed < frame - 8) cands.Add(e); }
        cands.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        foreach (var e in cands)
        {
            if (Store.GpuBytes <= target) break;
            Presenter.FreeResource(e); Store.AddBytes(-e.Bytes);
            e.State = 0; e.Gpu = null; e.Bytes = 0; Store.D.TryRemove(e.Key, out _); Evicted++;
        }
    }

    public void ResetStats()
    {
        foreach (var s in new[] { TileTotalMs, ThumbTotalMs, ReqTransitMs, EngQueueMs, EngLoadMs, EngRenderMs, RespTransitMs, CopyMs, UploadMs, UploadThumbMs, UiQueueMs, Presenter.UpdateMs }) s.Clear();
        TilesReady = ThumbsReady = Cancelled = Errors = Evicted = Requests = CancelCalls = 0; MaxInflightSeen = 0; MinFreeSlots = 99;
    }

    public void FreeAll()
    {
        foreach (var kv in Store.D) { var e = kv.Value; if (e.State == 2) { Presenter.FreeResource(e); Store.AddBytes(-e.Bytes); e.State = 0; e.Gpu = null; } }
        Store.D.Clear();
    }

    public int InFlight => Volatile.Read(ref _inflight);

    public void Dispose() { _stop = true; _wake.Set(); _uplWake.Set(); _sched?.Join(500); _upl?.Join(500); }
}
