namespace Quire.PdfWriter;

public enum XrefEntryType : byte
{
    Free = 0,
    InUse = 1,
    Compressed = 2,
}

/// <summary>
/// One cross-reference entry. For <see cref="XrefEntryType.InUse"/> the fields are (offset, generation);
/// for <see cref="XrefEntryType.Compressed"/> they are (object stream number, index in that stream);
/// for <see cref="XrefEntryType.Free"/> they are (next free object, generation).
/// </summary>
public readonly record struct XrefEntry(XrefEntryType Type, long Field2, int Field3)
{
    public long Offset => Field2;

    public int Generation => Type == XrefEntryType.Compressed ? 0 : Field3;

    public int ObjectStreamNumber => (int)Field2;

    public int IndexInObjectStream => Field3;
}

public enum PdfXrefForm
{
    Table,
    Stream,
}

/// <summary>One parsed cross-reference section plus the trailer data that came with it.</summary>
internal sealed class XrefSection
{
    public required int Offset { get; init; }

    public required PdfXrefForm Form { get; init; }

    public required PdfDictionary Trailer { get; init; }

    /// <summary>This section's own entries; for a hybrid file, the /XRefStm entries are merged in.</summary>
    public Dictionary<int, XrefEntry> Entries { get; } = [];

    /// <summary>Object numbers whose entry came from the /XRefStm of a hybrid section.</summary>
    public HashSet<int> HybridObjects { get; } = [];

    public long Size { get; init; }

    public long? Prev { get; init; }

    public int? HybridStreamOffset { get; init; }

    /// <summary>First byte of the xref region (the "xref" keyword, or the stream object header).</summary>
    public int ExtentStart { get; init; }

    /// <summary>One past the last byte of the xref table/trailer, or of the xref stream's "endobj".</summary>
    public int ExtentEnd { get; set; }

    /// <summary>The N of this section's own "startxref N", or -1 if the marker was absent (lenient only).</summary>
    public long StartXrefValue { get; set; } = -1;

    /// <summary>One past "%%EOF" for this section's revision, or -1 if absent (lenient only).</summary>
    public int EofEnd { get; set; } = -1;
}

/// <summary>
/// Reads the cross-reference chain: classic tables, xref streams, and hybrid (/XRefStm) sections, following /Prev
/// with cycle and length protection. Entry-level semantics are those of ISO 32000-2 clause 7.5.4 and 7.5.8; in strict
/// mode the table layout is the exact 20-byte entry form, because PDFium reads fixed-width records and a token-based
/// reader would disagree on any file that is not.
/// </summary>
internal static class XrefReader
{
    private const int EntryWidth = 20;

    public static List<XrefSection> ReadChain(ReadOnlyMemory<byte> data, ParseContext ctx, long startXref)
    {
        var sections = new List<XrefSection>();
        var seen = new HashSet<long>();
        long? next = startXref;
        while (next is long offset)
        {
            if (offset < 1 || offset >= data.Length)
                ParseContext.Fatal(PdfErrorCode.XrefOffsetOutOfRange, $"xref offset {offset} is outside the file", offset);
            if (!seen.Add(offset))
            {
                ctx.Violation(PdfErrorCode.XrefCycle, $"/Prev chain revisits offset {offset}", offset);
                break;
            }

            if (sections.Count >= ctx.Limits.MaxXrefSections)
                ParseContext.ExceedLimit(PdfLimit.XrefSections, "too many cross-reference sections", offset);

            var section = ReadSection(data, ctx, (int)offset, seen);
            sections.Add(section);
            next = section.Prev;
        }

        // /Size can only grow as revisions are added. A shrinking /Size changes which entries a reader honours.
        for (int i = 0; i + 1 < sections.Count; i++)
        {
            if (sections[i].Size < sections[i + 1].Size)
                ctx.Violation(PdfErrorCode.InvalidTrailer, "/Size of a newer section is smaller than that of an older one", sections[i].Offset);
        }

        return sections;
    }

    private static XrefSection ReadSection(ReadOnlyMemory<byte> mem, ParseContext ctx, int offset, HashSet<long> seen)
    {
        var data = mem.Span;
        int pos = PdfBytes.SkipWhitespace(data, offset);
        var section = PdfBytes.KeywordAt(data, pos, "xref"u8)
            ? ReadTable(mem, ctx, pos)
            : ReadStream(mem, ctx, offset, hybridPart: false);

        if (section.HybridStreamOffset is int xs)
        {
            if (!seen.Add(xs))
                ctx.Violation(PdfErrorCode.XrefCycle, $"/XRefStm offset {xs} was already visited", xs);
            else
                MergeHybrid(mem, ctx, section, xs);
        }

        return section;
    }

    private static XrefSection ReadTable(ReadOnlyMemory<byte> mem, ParseContext ctx, int keywordPos)
    {
        var data = mem.Span;
        int pos = keywordPos + 4;
        var entries = new Dictionary<int, XrefEntry>();
        while (true)
        {
            pos = PdfBytes.SkipWhitespace(data, pos);
            if (PdfBytes.KeywordAt(data, pos, "trailer"u8))
            {
                pos += 7;
                break;
            }

            long start = ReadUnsigned(data, ref pos, 10, "xref subsection start");
            pos = RequireWhitespace(data, pos);
            long count = ReadUnsigned(data, ref pos, 10, "xref subsection count");
            pos = PdfBytes.SkipWhitespace(data, pos);

            if (count > ctx.Limits.MaxObjectCount - ctx.XrefEntryCount)
                ParseContext.ExceedLimit(PdfLimit.ObjectCount, "too many cross-reference entries", pos);
            if (count > 0 && start + count - 1 > ctx.Limits.MaxObjectNumber)
                ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "object number above the limit in xref subsection", pos);
            if (count * EntryWidth > data.Length - pos + 2)
                ctx.Violation(PdfErrorCode.InvalidXref, "xref subsection claims more entries than the file holds", pos);

            for (long i = 0; i < count; i++)
            {
                ctx.XrefEntryCount++;
                int num = (int)(start + i);
                var entry = ReadTableEntry(data, ref pos, ctx);
                if (num == 0 && entry.Type == XrefEntryType.InUse)
                    ctx.Violation(PdfErrorCode.InvalidXrefEntry, "object 0 must be free", pos);
                if (!entries.TryAdd(num, entry))
                {
                    ctx.Violation(PdfErrorCode.DuplicateXrefEntry, $"object {num} appears twice in one xref table", pos);
                    entries[num] = entry;
                }
            }
        }

        var parser = new ObjectParser(mem, ctx, pos);
        if (parser.ParseObject() is not PdfDictionary trailer)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "trailer is not a dictionary", pos);
            return null!;
        }

        int trailerEnd = parser.Position;
        var (size, prev, xrefStm) = ReadTrailerFields(trailer, ctx, mem.Length, trailerEnd);
        var section = new XrefSection
        {
            Offset = keywordPos,
            Form = PdfXrefForm.Table,
            Trailer = trailer,
            Size = size,
            Prev = prev,
            HybridStreamOffset = xrefStm,
            ExtentStart = keywordPos,
            ExtentEnd = trailerEnd,
        };
        foreach (var kv in entries)
        {
            CheckAgainstSize(kv.Key, size, ctx, kv.Key);
            section.Entries[kv.Key] = kv.Value;
        }

        ReadEofMarker(data, section, trailerEnd, ctx);
        return section;
    }

    private static XrefEntry ReadTableEntry(ReadOnlySpan<byte> data, ref int pos, ParseContext ctx)
    {
        if (IsStrictEntry(data, pos))
        {
            long offset = ParseDigits(data.Slice(pos, 10));
            int gen = (int)ParseDigits(data.Slice(pos + 11, 5));
            bool inUse = data[pos + 17] == (byte)'n';
            pos += EntryWidth;
            return MakeTableEntry(inUse, offset, gen, data.Length, ctx, pos);
        }

        // Fixed-width form violated. Strict mode stops here; lenient mode falls back to whitespace-separated tokens.
        ctx.Violation(PdfErrorCode.InvalidXrefEntry, "xref entry is not the exact 20-byte form", pos);
        long off = ReadUnsigned(data, ref pos, 10, "xref entry offset");
        pos = RequireWhitespace(data, pos);
        long g = ReadUnsigned(data, ref pos, 5, "xref entry generation");
        pos = RequireWhitespace(data, pos);
        if (pos >= data.Length || data[pos] is not ((byte)'n' or (byte)'f'))
            ParseContext.Fatal(PdfErrorCode.InvalidXrefEntry, "xref entry type must be 'n' or 'f'", pos);
        bool used = data[pos] == (byte)'n';
        pos++;
        pos = PdfBytes.SkipWhitespace(data, pos);
        return MakeTableEntry(used, off, (int)Math.Min(g, int.MaxValue), data.Length, ctx, pos);
    }

    private static XrefEntry MakeTableEntry(bool inUse, long offset, int gen, int fileLength, ParseContext ctx, int where)
    {
        if (gen > 65535)
        {
            ctx.Violation(PdfErrorCode.InvalidXrefEntry, "generation number above 65535", where);
            gen = 65535;
        }

        if (!inUse)
            return new XrefEntry(XrefEntryType.Free, offset, gen);

        // The 8-byte floor: "%PDF-x.y" plus its end of line. No object can start earlier.
        if (offset < 8 || offset >= fileLength)
            ctx.Violation(PdfErrorCode.XrefOffsetOutOfRange, $"in-use entry offset {offset} is outside the file", where);
        return new XrefEntry(XrefEntryType.InUse, offset, gen);
    }

    /// <summary>"oooooooooo ggggg n" followed by SP CR, SP LF or CR LF (7.5.4).</summary>
    private static bool IsStrictEntry(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < 0 || pos > data.Length - EntryWidth)
            return false;
        for (int i = 0; i < 10; i++)
        {
            if (!PdfBytes.IsDigit(data[pos + i]))
                return false;
        }

        if (data[pos + 10] != (byte)' ')
            return false;
        for (int i = 11; i < 16; i++)
        {
            if (!PdfBytes.IsDigit(data[pos + i]))
                return false;
        }

        if (data[pos + 16] != (byte)' ' || data[pos + 17] is not ((byte)'n' or (byte)'f'))
            return false;
        byte e1 = data[pos + 18];
        byte e2 = data[pos + 19];
        return (e1 == (byte)' ' && e2 is 10 or 13) || (e1 == 13 && e2 == 10);
    }

    private static XrefSection ReadStream(ReadOnlyMemory<byte> mem, ParseContext ctx, int offset, bool hybridPart)
    {
        var parser = new ObjectParser(mem, ctx);

        // The Length of an xref stream cannot be resolved through an xref that is not loaded yet, so it must be direct.
        var indirect = parser.ParseIndirectObject(offset, lengthResolver: null);
        if (indirect.Value is not PdfStream stream)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidXref, $"no xref table or xref stream at offset {offset}", offset);
            return null!;
        }

        var dict = stream.Dictionary;
        if (dict.GetName("Type") != "XRef")
            ctx.Violation(PdfErrorCode.InvalidXref, "xref stream without /Type /XRef", offset);
        if (dict["Length"] is not PdfInteger)
            ctx.Violation(PdfErrorCode.InvalidXref, "xref stream /Length must be a direct integer", offset);
        if (hybridPart && dict.ContainsKey("Prev"))
            ctx.Violation(PdfErrorCode.HybridConflict, "/XRefStm stream has its own /Prev", offset);
        if (dict.ContainsKey("XRefStm"))
            ctx.Violation(PdfErrorCode.HybridConflict, "xref stream carries /XRefStm", offset);

        var (size, prev, _) = ReadTrailerFields(dict, ctx, mem.Length, offset);
        var (w0, w1, w2) = ReadWidths(dict, offset);
        var index = ReadIndex(dict, size, ctx, offset);

        var decoded = StreamDecoder.Decode(stream, resolve: null, ctx);
        int record = w0 + w1 + w2;
        long total = 0;
        foreach (var (start, count) in index)
        {
            if (count > ctx.Limits.MaxObjectCount - ctx.XrefEntryCount - total)
                ParseContext.ExceedLimit(PdfLimit.ObjectCount, "too many cross-reference entries", offset);
            if (count > 0 && (start > ctx.Limits.MaxObjectNumber || count - 1 > ctx.Limits.MaxObjectNumber - start))
                ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "object number above the limit in xref stream", offset);
            total += count;
        }

        if (total * record != decoded.Length)
            ctx.Violation(PdfErrorCode.InvalidXref, $"xref stream data is {decoded.Length} bytes, /W and /Index imply {total * record}", offset);

        var section = new XrefSection
        {
            Offset = offset,
            Form = PdfXrefForm.Stream,
            Trailer = dict,
            Size = size,
            Prev = hybridPart ? null : prev,
            ExtentStart = indirect.Start,
            ExtentEnd = indirect.End,
        };

        int p = 0;
        foreach (var (start, count) in index)
        {
            for (long i = 0; i < count; i++)
            {
                if (p + record > decoded.Length)
                    goto done;
                ctx.XrefEntryCount++;
                long num = start + i;
                if (num > ctx.Limits.MaxObjectNumber)
                    ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "object number above the limit in xref stream", offset);
                long type = w0 == 0 ? 1 : ReadBigEndian(decoded, p, w0);
                long f2 = ReadBigEndian(decoded, p + w0, w1);
                long f3 = ReadBigEndian(decoded, p + w0 + w1, w2);
                p += record;
                var entry = MakeStreamEntry(type, f2, f3, mem.Length, ctx, offset);
                if (entry is null)
                    continue;
                if (num == 0 && entry.Value.Type == XrefEntryType.InUse)
                    ctx.Violation(PdfErrorCode.InvalidXrefEntry, "object 0 must be free", offset);
                CheckAgainstSize((int)num, size, ctx, offset);
                if (!section.Entries.TryAdd((int)num, entry.Value))
                {
                    ctx.Violation(PdfErrorCode.DuplicateXrefEntry, $"object {num} appears twice in one xref stream", offset);
                    section.Entries[(int)num] = entry.Value;
                }
            }
        }

    done:
        if (!hybridPart)
            ReadEofMarker(mem.Span, section, indirect.End, ctx);
        return section;
    }

    private static XrefEntry? MakeStreamEntry(long type, long f2, long f3, int fileLength, ParseContext ctx, int where)
    {
        switch (type)
        {
            case 0:
                return new XrefEntry(XrefEntryType.Free, f2, (int)Math.Min(f3, 65535));
            case 1:
                if (f3 > 65535)
                {
                    ctx.Violation(PdfErrorCode.InvalidXrefEntry, "generation number above 65535", where);
                    f3 = 65535;
                }

                if (f2 < 8 || f2 >= fileLength)
                    ctx.Violation(PdfErrorCode.XrefOffsetOutOfRange, $"in-use entry offset {f2} is outside the file", where);
                return new XrefEntry(XrefEntryType.InUse, f2, (int)f3);
            case 2:
                if (f2 < 1 || f2 > int.MaxValue || f3 > int.MaxValue)
                {
                    ctx.Violation(PdfErrorCode.InvalidXrefEntry, "compressed entry has an out-of-range object stream number or index", where);
                    return null;
                }

                return new XrefEntry(XrefEntryType.Compressed, f2, (int)f3);
            default:
                ctx.Violation(PdfErrorCode.InvalidXrefEntry, $"unknown xref entry type {type}", where);
                return null;
        }
    }

    private static (int W0, int W1, int W2) ReadWidths(PdfDictionary dict, int offset)
    {
        if (dict["W"] is not PdfArray { Count: 3 } w
            || w[0] is not PdfInteger { Value: >= 0 and <= 8 } a
            || w[1] is not PdfInteger { Value: >= 0 and <= 8 } b
            || w[2] is not PdfInteger { Value: >= 0 and <= 8 } c)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidXref, "xref stream /W must be three integers in 0..8", offset);
            return default;
        }

        if (a.Value + b.Value + c.Value == 0)
            ParseContext.Fatal(PdfErrorCode.InvalidXref, "xref stream /W describes empty records", offset);
        return ((int)a.Value, (int)b.Value, (int)c.Value);
    }

    private static List<(long Start, long Count)> ReadIndex(PdfDictionary dict, long size, ParseContext ctx, int offset)
    {
        var result = new List<(long, long)>();
        switch (dict["Index"])
        {
            case null:
                result.Add((0, size));
                break;
            case PdfArray ia when ia.Count % 2 == 0:
                if (ia.Count / 2 > ctx.Limits.MaxArrayLength)
                    ParseContext.ExceedLimit(PdfLimit.ArrayLength, "xref /Index too long", offset);
                for (int i = 0; i < ia.Count; i += 2)
                {
                    if (ia[i] is not PdfInteger { Value: >= 0 } s || ia[i + 1] is not PdfInteger { Value: >= 0 } c)
                    {
                        ParseContext.Fatal(PdfErrorCode.InvalidXref, "xref /Index holds a non-integer or negative value", offset);
                        return result;
                    }

                    result.Add((s.Value, c.Value));
                }

                break;
            default:
                ParseContext.Fatal(PdfErrorCode.InvalidXref, "xref /Index must be an array of integer pairs", offset);
                break;
        }

        return result;
    }

    private static void MergeHybrid(ReadOnlyMemory<byte> mem, ParseContext ctx, XrefSection table, int xrefStmOffset)
    {
        var hybrid = ReadStream(mem, ctx, xrefStmOffset, hybridPart: true);
        foreach (var (num, entry) in hybrid.Entries)
        {
            if (table.Entries.TryGetValue(num, out var existing))
            {
                // Agreeing duplicates (both free, or the identical in-use entry) are harmless.
                if (existing == entry)
                    continue;
                if (existing.Type == XrefEntryType.Free && entry.Type == XrefEntryType.Compressed)
                {
                    table.Entries[num] = entry;
                    table.HybridObjects.Add(num);
                    continue;
                }

                ctx.Violation(PdfErrorCode.HybridConflict, $"object {num}: table entry {existing.Type} disagrees with /XRefStm entry {entry.Type}", xrefStmOffset);
            }
            else if (entry.Type == XrefEntryType.Compressed)
            {
                if (num >= table.Size)
                    ctx.Violation(PdfErrorCode.InvalidXref, $"object {num} is not below /Size {table.Size}", xrefStmOffset);
                table.Entries[num] = entry;
                table.HybridObjects.Add(num);
            }
            else
            {
                ctx.Violation(PdfErrorCode.HybridConflict, $"/XRefStm has a {entry.Type} entry for object {num} that the table does not list", xrefStmOffset);
            }
        }
    }

    /// <summary>Checks the trailer keys every reader depends on and returns /Size, /Prev, /XRefStm.</summary>
    private static (long Size, long? Prev, int? XRefStm) ReadTrailerFields(PdfDictionary trailer, ParseContext ctx, int fileLength, long where)
    {
        if (trailer.ContainsKey("Encrypt"))
            ParseContext.Fatal(PdfErrorCode.UnsupportedEncryption, "encrypted documents are not supported", where);

        long size = 0;
        if (trailer["Size"] is PdfInteger { Value: >= 1 } sz)
        {
            size = sz.Value;
            if (size > (long)ctx.Limits.MaxObjectNumber + 1)
                ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "/Size above the object-number limit", where);
        }
        else
        {
            ctx.Violation(PdfErrorCode.InvalidTrailer, "trailer /Size is missing or not a positive integer", where);
        }

        long? prev = null;
        if (trailer.ContainsKey("Prev"))
        {
            if (trailer["Prev"] is PdfInteger { Value: >= 1 } pv && pv.Value < fileLength)
                prev = pv.Value;
            else
                ctx.Violation(PdfErrorCode.InvalidTrailer, "trailer /Prev is not a valid offset", where);
        }

        int? xrefStm = null;
        if (trailer.ContainsKey("XRefStm"))
        {
            if (trailer["XRefStm"] is PdfInteger { Value: >= 1 } xv && xv.Value < fileLength)
                xrefStm = (int)xv.Value;
            else
                ctx.Violation(PdfErrorCode.InvalidTrailer, "trailer /XRefStm is not a valid offset", where);
        }

        if (trailer.ContainsKey("Root") && trailer["Root"] is not PdfReference)
            ctx.Violation(PdfErrorCode.InvalidTrailer, "trailer /Root is not an indirect reference", where);

        return (size, prev, xrefStm);
    }

    private static void CheckAgainstSize(int number, long size, ParseContext ctx, long where)
    {
        if (number >= size)
            ctx.Violation(PdfErrorCode.InvalidXref, $"object {number} is not below the trailer /Size {size}", where);
    }

    /// <summary>
    /// "startxref N %%EOF" right after a section. A section without one is allowed (the first-page xref stream of a
    /// linearized file has none; the document's final marker follows the last section instead), but a marker that is
    /// present must be well formed.
    /// </summary>
    private static void ReadEofMarker(ReadOnlySpan<byte> data, XrefSection section, int after, ParseContext ctx)
    {
        int p = PdfBytes.SkipWhitespace(data, after);
        if (!PdfBytes.KeywordAt(data, p, "startxref"u8))
            return;

        p = PdfBytes.SkipWhitespace(data, p + 9);
        int s = p;
        long v = 0;
        while (p < data.Length && PdfBytes.IsDigit(data[p]) && p - s < 10)
        {
            v = (v * 10) + (data[p] - '0');
            p++;
        }

        if (p == s)
        {
            ctx.Violation(PdfErrorCode.InvalidStartXref, "'startxref' is not followed by an offset", p);
            return;
        }

        section.StartXrefValue = v;
        p = PdfBytes.SkipWhitespace(data, p);
        if (p <= data.Length - 5 && data.Slice(p, 5).SequenceEqual("%%EOF"u8))
            section.EofEnd = p + 5;
        else
            ctx.Violation(PdfErrorCode.MissingEof, "'%%EOF' does not follow 'startxref'", p);
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> data, ref int pos, int maxDigits, string what)
    {
        int s = pos;
        long v = 0;
        while (pos < data.Length && PdfBytes.IsDigit(data[pos]))
        {
            if (pos - s >= maxDigits)
                ParseContext.Fatal(PdfErrorCode.InvalidXref, $"{what} has too many digits", pos);
            v = (v * 10) + (data[pos] - '0');
            pos++;
        }

        if (pos == s)
            ParseContext.Fatal(PdfErrorCode.InvalidXref, $"{what} expected", pos);
        return v;
    }

    private static int RequireWhitespace(ReadOnlySpan<byte> data, int pos)
    {
        int p = PdfBytes.SkipWhitespace(data, pos);
        if (p == pos)
            ParseContext.Fatal(PdfErrorCode.InvalidXref, "whitespace expected", pos);
        return p;
    }

    private static long ParseDigits(ReadOnlySpan<byte> digits)
    {
        long v = 0;
        foreach (byte b in digits)
            v = (v * 10) + (b - '0');
        return v;
    }

    private static long ReadBigEndian(byte[] data, int pos, int width)
    {
        ulong v = 0;
        for (int i = 0; i < width; i++)
            v = (v << 8) | data[pos + i];
        return v > long.MaxValue ? long.MaxValue : (long)v;
    }
}
