using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

public sealed class BaselineSelectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-baselines-" + Guid.NewGuid().ToString("N")[..8]);

    public BaselineSelectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PerfResults On(string runner, string cpu, int day, double searchMs = 1000, string architecture = "x64")
    {
        var results = Make.Results(runner, Make.Metric(MetricCatalog.SearchFull, searchMs));
        return results with { CreatedUtc = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), Machine = results.Machine with { Cpu = cpu, Architecture = architecture } };
    }

    private string Save(string name, PerfResults results)
    {
        var path = Path.Combine(_directory, name);
        ResultsJson.Save(path, results);
        return path;
    }

    [Fact]
    public void TheBaselineForTheSameCpuIsChosenWhateverTheOthersSay()
    {
        Save("ci-x64.epyc-7763.json", On("ci-x64", "AMD EPYC 7763", 1, 2000));
        var wanted = Save("ci-x64.epyc-9v45.json", On("ci-x64", "AMD EPYC 9V45", 2, 1000));
        Save("ci-x64.other.json", On("ci-x64", "Intel Xeon", 3, 3000)); // the newest, but not this CPU

        var choice = BaselineSelector.Choose(_directory, "ci-x64", "AMD EPYC 9V45");
        Assert.Equal(wanted, choice.Path);
        Assert.Equal(1000, choice.Baseline!.Find(MetricCatalog.SearchFull.Id)!.Median);
        Assert.Contains("same CPU", choice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherRunnersBaselinesAreNeverConsidered()
    {
        Save("ci-arm64.cobalt-100.json", On("ci-arm64", "Cobalt 100", 1, architecture: "arm64"));
        Save("ci-x64-extra.json", On("ci-x64", "AMD EPYC 9V45", 2)); // a name that merely starts with the runner id, without the dot
        Save("ci-x64.mislabelled.json", On("ci-arm64", "AMD EPYC 9V45", 2)); // the runner inside the file is the authority
        var choice = BaselineSelector.Choose(_directory, "ci-x64", "AMD EPYC 9V45");
        Assert.Null(choice.Baseline);
        Assert.Null(choice.Path);
        Assert.Contains("ci-x64.amd-epyc-9v45.json", choice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoBaselineForThisCpuTheNewestOtherIsOfferedForInformationAndTheFileNameToCommitIsSuggested()
    {
        Save("ci-x64.old.json", On("ci-x64", "AMD EPYC 7763", 1));
        var newest = Save("ci-x64.new.json", On("ci-x64", "Intel Xeon Platinum", 5));
        var choice = BaselineSelector.Choose(_directory, "ci-x64", "AMD EPYC 9V45 96-Core Processor");
        Assert.Equal(newest, choice.Path);
        Assert.Contains("for information only", choice.Message, StringComparison.Ordinal);
        Assert.Contains("ci-x64.amd-epyc-9v45-96-core-processor.json", choice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDirectoryIsNoBaselineNotAnError()
    {
        var choice = BaselineSelector.Choose(Path.Combine(_directory, "nope"), "ci-x64", "cpu");
        Assert.Null(choice.Baseline);
    }

    [Theory]
    [InlineData("ci-x64", "AMD EPYC 9V45 96-Core Processor", "ci-x64.amd-epyc-9v45-96-core-processor.json")]
    [InlineData("ci-arm64", "Cobalt 200", "ci-arm64.cobalt-200.json")]
    [InlineData("ci-x64", "Intel(R) Core(TM) Ultra 9 275HX", "ci-x64.intel-r-core-tm-ultra-9-275hx.json")]
    [InlineData("ci-x64", "", "ci-x64.unknown-cpu.json")]
    public void TheSuggestedFileNameIsALowercaseSlugOfTheCpu(string runner, string cpu, string expected) =>
        Assert.Equal(expected, BaselineSelector.SuggestedFileName(runner, cpu));

    [Fact]
    public void ARunOnADifferentCpuIsNotGatedBecauseTheSameCodeRunsTwiceAsFastOnABetterOne()
    {
        var baseline = On("ci-x64", "AMD EPYC 7763", 1, 1000);
        var slowerMachine = On("ci-x64", "Some Older CPU", 2, 2000);
        var report = BaselineComparer.Compare(baseline, slowerMachine);
        Assert.False(report.Failed);
        Assert.NotNull(report.NotComparable);
        var item = Assert.Single(report.Items);
        Assert.Equal(Verdict.Advisory, item.Verdict);
        Assert.Contains("advisory only", item.Note, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, w => w.Contains("different machines", StringComparison.Ordinal));

        // The same numbers on the same CPU fail.
        var sameCpu = BaselineComparer.Compare(baseline, On("ci-x64", "AMD EPYC 7763", 2, 2000));
        Assert.True(sameCpu.Failed);
        Assert.Null(sameCpu.NotComparable);

        // A metric that could not be measured fails regardless of the CPU.
        var broken = On("ci-x64", "Some Older CPU", 2) with { Metrics = [MetricResults.Failed(MetricCatalog.SearchFull, "boom")] };
        Assert.True(BaselineComparer.Compare(baseline, broken).Failed);
    }

    [Fact]
    public void AnotherArchitectureIsNotComparableEither()
    {
        var report = BaselineComparer.Compare(On("ci-x64", "cpu", 1, 1000), On("ci-arm64", "cpu", 2, 5000, architecture: "arm64"));
        Assert.False(report.Failed);
        Assert.Contains("another architecture", report.NotComparable, StringComparison.Ordinal);
    }
}
