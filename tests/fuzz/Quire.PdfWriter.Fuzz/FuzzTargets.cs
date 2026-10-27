namespace Quire.PdfWriter.Fuzz;

/// <summary>
/// The fuzz targets. Each takes arbitrary bytes and must return normally for any input: the public API reports bad input as
/// results, never as exceptions. Anything that throws out of here, hangs, or trips one of the invariant checks is a finding.
/// The invariant checks throw <see cref="InvariantViolationException"/> so a fuzzer records them as crashes.
/// </summary>
internal static class FuzzTargets
{
    /// <summary>Small enough that each execution is fast, tight enough that the limit paths are reached.</summary>
    internal static readonly ParserLimits Limits = new()
    {
        MaxFileSize = 2 << 20,
        MaxObjectCount = 50_000,
        MaxObjectNumber = 100_000,
        MaxDecompressedStreamBytes = 4 << 20,
        MaxDecompressedTotalBytes = 16 << 20,
        MaxXrefSections = 32,
    };

    /// <summary>Strict parse, lenient parse, whole-file validation, object and stream access.</summary>
    public static void ParseAndValidate(ReadOnlySpan<byte> input)
    {
        byte[] data = input.ToArray();
        var strict = StrictDocument.Open(data, Limits);
        var lenient = LenientDocument.Open(data, Limits);
        var report = StructureValidator.Validate(data, Limits);

        if (lenient.TryGetValue(out var lenientDoc))
        {
            foreach (var entry in lenientDoc.Latest.EnumerateEffectiveEntries().Take(100))
                _ = lenientDoc.GetObject(entry.Key);
            Check(lenientDoc.DecompressedBytes <= Limits.MaxDecompressedTotalBytes, "lenient decompression budget exceeded");
        }

        if (!strict.TryGetValue(out var doc))
        {
            Check(!report.IsValid, "a file the strict parser refused was reported valid");
            return;
        }

        Check(lenient.IsSuccess, "lenient refused a file strict accepted");
        Check(doc.DecompressedBytes <= Limits.MaxDecompressedTotalBytes, "strict decompression budget exceeded");
        Check(doc.XrefEntryCount <= Limits.MaxObjectCount, "xref entry limit exceeded");
        foreach (var entry in doc.Latest.EnumerateEffectiveEntries().Take(200))
        {
            if (doc.GetObject(entry.Key).TryGetValue(out var obj) && obj is PdfStream stream)
                _ = doc.DecodeStream(stream);
        }

        Check(StructureValidator.Validate(doc).IsValid == report.IsValid, "validator verdicts differ between entry points");
    }

    /// <summary>Strict parse, then an incremental append that rewrites a few parsed objects; the output must reopen strictly and keep the input as a prefix.</summary>
    public static void AppendRoundTrip(ReadOnlySpan<byte> input)
    {
        byte[] data = input.ToArray();
        if (!StrictDocument.Open(data, Limits).TryGetValue(out var doc))
            return;

        var update = new IncrementalUpdate(doc);
        if (update.Add(new PdfDictionary([new(new PdfName("Fuzz"), new PdfInteger(1))])).TryGetError(out _))
            return;

        int taken = 0;
        foreach (var entry in doc.Latest.EnumerateEffectiveEntries())
        {
            if (entry.Key == 0 || entry.Value.Type == XrefEntryType.Free || taken >= 3)
                continue;
            if (!doc.GetObject(entry.Key).TryGetValue(out var obj) || obj is PdfNull)
                continue;
            if (PdfObjects.EnumerateReferences(obj).Any(r => !doc.Latest.TryGetEntry(r.Number, out var e) || e.Type == XrefEntryType.Free || e.Generation != r.Generation))
                continue; // a dangling reference in the original would fail the writer's own self-check, correctly
            if (update.Replace(entry.Key, obj).IsSuccess)
                taken++;
        }

        var built = update.Build();
        if (built.TryGetError(out var error))
        {
            // Only these are legitimate for a base the strict parser accepted: a malformed trailer /ID or a size limit.
            Check(error.Code is PdfErrorCode.InvalidTrailer or PdfErrorCode.LimitExceeded, $"writer self-check failed on a strictly valid base: {error}");
            return;
        }

        byte[] output = built.Value;
        Check(output.AsSpan(0, data.Length).SequenceEqual(data), "original bytes are not a prefix of the output");
        Check(StrictDocument.Open(output, Limits).IsSuccess, "output does not reopen strictly");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvariantViolationException(message);
    }
}

internal sealed class InvariantViolationException(string message) : Exception(message);
