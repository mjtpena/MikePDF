using System.Collections.Immutable;
using System.Globalization;

namespace Quire.PdfWriter;

/// <summary>A parsed "N G obj ... endobj" and where it sits in the buffer.</summary>
internal sealed record ParsedIndirectObject(int Number, int Generation, PdfObject Value, int Start, int End);

/// <summary>
/// Lexer plus recursive-descent parser for PDF objects over one buffer (ISO 32000-2 clause 7.3).
///
/// Strict mode (see <see cref="ParseContext"/>) rejects everything a second reader might read differently:
/// duplicate dictionary keys, malformed numbers, bad name escapes, bad hex digits, a stream keyword not followed
/// by LF or CRLF, a /Length that does not land on "endstream", a missing "endobj".
/// Recursion is bounded by the nesting-depth limit; every length is checked before it is used.
/// </summary>
internal sealed class ObjectParser
{
    /// <summary>Longest run of regular characters read as one keyword or number token.</summary>
    private const int MaxTokenLength = 128;

    private const int SmallDictionary = 8;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly ParseContext _ctx;
    private int _pos;

    public ObjectParser(ReadOnlyMemory<byte> data, ParseContext ctx, int position = 0)
    {
        _data = data;
        _ctx = ctx;
        _pos = position;
    }

    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    /// <summary>Parses one direct object (or an "N G R" reference) starting at the current position.</summary>
    public PdfObject ParseObject(int depth = 0)
    {
        var data = _data.Span;
        _pos = PdfBytes.SkipWhitespaceAndComments(data, _pos);
        if (_pos >= data.Length)
            ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "object expected", _pos);

        byte b = data[_pos];
        switch (b)
        {
            case (byte)'<':
                return _pos + 1 < data.Length && data[_pos + 1] == (byte)'<' ? ParseDictionary(depth) : ParseHexString();
            case (byte)'(':
                return ParseLiteralString();
            case (byte)'[':
                return ParseArray(depth);
            case (byte)'/':
                return ParseName();
            default:
                if (PdfBytes.IsDelimiter(b))
                    ParseContext.Fatal(PdfErrorCode.InvalidSyntax, $"unexpected delimiter 0x{b:X2}", _pos);
                return ParseNumberOrKeyword();
        }
    }

    /// <summary>
    /// Parses "N G obj &lt;object&gt; [stream ... endstream] endobj" at an absolute offset.
    /// <paramref name="lengthResolver"/> resolves an indirect /Length (null when none is available).
    /// </summary>
    public ParsedIndirectObject ParseIndirectObject(int offset, Func<PdfReference, long?>? lengthResolver)
    {
        var data = _data.Span;
        if (offset < 0 || offset >= data.Length)
            ParseContext.Fatal(PdfErrorCode.XrefOffsetOutOfRange, $"object offset {offset} is outside the file", offset);

        // Leading whitespace is harmless (it cannot change which object is read), so it is tolerated; comments are not.
        int start = PdfBytes.SkipWhitespace(data, offset);
        int pos = start;
        long number = ReadHeaderInteger(data, ref pos, 10, "object number", offset);
        if (number > int.MaxValue)
            ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, "object number out of range", offset);
        pos = RequireWhitespace(data, pos, offset);
        long generation = ReadHeaderInteger(data, ref pos, 5, "generation number", offset);
        if (generation > 65535)
            ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, "generation number out of range", offset);
        pos = RequireWhitespace(data, pos, offset);
        if (!PdfBytes.KeywordAt(data, pos, "obj"u8))
            ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, "expected 'obj' keyword", offset);
        pos += 3;

        _pos = pos;
        int afterHeader = PdfBytes.SkipWhitespaceAndComments(data, pos);
        PdfObject value;
        if (PdfBytes.KeywordAt(data, afterHeader, "endobj"u8))
        {
            _ctx.Violation(PdfErrorCode.InvalidSyntax, "object has no value", afterHeader);
            value = PdfNull.Instance;
            _pos = afterHeader;
        }
        else
        {
            value = ParseObject();
        }

        int p = PdfBytes.SkipWhitespaceAndComments(data, _pos);
        if (PdfBytes.KeywordAt(data, p, "stream"u8))
        {
            if (value is PdfDictionary dict)
                return ParseStreamBody(dict, (int)number, (int)generation, start, p + 6, lengthResolver);
            _ctx.Violation(PdfErrorCode.InvalidStream, "'stream' keyword after a non-dictionary object", p);
        }

        int end;
        if (PdfBytes.KeywordAt(data, p, "endobj"u8))
        {
            end = p + 6;
        }
        else
        {
            _ctx.Violation(PdfErrorCode.MissingEndObj, $"'endobj' expected for object {number} {generation}", p);
            end = _pos;
        }

        _pos = end;
        return new ParsedIndirectObject((int)number, (int)generation, value, start, end);
    }

    private ParsedIndirectObject ParseStreamBody(PdfDictionary dict, int number, int generation, int start, int afterKeyword, Func<PdfReference, long?>? lengthResolver)
    {
        var data = _data.Span;
        int ds = afterKeyword;

        // 7.3.8.1: the keyword is followed by CRLF or LF, never a lone CR. A lone CR is a classic reader differential.
        if (ds < data.Length && data[ds] == 13)
        {
            ds++;
            if (ds < data.Length && data[ds] == 10)
                ds++;
            else
                _ctx.Violation(PdfErrorCode.InvalidStream, "'stream' keyword followed by a lone CR", afterKeyword);
        }
        else if (ds < data.Length && data[ds] == 10)
        {
            ds++;
        }
        else
        {
            _ctx.Violation(PdfErrorCode.InvalidStream, "'stream' keyword not followed by an end-of-line marker", afterKeyword);
        }

        long? declared = null;
        switch (dict["Length"])
        {
            case PdfInteger li:
                declared = li.Value;
                break;
            case PdfReference lr:
                declared = lengthResolver?.Invoke(lr);
                if (declared is null)
                    _ctx.Violation(PdfErrorCode.StreamLengthMismatch, "indirect /Length could not be resolved", ds);
                break;
            default:
                _ctx.Violation(PdfErrorCode.StreamLengthMismatch, "stream has no usable /Length", ds);
                break;
        }

        int length = -1;
        if (declared is long dl)
        {
            if (dl >= 0 && dl <= data.Length - ds)
            {
                int after = PdfBytes.SkipWhitespace(data, ds + (int)dl);
                if (PdfBytes.KeywordAt(data, after, "endstream"u8))
                    length = (int)dl;
            }

            if (length < 0)
                _ctx.Violation(PdfErrorCode.StreamLengthMismatch, $"/Length {dl} does not end at 'endstream'", ds);
        }

        if (length < 0)
        {
            // Lenient recovery only: scan for the keyword and trim one end-of-line marker before it.
            int idx = data[ds..].IndexOf("endstream"u8);
            if (idx < 0)
                ParseContext.Fatal(PdfErrorCode.StreamLengthMismatch, "'endstream' not found", ds);
            int endData = ds + idx;
            if (endData > ds && data[endData - 1] == 10)
                endData--;
            if (endData > ds && data[endData - 1] == 13)
                endData--;
            length = endData - ds;
        }

        int es = PdfBytes.SkipWhitespace(data, ds + length) + 9;
        int eo = PdfBytes.SkipWhitespace(data, es);
        int end;
        if (PdfBytes.KeywordAt(data, eo, "endobj"u8))
        {
            end = eo + 6;
        }
        else
        {
            _ctx.Violation(PdfErrorCode.MissingEndObj, $"'endobj' expected after stream of object {number} {generation}", eo);
            end = es;
        }

        _pos = end;
        return new ParsedIndirectObject(number, generation, new PdfStream(dict, _data.Slice(ds, length)), start, end);
    }

    private static long ReadHeaderInteger(ReadOnlySpan<byte> data, ref int pos, int maxDigits, string what, int offset)
    {
        int s = pos;
        long v = 0;
        while (pos < data.Length && PdfBytes.IsDigit(data[pos]))
        {
            if (pos - s >= maxDigits)
                ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, $"{what} too long", offset);
            v = (v * 10) + (data[pos] - '0');
            pos++;
        }

        if (pos == s)
            ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, $"{what} expected at object offset", offset);
        return v;
    }

    private static int RequireWhitespace(ReadOnlySpan<byte> data, int pos, int offset)
    {
        int p = PdfBytes.SkipWhitespace(data, pos);
        if (p == pos)
            ParseContext.Fatal(PdfErrorCode.ObjectHeaderMismatch, "whitespace expected in object header", offset);
        return p;
    }

    private PdfArray ParseArray(int depth)
    {
        if (depth >= _ctx.MaxDepth)
            ParseContext.ExceedLimit(PdfLimit.NestingDepth, "array nesting too deep", _pos);
        var data = _data.Span;
        _pos++;
        var items = ImmutableArray.CreateBuilder<PdfObject>();
        while (true)
        {
            _pos = PdfBytes.SkipWhitespaceAndComments(data, _pos);
            if (_pos >= data.Length)
                ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "unterminated array", _pos);
            if (data[_pos] == (byte)']')
            {
                _pos++;
                return PdfArray.FromTrusted(items.ToImmutable());
            }

            if (items.Count >= _ctx.Limits.MaxArrayLength)
                ParseContext.ExceedLimit(PdfLimit.ArrayLength, "array too long", _pos);
            items.Add(ParseObject(depth + 1));
        }
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        if (depth >= _ctx.MaxDepth)
            ParseContext.ExceedLimit(PdfLimit.NestingDepth, "dictionary nesting too deep", _pos);
        var data = _data.Span;
        _pos += 2;
        var entries = new List<KeyValuePair<PdfName, PdfObject>>();
        HashSet<string>? keys = null;
        while (true)
        {
            _pos = PdfBytes.SkipWhitespaceAndComments(data, _pos);
            if (_pos >= data.Length)
                ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "unterminated dictionary", _pos);
            if (data[_pos] == (byte)'>')
            {
                if (_pos + 1 >= data.Length || data[_pos + 1] != (byte)'>')
                    ParseContext.Fatal(PdfErrorCode.InvalidSyntax, "single '>' in dictionary", _pos);
                _pos += 2;
                return PdfDictionary.FromTrusted(entries.ToImmutableArray());
            }

            if (data[_pos] != (byte)'/')
                ParseContext.Fatal(PdfErrorCode.InvalidSyntax, "dictionary key (a name) expected", _pos);

            int keyPos = _pos;
            var key = ParseName();
            _pos = PdfBytes.SkipWhitespaceAndComments(data, _pos);
            PdfObject value;
            if (_pos + 1 < data.Length && data[_pos] == (byte)'>' && data[_pos + 1] == (byte)'>')
            {
                _ctx.Violation(PdfErrorCode.InvalidSyntax, $"dictionary key /{key.Value} has no value", keyPos);
                value = PdfNull.Instance;
            }
            else
            {
                value = ParseObject(depth + 1);
            }

            int existing = -1;
            if (entries.Count <= SmallDictionary)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Key.Value, key.Value, StringComparison.Ordinal))
                    {
                        existing = i;
                        break;
                    }
                }
            }
            else
            {
                if (keys is null)
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in entries)
                        keys.Add(e.Key.Value);
                }

                if (keys.Contains(key.Value))
                    existing = entries.FindIndex(e => string.Equals(e.Key.Value, key.Value, StringComparison.Ordinal));
            }

            if (existing >= 0)
            {
                // Readers disagree on first-wins vs last-wins, so strict mode refuses. Lenient: last wins (diagnostics only).
                _ctx.Violation(PdfErrorCode.DuplicateKey, $"duplicate dictionary key /{key.Value}", keyPos);
                entries[existing] = new KeyValuePair<PdfName, PdfObject>(entries[existing].Key, value);
                continue;
            }

            if (entries.Count >= _ctx.Limits.MaxDictionaryEntries)
                ParseContext.ExceedLimit(PdfLimit.DictionaryEntries, "dictionary has too many entries", keyPos);
            entries.Add(new KeyValuePair<PdfName, PdfObject>(key, value));
            keys?.Add(key.Value);
        }
    }

    private PdfName ParseName()
    {
        var data = _data.Span;
        int start = _pos + 1;
        int end = start;
        int rawMax = _ctx.Limits.MaxNameLength > int.MaxValue / 3 ? int.MaxValue : _ctx.Limits.MaxNameLength * 3;
        while (end < data.Length && PdfBytes.IsRegular(data[end]))
        {
            end++;
            if (end - start > rawMax)
                ParseContext.ExceedLimit(PdfLimit.NameLength, "name too long", _pos);
        }

        int rawLen = end - start;
        Span<char> buf = rawLen <= 256 ? stackalloc char[256] : new char[rawLen];
        int n = 0;
        int i = start;
        while (i < end)
        {
            byte c = data[i];
            if (c == (byte)'#')
            {
                int h1 = end - i >= 3 ? PdfBytes.HexValue(data[i + 1]) : -1;
                int h2 = h1 >= 0 ? PdfBytes.HexValue(data[i + 2]) : -1;
                if (h2 >= 0)
                {
                    int v = (h1 * 16) + h2;
                    if (v == 0)
                        _ctx.Violation(PdfErrorCode.InvalidName, "name contains #00", i);
                    buf[n++] = (char)v;
                    i += 3;
                    continue;
                }

                _ctx.Violation(PdfErrorCode.InvalidName, "'#' in a name is not followed by two hex digits", i);
            }

            buf[n++] = (char)c;
            i++;
        }

        if (n > _ctx.Limits.MaxNameLength)
            ParseContext.ExceedLimit(PdfLimit.NameLength, "name too long", _pos);
        _pos = end;
        return new PdfName(new string(buf[..n]));
    }

    private PdfString ParseHexString()
    {
        var data = _data.Span;
        int pos = _pos + 1;
        var bytes = new ByteBuilder(_ctx.Limits.MaxStringLength);
        int hi = -1;
        while (true)
        {
            if (pos >= data.Length)
                ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "unterminated hex string", _pos);
            byte c = data[pos++];
            if (c == (byte)'>')
                break;
            if (PdfBytes.IsWhitespace(c))
                continue;
            int v = PdfBytes.HexValue(c);
            if (v < 0)
            {
                _ctx.Violation(PdfErrorCode.InvalidString, $"invalid hex digit 0x{c:X2} in hex string", pos - 1);
                continue;
            }

            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                bytes.Add((byte)((hi * 16) + v));
                hi = -1;
            }
        }

        if (hi >= 0)
            bytes.Add((byte)(hi * 16)); // 7.3.4.3: a final lone digit is followed by an implied 0
        _pos = pos;
        return new PdfString(bytes.ToArray(), PdfStringFormat.Hex);
    }

    private PdfString ParseLiteralString()
    {
        var data = _data.Span;
        int pos = _pos + 1;
        int nesting = 1;
        var bytes = new ByteBuilder(_ctx.Limits.MaxStringLength);
        while (true)
        {
            if (pos >= data.Length)
                ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "unterminated string", _pos);
            byte c = data[pos++];
            switch (c)
            {
                case (byte)'\\':
                    if (pos >= data.Length)
                        ParseContext.Fatal(PdfErrorCode.UnexpectedEndOfData, "unterminated string escape", _pos);
                    byte e = data[pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case 13:
                            if (pos < data.Length && data[pos] == 10)
                                pos++;
                            break; // line continuation
                        case 10:
                            break; // line continuation
                        case >= (byte)'0' and <= (byte)'7':
                            int v = e - '0';
                            for (int k = 0; k < 2 && pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'7'; k++)
                                v = (v * 8) + (data[pos++] - '0');
                            bytes.Add((byte)(v & 0xFF)); // 7.3.4.2: high-order overflow is ignored
                            break;
                        default:
                            bytes.Add(e); // 7.3.4.2: an unknown escape just drops the backslash
                            break;
                    }

                    break;
                case (byte)'(':
                    nesting++;
                    bytes.Add(c);
                    break;
                case (byte)')':
                    if (--nesting == 0)
                    {
                        _pos = pos;
                        return new PdfString(bytes.ToArray(), PdfStringFormat.Literal);
                    }

                    bytes.Add(c);
                    break;
                case 13: // 7.3.4.2: a bare CR or CRLF inside a literal string reads as a single LF
                    bytes.Add(10);
                    if (pos < data.Length && data[pos] == 10)
                        pos++;
                    break;
                default:
                    bytes.Add(c);
                    break;
            }
        }
    }

    private PdfObject ParseNumberOrKeyword()
    {
        var data = _data.Span;
        int start = _pos;
        int end = start;
        while (end < data.Length && PdfBytes.IsRegular(data[end]))
        {
            end++;
            if (end - start > MaxTokenLength)
                ParseContext.ExceedLimit(PdfLimit.TokenLength, "token too long", start);
        }

        var token = data[start..end];
        _pos = end;

        if (token.SequenceEqual("true"u8))
            return PdfBoolean.True;
        if (token.SequenceEqual("false"u8))
            return PdfBoolean.False;
        if (token.SequenceEqual("null"u8))
            return PdfNull.Instance;

        byte first = token[0];
        if (!(PdfBytes.IsDigit(first) || first is (byte)'+' or (byte)'-' or (byte)'.'))
        {
            _ctx.Violation(PdfErrorCode.InvalidSyntax, $"unexpected keyword '{System.Text.Encoding.Latin1.GetString(token)}'", start);
            return PdfNull.Instance;
        }

        return ParseNumber(token, start);
    }

    private PdfObject ParseNumber(ReadOnlySpan<byte> token, int start)
    {
        int i = 0;
        bool negative = false;
        bool signed = false;
        if (token[0] is (byte)'+' or (byte)'-')
        {
            negative = token[0] == (byte)'-';
            signed = true;
            i = 1;
        }

        int digitsBefore = 0;
        int intStart = i;
        while (i < token.Length && PdfBytes.IsDigit(token[i]))
        {
            i++;
            digitsBefore++;
        }

        bool isReal = false;
        int digitsAfter = 0;
        if (i < token.Length && token[i] == (byte)'.')
        {
            isReal = true;
            i++;
            while (i < token.Length && PdfBytes.IsDigit(token[i]))
            {
                i++;
                digitsAfter++;
            }
        }

        if (i != token.Length || digitsBefore + digitsAfter == 0)
        {
            _ctx.Violation(PdfErrorCode.InvalidNumber, $"malformed number '{System.Text.Encoding.Latin1.GetString(token)}'", start);
            return PdfNull.Instance;
        }

        if (isReal)
        {
            if (!double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double d)
                || double.IsInfinity(d) || Math.Abs(d) > PdfReal.MaxMagnitude)
            {
                _ctx.Violation(PdfErrorCode.InvalidNumber, "real number out of range", start);
                return PdfNull.Instance;
            }

            return new PdfReal(d);
        }

        long value = 0;
        for (int k = intStart; k < token.Length; k++)
        {
            value = (value * 10) + (token[k] - '0');
            if (value > int.MaxValue + 1L)
            {
                // 32-bit range only: PDFium and most readers do not agree on what a larger integer means.
                _ctx.Violation(PdfErrorCode.InvalidNumber, "integer outside the 32-bit range", start);
                value = int.MaxValue;
                break;
            }
        }

        if (negative)
            value = -value;
        if (value is > int.MaxValue or < int.MinValue)
        {
            _ctx.Violation(PdfErrorCode.InvalidNumber, "integer outside the 32-bit range", start);
            value = negative ? int.MinValue : int.MaxValue;
        }

        if (!signed && value <= int.MaxValue && TryParseReferenceTail((int)value, out var reference))
            return reference;
        return new PdfInteger(value);
    }

    /// <summary>After "N", looks for " G R". Whitespace only between the tokens; comments are not skipped.</summary>
    private bool TryParseReferenceTail(int number, out PdfReference reference)
    {
        reference = null!;
        var data = _data.Span;
        int p = PdfBytes.SkipWhitespace(data, _pos);
        if (p == _pos)
            return false;
        int d0 = p;
        long gen = 0;
        while (p < data.Length && PdfBytes.IsDigit(data[p]))
        {
            if (p - d0 >= 5)
                return false;
            gen = (gen * 10) + (data[p] - '0');
            p++;
        }

        if (p == d0)
            return false;
        int p2 = PdfBytes.SkipWhitespace(data, p);
        if (p2 == p || !PdfBytes.KeywordAt(data, p2, "R"u8))
            return false;
        if (gen > 65535)
        {
            _ctx.Violation(PdfErrorCode.InvalidReference, "generation number above 65535 in a reference", d0);
            gen = 65535;
        }

        _pos = p2 + 1;
        reference = new PdfReference(number, (int)gen);
        return true;
    }

    /// <summary>Growable byte buffer that enforces a length limit.</summary>
    private sealed class ByteBuilder(int limit)
    {
        private byte[] _buf = [];
        private int _len;

        public void Add(byte b)
        {
            if (_len >= limit)
                ParseContext.ExceedLimit(PdfLimit.StringLength, "string too long");
            if (_len == _buf.Length)
                Array.Resize(ref _buf, Math.Min(Math.Max(16, _buf.Length * 2), Math.Max(limit, 16)));
            _buf[_len++] = b;
        }

        public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    }
}
