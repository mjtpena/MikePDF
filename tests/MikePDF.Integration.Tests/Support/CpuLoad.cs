namespace MikePDF.Integration.Tests.Support;

/// <summary>
/// A busy thread on every logical core for as long as it lives (the "every core is busy" condition under which an engine once failed to start).
/// The threads run at the priority of an ordinary process and never block, so the scheduler time-slices them against the engine's threads.
/// </summary>
internal sealed class CpuLoad : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread[] _threads;

    private CpuLoad(int threadsPerCore, ThreadPriority priority)
    {
        _threads = new Thread[Environment.ProcessorCount * threadsPerCore];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Spin) { IsBackground = true, Priority = priority, Name = "MikePDF.Test.CpuLoad" };
            _threads[i].Start();
        }
    }

    /// <summary>One busy thread per logical core (two with <paramref name="threadsPerCore"/> = 2).</summary>
    public static CpuLoad OnEveryCore(int threadsPerCore = 1, ThreadPriority priority = ThreadPriority.Normal) => new(threadsPerCore, priority);

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        _stop.Dispose();
    }

    private void Spin()
    {
        var token = _stop.Token;
        long sink = 0;
        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < 100_000; i++)
            {
                sink += i ^ (sink >> 3);
            }
        }

        GC.KeepAlive(sink);
    }
}
