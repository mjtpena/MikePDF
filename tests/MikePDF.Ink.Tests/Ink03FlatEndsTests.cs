using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-03: the highlighter's ends are flat (butt caps, ink-pen.md 3.8). The outline stops exactly at the first and last point; the pen keeps round caps.</summary>
public sealed class Ink03FlatEndsTests
{
    [Theory]
    [InlineData(6f)]
    [InlineData(14f)]
    [InlineData(40f)]
    public void Ink03_AFlatStrokeStopsExactlyAtItsFirstAndLastPointAndARoundOneRunsHalfAWidthPast(float width)
    {
        float[] xy = [10, 20, 60, 20, 110, 20];
        float[] widths = [width, width, width];

        var flat = StrokeOutline.Polygon(xy, widths, InkEnds.Flat);
        var round = StrokeOutline.Polygon(xy, widths);

        Assert.Equal(10, flat.Min(static p => p.X), 6);
        Assert.Equal(110, flat.Max(static p => p.X), 6);
        Assert.Equal(10 - (width / 2.0), round.Min(static p => p.X), 1);
        Assert.Equal(110 + (width / 2.0), round.Max(static p => p.X), 1);

        // A flat stroke is the rectangle the centreline sweeps: length times width.
        Assert.Equal(100.0 * width, Math.Abs(Geometry.PolygonArea(flat)), 4);
        Assert.All(flat, static p => Assert.True(p.X >= 10 - 1e-6 && p.X <= 110 + 1e-6));
    }

    [Fact]
    public void Ink03_AFlatEndIsAStraightEdgeAcrossTheEndPointPerpendicularToTheStroke()
    {
        // A diagonal stroke: both end edges pass through the end points and are square to the direction.
        float[] xy = [0, 0, 30, 40];
        float[] widths = [10, 10];
        var polygon = StrokeOutline.Polygon(xy, widths, InkEnds.Flat);

        Assert.Equal(4, polygon.Count);
        foreach (var (px, py) in new[] { (0.0, 0.0), (30.0, 40.0) })
        {
            var corners = polygon.Where(p => Math.Abs(((p.X - px) * 0.6) + ((p.Y - py) * 0.8)) < 1e-6).ToList();
            Assert.Equal(2, corners.Count);
            Assert.All(corners, p => Assert.Equal(5.0, Math.Sqrt(Geometry.Sq(p.X - px) + Geometry.Sq(p.Y - py)), 6));
        }
    }

    [Fact]
    public void Ink03_FlatEndsLeaveTheMiddleOfABendUnchanged()
    {
        // Same stroke, same bend: only the two end caps differ, so every interior vertex of the round outline is in the flat one.
        float[] xy = [0, 0, 40, 0, 40, 40];
        float[] widths = [12, 12, 12];
        var flat = StrokeOutline.Polygon(xy, widths, InkEnds.Flat);
        var round = StrokeOutline.Polygon(xy, widths);

        Assert.True(flat.Count < round.Count);
        var area = Math.Abs(Geometry.PolygonArea(flat));
        var expected = Geometry.SweptUnionArea([(0, 0), (40, 0), (40, 40)], 6, 0.02) - (Math.PI * 36.0);
        Assert.True(Math.Abs(area - expected) / expected < 0.03, $"{area:F2} vs {expected:F2}");

        // The first point sits on the edge, not a radius inside it: (0,6) and (0,-6) are corners and (-1,0) is outside.
        Assert.Contains(flat, static p => Math.Abs(p.X) < 1e-6 && Math.Abs(Math.Abs(p.Y) - 6) < 1e-6);
        Assert.Equal(0, Geometry.Winding(flat, -1, 0));
        Assert.NotEqual(0, Geometry.Winding(flat, 1, 0));
    }

    [Fact]
    public void Ink03_AFittedFlatStrokeKeepsItsCornersAndItsBoundsAreTheCentrelineAndHalfAWidth()
    {
        float[] xy = [10, 20, 110, 20];
        float[] widths = [14, 14];

        var flat = StrokeOutline.Fit(xy, widths, InkConstants.DefaultFitDeviation, InkEnds.Flat);
        var round = StrokeOutline.Fit(xy, widths);

        Assert.Equal(10, flat.Bounds.Left, 3);
        Assert.Equal(110, flat.Bounds.Right, 3);
        Assert.Equal(13, flat.Bounds.Bottom, 3);
        Assert.Equal(27, flat.Bounds.Top, 3);
        Assert.Equal(3, round.Bounds.Left, 1);
        Assert.Equal(117, round.Bounds.Right, 1);
        Assert.DoesNotContain(flat.Segments, static s => s.Kind == PathSegmentKind.CubicTo);
        Assert.Contains(round.Segments, static s => s.Kind == PathSegmentKind.CubicTo);
    }

    [Fact]
    public void Ink03_AppearanceFlatEndsFollowTheOptionAndAPenStaysRound()
    {
        SimplifiedStroke[] strokes = [new([0f, 0f, 80f, 0f], [14f, 14f])];

        var flat = InkAppearance.Build(strokes, InkEnds.Flat);
        var round = InkAppearance.Build(strokes);

        Assert.Equal(0, flat.Left, 3);
        Assert.Equal(80, flat.Right, 3);
        Assert.Equal(-7, round.Left, 1);
        Assert.Equal(87, round.Right, 1);
    }

    [Fact]
    public void Ink03_ADotWithFlatEndsIsStillACircleSoATapLeavesAMark()
    {
        var polygon = StrokeOutline.Polygon([30f, 40f], [14f], InkEnds.Flat);
        var area = Math.Abs(Geometry.PolygonArea(polygon));
        Assert.True(Math.Abs(area - (Math.PI * 49)) / (Math.PI * 49) < 0.01, $"area {area}");
    }
}
