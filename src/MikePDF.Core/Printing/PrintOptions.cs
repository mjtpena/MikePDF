namespace MikePDF.Core.Printing;

/// <summary>How a page is scaled onto the paper (print.md 3.2). The default is <see cref="Shrink"/>; a document that says <c>/PrintScaling /None</c> defaults to <see cref="Actual"/>.</summary>
public enum PrintScaling
{
    /// <summary>Pages larger than the printable area are shrunk to fit; smaller pages keep their size.</summary>
    Shrink,

    /// <summary>Every page is scaled up or down to fill the printable area.</summary>
    Fit,

    /// <summary>100%: one point is 1/72 inch. A page larger than the paper is cut off.</summary>
    Actual,

    /// <summary><see cref="PrintOptions.CustomPercent"/> percent.</summary>
    Custom,
}

/// <summary>The paper's orientation for the whole job (one <c>StartDoc</c> cannot change it per page; <see cref="PrintOptions.AutoRotate"/> turns single pages instead).</summary>
public enum PaperOrientation
{
    /// <summary>Portrait, or landscape when most of the printed sheets would use the paper better that way.</summary>
    Auto,
    Portrait,
    Landscape,
}

public enum PrintDuplex
{
    None,
    LongEdge,
    ShortEdge,
}

/// <summary>How the pages of a sheet with more than one page are laid out.</summary>
public enum SheetOrder
{
    /// <summary>Left to right, then the next row down.</summary>
    Horizontal,

    /// <summary>Top to bottom, then the next column to the right.</summary>
    Vertical,
}

/// <summary>
/// What the print sheet chooses (print.md 3.2), everything except which pages and which printer. Pure data; <see cref="Normalize"/> clamps what a hostile or stale
/// settings file could say.
/// </summary>
public sealed record PrintOptions
{
    public const int MinCopies = 1;
    public const int MaxCopies = 99;
    public const double MinCustomPercent = 10;
    public const double MaxCustomPercent = 400;

    /// <summary>The sheet sizes the sheet offers: pages per sheet. 1 is the normal job.</summary>
    public static IReadOnlyList<int> PagesPerSheetChoices { get; } = [1, 2, 4, 6, 9, 16];

    public int Copies { get; init; } = 1;

    public bool Collate { get; init; } = true;

    public PrintDuplex Duplex { get; init; } = PrintDuplex.None;

    public PaperOrientation Orientation { get; init; } = PaperOrientation.Auto;

    /// <summary>A <c>DMPAPER_*</c> value, or 0 for the printer's default paper.</summary>
    public int PaperSizeId { get; init; }

    public bool Grayscale { get; init; }

    public PrintScaling Scaling { get; init; } = PrintScaling.Shrink;

    public double CustomPercent { get; init; } = 100;

    /// <summary>Rotate a page by 90 degrees when its orientation differs from the paper's, and centre it (print.md 3.2). Not applied to sheets with more than one page.</summary>
    public bool AutoRotate { get; init; } = true;

    /// <summary>1, 2, 4, 6, 9 or 16 pages on one sheet. N-up fits every page in its cell, whatever <see cref="Scaling"/> says.</summary>
    public int PagesPerSheet { get; init; } = 1;

    public SheetOrder SheetOrder { get; init; } = SheetOrder.Horizontal;

    /// <summary>Print the annotations that carry the print flag (print.md 3.2: "Document and markup").</summary>
    public bool Annotations { get; init; } = true;

    /// <summary>Print the form fields' contents; the editing highlight never prints.</summary>
    public bool FormFields { get; init; } = true;

    /// <summary>One bitmap per page at 300 dpi at most (print.md 3.6) instead of bands at the printer's resolution.</summary>
    public bool AsImage { get; init; }

    public PrintOptions Normalize() => this with
    {
        Copies = Math.Clamp(Copies, MinCopies, MaxCopies),
        CustomPercent = double.IsNaN(CustomPercent) ? 100 : Math.Clamp(CustomPercent, MinCustomPercent, MaxCustomPercent),
        PagesPerSheet = PagesPerSheetChoices.Contains(PagesPerSheet) ? PagesPerSheet : 1,
        PaperSizeId = Math.Max(0, PaperSizeId),
        Scaling = Enum.IsDefined(Scaling) ? Scaling : PrintScaling.Shrink,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : PaperOrientation.Auto,
        Duplex = Enum.IsDefined(Duplex) ? Duplex : PrintDuplex.None,
        SheetOrder = Enum.IsDefined(SheetOrder) ? SheetOrder : SheetOrder.Horizontal,
    };

    /// <summary>The <c>RenderBand</c> flags (schema 5): bit 0 annotations, bit 1 form fields, bit 2 printing mode, which is always on.</summary>
    public uint BandFlags(bool includeFormLayer = true) =>
        4u | (Annotations ? 1u : 0u) | (FormFields && includeFormLayer ? 2u : 0u);
}

/// <summary>What a document says it would like (<c>/ViewerPreferences</c>), turned into the sheet's starting values. Never forced on the user (print.md 3.2).</summary>
public sealed record PrintDefaults(PrintScaling Scaling, int Copies, PrintDuplex Duplex, string? RangeText)
{
    public static PrintDefaults Plain { get; } = new(PrintScaling.Shrink, 1, PrintDuplex.None, null);

    /// <summary>
    /// From the engine's <c>GetViewerPreferences</c> reply (values as schema 5 defines them: printScaling 0 absent, 1 none, 2 app default; duplex 0 absent, 1 simplex, 2 flip
    /// short edge, 3 flip long edge; copies 0 absent). The file is hostile: a copy count is clamped to 1 to 99, a range that is not valid for this page count is
    /// ignored, and an unknown value is "absent".
    /// </summary>
    public static PrintDefaults FromViewerPreferences(int printScaling, int numCopies, int duplex, IReadOnlyList<uint>? rangeFirsts, IReadOnlyList<uint>? rangeLasts, int pageCount)
    {
        var scaling = printScaling == 1 ? PrintScaling.Actual : PrintScaling.Shrink;
        var copies = Math.Clamp(numCopies, PrintOptions.MinCopies, PrintOptions.MaxCopies);
        var side = duplex switch
        {
            2 => PrintDuplex.ShortEdge,
            3 => PrintDuplex.LongEdge,
            _ => PrintDuplex.None,
        };

        string? range = null;
        if (rangeFirsts is { Count: > 0 } && rangeLasts is not null && rangeFirsts.Count == rangeLasts.Count)
        {
            // /PrintPageRange is zero-based pairs in the file; the engine reply gives them as the file says. Anything outside the document drops the whole entry.
            var items = new List<string>(rangeFirsts.Count);
            var previous = 0u;
            foreach (var (first, last) in rangeFirsts.Zip(rangeLasts))
            {
                if (last < first || last >= (ulong)pageCount || first < previous)
                {
                    items.Clear();
                    break;
                }

                previous = first;
                items.Add(first == last ? (first + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{first + 1}-{last + 1}"));
            }

            if (items.Count > 0)
            {
                var text = string.Join(", ", items);
                if (PageRangeParser.Parse(text, pageCount).IsValid)
                {
                    range = text;
                }
            }
        }

        return new PrintDefaults(scaling, copies, side, range);
    }
}
