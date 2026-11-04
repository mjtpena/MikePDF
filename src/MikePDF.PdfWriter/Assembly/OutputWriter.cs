using System.Globalization;

namespace MikePDF.PdfWriter.Assembly;

/// <summary>
/// Writes the assembled file object by object to a stream through a small buffer, remembering where each object started. The file is classic
/// (header, objects, one cross-reference table, trailer), written once and never changed. Streams are copied byte for byte from the source; only their
/// /Length is rewritten.
/// </summary>
internal sealed class OutputWriter
{
    private const int BufferSize = 1 << 16;

    private readonly Stream _stream;
    private readonly long _maxBytes;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _length;
    private long _flushed;
    private long[] _offsets = new long[1024];

    public OutputWriter(Stream stream, long maxBytes)
    {
        _stream = stream;
        _maxBytes = maxBytes;
    }

    public long Position => _flushed + _length;

    public int ObjectsWritten { get; private set; }

    public bool IsWritten(int number) => number < _offsets.Length && _offsets[number] > 0;

    public void WriteHeader(int major, int minor)
    {
        WriteAscii(string.Create(CultureInfo.InvariantCulture, $"%PDF-{major}.{minor}\n%"));
        Write([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>Writes "N 0 obj", the object and "endobj". A stream is written with its dictionary, its raw bytes and a /Length that matches them.</summary>
    public void WriteObject(int number, PdfObject value)
    {
        if (IsWritten(number))
            throw new InvalidOperationException($"object {number} written twice");
        if (number >= _offsets.Length)
            Array.Resize(ref _offsets, Math.Max(number + 1, _offsets.Length * 2));
        _offsets[number] = Position;
        ObjectsWritten++;
        WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
        if (value is PdfStream stream)
        {
            var dictionary = stream.Dictionary.With("Length", new PdfInteger(stream.RawData.Length));
            WriteDirect(dictionary);
            Write("\nstream\n"u8);
            Write(stream.RawData.Span);
            Write("\nendstream"u8);
        }
        else
        {
            WriteDirect(value);
        }

        Write("\nendobj\n"u8);
    }

    /// <summary>The cross-reference table for objects 1 to <paramref name="size"/> - 1, the trailer and the end marker. Numbers that were never written are free entries.</summary>
    public void WriteTail(int size, int root, int info, ReadOnlySpan<byte> id)
    {
        var xref = Position;
        WriteAscii(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {size}\n"));
        // The free entries form a chain through the numbers that were never written; entry 0 heads it.
        var free = new List<int>();
        for (var n = 1; n < size; n++)
        {
            if (!IsWritten(n))
                free.Add(n);
        }

        WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{(free.Count > 0 ? free[0] : 0):D10} 65535 f \n"));
        var nextFree = 0;
        for (var n = 1; n < size; n++)
        {
            if (IsWritten(n))
            {
                WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{_offsets[n]:D10} 00000 n \n"));
            }
            else
            {
                nextFree++;
                var following = nextFree < free.Count ? free[nextFree] : 0;
                WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{following:D10} 00001 f \n"));
            }
        }

        var hex = Convert.ToHexString(id);
        WriteAscii(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root {root} 0 R /Info {info} 0 R /ID [<{hex}> <{hex}>] >>\nstartxref\n{xref}\n%%EOF\n"));
        Flush();
    }

    public void Flush()
    {
        if (_length == 0)
            return;
        _stream.Write(_buffer, 0, _length);
        _flushed += _length;
        _length = 0;
    }

    private void WriteDirect(PdfObject value)
    {
        var sink = new ByteSink(_maxBytes);
        PdfSerializer.Write(sink, value, 0, ParserLimits.AbsoluteMaxNestingDepth);
        Write(sink.AsSpan());
    }

    private void WriteAscii(string text)
    {
        foreach (var ch in text)
        {
            if (_length == BufferSize)
                Flush();
            _buffer[_length++] = (byte)ch;
        }

        CheckSize(0);
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        CheckSize(data.Length);
        while (data.Length > 0)
        {
            if (_length == BufferSize)
                Flush();
            var take = Math.Min(data.Length, BufferSize - _length);
            data[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            data = data[take..];
        }
    }

    private void CheckSize(int extra)
    {
        if (Position + extra > _maxBytes)
            throw AssemblyAbort.Limit(AssemblyLimit.OutputBytes, "the output would be larger than the limit");
    }
}
