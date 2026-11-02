using System.Globalization;
using MikePDF.Perf.Measurement;

namespace MikePDF.Perf.Trace;

/// <summary>The ids of the app's events (src/MikePDF.App/Diagnostics/AppTrace.cs, provider "MikePDF-App"). A test keeps this list equal to the app's.</summary>
public static class AppEventIds
{
    public const int ProcessMain = 1;
    public const int WindowInteractive = 2;
    public const int OpenRequested = 3;
    public const int FirstPagePixels = 4;
    public const int ScrollInput = 5;
    public const int FramePresented = 6;

    public const string ProviderName = "MikePDF-App";
    public const string ProviderGuid = "6f1b4a44-3d0e-4c6b-9b64-0d5a1d1f7a21";
}

/// <summary>One event read from a trace: when, which, and from which process.</summary>
public readonly record struct TraceEvent(DateTimeOffset Time, int Id, int ProcessId);

/// <summary>The scroll behaviour of one burst of scroll input.</summary>
public sealed record ScrollWindow(double DurationMs, int Inputs, int Frames, double Fps, double MedianFrameMs, double P95FrameMs, double MaxFrameMs, int FramesOver50Ms, double MaxInputGapMs);

/// <summary>What one launch of the app did, from its markers. A null means the trace did not contain what is needed, never a guess.</summary>
public sealed record AppRunMetrics
{
    /// <summary>Process creation (or, without it, the app's Main) to the window being interactive.</summary>
    public double? ColdStartToInteractiveMs { get; init; }

    /// <summary>"process-creation" or "main-entered": which instant the cold figures count from.</summary>
    public string? StartReference { get; init; }

    /// <summary>Process start to the first page pixels of the first document opened in this process.</summary>
    public double? ColdOpenToFirstPixelMs { get; init; }

    /// <summary>Open request to first page pixels, for each document opened after the first one.</summary>
    public IReadOnlyList<double> WarmOpensToFirstPixelMs { get; init; } = [];

    /// <summary>Open request to first page pixels for the first document (without the process start).</summary>
    public double? FirstOpenRequestToFirstPixelMs { get; init; }

    /// <summary>Bursts of at least <see cref="TraceAnalysis.SustainedScrollMs"/> of scroll input, with their frames.</summary>
    public IReadOnlyList<ScrollWindow> SustainedScrolls { get; init; } = [];

    /// <summary>Why a figure is missing.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

public static class TraceAnalysis
{
    /// <summary>Scroll input closer together than this belongs to one burst.</summary>
    public const double ScrollGapMs = 250;

    /// <summary>A burst shorter than this is a nudge, not the sustained scrolling the frame-rate budget is about.</summary>
    public const double SustainedScrollMs = 2000;

    /// <summary>A frame interval over this fails "no frame over 50 ms" (§11).</summary>
    public const double FrameLimitMs = 50;

    public static AppRunMetrics Analyze(IReadOnlyList<TraceEvent> events, DateTimeOffset? processStart = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var missing = new List<string>();
        var ordered = events.OrderBy(e => e.Time).ToList();
        var main = ordered.FirstOrDefault(e => e.Id == AppEventIds.ProcessMain);
        var pid = main.Id == AppEventIds.ProcessMain ? main.ProcessId : ordered.FirstOrDefault().ProcessId;
        ordered = [.. ordered.Where(e => e.ProcessId == pid)];

        DateTimeOffset? start = processStart;
        string? reference = start is null ? null : "process-creation";
        if (start is null && ordered.Any(e => e.Id == AppEventIds.ProcessMain))
        {
            start = ordered.First(e => e.Id == AppEventIds.ProcessMain).Time;
            reference = "main-entered";
        }

        double? coldStart = null;
        var interactive = ordered.Where(e => e.Id == AppEventIds.WindowInteractive).Select(e => (DateTimeOffset?)e.Time).FirstOrDefault();
        if (start is not null && interactive is not null)
        {
            coldStart = (interactive.Value - start.Value).TotalMilliseconds;
        }
        else
        {
            missing.Add(start is null ? "cold start: neither the process start time nor a ProcessMain event is in the trace" : "cold start: no WindowInteractive event");
        }

        var requests = ordered.Where(e => e.Id == AppEventIds.OpenRequested).ToList();
        var pixels = ordered.Where(e => e.Id == AppEventIds.FirstPagePixels).ToList();
        var pairs = new List<double>();
        double? coldOpen = null;
        double? firstRequestToPixel = null;
        for (var i = 0; i < requests.Count; i++)
        {
            var until = i + 1 < requests.Count ? requests[i + 1].Time : DateTimeOffset.MaxValue;
            var pixel = pixels.Where(p => p.Time >= requests[i].Time && p.Time < until).Select(p => (DateTimeOffset?)p.Time).FirstOrDefault();
            if (pixel is null)
            {
                continue;
            }

            var delta = (pixel.Value - requests[i].Time).TotalMilliseconds;
            if (i == 0)
            {
                firstRequestToPixel = delta;
                if (start is not null)
                {
                    coldOpen = (pixel.Value - start.Value).TotalMilliseconds;
                }
            }
            else
            {
                pairs.Add(delta);
            }
        }

        if (requests.Count == 0)
        {
            missing.Add("open: no OpenRequested event (no document was opened)");
        }
        else if (firstRequestToPixel is null)
        {
            missing.Add("open: the first document never drew any page content");
        }

        var scrolls = Scrolls(ordered, missing);
        return new AppRunMetrics
        {
            ColdStartToInteractiveMs = coldStart,
            StartReference = reference,
            ColdOpenToFirstPixelMs = coldOpen,
            WarmOpensToFirstPixelMs = pairs,
            FirstOpenRequestToFirstPixelMs = firstRequestToPixel,
            SustainedScrolls = scrolls,
            Missing = missing,
        };
    }

    private static List<ScrollWindow> Scrolls(List<TraceEvent> events, List<string> missing)
    {
        var inputs = events.Where(e => e.Id == AppEventIds.ScrollInput).Select(e => e.Time).ToList();
        var frames = events.Where(e => e.Id == AppEventIds.FramePresented).Select(e => e.Time).ToList();
        var windows = new List<ScrollWindow>();
        if (inputs.Count == 0)
        {
            missing.Add("scroll: no ScrollInput events (nothing was scrolled while tracing)");
            return windows;
        }

        var burstStart = 0;
        for (var i = 1; i <= inputs.Count; i++)
        {
            if (i < inputs.Count && (inputs[i] - inputs[i - 1]).TotalMilliseconds <= ScrollGapMs)
            {
                continue;
            }

            var first = inputs[burstStart];
            var last = inputs[i - 1];
            var end = last.AddMilliseconds(FrameLimitMs); // the frame answering the last input; frames after that are the page settling, not scrolling
            var duration = (last - first).TotalMilliseconds;
            if (duration >= SustainedScrollMs)
            {
                var inside = frames.Where(f => f >= first && f <= end).ToList();
                windows.Add(Window(inputs.Skip(burstStart).Take(i - burstStart).ToList(), inside, duration));
            }

            burstStart = i;
        }

        if (windows.Count == 0)
        {
            missing.Add($"scroll: no scroll burst of at least {SustainedScrollMs / 1000:0} s (the longest was {LongestBurstMs(inputs):0} ms)");
        }

        return windows;
    }

    private static double LongestBurstMs(List<DateTimeOffset> inputs)
    {
        var longest = 0.0;
        var start = inputs[0];
        for (var i = 1; i <= inputs.Count; i++)
        {
            if (i == inputs.Count || (inputs[i] - inputs[i - 1]).TotalMilliseconds > ScrollGapMs)
            {
                longest = Math.Max(longest, (inputs[i - 1] - start).TotalMilliseconds);
                if (i < inputs.Count)
                {
                    start = inputs[i];
                }
            }
        }

        return longest;
    }

    private static ScrollWindow Window(List<DateTimeOffset> inputs, List<DateTimeOffset> frames, double durationMs)
    {
        var intervals = new List<double>();
        for (var i = 1; i < frames.Count; i++)
        {
            intervals.Add((frames[i] - frames[i - 1]).TotalMilliseconds);
        }

        var maxGap = 0.0;
        for (var i = 1; i < inputs.Count; i++)
        {
            maxGap = Math.Max(maxGap, (inputs[i] - inputs[i - 1]).TotalMilliseconds);
        }

        if (intervals.Count == 0)
        {
            return new ScrollWindow(durationMs, inputs.Count, frames.Count, 0, 0, 0, 0, 0, maxGap);
        }

        var span = (frames[^1] - frames[0]).TotalSeconds;
        var summary = Stats.Summarize(intervals);
        return new ScrollWindow(durationMs, inputs.Count, frames.Count, span > 0 ? intervals.Count / span : 0, summary.Median, summary.P95, summary.Max, intervals.Count(i => i > FrameLimitMs), maxGap);
    }

    // ---- reading the CSV that measure-app.ps1 writes --------------------------------------------------------------------------------

    /// <summary>Reads <c>TimeUtc,EventId,ProcessId</c> rows (ISO 8601 times). Rows that are not for the app's events are kept; the analysis ignores ids it does not know.</summary>
    public static IReadOnlyList<TraceEvent> ParseCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var events = new List<TraceEvent>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith("TimeUtc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cells = line.Split(',').Select(c => c.Trim().Trim('"')).ToArray();
            if (cells.Length < 3
                || !DateTimeOffset.TryParse(cells[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                || !int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                throw new FormatException($"Line {number} is not 'TimeUtc,EventId,ProcessId': {line}");
            }

            events.Add(new TraceEvent(time, id, pid));
        }

        return events;
    }
}
