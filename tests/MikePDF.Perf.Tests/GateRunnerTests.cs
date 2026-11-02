using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

public sealed class GateRunnerTests
{
    private static readonly MetricDefinition Slow = MetricCatalog.SearchFull; // floor 25 ms
    private static readonly MetricDefinition Fast = MetricCatalog.LargeOpen; // floor 8 ms

    /// <summary>A measure function that returns, for each call, the next scripted value of each metric (the last value repeats), and records what it was asked for.</summary>
    private sealed class Script(params (MetricDefinition Metric, double[] Values)[] metrics)
    {
        private readonly Dictionary<string, int> _calls = [];

        public List<IReadOnlySet<string>?> Requests { get; } = [];

        public Task<PerfResults> Measure(IReadOnlySet<string>? only)
        {
            Requests.Add(only);
            var results = new List<MetricResult>();
            foreach (var (metric, values) in metrics)
            {
                if (only is not null && !only.Contains(metric.Id))
                {
                    continue;
                }

                var call = _calls.GetValueOrDefault(metric.Id);
                _calls[metric.Id] = call + 1;
                results.Add(Make.Metric(metric, values[Math.Min(call, values.Length - 1)]));
            }

            return Task.FromResult(Make.Results("ci-x64", [.. results]));
        }
    }

    private static PerfResults Baseline() => Make.Results("ci-x64", Make.Metric(Slow, 1000), Make.Metric(Fast, 100));

    [Fact]
    public async Task ACleanFirstRunPassesWithoutAnyRetry()
    {
        var script = new Script((Slow, [1020]), (Fast, [101]));
        var outcome = await GateRunner.RunAsync(Baseline(), script.Measure);
        Assert.False(outcome.Report.Failed);
        Assert.Equal(1, outcome.Runs);
        Assert.Single(script.Requests);
        Assert.Empty(outcome.Retried);
    }

    [Fact]
    public async Task OneNoisyRunIsVotedOutByTheRetries()
    {
        var script = new Script((Slow, [1300, 1010, 1005]), (Fast, [101]));
        var outcome = await GateRunner.RunAsync(Baseline(), script.Measure);
        Assert.False(outcome.Report.Failed);
        Assert.Equal(3, outcome.Runs);
        Assert.Equal([Slow.Id], outcome.Retried);
        Assert.Equal(1010, outcome.Results.Find(Slow.Id)!.Median); // the median of 1300, 1010, 1005
        Assert.Equal(101, outcome.Results.Find(Fast.Id)!.Median); // untouched
        // Only the suspect was measured again.
        Assert.Null(script.Requests[0]);
        Assert.Equal(new HashSet<string> { Slow.Id }, script.Requests[1]);
        Assert.Equal(new HashSet<string> { Slow.Id }, script.Requests[2]);
    }

    [Fact]
    public async Task ARegressionThatReproducesFailsTheGate()
    {
        var script = new Script((Slow, [1300, 1280, 1310]), (Fast, [101]));
        var outcome = await GateRunner.RunAsync(Baseline(), script.Measure);
        Assert.True(outcome.Report.Failed);
        Assert.Equal(3, outcome.Runs);
        Assert.Equal(1300, outcome.Results.Find(Slow.Id)!.Median);
        Assert.Equal(Slow.Id, Assert.Single(outcome.Report.Regressions).Id);
    }

    [Fact]
    public async Task ARegressionInTwoOfThreeRunsStillFailsBecauseTheMedianOfTheRunsIsOverTheLine()
    {
        var script = new Script((Slow, [1300, 1005, 1280]), (Fast, [101]));
        var outcome = await GateRunner.RunAsync(Baseline(), script.Measure);
        Assert.True(outcome.Report.Failed);
    }

    [Fact]
    public async Task WithNoBaselineTheFirstRunIsTheOnlyRun()
    {
        var script = new Script((Slow, [5000]), (Fast, [900]));
        var outcome = await GateRunner.RunAsync(null, script.Measure);
        Assert.False(outcome.Report.Failed);
        Assert.False(outcome.Report.HasBaseline);
        Assert.Equal(1, outcome.Runs);
    }

    [Fact]
    public async Task ZeroRetriesJudgesTheFirstRunAlone()
    {
        var script = new Script((Slow, [1300, 1000]), (Fast, [101]));
        var outcome = await GateRunner.RunAsync(Baseline(), script.Measure, retries: 0);
        Assert.True(outcome.Report.Failed);
        Assert.Equal(1, outcome.Runs);
    }

    [Fact]
    public async Task AMetricThatFailedToMeasureIsNotRetriedAndFailsTheGate()
    {
        PerfResults failing = Make.Results("ci-x64", MetricResults.Failed(Slow, "boom"));
        var outcome = await GateRunner.RunAsync(Baseline(), _ => Task.FromResult(failing));
        Assert.True(outcome.Report.Failed);
        Assert.Equal(1, outcome.Runs);
    }

    [Fact]
    public void MergingKeepsThePooledSamplesAndRecordsEachRunsMedian()
    {
        var first = MetricResults.Measured(Slow, [10, 11, 12]);
        var merged = GateRunner.Merge(first, [first, MetricResults.Measured(Slow, [20, 21, 22]), MetricResults.Measured(Slow, [11, 11, 11])]);
        Assert.Equal(11, merged.Median);
        Assert.Equal(9, merged.N);
        Assert.Equal(9, merged.Samples.Count);
        Assert.Equal(3, merged.Details!["runs"]);
        Assert.Equal(21, merged.Details["run2_median"]);
    }
}
