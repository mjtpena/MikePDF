using System.Text;

namespace S9;

/// <summary>Tiny PDF writer for spike fixtures: classic xref, optional incremental update. Not production code.</summary>
internal sealed class PdfBuilder
{
    readonly List<byte[]?> _objs = [];
    public int Add(string body) { _objs.Add(Encoding.Latin1.GetBytes(body)); return _objs.Count; }
    public int Reserve() { _objs.Add(null); return _objs.Count; }
    public void Set(int num, string body) => _objs[num - 1] = Encoding.Latin1.GetBytes(body);
    public int AddStream(string dictBody, string content)
    {
        var c = Encoding.Latin1.GetBytes(content);
        var head = Encoding.Latin1.GetBytes($"<< {dictBody} /Length {c.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        _objs.Add([.. head, .. c, .. tail]); return _objs.Count;
    }
    public int Count => _objs.Count;

    public (byte[] bytes, long xrefOffset) Build(int root)
    {
        using var ms = new MemoryStream(); var offs = new long[_objs.Count];
        ms.Write(Encoding.Latin1.GetBytes("%PDF-1.7\n%âãÏÓ\n"));
        for (var i = 0; i < _objs.Count; i++)
        {
            offs[i] = ms.Position; ms.Write(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n")); ms.Write(_objs[i] ?? throw new InvalidOperationException("unset object " + (i + 1))); ms.Write(Encoding.Latin1.GetBytes("\nendobj\n"));
        }
        var xref = ms.Position; var sb = new StringBuilder($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {_objs.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        return (ms.ToArray(), xref);
    }

    /// <summary>Appends a revision: the given (object number -> body) pairs, an xref subsection per object, trailer with /Prev.</summary>
    public static (byte[] bytes, long xrefOffset) Incremental(byte[] baseBytes, SortedDictionary<int, byte[]> objs, int newSize, int root, long prevXref)
    {
        using var ms = new MemoryStream(); ms.Write(baseBytes);
        var offs = new SortedDictionary<int, long>();
        foreach (var (n, body) in objs) { offs[n] = ms.Position; ms.Write(Encoding.Latin1.GetBytes($"{n} 0 obj\n")); ms.Write(body); ms.Write(Encoding.Latin1.GetBytes("\nendobj\n")); }
        var xref = ms.Position; var sb = new StringBuilder("xref\n");
        foreach (var (n, o) in offs) sb.Append($"{n} 1\n{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {newSize} /Root {root} 0 R /Prev {prevXref} >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        return (ms.ToArray(), xref);
    }
}

internal sealed class BaseDoc
{
    public byte[] Bytes = []; public long XrefOffset; public int ObjCount; public int Root = 1; public int PageObj0 = 7; public string PageBodyPrefix = "";
    public int PageCount;

    static readonly string[] Words = "the quick brown fox jumps over lazy dog annotation display strategy overlay tile render page vector ink highlight multiply blend portable document format reader engine broker sandbox frame budget".Split(' ');

    public static string Page0Content()
    {
        var rnd = new Random(7); var sb = new StringBuilder();
        sb.Append("0.85 0.92 1 rg 36 740 540 40 re f\nBT /F2 20 Tf 0 0 0 rg 54 752 Td (MikePDF spike S9 - annotation display) Tj ET\n");
        // coloured blocks (to test Multiply over colour), under the text lines
        sb.Append("1 0.85 0.8 rg 54 520 200 60 re f\n0.8 0.95 0.8 rg 300 440 180 70 re f\n0.45 0.45 0.55 rg 80 330 220 40 re f\n0.2 0.2 0.2 rg 330 250 200 50 re f\n");
        for (var line = 0; line < 44; line++)
        {
            var y = 735 - 14 * line; if (y < 60) break;
            var sbl = new StringBuilder(); var n = 8 + rnd.Next(8);
            for (var i = 0; i < n; i++) sbl.Append(Words[rnd.Next(Words.Length)]).Append(' ');
            var txt = sbl.ToString().TrimEnd();
            var col = (y < 600 && y > 240 && line % 7 == 0) ? "1 1 1 rg" : "0 g";
            sb.Append($"BT /F1 11 Tf {col} 54 {y} Td ({txt}) Tj ET\n");
        }
        sb.Append("0.9 0.2 0.2 rg 54 40 40 12 re f 0.2 0.6 0.2 rg 100 40 40 12 re f 0.2 0.2 0.8 rg 146 40 40 12 re f\n");
        return sb.ToString();
    }

    public int Rotate;

    public static BaseDoc Build(int pageCount, int rotate = 0)
    {
        var b = new PdfBuilder();
        b.Add("<< /Type /Catalog /Pages 2 0 R >>");                                         // 1
        var kids = new StringBuilder();
        b.Reserve();                                                                          // 2 pages (set later)
        b.Add("<< /Font << /F1 5 0 R /F2 6 0 R >> >>");                                       // 3 resources
        b.Add("<< /Type /Pages >>");                                                          // 4 unused spacer to keep numbering stable
        b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");                      // 5
        b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");                 // 6
        for (var i = 0; i < pageCount; i++)
        {
            var pageNum = b.Count + 1;
            b.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 3 0 R /Contents {pageNum + 1} 0 R{(rotate != 0 ? $" /Rotate {rotate}" : "")} >>");
            b.AddStream("", i == 0 ? Page0Content() : $"BT /F2 18 Tf 54 740 Td (Page {i + 1}) Tj ET\n0.9 g 54 600 400 100 re f\nBT /F1 11 Tf 0 g 54 580 Td (Body text of page {i + 1}) Tj ET\n");
            kids.Append($"{pageNum} 0 R ");
        }
        b.Set(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        var (bytes, xref) = b.Build(1);
        return new BaseDoc { Bytes = bytes, XrefOffset = xref, ObjCount = b.Count, PageCount = pageCount, Rotate = rotate };
    }

    /// <summary>Appends the annotations to page 0 as an incremental update (what the writer host does on Save, ADR-0008). withAp=false omits /AP so PDFium generates one.</summary>
    public (byte[] bytes, long xref) WithAnnotations(IReadOnlyList<Markup> annots, bool withAp = true, int firstNew = -1, int pageObj = -1)
    {
        var pg = pageObj > 0 ? pageObj : PageObj0;
        var objs = new SortedDictionary<int, byte[]>(); var next = ObjCount + 1;
        var refs = new StringBuilder();
        foreach (var a in annots)
        {
            var (l, b, r, t) = a.Rect(); var annotNum = next++;
            var ap = "";
            if (withAp)
            {
                var apNum = next++;
                var res = $"/Resources << {a.ApExtGState} {(a is FreeTextM f ? f.ApFont : "")} >>";
                var content = a.ApContent(); var c = Encoding.Latin1.GetBytes(content);
                var dict = $"<< /Type /XObject /Subtype /Form /BBox [{Fmt.F(l)} {Fmt.F(b)} {Fmt.F(r)} {Fmt.F(t)}] {res} /Length {c.Length} >>\nstream\n";
                objs[apNum] = [.. Encoding.Latin1.GetBytes(dict), .. c, .. Encoding.Latin1.GetBytes("\nendstream")];
                ap = $" /AP << /N {apNum} 0 R >>";
            }
            var body = $"<< /Type /Annot /Subtype /{a.Subtype} /Rect [{Fmt.F(l)} {Fmt.F(b)} {Fmt.F(r)} {Fmt.F(t)}]{a.ExtraDict()} /F 4 /NM ({a.Nm}) /P {pg} 0 R{ap} >>";
            objs[annotNum] = Encoding.Latin1.GetBytes(body); refs.Append($"{annotNum} 0 R ");
        }
        objs[pg] = Encoding.Latin1.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 3 0 R /Contents {pg + 1} 0 R{(Rotate != 0 ? $" /Rotate {Rotate}" : "")} /Annots [{refs}] >>");
        return PdfBuilder.Incremental(Bytes, objs, next, Root, XrefOffset);
    }
}
