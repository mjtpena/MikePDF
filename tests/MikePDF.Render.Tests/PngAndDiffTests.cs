using System.Buffers.Binary;
using System.IO.Compression;
using MikePDF.Render.Imaging;

namespace MikePDF.Render.Tests;

public sealed class PngAndDiffTests
{
    private static BgraImage Pattern(int width, int height)
    {
        var image = BgraImage.Create(width, height, 255, 255, 255);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                image.Pixels[at] = (byte)((x * 7) + y);
                image.Pixels[at + 1] = (byte)((y * 3) ^ x);
                image.Pixels[at + 2] = (byte)(x > width / 2 ? 255 : y);
            }
        }

        return image;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(300, 200)]
    [InlineData(1025, 3)]
    public void APictureSurvivesAPngRoundTripExactly(int width, int height)
    {
        var original = Pattern(width, height);
        var decoded = Png.Decode(Png.Encode(original));
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    [Fact]
    public void ThePngHasTheRightSignatureHeaderAndChecksums()
    {
        var bytes = Png.Encode(Pattern(5, 4));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
        Assert.Equal("IHDR"u8.ToArray(), bytes[12..16]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, bytes[24..29]);
        Assert.Equal(0xAE426082u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4))); // the well-known CRC of an empty IEND chunk
        Assert.Equal(0xAE426082u, Png.Crc("IEND"u8, []));
    }

    [Fact]
    public void ACorruptedPngIsRefused()
    {
        var bytes = Png.Encode(Pattern(20, 20));
        var flipped = (byte[])bytes.Clone();
        flipped[45] ^= 0xFF; // inside the compressed data (the IDAT body starts at byte 41): the chunk checksum no longer matches
        Assert.Throws<InvalidDataException>(() => Png.Decode(flipped));
        Assert.Throws<InvalidDataException>(() => Png.Decode(bytes.AsSpan(0, bytes.Length - 6)));
        Assert.Throws<InvalidDataException>(() => Png.Decode("not a png at all"u8));
        Assert.Throws<InvalidDataException>(() => Png.Decode([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryRowFilterOfThePngSpecificationIsUndoneCorrectly(int filter)
    {
        // A 6 x 5 RGB image filtered by hand with each filter type, so the decoder is checked against the specification and not just against our encoder.
        const int width = 6, height = 5, channels = 3;
        var rows = new byte[height][];
        for (var y = 0; y < height; y++)
        {
            rows[y] = new byte[width * channels];
            for (var i = 0; i < rows[y].Length; i++)
            {
                rows[y][i] = (byte)((i * 13) + (y * 29) + (i * y));
            }
        }

        var raw = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            raw.Add((byte)filter);
            for (var i = 0; i < rows[y].Length; i++)
            {
                int left = i >= channels ? rows[y][i - channels] : 0;
                int up = y > 0 ? rows[y - 1][i] : 0;
                int upLeft = y > 0 && i >= channels ? rows[y - 1][i - channels] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => PaethReference(left, up, upLeft),
                };
                raw.Add((byte)(rows[y][i] - predictor));
            }
        }

        var png = BuildPng(width, height, 2, raw.ToArray());
        var image = Png.Decode(png);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                Assert.Equal(rows[y][(x * 3) + 2], image.Pixels[at]);     // B
                Assert.Equal(rows[y][(x * 3) + 1], image.Pixels[at + 1]); // G
                Assert.Equal(rows[y][x * 3], image.Pixels[at + 2]);       // R
                Assert.Equal(255, image.Pixels[at + 3]);
            }
        }
    }

    [Fact]
    public void AnRgbaPngIsReadWithItsAlpha()
    {
        var raw = new byte[] { 0, 10, 20, 30, 40, 50, 60, 70, 80 }; // one row, filter none: pixel 1 = (10,20,30,40), pixel 2 = (50,60,70,80)
        var image = Png.Decode(BuildPng(2, 1, 6, raw));
        Assert.Equal(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 }, image.Pixels);
    }

    [Fact]
    public void PngsThisToolDoesNotReadAreRefusedWithAReason()
    {
        var sixteenBit = BuildPng(1, 1, 2, [0, 0, 0, 0], bitDepth: 16);
        Assert.Contains("8-bit", Assert.Throws<InvalidDataException>(() => Png.Decode(sixteenBit)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiffPictureHasThreePanelsAndMarksOnlyWhereThePagesDiffer()
    {
        var reference = BgraImage.Create(20, 10, 255, 255, 255);
        var actual = BgraImage.Create(20, 10, 255, 255, 255);
        var at = ((4 * 20) + 6) * 4;
        actual.Pixels[at] = 0;
        actual.Pixels[at + 1] = 0;
        actual.Pixels[at + 2] = 0; // one black pixel at (6, 4)

        var picture = DiffImage.Compose(reference, actual);
        Assert.Equal(10, picture.Height);
        Assert.Equal((3 * 20) + (2 * 6), picture.Width);
        var panelStart = 2 * (20 + 6);

        byte[] Pixel(int x, int y) => [picture.Pixels[((y * picture.Width) + x) * 4], picture.Pixels[(((y * picture.Width) + x) * 4) + 1], picture.Pixels[(((y * picture.Width) + x) * 4) + 2]];

        Assert.Equal(new byte[] { 255, 255, 255 }, Pixel(6, 4));                // reference panel: white there
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(20 + 6 + 6, 4));            // engine panel: the black pixel
        Assert.Equal(new byte[] { 0, 0, 255 }, Pixel(panelStart + 6, 4));      // difference panel: full red
        Assert.Equal(new byte[] { 255, 255, 255 }, Pixel(panelStart + 5, 4));  // and white next to it
        Assert.Equal(new byte[] { 128, 128, 128 }, Pixel(20 + 2, 4));          // the grey gutter
    }

    [Fact]
    public void DiffPanelsOfDifferentSizesAreMarkedRedWhereOnlyOneHasPixels()
    {
        var picture = DiffImage.Compose(BgraImage.Create(4, 4, 255, 255, 255), BgraImage.Create(6, 4, 255, 255, 255));
        Assert.Equal(3 * 6 + 12, picture.Width);
        var panelStart = 2 * (6 + 6);
        var at = ((1 * picture.Width) + panelStart + 5) * 4; // x = 5 exists only in the wider image
        Assert.Equal(new byte[] { 0, 0, 255 }, picture.Pixels.AsSpan(at, 3).ToArray());
    }

    [Fact]
    public void ATileIsPlacedWhereItBelongsAndAnOverhangingOneIsRefused()
    {
        var page = BgraImage.Create(10, 10, 255, 255, 255);
        var tile = new byte[4 * 4 * 4];
        Array.Fill(tile, (byte)7);
        page.Blit(tile, 16, 4, 4, 6, 6);
        Assert.Equal(7, page.Pixels[((6 * 10) + 6) * 4]);
        Assert.Equal(7, page.Pixels[((9 * 10) + 9) * 4]);
        Assert.Equal(255, page.Pixels[((5 * 10) + 6) * 4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Blit(tile, 16, 4, 4, 7, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Blit(tile.AsSpan(0, 20), 16, 4, 4, 0, 0));
    }

    private static int PaethReference(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] BuildPng(int width, int height, byte colourType, byte[] raw, byte bitDepth = 8)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = bitDepth;
        header[9] = colourType;
        Chunk(output, "IHDR"u8, header);
        Chunk(output, "IDAT"u8, compressed.ToArray());
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length);
        output.Write(number);
        output.Write(type);
        output.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Png.Crc(type, body));
        output.Write(number);
    }
}
