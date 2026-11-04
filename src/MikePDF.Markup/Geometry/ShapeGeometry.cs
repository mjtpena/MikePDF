using MikePDF.Core.Geometry;

namespace MikePDF.Markup;

/// <summary>The geometry of one line ending: how much the line is shortened where the head sits, and what is drawn instead.</summary>
public sealed record LineEndGeometry(double Shorten, IReadOnlyList<ScenePath> Items, IReadOnlyList<PointD> Extent);

/// <summary>
/// The constants and formulas of shapes (shapes 3.2): arrow heads, end shapes, dash patterns. One place, used by the overlay and the appearance, and
/// pinned by golden tests (SH-01, SH-02).
/// </summary>
public static class ShapeGeometry
{
    /// <summary>Half the opening angle of an arrow head.</summary>
    public const double ArrowHalfAngleDegrees = 25;

    public const double MinWidth = 0.25;

    public const double MaxWidth = 36;

    public const int MaxVertices = 1000;

    public const int MinPolygonVertices = 3;

    public const int MinPolylineVertices = 2;

    /// <summary>Join miter limit of polygons and polylines.</summary>
    public const double MiterLimit = 10;

    /// <summary>Arrow head length: <c>clamp(6 x width, 8, 48)</c> points.</summary>
    public static double ArrowLength(double width) => Math.Clamp(6 * width, 8, 48);

    /// <summary>The side of a square, circle or diamond end: <c>clamp(4 x width, 6, 36)</c> points.</summary>
    public static double EndShapeSide(double width) => Math.Clamp(4 * width, 6, 36);

    /// <summary>
    /// The dash array for a style (the <c>d</c> operator and <c>/D</c>): dashed is a dash of 4 x width and a gap of 2 x width, each at least 2 points;
    /// dotted is a dot of 1 x width and a gap of 2 x width, each at least 1 point, drawn with round caps. Null for solid.
    /// </summary>
    public static double[]? DashArray(DashStyle style, double width) => style switch
    {
        DashStyle.Dashed => [Math.Max(4 * width, 2), Math.Max(2 * width, 2)],
        DashStyle.Dotted => [Math.Max(width, 1), Math.Max(2 * width, 1)],
        _ => null,
    };

    public static CapStyle CapFor(DashStyle style) => style == DashStyle.Dotted ? CapStyle.Round : CapStyle.Butt;

    /// <summary>
    /// A line ending at <paramref name="tip"/>, with <paramref name="outward"/> the unit vector pointing from the line toward the tip. Returns how much to
    /// shorten the line and the paths of the head. The head lies beyond the shortened line and reaches the tip (arrows) or is centred on it (shapes).
    /// </summary>
    public static LineEndGeometry End(PointD tip, PointD outward, LineEnding ending, double width, Rgb stroke, Rgb? interior)
    {
        var u = Geo.Unit(outward);
        var items = new List<ScenePath>();
        var extent = new List<PointD> { tip };
        double shorten;
        switch (ending)
        {
            case LineEnding.OpenArrow:
            {
                var (a, b) = ArrowBase(tip, u, width);
                items.Add(new ScenePath(new PathBuilder().Move(a).Line(tip).Line(b).Segments, PaintMode.Stroke, null, stroke, width, CapStyle.Butt, JoinStyle.Round));
                extent.Add(a);
                extent.Add(b);
                shorten = 0;
                break;
            }

            case LineEnding.ClosedArrow:
            {
                var (a, b) = ArrowBase(tip, u, width);
                var length = ArrowLength(width) * Math.Cos(ArrowHalfAngleDegrees * Math.PI / 180);
                items.Add(new ScenePath(new PathBuilder().Move(tip).Line(a).Line(b).Close().Segments, PaintMode.Fill, interior ?? stroke, null, 0));
                extent.Add(a);
                extent.Add(b);
                shorten = length - Math.Min(0.5, width / 4);
                break;
            }

            case LineEnding.Square or LineEnding.Circle or LineEnding.Diamond:
            {
                var side = EndShapeSide(width);
                var half = side / 2;
                var paint = interior is null ? PaintMode.Stroke : PaintMode.FillAndStroke;
                PathBuilder path;
                if (ending == LineEnding.Circle)
                {
                    path = new PathBuilder().Ellipse(tip.X - half, tip.Y - half, tip.X + half, tip.Y + half);
                    extent.Add(new PointD(tip.X - half, tip.Y - half));
                    extent.Add(new PointD(tip.X + half, tip.Y + half));
                    shorten = half;
                }
                else
                {
                    var v = Geo.Perp(u);
                    PointD[] corners;
                    if (ending == LineEnding.Square)
                    {
                        corners = [Corner(tip, u, v, -half, -half), Corner(tip, u, v, half, -half), Corner(tip, u, v, half, half), Corner(tip, u, v, -half, half)];
                        shorten = half;
                    }
                    else
                    {
                        var d = side / Math.Sqrt(2);
                        corners = [Corner(tip, u, v, -d, 0), Corner(tip, u, v, 0, -d), Corner(tip, u, v, d, 0), Corner(tip, u, v, 0, d)];
                        shorten = d;
                    }

                    path = new PathBuilder().Polygon(corners);
                    extent.AddRange(corners);
                }

                items.Add(new ScenePath(path.Segments, paint, interior, stroke, width, CapStyle.Butt, JoinStyle.Miter, MiterLimit));
                break;
            }

            default:
                shorten = 0;
                break;
        }

        return new LineEndGeometry(shorten, items, extent);
    }

    /// <summary>The two back corners of an arrow head at a tip.</summary>
    public static (PointD A, PointD B) ArrowBase(PointD tip, PointD outward, double width)
    {
        var length = ArrowLength(width);
        var back = Geo.Scale(Geo.Unit(outward), -length);
        var angle = ArrowHalfAngleDegrees * Math.PI / 180;
        return (Geo.Add(tip, Geo.Rotate(back, angle)), Geo.Add(tip, Geo.Rotate(back, -angle)));
    }

    private static PointD Corner(PointD centre, PointD u, PointD v, double along, double across) =>
        new(centre.X + (u.X * along) + (v.X * across), centre.Y + (u.Y * along) + (v.Y * across));

    /// <summary>
    /// The drawn polyline of a line or polyline with its ends shortened, plus the end heads. Each end's heading is the direction of the first or last
    /// segment that has length.
    /// </summary>
    public static (IReadOnlyList<PointD> Line, IReadOnlyList<ScenePath> Heads, IReadOnlyList<PointD> Extent) WithEnds(
        IReadOnlyList<PointD> vertices, LineEnding start, LineEnding end, double width, Rgb stroke, Rgb? interior)
    {
        var points = vertices.ToList();
        var heads = new List<ScenePath>();
        var extent = new List<PointD>(points);
        if (points.Count >= 2)
        {
            var last = points[^1];
            var before = points[^2];
            var endHead = End(last, Geo.Sub(last, before), end, width, stroke, interior);
            var first = points[0];
            var after = points[1];
            var startHead = End(first, Geo.Sub(first, after), start, width, stroke, interior);
            heads.AddRange(startHead.Items);
            heads.AddRange(endHead.Items);
            extent.AddRange(startHead.Extent);
            extent.AddRange(endHead.Extent);
            points[^1] = Pull(last, before, endHead.Shorten);
            points[0] = Pull(first, after, startHead.Shorten);
        }

        return (points, heads, extent);
    }

    // Moves a line's end toward its neighbour by an amount, never past it.
    private static PointD Pull(PointD end, PointD neighbour, double amount)
    {
        if (amount <= 0)
        {
            return end;
        }

        var span = Geo.Distance(end, neighbour);
        return span < Geo.Epsilon ? end : Geo.Lerp(end, neighbour, Math.Min(amount, span) / span);
    }
}
