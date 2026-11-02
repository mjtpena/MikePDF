using System.Globalization;
using System.Text;

namespace MikePDF.Integration.Tests.Support;

/// <summary>Assembles a PDF object by object (classic xref), for tests of the reading services that need pages, links and bookmarks.</summary>
internal sealed class PdfAssembly
{
    private readonly List<string> _bodies = [];

    public int Reserve()
    {
        _bodies.Add(string.Empty);
        return _bodies.Count;
    }

    public void Set(int number, string body) => _bodies[number - 1] = body;

    public int Add(string body)
    {
        var number = Reserve();
        Set(number, body);
        return number;
    }

    public int AddStream(string content) => Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

    public byte[] Build(int root, string trailerExtra = "")
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < _bodies.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(_bodies[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(_bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(_bodies.Count + 1).Append(" /Root ").Append(root).Append(" 0 R ").Append(trailerExtra).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}

/// <summary>
/// A three page document: page 1 has two lines of text and links of every kind, page 2 is small, page 3 has a CropBox and /Rotate 90.
/// Bookmarks: "Chapter One" (with two children), "Chapter Two", and a web address.
/// </summary>
internal static class ReaderPdf
{
    public const string FirstLine = "Hello World";
    public const string SecondLine = "second line of text";

    public static byte[] Build()
    {
        var pdf = new PdfAssembly();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var a = pdf.Reserve();
        var b = pdf.Reserve();
        var c = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var contentA = pdf.AddStream($"BT /F1 12 Tf 72 700 Td ({FirstLine}) Tj ET\nBT /F1 12 Tf 72 680 Td ({SecondLine}) Tj ET");
        var contentB = pdf.AddStream("BT /F1 12 Tf 20 350 Td (Page two here) Tj ET");
        var contentC = pdf.AddStream("BT /F1 20 Tf 150 300 Td (Rotated) Tj ET");
        var outlines = pdf.Reserve();
        var one = pdf.Reserve();
        var two = pdf.Reserve();
        var three = pdf.Reserve();
        var oneA = pdf.Reserve();
        var oneB = pdf.Reserve();

        int Link(string rect, string rest) => pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [{rect}] /Border [0 0 0] {rest} >>");
        var l1 = Link("72 695 160 712", $"/A << /S /GoTo /D [{b} 0 R /XYZ 20 350 0] >>");
        var l2 = Link("72 660 200 676", "/A << /S /URI /URI (https://example.org/a%20b) >>");
        var l3 = Link("10 10 50 30", $"/Dest [{c} 0 R /FitH 500]");
        var l4 = Link("60 10 90 30", "/A << /S /Launch /F (calc.exe) >>");
        var l5 = Link("100 10 130 30", $"/Dest [{b} 0 R /Fit]");
        var annots = string.Join(' ', new[] { l1, l2, l3, l4, l5 }.Select(n => n + " 0 R"));

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{a} 0 R {b} 0 R {c} 0 R] /Count 3 >>");
        var resources = $"/Resources << /Font << /F1 {font} 0 R >> >>";
        pdf.Set(a, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] {resources} /Contents {contentA} 0 R /Annots [{annots}] >>");
        pdf.Set(b, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 300 400] {resources} /Contents {contentB} 0 R >>");
        pdf.Set(c, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /CropBox [100 150 500 650] /Rotate 90 {resources} /Contents {contentC} 0 R >>");
        pdf.Set(outlines, $"<< /Type /Outlines /First {one} 0 R /Last {three} 0 R /Count 3 >>");
        pdf.Set(one, $"<< /Title (Chapter One) /Parent {outlines} 0 R /Next {two} 0 R /First {oneA} 0 R /Last {oneB} 0 R /Count 2 /Dest [{a} 0 R /XYZ 72 700 0] >>");
        pdf.Set(two, $"<< /Title (Chapter Two) /Parent {outlines} 0 R /Prev {one} 0 R /Next {three} 0 R /A << /S /GoTo /D [{b} 0 R /Fit] >> >>");
        pdf.Set(three, $"<< /Title (Web) /Parent {outlines} 0 R /Prev {two} 0 R /A << /S /URI /URI (https://example.org/) >> >>");
        pdf.Set(oneA, $"<< /Title (Section 1.1) /Parent {one} 0 R /Next {oneB} 0 R /Dest [{b} 0 R /FitH 300] >>");
        pdf.Set(oneB, $"<< /Title (Section 1.2) /Parent {one} 0 R /Prev {oneA} 0 R /Dest [{c} 0 R /XYZ null 400 null] >>");
        return pdf.Build(catalog);
    }

    /// <summary>One page with <paramref name="characters"/> letters "a" in a row, to make a page whose text needs several windows.</summary>
    public static byte[] LongText(int characters)
    {
        var pdf = new PdfAssembly();
        var catalog = pdf.Reserve();
        var pages = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = pdf.AddStream($"BT /F1 1 Tf 10 50 Td ({new string('a', characters)}) Tj ET");
        var page = pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        return pdf.Build(catalog);
    }
}
