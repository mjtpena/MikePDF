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

    /// <summary>Consecutive attempts to bring a new engine up before giving up.</summary>
    public int MaxStartAttempts { get; init; } = 3;
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
            var instance = await EngineInstance.StartAsync(options, supervisor._launcher, supervisor._ring, options.Client, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Opens a document. The broker opens the file (read-only, deny-write) and keeps the handle for the document's lifetime so it can be
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
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
        {
            return EngineResult<SupervisedDocument>.Fail(Status.InvalidArgument, e.Message);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return EngineResult<SupervisedDocument>.Fail(Status.PermissionDenied, e.Message);
        }

        var (instance, _) = await AcquireAsync(null, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            file.Dispose();
            return EngineResult<SupervisedDocument>.Fail(Status.EngineCrashed, "No engine is available.");
        }

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

    public async ValueTask DisposeAsync()
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
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Recovery reports its failures through events; nothing more to say while shutting down.
        }

        if (instance is not null)
        {
            await instance.ShutdownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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

    /// <summary>The current engine and, for a document, the id it has there. Waits out a restart. Null instance: the supervisor is failed or disposed.</summary>
    internal async Task<(EngineInstance? Instance, uint EngineDocumentId)> AcquireAsync(SupervisedDocument? document, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task ready;
            lock (_gate)
            {
                if (_state == State.Running && _instance is not null)
                {
                    return (_instance, document?.EngineDocumentId ?? 0);
                }

                if (_state is State.Failed or State.Disposed)
                {
                    return (null, 0);
                }

                ready = _ready.Task;
            }

            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        instance.Client.RequestOverdue += type => Fail(instance, EngineFailureReason.Hung, $"A {type} request was not answered in time.");
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
                replacement = await EngineInstance.StartAsync(_options, _launcher, _ring, _options.Client, _shutdown.Token).ConfigureAwait(false);
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

    internal string? Password { get; }

    internal SafeFileHandle Handle => _file;

    internal uint EngineDocumentId { get; set; }

    /// <summary>The engine <see cref="EngineDocumentId"/> belongs to. A close must never be sent to a different one.</summary>
    internal EngineInstance? Engine { get; set; }

    /// <summary>Set when a restart could not reopen the document; every call returns this status.</summary>
    internal Status? LostStatus { get; set; }

    public Task<EngineResult<GetDocumentInfoReply>> GetInfoAsync(CancellationToken cancellationToken = default) =>
        CallAsync((client, id, deadline) => client.GetDocumentInfoAsync(id, deadline, cancellationToken), cancellationToken);

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

    public Task<EngineResult<CancelBeforeReply>> CancelBeforeAsync(ulong generation, CancellationToken cancellationToken = default) =>
        CallAsync((client, id, _) => client.CancelBeforeAsync(id, generation, TimeSpan.FromSeconds(5), cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _owner.Forget(this);
        var (instance, id) = await _owner.AcquireAsync(this, CancellationToken.None).ConfigureAwait(false);
        if (instance is not null && ReferenceEquals(instance, Engine) && LostStatus is null)
        {
            await instance.Client.CloseDocumentAsync(id, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

        (EngineInstance? instance, uint id) = await _owner.AcquireAsync(this, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return EngineResult<T>.Fail(Status.EngineCrashed, "No engine is available.");
        }

        if (LostStatus is { } lost)
        {
            return EngineResult<T>.Fail(lost, "The document could not be reopened after an engine restart.");
        }

        return await call(instance.Client, id, _owner.DefaultDeadline).ConfigureAwait(false);
    }
}
