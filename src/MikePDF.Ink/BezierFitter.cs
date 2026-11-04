namespace MikePDF.Ink;

/// <summary>
/// Schneider-style least-squares cubic Bezier fitting of a closed polygon (the stroke outline). The polygon is cut at corners
/// into open chains; each chain is fitted by one cubic, reparametrised by Newton steps, and split at the worst point when the error
/// is above the tolerance. A fit is accepted only when the sampled curve also stays within tolerance of the polygon chain, so the
/// deviation holds in both directions. Splitting is iterative with a depth bound; a chain that cannot be fitted within the bound is
/// emitted as straight lines, which are exact.
/// </summary>
internal static class BezierFitter
{
    private const int MaxDepth = 60;
    private const int MaxReparameterisations = 4;
    private const double CircleKappa = 0.5522847498307936;

    private readonly record struct Cubic(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3);

    private readonly record struct Work(int First, int Last, double T1X, double T1Y, double T2X, double T2Y, int Depth);

    public static List<PathSegment> Circle(double cx, double cy, double r)
    {
        var k = CircleKappa * r;
        var segs = new List<PathSegment>(6)
        {
            new(PathSegmentKind.MoveTo, R(cx + r), R(cy), 0, 0, 0, 0),
            new(PathSegmentKind.CubicTo, R(cx + r), R(cy - k), R(cx + k), R(cy - r), R(cx), R(cy - r)),
            new(PathSegmentKind.CubicTo, R(cx - k), R(cy - r), R(cx - r), R(cy - k), R(cx - r), R(cy)),
            new(PathSegmentKind.CubicTo, R(cx - r), R(cy + k), R(cx - k), R(cy + r), R(cx), R(cy + r)),
            new(PathSegmentKind.CubicTo, R(cx + k), R(cy + r), R(cx + r), R(cy + k), R(cx + r), R(cy)),
            new(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0),
        };
        return segs;
    }

    public static List<PathSegment> FitClosed(List<(double X, double Y)> polygon, double tolerance)
    {
        var n = polygon.Count;
        var segs = new List<PathSegment>(n + 2);
        if (n == 0)
        {
            return segs;
        }

        var px = new double[n];
        var py = new double[n];
        for (var i = 0; i < n; i++)
        {
            px[i] = polygon[i].X;
            py[i] = polygon[i].Y;
        }

        segs.Add(new PathSegment(PathSegmentKind.MoveTo, R(px[0]), R(py[0]), 0, 0, 0, 0));
        if (n < 4)
        {
            for (var i = 1; i < n; i++)
            {
                segs.Add(new PathSegment(PathSegmentKind.LineTo, R(px[i]), R(py[i]), 0, 0, 0, 0));
            }

            segs.Add(new PathSegment(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0));
            return segs;
        }

        // Corners: vertices where the outline turns sharply.
        var isCorner = new bool[n];
        var splits = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var prev = (i + n - 1) % n;
            var next = (i + 1) % n;
            var ax = px[i] - px[prev];
            var ay = py[i] - py[prev];
            var bx = px[next] - px[i];
            var by = py[next] - py[i];
            var turn = Math.Atan2(Math.Abs((ax * by) - (ay * bx)), (ax * bx) + (ay * by));
            if (turn > InkConstants.CornerAngle)
            {
                isCorner[i] = true;
                splits.Add(i);
            }
        }

        if (splits.Count == 0)
        {
            splits.Add(0);
            splits.Add(n / 2);
        }
        else if (splits.Count == 1)
        {
            splits.Add((splits[0] + (n / 2)) % n);
            splits.Sort();
        }

        // The path starts at the first split point, which is where the first chain begins.
        segs.Clear();
        segs.Add(new PathSegment(PathSegmentKind.MoveTo, R(px[splits[0]]), R(py[splits[0]]), 0, 0, 0, 0));
        for (var s = 0; s < splits.Count; s++)
        {
            var start = splits[s];
            var end = splits[(s + 1) % splits.Count];
            var length = (((end - start) + n) % n) + 1;
            var cx = new double[length];
            var cy = new double[length];
            for (var j = 0; j < length; j++)
            {
                cx[j] = px[(start + j) % n];
                cy[j] = py[(start + j) % n];
            }

            var (t1x, t1y) = isCorner[start]
                ? Normalise(cx[1] - cx[0], cy[1] - cy[0])
                : Normalise(px[(start + 1) % n] - px[(start + n - 1) % n], py[(start + 1) % n] - py[(start + n - 1) % n]);
            var (t2x, t2y) = isCorner[end]
                ? Normalise(cx[length - 2] - cx[length - 1], cy[length - 2] - cy[length - 1])
                : Normalise(px[(end + n - 1) % n] - px[(end + 1) % n], py[(end + n - 1) % n] - py[(end + 1) % n]);
            FitChain(cx, cy, t1x, t1y, t2x, t2y, tolerance, segs);
        }

        segs.Add(new PathSegment(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0));
        return segs;
    }

    private static void FitChain(double[] x, double[] y, double t1x, double t1y, double t2x, double t2y, double tol, List<PathSegment> output)
    {
        var stack = new Stack<Work>();
        stack.Push(new Work(0, x.Length - 1, t1x, t1y, t2x, t2y, 0));
        while (stack.Count > 0)
        {
            var w = stack.Pop();
            var count = w.Last - w.First + 1;
            if (count < 2)
            {
                continue;
            }

            if (count == 2 || w.Depth >= MaxDepth)
            {
                for (var i = w.First + 1; i <= w.Last; i++)
                {
                    output.Add(new PathSegment(PathSegmentKind.LineTo, R(x[i]), R(y[i]), 0, 0, 0, 0));
                }

                continue;
            }

            if (TryFit(x, y, w, tol, out var cubic, out var splitIndex))
            {
                output.Add(ToSegment(cubic));
                continue;
            }

            splitIndex = Math.Clamp(splitIndex, w.First + 1, w.Last - 1);
            var (cx, cy) = Normalise(x[splitIndex - 1] - x[splitIndex + 1], y[splitIndex - 1] - y[splitIndex + 1]);
            if (cx == 0 && cy == 0)
            {
                (cx, cy) = Normalise(x[w.Last] - x[w.First], y[w.Last] - y[w.First]);
            }

            // Push the right half first so the left half is emitted first.
            stack.Push(new Work(splitIndex, w.Last, -cx, -cy, w.T2X, w.T2Y, w.Depth + 1));
            stack.Push(new Work(w.First, splitIndex, w.T1X, w.T1Y, cx, cy, w.Depth + 1));
        }
    }

    private static PathSegment ToSegment(Cubic c)
    {
        // A cubic whose control points sit on the chord is written as a line.
        if (DistanceToSegment(c.X1, c.Y1, c.X0, c.Y0, c.X3, c.Y3) <= 1e-3 && DistanceToSegment(c.X2, c.Y2, c.X0, c.Y0, c.X3, c.Y3) <= 1e-3)
        {
            return new PathSegment(PathSegmentKind.LineTo, R(c.X3), R(c.Y3), 0, 0, 0, 0);
        }

        return new PathSegment(PathSegmentKind.CubicTo, R(c.X1), R(c.Y1), R(c.X2), R(c.Y2), R(c.X3), R(c.Y3));
    }

    private static bool TryFit(double[] x, double[] y, Work w, double tol, out Cubic cubic, out int splitIndex)
    {
        var n = w.Last - w.First + 1;
        var u = new double[n];
        for (var i = 1; i < n; i++)
        {
            u[i] = u[i - 1] + Math.Sqrt(Sq(x[w.First + i] - x[w.First + i - 1]) + Sq(y[w.First + i] - y[w.First + i - 1]));
        }

        var total = u[n - 1];
        if (total <= 0)
        {
            cubic = default;
            splitIndex = w.First + (n / 2);
            return false;
        }

        for (var i = 1; i < n; i++)
        {
            u[i] /= total;
        }

        u[n - 1] = 1.0;
        var tol2 = tol * tol;
        cubic = Generate(x, y, w, u, total);
        var err = MaxError(x, y, w, u, cubic, out var worst);
        if (err > tol2 * 36.0)
        {
            splitIndex = worst;
            return false;
        }

        for (var iteration = 0; iteration < MaxReparameterisations && err > tol2; iteration++)
        {
            Reparameterise(x, y, w, u, cubic);
            cubic = Generate(x, y, w, u, total);
            err = MaxError(x, y, w, u, cubic, out worst);
        }

        if (err > tol2)
        {
            splitIndex = worst;
            return false;
        }

        // The vertices are close to the curve; now check the curve (including the stretches between vertices) against the chain.
        if (!CurveWithinChain(x, y, w.First, w.Last, cubic, tol, out var farIndex))
        {
            splitIndex = farIndex;
            return false;
        }

        splitIndex = 0;
        return true;
    }

    private static Cubic Generate(double[] x, double[] y, Work w, double[] u, double pathLength)
    {
        var n = u.Length;
        var x0 = x[w.First];
        var y0 = y[w.First];
        var x3 = x[w.Last];
        var y3 = y[w.Last];
        double c00 = 0, c01 = 0, c11 = 0, xa = 0, xb = 0;
        for (var i = 0; i < n; i++)
        {
            var t = u[i];
            var s = 1 - t;
            var b0 = s * s * s;
            var b1 = 3 * s * s * t;
            var b2 = 3 * s * t * t;
            var b3 = t * t * t;
            var a1x = w.T1X * b1;
            var a1y = w.T1Y * b1;
            var a2x = w.T2X * b2;
            var a2y = w.T2Y * b2;
            c00 += (a1x * a1x) + (a1y * a1y);
            c01 += (a1x * a2x) + (a1y * a2y);
            c11 += (a2x * a2x) + (a2y * a2y);
            var tx = x[w.First + i] - (x0 * (b0 + b1)) - (x3 * (b2 + b3));
            var ty = y[w.First + i] - (y0 * (b0 + b1)) - (y3 * (b2 + b3));
            xa += (a1x * tx) + (a1y * ty);
            xb += (a2x * tx) + (a2y * ty);
        }

        var det = (c00 * c11) - (c01 * c01);
        var chord = Math.Sqrt(Sq(x3 - x0) + Sq(y3 - y0));
        double alphaL = 0, alphaR = 0;
        if (Math.Abs(det) > 1e-12)
        {
            alphaL = ((xa * c11) - (xb * c01)) / det;
            alphaR = ((c00 * xb) - (c01 * xa)) / det;
        }

        var eps = 1e-6 * chord;
        if (!double.IsFinite(alphaL) || !double.IsFinite(alphaR) || alphaL < eps || alphaR < eps || alphaL > pathLength || alphaR > pathLength)
        {
            alphaL = chord / 3.0;
            alphaR = chord / 3.0;
        }

        return new Cubic(x0, y0, x0 + (w.T1X * alphaL), y0 + (w.T1Y * alphaL), x3 + (w.T2X * alphaR), y3 + (w.T2Y * alphaR), x3, y3);
    }

    private static double MaxError(double[] x, double[] y, Work w, double[] u, Cubic c, out int worstIndex)
    {
        var worst = -1.0;
        worstIndex = w.First + (u.Length / 2);
        for (var i = 1; i < u.Length - 1; i++)
        {
            var (qx, qy) = Point(c, u[i]);
            var d = Sq(qx - x[w.First + i]) + Sq(qy - y[w.First + i]);
            if (d > worst)
            {
                worst = d;
                worstIndex = w.First + i;
            }
        }

        return Math.Max(worst, 0.0);
    }

    private static void Reparameterise(double[] x, double[] y, Work w, double[] u, Cubic c)
    {
        for (var i = 1; i < u.Length - 1; i++)
        {
            var t = u[i];
            var s = 1 - t;
            var (qx, qy) = Point(c, t);
            var q1x = 3 * ((s * s * (c.X1 - c.X0)) + (2 * s * t * (c.X2 - c.X1)) + (t * t * (c.X3 - c.X2)));
            var q1y = 3 * ((s * s * (c.Y1 - c.Y0)) + (2 * s * t * (c.Y2 - c.Y1)) + (t * t * (c.Y3 - c.Y2)));
            var q2x = 6 * ((s * (c.X2 - (2 * c.X1) + c.X0)) + (t * (c.X3 - (2 * c.X2) + c.X1)));
            var q2y = 6 * ((s * (c.Y2 - (2 * c.Y1) + c.Y0)) + (t * (c.Y3 - (2 * c.Y2) + c.Y1)));
            var dx = qx - x[w.First + i];
            var dy = qy - y[w.First + i];
            var numerator = (dx * q1x) + (dy * q1y);
            var denominator = (q1x * q1x) + (q1y * q1y) + (dx * q2x) + (dy * q2y);
            if (Math.Abs(denominator) > 1e-12)
            {
                u[i] = Math.Clamp(t - (numerator / denominator), 0.0, 1.0);
            }
        }

        // Keep the parameters ordered so a bad Newton step cannot fold the curve back on itself.
        for (var i = 1; i < u.Length - 1; i++)
        {
            u[i] = Math.Clamp(u[i], u[i - 1], 1.0);
        }
    }

    private static bool CurveWithinChain(double[] x, double[] y, int first, int last, Cubic c, double tol, out int farIndex)
    {
        var control = Math.Sqrt(Sq(c.X1 - c.X0) + Sq(c.Y1 - c.Y0)) + Math.Sqrt(Sq(c.X2 - c.X1) + Sq(c.Y2 - c.Y1)) + Math.Sqrt(Sq(c.X3 - c.X2) + Sq(c.Y3 - c.Y2));
        var samples = Math.Clamp((int)Math.Ceiling(control / 0.4), 16, 256);
        var tol2 = tol * tol;
        for (var s = 0; s <= samples; s++)
        {
            var (qx, qy) = Point(c, (double)s / samples);
            var best = double.MaxValue;
            for (var i = first; i < last; i++)
            {
                var d = DistanceToSegmentSquared(qx, qy, x[i], y[i], x[i + 1], y[i + 1]);
                if (d < best)
                {
                    best = d;
                }
            }

            if (best > tol2)
            {
                var nearest = first + 1;
                var nearestD = double.MaxValue;
                for (var i = first + 1; i < last; i++)
                {
                    var d = Sq(qx - x[i]) + Sq(qy - y[i]);
                    if (d < nearestD)
                    {
                        nearestD = d;
                        nearest = i;
                    }
                }

                farIndex = nearest;
                return false;
            }
        }

        farIndex = first + 1;
        return true;
    }

    private static (double X, double Y) Point(Cubic c, double t)
    {
        var s = 1 - t;
        var b0 = s * s * s;
        var b1 = 3 * s * s * t;
        var b2 = 3 * s * t * t;
        var b3 = t * t * t;
        return ((b0 * c.X0) + (b1 * c.X1) + (b2 * c.X2) + (b3 * c.X3), (b0 * c.Y0) + (b1 * c.Y1) + (b2 * c.Y2) + (b3 * c.Y3));
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by) =>
        Math.Sqrt(DistanceToSegmentSquared(px, py, ax, ay, bx, by));

    private static double DistanceToSegmentSquared(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 > 0 ? Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
        return Sq(ax + (t * dx) - px) + Sq(ay + (t * dy) - py);
    }

    private static (double X, double Y) Normalise(double x, double y)
    {
        var l = Math.Sqrt((x * x) + (y * y));
        return l > 1e-12 ? (x / l, y / l) : (0, 0);
    }

    private static double R(double v)
    {
        var r = Math.Round(v, InkConstants.ContentDecimals, MidpointRounding.AwayFromZero);
        return r == 0 ? 0.0 : r;
    }

    private static double Sq(double v) => v * v;
}
