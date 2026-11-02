using Microsoft.Win32.SafeHandles;

namespace MikePDF.ShellHost;

/// <summary>
/// A way to ask "has that process really gone?" for a process ShellHost started. A kill (TerminateProcess, a closed job) is a request: the process is gone when its handle is
/// signalled, a little later. ShellHost's cap on engine processes counts processes, not kill requests, so a slot is given back only after the exit (SHELL-12).
/// The handle is opened right after the engine started, while its id still means that process.
/// </summary>
internal sealed class ProcessExit : IDisposable
{
    private readonly ManualResetEvent? _handle;

    private ProcessExit(ManualResetEvent? handle, bool canTell)
    {
        _handle = handle;
        CanTell = canTell;
    }

    /// <summary>False when the process could not be opened for a reason other than being gone: <see cref="WaitAsync"/> then answers true without knowing.</summary>
    public bool CanTell { get; }

    /// <summary>
    /// Watches <paramref name="processId"/>. When the process cannot be opened because it is already gone, the watch reports an exit at once; when it cannot be opened for any
    /// other reason it reports <see cref="CanTell"/> false and an exit at once, which is no worse than not waiting.
    /// </summary>
    public static ProcessExit Watch(uint processId)
    {
        if (processId == 0)
        {
            return new ProcessExit(null, canTell: true);
        }

        var handle = Native.OpenProcess(Native.Synchronize, false, processId);
        if (handle == 0)
        {
            return new ProcessExit(null, canTell: System.Runtime.InteropServices.Marshal.GetLastWin32Error() == Native.ErrorInvalidParameter);
        }

        return new ProcessExit(new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true) }, canTell: true);
    }

    /// <summary>True once the process has exited; false if it is still running after <paramref name="timeout"/>.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout)
    {
        var handle = _handle;
        if (handle is null)
        {
            return Task.FromResult(true);
        }

        try
        {
            if (handle.WaitOne(0))
            {
                return Task.FromResult(true);
            }

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registered = ThreadPool.RegisterWaitForSingleObject(handle, static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), done, timeout, executeOnlyOnce: true);
            _ = done.Task.ContinueWith(_ => registered.Unregister(null), TaskScheduler.Default);
            return done.Task;
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(true);
        }
    }

    public void Dispose() => _handle?.Dispose();
}
