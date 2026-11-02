using MikePDF.Core.Geometry;

namespace MikePDF.Core.Layout;

/// <summary>How the zoom was chosen. Fit modes follow the window size; a number does not.</summary>
public enum FitMode
{
    None,
    Width,
    Page,
}

/// <summary>
/// Zoom arithmetic. A zoom of 1.0 is 100%: one point is 96/72 device-independent pixels, as in Edge and Acrobat. The range is 10% to 6400%
/// (master prompt 2.1).
/// </summary>
public static class ZoomMath
{
    public const double Min = 0.10;
    public const double Max = 64.0;

    /// <summary>96 DIP per inch over 72 points per inch.</summary>
    public const double DipPerPoint = 96.0 / 72.0;

    /// <summary>
    /// The largest scale the engine protocol accepts (schema RenderTile.scale max 64 device pixels per point). Above it tiles are rendered at
    /// this scale and stretched on the GPU (known limitation until the schema allows more; 6400% at 100% display scale needs 85).
    /// </summary>
    public const double MaxEngineScale = 64.0;

    /// <summary>Raw monitor densities outside this range are not believed (Windows reports 96 or garbage for displays whose size it does not know).</summary>
    public const double MinPlausibleDpi = 72.0;

    public const double MaxPlausibleDpi = 400.0;

    /// <summary>The usual stops of Ctrl+= and Ctrl+-.</summary>
    public static readonly IReadOnlyList<double> Steps = [0.10, 0.25, 0.50, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0, 48.0, 64.0];

    public static double Clamp(double zoom) => double.IsNaN(zoom) ? 1.0 : Math.Clamp(zoom, Min, Max);

    /// <summary>Layout pixels (device pixels) per point at a zoom and a display scale (1.0 = 100%, 1.5 = 150%, ...).</summary>
    public static double PixelsPerPoint(double zoom, double dpiScale) => Clamp(zoom) * DipPerPoint * dpiScale;

    /// <summary>
    /// The zoom at which one inch of the page is one inch on the monitor: the monitor's physical pixels per inch (<paramref name="rawDpi"/>) over the
    /// pixels per inch that 100% means at this display scale (96 x <paramref name="dpiScale"/>). Falls back to 100% when the density is missing or not
    /// believable, and is clamped to the zoom range.
    /// </summary>
    public static double ActualSizeZoom(double rawDpi, double dpiScale)
    {
        if (!double.IsFinite(rawDpi) || !double.IsFinite(dpiScale) || dpiScale <= 0 || rawDpi < MinPlausibleDpi || rawDpi > MaxPlausibleDpi)
        {
            return 1.0;
        }

        return Clamp(rawDpi / (96.0 * dpiScale));
    }

    public static double EngineScale(double pixelsPerPoint) => Math.Min(pixelsPerPoint, MaxEngineScale);

    /// <summary>The next stop above (<paramref name="direction"/> &gt; 0) or below the zoom. Stays put at the ends.</summary>
    public static double NextStep(double zoom, int direction)
    {
        zoom = Clamp(zoom);
        const double tolerance = 1e-6;
        if (direction > 0)
        {
            foreach (var step in Steps)
            {
                if (step > zoom * (1 + tolerance))
                {
                    return step;
                }
            }

            return Max;
        }

        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < zoom * (1 - tolerance))
            {
                return Steps[i];
            }
        }

        return Min;
    }

    /// <summary>The zoom at which a page of <paramref name="pageWidthPt"/> fills the viewport width less the margins.</summary>
    public static double FitWidth(double viewportWidthPx, double pageWidthPt, double dpiScale, double marginPx) =>
        Clamp(Math.Max(1, viewportWidthPx - (2 * marginPx)) / (pageWidthPt * DipPerPoint * dpiScale));

    /// <summary>The zoom at which a spread of <paramref name="spreadWidthPt"/> (both pages, and the blank half of a lone page) and a gutter of <paramref name="gutterPx"/> fills the viewport width less the margins.</summary>
    public static double FitSpreadWidth(double viewportWidthPx, double spreadWidthPt, double gutterPx, double dpiScale, double marginPx) =>
        Clamp(Math.Max(1, viewportWidthPx - (2 * marginPx) - gutterPx) / (spreadWidthPt * DipPerPoint * dpiScale));

    /// <summary>The zoom at which the whole spread is visible.</summary>
    public static double FitSpreadPage(double viewportWidthPx, double viewportHeightPx, SizeD spread, double gutterPx, double dpiScale, double marginPx)
    {
        var width = FitSpreadWidth(viewportWidthPx, spread.Width, gutterPx, dpiScale, marginPx);
        var height = Clamp(Math.Max(1, viewportHeightPx - (2 * marginPx)) / (spread.Height * DipPerPoint * dpiScale));
        return Math.Min(width, height);
    }

    /// <summary>The zoom at which the whole page is visible.</summary>
    public static double FitPage(double viewportWidthPx, double viewportHeightPx, SizeD page, double dpiScale, double marginPx)
    {
        var width = FitWidth(viewportWidthPx, page.Width, dpiScale, marginPx);
        var height = Clamp(Math.Max(1, viewportHeightPx - (2 * marginPx)) / (page.Height * DipPerPoint * dpiScale));
        return Math.Min(width, height);
    }
}
