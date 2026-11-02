using MikePDF.Fuzz.Shared;

namespace MikePDF.PdfWriter.Fuzz;

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
    public static void ParseAndValidate(ReadOnlySpan<byte> input) => _ = ParseOutcome(input.ToArray());

    /// <summary>
    /// <see cref="ParseAndValidate"/>, returning a summary of the verdicts (strict and lenient error codes, the validator's issue codes, a size
    /// class) that the mutational fuzzer, which has no coverage feedback, uses to tell an input that behaves differently from the ones it has.
    /// </summary>
    public static long ParseOutcome(byte[] data)
    {
        var strict = StrictDocument.Open(data, Limits);
        var lenient = LenientDocument.Open(data, Limits);
        var report = StructureValidator.Validate(data, Limits);
        long signature = 29;
        signature = MutationalFuzzer.Mix(signature, strict.TryGetError(out var strictError) ? (long)strictError.Code : -1);
        signature = MutationalFuzzer.Mix(signature, lenient.TryGetError(out var lenientError) ? (long)lenientError.Code : -1);
        foreach (var code in report.Issues.Select(i => i.Code).Distinct().Order())
            signature = MutationalFuzzer.Mix(signature, 100 + (long)code);
        if (strict.TryGetValue(out var parsed))
            signature = MutationalFuzzer.Mix(signature, 1000 + System.Numerics.BitOperations.Log2((uint)parsed.XrefEntryCount + 1));

        if (lenient.TryGetValue(out var lenientDoc))
        {
            foreach (var entry in lenientDoc.Latest.EnumerateEffectiveEntries().Take(100))
                _ = lenientDoc.GetObject(entry.Key);
            Check(lenientDoc.DecompressedBytes <= Limits.MaxDecompressedTotalBytes, "lenient decompression budget exceeded");
        }

        if (!strict.TryGetValue(out var doc))
        {
            Check(!report.IsValid, "a file the strict parser refused was reported valid");
            return signature;
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
        return signature;
    }

    /// <summary>Strict parse, then an incremental append that rewrites a few parsed objects; the output must reopen strictly and keep the input as a prefix.</summary>
    public static void AppendRoundTrip(ReadOnlySpan<byte> input) => _ = AppendOutcome(input.ToArray());

    /// <summary><see cref="AppendRoundTrip"/>, returning a summary of the verdicts for the mutational fuzzer.</summary>
    public static long AppendOutcome(byte[] data)
    {
        var opened = StrictDocument.Open(data, Limits);
        if (!opened.TryGetValue(out var doc))
            return 1 + (100_000 * (long)opened.Error.Code);

        var update = new IncrementalUpdate(doc);
        if (update.Add(new PdfDictionary([new(new PdfName("Fuzz"), new PdfInteger(1))])).TryGetError(out _))
            return 2;

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
            return 3 + 10 * (long)error.Code;
        }

        byte[] output = built.Value;
        Check(output.AsSpan(0, data.Length).SequenceEqual(data), "original bytes are not a prefix of the output");
        Check(StrictDocument.Open(output, Limits).IsSuccess, "output does not reopen strictly");
        return 4 + 1000 * taken;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvariantViolationException(message);
    }
}

internal sealed class InvariantViolationException(string message) : Exception(message);
