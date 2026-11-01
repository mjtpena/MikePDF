using System.Text;

namespace Quire.UI.Tests;

/// <summary>Makes small PDFs for the journeys (the fixtures are one or two pages). Written to a temporary folder that the test removes.</summary>
internal static class TestPdfs
{
    /// <summary>A PDF of <paramref name="pages"/> US Letter pages, each with its number in large type, so a screenshot shows which page it is.</summary>
    public static string Numbered(string directory, string name, int pages, string size = "0 0 612 792")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var objects = new List<string>();
        // 1 catalog, 2 pages, 3 font, then (page, content) pairs.
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + (2 * i)} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var i = 0; i < pages; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [{size}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R >>");
            var content = $"BT /F1 160 Tf 150 420 Td ({i + 1}) Tj ET\n0.1 0.5 0.6 rg 50 50 100 40 re f";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

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
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }
}
