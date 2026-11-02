using System.Globalization;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Trace;

/// <summary>One captured launch: its events, the process start time if known, and the UI process's memory if it was sampled.</summary>
public sealed record AppRun(IReadOnlyList<TraceEvent> Events, DateTimeOffset? ProcessStart, double? UiWorkingSetMb, double? UiPrivateMb);

public static class AppMetricCatalog
{
    public static readonly MetricDefinition ColdStart = new("app.cold_start_to_interactive", "ms", "lower",
        "Process creation to the window being shown, laid out and the UI thread idle (events ProcessMain and WindowInteractive).", "startup.cold", 25);

    public static readonly MetricDefinition ColdOpen = new("app.cold_open_to_first_pixel", "ms", "lower",
        "Process creation to the first page content drawn, when the app is started with a document.", "open.cold.1p", 25);

    public static readonly MetricDefinition WarmOpen = new("app.warm_open_to_first_pixel", "ms", "lower",
        "A document opened in the running app: open request to the first page content drawn.", "open.warm.20p", 10);

    public static readonly MetricDefinition ScrollFps = new("app.scroll_fps", "frames/s", "higher",
        "Frames drawn per second during a sustained scroll (at least 2 s of input).", "scroll.fps", 2);

    public static readonly MetricDefinition ScrollFrameP95 = new("app.scroll_frame_interval_p95", "ms", "lower",
        "95th percentile of the time between consecutive draws during a sustained scroll.", null, 2);

    public static readonly MetricDefinition ScrollFrameMax = new("app.scroll_frame_interval_max", "ms", "lower",
        "Longest time between consecutive draws during a sustained scroll.", "scroll.frame_max", 5);

    public static readonly MetricDefinition UiMemory = new("app.ui_working_set", "MB", "lower",
        "Working set of the UI process, idle, one document open.", "memory.ui.20p", 10);

    public static IReadOnlyList<MetricDefinition> All { get; } = [ColdStart, ColdOpen, WarmOpen, ScrollFps, ScrollFrameP95, ScrollFrameMax, UiMemory];
}

public static class AppTraceReport
{
    /// <summary>Turns the runs into the same result file the headless suite writes, so the same comparison and report code apply.</summary>
    public static PerfResults Build(IReadOnlyList<AppRun> runs, string runnerId, string? referenceTier, BudgetFile? budgets)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var analysed = runs.Select(r => (Run: r, Metrics: TraceAnalysis.Analyze(r.Events, r.ProcessStart))).ToList();
        var metrics = new List<MetricResult>
        {
            Measure(AppMetricCatalog.ColdStart, analysed.Select(a => a.Metrics.ColdStartToInteractiveMs), analysed.Count),
            Measure(AppMetricCatalog.ColdOpen, analysed.Select(a => a.Metrics.ColdOpenToFirstPixelMs), analysed.Count),
            Measure(AppMetricCatalog.WarmOpen, analysed.SelectMany(a => a.Metrics.WarmOpensToFirstPixelMs.Select(v => (double?)v)), analysed.Count),
            Measure(AppMetricCatalog.ScrollFps, analysed.Select(a => Pooled(a.Metrics, w => w.Fps, Math.Min)), analysed.Count),
            Measure(AppMetricCatalog.ScrollFrameP95, analysed.Select(a => Pooled(a.Metrics, w => w.P95FrameMs, Math.Max)), analysed.Count),
            Measure(AppMetricCatalog.ScrollFrameMax, analysed.Select(a => Pooled(a.Metrics, w => w.MaxFrameMs, Math.Max)), analysed.Count),
            Measure(AppMetricCatalog.UiMemory, analysed.Select(a => a.Run.UiWorkingSetMb), analysed.Count),
        };
        if (budgets is not null)
        {
            metrics = [.. budgets.Apply(metrics, referenceTier)];
        }

        var notes = new List<string>
        {
            "From the app's ETW markers (provider MikePDF-App). The capture step (wpr and Get-WinEvent in tools/MikePDF.Perf/measure-app.ps1) and the markers inside the running packaged app have NOT been run end to end by the author: only this analysis was tested, on synthetic traces. Treat the first real capture as unverified until the reviewer has run it.",
            "A frame is one draw of the page surface into its swap chain (CanvasVirtualControl RegionsInvalidated). The compositor and scan-out follow up to one refresh later and are not seen; PresentMon (needs administrator rights) sees them.",
            "Sustained scroll figures need a continuous input source for at least 2 s; a hand-driven scroll with pauses is split into separate bursts.",
        };
        foreach (var (run, metricsOfRun) in analysed.Select((a, i) => (i + 1, a.Metrics)))
        {
            foreach (var missing in metricsOfRun.Missing)
            {
                notes.Add($"Run {run}: not measured: {missing}");
            }
        }

        return new PerfResults
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Runner = new RunnerInfo { Id = runnerId, ReferenceTier = referenceTier },
            Machine = MachineProbe.Read(),
            Profile = new ProfileInfo("app-etw", 0, analysed.Count, 0),
            Metrics = metrics,
            Notes = notes,
        };
    }

    /// <summary>One number per run for a scroll metric: the worst of the run's sustained bursts (the lowest frame rate, the longest interval).</summary>
    private static double? Pooled(AppRunMetrics metrics, Func<ScrollWindow, double> pick, Func<double, double, double> worst) =>
        metrics.SustainedScrolls.Count == 0 ? null : metrics.SustainedScrolls.Select(pick).Aggregate(worst);

    private static MetricResult Measure(MetricDefinition definition, IEnumerable<double?> values, int runs)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (present.Count == 0)
        {
            return MetricResults.Skipped(definition, string.Create(CultureInfo.InvariantCulture, $"not measured: the trace of {runs} run(s) has no data for it (see the notes)")) with { Source = "app-etw" };
        }

        return MetricResults.Measured(definition, present) with { Source = "app-etw" };
    }
}
