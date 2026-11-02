using System.IO.Compression;
using System.Text;

namespace FixtureGen;

/// <summary>Minimal PDF 1.7 writer: numbered objects, optional Flate on streams, classic xref. Deterministic output (fixed zlib level, no timestamps).</summary>
public sealed class PdfBuilder
{
    readonly List<byte[]?> _objs = new();           // index = object number - 1
    readonly List<int> _pages = new();
    public int PagesObj { get; }
    public int CatalogObj { get; }

    public PdfBuilder()
    {
        CatalogObj = Reserve();
        PagesObj = Reserve();
    }

    public int Reserve() { _objs.Add(null); return _objs.Count; }
    public void Set(int num, string body) => _objs[num - 1] = Encoding.Latin1.GetBytes(body);
    public int Add(string body) { int n = Reserve(); Set(n, body); return n; }

    public int AddStream(string dictEntries, byte[] data, bool flate = true)
    {
        int n = Reserve();
        SetStream(n, dictEntries, data, flate);
        return n;
    }

    public void SetStream(int n, string dictEntries, byte[] data, bool flate = true)
    {
        byte[] payload = data; string filter = "";
        if (flate)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
            payload = ms.ToArray(); filter = "/Filter /FlateDecode ";
        }
        var head = Encoding.Latin1.GetBytes($"<< {dictEntries} {filter}/Length {payload.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        var all = new byte[head.Length + payload.Length + tail.Length];
        head.CopyTo(all, 0); payload.CopyTo(all, head.Length); tail.CopyTo(all, head.Length + payload.Length);
        _objs[n - 1] = all;
    }

    public int AddPage(double w, double h, byte[] content, string resources, string extra = "")
    {
        int c = AddStream("", content);
        int p = Add($"<< /Type /Page /Parent {PagesObj} 0 R /MediaBox [0 0 {F(w)} {F(h)}] /Resources {resources} /Contents {c} 0 R {extra} >>");
        _pages.Add(p);
        return p;
    }

    public static string F(double d) => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public byte[] ToBytes()
    {
        Set(CatalogObj, $"<< /Type /Catalog /Pages {PagesObj} 0 R >>");
        Set(PagesObj, $"<< /Type /Pages /Kids [{string.Join(" ", _pages.Select(p => p + " 0 R"))}] /Count {_pages.Count} >>");
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offs = new long[_objs.Count];
        for (int i = 0; i < _objs.Count; i++)
        {
            offs[i] = ms.Position;
            W($"{i + 1} 0 obj\n");
            ms.Write(_objs[i] ?? Encoding.Latin1.GetBytes("null"));
            W("\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {_objs.Count + 1} /Root {CatalogObj} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}

/// <summary>Content-stream text builder.</summary>
public sealed class Cs
{
    readonly MemoryStream _ms = new();
    public Cs Raw(string s) { var b = Encoding.Latin1.GetBytes(s); _ms.Write(b); return this; }
    public Cs Bytes(byte[] b) { _ms.Write(b); return this; }
    public Cs Line(string s) => Raw(s + "\n");
    public byte[] ToArray() => _ms.ToArray();
    public static string F(double d) => PdfBuilder.F(d);
    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    public Cs Circle(double cx, double cy, double r)
    {
        double k = 0.5523 * r;
        return Line($"{F(cx + r)} {F(cy)} m {F(cx + r)} {F(cy + k)} {F(cx + k)} {F(cy + r)} {F(cx)} {F(cy + r)} c {F(cx - k)} {F(cy + r)} {F(cx - r)} {F(cy + k)} {F(cx - r)} {F(cy)} c {F(cx - r)} {F(cy - k)} {F(cx - k)} {F(cy - r)} {F(cx)} {F(cy - r)} c {F(cx + k)} {F(cy - r)} {F(cx + r)} {F(cy - k)} {F(cx + r)} {F(cy)} c h");
    }
}
