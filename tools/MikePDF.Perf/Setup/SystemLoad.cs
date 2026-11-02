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

    /// <summary>Samples the machine for <paramref name="window"/> and returns the busy percentage; call before starting any work of your own.</summary>
    public static async Task<double?> MeasureAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        var start = Take();
        await Task.Delay(window, cancellationToken);
        return BusyPercent(start, Take());
    }
}
