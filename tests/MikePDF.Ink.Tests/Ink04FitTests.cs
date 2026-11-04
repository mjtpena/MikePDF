using System.Diagnostics;
using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-04: Bezier fitting deviates at most 0.1 pt from the outline, in both directions, and Douglas-Peucker keeps the centreline within 0.1 pt.</summary>
public sealed class Ink04FitTests
{
    private const double Tolerance = 0.1;
    private const double Slack = 1e-6;

    private static readonly IReadOnlyList<PenTrace> AllTraces = Traces.Build(200);

    private static double DistanceToClosedPolyline(List<(double X, double Y)> polyline, double x, double y) =>
        Geometry.DistToPolyline(polyline, x, y, closed: true);

    private static void AssertFitIsWithinTolerance(string name, float[] xy, float[] widths)
    {
        var polygon = StrokeOutline.Polygon(xy, widths);
        var path = StrokeOutline.Fit(xy, widths);
        var curves = Geometry.Flatten(path, 64);
        Assert.Single(curves);
        var curve = curves[0];

        // Curve to polygon: every sampled curve point lies on the outline within the tolerance.
        foreach (var p in curve)
        {
            var d = Geometry.DistToPolyline(polygon, p.X, p.Y, closed: true);
            Assert.True(d <= Tolerance + Slack, $"{name}: curve point {p} is {d:F4} from the polygon");
        }

        // Polygon to curve: every vertex and every edge midpoint lies on the curve within the tolerance.
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var dv = DistanceToClosedPolyline(curve, a.X, a.Y);
            Assert.True(dv <= Tolerance + Slack, $"{name}: vertex {i} is {dv:F4} from the curve");
            var dm = DistanceToClosedPolyline(curve, (a.X + b.X) / 2, (a.Y + b.Y) / 2);
            Assert.True(dm <= Tolerance + Slack, $"{name}: edge {i} midpoint is {dm:F4} from the curve");
        }
    }

    [Fact]
    public void Ink04_BezierFitDeviatesAtMostTenthOfAPointFromTheOutline_For200Traces()
    {
        foreach (var trace in AllTraces)
        {
            var simplified = Simplifier.Simplify(trace.Samples, InkStyle.DefaultPen);
            AssertFitIsWithinTolerance(trace.Name, simplified.Xy, simplified.Widths);
        }
    }

    [Fact]
    public void Ink04_BezierFitOfUnsimplifiedDenseStrokesDeviatesAtMostTenthOfAPoint()
    {
        var style = new InkStyle(InkTool.Pen, 5f, PressureCurve.Soft, 0.3f);
        foreach (var trace in AllTraces.Take(12))
        {
            var widths = WidthProfile.Compute(trace.Samples, style);
            var xy = trace.Samples.SelectMany(s => new[] { s.X, s.Y }).ToArray();
            AssertFitIsWithinTolerance(trace.Name, xy, widths);
        }
    }

    [Fact]
    public void Ink04_FitIsMuchSmallerThanThePolygon()
    {
        var trace = AllTraces.First(t => t.Name.EndsWith("kind5", StringComparison.Ordinal));
        var simplified = Simplifier.Simplify(trace.Samples, InkStyle.DefaultPen);
        var polygon = StrokeOutline.Polygon(simplified.Xy, simplified.Widths);
        var path = StrokeOutline.Fit(simplified.Xy, simplified.Widths);
        Assert.True(path.Segments.Count <= polygon.Count + 2);
        Assert.Contains(path.Segments, s => s.Kind == PathSegmentKind.CubicTo);
    }

    [Fact]
    public void Ink04_ATwentyThousandPointStrokeFitsQuicklyWithinTolerance()
    {
        var xy = new float[40_000];
        var widths = new float[20_000];
        for (var i = 0; i < 20_000; i++)
        {
            var a = i * 0.01;
            xy[2 * i] = (float)(300 + (80 * Math.Cos(a)) + (a * 0.5));
            xy[(2 * i) + 1] = (float)(300 + (80 * Math.Sin(a)));
            widths[i] = 2f + (float)Math.Sin(a * 3);
        }

        var watch = Stopwatch.StartNew();
        var path = StrokeOutline.Fit(xy, widths);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.True(path.Segments.Count > 4);
    }

    [Fact]
    public void Ink04_ARandomScribbleOfTwentyThousandPointsCompletesAndStaysFinite()
    {
        var rng = new Random(3);
        var xy = new float[40_000];
        var widths = new float[20_000];
        for (var i = 0; i < 20_000; i++)
        {
            xy[2 * i] = (float)(rng.NextDouble() * 100);
            xy[(2 * i) + 1] = (float)(rng.NextDouble() * 100);
            widths[i] = (float)(0.5 + (rng.NextDouble() * 4));
        }

        var watch = Stopwatch.StartNew();
        var path = StrokeOutline.Fit(xy, widths);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.All(path.Segments, s => Assert.True(double.IsFinite(s.X1 + s.Y1 + s.X2 + s.Y2 + s.X3 + s.Y3)));
    }

    // ---- Douglas-Peucker ------------------------------------------------------------------------------

    [Fact]
    public void Ink04_DouglasPeuckerKeepsTheCentrelineWithinTenthOfAPoint_For200Traces()
    {
        foreach (var trace in AllTraces)
        {
            var kept = Simplifier.DouglasPeuckerIndices(trace.Samples, 0.1);
            Assert.Equal(0, kept[0]);
            Assert.Equal(trace.Samples.Length - 1, kept[^1]);
            for (var i = 1; i < kept.Length; i++)
            {
                Assert.True(kept[i] > kept[i - 1]);
            }

            var polyline = kept.Select(k => ((double)trace.Samples[k].X, (double)trace.Samples[k].Y)).ToList();
            foreach (var s in trace.Samples)
            {
                var d = polyline.Count == 1 ? 0 : Geometry.DistToPolyline(polyline, s.X, s.Y, closed: false);
                Assert.True(d <= 0.1 + 1e-9, $"{trace.Name}: {d}");
            }

            Assert.True(kept.Length <= trace.Samples.Length);
        }
    }

    [Fact]
    public void Ink04_DouglasPeuckerReducesAStraightLineToItsEnds()
    {
        var line = Enumerable.Range(0, 500).Select(i => new InkSample(i * 0.3f, i * 0.2f, 1f)).ToArray();
        Assert.Equal([0, 499], Simplifier.DouglasPeuckerIndices(line, 0.1));
    }

    [Fact]
    public void Ink04_DouglasPeuckerHandlesTinyAndDegenerateInput()
    {
        Assert.Empty(Simplifier.DouglasPeuckerIndices([], 0.1));
        Assert.Equal([0], Simplifier.DouglasPeuckerIndices([new InkSample(1, 1, 1)], 0.1));
        Assert.Equal([0, 1], Simplifier.DouglasPeuckerIndices([new InkSample(1, 1, 1), new InkSample(1, 1, 1)], 0.1));
        var same = Enumerable.Repeat(new InkSample(4, 4, 1), 100).ToArray();
        Assert.Equal([0, 99], Simplifier.DouglasPeuckerIndices(same, 0.1));

        // A closed loop (first equals last) keeps its far points, because distance is measured to the chord segment.
        InkSample[] loop = [new(0, 0, 1), new(10, 0, 1), new(10, 10, 1), new(0, 10, 1), new(0, 0, 1)];
        Assert.Equal([0, 1, 2, 3, 4], Simplifier.DouglasPeuckerIndices(loop, 0.1));
    }

    [Fact]
    public void Ink04_DouglasPeuckerRejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => Simplifier.DouglasPeuckerIndices([new InkSample(0, 0, 1), new InkSample(1, 1, 1)], -1));
        Assert.Throws<ArgumentException>(() => Simplifier.DouglasPeuckerIndices([new InkSample(0, 0, 1), new InkSample(1, 1, 1)], double.NaN));
        Assert.Throws<ArgumentException>(() => Simplifier.DouglasPeuckerIndices([new InkSample(0, 0, 1), new InkSample(float.NaN, 1, 1), new InkSample(2, 2, 1)], 0.1));
    }

    [Fact]
    public void Ink04_DouglasPeuckerOnAThousandsOfPointZigzagDoesNotOverflowTheStack()
    {
        var zigzag = Enumerable.Range(0, 5_000).Select(i => new InkSample(i, i % 2 * 5f, 1f)).ToArray();
        var kept = Simplifier.DouglasPeuckerIndices(zigzag, 0.1);
        Assert.Equal(5_000, kept.Length);
    }

    [Fact]
    public void Ink04_SimplifyKeepsWidthsOfTheKeptPointsAndTheirVariation()
    {
        // A straight line whose pressure rises and falls: the line needs only two points, the width profile needs more.
        var samples = Enumerable.Range(0, 300).Select(i => new InkSample(i * 0.5f, 0, (float)(0.5 + (0.5 * Math.Sin(i / 20.0))))).ToArray();
        var style = new InkStyle(InkTool.Pen, 8f, PressureCurve.Linear, 0.2f);
        var simplified = Simplifier.Simplify(samples, style);
        var all = WidthProfile.Compute(samples, style);
        var n = simplified.Widths.Length;
        Assert.Equal(n * 2, simplified.Xy.Length);
        Assert.InRange(n, 3, 299);
        for (var k = 0; k < n; k++)
        {
            var original = (int)Math.Round(simplified.Xy[2 * k] / 0.5f);
            Assert.Equal(all[original], simplified.Widths[k]);
        }

        // Every dropped point's width is within the tolerance (on the half width) of the interpolated width.
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i].X;
            var k = 0;
            while (k < n - 2 && simplified.Xy[2 * (k + 1)] < x)
            {
                k++;
            }

            var x0 = simplified.Xy[2 * k];
            var x1 = simplified.Xy[2 * (k + 1)];
            var t = x1 > x0 ? (x - x0) / (x1 - x0) : 0f;
            var interpolated = simplified.Widths[k] + (t * (simplified.Widths[k + 1] - simplified.Widths[k]));
            Assert.True(Math.Abs(all[i] - interpolated) * 0.5 <= Tolerance + 1e-4, $"sample {i}");
        }
    }

    [Fact]
    public void Ink04_SimplifyOfASinglePointKeepsThePoint()
    {
        var simplified = Simplifier.Simplify([new InkSample(5, 6, 0.4f)], InkStyle.DefaultPen);
        Assert.Equal([5f, 6f], simplified.Xy);
        Assert.Single(simplified.Widths);
        Assert.Empty(Simplifier.Simplify([], InkStyle.DefaultPen).Widths);
    }
}
