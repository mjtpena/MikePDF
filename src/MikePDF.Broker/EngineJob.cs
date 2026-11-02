using MikePDF.Broker.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace MikePDF.Broker;

public enum EngineJobEventKind
{
    /// <summary>A commit request failed because of the per-process limit. The OS does not kill the process: this message is the only signal (S3).</summary>
    ProcessMemoryLimit,
    ProcessExited,
    ProcessExitedAbnormally,

    /// <summary>The last process left the job.</summary>
    ActiveProcessZero,

    /// <summary>The process tried to create a child and the active-process limit of 1 refused it.</summary>
    ActiveProcessLimit,
}

public readonly record struct EngineJobEvent(EngineJobEventKind Kind, uint ProcessId);

/// <summary>
/// The job object an engine runs in (ADR-0004 decision 3): kill on close, one active process, UI restrictions and a
/// per-process memory limit. A background thread reads the job's completion port and raises <see cref="EventRaised"/>.
/// Handlers run on that thread and must not block or throw.
/// </summary>
public sealed class EngineJob : IDisposable
{
    private const nuint CompletionKey = 0x51;
    private const nuint StopKey = 0xDEAD;

    private readonly OwnedHandle _job;
    private readonly OwnedHandle _port;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _emptied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _memoryLimitExceeded;
    private int _disposed;

    private EngineJob(OwnedHandle job, OwnedHandle port)
    {
        _job = job;
        _port = port;
        _thread = new Thread(PumpEvents) { IsBackground = true, Name = "MikePDF.EngineJob.Events" };
    }

    /// <summary>Raised on the job's own thread for every message that matters to the supervisor.</summary>
    public event Action<EngineJobEvent>? EventRaised;

    /// <summary>True once the job reported that a commit request was refused by the memory limit.</summary>
    public bool MemoryLimitExceeded => Volatile.Read(ref _memoryLimitExceeded) != 0;

    /// <summary>Completes when the job has no process left. Every message sent before that has already been raised.</summary>
    public Task Emptied => _emptied.Task;

    internal HANDLE Handle => _job.Value;

    /// <param name="processMemoryLimitBytes">The per-process commit limit.</param>
    /// <param name="onEvent">Subscribed before the event thread starts, so no message can be missed.</param>
    public static unsafe EngineJob Create(long processMemoryLimitBytes, Action<EngineJobEvent>? onEvent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processMemoryLimitBytes);
        var jobHandle = PInvoke.CreateJobObject(null, default(PCWSTR));
        if (jobHandle.IsNull)
        {
            throw Win32.LastError("CreateJobObject");
        }

        var job = new OwnedHandle(jobHandle);
        OwnedHandle? port = null;
        try
        {
            var limits = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            limits.BasicLimitInformation.ActiveProcessLimit = 1;
            limits.ProcessMemoryLimit = checked((nuint)processMemoryLimitBytes);
            Win32.ThrowIfFalse(
                PInvoke.SetInformationJobObject(job.Value, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)),
                "SetInformationJobObject(limits)");

            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = (JOB_OBJECT_UILIMIT)PInvoke.JOB_OBJECT_UILIMIT_ALL };
            Win32.ThrowIfFalse(
                PInvoke.SetInformationJobObject(job.Value, JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions, &ui, (uint)sizeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)),
                "SetInformationJobObject(ui)");

            var portHandle = PInvoke.CreateIoCompletionPort(Win32.InvalidHandle, default, 0, 1);
            if (portHandle.IsNull)
            {
                throw Win32.LastError("CreateIoCompletionPort");
            }

            port = new OwnedHandle(portHandle);
            var association = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT { CompletionKey = (void*)CompletionKey, CompletionPort = port.Value };
            Win32.ThrowIfFalse(
                PInvoke.SetInformationJobObject(job.Value, JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, &association, (uint)sizeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT)),
                "SetInformationJobObject(completion port)");

            var result = new EngineJob(job, port);
            result.EventRaised += onEvent;
            result._thread.Start();
            return result;
        }
        catch
        {
            port?.Dispose();
            job.Dispose();
            throw;
        }
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _job.Dispose(); // kill on close: anything still alive dies here
        PInvoke.PostQueuedCompletionStatus(_port.Value, 0, StopKey, null);
        _thread.Join(TimeSpan.FromSeconds(2));
        _port.Dispose();
        _emptied.TrySetResult();
    }

    private unsafe void PumpEvents()
    {
        while (true)
        {
            uint message;
            nuint key;
            NativeOverlapped* overlapped;
            if (!PInvoke.GetQueuedCompletionStatus(_port.Value, &message, &key, &overlapped, uint.MaxValue))
            {
                if (overlapped is null)
                {
                    return; // the port is gone
                }

                continue;
            }

            if (key == StopKey)
            {
                return;
            }

            var pid = (uint)(nuint)overlapped;
            switch (message)
            {
                case PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT:
                case PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT:
                    Volatile.Write(ref _memoryLimitExceeded, 1);
                    Raise(EngineJobEventKind.ProcessMemoryLimit, pid);
                    break;
                case PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS:
                    Raise(EngineJobEventKind.ProcessExited, pid);
                    break;
                case PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS:
                    Raise(EngineJobEventKind.ProcessExitedAbnormally, pid);
                    break;
                case PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT:
                    Raise(EngineJobEventKind.ActiveProcessLimit, pid);
                    break;
                case PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO:
                    Raise(EngineJobEventKind.ActiveProcessZero, pid);
                    _emptied.TrySetResult();
                    break;
                default:
                    break;
            }
        }
    }

    private void Raise(EngineJobEventKind kind, uint pid)
    {
        try
        {
            EventRaised?.Invoke(new EngineJobEvent(kind, pid));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A handler that throws must not take the pump down with it; the supervisor's handlers do not throw.
        }
    }
}
