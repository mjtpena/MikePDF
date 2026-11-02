using System.Buffers.Binary;
using System.IO.Compression;

namespace MikePDF.Render.Imaging;

/// <summary>
/// A small PNG codec for the images this tool writes and reads back: 8-bit RGB (written) and RGB or RGBA (read), no interlacing. The framework has
/// no image encoder and a dependency for 150 lines is not worth an ADR, so the compressor is <see cref="ZLibStream"/> and the rest is here.
/// </summary>
internal static class Png
{
    private const long MaxPixels = 400_000_000;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rowBytes = image.Width * 3;
        var raw = new byte[(rowBytes + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var rowStart = y * (rowBytes + 1);
            raw[rowStart] = 1; // filter "Sub": each byte minus the one a pixel to the left, which compresses flat page areas to almost nothing
            var source = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    // PNG stores R, G, B; the image is B, G, R.
                    var value = image.Pixels[source + (x * 4) + (2 - channel)];
                    var left = x == 0 ? 0 : image.Pixels[source + ((x - 1) * 4) + (2 - channel)];
                    raw[rowStart + 1 + (x * 3) + channel] = (byte)(value - left);
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    public static void Save(string path, BgraImage image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(image));
    }

    /// <summary>Decodes a PNG written by <see cref="Encode"/> or any other 8-bit, non-interlaced RGB or RGBA PNG. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        var position = Signature.Length;
        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        var sawEnd = false;
        while (position + 12 <= data.Length && !sawEnd)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = data.Slice(position + 4, 4);
            if (length > data.Length - position - 12)
            {
                throw new InvalidDataException("A PNG chunk runs past the end of the file.");
            }

            var body = data.Slice(position + 8, (int)length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8 + (int)length)..]);
            if (Crc(type, body) != stored)
            {
                throw new InvalidDataException("A PNG chunk has a bad checksum.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (body.Length != 13 || body[8] != 8 || body[10] != 0 || body[11] != 0 || body[12] != 0 || body[9] is not (2 or 6))
                {
                    throw new InvalidDataException("Only 8-bit, non-interlaced RGB and RGBA PNG files are supported.");
                }

                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                channels = body[9] == 2 ? 3 : 4;
                if (width < 1 || height < 1 || (long)width * height > MaxPixels)
                {
                    throw new InvalidDataException("The PNG dimensions are outside what this tool reads.");
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
            }

            position += 12 + (int)length;
        }

        if (width == 0 || !sawEnd)
        {
            throw new InvalidDataException("The PNG has no header or no end.");
        }

        var rowBytes = width * channels;
        var raw = new byte[(rowBytes + 1) * height];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.ReadExactly(raw);
        }

        var pixels = new byte[width * height * 4];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (rowBytes + 1);
            Unfilter(raw[rowStart], raw.AsSpan(rowStart + 1, rowBytes), previous, current, channels);
            for (var x = 0; x < width; x++)
            {
                var target = ((y * width) + x) * 4;
                pixels[target] = current[(x * channels) + 2];
                pixels[target + 1] = current[(x * channels) + 1];
                pixels[target + 2] = current[x * channels];
                pixels[target + 3] = channels == 4 ? current[(x * channels) + 3] : (byte)255;
            }

            (previous, current) = (current, previous);
        }

        return new BgraImage(width, height, pixels);
    }

    public static BgraImage Load(string path) => Decode(File.ReadAllBytes(path));

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, byte[] previous, byte[] current, int bytesPerPixel)
    {
        for (var i = 0; i < source.Length; i++)
        {
            int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
            int up = previous[i];
            int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException("Unknown PNG row filter."),
            };
            current[i] = (byte)(source[i] + predictor);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length);
        output.Write(number);
        output.Write(type);
        output.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(type, body));
        output.Write(number);
    }

    internal static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in body)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
