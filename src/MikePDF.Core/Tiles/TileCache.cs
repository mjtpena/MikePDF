using System.Collections.Concurrent;

namespace MikePDF.Core.Tiles;

/// <summary>
/// The GPU-tier LRU of ADR-0006 decision 5: one cache with a byte budget, true cost counted as nominal size times a factor (2.0 for Win2D,
/// which keeps a CPU copy as well). Tiles drawn in the current frame are never evicted; low-res placeholders go last. Eviction is to 90% of
/// the budget. Evicted surfaces are not disposed here: they are queued for <see cref="DisposeEvicted"/>, which the owner calls on the thread
/// the surfaces may be disposed on (the UI thread, for Win2D), so that a surface is never disposed while a frame is drawing it.
/// </summary>
public sealed class TileCache
{
    private sealed class Entry(ITileSurface surface, long cost)
    {
        public ITileSurface Surface { get; } = surface;

        public long Cost { get; } = cost;

        public long LastUsed { get; set; }

        public long PinnedFrame { get; set; } = -1;
    }

    public const double TargetFractionAfterEviction = 0.9;

    private readonly object _gate = new();
    private readonly Dictionary<TileKey, Entry> _entries = [];
    private readonly Dictionary<int, HashSet<TileKey>> _byPage = [];
    private readonly ConcurrentQueue<ITileSurface> _evicted = new();
    private readonly double _costFactor;
    private long _budget;
    private long _bytes;
    private long _clock;
    private long _frame;

    public TileCache(long budgetBytes, double costFactor = 2.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _budget = budgetBytes;
        _costFactor = costFactor;
    }

    /// <summary>clamp(2% of physical RAM, 96 MB, 256 MB) (ADR-0006).</summary>
    public static long DefaultBudget(long physicalMemoryBytes) => Math.Clamp(physicalMemoryBytes / 50, 96L << 20, 256L << 20);

    public long BudgetBytes
    {
        get
        {
            lock (_gate)
            {
                return _budget;
            }
        }
    }

    /// <summary>True cost of everything cached.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long EvictedCount { get; private set; }

    /// <summary>Starts a frame. What <see cref="Touch"/> marks from now on is protected from eviction until the next frame starts.</summary>
    public void BeginFrame()
    {
        lock (_gate)
        {
            _frame++;
        }
    }

    public bool Contains(TileKey key)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    public bool TryGet(TileKey key, out ITileSurface surface)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.LastUsed = ++_clock;
                surface = entry.Surface;
                return true;
            }
        }

        surface = null!;
        return false;
    }

    /// <summary>Like <see cref="TryGet"/>, and the entry is protected from eviction for the current frame (it is on screen).</summary>
    public bool Touch(TileKey key, out ITileSurface surface)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.LastUsed = ++_clock;
                entry.PinnedFrame = _frame;
                surface = entry.Surface;
                return true;
            }
        }

        surface = null!;
        return false;
    }

    /// <summary>The keys cached for a page (any scale), for the fallback of a zoom change.</summary>
    public List<TileKey> KeysForPage(int page)
    {
        lock (_gate)
        {
            return _byPage.TryGetValue(page, out var keys) ? [.. keys] : [];
        }
    }

    /// <summary>Adds (or replaces) an image and evicts as needed. The cache owns the surface from here on.</summary>
    public void Add(TileKey key, ITileSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var cost = (long)(surface.Width * (long)surface.Height * 4 * _costFactor);
        lock (_gate)
        {
            if (_entries.Remove(key, out var old))
            {
                _bytes -= old.Cost;
                _evicted.Enqueue(old.Surface);
            }

            var entry = new Entry(surface, cost) { LastUsed = ++_clock };
            _entries[key] = entry;
            if (!_byPage.TryGetValue(key.Page, out var set))
            {
                _byPage[key.Page] = set = [];
            }

            set.Add(key);
            _bytes += cost;
            EvictLocked(_budget, key);
        }
    }

    /// <summary>Sets a new budget (for example after a memory-pressure signal) and evicts to it.</summary>
    public void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        lock (_gate)
        {
            _budget = budgetBytes;
            EvictLocked(_budget);
        }
    }

    /// <summary>On minimise or a memory-pressure signal: drops everything that is not on screen in the current frame.</summary>
    public void TrimToVisible()
    {
        lock (_gate)
        {
            foreach (var key in _entries.Where(e => e.Value.PinnedFrame != _frame).Select(e => e.Key).ToList())
            {
                RemoveLocked(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                RemoveLocked(key);
            }
        }
    }

    /// <summary>Disposes the surfaces evicted so far. Call on the thread that owns the GPU resources.</summary>
    public int DisposeEvicted()
    {
        var count = 0;
        while (_evicted.TryDequeue(out var surface))
        {
            surface.Dispose();
            count++;
        }

        return count;
    }

    private void EvictLocked(long budget, TileKey? keep = null)
    {
        if (_bytes <= budget)
        {
            return;
        }

        var target = (long)(budget * TargetFractionAfterEviction);

        // Oldest tiles first, placeholders last; whatever is pinned in this frame stays even if that leaves the cache over budget.
        foreach (var (key, _) in _entries.Where(e => e.Value.PinnedFrame != _frame && e.Key != keep).OrderBy(e => e.Key.IsLowRes ? 1 : 0).ThenBy(e => e.Value.LastUsed).ToList())
        {
            if (_bytes <= target)
            {
                break;
            }

            RemoveLocked(key);
        }
    }

    private void RemoveLocked(TileKey key)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return;
        }

        _bytes -= entry.Cost;
        EvictedCount++;
        _evicted.Enqueue(entry.Surface);
        if (_byPage.TryGetValue(key.Page, out var set))
        {
            set.Remove(key);
            if (set.Count == 0)
            {
                _byPage.Remove(key.Page);
            }
        }
    }
}
