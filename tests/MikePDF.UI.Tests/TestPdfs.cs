using System.Text;

namespace MikePDF.UI.Tests;

/// <summary>Makes small PDFs for the journeys (the fixtures are one or two pages). Written to a temporary folder that the test removes.</summary>
internal static class TestPdfs
{
    /// <summary>A PDF of <paramref name="pages"/> US Letter pages, each with its number in large type, so a screenshot shows which page it is.</summary>
    public static string Numbered(string directory, string name, int pages, string size = "0 0 612 792", string? info = null, string catalogExtra = "")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var objects = new List<string>();
        // 1 catalog, 2 pages, 3 font, then (page, content) pairs.
        objects.Add("<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>");
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

        if (info is not null)
        {
            offsets.Add(pdf.Length);
            objects.Add(info);
            pdf.Append(objects.Count).Append(" 0 obj\n<< ").Append(info).Append(" >>\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R").Append(info is null ? string.Empty : " /Info " + objects.Count + " 0 R").Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }

    /// <summary>
    /// One US Letter page with a red square and a 16 x 16 colour picture (no white in it, so the engine counts it a photograph) drawn 300 pt square. Its top-left corner is green.
    /// </summary>
    public static string WithPhoto(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var hex = new StringBuilder();
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                hex.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{40 + (x * 6):X2}{200 - (y * 3):X2}{60 + (((x + y) * 5) % 100):X2}"));
            }
        }

        var content = $"1 0 0 rg 50 50 100 100 re f\nq 300 0 0 300 150 400 cm\nBI /W 16 /H 16 /CS /RGB /BPC 8 /F /AHx ID\n{hex}>\nEI\nQ\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }
}
