using System.Globalization;
using System.Text;

namespace S8.Core;

/// <summary>Strict-ish recursive-descent PDF object parser over a byte array.</summary>
public sealed class PdfParser(byte[] data)
{
    public byte[] Data { get; } = data;

    public static bool IsWs(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
    public static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    public static bool IsRegular(byte b) => !IsWs(b) && !IsDelim(b);

    public int SkipWs(int pos)
    {
        while (pos < Data.Length)
        {
            var b = Data[pos];
            if (IsWs(b)) pos++;
            else if (b == (byte)'%') { while (pos < Data.Length && Data[pos] != 10 && Data[pos] != 13) pos++; }
            else break;
        }
        return pos;
    }

    /// <summary>Skip whitespace only (comments are NOT skipped; needed to see %%EOF).</summary>
    public int SkipWsOnly(int pos) { while (pos < Data.Length && IsWs(Data[pos])) pos++; return pos; }

    public bool StartsWith(int pos, string kw)
    {
        if (pos < 0 || pos + kw.Length > Data.Length) return false;
        for (int i = 0; i < kw.Length; i++) if (Data[pos + i] != (byte)kw[i]) return false;
        return true;
    }

    /// <summary>Keyword match that requires a delimiter or whitespace after it.</summary>
    public bool KeywordAt(int pos, string kw)
    {
        if (!StartsWith(pos, kw)) return false;
        int e = pos + kw.Length;
        return e >= Data.Length || !IsRegular(Data[e]);
    }

    public string ReadToken(ref int pos)
    {
        pos = SkipWs(pos);
        int s = pos;
        while (pos < Data.Length && IsRegular(Data[pos])) pos++;
        return Encoding.Latin1.GetString(Data, s, pos - s);
    }

    public long ReadInt(ref int pos)
    {
        int p = pos;
        var t = ReadToken(ref p);
        if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            throw new PdfFormatException($"expected integer at {pos}, got '{t}'");
        pos = p;
        return v;
    }

    /// <summary>Parse one object (without lookahead into "obj"/"stream" keywords).</summary>
    public PdfObj ParseObject(ref int pos, int depth = 0)
    {
        if (depth > 64) throw new PdfFormatException("nesting too deep");
        pos = SkipWs(pos);
        if (pos >= Data.Length) throw new PdfFormatException("unexpected EOF");
        byte b = Data[pos];
        switch (b)
        {
            case (byte)'<':
                if (pos + 1 < Data.Length && Data[pos + 1] == (byte)'<') return ParseDict(ref pos, depth);
                return ParseHexString(ref pos);
            case (byte)'(':
                return ParseLiteralString(ref pos);
            case (byte)'[':
            {
                pos++;
                var arr = new PdfArray();
                while (true)
                {
                    pos = SkipWs(pos);
                    if (pos >= Data.Length) throw new PdfFormatException("unterminated array");
                    if (Data[pos] == (byte)']') { pos++; return arr; }
                    arr.Items.Add(ParseObject(ref pos, depth + 1));
                }
            }
            case (byte)'/':
                return ParseName(ref pos);
            default:
                return ParseNumberOrKeyword(ref pos);
        }
    }

    private PdfName ParseName(ref int pos)
    {
        pos++; // '/'
        var sb = new StringBuilder();
        while (pos < Data.Length && IsRegular(Data[pos]))
        {
            byte c = Data[pos];
            if (c == (byte)'#' && pos + 2 < Data.Length && Uri.IsHexDigit((char)Data[pos + 1]) && Uri.IsHexDigit((char)Data[pos + 2]))
            {
                sb.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(Data, pos + 1, 2), 16));
                pos += 3;
            }
            else { sb.Append((char)c); pos++; }
        }
        return new PdfName(sb.ToString());
    }

    private PdfDict ParseDict(ref int pos, int depth)
    {
        pos += 2;
        var d = new PdfDict();
        while (true)
        {
            pos = SkipWs(pos);
            if (pos + 1 >= Data.Length) throw new PdfFormatException("unterminated dictionary");
            if (Data[pos] == (byte)'>' && Data[pos + 1] == (byte)'>') { pos += 2; return d; }
            if (Data[pos] != (byte)'/') throw new PdfFormatException($"dictionary key expected at {pos}");
            var key = ParseName(ref pos).Value;
            var val = ParseObject(ref pos, depth + 1);
            if (d.Map.ContainsKey(key)) d.Duplicates.Add(key); // readers disagree on first-wins vs last-wins: recorded, and treated as a modification when new
            d.Map[key] = val;
        }
    }

    private PdfString ParseHexString(ref int pos)
    {
        int start = pos;
        pos++;
        var bytes = new List<byte>();
        int hi = -1;
        while (true)
        {
            if (pos >= Data.Length) throw new PdfFormatException("unterminated hex string");
            byte c = Data[pos++];
            if (c == (byte)'>') break;
            if (IsWs(c)) continue;
            int v = HexVal(c);
            if (v < 0) throw new PdfFormatException($"bad hex digit at {pos - 1}");
            if (hi < 0) hi = v; else { bytes.Add((byte)(hi * 16 + v)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi * 16));
        return new PdfString(bytes.ToArray(), true, start, pos);
    }

    private static int HexVal(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1
    };

    private PdfString ParseLiteralString(ref int pos)
    {
        int start = pos;
        pos++;
        int depth = 1;
        var bytes = new List<byte>();
        while (true)
        {
            if (pos >= Data.Length) throw new PdfFormatException("unterminated string");
            byte c = Data[pos++];
            if (c == (byte)'\\')
            {
                if (pos >= Data.Length) throw new PdfFormatException("bad escape");
                byte e = Data[pos++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13: if (pos < Data.Length && Data[pos] == 10) pos++; break;
                    case 10: break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        int v = e - '0';
                        for (int i = 0; i < 2 && pos < Data.Length && Data[pos] >= '0' && Data[pos] <= '7'; i++) v = v * 8 + (Data[pos++] - '0');
                        bytes.Add((byte)v);
                        break;
                    }
                    default: bytes.Add(e); break;
                }
            }
            else if (c == (byte)'(') { depth++; bytes.Add(c); }
            else if (c == (byte)')') { if (--depth == 0) break; bytes.Add(c); }
            else bytes.Add(c);
        }
        return new PdfString(bytes.ToArray(), false, start, pos);
    }

    private PdfObj ParseNumberOrKeyword(ref int pos)
    {
        int p0 = pos;
        var tok = ReadToken(ref pos);
        if (tok.Length == 0) throw new PdfFormatException($"unexpected byte 0x{Data[p0]:X2} at {p0}");
        if (tok == "true") return new PdfBool(true);
        if (tok == "false") return new PdfBool(false);
        if (tok == "null") return PdfNull.I;
        char c0 = tok[0];
        if (char.IsDigit(c0) || c0 == '+' || c0 == '-' || c0 == '.')
        {
            if (long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var iv))
            {
                // lookahead "G R"
                if (iv >= 0 && tok[0] != '+' && tok[0] != '-')
                {
                    int q = pos;
                    int q1 = SkipWs(q);
                    int s = q1;
                    while (q1 < Data.Length && Data[q1] >= '0' && Data[q1] <= '9') q1++;
                    if (q1 > s && q1 - s <= 5)
                    {
                        int q2 = SkipWs(q1);
                        if (q2 > q1 && q2 < Data.Length && Data[q2] == (byte)'R' && (q2 + 1 >= Data.Length || !IsRegular(Data[q2 + 1])))
                        {
                            int gen = int.Parse(Encoding.ASCII.GetString(Data, s, q1 - s), CultureInfo.InvariantCulture);
                            pos = q2 + 1;
                            return new PdfRef((int)iv, gen);
                        }
                    }
                }
                return new PdfInt(iv);
            }
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)) return new PdfReal(dv, tok);
        }
        throw new PdfFormatException($"unexpected token '{tok}' at {p0}");
    }

    /// <summary>
    /// Parse "N G obj &lt;object&gt; [stream...endstream] endobj" at an absolute offset.
    /// <paramref name="lengthResolver"/> resolves an indirect /Length. Falls back to scanning for "endstream".
    /// </summary>
    public PdfIndirect ParseIndirectAt(int offset, Func<PdfRef, PdfObj?>? lengthResolver = null)
    {
        if (offset < 0 || offset >= Data.Length) throw new PdfFormatException($"object offset {offset} out of range");
        int pos = offset;
        long num = ReadInt(ref pos);
        long gen = ReadInt(ref pos);
        var kw = ReadToken(ref pos);
        if (kw != "obj") throw new PdfFormatException($"expected 'obj' at {offset}, got '{kw}'");
        var obj = ParseObject(ref pos);
        int afterObj = SkipWs(pos);
        if (obj is PdfDict dict && KeywordAt(afterObj, "stream"))
        {
            int ds = afterObj + 6;
            if (ds < Data.Length && Data[ds] == 13) { ds++; if (ds < Data.Length && Data[ds] == 10) ds++; }
            else if (ds < Data.Length && Data[ds] == 10) ds++;
            int? len = null;
            var lo = dict["Length"];
            if (lo is PdfRef lr && lengthResolver != null) lo = lengthResolver(lr);
            if (lo is PdfInt li && li.Value >= 0 && ds + li.Value <= Data.Length) len = (int)li.Value;
            if (len != null)
            {
                int e = SkipWs(ds + len.Value);
                if (!KeywordAt(e, "endstream")) len = null;
            }
            if (len == null)
            {
                int idx = IndexOf("endstream", ds);
                if (idx < 0) throw new PdfFormatException($"endstream not found for object at {offset}");
                int end2 = idx;
                if (end2 > ds && Data[end2 - 1] == 10) end2--;
                if (end2 > ds && Data[end2 - 1] == 13) end2--;
                len = end2 - ds;
            }
            int epos = SkipWs(ds + len.Value);
            if (!KeywordAt(epos, "endstream")) throw new PdfFormatException($"endstream expected at {epos}");
            epos += 9;
            int eo = SkipWs(epos);
            if (KeywordAt(eo, "endobj")) epos = eo + 6;
            return new PdfIndirect((int)num, (int)gen, new PdfStream(dict, Data, ds, len.Value), offset, epos);
        }
        int e2 = afterObj;
        if (KeywordAt(e2, "endobj")) e2 += 6;
        else throw new PdfFormatException($"'endobj' expected at {afterObj} for object {num} {gen}");
        return new PdfIndirect((int)num, (int)gen, obj, offset, e2);
    }

    public int IndexOf(string s, int from)
    {
        var pat = Encoding.Latin1.GetBytes(s);
        return Data.AsSpan(from).IndexOf(pat) is var i and >= 0 ? from + i : -1;
    }

    public int LastIndexOf(string s, int endExclusive)
    {
        var pat = Encoding.Latin1.GetBytes(s);
        int len = Math.Min(endExclusive, Data.Length);
        var i = Data.AsSpan(0, len).LastIndexOf(pat);
        return i;
    }
}
