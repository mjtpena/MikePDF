using System.Globalization;
using System.Text;
using MikePDF.Fuzz.Shared;
using MikePDF.PdfWriter.Assembly;

namespace MikePDF.PdfWriter.Fuzz;

/// <summary>
/// The fuzz target of the page assembler (page-organiser PO-27): arbitrary bytes as a source, merged with a built-in partner document that has an
/// outline, named destinations, page labels, a form and links, through four plans (the first pages in order; reversed with the first page duplicated; a
/// merge with outline wrappers; every other page of the last few). Like the other targets it catches nothing: an exception that escapes, a hang, or a
/// broken invariant is the finding. An input the strict parser refuses must come back as a refusal, never as a file.
/// </summary>
internal static class AssembleFuzzTargets
{
    // Small enough that each execution is fast, tight enough that the limit paths are reached.
    private static readonly AssemblerLimits Limits = new()
    {
        MaxOutputPages = 200,
        MaxSourcePages = 5_000,
        MaxOutputObjects = 200_000,
        MaxOutputBytes = 16 << 20,
        MaxOutlineItems = 2_000,
        MaxNamedDestinations = 2_000,
        MaxTreeNodes = 2_000,
        MaxPageLabelRanges = 2_000,
        MaxFieldNodes = 4_000,
        MaxAnnotationsPerPage = 2_000,
        MaxOptionalContentGroups = 2_000,
    };

    private static readonly byte[] Partner = BuildPartner();

    public static void Assemble(ReadOnlySpan<byte> input) => _ = AssembleOutcome(input.ToArray());

    public static long AssembleOutcome(byte[] data)
    {
        var options = new AssemblerOptions
        {
            ParserLimits = FuzzTargets.Limits,
            Limits = Limits,
            DocumentId = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray(),
            ModificationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var set = PageAssembler.Open([new AssemblerSource("input", data), new AssemblerSource("partner", Partner)], options);
        Check(set.Sources.Count == 2, "a source went missing from the status list");
        Check(set.Sources[1].IsUsable, "the built-in partner document was refused: " + set.Sources[1].Refusal);
        var status = set.Sources[0];
        var strict = StrictDocument.Open(data, FuzzTargets.Limits);
        long signature = MutationalFuzzer.Mix(31, status.IsUsable ? 1 : 0);
        if (!status.IsUsable)
        {
            var refusal = status.Refusal!;
            Check(Enum.IsDefined(refusal.Reason), "a refusal without a defined reason");
            Check(refusal.Reason != AssemblyRefusalReason.OutputInvalid, "OutputInvalid at open");
            // A file the strict parser refuses is refused for a parser reason (or a limit). The converse does not hold: the strict open reads only the cross-reference
            // chain, and the assembler also parses the catalog and the page tree, where one object can be what the parser refuses.
            var fromParser = refusal.Reason is AssemblyRefusalReason.SourceNotReadable or AssemblyRefusalReason.SourceEncrypted or AssemblyRefusalReason.SourceUnsupported;
            Check(!strict.IsFailure() || fromParser || refusal.Reason == AssemblyRefusalReason.LimitExceeded, "the strict parser refused a file the assembler took");
            return MutationalFuzzer.Mix(signature, 100 + (long)refusal.Reason);
        }

        Check(!strict.IsFailure(), "the assembler took a file the strict parser refuses");
        var pages = status.Info!.PageCount;
        Check(pages >= 1, "a usable source with no pages");
        signature = MutationalFuzzer.Mix(signature, System.Numerics.BitOperations.Log2((uint)pages + 1));

        var last = Math.Min(pages, 12);
        var plans = new (string Name, AssemblyPlan Plan, bool Wrap)[]
        {
            ("first", AssemblyPlan.Range(0, 0, last - 1), false),
            ("reversed", new AssemblyPlan([.. Enumerable.Range(0, last).Reverse().Select(static p => new SourcePage(0, p)), new SourcePage(0, 0)]), false),
            ("merge", new AssemblyPlan([new SourcePage(1, 0), new SourcePage(1, 1), new SourcePage(1, 2), .. Enumerable.Range(0, Math.Min(pages, 3)).Select(static p => new SourcePage(0, p)), new SourcePage(1, 0)]), true),
            ("alternate", AssemblyPlan.Pick(0, Enumerable.Range(Math.Max(0, pages - 20), Math.Min(pages, 20)).Where(static p => p % 2 == 0)), false),
        };
        foreach (var (name, plan, wrap) in plans)
        {
            if (plan.Pages.Count == 0)
                continue;
            // The wrapper option belongs to the options a set was opened with, so a merge with wrappers opens its own.
            var result = wrap
                ? PageAssembler.Assemble([new AssemblerSource("input", data), new AssemblerSource("partner", Partner)], plan, options with { WrapSourceOutlines = true })
                : set.Assemble(plan);
            if (!result.IsSuccess)
            {
                var refusal = result.Refusal;
                Check(Enum.IsDefined(refusal.Reason), name + ": a refusal without a defined reason");
                Check(refusal.Reason is not (AssemblyRefusalReason.OutputInvalid or AssemblyRefusalReason.InvalidPlan or AssemblyRefusalReason.Cancelled or AssemblyRefusalReason.SourceRefused), $"{name}: refused as {refusal.Reason}: {refusal.Message}");
                signature = MutationalFuzzer.Mix(signature, 200 + (long)refusal.Reason);
                continue;
            }

            var output = result.Output;
            Check(output.PageCount == plan.Pages.Count, name + ": the page count is not the plan's");
            Check(output.Bytes.Length == output.BytesWritten, name + ": BytesWritten is not the size");
            var reopened = StrictDocument.Open(output.Bytes, FuzzTargets.Limits with { MaxFileSize = Limits.MaxOutputBytes });
            Check(reopened.IsSuccess, name + ": the output does not reopen strictly");
            var report = StructureValidator.Validate(reopened.Value, new ValidationOptions { DanglingReferencesAreErrors = true });
            Check(report.IsValid, name + ": the output is not structurally valid: " + report.Issues.FirstOrDefault(static i => i.Severity == ValidationSeverity.Error)?.Message);
            signature = MutationalFuzzer.Mix(signature, (name.Length * 1000L) + System.Numerics.BitOperations.Log2((uint)output.ObjectCount + 1));
            signature = MutationalFuzzer.Mix(signature, output.Warnings.Count + (output.Statistics.LinksRemoved > 0 ? 100 : 0) + (output.Statistics.OutlineEntriesKept > 0 ? 200 : 0));
        }

        // The same assembly twice gives the same bytes (nothing in it depends on iteration order or the clock).
        var first = set.Assemble(plans[0].Plan);
        var again = set.Assemble(plans[0].Plan);
        Check(first.IsSuccess == again.IsSuccess, "the same plan succeeded once and was refused once");
        if (first.IsSuccess)
            Check(first.Output.Bytes.Span.SequenceEqual(again.Output.Bytes.Span), "the same plan gave different bytes");
        return signature;
    }

    private static bool IsFailure(this PdfResult<StrictDocument> result) => !result.IsSuccess;

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvariantViolationException(message);
    }

    /// <summary>Three pages with a link each, an outline, named destinations, page labels and a text field, so every part of the assembler has something to merge with.</summary>
    private static byte[] BuildPartner()
    {
        var objects = new SortedDictionary<int, string>();
        string Ref(int n) => string.Create(CultureInfo.InvariantCulture, $"{n} 0 R");
        // 1 catalog, 2 pages, 3 font, 4-6 pages, 7-9 contents, 10-12 links, 13 widget, 14 outline root, 15-16 items, 17 names, 18 acroform
        objects[3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>";
        for (var i = 0; i < 3; i++)
        {
            objects[7 + i] = $"<< /Length 0 >>\nstream\n\nendstream";
            objects[10 + i] = $"<< /Type /Annot /Subtype /Link /Rect [10 10 60 30] /Dest [{Ref(4 + ((i + 1) % 3))} /Fit] >>";
            var annots = i == 1 ? $"{Ref(10 + i)} {Ref(13)}" : Ref(10 + i);
            objects[4 + i] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {Ref(7 + i)} /Annots [{annots}] >>";
        }

        objects[13] = $"<< /Type /Annot /Subtype /Widget /Rect [10 100 100 120] /P {Ref(5)} /FT /Tx /T (name) /V (partner) /DA (/Helv 12 Tf 0 g) >>";
        objects[14] = $"<< /Type /Outlines /First {Ref(15)} /Last {Ref(16)} /Count 2 >>";
        objects[15] = $"<< /Title (First) /Parent {Ref(14)} /Next {Ref(16)} /Dest [{Ref(4)} /Fit] >>";
        objects[16] = $"<< /Title (Second) /Parent {Ref(14)} /Prev {Ref(15)} /Dest [{Ref(6)} /Fit] >>";
        objects[17] = $"<< /Names [(dest-1) [{Ref(4)} /Fit] (dest-3) [{Ref(6)} /Fit]] >>";
        objects[18] = $"<< /Fields [{Ref(13)}] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv {Ref(3)} >> >> >>";
        objects[2] = $"<< /Type /Pages /Kids [{Ref(4)} {Ref(5)} {Ref(6)}] /Count 3 >>";
        objects[1] = $"<< /Type /Catalog /Pages {Ref(2)} /Outlines {Ref(14)} /Names << /Dests {Ref(17)} >> /AcroForm {Ref(18)} /PageLabels << /Nums [0 << /S /r >>] >> >>";

        var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = output.Position;
            Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n"));
        }

        var xref = output.Position;
        var size = objects.Keys.Max() + 1;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {size}\n0000000000 65535 f \n"));
        for (var n = 1; n < size; n++)
            Write(string.Create(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n"));
        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }
}
