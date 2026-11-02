using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

public enum Verdict
{
    /// <summary>Within the threshold (or within the floor).</summary>
    Ok,

    /// <summary>Better than the baseline by more than the threshold.</summary>
    Improved,

    /// <summary>Worse by more than the threshold and by more than the metric's floor. Fails the gate.</summary>
    Regressed,

    /// <summary>The metric could not be measured in this run (an error, not a skip). Fails the gate.</summary>
    Failed,

    /// <summary>Worse than the baseline over the threshold, but the metric is advisory (not gating).</summary>
    Advisory,

    /// <summary>No baseline value to compare with: the first run on a runner, or a metric added since. Passes, loudly.</summary>
    NoBaseline,

    /// <summary>Not run in this run, with a reason.</summary>
    Skipped,

    /// <summary>A/B gate only: looks worse than the threshold, but the evidence (the confidence interval, or the number of paired rounds) does not show it is real. Passes, loudly.</summary>
    Unconfirmed,
}

/// <summary><paramref name="Change"/> is a fraction of the baseline, signed so that positive always means worse, whichever way the metric points.</summary>
public sealed record MetricComparison(string Id, string Unit, Verdict Verdict, double? Baseline, double? Current, double? Change, string Note);

/// <summary>The outcome of holding one run against a baseline.</summary>
public sealed record ComparisonReport(double Threshold, bool HasBaseline, IReadOnlyList<MetricComparison> Items, IReadOnlyList<string> Warnings, string? NotComparable = null)
{
    public IReadOnlyList<MetricComparison> Regressions => [.. Items.Where(i => i.Verdict is Verdict.Regressed or Verdict.Failed)];

    /// <summary>True when the gate fails.</summary>
    public bool Failed => Regressions.Count > 0;
}

public static class BaselineComparer
{
    /// <summary>The §11 rule: a regression of more than 10% on a budget metric fails the build.</summary>
    public const double DefaultThreshold = 0.10;

    /// <summary>
    /// Holds <paramref name="current"/> against <paramref name="baseline"/> (null: there is none yet). A metric regresses only when it is worse by
    /// more than <paramref name="threshold"/> of the baseline <i>and</i> by more than its floor in absolute terms; both medians are medians of many runs.
    /// </summary>
    public static ComparisonReport Compare(PerfResults? baseline, PerfResults current, double threshold = DefaultThreshold, Func<string, MetricDefinition?>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        definitions ??= MetricCatalog.Find;
        var warnings = new List<string>();
        string? notComparable = null;
        if (baseline is not null)
        {
            if (!string.Equals(baseline.Machine.Architecture, current.Machine.Architecture, StringComparison.Ordinal))
            {
                warnings.Add($"The baseline is from {baseline.Machine.Architecture} and this run is {current.Machine.Architecture}: the comparison is not meaningful.");
                notComparable = "the baseline is for another architecture";
            }

            if (!string.Equals(baseline.Runner.Id, current.Runner.Id, StringComparison.Ordinal))
            {
                warnings.Add($"The baseline belongs to runner '{baseline.Runner.Id}' and this run to '{current.Runner.Id}'.");
            }

            if (!string.Equals(baseline.Machine.Cpu, current.Machine.Cpu, StringComparison.Ordinal))
            {
                warnings.Add($"The CPU differs from the baseline's ({baseline.Machine.Cpu} then, {current.Machine.Cpu} now): hosted runners are drawn from a pool of different machines, and the same code can run twice as fast on one as on another.");
                notComparable ??= $"the baseline was measured on a different CPU ({baseline.Machine.Cpu})";
            }
        }

        var items = new List<MetricComparison>();
        foreach (var metric in current.Metrics)
        {
            items.Add(CompareOne(baseline?.Find(metric.Id), metric, threshold, definitions(metric.Id), notComparable));
        }

        return new ComparisonReport(threshold, baseline is not null, items, warnings, notComparable);
    }

    private static MetricComparison CompareOne(MetricResult? baseline, MetricResult current, double threshold, MetricDefinition? definition, string? notComparable)
    {
        switch (current.Status)
        {
            case MetricStatus.Skipped:
                return new MetricComparison(current.Id, current.Unit, Verdict.Skipped, baseline?.Median, null, null, current.Reason ?? "skipped");
            case MetricStatus.Failed:
                return new MetricComparison(current.Id, current.Unit, Verdict.Failed, baseline?.Median, null, null, current.Reason ?? "could not be measured");
            default:
                break;
        }

        if (baseline is not { Status: MetricStatus.Measured, Median: { } before } || before <= 0 || current.Median is not { } after)
        {
            return new MetricComparison(current.Id, current.Unit, Verdict.NoBaseline, null, current.Median, null, "no baseline value");
        }

        var floor = definition?.Floor ?? 0;
        var worse = current.LowerIsBetter ? after - before : before - after;
        var change = worse / before;
        var verdict = Verdict.Ok;
        var note = string.Empty;
        if (change > threshold && worse > floor)
        {
            verdict = definition?.Gate == false || notComparable is not null ? Verdict.Advisory : Verdict.Regressed;
            note = $"{change:P1} worse (limit {threshold:P0}, floor {floor:0.###} {current.Unit})" + (notComparable is null ? string.Empty : $"; advisory only, because {notComparable}");
        }
        else if (change < -threshold && -worse > floor)
        {
            verdict = Verdict.Improved;
            note = $"{-change:P1} better";
        }
        else if (change > threshold)
        {
            note = $"{change:P1} worse but only {worse:0.###} {current.Unit}, under the {floor:0.###} {current.Unit} floor";
        }

        return new MetricComparison(current.Id, current.Unit, verdict, before, after, change, note);
    }
}
