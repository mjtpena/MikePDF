using System.Globalization;
using System.Text;

namespace MikePDF.Render.Corpus;

/// <summary>
/// Writes small PDF files byte for byte the same on every machine (no clock, no random numbers, no compression, whose output could differ between zlib
/// versions), so a generated corpus file has one SHA-256 for ever. Not a general writer: objects are numbered by the caller and written in order.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly List<byte[]?> _objects = [];

    /// <summary>Reserves an object number to be filled in later with <see cref="Set"/>.</summary>
    public int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    public int Add(string body)
    {
        var number = Reserve();
        Set(number, body);
        return number;
    }

    public int AddStream(string dictionaryEntries, byte[] data)
    {
        var number = Reserve();
        SetStream(number, dictionaryEntries, data);
        return number;
    }

    public int AddStream(string dictionaryEntries, string content) => AddStream(dictionaryEntries, Encoding.Latin1.GetBytes(content));

    public void Set(int number, string body) => _objects[number - 1] = Encoding.Latin1.GetBytes(body);

    public void SetStream(int number, string dictionaryEntries, byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< {dictionaryEntries} /Length {data.Length} >>\nstream\n")));
        buffer.Write(data);
        buffer.Write("\nendstream"u8);
        _objects[number - 1] = buffer.ToArray();
    }

    /// <summary>The finished file. <paramref name="xrefOffsetShift"/> moves every cross-reference offset (a broken file on purpose); <paramref name="startXrefOverride"/> replaces the <c>startxref</c> value.</summary>
    public byte[] Build(int root, int xrefOffsetShift = 0, long? startXrefOverride = null)
    {
        using var file = new MemoryStream();
        void Write(string text) => file.Write(Encoding.Latin1.GetBytes(text));

        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < _objects.Count; i++)
        {
            var body = _objects[i] ?? throw new InvalidOperationException($"Object {i + 1} was reserved and never set.");
            offsets.Add(file.Position);
            Write(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            file.Write(body);
            Write("\nendobj\n");
        }

        var xref = file.Position;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset + xrefOffsetShift:D10} 00000 n \n"));
        }

        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_objects.Count + 1} /Root {root} 0 R >>\nstartxref\n{startXrefOverride ?? xref}\n%%EOF\n"));
        return file.ToArray();
    }
}
