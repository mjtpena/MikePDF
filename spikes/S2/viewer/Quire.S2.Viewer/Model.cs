using System.Collections.Concurrent;

namespace Quire.S2.Viewer;

/// <summary>Tile identity. Thumbnails (low-resolution fallback) use Z = 0, Tx = Ty = -1.</summary>
public readonly record struct TileKey(int Z, int Page, int Tx, int Ty)
{
    public bool IsThumb => Tx < 0;
    public static TileKey Thumb(int page) => new(0, page, -1, -1);
}

public sealed class TileEntry
{
    public TileKey Key;
    public volatile int State;              // 0 none/evicted, 1 requested, 2 ready (GPU resource attached)
    public int W, H;                        // pixels
    public object? Gpu;                     // presenter-specific resource
    public long Bytes;                      // nominal GPU bytes (w*h*4); real usage is measured separately through DXGI
    public long LastUsed;                   // frame number of the last time the planner or the compositor wanted this tile
    public long RequestedAt, ReadyAt;       // QPC
    public uint Gen;
    public bool Attached;                   // presenter B: sprite is in the visual tree
    public long ModelStamp;                 // last frame whose FrameModel included this entry (presenter B detaches entries whose stamp is old)
}

/// <summary>Page positions at one zoom: document coordinates in device pixels.</summary>
public sealed class DocLayout
{
    public readonly int Z;                 // zoom percent (id)
    public readonly double Scale;          // device px per PDF point
    public readonly int N;
    public readonly double[] Top, W, H, WPt, HPt;
    public readonly double Total, MaxW;
    public readonly int Gap;

    public DocLayout(int z, (float w, float h)[] sizes, double scale, int gap)
    {
        Z = z; Scale = scale; N = sizes.Length; Gap = gap;
        Top = new double[N]; W = new double[N]; H = new double[N]; WPt = new double[N]; HPt = new double[N];
        double y = gap;
        for (int i = 0; i < N; i++)
        {
            WPt[i] = sizes[i].w; HPt[i] = sizes[i].h;
            W[i] = MathF.Ceiling(sizes[i].w * (float)scale); H[i] = MathF.Ceiling(sizes[i].h * (float)scale);
            Top[i] = y; y += H[i] + gap; MaxW = Math.Max(MaxW, W[i]);
        }
        Total = y;
    }

    public int PageAt(double y)
    {
        int lo = 0, hi = N - 1;
        while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (Top[mid] <= y) lo = mid; else hi = mid - 1; }
        return lo;
    }
    public double PageX(int i, double viewW, double scrollX) => W[i] <= viewW ? Math.Floor((viewW - W[i]) / 2) : 8 - scrollX;
    public int Cols(int i, int T) => (int)Math.Ceiling(W[i] / T);
    public int Rows(int i, int T) => (int)Math.Ceiling(H[i] / T);
}

/// <summary>What the UI thread publishes to the scheduler each frame, and what the compositor draws.</summary>
public sealed class ViewState
{
    public long Frame;
    public int ZDisp, ZTgt;
    public double ScrollYDisp, ScrollXDisp, ScrollYTgt, ScrollXTgt;
    public float Gesture = 1f;             // extra uniform scale applied to the displayed (ZDisp) tiles during a zoom gesture, about the viewport centre
    public double VW, VH, VelPxPerSec;
    public ViewState Clone() => (ViewState)MemberwiseClone();
}

public sealed class TileItem { public TileEntry E = null!; public float X, Y, W, H; public bool Visible; }
public sealed class PageItem
{
    public int Page; public float X, Y, W, H; public TileEntry? Thumb; public bool VisiblePage; public int MissingCells;
    public readonly List<TileItem> Tiles = new();
}

/// <summary>Per-frame draw list + coverage probe for the displayed layout. Reused every frame (no per-frame allocation).</summary>
public sealed class FrameModel
{
    public long Frame; public double ScrollY, ScrollX; public float Gesture = 1f; public double VW, VH;
    public readonly List<PageItem> Pages = new();
    int _used;
    public double SharpPx, LowResPx, BlankPx;          // visible page-area pixels by what covers them
    public int VisiblePages, VisibleCells, SharpCells, LowResCells, BlankCells;
    readonly List<PageItem> _pool = new();
    readonly List<TileItem> _tilePool = new();
    int _tileUsed;

    public void Reset() { Pages.Clear(); _used = 0; _tileUsed = 0; SharpPx = LowResPx = BlankPx = 0; VisiblePages = VisibleCells = SharpCells = LowResCells = BlankCells = 0; }
    public PageItem NewPage() { if (_used == _pool.Count) _pool.Add(new PageItem()); var p = _pool[_used++]; p.Tiles.Clear(); p.Thumb = null; p.MissingCells = 0; Pages.Add(p); return p; }
    public TileItem NewTile() { if (_tileUsed == _tilePool.Count) _tilePool.Add(new TileItem()); return _tilePool[_tileUsed++]; }
}

public sealed class TileStore
{
    public readonly ConcurrentDictionary<TileKey, TileEntry> D = new();
    long _bytes; public long GpuBytes => Interlocked.Read(ref _bytes);
    public void AddBytes(long b) => Interlocked.Add(ref _bytes, b);
    public int Count => D.Count;
    public TileEntry GetOrAdd(TileKey k) => D.GetOrAdd(k, key => new TileEntry { Key = key });
    public bool TryGet(TileKey k, out TileEntry e) => D.TryGetValue(k, out e!);
}
