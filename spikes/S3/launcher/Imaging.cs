using System.IO.Compression;
using System.Text;

namespace Quire.S3.Launcher;

public static class Imaging
{
    // ---- minimal PNG writer (BGRA -> RGBA 8-bit), enough to look at the renders
    static readonly uint[] CrcTable = BuildCrc();
    static uint[] BuildCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    static uint Crc(ReadOnlySpan<byte> b, uint c = 0xFFFFFFFF) { foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8); return c; }

    public static void WritePng(string path, ReadOnlySpan<byte> bgra, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); fs.Write(len);
            var tb = Encoding.ASCII.GetBytes(type);
            fs.Write(tb); fs.Write(data);
            uint crc = Crc(tb); crc = Crc(data, crc) ^ 0xFFFFFFFF;
            Span<byte> cb = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cb, crc); fs.Write(cb);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (1 + w * 3); raw[o] = 0;
            for (int x = 0; x < w; x++) { int s = (y * w + x) * 4; raw[o + 1 + x * 3] = bgra[s + 2]; raw[o + 2 + x * 3] = bgra[s + 1]; raw[o + 3 + x * 3] = bgra[s]; }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        Chunk("IDAT", ms.ToArray());
        Chunk("IEND", []);
    }

    public sealed record Diff(long Pixels, long DifferentPixels, int MaxChannelDiff, double MeanAbsErr, double Ssim, double Psnr);

    /// <summary>Per-pixel comparison plus mean SSIM on luma (8x8 windows, stride 4, standard K1=0.01 K2=0.03).</summary>
    public static Diff Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int w, int h)
    {
        long diffPx = 0; int max = 0; double sum = 0; double mse = 0;
        var la = new float[w * h]; var lb = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4; int pxMax = 0;
            for (int c = 0; c < 3; c++) { int d = Math.Abs(a[o + c] - b[o + c]); pxMax = Math.Max(pxMax, d); sum += d; mse += d * d; }
            if (pxMax > 0) diffPx++;
            max = Math.Max(max, pxMax);
            la[i] = 0.114f * a[o] + 0.587f * a[o + 1] + 0.299f * a[o + 2];
            lb[i] = 0.114f * b[o] + 0.587f * b[o + 1] + 0.299f * b[o + 2];
        }
        const double C1 = (0.01 * 255) * (0.01 * 255), C2 = (0.03 * 255) * (0.03 * 255);
        double ssimSum = 0; long wins = 0;
        for (int y = 0; y + 8 <= h; y += 4)
            for (int x = 0; x + 8 <= w; x += 4)
            {
                double ma = 0, mb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++) { double pa = la[(y + j) * w + x + i], pb = lb[(y + j) * w + x + i]; ma += pa; mb += pb; saa += pa * pa; sbb += pb * pb; sab += pa * pb; }
                ma /= 64; mb /= 64; saa = saa / 64 - ma * ma; sbb = sbb / 64 - mb * mb; sab = sab / 64 - ma * mb;
                ssimSum += ((2 * ma * mb + C1) * (2 * sab + C2)) / ((ma * ma + mb * mb + C1) * (saa + sbb + C2));
                wins++;
            }
        double m = mse / (w * h * 3.0);
        return new Diff((long)w * h, diffPx, max, sum / (w * h * 3.0), wins > 0 ? ssimSum / wins : 1, m == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / m));
    }
}
