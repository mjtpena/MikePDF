using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Protocol;

namespace MikePDF.Printing;

/// <summary>What a preview of one sheet is asked to show.</summary>
/// <param name="Options">The sheet's choices: scaling, orientation, pages per sheet, annotations, forms, grey scale.</param>
/// <param name="Pages">The document pages on the sheet, 1-based, in order (one, or up to 16 for pages per sheet).</param>
/// <param name="Printable">The printable area of the paper in the printer's own pixels, from the device context (<see cref="PrintPreflight.Paper"/>).</param>
/// <param name="Physical">The whole paper and its margins in the printer's pixels; all zero when the device did not say (the printable area is then the whole paper).</param>
/// <param name="LongEdgePx">The size of the longer edge of the paper in the picture, in pixels.</param>
/// <param name="BandDeadline">How long one band may take.</param>
public sealed record PreviewRequest(PrintOptions Options, IReadOnlyList<int> Pages, PaperArea Printable, PhysicalPaper Physical, int LongEdgePx = 900, TimeSpan? BandDeadline = null);

/// <summary>One page on the previewed sheet and what became of it.</summary>
public sealed record PreviewPage(int Page, bool Rotated, bool Clipped, bool Partial, bool Failed);

/// <summary>The picture of a sheet: the paper with its margins, and the pages where the printer will put them.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Bgra">Straight BGRA, <c>Width * Height * 4</c> bytes, opaque.</param>
/// <param name="Pages">What happened to each page on the sheet.</param>
/// <param name="Landscape">The paper is wider than it is tall.</param>
public sealed record PreviewImage(int Width, int Height, byte[] Bgra, IReadOnlyList<PreviewPage> Pages, bool Landscape)
{
    public bool AnyFailed => Pages.Any(p => p.Failed);
}

/// <summary>
/// The sheet's preview (design 11-print section 4.9): the first sheet of the selection, drawn by the same planner the job uses, so margins, scaling, orientation, pages per sheet, annotations and grey scale are
/// what the printer will do, at a size that fits the screen. The pages come from the engine as bands (<c>RenderBand</c>) and never touch the printer driver. Nothing is sent to a printer.
/// </summary>
public static class PrintPreview
{
    private const int MinDpi = 36;
    private const int MaxDpi = 200;
    private const byte Guide = 200;

    public static async Task<PreviewImage> RenderAsync(IPrintSource source, PreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var printable = request.Printable;
        var printableWidthPt = printable.WidthPoints;
        var printableHeightPt = printable.HeightPoints;
        var physicalKnown = request.Physical.WidthPx >= printable.WidthPx && request.Physical.HeightPx >= printable.HeightPx;
        var paperWidthPt = physicalKnown ? request.Physical.WidthPx * 72.0 / Math.Max(1, printable.DpiX) : printableWidthPt;
        var paperHeightPt = physicalKnown ? request.Physical.HeightPx * 72.0 / Math.Max(1, printable.DpiY) : printableHeightPt;
        var offsetXPt = physicalKnown ? request.Physical.OffsetX * 72.0 / Math.Max(1, printable.DpiX) : 0;
        var offsetYPt = physicalKnown ? request.Physical.OffsetY * 72.0 / Math.Max(1, printable.DpiY) : 0;

        // Pixels per point: the longer edge of the paper is LongEdgePx, whatever the paper. The planner wants whole dpi values.
        var pixelsPerPoint = request.LongEdgePx / Math.Max(1.0, Math.Max(paperWidthPt, paperHeightPt));
        var dpi = Math.Clamp((int)Math.Round(72 * pixelsPerPoint), MinDpi, MaxDpi);
        var scale = dpi / 72.0;
        var area = new PaperArea(
            Math.Max(1, (int)Math.Round(printableWidthPt * scale)),
            Math.Max(1, (int)Math.Round(printableHeightPt * scale)),
            dpi,
            dpi);
        var paperWidth = Math.Max(area.WidthPx, (int)Math.Round(paperWidthPt * scale));
        var paperHeight = Math.Max(area.HeightPx, (int)Math.Round(paperHeightPt * scale));
        var left = Math.Clamp((int)Math.Round(offsetXPt * scale), 0, paperWidth - area.WidthPx);
        var top = Math.Clamp((int)Math.Round(offsetYPt * scale), 0, paperHeight - area.HeightPx);

        var canvas = new byte[paperWidth * paperHeight * 4];
        Array.Fill(canvas, (byte)255);
        DrawGuides(canvas, paperWidth, paperHeight, left, top, area.WidthPx, area.HeightPx);

        var options = request.Options.Normalize() with { AsImage = false };
        var limits = new RenderLimits { MaxDpi = 1200, SlotBytes = source.SlotBytes, BandBytes = Math.Min(RenderLimits.DefaultBandBytes, source.SlotBytes) };
        var planner = new PrintPlanner(options, area, limits);
        var pages = request.Pages.Take(planner.PagesPerSheet).ToArray();
        var results = new List<PreviewPage>(pages.Length);
        if (pages.Length == 0)
        {
            return new PreviewImage(paperWidth, paperHeight, canvas, results, paperWidth > paperHeight);
        }

        var sizes = new List<(int Page, SizeD Size)>(pages.Length);
        foreach (var page in pages)
        {
            var (status, list) = await source.GetPageSizesAsync(page - 1, 1, cancellationToken).ConfigureAwait(false);
            if (status == Status.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            sizes.Add((page, status == Status.Ok && list.Count == 1 ? list[0] : new SizeD(612, 792)));
        }

        var sheet = planner.PlanSheet(sizes);
        var deadline = request.BandDeadline ?? TimeSpan.FromSeconds(15);
        ulong generation = 0;
        foreach (var placed in sheet.Pages)
        {
            var partial = false;
            var failed = false;
            var render = placed.Render;
            var flags = options.BandFlags();
            foreach (var band in render.Bands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var args = new RenderBandArgs((uint)(placed.Page - 1), render.Dpi, placed.Placement.Rotation, (uint)render.WidthPx, (uint)render.HeightPx, (uint)band.Top, (uint)band.Height, flags);
                var result = await source.RenderBandAsync(args, ++generation, deadline, cancellationToken).ConfigureAwait(false);
                if (result.Status == Status.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Band is not { } rendered)
                {
                    failed = true;
                    break;
                }

                using (rendered)
                {
                    partial |= rendered.PartiallyRendered;
                    Blit(canvas, paperWidth, paperHeight, rendered, left + placed.Placement.X, top + band.DestTop, placed.Placement.Width, band.DestHeight, area, left, top, options.Grayscale);
                }
            }

            results.Add(new PreviewPage(placed.Page, placed.Placement.Rotated, placed.Placement.Clipped, partial, failed));
        }

        return new PreviewImage(paperWidth, paperHeight, canvas, results, paperWidth > paperHeight);
    }

    // Draws a band into the canvas at (destX, destY), stretched to destWidth x destHeight when that differs, clipped to the printable area (a page larger than the paper is cut off, as it is on paper).
    private static void Blit(byte[] canvas, int canvasWidth, int canvasHeight, IPrintBand band, int destX, int destY, int destWidth, int destHeight, PaperArea area, int clipLeft, int clipTop, bool grayscale)
    {
        var pixels = band.Pixels;
        var clipRight = clipLeft + area.WidthPx;
        var clipBottom = clipTop + area.HeightPx;
        for (var dy = 0; dy < destHeight; dy++)
        {
            var y = destY + dy;
            if (y < clipTop || y >= clipBottom || y >= canvasHeight)
            {
                continue;
            }

            var sy = destHeight == band.Height ? dy : (int)((long)dy * band.Height / destHeight);
            var row = pixels.Slice(sy * band.Stride, band.Width * 4);
            for (var dx = 0; dx < destWidth; dx++)
            {
                var x = destX + dx;
                if (x < clipLeft || x >= clipRight || x >= canvasWidth)
                {
                    continue;
                }

                var sx = destWidth == band.Width ? dx : (int)((long)dx * band.Width / destWidth);
                var source = row.Slice(sx * 4, 4);
                var target = ((y * canvasWidth) + x) * 4;
                if (grayscale)
                {
                    var luma = (byte)(((source[0] * 29) + (source[1] * 150) + (source[2] * 77) + 128) >> 8);
                    canvas[target] = luma;
                    canvas[target + 1] = luma;
                    canvas[target + 2] = luma;
                }
                else
                {
                    canvas[target] = source[0];
                    canvas[target + 1] = source[1];
                    canvas[target + 2] = source[2];
                }

                canvas[target + 3] = 255;
            }
        }
    }

    // A dashed one pixel line around the printable area, where the paper has margins the printer cannot print in (design 11-print section 5: "margin guides").
    private static void DrawGuides(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
    {
        if (left == 0 && top == 0 && width >= canvasWidth && height >= canvasHeight)
        {
            return;
        }

        void Dot(int x, int y)
        {
            if (x < 0 || y < 0 || x >= canvasWidth || y >= canvasHeight)
            {
                return;
            }

            var i = ((y * canvasWidth) + x) * 4;
            canvas[i] = Guide;
            canvas[i + 1] = Guide;
            canvas[i + 2] = Guide;
        }

        for (var x = left; x < left + width; x++)
        {
            if ((x / 4) % 2 == 0)
            {
                Dot(x, top);
                Dot(x, top + height - 1);
            }
        }

        for (var y = top; y < top + height; y++)
        {
            if ((y / 4) % 2 == 0)
            {
                Dot(left, y);
                Dot(left + width - 1, y);
            }
        }
    }
}
