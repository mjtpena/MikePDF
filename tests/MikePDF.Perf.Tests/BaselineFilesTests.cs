using MikePDF.Perf.Comparison;
using MikePDF.Perf.Results;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Tests;

/// <summary>The baselines committed under docs/perf/baselines are what CI compares with: they must load, name their runner and machine, and compare cleanly with themselves.</summary>
public sealed class BaselineFilesTests
{
    private static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Path.Combine(EngineWorkspace.FindRepositoryRoot()!, "docs", "perf", "baselines"), "*.json");

    [Fact]
    public void EveryCommittedBaselineLoadsNamesItsRunnerAndMachineAndMatchesItsFileName()
    {
        var files = Files().ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var baseline = ResultsJson.Load(file);
            Assert.Equal(PerfResults.CurrentSchema, baseline.Schema);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), baseline.Runner.Id);
            Assert.False(string.IsNullOrWhiteSpace(baseline.Machine.Cpu));
            Assert.NotEmpty(baseline.Machine.Gpus);
            Assert.True(baseline.Machine.RamGb > 0);
            Assert.Contains(baseline.Metrics, m => m.Status == MetricStatus.Measured);
            Assert.DoesNotContain(baseline.Metrics, m => m.Status == MetricStatus.Failed);
            Assert.False(BaselineComparer.Compare(baseline, baseline).Failed, file);
            Assert.True(File.Exists(Path.ChangeExtension(file, ".md")), "each baseline has its readable Markdown beside it");
        }
    }

    [Fact]
    public void ADevelopmentBaselineIsNeverDeclaredToBeReferenceHardware()
    {
        foreach (var file in Files().Where(f => Path.GetFileName(f).StartsWith("dev-", StringComparison.Ordinal)))
        {
            var baseline = ResultsJson.Load(file);
            Assert.Null(baseline.Runner.ReferenceTier);
            Assert.All(baseline.Metrics.Where(m => m.Budget is not null), m => Assert.False(m.Budget!.ReferenceHardware));
        }
    }
}
