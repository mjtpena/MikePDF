namespace MikePDF.Render.Imaging;

/// <summary>The side-by-side picture §12.3 asks for on a failing page: reference, MikePDF, and where they differ.</summary>
internal static class DiffImage
{
    private const int Gutter = 6;

    /// <summary>
    /// Three panels, left to right: the reference render, the MikePDF render, and a difference map (white where the two agree, increasingly red where
    /// they differ; a pixel that differs by 64 levels or more is full red). Panels of different sizes are padded with grey and the difference map marks the
    /// padding red.
    /// </summary>
    public static BgraImage Compose(BgraImage reference, BgraImage actual)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);
        var panelWidth = Math.Max(reference.Width, actual.Width);
        var panelHeight = Math.Max(reference.Height, actual.Height);
        var width = (3 * panelWidth) + (2 * Gutter);
        var canvas = BgraImage.Create(width, panelHeight, 128, 128, 128);

        for (var y = 0; y < panelHeight; y++)
        {
            for (var x = 0; x < panelWidth; x++)
            {
                var inReference = x < reference.Width && y < reference.Height;
                var inActual = x < actual.Width && y < actual.Height;
                int rb = 0, rg = 0, rr = 0, ab = 0, ag = 0, ar = 0;
                if (inReference)
                {
                    var at = (y * reference.Stride) + (x * 4);
                    (rb, rg, rr) = (reference.Pixels[at], reference.Pixels[at + 1], reference.Pixels[at + 2]);
                    Set(canvas, x, y, (byte)rb, (byte)rg, (byte)rr);
                }

                if (inActual)
                {
                    var at = (y * actual.Stride) + (x * 4);
                    (ab, ag, ar) = (actual.Pixels[at], actual.Pixels[at + 1], actual.Pixels[at + 2]);
                    Set(canvas, panelWidth + Gutter + x, y, (byte)ab, (byte)ag, (byte)ar);
                }

                int delta = inReference && inActual ? Math.Max(Math.Abs(rb - ab), Math.Max(Math.Abs(rg - ag), Math.Abs(rr - ar))) : 255;
                var fade = (byte)(255 - Math.Min(255, delta * 4));
                Set(canvas, (2 * (panelWidth + Gutter)) + x, y, fade, fade, 255);
            }
        }

        return canvas;
    }

    private static void Set(BgraImage image, int x, int y, byte blue, byte green, byte red)
    {
        var at = (y * image.Stride) + (x * 4);
        image.Pixels[at] = blue;
        image.Pixels[at + 1] = green;
        image.Pixels[at + 2] = red;
    }
}
