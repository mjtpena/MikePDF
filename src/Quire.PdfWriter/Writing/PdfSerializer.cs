using System.Globalization;

namespace Quire.PdfWriter;

/// <summary>
/// Canonical serialisation of direct objects. Deterministic: dictionary keys come out in the order given,
/// names and strings are escaped per 7.3.5 / 7.3.4, reals never use exponent notation, and every token is
/// separated by a single space. Streams are written by the caller (<see cref="IncrementalUpdate"/>).
/// </summary>
public static class PdfSerializer
{
    /// <summary>Serialises <paramref name="obj"/>. Fails with a limit error when nesting exceeds <paramref name="maxDepth"/> or the object is a stream.</summary>
    public static PdfResult<byte[]> Serialize(PdfObject obj, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return PdfDocument.Guard(() =>
        {
            var sink = new ByteSink();
            Write(sink, obj, 0, Math.Clamp(maxDepth, 1, ParserLimits.AbsoluteMaxNestingDepth));
            return sink.ToArray();
        });
    }

    internal static void Write(ByteSink sink, PdfObject obj, int depth, int maxDepth)
    {
        switch (obj)
        {
            case PdfNull:
                sink.Write("null"u8);
                break;
            case PdfBoolean b:
                sink.Write(b.Value ? "true"u8 : "false"u8);
                break;
            case PdfInteger i:
                sink.WriteAscii(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal r:
                sink.WriteAscii(FormatReal(r.Value));
                break;
            case PdfName n:
                WriteName(sink, n.Value);
                break;
            case PdfString s:
                WriteString(sink, s);
                break;
            case PdfReference rf:
                sink.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{rf.Number} {rf.Generation} R"));
                break;
            case PdfArray a:
                if (depth >= maxDepth)
                    ParseContext.ExceedLimit(PdfLimit.NestingDepth, "object nesting exceeds the serialiser depth limit");
                sink.WriteByte((byte)'[');
                for (int k = 0; k < a.Count; k++)
                {
                    if (k > 0)
                        sink.WriteByte((byte)' ');
                    Write(sink, a[k], depth + 1, maxDepth);
                }

                sink.WriteByte((byte)']');
                break;
            case PdfDictionary d:
                WriteDictionary(sink, d, depth, maxDepth);
                break;
            case PdfStream:
                ParseContext.Fatal(PdfErrorCode.InvalidUpdate, "a stream cannot be serialised as a direct object");
                break;
        }
    }

    internal static void WriteDictionary(ByteSink sink, PdfDictionary d, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
            ParseContext.ExceedLimit(PdfLimit.NestingDepth, "object nesting exceeds the serialiser depth limit");
        sink.Write("<<"u8);
        foreach (var kv in d)
        {
            sink.WriteByte((byte)' ');
            WriteName(sink, kv.Key.Value);
            sink.WriteByte((byte)' ');
            Write(sink, kv.Value, depth + 1, maxDepth);
        }

        sink.Write(" >>"u8);
    }

    /// <summary>Reals as plain decimals with up to 15 fractional digits and no exponent. Whole numbers keep a ".0" so they stay reals.</summary>
    internal static string FormatReal(double value)
    {
        string s = value.ToString("0.###############", CultureInfo.InvariantCulture);
        if (s is "-0" or "0")
            return "0.0";
        return s.Contains('.', StringComparison.Ordinal) ? s : s + ".0";
    }

    private static void WriteName(ByteSink sink, string name)
    {
        sink.WriteByte((byte)'/');
        foreach (char ch in name)
        {
            byte c = (byte)ch;
            if (c <= 32 || c >= 127 || c == (byte)'#' || PdfBytes.IsDelimiter(c))
            {
                sink.WriteByte((byte)'#');
                sink.WriteHexByte(c);
            }
            else
            {
                sink.WriteByte(c);
            }
        }
    }

    private static void WriteString(ByteSink sink, PdfString s)
    {
        var bytes = s.Value;
        if (s.Format == PdfStringFormat.Hex)
        {
            sink.WriteByte((byte)'<');
            foreach (byte b in bytes)
                sink.WriteHexByte(b);
            sink.WriteByte((byte)'>');
            return;
        }

        sink.WriteByte((byte)'(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    sink.WriteByte((byte)'\\');
                    sink.WriteByte(b);
                    break;
                case 10:
                    sink.Write("\\n"u8);
                    break;
                case 13:
                    sink.Write("\\r"u8);
                    break;
                case < 32 or >= 127:
                    // Always three octal digits, so a following digit cannot extend the escape.
                    sink.WriteByte((byte)'\\');
                    sink.WriteByte((byte)('0' + (b >> 6)));
                    sink.WriteByte((byte)('0' + ((b >> 3) & 7)));
                    sink.WriteByte((byte)('0' + (b & 7)));
                    break;
                default:
                    sink.WriteByte(b);
                    break;
            }
        }

        sink.WriteByte((byte)')');
    }
}

/// <summary>Append-only byte buffer with a position counter and a size ceiling.</summary>
internal sealed class ByteSink(long maxLength = int.MaxValue)
{
    private byte[] _buf = new byte[256];
    private int _len;

    public int Length => _len;

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buf[_len++] = b;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    public void WriteAscii(string s)
    {
        Ensure(s.Length);
        for (int i = 0; i < s.Length; i++)
            _buf[_len++] = (byte)s[i];
    }

    public void WriteHexByte(byte b)
    {
        const string digits = "0123456789ABCDEF";
        Ensure(2);
        _buf[_len++] = (byte)digits[b >> 4];
        _buf[_len++] = (byte)digits[b & 15];
    }

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

    public ReadOnlySpan<byte> AsSpan() => _buf.AsSpan(0, _len);

    private void Ensure(int extra)
    {
        if ((long)_len + extra > maxLength)
            ParseContext.ExceedLimit(PdfLimit.OutputSize, "serialised output exceeds the size limit");
        if (_len + extra <= _buf.Length)
            return;
        long want = Math.Max((long)_buf.Length * 2, (long)_len + extra);
        Array.Resize(ref _buf, (int)Math.Min(want, Array.MaxLength));
    }
}
