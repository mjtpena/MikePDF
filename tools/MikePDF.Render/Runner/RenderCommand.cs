using System.Diagnostics;
using System.Globalization;
using MikePDF.Broker;
using MikePDF.Render.Cli;
using MikePDF.Render.Corpus;
using MikePDF.Render.Engine;
using MikePDF.Render.Reference;
using MikePDF.Render.Reporting;

namespace MikePDF.Render.Runner;

/// <summary><c>mikepdf-render render</c>: build the list of files, run them, write the report, return the exit code.</summary>
internal static class RenderCommand
{
    public const int Passed = 0;
    public const int GateFailed = 1;
    public const int CouldNotRun = 2;

    private static readonly string[] PictureFolders = ["diff", "png"];

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        RenderOptions options;
        try
        {
            options = RenderOptions.Parse(args);
        }
        catch (FormatException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            await error.WriteLineAsync("Try: mikepdf-render --help").ConfigureAwait(false);
            return CouldNotRun;
        }

        if (options.Help)
        {
            await output.WriteLineAsync(RenderOptions.Usage).ConfigureAwait(false);
            return Passed;
        }

        var clock = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        List<WorkItem> items;
        string? baselinePath;
        SsimBaseline? baseline;
        try
        {
            items = BuildWorkItems(options);
            baselinePath = options.BaselinePath ?? (options.Tier is null ? null : Locations.DefaultBaseline(options.Tier));
            baseline = baselinePath is null ? null : SsimBaseline.Load(baselinePath);
        }
        catch (Exception e) when (e is FormatException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return CouldNotRun;
        }

        if (items.Count == 0)
        {
            await error.WriteLineAsync("No PDF files to render.").ConfigureAwait(false);
            return CouldNotRun;
        }

        var outDirectory = Path.GetFullPath(options.OutDirectory);
        Directory.CreateDirectory(outDirectory);
        // Pictures from an earlier run in the same folder would pass for this run's failures.
        foreach (var stale in PictureFolders.Select(name => Path.Combine(outDirectory, name)).Where(Directory.Exists))
        {
            Directory.Delete(stale, recursive: true);
        }
        var workDirectory = Path.GetFullPath(options.WorkDirectory ?? Locations.DefaultWorkDirectory());

        SandboxedEngineHost host;
        ReferenceRunner? reference = null;
        string? pdfiumVersion = null;
        try
        {
            var engineDirectory = Locations.EngineDirectory(options.EngineDirectory);
            if (options.Reference)
            {
                var pdfium = Locations.PdfiumDll(options.PdfiumDirectory, engineDirectory);
                reference = new ReferenceRunner(Locations.ReferenceExecutable(), pdfium, workDirectory);
                pdfiumVersion = ReadPdfiumVersion(pdfium);
            }

            host = SandboxedEngineHost.Create(engineDirectory, workDirectory, options.OpenDeadline, clock);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return CouldNotRun;
        }

        await using (host.ConfigureAwait(false))
        {
            try
            {
                // Fail fast and loudly if the sandbox cannot be brought up: there is no weaker fallback (ADR-0004 decision 5).
                await host.GetSupervisorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EngineStartException e)
            {
                await error.WriteLineAsync("The sandboxed engine could not be started: " + e.Message).ConfigureAwait(false);
                return CouldNotRun;
            }

            var run = new RenderRun(options, host, reference, outDirectory, line => output.WriteLine(line));
            var files = await run.RunAsync(items, cancellationToken).ConfigureAwait(false);
            var interrupted = cancellationToken.IsCancellationRequested;

            var settings = RenderRun.SettingsFor(options, baselinePath);
            var gate = GateEvaluator.Evaluate(files, settings, baseline);
            if (interrupted)
            {
                gate = gate with { Passed = false, Failures = [.. gate.Failures, new GateFailure("Interrupted", null, null, $"The run was stopped after {files.Count} of {items.Count} files.")] };
            }

            var failedFiles = gate.Failures.Where(f => f.File is not null).Select(f => f.File!).ToHashSet(StringComparer.Ordinal);
            foreach (var file in files)
            {
                file.Verdict = failedFiles.Contains(file.Id) ? "fail" : Summarizer.ExpectedStatus(file) != "Ok" ? "expected-failure" : "pass";
            }

            var report = new RunReport
            {
                Tool = RenderRun.ToolInfo(),
                StartedUtc = started,
                DurationMs = clock.ElapsedMilliseconds,
                Machine = RenderRun.Machine(pdfiumVersion),
                Settings = settings,
                Files = files,
                Summary = Summarizer.Summarize(files, settings),
                Gate = gate,
            };
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "report.json"), ReportJson.Serialize(report), CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "report.md"), MarkdownReport.Write(report), CancellationToken.None).ConfigureAwait(false);
            if (options.WriteBaselinePath is { } baselineOut)
            {
                SsimBaseline.FromReport(files, settings, Locations.Architecture, pdfiumVersion).Save(baselineOut);
            }

            await PrintSummaryAsync(output, report, outDirectory).ConfigureAwait(false);
            return gate.Passed || options.NoGate ? Passed : GateFailed;
        }
    }

    private static Task WriteAsync(TextWriter output, FormattableString line) => output.WriteLineAsync(line.ToString(CultureInfo.InvariantCulture));

    private static async Task PrintSummaryAsync(TextWriter output, RunReport report, string outDirectory)
    {
        var s = report.Summary;
        var inv = CultureInfo.InvariantCulture;
        await output.WriteLineAsync().ConfigureAwait(false);
        await WriteAsync(output, $"{s.Files} files: {s.FilesOpened} of {s.FilesExpectedToOpen} that should open did ({s.OpenSuccessPercent:0.###}%); {s.FilesExpectedNotToOpen} are broken on purpose.").ConfigureAwait(false);
        await WriteAsync(output, $"{s.PagesAttempted} pages: {s.PagesRendered} Ok, {s.PagesPartial} partial, {s.PagesFailed} failed ({s.Timeouts} timeouts). Engine crashes {s.EngineCrashes}, hangs {s.EngineHangs}.").ConfigureAwait(false);
        if (report.Settings.Reference)
        {
            await WriteAsync(output, $"SSIM: {s.SsimAtOrAboveThreshold} of {s.SsimCompared} compared pages at or above {report.Settings.SsimThreshold:0.###} ({s.SsimPassPercent:0.###}%, target {report.Settings.SsimPassTarget:0.###}%); min {s.SsimMin?.ToString("0.0000", inv) ?? "-"}, mean {s.SsimMean?.ToString("0.0000", inv) ?? "-"}; {s.ReferenceMissing} pages had no reference.").ConfigureAwait(false);
        }

        foreach (var failure in report.Gate.Failures.Take(40))
        {
            await output.WriteLineAsync($"FAIL {failure.Kind}{(failure.File is null ? "" : " " + failure.File)}{(failure.Page is null ? "" : " " + failure.Page)}: {failure.Message}").ConfigureAwait(false);
        }

        if (report.Gate.Failures.Count > 40)
        {
            await WriteAsync(output, $"... and {report.Gate.Failures.Count - 40} more (see report.md)").ConfigureAwait(false);
        }

        await WriteAsync(output, $"Gate: {(report.Gate.Passed ? "PASSED" : "FAILED")} ({report.Gate.Failures.Count} failures, {report.Gate.Warnings.Count} warnings). Report: {Path.Combine(outDirectory, "report.md")}").ConfigureAwait(false);
    }

    private static List<WorkItem> BuildWorkItems(RenderOptions options)
    {
        var items = new List<WorkItem>();
        if (options.Tier is { } tier)
        {
            var manifest = CorpusManifest.Load(options.ManifestPath ?? Locations.DefaultManifest());
            var corpus = CorpusStore.Resolve(manifest, tier, Path.GetFullPath(options.CorpusDirectory ?? Locations.DefaultCorpusDirectory()), options.Filter);
            foreach (var item in corpus)
            {
                var pages = item.Entry.Render?.Pages is { } spec ? PageSpec.Parse(spec) : options.Pages;
                items.Add(new WorkItem(
                    item.Entry.Id,
                    item.Path,
                    item.Entry.Expect.Password,
                    new ExpectedOutcome
                    {
                        Status = item.Entry.Expect.Status,
                        Pages = item.Entry.Expect.Pages,
                        AllowsEngineCrash = item.Entry.Expect.AllowsEngineCrash,
                        AllowsPartialRender = item.Entry.Expect.AllowsPartialRender,
                    },
                    pages,
                    item.Entry.Tags));
            }
        }

        foreach (var input in options.Inputs)
        {
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                foreach (var pdf in Directory.EnumerateFiles(full, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new WorkItem(Path.GetRelativePath(full, pdf).Replace('\\', '/'), pdf, options.Password, null, options.Pages, []));
                }
            }
            else if (File.Exists(full))
            {
                items.Add(new WorkItem(Path.GetFileName(full), full, options.Password, null, options.Pages, []));
            }
            else
            {
                throw new FileNotFoundException($"'{input}' is not a file or a folder.");
            }
        }

        // Two files with the same name from different folders must not share a report entry or an image folder.
        foreach (var group in items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var n = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, group.Key, StringComparison.OrdinalIgnoreCase))
                {
                    items[i] = items[i] with { Id = $"{items[i].Id}~{++n}" };
                }
            }
        }

        return items;
    }

    /// <summary>The pinned build's VERSION file sits one folder above <c>bin/pdfium.dll</c>.</summary>
    private static string? ReadPdfiumVersion(string pdfiumDll)
    {
        var version = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pdfiumDll)) ?? "", "VERSION");
        if (!File.Exists(version))
        {
            return null;
        }

        var values = File.ReadAllLines(version).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        return values.TryGetValue("MAJOR", out var major) && values.TryGetValue("MINOR", out var minor) && values.TryGetValue("BUILD", out var build) && values.TryGetValue("PATCH", out var patch)
            ? $"{major}.{minor}.{build}.{patch}"
            : null;
    }
}
