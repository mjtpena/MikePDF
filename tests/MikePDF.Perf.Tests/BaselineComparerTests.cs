using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

public sealed class BaselineComparerTests
{
    private static MetricComparison Only(PerfResults? baseline, PerfResults current) => Assert.Single(BaselineComparer.Compare(baseline, current).Items);

    [Fact]
    public void ElevenPercentWorseOnALongMetricFailsAndNinePercentDoesNot()
    {
        var baseline = Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1000));
        Assert.Equal(Verdict.Regressed, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1110))).Verdict);
        Assert.Equal(Verdict.Ok, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1090))).Verdict);
        Assert.Equal(Verdict.Ok, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1100))).Verdict); // exactly the limit: not over it
    }

    [Fact]
    public void AChangeUnderTheMetricsFloorIsNoiseEvenWhenItIsALargeFraction()
    {
        // Tile latency: 1.0 ms to 1.2 ms is 20% worse but 0.2 ms, under its 0.25 ms floor.
        var baseline = Make.Results("ci-x64", Make.Metric(MetricCatalog.TileLatency, 1.0));
        var small = Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.TileLatency, 1.2)));
        Assert.Equal(Verdict.Ok, small.Verdict);
        Assert.Contains("floor", small.Note, StringComparison.Ordinal);
        Assert.Equal(Verdict.Regressed, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.TileLatency, 1.3))).Verdict);
    }

    [Fact]
    public void ForAMetricWhereMoreIsBetterTheDirectionIsReversed()
    {
        var baseline = Make.Results("ci-x64", Make.Metric(MetricCatalog.TileThroughput, 1000));
        Assert.Equal(Verdict.Regressed, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.TileThroughput, 850))).Verdict); // 15% fewer tiles per second
        Assert.Equal(Verdict.Improved, Only(baseline, Make.Results("ci-x64", Make.Metric(MetricCatalog.TileThroughput, 1300))).Verdict);
    }

    [Fact]
    public void ChangeIsSignedSoThatPositiveAlwaysMeansWorse()
    {
        var lower = Only(Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000)), Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1200)));
        var higher = Only(Make.Results("r", Make.Metric(MetricCatalog.TileThroughput, 1000)), Make.Results("r", Make.Metric(MetricCatalog.TileThroughput, 800)));
        Assert.Equal(0.2, lower.Change!.Value, 9);
        Assert.Equal(0.2, higher.Change!.Value, 9);
    }

    [Fact]
    public void ABetterResultIsReportedAsImprovedOnlyWhenItBeatsThresholdAndFloor()
    {
        var baseline = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000));
        Assert.Equal(Verdict.Improved, Only(baseline, Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 800))).Verdict);
        Assert.Equal(Verdict.Ok, Only(baseline, Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 950))).Verdict);
    }

    [Fact]
    public void WithNoBaselineEverythingPassesAndSaysSo()
    {
        var report = BaselineComparer.Compare(null, Make.Results("ci-arm64", Make.Metric(MetricCatalog.SearchFull, 1000)));
        Assert.False(report.HasBaseline);
        Assert.False(report.Failed);
        Assert.Equal(Verdict.NoBaseline, Assert.Single(report.Items).Verdict);
    }

    [Fact]
    public void AMetricNewSinceTheBaselineHasNothingToBeComparedWith()
    {
        var baseline = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000));
        var report = BaselineComparer.Compare(baseline, Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000), Make.Metric(MetricCatalog.TileLatency, 1)));
        Assert.Equal(Verdict.NoBaseline, report.Items.Single(i => i.Id == MetricCatalog.TileLatency.Id).Verdict);
        Assert.False(report.Failed);
    }

    [Fact]
    public void AMetricThatCouldNotBeMeasuredFailsTheGateAndASkippedOneDoesNot()
    {
        var baseline = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000), Make.Metric(MetricCatalog.InstallSize, 40));
        var current = Make.Results("r", MetricResults.Failed(MetricCatalog.SearchFull, "the engine would not start"), MetricResults.Skipped(MetricCatalog.InstallSize, "no package"));
        var report = BaselineComparer.Compare(baseline, current);
        Assert.Equal(Verdict.Failed, report.Items[0].Verdict);
        Assert.Equal(Verdict.Skipped, report.Items[1].Verdict);
        Assert.True(report.Failed);
        Assert.Equal([MetricCatalog.SearchFull.Id], report.Regressions.Select(r => r.Id));
    }

    [Fact]
    public void ANoisyMetricMarkedNotGatingIsAdvisoryAndNeverFailsTheGate()
    {
        var noisy = new MetricDefinition("test.noisy", "ms", "lower", "x", null, 0, Gate: false);
        var baseline = Make.Results("r", Make.Metric(noisy, 100));
        var report = BaselineComparer.Compare(baseline, Make.Results("r", Make.Metric(noisy, 200)), definitions: id => id == noisy.Id ? noisy : null);
        Assert.Equal(Verdict.Advisory, Assert.Single(report.Items).Verdict);
        Assert.False(report.Failed);

        // The same numbers on a gating metric fail. A metric id the catalog does not know gates, with no floor.
        Assert.True(BaselineComparer.Compare(Make.Results("r", Make.Metric("test.unknown", "ms", "lower", 100)), Make.Results("r", Make.Metric("test.unknown", "ms", "lower", 200))).Failed);
        Assert.True(MetricCatalog.All.All(m => m.Gate), "Every shipped metric gates today; the flag exists for the ones that prove too noisy on hosted runners.");
    }

    [Fact]
    public void AWarningIsRaisedWhenTheBaselineBelongsToAnotherArchitectureOrRunner()
    {
        var baseline = Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1000)) with { Machine = Make.Results("x").Machine with { Architecture = "arm64" } };
        var report = BaselineComparer.Compare(baseline, Make.Results("ci-arm64", Make.Metric(MetricCatalog.SearchFull, 1000)));
        Assert.Contains(report.Warnings, w => w.Contains("arm64", StringComparison.Ordinal) && w.Contains("not meaningful", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("ci-x64", StringComparison.Ordinal));
    }

    [Fact]
    public void ABaselineMedianOfZeroCannotBeComparedWith()
    {
        var baseline = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 0));
        Assert.Equal(Verdict.NoBaseline, Only(baseline, Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 5))).Verdict);
    }

    [Fact]
    public void AThresholdThatIsNotPositiveIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BaselineComparer.Compare(null, Make.Results("r"), 0));
    }
}
