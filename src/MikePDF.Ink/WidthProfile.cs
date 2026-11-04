namespace MikePDF.Ink;

/// <summary>
/// Per-sample stroke widths (ink-pen.md 3.3 and 7). Pressure is low-passed with a (1,2,1) kernel, mapped through the curve, then
/// the width change is limited to <see cref="InkConstants.MaxWidthSlopePerBaseWidth"/> times the base width per point of path
/// length, so quantised pressure never produces stepped widths. Widths stay within [BaseWidth*MinWidthRatio, BaseWidth].
/// </summary>
public static class WidthProfile
{
    /// <summary>
    /// Computes the width in points for every sample. Never returns NaN: non-finite pressure counts as 1 and non-finite
    /// coordinates add no path length. The highlighter returns its constant base width.
    /// </summary>
    public static float[] Compute(IReadOnlyList<InkSample> samples, InkStyle style)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(style);
        var n = samples.Count;
        var resolved = new ResolvedStyle(style);
        var widths = new float[n];
        if (n == 0)
        {
            return widths;
        }

        if (resolved.IsConstant)
        {
            Array.Fill(widths, resolved.BaseWidth);
            return widths;
        }

        // Low-pass the pressure; the end samples keep their own value so a pen-down or pen-up is not smeared.
        var p = new float[n];
        for (var i = 0; i < n; i++)
        {
            p[i] = InkStroke.ClampPressure(samples[i].Pressure);
        }

        var filtered = new float[n];
        for (var i = 0; i < n; i++)
        {
            filtered[i] = i == 0 || i == n - 1 ? p[i] : (p[i - 1] + (2f * p[i]) + p[i + 1]) * 0.25f;
        }

        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            w[i] = PressureCurves.Width(resolved, filtered[i]);
        }

        // Slope limit: forward then backward pass, so the result is Lipschitz in path length in both directions.
        var slope = InkConstants.MaxWidthSlopePerBaseWidth * resolved.BaseWidth;
        for (var i = 1; i < n; i++)
        {
            var lim = slope * Distance(samples[i - 1], samples[i]);
            w[i] = Math.Clamp(w[i], w[i - 1] - lim, w[i - 1] + lim);
        }

        for (var i = n - 2; i >= 0; i--)
        {
            var lim = slope * Distance(samples[i], samples[i + 1]);
            w[i] = Math.Clamp(w[i], w[i + 1] - lim, w[i + 1] + lim);
        }

        var min = resolved.MinWidth;
        for (var i = 0; i < n; i++)
        {
            widths[i] = (float)Math.Clamp(w[i], min, resolved.BaseWidth);
        }

        return widths;
    }

    private static double Distance(InkSample a, InkSample b)
    {
        var dx = (double)b.X - a.X;
        var dy = (double)b.Y - a.Y;
        var d = Math.Sqrt((dx * dx) + (dy * dy));
        return double.IsFinite(d) ? d : 0.0;
    }
}
