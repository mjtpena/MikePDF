namespace MikePDF.Render.Imaging;

/// <summary>A tightly packed BGRA image (stride is always <c>Width * 4</c>), as PDFium and the engine produce it. Alpha is opaque for a rendered page.</summary>
internal sealed class BgraImage
{
    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != (long)width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public static BgraImage Create(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }

        return new BgraImage(width, height, pixels);
    }

    /// <summary>Copies a tile of <paramref name="tileWidth"/> x <paramref name="tileHeight"/> pixels (rows <paramref name="tileStride"/> bytes apart) to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Blit(ReadOnlySpan<byte> tile, int tileStride, int tileWidth, int tileHeight, int x, int y)
    {
        if (x < 0 || y < 0 || tileWidth < 1 || tileHeight < 1 || x + tileWidth > Width || y + tileHeight > Height || tileStride < tileWidth * 4 || tile.Length < ((tileHeight - 1) * tileStride) + (tileWidth * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(tile), "The tile does not fit the image.");
        }

        for (var row = 0; row < tileHeight; row++)
        {
            tile.Slice(row * tileStride, tileWidth * 4).CopyTo(Pixels.AsSpan(((y + row) * Stride) + (x * 4)));
        }
    }

    /// <summary>The 8-bit luminance (Rec. 601) of every pixel, the plane SSIM is computed on.</summary>
    public byte[] Luma()
    {
        var luma = new byte[Width * Height];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
        {
            luma[i] = (byte)(((299 * Pixels[p + 2]) + (587 * Pixels[p + 1]) + (114 * Pixels[p]) + 500) / 1000);
        }

        return luma;
    }
}
