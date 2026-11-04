using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-03: outline geometry. The polygon contains every centreline point at its width, has no area artefacts at sharp turns, and a dot is a circle.</summary>
public sealed class Ink03OutlineTests
{
    private static readonly InkStyle WideStyle = new(InkTool.Pen, 6f, PressureCurve.Linear, 0.35f);

    private static (float[] Xy, float[] Widths) Flat(InkSample[] samples, InkStyle style)
    {
        var widths = WidthProfile.Compute(samples, style);
        var xy = new float[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            xy[2 * i] = samples[i].X;
            xy[(2 * i) + 1] = samples[i].Y;
        }

        return (xy, widths);
    }

    [Fact]
    public void Ink03_OutlineContainsEveryCentrelinePointAtItsWidth_For200Traces()
    {
        var traces = Traces.Build(200);
        Assert.Equal(200, traces.Count);
        foreach (var trace in traces)
        {
            var (xy, widths) = Flat(trace.Samples, WideStyle);
            var polygon = StrokeOutline.Polygon(xy, widths);
            for (var i = 0; i < widths.Length; i++)
            {
                var cx = (double)xy[2 * i];
                var cy = (double)xy[(2 * i) + 1];
                var r = widths[i] * 0.5 * 0.97;
                Assert.True(Geometry.Winding(polygon, cx, cy) != 0, $"{trace.Name} centre {i} outside");
                // Four directions per sample, rotated by the sample index so all eight compass directions are covered along a stroke.
                for (var k = 0; k < 4; k++)
                {
                    var a = ((k * 2) + (i % 2)) * Math.PI / 4;
                    Assert.True(Geometry.Winding(polygon, cx + (r * Math.Cos(a)), cy + (r * Math.Sin(a))) != 0, $"{trace.Name} disc {i} direction {k} not covered");
                }
            }
        }
    }

    [Fact]
    public void Ink03_NoVertexLiesFartherFromTheCentrelineThanHalfTheWidestWidth_For200Traces()
    {
        foreach (var trace in Traces.Build(200))
        {
            var (xy, widths) = Flat(trace.Samples, WideStyle);
            var centreline = trace.Samples.Select(s => ((double)s.X, (double)s.Y)).ToList();
            var maxRadius = widths.Max() * 0.5;
            foreach (var v in StrokeOutline.Polygon(xy, widths))
            {
                Assert.True(Geometry.DistToPolyline(centreline, v.X, v.Y, closed: false) <= maxRadius + 0.01, $"{trace.Name} has a spike");
            }
        }
    }

    [Fact]
    public void Ink03_SharpTurnsChangeTheFilledAreaByLessThanOnePercent()
    {
        // Constant width makes the union of discs and rectangles swept along the centreline an exact reference.
        var traces = Traces.Build(40, sharpOnly: true);
        foreach (var trace in traces)
        {
            var points = trace.Samples.Select(s => ((double)s.X, (double)s.Y)).ToList();
            foreach (var width in new[] { 2f, 6f })
            {
                var xy = trace.Samples.SelectMany(s => new[] { s.X, s.Y }).ToArray();
                var widths = Enumerable.Repeat(width, trace.Samples.Length).ToArray();
                var polygon = StrokeOutline.Polygon(xy, widths);
                var actual = Geometry.NonZeroArea(polygon, 0.05);
                var expected = Geometry.SweptUnionArea(points, width / 2.0, 0.05);
                Assert.True(Math.Abs(actual - expected) / expected < 0.01, $"{trace.Name} width {width}: area {actual:F2} vs {expected:F2}");
            }
        }
    }

    [Fact]
    public void Ink03_AHairpinKeepsItsAreaAndAddsNoSpike()
    {
        float[] xy = [0, 0, 20, 0, 0, 1];
        float[] widths = [4, 4, 4];
        var polygon = StrokeOutline.Polygon(xy, widths);
        var actual = Geometry.NonZeroArea(polygon, 0.02);
        var expected = Geometry.SweptUnionArea([(0, 0), (20, 0), (0, 1)], 2, 0.02);
        Assert.True(Math.Abs(actual - expected) / expected < 0.01, $"{actual:F2} vs {expected:F2}");
    }

    [Fact]
    public void Ink03_WideStrokeAroundATightCurveStillCoversEveryDisc()
    {
        // 24 pt wide on a radius-5 arc at 0.2 pt spacing: the inner offset curve is degenerate and uses the centre fallback.
        var samples = new List<InkSample>();
        for (var i = 0; i < 100; i++)
        {
            var a = i * 0.04;
            samples.Add(new InkSample((float)(50 + (5 * Math.Cos(a))), (float)(50 + (5 * Math.Sin(a))), 1f));
        }

        var xy = samples.SelectMany(s => new[] { s.X, s.Y }).ToArray();
        var widths = Enumerable.Repeat(24f, samples.Count).ToArray();
        var polygon = StrokeOutline.Polygon(xy, widths);
        foreach (var s in samples)
        {
            for (var k = 0; k < 8; k++)
            {
                var a = k * Math.PI / 4;
                Assert.True(Geometry.Winding(polygon, s.X + (11.5 * Math.Cos(a)), s.Y + (11.5 * Math.Sin(a))) != 0);
            }
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    [InlineData(14f)]
    [InlineData(24f)]
    public void Ink03_AOnePointStrokeIsAFilledCircleOfTheBaseWidth(float width)
    {
        var polygon = StrokeOutline.Polygon([30f, 40f], [width]);
        var r = width / 2.0;
        Assert.All(polygon, v => Assert.Equal(r, Math.Sqrt(Geometry.Sq(v.X - 30) + Geometry.Sq(v.Y - 40)), 6));
        var area = Math.Abs(Geometry.PolygonArea(polygon));
        Assert.True(Math.Abs(area - (Math.PI * r * r)) / (Math.PI * r * r) < 0.01, $"area {area}");
        Assert.True(Geometry.Winding(polygon, 30, 40) != 0);

        var path = StrokeOutline.Fit([30f, 40f], [width]);
        Assert.Equal(6, path.Segments.Count);
        Assert.Equal(4, path.Segments.Count(s => s.Kind == PathSegmentKind.CubicTo));
        Assert.Equal(30 - r, path.Bounds.Left, 3);
        Assert.Equal(30 + r, path.Bounds.Right, 3);
        Assert.Equal(40 - r, path.Bounds.Bottom, 3);
        Assert.Equal(40 + r, path.Bounds.Top, 3);
    }

    [Fact]
    public void Ink03_RepeatedPointsMakeACircleOfTheLargestWidth()
    {
        var polygon = StrokeOutline.Polygon([5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f], [1f, 3f, 2f, 3f]);
        Assert.All(polygon, v => Assert.Equal(1.5, Math.Sqrt(Geometry.Sq(v.X - 5) + Geometry.Sq(v.Y - 5)), 6));
    }

    [Fact]
    public void Ink03_ADiscInsideItsNeighbourIsMergedAway()
    {
        // The second disc (width 1) lies inside the first (width 6); the outline equals that of the two end discs.
        var merged = StrokeOutline.Polygon([0f, 0f, 1f, 0f, 20f, 0f], [6f, 1f, 6f]);
        var direct = StrokeOutline.Polygon([0f, 0f, 20f, 0f], [6f, 6f]);
        Assert.Equal(direct.Count, merged.Count);
        Assert.Equal(Math.Abs(Geometry.PolygonArea(direct)), Math.Abs(Geometry.PolygonArea(merged)), 6);
    }

    [Fact]
    public void Ink03_ZeroWidthAndDuplicateInputNeverProducesNaN()
    {
        float[][] xys = [[1, 1], [1, 1, 1, 1], [0, 0, 5, 0, 5, 0, 5, 5], [0, 0, 5, 0]];
        float[][] widthSets = [[0], [0, 0], [0, 0, 0, 0], [2, 0], [0, 2], [2, 2]];
        foreach (var xy in xys)
        {
            foreach (var widths in widthSets.Where(w => w.Length * 2 == xy.Length))
            {
                var polygon = StrokeOutline.Polygon(xy, widths);
                Assert.NotEmpty(polygon);
                Assert.All(polygon, v => Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y)));
                var path = StrokeOutline.Fit(xy, widths);
                Assert.All(path.Segments, s => Assert.True(double.IsFinite(s.X1 + s.Y1 + s.X2 + s.Y2 + s.X3 + s.Y3)));
                Assert.DoesNotContain("NaN", path.ToContentStream(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Ink03_NegativeAndHugeWidthsAreClamped()
    {
        var polygon = StrokeOutline.Polygon([0f, 0f], [-5f]);
        Assert.NotEmpty(polygon);
        var huge = StrokeOutline.Polygon([0f, 0f], [1e9f]);
        Assert.All(huge, v => Assert.InRange(Math.Sqrt((v.X * v.X) + (v.Y * v.Y)), 499.9, 500.1));
    }

    [Fact]
    public void Ink03_BadInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([1f, 2f, 3f], [1f]));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([], []));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([float.NaN, 0f], [1f]));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([0f, float.PositiveInfinity], [1f]));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([0f, 0f], [float.NaN]));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Polygon([1e9f, 0f], [1f]));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Fit([0f, 0f], [1f], 0));
        Assert.Throws<ArgumentException>(() => StrokeOutline.Fit([0f, 0f], [1f], double.NaN));
    }

    [Fact]
    public void Ink03_AVariableWidthStrokeIsWiderWhereTheWidthIsLarger()
    {
        float[] xy = [0, 0, 50, 0, 100, 0];
        float[] widths = [1, 5, 9];
        var polygon = StrokeOutline.Polygon(xy, widths);
        Assert.True(Geometry.Winding(polygon, 100, 4.4) != 0);
        Assert.True(Geometry.Winding(polygon, 100, 4.6) == 0);
        Assert.True(Geometry.Winding(polygon, 0, 0.4) != 0);
        Assert.True(Geometry.Winding(polygon, 50, 2.6) == 0);
        Assert.True(Geometry.Winding(polygon, 50, 2.4) != 0);
    }
}
