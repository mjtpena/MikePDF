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
