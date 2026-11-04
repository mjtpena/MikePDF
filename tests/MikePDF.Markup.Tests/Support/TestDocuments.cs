using System.Globalization;
using System.Text;
using MikePDF.Core.Editing;
using MikePDF.Core.Geometry;
using MikePDF.PdfWriter;
using MikePDF.PdfWriter.Editing;
using MikePDF.Protocol.Edits;

namespace MikePDF.Markup.Tests.Support;

/// <summary>A page of a test document: its boxes and /Rotate.</summary>
internal sealed record PageSpec(double[] MediaBox, double[]? CropBox = null, int Rotate = 0, string? Content = null)
{
    public static PageSpec Letter { get; } = new([0, 0, 612, 792]);
}

/// <summary>Builds small classic-xref PDFs, and runs the save pipeline's stage 4 on them in this process (the sandboxed host is exercised by the integration tests).</summary>
internal static class TestDocuments
{
    public static byte[] Build(params PageSpec[] pages)
    {
        if (pages.Length == 0)
        {
            pages = [PageSpec.Letter];
        }

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Object(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{3 + i} 0 R"))}] /Count {pages.Length} >>");
        var contentNumber = 3 + pages.Length;
        foreach (var page in pages)
        {
            var box = string.Join(' ', page.MediaBox.Select(static v => v.ToString(CultureInfo.InvariantCulture)));
            var crop = page.CropBox is null ? string.Empty : " /CropBox [" + string.Join(' ', page.CropBox.Select(static v => v.ToString(CultureInfo.InvariantCulture))) + "]";
            var rotate = page.Rotate == 0 ? string.Empty : $" /Rotate {page.Rotate}";
            var contents = page.Content is null ? string.Empty : $" /Contents {contentNumber++} 0 R";
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [{box}]{crop}{rotate}{contents} >>");
        }

        foreach (var page in pages.Where(static p => p.Content is not null))
        {
            Object("<< /Length " + page.Content!.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + page.Content + "\nendstream");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    public static StrictDocument Open(byte[] bytes)
    {
        var opened = StrictDocument.Open(bytes);
        return opened.TryGetValue(out var document) ? document : throw new InvalidOperationException("strict open failed: " + opened.Error);
    }

    /// <summary>The pending commands become the blob, the blob goes through its codec (as it does to the host), and stage 4 appends it.</summary>
    public static byte[] Save(byte[] original, IReadOnlyList<EditCommand> pending, int pageCount, out EditBlob blob)
    {
        blob = AnnotationEditMapper.ToBlob(pending, pageCount);
        var wire = EditBlobCodec.Encode(blob);
        Assert.True(EditBlobCodec.TryDecode(wire, out var decoded, out var error), error);
        var changes = decoded.Edits.Select(MikePDF.WriterHost.EditConversion.ToChange).ToList();
        var appended = AnnotationAppender.Append(Open(original), pageCount, changes, new AppendOptions { Modified = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero) });
        Assert.True(appended.TryGetValue(out var result), appended.TryGetError(out var failure) ? failure.Message : string.Empty);
        return result.Output;
    }

    public static byte[] Save(byte[] original, EditModel model, int pageCount) => Save(original, model.PendingCommands, pageCount, out _);

    /// <summary>The structural validator finds no error the original did not have.</summary>
    public static void AssertSound(byte[] original, byte[] saved)
    {
        static Dictionary<ValidationCode, int> Errors(byte[] bytes) =>
            StructureValidator.Validate(bytes).Issues.Where(static i => i.Severity == ValidationSeverity.Error).GroupBy(static i => i.Code).ToDictionary(static g => g.Key, static g => g.Count());
        var allowed = Errors(original);
        foreach (var (code, count) in Errors(saved))
        {
            Assert.True(count <= allowed.GetValueOrDefault(code), $"the save added {count - allowed.GetValueOrDefault(code)} validation error(s) of kind {code}");
        }

        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes are not an exact prefix of the output");
    }

    /// <summary>The page's annotation dictionaries, resolved, in /Annots order.</summary>
    public static IReadOnlyList<(PdfReference? Reference, PdfDictionary Dictionary)> Annotations(StrictDocument document, int page)
    {
        var pages = PageTree.Enumerate(document).Value;
        var list = pages[page].Dictionary["Annots"];
        if (list is null || Res(document, list) is not PdfArray array)
        {
            return [];
        }

        return [.. array.Select(item => (item as PdfReference, (PdfDictionary)(item is PdfReference r ? document.GetObject(r.Number).Value! : item)))];
    }

    /// <summary>Follows a reference (anything else is itself).</summary>
    public static PdfObject? Res(StrictDocument document, PdfObject? value) => value is PdfReference r ? document.GetObject(r.Number).Value : value;

    public static PdfDictionary Dictionary(StrictDocument document, PdfObject? value) => (PdfDictionary)Res(document, value)!;

    public static string StreamText(StrictDocument document, PdfStream stream) => Encoding.Latin1.GetString(document.DecodeStream(stream).Value);

    public static PdfStream Appearance(StrictDocument document, PdfDictionary annotation) =>
        (PdfStream)Res(document, Dictionary(document, annotation["AP"])["N"])!;

    public static double[] Numbers(StrictDocument document, PdfObject? value) =>
        [.. ((PdfArray)Res(document, value)!).Select(item => Res(document, item) switch { PdfInteger i => (double)i.Value, PdfReal r => r.Value, _ => double.NaN })];

    public static PdfRect Rect(StrictDocument document, PdfDictionary annotation)
    {
        var n = Numbers(document, annotation["Rect"]);
        return new PdfRect(n[0], n[1], n[2], n[3]);
    }
}
