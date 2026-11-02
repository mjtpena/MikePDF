using System.IO.Compression;

namespace MikePDF.PdfWriter;

/// <summary>
/// Stream decoding: FlateDecode (with PNG predictors 10-15) only. Any other filter is reported as
/// <see cref="PdfErrorCode.UnsupportedFilter"/> rather than guessed at. All output is charged against the
/// per-stream and per-document decompression limits while it is produced, so a zip bomb stops at the limit
/// instead of after it.
/// </summary>
internal static class StreamDecoder
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>Largest row (bytes) a predictor may declare. Real rows are a few KB; this only bounds hostile /Columns.</summary>
    private const long MaxPredictorRowBytes = 1 << 24;

    // resolve: resolves indirect /Filter and /DecodeParms values; null when none can be resolved (xref streams).
    public static byte[] Decode(PdfStream stream, Func<PdfObject, PdfObject?>? resolve, ParseContext ctx)
    {
        var dict = stream.Dictionary;
        if (dict.ContainsKey("F"))
            ParseContext.Fatal(PdfErrorCode.UnsupportedFeature, "stream data in an external file");

        var filterObj = Resolve(dict["Filter"], resolve);
        var parmsObj = Resolve(dict["DecodeParms"], resolve);

        var filters = new List<string>();
        switch (filterObj)
        {
            case null or PdfNull:
                break;
            case PdfName n:
                filters.Add(n.Value);
                break;
            case PdfArray a:
                foreach (var item in a)
                {
                    if (Resolve(item, resolve) is PdfName fn)
                        filters.Add(fn.Value);
                    else
                        ParseContext.Fatal(PdfErrorCode.UnsupportedFilter, "filter array contains a non-name entry");
                }

                break;
            default:
                ParseContext.Fatal(PdfErrorCode.UnsupportedFilter, "unusable /Filter value");
                break;
        }

        if (filters.Count > ctx.Limits.MaxFilterChainLength)
            ParseContext.ExceedLimit(PdfLimit.FilterChainLength, "too many filters on one stream");

        var parms = new List<PdfDictionary?>();
        switch (parmsObj)
        {
            case PdfDictionary pd:
                parms.Add(pd);
                break;
            case PdfArray pa:
                foreach (var item in pa)
                    parms.Add(Resolve(item, resolve) as PdfDictionary);
                break;
        }

        ReadOnlyMemory<byte> current = stream.RawData;
        byte[]? owned = null;
        for (int i = 0; i < filters.Count; i++)
        {
            string name = filters[i];
            if (name is not ("FlateDecode" or "Fl"))
                ParseContext.Fatal(PdfErrorCode.UnsupportedFilter, $"filter /{name} is not supported");

            owned = Inflate(current, ctx);
            var p = i < parms.Count ? parms[i] : null;
            if (p is not null)
                owned = ApplyPredictor(owned, p, resolve, ctx);
            current = owned;
        }

        if (owned is not null)
            return owned;

        // No filter: the "decoded" data is the raw data. Count it, so the budget covers everything a caller can hold.
        if (current.Length > ctx.Limits.MaxDecompressedStreamBytes)
            ParseContext.ExceedLimit(PdfLimit.DecompressedStreamBytes, "stream larger than the per-stream limit");
        ctx.ChargeDecompressed(current.Length);
        return current.ToArray();
    }

    private static PdfObject? Resolve(PdfObject? o, Func<PdfObject, PdfObject?>? resolve)
    {
        if (o is PdfReference && resolve is not null)
            return resolve(o);
        if (o is PdfReference)
            ParseContext.Fatal(PdfErrorCode.UnsupportedFeature, "indirect filter parameter where none can be resolved");
        return o;
    }

    private static byte[] Inflate(ReadOnlyMemory<byte> input, ParseContext ctx)
    {
        long perStream = ctx.Limits.MaxDecompressedStreamBytes;
        using var source = OpenMemory(input);
        using var z = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buf = new byte[ChunkSize];
        long total = 0;
        try
        {
            while (true)
            {
                int n = z.Read(buf, 0, buf.Length);
                if (n == 0)
                    break;
                total += n;
                if (total > perStream)
                    ParseContext.ExceedLimit(PdfLimit.DecompressedStreamBytes, "decompressed stream exceeds the per-stream limit");
                ctx.ChargeDecompressed(n);
                output.Write(buf, 0, n);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Corrupt zlib data surfaces as InvalidDataException or, for some inflate errors, ZLibException (an IOException).
            ParseContext.Fatal(PdfErrorCode.InvalidStream, "invalid FlateDecode data: " + ex.Message);
        }

        return output.ToArray();
    }

    private static MemoryStream OpenMemory(ReadOnlyMemory<byte> input) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(input, out var seg) && seg.Array is not null
            ? new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false)
            : new MemoryStream(input.ToArray(), writable: false);

    private static byte[] ApplyPredictor(byte[] data, PdfDictionary p, Func<PdfObject, PdfObject?>? resolve, ParseContext ctx)
    {
        long predictor = (Resolve(p["Predictor"], resolve) as PdfInteger)?.Value ?? 1;
        if (predictor == 1)
            return data;
        if (predictor is < 10 or > 15)
            ParseContext.Fatal(PdfErrorCode.UnsupportedFilter, $"predictor {predictor} is not supported (PNG predictors 10-15 only)");

        long colors = (Resolve(p["Colors"], resolve) as PdfInteger)?.Value ?? 1;
        long bpc = (Resolve(p["BitsPerComponent"], resolve) as PdfInteger)?.Value ?? 8;
        long columns = (Resolve(p["Columns"], resolve) as PdfInteger)?.Value ?? 1;
        if (colors is < 1 or > 32 || bpc is not (1 or 2 or 4 or 8 or 16) || columns < 1)
            ParseContext.Fatal(PdfErrorCode.InvalidStream, "invalid predictor parameters");

        // colors <= 32, bpc <= 16 and columns <= int.MaxValue (the parser rejects larger integers), so this cannot overflow a long.
        long rowLen = ((colors * bpc * columns) + 7) / 8;
        if (rowLen > MaxPredictorRowBytes)
            ParseContext.ExceedLimit(PdfLimit.DecompressedStreamBytes, "predictor row too large");
        int rl = (int)rowLen;
        int bpp = (int)Math.Max(1, colors * bpc / 8);

        if (data.Length % (rl + 1) != 0)
        {
            ctx.Violation(PdfErrorCode.InvalidStream, "predictor data is not a whole number of rows");
        }

        int rows = data.Length / (rl + 1);
        var result = new byte[rows * rl]; // rows * rl < data.Length, so no overflow and no amplification
        for (int r = 0; r < rows; r++)
        {
            int rowType = data[r * (rl + 1)];
            int src = (r * (rl + 1)) + 1;
            int dst = r * rl;
            int prev = dst - rl;
            for (int i = 0; i < rl; i++)
            {
                int x = data[src + i];
                int a = i >= bpp ? result[dst + i - bpp] : 0;
                int b = r > 0 ? result[prev + i] : 0;
                int c = r > 0 && i >= bpp ? result[prev + i - bpp] : 0;
                int v;
                switch (rowType)
                {
                    case 0: v = x; break;
                    case 1: v = x + a; break;
                    case 2: v = x + b; break;
                    case 3: v = x + ((a + b) >> 1); break;
                    case 4: v = x + Paeth(a, b, c); break;
                    default:
                        ParseContext.Fatal(PdfErrorCode.InvalidStream, $"invalid PNG predictor row type {rowType}");
                        return result;
                }

                result[dst + i] = (byte)v;
            }
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    /// <summary>Deflates <paramref name="data"/> as a zlib stream. Used by the writer for format-matched xref streams.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    /// <summary>PNG "Up" predictor (row type 2) encoder for rows of <paramref name="rowLength"/> bytes.</summary>
    public static byte[] EncodePngUp(ReadOnlySpan<byte> data, int rowLength)
    {
        int rows = data.Length / rowLength;
        var result = new byte[rows * (rowLength + 1)];
        for (int r = 0; r < rows; r++)
        {
            int dst = r * (rowLength + 1);
            result[dst] = 2;
            for (int i = 0; i < rowLength; i++)
            {
                byte cur = data[(r * rowLength) + i];
                byte up = r > 0 ? data[((r - 1) * rowLength) + i] : (byte)0;
                result[dst + 1 + i] = (byte)(cur - up);
            }
        }

        return result;
    }
}
