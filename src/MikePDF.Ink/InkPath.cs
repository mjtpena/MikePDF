using System.Globalization;
using System.Text;

namespace MikePDF.Ink;

/// <summary>The kind of one path segment.</summary>
public enum PathSegmentKind
{
    /// <summary>Starts a sub-path at (X1, Y1).</summary>
    MoveTo,

    /// <summary>A straight line to (X1, Y1).</summary>
    LineTo,

    /// <summary>A cubic Bezier with control points (X1, Y1), (X2, Y2) ending at (X3, Y3).</summary>
    CubicTo,

    /// <summary>Closes the sub-path.</summary>
    Close,
}

/// <summary>One path segment in page space points. MoveTo and LineTo use X1,Y1; CubicTo uses all three points.</summary>
public readonly record struct PathSegment(PathSegmentKind Kind, double X1, double Y1, double X2, double Y2, double X3, double Y3);

/// <summary>A closed vector path (one stroke outline) with its exact bounds and a PDF content stream rendering.</summary>
public sealed class InkPath
{
    private readonly PathSegment[] _segments;

    /// <summary>Creates a path from segments. Throws <see cref="ArgumentException"/> for non-finite coordinates.</summary>
    public InkPath(IReadOnlyList<PathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = new PathSegment[segments.Count];
        for (var i = 0; i < _segments.Length; i++)
        {
            var s = segments[i];
            if (!double.IsFinite(s.X1 + s.Y1 + s.X2 + s.Y2 + s.X3 + s.Y3))
            {
                throw new ArgumentException($"Segment {i} is not finite.", nameof(segments));
            }

            _segments[i] = s;
        }

        Segments = Array.AsReadOnly(_segments);
        Bounds = ComputeBounds(_segments);
    }

    /// <summary>The segments in drawing order.</summary>
    public IReadOnlyList<PathSegment> Segments { get; }

    /// <summary>The exact bounds of the drawn path (curve extrema, not control points): Left, Bottom, Right, Top. All zero for an empty path.</summary>
    public (double Left, double Bottom, double Right, double Top) Bounds { get; }

    /// <summary>
    /// The path as PDF operators, one per line: <c>x y m</c>, <c>x y l</c>, <c>x1 y1 x2 y2 x3 y3 c</c>, <c>h</c>.
    /// Numbers use the invariant culture, at most 3 decimals, no exponent, negative zero as 0.
    /// </summary>
    public string ToContentStream()
    {
        var sb = new StringBuilder(_segments.Length * 24);
        AppendContent(sb);
        return sb.ToString();
    }

    internal void AppendContent(StringBuilder sb)
    {
        foreach (var s in _segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo:
                    AppendNumber(sb, s.X1).Append(' ');
                    AppendNumber(sb, s.Y1).Append(" m\n");
                    break;
                case PathSegmentKind.LineTo:
                    AppendNumber(sb, s.X1).Append(' ');
                    AppendNumber(sb, s.Y1).Append(" l\n");
                    break;
                case PathSegmentKind.CubicTo:
                    AppendNumber(sb, s.X1).Append(' ');
                    AppendNumber(sb, s.Y1).Append(' ');
                    AppendNumber(sb, s.X2).Append(' ');
                    AppendNumber(sb, s.Y2).Append(' ');
                    AppendNumber(sb, s.X3).Append(' ');
                    AppendNumber(sb, s.Y3).Append(" c\n");
                    break;
                default:
                    sb.Append("h\n");
                    break;
            }
        }
    }

    /// <summary>Formats a number for a content stream: invariant, at most 3 decimals, no exponent, no negative zero, non-finite as 0.</summary>
    public static string FormatNumber(double value)
    {
        var sb = new StringBuilder(16);
        AppendNumber(sb, value);
        return sb.ToString();
    }

    internal static StringBuilder AppendNumber(StringBuilder sb, double value)
    {
        if (!double.IsFinite(value))
        {
            return sb.Append('0');
        }

        var r = Math.Round(value, InkConstants.ContentDecimals, MidpointRounding.AwayFromZero);
        if (r == 0)
        {
            return sb.Append('0');
        }

        // The "0.###" custom format never uses exponent notation, whatever the magnitude.
        return sb.Append(r.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static (double Left, double Bottom, double Right, double Top) ComputeBounds(PathSegment[] segments)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var cx = 0.0;
        var cy = 0.0;

        void Add(double x, double y)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        foreach (var s in segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo:
                case PathSegmentKind.LineTo:
                    Add(s.X1, s.Y1);
                    cx = s.X1;
                    cy = s.Y1;
                    break;
                case PathSegmentKind.CubicTo:
                    Add(s.X3, s.Y3);
                    foreach (var t in CubicExtrema(cx, s.X1, s.X2, s.X3))
                    {
                        Add(Cubic(cx, s.X1, s.X2, s.X3, t), Cubic(cy, s.Y1, s.Y2, s.Y3, t));
                    }

                    foreach (var t in CubicExtrema(cy, s.Y1, s.Y2, s.Y3))
                    {
                        Add(Cubic(cx, s.X1, s.X2, s.X3, t), Cubic(cy, s.Y1, s.Y2, s.Y3, t));
                    }

                    cx = s.X3;
                    cy = s.Y3;
                    break;
                default:
                    break;
            }
        }

        return double.IsFinite(minX) ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        var u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    // Parameters in (0,1) where the derivative of one coordinate of a cubic is zero.
    private static List<double> CubicExtrema(double p0, double p1, double p2, double p3)
    {
        var result = new List<double>(2);
        var a = -p0 + (3 * p1) - (3 * p2) + p3;
        var b = 2 * (p0 - (2 * p1) + p2);
        var c = p1 - p0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                AddRoot(result, -c / b);
            }

            return result;
        }

        var disc = (b * b) - (4 * a * c);
        if (disc < 0)
        {
            return result;
        }

        var sq = Math.Sqrt(disc);
        AddRoot(result, (-b + sq) / (2 * a));
        AddRoot(result, (-b - sq) / (2 * a));
        return result;
    }

    private static void AddRoot(List<double> roots, double t)
    {
        if (t > 0 && t < 1)
        {
            roots.Add(t);
        }
    }
}
