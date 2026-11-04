namespace MikePDF.Ink.Tests.Support;

/// <summary>Plain geometry used by the tests as an independent reference for the library.</summary>
internal static class Geometry
{
    public static double Sq(double v) => v * v;

    /// <summary>Winding number of a closed polygon around a point (non-zero means inside under the non-zero rule).</summary>
    public static int Winding(IReadOnlyList<(double X, double Y)> polygon, double px, double py)
    {
        var winding = 0;
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            if (a.Y <= py)
            {
                if (b.Y > py && ((b.X - a.X) * (py - a.Y)) - ((px - a.X) * (b.Y - a.Y)) > 0)
                {
                    winding++;
                }
            }
            else if (b.Y <= py && ((b.X - a.X) * (py - a.Y)) - ((px - a.X) * (b.Y - a.Y)) < 0)
            {
                winding--;
            }
        }

        return winding;
    }

    public static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 > 0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
        return Math.Sqrt(Sq(ax + (t * dx) - px) + Sq(ay + (t * dy) - py));
    }

    /// <summary>Distance from a point to a polyline (open) or polygon boundary (closed).</summary>
    public static double DistToPolyline(IReadOnlyList<(double X, double Y)> pts, double px, double py, bool closed)
    {
        var best = double.MaxValue;
        var n = pts.Count;
        if (n == 1)
        {
            return Math.Sqrt(Sq(px - pts[0].X) + Sq(py - pts[0].Y));
        }

        var segs = closed ? n : n - 1;
        for (var i = 0; i < segs; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            best = Math.Min(best, DistToSegment(px, py, a.X, a.Y, b.X, b.Y));
        }

        return best;
    }

    /// <summary>Flattens every sub-path of a path into closed polylines, <paramref name="steps"/> points per cubic.</summary>
    public static List<List<(double X, double Y)>> Flatten(InkPath path, int steps)
    {
        var result = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? current = null;
        var cx = 0.0;
        var cy = 0.0;
        foreach (var s in path.Segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo:
                    current = [(s.X1, s.Y1)];
                    result.Add(current);
                    cx = s.X1;
                    cy = s.Y1;
                    break;
                case PathSegmentKind.LineTo:
                    current!.Add((s.X1, s.Y1));
                    cx = s.X1;
                    cy = s.Y1;
                    break;
                case PathSegmentKind.CubicTo:
                    for (var k = 1; k <= steps; k++)
                    {
                        var t = (double)k / steps;
                        var u = 1 - t;
                        current!.Add((
                            (u * u * u * cx) + (3 * u * u * t * s.X1) + (3 * u * t * t * s.X2) + (t * t * t * s.X3),
                            (u * u * u * cy) + (3 * u * u * t * s.Y1) + (3 * u * t * t * s.Y2) + (t * t * t * s.Y3)));
                    }

                    cx = s.X3;
                    cy = s.Y3;
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>Area of the region with non-zero winding, by horizontal scanlines of height <paramref name="dy"/>.</summary>
    public static double NonZeroArea(IReadOnlyList<(double X, double Y)> polygon, double dy)
    {
        var minY = polygon.Min(p => p.Y);
        var maxY = polygon.Max(p => p.Y);
        double area = 0;
        var crossings = new List<(double X, int Dir)>();
        for (var y = minY + (dy / 2); y < maxY; y += dy)
        {
            crossings.Clear();
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if ((a.Y <= y) == (b.Y <= y))
                {
                    continue;
                }

                var x = a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                crossings.Add((x, b.Y > a.Y ? 1 : -1));
            }

            crossings.Sort((l, r) => l.X.CompareTo(r.X));
            var winding = 0;
            for (var i = 0; i < crossings.Count - 1; i++)
            {
                winding += crossings[i].Dir;
                if (winding != 0)
                {
                    area += (crossings[i + 1].X - crossings[i].X) * dy;
                }
            }
        }

        return area;
    }

    /// <summary>Area of the union of discs and rectangles swept along a constant-radius polyline (exact reference for constant width), by scanlines.</summary>
    public static double SweptUnionArea(IReadOnlyList<(double X, double Y)> points, double radius, double dy)
    {
        var minY = points.Min(p => p.Y) - radius;
        var maxY = points.Max(p => p.Y) + radius;
        double area = 0;
        var intervals = new List<(double L, double R)>();
        for (var y = minY + (dy / 2); y < maxY; y += dy)
        {
            intervals.Clear();
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var h = radius * radius - Sq(y - p.Y);
                if (h > 0)
                {
                    var s = Math.Sqrt(h);
                    intervals.Add((p.X - s, p.X + s));
                }

                if (i == points.Count - 1)
                {
                    continue;
                }

                var q = points[i + 1];
                var dx = q.X - p.X;
                var dyy = q.Y - p.Y;
                var len = Math.Sqrt((dx * dx) + (dyy * dyy));
                if (len < 1e-12)
                {
                    continue;
                }

                // Rectangle: corners p +- n r and q +- n r. Clip the scanline against the four edges (convex polygon).
                var nx = -dyy / len * radius;
                var ny = dx / len * radius;
                var rect = new (double X, double Y)[] { (p.X + nx, p.Y + ny), (q.X + nx, q.Y + ny), (q.X - nx, q.Y - ny), (p.X - nx, p.Y - ny) };
                var lo = double.PositiveInfinity;
                var hi = double.NegativeInfinity;
                for (var k = 0; k < 4; k++)
                {
                    var a = rect[k];
                    var b = rect[(k + 1) % 4];
                    if ((a.Y <= y) != (b.Y <= y))
                    {
                        var x = a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                        lo = Math.Min(lo, x);
                        hi = Math.Max(hi, x);
                    }
                }

                if (lo < hi)
                {
                    intervals.Add((lo, hi));
                }
            }

            intervals.Sort((l, r) => l.L.CompareTo(r.L));
            var end = double.NegativeInfinity;
            foreach (var (l, r) in intervals)
            {
                if (r <= end)
                {
                    continue;
                }

                area += (r - Math.Max(l, end)) * dy;
                end = r;
            }
        }

        return area;
    }

    public static double PolygonArea(IReadOnlyList<(double X, double Y)> polygon)
    {
        double a = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            a += (p.X * q.Y) - (q.X * p.Y);
        }

        return a / 2.0;
    }
}
