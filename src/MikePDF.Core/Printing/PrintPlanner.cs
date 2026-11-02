using MikePDF.Core.Geometry;

namespace MikePDF.Core.Printing;

/// <summary>One page of a sheet: which page, where it goes, and how it is rendered.</summary>
public sealed record PlacedPage(int Page, PagePlacement Placement, PageRenderPlan Render);

/// <summary>One side of one sheet of paper: the pages on it (one, or up to 16 for N-up) with their places.</summary>
public sealed record SheetPlan(IReadOnlyList<PlacedPage> Pages);

/// <summary>
/// Turns the options and the paper into the sheets of a job: which pages share a sheet, where each goes and how it is rendered. Pure, and lazy in the number of pages: one
/// sheet is planned from the sizes of its own pages.
/// </summary>
public sealed class PrintPlanner
{
    private readonly PrintOptions _options;
    private readonly PaperArea _paper;
    private readonly RenderLimits _limits;
    private readonly IReadOnlyList<(int X, int Y, PaperArea Area)> _cells;

    public PrintPlanner(PrintOptions options, PaperArea paper, RenderLimits limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        _options = options.Normalize();
        _paper = paper;
        _limits = limits;
        _cells = SheetLayout.Cells(paper, _options.PagesPerSheet, _options.SheetOrder);
    }

    public int PagesPerSheet => _cells.Count;

    public PaperArea Paper => _paper;

    /// <summary>The sheets a page count fills.</summary>
    public int SheetCount(int pageCount) => (pageCount + PagesPerSheet - 1) / PagesPerSheet;

    /// <summary>
    /// Plans one sheet. <paramref name="pages"/> are the pages on it, in order, with their displayed sizes in points; at most <see cref="PagesPerSheet"/> of them (the last sheet of a job may
    /// have fewer, and its remaining cells stay empty).
    /// </summary>
    public SheetPlan PlanSheet(IReadOnlyList<(int Page, SizeD Size)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count > PagesPerSheet)
        {
            throw new ArgumentException("More pages than the sheet has cells.", nameof(pages));
        }

        var placed = new List<PlacedPage>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var (cellX, cellY, cell) = _cells[i];
            var placement = Place(pages[i].Size, cell).Offset(cellX, cellY);
            placed.Add(new PlacedPage(pages[i].Page, placement, RenderPlanner.Plan(placement, _paper, _limits)));
        }

        return new SheetPlan(placed);
    }

    /// <summary>Whether this page, printed alone with these options, is larger than the printable area and is cut off (print.md 3.4: the sheet warns before the job starts).</summary>
    public bool IsCutOff(SizeD page) => PagesPerSheet == 1 && Place(page, _cells[0].Area).Clipped;

    private PagePlacement Place(SizeD size, PaperArea cell) => PagesPerSheet == 1
        ? PrintLayout.Place(size, cell, _options.Scaling, _options.CustomPercent, _options.AutoRotate)
        : PrintLayout.Place(size, cell, PrintScaling.Fit, 100, autoRotate: false);
}
