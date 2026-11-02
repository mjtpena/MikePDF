using System.Text.Json.Serialization;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Runner;

/// <summary>One row of master prompt §11 and how it can be measured. docs/perf/budgets.json holds them all.</summary>
public sealed record BudgetEntry
{
    public required string BudgetId { get; init; }

    public required string Name { get; init; }

    public required string Unit { get; init; }

    /// <summary>"lower" or "higher" is better.</summary>
    public required string Direction { get; init; }

    /// <summary>Tier ("R1", "R2", "R3") to target; null where §11 sets none for that tier.</summary>
    public required IReadOnlyDictionary<string, double?> Targets { get; init; }

    /// <summary>Tiers whose target is simply R1's, because §11 says budgets are measured on R1 unless stated.</summary>
    public IReadOnlyList<string> TargetsInherited { get; init; } = [];

    /// <summary>"headless" (this harness), "headless-part" (this harness measures only the engine's share), "app-etw" (the app's trace markers and tools/MikePDF.Perf/measure-app.ps1), "package" (a built MSIX) or "manual" (no automation yet).</summary>
    public required string MeasuredBy { get; init; }

    /// <summary>Result metric ids held against this budget; the first is the primary one.</summary>
    public IReadOnlyList<string> HarnessMetrics { get; init; } = [];

    /// <summary>
    /// Metric id to "full" or "lower-bound". A <c>lower-bound</c> metric measures only part of what the budget covers (the engine's share of an
    /// open, without the UI thread), so being under the target proves nothing and being over it proves the budget is missed. Absent means "full".
    /// </summary>
    public IReadOnlyDictionary<string, string> Coverage { get; init; } = new Dictionary<string, string>();

    /// <summary>Which statistic of the metric is compared with the target.</summary>
    public string Statistic { get; init; } = "median";

    public string? Notes { get; init; }

    [JsonIgnore]
    public bool LowerIsBetter => string.Equals(Direction, "lower", StringComparison.Ordinal);
}

public sealed record TierInfo(string Description, string? Hardware);

public sealed record BudgetFile
{
    public required int Schema { get; init; }

    public required string Source { get; init; }

    /// <summary>The fraction a metric may get worse before the build fails (§11: 10%).</summary>
    public required double RegressionThreshold { get; init; }

    public required IReadOnlyDictionary<string, TierInfo> Tiers { get; init; }

    public required IReadOnlyList<BudgetEntry> Metrics { get; init; }

    public BudgetEntry? Find(string budgetId) => Metrics.FirstOrDefault(m => string.Equals(m.BudgetId, budgetId, StringComparison.Ordinal));

    public static BudgetFile Load(string path) => ResultsJson.Deserialize<BudgetFile>(File.ReadAllText(path));

    /// <summary>docs/perf/budgets.json of the checkout this tool runs from, or null.</summary>
    public static string? DefaultPath(string repositoryRoot) =>
        string.IsNullOrEmpty(repositoryRoot) ? null : Path.Combine(repositoryRoot, "docs", "perf", "budgets.json");

    /// <summary>Attaches the §11 target to every measured metric that has one.</summary>
    public IReadOnlyList<MetricResult> Apply(IReadOnlyList<MetricResult> metrics, string? referenceTier)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var tier = referenceTier ?? "R1";
        var checkedMetrics = new List<MetricResult>(metrics.Count);
        foreach (var metric in metrics)
        {
            var definition = MetricCatalog.Find(metric.Id);
            var entry = definition?.BudgetId is { } id ? Find(id) : null;
            var target = entry is null ? null : TargetFor(entry, tier);
            if (entry is null || target is null || metric.Status != MetricStatus.Measured || metric.Median is not { } value)
            {
                checkedMetrics.Add(metric);
                continue;
            }

            var within = entry.LowerIsBetter ? value <= target.Value : value >= target.Value;
            var coverage = entry.Coverage.TryGetValue(metric.Id, out var declared) ? declared : "full";
            checkedMetrics.Add(metric with { Budget = new BudgetCheck(entry.BudgetId, tier, target.Value, within, ReferenceHardware: referenceTier is not null, Coverage: coverage) });
        }

        return checkedMetrics;
    }

    /// <summary>The target for a tier, or R1's where §11 sets none for that tier.</summary>
    public static double? TargetFor(BudgetEntry entry, string tier)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Targets.TryGetValue(tier, out var own) && own is not null)
        {
            return own;
        }

        return entry.Targets.TryGetValue("R1", out var r1) ? r1 : null;
    }
}
