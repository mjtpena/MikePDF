using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MikePDF.PdfWriter;

public sealed record IncrementalOptions
{
    /// <summary>
    /// Second element of the new trailer /ID. Null (the default) means 16 random bytes. The first element is always
    /// kept from the base document, so the permanent identifier survives the update (14.4).
    /// </summary>
    public ReadOnlyMemory<byte>? NewIdSecondElement { get; init; }

    /// <summary>
    /// Re-parses the output strictly and checks the prefix property, the new section, every written object and every
    /// reference they make. On by default; turning it off is for benchmarks only.
    /// </summary>
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>
/// Builds an incremental update (7.5.6) for a strictly parsed document: new and replaced objects plus a new
/// cross-reference section, appended after the unchanged original bytes.
///
/// The new section is written in the same form as the document's newest one (a classic table after a table, an xref
/// stream after a stream, including the stream's own entry). A hybrid base gets a plain table. Appearance and annotation
/// generation are not part of this class; callers supply finished objects.
/// </summary>
public sealed class IncrementalUpdate
{
    private readonly StrictDocument _base;
    private readonly SortedDictionary<int, PdfObject> _objects = [];
    private readonly Dictionary<int, int> _generations = [];
    private int _nextNumber;

    public IncrementalUpdate(StrictDocument baseDocument)
    {
        ArgumentNullException.ThrowIfNull(baseDocument);
        _base = baseDocument;
        _nextNumber = baseDocument.Size;
    }

    /// <summary>The object number the next <see cref="Add"/> will use.</summary>
    public int NextObjectNumber => _nextNumber;

    public IReadOnlyCollection<int> ChangedObjectNumbers => _objects.Keys;

    /// <summary>Allocates a new object number for <paramref name="obj"/> (generation 0).</summary>
    public PdfResult<PdfReference> Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (CheckObject(obj) is { } bad)
            return PdfResult.Fail<PdfReference>(bad);
        if (_nextNumber > _base.Limits.MaxObjectNumber - 1)
            return PdfResult.Fail<PdfReference>(new PdfError(PdfErrorCode.LimitExceeded, "no object numbers left", Limit: PdfLimit.ObjectNumber));
        int number = _nextNumber++;
        _objects[number] = obj;
        _generations[number] = 0;
        return PdfResult.Ok(new PdfReference(number, 0));
    }

    /// <summary>Replaces an existing in-use object, keeping its object and generation number.</summary>
    public PdfResult<PdfReference> Replace(int number, PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (CheckObject(obj) is { } bad)
            return PdfResult.Fail<PdfReference>(bad);

        if (_objects.ContainsKey(number) && number >= _base.Size)
        {
            _objects[number] = obj; // replacing an object this update added
            return PdfResult.Ok(new PdfReference(number, 0));
        }

        if (number < 1 || !_base.Latest.TryGetEntry(number, out var entry) || entry.Type == XrefEntryType.Free)
            return PdfResult.Fail<PdfReference>(new PdfError(PdfErrorCode.InvalidUpdate, $"object {number} is not an in-use object of the base document"));

        // Replacing a container the xref depends on would orphan every object inside it.
        var existing = _base.GetObject(number);
        if (existing.TryGetError(out var error))
            return PdfResult.Fail<PdfReference>(error);
        if (existing.Value is PdfStream { Dictionary: var d } && d.GetName("Type") is "XRef" or "ObjStm")
            return PdfResult.Fail<PdfReference>(new PdfError(PdfErrorCode.InvalidUpdate, $"object {number} is a structural stream and cannot be replaced"));

        _objects[number] = obj;
        _generations[number] = entry.Generation;
        return PdfResult.Ok(new PdfReference(number, entry.Generation));
    }

    /// <summary>
    /// Returns the complete new file: the original bytes unchanged, then the update. Never called without changes
    /// (ADR-0008 stage 1): an empty update is an error.
    /// </summary>
    public PdfResult<byte[]> Build(IncrementalOptions? options = null) =>
        PdfDocument.Guard(() => BuildCore(options ?? new IncrementalOptions()));

    private static PdfError? CheckObject(PdfObject obj)
    {
        if (obj is PdfStream { Dictionary: var d } && d.GetName("Type") is "XRef" or "ObjStm")
            return new PdfError(PdfErrorCode.InvalidUpdate, "xref streams and object streams are written by the writer, not supplied");
        return null;
    }

    private byte[] BuildCore(IncrementalOptions options)
    {
        if (_objects.Count == 0)
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "an incremental update with no changes is refused");

        var baseData = _base.Data;
        var limits = _base.Limits;
        var latest = _base.Latest;
        bool useStream = latest.Form == PdfXrefForm.Stream;
        int baseLength = baseData.Length;

        if (latest.Trailer["Root"] is not PdfReference root)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "base document has no /Root");
            return [];
        }

        int xrefObjectNumber = useStream ? _nextNumber : -1;
        int newSize = useStream ? _nextNumber + 1 : _nextNumber;
        if (newSize - 1 > limits.MaxObjectNumber)
            ParseContext.ExceedLimit(PdfLimit.ObjectNumber, "update needs an object number above the limit");

        long maxTail = (long)limits.EffectiveMaxFileSize - baseLength;
        if (maxTail <= 0)
            ParseContext.ExceedLimit(PdfLimit.OutputSize, "base document already fills the size limit");
        var sink = new ByteSink(maxTail);

        // The previous %%EOF may not have been followed by a line ending; the update must start on a fresh line.
        var baseSpan = baseData.Span;
        if (baseLength > 0 && !PdfBytes.IsEol(baseSpan[^1]))
            sink.WriteByte((byte)'\n');

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        var written = new Dictionary<int, PdfObject>();
        foreach (var (number, obj) in _objects)
        {
            int gen = _generations[number];
            offsets[number] = (baseLength + sink.Length, gen);
            written[number] = WriteIndirectObject(sink, number, gen, obj);
        }

        long xrefOffset = baseLength + sink.Length;
        PdfObject? info = latest.Trailer["Info"];
        PdfArray? id = BuildId(latest.Trailer["ID"], options);

        if (useStream)
        {
            offsets[xrefObjectNumber] = (xrefOffset, 0);
            WriteXrefStream(sink, latest, offsets, xrefObjectNumber, newSize, root, info, id);
        }
        else
        {
            WriteXrefTable(sink, offsets, newSize, root, info, id);
        }

        sink.Write("startxref\n"u8);
        sink.WriteAscii(xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sink.Write("\n%%EOF\n"u8);

        var output = new byte[baseLength + sink.Length];
        baseSpan.CopyTo(output);
        sink.AsSpan().CopyTo(output.AsSpan(baseLength));

        if (options.VerifyOutput)
            Verify(output, baseSpan, latest, xrefOffset, offsets, written);
        return output;
    }

    /// <summary>Writes "N G obj ... endobj". For a stream, /Length is set to the real length. Returns the object as it was written.</summary>
    private PdfObject WriteIndirectObject(ByteSink sink, int number, int generation, PdfObject obj)
    {
        sink.WriteAscii(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{number} {generation} obj\n"));
        PdfObject result = obj;
        if (obj is PdfStream stream)
        {
            var dict = stream.Dictionary.With("Length", new PdfInteger(stream.RawData.Length));
            PdfSerializer.WriteDictionary(sink, dict, 0, _base.Limits.EffectiveMaxNestingDepth);
            sink.Write("\nstream\n"u8);
            sink.Write(stream.RawData.Span);
            sink.Write("\nendstream"u8);
            result = new PdfStream(dict, stream.RawData);
        }
        else
        {
            PdfSerializer.Write(sink, obj, 0, _base.Limits.EffectiveMaxNestingDepth);
        }

        sink.Write("\nendobj\n"u8);
        return result;
    }

    private static PdfArray? BuildId(PdfObject? existing, IncrementalOptions options)
    {
        if (existing is null)
            return null;
        if (existing is not PdfArray { Count: 2 } arr || arr[0] is not PdfString first || arr[1] is not PdfString)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "trailer /ID is not an array of two strings");
            return null;
        }

        ReadOnlySpan<byte> second = options.NewIdSecondElement is { } fixedId ? fixedId.Span : RandomNumberGenerator.GetBytes(16);
        return new PdfArray([first, new PdfString(second, PdfStringFormat.Hex)]);
    }

    private void WriteXrefTable(ByteSink sink, SortedDictionary<int, (long Offset, int Generation)> offsets, int newSize, PdfReference root, PdfObject? info, PdfObject? id)
    {
        sink.Write("xref\n"u8);
        foreach (var (start, count) in Group(offsets.Keys))
        {
            sink.WriteAscii(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{start} {count}\n"));
            for (int n = start; n < start + count; n++)
            {
                var (offset, gen) = offsets[n];
                // 7.5.4: exactly 20 bytes per entry; "SP LF" is one of the three allowed end-of-line forms.
                sink.WriteAscii(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} {gen:D5} n \n"));
            }
        }

        sink.Write("trailer\n"u8);
        PdfSerializer.WriteDictionary(sink, TrailerDictionary(newSize, root, info, id, extra: null), 0, _base.Limits.EffectiveMaxNestingDepth);
        sink.WriteByte((byte)'\n');
    }

    private void WriteXrefStream(ByteSink sink, PdfDocumentRevision previous, SortedDictionary<int, (long Offset, int Generation)> offsets, int xrefNumber, int newSize, PdfReference root, PdfObject? info, PdfObject? id)
    {
        // W [1 4 2]: type, a 32-bit offset (files are capped below 2 GiB) and a 16-bit generation.
        const int recordSize = 7;
        var raw = new byte[offsets.Count * recordSize];
        var index = new List<PdfObject>();
        int p = 0;
        foreach (var (start, count) in Group(offsets.Keys))
        {
            index.Add(new PdfInteger(start));
            index.Add(new PdfInteger(count));
            for (int n = start; n < start + count; n++)
            {
                var (offset, gen) = offsets[n];
                raw[p] = 1;
                BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(p + 1), (uint)offset);
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(p + 5), (ushort)gen);
                p += recordSize;
            }
        }

        // Match the previous xref stream's encoding: Flate (+ PNG Up predictor) if it used it, nothing otherwise.
        bool flate = UsesFlate(previous.Trailer, out bool predictor);
        byte[] payload = raw;
        var extra = new List<KeyValuePair<PdfName, PdfObject>>();
        if (flate)
        {
            payload = StreamDecoder.Deflate(predictor ? StreamDecoder.EncodePngUp(raw, recordSize) : raw);
            extra.Add(new(new PdfName("Filter"), new PdfName("FlateDecode")));
            if (predictor)
            {
                extra.Add(new(new PdfName("DecodeParms"), new PdfDictionary(
                [
                    new(new PdfName("Columns"), new PdfInteger(recordSize)),
                    new(new PdfName("Predictor"), new PdfInteger(12)),
                ])));
            }
        }

        var head = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(new PdfName("Type"), new PdfName("XRef")),
            new(new PdfName("Size"), new PdfInteger(newSize)),
            new(new PdfName("W"), new PdfArray([new PdfInteger(1), new PdfInteger(4), new PdfInteger(2)])),
            new(new PdfName("Index"), new PdfArray(index)),
        };
        var dict = TrailerDictionary(newSize, root, info, id, extra, head);
        dict = dict.With("Length", new PdfInteger(payload.Length));

        sink.WriteAscii(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{xrefNumber} 0 obj\n"));
        PdfSerializer.WriteDictionary(sink, dict, 0, _base.Limits.EffectiveMaxNestingDepth);
        sink.Write("\nstream\n"u8);
        sink.Write(payload);
        sink.Write("\nendstream\nendobj\n"u8);
    }

    private PdfDictionary TrailerDictionary(int newSize, PdfReference root, PdfObject? info, PdfObject? id, List<KeyValuePair<PdfName, PdfObject>>? extra, List<KeyValuePair<PdfName, PdfObject>>? head = null)
    {
        var entries = head ?? [new(new PdfName("Size"), new PdfInteger(newSize))];
        entries.Add(new(new PdfName("Root"), root));
        entries.Add(new(new PdfName("Prev"), new PdfInteger(_base.StartXref)));
        if (info is not null)
            entries.Add(new(new PdfName("Info"), info));
        if (id is not null)
            entries.Add(new(new PdfName("ID"), id));
        if (extra is not null)
            entries.AddRange(extra);
        return new PdfDictionary(entries);
    }

    private static bool UsesFlate(PdfDictionary previousXrefDict, out bool predictor)
    {
        predictor = false;
        var filter = previousXrefDict["Filter"];
        bool flate = filter is PdfName { Value: "FlateDecode" or "Fl" }
            || (filter is PdfArray { Count: 1 } fa && fa[0] is PdfName { Value: "FlateDecode" or "Fl" });
        if (!flate)
            return false;
        var parms = previousXrefDict["DecodeParms"];
        if (parms is PdfArray { Count: 1 } pa)
            parms = pa[0];
        predictor = parms is PdfDictionary pd && pd["Predictor"] is PdfInteger { Value: >= 10 };
        return true;
    }

    /// <summary>Groups ascending object numbers into runs of consecutive numbers.</summary>
    private static List<(int Start, int Count)> Group(IEnumerable<int> numbers)
    {
        var runs = new List<(int, int)>();
        int start = -1;
        int count = 0;
        foreach (int n in numbers)
        {
            if (count > 0 && n == start + count)
            {
                count++;
                continue;
            }

            if (count > 0)
                runs.Add((start, count));
            start = n;
            count = 1;
        }

        if (count > 0)
            runs.Add((start, count));
        return runs;
    }

    private void Verify(byte[] output, ReadOnlySpan<byte> baseSpan, PdfDocumentRevision previous, long xrefOffset, SortedDictionary<int, (long Offset, int Generation)> offsets, Dictionary<int, PdfObject> written)
    {
        static void Fail(string message) => ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "output self-check failed: " + message);

        if (!output.AsSpan(0, baseSpan.Length).SequenceEqual(baseSpan))
            Fail("original bytes are not an exact prefix");

        var reopened = StrictDocument.Open(output, _base.Limits);
        if (reopened.TryGetError(out var error))
            Fail("output does not reparse strictly: " + error);
        var doc = reopened.Value;
        if (doc.Revisions.Count != _base.Revisions.Count + 1)
            Fail("revision count did not grow by exactly one");
        if (doc.StartXref != xrefOffset || doc.Latest.Form != previous.Form)
            Fail("new section is misplaced or not in the previous section's form");
        if (doc.Latest.PrevOffset != _base.StartXref)
            Fail("/Prev does not point at the previous section");
        if (!PdfObjects.DeepEquals(doc.Trailer["Root"], previous.Trailer["Root"]))
            Fail("/Root changed");

        foreach (var (number, expected) in written)
        {
            if (!doc.Latest.TryGetEntry(number, out var entry) || entry.Type != XrefEntryType.InUse || entry.Offset != offsets[number].Offset)
                Fail($"object {number} has the wrong xref entry");
            var loaded = doc.Latest.GetObject(number);
            if (loaded.TryGetError(out var loadError))
                Fail($"object {number} does not load: {loadError}");
            if (!PdfObjects.DeepEquals(loaded.Value, expected))
                Fail($"object {number} does not read back as written");

            foreach (var reference in PdfObjects.EnumerateReferences(expected))
            {
                if (!doc.Latest.TryGetEntry(reference.Number, out var target) || target.Type == XrefEntryType.Free || target.Generation != reference.Generation)
                    Fail($"object {number} refers to {reference}, which does not exist");
            }
        }
    }
}
