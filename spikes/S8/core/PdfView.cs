using System.IO.Compression;

namespace S8.Core;

public readonly record struct XrefEntry(byte Type, long A, int B); // 0 free, 1 in-use (A=offset,B=gen), 2 compressed (A=objstm num,B=index)

public sealed class XrefSection
{
    public int Offset { get; init; }
    public bool IsStream { get; init; }
    public Dictionary<int, XrefEntry> Entries { get; } = new();
    public PdfDict Trailer { get; init; } = new();
    public long? Prev => Trailer["Prev"].IntOf();
    public long? XRefStm => Trailer["XRefStm"].IntOf();
    /// <summary>Absolute end of the "%%EOF" for this section if it could be located (-1 otherwise).</summary>
    public int EofEnd { get; set; } = -1;
}

/// <summary>A PDF file plus helpers to open "views": the document as seen by a reader at a given revision end.</summary>
public sealed class PdfFile
{
    public byte[] Data { get; }
    public PdfParser P { get; }
    public PdfFile(byte[] data) { Data = data; P = new PdfParser(data); }

    /// <summary>Find the last "startxref N" strictly before <paramref name="end"/>. Returns (startxrefKeywordPos, N).</summary>
    public (int kwPos, long offset)? FindStartXref(int end)
    {
        int idx = end;
        while (true)
        {
            idx = P.LastIndexOf("startxref", idx);
            if (idx < 0) return null;
            int pos = idx + 9;
            try
            {
                long off = P.ReadInt(ref pos);
                return (idx, off);
            }
            catch (PdfFormatException) { }
            if (idx == 0) return null;
            // keep searching earlier (the match may have been inside binary data)
        }
    }

    public PdfView OpenView(int end)
    {
        if (end <= 0 || end > Data.Length) throw new ArgumentOutOfRangeException(nameof(end));
        var sx = FindStartXref(end) ?? throw new PdfFormatException($"no startxref before offset {end}");
        return new PdfView(this, end, sx.offset);
    }

    public PdfView OpenFinalView() => OpenView(Data.Length);

    internal XrefSection ParseXrefSection(long offset, int end)
    {
        if (offset <= 0 || offset >= end) throw new PdfFormatException($"xref offset {offset} outside revision (end {end})");
        int pos = P.SkipWs((int)offset);
        if (P.KeywordAt(pos, "xref"))
        {
            pos += 4;
            var entries = new Dictionary<int, XrefEntry>();
            while (true)
            {
                pos = P.SkipWs(pos);
                if (P.KeywordAt(pos, "trailer")) { pos += 7; break; }
                long start = P.ReadInt(ref pos);
                long count = P.ReadInt(ref pos);
                if (count < 0 || count > 10_000_000) throw new PdfFormatException("bad xref subsection count");
                for (long i = 0; i < count; i++)
                {
                    long off = P.ReadInt(ref pos);
                    long gen = P.ReadInt(ref pos);
                    var t = P.ReadToken(ref pos);
                    if (t == "n") entries[(int)(start + i)] = new XrefEntry(1, off, (int)gen);
                    else if (t == "f") entries[(int)(start + i)] = new XrefEntry(0, off, (int)gen);
                    else throw new PdfFormatException($"bad xref entry type '{t}'");
                }
            }
            var trailer = P.ParseObject(ref pos) as PdfDict ?? throw new PdfFormatException("trailer is not a dictionary");
            var sec = new XrefSection { Offset = (int)offset, IsStream = false, Trailer = trailer };
            foreach (var kv in entries) sec.Entries[kv.Key] = kv.Value;
            sec.EofEnd = FindEofAfter(pos);
            return sec;
        }
        else
        {
            var ind = P.ParseIndirectAt(pos);
            if (ind.Obj is not PdfStream st || st.Dict["Type"].NameOf() != "XRef") throw new PdfFormatException($"no xref table or stream at {offset}");
            var sec = new XrefSection { Offset = (int)offset, IsStream = true, Trailer = st.Dict };
            var wArr = st.Dict["W"] as PdfArray ?? throw new PdfFormatException("xref stream without /W");
            int w0 = (int)wArr[0].IntOf()!, w1 = (int)wArr[1].IntOf()!, w2 = (int)wArr[2].IntOf()!;
            long size = st.Dict["Size"].IntOf() ?? throw new PdfFormatException("xref stream without /Size");
            var index = new List<(long, long)>();
            if (st.Dict["Index"] is PdfArray ia) for (int i = 0; i + 1 < ia.Count; i += 2) index.Add((ia[i].IntOf()!.Value, ia[i + 1].IntOf()!.Value));
            else index.Add((0, size));
            var data = PdfFilters.Decode(st, null);
            int rec = w0 + w1 + w2, p = 0;
            foreach (var (s, c) in index)
                for (long i = 0; i < c; i++)
                {
                    if (p + rec > data.Length) throw new PdfFormatException("xref stream data truncated");
                    long f0 = w0 == 0 ? 1 : ReadBE(data, p, w0);
                    long f1 = ReadBE(data, p + w0, w1);
                    long f2 = ReadBE(data, p + w0 + w1, w2);
                    p += rec;
                    sec.Entries[(int)(s + i)] = new XrefEntry((byte)f0, f1, (int)f2);
                }
            sec.EofEnd = FindEofAfter(ind.End);
            return sec;
        }
    }

    private static long ReadBE(byte[] d, int p, int n) { long v = 0; for (int i = 0; i < n; i++) v = (v << 8) | d[p + i]; return v; }

    /// <summary>After a trailer / xref stream: skip "startxref N" and locate the end of "%%EOF". -1 if not found.</summary>
    private int FindEofAfter(int pos)
    {
        try
        {
            int p = P.SkipWs(pos);
            if (!P.KeywordAt(p, "startxref")) return -1;
            p += 9;
            P.ReadInt(ref p);
            p = P.SkipWsOnly(p);
            if (P.StartsWith(p, "%%EOF")) return p + 5;
        }
        catch (PdfFormatException) { }
        return -1;
    }
}

public sealed class PdfView
{
    public PdfFile File { get; }
    public int End { get; }
    public List<XrefSection> Sections { get; } = new(); // newest first
    public Dictionary<int, XrefEntry> Entries { get; } = new();
    public PdfDict Trailer { get; }
    public long StartXref { get; }
    private readonly Dictionary<int, PdfIndirect?> _cache = new();
    private readonly Dictionary<int, List<(int num, PdfObj obj)>?> _objStmCache = new();

    public PdfView(PdfFile file, int end, long startXref)
    {
        File = file; End = end; StartXref = startXref;
        var seen = new HashSet<long>();
        long? next = startXref;
        while (next != null)
        {
            if (!seen.Add(next.Value)) throw new PdfFormatException("xref /Prev loop");
            var sec = file.ParseXrefSection(next.Value, end);
            Sections.Add(sec);
            if (sec.XRefStm is long xs && !seen.Contains(xs))
            {
                seen.Add(xs);
                var hs = file.ParseXrefSection(xs, end);
                // hybrid: stream entries sit between this table and /Prev
                Sections.Add(hs);
            }
            next = sec.Prev;
        }
        foreach (var s in Sections)
            foreach (var kv in s.Entries)
                if (!Entries.ContainsKey(kv.Key)) Entries[kv.Key] = kv.Value;
        Trailer = Sections[0].Trailer;
    }

    public bool IsEncrypted => Trailer.Has("Encrypt");
    public PdfRef? RootRef => Trailer["Root"] as PdfRef;
    public int Size => (int)(Trailer["Size"].IntOf() ?? Entries.Keys.DefaultIfEmpty(0).Max() + 1);

    public IEnumerable<int> LiveObjectNumbers => Entries.Where(kv => kv.Value.Type != 0).Select(kv => kv.Key).OrderBy(x => x);

    public PdfObj? Resolve(PdfObj? o)
    {
        for (int i = 0; i < 32 && o is PdfRef r; i++) o = GetObject(r.Num);
        return o is PdfRef ? null : o;
    }

    public PdfDict? ResolveDict(PdfObj? o) => Resolve(o) switch { PdfDict d => d, PdfStream s => s.Dict, _ => null };
    public PdfArray? ResolveArray(PdfObj? o) => Resolve(o) as PdfArray;

    public PdfObj? GetObject(int num) => GetIndirect(num)?.Obj ?? GetCompressed(num);

    /// <summary>The indirect object (with file offsets) for a type-1 xref entry. Verifies the object header matches.</summary>
    public PdfIndirect? GetIndirect(int num)
    {
        if (_cache.TryGetValue(num, out var c)) return c;
        PdfIndirect? res = null;
        if (Entries.TryGetValue(num, out var e) && e.Type == 1)
        {
            if (e.A <= 0 || e.A >= End) throw new PdfFormatException($"object {num}: offset {e.A} outside revision (end {End})");
            res = File.P.ParseIndirectAt((int)e.A, r => ResolveLength(r));
            if (res.Num != num) throw new PdfFormatException($"xref mismatch: entry for {num} points at object {res.Num} (offset {e.A})");
            if (res.End > End) throw new PdfFormatException($"object {num} extends past revision end");
        }
        _cache[num] = res;
        return res;
    }

    private PdfObj? ResolveLength(PdfRef r)
    {
        if (Entries.TryGetValue(r.Num, out var e) && e.Type == 1 && e.A > 0 && e.A < End)
        {
            try { return File.P.ParseIndirectAt((int)e.A).Obj; } catch (PdfFormatException) { return null; }
        }
        return null;
    }

    private PdfObj? GetCompressed(int num)
    {
        if (!Entries.TryGetValue(num, out var e) || e.Type != 2) return null;
        int stm = (int)e.A;
        if (!_objStmCache.TryGetValue(stm, out var list))
        {
            list = LoadObjStm(stm);
            _objStmCache[stm] = list;
        }
        if (list == null) return null;
        foreach (var (n, o) in list) if (n == num) return o;
        return null;
    }

    private List<(int, PdfObj)>? LoadObjStm(int stmNum)
    {
        if (IsEncrypted) throw new PdfUnsupportedException("object stream in encrypted file");
        var ind = GetIndirect(stmNum) ?? throw new PdfFormatException($"object stream {stmNum} missing");
        var st = ind.Obj as PdfStream ?? throw new PdfFormatException($"object {stmNum} is not a stream");
        int n = (int)(st.Dict["N"].IntOf() ?? throw new PdfFormatException("ObjStm /N"));
        int first = (int)(st.Dict["First"].IntOf() ?? throw new PdfFormatException("ObjStm /First"));
        var data = PdfFilters.Decode(st, this);
        var p = new PdfParser(data);
        var headers = new List<(int, int)>();
        int pos = 0;
        for (int i = 0; i < n; i++) { int on = (int)p.ReadInt(ref pos); int off = (int)p.ReadInt(ref pos); headers.Add((on, off)); }
        var res = new List<(int, PdfObj)>();
        foreach (var (on, off) in headers)
        {
            int q = first + off;
            res.Add((on, p.ParseObject(ref q)));
        }
        return res;
    }

    /// <summary>Raw bytes of a stream object's (decoded) contents.</summary>
    public byte[] DecodeStream(PdfStream s) => PdfFilters.Decode(s, this);
}

public static class PdfFilters
{
    public static byte[] Decode(PdfStream s, PdfView? view)
    {
        var filt = view != null ? view.Resolve(s.Dict["Filter"]) : s.Dict["Filter"];
        var names = new List<string>();
        if (filt is PdfName fn) names.Add(fn.Value);
        else if (filt is PdfArray fa) foreach (var x in fa.Items) names.Add(x.NameOf() ?? "?");
        var parms = view != null ? view.Resolve(s.Dict["DecodeParms"]) : s.Dict["DecodeParms"];
        var plist = new List<PdfDict?>();
        if (parms is PdfDict pd) plist.Add(pd);
        else if (parms is PdfArray pa) foreach (var x in pa.Items) plist.Add((view?.Resolve(x) ?? x) as PdfDict);
        var data = s.Raw.ToArray();
        for (int i = 0; i < names.Count; i++)
        {
            var n = names[i];
            if (n is "FlateDecode" or "Fl")
            {
                using var ms = new MemoryStream(data);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                data = o.ToArray();
                var pp = i < plist.Count ? plist[i] : null;
                if (pp != null) data = ApplyPredictor(data, pp);
            }
            else throw new PdfUnsupportedException($"stream filter {n}");
        }
        return data;
    }

    private static byte[] ApplyPredictor(byte[] data, PdfDict p)
    {
        long pred = p["Predictor"].IntOf() ?? 1;
        if (pred <= 1) return data;
        if (pred < 10) throw new PdfUnsupportedException($"predictor {pred}");
        int colors = (int)(p["Colors"].IntOf() ?? 1), bpc = (int)(p["BitsPerComponent"].IntOf() ?? 8), cols = (int)(p["Columns"].IntOf() ?? 1);
        int bpp = Math.Max(1, colors * bpc / 8);
        int rowLen = (colors * bpc * cols + 7) / 8;
        int rows = data.Length / (rowLen + 1);
        var outp = new byte[rows * rowLen];
        for (int r = 0; r < rows; r++)
        {
            int ft = data[r * (rowLen + 1)];
            int src = r * (rowLen + 1) + 1, dst = r * rowLen, prev = (r - 1) * rowLen;
            for (int i = 0; i < rowLen; i++)
            {
                int x = data[src + i];
                int a = i >= bpp ? outp[dst + i - bpp] : 0;
                int b = r > 0 ? outp[prev + i] : 0;
                int c = (r > 0 && i >= bpp) ? outp[prev + i - bpp] : 0;
                outp[dst + i] = (byte)(ft switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),
                    4 => x + Paeth(a, b, c),
                    _ => throw new PdfFormatException($"bad PNG predictor row type {ft}")
                });
            }
        }
        return outp;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
    }
}
