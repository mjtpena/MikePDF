using MikePDF.Core.Geometry;
using MikePDF.Ink;

namespace MikePDF.Markup;

/// <summary>Small vector helpers on <see cref="PointD"/>, so the geometry reads as geometry.</summary>
public static class Geo
{
    public const double Epsilon = 1e-9;

    public static PointD Add(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    public static PointD Sub(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    public static PointD Scale(PointD a, double k) => new(a.X * k, a.Y * k);

    public static PointD Mid(PointD a, PointD b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    public static PointD Lerp(PointD a, PointD b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    public static double Dot(PointD a, PointD b) => (a.X * b.X) + (a.Y * b.Y);

    public static double Cross(PointD a, PointD b) => (a.X * b.Y) - (a.Y * b.X);

    public static double Length(PointD a) => Math.Sqrt((a.X * a.X) + (a.Y * a.Y));

    public static double Distance(PointD a, PointD b) => Length(Sub(a, b));

    /// <summary>The unit vector, or (1, 0) for a vector shorter than <see cref="Epsilon"/>.</summary>
    public static PointD Unit(PointD a)
    {
        var length = Length(a);
        return length < Epsilon ? new PointD(1, 0) : new PointD(a.X / length, a.Y / length);
    }

    /// <summary>The vector turned a quarter turn counter-clockwise (in a y-up space).</summary>
    public static PointD Perp(PointD a) => new(-a.Y, a.X);

    public static PointD Rotate(PointD p, double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new PointD((p.X * cos) - (p.Y * sin), (p.X * sin) + (p.Y * cos));
    }

    /// <summary>Distance from a point to a segment.</summary>
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var ab = Sub(b, a);
        var lengthSquared = Dot(ab, ab);
        if (lengthSquared < Epsilon)
        {
            return Distance(p, a);
        }

        var t = Math.Clamp(Dot(Sub(p, a), ab) / lengthSquared, 0, 1);
        return Distance(p, new PointD(a.X + (ab.X * t), a.Y + (ab.Y * t)));
    }

    /// <summary>Non-zero winding number test of a point against a polygon (shapes 3.4: self-intersecting polygons are filled with the non-zero rule).</summary>
    public static bool InsidePolygonNonZero(PointD p, IReadOnlyList<PointD> polygon)
    {
        var winding = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (a.Y <= p.Y)
            {
                if (b.Y > p.Y && Cross(Sub(b, a), Sub(p, a)) > 0)
                {
                    winding++;
                }
            }
            else if (b.Y <= p.Y && Cross(Sub(b, a), Sub(p, a)) < 0)
            {
                winding--;
            }
        }

        return winding != 0;
    }

    public static PdfRect Union(PdfRect a, PdfRect b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));

    public static PdfRect Inflate(PdfRect r, double amount) => new(r.Left - amount, r.Bottom - amount, r.Right + amount, r.Top + amount);

    public static PdfRect BoundsOf(IEnumerable<PointD> points)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        var any = false;
        foreach (var p in points)
        {
            any = true;
            l = Math.Min(l, p.X);
            r = Math.Max(r, p.X);
            b = Math.Min(b, p.Y);
            t = Math.Max(t, p.Y);
        }

        return any ? new PdfRect(l, b, r, t) : new PdfRect(0, 0, 0, 0);
    }

    public static bool Contains(PdfRect r, PointD p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    /// <summary>Whether two normalised rectangles overlap or touch.</summary>
    public static bool Intersects(PdfRect a, PdfRect b) => a.Left <= b.Right && b.Left <= a.Right && a.Bottom <= b.Top && b.Bottom <= a.Top;

    public static PointD Center(PdfRect r) => new((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);

    public static PdfRect Normalised(PdfRect r) => PdfRect.FromCorners(r.Left, r.Bottom, r.Right, r.Top);

    public static bool IsFinite(PdfRect r) => double.IsFinite(r.Left) && double.IsFinite(r.Bottom) && double.IsFinite(r.Right) && double.IsFinite(r.Top);

    public static PdfRect Translate(PdfRect r, double dx, double dy) => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);

    public static bool NearlyEqual(PdfRect a, PdfRect b, double tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance && Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance;
}

/// <summary>Builds a list of path segments in the order a drawing program would (the same segments the overlay draws and the appearance writes).</summary>
public sealed class PathBuilder
{
    // Control point distance for a quarter circle: 4/3 * (sqrt(2) - 1).
    private const double Kappa = 0.5522847498307936;

    private readonly List<PathSegment> _segments = [];

    public IReadOnlyList<PathSegment> Segments => _segments;

    public PathBuilder Move(double x, double y)
    {
        _segments.Add(new PathSegment(PathSegmentKind.MoveTo, x, y, 0, 0, 0, 0));
        return this;
    }

    public PathBuilder Move(PointD p) => Move(p.X, p.Y);

    public PathBuilder Line(double x, double y)
    {
        _segments.Add(new PathSegment(PathSegmentKind.LineTo, x, y, 0, 0, 0, 0));
        return this;
    }

    public PathBuilder Line(PointD p) => Line(p.X, p.Y);

    public PathBuilder Curve(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _segments.Add(new PathSegment(PathSegmentKind.CubicTo, x1, y1, x2, y2, x3, y3));
        return this;
    }

    public PathBuilder Close()
    {
        _segments.Add(new PathSegment(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0));
        return this;
    }

    public PathBuilder Rectangle(double left, double bottom, double right, double top) =>
        Move(left, bottom).Line(right, bottom).Line(right, top).Line(left, top).Close();

    /// <summary>An ellipse inscribed in a box, as four cubic curves starting at the right-most point and going counter-clockwise.</summary>
    public PathBuilder Ellipse(double left, double bottom, double right, double top)
    {
        double cx = (left + right) / 2, cy = (bottom + top) / 2, rx = (right - left) / 2, ry = (top - bottom) / 2;
        double kx = rx * Kappa, ky = ry * Kappa;
        return Move(cx + rx, cy)
            .Curve(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .Curve(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .Curve(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .Curve(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .Close();
    }

    public PathBuilder Polygon(IEnumerable<PointD> points)
    {
        var first = true;
        foreach (var p in points)
        {
            if (first)
            {
                Move(p);
                first = false;
            }
            else
            {
                Line(p);
            }
        }

        return Close();
    }

    public PathBuilder Polyline(IEnumerable<PointD> points)
    {
        var first = true;
        foreach (var p in points)
        {
            if (first)
            {
                Move(p);
                first = false;
            }
            else
            {
                Line(p);
            }
        }

        return this;
    }

    /// <summary>A rounded rectangle with a corner radius (clamped to half the shorter side).</summary>
    public PathBuilder RoundedRectangle(double left, double bottom, double right, double top, double radius)
    {
        var r = Math.Max(0, Math.Min(radius, Math.Min(right - left, top - bottom) / 2));
        var k = r * Kappa;
        return Move(left + r, bottom)
            .Line(right - r, bottom).Curve(right - r + k, bottom, right, bottom + r - k, right, bottom + r)
            .Line(right, top - r).Curve(right, top - r + k, right - r + k, top, right - r, top)
            .Line(left + r, top).Curve(left + r - k, top, left, top - r + k, left, top - r)
            .Line(left, bottom + r).Curve(left, bottom + r - k, left + r - k, bottom, left + r, bottom)
            .Close();
    }

    /// <summary>The points the path visits (curve end points and control points included), for rough bounds and sampling.</summary>
    public static IEnumerable<PointD> Points(IEnumerable<PathSegment> segments)
    {
        foreach (var s in segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo or PathSegmentKind.LineTo:
                    yield return new PointD(s.X1, s.Y1);
                    break;
                case PathSegmentKind.CubicTo:
                    yield return new PointD(s.X1, s.Y1);
                    yield return new PointD(s.X2, s.Y2);
                    yield return new PointD(s.X3, s.Y3);
                    break;
            }
        }
    }
}
