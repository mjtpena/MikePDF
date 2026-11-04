namespace MikePDF.Ink;

/// <summary>
/// Pressure to width mapping (ink-pen.md 3.1, INK-02). All curves are monotonic, map 0 to 0 and 1 to 1, and have a slope of at most
/// <see cref="MaxCurveSlope"/>, so width never jumps.
/// </summary>
public static class PressureCurves
{
    /// <summary>Largest slope of any curve (Soft at 0 and Firm at 1).</summary>
    public const float MaxCurveSlope = 2f;

    /// <summary>
    /// Maps pressure 0..1 through the curve to 0..1. Linear is <c>p</c>, Soft is <c>1-(1-p)^2</c>, Firm is <c>p^2</c>.
    /// Pressure outside 0..1 is clamped; NaN counts as 1 (a mouse).
    /// </summary>
    public static float Map(PressureCurve curve, float pressure01)
    {
        var p = InkStroke.ClampPressure(pressure01);
        return curve switch
        {
            PressureCurve.Soft => 1f - ((1f - p) * (1f - p)),
            PressureCurve.Firm => p * p,
            _ => p,
        };
    }

    /// <summary>
    /// Width in points for a pressure: <c>baseWidth * (minRatio + (1 - minRatio) * Map(curve, p))</c>, so it lies in
    /// [baseWidth*minRatio, baseWidth]. The highlighter always returns its base width. The style is clamped first.
    /// </summary>
    public static float Width(InkStyle style, float pressure01)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Width(new ResolvedStyle(style), pressure01);
    }

    internal static float Width(in ResolvedStyle style, float pressure01)
    {
        if (style.IsConstant)
        {
            return style.BaseWidth;
        }

        var m = Map(style.Curve, pressure01);
        return style.BaseWidth * (style.Ratio + ((1f - style.Ratio) * m));
    }
}

/// <summary>A clamped view of an <see cref="InkStyle"/> used inside the computations.</summary>
internal readonly struct ResolvedStyle
{
    public ResolvedStyle(InkStyle style)
    {
        var c = style.Clamped();
        BaseWidth = c.BaseWidth;
        Ratio = c.MinWidthRatio;
        Curve = c.Curve;
        IsConstant = c.Tool == InkTool.Highlighter;
    }

    public float BaseWidth { get; }

    public float Ratio { get; }

    public PressureCurve Curve { get; }

    public bool IsConstant { get; }

    public float MinWidth => IsConstant ? BaseWidth : BaseWidth * Ratio;
}
