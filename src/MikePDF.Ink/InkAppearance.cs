using System.Text;

namespace MikePDF.Ink;

/// <summary>
/// The appearance geometry of one ink annotation: the union bounds of the outlines, the content stream (path operators and
/// <c>f</c> per stroke, strokes separated by a newline) and the fitted paths. Colour, opacity and blend mode are added by Markup.
/// </summary>
public sealed record InkAppearanceResult(double Left, double Bottom, double Right, double Top, string Content, IReadOnlyList<InkPath> Paths);

/// <summary>Builds the appearance stream content of an ink annotation (ink-pen.md 3.8, annotation-core.md 3.9).</summary>
public static class InkAppearance
{
    /// <summary>
    /// Fits every stroke outline and joins them into one content stream. <c>Left..Top</c> is the exact union of the outline bounds
    /// (no padding). Throws <see cref="ArgumentException"/> for no strokes, a stroke without points, mismatched or non-finite data or a
    /// stroke above 20,000 points, and <see cref="InkLimitException"/> when the content exceeds 2 MiB. <paramref name="ends"/> is
    /// <see cref="InkEnds.Flat"/> for the highlighter (butt caps, as a marker pen is cut) and <see cref="InkEnds.Round"/> for the pen.
    /// </summary>
    public static InkAppearanceResult Build(IReadOnlyList<SimplifiedStroke> strokes, InkEnds ends = InkEnds.Round)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (strokes.Count == 0)
        {
            throw new ArgumentException("An ink annotation needs at least one stroke.", nameof(strokes));
        }

        var content = new StringBuilder();
        var paths = new List<InkPath>(strokes.Count);
        var left = double.PositiveInfinity;
        var bottom = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var top = double.NegativeInfinity;
        for (var i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            if (stroke is null || stroke.Xy is null || stroke.Widths is null)
            {
                throw new ArgumentException($"Stroke {i} is missing.", nameof(strokes));
            }

            if (stroke.Widths.Length < 1 || stroke.Xy.Length != stroke.Widths.Length * 2)
            {
                throw new ArgumentException($"Stroke {i} needs at least one point and two numbers per width.", nameof(strokes));
            }

            if (stroke.Widths.Length > StrokeLimits.MaxSamplesPerStroke)
            {
                throw new ArgumentException($"Stroke {i} has more than {StrokeLimits.MaxSamplesPerStroke} points.", nameof(strokes));
            }

            var path = StrokeOutline.Fit(stroke.Xy, stroke.Widths, InkConstants.DefaultFitDeviation, ends);
            paths.Add(path);
            if (i > 0)
            {
                content.Append('\n');
            }

            path.AppendContent(content);
            content.Append('f');
            if (content.Length > StrokeLimits.MaxAppearanceBytes)
            {
                throw new InkLimitException($"The ink appearance exceeds {StrokeLimits.MaxAppearanceBytes} bytes.");
            }

            var b = path.Bounds;
            left = Math.Min(left, b.Left);
            bottom = Math.Min(bottom, b.Bottom);
            right = Math.Max(right, b.Right);
            top = Math.Max(top, b.Top);
        }

        return new InkAppearanceResult(left, bottom, right, top, content.ToString(), paths);
    }
}
