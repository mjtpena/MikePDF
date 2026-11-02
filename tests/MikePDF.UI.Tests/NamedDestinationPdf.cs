using System.Globalization;
using System.Text;

namespace MikePDF.UI.Tests;

/// <summary>
/// A four page document whose links and bookmarks go to places by name (docs/features/m2-reader-gaps.md, N8): names in the catalog's name tree, in
/// the old /Dests dictionary, through a GoTo action, and one name that does not exist. Page 1 lists the links as text; pages 2 to 4 are numbered.
/// </summary>
internal static class NamedDestinationPdf
{
    public const string AlphaLine = "Jump to alpha";
    public const string BetaLine = "Jump to beta";
    public const string OldLine = "Jump to the old style name";
    public const string NowhereLine = "Jump to nowhere";

    /// <summary>The page (1-based) each name leads to.</summary>
    public const int AlphaPage = 3;
    public const int BetaPage = 4;
    public const int OldPage = 2;

    public static string Create(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var objects = new List<string>();
        int Add(string body)
        {
            objects.Add(body);
            return objects.Count;
        }

        int Reserve() => Add(string.Empty);
        var catalog = Reserve();
        var pages = Reserve();
        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var page1 = Reserve();
        var page2 = Reserve();
        var page3 = Reserve();
        var page4 = Reserve();
        var content1 = Stream("BT /F1 20 Tf 72 700 Td (" + AlphaLine + ") Tj ET\nBT /F1 20 Tf 72 660 Td (" + BetaLine + ") Tj ET\nBT /F1 20 Tf 72 620 Td (" + OldLine + ") Tj ET\nBT /F1 20 Tf 72 580 Td (" + NowhereLine + ") Tj ET", Add);
        int Numbered(int n) => Stream($"BT /F1 160 Tf 150 420 Td ({n}) Tj ET", Add);
        var content2 = Numbered(2);
        var content3 = Numbered(3);
        var content4 = Numbered(4);

        var kid = Add($"<< /Limits [(alpha) (beta)] /Names [(alpha) [{page3} 0 R /XYZ 72 400 0] (beta) << /D [{page4} 0 R /FitH 700] >>] >>");
        var tree = Add($"<< /Kids [{kid} 0 R] >>");
        string Link(string rect, string rest) => Add($"<< /Type /Annot /Subtype /Link /Rect [{rect}] /Border [0 0 0] {rest} >>") + " 0 R";
        var annots = string.Join(' ', new[]
        {
            Link("72 696 260 716", "/Dest (alpha)"),
            Link("72 656 260 676", "/A << /S /GoTo /D (beta) >>"),
            Link("72 616 400 636", "/Dest /OldName"),
            Link("72 576 260 596", "/Dest (nonesuch)"),
        });

        var outlines = Reserve();
        var byName = Reserve();
        var byAction = Reserve();
        var unknown = Reserve();
        objects[outlines - 1] = $"<< /Type /Outlines /First {byName} 0 R /Last {unknown} 0 R /Count 3 >>";
        objects[byName - 1] = $"<< /Title (Alpha place) /Parent {outlines} 0 R /Next {byAction} 0 R /Dest (alpha) >>";
        objects[byAction - 1] = $"<< /Title (Beta place) /Parent {outlines} 0 R /Prev {byName} 0 R /Next {unknown} 0 R /A << /S /GoTo /D (beta) >> >>";
        objects[unknown - 1] = $"<< /Title (Nowhere) /Parent {outlines} 0 R /Prev {byAction} 0 R /Dest (nope) >>";

        objects[catalog - 1] = $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /Names << /Dests {tree} 0 R >> /Dests << /OldName [{page2} 0 R /Fit] >> >>";
        objects[pages - 1] = $"<< /Type /Pages /Kids [{page1} 0 R {page2} 0 R {page3} 0 R {page4} 0 R] /Count 4 >>";
        string Page(int content, string extra = "") => $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R {extra} >>";
        objects[page1 - 1] = Page(content1, $"/Annots [{annots}]");
        objects[page2 - 1] = Page(content2);
        objects[page3 - 1] = Page(content3);
        objects[page4 - 1] = Page(content4);

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root ").Append(catalog).Append(" 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }

    private static int Stream(string content, Func<string, int> add) => add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
}
