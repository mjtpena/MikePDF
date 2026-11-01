using Quire.Core.Geometry;

namespace Quire.Core.Tiles;

/// <summary>
/// Identity of one cached image: a 512 px tile of a page at a render scale and view rotation, or the page's low-resolution placeholder
/// (<see cref="IsLowRes"/>).
/// </summary>
public readonly record struct TileKey(int Page, uint ScaleBits, int Turns, int Tx, int Ty)
{
    public const int LowResMarker = -1;

    public bool IsLowRes => Tx == LowResMarker;

    public float Scale => BitConverter.UInt32BitsToSingle(ScaleBits);

    public static TileKey ForTile(int page, float scale, int turns, int tx, int ty) => new(page, BitConverter.SingleToUInt32Bits(scale), QuarterTurns.Normalize(turns), tx, ty);

    public static TileKey LowRes(int page, int turns) => new(page, 0, QuarterTurns.Normalize(turns), LowResMarker, LowResMarker);
}

/// <summary>A pixel rectangle inside a page at a render scale.</summary>
public readonly record struct TileRect(int X, int Y, int Width, int Height);

/// <summary>The 512 px device-pixel tile grid of ADR-0006.</summary>
public static class TileGrid
{
    public const int TileSize = 512;

    public static int Columns(long pageWidthPx) => (int)((pageWidthPx + TileSize - 1) / TileSize);

    public static int Rows(long pageHeightPx) => (int)((pageHeightPx + TileSize - 1) / TileSize);

    /// <summary>The pixel rectangle of tile (tx, ty), clipped to the page. Edge tiles are smaller.</summary>
    public static TileRect Rect(int tx, int ty, long pageWidthPx, long pageHeightPx)
    {
        var x = (long)tx * TileSize;
        var y = (long)ty * TileSize;
        return new TileRect((int)x, (int)y, (int)Math.Min(TileSize, pageWidthPx - x), (int)Math.Min(TileSize, pageHeightPx - y));
    }

    /// <summary>The inclusive tile index ranges that intersect a pixel rectangle of the page (empty ranges have Last &lt; First).</summary>
    public static (int FirstX, int LastX, int FirstY, int LastY) Covering(RectD pagePixels, long pageWidthPx, long pageHeightPx)
    {
        var clipped = pagePixels.Intersect(new RectD(0, 0, pageWidthPx, pageHeightPx));
        if (clipped.IsEmpty)
        {
            return (0, -1, 0, -1);
        }

        return ((int)Math.Floor(clipped.X / TileSize), (int)Math.Floor((clipped.Right - 1e-9) / TileSize), (int)Math.Floor(clipped.Y / TileSize), (int)Math.Floor((clipped.Bottom - 1e-9) / TileSize));
    }
}

/// <summary>A decoded image in BGRA, opaque white where the page has no content. The pixels belong to the owner (a slot of the ring) until disposed.</summary>
public interface IPixelBuffer : IDisposable
{
    int Width { get; }

    int Height { get; }

    int Stride { get; }

    ReadOnlySpan<byte> Pixels { get; }
}

public enum RenderStatus
{
    Ok,

    /// <summary>Some of the page failed to render; what was drawn is still shown (a small warning chip).</summary>
    Partial,

    Cancelled,
    Timeout,

    /// <summary>The engine died. A new one is on the way (EngineSupervisor), so the request is worth repeating shortly.</summary>
    EngineLost,

    /// <summary>The page or the document cannot be drawn (damaged, unsupported).</summary>
    Failed,
}

public sealed class RenderResult(RenderStatus status, IPixelBuffer? pixels = null, string? detail = null) : IDisposable
{
    public RenderStatus Status { get; } = status;

    public IPixelBuffer? Pixels { get; } = pixels;

    public string? Detail { get; } = detail;

    public void Dispose() => Pixels?.Dispose();
}

public readonly record struct RenderRequest(TileKey Key, float Scale, int Turns, int X, int Y, int Width, int Height, ulong Generation);

/// <summary>Where pixels come from: the sandboxed engine behind the broker (App), or a fake in tests. Never throws for engine failures; reports them in the result.</summary>
public interface ITileRenderer
{
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
}

/// <summary>A GPU image. Created off the UI thread by <see cref="ITilePresenter.CreateSurface"/>.</summary>
public interface ITileSurface : IDisposable
{
    int Width { get; }

    int Height { get; }
}

/// <summary>
/// The drawing back end behind the page surface (ADR-0006 decision 1). Win2D is the first implementation; a Composition one can follow
/// without touching the scheduler, the cache or the controller.
/// </summary>
public interface ITilePresenter
{
    /// <summary>Uploads pixels into a GPU surface. Called from a worker thread, never the UI thread; must be thread-safe.</summary>
    ITileSurface CreateSurface(IPixelBuffer pixels);

    /// <summary>Shows a frame. UI thread.</summary>
    void Present(FrameComposition frame);
}

/// <summary>One image to draw: a surface into a destination rectangle (viewport pixels).</summary>
public readonly record struct SurfaceDraw(ITileSurface Surface, RectD Destination);

public sealed class PageDraw(int page, RectD pageRect)
{
    public int Page { get; } = page;

    /// <summary>The page in viewport pixels (paper).</summary>
    public RectD PageRect { get; } = pageRect;

    /// <summary>Back to front: low-res placeholder, tiles of another scale, sharp tiles.</summary>
    public List<SurfaceDraw> Layers { get; } = [];
}

public sealed class FrameComposition
{
    public double ViewportWidth { get; init; }

    public double ViewportHeight { get; init; }

    public List<PageDraw> Pages { get; } = [];

    /// <summary>GPU scale applied to everything during a zoom gesture, about <see cref="GestureOrigin"/>.</summary>
    public double GestureScale { get; init; } = 1.0;

    public PointD GestureOrigin { get; init; }

    /// <summary>Visible area, in pixels, by what covers it: sharp, low-res or stale, nothing. Used by tests and diagnostics.</summary>
    public double SharpArea { get; set; }

    public double FallbackArea { get; set; }

    public double BlankArea { get; set; }
}
