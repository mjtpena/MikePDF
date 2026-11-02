using System.Runtime.InteropServices;

namespace MikePDF.Perf.Setup;

/// <summary>Whole-machine CPU use, so a result file can say how busy the machine already was. Timings taken while other programs are working are inflated by that.</summary>
public static partial class SystemLoad
{
    public readonly record struct Snapshot(long Idle, long Busy);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static Snapshot? Take() => GetSystemTimes(out var idle, out var kernel, out var user) ? new Snapshot(idle, kernel + user - idle) : null; // kernel time includes idle time

    /// <summary>The share of all logical processors' time spent doing anything but idling between two snapshots, in percent.</summary>
    public static double? BusyPercent(Snapshot? start, Snapshot? end)
    {
        if (start is not { } a || end is not { } b)
        {
            return null;
        }

        var busy = b.Busy - a.Busy;
        var total = busy + (b.Idle - a.Idle);
        return total <= 0 ? null : Math.Round(100.0 * busy / total, 1);
    }

    /// <summary>
    /// Waits for the machine to go quiet before measuring: samples (each sample takes its own time) until a reading is at or under
    /// <paramref name="thresholdPercent"/>, or <paramref name="maxWait"/> has passed. A hosted runner has just finished a build whose compiler servers
    /// are still winding down; measuring straight away would time them. Returns the last reading (null if the counter is unavailable) and how long it took.
    /// </summary>
    public static async Task<(double? Busy, TimeSpan Waited)> WaitForQuietAsync(Func<Task<double?>> sample, TimeSpan maxWait, double thresholdPercent, TimeSpan pause, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var busy = await sample();
            if (busy is null || busy <= thresholdPercent || clock.Elapsed >= maxWait)
            {
                return (busy, clock.Elapsed);
            }

            await Task.Delay(pause, cancellationToken);
        }
    }

    /// <summary>Samples the machine for <paramref name="window"/> and returns the busy percentage; call before starting any work of your own.</summary>
    public static async Task<double?> MeasureAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        var start = Take();
        await Task.Delay(window, cancellationToken);
        return BusyPercent(start, Take());
    }
}
