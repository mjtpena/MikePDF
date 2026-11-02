using MikePDF.Core.Geometry;

namespace MikePDF.Core.Printing;

/// <summary>The printable area of the paper in device pixels, from the printer's device context (<c>HORZRES</c>, <c>VERTRES</c>, <c>LOGPIXELSX</c>, <c>LOGPIXELSY</c>).</summary>
public readonly record struct PaperArea(int WidthPx, int HeightPx, int DpiX, int DpiY)
{
    public double WidthPoints => WidthPx * 72.0 / Math.Max(1, DpiX);

    public double HeightPoints => HeightPx * 72.0 / Math.Max(1, DpiY);

    public bool IsLandscape => WidthPoints > HeightPoints;
}

/// <summary>
/// Where one page goes on the paper: the device-pixel rectangle of the page as it will look (after <see cref="Rotated"/>), relative to the top-left of the printable
/// area. <see cref="X"/> and <see cref="Y"/> are negative or <see cref="Width"/> and <see cref="Height"/> exceed the area when the page is cut off (<see cref="Clipped"/>).
/// </summary>
public readonly record struct PagePlacement(bool Rotated, double Scale, int X, int Y, int Width, int Height, bool Clipped)
{
    /// <summary>The extra turn the engine is asked for, in quarter turns clockwise, on top of the page's own /Rotate.</summary>
    public ushort Rotation => Rotated ? (ushort)1 : (ushort)0;

    public PagePlacement Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>
/// Page to paper geometry (print.md 3.4), pure. The page is its displayed box (what <c>GetPageSizes</c> reports: the CropBox clipped to the MediaBox, with /Rotate applied; the
/// viewer's own rotation is not applied), in points. Scale is the factor on the page's size in points (1 = actual size). The scaled page is centred in the printable area.
/// </summary>
public static class PrintLayout
{
    /// <summary>The smallest and largest page edge taken as real. A page of 0 points (a damaged box) prints as a 1 point page and not as a division by zero.</summary>
    public const double MinPagePoints = 1;
    public const double MaxPagePoints = 1_000_000;

    public static PagePlacement Place(SizeD page, PaperArea area, PrintScaling scaling, double customPercent, bool autoRotate)
    {
        var pageWidth = SanePoints(page.Width);
        var pageHeight = SanePoints(page.Height);
        var dpiX = Math.Max(1, area.DpiX);
        var dpiY = Math.Max(1, area.DpiY);
        var areaWidth = Math.Max(1, area.WidthPoints);
        var areaHeight = Math.Max(1, area.HeightPoints);

        var rotated = autoRotate && pageWidth != pageHeight && areaWidth != areaHeight && (pageWidth > pageHeight) != (areaWidth > areaHeight);
        var width = rotated ? pageHeight : pageWidth;
        var height = rotated ? pageWidth : pageHeight;

        var fit = Math.Min(areaWidth / width, areaHeight / height);
        var scale = scaling switch
        {
            PrintScaling.Fit => fit,
            PrintScaling.Actual => 1,
            PrintScaling.Custom => Math.Clamp(double.IsNaN(customPercent) ? 100 : customPercent, PrintOptions.MinCustomPercent, PrintOptions.MaxCustomPercent) / 100.0,
            _ => Math.Min(1, fit),
        };

        var widthPx = ToPixels(width * scale * dpiX / 72.0);
        var heightPx = ToPixels(height * scale * dpiY / 72.0);
        if (scaling is PrintScaling.Fit or PrintScaling.Shrink)
        {
            // The scale is exact, the pixels are rounded: a page that fills the area must not come out one pixel too big.
            widthPx = Math.Min(widthPx, Math.Max(1, area.WidthPx));
            heightPx = Math.Min(heightPx, Math.Max(1, area.HeightPx));
        }

        var x = (int)Math.Floor((area.WidthPx - (double)widthPx) / 2);
        var y = (int)Math.Floor((area.HeightPx - (double)heightPx) / 2);
        return new PagePlacement(rotated, scale, x, y, widthPx, heightPx, widthPx > area.WidthPx || heightPx > area.HeightPx);
    }

    private static double SanePoints(double value) => double.IsNaN(value) ? MinPagePoints : Math.Clamp(value, MinPagePoints, MaxPagePoints);

    private static int ToPixels(double value) => (int)Math.Clamp(Math.Round(value), 1, int.MaxValue / 4);
}

/// <summary>Several pages on one sheet: the grid and the cells (print.md section 7 left N-up out of v1; the pipeline supports it so the sheet can offer it).</summary>
public static class SheetLayout
{
    /// <summary>The gap between cells, in points.</summary>
    public const double GapPoints = 10;

    /// <summary>Columns and rows for <paramref name="pagesPerSheet"/> pages on paper of the given orientation: 2 and 6 pages put the long side of the grid along the paper's long side.</summary>
    public static (int Columns, int Rows) Grid(int pagesPerSheet, bool landscapePaper) => pagesPerSheet switch
    {
        2 => landscapePaper ? (2, 1) : (1, 2),
        4 => (2, 2),
        6 => landscapePaper ? (3, 2) : (2, 3),
        9 => (3, 3),
        16 => (4, 4),
        _ => (1, 1),
    };

    /// <summary>
    /// The cells of one sheet in the order pages fill them (device pixels, relative to the printable area). A sheet with one page has one cell: the whole area, with no
    /// gap.
    /// </summary>
    public static IReadOnlyList<(int X, int Y, PaperArea Area)> Cells(PaperArea area, int pagesPerSheet, SheetOrder order)
    {
        var (columns, rows) = Grid(pagesPerSheet, area.IsLandscape);
        if (columns * rows == 1)
        {
            return [(0, 0, area)];
        }

        var gapX = (int)Math.Round(GapPoints * area.DpiX / 72.0);
        var gapY = (int)Math.Round(GapPoints * area.DpiY / 72.0);
        var cellWidth = Math.Max(1, (area.WidthPx - ((columns - 1) * gapX)) / columns);
        var cellHeight = Math.Max(1, (area.HeightPx - ((rows - 1) * gapY)) / rows);
        // The leftover pixels from the integer division go to the outer margin, so the grid stays centred.
        var offsetX = Math.Max(0, (area.WidthPx - ((columns * cellWidth) + ((columns - 1) * gapX))) / 2);
        var offsetY = Math.Max(0, (area.HeightPx - ((rows * cellHeight) + ((rows - 1) * gapY))) / 2);
        var cells = new List<(int, int, PaperArea)>(columns * rows);
        for (var i = 0; i < columns * rows; i++)
        {
            var column = order == SheetOrder.Horizontal ? i % columns : i / rows;
            var row = order == SheetOrder.Horizontal ? i / columns : i % rows;
            cells.Add((offsetX + (column * (cellWidth + gapX)), offsetY + (row * (cellHeight + gapY)), new PaperArea(cellWidth, cellHeight, area.DpiX, area.DpiY)));
        }

        return cells;
    }

    /// <summary>
    /// The paper's orientation for a job. Portrait and landscape are the person's choice. <see cref="PaperOrientation.Auto"/> takes the orientation most sheets would use best:
    /// the one most of the pages have, turned over for 2 and 6 pages per sheet (whose grids lie along the paper's long side). A tie is portrait.
    /// </summary>
    public static bool ChooseLandscape(PaperOrientation orientation, IEnumerable<SizeD> pages, int pagesPerSheet)
    {
        if (orientation != PaperOrientation.Auto)
        {
            return orientation == PaperOrientation.Landscape;
        }

        var landscape = 0;
        var portrait = 0;
        foreach (var page in pages)
        {
            if (page.Width > page.Height)
            {
                landscape++;
            }
            else if (page.Height > page.Width)
            {
                portrait++;
            }
        }

        return pagesPerSheet is 2 or 6 ? portrait > landscape : landscape > portrait;
    }
}
