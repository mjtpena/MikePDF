using System.Globalization;
using System.Text;

namespace MikePDF.Render.Reporting;

/// <summary>The human-readable half of the report (the JSON is the machine-readable one).</summary>
internal static class MarkdownReport
{
    public static string Write(RunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        var summary = report.Summary;
        var inv = CultureInfo.InvariantCulture;

        text.Append("# mikepdf-render report\n\n");
        text.Append(inv, $"**Gate: {(report.Gate.Passed ? "PASSED" : "FAILED")}** ");
        text.Append(inv, $"({report.Gate.Failures.Count} failure(s), {report.Gate.Warnings.Count} warning(s))\n\n");
        text.Append(inv, $"- Started {report.StartedUtc:u}, took {report.DurationMs / 1000.0:0.0} s\n");
        text.Append(inv, $"- {report.Machine.Os}, {report.Machine.Architecture}, {report.Machine.Runtime}");
        if (report.Machine.PdfiumVersion is { } pdfium)
        {
            text.Append(inv, $", PDFium {pdfium}");
        }

        text.Append('\n');
        text.Append(inv, $"- Tier: {report.Settings.Tier ?? "(files given on the command line)"}; scales {string.Join(", ", report.Settings.Scales.Select(s => s.ToString("0.###", inv)))}; pages {report.Settings.Pages}; reference {(report.Settings.Reference ? "on" : "off")}\n\n");

        text.Append("## Summary\n\n| Measure | Value |\n|---|---|\n");
        Row(text, "Files", summary.Files.ToString(inv));
        Row(text, "Opened (of those expected to open)", $"{summary.FilesOpened} of {summary.FilesExpectedToOpen} ({summary.OpenSuccessPercent:0.###}%)");
        Row(text, "Files expected not to open (hostile on purpose)", summary.FilesExpectedNotToOpen.ToString(inv));
        Row(text, "Unexpected open outcomes", summary.UnexpectedOpenOutcomes.ToString(inv));
        Row(text, "Pages attempted", summary.PagesAttempted.ToString(inv));
        Row(text, "Pages rendered Ok", summary.PagesRendered.ToString(inv));
        Row(text, "Pages partially rendered", summary.PagesPartial.ToString(inv));
        Row(text, "Pages failed", summary.PagesFailed.ToString(inv));
        Row(text, "Timeouts", summary.Timeouts.ToString(inv));
        Row(text, "Engine crashes / hangs", $"{summary.EngineCrashes} / {summary.EngineHangs}");
        Row(text, "Pages compared with the reference", summary.SsimCompared.ToString(inv));
        Row(text, $"... at or above SSIM {report.Settings.SsimThreshold:0.###}", $"{summary.SsimAtOrAboveThreshold} ({summary.SsimPassPercent:0.###}%; target {report.Settings.SsimPassTarget:0.###}%)");
        Row(text, "SSIM min / mean", $"{Number(summary.SsimMin)} / {Number(summary.SsimMean)}");
        Row(text, "Pages without a reference render", summary.ReferenceMissing.ToString(inv));
        Row(text, "Engine time in rendering", $"{summary.TotalRenderMs / 1000.0:0.00} s");

        if (report.Gate.Failures.Count > 0)
        {
            text.Append("\n## Gate failures\n\n");
            foreach (var failure in report.Gate.Failures)
            {
                text.Append(inv, $"- **{failure.Kind}**{Where(failure)}: {failure.Message}\n");
            }
        }

        if (report.Gate.Warnings.Count > 0)
        {
            text.Append("\n## Warnings\n\n");
            foreach (var warning in report.Gate.Warnings)
            {
                text.Append(inv, $"- {warning.Kind}{Where(warning)}: {warning.Message}\n");
            }
        }

        text.Append("\n## Files\n\n| File | Open | Pages | Rendered | Worst SSIM | Notes |\n|---|---|---|---|---|---|\n");
        foreach (var file in report.Files)
        {
            var rendered = file.Pages.Count(p => p.Status == "Ok");
            var worst = file.Pages.Where(p => p.Ssim is not null).Select(p => p.Ssim!.Value).DefaultIfEmpty(double.NaN).Min();
            var notes = new List<string>();
            if (file.Verdict == "expected-failure")
            {
                notes.Add("failed to open, as expected");
            }

            if (file.EngineEvents.Any(e => e.Kind == "crashed"))
            {
                notes.Add($"{file.EngineEvents.Count(e => e.Kind == "crashed")} engine restart(s)");
            }

            if (file.Pages.Any(p => p.Retries > 0))
            {
                notes.Add("retried after an engine crash");
            }

            if (file.Pages.Any(p => p.ScaleApplied < p.Scale))
            {
                notes.Add("scale reduced to the pixel budget");
            }

            if (file.Reference is { } reference and not "Ok")
            {
                notes.Add("reference " + reference);
            }

            var verdict = file.Verdict == "fail" ? "**FAIL** " : "";
            text.Append(inv, $"| {verdict}`{file.Id}` | {file.Open.Status} | {(file.Open.Status == "Ok" ? file.PageCount.ToString(inv) : "-")} | {rendered} of {file.Pages.Count} | {(double.IsNaN(worst) ? "-" : worst.ToString("0.0000", inv))} | {string.Join("; ", notes)} |\n");
        }

        var problems = report.Files.Where(f => f.Pages.Any(p => p.Status is not "Ok" || p.Ssim is < 0.98)).ToList();
        if (problems.Count > 0)
        {
            text.Append("\n## Pages that did not render Ok or scored under 0.98\n\n| File | Page | Status | SSIM | Changed | Image |\n|---|---|---|---|---|---|\n");
            foreach (var file in problems)
            {
                foreach (var page in file.Pages.Where(p => p.Status is not "Ok" || p.Ssim is < 0.98))
                {
                    text.Append(inv, $"| `{file.Id}` | {page.Key} | {page.Status}{(page.Detail is null ? "" : " - " + page.Detail)} | {Number(page.Ssim)} | {(page.ChangedPercent is { } c ? c.ToString("0.##", inv) + "%" : "-")} | {page.DiffImage ?? "-"} |\n");
                }
            }
        }

        var events = report.Files.SelectMany(f => f.EngineEvents.Select(e => (f.Id, Event: e))).ToList();
        if (events.Count > 0)
        {
            text.Append("\n## Engine events\n\n| At | File | Event | Reason | Exit code | Detail |\n|---|---|---|---|---|---|\n");
            foreach (var (id, e) in events)
            {
                text.Append(inv, $"| {e.AtMs / 1000.0:0.00} s | `{id}` | {e.Kind}{(e.RestoreMs is { } ms ? $" ({ms} ms)" : "")} | {e.Reason} | {e.ExitCode} | {e.Detail} {(e.During is null ? "" : "(during " + e.During + ")")} |\n");
            }
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, string name, string value) => text.Append(CultureInfo.InvariantCulture, $"| {name} | {value} |\n");

    private static string Number(double? value) => value is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "-";

    private static string Where(GateFailure failure) =>
        failure.File is null ? "" : failure.Page is null ? $" `{failure.File}`" : $" `{failure.File}` {failure.Page}";
}
