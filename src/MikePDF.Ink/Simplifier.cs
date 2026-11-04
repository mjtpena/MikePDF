namespace MikePDF.Ink;

/// <summary>A simplified centreline: <see cref="Xy"/> holds x,y pairs (length 2n) and <see cref="Widths"/> n widths, all in points.</summary>
public sealed record SimplifiedStroke(float[] Xy, float[] Widths);

/// <summary>
/// Douglas-Peucker simplification of a stroke centreline (ink-pen.md 3.8, INK-04). Iterative (an explicit stack), so a stroke of
/// any length cannot overflow the call stack.
/// </summary>
public static class Simplifier
{
    /// <summary>
    /// Returns the indices of the points kept, ascending, always including the first and last. Every dropped point lies within
    /// <paramref name="tolerance"/> of the segment between the kept points that surround it.
    /// Throws <see cref="ArgumentException"/> for a negative or non-finite tolerance or a non-finite coordinate.
    /// </summary>
    public static int[] DouglasPeuckerIndices(ReadOnlySpan<InkSample> points, double tolerance)
    {
        var x = new double[points.Length];
        var y = new double[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            x[i] = points[i].X;
            y[i] = points[i].Y;
        }

        return Reduce(x, y, null, tolerance);
    }

    /// <summary>
    /// Simplifies a stroke and keeps the width of each kept point. A point is also kept where dropping it would change the half
    /// width by more than <paramref name="tolerance"/> (linear interpolation along the chord), so pressure variation survives.
    /// </summary>
    public static SimplifiedStroke Simplify(IReadOnlyList<InkSample> samples, InkStyle style, double tolerance = InkConstants.DefaultSimplifyTolerance)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(style);
        var n = samples.Count;
        var widths = WidthProfile.Compute(samples, style);
        var x = new double[n];
        var y = new double[n];
        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = samples[i].X;
            y[i] = samples[i].Y;
            w[i] = widths[i];
        }

        var keep = Reduce(x, y, w, tolerance);
        var xy = new float[keep.Length * 2];
        var kw = new float[keep.Length];
        for (var k = 0; k < keep.Length; k++)
        {
            xy[2 * k] = samples[keep[k]].X;
            xy[(2 * k) + 1] = samples[keep[k]].Y;
            kw[k] = widths[keep[k]];
        }

        return new SimplifiedStroke(xy, kw);
    }

    private static int[] Reduce(double[] x, double[] y, double[]? w, double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentException("The tolerance must be finite and not negative.", nameof(tolerance));
        }

        var n = x.Length;
        for (var i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                throw new ArgumentException($"Point {i} is not finite.");
            }
        }

        if (n <= 2)
        {
            var all = new int[n];
            for (var i = 0; i < n; i++)
            {
                all[i] = i;
            }

            return all;
        }

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last - first < 2)
            {
                continue;
            }

            var worst = -1.0;
            var worstIndex = -1;
            var ax = x[first];
            var ay = y[first];
            var dx = x[last] - ax;
            var dy = y[last] - ay;
            var len2 = (dx * dx) + (dy * dy);
            for (var i = first + 1; i < last; i++)
            {
                var t = len2 > 0 ? Math.Clamp((((x[i] - ax) * dx) + ((y[i] - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
                var px = ax + (t * dx) - x[i];
                var py = ay + (t * dy) - y[i];
                var err = Math.Sqrt((px * px) + (py * py));
                if (w is not null)
                {
                    var interpolated = w[first] + (t * (w[last] - w[first]));
                    err = Math.Max(err, 0.5 * Math.Abs(w[i] - interpolated));
                }

                if (err > worst)
                {
                    worst = err;
                    worstIndex = i;
                }
            }

            if (worst > tolerance)
            {
                keep[worstIndex] = true;
                stack.Push((first, worstIndex));
                stack.Push((worstIndex, last));
            }
        }

        var list = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (keep[i])
            {
                list.Add(i);
            }
        }

        return [.. list];
    }
}
