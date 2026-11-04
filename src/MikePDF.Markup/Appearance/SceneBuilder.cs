using MikePDF.Core.Geometry;
using MikePDF.Ink;
using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>Thicknesses, wave constants and defaults the specs fix (text-markup 2.1, shapes 3.2, sticky-notes 2.1).</summary>
public static class MarkupDefaults
{
    public const double MinThickness = 0.5;
    public const double MaxThickness = 4;
    public const double MinOpacity = 0.1;

    /// <summary>The wave of a squiggly underline at thickness 1: a wavelength of 3 points and an amplitude of 1 point, both scaled with the thickness (text-markup 2.1; the scaling of the wavelength is a reading of the spec, see the deviations).</summary>
    public const double SquigglyWavelength = 3;

    public const double SquigglyAmplitude = 1;

    public const uint FlagPrint = 4;
    public const uint FlagNoZoom = 8;
    public const uint FlagNoRotate = 16;

    /// <summary>Print for every owned annotation; notes also keep their size and orientation (annotation-core 3.5).</summary>
    public static uint DefaultFlags(MarkupKind kind) => kind == MarkupKind.Note ? FlagPrint | FlagNoZoom | FlagNoRotate : FlagPrint;

    public static uint EffectiveFlags(Annotation a) => a.Flags == 0 ? DefaultFlags(a.Kind) : a.Flags;

    /// <summary>The tool's name written to <c>/Subj</c> (annotation-core 3.5).</summary>
    public static string SubjectFor(MarkupKind kind) => kind switch
    {
        MarkupKind.Highlight => "Highlight",
        MarkupKind.Underline => "Underline",
        MarkupKind.StrikeOut => "Strikethrough",
        MarkupKind.Squiggly => "Squiggly Underline",
        MarkupKind.Note => "Sticky Note",
        MarkupKind.FreeText => "Text Box",
        MarkupKind.Ink => "Drawing",
        MarkupKind.Rectangle => "Rectangle",
        MarkupKind.Ellipse => "Ellipse",
        MarkupKind.Line => "Line",
        MarkupKind.Arrow => "Arrow",
        MarkupKind.Polygon => "Polygon",
        MarkupKind.PolyLine => "Polyline",
        MarkupKind.Stamp => "Stamp",
        _ => string.Empty,
    };
}

/// <summary>
/// Turns an annotation into the <see cref="Scene"/> both the overlay and the appearance stream draw (annotation-core 3.3 "shared geometry"). It
/// never reads an existing appearance: it draws only from the model's values (annotation-core 3.2 "hostile owned claims").
/// </summary>
public static class SceneBuilder
{
    /// <summary>The scene of an annotation on a page that is turned <paramref name="pageRotation"/> degrees clockwise (0, 90, 180 or 270).</summary>
    public static Scene Build(Annotation a, int pageRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(a);
        var rotation = ((pageRotation % 360) + 360) % 360;
        if (rotation % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageRotation));
        }

        var rect = a.Rect;
        var group = a.Opacity < 1;
        if (a.ReplyKind != ReplyKind.None)
        {
            // A reply or a review state is not drawn: its appearance is an empty form (OQ-SN-2, proposal).
            return new Scene([], rect, Scene.Identity, 1, false, false);
        }

        return a.Payload switch
        {
            TextMarkupPayload markup => TextMarkup(a, markup, rect),
            NotePayload note => new Scene(NoteArtwork.Items(note.Icon, rect, a.Color).Cast<SceneItem>().ToList(), rect, Scene.Identity, a.Opacity, false, false),
            FreeTextPayload text => FreeText(a, text, rotation),
            BoxPayload => Box(a, rect, group),
            LinePayload line => Line(a, line, group),
            PolyPayload poly => Poly(a, poly, group),
            StampPayload stamp => Stamp(a, stamp, rotation),
            InkPayload ink => Ink(a, ink),
            _ => throw new InvalidOperationException("Unknown payload."),
        };
    }

    private static Scene TextMarkup(Annotation a, TextMarkupPayload markup, PdfRect rect)
    {
        var items = new List<SceneItem>();
        var w = a.Width;
        foreach (var q in markup.Quads)
        {
            switch (a.Kind)
            {
                case MarkupKind.Highlight:
                    items.Add(new ScenePath(new PathBuilder().Polygon(q.Outline()).Segments, PaintMode.Fill, a.Color, null, 0));
                    break;
                case MarkupKind.Underline:
                {
                    var up = Geo.Unit(Geo.Sub(q.TopLeft, q.BottomLeft));
                    var inset = Geo.Scale(up, w / 2);
                    items.Add(Stroked(new PathBuilder().Move(Geo.Add(q.BottomLeft, inset)).Line(Geo.Add(q.BottomRight, inset)).Segments, a.Color, w, CapStyle.Butt, JoinStyle.Miter));
                    break;
                }

                case MarkupKind.StrikeOut:
                    items.Add(Stroked(new PathBuilder().Move(Geo.Mid(q.TopLeft, q.BottomLeft)).Line(Geo.Mid(q.TopRight, q.BottomRight)).Segments, a.Color, w, CapStyle.Butt, JoinStyle.Miter));
                    break;
                case MarkupKind.Squiggly:
                    items.Add(Stroked(new PathBuilder().Polyline(SquigglyPoints(q, w)).Segments, a.Color, w, CapStyle.Round, JoinStyle.Miter));
                    break;
                default:
                    throw new InvalidOperationException("Not a text markup.");
            }
        }

        return new Scene(items, rect, Scene.Identity, a.Opacity, a.Kind == MarkupKind.Highlight, false);
    }

    /// <summary>The zigzag of a squiggly underline along the bottom edge of a quad: points half a wavelength apart, alternating above and below the line, which sits inside the quad.</summary>
    public static IReadOnlyList<PointD> SquigglyPoints(Quad q, double thickness)
    {
        var amplitude = MarkupDefaults.SquigglyAmplitude * thickness;
        var step = MarkupDefaults.SquigglyWavelength * thickness / 2;
        var along = Geo.Sub(q.BottomRight, q.BottomLeft);
        var length = Geo.Length(along);
        var u = Geo.Unit(along);
        var up = Geo.Unit(Geo.Sub(q.TopLeft, q.BottomLeft));
        var baseline = Geo.Add(q.BottomLeft, Geo.Scale(up, (thickness / 2) + amplitude));
        var points = new List<PointD>();
        var count = Math.Max(1, (int)Math.Ceiling(length / step));
        count = Math.Min(count, 20_000);
        for (var i = 0; i <= count; i++)
        {
            var distance = i == count ? length : i * step;
            var offset = i % 2 == 0 ? -amplitude : amplitude;
            points.Add(Geo.Add(Geo.Add(baseline, Geo.Scale(u, distance)), Geo.Scale(up, offset)));
        }

        return points;
    }

    private static Scene Box(Annotation a, PdfRect rect, bool group)
    {
        var w = a.Width;
        var inset = Geo.Inflate(rect, -w / 2);
        if (!(inset.Right > inset.Left) || !(inset.Top > inset.Bottom))
        {
            inset = rect;
        }

        var path = a.Kind == MarkupKind.Ellipse
            ? new PathBuilder().Ellipse(inset.Left, inset.Bottom, inset.Right, inset.Top)
            : new PathBuilder().Rectangle(inset.Left, inset.Bottom, inset.Right, inset.Top);
        var dash = ShapeGeometry.DashArray(a.Dash, w);
        var paint = (a.Interior, w > 0) switch
        {
            (not null, true) => PaintMode.FillAndStroke,
            (not null, false) => PaintMode.Fill,
            _ => PaintMode.Stroke,
        };
        if (a.Interior is null && w <= 0)
        {
            return new Scene([], rect, Scene.Identity, a.Opacity, false, group);
        }

        var item = new ScenePath(path.Segments, paint, a.Interior, w > 0 ? a.Color : null, w, ShapeGeometry.CapFor(a.Dash), JoinStyle.Miter, ShapeGeometry.MiterLimit, dash);
        return new Scene([item], rect, Scene.Identity, a.Opacity, false, group);
    }

    private static Scene Line(Annotation a, LinePayload line, bool group) =>
        Open(a, [line.Start, line.End], line.StartEnding, line.EndEnding, group);

    private static Scene Poly(Annotation a, PolyPayload poly, bool group)
    {
        if (a.Kind != MarkupKind.Polygon)
        {
            return Open(a, poly.Vertices, poly.StartEnding, poly.EndEnding, group);
        }

        var w = a.Width;
        var paint = (a.Interior, w > 0) switch
        {
            (not null, true) => PaintMode.FillAndStroke,
            (not null, false) => PaintMode.Fill,
            _ => PaintMode.Stroke,
        };
        var item = new ScenePath(new PathBuilder().Polygon(poly.Vertices).Segments, paint, a.Interior, w > 0 ? a.Color : null, w, ShapeGeometry.CapFor(a.Dash), JoinStyle.Miter, ShapeGeometry.MiterLimit, ShapeGeometry.DashArray(a.Dash, w));
        return new Scene([item], a.Rect, Scene.Identity, a.Opacity, false, group);
    }

    private static Scene Open(Annotation a, IReadOnlyList<PointD> vertices, LineEnding start, LineEnding end, bool group)
    {
        var w = a.Width;
        var (points, heads, _) = ShapeGeometry.WithEnds(vertices, start, end, w, a.Color, a.Interior);
        var items = new List<SceneItem>
        {
            new ScenePath(new PathBuilder().Polyline(points).Segments, PaintMode.Stroke, null, a.Color, w, ShapeGeometry.CapFor(a.Dash), JoinStyle.Miter, ShapeGeometry.MiterLimit, ShapeGeometry.DashArray(a.Dash, w)),
        };
        items.AddRange(heads);
        return new Scene(items, a.Rect, Scene.Identity, a.Opacity, false, group);
    }

    private static Scene Ink(Annotation a, InkPayload ink)
    {
        var result = InkAppearance.Build([.. ink.Strokes.Select(static s => new SimplifiedStroke(s.Xy, s.Widths))]);
        var items = result.Paths.Select(p => (SceneItem)new ScenePath(p.Segments, PaintMode.Fill, a.Color, null, 0)).ToList();
        return new Scene(items, a.Rect, Scene.Identity, a.Opacity, ink.Highlighter, false);
    }

    private static Scene FreeText(Annotation a, FreeTextPayload text, int pageRotation)
    {
        var rect = a.Rect;
        var odd = pageRotation % 180 != 0;
        var width = odd ? rect.Height : rect.Width;
        var height = odd ? rect.Width : rect.Height;
        var border = text.Typewriter ? 0 : a.Width;
        PdfRect bbox;
        IReadOnlyList<double> matrix;
        double ox, oy;
        if (pageRotation == 0)
        {
            bbox = rect;
            matrix = Scene.Identity;
            ox = rect.Left;
            oy = rect.Bottom;
        }
        else
        {
            bbox = new PdfRect(0, 0, width, height);
            matrix = Frame.Matrix(rect, width, height, pageRotation);
            ox = 0;
            oy = 0;
        }

        var items = new List<SceneItem>();
        if (!text.Typewriter && a.Interior is { } fill)
        {
            items.Add(new ScenePath(new PathBuilder().Rectangle(ox, oy, ox + width, oy + height).Segments, PaintMode.Fill, fill, null, 0));
        }

        if (border > 0)
        {
            var half = border / 2;
            items.Add(new ScenePath(new PathBuilder().Rectangle(ox + half, oy + half, ox + width - half, oy + height - half).Segments, PaintMode.Stroke, null, a.Color, border, CapStyle.Butt, JoinStyle.Miter));
        }

        var fontId = StandardFonts.FontId(text.Family, text.Bold, text.Italic);
        var inner = TextLayout.InnerWidth(width, border);
        var layout = TextLayout.Layout(a.Contents, fontId, text.Size, inner, text.Align);
        var inset = border + TextLayout.Padding;
        foreach (var line in layout.Lines)
        {
            if (line.Text.Length > 0)
            {
                items.Add(new SceneText(line.Text, fontId, text.Size, text.TextColor, ox + inset + line.X, oy + height - inset - line.Baseline));
            }
        }

        return new Scene(items, bbox, matrix, a.Opacity, false, a.Opacity < 1);
    }

    private static Scene Stamp(Annotation a, StampPayload stamp, int pageRotation)
    {
        if (!StampCatalog.IsStandard(stamp.Name))
        {
            throw new InvalidOperationException("Not a standard stamp name.");
        }

        var (w, h) = StampArtwork.NativeSize(stamp.Name);
        var turns = ((stamp.UserTurns % 4) + 4) % 4;
        var net = (((pageRotation - (90 * turns)) % 360) + 360) % 360;
        // The enclosing rectangle is the annotation's rectangle; the form is native-sized and the reader scales it onto the rectangle.
        var odd = net % 180 != 0;
        var frame = Frame.Matrix(a.Rect, odd ? h : w, odd ? w : h, net, w, h);
        return new Scene(StampArtwork.Items(stamp.Name), new PdfRect(0, 0, w, h), frame, a.Opacity, false, false);
    }

    private static ScenePath Stroked(IReadOnlyList<PathSegment> segments, Rgb colour, double width, CapStyle cap, JoinStyle join) =>
        new(segments, PaintMode.Stroke, null, colour, width, cap, join, ShapeGeometry.MiterLimit);
}

/// <summary>The form matrix that turns a form's content to make it upright on a rotated page and lands the form's bounding box on a rectangle.</summary>
public static class Frame
{
    /// <summary>
    /// The matrix for a form of size <paramref name="formWidth"/> by <paramref name="formHeight"/> (its <c>/BBox</c> is <c>[0 0 w h]</c>) turned
    /// <paramref name="ccwDegrees"/> counter-clockwise (a multiple of 90), translated so that the turned box starts at the rectangle's lower left.
    /// </summary>
    public static IReadOnlyList<double> Matrix(PdfRect rect, double enclosingWidth, double enclosingHeight, int ccwDegrees, double? formWidth = null, double? formHeight = null)
    {
        var fw = formWidth ?? enclosingWidth;
        var fh = formHeight ?? enclosingHeight;
        var (cos, sin) = (((ccwDegrees % 360) + 360) % 360) switch
        {
            90 => (0.0, 1.0),
            180 => (-1.0, 0.0),
            270 => (0.0, -1.0),
            _ => (1.0, 0.0),
        };
        // Corners of the form through the rotation (no translation yet): x' = x cos - y sin, y' = x sin + y cos.
        var xs = new[] { 0, (fw * cos), (-fh * sin), (fw * cos) - (fh * sin) };
        var ys = new[] { 0, (fw * sin), (fh * cos), (fw * sin) + (fh * cos) };
        return [cos, sin, -sin, cos, rect.Left - xs.Min(), rect.Bottom - ys.Min()];
    }
}
