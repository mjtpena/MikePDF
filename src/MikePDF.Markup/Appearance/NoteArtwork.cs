using MikePDF.Core.Geometry;

namespace MikePDF.Markup;

/// <summary>
/// The artwork of the seven note icons (sticky-notes 2.1, OQ-SN-1): MikePDF's own vector shapes, drawn in a 20 x 20 design box and scaled onto the
/// annotation's rectangle. The fill follows the annotation's colour and the outline is dark grey. The overlay and the appearance draw the same paths.
/// </summary>
public static class NoteArtwork
{
    /// <summary>The size of a note icon in points (sticky-notes 2.1).</summary>
    public const double IconSize = 20;

    private static readonly Rgb Ink = new(0.2, 0.2, 0.2);

    public static string NameOf(NoteIcon icon) => icon.ToString();

    public static bool TryParse(string? name, out NoteIcon icon) => Enum.TryParse(name, ignoreCase: false, out icon) && Enum.IsDefined(icon);

    public static IReadOnlyList<ScenePath> Items(NoteIcon icon, PdfRect rect, Rgb fill)
    {
        var sx = rect.Width / IconSize;
        var sy = rect.Height / IconSize;
        PointD P(double x, double y) => new(rect.Left + (x * sx), rect.Bottom + (y * sy));
        double Stroke(double w) => w * Math.Min(sx, sy);
        var items = new List<ScenePath>();

        ScenePath Shape(PathBuilder path, bool filled, double lineWidth, Rgb? fillColour = null) =>
            new(path.Segments, filled ? PaintMode.FillAndStroke : PaintMode.Stroke, filled ? (fillColour ?? fill) : null, Ink, Stroke(lineWidth), CapStyle.Round, JoinStyle.Round);

        ScenePath Line(double x1, double y1, double x2, double y2, double w = 1) =>
            new(new PathBuilder().Move(P(x1, y1)).Line(P(x2, y2)).Segments, PaintMode.Stroke, null, Ink, Stroke(w), CapStyle.Round);

        switch (icon)
        {
            case NoteIcon.Comment:
                items.Add(Shape(new PathBuilder().Polygon([P(4, 6), P(4, 1), P(10, 6)]), true, 0.8));
                items.Add(Shape(RoundedBox(P(1, 5), P(19, 19), 3 * Math.Min(sx, sy)), true, 0.8));
                items.Add(Line(4.5, 15, 15.5, 15));
                items.Add(Line(4.5, 12, 15.5, 12));
                items.Add(Line(4.5, 9, 11.5, 9));
                break;
            case NoteIcon.Note:
                items.Add(Shape(new PathBuilder().Polygon([P(3, 1), P(17, 1), P(17, 13), P(11, 19), P(3, 19)]), true, 0.8));
                items.Add(Shape(new PathBuilder().Polygon([P(11, 19), P(11, 13), P(17, 13)]), true, 0.8, new Rgb(fill.R * 0.7, fill.G * 0.7, fill.B * 0.7)));
                items.Add(Line(6, 10, 14, 10));
                items.Add(Line(6, 7, 14, 7));
                items.Add(Line(6, 4, 11, 4));
                break;
            case NoteIcon.Key:
                items.Add(Shape(new PathBuilder().Ellipse(P(1.5, 5.5).X, P(1.5, 5.5).Y, P(10.5, 14.5).X, P(10.5, 14.5).Y), true, 0.8));
                items.Add(Shape(new PathBuilder().Rectangle(P(10, 9).X, P(10, 9).Y, P(19, 11).X, P(19, 11).Y), true, 0.8));
                items.Add(Shape(new PathBuilder().Rectangle(P(15, 5.5).X, P(15, 5.5).Y, P(17, 9).X, P(17, 9).Y), true, 0.8));
                items.Add(Shape(new PathBuilder().Rectangle(P(12, 6.5).X, P(12, 6.5).Y, P(13.5, 9).X, P(13.5, 9).Y), true, 0.8));
                items.Add(Shape(new PathBuilder().Ellipse(P(4.5, 8.5).X, P(4.5, 8.5).Y, P(7.5, 11.5).X, P(7.5, 11.5).Y), true, 0.6, Rgb.White));
                break;
            case NoteIcon.Help:
                items.Add(Shape(new PathBuilder().Ellipse(P(1, 1).X, P(1, 1).Y, P(19, 19).X, P(19, 19).Y), true, 0.8));
                var question = new PathBuilder().Move(P(7, 13.2)).Curve(P(7, 17).X, P(7, 17).Y, P(13, 17).X, P(13, 17).Y, P(13, 13.4).X, P(13, 13.4).Y)
                    .Curve(P(13, 11.2).X, P(13, 11.2).Y, P(10, 11).X, P(10, 11).Y, P(10, 8.6).X, P(10, 8.6).Y);
                items.Add(new ScenePath(question.Segments, PaintMode.Stroke, null, Ink, Stroke(1.8), CapStyle.Round, JoinStyle.Round));
                items.Add(new ScenePath(new PathBuilder().Ellipse(P(9.1, 4.8).X, P(9.1, 4.8).Y, P(10.9, 6.6).X, P(10.9, 6.6).Y).Segments, PaintMode.Fill, Ink, null, 0));
                break;
            case NoteIcon.Paragraph:
                items.Add(Shape(RoundedBox(P(1, 1), P(19, 19), 3 * Math.Min(sx, sy)), true, 0.8));
                AddPilcrow(items, P, Stroke, 0);
                break;
            case NoteIcon.NewParagraph:
                items.Add(Shape(RoundedBox(P(1, 1), P(19, 19), 3 * Math.Min(sx, sy)), true, 0.8));
                AddPilcrow(items, P, Stroke, 2);
                items.Add(new ScenePath(new PathBuilder().Move(P(4.5, 3.5)).Line(P(8, 6.5)).Line(P(11.5, 3.5)).Segments, PaintMode.Stroke, null, Ink, Stroke(1.2), CapStyle.Round, JoinStyle.Round));
                break;
            case NoteIcon.Insert:
                var caret = new PathBuilder().Move(P(2, 3)).Curve(P(8, 3).X, P(8, 3).Y, P(9, 10).X, P(9, 10).Y, P(10, 17).X, P(10, 17).Y)
                    .Curve(P(11, 10).X, P(11, 10).Y, P(12, 3).X, P(12, 3).Y, P(18, 3).X, P(18, 3).Y).Close();
                items.Add(Shape(caret, true, 0.8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(icon));
        }

        return items;
    }

    private static void AddPilcrow(List<ScenePath> items, Func<double, double, PointD> p, Func<double, double> stroke, double lift)
    {
        var bowl = new PathBuilder().Ellipse(p(5, 10 + lift).X, p(5, 10 + lift).Y, p(13, 17 + lift).X, p(13, 17 + lift).Y);
        items.Add(new ScenePath(bowl.Segments, PaintMode.Fill, Ink, null, 0));
        items.Add(new ScenePath(new PathBuilder().Move(p(9, 5 + lift)).Line(p(9, 17.5 + lift)).Segments, PaintMode.Stroke, null, Ink, stroke(1.6), CapStyle.Butt));
        items.Add(new ScenePath(new PathBuilder().Move(p(13, 5 + lift)).Line(p(13, 17.5 + lift)).Segments, PaintMode.Stroke, null, Ink, stroke(1.6), CapStyle.Butt));
        items.Add(new ScenePath(new PathBuilder().Move(p(9, 17.2 + lift)).Line(p(16, 17.2 + lift)).Segments, PaintMode.Stroke, null, Ink, stroke(1.6), CapStyle.Butt));
    }

    private static PathBuilder RoundedBox(PointD a, PointD b, double radius) => new PathBuilder().RoundedRectangle(a.X, a.Y, b.X, b.Y, radius);
}
