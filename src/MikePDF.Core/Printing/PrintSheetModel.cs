using System.Globalization;

namespace MikePDF.Core.Printing;

/// <summary>A paper size a printer lists: the <c>DMPAPER_*</c> id, its name and its size in tenths of a millimetre.</summary>
public sealed record PaperChoice(int Id, string Name, int WidthTenthsMm, int HeightTenthsMm);

/// <summary>
/// What a printer can do, as far as the sheet needs to know (the spooler's <c>DeviceCapabilities</c>, read by the broker). A first guess: the driver's merged DEVMODE is the truth when a job starts,
/// and a driver may keep a copy count that <see cref="MaxCopies"/> says it cannot make (the pipeline then makes the copies itself), so copies are never limited by it.
/// </summary>
public sealed record PrinterAbilities(IReadOnlyList<PaperChoice> Papers, bool Duplex, bool Colour, bool Collate, int MaxCopies)
{
    /// <summary>Nothing is known yet (the printer list has not arrived, or the printer did not answer): nothing is offered that depends on the printer.</summary>
    public static PrinterAbilities Unknown { get; } = new([], false, true, true, 1);
}

/// <summary>Everything the sheet shows that follows from the choices: the pages, the sheets, the words for the summary, and whether Print can go ahead.</summary>
/// <param name="Selection">The pages the choices select, or why none (the range's error).</param>
/// <param name="PageCount">The pages that will be printed.</param>
/// <param name="Sides">The sides of paper one copy needs (pages divided by pages per sheet, rounded up).</param>
/// <param name="Sheets">The sheets of paper one copy needs: sides, or half of them when printing on both sides.</param>
/// <param name="BothSides">The job prints on both sides of the paper (chosen, and the printer can).</param>
/// <param name="SummaryKey">The resource key of the summary sentence ("Printing {0} pages on {1} sheets, both sides"); {0} is <see cref="PageCount"/> and {1} is <see cref="Sheets"/>. Null when there is no valid selection.</param>
/// <param name="CopiesKey">The resource key of the copies line, or null for one copy; {0} is the copy count.</param>
/// <param name="RangeErrorKey">The resource key of the range's error text, or null.</param>
/// <param name="RangeErrorLimit">{0} of <see cref="RangeErrorKey"/> when the error names the page count.</param>
/// <param name="ErrorIsLive">The range box has text that is wrong (an empty box is not announced as an error: nothing has been typed yet).</param>
public sealed record PrintSheetEvaluation(
    PageRangeResult Selection,
    int PageCount,
    int Sides,
    int Sheets,
    bool BothSides,
    string? SummaryKey,
    string? CopiesKey,
    string? RangeErrorKey,
    int RangeErrorLimit,
    bool ErrorIsLive)
{
    public bool HasPages => Selection.IsValid;
}

/// <summary>What the person has chosen in the sheet apart from the document's pages: the printer, which pages, and the options.</summary>
public sealed record PrintSheetState
{
    public string Printer { get; init; } = string.Empty;

    public PrintScope Scope { get; init; } = PrintScope.AllPages;

    /// <summary>The range box's text. Kept when another scope is chosen, so going back to "Pages" finds what was typed.</summary>
    public string RangeText { get; init; } = string.Empty;

    public PageSubset Subset { get; init; } = PageSubset.All;

    public PrintOptions Options { get; init; } = new();

    public PrinterAbilities Abilities { get; init; } = PrinterAbilities.Unknown;
}

/// <summary>
/// The print sheet's rules, with no window: the values it opens with (the last-used ones, then the document's own wishes), what a printer's abilities do to them, and what the choices
/// mean (docs/features/print.md 3.2 and 3.3, design 11-print section 4). Pure, so every rule is a unit test.
/// </summary>
public static class PrintSheetModel
{
    /// <summary>
    /// The values the sheet opens with. The last-used printer and options come first; a document's <c>/ViewerPreferences</c> then win for what they say (they are about this file, and the person can
    /// change all of them): <c>/PrintScaling /None</c> is Actual size, <c>/NumCopies</c> the copies, <c>/Duplex</c> the sides and <c>/PrintPageRange</c> the pages.
    /// </summary>
    /// <param name="saved">The last-used printer and options (<see cref="PrintPreferences.Load"/>).</param>
    /// <param name="hints">What the document asks for, already clamped (<see cref="PrintDefaults.FromViewerPreferences"/>); <see cref="PrintDefaults.Plain"/> when it says nothing or cannot be read.</param>
    /// <param name="requestedScope">The scope the command asked for ("Print current page"), or null.</param>
    public static PrintSheetState Initial(PrintPreferences saved, PrintDefaults hints, PrintScope? requestedScope = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(hints);
        var options = saved.Options.Normalize() with { Copies = Math.Clamp(hints.Copies, PrintOptions.MinCopies, PrintOptions.MaxCopies) };
        if (hints.Scaling == PrintScaling.Actual)
        {
            options = options with { Scaling = PrintScaling.Actual };
        }

        if (hints.Duplex != PrintDuplex.None)
        {
            options = options with { Duplex = hints.Duplex };
        }

        var scope = PrintScope.AllPages;
        var range = string.Empty;
        if (hints.RangeText is { Length: > 0 } hinted)
        {
            scope = PrintScope.Range;
            range = hinted;
        }

        if (requestedScope is { } requested)
        {
            scope = requested;
        }

        return new PrintSheetState { Printer = saved.Printer, Scope = scope, RangeText = range, Options = options };
    }

    /// <summary>
    /// A printer's abilities applied to the choices: sides it cannot print are dropped, paper it does not list falls back to its default, a black-and-white printer prints grey. Values
    /// the printer does not limit are untouched.
    /// </summary>
    public static PrintSheetState WithAbilities(PrintSheetState state, PrinterAbilities abilities)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        var options = state.Options;
        if (!abilities.Duplex && options.Duplex != PrintDuplex.None)
        {
            options = options with { Duplex = PrintDuplex.None };
        }

        if (options.PaperSizeId != 0 && abilities.Papers.Count > 0 && abilities.Papers.All(p => p.Id != options.PaperSizeId))
        {
            options = options with { PaperSizeId = 0 }; // the printer's own default paper
        }

        if (!abilities.Colour && !options.Grayscale)
        {
            options = options with { Grayscale = true };
        }

        return state with { Options = options, Abilities = abilities };
    }

    /// <summary>The printer to start with: the last used if it still exists, else the system default, else Microsoft Print to PDF (always there on Windows 11), else the first. Null for no printers at all.</summary>
    public static string? ChoosePrinter(string lastUsed, IReadOnlyList<string> names, string? systemDefault, string fallback = "Microsoft Print to PDF")
    {
        ArgumentNullException.ThrowIfNull(names);
        string? Find(string? wanted) => string.IsNullOrEmpty(wanted) ? null : names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        return Find(lastUsed) ?? Find(systemDefault) ?? Find(fallback) ?? (names.Count > 0 ? names[0] : null);
    }

    /// <summary>What the choices mean for a document of <paramref name="pageCount"/> pages with <paramref name="currentPage"/> shown.</summary>
    public static PrintSheetEvaluation Evaluate(PrintSheetState state, int pageCount, int currentPage)
    {
        ArgumentNullException.ThrowIfNull(state);
        var selection = PageSelection.Resolve(state.Scope, pageCount, currentPage, state.RangeText, null, state.Subset);
        var options = state.Options.Normalize();
        if (!selection.IsValid)
        {
            var typed = state.Scope == PrintScope.Range && !string.IsNullOrWhiteSpace(state.RangeText);
            return new PrintSheetEvaluation(selection, 0, 0, 0, false, null, null, selection.ResourceKey, selection.Limit > 0 ? selection.Limit : pageCount, typed || selection.Error == PageRangeError.NoPages);
        }

        var pages = selection.Pages.Count;
        var perSheet = options.PagesPerSheet;
        var sides = (int)(((long)pages + perSheet - 1) / perSheet);
        var both = options.Duplex != PrintDuplex.None && state.Abilities.Duplex;
        var sheets = both ? (sides + 1) / 2 : sides;
        var key = string.Create(CultureInfo.InvariantCulture, $"Print_Summary_{(pages == 1 ? "P1" : "PN")}_{(sheets == 1 ? "S1" : "SN")}{(both ? "_Both" : string.Empty)}");
        var copies = options.Copies <= 1 ? null : options.Collate ? "Print_Summary_Copies_Collated" : "Print_Summary_Copies_Uncollated";
        return new PrintSheetEvaluation(selection, pages, sides, sheets, both, key, copies, null, 0, false);
    }

    /// <summary>
    /// How a hostile or stale choice is made safe before a job starts: options are clamped, a duplex the printer cannot do is dropped, and a custom scale is only used when Custom is chosen.
    /// </summary>
    public static PrintOptions OptionsForJob(PrintSheetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var options = state.Options.Normalize();
        if (!state.Abilities.Duplex)
        {
            options = options with { Duplex = PrintDuplex.None };
        }

        return options;
    }

    /// <summary>The sheet index (0-based) clamped to the sheets there are: the preview's pager.</summary>
    public static int ClampSheet(int sheet, int sheets) => sheets <= 0 ? 0 : Math.Clamp(sheet, 0, sheets - 1);

    /// <summary>The pages on sheet <paramref name="sheet"/> (0-based) of the selection: up to <see cref="PrintOptions.PagesPerSheet"/> consecutive pages of it.</summary>
    public static IReadOnlyList<int> PagesOnSheet(PageSet pages, int pagesPerSheet, int sheet)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var perSheet = Math.Max(1, pagesPerSheet);
        var first = (long)sheet * perSheet;
        if (sheet < 0 || first >= pages.Count)
        {
            return [];
        }

        var count = (int)Math.Min(perSheet, pages.Count - first);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = pages[(int)first + i];
        }

        return result;
    }
}
