using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using MikePDF.Broker;
using MikePDF.Protocol;
using MikePDF.Render.Cli;
using MikePDF.Render.Engine;
using MikePDF.Render.Imaging;
using MikePDF.Render.Reference;
using MikePDF.Render.Reporting;

namespace MikePDF.Render.Runner;

/// <summary>One file to render and what is known about it beforehand.</summary>
internal sealed record WorkItem(string Id, string Path, string? Password, ExpectedOutcome? Expected, PageSpec Pages, IReadOnlyList<string> Tags);

/// <summary>
/// A run: every file through the sandboxed engine, page by page and tile by tile, each page compared with the reference render, everything recorded in a
/// <see cref="RunReport"/>. Nothing a file does stops the run: an engine that dies or hangs is restarted by the supervisor and the page is tried
/// again (<see cref="RenderOptions.Retries"/>), a file that keeps killing it is abandoned after <see cref="MaxConsecutiveFailures"/> pages, a supervisor that
/// gives up is replaced, and a file that outlives its time limit is cut off. The crash is still a finding: it fails the gate unless the manifest tolerates it.
/// </summary>
internal sealed class RenderRun
{
    private const int MaxConsecutiveFailures = 3;

    private readonly RenderOptions _options;
    private readonly SandboxedEngineHost _host;
    private readonly ReferenceRunner? _reference;
    private readonly string _outDirectory;
    private readonly Stopwatch _clock;
    private readonly Action<string> _log;

    public RenderRun(RenderOptions options, SandboxedEngineHost host, ReferenceRunner? reference, string outDirectory, Stopwatch clock, Action<string> log)
    {
        _options = options;
        _host = host;
        _reference = reference;
        _outDirectory = outDirectory;
        _clock = clock;
        _log = log;
    }

    public static RunSettings SettingsFor(RenderOptions options, string? baselinePath) => new()
    {
        Tier = options.Tier,
        Manifest = options.Tier is null ? null : options.ManifestPath,
        Scales = options.Scales,
        Pages = options.Pages.Text,
        Rotation = options.Rotation * 90,
        TileDeadlineMs = (long)options.TileDeadline.TotalMilliseconds,
        OpenDeadlineMs = (long)options.OpenDeadline.TotalMilliseconds,
        FileTimeoutMs = (long)options.FileTimeout.TotalMilliseconds,
        MaxPixels = options.MaxPixels,
        Reference = options.Reference,
        SsimThreshold = options.SsimThreshold,
        SsimPassTarget = options.SsimTarget,
        Baseline = baselinePath,
    };

    public static ToolInfo ToolInfo() => new(
        "mikepdf-render",
        typeof(RenderRun).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
        ProtocolInfo.SchemaVersion);

    public static MachineInfo Machine(string? pdfiumVersion) => new(RuntimeInformation.OSDescription, Locations.Architecture, RuntimeInformation.FrameworkDescription, pdfiumVersion);

    /// <summary>Renders every item. Returns the file reports (the caller adds the summary and the gate). <paramref name="cancellationToken"/> ends the run early, keeping what was done.</summary>
    public async Task<List<FileReport>> RunAsync(IReadOnlyList<WorkItem> items, CancellationToken cancellationToken)
    {
        var reports = new List<FileReport>();
        for (var i = 0; i < items.Count && !cancellationToken.IsCancellationRequested; i++)
        {
            var report = await RenderFileAsync(items[i], cancellationToken).ConfigureAwait(false);
            reports.Add(report);
            if (!_options.Quiet)
            {
                _log(Progress(i + 1, items.Count, report));
            }
        }

        return reports;
    }

    private static string Progress(int number, int total, FileReport report)
    {
        var rendered = report.Pages.Count(p => p.Status == "Ok");
        var worst = report.Pages.Where(p => p.Ssim is not null).Select(p => p.Ssim!.Value).DefaultIfEmpty(double.NaN).Min();
        var restarts = report.EngineEvents.Count(e => e.Kind == "crashed");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{number,3}/{total}] open {report.Open.Status,-16} {(report.Open.Status == "Ok" ? $"{rendered}/{report.Pages.Count} pages" : "-"),-13} {(double.IsNaN(worst) ? "" : $"ssim>={worst:0.0000}"),-14} {(restarts > 0 ? $"[{restarts} engine restart(s)] " : "")}{report.Id}");
    }

    private async Task<FileReport> RenderFileAsync(WorkItem item, CancellationToken runToken)
    {
        var file = new FileReport
        {
            Id = item.Id,
            Path = item.Path,
            SizeBytes = new FileInfo(item.Path).Length,
            Tags = item.Tags,
            Expected = item.Expected,
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        limit.CancelAfter(_options.FileTimeout);
        _host.SetContext("open");
        try
        {
            await OpenAndRenderAsync(item, file, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!runToken.IsCancellationRequested)
        {
            file.Problems.Add($"The file did not finish within {_options.FileTimeout.TotalSeconds:0} s and was cut off.");
            foreach (var page in file.Pages.Where(p => p.Status == RenderStatus.NotRun))
            {
                page.Status = RenderStatus.FileTimeout;
            }

            if (file.Open.Status == "NotRun")
            {
                file.Open = new OpenReport(RenderStatus.FileTimeout, "The file's time limit ran out while opening.", (long)_options.FileTimeout.TotalMilliseconds);
            }
        }
        catch (EngineStartException e)
        {
            file.Open = new OpenReport(nameof(Status.EngineCrashed), "No sandboxed engine could be started: " + e.Message, 0);
        }
        finally
        {
            _host.SetContext(null);
            file.EngineEvents.AddRange(_host.DrainEvents());
        }

        return file;
    }

    private async Task OpenAndRenderAsync(WorkItem item, FileReport file, CancellationToken cancellationToken)
    {
        var password = item.Password;
        var open = Stopwatch.StartNew();
        EngineResult<SupervisedDocument> opened = default;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var supervisor = await _host.GetSupervisorAsync(cancellationToken).ConfigureAwait(false);
            opened = await supervisor.OpenDocumentAsync(item.Path, password, cancellationToken).ConfigureAwait(false);
            if (opened.Status != Status.EngineCrashed)
            {
                break;
            }
        }

        file.Open = new OpenReport(opened.Status.ToString(), opened.Detail, open.ElapsedMilliseconds);
        if (!opened.IsOk || opened.Value is null)
        {
            if (_reference is not null)
            {
                // Does PDFium, outside the sandbox, see the same? A file the reference opens and the engine does not is a finding.
                using var probe = await _reference.RenderAsync(item.Path, password, 0, [(1, 1f)], TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                file.Reference = probe.Outcome != "Ok" ? probe.Outcome : probe.OpenStatus == "Ok" ? "OpenedByReference" : "Ok";
            }

            return;
        }

        await using var document = opened.Value;
        file.PageCount = document.PageCount;
        await RenderPagesAsync(item, file, document, password, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenderPagesAsync(WorkItem item, FileReport file, SupervisedDocument document, string? password, CancellationToken cancellationToken)
    {
        var pages = item.Pages.Resolve((int)document.PageCount, out var outside);
        foreach (var page in outside)
        {
            file.Pages.Add(new PageReport { Page = page, Scale = _options.Scales[0], ScaleApplied = _options.Scales[0], Status = RenderStatus.OutOfRange, Detail = $"The document has {document.PageCount} pages." });
        }

        // Plan every page first: the reference renderer needs the same sizes, in one process for the whole file.
        var planned = new List<(PageReport Report, PagePlan Plan)>();
        foreach (var page in pages)
        {
            _host.SetContext($"sizes of page {page}");
            var size = await document.GetPageSizesAsync((uint)page - 1, 1, cancellationToken).ConfigureAwait(false);
            foreach (var scale in _options.Scales)
            {
                var report = new PageReport { Page = page, Scale = scale, ScaleApplied = scale };
                file.Pages.Add(report);
                if (size.Value is not { Widths.Length: 1 } sizes)
                {
                    report.Status = size.Status.ToString();
                    report.Detail = size.Detail ?? "The page size could not be read.";
                    continue;
                }

                var plan = PagePlan.For(sizes.Widths[0], sizes.Heights[0], scale, _options.Rotation, _options.MaxPixels);
                report.ScaleApplied = plan.Scale;
                report.Width = plan.Width;
                report.Height = plan.Height;
                planned.Add((report, plan));
            }
        }

        ReferenceResult? reference = null;
        try
        {
            if (_reference is not null && planned.Count > 0)
            {
                _host.SetContext("reference");
                var timeout = TimeSpan.FromSeconds(Math.Min(_options.FileTimeout.TotalSeconds, 60 + (planned.Count * 5.0)));
                reference = await _reference.RenderAsync(item.Path, password, _options.Rotation, [.. planned.Select(p => (p.Report.Page, p.Plan.Scale))], timeout, cancellationToken).ConfigureAwait(false);
                file.Reference = reference.Outcome != "Ok" ? reference.Outcome : reference.OpenStatus == "Ok" ? "Ok" : "OpenFailed:" + reference.OpenStatus;
            }

            var consecutiveFailures = 0;
            for (var job = 0; job < planned.Count; job++)
            {
                var (report, plan) = planned[job];
                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    report.Status = RenderStatus.Skipped;
                    report.Detail = $"Not tried: {MaxConsecutiveFailures} pages in a row failed.";
                    continue;
                }

                _host.SetContext($"page {report.Key}");
                var image = await RenderWithRetriesAsync(document, report, plan, cancellationToken).ConfigureAwait(false);
                consecutiveFailures = report.Status is "Ok" or "PartiallyRendered" ? 0 : consecutiveFailures + 1;
                if (image is not null)
                {
                    Compare(item, file, report, image, reference, job);
                }
            }
        }
        finally
        {
            reference?.Dispose();
        }
    }

    private async Task<BgraImage?> RenderWithRetriesAsync(SupervisedDocument document, PageReport report, PagePlan plan, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        PageRender result;
        var attempt = 0;
        while (true)
        {
            result = await EnginePageRenderer.RenderAsync(document, (uint)report.Page - 1, plan, _options.Rotation, _options.TileDeadline, cancellationToken).ConfigureAwait(false);
            report.Tiles += result.Tiles;
            if (result.Status == nameof(Status.Cancelled) && cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.Image is not null || result.Status != nameof(Status.EngineCrashed) || attempt >= _options.Retries)
            {
                break;
            }

            attempt++;
            report.Retries = attempt;
        }

        report.Ms = clock.ElapsedMilliseconds;
        report.Status = result.Status;
        report.Detail = result.Detail;
        return result.Image;
    }

    private void Compare(WorkItem item, FileReport file, PageReport report, BgraImage image, ReferenceResult? reference, int job)
    {
        if (_options.WritePng)
        {
            report.Png = SavePicture("png", item.Id, report, image);
        }

        if (reference is null)
        {
            report.ReferenceStatus = "Disabled";
            return;
        }

        var expected = reference.Pages.GetValueOrDefault(job);
        var pixels = reference.Load(job);
        if (pixels is null)
        {
            report.ReferenceStatus = "Missing";
            report.Detail = AppendNote(report.Detail, expected is null ? $"No reference render ({file.Reference})." : $"No reference render ({expected.Status}: {expected.Detail}).");
            return;
        }

        report.ReferenceStatus = expected?.Status ?? "Ok";
        var comparison = Ssim.Compare(pixels, image);
        report.Ssim = Math.Round(comparison.Ssim, 6);
        report.ChangedPercent = Math.Round(comparison.ChangedPercent, 3);
        if (comparison.SizeMismatch)
        {
            report.Detail = AppendNote(report.Detail, $"Engine render is {image.Width} x {image.Height}, reference is {pixels.Width} x {pixels.Height}.");
        }

        if (report.Ssim < _options.SsimThreshold)
        {
            report.DiffImage = SavePicture("diff", item.Id, report, DiffImage.Compose(pixels, image));
        }
    }

    private string SavePicture(string kind, string id, PageReport report, BgraImage image)
    {
        var relative = Path.Combine(kind, SafeName(id), string.Create(CultureInfo.InvariantCulture, $"p{report.Page:D5}@{report.Scale:0.####}x.png"));
        Png.Save(Path.Combine(_outDirectory, relative), image);
        return relative.Replace('\\', '/');
    }

    internal static string SafeName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = id.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part is "." or ".." ? "_" : string.Concat(part.Select(c => invalid.Contains(c) ? '_' : c)));
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string AppendNote(string? existing, string note) => string.IsNullOrEmpty(existing) ? note : existing + " " + note;
}
