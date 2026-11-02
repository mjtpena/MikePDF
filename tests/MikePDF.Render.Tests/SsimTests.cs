using MikePDF.Render.Imaging;

namespace MikePDF.Render.Tests;

public sealed class SsimTests
{
    private static BgraImage Random(int width, int height, int seed)
    {
        var random = new System.Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return new BgraImage(width, height, pixels);
    }

    private static BgraImage Gradient(int width, int height)
    {
        var image = BgraImage.Create(width, height, 0, 0, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                image.Pixels[at] = (byte)(x * 255 / Math.Max(1, width - 1));
                image.Pixels[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                image.Pixels[at + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }

    /// <summary>The definition, window by window, in double precision: the oracle the sliding-window version must agree with.</summary>
    private static double Naive(byte[] a, byte[] b, int width, int height)
    {
        var window = Math.Min(8, Math.Min(width, height));
        const double c1 = 6.5025, c2 = 58.5225;
        double total = 0;
        var count = 0;
        for (var top = 0; top + window <= height; top++)
        {
            for (var left = 0; left + window <= width; left++)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var y = top; y < top + window; y++)
                {
                    for (var x = left; x < left + window; x++)
                    {
                        double va = a[(y * width) + x], vb = b[(y * width) + x];
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                double n = window * window;
                double meanA = sa / n, meanB = sb / n;
                var varA = (saa / n) - (meanA * meanA);
                var varB = (sbb / n) - (meanB * meanB);
                var cov = (sab / n) - (meanA * meanB);
                total += ((2 * meanA * meanB) + c1) * ((2 * cov) + c2) / (((meanA * meanA) + (meanB * meanB) + c1) * (varA + varB + c2));
                count++;
            }
        }

        return total / count;
    }

    [Fact]
    public void IdenticalImagesScoreExactlyOne()
    {
        var image = Random(64, 48, 1);
        var result = Ssim.Compare(image, new BgraImage(64, 48, (byte[])image.Pixels.Clone()));
        Assert.Equal(1.0, result.Ssim);
        Assert.Equal(0.0, result.ChangedPercent);
        Assert.False(result.SizeMismatch);
    }

    [Theory]
    [InlineData(8, 8, 1)]
    [InlineData(9, 8, 2)]
    [InlineData(37, 29, 3)]
    [InlineData(100, 64, 4)]
    [InlineData(3, 40, 5)] // narrower than the window: the window shrinks to fit
    [InlineData(1, 1, 6)]
    public void TheSlidingWindowAgreesWithTheDefinition(int width, int height, int seed)
    {
        var a = Random(width, height, seed).Luma();
        var b = Random(width, height, seed + 100).Luma();
        Assert.Equal(Naive(a, b, width, height), Ssim.MeanSsim(a, b, width, height), 9);
        Assert.Equal(Ssim.MeanSsim(a, b, width, height), Ssim.MeanSsim(b, a, width, height), 12); // symmetric
    }

    [Fact]
    public void AClosePictureScoresNearOneAndAnUnrelatedOneScoresFarLower()
    {
        var reference = Gradient(120, 90);
        var noisy = new BgraImage(120, 90, (byte[])reference.Pixels.Clone());
        var random = new System.Random(7);
        for (var i = 0; i < noisy.Pixels.Length; i += 4)
        {
            noisy.Pixels[i] = (byte)Math.Clamp(noisy.Pixels[i] + random.Next(-2, 3), 0, 255);
        }

        var close = Ssim.Compare(reference, noisy).Ssim;
        var unrelated = Ssim.Compare(reference, Random(120, 90, 8)).Ssim;
        Assert.InRange(close, 0.98, 1.0);
        Assert.True(unrelated < 0.2, $"an unrelated image scored {unrelated}");
        Assert.True(close > unrelated + 0.5);
    }

    [Fact]
    public void ABlankPageAgainstAPageWithContentScoresLow()
    {
        // The failure the reference caught while this tool was written: PDFium handed back a white page.
        var blank = BgraImage.Create(200, 300, 255, 255, 255);
        var page = BgraImage.Create(200, 300, 255, 255, 255);
        for (var y = 100; y < 160; y++)
        {
            for (var x = 20; x < 180; x++)
            {
                var at = ((y * 200) + x) * 4;
                page.Pixels[at] = 0;
                page.Pixels[at + 1] = 0;
                page.Pixels[at + 2] = 0;
            }
        }

        var result = Ssim.Compare(blank, page);
        Assert.True(result.Ssim < 0.98, $"SSIM was {result.Ssim}");
        Assert.InRange(result.ChangedPercent, 15.0, 17.0); // 160 x 60 of 200 x 300 pixels = 16%
    }

    [Fact]
    public void ChangedPixelsIgnoreDifferencesWithinTheTolerance()
    {
        var a = BgraImage.Create(10, 10, 100, 100, 100);
        var within = BgraImage.Create(10, 10, 100 + Ssim.ChangedTolerance, 100, 100);
        var beyond = BgraImage.Create(10, 10, 100 + Ssim.ChangedTolerance + 1, 100, 100);
        Assert.Equal(0.0, Ssim.Compare(a, within).ChangedPercent);
        Assert.Equal(100.0, Ssim.Compare(a, beyond).ChangedPercent);
    }

    [Fact]
    public void DifferentSizesAreAMismatchWithScoreZero()
    {
        var result = Ssim.Compare(BgraImage.Create(10, 10, 0, 0, 0), BgraImage.Create(10, 11, 0, 0, 0));
        Assert.True(result.SizeMismatch);
        Assert.Equal(0.0, result.Ssim);
    }

    [Fact]
    public void ConstantPlanesOfDifferentBrightnessScoreByLuminanceOnly()
    {
        // No structure in either image: the score is the luminance term (2 a b + C1) / (a^2 + b^2 + C1), here for 100 and 200.
        var a = new byte[64];
        var b = new byte[64];
        Array.Fill(a, (byte)100);
        Array.Fill(b, (byte)200);
        var expected = ((2 * 100.0 * 200.0) + 6.5025) / ((100.0 * 100.0) + (200.0 * 200.0) + 6.5025);
        Assert.Equal(expected, Ssim.MeanSsim(a, b, 8, 8), 12);
    }

    [Fact]
    public void PlanesThatDoNotMatchTheirDimensionsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => Ssim.MeanSsim(new byte[10], new byte[10], 4, 4));
        Assert.Throws<ArgumentException>(() => Ssim.MeanSsim(new byte[16], new byte[15], 4, 4));
    }

    [Fact]
    public void ALargePageScoresInReasonableTime()
    {
        var a = Gradient(2550, 3300); // US Letter at 300 dpi
        var b = new BgraImage(2550, 3300, (byte[])a.Pixels.Clone());
        b.Pixels[1000] ^= 0x40;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = Ssim.Compare(a, b);
        clock.Stop();
        Assert.InRange(result.Ssim, 0.999, 1.0);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"SSIM of a 8.4 megapixel page took {clock.Elapsed}");
    }
}
