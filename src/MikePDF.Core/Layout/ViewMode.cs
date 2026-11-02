namespace MikePDF.Core.Layout;

/// <summary>Which pages share a row (docs/features/page-view-modes.md 3.1).</summary>
public enum PageArrangement
{
    /// <summary>Every page is its own spread.</summary>
    OnePage,

    /// <summary>Pages 1-2, 3-4 and so on.</summary>
    Facing,

    /// <summary>Page 1 alone, then 2-3, 4-5 (a book's cover).</summary>
    FacingWithCover,
}

/// <summary>Whether spreads are stacked and scrolled, or shown one at a time.</summary>
public enum PageFlow
{
    Continuous,
    Paged,
}

/// <summary>The binding side of a document. <c>Auto</c> follows the document's own <c>/Direction</c>.</summary>
public enum ReadingDirection
{
    Auto,
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// How the pages are arranged and turned: the arrangement, the flow, and the user's override of the reading direction. The default (zero) value is
/// single page, continuous, automatic: what the app has always done.
/// </summary>
public readonly record struct ViewMode(PageArrangement Arrangement = PageArrangement.OnePage, PageFlow Flow = PageFlow.Continuous, ReadingDirection Direction = ReadingDirection.Auto)
{
    public static ViewMode Default => default;

    public bool IsPaged => Flow == PageFlow.Paged;

    public bool IsFacing => Arrangement != PageArrangement.OnePage;

    /// <summary>
    /// Reads a stored name, never throwing: anything that is not one of the known names (a value written by another version, or by hand) is the default for that part.
    /// </summary>
    public static ViewMode Parse(string? arrangement, string? flow, string? direction) => new(
        Enum.TryParse<PageArrangement>(arrangement, ignoreCase: false, out var a) && Enum.IsDefined(a) ? a : PageArrangement.OnePage,
        Enum.TryParse<PageFlow>(flow, ignoreCase: false, out var f) && Enum.IsDefined(f) ? f : PageFlow.Continuous,
        Enum.TryParse<ReadingDirection>(direction, ignoreCase: false, out var d) && Enum.IsDefined(d) ? d : ReadingDirection.Auto);

    public ViewMode With(PageArrangement arrangement) => this with { Arrangement = arrangement };

    public ViewMode With(PageFlow flow) => this with { Flow = flow };

    public ViewMode With(ReadingDirection direction) => this with { Direction = direction };
}

/// <summary>
/// Which pages make up which spread, for a page count, an arrangement and a binding side. O(1) per question and no per-page storage, so a document of ten
/// million pages costs nothing here (PV-22). A document of 0 or 1 pages is always single pages.
/// </summary>
public sealed class SpreadMap
{
    public SpreadMap(int pageCount, PageArrangement arrangement, bool rightToLeft)
    {
        PageCount = Math.Max(0, pageCount);
        Arrangement = PageCount <= 1 ? PageArrangement.OnePage : arrangement;
        RightToLeft = rightToLeft;
        SpreadCount = Arrangement switch
        {
            PageArrangement.Facing => (PageCount + 1) / 2,
            PageArrangement.FacingWithCover => PageCount == 0 ? 0 : 1 + (PageCount / 2),
            _ => PageCount,
        };
    }

    public static SpreadMap Column(int pageCount) => new(pageCount, PageArrangement.OnePage, rightToLeft: false);

    public int PageCount { get; }

    /// <summary>The arrangement in effect (single when the document has fewer than two pages).</summary>
    public PageArrangement Arrangement { get; }

    public bool RightToLeft { get; }

    public int SpreadCount { get; }

    public bool IsSingle => Arrangement == PageArrangement.OnePage;

    /// <summary>The spread holding a page (clamped into range; -1 for an empty document).</summary>
    public int SpreadOf(int page)
    {
        if (PageCount == 0)
        {
            return -1;
        }

        page = Math.Clamp(page, 0, PageCount - 1);
        return Arrangement switch
        {
            PageArrangement.Facing => page / 2,
            PageArrangement.FacingWithCover => page == 0 ? 0 : 1 + ((page - 1) / 2),
            _ => page,
        };
    }

    /// <summary>The first and last page of a spread (equal for a lone page).</summary>
    public (int First, int Last) PagesOf(int spread)
    {
        spread = Math.Clamp(spread, 0, Math.Max(0, SpreadCount - 1));
        var first = FirstPage(spread);
        var last = Arrangement switch
        {
            PageArrangement.Facing => Math.Min(first + 1, PageCount - 1),
            PageArrangement.FacingWithCover => spread == 0 ? 0 : Math.Min(first + 1, PageCount - 1),
            _ => first,
        };
        return (first, last);
    }

    public int FirstPage(int spread)
    {
        spread = Math.Clamp(spread, 0, Math.Max(0, SpreadCount - 1));
        return Arrangement switch
        {
            PageArrangement.Facing => spread * 2,
            PageArrangement.FacingWithCover => spread == 0 ? 0 : (spread * 2) - 1,
            _ => spread,
        };
    }

    public int PagesIn(int spread)
    {
        var (first, last) = PagesOf(spread);
        return last - first + 1;
    }

    /// <summary>
    /// The pages in the left and the right half of a spread, -1 for a half left empty. A lone page keeps its own half, so a cover does not jump to the
    /// middle: in a book the cover is on the right (on the left for a right-to-left binding), and a lone last page is on the left (the right).
    /// </summary>
    public (int Left, int Right) Slots(int spread)
    {
        var (first, last) = PagesOf(spread);
        if (first == last)
        {
            if (IsSingle)
            {
                return (first, -1); // the only slot; the layout does not reserve an empty half for single pages
            }

            var isCover = Arrangement == PageArrangement.FacingWithCover && spread == 0;
            var onRight = isCover != RightToLeft;
            return onRight ? (-1, first) : (first, -1);
        }

        return RightToLeft ? (last, first) : (first, last);
    }
}
