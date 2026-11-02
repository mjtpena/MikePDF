using System.Text;
using S8.Core;

namespace S8.Fixtures;

public sealed record Obj(int Num, string Body, int Gen = 0);

/// <summary>Builds small, fully-valid PDFs (classic xref or xref stream) and appends raw incremental updates. Test-fixture tooling only.</summary>
public static class Pdf
{
    public static string Stream(string dictInner, string content) =>
        $"<< {dictInner} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private const string IdHex = "<5155495245533853504B455453414D50><5155495245533853504B455453414D51>";

    public static byte[] Build(IEnumerable<Obj> objs, int rootNum = 1, bool xrefStream = false, string? infoRef = null)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%âãÏÓ\n");
        var offs = new SortedDictionary<int, (long, int)>();
        foreach (var o in objs)
        {
            offs[o.Num] = (ms.Position, o.Gen);
            W($"{o.Num} {o.Gen} obj\n{o.Body}\nendobj\n");
        }
        int size = offs.Keys.Max() + 1;
        long xo = ms.Position;
        if (!xrefStream)
        {
            W($"xref\n0 {size}\n");
            for (int n = 0; n < size; n++)
            {
                if (offs.TryGetValue(n, out var e)) W($"{e.Item1:D10} {e.Item2:D5} n \n");
                else W(n == 0 ? "0000000000 65535 f \n" : "0000000000 00000 f \n");
            }
            W($"trailer\n<< /Size {size} /Root {rootNum} 0 R{(infoRef != null ? $" /Info {infoRef}" : "")} /ID [{IdHex.Replace("><", "> <")}] >>\n");
        }
        else
        {
            int xn = size; size++;
            offs[xn] = (xo, 0);
            var data = new MemoryStream();
            for (int n = 0; n < size; n++)
            {
                if (offs.TryGetValue(n, out var e))
                {
                    data.WriteByte(1);
                    long off = e.Item1;
                    data.WriteByte((byte)(off >> 24)); data.WriteByte((byte)(off >> 16)); data.WriteByte((byte)(off >> 8)); data.WriteByte((byte)off);
                    data.WriteByte((byte)(e.Item2 >> 8)); data.WriteByte((byte)e.Item2);
                }
                else { data.WriteByte(0); data.Write(new byte[] { 0, 0, 0, 0 }); data.WriteByte(n == 0 ? (byte)255 : (byte)0); data.WriteByte(n == 0 ? (byte)255 : (byte)0); }
            }
            W($"{xn} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root {rootNum} 0 R /ID [{IdHex.Replace("><", "> <")}] /Length {data.Length} >>\nstream\n");
            ms.Write(data.ToArray());
            W("\nendstream\nendobj\n");
        }
        W($"startxref\n{xo}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// Base document used by most fixtures: one page, text, a form XObject (/Fm1), an unreferenced "alternative" XObject (obj 7),
    /// an AcroForm (obj 8, indirect) with one text field (obj 9, merged widget) and its appearance stream (obj 10).
    /// </summary>
    public static byte[] BaseDocument(bool xrefStream = false)
    {
        var fontRes = "/Font << /F1 5 0 R >>";
        var objs = new List<Obj>
        {
            new(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>"),
            new(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << {fontRes} /XObject << /Fm1 6 0 R >> >> /Contents 4 0 R /Annots [9 0 R] >>"),
            new(4, Stream("", "BT /F1 16 Tf 72 720 Td (Payment agreement) Tj 0 -26 Td /F1 12 Tf (Total due: USD 100.00) Tj ET\nq 1 0 0 1 72 640 cm /Fm1 Do Q")),
            new(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            new(6, Stream($"/Type /XObject /Subtype /Form /BBox [0 0 400 30] /Resources << {fontRes} >>", "BT /F1 12 Tf 0 8 Td (Pay to account 12-3456-789) Tj ET")),
            new(7, Stream($"/Type /XObject /Subtype /Form /BBox [0 0 400 30] /Resources << {fontRes} >>", "BT /F1 12 Tf 0 8 Td (Pay to account 99-9999-999) Tj ET")),
            new(8, "<< /Fields [9 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >>"),
            new(9, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Comment) /V () /Rect [72 520 372 545] /P 3 0 R /F 4 /DA (/Helv 11 Tf 0 g) /MK << /BC [0] >> /AP << /N 10 0 R >> >>"),
            new(10, Stream("/Type /XObject /Subtype /Form /BBox [0 0 300 25]", "q 0 G 0.5 0.5 299 24 re S Q")),
        };
        return Build(objs, 1, xrefStream);
    }

    /// <summary>
    /// Append a plain incremental update (classic xref) redefining/adding the given objects. Trailer is copied from the previous
    /// revision (Root/Info/ID) unless overridden. This is the attacker's tool: it has no knowledge of signatures.
    /// </summary>
    public static byte[] AppendUpdate(byte[] pdf, IEnumerable<Obj> objs, int? newRoot = null, bool omitPrev = false, int? sizeOverride = null)
    {
        var file = new PdfFile(pdf);
        var view = file.OpenFinalView();
        if (view.Sections[0].IsStream) throw new NotSupportedException("AppendUpdate supports classic-xref bases only");
        var ms = new MemoryStream();
        ms.Write(pdf);
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        if (pdf[^1] != 10) W("\n");
        var offs = new SortedDictionary<int, (long, int)>();
        foreach (var o in objs)
        {
            offs[o.Num] = (ms.Position, o.Gen);
            W($"{o.Num} {o.Gen} obj\n{o.Body}\nendobj\n");
        }
        int size = Math.Max(view.Size, offs.Keys.Max() + 1);
        if (sizeOverride != null) size = sizeOverride.Value;
        long xo = ms.Position;
        W("xref\n");
        var nums = offs.Keys.ToList();
        for (int i = 0; i < nums.Count;)
        {
            int j = i;
            while (j + 1 < nums.Count && nums[j + 1] == nums[j] + 1) j++;
            W($"{nums[i]} {j - i + 1}\n");
            for (int k = i; k <= j; k++) W($"{offs[nums[k]].Item1:D10} {offs[nums[k]].Item2:D5} n \n");
            i = j + 1;
        }
        int root = newRoot ?? view.RootRef!.Num;
        var tr = new System.Text.StringBuilder($"trailer\n<< /Size {size} /Root {root} 0 R");
        if (!omitPrev) tr.Append($" /Prev {view.StartXref}");
        if (view.Trailer["Info"] != null) tr.Append(" /Info ").Append(PdfSerializer.ToPdf(view.Trailer["Info"]!));
        if (view.Trailer["ID"] != null) tr.Append(" /ID ").Append(PdfSerializer.ToPdf(view.Trailer["ID"]!));
        tr.Append(" >>\n");
        W(tr.ToString());
        W($"startxref\n{xo}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The current serialised body of an object in the final view (dictionary / array only, no streams).</summary>
    public static PdfDict DictOf(byte[] pdf, int num)
    {
        var v = new PdfFile(pdf).OpenFinalView();
        return v.ResolveDict(new PdfRef(num, 0)) ?? throw new InvalidOperationException($"object {num} is not a dictionary");
    }

    public static byte[] ReplaceAscii(byte[] data, string find, string repl)
    {
        if (find.Length != repl.Length) throw new ArgumentException("replacement must keep the length");
        var f = Encoding.Latin1.GetBytes(find);
        int i = data.AsSpan().IndexOf(f);
        if (i < 0) throw new InvalidOperationException($"'{find}' not found");
        var res = (byte[])data.Clone();
        Encoding.Latin1.GetBytes(repl).CopyTo(res, i);
        return res;
    }
}
