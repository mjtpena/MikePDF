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
        Assert.True(busy > idle, "and busier than it was a moment ago");
    }
}
