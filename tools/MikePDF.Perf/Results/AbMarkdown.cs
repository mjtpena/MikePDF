using System.Globalization;
using System.Text;
using MikePDF.Perf.Comparison;
using MikePDF.Perf.Measurement;

namespace MikePDF.Perf.Results;

/// <summary>What an A/B run compared, for the report.</summary>
public sealed record AbMeta(
    string BaseLabel,
    string HeadLabel,
    string RunnerId,
    string Profile,
    int PlannedRounds,
    int RoundsRun,
    IReadOnlyList<string> Confirmed,
    double PlantedSlowdown,
    string? ReferenceTier,
    string? TrendNote);

/// <summary>The A/B run as Markdown: the gate table (base, head, ratio, interval, verdict), the §11 targets, and the stored-baseline trend.</summary>
public static class AbMarkdown
{
    public static string Render(AbReport report, AbMeta meta, PerfResults head, ComparisonReport? trend)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(head);
        var text = new StringBuilder();
        var machine = head.Machine;
        var settings = report.Settings;
        text.AppendLine("# MikePDF performance: head against base on the same runner").AppendLine();
        if (meta.PlantedSlowdown > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"> **PLANTED SLOWDOWN of {Fmt.Percent(meta.PlantedSlowdown, 0)} in the head.** This run tests the gate itself. It is not a measurement of the engine.").AppendLine();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Base: {meta.BaseLabel}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Head: {meta.HeadLabel}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Runner: `{meta.RunnerId}`, {machine.Cpu}, {machine.LogicalCores} logical cores, {machine.Os} ({machine.Architecture}){(machine.CiHosted ? ", CI-hosted" : string.Empty)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Rounds: {meta.PlannedRounds} planned, {meta.RoundsRun} run, profile `{meta.Profile}`. In every round the base and the head are measured one after the other, and which goes first alternates, so a slow minute on the machine lands on both.");
        if (meta.Confirmed.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Measured again for {meta.RoundsRun - meta.PlannedRounds} more round(s) because they looked regressed: {string.Join(", ", meta.Confirmed.Select(c => $"`{c}`"))}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Rule: a metric regresses when its median change over the rounds is more than {Fmt.Percent(settings.Threshold, 0)} worse, the lower end of its {Fmt.Percent(settings.Confidence, 0)} confidence interval is more than {Fmt.Percent(settings.Margin, 0)} worse, and the difference is over the metric's absolute floor. Anything else passes.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"**Result: {(report.Failed ? "FAILED, " + report.Regressions.Count + " metric(s) regressed or could not be measured" : "passed, no regression")}.**").AppendLine();

        text.AppendLine("## Head against base (this is what gates)").AppendLine();
        text.AppendLine("| Metric | Unit | Base | Head | Ratio head/base | Interval | Rounds | Verdict |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.Items)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{item.Id}` | {item.Unit} | {Number(item.Base)} | {Number(item.Head)} | {Ratio(item.Ratio)} | {Interval(item)} | {item.Pairs} | {VerdictText(item)} |");
        }

        foreach (var item in report.Items.Where(i => i.Verdict is Verdict.Regressed or Verdict.Failed or Verdict.Advisory or Verdict.Unconfirmed))
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"- **{item.Verdict}** `{item.Id}`: {item.Note}");
        }

        text.AppendLine().AppendLine("## Against the section 11 targets (reported here, not gating on this runner)").AppendLine();
        text.AppendLine(meta.ReferenceTier is null
            ? "These are hosted-runner numbers, not reference hardware (R-09, R-17): a value over a target does not fail this job, and a value under it proves nothing about the budget. The targets gate only on declared reference hardware."
            : $"This run is declared reference hardware ({meta.ReferenceTier}): a full-coverage metric over its target fails the job.");
        text.AppendLine().AppendLine("| Metric | Target | Head median | Result |").AppendLine("|---|---:|---:|---|");
        foreach (var metric in head.Metrics.Where(m => m.Budget is not null))
        {
            var budget = metric.Budget!;
            var where = budget.ReferenceHardware ? string.Empty : " here";
            var result = budget.IsLowerBound
                ? budget.Within ? "engine share only: under target" + where + ", not a pass" : "OVER target" + where + ", and the app adds to it"
                : budget.Within ? "within target" + where : "OVER target" + where;
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{metric.Id}` | {(metric.LowerIsBetter ? "≤" : "≥")} {budget.Target.ToString("0.##", CultureInfo.InvariantCulture)} {metric.Unit} ({budget.Tier}) | {Number(metric.Median)} | {result} |");
        }

        if (trend is not null)
        {
            text.AppendLine().AppendLine("## Trend against the stored baseline (information only, never gates)").AppendLine();
            text.AppendLine(meta.TrendNote ?? string.Empty).AppendLine();
            text.AppendLine("| Metric | Stored baseline | Head | Change |").AppendLine("|---|---:|---:|---:|");
            foreach (var item in trend.Items.Where(i => i.Baseline is not null))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{item.Id}` | {Number(item.Baseline)} | {Number(item.Current)} | {(item.Change is { } c ? (c * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "-")} |");
            }
        }

        if (report.Warnings.Count > 0 || head.Notes.Count > 0)
        {
            text.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in report.Warnings.Concat(head.Notes))
            {
                text.AppendLine("- " + note);
            }
        }

        return text.ToString();
    }

    private static string Number(double? value) => value is { } v ? v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) : "-";

    private static string Ratio(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    private static string Interval(AbMetric item) =>
        item is { RatioLower: { } low, RatioUpper: { } high, Confidence: { } confidence }
            ? string.Create(CultureInfo.InvariantCulture, $"{low:0.000} to {high:0.000} ({Fmt.Percent(confidence, 0)})")
            : "-";

    private static string VerdictText(AbMetric item) => item.Verdict switch
    {
        Verdict.Ok => "ok",
        Verdict.NoBaseline => "no base value",
        Verdict.Regressed => "**REGRESSED**",
        Verdict.Failed => "**FAILED** (could not be measured)",
        _ => item.Verdict.ToString().ToLowerInvariant(),
    };
}
