using System.IO.Compression;

namespace S9;

internal static class Imaging
{
    // ---- PNG writer (8-bit RGB, no deps) ----
    public static void SavePng(string path, Bgra img)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, img.W); WriteBE(ihdr, 4, img.H); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        var raw = new byte[(img.W * 3 + 1) * img.H];
        for (var y = 0; y < img.H; y++)
        {
            var o = y * (img.W * 3 + 1); raw[o] = 0;
            for (var x = 0; x < img.W; x++)
            {
                var s = (y * img.W + x) * 4; var d = o + 1 + x * 3;
                raw[d] = img.Px[s + 2]; raw[d + 1] = img.Px[s + 1]; raw[d + 2] = img.Px[s];
            }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", []);
    }

    static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return ~c;
    }

    static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        var crcIn = new byte[4 + data.Length]; t.CopyTo(crcIn, 0); data.CopyTo(crcIn, 4);
        var crc = Crc32(crcIn);
        var c = new byte[4]; WriteBE(c, 0, (int)crc); s.Write(c);
    }

    // ---- Crop ----
    public static Bgra Crop(Bgra a, int x0, int y0, int w, int h)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); w = Math.Min(w, a.W - x0); h = Math.Min(h, a.H - y0);
        var r = new Bgra(w, h);
        for (var y = 0; y < h; y++) Buffer.BlockCopy(a.Px, ((y0 + y) * a.W + x0) * 4, r.Px, y * w * 4, w * 4);
        return r;
    }

    // ---- SSIM (Wang et al. 2004): 11x11 Gaussian window sigma 1.5, K1 0.01, K2 0.03, L 255, on BT.601 luma, mean over the valid region ----
    static float[]? _g;
    static float[] Gauss()
    {
        if (_g != null) return _g;
        var g = new float[11]; float sum = 0;
        for (var i = 0; i < 11; i++) { var d = i - 5; g[i] = MathF.Exp(-d * d / (2 * 1.5f * 1.5f)); sum += g[i]; }
        for (var i = 0; i < 11; i++) g[i] /= sum;
        return _g = g;
    }

    static float[] Luma(Bgra a)
    {
        var l = new float[a.W * a.H];
        for (var i = 0; i < l.Length; i++) l[i] = 0.299f * a.Px[i * 4 + 2] + 0.587f * a.Px[i * 4 + 1] + 0.114f * a.Px[i * 4];
        return l;
    }

    /// <summary>Separable valid-region Gaussian filter: output (w-10) x (h-10).</summary>
    static float[] Filter(float[] src, int w, int h)
    {
        var g = Gauss(); var ow = w - 10; var oh = h - 10;
        var tmp = new float[ow * h];
        Parallel.For(0, h, y => { for (var x = 0; x < ow; x++) { float s = 0; for (var k = 0; k < 11; k++) s += g[k] * src[y * w + x + k]; tmp[y * ow + x] = s; } });
        var dst = new float[ow * oh];
        Parallel.For(0, oh, y => { for (var x = 0; x < ow; x++) { float s = 0; for (var k = 0; k < 11; k++) s += g[k] * tmp[(y + k) * ow + x]; dst[y * ow + x] = s; } });
        return dst;
    }

    /// <summary>Mean SSIM; images must be the same size and at least 11x11.</summary>
    public static double Ssim(Bgra a, Bgra b)
    {
        if (a.W != b.W || a.H != b.H) throw new ArgumentException("size mismatch");
        if (a.W < 11 || a.H < 11) return 1.0;
        var la = Luma(a); var lb = Luma(b); var n = la.Length;
        var aa = new float[n]; var bb = new float[n]; var ab = new float[n];
        for (var i = 0; i < n; i++) { aa[i] = la[i] * la[i]; bb[i] = lb[i] * lb[i]; ab[i] = la[i] * lb[i]; }
        var ma = Filter(la, a.W, a.H); var mb = Filter(lb, a.W, a.H);
        var saa = Filter(aa, a.W, a.H); var sbb = Filter(bb, a.W, a.H); var sab = Filter(ab, a.W, a.H);
        const float c1 = (0.01f * 255) * (0.01f * 255), c2 = (0.03f * 255) * (0.03f * 255);
        double total = 0;
        for (var i = 0; i < ma.Length; i++)
        {
            var va = saa[i] - ma[i] * ma[i]; var vb = sbb[i] - mb[i] * mb[i]; var cov = sab[i] - ma[i] * mb[i];
            total += ((2 * ma[i] * mb[i] + c1) * (2 * cov + c2)) / ((ma[i] * ma[i] + mb[i] * mb[i] + c1) * (va + vb + c2));
        }
        return total / ma.Length;
    }

    public record DiffStats(double MeanAbs, int MaxAbs, double PctOver8, double PctOver32, long Differing);

    public static DiffStats Diff(Bgra a, Bgra b)
    {
        long sum = 0; int max = 0; long o8 = 0, o32 = 0, nz = 0; var n = a.W * a.H;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Max(Math.Abs(a.Px[i * 4] - b.Px[i * 4]), Math.Max(Math.Abs(a.Px[i * 4 + 1] - b.Px[i * 4 + 1]), Math.Abs(a.Px[i * 4 + 2] - b.Px[i * 4 + 2])));
            sum += d; if (d > max) max = d; if (d > 8) o8++; if (d > 32) o32++; if (d != 0) nz++;
        }
        return new DiffStats(sum / (double)n, max, 100.0 * o8 / n, 100.0 * o32 / n, nz);
    }

    public static string Hash(Bgra a) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Px))[..16];

    /// <summary>Force alpha to 255 and un-premultiply (Win2D read-back is premultiplied; our targets are opaque so this is a no-op, kept for safety).</summary>
    public static Bgra Opaque(Bgra a) { for (var i = 3; i < a.Px.Length; i += 4) a.Px[i] = 255; return a; }
}
