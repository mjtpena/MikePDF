using System.Globalization;
using System.Text;
using MikePDF.Perf.Comparison;

namespace MikePDF.Perf.Results;

/// <summary>The results as a Markdown page: for the CI job summary and for docs/perf/baselines.</summary>
public static class MarkdownReport
{
    public static string Render(PerfResults results, ComparisonReport? comparison = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        var text = new StringBuilder();
        var machine = results.Machine;
        text.AppendLine("# MikePDF performance results").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Runner: `{results.Runner.Id}`{(results.Runner.ReferenceTier is { } tier ? $", declared reference hardware {tier}" : ", **not reference hardware**")}{(machine.CiHosted ? " (CI-hosted)" : string.Empty)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {machine.Cpu}, {machine.LogicalCores} logical cores");
        text.AppendLine(CultureInfo.InvariantCulture, $"- RAM: {machine.RamGb:0.#} GB");
        text.AppendLine(CultureInfo.InvariantCulture, $"- GPU: {(machine.Gpus.Count == 0 ? "none found" : string.Join("; ", machine.Gpus))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- OS: {machine.Os} ({machine.Architecture}); {machine.DotNet}; power plan: {machine.PowerPlan ?? "unknown"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Profile: {results.Profile.Name} (warm-up {results.Profile.Warmup}, {results.Profile.Iterations} iterations, {results.Profile.HeavyIterations} for the heavy scenarios)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Date: {results.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        if (results.Engine is { } engine)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Engine: MikePDF.Engine.exe `{engine.ExeSha256[..12]}`, pdfium.dll `{engine.PdfiumSha256[..12]}`");
        }

        text.AppendLine().AppendLine("## Metrics").AppendLine();
        text.AppendLine("| Metric | Unit | n | Median | p95 | CV | §11 target | Baseline | Change | Result |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var metric in results.Metrics)
        {
            var item = comparison?.Items.FirstOrDefault(i => string.Equals(i.Id, metric.Id, StringComparison.Ordinal));
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{metric.Id}` | {metric.Unit} | {Cell(metric.N, metric.Status)} | {Number(metric.Median)} | {Number(metric.P95)} | {Percent(metric.CoefficientOfVariation)} | {Target(metric)} | {Number(item?.Baseline)} | {Change(item)} | {Result(metric, item)} |");
        }

        if (comparison is not null)
        {
            text.AppendLine().AppendLine("## Against the baseline").AppendLine();
            text.AppendLine(comparison.HasBaseline
                ? $"A metric fails when it is worse than the baseline by more than {comparison.Threshold:P0} and by more than its absolute floor, after the retries described in docs/perf/README.md."
                : "**No baseline exists for this runner yet, so nothing was compared.** The first results are the candidate baseline: review them and commit them as `docs/perf/baselines/<runner>.json`.");
            foreach (var warning in comparison.Warnings)
            {
                text.AppendLine().AppendLine("- Warning: " + warning);
            }

            foreach (var item in comparison.Items.Where(i => i.Verdict is Verdict.Regressed or Verdict.Failed or Verdict.Advisory))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- **{item.Verdict}** `{item.Id}`: {item.Note}");
            }
        }

        if (results.Documents.Count > 0)
        {
            text.AppendLine().AppendLine("## Generated documents").AppendLine();
            text.AppendLine("| Id | Pages | Bytes | SHA-256 |").AppendLine("|---|---:|---:|---|");
            foreach (var document in results.Documents)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {document.Id} | {document.Pages} | {document.Bytes:N0} | `{document.Sha256[..16]}` |");
            }
        }

        if (results.Notes.Count > 0)
        {
            text.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in results.Notes)
            {
                text.AppendLine("- " + note);
            }
        }

        return text.ToString();
    }

    private static string Cell(int n, MetricStatus status) => status == MetricStatus.Measured ? n.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Number(double? value) => value is { } v ? v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) : "-";

    private static string Percent(double? fraction) => fraction is { } f ? (f * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";

    private static string Target(MetricResult metric) =>
        metric.Budget is { } budget
            ? $"{(metric.LowerIsBetter ? "≤" : "≥")} {budget.Target.ToString("0.##", CultureInfo.InvariantCulture)} ({budget.Tier})"
            : "-";

    private static string Change(MetricComparison? item) => item?.Change is { } change ? (change * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "-";

    private static string Result(MetricResult metric, MetricComparison? item)
    {
        if (metric.Status == MetricStatus.Skipped)
        {
            return "skipped";
        }

        if (metric.Status == MetricStatus.Failed)
        {
            return "FAILED";
        }

        var parts = new List<string>();
        if (item is not null && item.Verdict != Verdict.Ok)
        {
            parts.Add(item.Verdict.ToString().ToLowerInvariant());
        }

        if (metric.Budget is { } budget)
        {
            var where = budget.ReferenceHardware ? string.Empty : " here (not reference hardware)";
            parts.Add(budget.IsLowerBound
                ? budget.Within ? "engine share only: under target" + where + ", not a pass" : "OVER target" + where + ", and the app adds to it"
                : budget.Within ? "within target" + where : "OVER target" + where);
        }

        return parts.Count == 0 ? "ok" : string.Join("; ", parts);
    }
}
