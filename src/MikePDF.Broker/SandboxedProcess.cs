using System.Text;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker.Native;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MikePDF.Broker;

/// <summary>
/// A process started inside the sandbox together with its job and the text it wrote to its log pipe. Nothing is inherited by such a
/// process, so there is no stdout to capture: the process opens the named log pipe from <c>MIKEPDF_LOG_PIPE</c> and writes to that.
/// Disposing kills it if it is still running.
/// </summary>
public sealed class SandboxedProcess : IDisposable
{
    private const int MaxLogBytes = 64 * 1024;

    private readonly OwnedHandle _process;
    private OwnedHandle? _mainThread;
    private readonly EnginePipeServer _logServer;
    private readonly CancellationTokenSource _logStop = new();
    private readonly Task _logTask;
    private readonly TaskCompletionSource _logConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _log = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ManualResetEvent _exitEvent;
    private int _disposed;

    internal SandboxedProcess(OwnedHandle process, OwnedHandle mainThread, uint processId, EngineJob job, EnginePipeServer logServer)
    {
        _process = process;
        _mainThread = mainThread;
        ProcessId = processId;
        Job = job;
        _logServer = logServer;
        _logTask = Task.Run(ReadLogAsync);

        _exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), _exited, Timeout.Infinite, executeOnlyOnce: true);
    }

    public uint ProcessId { get; }

    public EngineJob Job { get; }

    /// <summary>Completes when the process has exited, however it exited.</summary>
    public Task Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Everything the process wrote to its log pipe so far (capped).</summary>
    public string LogText
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public uint ExitCode
    {
        get
        {
            unsafe
            {
                uint code;
                return PInvoke.GetExitCodeProcess(_process.Value, &code) ? code : uint.MaxValue;
            }
        }
    }

    internal HANDLE Handle => _process.Value;

    /// <summary>Every kernel handle the broker holds for this process: its own, its job and the log pipe. For tests that check none is inheritable.</summary>
    internal IReadOnlyList<nint> BrokerHandles => [NativeHandle, (nint)Job.Handle, _logServer.NativeHandle];

    /// <summary>The process handle as an integer, for tests that need to call APIs the Broker does not wrap.</summary>
    internal nint NativeHandle => _process.DangerousGetHandle();

    /// <summary>
    /// Duplicates a file handle into this process with the access it already has, and returns the value in the process's own handle table.
    /// The caller opened the file read-only and deny-write, so that is all the process can ever do with it. Works while it is still suspended.
    /// </summary>
    internal unsafe ulong DuplicateFileInto(SafeFileHandle file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return DuplicateInto((HANDLE)file.DangerousGetHandle(), 0, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS);
    }

    /// <summary>Duplicates any handle of this process into the child (not inheritable) and returns its value there.</summary>
    internal unsafe ulong DuplicateInto(HANDLE source, uint access, DUPLICATE_HANDLE_OPTIONS options)
    {
        HANDLE remote;
        Win32.ThrowIfFalse(PInvoke.DuplicateHandle(PInvoke.GetCurrentProcess(), source, _process.Value, &remote, access, false, options), "DuplicateHandle(into the sandboxed process)");
        return (ulong)(nint)remote;
    }

    /// <summary>Lets a process that was created suspended run. Harmless if it already does.</summary>
    internal void Resume()
    {
        var thread = Interlocked.Exchange(ref _mainThread, null);
        if (thread is null)
        {
            return;
        }

        var previous = PInvoke.ResumeThread(thread.Value);
        thread.Dispose();
        if (previous == uint.MaxValue)
        {
            throw Win32.LastError("ResumeThread");
        }
    }

    /// <summary>TerminateProcess. Safe to call at any time and more than once.</summary>
    public void Kill(uint exitCode = 0xDEAD)
    {
        if (!_process.IsClosed && !HasExited)
        {
            PInvoke.TerminateProcess(_process.Value, exitCode);
        }
    }

    /// <summary>Waits for the process to exit and for what it logged to arrive.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var finished = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != _exited.Task)
        {
            return false;
        }

        // A process that never opened its log pipe has nothing to wait for; one that did is read to its end.
        if (await Task.WhenAny(_logConnected.Task, Task.Delay(50)).ConfigureAwait(false) != _logConnected.Task)
        {
            await _logStop.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAny(_logTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        Job.Dispose(); // kill on close, then stops the event thread
        _exitWait.Unregister(null);
        _exitEvent.Dispose();
        _logStop.Cancel();
        _logTask.Wait(TimeSpan.FromSeconds(2));
        _logServer.Dispose();
        _logStop.Dispose();
        _mainThread?.Dispose();
        _process.Dispose();
    }

    private async Task ReadLogAsync()
    {
        try
        {
            // Only the process we started may speak on this pipe (the check inside AcceptAsync); anyone else is dropped.
            var stream = await _logServer.AcceptAsync(ProcessId, _logStop.Token).ConfigureAwait(false);
            _logConnected.TrySetResult();
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, _logStop.Token).ConfigureAwait(false)) > 0)
            {
                lock (_log)
                {
                    if (_log.Length < MaxLogBytes)
                    {
                        _log.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or EnginePipeException or System.ComponentModel.Win32Exception)
        {
            // The write end closed abruptly, the process never logged, or a stranger connected: whatever was read is all there is.
        }
    }
}
