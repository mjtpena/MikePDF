namespace MikePDF.Ink.Tests;

/// <summary>INK-02: pressure curves are monotonic, map 0 to the minimum ratio and 1 to the full width, and have a bounded slope. Also width profiles and styles.</summary>
public sealed class Ink02PressureCurveTests
{
    public static TheoryData<PressureCurve> Curves => new() { PressureCurve.Linear, PressureCurve.Soft, PressureCurve.Firm };

    [Theory]
    [MemberData(nameof(Curves))]
    public void Ink02_CurveIsMonotonicAndMapsZeroToZeroAndOneToOne(PressureCurve curve)
    {
        Assert.Equal(0f, PressureCurves.Map(curve, 0f));
        Assert.Equal(1f, PressureCurves.Map(curve, 1f));
        var previous = -1f;
        for (var i = 0; i <= 1000; i++)
        {
            var v = PressureCurves.Map(curve, i / 1000f);
            Assert.True(v >= previous, $"{curve} not monotonic at {i}");
            Assert.InRange(v, 0f, 1f);
            previous = v;
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void Ink02_CurveSlopeIsBounded(PressureCurve curve)
    {
        const float h = 1f / 1000f;
        for (var i = 0; i < 1000; i++)
        {
            var slope = (PressureCurves.Map(curve, (i + 1) * h) - PressureCurves.Map(curve, i * h)) / h;
            Assert.InRange(slope, 0f, PressureCurves.MaxCurveSlope + 1e-3f);
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void Ink02_WidthMapsZeroToTheMinimumRatioAndOneToTheBaseWidth(PressureCurve curve)
    {
        var style = new InkStyle(InkTool.Pen, 4f, curve, 0.35f);
        Assert.Equal(4f * 0.35f, PressureCurves.Width(style, 0f), 5);
        Assert.Equal(4f, PressureCurves.Width(style, 1f), 5);
        var previous = 0f;
        for (var i = 0; i <= 200; i++)
        {
            var w = PressureCurves.Width(style, i / 200f);
            Assert.True(w >= previous - 1e-6f);
            Assert.InRange(w, 4f * 0.35f - 1e-5f, 4f + 1e-5f);
            previous = w;
        }
    }

    [Fact]
    public void Ink02_SoftIsWiderThanLinearAndFirmNarrowerAtMidPressure()
    {
        var soft = PressureCurves.Map(PressureCurve.Soft, 0.5f);
        var linear = PressureCurves.Map(PressureCurve.Linear, 0.5f);
        var firm = PressureCurves.Map(PressureCurve.Firm, 0.5f);
        Assert.True(soft > linear && linear > firm);
    }

    [Fact]
    public void Ink02_PressureOutsideRangeIsClampedAndNaNCountsAsAMouse()
    {
        Assert.Equal(0f, PressureCurves.Map(PressureCurve.Linear, -5f));
        Assert.Equal(1f, PressureCurves.Map(PressureCurve.Linear, 5f));
        Assert.Equal(1f, PressureCurves.Map(PressureCurve.Soft, float.NaN));
        Assert.Equal(1f, PressureCurves.Map(PressureCurve.Firm, float.PositiveInfinity));
        Assert.Equal(1f, PressureCurves.Map((PressureCurve)99, 1f));
    }

    [Fact]
    public void Ink02_HighlighterWidthIsConstant()
    {
        var style = InkStyle.DefaultHighlighter;
        foreach (var p in new[] { 0f, 0.3f, 1f, float.NaN })
        {
            Assert.Equal(14f, PressureCurves.Width(style, p));
        }

        var samples = new[] { new InkSample(0, 0, 0), new InkSample(5, 0, 1), new InkSample(10, 0, 0.2f) };
        Assert.All(WidthProfile.Compute(samples, style), w => Assert.Equal(14f, w));
    }

    [Fact]
    public void Ink02_StyleDefaultsAndClamping()
    {
        Assert.Equal(new InkStyle(InkTool.Pen, 2f, PressureCurve.Linear, 0.35f), InkStyle.DefaultPen);
        Assert.Equal(new InkStyle(InkTool.Highlighter, 14f, PressureCurve.Linear, 1f), InkStyle.DefaultHighlighter);
        Assert.Equal(0.5f, new InkStyle(InkTool.Pen, 0.01f, PressureCurve.Linear, 0.35f).Clamped().BaseWidth);
        Assert.Equal(24f, new InkStyle(InkTool.Pen, 99f, PressureCurve.Linear, 0.35f).Clamped().BaseWidth);
        Assert.Equal(6f, new InkStyle(InkTool.Highlighter, 1f, PressureCurve.Linear, 0.2f).Clamped().BaseWidth);
        Assert.Equal(40f, new InkStyle(InkTool.Highlighter, 500f, PressureCurve.Linear, 0.2f).Clamped().BaseWidth);
        Assert.Equal(1f, new InkStyle(InkTool.Highlighter, 14f, PressureCurve.Soft, 0.2f).Clamped().MinWidthRatio);
        Assert.Equal(InkConstants.MinWidthRatioFloor, new InkStyle(InkTool.Pen, 2f, PressureCurve.Linear, -3f).Clamped().MinWidthRatio);
        Assert.Equal(1f, new InkStyle(InkTool.Pen, 2f, PressureCurve.Linear, 9f).Clamped().MinWidthRatio);
        var nan = new InkStyle(InkTool.Pen, float.NaN, (PressureCurve)42, float.NaN).Clamped();
        Assert.Equal(new InkStyle(InkTool.Pen, 2f, PressureCurve.Linear, 0.35f), nan);
        Assert.Equal(InkTool.Pen, new InkStyle((InkTool)7, 2f, PressureCurve.Linear, 0.35f).Clamped().Tool);
    }

    [Fact]
    public void Ink02_WidthProfileStaysInRangeNeverNaNAndHasBoundedSlope()
    {
        var style = InkStyle.DefaultPen;
        var rng = new Random(11);
        var samples = new List<InkSample>();
        for (var i = 0; i < 400; i++)
        {
            // Quantised, jumpy pressure (a digitiser with 8 levels) at 0.5 pt spacing.
            samples.Add(new InkSample(i * 0.5f, 0, (float)Math.Round(rng.NextDouble() * 7) / 7f));
        }

        var widths = WidthProfile.Compute(samples, style);
        var maxSlope = InkConstants.MaxWidthSlopePerBaseWidth * style.BaseWidth;
        for (var i = 0; i < widths.Length; i++)
        {
            Assert.True(float.IsFinite(widths[i]));
            Assert.InRange(widths[i], style.BaseWidth * style.MinWidthRatio - 1e-5f, style.BaseWidth + 1e-5f);
            if (i > 0)
            {
                Assert.True(Math.Abs(widths[i] - widths[i - 1]) <= (maxSlope * 0.5) + 1e-4, $"step {i}");
            }
        }
    }

    [Fact]
    public void Ink02_ZeroPressureEndsAreClampedToTheMinimumWidth()
    {
        var samples = Enumerable.Range(0, 30).Select(i => new InkSample(i, 0, i is < 3 or > 26 ? 0f : 1f)).ToArray();
        var widths = WidthProfile.Compute(samples, InkStyle.DefaultPen);
        Assert.Equal(2f * 0.35f, widths[0], 5);
        Assert.Equal(2f * 0.35f, widths[^1], 5);
        Assert.All(widths, w => Assert.True(w >= 0.7f - 1e-5f));
    }

    [Fact]
    public void Ink02_WidthProfileHandlesDuplicatesAndNonFiniteSamples()
    {
        var samples = new[]
        {
            new InkSample(1, 1, 0f), new InkSample(1, 1, 1f), new InkSample(1, 1, 0f), new InkSample(float.NaN, 2, 0.5f),
            new InkSample(3, float.PositiveInfinity, float.NaN), new InkSample(4, 4, 1f),
        };
        var widths = WidthProfile.Compute(samples, InkStyle.DefaultPen);
        Assert.Equal(samples.Length, widths.Length);
        Assert.All(widths, w => Assert.True(float.IsFinite(w) && w >= 0.7f - 1e-5f && w <= 2f + 1e-5f));
        Assert.Empty(WidthProfile.Compute([], InkStyle.DefaultPen));
    }

    [Fact]
    public void Ink02_NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => PressureCurves.Width(null!, 0.5f));
        Assert.Throws<ArgumentNullException>(() => WidthProfile.Compute(null!, InkStyle.DefaultPen));
        Assert.Throws<ArgumentNullException>(() => WidthProfile.Compute([], null!));
    }
}
