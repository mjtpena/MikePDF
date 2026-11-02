using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;

namespace MikePDF.Perf.Tests;

public sealed class AbComparerTests
{
    private static readonly MetricDefinition Search = MetricCatalog.SearchFull; // lower is better, floor 25 ms
    private static readonly MetricDefinition Throughput = MetricCatalog.TileThroughput; // higher is better, floor 40 tiles/s
    private static readonly MetricDefinition Tile = MetricCatalog.TileLatency; // floor 0.25 ms

    private static AbRound Round(MetricDefinition metric, double baseValue, double headValue) =>
        new(Make.Results("ci-x64", Make.Metric(metric, baseValue)), Make.Results("ci-x64", Make.Metric(metric, headValue)));

    private static AbMetric One(IReadOnlyList<AbRound> rounds, AbSettings? settings = null, Func<string, MetricDefinition?>? definitions = null) =>
        Assert.Single(AbComparer.Compare(rounds, settings, definitions).Items);

    /// <summary>Rounds in which the machine's speed moves a lot from one to the next (<paramref name="speed"/>) and the head costs <paramref name="headCost"/> times the base on top.</summary>
    private static List<AbRound> Rounds(MetricDefinition metric, double value, IEnumerable<double> speed, IEnumerable<double> headCost) =>
        [.. speed.Zip(headCost, (s, c) => Round(metric, value * s, value * s * c))];

    [Fact]
    public void ARunnerThatIsThirtyPercentSlowerInSomeRoundsIsNotARegressionWhenBothSidesSlowDownTogether()
    {
        // The failure this gate replaces: the whole machine is 20-30% slower for a while. Here it is, in rounds 2, 3 and 6, for base and head alike.
        var rounds = Rounds(Search, 1000, [1.0, 1.3, 1.25, 1.0, 1.0, 1.3, 0.95, 1.0], [1.01, 0.99, 1.0, 1.02, 0.98, 1.0, 1.01, 1.0]);
        var item = One(rounds);
        Assert.Equal(Verdict.Ok, item.Verdict);
        Assert.InRange(item.Ratio!.Value, 0.98, 1.03);
    }

    [Fact]
    public void ATwentyPercentSlowdownInEveryRoundFailsEvenOnAnUnsteadyRunner()
    {
        var rounds = Rounds(Search, 1000, [1.0, 1.3, 1.25, 1.0, 1.0, 1.3, 0.95, 1.0], [1.20, 1.21, 1.19, 1.22, 1.18, 1.20, 1.21, 1.20]);
        var item = One(rounds);
        Assert.Equal(Verdict.Regressed, item.Verdict);
        Assert.InRange(item.Ratio!.Value, 1.19, 1.21);
        Assert.True(item.ChangeLower > 0.17);
        Assert.Equal(8, item.Pairs);
        Assert.Equal(8, item.Ratios.Count);
    }

    [Fact]
    public void AFifteenPercentSlowdownFailsAndTwelvePercentToo()
    {
        Assert.Equal(Verdict.Regressed, One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), [1.15, 1.14, 1.16, 1.15, 1.17, 1.13, 1.15, 1.15])).Verdict);
        Assert.Equal(Verdict.Regressed, One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), [1.12, 1.11, 1.13, 1.12, 1.13, 1.11, 1.12, 1.12])).Verdict);
    }

    [Fact]
    public void NineAndAHalfPercentIsUnderTheLine()
    {
        var item = One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.095, 8)));
        Assert.Equal(Verdict.Ok, item.Verdict);
    }

    [Fact]
    public void OneWildRoundDoesNotFailTheGate()
    {
        var item = One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), [1.01, 1.60, 0.99, 1.00, 1.02, 1.00, 0.98, 1.01]));
        Assert.Equal(Verdict.Ok, item.Verdict);
    }

    [Fact]
    public void ASlowdownThatHalfTheRoundsDoNotShowIsReportedButDoesNotFail()
    {
        // Median change +12%, but 3 of 8 rounds show nothing: the lower end of the interval is at or under the margin.
        var item = One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), [1.13, 1.14, 1.13, 1.12, 1.12, 1.00, 1.00, 1.01]));
        Assert.Equal(Verdict.Unconfirmed, item.Verdict);
        Assert.Contains("not enough evidence", item.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnythingUnderTheMetricsFloorIsNoise()
    {
        // 0.20 ms to 0.23 ms in every round is 15% worse but 0.03 ms, under the 0.04 ms floor the A/B gate uses for a tile.
        var item = One(Rounds(Tile, 0.20, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.15, 8)));
        Assert.Equal(Verdict.Ok, item.Verdict);
        Assert.Contains("floor", item.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheABGateUsesItsOwnFloorNotTheOneForStoredBaselines()
    {
        // 1.0 ms to 1.2 ms is 0.2 ms: over the A/B floor (0.04 ms), under the 0.25 ms floor a stored baseline from another runner needs.
        Assert.Equal(0.04, Tile.FloorForAb);
        Assert.Equal(0.25, Tile.Floor);
        Assert.Equal(Verdict.Regressed, One(Rounds(Tile, 1.0, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.2, 8))).Verdict);
        Assert.Equal(5, new MetricDefinition("x", "ms", "lower", "x", null, 5).FloorForAb); // no A/B floor given: the other one
    }

    [Fact]
    public void EveryTimeAndMemoryMetricHasAnABFloorNoLargerThanTheOneForStoredBaselines()
    {
        foreach (var metric in MetricCatalog.All.Where(m => !m.Id.StartsWith("package.", StringComparison.Ordinal)))
        {
            Assert.True(metric.AbFloor is > 0, metric.Id + " has no A/B floor");
            Assert.True(metric.AbFloor <= metric.Floor, metric.Id + ": the A/B floor must not exceed the stored-baseline floor");
        }
    }

    [Fact]
    public void ForAThroughputFewerTilesPerSecondIsWorseAndTheRatioIsBelowOne()
    {
        var item = One(Rounds(Throughput, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(0.82, 8)));
        Assert.Equal(Verdict.Regressed, item.Verdict);
        Assert.InRange(item.Ratio!.Value, 0.81, 0.83);
        Assert.InRange(item.Change!.Value, 0.17, 0.19); // positive means worse whichever way the metric points
        Assert.True(item.RatioLower <= item.Ratio && item.Ratio <= item.RatioUpper);
        Assert.Equal(Verdict.Improved, One(Rounds(Throughput, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.3, 8))).Verdict);
    }

    [Fact]
    public void AClearlyBetterHeadIsImproved()
    {
        var item = One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(0.8, 8)));
        Assert.Equal(Verdict.Improved, item.Verdict);
    }

    [Fact]
    public void FewerPairedRoundsThanTheIntervalNeedsCanNeverFail()
    {
        var item = One(Rounds(Search, 1000, Enumerable.Repeat(1.0, 4), Enumerable.Repeat(1.5, 4)));
        Assert.Equal(Verdict.Unconfirmed, item.Verdict);
        Assert.Contains("paired round", item.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AMetricTheHeadCouldNotMeasureFailsTheGateAndOneTheBaseLacksPassesLoudly()
    {
        var failed = new AbRound(Make.Results("ci-x64", Make.Metric(Search, 1000)), Make.Results("ci-x64", MetricResults.Failed(Search, "the engine would not start")));
        var item = One([failed]);
        Assert.Equal(Verdict.Failed, item.Verdict);
        Assert.Equal("the engine would not start", item.Note);

        var newMetric = new AbRound(Make.Results("ci-x64"), Make.Results("ci-x64", Make.Metric(Search, 1000)));
        Assert.Equal(Verdict.NoBaseline, One([newMetric]).Verdict);
    }

    [Fact]
    public void ASkippedMetricIsSkippedAndAMetricOnlyTheBaseHasIsLeftOut()
    {
        var skipped = new AbRound(Make.Results("ci-x64"), Make.Results("ci-x64", MetricResults.Skipped(MetricCatalog.InstallSize, "no package")));
        Assert.Equal(Verdict.Skipped, One([skipped]).Verdict);

        var gone = AbComparer.Compare([new AbRound(Make.Results("ci-x64", Make.Metric(Search, 1000)), Make.Results("ci-x64"))]);
        Assert.Empty(gone.Items);
    }

    [Fact]
    public void AnAdvisoryMetricIsReportedAsAdvisoryAndNeverFails()
    {
        var advisory = Search with { Gate = false };
        var report = AbComparer.Compare(Rounds(advisory, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.3, 8)), definitions: _ => advisory);
        Assert.Equal(Verdict.Advisory, Assert.Single(report.Items).Verdict);
        Assert.False(report.Failed);
    }

    [Fact]
    public void ReportFailedMeansARegressionOrAFailure()
    {
        var bad = AbComparer.Compare(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.3, 8)));
        Assert.True(bad.Failed);
        Assert.Equal(Search.Id, Assert.Single(bad.Regressions).Id);
        Assert.False(AbComparer.Compare(Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.01, 8))).Failed);
        Assert.Empty(AbComparer.Compare([]).Items);
    }

    [Fact]
    public void TheThresholdAndTheMarginAreTheCallersToChoose()
    {
        var rounds = Rounds(Search, 1000, Enumerable.Repeat(1.0, 8), Enumerable.Repeat(1.06, 8));
        Assert.Equal(Verdict.Ok, One(rounds).Verdict);
        Assert.Equal(Verdict.Regressed, One(rounds, new AbSettings { Threshold = 0.05, Margin = 0.02 }).Verdict);
        Assert.Equal(Verdict.Unconfirmed, One(rounds, new AbSettings { Threshold = 0.05, Margin = 0.07 }).Verdict);
    }
}
