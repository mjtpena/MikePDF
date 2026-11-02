namespace MikePDF.Core.Printing;

/// <summary>One band of a page: rows <see cref="Top"/> to <see cref="Top"/> + <see cref="Height"/> of the rendered page, drawn on the paper at rows <see cref="DestTop"/> to <see cref="DestTop"/> + <see cref="DestHeight"/>.</summary>
public readonly record struct BandSpec(int Top, int Height, int DestTop, int DestHeight);

/// <summary>
/// How one page is rendered and sent: the size the engine lays the page out at (<see cref="WidthPx"/> x <see cref="HeightPx"/>), the resolution that stands for
/// (<see cref="Dpi"/>, what the protocol and the permission rules see), and the bands to ask for. The bitmap is stretched onto <see cref="PagePlacement"/> when the render is
/// smaller than the place it is printed in (a 600 dpi render on a 1,200 dpi device, a capped big page).
/// </summary>
public sealed record PageRenderPlan(int WidthPx, int HeightPx, uint Dpi, IReadOnlyList<BandSpec> Bands, bool Stretched)
{
    public static PageRenderPlan None { get; } = new(1, 1, 72, [], false);
}

/// <summary>The limits a page is rendered under (ADR-0011 decision 4: memory is capped by pixels per page, not by band size).</summary>
public sealed record RenderLimits
{
    /// <summary>Resolution cap for bands: the printer's resolution, but at most 600 dpi.</summary>
    public const int DefaultMaxDpi = 600;

    /// <summary>"Print as image" is at most 300 dpi (print.md 3.6).</summary>
    public const int ImageMaxDpi = 300;

    /// <summary>A document that allows only low-quality printing is capped at 150 dpi (print.md 3.7).</summary>
    public const int LowQualityMaxDpi = 150;

    /// <summary>About one A4 page at 600 dpi (35 Mpixels) and a little more: the most pixels one page is rendered at.</summary>
    public const long DefaultMaxPagePixels = 40_000_000;

    public const int DefaultBandBytes = 16 << 20;

    /// <summary>The protocol's limits for <c>RenderBand</c> (schema 5).</summary>
    public const int ProtocolMaxPageSide = 65536;
    public const int ProtocolMaxBandHeight = 16384;
    public const int ProtocolMinDpi = 72;
    public const int ProtocolMaxDpi = 1200;

    /// <summary>The cap on the render resolution. Lower than the device's: the bitmap is stretched.</summary>
    public int MaxDpi { get; init; } = DefaultMaxDpi;

    public long MaxPagePixels { get; init; } = DefaultMaxPagePixels;

    /// <summary>The bytes of one band (the pipelining unit). A slot of the print ring is at least this size.</summary>
    public int BandBytes { get; init; } = DefaultBandBytes;

    /// <summary>One band per page: "print as image". The page then has to fit one slot of <see cref="SlotBytes"/>.</summary>
    public bool SingleBand { get; init; }

    /// <summary>The size of one slot of the print ring.</summary>
    public int SlotBytes { get; init; } = DefaultBandBytes;

    /// <summary>The limits for a job: the options (image mode) and the document's permission (low quality).</summary>
    public static RenderLimits For(PrintOptions options, bool lowQualityOnly, int slotBytesForImage = 64 << 20)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maxDpi = DefaultMaxDpi;
        if (options.AsImage)
        {
            maxDpi = Math.Min(maxDpi, ImageMaxDpi);
        }

        if (lowQualityOnly)
        {
            maxDpi = Math.Min(maxDpi, LowQualityMaxDpi);
        }

        return options.AsImage
            ? new RenderLimits { MaxDpi = maxDpi, SingleBand = true, SlotBytes = slotBytesForImage, BandBytes = slotBytesForImage }
            : new RenderLimits { MaxDpi = maxDpi };
    }
}

/// <summary>Pure band and resolution planning (print.md 3.5).</summary>
public static class RenderPlanner
{
    /// <summary>
    /// The bands that tile a page of <paramref name="heightPx"/> rows: no gap, no overlap, the last one short. A band is <c>bandBytes / (4 * widthPx)</c> rows (at least 1, at most
    /// the protocol's 16,384), so no band is larger than <paramref name="bandBytes"/> unless one row is.
    /// </summary>
    public static IReadOnlyList<(int Top, int Height)> Tile(int widthPx, int heightPx, int bandBytes, bool singleBand = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(widthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heightPx, 1);
        var rows = singleBand ? heightPx : (int)Math.Clamp(bandBytes / (4L * widthPx), 1, RenderLimits.ProtocolMaxBandHeight);
        var bands = new List<(int, int)>((heightPx + rows - 1) / rows);
        for (var top = 0; top < heightPx; top += rows)
        {
            bands.Add((top, Math.Min(rows, heightPx - top)));
        }

        return bands;
    }

    /// <summary>
    /// The render for a page placed at <paramref name="placement"/> on paper of <paramref name="paper"/>. The page is rendered at the device's resolution unless a limit says
    /// less: the dpi cap, the pixels per page, the protocol's side limit, and for one band per page the slot. Bands that lie wholly outside the paper are left out.
    /// </summary>
    public static PageRenderPlan Plan(PagePlacement placement, PaperArea paper, RenderLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (placement.Width < 1 || placement.Height < 1 || placement.X >= paper.WidthPx || placement.X + placement.Width <= 0)
        {
            return PageRenderPlan.None; // the page is entirely off the paper
        }

        var dpiX = Math.Max(1, paper.DpiX);
        var dpiY = Math.Max(1, paper.DpiY);
        var ratioX = Math.Min(1.0, (double)limits.MaxDpi / dpiX);
        var ratioY = Math.Min(1.0, (double)limits.MaxDpi / dpiY);
        double width = Math.Max(1, Math.Floor(placement.Width * ratioX));
        double height = Math.Max(1, Math.Floor(placement.Height * ratioY));

        // One factor for both sides so the page keeps its shape.
        var factor = 1.0;
        factor = Math.Min(factor, Math.Sqrt(limits.MaxPagePixels / (width * height)));
        factor = Math.Min(factor, RenderLimits.ProtocolMaxPageSide / width);
        factor = Math.Min(factor, RenderLimits.ProtocolMaxPageSide / height);
        if (limits.SingleBand)
        {
            factor = Math.Min(factor, Math.Sqrt(limits.SlotBytes / 4.0 / (width * height)));
            factor = Math.Min(factor, RenderLimits.ProtocolMaxBandHeight / height);
        }

        var widthPx = (int)Math.Max(1, Math.Floor(width * factor));
        var heightPx = (int)Math.Max(1, Math.Floor(height * factor));
        var tiles = Tile(widthPx, heightPx, limits.BandBytes, limits.SingleBand);

        var bands = new List<BandSpec>(tiles.Count);
        foreach (var (top, rows) in tiles)
        {
            var destTop = placement.Y + Scale(top, placement.Height, heightPx);
            var destBottom = placement.Y + Scale(top + rows, placement.Height, heightPx);
            if (destBottom > 0 && destTop < paper.HeightPx && destBottom > destTop)
            {
                bands.Add(new BandSpec(top, rows, destTop, destBottom - destTop));
            }
        }

        var dpi = (uint)Math.Clamp(Math.Round((double)dpiX * widthPx / placement.Width), RenderLimits.ProtocolMinDpi, RenderLimits.ProtocolMaxDpi);
        return new PageRenderPlan(widthPx, heightPx, dpi, bands, widthPx != placement.Width || heightPx != placement.Height);
    }

    /// <summary>Row <paramref name="row"/> of a render of <paramref name="renderRows"/> rows on a place <paramref name="destRows"/> rows high, rounded the same way for both edges of a band so that neighbours meet.</summary>
    private static int Scale(int row, int destRows, int renderRows) => (int)(((row * (long)destRows) + (renderRows / 2)) / renderRows);
}
