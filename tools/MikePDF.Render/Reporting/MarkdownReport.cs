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
        Row(text, "SSIM min / 1st percentile / 5th percentile / median / mean", $"{Number(summary.SsimMin)} / {Number(summary.SsimP01)} / {Number(summary.SsimP05)} / {Number(summary.SsimMedian)} / {Number(summary.SsimMean)}");
        Row(text, "Pages without a reference render", summary.ReferenceMissing.ToString(inv));
        Row(text, "Engine time in rendering", $"{summary.TotalRenderMs / 1000.0:0.00} s");

        // The two open-rate bars the project states: §0.3 (production quality) and the M2 exit gate. Only the first is enforced by the gate; the second is reported.
        text.Append("\n## Against the project's bars\n\n| Bar | Required | This run | Met |\n|---|---|---|---|\n");
        Bar(text, "§0.3: files that should open and render without crash, hang or blank page", GateEvaluator.OpenSuccessTarget, summary.OpenSuccessPercent, summary.FilesExpectedToOpen > 0);
        Bar(text, "M2 exit gate: open success on the corpus", 99.0, summary.OpenSuccessPercent, summary.FilesExpectedToOpen > 0);
        Bar(text, $"§0.3: pages at SSIM >= {report.Settings.SsimThreshold:0.###} against the PDFium reference", report.Settings.SsimPassTarget, summary.SsimPassPercent, summary.SsimCompared > 0);
        text.Append(inv, $"\nOpen rate counts {summary.FilesKnownIssue} real file(s) that should open and are known not to (manifest `knownIssue`); the {summary.FilesExpectedNotToOpen} files broken on purpose are left out of it.\n");

        if (summary.SsimCompared > 0)
        {
            text.Append("\n## SSIM distribution\n\n| Range | Pages | Share |\n|---|---|---|\n");
            foreach (var bucket in summary.SsimDistribution)
            {
                text.Append(inv, $"| {bucket.Range} | {bucket.Pages} | {100.0 * bucket.Pages / summary.SsimCompared:0.##}% |\n");
            }
        }

        var tagged = report.Files.SelectMany(f => f.Tags.Select(tag => (Tag: tag, File: f))).GroupBy(x => x.Tag, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (tagged.Count > 0)
        {
            text.Append("\n## By tag\n\n| Tag | Files | Should open | Opened | Pages rendered Ok | Engine crashes or hangs |\n|---|---|---|---|---|---|\n");
            foreach (var group in tagged)
            {
                var files = group.Select(x => x.File).ToList();
                var should = files.Where(Summarizer.ShouldOpen).ToList();
                var pages = files.SelectMany(f => f.Pages).Where(p => p.Status != RenderStatus.OutOfRange).ToList();
                var down = files.Sum(f => f.EngineEvents.Count(e => e.Kind == "crashed"));
                text.Append(inv, $"| {group.Key} | {files.Count} | {should.Count} | {should.Count(f => f.Open.Status == "Ok")} | {pages.Count(p => p.Status == "Ok")} of {pages.Count} | {down} |\n");
            }
        }

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

        var bar = report.Settings.SsimThreshold;
        var problems = report.Files.Where(f => f.Pages.Any(p => p.Status is not "Ok" || p.Ssim < bar)).ToList();
        if (problems.Count > 0)
        {
            text.Append("\n## Pages that did not render Ok or scored under 0.98\n\n| File | Page | Status | SSIM | Changed | Image |\n|---|---|---|---|---|---|\n");
            foreach (var file in problems)
            {
                foreach (var page in file.Pages.Where(p => p.Status is not "Ok" || p.Ssim < bar))
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

    private static void Bar(StringBuilder text, string name, double required, double actual, bool measured) =>
        text.Append(CultureInfo.InvariantCulture, $"| {name} | {required:0.###}% | {(measured ? actual.ToString("0.###", CultureInfo.InvariantCulture) + "%" : "not measured")} | {(!measured ? "-" : actual >= required ? "yes" : "**no**")} |\n");

    private static void Row(StringBuilder text, string name, string value) => text.Append(CultureInfo.InvariantCulture, $"| {name} | {value} |\n");

    private static string Number(double? value) => value is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "-";

    private static string Where(GateFailure failure) =>
        failure.File is null ? "" : failure.Page is null ? $" `{failure.File}`" : $" `{failure.File}` {failure.Page}";
}
