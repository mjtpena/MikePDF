using System.Globalization;

namespace MikePDF.Render.Cli;

/// <summary>
/// A page selection: <c>all</c>, or comma-separated 1-based pages and ranges (<c>3</c>, <c>2-5</c>, <c>10-</c> for page 10 to the end). Pages outside the
/// document are reported by <see cref="Resolve"/>, not silently dropped.
/// </summary>
internal sealed class PageSpec
{
    private readonly List<(int First, int Last)> _ranges;

    private PageSpec(string text, List<(int First, int Last)> ranges)
    {
        Text = text;
        _ranges = ranges;
    }

    public static PageSpec All { get; } = new("all", [(1, int.MaxValue)]);

    public string Text { get; }

    public static PageSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        var ranges = new List<(int, int)>();
        foreach (var part in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                var page = ParsePage(part, trimmed);
                ranges.Add((page, page));
            }
            else
            {
                var first = ParsePage(part[..dash], trimmed);
                var lastText = part[(dash + 1)..];
                var last = lastText.Length == 0 ? int.MaxValue : ParsePage(lastText, trimmed);
                if (last < first)
                {
                    throw new FormatException($"The page range '{part}' runs backwards.");
                }

                ranges.Add((first, last));
            }
        }

        return ranges.Count == 0 ? throw new FormatException($"'{text}' selects no pages.") : new PageSpec(trimmed, ranges);
    }

    /// <summary>The selected pages that exist in a document of <paramref name="pageCount"/> pages, ascending and without repeats; <paramref name="outsideDocument"/> gets the explicitly named ones that do not.</summary>
    public IReadOnlyList<int> Resolve(int pageCount, out IReadOnlyList<int> outsideDocument)
    {
        var pages = new SortedSet<int>();
        var outside = new SortedSet<int>();
        foreach (var (first, last) in _ranges)
        {
            // A range that runs past the last page is clipped quietly; a single page past the end is reported.
            if (first > pageCount)
            {
                if (first == last)
                {
                    outside.Add(first);
                }

                continue;
            }

            var end = Math.Min(last, pageCount);
            for (var page = first; page <= end; page++)
            {
                pages.Add(page);
            }
        }

        outsideDocument = [.. outside];
        return [.. pages];
    }

    public override string ToString() => Text;

    private static int ParsePage(string text, string whole)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
        {
            throw new FormatException($"'{text}' in the page selection '{whole}' is not a page number (pages start at 1).");
        }

        return page;
    }
}
