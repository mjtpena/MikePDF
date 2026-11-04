namespace MikePDF.Ink;

/// <summary>The result of building an outline: the polygon, and when the stroke is a single disc, that disc.</summary>
internal sealed class OutlineGeometry
{
    public required List<(double X, double Y)> Polygon { get; init; }

    public bool IsDisc { get; init; }

    public double DiscX { get; init; }

    public double DiscY { get; init; }

    public double DiscRadius { get; init; }
}

/// <summary>
/// Builds the closed polygon around a variable-width centreline. Each segment between two discs is the pair of outer tangent
/// lines of the discs (so a changing width is exact), outer joins and both caps are round arcs, and inner joins use the miter point
/// when it lies on both offset segments and the centre point otherwise, which keeps the non-zero fill free of artefacts.
/// </summary>
internal static class OutlineBuilder
{
    private const double Epsilon = 1e-9;
    private const double SmallJoinAngle = 2.0 * Math.PI / 180.0;
    private const int MaxPoints = 200_000;

    public static OutlineGeometry Build(ReadOnlySpan<float> xy, ReadOnlySpan<float> widths)
    {
        if (xy.Length != widths.Length * 2)
        {
            throw new ArgumentException("xy must hold two numbers for every width.", nameof(xy));
        }

        if (widths.Length < 1)
        {
            throw new ArgumentException("A stroke needs at least one point.", nameof(widths));
        }

        if (widths.Length > MaxPoints)
        {
            throw new ArgumentException($"A stroke holds at most {MaxPoints} points.", nameof(widths));
        }

        var qx = new double[widths.Length];
        var qy = new double[widths.Length];
        var qr = new double[widths.Length];
        var m = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            var x = xy[2 * i];
            var y = xy[(2 * i) + 1];
            var w = widths[i];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(w) || Math.Abs(x) > StrokeLimits.MaxCoordinate || Math.Abs(y) > StrokeLimits.MaxCoordinate)
            {
                throw new ArgumentException($"Point {i} is not finite or is out of range.", nameof(xy));
            }

            var r = Math.Clamp(w, 0.0, InkConstants.MaxOutlineWidth) * 0.5;

            // Merge a disc into its neighbour when one contains the other: the union of the segment hulls does not shrink.
            while (true)
            {
                if (m == 0)
                {
                    qx[0] = x;
                    qy[0] = y;
                    qr[0] = r;
                    m = 1;
                    break;
                }

                var d = Math.Sqrt(Sq(x - qx[m - 1]) + Sq(y - qy[m - 1]));
                if (d <= Math.Abs(r - qr[m - 1]) + Epsilon)
                {
                    if (r > qr[m - 1])
                    {
                        m--;
                        continue;
                    }

                    break;
                }

                qx[m] = x;
                qy[m] = y;
                qr[m] = r;
                m++;
                break;
            }
        }

        if (m == 1)
        {
            return new OutlineGeometry
            {
                Polygon = Circle(qx[0], qy[0], qr[0]),
                IsDisc = true,
                DiscX = qx[0],
                DiscY = qy[0],
                DiscRadius = qr[0],
            };
        }

        // Tangent directions of every segment, left (+1) and right (-1).
        var segCount = m - 1;
        var ulx = new double[segCount];
        var uly = new double[segCount];
        var urx = new double[segCount];
        var ury = new double[segCount];
        var betaL = new double[segCount];
        for (var k = 0; k < segCount; k++)
        {
            var dx = qx[k + 1] - qx[k];
            var dy = qy[k + 1] - qy[k];
            var d = Math.Sqrt((dx * dx) + (dy * dy));
            var tx = dx / d;
            var ty = dy / d;
            var s = Math.Clamp((qr[k] - qr[k + 1]) / d, -1.0, 1.0);
            var c = Math.Sqrt(Math.Max(0.0, 1.0 - (s * s)));
            ulx[k] = (-ty * c) + (tx * s);
            uly[k] = (tx * c) + (ty * s);
            urx[k] = (ty * c) + (tx * s);
            ury[k] = (-tx * c) + (ty * s);
            betaL[k] = Math.Acos(s);
        }

        var left = BuildSide(qx, qy, qr, m, ulx, uly, +1);
        var right = BuildSide(qx, qy, qr, m, urx, ury, -1);

        var polygon = new List<(double X, double Y)>(left.Count + right.Count + 64);
        polygon.AddRange(left);

        // End cap: clockwise from the left tangent direction to the right one, through the forward direction.
        AddArc(polygon, qx[m - 1], qy[m - 1], qr[m - 1], ulx[segCount - 1], uly[segCount - 1], -2.0 * betaL[segCount - 1]);
        for (var i = right.Count - 1; i >= 0; i--)
        {
            polygon.Add(right[i]);
        }

        // Start cap: clockwise from the right tangent direction to the left one, through the backward direction.
        AddArc(polygon, qx[0], qy[0], qr[0], urx[0], ury[0], -((2.0 * Math.PI) - (2.0 * betaL[0])));
        return new OutlineGeometry { Polygon = RemoveDuplicates(polygon) };
    }

    private static List<(double X, double Y)> BuildSide(double[] qx, double[] qy, double[] qr, int m, double[] ux, double[] uy, int sign)
    {
        var side = new List<(double X, double Y)>(m * 2)
        {
            (qx[0] + (qr[0] * ux[0]), qy[0] + (qr[0] * uy[0])),
        };

        for (var k = 1; k <= m - 2; k++)
        {
            var px = ux[k - 1];
            var py = uy[k - 1];
            var nx = ux[k];
            var ny = uy[k];
            var cross = (px * ny) - (py * nx);
            var dot = (px * nx) + (py * ny);
            var theta = Math.Atan2(cross, dot);
            var r = qr[k];
            var bx = qx[k] + (r * px);
            var by = qy[k] + (r * py);
            var ax = qx[k] + (r * nx);
            var ay = qy[k] + (r * ny);

            if (Math.Abs(theta) <= SmallJoinAngle)
            {
                // Nearly straight: one point on the bisector (the radial error is below 4e-5 of the radius).
                var sx = px + nx;
                var sy = py + ny;
                var sl = Math.Sqrt((sx * sx) + (sy * sy));
                side.Add(sl > Epsilon ? (qx[k] + (r * sx / sl), qy[k] + (r * sy / sl)) : (bx, by));
                continue;
            }

            if (sign * theta < 0)
            {
                // Outer side of the turn: a round join.
                side.Add((bx, by));
                var step = ArcStep(r);
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / step));
                for (var j = 1; j < steps; j++)
                {
                    var a = theta * j / steps;
                    var ca = Math.Cos(a);
                    var sa = Math.Sin(a);
                    side.Add((qx[k] + (r * ((px * ca) - (py * sa))), qy[k] + (r * ((px * sa) + (py * ca)))));
                }

                side.Add((ax, ay));
                continue;
            }

            // Inner side: the miter point if it lies on the first half of the next offset segment and the second half of the
            // previous one (so neighbouring miter points cannot cross), otherwise through the centre point.
            var den = 1.0 + dot;
            if (den > 1e-6)
            {
                var ix = qx[k] + (r * (px + nx) / den);
                var iy = qy[k] + (r * (py + ny) / den);
                var prevA = (X: qx[k - 1] + (qr[k - 1] * px), Y: qy[k - 1] + (qr[k - 1] * py));
                var nextB = (X: qx[k + 1] + (qr[k + 1] * nx), Y: qy[k + 1] + (qr[k + 1] * ny));
                var t1 = Along(prevA.X, prevA.Y, bx, by, ix, iy);
                var t2 = Along(ax, ay, nextB.X, nextB.Y, ix, iy);
                if (t1 >= 0.5 && t1 <= 1.0 && t2 >= 0.0 && t2 <= 0.5)
                {
                    side.Add((ix, iy));
                    continue;
                }
            }

            side.Add((bx, by));
            side.Add((qx[k], qy[k]));
            side.Add((ax, ay));
        }

        var last = m - 2;
        side.Add((qx[m - 1] + (qr[m - 1] * ux[last]), qy[m - 1] + (qr[m - 1] * uy[last])));
        return side;
    }

    // Position of (px,py) along the segment a->b as a fraction; NaN-safe (returns -1 for a degenerate segment).
    private static double Along(double ax, double ay, double bx, double by, double px, double py)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = (dx * dx) + (dy * dy);
        return len2 < 1e-18 ? -1.0 : (((px - ax) * dx) + ((py - ay) * dy)) / len2;
    }

    private static void AddArc(List<(double X, double Y)> polygon, double cx, double cy, double r, double ux, double uy, double sweep)
    {
        var step = ArcStep(r);
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / step));
        for (var j = 1; j < steps; j++)
        {
            var a = sweep * j / steps;
            var ca = Math.Cos(a);
            var sa = Math.Sin(a);
            polygon.Add((cx + (r * ((ux * ca) - (uy * sa))), cy + (r * ((ux * sa) + (uy * ca)))));
        }
    }

    private static double ArcStep(double radius)
    {
        if (radius <= 0)
        {
            return InkConstants.MaxArcStep;
        }

        var ratio = Math.Clamp(1.0 - (InkConstants.OutlineChordTolerance / radius), 0.0, 1.0);
        return Math.Clamp(2.0 * Math.Acos(ratio), InkConstants.MinArcStep, InkConstants.MaxArcStep);
    }

    private static List<(double X, double Y)> Circle(double cx, double cy, double r)
    {
        var step = ArcStep(r);
        var n = Math.Max(32, (int)Math.Ceiling(2.0 * Math.PI / step));
        var polygon = new List<(double X, double Y)>(n);
        for (var i = 0; i < n; i++)
        {
            var a = -2.0 * Math.PI * i / n;
            polygon.Add((cx + (r * Math.Cos(a)), cy + (r * Math.Sin(a))));
        }

        return r > 0 ? polygon : [(cx, cy)];
    }

    private static List<(double X, double Y)> RemoveDuplicates(List<(double X, double Y)> points)
    {
        var result = new List<(double X, double Y)>(points.Count);
        foreach (var p in points)
        {
            if (result.Count == 0 || Dist2(result[^1], p) > 1e-12)
            {
                result.Add(p);
            }
        }

        while (result.Count > 1 && Dist2(result[^1], result[0]) <= 1e-12)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static double Dist2((double X, double Y) a, (double X, double Y) b) => Sq(a.X - b.X) + Sq(a.Y - b.Y);

    private static double Sq(double v) => v * v;
}
