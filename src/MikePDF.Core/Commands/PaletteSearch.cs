using System.Globalization;
using System.Text;

namespace MikePDF.Core.Commands;

public enum PaletteItemKind
{
    Command,
    Tab,
    RecentFile,
    SettingsPage,
    GoToPage,
}

/// <summary>One row of the palette. <see cref="Key"/> identifies what running it does: a command id, a tab id, a file path, a settings page id or a page number.</summary>
public sealed record PaletteItem(
    PaletteItemKind Kind,
    string Key,
    string Title,
    string Category,
    string Detail,
    string Shortcut,
    bool Enabled,
    string? DisabledReason,
    string Keywords = "")
{
    /// <summary>The row as a screen reader says it: "{name}, {shortcut or no shortcut}, {category}", and for a disabled one "unavailable, {reason}".</summary>
    public string Spoken(Func<string, string> text)
    {
        var shortcut = Shortcut.Length > 0 ? Shortcut : text("Palette_NoShortcut");
        var line = string.Format(CultureInfo.CurrentCulture, text("Palette_Row_Automation"), Title, shortcut, Category);
        return Enabled || DisabledReason is null ? line : line + ", " + string.Format(CultureInfo.CurrentCulture, text("Palette_Row_Unavailable"), DisabledReason);
    }
}

public sealed record PaletteResult(PaletteItem Item, int Score);

/// <summary>A source of palette rows. A source that throws is skipped (the others still show) and reported to <see cref="PaletteEngine.SourceFailed"/>.</summary>
public interface IPaletteSource
{
    /// <summary>
    /// The rows this source offers. An ordinary source is asked once (with an empty query) and the engine scores its rows; a pinned source is asked for every
    /// query and its rows go first.
    /// </summary>
    IReadOnlyList<PaletteItem> Items(string query);

    /// <summary>True for a source whose rows are not matched against the text (the "Go to page" row appears first for a number).</summary>
    bool Pinned => false;
}

/// <summary>Text matching for the palette (docs/features/command-palette-shortcuts.md 3.5): NFKC, case, diacritics and width insensitive; every word must match.</summary>
public static class PaletteText
{
    public const int MaxQueryLength = 256;

    /// <summary>Folds text for comparison: NFKC (which also folds full-width letters), lower case, accents removed.</summary>
    public static string Fold(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var compatible = text.Normalize(NormalizationForm.FormKC);
        var decomposed = compatible.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static string[] Words(string folded) => folded.Split([' ', '\t', '-', '_', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>An item with its text folded once, so a keystroke costs a few string searches per item.</summary>
internal sealed class PreparedItem
{
    public PreparedItem(PaletteItem item)
    {
        Item = item;
        Name = PaletteText.Fold(item.Title);
        Words = PaletteText.Words(Name);
        Initials = new string([.. Words.Select(w => w[0])]);
        Keywords = PaletteText.Fold(item.Keywords);
        KeywordWords = PaletteText.Words(Keywords);
        Category = PaletteText.Fold(item.Category);
    }

    public PaletteItem Item { get; }

    public string Name { get; }

    public string[] Words { get; }

    public string Initials { get; }

    public string Keywords { get; }

    public string[] KeywordWords { get; }

    public string Category { get; }
}

/// <summary>
/// Ranks palette rows for a query. Match quality, best first: exact name, name prefix, word start (and acronyms: "zi" is "Zoom in"), contiguous
/// substring, subsequence; matches in keywords and category count for less. A row must match every word of the query. Recently used commands and
/// commands that can run now get a bonus; ties break on the name.
/// </summary>
public sealed class PaletteEngine
{
    public const int Exact = 1000;
    public const int Prefix = 800;
    public const int WordStart = 600;
    public const int Acronym = 550;
    public const int Substring = 400;
    public const int KeywordWordStart = 300;
    public const int Subsequence = 200;
    public const int KeywordSubstring = 150;
    public const int CategoryMatch = 100;
    public const int EnabledBonus = 50;
    public const int RecentBonusPerRank = 5;

    private readonly IReadOnlyList<IPaletteSource> _sources;
    private readonly Func<string, int> _recencyRank;
    private List<PreparedItem>? _prepared;

    /// <param name="sources">The sources, asked in order.</param>
    /// <param name="recencyRank">1 for the most recently used row key, up to 20; 0 for one not used lately.</param>
    public PaletteEngine(IEnumerable<IPaletteSource> sources, Func<string, int>? recencyRank = null)
    {
        _sources = [.. sources];
        _recencyRank = recencyRank ?? (_ => 0);
    }

    /// <summary>Raised when a source throws; the engine goes on without it.</summary>
    public event Action<IPaletteSource, Exception>? SourceFailed;

    /// <summary>
    /// The rows for a query, best first. The rows of the ordinary sources are read and folded once per engine (the palette makes a new engine each time it
    /// opens, so they are current), which keeps a keystroke to a few string searches per row. Pinned sources are asked every time.
    /// </summary>
    public IReadOnlyList<PaletteResult> Search(string query)
    {
        var text = query ?? string.Empty;
        var folded = PaletteText.Fold(text.Length > PaletteText.MaxQueryLength ? text[..PaletteText.MaxQueryLength] : text).Trim();
        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pinned = new List<PaletteResult>();
        foreach (var source in _sources.Where(s => s.Pinned))
        {
            try
            {
                pinned.AddRange(source.Items(folded).Select(i => new PaletteResult(i, int.MaxValue)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                SourceFailed?.Invoke(source, e);
            }
        }

        var scored = new List<(PaletteResult Result, string Name)>();
        foreach (var item in Prepared())
        {
            var score = tokens.Length == 0 ? Bonus(item.Item) : Score(item, folded, tokens);
            if (score >= 0)
            {
                scored.Add((new PaletteResult(item.Item, score), item.Name));
            }
        }

        var ordered = scored
            .OrderByDescending(s => s.Result.Score)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Result.Item.Key, StringComparer.Ordinal)
            .Select(s => s.Result);
        return [.. pinned, .. ordered];
    }

    private List<PreparedItem> Prepared()
    {
        if (_prepared is not null)
        {
            return _prepared;
        }

        var prepared = new List<PreparedItem>();
        foreach (var source in _sources.Where(s => !s.Pinned))
        {
            try
            {
                prepared.AddRange(source.Items(string.Empty).Select(i => new PreparedItem(i)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                SourceFailed?.Invoke(source, e);
            }
        }

        return _prepared = prepared;
    }

    /// <summary>The rows for an empty box: the most recently used commands first (up to eight), then the first couple of each category.</summary>
    public IReadOnlyList<PaletteResult> Suggestions(int recentCount = 8, int perCategory = 2)
    {
        var all = Search(string.Empty).Where(r => r.Item.Kind == PaletteItemKind.Command).ToList();
        var recent = all.Where(r => _recencyRank(r.Item.Key) > 0).OrderBy(r => _recencyRank(r.Item.Key)).Take(recentCount).ToList();
        var chosen = new HashSet<string>(recent.Select(r => r.Item.Key), StringComparer.Ordinal);
        var rest = new List<PaletteResult>();
        foreach (var group in all.Where(r => !chosen.Contains(r.Item.Key)).GroupBy(r => r.Item.Category))
        {
            rest.AddRange(group.Take(perCategory));
        }

        return [.. recent, .. rest];
    }

    private int Bonus(PaletteItem item)
    {
        var rank = _recencyRank(item.Key);
        return (item.Enabled ? EnabledBonus : 0) + (rank > 0 ? (21 - rank) * RecentBonusPerRank : 0);
    }

    // -1 when some word of the query matches nothing.
    private int Score(PreparedItem item, string folded, string[] tokens)
    {
        var total = 0;
        foreach (var token in tokens)
        {
            var best = TokenScore(item, token, wholeQuery: tokens.Length == 1 && folded == token);
            if (best < 0)
            {
                return -1;
            }

            total += best;
        }

        return total + Bonus(item.Item);
    }

    private static int TokenScore(PreparedItem item, string token, bool wholeQuery)
    {
        var best = -1;
        if (wholeQuery && item.Name == token)
        {
            return Exact;
        }

        if (item.Name.StartsWith(token, StringComparison.Ordinal))
        {
            best = Prefix;
        }
        else if (item.Words.Any(w => w.StartsWith(token, StringComparison.Ordinal)))
        {
            best = WordStart;
        }
        else if (token.Length > 1 && item.Initials.StartsWith(token, StringComparison.Ordinal))
        {
            best = Acronym;
        }
        else if (item.Name.Contains(token, StringComparison.Ordinal))
        {
            best = Substring;
        }
        else if (IsSubsequence(token, item.Name))
        {
            best = Subsequence;
        }

        if (best >= Substring)
        {
            return best;
        }

        var other = -1;
        if (item.KeywordWords.Any(w => w.StartsWith(token, StringComparison.Ordinal)))
        {
            other = KeywordWordStart;
        }
        else if (item.Keywords.Contains(token, StringComparison.Ordinal))
        {
            other = KeywordSubstring;
        }
        else if (item.Category.Contains(token, StringComparison.Ordinal))
        {
            other = CategoryMatch;
        }

        return Math.Max(best, other);
    }

    private static bool IsSubsequence(string token, string text)
    {
        var at = 0;
        foreach (var c in text)
        {
            if (at < token.Length && c == token[at])
            {
                at++;
            }
        }

        return at == token.Length;
    }
}
