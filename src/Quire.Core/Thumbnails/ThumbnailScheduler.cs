namespace Quire.Core.Thumbnails;

/// <summary>
/// Decides which thumbnails of the pane get drawn and in what order (ADR-0006: they come from the pool engine, so the document's own engine is
/// never held up). A list shows only a screenful of items and recycles them as it scrolls, so requests come and go quickly: the page asked for
/// last is drawn first, a page that scrolled away before its turn is never drawn, and only a couple are in flight at once. Finished
/// thumbnails are kept (the most recent <c>capacity</c> of them) so scrolling back is instant.
/// <para>
/// A render that has already started is not interrupted: it takes a few milliseconds, and cancelling it would also cancel renders the engine
/// holds for other thumbnails. Its result is kept either way.
/// </para>
/// </summary>
/// <typeparam name="T">The finished thumbnail: pixels, usually.</typeparam>
public sealed class ThumbnailScheduler<T> : IDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly Func<int, CancellationToken, Task<T?>> _render;
    private readonly int _maxInFlight;
    private readonly int _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkedList<int> _pending = new();
    private readonly Dictionary<int, LinkedListNode<int>> _pendingNodes = [];
    private readonly HashSet<int> _inFlight = [];
    private readonly Dictionary<int, (T Thumbnail, long Used)> _done = [];
    private long _clock;
    private bool _disposed;

    public ThumbnailScheduler(Func<int, CancellationToken, Task<T?>> render, int maxInFlight = 2, int capacity = 160)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _render = render;
        _maxInFlight = maxInFlight;
        _capacity = capacity;
    }

    /// <summary>A thumbnail finished. Raised from a worker thread; a handler that touches the UI marshals.</summary>
    public event Action<int, T>? Ready;

    /// <summary>The thumbnail of a page, if it has been drawn and is still kept.</summary>
    public bool TryGet(int page, out T thumbnail)
    {
        lock (_gate)
        {
            if (_done.TryGetValue(page, out var entry))
            {
                _done[page] = (entry.Thumbnail, ++_clock);
                thumbnail = entry.Thumbnail;
                return true;
            }
        }

        thumbnail = null!;
        return false;
    }

    /// <summary>Asks for a page's thumbnail. Asking again moves it to the front of the queue.</summary>
    public void Request(int page)
    {
        lock (_gate)
        {
            if (_disposed || _done.ContainsKey(page) || _inFlight.Contains(page))
            {
                return;
            }

            if (_pendingNodes.Remove(page, out var existing))
            {
                _pending.Remove(existing);
            }

            _pendingNodes[page] = _pending.AddFirst(page);
        }

        Pump();
    }

    /// <summary>The page is no longer wanted (its item scrolled away). A render already under way finishes.</summary>
    public void Cancel(int page)
    {
        lock (_gate)
        {
            if (_pendingNodes.Remove(page, out var node))
            {
                _pending.Remove(node);
            }
        }
    }

    /// <summary>Forgets every thumbnail and drops every request (the document changed, or the window shrank).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingNodes.Clear();
            _done.Clear();
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _pendingNodes.Clear();
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private void Pump()
    {
        while (true)
        {
            int page;
            lock (_gate)
            {
                if (_disposed || _inFlight.Count >= _maxInFlight || _pending.First is null)
                {
                    return;
                }

                page = _pending.First.Value;
                _pending.RemoveFirst();
                _pendingNodes.Remove(page);
                _inFlight.Add(page);
            }

            _ = RunAsync(page);
        }
    }

    private async Task RunAsync(int page)
    {
        T? thumbnail = null;
        try
        {
            thumbnail = await Task.Run(() => _render(page, _stop.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // Closing.
        }

        lock (_gate)
        {
            _inFlight.Remove(page);
            if (thumbnail is not null && !_disposed)
            {
                _done[page] = (thumbnail, ++_clock);
                Trim();
            }
        }

        if (thumbnail is not null)
        {
            Ready?.Invoke(page, thumbnail);
        }

        Pump();
    }

    private void Trim()
    {
        while (_done.Count > _capacity)
        {
            var oldest = _done.MinBy(e => e.Value.Used).Key;
            _done.Remove(oldest);
        }
    }
}
