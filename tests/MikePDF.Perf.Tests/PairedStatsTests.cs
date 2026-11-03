using MikePDF.Perf.Measurement;

namespace MikePDF.Perf.Tests;

public sealed class PairedStatsTests
{
    [Theory]
    [InlineData(6, 1, 0.96875)] // 1 - 2 / 64
    [InlineData(7, 1, 0.984375)] // 1 - 2 / 128
    [InlineData(5, 1, 0.9375)] // 1 - 2 / 32: five rounds cannot reach 95%
    [InlineData(8, 2, 0.9296875)] // 1 - 2 * 9 / 256
    [InlineData(9, 2, 0.9609375)] // 1 - 2 * 10 / 512
    [InlineData(12, 3, 0.9614258)] // 1 - 2 * 79 / 4096
    [InlineData(1, 1, 0)] // one ratio says nothing about its own median
    public void TheSignTestIntervalHasTheCoverageTheBinomialGives(int n, int k, double expected) =>
        Assert.Equal(expected, PairedStats.Coverage(n, k), 6);

    [Fact]
    public void AnImpossibleRankHasNoCoverage() => Assert.Equal(0, PairedStats.Coverage(4, 3));

    [Fact]
    public void ThePickedIntervalIsTheNarrowestOneThatReachesTheConfidence()
    {
        // 9 ratios: [2nd, 8th] has 96.1%; [3rd, 7th] would have 82%.
        var interval = PairedStats.MedianInterval([1.05, 1.01, 0.99, 1.02, 1.00, 1.03, 0.98, 1.04, 1.50]);
        Assert.Equal(1.02, interval.Estimate);
        Assert.Equal(0.99, interval.Lower);
        Assert.Equal(1.05, interval.Upper);
        Assert.Equal(0.9609, interval.Confidence, 3);
        Assert.Equal(9, interval.Count);
    }

    [Fact]
    public void WithSixRatiosTheIntervalIsTheWholeRange()
    {
        var interval = PairedStats.MedianInterval([1.0, 1.1, 1.2, 1.3, 1.4, 1.5]);
        Assert.Equal(1.0, interval.Lower);
        Assert.Equal(1.5, interval.Upper);
        Assert.Equal(1.25, interval.Estimate);
        Assert.True(interval.Confidence >= 0.95);
    }

    [Fact]
    public void WithTooFewRatiosTheIntervalIsTheRangeAndSaysItIsNotAsSureAsAsked()
    {
        var interval = PairedStats.MedianInterval([1.0, 1.1, 1.2], 0.95);
        Assert.Equal(1.0, interval.Lower);
        Assert.Equal(1.2, interval.Upper);
        Assert.Equal(0.75, interval.Confidence, 6);
    }

    [Fact]
    public void OneWildRatioMovesNeitherTheEstimateNorTheInnerEnds()
    {
        var calm = PairedStats.MedianInterval([1.00, 1.01, 0.99, 1.02, 0.98, 1.00, 1.01, 0.99, 1.00, 1.00]);
        var wild = PairedStats.MedianInterval([1.00, 1.01, 0.99, 1.02, 0.98, 1.00, 1.01, 0.99, 1.00, 9.00]);
        Assert.Equal(calm.Estimate, wild.Estimate, 9);
        Assert.Equal(calm.Lower, wild.Lower, 9);
    }

    [Fact]
    public void NoRatiosIsAnErrorAndSoIsAConfidenceOutsideTheOpenUnitInterval()
    {
        Assert.Throws<ArgumentException>(() => PairedStats.MedianInterval([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairedStats.MedianInterval([1.0], 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairedStats.MedianInterval([1.0], 0));
    }

    [Fact]
    public void TheIntervalReallyCoversTheMedianAtItsStatedRateInASimulation()
    {
        // Skewed noise (log-normal) around a true median of 1.0: 9 rounds, many repetitions. The 2nd to 8th ratio must contain 1.0 about 96% of the time.
        var random = new Random(12345);
        var covered = 0;
        const int trials = 4000;
        for (var t = 0; t < trials; t++)
        {
            var ratios = Enumerable.Range(0, 9).Select(_ => Math.Exp(Gaussian(random) * 0.2)).ToArray();
            var interval = PairedStats.MedianInterval(ratios, 0.95);
            if (interval.Lower <= 1.0 && 1.0 <= interval.Upper)
            {
                covered++;
            }
        }

        Assert.InRange(covered / (double)trials, 0.95, 0.975);
    }

    private static double Gaussian(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
