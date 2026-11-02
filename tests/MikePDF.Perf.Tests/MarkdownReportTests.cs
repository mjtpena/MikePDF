using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;

namespace MikePDF.Perf.Tests;

public sealed class MarkdownReportTests
{
    [Fact]
    public void TheHeaderNamesTheMachineTheProfileAndThatItIsNotReferenceHardware()
    {
        var results = Make.Results("ci-x64", Make.Metric(MetricCatalog.SearchFull, 500)) with { BackgroundCpuBusyPercent = 7 };
        var markdown = MarkdownReport.Render(results);
        Assert.Contains("- Runner: `ci-x64`, **not reference hardware**", markdown, StringComparison.Ordinal);
        Assert.Contains("- CPU: Test CPU, 4 logical cores", markdown, StringComparison.Ordinal);
        Assert.Contains("- Profile: ci (warm-up 3, 15 iterations, 5 for the heavy scenarios)", markdown, StringComparison.Ordinal);
        Assert.Contains("- Machine busy with other work before the run: 7%", markdown, StringComparison.Ordinal);

        var reference = Make.Results("ref-r1") with { Runner = new RunnerInfo { Id = "ref-r1", ReferenceTier = "R1" } };
        Assert.Contains("declared reference hardware R1", MarkdownReport.Render(reference), StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetVerdictSaysWhereItCameFromAndALowerBoundIsNeverCalledAPass()
    {
        var budgets = BudgetFile.Load(Path.Combine(Setup.EngineWorkspace.FindRepositoryRoot()!, "docs", "perf", "budgets.json"));
        var metrics = budgets.Apply([Make.Metric(MetricCatalog.SearchFull, 500), Make.Metric(MetricCatalog.WarmOpen, 40), Make.Metric(MetricCatalog.CrashToTile, 5000)], null);
        var markdown = MarkdownReport.Render(Make.Results("x") with { Metrics = metrics });
        Assert.Contains("within target here (not reference hardware)", markdown, StringComparison.Ordinal);
        Assert.Contains("engine share only: under target here (not reference hardware), not a pass", markdown, StringComparison.Ordinal);
        Assert.Contains("OVER target here (not reference hardware)", markdown, StringComparison.Ordinal);

        var onReference = budgets.Apply([Make.Metric(MetricCatalog.SearchFull, 500)], "R1");
        Assert.Contains("| within target |", MarkdownReport.Render(Make.Results("x") with { Metrics = onReference }), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutABaselineTheReportSaysNothingWasComparedAndWithOneItListsTheRegressions()
    {
        var current = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1300));
        var none = MarkdownReport.Render(current, BaselineComparer.Compare(null, current));
        Assert.Contains("No baseline exists for this runner yet, so nothing was compared.", none, StringComparison.Ordinal);
        Assert.Contains("no baseline", none, StringComparison.Ordinal);
        Assert.DoesNotContain("nobaseline", none, StringComparison.Ordinal);

        var baseline = Make.Results("r", Make.Metric(MetricCatalog.SearchFull, 1000));
        var some = MarkdownReport.Render(current, BaselineComparer.Compare(baseline, current));
        Assert.Contains("**Regressed** `search.full.text500`", some, StringComparison.Ordinal);
        Assert.Contains("+30.0%", some, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedAndFailedMetricsAreShownAsSuch()
    {
        var results = Make.Results("r", MetricResults.Skipped(MetricCatalog.InstallSize, "no package"), MetricResults.Failed(MetricCatalog.SearchFull, "boom")) with { Notes = ["Skipped package.install_size: no package"] };
        var markdown = MarkdownReport.Render(results);
        Assert.Contains("| skipped |", markdown, StringComparison.Ordinal);
        Assert.Contains("| FAILED |", markdown, StringComparison.Ordinal);
        Assert.Contains("- Skipped package.install_size: no package", markdown, StringComparison.Ordinal);
    }
}
