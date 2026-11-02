using Quire.Protocol;

namespace Quire.Core.Reading;

/// <summary>
/// Per-page data fetched on demand and kept for a while (page text, links, boxes): at most <c>capacity</c> pages, least recently used out
/// first. A page asked for twice at once is fetched once. A failure is remembered only when asking again would give the same answer; a
/// failure that an engine restart cures (a crash, a timeout, a cancellation) is forgotten so the next ask tries again.
/// </summary>
public sealed class PageDataCache<T> : IDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly Func<int, CancellationToken, Task<ReadReply<T>>> _load;
    private readonly int _capacity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private long _clock;

    private sealed class Entry(Task<ReadReply<T>> task)
    {
        public Task<ReadReply<T>> Task { get; } = task;

        public long LastUsed { get; set; }
    }

    public PageDataCache(Func<int, CancellationToken, Task<ReadReply<T>>> load, int capacity)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _load = load;
        _capacity = capacity;
    }

    /// <summary>A page finished loading successfully. Raised from the thread that finished the load.</summary>
    public event Action<int>? Loaded;

    /// <summary>The page's data, from the cache or fetched. Cancelling <paramref name="cancellationToken"/> stops waiting, not the fetch other callers share.</summary>
    public Task<ReadReply<T>> GetAsync(int page, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(page, out entry!))
            {
                entry = new Entry(Run(page));
                _entries[page] = entry;
            }

            entry.LastUsed = ++_clock;
            Trim();
        }

        return cancellationToken.CanBeCanceled ? entry.Task.WaitAsync(cancellationToken) : entry.Task;
    }

    /// <summary>The page's data if it is already loaded.</summary>
    public bool TryGetLoaded(int page, out T value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(page, out var entry) && entry.Task.IsCompletedSuccessfully && entry.Task.Result is { IsOk: true, Value: { } loaded })
            {
                entry.LastUsed = ++_clock;
                value = loaded;
                return true;
            }
        }

        value = null!;
        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        Clear();
    }

    private async Task<ReadReply<T>> Run(int page)
    {
        ReadReply<T> reply;
        try
        {
            reply = await Task.Run(() => _load(page, _lifetime.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            reply = ReadReply.Fail<T>(Status.Cancelled);
        }

        if (reply.IsOk)
        {
            Loaded?.Invoke(page);
        }
        else if (reply.Status is Status.Cancelled or Status.Timeout or Status.EngineCrashed)
        {
            lock (_gate)
            {
                _entries.Remove(page); // ask again next time
            }
        }

        return reply;
    }

    // Called with the gate held: evict the least recently used completed entries beyond the capacity.
    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            var oldest = -1;
            var oldestTime = long.MaxValue;
            foreach (var (page, entry) in _entries)
            {
                if (entry.Task.IsCompleted && entry.LastUsed < oldestTime)
                {
                    oldest = page;
                    oldestTime = entry.LastUsed;
                }
            }

            if (oldest < 0)
            {
                return; // everything is in flight
            }

            _entries.Remove(oldest);
        }
    }
}
