namespace MikePDF.ShellHost;

/// <summary>A request that waited for an engine and was dropped: its deadline passed, the queue was full, or ShellHost is leaving.</summary>
internal sealed class EngineWaitException(EngineWaitFailure failure) : Exception(failure.ToString())
{
    public EngineWaitFailure Failure { get; } = failure;
}

internal enum EngineWaitFailure
{
    /// <summary>The caller's deadline passed (or it went away) while it waited.</summary>
    Cancelled,

    /// <summary>The queue held 64 and this was the oldest.</summary>
    Dropped,

    /// <summary>ShellHost is shutting down.</summary>
    Closed,

    /// <summary>The engine could not be started.</summary>
    StartFailed,
}

/// <summary>
/// The engines ShellHost may have (ADR-0018 decision 5, spec 3.3): <b>one fresh engine for every file</b>, killed when the file is done; <b>one spare started ahead</b> so
/// that the next file does not pay for a process start (S6b: p50 28 ms against 37 ms); and <b>at most <c>shell.maxEngines</c> engine processes alive at once, the spare
/// included</b>. Requests that cannot have one yet wait <b>newest first</b> (the folder the person is looking at now, not the one they scrolled past) in a queue of at most
/// 64, and a request whose deadline passes while it waits is dropped without any work being done for it.
/// </summary>
internal sealed class ShellEnginePool(IShellEngineFactory factory, Func<int> maxEngines) : IAsyncDisposable
{
    public const int MaxWaiting = 64;

    private readonly object _gate = new();
    private readonly List<Waiter> _waiting = []; // oldest first; served from the end
    private Task<IShellEngine>? _spare;
    private int _live; // engine processes that exist or are being started: leased, spare, and starting
    private int _peak;
    private bool _closed;

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<bool> Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Engine processes alive or starting (the spare included).</summary>
    public int Live
    {
        get
        {
            lock (_gate)
            {
                return _live;
            }
        }
    }

    /// <summary>The most engines that were ever alive at once. For the flood test.</summary>
    public int Peak
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }

    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    public bool HasSpare
    {
        get
        {
            lock (_gate)
            {
                return _spare is { IsCompletedSuccessfully: true };
            }
        }
    }

    private int Cap => Math.Clamp(maxEngines(), 1, 6);

    /// <summary>Starts the spare if there is room. Called when ShellHost starts, so the first request finds one.</summary>
    public void Prewarm()
    {
        lock (_gate)
        {
            TopUpSpareLocked();
        }
    }

    /// <summary>
    /// An engine for one file. Waits for a slot if there is none (newest first); throws <see cref="EngineWaitException"/> if the wait ends without one. The lease owns the
    /// engine: disposing it kills the process and frees the slot.
    /// </summary>
    public async Task<EngineLease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<IShellEngine>? spare = null;
            Waiter? waiter = null;
            var mustStart = false;
            lock (_gate)
            {
                if (_closed)
                {
                    throw new EngineWaitException(EngineWaitFailure.Closed);
                }

                if (_spare is { IsCompleted: true, IsCompletedSuccessfully: false })
                {
                    // A spare that failed to start is dead weight: give its slot back and start this request's own engine instead of failing it with the spare's failure.
                    _spare = null;
                    _live--;
                }

                if (_spare is not null)
                {
                    spare = _spare;
                    _spare = null; // the spare becomes this request's engine; the slot it holds is the request's now
                    TopUpSpareLocked();
                }
                else if (_live < Cap)
                {
                    _live++;
                    NotePeakLocked();
                    mustStart = true;
                }
                else
                {
                    if (_waiting.Count >= MaxWaiting)
                    {
                        _waiting[0].Granted.TrySetException(new EngineWaitException(EngineWaitFailure.Dropped)); // the oldest gives way
                        _waiting.RemoveAt(0);
                    }

                    waiter = new Waiter();
                    _waiting.Add(waiter);
                }
            }

            if (spare is not null)
            {
                IShellEngine engine;
                try
                {
                    engine = await spare.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ = ReleaseAfterAsync(spare);
                    throw;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // The spare failed to start. That says nothing about this request's engine: give the slot back (without starting another spare that may fail the same way
                    // while the cause is still there) and start one for this request, which is what fails the request if an engine really cannot start.
                    DropSlot();
                    continue;
                }

                if (engine.IsAlive)
                {
                    return new EngineLease(this, engine);
                }

                // The spare died while it waited. Its slot is free again; take another route.
                await engine.KillAsync().ConfigureAwait(false);
                ReleaseSlot();
                continue;
            }

            if (mustStart)
            {
                try
                {
                    return new EngineLease(this, await factory.StartAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    ReleaseSlot();
                    throw;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    ReleaseSlot();
                    throw new EngineWaitException(EngineWaitFailure.StartFailed);
                }
            }

            // Queued: wait for a slot to be handed over (the slot is already counted in _live when it is), or for the deadline.
            using var registration = cancellationToken.Register(static state =>
            {
                var (pool, queued) = ((ShellEnginePool, Waiter))state!;
                pool.Abandon(queued);
            }, (this, waiter!));
            await waiter!.Granted.Task.ConfigureAwait(false);

            try
            {
                return new EngineLease(this, await factory.StartAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                ReleaseSlot();
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ReleaseSlot();
                throw new EngineWaitException(EngineWaitFailure.StartFailed);
            }
        }
    }

    private void Abandon(Waiter waiter)
    {
        lock (_gate)
        {
            _waiting.Remove(waiter);
        }

        waiter.Granted.TrySetCanceled();
    }

    private void NotePeakLocked() => _peak = Math.Max(_peak, _live);

    private void TopUpSpareLocked()
    {
        if (_closed || _spare is not null || _waiting.Count > 0 || _live >= Cap)
        {
            return;
        }

        _live++;
        NotePeakLocked();
        _spare = StartSpareAsync();
    }

    // Started on the thread pool so that taking the lock never waits for a process. If it fails, whoever takes the spare sees the failure and gives the slot back.
    private Task<IShellEngine> StartSpareAsync() => Task.Run(() => factory.StartAsync(CancellationToken.None));

    private async Task ReleaseAfterAsync(Task<IShellEngine> spare)
    {
        try
        {
            var engine = await spare.ConfigureAwait(false);
            await engine.KillAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // It never started, so there is nothing to kill.
        }

        ReleaseSlot();
    }

    private void DropSlot()
    {
        lock (_gate)
        {
            _live--;
        }
    }

    /// <summary>A slot is free (an engine died or was killed): hand it to the newest waiter, else start a spare.</summary>
    private void ReleaseSlot()
    {
        lock (_gate)
        {
            _live--;
            while (_waiting.Count > 0 && !_closed && _live < Cap)
            {
                var newest = _waiting[^1];
                _waiting.RemoveAt(_waiting.Count - 1);
                if (newest.Granted.TrySetResult(true))
                {
                    _live++; // the slot belongs to the waiter from here on
                    NotePeakLocked();
                    return;
                }
            }

            TopUpSpareLocked();
        }
    }

    internal async ValueTask ReturnAsync(IShellEngine engine)
    {
        try
        {
            await engine.KillAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IShellEngine>? spare;
        List<Waiter> waiting;
        lock (_gate)
        {
            _closed = true;
            spare = _spare;
            _spare = null;
            waiting = [.. _waiting];
            _waiting.Clear();
        }

        foreach (var waiter in waiting)
        {
            waiter.Granted.TrySetException(new EngineWaitException(EngineWaitFailure.Closed));
        }

        if (spare is not null)
        {
            try
            {
                await (await spare.ConfigureAwait(false)).KillAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The spare never started.
            }
        }
    }
}

/// <summary>An engine on loan for one file. Disposing kills it and frees its slot, once.</summary>
internal sealed class EngineLease : IAsyncDisposable
{
    private readonly ShellEnginePool _pool;
    private int _returned;

    internal EngineLease(ShellEnginePool pool, IShellEngine engine)
    {
        _pool = pool;
        Engine = engine;
    }

    public IShellEngine Engine { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            await _pool.ReturnAsync(Engine).ConfigureAwait(false);
        }
    }
}
