using MikePDF.Core.Layout;

namespace MikePDF.Core.Windowing;

/// <summary>What a tab shows.</summary>
public enum TabKind
{
    /// <summary>A document (live, or a placeholder that opens when first selected).</summary>
    Document,

    /// <summary>No document: the empty state with the recent files.</summary>
    Empty,

    /// <summary>The Settings page. One per window.</summary>
    Settings,
}

/// <summary>Where a document was left in a tab: kept with the tab, in the session file and in the closed-tab stack.</summary>
public sealed record TabViewState(int Page, double PageFraction, double Zoom, FitMode Fit, int ViewTurns)
{
    public static TabViewState Start { get; } = new(0, 0, 1.0, FitMode.Width, 0);
}

/// <summary>
/// One tab, as plain data (docs/features/tabs-windows-session.md 3.1). The id is a GUID that is never reused. The window code keeps the live parts (the session, the strip item) beside it,
/// keyed by <see cref="Id"/>; nothing here knows about XAML.
/// </summary>
public sealed class TabModel
{
    public TabModel(TabKind kind, string? path = null, DocumentIdentity? identity = null)
    {
        Id = Guid.NewGuid();
        Kind = kind;
        Path = path;
        Identity = identity ?? default;
    }

    public Guid Id { get; }

    public TabKind Kind { get; set; }

    /// <summary>The full path of the document; null for an empty tab and for Settings.</summary>
    public string? Path { get; set; }

    public DocumentIdentity Identity { get; set; }

    /// <summary>The title in the document's own information, when it has one.</summary>
    public string? DocumentTitle { get; set; }

    public TabViewState View { get; set; } = TabViewState.Start;

    /// <summary>The tab has not opened its document yet (a restored session opens only the active tab of each window).</summary>
    public bool IsSuspended { get; set; }

    /// <summary>Unsaved edits. Nothing edits a document yet, so this is false outside tests; the close prompts are built on it so the save pipeline only has to set it.</summary>
    public bool IsDirty { get; set; }

    public bool HasDocument => Kind == TabKind.Document && !string.IsNullOrEmpty(Path);
}

/// <summary>The text of the tabs of one window (docs/features/tabs-windows-session.md 3.3).</summary>
public static class TabTitles
{
    /// <summary>The title of one tab: the document's own title if it has one, else the file name without its extension.</summary>
    public static string BaseTitle(TabModel tab, string emptyTitle, string settingsTitle)
    {
        switch (tab.Kind)
        {
            case TabKind.Empty:
                return emptyTitle;
            case TabKind.Settings:
                return settingsTitle;
            default:
                if (!string.IsNullOrWhiteSpace(tab.DocumentTitle))
                {
                    return tab.DocumentTitle.Trim();
                }

                return string.IsNullOrEmpty(tab.Path) ? emptyTitle : System.IO.Path.GetFileNameWithoutExtension(tab.Path);
        }
    }

    /// <summary>
    /// The titles of the tabs of one window, in order. Two tabs with the same title show the name of their parent folder after it, using <paramref name="withFolder"/>
    /// (<c>"{0} ({1})"</c> in the resources, so a translation can order it).
    /// </summary>
    public static IReadOnlyList<string> For(IReadOnlyList<TabModel> tabs, string emptyTitle, string settingsTitle, Func<string, string, string> withFolder)
    {
        var titles = tabs.Select(t => BaseTitle(t, emptyTitle, settingsTitle)).ToList();
        var result = new List<string>(titles.Count);
        for (var i = 0; i < tabs.Count; i++)
        {
            var duplicate = tabs[i].HasDocument && titles.Where((t, j) => j != i && tabs[j].HasDocument && string.Equals(t, titles[i], StringComparison.OrdinalIgnoreCase)).Any();
            result.Add(duplicate ? withFolder(titles[i], ParentFolderName(tabs[i].Path!)) : titles[i]);
        }

        return result;
    }

    /// <summary>The name of the folder a file is in (the drive for a file at the root).</summary>
    public static string ParentFolderName(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        var name = System.IO.Path.GetFileName(directory);
        return string.IsNullOrEmpty(name) ? directory : name;
    }
}
