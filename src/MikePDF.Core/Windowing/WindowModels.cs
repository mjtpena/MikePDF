namespace MikePDF.Core.Windowing;

/// <summary>
/// The tabs of one window, in order, and which is active (docs/features/tabs-windows-session.md 3.1). Plain C#: the rules are tested without XAML, and the window binds to it.
/// A window never has no tab: closing the last one leaves an empty tab (docs/design/screens/01-main-window.md 3.1.4), so Ctrl+W pressed twice cannot quit the app.
/// </summary>
public sealed class WindowModel
{
    /// <summary>The most tabs one window holds; the next one opens in a new window (3.3, proposed number).</summary>
    public const int MaxTabs = 50;

    private readonly List<TabModel> _tabs = [];

    public WindowModel()
    {
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    public IReadOnlyList<TabModel> Tabs => _tabs;

    public Guid ActiveId { get; private set; }

    public TabModel? Active => _tabs.FirstOrDefault(t => t.Id == ActiveId);

    public int ActiveIndex => _tabs.FindIndex(t => t.Id == ActiveId);

    public bool IsFull => _tabs.Count >= MaxTabs;

    public int IndexOf(Guid id) => _tabs.FindIndex(t => t.Id == id);

    public TabModel? Find(Guid id) => _tabs.FirstOrDefault(t => t.Id == id);

    public TabModel? Find(DocumentIdentity identity) => identity.IsEmpty ? null : _tabs.FirstOrDefault(t => t.HasDocument && t.Identity == identity);

    public TabModel? FirstOfKind(TabKind kind) => _tabs.FirstOrDefault(t => t.Kind == kind);

    /// <summary>Puts a tab in at <paramref name="index"/> (clamped). False when the window is full.</summary>
    public bool Insert(int index, TabModel tab, bool activate)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (IsFull || _tabs.Any(t => t.Id == tab.Id))
        {
            return false;
        }

        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        if (activate || ActiveId == Guid.Empty)
        {
            ActiveId = tab.Id;
        }

        return true;
    }

    public bool Activate(Guid id)
    {
        if (_tabs.All(t => t.Id != id))
        {
            return false;
        }

        ActiveId = id;
        return true;
    }

    /// <summary>
    /// Takes a tab out. If it was the active one, the tab to its right becomes active (or the one to its left when it was last). Returns the tab, or null if it was not here. The window can be left
    /// with no tab: <see cref="EnsureNotEmpty"/> puts an empty one in.
    /// </summary>
    public TabModel? Remove(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return null;
        }

        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        if (ActiveId == id)
        {
            ActiveId = _tabs.Count == 0 ? Guid.Empty : _tabs[Math.Min(index, _tabs.Count - 1)].Id;
        }

        return tab;
    }

    /// <summary>Adds an empty tab if there is no tab at all. Returns it, or null when nothing was needed.</summary>
    public TabModel? EnsureNotEmpty()
    {
        if (_tabs.Count > 0)
        {
            return null;
        }

        var empty = new TabModel(TabKind.Empty);
        Insert(0, empty, activate: true);
        return empty;
    }

    /// <summary>Moves a tab to a new index (as an index in the list after the tab is taken out). False when nothing changed.</summary>
    public bool Move(Guid id, int newIndex)
    {
        var from = IndexOf(id);
        newIndex = Math.Clamp(newIndex, 0, _tabs.Count - 1);
        if (from < 0 || from == newIndex)
        {
            return false;
        }

        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(newIndex, tab);
        return true;
    }

    /// <summary>Puts the tabs in the order given (ids not named keep their relative order at the end). For when the strip was reordered by dragging.</summary>
    public void Reorder(IEnumerable<Guid> order)
    {
        var ranked = order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        var sorted = _tabs.OrderBy(t => ranked.TryGetValue(t.Id, out var r) ? r : int.MaxValue).ToList();
        _tabs.Clear();
        _tabs.AddRange(sorted);
    }

    /// <summary>The next tab, wrapping round; null with fewer than two tabs.</summary>
    public TabModel? Next(int step)
    {
        if (_tabs.Count < 2)
        {
            return null;
        }

        var index = (ActiveIndex + step) % _tabs.Count;
        return _tabs[index < 0 ? index + _tabs.Count : index];
    }

    /// <summary>Tab number <paramref name="number"/> (1 based), or null when there is none.</summary>
    public TabModel? ByNumber(int number) => number >= 1 && number <= _tabs.Count ? _tabs[number - 1] : null;

    public TabModel? Last => _tabs.Count == 0 ? null : _tabs[^1];

    /// <summary>The tabs that Close other tabs would close, and Close tabs to the right.</summary>
    public IReadOnlyList<TabModel> Others(Guid keep) => [.. _tabs.Where(t => t.Id != keep)];

    public IReadOnlyList<TabModel> ToTheRightOf(Guid id)
    {
        var index = IndexOf(id);
        return index < 0 ? [] : [.. _tabs.Skip(index + 1)];
    }
}

/// <summary>A tab that was closed: enough to open it again (the path and the view; never edits, which were saved or discarded on closing).</summary>
public sealed record ClosedTab(Guid WindowId, string Path, TabViewState View);

/// <summary>The last tabs that were closed, most recent first (Ctrl+Shift+T). Ten, for this run of the app only: it is not persisted (3.5).</summary>
public sealed class ClosedTabHistory
{
    public const int Capacity = 10;

    private readonly List<ClosedTab> _items = [];

    public int Count => _items.Count;

    public void Push(ClosedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _items.Insert(0, tab);
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        }
    }

    public ClosedTab? Pop()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        var top = _items[0];
        _items.RemoveAt(0);
        return top;
    }

    public void Clear() => _items.Clear();
}

/// <summary>Where a request to open a file came from. It decides where the new tab goes (3.2).</summary>
public enum OpenSource
{
    /// <summary>Ctrl+O, a drop, the Recent list: right of the active tab.</summary>
    User,

    /// <summary>Explorer, the alias, the Jump List: at the end of the strip.</summary>
    Outside,
}

public enum OpenAction
{
    /// <summary>A tab for the file exists: show it, and its window.</summary>
    ActivateExisting,

    /// <summary>The active tab is empty: open the file in it.</summary>
    ReuseEmpty,

    /// <summary>A new tab in <see cref="OpenDecision.Window"/> at <see cref="OpenDecision.Index"/>.</summary>
    NewTab,

    /// <summary>A new window, with the file in its first tab.</summary>
    NewWindow,

    /// <summary>Too many documents are open: the file is not opened.</summary>
    Refused,
}

public sealed record OpenDecision(OpenAction Outcome, WindowModel? Window, TabModel? Tab, int Index)
{
    public static OpenDecision Refuse { get; } = new(OpenAction.Refused, null, null, 0);
}

/// <summary>The windows of the app, the one used last first, and the rules for where a document opens (docs/features/tabs-windows-session.md 3.2).</summary>
public sealed class AppModel
{
    /// <summary>
    /// The most documents open at once in the whole app. Each open document holds <b>two</b> documents in the shared pool engine (one for its placeholders, one for text and search), and
    /// the pool engine allows 32, so 16 documents is what fits; a restored tab that has not opened its document holds none, and does not count. The spec's text says 32; that was before
    /// the two were counted (see the work package note).
    /// </summary>
    public const int MaxOpenDocuments = 16;

    private readonly List<WindowModel> _windows = [];

    public AppModel(ClosedTabHistory? closed = null)
    {
        Closed = closed ?? new ClosedTabHistory();
    }

    public ClosedTabHistory Closed { get; }

    /// <summary>Every window, the most recently active first.</summary>
    public IReadOnlyList<WindowModel> Windows => _windows;

    public WindowModel? LastActive => _windows.Count == 0 ? null : _windows[0];

    public WindowModel NewWindow(bool makeActive = true)
    {
        var window = new WindowModel();
        window.EnsureNotEmpty();
        if (makeActive)
        {
            _windows.Insert(0, window);
        }
        else
        {
            _windows.Add(window);
        }

        return window;
    }

    public void Remove(WindowModel window) => _windows.Remove(window);

    /// <summary>Marks a window as the one used last.</summary>
    public void Touch(WindowModel window)
    {
        if (_windows.Remove(window))
        {
            _windows.Insert(0, window);
        }
    }

    /// <summary>The tab (and its window) that shows a document, in any window.</summary>
    public (WindowModel Window, TabModel Tab)? Find(DocumentIdentity identity)
    {
        foreach (var window in _windows)
        {
            if (window.Find(identity) is { } tab)
            {
                return (window, tab);
            }
        }

        return null;
    }

    /// <summary>How many documents are open in all windows (tabs that hold a live document; a restored placeholder does not).</summary>
    public int OpenDocumentCount => _windows.Sum(w => w.Tabs.Count(t => t.HasDocument && !t.IsSuspended));

    /// <summary>
    /// Where a file opens. The same file in any window: that tab. Else the active tab if it is empty, else a new tab (right of the active tab for <see cref="OpenSource.User"/>, at the end for
    /// <see cref="OpenSource.Outside"/>), in <paramref name="preferred"/> (or the window used last); a full window sends it to a new window; too many documents refuse it. The tab is
    /// not created here: the caller does that, so a failed open leaves nothing behind.
    /// </summary>
    public OpenDecision Plan(DocumentIdentity identity, OpenSource source, WindowModel? preferred, bool forceNewWindow = false)
    {
        if (Find(identity) is { } existing)
        {
            return new OpenDecision(OpenAction.ActivateExisting, existing.Window, existing.Tab, existing.Window.IndexOf(existing.Tab.Id));
        }

        if (OpenDocumentCount >= MaxOpenDocuments)
        {
            return OpenDecision.Refuse;
        }

        var window = forceNewWindow ? null : preferred ?? LastActive;
        if (window is null || window.IsFull)
        {
            return new OpenDecision(OpenAction.NewWindow, null, null, 0);
        }

        if (window.Active is { Kind: TabKind.Empty } empty)
        {
            return new OpenDecision(OpenAction.ReuseEmpty, window, empty, window.ActiveIndex);
        }

        var index = source == OpenSource.User ? window.ActiveIndex + 1 : window.Tabs.Count;
        return new OpenDecision(OpenAction.NewTab, window, null, index);
    }
}

/// <summary>What closing some tabs needs to ask the person (docs/features/tabs-windows-session.md 3.5).</summary>
public static class ClosePlan
{
    /// <summary>The tabs among <paramref name="closing"/> that have unsaved edits. Empty: close without asking.</summary>
    public static IReadOnlyList<TabModel> Dirty(IEnumerable<TabModel> closing) => [.. closing.Where(t => t.IsDirty)];
}

/// <summary>How many files an activation may open without asking, and how many it ever opens.</summary>
public static class ActivationLimits
{
    /// <summary>More than this asks "Open all N files?" first (decisions.md Q12). The spec's number was 25; the decision log says 10 and was written later.</summary>
    public const int ConfirmAbove = 10;

    /// <summary>The most files one activation is read for: a hostile or accidental launch cannot start hundreds of documents.</summary>
    public const int Maximum = 100;

    public static bool NeedsConfirmation(int count) => count > ConfirmAbove;
}
