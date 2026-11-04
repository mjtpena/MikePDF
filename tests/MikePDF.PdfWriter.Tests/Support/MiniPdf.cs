using System.Globalization;
using System.Text;

namespace MikePDF.PdfWriter.Tests.Support;

/// <summary>
/// A small PDF writer for the assembler's test inputs, independent of MikePDF.PdfWriter's own serialiser: objects are given as text, and the file is written
/// either classically or with an xref stream and an object stream (so the same content is tested in both layouts).
/// </summary>
internal sealed class MiniPdf
{
    private readonly SortedDictionary<int, (string Text, byte[]? Data)> _objects = [];
    private int _next = 1;

    public int Reserve() => _next++;

    public int[] Reserve(int count) => [.. Enumerable.Range(0, count).Select(_ => Reserve())];

    public void Set(int number, string text, byte[]? streamData = null)
    {
        _objects[number] = (text, streamData);
        _next = Math.Max(_next, number + 1);
    }

    public int Add(string text, byte[]? streamData = null)
    {
        var number = Reserve();
        Set(number, text, streamData);
        return number;
    }

    /// <summary>Adds a stream object. <paramref name="dictionary"/> holds the entries without the brackets and without /Length.</summary>
    public int AddStream(string dictionary, string content, bool compress = false) => AddStream(dictionary, Encoding.Latin1.GetBytes(content), compress);

    public int AddStream(string dictionary, byte[] data, bool compress = false)
    {
        if (compress)
        {
            data = PdfBuilder.Deflate(data);
            dictionary += " /Filter /FlateDecode";
        }

        return Add($"<< {dictionary} >>", data);
    }

    public byte[] WriteClassic(int root, int? info = null)
    {
        var ms = new MemoryStream();
        Write(ms, "%PDF-1.7\n%");
        ms.Write([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new Dictionary<int, long>();
        foreach (var (number, (text, data)) in _objects)
        {
            offsets[number] = ms.Position;
            WriteObject(ms, number, text, data);
        }

        var xref = ms.Position;
        var size = _objects.Keys.Max() + 1;
        Write(ms, $"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++)
            Write(ms, offsets.TryGetValue(n, out var at) ? string.Create(CultureInfo.InvariantCulture, $"{at:D10} 00000 n \n") : "0000000000 65535 f \n");
        var id = Convert.ToHexString(Encoding.ASCII.GetBytes("0123456789abcdef"));
        Write(ms, $"trailer\n<< /Size {size} /Root {root} 0 R{(info is null ? "" : $" /Info {info} 0 R")} /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The same objects with an xref stream, and every non-stream object in one compressed object stream.</summary>
    public byte[] WriteXrefStream(int root, int? info = null)
    {
        var ms = new MemoryStream();
        Write(ms, "%PDF-1.7\n%");
        ms.Write([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new Dictionary<int, long>();
        var compressed = _objects.Where(o => o.Value.Data is null).Select(o => o.Key).ToList();
        foreach (var (number, (text, data)) in _objects.Where(o => o.Value.Data is not null))
        {
            offsets[number] = ms.Position;
            WriteObject(ms, number, text, data);
        }

        var objectStream = _next;
        var header = new StringBuilder();
        var body = new StringBuilder();
        foreach (var number in compressed)
        {
            header.Append(CultureInfo.InvariantCulture, $"{number} {body.Length} ");
            body.Append(_objects[number].Text).Append('\n');
        }

        offsets[objectStream] = ms.Position;
        WriteObject(ms, objectStream, $"<< /Type /ObjStm /N {compressed.Count} /First {header.Length} /Filter /FlateDecode >>", PdfBuilder.Deflate(Encoding.Latin1.GetBytes(header.ToString() + body)));

        var xrefNumber = objectStream + 1;
        var size = xrefNumber + 1;
        var xrefOffset = ms.Position;
        offsets[xrefNumber] = xrefOffset;
        var records = new byte[size * 7]; // W [1 4 2]
        void Put(int n, int type, long f2, int f3)
        {
            var p = n * 7;
            records[p] = (byte)type;
            records[p + 1] = (byte)(f2 >> 24);
            records[p + 2] = (byte)(f2 >> 16);
            records[p + 3] = (byte)(f2 >> 8);
            records[p + 4] = (byte)f2;
            records[p + 5] = (byte)(f3 >> 8);
            records[p + 6] = (byte)f3;
        }

        Put(0, 0, 0, 65535);
        foreach (var (number, at) in offsets)
            Put(number, 1, at, 0);
        for (var i = 0; i < compressed.Count; i++)
            Put(compressed[i], 2, objectStream, i);
        var id = Convert.ToHexString(Encoding.ASCII.GetBytes("0123456789abcdef"));
        WriteObject(ms, xrefNumber, $"<< /Type /XRef /Size {size} /W [1 4 2] /Root {root} 0 R{(info is null ? "" : $" /Info {info} 0 R")} /ID [<{id}> <{id}>] /Filter /FlateDecode >>", PdfBuilder.Deflate(records));
        Write(ms, $"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void WriteObject(MemoryStream ms, int number, string text, byte[]? data)
    {
        if (data is null)
        {
            Write(ms, $"{number} 0 obj\n{text}\nendobj\n");
            return;
        }

        var dictionary = text.TrimEnd();
        dictionary = dictionary[..^2].TrimEnd() + $" /Length {data.Length} >>";
        Write(ms, $"{number} 0 obj\n{dictionary}\nstream\n");
        ms.Write(data);
        Write(ms, "\nendstream\nendobj\n");
    }

    private static void Write(MemoryStream ms, string text) => ms.Write(Encoding.Latin1.GetBytes(text));

    /// <summary>The objects of an existing file as a MiniPdf, so the same document can be written in the other layout.</summary>
    public static MiniPdf From(StrictDocument document)
    {
        var copy = new MiniPdf();
        foreach (var entry in document.Latest.EnumerateEffectiveEntries().Where(e => e.Key > 0 && e.Value.Type != XrefEntryType.Free).OrderBy(e => e.Key))
        {
            var value = document.GetObject(entry.Key).Value;
            if (value is PdfStream stream)
                copy.Set(entry.Key, Text(stream.Dictionary.Without("Length")), stream.RawData.ToArray());
            else
                copy.Set(entry.Key, Text(value));
        }

        return copy;
    }

    private static string Text(PdfObject value) => Encoding.Latin1.GetString(PdfSerializer.Serialize(value).Value);

    // ---- the large documents of the performance measurement

    /// <summary>
    /// A classic file of <paramref name="pages"/> pages: 30 lines of text, a link to the next page, an outline entry every ten pages, and a 96 KB
    /// incompressible image every fifth page, so the file has the bulk of a scan. Every page carries the marker "PAGE-{doc}-{nn}".
    /// </summary>
    public static byte[] Large(string doc, int pages, int seed)
    {
        var random = new Random(seed);
        var pdf = new MiniPdf();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pageNumbers = pdf.Reserve(pages);
        var images = new Dictionary<int, int>();
        for (var i = 1; i <= pages; i += 5)
        {
            var noise = new byte[128 * 256 * 3];
            random.NextBytes(noise);
            images[i] = pdf.AddStream("/Type /XObject /Subtype /Image /Width 128 /Height 256 /ColorSpace /DeviceRGB /BitsPerComponent 8", noise);
        }

        for (var i = 1; i <= pages; i++)
        {
            var content = new StringBuilder();
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 28 Tf 72 700 Td (PAGE-{doc}-{i:D2}) Tj ET\n");
            for (var k = 0; k < 30; k++)
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 10 Tf 72 {500 - (k * 12)} Td (Lorem ipsum dolor sit amet {i}-{k}) Tj ET\n");
            var resources = $"/Font << /F1 {font} 0 R >>";
            var image = images[((i - 1) / 5 * 5) + 1];
            resources += $" /XObject << /Im0 {image} 0 R >>";
            content.Append("q 100 0 0 100 400 560 cm /Im0 Do Q\n");
            var contentNumber = pdf.AddStream("", content.ToString(), compress: true);
            var link = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [72 460 250 480] /Border [0 0 0] /Dest [{pageNumbers[i % pages]} 0 R /Fit] /F 4 >>");
            pdf.Set(pageNumbers[i - 1], $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 792] /Resources << {resources} >> /Contents {contentNumber} 0 R /Annots [{link} 0 R] >>");
        }

        pdf.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', pageNumbers.Select(n => $"{n} 0 R"))}] /Count {pages} >>");
        var starts = Enumerable.Range(0, (pages + 9) / 10).Select(k => (k * 10) + 1).ToList();
        var root = pdf.Reserve();
        var items = pdf.Reserve(starts.Count);
        for (var k = 0; k < starts.Count; k++)
        {
            var text = $"<< /Title ({doc} chapter {k + 1}) /Parent {root} 0 R /Dest [{pageNumbers[starts[k] - 1]} 0 R /Fit]";
            if (k > 0)
                text += $" /Prev {items[k - 1]} 0 R";
            if (k < starts.Count - 1)
                text += $" /Next {items[k + 1]} 0 R";
            pdf.Set(items[k], text + " >>");
        }

        pdf.Set(root, $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {starts.Count} >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R /Outlines {root} 0 R >>");
        return pdf.WriteClassic(catalog);
    }
}
