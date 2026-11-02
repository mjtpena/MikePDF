// S2 Q3: is a compressed CPU tier worth it? Compress real rendered 512x512 BGRA tiles with the codecs that ship in .NET (no new dependency) and time them.
// usage: tierbench <dir with *.bgra (512x512x4)>
using System.Diagnostics;
using System.IO.Compression;

static class P
{
    static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
    static (double p50, double p95) Stat(List<double> v) { v.Sort(); return (v[v.Count / 2], v[(int)(0.95 * (v.Count - 1))]); }

    static void Main(string[] a)
    {
        foreach (var file in Directory.GetFiles(a[0], "*.bgra").OrderBy(x => x))
        {
            var raw = File.ReadAllBytes(file);
            Console.WriteLine($"== {Path.GetFileName(file)}  raw {raw.Length / 1024} KB");
            var codecs = new (string name, Func<byte[], byte[]> enc, Func<byte[], byte[]> dec)[]
            {
                ("deflate-fastest", b => Deflate(b, CompressionLevel.Fastest), b => Inflate(b, raw.Length)),
                ("deflate-optimal", b => Deflate(b, CompressionLevel.Optimal), b => Inflate(b, raw.Length)),
                ("brotli-q1", b => Brotli(b, 1), b => Unbrotli(b, raw.Length)),
                ("brotli-q4", b => Brotli(b, 4), b => Unbrotli(b, raw.Length)),
                ("delta+deflate-fastest", b => Deflate(DeltaEncode(b), CompressionLevel.Fastest), b => DeltaDecode(Inflate(b, raw.Length))),
            };
            foreach (var (name, enc, dec) in codecs)
            {
                byte[] c = enc(raw); dec(c);                                 // warm-up
                var te = new List<double>(); var td = new List<double>();
                for (int i = 0; i < 40; i++) { long t0 = Stopwatch.GetTimestamp(); c = enc(raw); long t1 = Stopwatch.GetTimestamp(); var d = dec(c); long t2 = Stopwatch.GetTimestamp(); te.Add(Ms(t1 - t0)); td.Add(Ms(t2 - t1)); if (i == 0 && !d.AsSpan().SequenceEqual(raw)) Console.WriteLine("  MISMATCH"); }
                var (e50, e95) = Stat(te); var (d50, d95) = Stat(td);
                Console.WriteLine($"  {name,-22} {c.Length / 1024,6} KB  ratio {raw.Length / (double)c.Length,6:F1}x   compress p50 {e50,7:F2} p95 {e95,7:F2} ms   decompress p50 {d50,6:F2} p95 {d95,6:F2} ms");
            }
        }
    }
    static byte[] Deflate(byte[] b, CompressionLevel l) { using var ms = new MemoryStream(); using (var z = new DeflateStream(ms, l, true)) z.Write(b); return ms.ToArray(); }
    static byte[] Inflate(byte[] b, int n) { var r = new byte[n]; using var z = new DeflateStream(new MemoryStream(b), CompressionMode.Decompress); z.ReadExactly(r); return r; }
    static byte[] Brotli(byte[] b, int q) { var o = new byte[BrotliEncoder.GetMaxCompressedLength(b.Length)]; BrotliEncoder.TryCompress(b, o, out int w, q, 22); return o.AsSpan(0, w).ToArray(); }
    static byte[] Unbrotli(byte[] b, int n) { var r = new byte[n]; BrotliDecoder.TryDecompress(b, r, out int w); return r; }
    static byte[] DeltaEncode(byte[] b) { var r = new byte[b.Length]; byte p0 = 0, p1 = 0, p2 = 0, p3 = 0; for (int i = 0; i < b.Length; i += 4) { r[i] = (byte)(b[i] - p0); r[i + 1] = (byte)(b[i + 1] - p1); r[i + 2] = (byte)(b[i + 2] - p2); r[i + 3] = (byte)(b[i + 3] - p3); p0 = b[i]; p1 = b[i + 1]; p2 = b[i + 2]; p3 = b[i + 3]; } return r; }
    static byte[] DeltaDecode(byte[] b) { var r = new byte[b.Length]; byte p0 = 0, p1 = 0, p2 = 0, p3 = 0; for (int i = 0; i < b.Length; i += 4) { p0 += b[i]; p1 += b[i + 1]; p2 += b[i + 2]; p3 += b[i + 3]; r[i] = p0; r[i + 1] = p1; r[i + 2] = p2; r[i + 3] = p3; } return r; }
}
