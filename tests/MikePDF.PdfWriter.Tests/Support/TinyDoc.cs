using System.Globalization;

namespace MikePDF.PdfWriter.Tests.Support;

/// <summary>
/// A small document under the test's control for the hostile and limit cases: <c>n</c> pages with a marker each, to which the test adds annotations, an
/// outline, names and whatever else it needs before <see cref="Build"/>. Object numbers: 1 catalog, 2 page tree, 3 font, then the pages, then their contents.
/// </summary>
internal sealed class TinyDoc
{
    private readonly Dictionary<int, string> _pageExtra = [];

    public TinyDoc(int pages, string marker = "T")
    {
        Pdf = new MiniPdf();
        Catalog = Pdf.Reserve();
        Tree = Pdf.Reserve();
        Font = Pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Pages = Pdf.Reserve(pages);
        Marker = marker;
    }

    public MiniPdf Pdf { get; }

    public int Catalog { get; }

    public int Tree { get; }

    public int Font { get; }

    public int[] Pages { get; }

    public string Marker { get; }

    /// <summary>Entries added to page <paramref name="index"/>'s dictionary (for example <c>/Annots [5 0 R]</c>).</summary>
    public TinyDoc OnPage(int index, string entries)
    {
        _pageExtra[index] = _pageExtra.TryGetValue(index, out var existing) ? existing + " " + entries : entries;
        return this;
    }

    /// <summary>A destination array for page <paramref name="index"/>.</summary>
    public string Dest(int index, string fit = "/Fit") => $"[{Pages[index]} 0 R {fit}]";

    /// <summary>Writes the page contents and the page dictionaries, then the catalog with <paramref name="catalogEntries"/>, and returns the file.</summary>
    public byte[] Build(string catalogEntries = "", bool xrefStream = false, string? tree = null)
    {
        for (var i = 0; i < Pages.Length; i++)
        {
            var content = Pdf.AddStream("", string.Create(CultureInfo.InvariantCulture, $"BT /F1 24 Tf 20 100 Td (PAGE-{Marker}-{i + 1:D2}) Tj ET"));
            var extra = _pageExtra.TryGetValue(i, out var e) ? " " + e : "";
            Pdf.Set(Pages[i], $"<< /Type /Page /Parent {Tree} 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 {Font} 0 R >> >> /Contents {content} 0 R{extra} >>");
        }

        Pdf.Set(Tree, tree ?? $"<< /Type /Pages /Kids [{string.Join(' ', Pages.Select(n => $"{n} 0 R"))}] /Count {Pages.Length} >>");
        Pdf.Set(Catalog, $"<< /Type /Catalog /Pages {Tree} 0 R {catalogEntries} >>");
        return xrefStream ? Pdf.WriteXrefStream(Catalog) : Pdf.WriteClassic(Catalog);
    }
}
