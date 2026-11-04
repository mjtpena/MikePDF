namespace MikePDF.Ink;

/// <summary>One ink sample in page space: X and Y in PDF points, Pressure 0..1 (a mouse reports 1).</summary>
public readonly record struct InkSample(float X, float Y, float Pressure);

/// <summary>A raw capture sample with its device timestamp, the input of <see cref="StrokeSmoother"/>. Position in points, Pressure 0..1, timestamp in milliseconds.</summary>
public readonly record struct RawInkSample(float X, float Y, float Pressure, long TimestampMs);

/// <summary>The kind of ink tool (ink-pen.md 3.1).</summary>
public enum InkTool
{
    /// <summary>Variable width by pressure.</summary>
    Pen = 0,

    /// <summary>Constant width, translucent highlighter with flat (butt) ends.</summary>
    Highlighter = 1,
}

/// <summary>How the two ends of a stroke outline are finished (ink-pen.md 3.8).</summary>
public enum InkEnds
{
    /// <summary>A half circle of the end width (the pen).</summary>
    Round = 0,

    /// <summary>Straight across at the end point, so the stroke stops exactly there (the highlighter).</summary>
    Flat = 1,
}

/// <summary>How pressure is turned into width (ink-pen.md 3.1).</summary>
public enum PressureCurve
{
    /// <summary>Width follows pressure linearly.</summary>
    Linear = 0,

    /// <summary>Light pressure already gives a wide line.</summary>
    Soft = 1,

    /// <summary>Wide lines need firm pressure.</summary>
    Firm = 2,
}

/// <summary>
/// The width style of a stroke: tool, base width in points, pressure curve and the width ratio at zero pressure.
/// Pen defaults are 2 pt, Linear, 0.35; the highlighter is 14 pt (6..40) with a constant width (ratio 1).
/// </summary>
public sealed record InkStyle(InkTool Tool, float BaseWidth, PressureCurve Curve, float MinWidthRatio)
{
    /// <summary>The default pen style (ink-pen.md 3.1).</summary>
    public static InkStyle DefaultPen { get; } = new(InkTool.Pen, InkConstants.DefaultPenWidth, PressureCurve.Linear, InkConstants.DefaultMinWidthRatio);

    /// <summary>The default highlighter style (ink-pen.md 3.1).</summary>
    public static InkStyle DefaultHighlighter { get; } = new(InkTool.Highlighter, InkConstants.DefaultHighlighterWidth, PressureCurve.Linear, 1f);

    /// <summary>
    /// Returns a style with every value inside its legal range: width 0.5..24 (pen) or 6..40 (highlighter), ratio
    /// <see cref="InkConstants.MinWidthRatioFloor"/>..1 (always 1 for the highlighter), unknown enum values replaced by the defaults.
    /// Non-finite numbers fall back to the tool default.
    /// </summary>
    public InkStyle Clamped()
    {
        var tool = Tool == InkTool.Highlighter ? InkTool.Highlighter : InkTool.Pen;
        var curve = Curve is PressureCurve.Soft or PressureCurve.Firm ? Curve : PressureCurve.Linear;
        if (tool == InkTool.Highlighter)
        {
            var w = float.IsFinite(BaseWidth) ? Math.Clamp(BaseWidth, InkConstants.MinHighlighterWidth, InkConstants.MaxHighlighterWidth) : InkConstants.DefaultHighlighterWidth;
            return new InkStyle(tool, w, curve, 1f);
        }

        var pw = float.IsFinite(BaseWidth) ? Math.Clamp(BaseWidth, InkConstants.MinPenWidth, InkConstants.MaxPenWidth) : InkConstants.DefaultPenWidth;
        var ratio = float.IsFinite(MinWidthRatio) ? Math.Clamp(MinWidthRatio, InkConstants.MinWidthRatioFloor, 1f) : InkConstants.DefaultMinWidthRatio;
        return new InkStyle(tool, pw, curve, ratio);
    }
}

/// <summary>
/// An immutable stroke: an id and 1 to <see cref="StrokeLimits.MaxSamplesPerStroke"/> samples in page space.
/// The constructor copies the samples, clamps pressure to 0..1 (NaN becomes 1) and rejects non-finite or out-of-range coordinates.
/// </summary>
public sealed class InkStroke
{
    private readonly InkSample[] _samples;

    /// <summary>Creates a stroke. Throws <see cref="ArgumentException"/> for an empty id, no samples, too many samples or bad coordinates.</summary>
    public InkStroke(string id, IReadOnlyList<InkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("A stroke needs an id.", nameof(id));
        }

        if (samples.Count < 1 || samples.Count > StrokeLimits.MaxSamplesPerStroke)
        {
            throw new ArgumentException($"A stroke holds 1 to {StrokeLimits.MaxSamplesPerStroke} samples.", nameof(samples));
        }

        var copy = new InkSample[samples.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var s = samples[i];
            if (!IsValidCoordinate(s.X) || !IsValidCoordinate(s.Y))
            {
                throw new ArgumentException($"Sample {i} has a non-finite or out-of-range coordinate.", nameof(samples));
            }

            copy[i] = new InkSample(s.X, s.Y, ClampPressure(s.Pressure));
        }

        Id = id;
        _samples = copy;
        Samples = Array.AsReadOnly(copy);
    }

    /// <summary>The stroke id (a journal id owned by the caller).</summary>
    public string Id { get; }

    /// <summary>The samples, in page space points.</summary>
    public IReadOnlyList<InkSample> Samples { get; }

    internal ReadOnlySpan<InkSample> Span => _samples;

    internal static bool IsValidCoordinate(float v) => float.IsFinite(v) && Math.Abs(v) <= StrokeLimits.MaxCoordinate;

    internal static float ClampPressure(float p) => float.IsNaN(p) ? 1f : Math.Clamp(p, 0f, 1f);
}

/// <summary>Raised when a result would exceed a hard size limit (for example the 2 MiB appearance cap).</summary>
public sealed class InkLimitException(string message) : Exception(message);
