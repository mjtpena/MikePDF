using System.Diagnostics;

namespace MikePDF.Broker;

public sealed record EngineBudgetOptions
{
    /// <summary>The options of a document engine (<see cref="EngineSupervisorOptions.Kind"/> is forced to <see cref="EngineKind.Document"/>).</summary>
    public required EngineSupervisorOptions Document { get; init; }

    /// <summary>The options of the shared pool engine (<see cref="EngineSupervisorOptions.Kind"/> is forced to <see cref="EngineKind.Pool"/>).</summary>
    public required EngineSupervisorOptions Pool { get; init; }

    /// <summary>The most document engines that run at once: 6 by default (ADR-0004), set by policy. The shared pool engine is on top of these.</summary>
    public int MaxDocumentEngines { get; init; } = 6;

    /// <summary>How long a document waits for a slot when every engine is busy or pinned before its call fails with <c>Timeout</c>.</summary>
    public TimeSpan SlotWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The engines the app may have at once (ADR-0004 decision 1, amendment A3): <b>one shared pool engine</b> for every document's thumbnails, text and search, and at most
/// <see cref="EngineBudgetOptions.MaxDocumentEngines"/> <b>document engines</b>, one per document being drawn. A document over the cap is <i>suspended</i>: its engine
/// process is shut down, while its file handle (opened deny-write), its password and its <see cref="SupervisedDocument"/> stay with the broker, and the first call that
/// needs the engine starts a new one and reopens the file, the way a crash is recovered from but without the banner. The document that gives way is the least recently used
/// one that is not busy (no call in flight) and not pinned; when every engine is busy or pinned the newcomer waits, up to <see cref="EngineBudgetOptions.SlotWaitTimeout"/>,
/// and then fails with <c>Timeout</c>, instead of starting one process too many. The victim's process has exited before the newcomer's starts, so the number of engine
/// processes never exceeds the cap plus one.
/// </summary>
public sealed class EngineBudget : IAsyncDisposable
{
    private readonly EngineBudgetOptions _options;
    private readonly object _gate = new();
    private readonly List<DocumentEngine> _engines = [];
    private readonly SemaphoreSlim _poolStart = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private EngineSupervisor? _pool;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _live; // document engines that hold a slot: running, restarting or starting
    private long _tick;
    private bool _disposed;

    public EngineBudget(EngineBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDocumentEngines, 1);
        _options = options with
        {
            Document = options.Document with { Kind = EngineKind.Document },
            Pool = options.Pool with { Kind = EngineKind.Pool },
        };
    }

    public int MaxDocumentEngines => _options.MaxDocumentEngines;

    /// <summary>Document engines that hold a slot: a process is running for them (or is being started or restarted).</summary>
    public int LiveDocumentEngines
    {
        get
        {
            lock (_gate)
            {
                return _live;
            }
        }
    }

    /// <summary>Document engines that are suspended: no process.</summary>
    public int SuspendedDocumentEngines
    {
        get
        {
            lock (_gate)
            {
                return _engines.Count(e => e.State == DocumentEngineState.Suspended);
            }
        }
    }

    /// <summary>Every document engine the budget knows, live or suspended, oldest first.</summary>
    public IReadOnlyList<DocumentEngine> DocumentEngines
    {
        get
        {
            lock (_gate)
            {
                return [.. _engines];
            }
        }
    }

    /// <summary>The shared pool engine's process, or 0 when it has not been started.</summary>
    public uint PoolProcessId
    {
        get
        {
            lock (_gate)
            {
                return _pool?.EngineProcessId ?? 0;
            }
        }
    }

    /// <summary>
    /// Starts a document engine, suspending the least recently used one first if the cap is reached. Throws <see cref="EngineStartException"/> if no engine can be
    /// started and <see cref="TimeoutException"/> if every slot stayed busy or pinned for <see cref="EngineBudgetOptions.SlotWaitTimeout"/>.
    /// </summary>
    public async Task<DocumentEngine> CreateDocumentEngineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = new DocumentEngine(this);
        if (!await AcquireSlotAsync(engine, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException($"All {_options.MaxDocumentEngines} document engines are busy.");
        }

        try
        {
            var supervisor = await EngineSupervisor.StartAsync(_options.Document, cancellationToken).ConfigureAwait(false);
            engine.Attach(supervisor);
            supervisor.UseClock = NextTick;
            supervisor.Idled = Pulse;
            supervisor.ResumeGate = (_, ct) => AcquireSlotAsync(engine, ct);
            lock (_gate)
            {
                _engines.Add(engine);
            }

            return engine;
        }
        catch
        {
            ReleaseSlot(engine);
            throw;
        }
    }

    /// <summary>
    /// The one pool engine every document shares, started on first use. If it gave up (a crash loop) a new one replaces it; documents that were open in the old one are
    /// lost to their callers, who open them again and in the meantime read through the document's own engine.
    /// </summary>
    public async Task<EngineSupervisor> GetPoolAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _poolStart.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EngineSupervisor? stale = null;
            lock (_gate)
            {
                if (_pool is { IsFailed: false })
                {
                    return _pool;
                }

                stale = _pool;
                _pool = null;
            }

            if (stale is not null)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            var pool = await EngineSupervisor.StartAsync(_options.Pool, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _pool = pool;
            }

            return pool;
        }
        finally
        {
            _poolStart.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        DocumentEngine[] engines;
        EngineSupervisor? pool;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            engines = [.. _engines];
            pool = _pool;
            _pool = null;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var engine in engines)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        if (pool is not null)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _poolStart.Dispose();
    }

    // ---- slots ----------------------------------------------------------------------------------

    internal long NextTick() => Interlocked.Increment(ref _tick);

    internal void Forget(DocumentEngine engine)
    {
        lock (_gate)
        {
            _engines.Remove(engine);
        }
    }

    /// <summary>A call ended, an engine was unpinned or a slot was freed: somebody waiting for a slot may now find a victim.</summary>
    internal void Pulse()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    internal void ReleaseSlot(DocumentEngine engine)
    {
        lock (_gate)
        {
            if (!engine.HoldsSlot)
            {
                return;
            }

            engine.HoldsSlot = false;
            _live--;
        }

        Pulse();
    }

    /// <summary>
    /// Gives <paramref name="engine"/> a slot: at once if the cap allows it, else by suspending the least recently used engine that is idle and unpinned and waiting for its
    /// process to be gone. False when no slot became free in time. The slot is released by <see cref="ReleaseSlot"/> (the engine failed or was disposed) or by being
    /// suspended.
    /// </summary>
    internal async Task<bool> AcquireSlotAsync(DocumentEngine engine, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            List<DocumentEngine> candidates;
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (engine.HoldsSlot)
                {
                    return true;
                }

                if (_live < _options.MaxDocumentEngines)
                {
                    _live++;
                    engine.HoldsSlot = true;
                    return true;
                }

                changed = _changed.Task;
                candidates = [.. _engines.Where(e => e.HoldsSlot && e != engine).OrderBy(e => e.LastUsed)];
            }

            var freed = false;
            foreach (var candidate in candidates)
            {
                if (await candidate.TrySuspendForBudgetAsync().ConfigureAwait(false))
                {
                    freed = true;
                    break;
                }
            }

            if (freed)
            {
                continue; // its slot is free now (unless somebody else took it first: then the loop picks the next victim)
            }

            var remaining = _options.SlotWaitTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            // The wake-up is a pulse; the short delay is for the pulse that raced with the look at the candidates.
            await Task.WhenAny(changed, Task.Delay(TimeSpan.Compare(remaining, TimeSpan.FromMilliseconds(250)) < 0 ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
