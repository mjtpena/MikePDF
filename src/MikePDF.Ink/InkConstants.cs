namespace MikePDF.Ink;

/// <summary>
/// Hard limits shared by capture, journal decoding, batching and appearance building (ink-pen.md 3.7, 3.8, 6, 7).
/// </summary>
public static class StrokeLimits
{
    /// <summary>Most samples one stroke may hold; longer gestures are split by the caller (ink-pen.md 6). Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const int MaxSamplesPerStroke = 20_000;

    /// <summary>Most strokes in one batch (annotation); provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const int MaxStrokesPerBatch = 200;

    /// <summary>Most points in one batch (annotation); provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const int MaxPointsPerBatch = 20_000;

    /// <summary>Largest absolute page-space coordinate, in points, accepted from untrusted data (ink-pen.md 7, INK-28).</summary>
    public const double MaxCoordinate = 10_000_000;

    /// <summary>Bytes per journal sample: x f32, y f32, pressure u8 (ink-pen.md 9).</summary>
    public const int BytesPerSample = 9;

    /// <summary>Appearance content stream cap in bytes (ink-pen.md 3.8: an annotation's appearance is capped at 2 MB).</summary>
    public const int MaxAppearanceBytes = 2 * 1024 * 1024;

    /// <summary>Most path points one eraser gesture may contain; a hostile-input bound, not a UI limit.</summary>
    public const int MaxGesturePoints = 1_000_000;
}

/// <summary>
/// Every tuning constant of the ink library in one place, with the spec reference.
/// Values marked provisional depend on the pen and digitiser and are set finally by the S5 device run (ink-pen.md section 1).
/// </summary>
public static class InkConstants
{
    // ---- Tool defaults (ink-pen.md 3.1) -----------------------------------------------------------------

    /// <summary>Default pen base width in points.</summary>
    public const float DefaultPenWidth = 2f;

    /// <summary>Smallest pen base width in points.</summary>
    public const float MinPenWidth = 0.5f;

    /// <summary>Largest pen base width in points.</summary>
    public const float MaxPenWidth = 24f;

    /// <summary>Default highlighter width in points.</summary>
    public const float DefaultHighlighterWidth = 14f;

    /// <summary>Smallest highlighter width in points.</summary>
    public const float MinHighlighterWidth = 6f;

    /// <summary>Largest highlighter width in points.</summary>
    public const float MaxHighlighterWidth = 40f;

    /// <summary>Default minimum width ratio of the pen (width at zero pressure / base width). Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const float DefaultMinWidthRatio = 0.35f;

    /// <summary>Lowest minimum width ratio accepted by <see cref="InkStyle.Clamped"/>, so a stroke never vanishes.</summary>
    public const float MinWidthRatioFloor = 0.05f;

    // ---- Pressure and width (ink-pen.md 3.1, 7) ---------------------------------------------------------

    /// <summary>
    /// Largest width change per point of path length, as a fraction of the base width (ink-pen.md 7: width changes are
    /// interpolated and limited to a maximum slope). Provisional until the S5 device run (ink-pen.md section 1).
    /// </summary>
    public const double MaxWidthSlopePerBaseWidth = 0.25;

    // ---- Smoothing (ink-pen.md 3.3 step 3) --------------------------------------------------------------

    /// <summary>1-euro position filter minimum cutoff in Hz. Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const double OneEuroMinCutoffHz = 1.0;

    /// <summary>
    /// 1-euro position filter speed coefficient (the cutoff grows by this times the speed in points per second). ink-pen.md 3.3 starts at 0.007,
    /// but with that value the filter lags a 100 to 300 point per second stroke by several points and INK-01 (0.5 DIP) cannot hold (measured: 9 pt
    /// worst deviation at 250 Hz); 0.2 meets INK-01 with margin. Provisional until the S5 device run (ink-pen.md section 1).
    /// </summary>
    public const double OneEuroBeta = 0.2;

    /// <summary>1-euro derivative (speed estimate) cutoff in Hz. The usual 1 Hz delays the speed estimate by about 160 ms, longer than many strokes; 10 Hz follows it. Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const double OneEuroDerivativeCutoffHz = 10.0;

    /// <summary>Pressure low-pass time constant in milliseconds. Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const double PressureTimeConstantMs = 30.0;

    /// <summary>Output points closer than this to the previous emitted point are dropped, in points (ink-pen.md 3.3 step 3).</summary>
    public const double MinPointSpacing = 0.1;

    /// <summary>Smallest time step the smoother uses, in seconds; duplicate and out-of-order timestamps are floored to it.</summary>
    public const double MinTimeStepSeconds = 0.001;

    // ---- Simplification and outline (ink-pen.md 3.8) ----------------------------------------------------

    /// <summary>Douglas-Peucker tolerance for the centreline, in points (ink-pen.md 3.8).</summary>
    public const double DefaultSimplifyTolerance = 0.1;

    /// <summary>Maximum deviation of the fitted Bezier outline from the outline polygon, in points (ink-pen.md 3.8).</summary>
    public const double DefaultFitDeviation = 0.1;

    /// <summary>Chord error allowed when a round cap or join is turned into polygon vertices, in points.</summary>
    public const double OutlineChordTolerance = 0.005;

    /// <summary>Largest arc step in radians used for caps and outer joins (20 degrees).</summary>
    public const double MaxArcStep = Math.PI / 9.0;

    /// <summary>Smallest arc step in radians used for caps and outer joins (2 degrees).</summary>
    public const double MinArcStep = Math.PI / 90.0;

    /// <summary>Fraction of the requested maximum deviation the fitter aims for, leaving room for sampling and rounding error.</summary>
    public const double FitToleranceFactor = 0.9;

    /// <summary>Turn angle in radians above which an outline vertex is treated as a corner when fitting (35 degrees).</summary>
    public const double CornerAngle = 35.0 * Math.PI / 180.0;

    /// <summary>Decimals written for content stream numbers.</summary>
    public const int ContentDecimals = 3;

    /// <summary>Largest width in points the outline builder accepts (ink-pen.md 7: width samples outside 0 to 1000 are clamped).</summary>
    public const double MaxOutlineWidth = 1000.0;

    // ---- Batching (ink-pen.md 3.7) ----------------------------------------------------------------------

    /// <summary>Idle time that closes a batch, in milliseconds. Provisional until the S5 device run (ink-pen.md section 1).</summary>
    public const long BatchIdleMs = 10_000;
}
