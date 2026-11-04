using System.Globalization;

namespace MikePDF.PdfWriter;

/// <summary>What <see cref="TailNormalizer"/> may do besides rewriting the cross-reference section.</summary>
public sealed record TailNormalizeOptions
{
    /// <summary>
    /// For a check box or radio button widget whose appearance state names are the ones the original had, put the original's appearance dictionary back and drop PDFium's rewrite
    /// of its state streams; PDFium's <c>/AS</c> and <c>/V</c> stay (forms 10.2, S4 surprise 6). A widget whose state names changed keeps PDFium's output.
    /// </summary>
    public bool RestoreCheckAndRadioStreams { get; init; } = true;

    /// <summary>Remove every dictionary key PDFium adds for itself (names starting with <c>PDFIUM_</c>, such as <c>/PDFIUM_HasGeneratedAP</c>).</summary>
    public bool StripPdfiumKeys { get; init; } = true;

    /// <summary>Second element of the new trailer /ID; null means 16 random bytes.</summary>
    public ReadOnlyMemory<byte>? NewIdSecondElement { get; init; }
}

/// <summary>What a normalisation did.</summary>
public sealed record TailNormalizeResult(byte[] Output, int ObjectsRewritten, int ObjectsAdded, int ObjectsDropped, int WidgetsRestored, int KeysStripped);

/// <summary>
/// Stage 3 of the save pipeline (save-pipeline 3.3, ADR-0008 decision 3 step 3): repairs the tail PDFium appends in its incremental save.
///
/// PDFium's incremental update is never just the new part, and for a file whose newest cross-reference section is a stream it is not valid (S4 F9: the appended stream lacks
/// <c>/Type /XRef</c> and its <c>endobj</c>, and has no entry for itself). Patching the damage in place is forbidden; this reads the objects PDFium wrote with the strict parser,
/// drops its cross-reference section and writes the original bytes followed by those objects and a new section in the form of the original's newest one, through
/// <see cref="IncrementalUpdate"/>, whose output is re-parsed and checked before it is returned.
///
/// The PDFium file must begin with the original's exact bytes (an incremental save always does), its last <c>startxref</c> must point into the appended part, its trailer must name
/// the original's <c>/Root</c> and <c>/Info</c>, and every appended object must either replace an in-use object of the original (same generation) or have a number the original
/// does not use. Anything else is refused, never guessed at: a duplicate key, an object the strict parser cannot read, an indirect <c>/Length</c>.
/// </summary>
public static class TailNormalizer
{
    private const string PrivateKeyPrefix = "PDFIUM_";
    private const int MaxNesting = 64;

    /// <param name="original">The file as it was opened.</param>
    /// <param name="pdfium">The engine's incremental save of it.</param>
    /// <param name="options">What to restore and strip; null means everything.</param>
    public static PdfResult<TailNormalizeResult> Normalize(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> pdfium, TailNormalizeOptions? options = null) =>
        PdfDocument.Guard(() => NormalizeCore(original, pdfium, options ?? new TailNormalizeOptions()));

    private static TailNormalizeResult NormalizeCore(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> pdfium, TailNormalizeOptions options)
    {
        var opened = StrictDocument.Open(original);
        if (opened.TryGetError(out var openError))
        {
            throw new PdfParseException(openError);
        }

        var baseDocument = opened.Value;
        if (pdfium.Length <= original.Length)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "the PDFium file is not longer than the original");
        }

        if (!pdfium.Span[..original.Length].SequenceEqual(original.Span))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "the PDFium file does not begin with the original's bytes");
        }

        var limits = baseDocument.Limits;
        if (pdfium.Length > limits.EffectiveMaxFileSize)
        {
            ParseContext.ExceedLimit(PdfLimit.FileSize, "the PDFium file exceeds the size limit");
        }

        var data = pdfium.Span;
        var context = new ParseContext(limits, strict: true);
        int tailStart = PdfBytes.SkipWhitespace(data, original.Length);
        int xrefOffset = FindFinalXref(data, tailStart, out var trailerRoot, out var trailerInfo, context, pdfium);
        CheckTrailer(baseDocument, trailerRoot, trailerInfo);

        // The objects PDFium appended, in file order, up to the cross-reference section.
        var objects = new SortedDictionary<int, PdfObject>();
        var parser = new ObjectParser(pdfium, context);
        var position = tailStart;
        while (position < xrefOffset)
        {
            var parsed = parser.ParseIndirectObject(position, reference => ResolveLength(objects, reference));
            if (parsed.End > xrefOffset)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "an appended object runs into the cross-reference section", parsed.Start);
            }

            if (objects.ContainsKey(parsed.Number))
            {
                ParseContext.Fatal(PdfErrorCode.DuplicateXrefEntry, $"object {parsed.Number} is appended twice", parsed.Start);
            }

            if (parsed.Number < 1 || parsed.Number > limits.MaxObjectNumber)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidUpdate, $"object number {parsed.Number} is outside the limits", parsed.Start);
            }

            CheckGeneration(baseDocument, parsed);
            objects[parsed.Number] = parsed.Value;
            position = PdfBytes.SkipWhitespace(data, parsed.End);
        }

        if (position != xrefOffset)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "bytes between the last appended object and the cross-reference section are not an object", position);
        }

        if (objects.Count == 0)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "PDFium appended no objects");
        }

        var keysStripped = 0;
        if (options.StripPdfiumKeys)
        {
            foreach (var number in objects.Keys.ToList())
            {
                objects[number] = StripPrivateKeys(objects[number], ref keysStripped, 0);
            }
        }

        var restored = options.RestoreCheckAndRadioStreams ? RestoreAppearances(baseDocument, objects) : 0;
        var dropped = DropUnreachable(baseDocument, objects);

        var update = new IncrementalUpdate(baseDocument);
        var added = 0;
        foreach (var (number, value) in objects)
        {
            var written = number < baseDocument.Size ? update.Replace(number, value) : update.AddAt(number, value);
            if (written.TryGetError(out var writeError))
            {
                throw new PdfParseException(writeError);
            }

            if (number >= baseDocument.Size)
            {
                added++;
            }
        }

        var built = update.Build(new IncrementalOptions { NewIdSecondElement = options.NewIdSecondElement, VerifyOutput = true });
        if (built.TryGetError(out var buildError))
        {
            throw new PdfParseException(buildError);
        }

        return new TailNormalizeResult(built.Value, objects.Count - added, added, dropped, restored, keysStripped);
    }

    // ---- the appended cross-reference section -------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the section the final <c>startxref</c> points at and reads its trailer (a table's <c>trailer</c> dictionary, or the dictionary of a cross-reference stream) for /Root and
    /// /Info. Nothing else of the section is trusted or kept.
    /// </summary>
    private static int FindFinalXref(ReadOnlySpan<byte> data, int tailStart, out PdfObject? root, out PdfObject? info, ParseContext context, ReadOnlyMemory<byte> memory)
    {
        var keyword = data.LastIndexOf("startxref"u8);
        if (keyword < tailStart)
        {
            ParseContext.Fatal(PdfErrorCode.MissingStartXref, "the PDFium file has no startxref after the original");
        }

        var p = PdfBytes.SkipWhitespace(data, keyword + 9);
        var digits = p;
        long offset = 0;
        while (p < data.Length && PdfBytes.IsDigit(data[p]) && p - digits < 10)
        {
            offset = (offset * 10) + (data[p] - '0');
            p++;
        }

        if (p == digits || offset < tailStart || offset >= keyword)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidStartXref, "the final startxref does not point into the appended part", p);
        }

        p = PdfBytes.SkipWhitespace(data, p);
        if (!PdfBytes.KeywordAt(data, p, "%%EOF"u8) && !(p + 5 <= data.Length && data.Slice(p, 5).SequenceEqual("%%EOF"u8)))
        {
            ParseContext.Fatal(PdfErrorCode.MissingEof, "the PDFium file does not end with %%EOF", p);
        }

        var section = (int)offset;
        var parser = new ObjectParser(memory, context, section);
        PdfDictionary trailer;
        if (PdfBytes.KeywordAt(data, section, "xref"u8))
        {
            var at = data[section..].IndexOf("trailer"u8);
            if (at < 0 || section + at >= keyword)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "the appended table has no trailer", section);
            }

            parser.Position = section + at + 7;
            trailer = parser.ParseObject() as PdfDictionary ?? throw new PdfParseException(new PdfError(PdfErrorCode.InvalidTrailer, "the appended trailer is not a dictionary", section));
        }
        else
        {
            // "N G obj << ... >> stream": the dictionary of PDFium's cross-reference stream, which has no /Type and no endobj. Only the dictionary is read.
            var header = PdfBytes.SkipWhitespace(data, section);
            var objKeyword = data[header..].IndexOf("obj"u8);
            if (objKeyword < 0 || header + objKeyword + 3 >= keyword)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidXref, "the appended cross-reference section is neither a table nor an object", section);
            }

            parser.Position = header + objKeyword + 3;
            trailer = parser.ParseObject() as PdfDictionary ?? throw new PdfParseException(new PdfError(PdfErrorCode.InvalidXref, "the appended cross-reference stream has no dictionary", section));
        }

        root = trailer["Root"];
        info = trailer["Info"];
        return section;
    }

    private static void CheckTrailer(StrictDocument baseDocument, PdfObject? root, PdfObject? info)
    {
        if (root is not PdfReference || !PdfObjects.DeepEquals(root, baseDocument.Trailer["Root"]))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "the PDFium trailer names another /Root than the original's");
        }

        if (!PdfObjects.DeepEquals(info, baseDocument.Trailer["Info"]))
        {
            ParseContext.Fatal(PdfErrorCode.InvalidTrailer, "the PDFium trailer names another /Info than the original's");
        }
    }

    private static long? ResolveLength(SortedDictionary<int, PdfObject> parsedSoFar, PdfReference reference) =>
        parsedSoFar.TryGetValue(reference.Number, out var value) && value is PdfInteger length ? length.Value : null;

    private static void CheckGeneration(StrictDocument baseDocument, ParsedIndirectObject parsed)
    {
        if (parsed.Number >= baseDocument.Size)
        {
            if (parsed.Generation != 0)
            {
                ParseContext.Fatal(PdfErrorCode.InvalidUpdate, $"new object {parsed.Number} has generation {parsed.Generation}", parsed.Start);
            }

            return;
        }

        if (!baseDocument.Latest.TryGetEntry(parsed.Number, out var entry) || entry.Type == XrefEntryType.Free)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, $"PDFium rewrote object {parsed.Number}, which the original does not use", parsed.Start);
        }

        if (entry.Generation != parsed.Generation)
        {
            ParseContext.Fatal(PdfErrorCode.InvalidUpdate, $"PDFium rewrote object {parsed.Number} with generation {parsed.Generation}, the original has {entry.Generation}", parsed.Start);
        }
    }

    // ---- PDFium's private keys ---------------------------------------------------------------------------------------------------------

    private static PdfObject StripPrivateKeys(PdfObject value, ref int stripped, int depth)
    {
        if (depth > MaxNesting)
        {
            ParseContext.ExceedLimit(PdfLimit.NestingDepth, "nesting too deep while looking for PDFium's private keys");
        }

        switch (value)
        {
            case PdfDictionary dictionary:
                var entries = new List<KeyValuePair<PdfName, PdfObject>>(dictionary.Count);
                var changed = false;
                foreach (var entry in dictionary)
                {
                    if (entry.Key.Value.StartsWith(PrivateKeyPrefix, StringComparison.Ordinal))
                    {
                        stripped++;
                        changed = true;
                        continue;
                    }

                    var inner = StripPrivateKeys(entry.Value, ref stripped, depth + 1);
                    changed |= !ReferenceEquals(inner, entry.Value);
                    entries.Add(new KeyValuePair<PdfName, PdfObject>(entry.Key, inner));
                }

                return changed ? new PdfDictionary(entries) : dictionary;
            case PdfArray array:
                var items = new List<PdfObject>(array.Count);
                var arrayChanged = false;
                foreach (var item in array)
                {
                    var inner = StripPrivateKeys(item, ref stripped, depth + 1);
                    arrayChanged |= !ReferenceEquals(inner, item);
                    items.Add(inner);
                }

                return arrayChanged ? new PdfArray(items) : array;
            case PdfStream stream:
                var dictionaryAfter = (PdfDictionary)StripPrivateKeys(stream.Dictionary, ref stripped, depth + 1);
                return ReferenceEquals(dictionaryAfter, stream.Dictionary) ? stream : new PdfStream(dictionaryAfter, stream.RawData);
            default:
                return value;
        }
    }

    // ---- check boxes and radio buttons -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A widget whose appearance has named states (/AP /N is a dictionary, not a stream) and the same state names as in the original gets the original's /AP back, and the state streams
    /// PDFium rewrote in place are dropped so that the original objects stay in force. Returns the number of widgets restored.
    /// </summary>
    private static int RestoreAppearances(StrictDocument baseDocument, SortedDictionary<int, PdfObject> objects)
    {
        var restored = 0;
        var overwritten = new HashSet<int>();
        foreach (var number in objects.Keys.ToList())
        {
            if (number >= baseDocument.Size || objects[number] is not PdfDictionary now || now.GetName("Subtype") != "Widget")
            {
                continue;
            }

            var before = baseDocument.GetObject(number);
            if (before.TryGetError(out _) || before.Value is not PdfDictionary then || then.GetName("Subtype") != "Widget")
            {
                continue;
            }

            var oldStates = StateStreams(baseDocument, then);
            var newStates = StateNames(baseDocument, now, objects);
            if (oldStates is null || newStates is null || oldStates.Count == 0 || !oldStates.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newStates))
            {
                continue;
            }

            if (then["AP"] is not { } originalAppearance)
            {
                continue;
            }

            objects[number] = now.With("AP", originalAppearance);
            foreach (var reference in oldStates.Values)
            {
                overwritten.Add(reference.Number);
            }

            restored++;
        }

        foreach (var number in overwritten)
        {
            if (number < baseDocument.Size)
            {
                objects.Remove(number); // PDFium's artwork over the author's: the original stays
            }
        }

        return restored;
    }

    // The widget's normal-appearance state streams as references, by state name; null when the appearance has no named states.
    private static Dictionary<string, PdfReference>? StateStreams(StrictDocument document, PdfDictionary widget)
    {
        var n = NormalAppearance(document, widget, null);
        if (n is null)
        {
            return null;
        }

        var result = new Dictionary<string, PdfReference>(StringComparer.Ordinal);
        foreach (var entry in n)
        {
            if (entry.Value is not PdfReference reference)
            {
                return null; // a direct stream cannot exist, and anything else is not a state stream
            }

            result[entry.Key.Value] = reference;
        }

        return result;
    }

    private static HashSet<string>? StateNames(StrictDocument document, PdfDictionary widget, SortedDictionary<int, PdfObject> objects)
    {
        var n = NormalAppearance(document, widget, objects);
        return n?.Select(e => e.Key.Value).ToHashSet(StringComparer.Ordinal);
    }

    private static PdfDictionary? NormalAppearance(StrictDocument document, PdfDictionary widget, SortedDictionary<int, PdfObject>? tail)
    {
        PdfObject? Resolve(PdfObject? value)
        {
            if (value is PdfReference reference)
            {
                if (tail is not null && tail.TryGetValue(reference.Number, out var local))
                {
                    return local;
                }

                var loaded = document.GetObject(reference.Number);
                return loaded.TryGetValue(out var found) ? found : null;
            }

            return value;
        }

        return Resolve(widget["AP"]) is PdfDictionary appearance && Resolve(appearance["N"]) is PdfDictionary states && states.Count > 0 ? states : null;
    }

    // ---- unreferenced new objects --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An object PDFium added that nothing in the update refers to cannot be reached from the original either, so it is dead weight (the stream a restored check box no longer uses).
    /// Dropped to a fixed point, since dropping one may orphan another.
    /// </summary>
    private static int DropUnreachable(StrictDocument baseDocument, SortedDictionary<int, PdfObject> objects)
    {
        var dropped = 0;
        while (true)
        {
            var referenced = new HashSet<int>();
            foreach (var value in objects.Values)
            {
                foreach (var reference in PdfObjects.EnumerateReferences(value))
                {
                    referenced.Add(reference.Number);
                }
            }

            var doomed = objects.Keys.Where(n => n >= baseDocument.Size && !referenced.Contains(n)).ToList();
            if (doomed.Count == 0)
            {
                return dropped;
            }

            foreach (var number in doomed)
            {
                objects.Remove(number);
                dropped++;
            }
        }
    }

    internal static string Describe(TailNormalizeResult result) =>
        string.Create(CultureInfo.InvariantCulture, $"{result.ObjectsRewritten} rewritten, {result.ObjectsAdded} added, {result.ObjectsDropped} dropped, {result.WidgetsRestored} widgets restored, {result.KeysStripped} private keys removed");
}
