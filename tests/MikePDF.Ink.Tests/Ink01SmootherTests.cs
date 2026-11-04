using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-01: smoothing keeps the endpoints, stays within 0.5 DIP of the noise-free path, never produces NaN and allocates nothing per sample.</summary>
public sealed class Ink01SmootherTests
{
    private const double JitterDip = 0.3;
    private const double AllowedDeviation = 0.5;

    // Pen speeds of 100 to 300 points per second at 250 Hz (4 ms), plus slower rates.
    private static readonly IReadOnlyList<PenTrace> SmoothTraces = Traces.BuildSmooth(100, 20261104, 1.2);

    private static (double X, double Y) Disc(Random rng)
    {
        var a = rng.NextDouble() * 2 * Math.PI;
        var r = Math.Sqrt(rng.NextDouble());
        return (r * Math.Cos(a), r * Math.Sin(a));
    }

    // Jitter is uniform in a disc of radius 0.3 DIP; the end points are the exact pen-down and pen-up positions.
    private static RawInkSample[] Jittered(InkSample[] reference, Random rng, long dtMs)
    {
        var raw = new RawInkSample[reference.Length];
        for (var i = 0; i < reference.Length; i++)
        {
            var (jx, jy) = Disc(rng);
            raw[i] = new RawInkSample(reference[i].X + (float)(JitterDip * jx), reference[i].Y + (float)(JitterDip * jy), reference[i].Pressure, i * dtMs);
        }

        raw[0] = new RawInkSample(reference[0].X, reference[0].Y, reference[0].Pressure, 0);
        raw[^1] = new RawInkSample(reference[^1].X, reference[^1].Y, reference[^1].Pressure, (reference.Length - 1) * dtMs);
        return raw;
    }

    private static List<InkSample> Run(StrokeSmoother smoother, RawInkSample[] raw)
    {
        var output = new List<InkSample>();
        foreach (var r in raw)
        {
            if (smoother.TryAdd(r, out var s))
            {
                output.Add(s);
            }
        }

        if (smoother.TryFinish(out var last))
        {
            output.Add(last);
        }

        return output;
    }

    private static double WorstDeviation(StrokeSmoother smoother, long dtMs)
    {
        var rng = new Random(77);
        var worst = 0.0;
        foreach (var trace in SmoothTraces)
        {
            var polyline = trace.Samples.Select(s => ((double)s.X, (double)s.Y)).ToList();
            var output = Run(smoother, Jittered(trace.Samples, rng, dtMs));
            foreach (var s in output)
            {
                worst = Math.Max(worst, Geometry.DistToPolyline(polyline, s.X, s.Y, closed: false));
            }
        }

        return worst;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Ink01_OutputStaysWithinHalfDipOfTheNoiseFreeReference_For100Traces(long dtMs)
    {
        var worst = WorstDeviation(new StrokeSmoother(), dtMs);
        Assert.True(worst <= AllowedDeviation, $"worst deviation {worst:F3} DIP at {dtMs} ms sampling");
    }

    [Fact]
    public void Ink01_StartingConstantsOfTheSpecLagFarBeyondTheBound_DocumentedFinding()
    {
        // ink-pen.md 3.3 starts at cutoff 1.0 Hz, beta 0.007 and a 1 Hz speed estimate. Measured here: the filter lags by several
        // points at pen speed, so INK-01 cannot hold with them. InkConstants carries the tuned values; this test records the finding.
        var worst = WorstDeviation(new StrokeSmoother(1.0, 0.007, 30.0, 1.0), 4);
        Assert.True(worst > 2.0, $"worst deviation {worst:F3}");
    }

    [Fact]
    public void Ink01_FirstAndLastPointsAreNeverMoved_For100Traces()
    {
        var rng = new Random(5);
        foreach (var trace in SmoothTraces)
        {
            var raw = Jittered(trace.Samples, rng, 4);
            var output = Run(new StrokeSmoother(), raw);
            Assert.Equal(raw[0].X, output[0].X);
            Assert.Equal(raw[0].Y, output[0].Y);
            Assert.Equal(raw[0].Pressure, output[0].Pressure);
            Assert.Equal(raw[^1].X, output[^1].X);
            Assert.Equal(raw[^1].Y, output[^1].Y);
            Assert.Equal(raw[^1].Pressure, output[^1].Pressure);
        }
    }

    [Fact]
    public void Ink01_ARestingPenIsSmoothedToAFractionOfTheJitter()
    {
        var rng = new Random(9);
        var smoother = new StrokeSmoother();
        double rawSq = 0, outSq = 0;
        var outCount = 0;
        for (var i = 0; i < 1000; i++)
        {
            var (jx, jy) = Disc(rng);
            var raw = new RawInkSample((float)(50 + (JitterDip * jx)), (float)(50 + (JitterDip * jy)), 0.5f, i * 4L);
            rawSq += JitterDip * JitterDip * ((jx * jx) + (jy * jy));
            if (smoother.TryAdd(raw, out var s) && i > 100)
            {
                outSq += ((s.X - 50.0) * (s.X - 50.0)) + ((s.Y - 50.0) * (s.Y - 50.0));
                outCount++;
            }
        }

        var rawRms = Math.Sqrt(rawSq / 1000);
        var outRms = outCount == 0 ? 0 : Math.Sqrt(outSq / outCount);
        Assert.True(outRms < rawRms / 3, $"raw {rawRms:F3}, smoothed {outRms:F3}");
    }

    [Fact]
    public void Ink01_NoNaNForDuplicateOutOfOrderAndZeroPressureInput()
    {
        var smoother = new StrokeSmoother();
        var inputs = new List<RawInkSample>();
        for (var i = 0; i < 50; i++)
        {
            inputs.Add(new RawInkSample(10, 10, 0f, 100)); // the same sample and timestamp over and over, zero pressure
        }

        for (var i = 0; i < 50; i++)
        {
            inputs.Add(new RawInkSample(10 + i, 10 - i, 0f, 100 - (i * 3L))); // timestamps running backwards
        }

        inputs.Add(new RawInkSample(float.NaN, 5, 0.5f, 5000)); // dropped, must not corrupt the state
        inputs.Add(new RawInkSample(5, float.PositiveInfinity, 0.5f, 5001));
        inputs.Add(new RawInkSample(60, 60, float.NaN, 5002));
        inputs.Add(new RawInkSample(61, 61, float.PositiveInfinity, 5002));
        inputs.Add(new RawInkSample(62, 62, -3f, long.MaxValue));
        inputs.Add(new RawInkSample(63, 63, 7f, long.MinValue));
        inputs.Add(new RawInkSample(1e7f, -1e7f, 0.5f, 0));
        foreach (var raw in inputs)
        {
            if (smoother.TryAdd(raw, out var s))
            {
                Assert.True(float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Pressure), $"{s}");
                Assert.InRange(s.Pressure, 0f, 1f);
            }
        }

        if (smoother.TryFinish(out var last))
        {
            Assert.True(float.IsFinite(last.X) && float.IsFinite(last.Y) && float.IsFinite(last.Pressure));
        }
    }

    [Fact]
    public void Ink01_AllZeroPressureStrokeGivesZeroPressureOutput()
    {
        var raw = Enumerable.Range(0, 60).Select(i => new RawInkSample(i * 2f, 0, 0f, i * 4L)).ToArray();
        var output = Run(new StrokeSmoother(), raw);
        Assert.All(output, s => Assert.Equal(0f, s.Pressure));
    }

    [Fact]
    public void Ink01_PointsCloserThanTheMinimumSpacingAreDropped()
    {
        var smoother = new StrokeSmoother();
        Assert.True(smoother.TryAdd(new RawInkSample(10, 10, 1, 0), out _));
        for (var i = 1; i < 20; i++)
        {
            Assert.False(smoother.TryAdd(new RawInkSample(10 + (0.001f * i), 10, 1, i * 4L), out _));
        }

        // The pen-up position is kept exactly even though it is closer than 0.1 to the last emitted point.
        Assert.True(smoother.TryFinish(out var last));
        Assert.Equal(10.019f, last.X, 4);
    }

    [Fact]
    public void Ink01_ASingleSampleStrokeHasNothingPendingAtTheEnd()
    {
        var smoother = new StrokeSmoother();
        Assert.True(smoother.TryAdd(new RawInkSample(3, 4, 0.7f, 0), out var first));
        Assert.Equal(new InkSample(3, 4, 0.7f), first);
        Assert.False(smoother.TryFinish(out _));
        Assert.False(smoother.TryFinish(out _));
    }

    [Fact]
    public void Ink01_ResetStartsANewStroke()
    {
        var smoother = new StrokeSmoother();
        smoother.TryAdd(new RawInkSample(0, 0, 1, 0), out _);
        smoother.TryAdd(new RawInkSample(50, 0, 1, 4), out _);
        smoother.Reset();
        Assert.True(smoother.TryAdd(new RawInkSample(200, 200, 0.3f, 1000), out var s));
        Assert.Equal(new InkSample(200, 200, 0.3f), s);
    }

    [Fact]
    public void Ink01_SmoothAllMatchesTheStreamingPath()
    {
        var raw = Jittered(SmoothTraces[3].Samples, new Random(3), 4);
        var streamed = Run(new StrokeSmoother(), raw);
        Assert.Equal(streamed, StrokeSmoother.SmoothAll(raw));
        Assert.Empty(StrokeSmoother.SmoothAll([]));
    }

    [Fact]
    public void Ink01_ConstructorRejectsBadTuning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSmoother(0, 0.1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSmoother(1, -1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSmoother(1, 0.1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeSmoother(1, 0.1, 30, 0));
    }

    [Fact]
    public void Ink01_SteadyStateProcessingAllocatesZeroBytesPerSample()
    {
        var smoother = new StrokeSmoother();
        for (var i = 0; i < 5000; i++)
        {
            smoother.TryAdd(new RawInkSample(i * 0.5f, (i % 7) * 0.1f, 0.5f, i * 4L), out _); // warm up the JIT
        }

        smoother.Reset();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var emitted = 0;
        for (var i = 0; i < 200_000; i++)
        {
            if (smoother.TryAdd(new RawInkSample(i * 0.5f, (i % 7) * 0.1f, (i % 10) / 10f, i * 4L), out _))
            {
                emitted++;
            }
        }

        smoother.TryFinish(out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(emitted > 1000);
        Assert.Equal(0L, allocated);
    }
}
