namespace MikePDF.Ink;

/// <summary>
/// Variable-width stroke outline (ink-pen.md 3.8, INK-03, INK-04). <see cref="Polygon"/> builds the closed outline polygon of a
/// centreline with a width per point: left offsets forward, a round cap, right offsets back, a round cap. <see cref="Fit"/> turns
/// that polygon into a Bezier path to fill with the non-zero winding rule.
/// </summary>
public static class StrokeOutline
{
    /// <summary>
    /// Builds the outline polygon. <paramref name="xy"/> holds x,y pairs and <paramref name="widths"/> one width per point, in points.
    /// Consecutive duplicate points are skipped, a point whose disc lies inside its neighbour's disc is merged into it, and a stroke
    /// that reduces to one disc gives a circle polygon of that width. Widths are clamped to 0..1000.
    /// <paramref name="ends"/> picks round caps (the pen) or flat butt caps (the highlighter); a stroke that is one disc is a circle either way, so a tap still leaves a mark.
    /// Throws <see cref="ArgumentException"/> for mismatched lengths, no points, non-finite or out-of-range values.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Polygon(ReadOnlySpan<float> xy, ReadOnlySpan<float> widths, InkEnds ends = InkEnds.Round)
    {
        var geometry = OutlineBuilder.Build(xy, widths, ends);
        return geometry.Polygon;
    }

    /// <summary>
    /// Fits a closed Bezier path to <see cref="Polygon"/> with at most <paramref name="maxDeviation"/> points of error in both
    /// directions (curve to polygon and polygon to curve): Schneider least-squares fitting with corner detection, splitting at the
    /// worst point. A single dot gives four cubics approximating a circle. Coordinates are rounded to 3 decimals.
    /// </summary>
    public static InkPath Fit(ReadOnlySpan<float> xy, ReadOnlySpan<float> widths, double maxDeviation = InkConstants.DefaultFitDeviation, InkEnds ends = InkEnds.Round)
    {
        if (!double.IsFinite(maxDeviation) || maxDeviation <= 0)
        {
            throw new ArgumentException("The deviation must be positive and finite.", nameof(maxDeviation));
        }

        var geometry = OutlineBuilder.Build(xy, widths, ends);
        if (geometry.IsDisc)
        {
            return new InkPath(BezierFitter.Circle(geometry.DiscX, geometry.DiscY, geometry.DiscRadius));
        }

        return new InkPath(BezierFitter.FitClosed(geometry.Polygon, maxDeviation * InkConstants.FitToleranceFactor));
    }
}
