using MikePDF.Perf.Measurement;

namespace MikePDF.Perf.Tests;

public sealed class StatsTests
{
    [Fact]
    public void TheMedianOfAnOddCountIsTheMiddleValueAndOfAnEvenCountTheMeanOfTheTwoMiddle()
    {
        Assert.Equal(3, Stats.Median([5, 1, 3]));
        Assert.Equal(2.5, Stats.Median([4, 1, 3, 2]));
        Assert.Equal(7, Stats.Median([7]));
    }

    [Fact]
    public void TheNinetyFifthPercentileIsANearestRankMeasuredValue()
    {
        var samples = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        Assert.Equal(19, Stats.Percentile(samples, 0.95)); // ceil(0.95 * 20) = 19th smallest
        Assert.Equal(20, Stats.Percentile(samples, 1.0));
        Assert.Equal(1, Stats.Percentile(samples, 0.0));
        Assert.Equal(5, Stats.Percentile([5.0], 0.95)); // one sample: that sample
    }

    [Fact]
    public void SummarizeReportsAllTheFigures()
    {
        var summary = Stats.Summarize([10, 12, 11, 100, 9]);
        Assert.Equal(5, summary.Count);
        Assert.Equal(9, summary.Min);
        Assert.Equal(11, summary.Median); // robust to the 100
        Assert.Equal(100, summary.Max);
        Assert.Equal(100, summary.P95);
        Assert.Equal(28.4, summary.Mean, 6);
        Assert.Equal(Math.Sqrt(((18.4 * 18.4) + (16.4 * 16.4) + (17.4 * 17.4) + (71.6 * 71.6) + (19.4 * 19.4)) / 4), summary.StdDev, 6); // sample standard deviation
        Assert.Equal(summary.StdDev / summary.Mean, summary.CoefficientOfVariation, 9);
    }

    [Fact]
    public void AnEmptyListOfSamplesIsAnErrorNotAZero()
    {
        Assert.Throws<ArgumentException>(() => Stats.Summarize([]));
        Assert.Throws<ArgumentException>(() => Stats.Median([]));
        Assert.Throws<ArgumentException>(() => Stats.Percentile([], 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Percentile([1.0], 1.5));
    }
}
