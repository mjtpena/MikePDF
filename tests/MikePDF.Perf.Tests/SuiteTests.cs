using MikePDF.Perf.Cli;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;
using MikePDF.Perf.Scenarios;

namespace MikePDF.Perf.Tests;

/// <summary>The command line end to end against the real engine: the same commands the CI job runs, with the quick profile.</summary>
public sealed class SuiteTests(HarnessFixture fixture)
{
    private sealed record Outcome(string Directory, int ExitCode, string Output, PerfResults Results);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Outcome? _quick;

    private static async Task<(int ExitCode, string Output, string Error)> Invoke(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await Commands.RunAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string[] Common(string directory) => ["--profile", "quick", "--out", directory, "--docs-dir", fixture.DocumentsDirectory, "--appcontainer", "MikePDF.PerfTests.Cli", "--runner-id", "test-runner"];

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "mikepdf-perf-suite-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The whole quick suite, run once and shared by the tests that need its results.</summary>
    private async Task<Outcome> QuickRunAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_quick is null)
            {
                var directory = NewDirectory();
                var (exit, output, error) = await Invoke(["run", .. Common(directory)]);
                Assert.True(exit is 0, error + output);
                _quick = new Outcome(directory, exit, output, ResultsJson.Load(Path.Combine(directory, "perf-results.json")));
            }

            return _quick;
        }
        finally
        {
            Gate.Release();
        }
    }

    [Fact]
    public async Task TheRunCommandMeasuresEveryHeadlessMetricAndWritesJsonAndMarkdown()
    {
        var run = await QuickRunAsync();
        Assert.True(File.Exists(Path.Combine(run.Directory, "perf-results.md")));

        var ids = run.Results.Metrics.Select(m => m.Id).ToList();
        Assert.Equal(MetricCatalog.All.Select(m => m.Id).Order(), ids.Order());
        foreach (var metric in run.Results.Metrics)
        {
            if (metric.Id.StartsWith("package.", StringComparison.Ordinal))
            {
                Assert.True(metric.Status is MetricStatus.Skipped or MetricStatus.Measured, metric.Id);
                continue;
            }

            Assert.Equal(MetricStatus.Measured, metric.Status);
            Assert.True(metric.Median > 0, metric.Id);
            Assert.Equal(metric.N, metric.Samples.Count);
        }

        // Search and memory are the heavy scenarios, everything else uses the plain iteration count.
        Assert.Equal(PerfProfile.Quick.HeavyIterations, run.Results.Find("search.full.text500")!.N);
        Assert.Equal(PerfProfile.Quick.HeavyIterations, run.Results.Find("memory.engine_peak_working_set.text500")!.N);
        Assert.Equal(PerfProfile.Quick.Iterations, run.Results.Find("open.warm.first_tile.text20")!.N);
    }

    [Fact]
    public async Task TheResultFileSaysWhatMachineItRanOnAndThatItIsNotReferenceHardware()
    {
        var results = (await QuickRunAsync()).Results;
        Assert.False(string.IsNullOrWhiteSpace(results.Machine.Cpu));
        Assert.True(results.Machine.LogicalCores > 0);
        Assert.True(results.Machine.RamGb > 1);
        Assert.Equal("test-runner", results.Runner.Id);
        Assert.Null(results.Runner.ReferenceTier);
        Assert.Equal(["text1", "text20", "text500", "text2000"], results.Documents.Select(d => d.Id));
        Assert.All(results.Documents, d => Assert.Equal(64, d.Sha256.Length));
        Assert.Equal(64, results.Engine!.ExeSha256.Length);
        Assert.Contains(results.Notes, n => n.Contains("Not reference hardware", StringComparison.Ordinal));
        Assert.InRange(results.BackgroundCpuBusyPercent!.Value, 0, 100);
        Assert.Equal(results.BackgroundCpuBusyPercent > PerfRunner.BusyWarningPercent, results.Notes.Any(n => n.Contains("busy with other work", StringComparison.Ordinal)));
        Assert.All(results.Metrics.Where(m => m.Budget is not null), m => Assert.False(m.Budget!.ReferenceHardware));
        Assert.NotNull(results.Find("memory.engine_peak_working_set.text500")!.Budget); // held against §11, marked as not from reference hardware
    }

    [Fact]
    public async Task ResultsSurviveAJsonRoundTripUnchangedAndTheMarkdownNamesEveryMetric()
    {
        var run = await QuickRunAsync();
        var json = File.ReadAllText(Path.Combine(run.Directory, "perf-results.json"));
        Assert.Equal(json.TrimEnd(), ResultsJson.Serialize(ResultsJson.Deserialize<PerfResults>(json)).TrimEnd());

        var markdown = File.ReadAllText(Path.Combine(run.Directory, "perf-results.md"));
        foreach (var metric in run.Results.Metrics)
        {
            Assert.Contains($"`{metric.Id}`", markdown, StringComparison.Ordinal);
        }

        Assert.Contains("not reference hardware", markdown, StringComparison.OrdinalIgnoreCase);
        if (run.Results.Metrics.Any(m => m.Status == MetricStatus.Skipped))
        {
            Assert.Contains("skipped", markdown, StringComparison.Ordinal); // a skipped metric is shown as skipped, with its reason under Notes
            Assert.Contains("- Skipped package.install_size:", markdown, StringComparison.Ordinal);
        }
    }

    private static readonly string[] CheapMetrics = ["render.tile_latency.text20", "render.tile_throughput.text20", "open.large.first_tile.text2000"];

    [Fact]
    public async Task TheGatePassesAgainstABaselineItMatchesAndSaysSoWhenThereIsNone()
    {
        var run = await QuickRunAsync();
        var baseline = Path.Combine(run.Directory, "perf-results.json");

        var directory = NewDirectory();
        var (exit, output, error) = await Invoke(["gate", .. Common(directory), "--baseline", baseline, "--threshold", "50", "--only", string.Join(',', CheapMetrics)]);
        Assert.True(exit == 0, error + output);
        Assert.Equal(CheapMetrics.Order(), ResultsJson.Load(Path.Combine(directory, "perf-results.json")).Metrics.Select(m => m.Id).Order());

        var (noBaselineExit, noBaselineOutput, _) = await Invoke(["gate", .. Common(NewDirectory()), "--baseline", Path.Combine(directory, "does-not-exist.json"), "--only", CheapMetrics[0]]);
        Assert.Equal(0, noBaselineExit);
        Assert.Contains("No baseline", noBaselineOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGateFailsWithExitCodeOneWhenTheBaselineWasMuchFasterAndTriesTheSuspectsAgain()
    {
        var run = await QuickRunAsync();
        // A baseline in which everything was five times better than it is now.
        var doctored = run.Results with
        {
            Metrics = [.. run.Results.Metrics.Where(m => CheapMetrics.Contains(m.Id)).Select(m => m with { Median = m.LowerIsBetter ? m.Median / 5 : m.Median * 5 })],
        };
        var baseline = Path.Combine(NewDirectory(), "baseline.json");
        ResultsJson.Save(baseline, doctored);

        var directory = NewDirectory();
        var (exit, output, _) = await Invoke(["gate", .. Common(directory), "--baseline", baseline, "--only", string.Join(',', CheapMetrics)]);

        Assert.Equal(1, exit);
        foreach (var id in CheapMetrics)
        {
            Assert.Contains($"REGRESSION {id}", output, StringComparison.Ordinal);
        }

        Assert.Contains("Measuring them 2 more time", output, StringComparison.Ordinal);
        var written = ResultsJson.Load(Path.Combine(directory, "perf-results.json"));
        Assert.All(written.Metrics, m => Assert.Equal(3.0, m.Details!["runs"])); // each suspect measured three times, the median of the three runs reported
        Assert.True(File.Exists(Path.Combine(directory, "perf-comparison.json")));
        Assert.Contains("regressed", File.ReadAllText(Path.Combine(directory, "perf-comparison.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareJudgesTwoFilesAndReturnsOneForARegression()
    {
        var run = await QuickRunAsync();
        var better = run.Results with { Metrics = [.. run.Results.Metrics.Where(m => m.Id == "render.tile_latency.text20").Select(m => m with { Median = m.Median / 10 })] };
        var good = Path.Combine(NewDirectory(), "better.json");
        var current = Path.Combine(run.Directory, "perf-results.json");
        ResultsJson.Save(good, better);

        var (exitRegressed, output, _) = await Invoke("compare", "--baseline", good, "--current", current);
        Assert.Equal(1, exitRegressed);
        Assert.Contains("REGRESSION render.tile_latency.text20", output, StringComparison.Ordinal);

        var (exitSame, _, _) = await Invoke("compare", "--baseline", current, "--current", current);
        Assert.Equal(0, exitSame);
    }

    [Fact]
    public async Task GenerateWritesTheDocumentsAndPrintsTheirHashes()
    {
        var directory = NewDirectory();
        var (exit, output, _) = await Invoke("generate", "--out", directory, "--only", "text1,text20");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(directory, "text20.pdf")));
        Assert.False(File.Exists(Path.Combine(directory, "text500.pdf")));
        Assert.Contains("0733a3d9c5c96947721d346e7ec840ee6db2f86f7afb1ad501b0b14dd293a4cf", output, StringComparison.Ordinal);
        Assert.Equal(2, (await Invoke("generate", "--out", directory, "--only", "nope")).ExitCode);
    }

    [Theory]
    [InlineData(2, "bogus")]
    [InlineData(2, "run", "--profile", "bogus")]
    [InlineData(2, "run", "--reference-tier", "R9")]
    [InlineData(2, "run", "--profil", "quick")] // a misspelt option must not silently change what is measured
    [InlineData(2, "run", "--only", "not.a.metric")]
    [InlineData(2, "gate")] // --baseline is required
    [InlineData(2, "compare", "--baseline", "a.json")]
    [InlineData(2, "trace-metrics", "--out", "x")]
    [InlineData(0, "help")]
    [InlineData(0, "info")]
    public async Task BadUsageIsRefusedWithExitCodeTwoBeforeAnythingIsMeasured(int expected, params string[] args)
    {
        var (exit, _, error) = await Invoke(args);
        Assert.Equal(expected, exit);
        if (expected == 2)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public async Task NoArgumentsPrintsTheUsageAndFails()
    {
        using var output = new StringWriter();
        var exit = await Commands.RunAsync([], output, TextWriter.Null);
        Assert.Equal(2, exit);
        Assert.Contains("run            Measure", output.ToString(), StringComparison.Ordinal);
    }
}
