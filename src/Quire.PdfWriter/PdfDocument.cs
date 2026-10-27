namespace Quire.PdfWriter;

/// <summary>
/// A parsed document: the cross-reference chain plus lazy object access. Not thread-safe.
///
/// Two sealed subclasses exist and the split is deliberate:
/// <list type="bullet">
/// <item><see cref="StrictDocument"/> is parsed in strict mode and fails closed. Only it can be validated or
/// extended by the incremental writer.</item>
/// <item><see cref="LenientDocument"/> is for diagnostics. It records what it recovered from in
/// <see cref="Issues"/> and can never be handed to <see cref="StructureValidator"/> or <see cref="IncrementalUpdate"/>,
/// so a lenient reading cannot upgrade a validation verdict (ADR-0008 decision 5).</item>
/// </list>
/// </summary>
public abstract class PdfDocument
{
    private readonly ParseContext _ctx;

    internal PdfDocument(ReadOnlyMemory<byte> data, ParseContext ctx, List<XrefSection> sections, long startXref)
    {
        Data = data;
        _ctx = ctx;
        StartXref = startXref;
        var revisions = new PdfDocumentRevision[sections.Count];
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = new PdfDocumentRevision(this, ctx, sections, i);
        Revisions = revisions;
        revisions[0].BuildEffectiveEntries();
    }

    public ReadOnlyMemory<byte> Data { get; }

    public ParserLimits Limits => _ctx.Limits;

    /// <summary>The offset the final "startxref" points at.</summary>
    public long StartXref { get; }

    /// <summary>One entry per cross-reference section, newest first.</summary>
    public IReadOnlyList<PdfDocumentRevision> Revisions { get; }

    public PdfDocumentRevision Latest => Revisions[0];

    public PdfDictionary Trailer => Latest.Trailer;

    /// <summary>The catalog reference from the newest trailer.</summary>
    public PdfReference? Root => Latest.Trailer["Root"] as PdfReference;

    /// <summary>/Size of the newest trailer: one more than the highest object number in use.</summary>
    public int Size => (int)Latest.Size;

    /// <summary>Total bytes decompressed so far while reading this document. Never exceeds <see cref="ParserLimits.MaxDecompressedTotalBytes"/>.</summary>
    public long DecompressedBytes => _ctx.DecompressedTotal;

    /// <summary>Recoveries recorded while parsing. Always empty for a strict document (strict never recovers).</summary>
    public IReadOnlyList<PdfError> Issues => _ctx.Issues;

    public bool IssuesTruncated => _ctx.IssuesTruncated;

    public int XrefEntryCount => _ctx.XrefEntryCount;

    public PdfResult<PdfObject> GetObject(int number) => Latest.GetObject(number);

    public PdfResult<PdfObject> Resolve(PdfObject? obj) => Latest.Resolve(obj);

    public PdfResult<byte[]> DecodeStream(PdfStream stream) => Latest.DecodeStream(stream);

    internal static T OpenCore<T>(ReadOnlyMemory<byte> data, ParserLimits? limits, bool strict, Func<ReadOnlyMemory<byte>, ParseContext, List<XrefSection>, long, T> create)
        where T : PdfDocument
    {
        limits ??= ParserLimits.Default;
        var ctx = new ParseContext(limits, strict);
        if (data.Length > limits.MaxFileSize || data.Length > int.MaxValue)
            ParseContext.ExceedLimit(PdfLimit.FileSize, $"file of {data.Length} bytes exceeds the limit");

        CheckHeader(data.Span, ctx);
        var (startXref, eofEnd) = FindStartXref(data.Span, ctx);
        var sections = XrefReader.ReadChain(data, ctx, startXref);

        // The final startxref/%%EOF must directly follow one of the sections: the newest for an ordinary file, the last
        // one in the /Prev chain for a linearized file (whose first-page section comes first in the chain but not in the file).
        if (eofEnd >= 0 && !sections.Any(s => s.EofEnd == eofEnd))
            ctx.Violation(PdfErrorCode.TrailingData, "the final startxref does not directly follow any xref section", sections[0].Offset);
        if (sections[0].Trailer["Root"] is not PdfReference)
            ctx.Violation(PdfErrorCode.InvalidTrailer, "newest trailer has no /Root reference", sections[0].Offset);

        var doc = create(data, ctx, sections, startXref);

        if (doc.Root is PdfReference root && doc.Latest.LoadObject(root.Number) is not PdfDictionary)
            ctx.Violation(PdfErrorCode.InvalidTrailer, "/Root does not resolve to a dictionary", sections[0].Offset);
        return doc;
    }

    internal static PdfResult<T> Guard<T>(Func<T> action)
    {
        try
        {
            return PdfResult.Ok(action());
        }
        catch (PdfParseException ex)
        {
            return PdfResult.Fail<T>(ex.Error);
        }
    }

    private static void CheckHeader(ReadOnlySpan<byte> data, ParseContext ctx)
    {
        // PDFium accepts the header anywhere in the first 1024 bytes and then shifts every offset by that amount.
        // Offsets here are absolute, so strict mode requires the header at byte 0.
        static bool IsHeaderAt(ReadOnlySpan<byte> d, int p) =>
            p + 8 <= d.Length
            && d.Slice(p, 5).SequenceEqual("%PDF-"u8)
            && d[p + 5] is (byte)'1' or (byte)'2'
            && d[p + 6] == (byte)'.'
            && PdfBytes.IsDigit(d[p + 7]);

        if (IsHeaderAt(data, 0) && (data.Length == 8 || PdfBytes.IsWhitespace(data[8])))
            return;

        int found = data[..Math.Min(data.Length, 1024)].IndexOf("%PDF-"u8);
        if (found > 0 && IsHeaderAt(data, found))
            ctx.Violation(PdfErrorCode.InvalidHeader, $"header found at offset {found}, not 0", found);
        else
            ctx.Violation(PdfErrorCode.InvalidHeader, "missing or malformed %PDF- header", 0);
    }

    private static (long StartXref, int EofEnd) FindStartXref(ReadOnlySpan<byte> data, ParseContext ctx)
    {
        int idx = data.LastIndexOf("startxref"u8);
        if (idx < 0)
            ParseContext.Fatal(PdfErrorCode.MissingStartXref, "no 'startxref' in the file");

        int p = PdfBytes.SkipWhitespace(data, idx + 9);
        int s = p;
        long v = 0;
        while (p < data.Length && PdfBytes.IsDigit(data[p]) && p - s < 10)
        {
            v = (v * 10) + (data[p] - '0');
            p++;
        }

        if (p == s)
            ParseContext.Fatal(PdfErrorCode.InvalidStartXref, "'startxref' is not followed by an offset", p);
        if (v < 1 || v >= data.Length)
            ParseContext.Fatal(PdfErrorCode.InvalidStartXref, $"startxref offset {v} is outside the file", p);

        p = PdfBytes.SkipWhitespace(data, p);
        int eofEnd = -1;
        if (p <= data.Length - 5 && data.Slice(p, 5).SequenceEqual("%%EOF"u8))
        {
            eofEnd = p + 5;
            if (data[eofEnd..].IndexOfAnyExcept(WhitespaceBytes) >= 0)
                ctx.Violation(PdfErrorCode.TrailingData, "data follows the final %%EOF", eofEnd);
        }
        else
        {
            ctx.Violation(PdfErrorCode.MissingEof, "final '%%EOF' missing after 'startxref'", p);
        }

        return (v, eofEnd);
    }

    private static readonly System.Buffers.SearchValues<byte> WhitespaceBytes = System.Buffers.SearchValues.Create([0, 9, 10, 12, 13, 32]);
}

/// <summary>A document parsed in strict mode; the only kind the validator and the incremental writer accept.</summary>
public sealed class StrictDocument : PdfDocument
{
    private StrictDocument(ReadOnlyMemory<byte> data, ParseContext ctx, List<XrefSection> sections, long startXref)
        : base(data, ctx, sections, startXref)
    {
    }

    /// <summary>Parses <paramref name="data"/> strictly. Fails closed on any construct a second reader could read differently.</summary>
    public static PdfResult<StrictDocument> Open(ReadOnlyMemory<byte> data, ParserLimits? limits = null) =>
        Guard(() => OpenCore(data, limits, strict: true, (d, c, s, x) => new StrictDocument(d, c, s, x)));
}

/// <summary>
/// A document parsed leniently, for diagnostics only. See <see cref="PdfDocument"/> for why it is a separate type.
/// </summary>
public sealed class LenientDocument : PdfDocument
{
    private LenientDocument(ReadOnlyMemory<byte> data, ParseContext ctx, List<XrefSection> sections, long startXref)
        : base(data, ctx, sections, startXref)
    {
    }

    /// <summary>Parses <paramref name="data"/> recording recoverable problems in <see cref="PdfDocument.Issues"/>. Limits are still enforced.</summary>
    public static PdfResult<LenientDocument> Open(ReadOnlyMemory<byte> data, ParserLimits? limits = null) =>
        Guard(() => OpenCore(data, limits, strict: false, (d, c, s, x) => new LenientDocument(d, c, s, x)));
}
