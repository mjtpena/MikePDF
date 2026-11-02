using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MikePDF.PdfWriter.Tests.Support;

/// <summary>
/// Independent PDF generator for tests. It deliberately does not use MikePDF.PdfWriter, so a bug in the writer
/// cannot hide behind a matching bug in the fixture generator. Produces classic, xref-stream+object-stream and
/// hybrid-reference files from plain text object bodies.
/// </summary>
internal static class PdfBuilder
{
    public sealed record Obj(int Num, string Text, byte[]? StreamData = null, int Gen = 0);

    public sealed record Built(byte[] Bytes, int XrefOffset, IReadOnlyDictionary<int, int> Offsets)
    {
        public int Find(string text, int from = 0)
        {
            int i = IndexOf(Bytes, text, from);
            return i >= 0 ? i : throw new InvalidOperationException($"'{text}' not found");
        }
    }

    private static readonly byte[] Header = [.. "%PDF-1.7\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, .. "\n"u8];

    public const string IdText = "[<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>]";

    /// <summary>Catalog, page tree, two pages (page 1 has an annotation array), a content stream, a font and an Info dictionary.</summary>
    public static List<Obj> TwoPageObjects() =>
    [
        new(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
        new(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots [] >>"),
        new(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
        new(5, "<< >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 10 100 Td (Hello) Tj ET")),
        new(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new(7, "<< /Producer (MikePDFTests) /Title (Two pages) >>"),
    ];

    public static Built Classic(IReadOnlyList<Obj>? objs = null, string trailerExtra = "")
    {
        objs ??= TwoPageObjects();
        var ms = new MemoryStream();
        ms.Write(Header);
        var offsets = new Dictionary<int, int>();
        foreach (var o in objs.OrderBy(o => o.Num))
        {
            offsets[o.Num] = (int)ms.Length;
            WriteObject(ms, o);
        }

        int size = objs.Max(o => o.Num) + 1;
        int xref = (int)ms.Length;
        Ascii(ms, $"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
        {
            var o = objs.FirstOrDefault(x => x.Num == n);
            Ascii(ms, o is null
                ? "0000000000 00000 f \n"
                : string.Create(CultureInfo.InvariantCulture, $"{offsets[n]:D10} {o.Gen:D5} n \n"));
        }

        Ascii(ms, $"trailer\n<< /Size {size} /Root 1 0 R /Info 7 0 R /ID {IdText} {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return new Built(ms.ToArray(), xref, offsets);
    }

    /// <summary>
    /// Writes the objects, then whatever <paramref name="tail"/> returns (xref section, trailer, startxref, %%EOF).
    /// It receives the object offsets and the offset where the tail starts, for hand-made (and hand-broken) xref sections.
    /// </summary>
    public static Built ClassicCustom(IReadOnlyList<Obj> objs, Func<IReadOnlyDictionary<int, int>, int, string> tail)
    {
        var ms = new MemoryStream();
        ms.Write(Header);
        var offsets = new Dictionary<int, int>();
        foreach (var o in objs.OrderBy(o => o.Num))
        {
            offsets[o.Num] = (int)ms.Length;
            WriteObject(ms, o);
        }

        int xref = (int)ms.Length;
        Ascii(ms, tail(offsets, xref));
        return new Built(ms.ToArray(), xref, offsets);
    }

    /// <summary>"0000000009 00000 n \n" for every object 1..max, preceded by the free entry 0; the body of a one-subsection table.</summary>
    public static string TableBody(IReadOnlyDictionary<int, int> offsets, int size)
    {
        var sb = new StringBuilder("0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
        {
            sb.Append(offsets.TryGetValue(n, out int off)
                ? string.Create(CultureInfo.InvariantCulture, $"{off:D10} 00000 n \n")
                : "0000000000 00000 f \n");
        }

        return sb.ToString();
    }

    /// <summary>Xref-stream file. Non-stream objects with number &lt;= <paramref name="compressUpTo"/> go into one object stream.</summary>
    public static Built XrefStream(IReadOnlyList<Obj>? objs = null, bool objectStreams = true, bool flate = true, bool predictor = true, int compressUpTo = 99, string xrefDictExtra = "", byte[]? xrefPayload = null)
    {
        objs ??= TwoPageObjects();
        var ms = new MemoryStream();
        ms.Write(Header);
        var offsets = new Dictionary<int, int>();
        var compressed = objectStreams
            ? objs.Where(o => o.StreamData is null && o.Gen == 0 && o.Num <= compressUpTo).OrderBy(o => o.Num).ToList()
            : [];
        int maxNum = objs.Max(o => o.Num);
        int objStmNum = objectStreams ? maxNum + 1 : -1;
        int xrefNum = maxNum + (objectStreams ? 2 : 1);
        int size = xrefNum + 1;

        foreach (var o in objs.Where(o => !compressed.Contains(o)).OrderBy(o => o.Num))
        {
            offsets[o.Num] = (int)ms.Length;
            WriteObject(ms, o);
        }

        if (objectStreams)
        {
            var header = new StringBuilder();
            var body = new StringBuilder();
            foreach (var o in compressed)
            {
                header.Append(CultureInfo.InvariantCulture, $"{o.Num} {body.Length} ");
                body.Append(o.Text).Append('\n');
            }

            byte[] payload = Encoding.Latin1.GetBytes(header.ToString() + body);
            string filter = flate ? " /Filter /FlateDecode" : "";
            if (flate)
                payload = Deflate(payload);
            offsets[objStmNum] = (int)ms.Length;
            WriteObject(ms, new Obj(objStmNum, $"<< /Type /ObjStm /N {compressed.Count} /First {header.Length}{filter} >>", payload));
        }

        int xrefOffset = (int)ms.Length;
        offsets[xrefNum] = xrefOffset;
        const int rec = 6; // W [1 3 2]
        var raw = new byte[size * rec];
        void Put(int n, int type, int f2, int f3)
        {
            int p = n * rec;
            raw[p] = (byte)type;
            raw[p + 1] = (byte)(f2 >> 16);
            raw[p + 2] = (byte)(f2 >> 8);
            raw[p + 3] = (byte)f2;
            raw[p + 4] = (byte)(f3 >> 8);
            raw[p + 5] = (byte)f3;
        }

        Put(0, 0, 0, 65535);
        foreach (var (num, off) in offsets)
            Put(num, 1, off, 0);
        for (int i = 0; i < compressed.Count; i++)
            Put(compressed[i].Num, 2, objStmNum, i);

        byte[] data = raw;
        string xfilter = "";
        if (flate)
        {
            data = Deflate(predictor ? PngUp(raw, rec) : raw);
            xfilter = predictor ? " /Filter /FlateDecode /DecodeParms << /Columns 6 /Predictor 12 >>" : " /Filter /FlateDecode";
        }

        WriteObject(ms, new Obj(xrefNum, $"<< /Type /XRef /Size {size} /W [1 3 2] /Root 1 0 R /Info 7 0 R /ID {IdText}{xfilter} {xrefDictExtra} >>", xrefPayload ?? data));
        Ascii(ms, $"startxref\n{xrefOffset}\n%%EOF\n");
        return new Built(ms.ToArray(), xrefOffset, offsets);
    }

    /// <summary>
    /// Hybrid-reference file: a classic table (listing objects 6 and 7 as free) plus an /XRefStm that locates them in an object stream.
    /// </summary>
    public static Built Hybrid(IReadOnlyList<Obj>? objs = null, Func<int, int, string>? xrefStmText = null, string xstmDictExtra = "", bool omitHiddenFromTable = false, byte[]? xstmRecords = null, string xstmIndex = "[6 2]")
    {
        objs ??= TwoPageObjects();
        int[] hidden = [6, 7];
        var ms = new MemoryStream();
        ms.Write(Header);
        var offsets = new Dictionary<int, int>();
        foreach (var o in objs.Where(o => !hidden.Contains(o.Num)).OrderBy(o => o.Num))
        {
            offsets[o.Num] = (int)ms.Length;
            WriteObject(ms, o);
        }

        int objStmNum = objs.Max(o => o.Num) + 1;
        int xstmNum = objStmNum + 1;
        int size = xstmNum + 1;

        var header = new StringBuilder();
        var body = new StringBuilder();
        foreach (int h in hidden)
        {
            header.Append(CultureInfo.InvariantCulture, $"{h} {body.Length} ");
            body.Append(objs.Single(o => o.Num == h).Text).Append('\n');
        }

        offsets[objStmNum] = (int)ms.Length;
        WriteObject(ms, new Obj(objStmNum, $"<< /Type /ObjStm /N 2 /First {header.Length} >>", Encoding.Latin1.GetBytes(header.ToString() + body)));

        // The hybrid stream: /Index [6 2], W [1 3 1], two type-2 records.
        byte[] xs = xstmRecords ?? [2, 0, 0, (byte)objStmNum, 0, 2, 0, 0, (byte)objStmNum, 1];
        int xstmOffset = (int)ms.Length;
        offsets[xstmNum] = xstmOffset;
        WriteObject(ms, new Obj(xstmNum, $"<< /Type /XRef /Size {size} /W [1 3 1] /Index {xstmIndex} {xstmDictExtra} >>", xs));

        int xref = (int)ms.Length;
        if (omitHiddenFromTable)
        {
            // Two subsections that skip the hidden objects instead of listing them as free.
            Ascii(ms, $"xref\n0 6\n0000000000 65535 f \n");
            for (int n = 1; n < 6; n++)
                Ascii(ms, string.Create(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n"));
            Ascii(ms, $"{objStmNum} 2\n");
            for (int n = objStmNum; n < size; n++)
                Ascii(ms, string.Create(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n"));
        }
        else
        {
            Ascii(ms, $"xref\n0 {size}\n0000000000 65535 f \n");
            for (int n = 1; n < size; n++)
            {
                Ascii(ms, offsets.TryGetValue(n, out int off)
                    ? string.Create(CultureInfo.InvariantCulture, $"{off:D10} 00000 n \n")
                    : "0000000000 00000 f \n");
            }
        }

        Ascii(ms, $"trailer\n<< /Size {size} /Root 1 0 R /Info 7 0 R /ID {IdText} /XRefStm {xrefStmText?.Invoke(xstmOffset, xref) ?? xstmOffset.ToString(CultureInfo.InvariantCulture)} >>\nstartxref\n{xref}\n%%EOF\n");
        return new Built(ms.ToArray(), xref, offsets);
    }

    public static int IndexOf(byte[] data, string text, int from = 0)
    {
        var needle = Encoding.Latin1.GetBytes(text);
        int i = data.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }

    public static int LastIndexOf(byte[] data, string text) => data.AsSpan().LastIndexOf(Encoding.Latin1.GetBytes(text));

    /// <summary>Overwrites bytes at the first occurrence of <paramref name="find"/> with <paramref name="replace"/> (same length required).</summary>
    public static byte[] Patch(byte[] data, string find, string replace, int occurrence = 0)
    {
        if (find.Length != replace.Length)
            throw new ArgumentException("replacement must keep the length");
        int at = -1;
        for (int k = 0; k <= occurrence; k++)
        {
            at = IndexOf(data, find, at + 1);
            if (at < 0)
                throw new InvalidOperationException($"'{find}' not found");
        }

        var copy = (byte[])data.Clone();
        Encoding.Latin1.GetBytes(replace).CopyTo(copy, at);
        return copy;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] PngUp(byte[] raw, int rowLen)
    {
        int rows = raw.Length / rowLen;
        var o = new byte[rows * (rowLen + 1)];
        for (int r = 0; r < rows; r++)
        {
            o[r * (rowLen + 1)] = 2;
            for (int i = 0; i < rowLen; i++)
                o[(r * (rowLen + 1)) + 1 + i] = (byte)(raw[(r * rowLen) + i] - (r > 0 ? raw[((r - 1) * rowLen) + i] : 0));
        }

        return o;
    }

    private static void WriteObject(MemoryStream ms, Obj o)
    {
        if (o.StreamData is null)
        {
            Ascii(ms, $"{o.Num} {o.Gen} obj\n{o.Text}\nendobj\n");
            return;
        }

        string dict = o.Text.TrimEnd();
        dict = dict[..^2].TrimEnd() + $" /Length {o.StreamData.Length} >>";
        Ascii(ms, $"{o.Num} {o.Gen} obj\n{dict}\nstream\n");
        ms.Write(o.StreamData);
        Ascii(ms, "\nendstream\nendobj\n");
    }

    private static void Ascii(MemoryStream ms, string s) => ms.Write(Encoding.Latin1.GetBytes(s));
}
