using MikePDF.Protocol;

namespace MikePDF.Broker;

/// <summary>
/// A typed result (§6.4): no exceptions cross the engine boundary. <see cref="Value"/> is present for <c>Ok</c> and, for
/// RenderTile, for <c>PartiallyRendered</c>. Statuses the engine never sends (<c>EngineCrashed</c>, and <c>Cancelled</c> or
/// <c>Timeout</c> decided locally) are produced by the client.
/// </summary>
public readonly record struct EngineResult<T>(Status Status, T? Value, string? Detail = null)
    where T : class
{
    public bool IsOk => Status == Status.Ok;

    public bool HasValue => Value is not null;

    internal static EngineResult<T> Fail(Status status, string? detail = null) => new(status, null, detail);
}

/// <summary>The parameters of one tile, in the terms of the protocol: device pixels of the whole scaled and rotated page.</summary>
public readonly record struct RenderTileArgs(
    uint Page,
    float Scale,
    ushort Rotation,
    int TileX,
    int TileY,
    uint TileWidth,
    uint TileHeight,
    uint Flags = RenderTileArgs.Annotations)
{
    public const uint Annotations = 1;
    public const uint FormFields = 2;

    /// <summary>
    /// Dark pages: the engine recolours the tile the way <c>PageColourMatrix.Inverted</c> does (lightness turned over, hue kept), except for colour photographs, which keep their
    /// own colours (a picture that is mostly white paper, such as a scan or a diagram, is recoloured with the page). Pixels that come back are final: the presenter must not apply
    /// the inverted matrix to them. A tile cache must be emptied when this changes, because the pixels differ.
    /// </summary>
    public const uint DarkPage = 4;

    /// <summary>
    /// The fillable-field highlight (schema 7): PDFium's own translucent tint over every fillable widget, drawn with the form layer, so it only means something together with
    /// <see cref="FormFields"/>. Never for a print, a snapshot or a flattened copy. A tile cache must be emptied when this changes, because the pixels differ.
    /// </summary>
    public const uint FieldHighlight = 8;
}

/// <summary>
/// One band of a page for printing (schema 5): rows <see cref="BandTop"/> to <see cref="BandTop"/> + <see cref="BandHeight"/> of the page laid out at
/// <see cref="PageWidthPx"/> x <see cref="PageHeightPx"/> device pixels after the rotation. <see cref="Dpi"/> is the resolution the layout was made for
/// (72 to 1200); the engine draws the page to fit the pixels, so it is informational there and what the broker's permission rules key on.
/// </summary>
public readonly record struct RenderBandArgs(
    uint Page,
    uint Dpi,
    ushort Rotation,
    uint PageWidthPx,
    uint PageHeightPx,
    uint BandTop,
    uint BandHeight,
    uint Flags = RenderBandArgs.Annotations | RenderBandArgs.Printing)
{
    public const uint Annotations = 1;
    public const uint FormFields = 2;
    public const uint Printing = 4;
}

/// <summary>
/// A rendered tile: BGRA over opaque white, <see cref="Stride"/> bytes per row, in a slot of the ring. Dispose it to give the slot
/// back. The engine is untrusted: the dimensions here were checked against the request, but the pixels are whatever it wrote.
/// </summary>
public sealed class TileLease : IDisposable
{
    private readonly SlotLease _slot;

    internal TileLease(SlotLease slot, int width, int height, int stride, ulong generation, bool partiallyRendered)
    {
        _slot = slot;
        Width = width;
        Height = height;
        Stride = stride;
        Generation = generation;
        PartiallyRendered = partiallyRendered;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public ulong Generation { get; }

    /// <summary>PDFium reported a failure part-way through the page (status <c>PartiallyRendered</c>).</summary>
    public bool PartiallyRendered { get; }

    /// <summary>Exactly <c>Stride * Height</c> bytes.</summary>
    public ReadOnlySpan<byte> Pixels => _slot.Bytes[..(Stride * Height)];

    public void Dispose() => _slot.Dispose();
}
