using System.Globalization;
using MikePDF.Core.Geometry;

namespace MikePDF.Markup.Tests.Support;

/// <summary>Reads back the path operators of an appearance's content stream, so a test can compare them with the geometry that made them (AC-13).</summary>
internal static class ContentStreams
{
    public sealed record Operation(string Operator, IReadOnlyList<string> Operands);

    public static IReadOnlyList<Operation> Parse(string content)
    {
        var operations = new List<Operation>();
        var operands = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                // A literal string: balanced parentheses, backslash escapes.
                var depth = 0;
                var start = i;
                do
                {
                    if (content[i] == '\\')
                    {
                        i++;
                    }
                    else if (content[i] == '(')
                    {
                        depth++;
                    }
                    else if (content[i] == ')')
                    {
                        depth--;
                    }

                    i++;
                }
                while (depth > 0 && i < content.Length);
                operands.Add(content[start..i]);
            }
            else if (c is '[' or ']')
            {
                operands.Add(c.ToString());
                i++;
            }
            else
            {
                var start = i;
                while (i < content.Length && !char.IsWhiteSpace(content[i]) && content[i] is not ('[' or ']' or '('))
                {
                    i++;
                }

                var token = content[start..i];
                var isNumber = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (isNumber || token.StartsWith('/'))
                {
                    operands.Add(token);
                }
                else
                {
                    operations.Add(new Operation(token, [.. operands]));
                    operands.Clear();
                }
            }
        }

        return operations;
    }

    /// <summary>Every point of the m, l and c operators in order (control points included), as the scene lists them.</summary>
    public static IReadOnlyList<PointD> PathPoints(string content)
    {
        var points = new List<PointD>();
        foreach (var op in Parse(content))
        {
            if (op.Operator is "m" or "l" or "c")
            {
                for (var i = 0; i + 1 < op.Operands.Count; i += 2)
                {
                    points.Add(new PointD(Number(op.Operands[i]), Number(op.Operands[i + 1])));
                }
            }
        }

        return points;
    }

    public static double Number(string token) => double.Parse(token, CultureInfo.InvariantCulture);

    /// <summary>The bounds of a scene path including the extent of its curves (sampled), not counting the stroke's width.</summary>
    public static (double Left, double Bottom, double Right, double Top) ExactBounds(ScenePath path)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        void Add(double x, double y)
        {
            l = Math.Min(l, x);
            r = Math.Max(r, x);
            b = Math.Min(b, y);
            t = Math.Max(t, y);
        }

        var (cx, cy) = (0.0, 0.0);
        foreach (var s in path.Segments)
        {
            switch (s.Kind)
            {
                case MikePDF.Ink.PathSegmentKind.MoveTo or MikePDF.Ink.PathSegmentKind.LineTo:
                    (cx, cy) = (s.X1, s.Y1);
                    Add(cx, cy);
                    break;
                case MikePDF.Ink.PathSegmentKind.CubicTo:
                    for (var k = 1; k <= 24; k++)
                    {
                        var u = k / 24.0;
                        var v = 1 - u;
                        Add(
                            (v * v * v * cx) + (3 * v * v * u * s.X1) + (3 * v * u * u * s.X2) + (u * u * u * s.X3),
                            (v * v * v * cy) + (3 * v * v * u * s.Y1) + (3 * v * u * u * s.Y2) + (u * u * u * s.Y3));
                    }

                    (cx, cy) = (s.X3, s.Y3);
                    break;
            }
        }

        return (l, b, r, t);
    }
}
