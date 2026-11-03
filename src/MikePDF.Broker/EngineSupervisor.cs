using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;

namespace MikePDF.Broker;

public enum EngineFailureReason
{
    /// <summary>The process exited by itself (a crash, or the engine ended on a protocol error).</summary>
    ProcessExited,

    /// <summary>The job reported that the memory limit was hit.</summary>
    MemoryLimit,

    /// <summary>No heartbeat for the watchdog timeout, or a request overdue.</summary>
    Hung,

    /// <summary>The engine sent something it must never send.</summary>
    ProtocolViolation,

    /// <summary>The pipe broke while the process was still alive.</summary>
    PipeClosed,
}

public sealed class EngineCrashedEventArgs(EngineFailureReason reason, string detail, uint exitCode, int restartNumber) : EventArgs
{
    public EngineFailureReason Reason { get; } = reason;

    public string Detail { get; } = detail;

    /// <summary>The exit code the engine ended with; 0xDEAD when the supervisor had to kill it.</summary>
    public uint ExitCode { get; } = exitCode;

    public int RestartNumber { get; } = restartNumber;
}

public sealed class EngineRestoredEventArgs(TimeSpan elapsed, int documentsRestored, int documentsLost, int restartNumber) : EventArgs
{
    /// <summary>From the moment the failure was noticed to the moment the documents were reopened in the new engine.</summary>
    public TimeSpan Elapsed { get; } = elapsed;

    public int DocumentsRestored { get; } = documentsRestored;

    /// <summary>Documents that could not be reopened (their calls now return the reason).</summary>
    public int DocumentsLost { get; } = documentsLost;

    public int RestartNumber { get; } = restartNumber;
}

public sealed record EngineSupervisorOptions
{
    /// <summary>Path of <c>MikePDF.Engine.exe</c> (or <c>MikePDF.WriterHost.exe</c>). Its directory must be readable by the AppContainer (see <see cref="EngineDeployment"/>).</summary>
    public required string EnginePath { get; init; }

    public required AppContainerProfile Profile { get; init; }

    public SandboxPolicy Policy { get; init; } = SandboxPolicy.Default;

    public EngineKind Kind { get; init; } = EngineKind.Document;

    /// <summary>What is being supervised: the PDFium engine (default) or the PdfWriter host. Same sandbox, same watchdog, same restart rules.</summary>
    public ProcessKind Peer { get; init; } = ProcessKind.Engine;

    public int SlotCount { get; init; } = 8;

    public int SlotSize { get; init; } = 4 << 20; // 1024 x 1024 BGRA, the largest tile

    /// <summary>§5.2 rule 4: a ping every second ...</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>... and the engine is killed when a ping goes unanswered for this long.</summary>
    public TimeSpan WatchdogTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan DefaultRequestDeadline { get; init; } = TimeSpan.FromSeconds(30);

    public EngineClientOptions Client { get; init; } = new();

    /// <summary>A crash loop is not restarted forever: more than this many restarts within <see cref="RestartWindow"/> leaves the supervisor failed.</summary>
    public int MaxRestartsPerWindow { get; init; } = 5;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Consecutive attempts to bring a new engine up before giving up. The first start gets the same attempts as a restart: an engine process that is lost
    /// while it starts (<see cref="EngineStartException.IsTransient"/>) costs a few milliseconds to replace, and a user opening a document should not have
    /// to be the one to retry.
    /// </summary>
    public int MaxStartAttempts { get; init; } = 3;

    /// <summary>Test seam: called with the new process (still suspended) and the attempt number, before it runs. Lets a test lose the process the way a busy machine can.</summary>
    internal Action<SandboxedProcess, int>? AfterLaunch { get; init; }
}

/// <summary>
/// Keeps one engine alive for the documents opened through it (§5.2 rules 3 and 4, ADR-0004 decisions 3 and 4). A
/// heartbeat watches the engine; a crash, a memory-limit event, a missed heartbeat, an overdue request or a protocol violation
/// kills it, starts a fresh sandboxed engine, reopens every document from the broker's own file handles, and raises
/// <see cref="EngineCrashed"/> then <see cref="Restored"/>. Requests that were in flight at the time end with <c>EngineCrashed</c>;
/// the caller retries.
/// </summary>
public sealed class EngineSupervisor : IAsyncDisposable
{
    private enum State
    {
        Running,
        Restarting,

        /// <summary>No engine process on purpose (the budget needed the slot); the documents, their file handles and passwords are kept. The next call resumes.</summary>
        Suspended,

        /// <summary>A call asked for a suspended engine: a process is being started and the documents reopened in it.</summary>
        Resuming,
        Failed,
        Disposed,
    }

    private readonly EngineSupervisorOptions _options;
    private readonly SandboxedProcessLauncher _launcher;
    private readonly SlotRing? _ring;
    private readonly object _gate = new();
    private readonly List<SupervisedDocument> _documents = [];
    private readonly Queue<DateTime> _restartTimes = new();
    private readonly CancellationTokenSource _shutdown = new();
    private State _state = State.Running;
    private EngineInstance? _instance;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _recovery = Task.CompletedTask;
    private int _restartCount;
    private long _pingNonce;
    private string _pdfiumVersion = string.Empty;

    // Suspension (EngineBudget). All guarded by _gate unless said otherwise.
    private int _activeCalls;        // calls in flight: an engine with one is never suspended
    private int _pins;               // callers that need the engine to stay (a visible tab, a save, a print job)
    private Task _resume = Task.CompletedTask;
    private Task _suspendDone = Task.CompletedTask; // ends when the process of the latest suspension has exited and its slot has been returned
    private Status _resumeFailure = Status.Ok;
    private long _lastUsed;          // Volatile

    private EngineSupervisor(EngineSupervisorOptions options)
    {
        _options = options;
        _launcher = new SandboxedProcessLauncher(options.Profile, options.Policy);
        if (options.Peer is not (ProcessKind.Engine or ProcessKind.WriterHost))
        {
            throw new ArgumentException("Only the engine and the WriterHost are supervised.", nameof(options));
        }

        _ring = options.Peer == ProcessKind.Engine ? new SlotRing(options.SlotCount, options.SlotSize) : null;
    }

    /// <summary>The engine died, hung or was killed. Raised once per failure, before the restart begins. Handlers must not block.</summary>
    public event EventHandler<EngineCrashedEventArgs>? EngineCrashed;

    /// <summary>A new engine is up and every document has been reopened in it.</summary>
    public event EventHandler<EngineRestoredEventArgs>? Restored;

    /// <summary>The supervisor gave up (a crash loop, or no engine would start). Every call now returns <c>EngineCrashed</c>.</summary>
    public event EventHandler<EngineCrashedEventArgs>? Failed;

    public int RestartCount => Volatile.Read(ref _restartCount);

    /// <summary>The engine was shut down on purpose to free its slot, or is being brought back (see <see cref="EngineBudget"/>). Documents stay open to their callers.</summary>
    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _state is State.Suspended or State.Resuming;
            }
        }
    }

    /// <summary>How recently a call used this engine: the budget's clock when it did (0 when it never has). Larger is more recent.</summary>
    internal long LastUsed => Volatile.Read(ref _lastUsed);

    /// <summary>The budget's clock; set by the budget, read when a call uses the engine.</summary>
    internal Func<long>? UseClock { get; set; }

    /// <summary>Called when the engine became idle (its last call ended or the last pin was dropped), so a document waiting for a slot can look again.</summary>
    internal Action? Idled { get; set; }

    /// <summary>
    /// Called before a suspended engine is started again, to obtain the slot it needs. Returns false if none became free: the call that asked fails with
    /// <c>Timeout</c> and the engine stays suspended.
    /// </summary>
    internal Func<EngineSupervisor, CancellationToken, Task<bool>>? ResumeGate { get; set; }

    /// <summary>Raised with the new state when the engine is suspended, starts resuming, runs again after a resume or fails. Runs on the thread that caused it.</summary>
    internal Action<DocumentEngineState>? LifeChanged { get; set; }

    public bool IsFailed
    {
        get
        {
            lock (_gate)
            {
                return _state == State.Failed;
            }
        }
    }

    /// <summary>Every kernel handle the broker holds for the current engine and for the ring, for tests that check none is inheritable.</summary>
    internal IReadOnlyList<nint> BrokerHandles
    {
        get
        {
            lock (_gate)
            {
                return [.. _instance?.BrokerHandles ?? [], .. _ring is null ? [] : new[] { _ring.SectionHandle }];
            }
        }
    }

    /// <summary>The sandboxed engine process, for tests.</summary>
    internal SandboxedProcess? CurrentProcess
    {
        get
        {
            lock (_gate)
            {
                return _instance?.Process;
            }
        }
    }

    /// <summary>
    /// What the engine reported as its PDFium version in its Hello (empty for the WriterHost, which has none). Remembered across restarts and suspensions: it is a property of
    /// the installation, not of one process. Empty until the first engine has answered.
    /// </summary>
    public string PdfiumVersion
    {
        get
        {
            lock (_gate)
            {
                return _instance?.Hello.PdfiumVersion is { Length: > 0 } version ? _pdfiumVersion = version : _pdfiumVersion;
            }
        }
    }

    public uint EngineProcessId
    {
        get
        {
            lock (_gate)
            {
                return _instance?.Process.ProcessId ?? 0;
            }
        }
    }

    /// <summary>The ring the tiles are rendered into. Exposed for tests; callers normally only see <see cref="TileLease"/>.</summary>
    public SlotRing Ring => _ring ?? throw new InvalidOperationException("This peer has no slot ring.");

    /// <summary>Starts the first engine. Throws <see cref="EngineStartException"/> if the sandbox or the engine cannot be brought up: there is no weaker fallback.</summary>
    public static async Task<EngineSupervisor> StartAsync(EngineSupervisorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var supervisor = new EngineSupervisor(options);
        try
        {
            var instance = await supervisor.StartInstanceAsync(cancellationToken).ConfigureAwait(false);
            lock (supervisor._gate)
            {
                supervisor._instance = instance;
            }

            supervisor.Attach(instance);
            supervisor._ready.TrySetResult();
            return supervisor;
        }
        catch
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts an engine, trying again (up to <see cref="EngineSupervisorOptions.MaxStartAttempts"/> times) when the process is lost while it starts.</summary>
    private async Task<EngineInstance> StartInstanceAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await EngineInstance.StartAsync(_options, _launcher, _ring, _options.Client, cancellationToken, attempt).ConfigureAwait(false);
            }
            catch (EngineStartException e) when (e.IsTransient && attempt < _options.MaxStartAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How the broker opens a document, and every handle it gives the writer host or verifies with (ADR-0005 amendment A3, S10): others may not
    /// write, append or truncate while it is open, but may rename, delete or replace the path, which is what lets a save replace the file while the
    /// engines still map the old one.
    /// </summary>
    public const FileShare DocumentShare = FileShare.Read | FileShare.Delete;

    /// <summary>
    /// Opens a document. The broker opens the file (read-only, deny-write, share-delete) and keeps the handle for the document's lifetime so it can be
    /// reopened after a crash; the engine only ever receives a duplicate of it.
    /// </summary>
    public async Task<EngineResult<SupervisedDocument>> OpenDocumentAsync(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_options.Peer != ProcessKind.Engine)
        {
            return EngineResult<SupervisedDocument>.Fail(Status.Unsupported, "Only the engine opens documents.");
        }

        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, DocumentShare);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
        {
            return EngineResult<SupervisedDocument>.Fail(Status.InvalidArgument, e.Message);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return EngineResult<SupervisedDocument>.Fail(Status.PermissionDenied, e.Message);
        }

        var acquired = await AcquireAsync(null, cancellationToken, countCall: true).ConfigureAwait(false);
        var instance = acquired.Instance;
        if (instance is null)
        {
            file.Dispose();
            return EngineResult<SupervisedDocument>.Fail(acquired.Failure, "No engine is available.");
        }

        try // the call is counted as in flight until the document is registered, so the engine cannot be suspended in between
        {
            ulong remote;
            EngineResult<OpenDocumentReply> opened;
            try
            {
                remote = instance.DuplicateIntoEngine(file);
                opened = await instance.Client.OpenDocumentAsync(remote, password, _options.DefaultRequestDeadline, cancellationToken).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                file.Dispose();
                return EngineResult<SupervisedDocument>.Fail(Status.EngineCrashed, e.Message);
            }

            if (!opened.IsOk || opened.Value is null)
            {
                file.Dispose();
                return EngineResult<SupervisedDocument>.Fail(opened.Status, opened.Detail);
            }

            var document = new SupervisedDocument(this, file, password, opened.Value.DocumentId, opened.Value.PageCount) { Engine = instance };
            lock (_gate)
            {
                // If the engine was replaced in the meantime, this document missed the restore: the caller retries.
                if (_state != State.Running || !ReferenceEquals(_instance, instance))
                {
                    document.DisposeHandle();
                    return EngineResult<SupervisedDocument>.Fail(Status.EngineCrashed, "The engine was replaced while the document was opening.");
                }

                _documents.Add(document);
            }

            return new EngineResult<SupervisedDocument>(Status.Ok, document);
        }
        finally
        {
            EndCall();
        }
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(graceful: true);

    /// <summary>
    /// Ends the engine now: the process is killed without being asked to leave, and nothing is restarted. For a caller that has decided the engine's work is no longer
    /// wanted (ShellHost, when its client gave up or a deadline passed): <see cref="DisposeAsync"/> would give a stuck engine two seconds to answer Shutdown first.
    /// </summary>
    public ValueTask KillAsync() => DisposeCoreAsync(graceful: false);

    private async ValueTask DisposeCoreAsync(bool graceful)
    {
        EngineInstance? instance;
        lock (_gate)
        {
            if (_state == State.Disposed)
            {
                return;
            }

            _state = State.Disposed;
            instance = _instance;
            _instance = null;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _ready.TrySetResult();
        try
        {
            await _recovery.ConfigureAwait(false);
            await _resume.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Recovery and resuming report their failures through events and statuses; nothing more to say while shutting down.
        }

        if (instance is not null)
        {
            if (graceful)
            {
                await instance.ShutdownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            await instance.DisposeAsync().ConfigureAwait(false);
        }

        SupervisedDocument[] documents;
        lock (_gate)
        {
            documents = [.. _documents];
            _documents.Clear();
        }

        foreach (var document in documents)
        {
            document.DisposeHandle();
        }

        _ring?.Dispose();
        _shutdown.Dispose();
    }

    // ---- used by SupervisedDocument ------------------------------------------------------------

    /// <summary>What <see cref="AcquireAsync"/> found: the engine (null when there is none) and why not.</summary>
    internal readonly record struct Acquired(EngineInstance? Instance, uint EngineDocumentId, Status Failure);

    /// <summary>
    /// The current engine and, for a document, the id it has there. Waits out a restart and, for a suspended engine, <b>resumes it</b> (unless <paramref name="resume"/> is
    /// false, for a call that has nothing to do with a suspended engine, such as closing a document: then the instance is null). With <paramref name="countCall"/> the call is
    /// counted as in flight, which keeps the engine from being suspended, until <see cref="EndCall"/>. Null instance: the supervisor failed or was disposed, or a resume
    /// found no slot (<see cref="Acquired.Failure"/> says which).
    /// </summary>
    internal async Task<Acquired> AcquireAsync(SupervisedDocument? document, CancellationToken cancellationToken, bool countCall = false, bool resume = true)
    {
        var waitedForResume = false;
        while (true)
        {
            Task ready;
            lock (_gate)
            {
                if (_state == State.Running && _instance is not null)
                {
                    if (countCall)
                    {
                        _activeCalls++;
                    }

                    Volatile.Write(ref _lastUsed, UseClock?.Invoke() ?? 0);
                    return new Acquired(_instance, document?.EngineDocumentId ?? 0, Status.Ok);
                }

                if (_state is State.Failed or State.Disposed)
                {
                    return new Acquired(null, 0, Status.EngineCrashed);
                }

                if (_state == State.Suspended)
                {
                    if (!resume)
                    {
                        return new Acquired(null, 0, Status.EngineCrashed);
                    }

                    if (waitedForResume && _resumeFailure != Status.Ok)
                    {
                        return new Acquired(null, 0, _resumeFailure); // the resume this call waited for did not happen: no slot came free
                    }

                    // (A resume that worked and was undone by the budget before this call could use it is simply done again.)

                    _state = State.Resuming;
                    _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _resumeFailure = Status.Ok;
                    _resume = Task.Run(ResumeAsync);
                }

                waitedForResume |= _state == State.Resuming;
                ready = _ready.Task;
            }

            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A counted call ended. When it was the last, a document waiting for a slot may now find this engine idle.</summary>
    internal void EndCall()
    {
        bool idle;
        lock (_gate)
        {
            idle = --_activeCalls == 0 && _pins == 0;
        }

        if (idle)
        {
            Idled?.Invoke();
        }
    }

    /// <summary>Keeps the engine from being suspended until the returned lease is disposed (a visible tab, a save or print job). A suspended engine is not resumed by this.</summary>
    internal IDisposable Pin()
    {
        lock (_gate)
        {
            _pins++;
        }

        return new PinLease(this);
    }

    private sealed class PinLease(EngineSupervisor owner) : IDisposable
    {
        private EngineSupervisor? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } supervisor)
            {
                supervisor.Unpin();
            }
        }
    }

    private void Unpin()
    {
        bool idle;
        lock (_gate)
        {
            idle = --_pins == 0 && _activeCalls == 0;
        }

        if (idle)
        {
            Idled?.Invoke();
        }
    }

    /// <summary>Starts the engine again if it is suspended and waits until it runs. The failure status when it cannot (no slot, or the engine will not start).</summary>
    internal async Task<Status> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(null, cancellationToken).ConfigureAwait(false);
        return acquired.Instance is null ? acquired.Failure : Status.Ok;
    }

    /// <summary>
    /// Shuts the engine down to free its slot if it is idle: running, no call in flight, nobody pinning it. The documents stay open to their callers and the next call
    /// starts a new engine (see <see cref="ResumeAsync"/>). False, and nothing changed, when the engine is busy, pinned, restarting, suspended or gone. When this returns
    /// true the process has exited.
    /// </summary>
    internal async Task<bool> TrySuspendAsync(Action? afterExit = null)
    {
        EngineInstance instance;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_state != State.Running || _instance is null || _activeCalls > 0 || _pins > 0)
            {
                return false;
            }

            instance = _instance;
            _instance = null; // reports about the old process are ignored from here on, and nothing can pick it up
            _state = State.Suspended;
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _suspendDone = done.Task;
            foreach (var document in _documents)
            {
                document.Engine = null; // its engine-side id died with the process; the next resume gives it a new one
            }
        }

        NotifyLife(DocumentEngineState.Suspended);
        try
        {
            await instance.ShutdownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await instance.DisposeAsync().ConfigureAwait(false);
            afterExit?.Invoke(); // the process is gone: give its slot back (the budget's) before anything can ask for it again
        }
        finally
        {
            done.TrySetResult(); // a resume that was asked for meanwhile has been waiting for exactly this
        }

        return true;
    }

    /// <summary>Starts an engine for a suspended supervisor and reopens its documents from the broker's own handles, as after a crash but without the events or the banner.</summary>
    private async Task ResumeAsync()
    {
        NotifyLife(DocumentEngineState.Resuming);
        Task suspending;
        lock (_gate)
        {
            suspending = _suspendDone;
        }

        await suspending.ConfigureAwait(false); // the old process must be gone, and its slot returned, before a new one is asked for
        var failure = Status.EngineCrashed;
        EngineInstance? instance = null;
        var failed = false;
        try
        {
            if (ResumeGate is { } gate && !await gate(this, _shutdown.Token).ConfigureAwait(false))
            {
                failure = Status.Timeout; // no slot became free: stay suspended, the caller can try again
            }
            else
            {
                for (var attempt = 1; attempt <= _options.MaxStartAttempts && instance is null && !_shutdown.IsCancellationRequested; attempt++)
                {
                    EngineInstance? candidate = null;
                    try
                    {
                        candidate = await EngineInstance.StartAsync(_options, _launcher, _ring, _options.Client, _shutdown.Token, attempt).ConfigureAwait(false);
                        await ReopenDocumentsAsync(candidate).ConfigureAwait(false);
                        instance = candidate;
                    }
                    catch (Exception e) when (e is EngineStartException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
                    {
                        if (candidate is not null)
                        {
                            await candidate.DisposeAsync().ConfigureAwait(false);
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), CancellationToken.None).ConfigureAwait(false);
                    }
                }

                failed = instance is null;
            }
        }
        catch (OperationCanceledException)
        {
            failed = false; // disposed while waiting for a slot
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever went wrong, a call is waiting for this resume: it must end (the engine failed), not wait for ever.
            failed = true;
            instance = null;
        }

        var adopted = false;
        lock (_gate)
        {
            if (_state == State.Disposed)
            {
                // Disposed meanwhile: whatever was started goes again below.
            }
            else if (instance is not null)
            {
                _instance = instance;
                _state = State.Running;
                adopted = true;
                Volatile.Write(ref _lastUsed, UseClock?.Invoke() ?? 0); // the freshest engine there is: the calls that woke it must get to use it before it is anybody's victim
            }
            else
            {
                _state = failed ? State.Failed : State.Suspended;
                _resumeFailure = failure;
            }

            _ready.TrySetResult();
        }

        if (instance is not null && !adopted)
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (adopted)
        {
            Attach(instance!);
            if (instance!.Client.IsFaulted || instance.Process.HasExited)
            {
                Fail(instance, EngineFailureReason.ProcessExited, "The resumed engine died while it was being attached.");
            }

            NotifyLife(DocumentEngineState.Running);
        }
        else if (failed)
        {
            NotifyLife(DocumentEngineState.Failed);
            Raise(Failed, new EngineCrashedEventArgs(EngineFailureReason.ProcessExited, "The engine could not be started again after being suspended.", 0, RestartCount));
        }
        else
        {
            NotifyLife(DocumentEngineState.Suspended); // no slot came free (or it was disposed): back to waiting for the next call
        }
    }

    private void NotifyLife(DocumentEngineState state)
    {
        try
        {
            LifeChanged?.Invoke(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A subscriber's failure is the subscriber's problem.
        }
    }

    internal TimeSpan DefaultDeadline => _options.DefaultRequestDeadline;

    internal void Forget(SupervisedDocument document)
    {
        lock (_gate)
        {
            _documents.Remove(document);
        }
    }

    // ---- failure detection ---------------------------------------------------------------------

    private void Attach(EngineInstance instance)
    {
        instance.Client.Faulted += fault => Fail(instance, fault.Kind == EngineFaultKind.ProtocolViolation ? EngineFailureReason.ProtocolViolation : EngineFailureReason.PipeClosed, fault.Detail);
        instance.Client.RequestOverdue += type => Fail(instance, EngineFailureReason.Hung, $"A {type} request is overdue and the engine is not getting it done: it has made no progress, or has held the request for too long.");
        instance.Process.Job.EventRaised += job =>
        {
            switch (job.Kind)
            {
                case EngineJobEventKind.ProcessMemoryLimit:
                    Fail(instance, EngineFailureReason.MemoryLimit, "The job's memory limit was hit.");
                    break;
                case EngineJobEventKind.ProcessExited:
                case EngineJobEventKind.ProcessExitedAbnormally:
                    if (!instance.Retiring)
                    {
                        Fail(instance, EngineFailureReason.ProcessExited, "The engine process exited.");
                    }

                    break;
                default:
                    break;
            }
        };
        _ = Task.Run(() => WatchdogAsync(instance));
    }

    private async Task WatchdogAsync(EngineInstance instance)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(instance.Lifetime).ConfigureAwait(false))
            {
                var nonce = (ulong)Interlocked.Increment(ref _pingNonce);
                var pong = await instance.Client.PingAsync(nonce, _options.WatchdogTimeout, instance.Lifetime).ConfigureAwait(false);
                if (pong.Status == Status.Timeout)
                {
                    Fail(instance, EngineFailureReason.Hung, $"No answer to a ping for {_options.WatchdogTimeout.TotalSeconds:0.#} s.");
                    return;
                }

                if (!pong.IsOk)
                {
                    return; // cancelled, or the client faulted (which has its own handler)
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The instance was retired.
        }
    }

    /// <summary>Marks the failure and starts recovery. Idempotent per instance; stale reports (an old instance, a deliberate shutdown) are ignored.</summary>
    private void Fail(EngineInstance instance, EngineFailureReason reason, string detail)
    {
        lock (_gate)
        {
            if (_state != State.Running || !ReferenceEquals(_instance, instance) || instance.Retiring)
            {
                return;
            }

            _state = State.Restarting;
            _instance = null; // stale reports about the dead engine are now ignored, and nobody can pick it up again
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recovery = Task.Run(() => RecoverAsync(instance, reason, detail));
        }
    }

    private async Task RecoverAsync(EngineInstance failed, EngineFailureReason reason, string detail)
    {
        var clock = Stopwatch.StartNew();
        failed.Process.Kill();
        await failed.Process.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // The job queues its messages in order; waiting for it to empty means a memory-limit message sent just before the exit has been seen.
        await Task.WhenAny(failed.Process.Job.Emptied, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        if (failed.Process.Job.MemoryLimitExceeded)
        {
            reason = EngineFailureReason.MemoryLimit;
        }

        var exitCode = failed.Process.ExitCode;
        await failed.DisposeAsync().ConfigureAwait(false);
        var restartNumber = Interlocked.Increment(ref _restartCount);
        Raise(EngineCrashed, new EngineCrashedEventArgs(reason, detail, exitCode, restartNumber));

        var lastError = detail;
        for (var attempt = 1; attempt <= _options.MaxStartAttempts && !_shutdown.IsCancellationRequested; attempt++)
        {
            if (CrashLoop())
            {
                lastError = "Too many restarts in a short time.";
                break;
            }

            EngineInstance? replacement = null;
            try
            {
                replacement = await EngineInstance.StartAsync(_options, _launcher, _ring, _options.Client, _shutdown.Token, attempt).ConfigureAwait(false);
                var (restored, lost) = await ReopenDocumentsAsync(replacement).ConfigureAwait(false);
                bool adopted;
                lock (_gate)
                {
                    adopted = _state != State.Disposed;
                    if (adopted)
                    {
                        _instance = replacement;
                        _state = State.Running;
                        _ready.TrySetResult();
                    }
                }

                if (!adopted)
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                Attach(replacement);
                if (replacement.Client.IsFaulted || replacement.Process.HasExited)
                {
                    Fail(replacement, EngineFailureReason.ProcessExited, "The new engine died while it was being attached.");
                }

                Raise(Restored, new EngineRestoredEventArgs(clock.Elapsed, restored, lost, restartNumber));
                return;
            }
            catch (Exception e) when (e is EngineStartException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                lastError = e.Message;
                if (replacement is not null)
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        lock (_gate)
        {
            if (_state != State.Disposed)
            {
                _state = State.Failed;
            }

            _instance = null;
            _ready.TrySetResult();
        }

        Raise(Failed, new EngineCrashedEventArgs(reason, lastError, exitCode, restartNumber));
    }

    private bool CrashLoop()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            while (_restartTimes.Count > 0 && now - _restartTimes.Peek() > _options.RestartWindow)
            {
                _restartTimes.Dequeue();
            }

            if (_restartTimes.Count >= _options.MaxRestartsPerWindow)
            {
                return true;
            }

            _restartTimes.Enqueue(now);
            return false;
        }
    }

    /// <summary>Reopens every live document in the new engine from the broker's own handles. A document that no longer opens is marked lost.</summary>
    private async Task<(int Restored, int Lost)> ReopenDocumentsAsync(EngineInstance instance)
    {
        SupervisedDocument[] documents;
        lock (_gate)
        {
            documents = [.. _documents];
        }

        var restored = 0;
        var lost = 0;
        foreach (var document in documents)
        {
            var remote = instance.DuplicateIntoEngine(document.Handle);
            var opened = await instance.Client.OpenDocumentAsync(remote, document.Password, _options.DefaultRequestDeadline, _shutdown.Token).ConfigureAwait(false);
            if (opened.Status == Status.EngineCrashed)
            {
                throw new InvalidOperationException("The new engine died while the documents were being restored.");
            }

            if (opened.IsOk && opened.Value is { } reply && reply.PageCount == document.PageCount)
            {
                document.EngineDocumentId = reply.DocumentId;
                document.Engine = instance;
                document.LostStatus = null;
                restored++;
            }
            else
            {
                document.LostStatus = opened.IsOk ? Status.Damaged : opened.Status;
                lost++;
            }
        }

        return (restored, lost);
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
        where T : EventArgs
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A subscriber's failure is the subscriber's problem; recovery must finish.
        }
    }
}

/// <summary>
/// A document open in the supervised engine. Its id here is stable across engine restarts; the engine's own id for it is not.
/// </summary>
public sealed class SupervisedDocument : IAsyncDisposable
{
    private readonly EngineSupervisor _owner;
    private readonly SafeFileHandle _file;
    private int _closed;

    internal SupervisedDocument(EngineSupervisor owner, SafeFileHandle file, string? password, uint engineDocumentId, uint pageCount)
    {
        _owner = owner;
        _file = file;
        Password = password;
        EngineDocumentId = engineDocumentId;
        PageCount = pageCount;
    }

    public uint PageCount { get; }

    /// <summary>How many times the engine this document lives in has been replaced after a crash. A caller that reads it before a call can tell calls that met the same engine death from calls that met two.</summary>
    public int EngineRestartCount => _owner.RestartCount;

    internal string? Password { get; }

    internal SafeFileHandle Handle => _file;

    internal uint EngineDocumentId { get; set; }

    /// <summary>The engine <see cref="EngineDocumentId"/> belongs to. A close must never be sent to a different one.</summary>
    internal EngineInstance? Engine { get; set; }

    /// <summary>Set when a restart could not reopen the document; every call returns this status.</summary>
    internal Status? LostStatus { get; set; }

    public Task<EngineResult<GetDocumentInfoReply>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetDocumentInfoAsync(id, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<GetDocumentDetailsReply>> GetDocumentDetailsAsync(CancellationToken cancellationToken = default) =>
        CallAsync((client, id, _) => client.GetDocumentDetailsAsync(id, TimeSpan.FromSeconds(10), cancellationToken), cancellationToken);

    public Task<EngineResult<GetPageSizesReply>> GetPageSizesAsync(uint first, uint count, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetPageSizesAsync(id, first, count, deadline, cancellationToken), cancellationToken);

    // The reading services. Each is one window of a list that is too long for a frame; the caller pages through it. A restart of the engine is
    // invisible here: the position-based addressing (outline paths, page and index) means the same call works against the reopened document.

    public Task<EngineResult<GetOutlineReply>> GetOutlineAsync(IReadOnlyList<uint> path, uint first, uint count, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetOutlineAsync(id, path, first, count, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<GetPageLinksReply>> GetPageLinksAsync(uint page, uint first, uint count, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetPageLinksAsync(id, page, first, count, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<GetLinkUriReply>> GetLinkUriAsync(uint page, uint index, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetLinkUriAsync(id, page, index, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<GetPageBoxesReply>> GetPageBoxesAsync(uint page, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetPageBoxesAsync(id, page, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<GetPageTextReply>> GetPageTextAsync(uint page, uint first, uint count, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetPageTextAsync(id, page, first, count, deadline, cancellationToken), cancellationToken);

    public Task<EngineResult<SearchPageReply>> SearchPageAsync(uint page, string query, uint flags, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.SearchPageAsync(id, page, query, flags, deadline, cancellationToken), cancellationToken);

    /// <summary>See <see cref="EngineClient.RenderTileAsync"/> for the meaning of <paramref name="generation"/> and of cancelling the token.</summary>
    public Task<EngineResult<TileLease>> RenderTileAsync(RenderTileArgs args, ulong generation, TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, defaultDeadline) => client.RenderTileAsync(id, args, generation, deadline ?? defaultDeadline, cancellationToken), cancellationToken);

    /// <summary>The document's /ViewerPreferences: reading direction, print defaults and page ranges (schema 5).</summary>
    public Task<EngineResult<GetViewerPreferencesReply>> GetViewerPreferencesAsync(CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetViewerPreferencesAsync(id, deadline, cancellationToken), cancellationToken);

    /// <summary>One band of a page for printing (schema 5). See <see cref="EngineClient.RenderBandAsync"/>; the ring's slots must be large enough for the band.</summary>
    public Task<EngineResult<TileLease>> RenderBandAsync(RenderBandArgs args, ulong generation, TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, defaultDeadline) => client.RenderBandAsync(id, args, generation, deadline ?? defaultDeadline, cancellationToken), cancellationToken);

    public Task<EngineResult<CancelBeforeReply>> CancelBeforeAsync(ulong generation, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, _) => client.CancelBeforeAsync(id, generation, TimeSpan.FromSeconds(5), cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _owner.Forget(this);
        // Closing a document in a suspended engine has no engine to tell: it is not worth starting one for (resume: false).
        var acquired = await _owner.AcquireAsync(this, CancellationToken.None, countCall: true, resume: false).ConfigureAwait(false);
        if (acquired.Instance is { } instance)
        {
            try
            {
                if (ReferenceEquals(instance, Engine) && LostStatus is null)
                {
                    await instance.Client.CloseDocumentAsync(acquired.EngineDocumentId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            finally
            {
                _owner.EndCall();
            }
        }

        DisposeHandle();
    }

    internal void DisposeHandle() => _file.Dispose();

    private async Task<EngineResult<T>> CallAsync<T>(Func<EngineClient, uint, TimeSpan, Task<EngineResult<T>>> call, CancellationToken cancellationToken)
        where T : class
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return EngineResult<T>.Fail(Status.InvalidArgument, "The document is closed.");
        }

        // A suspended engine is resumed here; the call is counted as in flight so the engine is not suspended again under it.
        var acquired = await _owner.AcquireAsync(this, cancellationToken, countCall: true).ConfigureAwait(false);
        if (acquired.Instance is not { } instance)
        {
            return EngineResult<T>.Fail(acquired.Failure, acquired.Failure == Status.Timeout ? "No engine slot became free in time." : "No engine is available.");
        }

        try
        {
            if (LostStatus is { } lost)
            {
                return EngineResult<T>.Fail(lost, "The document could not be reopened after an engine restart.");
            }

            return await call(instance.Client, acquired.EngineDocumentId, _owner.DefaultDeadline).ConfigureAwait(false);
        }
        finally
        {
            _owner.EndCall();
        }
    }
}
