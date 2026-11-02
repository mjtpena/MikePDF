namespace MikePDF.Core.Navigation;

/// <summary>Where the view was: the page at the top of the window and how far down that page the top of the window is (0 to 1).</summary>
public readonly record struct ViewLocation(int Page, double Fraction)
{
    /// <summary>Two locations are the same place when they are on one page and within 1% of the page's height of each other.</summary>
    public bool IsSamePlaceAs(ViewLocation other) => Page == other.Page && Math.Abs(Fraction - other.Fraction) < 0.01;
}

/// <summary>
/// Back and forward for one document (master prompt 2.1). Only <i>jumps</i> are recorded (a link, Go to page, a bookmark, a thumbnail, a find
/// match), never scrolling: the caller says where the view was just before it jumped. Each direction holds at most
/// <see cref="Capacity"/> entries and drops the oldest. Not thread-safe; use it on the UI thread.
/// <para>
/// A jump may carry a coalescing key. Moving through the matches of one search records one entry, the place the search began: a jump whose key is
/// the key of the last recorded jump is not recorded again. Any jump with another key (or none), a Back, a Forward and <see cref="ResetKey"/>
/// end the run.
/// </para>
/// </summary>
public sealed class NavigationHistory
{
    public const int DefaultCapacity = 64;

    private readonly List<ViewLocation> _back = [];
    private readonly List<ViewLocation> _forward = [];
    private string? _lastKey;

    public NavigationHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int BackCount => _back.Count;

    public int ForwardCount => _forward.Count;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Records that the view jumped away from <paramref name="from"/>. Clears the forward list. Returns whether anything was recorded.</summary>
    public bool Record(ViewLocation from, string? coalesceKey = null)
    {
        if (coalesceKey is not null && coalesceKey == _lastKey)
        {
            return false;
        }

        _lastKey = coalesceKey;
        if (_back.Count > 0 && _back[^1].IsSamePlaceAs(from))
        {
            return false; // the same place again: going back would go nowhere
        }

        _forward.Clear();
        Push(_back, from);
        return true;
    }

    /// <summary>The place to go back to, given where the view is now (which becomes what Forward returns). Null when there is none.</summary>
    public ViewLocation? Back(ViewLocation current)
    {
        _lastKey = null;
        return Step(_back, _forward, current);
    }

    /// <summary>The place to go forward to, given where the view is now (which becomes what Back returns). Null when there is none.</summary>
    public ViewLocation? Forward(ViewLocation current)
    {
        _lastKey = null;
        return Step(_forward, _back, current);
    }

    /// <summary>Ends a run of coalesced jumps (a new search starts a new one).</summary>
    public void ResetKey() => _lastKey = null;

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
        _lastKey = null;
    }

    private ViewLocation? Step(List<ViewLocation> from, List<ViewLocation> to, ViewLocation current)
    {
        // An entry that is where the view already is would make the button do nothing: skip it.
        while (from.Count > 0 && from[^1].IsSamePlaceAs(current))
        {
            from.RemoveAt(from.Count - 1);
        }

        if (from.Count == 0)
        {
            return null;
        }

        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        if (to.Count == 0 || !to[^1].IsSamePlaceAs(current))
        {
            Push(to, current);
        }

        return target;
    }

    private void Push(List<ViewLocation> list, ViewLocation location)
    {
        list.Add(location);
        if (list.Count > Capacity)
        {
            list.RemoveRange(0, list.Count - Capacity);
        }
    }
}
