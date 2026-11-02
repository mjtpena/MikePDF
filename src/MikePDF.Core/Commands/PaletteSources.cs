using System.Globalization;

namespace MikePDF.Core.Commands;

/// <summary>Every registered command, enabled or not, with its current shortcut and, when it cannot run, why.</summary>
public sealed class CommandPaletteSource : IPaletteSource
{
    private readonly CommandRegistry _registry;
    private readonly Func<CommandContext> _context;
    private readonly Func<string, string> _text;
    private readonly Func<Chord, string> _format;

    /// <param name="registry">The commands and their shortcuts.</param>
    /// <param name="context">What the window knows now; asked each time the rows are built.</param>
    /// <param name="text">Resource text by key (the window supplies its resources; tests supply a table).</param>
    /// <param name="format">How a chord is shown on this keyboard layout.</param>
    public CommandPaletteSource(CommandRegistry registry, Func<CommandContext> context, Func<string, string> text, Func<Chord, string>? format = null)
    {
        _registry = registry;
        _context = context;
        _text = text;
        _format = format ?? (chord => ChordDisplay.Format(chord));
    }

    public IReadOnlyList<PaletteItem> Items(string query)
    {
        var context = _context();
        var items = new List<PaletteItem>(_registry.Commands.Count);
        foreach (var command in _registry.Commands)
        {
            var availability = command.CanExecute(context);
            var chords = _registry.EffectiveChords(command.Id);
            items.Add(new PaletteItem(
                PaletteItemKind.Command,
                command.Id,
                _text(command.NameKey),
                _text(CommandDefinition.CategoryKey(command.Category)),
                string.Empty,
                chords.Count > 0 ? _format(chords[0]) : string.Empty,
                availability.IsEnabled,
                availability.IsEnabled ? null : _text(Reasons.ResourceKey(availability.Reason ?? Reasons.NoDocument)),
                command.HasKeywords ? _text(command.KeywordsKey) : string.Empty));
        }

        return items;
    }
}

/// <summary>"Switch to tab: {title}" for the open tabs, at most 20.</summary>
public sealed class TabsPaletteSource : IPaletteSource
{
    public const int Limit = 20;

    private readonly Func<IReadOnlyList<(string Id, string Title)>> _tabs;
    private readonly Func<string, string> _text;

    public TabsPaletteSource(Func<IReadOnlyList<(string Id, string Title)>> tabs, Func<string, string> text)
    {
        _tabs = tabs;
        _text = text;
    }

    public IReadOnlyList<PaletteItem> Items(string query) =>
    [
        .. _tabs().Take(Limit).Select(t => new PaletteItem(
            PaletteItemKind.Tab,
            t.Id,
            string.Format(CultureInfo.CurrentCulture, _text("Palette_SwitchTab"), t.Title),
            _text("Category_TabsAndWindows"),
            string.Empty,
            string.Empty,
            true,
            null)),
    ];
}

/// <summary>"Open recent: {file name}" with the folder as the detail, at most 20.</summary>
public sealed class RecentFilesPaletteSource : IPaletteSource
{
    public const int Limit = 20;

    private readonly Func<IReadOnlyList<string>> _paths;
    private readonly Func<string, string> _text;

    public RecentFilesPaletteSource(Func<IReadOnlyList<string>> paths, Func<string, string> text)
    {
        _paths = paths;
        _text = text;
    }

    public IReadOnlyList<PaletteItem> Items(string query) =>
    [
        .. _paths().Take(Limit).Select(p => new PaletteItem(
            PaletteItemKind.RecentFile,
            p,
            string.Format(CultureInfo.CurrentCulture, _text("Palette_OpenRecent"), Path.GetFileName(p)),
            _text("Category_File"),
            Path.GetDirectoryName(p) ?? string.Empty,
            string.Empty,
            true,
            null)),
    ];
}

/// <summary>"Settings: {section}" for each page of Settings.</summary>
public sealed class SettingsPagesPaletteSource : IPaletteSource
{
    private readonly Func<IReadOnlyList<(string Id, string Title)>> _pages;
    private readonly Func<string, string> _text;

    public SettingsPagesPaletteSource(Func<IReadOnlyList<(string Id, string Title)>> pages, Func<string, string> text)
    {
        _pages = pages;
        _text = text;
    }

    public IReadOnlyList<PaletteItem> Items(string query) =>
    [
        .. _pages().Select(p => new PaletteItem(
            PaletteItemKind.SettingsPage,
            p.Id,
            string.Format(CultureInfo.CurrentCulture, _text("Palette_Settings"), p.Title),
            _text("Category_Settings"),
            string.Empty,
            string.Empty,
            true,
            null)),
    ];
}

/// <summary>When the query is a whole number (or ":" and one) within the page range, "Go to page {n}" is the first row.</summary>
public sealed class GoToPagePaletteSource : IPaletteSource
{
    private readonly Func<int> _pageCount;
    private readonly Func<string, string> _text;

    public GoToPagePaletteSource(Func<int> pageCount, Func<string, string> text)
    {
        _pageCount = pageCount;
        _text = text;
    }

    public bool Pinned => true;

    public IReadOnlyList<PaletteItem> Items(string query)
    {
        var count = _pageCount();
        var digits = query.TrimStart(':').Trim();
        if (count <= 0 || digits.Length is 0 or > 9 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1 || page > count)
        {
            return [];
        }

        return
        [
            new PaletteItem(
                PaletteItemKind.GoToPage,
                page.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.CurrentCulture, _text("Palette_GoToPage"), page),
                _text("Category_Navigate"),
                string.Empty,
                string.Empty,
                true,
                null),
        ];
    }
}
