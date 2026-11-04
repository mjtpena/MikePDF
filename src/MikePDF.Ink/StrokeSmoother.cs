namespace MikePDF.Ink;

/// <summary>
/// Streaming stroke smoother (ink-pen.md 3.3 step 3, INK-01): a 1-euro low-pass on position (the cutoff grows with speed),
/// an exponential low-pass on pressure, removal of points closer than <see cref="InkConstants.MinPointSpacing"/> to the previous
/// emitted point. The first sample and the last sample (see <see cref="TryFinish"/>) are emitted exactly as captured.
/// Allocation-free after construction. Duplicate, out-of-order and zero-pressure input is safe; non-finite positions are ignored.
/// Not thread safe. One instance per stroke at a time; call <see cref="Reset"/> or <see cref="TryFinish"/> between strokes.
/// </summary>
public sealed class StrokeSmoother
{
    private readonly double _minCutoffHz;
    private readonly double _beta;
    private readonly double _pressureTauSeconds;
    private readonly double _derivativeCutoffHz;
    private bool _hasVelocity;

    private bool _started;
    private long _lastTimeMs;
    private double _rawX;
    private double _rawY;
    private double _filteredX;
    private double _filteredY;
    private double _filteredPressure;
    private double _velocityX;
    private double _velocityY;
    private double _emittedX;
    private double _emittedY;
    private bool _lastRawWasEmitted;
    private RawInkSample _lastRaw;

    /// <summary>Creates a smoother with the provisional constants of <see cref="InkConstants"/>.</summary>
    public StrokeSmoother()
        : this(InkConstants.OneEuroMinCutoffHz, InkConstants.OneEuroBeta, InkConstants.PressureTimeConstantMs)
    {
    }

    /// <summary>Creates a smoother with explicit tuning (for the device tuning run and tests).</summary>
    /// <param name="minCutoffHz">1-euro minimum cutoff in Hz, greater than 0.</param>
    /// <param name="beta">Speed coefficient, 0 or more.</param>
    /// <param name="pressureTimeConstantMs">Pressure time constant in milliseconds, 0 or more.</param>
    /// <param name="derivativeCutoffHz">Cutoff of the speed estimate in Hz, greater than 0.</param>
    public StrokeSmoother(double minCutoffHz, double beta, double pressureTimeConstantMs, double derivativeCutoffHz = InkConstants.OneEuroDerivativeCutoffHz)
    {
        if (!double.IsFinite(minCutoffHz) || minCutoffHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minCutoffHz));
        }

        if (!double.IsFinite(beta) || beta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beta));
        }

        if (!double.IsFinite(pressureTimeConstantMs) || pressureTimeConstantMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pressureTimeConstantMs));
        }

        if (!double.IsFinite(derivativeCutoffHz) || derivativeCutoffHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(derivativeCutoffHz));
        }

        _minCutoffHz = minCutoffHz;
        _beta = beta;
        _pressureTauSeconds = pressureTimeConstantMs / 1000.0;
        _derivativeCutoffHz = derivativeCutoffHz;
    }

    /// <summary>Forgets the current stroke so the next sample starts a new one.</summary>
    public void Reset()
    {
        _started = false;
        _lastRawWasEmitted = false;
        _lastRaw = default;
    }

    /// <summary>
    /// Feeds one raw sample. Returns true and the smoothed sample when the point should be kept, false when it was dropped
    /// (too close to the previous kept point, or a non-finite position).
    /// </summary>
    public bool TryAdd(in RawInkSample raw, out InkSample smoothed)
    {
        smoothed = default;
        if (!float.IsFinite(raw.X) || !float.IsFinite(raw.Y))
        {
            return false;
        }

        var pressure = InkStroke.ClampPressure(raw.Pressure);
        var clean = new RawInkSample(raw.X, raw.Y, pressure, raw.TimestampMs);

        if (!_started)
        {
            _started = true;
            _lastTimeMs = raw.TimestampMs;
            _rawX = _filteredX = _emittedX = raw.X;
            _rawY = _filteredY = _emittedY = raw.Y;
            _filteredPressure = pressure;
            _velocityX = 0;
            _velocityY = 0;
            _hasVelocity = false;
            _lastRaw = clean;
            _lastRawWasEmitted = true;
            smoothed = new InkSample(raw.X, raw.Y, pressure);
            return true;
        }

        // Out-of-order and duplicate timestamps get the smallest legal step; the clock never runs backwards inside the filter.
        var dt = (raw.TimestampMs - _lastTimeMs) / 1000.0;
        if (dt < InkConstants.MinTimeStepSeconds)
        {
            dt = InkConstants.MinTimeStepSeconds;
        }

        if (raw.TimestampMs > _lastTimeMs)
        {
            _lastTimeMs = raw.TimestampMs;
        }

        var derivativeAlpha = _hasVelocity ? Alpha(_derivativeCutoffHz, dt) : 1.0;
        _hasVelocity = true;
        _velocityX += derivativeAlpha * (((raw.X - _rawX) / dt) - _velocityX);
        _velocityY += derivativeAlpha * (((raw.Y - _rawY) / dt) - _velocityY);
        var speed = Math.Sqrt((_velocityX * _velocityX) + (_velocityY * _velocityY));
        var cutoff = _minCutoffHz + (_beta * speed);
        var alpha = Alpha(cutoff, dt);
        _filteredX += alpha * (raw.X - _filteredX);
        _filteredY += alpha * (raw.Y - _filteredY);
        _filteredPressure += (dt / (_pressureTauSeconds + dt)) * (pressure - _filteredPressure);
        _rawX = raw.X;
        _rawY = raw.Y;
        _lastRaw = clean;

        var ex = _filteredX - _emittedX;
        var ey = _filteredY - _emittedY;
        if ((ex * ex) + (ey * ey) < InkConstants.MinPointSpacing * InkConstants.MinPointSpacing)
        {
            _lastRawWasEmitted = false;
            return false;
        }

        _emittedX = _filteredX;
        _emittedY = _filteredY;
        _lastRawWasEmitted = false;
        smoothed = new InkSample((float)_filteredX, (float)_filteredY, (float)Math.Clamp(_filteredPressure, 0.0, 1.0));
        return true;
    }

    /// <summary>
    /// Ends the stroke. Returns true with the last captured sample, unmoved, when it differs from the last emitted point (the end of
    /// a stroke is never smoothed away); false when nothing is pending. The smoother is then reset.
    /// </summary>
    public bool TryFinish(out InkSample last)
    {
        last = default;
        var pending = _started && !_lastRawWasEmitted;
        if (pending)
        {
            var dx = _lastRaw.X - _emittedX;
            var dy = _lastRaw.Y - _emittedY;
            pending = (dx * dx) + (dy * dy) > 1e-12;
            if (pending)
            {
                last = new InkSample(_lastRaw.X, _lastRaw.Y, _lastRaw.Pressure);
            }
        }

        Reset();
        return pending;
    }

    /// <summary>Smooths a whole captured stroke in one call (first and last samples unmoved).</summary>
    public static InkSample[] SmoothAll(IReadOnlyList<RawInkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var smoother = new StrokeSmoother();
        var result = new List<InkSample>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            if (smoother.TryAdd(samples[i], out var s))
            {
                result.Add(s);
            }
        }

        if (smoother.TryFinish(out var last))
        {
            result.Add(last);
        }

        return [.. result];
    }

    private static double Alpha(double cutoffHz, double dt)
    {
        var tau = 1.0 / (2.0 * Math.PI * cutoffHz);
        return 1.0 / (1.0 + (tau / dt));
    }
}
