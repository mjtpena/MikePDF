// Performance markers for the app, written to Event Tracing for Windows (ETW) and read by tools/MikePDF.Perf/measure-app.ps1 (master prompt 11).
// Nothing is recorded, stored or sent unless a local trace session is listening for this provider, which only an administrator can start, and
// then the events stay in the trace file on this machine. No payload names a document, a path or a person. This is not usage reporting.
global using MikePDF.App.Diagnostics;

using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.UI.Dispatching;
using MikePDF.Core.Tiles;

namespace MikePDF.App.Diagnostics;

/// <summary>
/// The provider "MikePDF-App" ({6F1B4A44-3D0E-4C6B-9B64-0D5A1D1F7A21}). The event ids are read by <c>MikePDF.Perf.Trace.TraceAnalysis</c>; a test
/// keeps the two lists equal.
/// </summary>
[EventSource(Name = "MikePDF-App", Guid = "6f1b4a44-3d0e-4c6b-9b64-0d5a1d1f7a21")]
internal sealed class AppEventSource : EventSource
{
    public static readonly AppEventSource Log = new();

    /// <summary>The first line of Main: the process is running our code.</summary>
    [Event(1, Level = EventLevel.Informational)]
    public void ProcessMain() => WriteEvent(1);

    /// <summary>The window is shown, laid out and the UI thread has gone idle: the person can use it.</summary>
    [Event(2, Level = EventLevel.Informational)]
    public void WindowInteractive() => WriteEvent(2);

    /// <summary>A document was asked for (command line, picker, drop, recent list). <paramref name="sequence"/> counts them from 1 in this process.</summary>
    [Event(3, Level = EventLevel.Informational)]
    public void OpenRequested(int sequence) => WriteEvent(3, sequence);

    /// <summary>The first draw that put page content (a placeholder or a tile) on the surface after <see cref="OpenRequested"/> number <paramref name="sequence"/>.</summary>
    [Event(4, Level = EventLevel.Informational)]
    public void FirstPagePixels(int sequence) => WriteEvent(4, sequence);

    /// <summary>A scroll step arrived (wheel, touch, keyboard, scroll bar).</summary>
    [Event(5, Level = EventLevel.Verbose)]
    public void ScrollInput() => WriteEvent(5);

    /// <summary>One draw of the page surface finished; <paramref name="drawMicroseconds"/> is how long the UI thread spent in it.</summary>
    [Event(6, Level = EventLevel.Verbose)]
    public void FramePresented(long drawMicroseconds) => WriteEvent(6, drawMicroseconds);
}

/// <summary>The one-line calls the shell makes. Each is a no-op unless a trace session is listening.</summary>
internal static class AppTrace
{
    private static int _openSequence;
    private static int _awaitingFirstPixels;

    public static void ProcessMain() => AppEventSource.Log.ProcessMain();

    /// <summary>Call from the window's Loaded event; the marker is written when the UI thread next has nothing to do.</summary>
    public static void WindowLoaded(DispatcherQueue queue)
    {
        if (AppEventSource.Log.IsEnabled())
        {
            queue.TryEnqueue(DispatcherQueuePriority.Low, AppEventSource.Log.WindowInteractive);
        }
    }

    public static void OpenRequested()
    {
        if (AppEventSource.Log.IsEnabled())
        {
            var sequence = Interlocked.Increment(ref _openSequence);
            Volatile.Write(ref _awaitingFirstPixels, sequence);
            AppEventSource.Log.OpenRequested(sequence);
        }
    }

    public static void ScrollInput() => AppEventSource.Log.ScrollInput();

    public static long FrameBegin() => Stopwatch.GetTimestamp();

    /// <summary>Call when a draw of the page surface is finished, with the frame that was drawn.</summary>
    public static void FrameEnd(long begin, FrameComposition? frame)
    {
        if (!AppEventSource.Log.IsEnabled())
        {
            return;
        }

        AppEventSource.Log.FramePresented((long)Stopwatch.GetElapsedTime(begin).TotalMicroseconds);
        if (frame is not null && frame.Pages.Any(p => p.Layers.Count > 0) && Interlocked.Exchange(ref _awaitingFirstPixels, 0) is var sequence and > 0)
        {
            AppEventSource.Log.FirstPagePixels(sequence);
        }
    }
}
