using System.Globalization;
using System.Text;

namespace S12;

/// <summary>Spike PDF generator: a 6-page mixed-size document and a long text document. No dependencies.</summary>
internal sealed class Pdf
{
    readonly List<byte[]?> _o = [];
    public int Add(string body) { _o.Add(Encoding.Latin1.GetBytes(body)); return _o.Count; }
    public int Reserve() { _o.Add(null); return _o.Count; }
    public void Set(int n, string body) => _o[n - 1] = Encoding.Latin1.GetBytes(body);
    public int Stream(string dict, string data) { var c = Encoding.Latin1.GetBytes(data); _o.Add([.. Encoding.Latin1.GetBytes($"<< {dict} /Length {c.Length} >>\nstream\n"), .. c, .. Encoding.Latin1.GetBytes("\nendstream")]); return _o.Count; }
    public int Count => _o.Count;
    public byte[] Build(int root)
    {
        using var ms = new MemoryStream(); ms.Write(Encoding.Latin1.GetBytes("%PDF-1.7\n")); var offs = new List<long>();
        for (var i = 0; i < _o.Count; i++) { offs.Add(ms.Position); ms.Write(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n")); ms.Write(_o[i]!); ms.Write(Encoding.Latin1.GetBytes("\nendobj\n")); }
        var x = ms.Position; var sb = new StringBuilder($"xref\n0 {_o.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {_o.Count + 1} /Root {root} 0 R >>\nstartxref\n{x}\n%%EOF\n"); ms.Write(Encoding.Latin1.GetBytes(sb.ToString())); return ms.ToArray();
    }
}

internal static class Fixtures
{
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static readonly string[] Words = "print job band render spooler driver duplex collate scale page range device raster vector engine broker sandbox memory flat fidelity annotation form field".Split(' ');

    /// <summary>Content for a box [x0,y0,x1,y1] in user space: frame at the edge, diagonal, label, ruler, text, gradient.</summary>
    static string Content(double x0, double y0, double x1, double y1, string label, int seed, int textLines, bool image)
    {
        var w = x1 - x0; var h = y1 - y0; var sb = new StringBuilder(); var rnd = new Random(seed);
        sb.Append($"1 0 0 RG {F(Math.Max(2, w / 150))} w {F(x0 + 1)} {F(y0 + 1)} {F(w - 2)} {F(h - 2)} re S\n");
        sb.Append($"0 0.4 0.8 RG 0.5 w {F(x0)} {F(y0)} m {F(x1)} {F(y1)} l S {F(x0)} {F(y1)} m {F(x1)} {F(y0)} l S\n");
        var fs = Math.Max(10, Math.Min(w, h) / 10);
        sb.Append($"BT /F2 {F(fs)} Tf 0 g {F(x0 + w * 0.08)} {F(y1 - h * 0.1)} Td ({label}) Tj ET\n");
        for (var t = 0.0; t <= w; t += 72) sb.Append($"0 g 0.5 w {F(x0 + t)} {F(y0)} m {F(x0 + t)} {F(y0 + (((int)(t / 72) % 5 == 0) ? 14 : 7))} l S\n");
        var lines = Math.Min(textLines, (int)((h * 0.7) / 13)); var y = y1 - h * 0.18;
        for (var l = 0; l < lines; l++)
        {
            var sbl = new StringBuilder(); var nn = 8 + rnd.Next(7); for (var i = 0; i < nn; i++) sbl.Append(Words[rnd.Next(Words.Length)]).Append(' ');
            sb.Append($"BT /F1 10 Tf 0 g {F(x0 + w * 0.08)} {F(y)} Td ({sbl.ToString().TrimEnd()}) Tj ET\n"); y -= 13;
        }
        for (var i = 0; i < 40; i++) { var g = i / 39.0; sb.Append($"{F(g)} {F(1 - g)} {F(0.5)} rg {F(x0 + w * 0.08 + i * (w * 0.84 / 40))} {F(y0 + h * 0.05)} {F(w * 0.84 / 40 + 0.3)} {F(h * 0.04)} re f\n"); }
        if (image) sb.Append($"q {F(w * 0.3)} 0 0 {F(w * 0.3)} {F(x0 + w * 0.6)} {F(y0 + h * 0.12)} cm /Im0 Do Q\n");
        return sb.ToString();
    }

    static string ImageData()
    {
        var sb = new StringBuilder(120 * 120 * 3);
        for (var y = 0; y < 120; y++) for (var x = 0; x < 120; x++) { sb.Append((char)(x * 2)); sb.Append((char)(y * 2)); sb.Append((char)((x + y) % 256)); }
        return sb.ToString();
    }

    /// <summary>6 pages: Letter, A4, Letter landscape, A4 with /Rotate 90, Letter with a CropBox that does not start at the origin, and a 30 x 40 inch poster.</summary>
    public static byte[] Mix6()
    {
        var p = new Pdf(); p.Add("<< /Type /Catalog /Pages 2 0 R >>"); p.Reserve();
        p.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); p.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");   // 3, 4
        var img = p.Stream("/Type /XObject /Subtype /Image /Width 120 /Height 120 /ColorSpace /DeviceRGB /BitsPerComponent 8", ImageData());         // 5
        var res = p.Add($"<< /Font << /F1 3 0 R /F2 4 0 R >> /XObject << /Im0 {img} 0 R >> >>");                                                      // 6
        var specs = new (string box, string extra, double x0, double y0, double x1, double y1, string label)[]
        {
            ("[0 0 612 792]", "", 0, 0, 612, 792, "1 Letter portrait"),
            ("[0 0 595.28 841.89]", "", 0, 0, 595.28, 841.89, "2 A4 portrait"),
            ("[0 0 792 612]", "", 0, 0, 792, 612, "3 Letter landscape"),
            ("[0 0 595.28 841.89]", "/Rotate 90", 0, 0, 595.28, 841.89, "4 A4 Rotate 90"),
            ("[0 0 700 900]", "/CropBox [50 30 662 822]", 50, 30, 662, 822, "5 Crop 50,30"),
            ("[0 0 2160 2880]", "", 0, 0, 2160, 2880, "6 Poster 30x40 in"),
        };
        var kids = new StringBuilder();
        foreach (var s in specs)
        {
            var pg = p.Count + 1; var c = p.Stream("", Content(s.x0, s.y0, s.x1, s.y1, s.label, pg, 40, true));
            p.Add($"<< /Type /Page /Parent 2 0 R /MediaBox {s.box} {s.extra} /Resources {res} 0 R /Contents {c} 0 R >>"); kids.Append($"{c + 1} 0 R ");
        }
        p.Set(2, $"<< /Type /Pages /Kids [{kids}] /Count {specs.Length} >>");
        return p.Build(1);
    }

    /// <summary>n Letter pages with text, a gradient bar and an image. Used for the 100-page cost measurement.</summary>
    public static byte[] Text(int n)
    {
        var p = new Pdf(); p.Add("<< /Type /Catalog /Pages 2 0 R >>"); p.Reserve();
        p.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); p.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var img = p.Stream("/Type /XObject /Subtype /Image /Width 120 /Height 120 /ColorSpace /DeviceRGB /BitsPerComponent 8", ImageData());
        var res = p.Add($"<< /Font << /F1 3 0 R /F2 4 0 R >> /XObject << /Im0 {img} 0 R >> >>");
        var kids = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var c = p.Stream("", Content(0, 0, 612, 792, $"Page {i + 1}", i + 1, 48, true));
            p.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources {res} 0 R /Contents {c} 0 R >>"); kids.Append($"{c + 1} 0 R ");
        }
        p.Set(2, $"<< /Type /Pages /Kids [{kids}] /Count {n} >>");
        return p.Build(1);
    }
}
