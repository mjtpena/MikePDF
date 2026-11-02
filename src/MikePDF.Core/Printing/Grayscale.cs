namespace MikePDF.Core.Printing;

/// <summary>Grey scale printing in the broker: the engine only renders colour, and a driver may ignore a "monochrome" request.</summary>
public static class Grayscale
{
    /// <summary>
    /// Converts BGRA pixels to grey (BGRA with equal channels) with the Rec. 601 weights the rest of the app uses for luminance, in integer arithmetic.
    /// <paramref name="source"/> and <paramref name="destination"/> may be the same memory. Rows are <paramref name="stride"/> bytes apart; only the first
    /// <c>width * 4</c> bytes of each row are touched.
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        var needed = ((height - 1) * (long)stride) + (width * 4L);
        if (source.Length < needed || destination.Length < needed)
        {
            throw new ArgumentException("The buffers are smaller than the image.");
        }

        for (var row = 0; row < height; row++)
        {
            var from = source.Slice(row * stride, width * 4);
            var to = destination.Slice(row * stride, width * 4);
            for (var i = 0; i < from.Length; i += 4)
            {
                // 0.114 B + 0.587 G + 0.299 R, in 1/256ths (29, 150, 77 sum to 256); rounded.
                var luma = (byte)(((from[i] * 29) + (from[i + 1] * 150) + (from[i + 2] * 77) + 128) >> 8);
                to[i] = luma;
                to[i + 1] = luma;
                to[i + 2] = luma;
                to[i + 3] = 255;
            }
        }
    }
}
