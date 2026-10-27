namespace Quire.PdfWriter;

/// <summary>
/// Hard resource limits. Every value read from an untrusted file is checked against one of these before it is
/// used to allocate or loop. Exceeding a limit is always a failure (<see cref="PdfErrorCode.LimitExceeded"/>),
/// in strict and lenient mode alike.
/// </summary>
public sealed record ParserLimits
{
    /// <summary>Absolute ceiling for nesting depth regardless of <see cref="MaxNestingDepth"/>; bounds stack use.</summary>
    internal const int AbsoluteMaxNestingDepth = 256;

    /// <summary>Largest input accepted, in bytes. Offsets are 32-bit internally, so values above int.MaxValue are clamped.</summary>
    public long MaxFileSize { get; init; } = 1L << 30;

    /// <summary>Maximum number of cross-reference entries summed over every section of the chain.</summary>
    public int MaxObjectCount { get; init; } = 4_194_304;

    /// <summary>Highest object number accepted. PDFium ignores numbers above 4,194,303, so a larger one is a reader differential.</summary>
    public int MaxObjectNumber { get; init; } = 4_194_303;

    /// <summary>Maximum array/dictionary nesting depth. PDFium uses 64.</summary>
    public int MaxNestingDepth { get; init; } = 64;

    /// <summary>Maximum decoded length of one string, in bytes.</summary>
    public int MaxStringLength { get; init; } = 1 << 20;

    /// <summary>Maximum decoded length of one name, in bytes (the spec recommends 127).</summary>
    public int MaxNameLength { get; init; } = 4096;

    /// <summary>Maximum number of elements in one array.</summary>
    public int MaxArrayLength { get; init; } = 1 << 20;

    /// <summary>Maximum number of entries in one dictionary.</summary>
    public int MaxDictionaryEntries { get; init; } = 1 << 16;

    /// <summary>Maximum decompressed size of one stream, in bytes.</summary>
    public long MaxDecompressedStreamBytes { get; init; } = 256L << 20;

    /// <summary>Maximum decompressed bytes summed over everything one document decodes (zip-bomb guard).</summary>
    public long MaxDecompressedTotalBytes { get; init; } = 1L << 30;

    /// <summary>Maximum number of cross-reference sections walked via /Prev.</summary>
    public int MaxXrefSections { get; init; } = 512;

    /// <summary>Maximum number of filters on one stream.</summary>
    public int MaxFilterChainLength { get; init; } = 4;

    /// <summary>Maximum /N of one object stream.</summary>
    public int MaxObjectStreamObjects { get; init; } = 1 << 20;

    public static ParserLimits Default { get; } = new();

    internal int EffectiveMaxNestingDepth => Math.Clamp(MaxNestingDepth, 1, AbsoluteMaxNestingDepth);

    internal int EffectiveMaxFileSize => (int)Math.Clamp(MaxFileSize, 0, int.MaxValue);
}
