using System.Text;
using Quire.PdfWriter;
using Quire.Protocol;

namespace Quire.WriterHost;

/// <summary>ValidateStructure: a strict parse, then <see cref="StructureValidator"/>, reported within the schema's limits.</summary>
internal static class Validation
{
    // Schema limits of ValidateStructureReply (src/Quire.Protocol/schema/quire.protocol.json).
    internal const int MaxIssues = 256;
    internal const int MaxMessageBytes = 192;

    public static ValidateStructureReply Run(ReadOnlyMemory<byte> data)
    {
        var report = StructureValidator.Validate(data);
        return Report(report);
    }

    /// <summary>Errors first, then warnings, each group in the order found; at most 256, each message cut to 192 UTF-8 bytes on a character boundary.</summary>
    internal static ValidateStructureReply Report(ValidationReport report)
    {
        var ordered = report.Issues.OrderByDescending(static i => i.Severity).ToList();
        var kept = ordered.Take(MaxIssues).ToList();
        return new ValidateStructureReply
        {
            Ok = report.IsValid,
            Truncated = ordered.Count > kept.Count,
            IssueCodes = [.. kept.Select(static i => (uint)i.Code)],
            IssueMessages = [.. kept.Select(static i => Bounded((i.Severity == ValidationSeverity.Error ? "error: " : "warning: ") + i.Message))],
        };
    }

    /// <summary>Strict UTF-8 without NUL, at most <see cref="MaxMessageBytes"/> bytes, never a split character.</summary>
    internal static string Bounded(string text)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            // A lone surrogate was already replaced by U+FFFD when the string was enumerated.
            if (rune.Value == 0)
            {
                continue;
            }

            var size = rune.Utf8SequenceLength;
            if (bytes + size > MaxMessageBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        return builder.ToString();
    }
}
