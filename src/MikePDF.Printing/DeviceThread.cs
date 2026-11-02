using System.Collections.Concurrent;

namespace MikePDF.Printing;

/// <summary>
/// One thread that makes every call on a printer device, in order, so that a device context is only ever used from one thread and never from the UI thread (ADR-0011 decision 6). The job awaits
/// each call; nothing blocks while it waits. Calls queued behind a long one (<c>EndDoc</c>) wait for it.
/// </summary>
internal sealed class DeviceThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public DeviceThread()
    {
        _thread = new Thread(Run) { Name = "MikePDF print device", IsBackground = true };
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        });
        return completion.Task;
    }

    public Task RunAsync(Action work) => RunAsync(() =>
    {
        work();
        return true;
    });

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
