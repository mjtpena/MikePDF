using MikePDF.Perf.Cli;
using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

public sealed class AbSupportTests
{
    [Fact]
    public void TheArgumentsOfARunAreTheSidesOwnAndOnlyTheSuspectsWhenAsked()
    {
        var side = new ProcessSide("head", @"C:\bundle\harness\MikePDF.Perf.exe", @"C:\bundle\engine", @"C:\work\head", "ci-x64", "ci", "MikePDF.Perf", 0.2);
        var all = side.Arguments(@"C:\out\r1", null).ToList();
        Assert.Equal(["run", "--profile", "ci", "--out", @"C:\out\r1", "--runner-id", "ci-x64", "--engine-dir", @"C:\bundle\engine", "--docs-dir", @"C:\work\head\documents", "--appcontainer", "MikePDF.Perf.head"], all);

        var some = side.Arguments(@"C:\out\r2", new HashSet<string> { "b.metric", "a.metric" }).ToList();
        Assert.Equal(["--only", "a.metric,b.metric"], some.TakeLast(2));
    }

    [Fact]
    public void TheReportShowsBaseHeadRatioIntervalAndVerdictForEveryMetricAndSaysWhenItIsPlanted()
    {
        var rounds = Enumerable.Range(0, 8).Select(_ => new AbRound(
            Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1000), Make.Metric(MetricCatalog.TileThroughput, 1000)),
            Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1250), Make.Metric(MetricCatalog.TileThroughput, 1001)))).ToList();
        var report = AbComparer.Compare(rounds);
        var head = rounds[0].Head;
        var markdown = AbMarkdown.Render(report, new AbMeta("main abc1234", "merge def5678", "ci-x64", "ci", 8, 8, [], 0.25, null, null), head, null);

        Assert.Contains("PLANTED SLOWDOWN of 25%", markdown, StringComparison.Ordinal);
        Assert.Contains("| Metric | Unit | Base | Head | Ratio head/base | Interval | Rounds | Verdict |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `search.full.text500` | ms | 1000 | 1250 | 1.250 | 1.250 to 1.250 (99%) | 8 | **REGRESSED** |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `render.tile_throughput.text20` | tiles/s | 1000 | 1001 | 1.001 |", markdown, StringComparison.Ordinal);
        Assert.Contains("FAILED, 1 metric(s)", markdown, StringComparison.Ordinal);
        Assert.Contains("not gating on this runner", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonOfAReportRoundTripsTheVerdictsAsNames()
    {
        var rounds = Enumerable.Range(0, 8).Select(_ => new AbRound(
            Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1000)), Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 1250)))).ToList();
        var json = ResultsJson.Serialize(AbComparer.Compare(rounds));
        Assert.Contains("\"verdict\": \"regressed\"", json, StringComparison.Ordinal);
        var back = ResultsJson.Deserialize<AbReport>(json);
        Assert.Equal(Verdict.Regressed, Assert.Single(back.Items).Verdict);
        Assert.Equal(8, back.Items[0].Ratios.Count);
    }

    [Fact]
    public async Task CalibrateCountsTheVerdictsOfManyRunsPerRunnerAndMetric()
    {
        AbFile Run(string runner, double headCost, double planted)
        {
            var rounds = Enumerable.Range(0, 8).Select(_ => new AbRound(
                Make.Results(runner, Make.Metric(MetricCatalog.SearchFull, 1000)), Make.Results(runner, Make.Metric(MetricCatalog.SearchFull, 1000 * headCost)))).ToList();
            return new AbFile(new AbMeta("base", "head", runner, "ci", 8, 8, [], planted, null, null), AbComparer.Compare(rounds));
        }

        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-cal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(directory, "x64", "1"));
        Directory.CreateDirectory(Path.Combine(directory, "x64", "2"));
        Directory.CreateDirectory(Path.Combine(directory, "arm64", "3"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "x64", "1", "perf-ab.json"), ResultsJson.Serialize(Run("ci-x64", 1.01, 0)));
            File.WriteAllText(Path.Combine(directory, "x64", "2", "perf-ab.json"), ResultsJson.Serialize(Run("ci-x64", 1.20, 0.2)));
            File.WriteAllText(Path.Combine(directory, "arm64", "3", "perf-ab.json"), ResultsJson.Serialize(Run("ci-arm64", 1.00, 0)));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await Commands.RunAsync(["calibrate", "--dir", directory], output, error);
            var text = output.ToString();
            Assert.Equal(0, exit);
            Assert.Contains("## `ci-arm64`: 1 run(s), no slowdown planted", text, StringComparison.Ordinal);
            Assert.Contains("## `ci-x64`: 2 run(s), planted slowdown 0%, 20%", text, StringComparison.Ordinal);
            Assert.Contains("Runs with at least one regressed metric: **1 of 2**", text, StringComparison.Ordinal);
            Assert.Contains("| `search.full.text500` | 2 | 1% | 10.5% | 20% | 20% | 1 | 0 | 1 |", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingExecutableIsAUsageError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await Commands.RunAsync(["ab", "--base-exe", "nope.exe", "--base-engine", ".", "--head-exe", "nope.exe", "--head-engine", "."], output, error);
        Assert.Equal(2, exit);
        Assert.Contains("nope.exe does not exist", error.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The <c>ab</c> command end to end: real harness processes, the real sandboxed engine, a planted slowdown in the head and none in the base.</summary>
public sealed class AbCommandTests(HarnessFixture fixture)
{
    [Fact]
    public async Task AnAbRunMeasuresBothSidesInSeparateProcessesAndTheHeadAloneCarriesThePlantedSlowdown()
    {
        var harness = Path.Combine(AppContext.BaseDirectory, "MikePDF.Perf.exe");
        Assert.True(File.Exists(harness), harness + " is copied next to the tests by the project reference.");
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-ab-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await Commands.RunAsync(
            [
                "ab", "--base-exe", harness, "--base-engine", fixture.Engine.DeployDirectory, "--head-exe", harness, "--head-engine", fixture.Engine.DeployDirectory,
                "--profile", "quick", "--rounds", "2", "--confirm-rounds", "0", "--only", MetricCatalog.TileThroughput.Id,
                "--plant-head", "0.5", "--runner-id", "test-runner", "--out", directory, "--work-dir", Path.Combine(directory, "work"), "--appcontainer-prefix", HarnessFixture.CliProfileName,
            ], output, error);

            // Two rounds are too few for a 95% interval, so nothing can fail: the exit code is 0 and the metric is "unconfirmed".
            Assert.True(exit == 0, error + output.ToString());
            var report = ResultsJson.Deserialize<AbFile>(await File.ReadAllTextAsync(Path.Combine(directory, "perf-ab.json"))).Report;
            var item = Assert.Single(report.Items);
            Assert.Equal(2, item.Pairs);
            Assert.NotNull(item.Ratio); // its size is not asserted: two quick rounds on a cold VM differ by 2x in either direction; the size of a planted slowdown is Clock's unit test
            Assert.Equal(Verdict.Unconfirmed, item.Verdict);

            var head = ResultsJson.Load(Path.Combine(directory, "perf-results.json"));
            var baseResults = ResultsJson.Load(Path.Combine(directory, "perf-results-base.json"));
            Assert.Contains(head.Notes, n => n.Contains("PLANTED SLOWDOWN", StringComparison.Ordinal));
            Assert.DoesNotContain(baseResults.Notes, n => n.Contains("PLANTED", StringComparison.Ordinal));
            Assert.Equal("test-runner", head.Runner.Id);
            Assert.True(File.Exists(Path.Combine(directory, "perf-results.md")));
            Assert.True(File.Exists(Path.Combine(directory, "rounds", "head-02", "perf-results.json")));
            Assert.True(File.Exists(Path.Combine(directory, "rounds", "base-02", "run.log")));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Left for the OS temp cleanup.
            }
        }
    }
}
