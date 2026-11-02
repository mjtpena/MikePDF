using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Tests;

public sealed class SystemLoadTests
{
    [Fact]
    public void BusyPercentIsTheShareOfNonIdleTimeBetweenTwoSnapshots()
    {
        Assert.Equal(50, SystemLoad.BusyPercent(new SystemLoad.Snapshot(100, 0), new SystemLoad.Snapshot(150, 50)));
        Assert.Equal(100, SystemLoad.BusyPercent(new SystemLoad.Snapshot(100, 0), new SystemLoad.Snapshot(100, 400)));
        Assert.Equal(0, SystemLoad.BusyPercent(new SystemLoad.Snapshot(100, 7), new SystemLoad.Snapshot(300, 7)));
        Assert.Equal(12.5, SystemLoad.BusyPercent(new SystemLoad.Snapshot(0, 0), new SystemLoad.Snapshot(700, 100)));
    }

    [Fact]
    public void NoElapsedTimeOrNoSnapshotMeansNoAnswerNotZero()
    {
        Assert.Null(SystemLoad.BusyPercent(new SystemLoad.Snapshot(5, 5), new SystemLoad.Snapshot(5, 5)));
        Assert.Null(SystemLoad.BusyPercent(null, new SystemLoad.Snapshot(5, 5)));
        Assert.Null(SystemLoad.BusyPercent(new SystemLoad.Snapshot(5, 5), null));
    }

    private static Func<Task<double?>> Script(params double?[] readings)
    {
        var next = 0;
        return () => Task.FromResult(readings[Math.Min(next++, readings.Length - 1)]);
    }

    [Fact]
    public async Task WaitingForQuietStopsAtTheFirstReadingUnderTheThreshold()
    {
        var (busy, waited) = await SystemLoad.WaitForQuietAsync(Script(80, 60, 10, 90), TimeSpan.FromSeconds(30), 15, TimeSpan.FromMilliseconds(5));
        Assert.Equal(10, busy);
        Assert.True(waited < TimeSpan.FromSeconds(10));

        var (immediately, _) = await SystemLoad.WaitForQuietAsync(Script(3), TimeSpan.FromSeconds(30), 15, TimeSpan.FromMilliseconds(5));
        Assert.Equal(3, immediately);
    }

    [Fact]
    public async Task WaitingForQuietGivesUpAfterTheMaximumAndReportsHowBusyItStillWas()
    {
        var (busy, waited) = await SystemLoad.WaitForQuietAsync(Script(95), TimeSpan.FromMilliseconds(200), 15, TimeSpan.FromMilliseconds(20));
        Assert.Equal(95, busy);
        Assert.InRange(waited.TotalMilliseconds, 200, 5000);

        var (none, _) = await SystemLoad.WaitForQuietAsync(Script((double?)null), TimeSpan.FromSeconds(30), 15, TimeSpan.FromMilliseconds(5));
        Assert.Null(none); // no counter: do not wait for nothing
    }

    [Fact]
    public async Task TheRealCounterGivesAPercentageAndRisesWhenEveryCoreIsKeptBusy()
    {
        var idle = await SystemLoad.MeasureAsync(TimeSpan.FromMilliseconds(300));
        Assert.InRange(idle!.Value, 0, 100);

        using var stop = new CancellationTokenSource();
        var spinners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Thread.SpinWait(1000);
            }
        })).ToArray();
        var busy = await SystemLoad.MeasureAsync(TimeSpan.FromMilliseconds(500));
        await stop.CancelAsync();
        await Task.WhenAll(spinners);

        Assert.True(busy > 80, $"with a thread spinning on every logical processor the machine should be near 100% busy, not {busy}%");
        // Not compared with `idle`: on a machine other programs already saturate, both readings are near 100 and either may be the larger.
    }
}
