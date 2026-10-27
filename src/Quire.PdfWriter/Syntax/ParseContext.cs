using System.Diagnostics.CodeAnalysis;

namespace Quire.PdfWriter;

/// <summary>
/// Per-parse state shared by every component: the limits, the strict/lenient policy, recovered issues and the
/// decompression budget.
///
/// Policy is centralised in <see cref="Violation"/>: strict mode throws, lenient mode records the issue and returns
/// so the caller can apply its documented recovery. <see cref="Fatal"/> and <see cref="ExceedLimit"/> always throw.
/// </summary>
internal sealed class ParseContext(ParserLimits limits, bool strict)
{
    /// <summary>Cap on recorded lenient issues, so a hostile file cannot make the diagnostic list itself a memory problem.</summary>
    private const int MaxRecordedIssues = 1000;

    private readonly List<PdfError> _issues = [];

    public ParserLimits Limits { get; } = limits;

    public bool Strict { get; } = strict;

    public IReadOnlyList<PdfError> Issues => _issues;

    public bool IssuesTruncated { get; private set; }

    public long DecompressedTotal { get; private set; }

    public int XrefEntryCount { get; set; }

    public int MaxDepth => Limits.EffectiveMaxNestingDepth;

    /// <summary>A deviation from the spec that a strict reader must refuse. Returns only in lenient mode.</summary>
    public void Violation(PdfErrorCode code, string message, long offset = -1)
    {
        var error = new PdfError(code, message, offset);
        if (Strict)
            throw new PdfParseException(error);
        if (_issues.Count < MaxRecordedIssues)
            _issues.Add(error);
        else
            IssuesTruncated = true;
    }

    /// <summary>An error with no sensible recovery, in either mode.</summary>
    [DoesNotReturn]
    public static void Fatal(PdfErrorCode code, string message, long offset = -1) =>
        throw new PdfParseException(new PdfError(code, message, offset));

    [DoesNotReturn]
    public static void ExceedLimit(PdfLimit limit, string message, long offset = -1) =>
        throw new PdfParseException(new PdfError(PdfErrorCode.LimitExceeded, message, offset, limit));

    /// <summary>
    /// Records decompressed bytes against the document-wide budget; throws if they would not fit. Rejected bytes are not
    /// counted, so <see cref="DecompressedTotal"/> never exceeds the limit.
    /// </summary>
    public void ChargeDecompressed(long bytes)
    {
        if (bytes > Limits.MaxDecompressedTotalBytes - DecompressedTotal)
            ExceedLimit(PdfLimit.DecompressedTotalBytes, "total decompressed bytes exceed the limit");
        DecompressedTotal += bytes;
    }

    /// <summary>Bytes still available in the document-wide decompression budget.</summary>
    public long RemainingDecompressionBudget => Math.Max(0, Limits.MaxDecompressedTotalBytes - DecompressedTotal);
}
