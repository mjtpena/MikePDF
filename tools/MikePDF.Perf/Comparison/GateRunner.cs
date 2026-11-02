using MikePDF.Perf.Measurement;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

public sealed record GateOutcome(PerfResults Results, ComparisonReport Report, int Runs, IReadOnlyList<string> Retried);

/// <summary>
/// The regression gate with its noise policy (docs/perf/README.md). Each metric is already the median of many iterations. A metric that looks
/// regressed after the first run is measured again, alone, <c>retries</c> more times; its final value is the median of its per-run medians, so a
/// slowdown has to show up in at least two of three runs to fail the build, and one noisy run cannot.
/// </summary>
public static class GateRunner
{
    public const int DefaultRetries = 2;

    /// <param name="baseline">The per-runner baseline; null on the first run, which then passes with a notice.</param>
    /// <param name="measure">Runs the suite, or just the given metric ids.</param>
    /// <param name="threshold">How much worse than the baseline fails (a fraction; 0.10).</param>
    /// <param name="retries">How many more times a metric that looks regressed is measured.</param>
    /// <param name="log">Progress messages.</param>
    public static async Task<GateOutcome> RunAsync(
        PerfResults? baseline,
        Func<IReadOnlySet<string>?, Task<PerfResults>> measure,
        double threshold = BaselineComparer.DefaultThreshold,
        int retries = DefaultRetries,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        log ??= _ => { };

        var first = await measure(null);
        var report = BaselineComparer.Compare(baseline, first, threshold);
        var suspects = report.Items.Where(i => i.Verdict == Verdict.Regressed).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        if (baseline is null || suspects.Count == 0 || retries == 0)
        {
            return new GateOutcome(first, report, 1, []);
        }

        log($"Looks regressed after the first run: {string.Join(", ", suspects.Order(StringComparer.Ordinal))}. Measuring them {retries} more time(s).");
        var runs = new List<PerfResults> { first };
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            runs.Add(await measure(suspects));
        }

        var merged = first with { Metrics = [.. first.Metrics.Select(m => suspects.Contains(m.Id) ? Merge(m, runs.Select(r => r.Find(m.Id))) : m)] };
        return new GateOutcome(merged, BaselineComparer.Compare(baseline, merged, threshold), runs.Count, [.. suspects.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The metric over several runs: the median of the runs' medians, with the pooled samples kept for the record. A run that could not measure it counts against it.</summary>
    internal static MetricResult Merge(MetricResult first, IEnumerable<MetricResult?> runs)
    {
        var measured = runs.Where(r => r is { Status: MetricStatus.Measured, Median: not null }).Select(r => r!).ToList();
        if (measured.Count == 0 || first.Status != MetricStatus.Measured)
        {
            return first;
        }

        var medians = measured.Select(r => r.Median!.Value).ToList();
        var pooled = measured.SelectMany(r => r.Samples).ToList();
        var details = new Dictionary<string, double>(first.Details ?? new Dictionary<string, double>(), StringComparer.Ordinal)
        {
            ["runs"] = measured.Count,
        };
        for (var i = 0; i < medians.Count; i++)
        {
            details[$"run{i + 1}_median"] = medians[i];
        }

        var summary = pooled.Count > 0 ? Stats.Summarize(pooled) : null;
        return first with
        {
            N = measured.Sum(r => r.N),
            Median = Math.Round(Stats.Median(medians), 3),
            P95 = Math.Round(Stats.Median(measured.Select(r => r.P95 ?? r.Median!.Value).ToList()), 3),
            Min = summary is null ? first.Min : Math.Round(summary.Min, 3),
            Max = summary is null ? first.Max : Math.Round(summary.Max, 3),
            Mean = summary is null ? first.Mean : Math.Round(summary.Mean, 3),
            StdDev = summary is null ? first.StdDev : Math.Round(summary.StdDev, 3),
            CoefficientOfVariation = summary is null ? first.CoefficientOfVariation : Math.Round(summary.CoefficientOfVariation, 4),
            Samples = pooled,
            Details = details,
        };
    }
}
