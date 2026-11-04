namespace MikePDF.Ink;

/// <summary>
/// Clips a stroke to the page box (ink-pen.md 7, INK-17): a stroke that starts outside the box creates nothing; a stroke that
/// leaves the box is cut where it crosses the edge and the rest is dropped, even if it re-enters.
/// </summary>
public static class StrokeClipper
{
    /// <summary>
    /// Returns an empty list when the first sample is outside the box (edges count as inside), the original stroke (same instance)
    /// when it never leaves the box, otherwise one new stroke (id from <paramref name="newId"/>) holding the samples up to the first
    /// exit plus the exit point interpolated onto the edge, with interpolated pressure.
    /// Throws <see cref="ArgumentException"/> for a non-finite or inverted box.
    /// </summary>
    public static IReadOnlyList<InkStroke> Clip(InkStroke stroke, double left, double bottom, double right, double top, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(newId);
        if (!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top) || left > right || bottom > top)
        {
            throw new ArgumentException("The clip box must be finite with left <= right and bottom <= top.");
        }

        var samples = stroke.Span;
        if (!Inside(samples[0], left, bottom, right, top))
        {
            return [];
        }

        for (var i = 1; i < samples.Length; i++)
        {
            if (Inside(samples[i], left, bottom, right, top))
            {
                continue;
            }

            var kept = new List<InkSample>(i + 1);
            for (var j = 0; j < i; j++)
            {
                kept.Add(samples[j]);
            }

            var a = samples[i - 1];
            var b = samples[i];
            var t = ExitParameter(a, b, left, bottom, right, top);
            if (t > 0)
            {
                kept.Add(new InkSample(
                    (float)Math.Clamp(a.X + (t * ((double)b.X - a.X)), left, right),
                    (float)Math.Clamp(a.Y + (t * ((double)b.Y - a.Y)), bottom, top),
                    (float)(a.Pressure + (t * ((double)b.Pressure - a.Pressure)))));
            }

            return [new InkStroke(newId(), kept)];
        }

        return [stroke];
    }

    private static bool Inside(InkSample s, double left, double bottom, double right, double top) =>
        s.X >= left && s.X <= right && s.Y >= bottom && s.Y <= top;

    // Largest t in [0,1] such that a + t (b - a) is still inside the box, given that a is inside and b is outside.
    private static double ExitParameter(InkSample a, InkSample b, double left, double bottom, double right, double top)
    {
        double dx = (double)b.X - a.X;
        double dy = (double)b.Y - a.Y;
        var t = 1.0;
        if (dx > 0)
        {
            t = Math.Min(t, (right - a.X) / dx);
        }
        else if (dx < 0)
        {
            t = Math.Min(t, (left - a.X) / dx);
        }

        if (dy > 0)
        {
            t = Math.Min(t, (top - a.Y) / dy);
        }
        else if (dy < 0)
        {
            t = Math.Min(t, (bottom - a.Y) / dy);
        }

        return Math.Clamp(t, 0.0, 1.0);
    }
}
