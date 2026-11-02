using MikePDF.Core.Layout;

namespace MikePDF.Core.Tiles;

public sealed record TileSchedulerOptions
{
    /// <summary>1 to 2 requests in flight per engine (ADR-0006 decision 4).</summary>
    public int MaxInFlightPerLane { get; init; } = 2;

    public TilePlannerOptions Planner { get; init; } = new();

    /// <summary>A request the engine could not do is not repeated for this long.</summary>
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan EngineLostBackoff { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan TimeoutBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan CancelledBackoff { get; init; } = TimeSpan.FromMilliseconds(40);
}

public sealed class TileSchedulerStats
{
    public long Requested { get; internal set; }

    public long Completed { get; internal set; }

    public long Cancelled { get; internal set; }

    public long Failed { get; internal set; }

    public int MaxInFlightSeen { get; internal set; }
}

/// <summary>
/// Keeps the cache filled with what the view needs (ADR-0006 decision 4). <see cref="Update"/> may be called from any thread as often as the
/// view changes; planning is coalesced. Requests made obsolete by scrolling or zooming are cancelled at once; each request carries a
/// generation that grows with every view update, so the engine side can drop everything older.
/// <list type="bullet">
/// <item>Two lanes, each with its own renderer and limit: sharp tiles (the document's engine) and low-res placeholders (the pool engine), so
/// a slow page parse in the document engine never blocks the placeholder.</item>
/// <item>Uploads to the GPU happen on the worker that received the pixels, never on the UI thread.</item>
/// <item>A failure is reported through <see cref="PageStatus"/> and not retried at once.</item>
/// </list>
/// </summary>
public sealed class TileScheduler : IAsyncDisposable
{
    private sealed class Flight(CancellationTokenSource cts, bool lowRes)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public bool LowRes { get; } = lowRes;
    }

    private readonly ITileRenderer _tiles;
    private readonly ITileRenderer _lowRes;
    private readonly ITilePresenter _presenter;
    private readonly TileCache _cache;
    private readonly TileSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<TileKey, Flight> _inFlight = [];
    private readonly Dictionary<TileKey, long> _retryAt = [];
    private readonly Task _loop;
    private ViewSnapshot? _latest;
    private bool _dirty;
    private bool _planning;
    private ulong _generation;
    private int _disposed;
    private int _wakePending;
    private long _retryWakeAt; // the timestamp a back-off wake-up is already scheduled for, 0 when none (guarded by _gate)

    // tiles: renders sharp tiles (the document's engine). lowRes: renders the placeholders (the pool engine); may be the same renderer.
    public TileScheduler(ITileRenderer tiles, ITileRenderer lowRes, ITilePresenter presenter, TileCache cache, TileSchedulerOptions? options = null, TimeProvider? time = null)
    {
        _tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
        _lowRes = lowRes ?? throw new ArgumentNullException(nameof(lowRes));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? new TileSchedulerOptions();
        _time = time ?? TimeProvider.System;
        _loop = Task.Run(RunAsync);
    }

    public TileSchedulerStats Stats { get; } = new();

    /// <summary>Raised from a worker thread when an image entered the cache (the surface should repaint).</summary>
    public event Action<TileKey>? TileReady;

    /// <summary>Raised from a worker thread when a page could not be drawn completely: <see cref="RenderStatus.Partial"/> or <see cref="RenderStatus.Failed"/>, and <see cref="RenderStatus.EngineLost"/>.</summary>
    public event Action<int, RenderStatus>? PageStatus;

    public ulong Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>Publishes the view. Cheap; the planner runs on the scheduler's own thread.</summary>
    public void Update(ViewSnapshot view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            _latest = view;
            _dirty = true;
            _generation++;
        }

        Wake();
    }

    /// <summary>Waits until nothing is in flight and nothing is left to start (for tests and screenshots).</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (_time.GetUtcNow() < deadline)
        {
            lock (_gate)
            {
                if (_inFlight.Count == 0 && !_dirty && !_planning && Volatile.Read(ref _wakePending) == 0 && !HasStartableWorkLocked())
                {
                    return true;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        List<Flight> flights;
        lock (_gate)
        {
            flights = [.. _inFlight.Values];
        }

        foreach (var f in flights)
        {
            f.Cts.Cancel();
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Let cancelled renders unwind (they hold slots of the ring).
        for (var i = 0; i < 200; i++)
        {
            lock (_gate)
            {
                if (_inFlight.Count == 0)
                {
                    break;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        _signal.Dispose();
        _stop.Dispose();
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                Volatile.Write(ref _wakePending, 0);
                ViewSnapshot? view;
                lock (_gate)
                {
                    view = _latest;
                    _dirty = false;
                    _planning = view is not null;
                }

                if (view is null)
                {
                    continue;
                }

                try
                {
                    Pump(view);
                }
                finally
                {
                    lock (_gate)
                    {
                        _planning = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }
    }

    private TilePlan? _plan;

    private bool HasStartableWorkLocked() => _plan is { } plan && (NextStartable(plan, lowRes: true) is not null || NextStartable(plan, lowRes: false) is not null);

    private void Pump(ViewSnapshot view)
    {
        var plan = TilePlanner.Plan(view, _cache, _options.Planner);
        var starts = new List<(PlannedItem Item, bool LowRes, ulong Generation, CancellationTokenSource Cts)>();
        long retryAt;
        lock (_gate)
        {
            _plan = plan;

            // Obsolete work is cancelled at once.
            foreach (var (key, flight) in _inFlight)
            {
                if (!plan.Wanted.Contains(key))
                {
                    flight.Cts.Cancel();
                }
            }

            foreach (var lowRes in new[] { true, false })
            {
                while (!_stop.IsCancellationRequested && InFlightCountLocked(lowRes) < _options.MaxInFlightPerLane && NextStartable(plan, lowRes) is { } item)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _inFlight[item.Key] = new Flight(cts, lowRes);
                    Stats.Requested++;
                    Stats.MaxInFlightSeen = Math.Max(Stats.MaxInFlightSeen, InFlightCountLocked(lowRes));
                    starts.Add((item, lowRes, _generation, cts));
                }
            }

            retryAt = EarliestBackOffLocked(plan);
        }

        ScheduleRetryWake(retryAt);

        foreach (var (item, lowRes, generation, cts) in starts)
        {
            _ = Task.Run(() => RunOneAsync(item, lowRes, generation, cts));
        }
    }

    /// <summary>When the first wanted tile that is only waiting out its back-off may be tried again, or 0 when none is. Gate held.</summary>
    private long EarliestBackOffLocked(TilePlan plan)
    {
        var now = _time.GetTimestamp();
        var earliest = 0L;
        foreach (var key in plan.Wanted)
        {
            if (_retryAt.TryGetValue(key, out var at) && now < at && !_inFlight.ContainsKey(key) && !_cache.Contains(key) && (earliest == 0 || at < earliest))
            {
                earliest = at;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Makes sure the scheduler looks again when the earliest back-off is over. A timer started for a back-off is not enough on its own: a timer can fire a little before the
    /// timestamp it was meant to reach (timers count whole milliseconds, the clock does not), the look-again then finds the tile still backing off and starts nothing, and with no
    /// other event to follow it the tile is never tried again. So the look that finds a tile still waiting is the one that sets the next timer, for what is left of the wait.
    /// </summary>
    private void ScheduleRetryWake(long at)
    {
        if (at == 0 || _stop.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (_retryWakeAt != 0 && _retryWakeAt <= at)
            {
                return; // a wake-up at or before this one is on its way and will look again
            }

            _retryWakeAt = at;
        }

        var remaining = TimeSpan.FromSeconds((double)(at - _time.GetTimestamp()) / _time.TimestampFrequency);
        var wait = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
        _ = Task.Delay(wait, _time).ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (_retryWakeAt == at)
                    {
                        _retryWakeAt = 0;
                    }
                }

                Wake();
            },
            TaskScheduler.Default);
    }

    private int InFlightCountLocked(bool lowRes) => _inFlight.Values.Count(f => f.LowRes == lowRes);

    /// <summary>The next item of a lane that is not cached, not in flight and not backing off. Gate held.</summary>
    private PlannedItem? NextStartable(TilePlan plan, bool lowRes)
    {
        var now = _time.GetTimestamp();
        IEnumerable<PlannedItem> candidates = lowRes
            ? plan.VisibleLowRes.Concat(plan.AheadLowRes)
            : plan.VisibleTiles.Concat(plan.Prefetch);
        foreach (var item in candidates)
        {
            if (_inFlight.ContainsKey(item.Key) || _cache.Contains(item.Key))
            {
                continue;
            }

            if (_retryAt.TryGetValue(item.Key, out var at) && now < at)
            {
                continue;
            }

            return item;
        }

        return null;
    }

    private async Task RunOneAsync(PlannedItem item, bool lowRes, ulong generation, CancellationTokenSource cts)
    {
        var status = RenderStatus.Failed;
        try
        {
            var request = new RenderRequest(item.Key, item.Scale, item.Turns, item.X, item.Y, item.Width, item.Height, generation);
            RenderResult result;
            try
            {
                result = await (lowRes ? _lowRes : _tiles).RenderAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new RenderResult(RenderStatus.Cancelled);
            }

            using (result)
            {
                status = result.Status;
                if (result.Status is RenderStatus.Ok or RenderStatus.Partial && result.Pixels is { } pixels && !cts.IsCancellationRequested)
                {
                    // Even a tile that became obsolete while rendering is worth keeping if it is already here; but not if we are stopping.
                    var surface = _presenter.CreateSurface(pixels);
                    _cache.Add(item.Key, surface);
                    lock (_gate)
                    {
                        Stats.Completed++;
                    }

                    Raise(() => TileReady?.Invoke(item.Key));
                    if (result.Status == RenderStatus.Partial)
                    {
                        Raise(() => PageStatus?.Invoke(item.Key.Page, RenderStatus.Partial));
                    }
                }
                else if (result.Status is RenderStatus.Ok or RenderStatus.Partial)
                {
                    status = RenderStatus.Cancelled;
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or IOException or ArgumentException)
        {
            // The presenter failed to create the surface (for example the GPU device was lost). Not fatal; the tile is retried later.
            status = RenderStatus.Failed;
        }
        finally
        {
            var backoff = status switch
            {
                RenderStatus.Failed => _options.FailureBackoff,
                RenderStatus.EngineLost => _options.EngineLostBackoff,
                RenderStatus.Timeout => _options.TimeoutBackoff,
                RenderStatus.Cancelled => _options.CancelledBackoff,
                _ => TimeSpan.Zero,
            };
            lock (_gate)
            {
                _inFlight.Remove(item.Key);
                if (backoff > TimeSpan.Zero)
                {
                    _retryAt[item.Key] = _time.GetTimestamp() + (long)(backoff.TotalSeconds * _time.TimestampFrequency);
                }
                else
                {
                    _retryAt.Remove(item.Key);
                }

                switch (status)
                {
                    case RenderStatus.Cancelled:
                        Stats.Cancelled++;
                        break;
                    case RenderStatus.Failed or RenderStatus.Timeout or RenderStatus.EngineLost:
                        Stats.Failed++;
                        break;
                    default:
                        break;
                }

                _dirty = true;
            }

            cts.Dispose();
            if (status is RenderStatus.Failed or RenderStatus.EngineLost)
            {
                Raise(() => PageStatus?.Invoke(item.Key.Page, status));
            }

            // The look-again that follows (Wake) finds the tile backing off and schedules the wake-up for the end of the back-off (ScheduleRetryWake).
            Wake();
        }
    }

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A subscriber's failure must not stop rendering.
        }
    }
}
