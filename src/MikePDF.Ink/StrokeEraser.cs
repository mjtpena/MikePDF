namespace MikePDF.Ink;

/// <summary>How the eraser acts (ink-pen.md 3.6).</summary>
public enum EraserMode
{
    /// <summary>Removes every whole stroke the eraser touches.</summary>
    Stroke,

    /// <summary>Removes the part of a stroke under the eraser and splits the stroke.</summary>
    Point,
}

/// <summary>The outcome of one eraser gesture: ids of the original strokes that are gone and the pieces that replace some of them.</summary>
public sealed record EraseResult(IReadOnlyList<string> RemovedIds, IReadOnlyList<InkStroke> Added);

/// <summary>
/// Eraser geometry (ink-pen.md 3.6, INK-06). The eraser is a circle in the same page space as the strokes. The one-command and
/// undo behaviour belongs to the caller: one <see cref="EraseGesture"/> result is one undoable step.
/// </summary>
public static class StrokeEraser
{
    /// <summary>
    /// True when the eraser circle touches the stroke outline: the distance from the circle centre to a segment of the centreline is
    /// at most the radius plus the half width at the closest point (the width varies linearly along a segment). A one-sample stroke is a
    /// disc of its width. Throws <see cref="ArgumentException"/> for a non-finite centre or a negative or non-finite radius.
    /// </summary>
    public static bool Touches(InkStroke stroke, InkStyle style, double cx, double cy, double radius)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(style);
        ValidateCircle(cx, cy, radius);
        return Touches(stroke, WidthProfile.Compute(stroke.Samples, style), cx, cy, radius);
    }

    /// <summary>Returns the ids of the strokes the eraser circle touches, in input order (stroke mode).</summary>
    public static IReadOnlyList<string> TouchedIds(IReadOnlyList<InkStroke> strokes, InkStyle style, double cx, double cy, double radius)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(style);
        ValidateCircle(cx, cy, radius);
        var ids = new List<string>();
        foreach (var stroke in strokes)
        {
            if (Touches(stroke, WidthProfile.Compute(stroke.Samples, style), cx, cy, radius))
            {
                ids.Add(stroke.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Removes the part of the centreline inside the eraser circle (point mode). The cut points are interpolated onto the circle
    /// boundary with interpolated pressure; the pieces keep the pressure of their samples and get ids from <paramref name="newId"/>.
    /// Returns the original stroke unchanged (same instance, in a one-element list) when nothing is inside, and an empty list when
    /// everything is erased. The <paramref name="style"/> is accepted for symmetry with the other calls; the cut uses the centreline.
    /// </summary>
    public static IReadOnlyList<InkStroke> SplitPoint(InkStroke stroke, InkStyle style, double cx, double cy, double radius, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(newId);
        ValidateCircle(cx, cy, radius);
        var s = stroke.Span;
        var n = s.Length;
        var r2 = radius * radius;
        var inside = new bool[n];
        for (var i = 0; i < n; i++)
        {
            inside[i] = Dist2(s[i], cx, cy) < r2;
        }

        var pieces = new List<List<InkSample>>();
        var current = new List<InkSample>();
        var changed = false;
        for (var i = 0; i < n; i++)
        {
            if (inside[i])
            {
                changed = true;
            }
            else
            {
                AddDistinct(current, s[i]);
            }

            if (i == n - 1)
            {
                break;
            }

            var a = s[i];
            var b = s[i + 1];
            if (inside[i] && inside[i + 1])
            {
                continue;
            }

            if (!CircleInterval(a, b, cx, cy, radius, out var t0, out var t1))
            {
                continue;
            }

            if (!inside[i] && !inside[i + 1])
            {
                // A chord through the circle with both ends outside.
                if (t0 > 0 && t1 < 1 && t1 - t0 > 1e-12)
                {
                    changed = true;
                    AddDistinct(current, Lerp(a, b, t0));
                    Close(pieces, ref current);
                    AddDistinct(current, Lerp(a, b, t1));
                }
            }
            else if (!inside[i])
            {
                AddDistinct(current, Lerp(a, b, t0));
                Close(pieces, ref current);
            }
            else
            {
                current = [];
                AddDistinct(current, Lerp(a, b, t1));
            }
        }

        if (current.Count > 0)
        {
            pieces.Add(current);
        }

        if (!changed)
        {
            return [stroke];
        }

        var result = new List<InkStroke>(pieces.Count);
        foreach (var piece in pieces)
        {
            result.Add(new InkStroke(newId(), piece));
        }

        return result;
    }

    /// <summary>
    /// Applies the eraser circle at every path point in order, each to the result of the previous (one gesture, one undoable step).
    /// Stroke mode removes the touched strokes; point mode splits them. <see cref="EraseResult.RemovedIds"/> lists the input strokes that
    /// are gone and <see cref="EraseResult.Added"/> the pieces that survive the whole gesture, in stroke order.
    /// Throws <see cref="ArgumentException"/> for a non-finite path point or a path above 1,000,000 points.
    /// </summary>
    public static EraseResult EraseGesture(IReadOnlyList<InkStroke> strokes, InkStyle style, EraserMode mode, IReadOnlyList<(double X, double Y)> path, double radius, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newId);
        if (path.Count > StrokeLimits.MaxGesturePoints)
        {
            throw new ArgumentException($"A gesture holds at most {StrokeLimits.MaxGesturePoints} points.", nameof(path));
        }

        for (var i = 0; i < path.Count; i++)
        {
            ValidateCircle(path[i].X, path[i].Y, radius);
        }

        var working = new List<(InkStroke Stroke, bool Original, float[]? Widths)>(strokes.Count);
        foreach (var stroke in strokes)
        {
            working.Add((stroke, true, mode == EraserMode.Stroke ? WidthProfile.Compute(stroke.Samples, style) : null));
        }

        var removed = new List<string>();
        foreach (var (px, py) in path)
        {
            var next = new List<(InkStroke Stroke, bool Original, float[]? Widths)>(working.Count);
            foreach (var item in working)
            {
                if (mode == EraserMode.Stroke)
                {
                    if (Touches(item.Stroke, item.Widths!, px, py, radius))
                    {
                        removed.Add(item.Stroke.Id);
                    }
                    else
                    {
                        next.Add(item);
                    }

                    continue;
                }

                var pieces = SplitPoint(item.Stroke, style, px, py, radius, newId);
                if (pieces.Count == 1 && ReferenceEquals(pieces[0], item.Stroke))
                {
                    next.Add(item);
                    continue;
                }

                if (item.Original)
                {
                    removed.Add(item.Stroke.Id);
                }

                foreach (var piece in pieces)
                {
                    next.Add((piece, false, null));
                }
            }

            working = next;
        }

        var added = new List<InkStroke>();
        foreach (var item in working)
        {
            if (!item.Original)
            {
                added.Add(item.Stroke);
            }
        }

        return new EraseResult(removed, added);
    }

    private static bool Touches(InkStroke stroke, float[] widths, double cx, double cy, double radius)
    {
        var s = stroke.Span;
        if (s.Length == 1)
        {
            return Math.Sqrt(Dist2(s[0], cx, cy)) <= radius + (widths[0] * 0.5);
        }

        for (var i = 0; i < s.Length - 1; i++)
        {
            var ax = (double)s[i].X;
            var ay = (double)s[i].Y;
            var dx = s[i + 1].X - ax;
            var dy = s[i + 1].Y - ay;
            var len2 = (dx * dx) + (dy * dy);
            var t = len2 > 0 ? Math.Clamp((((cx - ax) * dx) + ((cy - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
            var qx = ax + (t * dx) - cx;
            var qy = ay + (t * dy) - cy;
            var half = (widths[i] + (t * (widths[i + 1] - widths[i]))) * 0.5;
            if (Math.Sqrt((qx * qx) + (qy * qy)) <= radius + half)
            {
                return true;
            }
        }

        return false;
    }

    // The parameter interval of the segment a->b that lies inside the circle; false when the line misses it.
    private static bool CircleInterval(InkSample a, InkSample b, double cx, double cy, double radius, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 0;
        var dx = (double)b.X - a.X;
        var dy = (double)b.Y - a.Y;
        var fx = (double)a.X - cx;
        var fy = (double)a.Y - cy;
        var qa = (dx * dx) + (dy * dy);
        if (qa <= 0)
        {
            return false;
        }

        var qb = 2.0 * ((fx * dx) + (fy * dy));
        var qc = (fx * fx) + (fy * fy) - (radius * radius);
        var disc = (qb * qb) - (4.0 * qa * qc);
        if (disc < 0)
        {
            // Inside/outside classification said the segment crosses, but rounding says it does not: treat as touching tangentially.
            disc = 0;
        }

        var root = Math.Sqrt(disc);
        t0 = Math.Clamp((-qb - root) / (2.0 * qa), 0.0, 1.0);
        t1 = Math.Clamp((-qb + root) / (2.0 * qa), 0.0, 1.0);
        return true;
    }

    private static void Close(List<List<InkSample>> pieces, ref List<InkSample> current)
    {
        if (current.Count > 0)
        {
            pieces.Add(current);
        }

        current = [];
    }

    private static void AddDistinct(List<InkSample> list, InkSample s)
    {
        if (list.Count == 0 || list[^1].X != s.X || list[^1].Y != s.Y)
        {
            list.Add(s);
        }
    }

    private static InkSample Lerp(InkSample a, InkSample b, double t) =>
        new(
            (float)(a.X + (t * ((double)b.X - a.X))),
            (float)(a.Y + (t * ((double)b.Y - a.Y))),
            (float)Math.Clamp(a.Pressure + (t * ((double)b.Pressure - a.Pressure)), 0.0, 1.0));

    private static double Dist2(InkSample s, double cx, double cy)
    {
        var dx = s.X - cx;
        var dy = s.Y - cy;
        return (dx * dx) + (dy * dy);
    }

    private static void ValidateCircle(double cx, double cy, double radius)
    {
        if (!double.IsFinite(cx) || !double.IsFinite(cy))
        {
            throw new ArgumentException("The eraser centre must be finite.");
        }

        if (!double.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentException("The eraser radius must be finite and not negative.");
        }
    }
}
