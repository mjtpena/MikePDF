namespace MikePDF.Render.Imaging;

/// <summary>What comparing two renders of the same page found.</summary>
/// <param name="Ssim">Mean structural similarity of the luminance planes, 1.0 for identical images. 0 when the sizes differ.</param>
/// <param name="ChangedPercent">Share of pixels (0 to 100) where some colour channel differs by more than <see cref="Ssim.ChangedTolerance"/>.</param>
/// <param name="SizeMismatch">The two images are not the same size, so nothing else could be compared.</param>
internal readonly record struct ImageComparison(double Ssim, double ChangedPercent, bool SizeMismatch);

/// <summary>
/// Structural similarity (Wang, Bovik, Sheikh and Simoncelli, 2004) on 8-bit luminance, written for this repository so the visual-diff gate has no
/// dependency (ADR-0003). Uniform 8 x 8 windows at stride 1, K1 = 0.01, K2 = 0.03, L = 255, population variances; the score is the mean over every
/// window. The window sums are exact integers (a sliding window over column sums), so the result is deterministic across platforms and does not
/// depend on summation order.
/// </summary>
internal static class Ssim
{
    public const int Window = 8;

    /// <summary>A channel difference of up to this many levels does not count a pixel as changed (anti-aliasing noise).</summary>
    public const int ChangedTolerance = 8;

    private const double C1 = 6.5025;   // (0.01 * 255)^2
    private const double C2 = 58.5225;  // (0.03 * 255)^2

    public static ImageComparison Compare(BgraImage reference, BgraImage actual)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);
        if (reference.Width != actual.Width || reference.Height != actual.Height)
        {
            return new ImageComparison(0, 100, SizeMismatch: true);
        }

        var changed = CountChanged(reference.Pixels, actual.Pixels);
        var changedPercent = 100.0 * changed / ((long)reference.Width * reference.Height);
        if (reference.Pixels.AsSpan().SequenceEqual(actual.Pixels))
        {
            return new ImageComparison(1.0, 0, SizeMismatch: false);
        }

        return new ImageComparison(MeanSsim(reference.Luma(), actual.Luma(), reference.Width, reference.Height), changedPercent, SizeMismatch: false);
    }

    /// <summary>Mean SSIM of two equally sized 8-bit planes. A plane smaller than the window uses the largest window that fits.</summary>
    public static double MeanSsim(byte[] a, byte[] b, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != width * height || b.Length != a.Length || width < 1 || height < 1)
        {
            throw new ArgumentException("The planes do not match the dimensions.");
        }

        var window = Math.Min(Window, Math.Min(width, height));
        var windowArea = (long)window * window;
        var columnsA = new long[width];
        var columnsB = new long[width];
        var columnsAA = new long[width];
        var columnsBB = new long[width];
        var columnsAB = new long[width];

        void Add(int row, int sign)
        {
            var offset = row * width;
            for (var x = 0; x < width; x++)
            {
                long va = a[offset + x];
                long vb = b[offset + x];
                columnsA[x] += sign * va;
                columnsB[x] += sign * vb;
                columnsAA[x] += sign * va * va;
                columnsBB[x] += sign * vb * vb;
                columnsAB[x] += sign * va * vb;
            }
        }

        for (var row = 0; row < window; row++)
        {
            Add(row, 1);
        }

        double total = 0;
        long count = 0;
        for (var top = 0; top + window <= height; top++)
        {
            if (top > 0)
            {
                Add(top - 1, -1);
                Add(top + window - 1, 1);
            }

            long sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            for (var x = 0; x < window; x++)
            {
                sa += columnsA[x];
                sb += columnsB[x];
                saa += columnsAA[x];
                sbb += columnsBB[x];
                sab += columnsAB[x];
            }

            for (var left = 0; left + window <= width; left++)
            {
                if (left > 0)
                {
                    var leaving = left - 1;
                    var entering = left + window - 1;
                    sa += columnsA[entering] - columnsA[leaving];
                    sb += columnsB[entering] - columnsB[leaving];
                    saa += columnsAA[entering] - columnsAA[leaving];
                    sbb += columnsBB[entering] - columnsBB[leaving];
                    sab += columnsAB[entering] - columnsAB[leaving];
                }

                total += WindowScore(sa, sb, saa, sbb, sab, windowArea);
                count++;
            }
        }

        return total / count;
    }

    private static double WindowScore(long sa, long sb, long saa, long sbb, long sab, long n)
    {
        double meanA = (double)sa / n;
        double meanB = (double)sb / n;
        // Exact integer numerators: n * sum(x^2) - (sum x)^2 is n^2 times the population variance.
        var nn = (double)n * n;
        var varA = ((n * saa) - (sa * sa)) / nn;
        var varB = ((n * sbb) - (sb * sb)) / nn;
        var covariance = ((n * sab) - (sa * sb)) / nn;
        var numerator = ((2 * meanA * meanB) + C1) * ((2 * covariance) + C2);
        var denominator = ((meanA * meanA) + (meanB * meanB) + C1) * (varA + varB + C2);
        return numerator / denominator;
    }

    private static long CountChanged(byte[] a, byte[] b)
    {
        long changed = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var delta = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            if (delta > ChangedTolerance)
            {
                changed++;
            }
        }

        return changed;
    }
}
