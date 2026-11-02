using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MikePDF.Perf.Results;
using MikePDF.Perf.Setup;
using MikePDF.Perf.Trace;

namespace MikePDF.Perf.Tests;

/// <summary>The computation behind measure-app.ps1, on synthetic traces. No app is launched and no trace is captured.</summary>
public sealed partial class TraceAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private const int Pid = 4242;

    private static TraceEvent At(double ms, int id, int pid = Pid) => new(T0.AddMilliseconds(ms), id, pid);

    /// <summary>Scroll input every 8 ms from <paramref name="from"/> for <paramref name="seconds"/>, a frame every 16.67 ms, and optionally one long gap (a missed frame) at <paramref name="stallAtMs"/>.</summary>
    private static List<TraceEvent> Fling(double from, double seconds, double? stallAtMs = null, double stallMs = 0)
    {
        var events = new List<TraceEvent>();
        for (var t = from; t < from + (seconds * 1000); t += 8)
        {
            events.Add(At(t, AppEventIds.ScrollInput));
        }

        var shift = 0.0;
        for (var t = from; t < from + (seconds * 1000); t += 1000.0 / 60)
        {
            if (stallAtMs is { } s && shift == 0 && t >= s)
            {
                shift = stallMs;
            }

            events.Add(At(t + shift, AppEventIds.FramePresented));
        }

        return events;
    }

    [Fact]
    public void ColdStartIsProcessCreationToTheWindowBeingInteractive()
    {
        var events = new[] { At(120, AppEventIds.ProcessMain), At(480, AppEventIds.WindowInteractive) };
        var withCreation = TraceAnalysis.Analyze(events, T0);
        Assert.Equal(480, withCreation.ColdStartToInteractiveMs!.Value, 3);
        Assert.Equal("process-creation", withCreation.StartReference);

        var withoutCreation = TraceAnalysis.Analyze(events);
        Assert.Equal(360, withoutCreation.ColdStartToInteractiveMs!.Value, 3); // from Main: the runtime's start-up before Main is not counted
        Assert.Equal("main-entered", withoutCreation.StartReference);
    }

    [Fact]
    public void ColdOpenCountsFromTheProcessStartAndTheOpenRequestToo()
    {
        var events = new[]
        {
            At(100, AppEventIds.ProcessMain), At(400, AppEventIds.WindowInteractive),
            At(300, AppEventIds.OpenRequested), At(650, AppEventIds.FirstPagePixels),
        };
        var metrics = TraceAnalysis.Analyze(events, T0);
        Assert.Equal(650, metrics.ColdOpenToFirstPixelMs!.Value, 3);
        Assert.Equal(350, metrics.FirstOpenRequestToFirstPixelMs!.Value, 3);
        Assert.Empty(metrics.WarmOpensToFirstPixelMs);
    }

    [Fact]
    public void WarmOpensAreEachOpenAfterTheFirstMeasuredFromItsOwnRequest()
    {
        var events = new[]
        {
            At(0, AppEventIds.ProcessMain), At(300, AppEventIds.OpenRequested), At(650, AppEventIds.FirstPagePixels),
            At(5000, AppEventIds.OpenRequested), At(5090, AppEventIds.FirstPagePixels),
            At(9000, AppEventIds.OpenRequested), At(9120, AppEventIds.FirstPagePixels),
            At(9500, AppEventIds.FirstPagePixels), // a later draw of the same document is not another first pixel
        };
        var metrics = TraceAnalysis.Analyze(events, T0);
        Assert.Equal([90.0, 120.0], metrics.WarmOpensToFirstPixelMs);
    }

    [Fact]
    public void AnOpenThatNeverDrewIsReportedAsMissingNotAsZero()
    {
        var metrics = TraceAnalysis.Analyze([At(0, AppEventIds.ProcessMain), At(300, AppEventIds.OpenRequested)], T0);
        Assert.Null(metrics.ColdOpenToFirstPixelMs);
        Assert.Null(metrics.FirstOpenRequestToFirstPixelMs);
        Assert.Contains(metrics.Missing, m => m.Contains("never drew", StringComparison.Ordinal));
    }

    [Fact]
    public void EventsOfAnotherProcessAreIgnored()
    {
        var events = new[]
        {
            At(0, AppEventIds.ProcessMain), At(400, AppEventIds.WindowInteractive),
            At(100, AppEventIds.WindowInteractive, pid: 999), // a second MikePDF launch that redirected and exited
        };
        Assert.Equal(400, TraceAnalysis.Analyze(events, T0).ColdStartToInteractiveMs!.Value, 3);
    }

    [Fact]
    public void ASmoothTenSecondFlingGivesSixtyFramesPerSecondAndNoFrameOverFiftyMilliseconds()
    {
        var events = Fling(1000, 10);
        events.Add(At(0, AppEventIds.ProcessMain));
        var metrics = TraceAnalysis.Analyze(events, T0);
        var window = Assert.Single(metrics.SustainedScrolls);
        Assert.InRange(window.DurationMs, 9900, 10100);
        Assert.InRange(window.Fps, 59.5, 60.5);
        Assert.InRange(window.MaxFrameMs, 16.6, 16.7);
        Assert.InRange(window.P95FrameMs, 16.6, 16.7);
        Assert.Equal(0, window.FramesOver50Ms);
        Assert.InRange(window.MaxInputGapMs, 7.9, 8.1);
    }

    [Fact]
    public void OneMissedFrameIsFoundAndCounted()
    {
        var events = Fling(1000, 10, stallAtMs: 5000, stallMs: 70);
        var window = Assert.Single(TraceAnalysis.Analyze(events).SustainedScrolls);
        Assert.InRange(window.MaxFrameMs, 86, 87); // 16.67 ms of normal spacing plus the 70 ms stall
        Assert.Equal(1, window.FramesOver50Ms);
        Assert.True(window.Fps < 60);
    }

    [Fact]
    public void AShortScrollIsNotMistakenForASustainedOne()
    {
        var events = Fling(0, 1);
        var metrics = TraceAnalysis.Analyze(events);
        Assert.Empty(metrics.SustainedScrolls);
        Assert.Contains(metrics.Missing, m => m.Contains("scroll", StringComparison.Ordinal) && m.Contains("longest was", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoBurstsMoreThanTheGapApartAreTwoWindows()
    {
        var events = Fling(0, 3);
        events.AddRange(Fling(5000, 4));
        var metrics = TraceAnalysis.Analyze(events);
        Assert.Equal(2, metrics.SustainedScrolls.Count);
        Assert.InRange(metrics.SustainedScrolls[0].DurationMs, 2900, 3100);
        Assert.InRange(metrics.SustainedScrolls[1].DurationMs, 3900, 4100);
    }

    [Fact]
    public void FramesAfterTheLastInputAreThePageSettlingAndAreNotScrollFrames()
    {
        var events = Fling(0, 3);
        events.Add(At(3300, AppEventIds.FramePresented)); // a repaint 300 ms after the fling ended: a 300 ms "interval" that is nobody's stall
        var window = Assert.Single(TraceAnalysis.Analyze(events).SustainedScrolls);
        Assert.InRange(window.MaxFrameMs, 16.6, 16.7);
    }

    [Fact]
    public void NothingScrolledMeansNotMeasured()
    {
        var metrics = TraceAnalysis.Analyze([At(0, AppEventIds.ProcessMain), At(400, AppEventIds.WindowInteractive)], T0);
        Assert.Empty(metrics.SustainedScrolls);
        Assert.Contains(metrics.Missing, m => m.Contains("no ScrollInput", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingWindowEventLeavesTheColdStartUnmeasuredWithAReason()
    {
        var metrics = TraceAnalysis.Analyze([At(0, AppEventIds.ProcessMain)], T0);
        Assert.Null(metrics.ColdStartToInteractiveMs);
        Assert.Contains(metrics.Missing, m => m.Contains("WindowInteractive", StringComparison.Ordinal));
        Assert.Contains(TraceAnalysis.Analyze([At(5, AppEventIds.WindowInteractive)]).Missing, m => m.Contains("neither", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCsvTheScriptWritesIsReadBackExactly()
    {
        const string csv = "TimeUtc,EventId,ProcessId\r\n2026-11-02T09:00:00.1234567Z,1,4242\r\n2026-11-02T09:00:00.5000000Z,2,4242\r\n\r\n";
        var events = TraceAnalysis.ParseCsv(csv);
        Assert.Equal(2, events.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567), events[0].Time);
        Assert.Equal((AppEventIds.WindowInteractive, 4242), (events[1].Id, events[1].ProcessId));
        Assert.Throws<FormatException>(() => TraceAnalysis.ParseCsv("TimeUtc,EventId,ProcessId\nyesterday,1,2"));
        Assert.Throws<FormatException>(() => TraceAnalysis.ParseCsv("2026-11-02T09:00:00Z,1"));
    }

    [Fact]
    public void TheReportMarksAMetricWithNoDataAsSkippedWithTheReasonAndTheRestAsMeasured()
    {
        var events = new List<TraceEvent> { At(0, AppEventIds.ProcessMain), At(500, AppEventIds.WindowInteractive) };
        var results = AppTraceReport.Build([new AppRun(events, T0, 150, 120), new AppRun(events, T0, 170, 130)], "dev", referenceTier: null, budgets: null);
        var cold = results.Find("app.cold_start_to_interactive")!;
        Assert.Equal(MetricStatus.Measured, cold.Status);
        Assert.Equal(500, cold.Median);
        Assert.Equal(2, cold.N);
        Assert.Equal("app-etw", cold.Source);
        Assert.Equal(160, results.Find("app.ui_working_set")!.Median);

        var scroll = results.Find("app.scroll_fps")!;
        Assert.Equal(MetricStatus.Skipped, scroll.Status);
        Assert.Contains("not measured", scroll.Reason, StringComparison.Ordinal);
        Assert.Contains(results.Notes, n => n.Contains("NOT been run end to end", StringComparison.Ordinal)); // the report says what has not been verified
        Assert.Contains(results.Notes, n => n.StartsWith("Run 1: not measured", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReportTakesTheWorstBurstOfARun()
    {
        var events = Fling(0, 3);
        events.AddRange(Fling(6000, 3, stallAtMs: 7000, stallMs: 60));
        var results = AppTraceReport.Build([new AppRun(events, null, null, null)], "dev", null, null);
        Assert.InRange(results.Find("app.scroll_frame_interval_max")!.Median!.Value, 76, 77);
        Assert.True(results.Find("app.scroll_fps")!.Median < 60);
    }

    [Fact]
    public void TheAppDeclaresExactlyTheEventsTheAnalysisReads()
    {
        var root = EngineWorkspace.FindRepositoryRoot()!;
        var source = File.ReadAllText(Path.Combine(root, "src", "MikePDF.App", "Diagnostics", "AppTrace.cs"));
        var declared = EventDeclaration().Matches(source).ToDictionary(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m => m.Groups[2].Value);
        var read = typeof(AppEventIds).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => (int)f.GetRawConstantValue()!, f => f.Name);
        Assert.Equal(read.OrderBy(p => p.Key), declared.OrderBy(p => p.Key));

        Assert.Contains($"Name = \"{AppEventIds.ProviderName}\"", source, StringComparison.Ordinal);
        Assert.Contains($"Guid = \"{AppEventIds.ProviderGuid}\"", source, StringComparison.Ordinal);
        // The capture script enables the provider by the same GUID.
        var script = File.ReadAllText(Path.Combine(root, "tools", "MikePDF.Perf", "measure-app.ps1"));
        Assert.Contains($"'{AppEventIds.ProviderGuid}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellCallsEachMarkerFromTheOneLineItWasMeantFor()
    {
        var app = Path.Combine(EngineWorkspace.FindRepositoryRoot()!, "src", "MikePDF.App");
        string Source(string relative) => File.ReadAllText(Path.Combine(app, relative));
        Assert.Contains("AppTrace.ProcessMain();", Source("Program.cs"), StringComparison.Ordinal);
        Assert.Contains("AppTrace.WindowLoaded(DispatcherQueue);", Source("MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("AppTrace.OpenRequested();", Source(Path.Combine("Services", "AppHost.cs")), StringComparison.Ordinal); // every open goes through the one place that decides where it opens
        Assert.Contains("AppTrace.ScrollInput();", Source(Path.Combine("Services", "DocumentSession.cs")), StringComparison.Ordinal);
        var presenter = Source(Path.Combine("Views", "Win2DTilePresenter.cs"));
        Assert.Contains("AppTrace.FrameBegin();", presenter, StringComparison.Ordinal);
        Assert.Contains("AppTrace.FrameEnd(traceStart, frame);", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScriptComputesTheFiguresFromEventFilesWithoutCapturingAnything()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-script-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var events = new List<TraceEvent> { At(100, AppEventIds.ProcessMain), At(520, AppEventIds.WindowInteractive), At(300, AppEventIds.OpenRequested), At(740, AppEventIds.FirstPagePixels) };
            events.AddRange(Fling(2000, 10));
            var csv = new StringBuilder("TimeUtc,EventId,ProcessId\r\n");
            foreach (var e in events.OrderBy(e => e.Time))
            {
                csv.Append(e.Time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(',').Append(e.Id).Append(',').Append(e.ProcessId).Append("\r\n");
            }

            var file = Path.Combine(directory, "run1.csv");
            await File.WriteAllTextAsync(file, csv.ToString());

            var root = EngineWorkspace.FindRepositoryRoot()!;
            var tool = Directory.EnumerateFiles(Path.Combine(root, "tools", "MikePDF.Perf", "bin"), "MikePDF.Perf.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).First();
            var output = Path.Combine(directory, "out");
            var start = T0.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            var run = await RunPwshAsync(Path.Combine(root, "tools", "MikePDF.Perf", "measure-app.ps1"), "-EventsCsv", file, "-ProcessStart", start, "-PerfTool", tool, "-OutDir", output, "-RunnerId", "synthetic");
            Assert.True(run.ExitCode == 0, run.Output);

            var results = ResultsJson.Load(Path.Combine(output, "app-results.json"));
            Assert.Equal("synthetic", results.Runner.Id);
            Assert.Equal(520, results.Find("app.cold_start_to_interactive")!.Median);
            Assert.Equal(740, results.Find("app.cold_open_to_first_pixel")!.Median);
            Assert.InRange(results.Find("app.scroll_fps")!.Median!.Value, 59.5, 60.5);
            Assert.Equal(MetricStatus.Skipped, results.Find("app.ui_working_set")!.Status);
            Assert.True(results.Find("app.cold_start_to_interactive")!.Budget is { Within: true, Target: 600, ReferenceHardware: false }); // 520 ms against the 600 ms R1 target, on a trace that proves nothing about hardware
            Assert.True(File.Exists(Path.Combine(output, "app-results.md")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPwshAsync(string script, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("pwsh.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return (process.ExitCode, await stdout + await stderr);
    }

    [GeneratedRegex(@"\[Event\((\d+),[^\]]*\]\s*public void (\w+)\(", RegexOptions.None, 5000)]
    private static partial Regex EventDeclaration();
}
