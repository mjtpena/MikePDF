namespace MikePDF.Core.Printing;

/// <summary>The "Pages" choice of the print sheet (print.md 3.2).</summary>
public enum PrintScope
{
    AllPages,
    CurrentPage,

    /// <summary>The range text of print.md 3.3.</summary>
    Range,

    /// <summary>The pages selected in the sidebar.</summary>
    SelectedPages,
}

/// <summary>Turns the sheet's "Pages" and "Subset" choices into the pages to print.</summary>
public static class PageSelection
{
    /// <param name="scope">What the sheet's "Pages" choice says.</param>
    /// <param name="pageCount">The document's page count.</param>
    /// <param name="currentPage">The page being read, 1-based; used by <see cref="PrintScope.CurrentPage"/>.</param>
    /// <param name="rangeText">The range box's text; used by <see cref="PrintScope.Range"/>.</param>
    /// <param name="selectedPages">The sidebar's selected pages, 1-based, in any order; used by <see cref="PrintScope.SelectedPages"/>.</param>
    /// <param name="subset">Odd or even only, applied after the choice above.</param>
    public static PageRangeResult Resolve(PrintScope scope, int pageCount, int currentPage, string? rangeText, IReadOnlyCollection<int>? selectedPages, PageSubset subset)
    {
        PageRangeResult chosen;
        switch (scope)
        {
            case PrintScope.AllPages:
                chosen = pageCount < 1
                    ? PageRangeResult.Fail(PageRangeError.NoPages)
                    : PageRangeResult.Ok(new PageSet([new PageRun(1, pageCount, 1)]));
                break;
            case PrintScope.CurrentPage:
                chosen = currentPage < 1 || currentPage > pageCount
                    ? PageRangeResult.Fail(PageRangeError.OutOfRange, pageCount)
                    : PageRangeResult.Ok(new PageSet([new PageRun(currentPage, currentPage, 1)]));
                break;
            case PrintScope.Range:
                chosen = PageRangeParser.Parse(rangeText, pageCount);
                break;
            case PrintScope.SelectedPages:
                chosen = FromList(selectedPages, pageCount);
                break;
            default:
                chosen = PageRangeResult.Fail(PageRangeError.Syntax);
                break;
        }

        if (!chosen.IsValid)
        {
            return chosen;
        }

        var pages = chosen.Pages.Filter(subset);
        return pages.IsEmpty ? PageRangeResult.Fail(PageRangeError.NoPages) : PageRangeResult.Ok(pages);
    }

    private static PageRangeResult FromList(IReadOnlyCollection<int>? selected, int pageCount)
    {
        if (selected is null || selected.Count == 0)
        {
            return PageRangeResult.Fail(PageRangeError.NoPages);
        }

        var sorted = new SortedSet<int>();
        foreach (var page in selected)
        {
            if (page < 1 || page > pageCount)
            {
                return PageRangeResult.Fail(PageRangeError.OutOfRange, pageCount);
            }

            sorted.Add(page);
        }

        var runs = new List<PageRun>();
        foreach (var page in sorted)
        {
            if (runs.Count > 0 && runs[^1].Last + 1 == page)
            {
                runs[^1] = runs[^1] with { Last = page };
            }
            else
            {
                runs.Add(new PageRun(page, page, 1));
            }
        }

        return PageRangeResult.Ok(new PageSet([.. runs]));
    }
}
