namespace MikePDF.PdfWriter;

/// <summary>
/// The document as seen through one cross-reference section: that section's own entries, then every older section's.
/// <c>Revisions[0]</c> is the newest. Object lookups go through this revision's view only, so a later revision can
/// never change what an earlier view returns.
/// </summary>
public sealed class PdfDocumentRevision
{
    private const int MaxReferenceChain = 32;

    private readonly PdfDocument _document;
    private readonly ParseContext _ctx;
    private readonly List<XrefSection> _sections;
    private readonly XrefSection _section;
    private readonly Dictionary<int, PdfObject> _cache = [];
    private readonly Dictionary<int, ObjectStreamData> _objectStreams = [];
    private Dictionary<int, XrefEntry>? _effective;

    internal PdfDocumentRevision(PdfDocument document, ParseContext ctx, List<XrefSection> sections, int index)
    {
        _document = document;
        _ctx = ctx;
        _sections = sections;
        _section = sections[index];
        Index = index;
    }

    /// <summary>0 for the newest revision.</summary>
    public int Index { get; }

    public PdfDocument Document => _document;

    /// <summary>Offset of the "xref" keyword or of the xref stream object.</summary>
    public int XrefOffset => _section.Offset;

    public PdfXrefForm Form => _section.Form;

    public PdfDictionary Trailer => _section.Trailer;

    public long Size => _section.Size;

    /// <summary>/Prev, or null for the oldest section.</summary>
    public long? PrevOffset => _section.Prev;

    /// <summary>The /XRefStm offset when this is a hybrid-reference section.</summary>
    public int? HybridStreamOffset => _section.HybridStreamOffset;

    /// <summary>This section's own entries (for a hybrid section, the /XRefStm entries are included).</summary>
    public IReadOnlyDictionary<int, XrefEntry> Entries => _section.Entries;

    /// <summary>Object numbers whose entry came from a hybrid section's /XRefStm.</summary>
    public IReadOnlySet<int> HybridObjects => _section.HybridObjects;

    /// <summary>
    /// One past the "%%EOF" that directly follows this section; the exact end of the bytes a signature over this revision would
    /// cover. -1 when the section has no marker of its own (the first-page section of a linearized file).
    /// </summary>
    public int EndOffset => _section.EofEnd;

    /// <summary>The N in this section's own "startxref N", or -1.</summary>
    public long DeclaredStartXref => _section.StartXrefValue;

    public int ExtentStart => _section.ExtentStart;

    public int ExtentEnd => _section.ExtentEnd;

    /// <summary>The newest entry for <paramref name="number"/> as of this revision (a free entry counts), or false when no section lists it.</summary>
    public bool TryGetEntry(int number, out XrefEntry entry)
    {
        if (_effective is not null)
            return _effective.TryGetValue(number, out entry);
        for (int i = Index; i < _sections.Count; i++)
        {
            if (_sections[i].Entries.TryGetValue(number, out entry))
                return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Every object number this revision knows about, with its effective entry.</summary>
    public IEnumerable<KeyValuePair<int, XrefEntry>> EnumerateEffectiveEntries()
    {
        BuildEffectiveEntries();
        return _effective!;
    }

    /// <summary>Loads and caches an object. Free or unknown objects are the null object (7.3.9). Failures are returned, not thrown.</summary>
    public PdfResult<PdfObject> GetObject(int number) => PdfDocument.Guard(() => LoadObject(number));

    /// <summary>Follows indirect references until a direct object (bounded; a longer chain is an error).</summary>
    public PdfResult<PdfObject> Resolve(PdfObject? obj) => PdfDocument.Guard(() => ResolveInternal(obj));

    /// <summary>Decodes a stream's data with the supported filters (FlateDecode, PNG predictors).</summary>
    public PdfResult<byte[]> DecodeStream(PdfStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PdfDocument.Guard(() => StreamDecoder.Decode(stream, o => ResolveInternal(o), _ctx));
    }

    internal void BuildEffectiveEntries()
    {
        if (_effective is not null)
            return;
        var map = new Dictionary<int, XrefEntry>();
        for (int i = _sections.Count - 1; i >= Index; i--)
        {
            foreach (var kv in _sections[i].Entries)
                map[kv.Key] = kv.Value;
        }

        _effective = map;
    }

    internal PdfObject ResolveInternal(PdfObject? obj)
    {
        for (int i = 0; i < MaxReferenceChain; i++)
        {
            if (obj is not PdfReference r)
                return obj ?? PdfNull.Instance;
            obj = LoadReferenced(r);
        }

        ParseContext.Fatal(PdfErrorCode.InvalidReference, "indirect reference chain is too long");
        return PdfNull.Instance;
    }

    /// <summary>Loads the target of a reference, checking that its generation matches the xref entry.</summary>
    internal PdfObject LoadReferenced(PdfReference r)
    {
        if (TryGetEntry(r.Number, out var e) && e.Type != XrefEntryType.Free && e.Generation != r.Generation)
        {
            _ctx.Violation(PdfErrorCode.InvalidReference, $"reference {r} does not match the generation {e.Generation} of its xref entry");
            return PdfNull.Instance;
        }

        return LoadObject(r.Number);
    }

    internal PdfObject LoadObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
            return cached;
        if (!TryGetEntry(number, out var entry) || entry.Type == XrefEntryType.Free)
            return PdfNull.Instance;

        var obj = entry.Type == XrefEntryType.InUse ? LoadInUse(number, entry) : LoadCompressed(number, entry);
        _cache[number] = obj;
        return obj;
    }

    /// <summary>Parses the indirect object an in-use entry points at and checks its "N G obj" header against the entry.</summary>
    internal ParsedIndirectObject ParseAtEntry(int number, XrefEntry entry)
    {
        if (entry.Offset < 1 || entry.Offset >= _document.Data.Length)
            ParseContext.Fatal(PdfErrorCode.XrefOffsetOutOfRange, $"object {number}: offset {entry.Offset} is outside the file", entry.Offset);
        var parser = new ObjectParser(_document.Data, _ctx);
        var parsed = parser.ParseIndirectObject((int)entry.Offset, ResolveLength);
        if (parsed.Number != number || parsed.Generation != entry.Generation)
        {
            _ctx.Violation(
                PdfErrorCode.ObjectHeaderMismatch,
                $"xref entry for {number} {entry.Generation} points at object {parsed.Number} {parsed.Generation}",
                entry.Offset);
        }

        return parsed;
    }

    private PdfObject LoadInUse(int number, XrefEntry entry)
    {
        var parsed = ParseAtEntry(number, entry);
        return parsed.Number == number && parsed.Generation == entry.Generation ? parsed.Value : PdfNull.Instance;
    }

    /// <summary>Resolves an indirect /Length. Only a plain in-use integer object counts; this never recurses into another stream.</summary>
    private long? ResolveLength(PdfReference r)
    {
        if (!TryGetEntry(r.Number, out var e) || e.Type != XrefEntryType.InUse || e.Generation != r.Generation)
            return null;
        if (e.Offset < 1 || e.Offset >= _document.Data.Length)
            return null;
        var parser = new ObjectParser(_document.Data, _ctx);
        var parsed = parser.ParseIndirectObject((int)e.Offset, lengthResolver: null);
        return parsed.Number == r.Number && parsed.Value is PdfInteger i ? i.Value : null;
    }

    private PdfObject LoadCompressed(int number, XrefEntry entry)
    {
        var os = GetObjectStream(entry.ObjectStreamNumber);
        int index = entry.IndexInObjectStream;
        if (index >= os.Numbers.Length)
        {
            _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object {number}: index {index} is beyond the {os.Numbers.Length} objects of stream {entry.ObjectStreamNumber}");
            return PdfNull.Instance;
        }

        if (os.Numbers[index] != number)
        {
            _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object {number}: slot {index} of stream {entry.ObjectStreamNumber} holds object {os.Numbers[index]}");
            return PdfNull.Instance;
        }

        long pos = (long)os.First + os.Offsets[index];
        if (pos >= os.Data.Length)
        {
            _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object {number}: offset is beyond the decoded object stream");
            return PdfNull.Instance;
        }

        var parser = new ObjectParser(os.Data, _ctx, (int)pos);
        return parser.ParseObject();
    }

    private ObjectStreamData GetObjectStream(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out var cached))
            return cached;

        // An object stream must itself be an uncompressed object; nesting would let a file build a cycle.
        if (!TryGetEntry(streamNumber, out var e) || e.Type != XrefEntryType.InUse)
            ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber} is missing or not an uncompressed object");
        var parsed = ParseAtEntry(streamNumber, e);
        if (parsed.Number != streamNumber || parsed.Value is not PdfStream stream)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, $"object {streamNumber} is not an object stream");
            return null!;
        }

        var dict = stream.Dictionary;
        if (dict.GetName("Type") != "ObjStm")
            _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber} lacks /Type /ObjStm");
        if (dict["N"] is not PdfInteger { Value: >= 0 } n || dict["First"] is not PdfInteger { Value: >= 0 } first)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber} lacks a direct /N or /First");
            return null!;
        }

        if (n.Value > _ctx.Limits.MaxObjectStreamObjects)
            ParseContext.ExceedLimit(PdfLimit.ObjectStreamObjects, $"object stream {streamNumber} declares {n.Value} objects");

        var decoded = StreamDecoder.Decode(stream, o => ResolveInternal(o), _ctx);

        // Each header pair needs at least four bytes ("1 0 "), so this bounds the allocation by the decoded size.
        if (n.Value * 4 > decoded.Length || first.Value > decoded.Length)
            ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber}: /N or /First exceeds the decoded data");

        int count = (int)n.Value;
        var numbers = new int[count];
        var offsets = new int[count];
        var seen = new HashSet<int>();
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            long num = ReadHeaderInt(decoded, ref pos);
            long off = ReadHeaderInt(decoded, ref pos);
            if (num > _ctx.Limits.MaxObjectNumber || off > decoded.Length)
                ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber}: header entry {i} is out of range");
            if (!seen.Add((int)num))
                _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber} lists object {num} twice");
            numbers[i] = (int)num;
            offsets[i] = (int)off;
        }

        if (pos > first.Value)
            _ctx.Violation(PdfErrorCode.InvalidObjectStream, $"object stream {streamNumber}: header overruns /First");

        var data = new ObjectStreamData(decoded, (int)first.Value, numbers, offsets);
        _objectStreams[streamNumber] = data;
        return data;
    }

    private static long ReadHeaderInt(byte[] data, ref int pos)
    {
        pos = PdfBytes.SkipWhitespaceAndComments(data, pos);
        int s = pos;
        long v = 0;
        while (pos < data.Length && PdfBytes.IsDigit(data[pos]))
        {
            if (pos - s >= 10)
                ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, "object stream header integer too long");
            v = (v * 10) + (data[pos] - '0');
            pos++;
        }

        if (pos == s)
            ParseContext.Fatal(PdfErrorCode.InvalidObjectStream, "object stream header integer expected");
        return v;
    }

    private sealed record ObjectStreamData(byte[] Data, int First, int[] Numbers, int[] Offsets);
}
