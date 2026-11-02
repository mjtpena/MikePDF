namespace MikePDF.Core.Printing;

/// <summary>Why a range was refused. Each one maps to the resource string the print sheet shows (<see cref="PrintStrings"/>).</summary>
public enum PageRangeError
{
    None,

    /// <summary>The text is empty.</summary>
    Empty,

    /// <summary>The choice leaves no page to print (an odd-only subset of page 2, no page selected).</summary>
    NoPages,

    /// <summary>Not a list of numbers and ranges: letters, a lone dash, "1,,2", a zero.</summary>
    Syntax,

    /// <summary>More than <see cref="PageRangeParser.MaxLength"/> characters.</summary>
    TooLong,

    /// <summary>More than <see cref="PageRangeParser.MaxItems"/> items.</summary>
    TooManyItems,

    /// <summary>A range that runs backwards ("3-1") or items that are not in ascending order ("5,1").</summary>
    OutOfOrder,

    /// <summary>A page number above the document's page count.</summary>
    OutOfRange,
}

/// <summary>The result of reading a range or choosing pages: the pages, or the reason there are none and the text to show.</summary>
public sealed record PageRangeResult(PageSet Pages, PageRangeError Error, string? ResourceKey, int Limit)
{
    public bool IsValid => Error == PageRangeError.None;

    public static PageRangeResult Ok(PageSet pages) => new(pages, PageRangeError.None, null, 0);

    internal static PageRangeResult Fail(PageRangeError error, int limit = 0) => new(PageSet.Empty, error, PrintStrings.ForRangeError(error), limit);
}

/// <summary>
/// The page range grammar of print.md 3.3. ASCII digits, whitespace ignored; <c>list = item { "," item }</c>, <c>item = n | n "-" n | n "-" | "-" n</c>. Pages are
/// 1-based physical pages. "5-" is page 5 to the last, "-5" is 1 to 5. A range that runs backwards, or an item that starts before the one in front of it, is an error
/// and is never silently reordered; overlaps are allowed and a page is printed once. At most 256 characters and 4,096 items, and no page above the page count.
/// </summary>
public static class PageRangeParser
{
    public const int MaxLength = 256;
    public const int MaxItems = 4096;

    public static PageRangeResult Parse(string? text, int pageCount) => Parse(text, pageCount, MaxLength, MaxItems);

    /// <summary>The limits are parameters for the tests: with 256 characters an item costs two at least, so 4,096 items can never be reached through the public overload.</summary>
    internal static PageRangeResult Parse(string? text, int pageCount, int maxLength, int maxItems)
    {
        if (text is null)
        {
            return PageRangeResult.Fail(PageRangeError.Empty);
        }

        if (text.Length > maxLength)
        {
            return PageRangeResult.Fail(PageRangeError.TooLong);
        }

        var runs = new List<PageRun>();
        var covered = 0; // the highest page in the runs so far
        var previousStart = 0;
        var items = 0;
        var position = 0;
        var sawAnything = false;

        while (true)
        {
            var itemEnd = text.IndexOf(',', position);
            var item = itemEnd < 0 ? text.AsSpan(position) : text.AsSpan(position, itemEnd - position);
            if (item.IsWhiteSpace() && itemEnd < 0 && !sawAnything)
            {
                return PageRangeResult.Fail(PageRangeError.Empty);
            }

            sawAnything = true;
            if (++items > maxItems)
            {
                return PageRangeResult.Fail(PageRangeError.TooManyItems);
            }

            var parsed = ParseItem(item, pageCount, out var first, out var last);
            if (parsed != PageRangeError.None)
            {
                return PageRangeResult.Fail(parsed, pageCount);
            }

            if (first < previousStart)
            {
                return PageRangeResult.Fail(PageRangeError.OutOfOrder);
            }

            previousStart = first;
            var from = Math.Max(first, covered + 1);
            if (from <= last)
            {
                // Items start in ascending order, so a new page is always above everything before it and the runs stay ascending. Touching runs merge.
                if (runs.Count > 0 && runs[^1].Last + 1 == from)
                {
                    runs[^1] = runs[^1] with { Last = last };
                }
                else
                {
                    runs.Add(new PageRun(from, last, 1));
                }

                covered = last;
            }

            if (itemEnd < 0)
            {
                break;
            }

            position = itemEnd + 1;
        }

        return PageRangeResult.Ok(new PageSet([.. runs]));
    }

    private static PageRangeError ParseItem(ReadOnlySpan<char> raw, int pageCount, out int first, out int last)
    {
        first = last = 0;
        // Whitespace is dropped wherever it is; what is left is digits and at most one dash.
        Span<char> item = stackalloc char[Math.Min(raw.Length, MaxLength)];
        var length = 0;
        foreach (var c in raw)
        {
            if (!char.IsWhiteSpace(c))
            {
                item[length++] = c;
            }
        }

        item = item[..length];
        if (item.IsEmpty)
        {
            return PageRangeError.Syntax;
        }

        var dash = item.IndexOf('-');
        if (dash < 0)
        {
            var error = ParseNumber(item, pageCount, out first);
            last = first;
            return error;
        }

        var left = item[..dash];
        var right = item[(dash + 1)..];
        if (right.IndexOf('-') >= 0 || (left.IsEmpty && right.IsEmpty))
        {
            return PageRangeError.Syntax;
        }

        var start = 1;
        var end = pageCount;
        var leftError = left.IsEmpty ? PageRangeError.None : ParseNumber(left, pageCount, out start);
        var rightError = right.IsEmpty ? PageRangeError.None : ParseNumber(right, pageCount, out end);
        // A malformed item is a syntax error even when the other half is also past the end.
        if (leftError == PageRangeError.Syntax || rightError == PageRangeError.Syntax)
        {
            return PageRangeError.Syntax;
        }

        if (leftError != PageRangeError.None || rightError != PageRangeError.None)
        {
            return PageRangeError.OutOfRange;
        }

        if (start > end)
        {
            return PageRangeError.OutOfOrder;
        }

        first = start;
        last = end;
        return PageRangeError.None;
    }

    /// <summary>Digits only; zero is not a page; a number above <paramref name="pageCount"/> is out of range.</summary>
    private static PageRangeError ParseNumber(ReadOnlySpan<char> digits, int pageCount, out int value)
    {
        value = 0;
        long number = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return PageRangeError.Syntax;
            }

            number = Math.Min((number * 10) + (c - '0'), int.MaxValue); // saturates: "99999999999" is a big number, not an overflow
        }

        if (number == 0)
        {
            return PageRangeError.Syntax;
        }

        if (number > pageCount)
        {
            return PageRangeError.OutOfRange;
        }

        value = (int)number;
        return PageRangeError.None;
    }
}
