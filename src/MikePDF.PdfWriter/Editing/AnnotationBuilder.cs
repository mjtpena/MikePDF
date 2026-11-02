using System.Globalization;
using System.Text;

namespace MikePDF.PdfWriter.Editing;

/// <summary>
/// Builds the dictionary and the appearance stream of a new annotation from the model's values (ADR-0008 decision 1: the writer owns every annotation
/// dictionary and appearance stream MikePDF creates; PDFium writes no appearance for most kinds, S4). The appearance is drawn in default user space with
/// the form's <c>/BBox</c> equal to the annotation's <c>/Rect</c> and an identity <c>/Matrix</c>, so a reader that places it by the usual algorithm puts it
/// exactly on the rectangle.
/// </summary>
internal static class AnnotationBuilder
{
    private const double Kappa = 0.5522847498307936;

    public static (PdfStream Appearance, PdfDictionary Annotation) Build(AnnotationChange change, PdfReference page, DateTimeOffset modified)
    {
        var rect = change.Rect!;
        var color = change.Color!;
        var subtype = change.Annotation switch
        {
            NewAnnotationKind.Highlight => "Highlight",
            NewAnnotationKind.Underline => "Underline",
            NewAnnotationKind.StrikeOut => "StrikeOut",
            NewAnnotationKind.Text => "Text",
            NewAnnotationKind.Ink => "Ink",
            NewAnnotationKind.Square => "Square",
            NewAnnotationKind.Circle => "Circle",
            _ => throw new ArgumentOutOfRangeException(nameof(change), "unknown annotation kind"),
        };

        var content = new StringBuilder();
        var multiply = change.Annotation == NewAnnotationKind.Highlight;
        var width = change.Width;
        var rg = Colour(color, "rg");
        var stroke = Colour(color, "RG");
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Type", new PdfName("Annot")),
            Entry("Subtype", new PdfName(subtype)),
            Entry("Rect", Numbers(rect)),
            Entry("P", page),
            Entry("NM", Text(change.Name!)),
            Entry("M", PdfString.FromAscii("D:" + modified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z00'00'")),
            Entry("C", Numbers(color)),
            Entry("CA", new PdfReal(change.Opacity)),
        };
        if (!string.IsNullOrEmpty(change.Author))
        {
            entries.Add(Entry("T", Text(change.Author)));
        }

        if (!string.IsNullOrEmpty(change.Contents))
        {
            entries.Add(Entry("Contents", Text(change.Contents)));
        }

        // F: Print (4); a note icon also keeps its size and orientation (NoZoom 8, NoRotate 16).
        var defaultFlags = change.Annotation == NewAnnotationKind.Text ? 28u : 4u;
        entries.Add(Entry("F", new PdfInteger(change.Flags == 0 ? defaultFlags : change.Flags)));

        switch (change.Annotation)
        {
            case NewAnnotationKind.Highlight:
                content.Append(rg).Append('\n');
                foreach (var q in Quads(change.QuadPoints!))
                {
                    // The quadrilateral's corners are top left, top right, bottom left, bottom right; filled around the outline.
                    Move(content, q[0], q[1]);
                    Line(content, q[2], q[3]);
                    Line(content, q[6], q[7]);
                    Line(content, q[4], q[5]);
                    content.Append("h f\n");
                }

                entries.Add(Entry("QuadPoints", Numbers(change.QuadPoints!)));
                break;
            case NewAnnotationKind.Underline:
            case NewAnnotationKind.StrikeOut:
                content.Append(stroke).Append(' ').Append(F(width)).Append(" w\n");
                foreach (var q in Quads(change.QuadPoints!))
                {
                    if (change.Annotation == NewAnnotationKind.Underline)
                    {
                        Move(content, q[4], q[5]);
                        Line(content, q[6], q[7]);
                    }
                    else
                    {
                        Move(content, (q[0] + q[4]) / 2, (q[1] + q[5]) / 2);
                        Line(content, (q[2] + q[6]) / 2, (q[3] + q[7]) / 2);
                    }

                    content.Append("S\n");
                }

                entries.Add(Entry("QuadPoints", Numbers(change.QuadPoints!)));
                break;
            case NewAnnotationKind.Text:
                // A small note: a filled square with a border and three lines of "text".
                var (l, b, r, t) = (rect[0], rect[1], rect[2], rect[3]);
                content.Append(rg).Append('\n');
                content.Append(F(l)).Append(' ').Append(F(b)).Append(' ').Append(F(r - l)).Append(' ').Append(F(t - b)).Append(" re f\n");
                content.Append("0 0 0 RG 0.5 w\n");
                content.Append(F(l + 0.25)).Append(' ').Append(F(b + 0.25)).Append(' ').Append(F(r - l - 0.5)).Append(' ').Append(F(t - b - 0.5)).Append(" re S\n");
                for (var i = 1; i <= 3; i++)
                {
                    var y = b + ((t - b) * (0.25 * i));
                    Move(content, l + ((r - l) * 0.2), y);
                    Line(content, r - ((r - l) * 0.2), y);
                    content.Append("S\n");
                }

                entries.Add(Entry("Name", new PdfName("Note")));
                entries.Add(Entry("Open", PdfBoolean.False));
                break;
            case NewAnnotationKind.Ink:
                content.Append(stroke).Append(' ').Append(F(width)).Append(" w 1 J 1 j\n");
                var inkList = new List<PdfObject>();
                foreach (var points in change.Strokes!)
                {
                    Move(content, points[0], points[1]);
                    for (var i = 2; i < points.Length; i += 2)
                    {
                        Line(content, points[i], points[i + 1]);
                    }

                    content.Append("S\n");
                    inkList.Add(Numbers(points));
                }

                entries.Add(Entry("InkList", new PdfArray(inkList)));
                entries.Add(Entry("BS", Dictionary(("W", new PdfReal(width)))));
                break;
            case NewAnnotationKind.Square:
            case NewAnnotationKind.Circle:
                content.Append(stroke).Append(' ').Append(F(width)).Append(" w\n");
                var half = width / 2;
                var (x0, y0, x1, y1) = (rect[0] + half, rect[1] + half, rect[2] - half, rect[3] - half);
                if (change.Annotation == NewAnnotationKind.Square)
                {
                    content.Append(F(x0)).Append(' ').Append(F(y0)).Append(' ').Append(F(x1 - x0)).Append(' ').Append(F(y1 - y0)).Append(" re S\n");
                }
                else
                {
                    Ellipse(content, (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, (y1 - y0) / 2);
                }

                entries.Add(Entry("BS", Dictionary(("W", new PdfReal(width)), ("S", new PdfName("S")))));
                break;
        }

        var graphicsState = Dictionary(
            ("Type", new PdfName("ExtGState")),
            ("CA", new PdfReal(change.Opacity)),
            ("ca", new PdfReal(change.Opacity)),
            ("BM", new PdfName(multiply ? "Multiply" : "Normal")));
        var resources = Dictionary(("ExtGState", Dictionary(("GS", graphicsState))));
        var body = "/GS gs\n" + content;
        var form = Dictionary(
            ("Type", new PdfName("XObject")),
            ("Subtype", new PdfName("Form")),
            ("BBox", Numbers(rect)),
            ("Resources", resources));
        return (new PdfStream(form, Encoding.ASCII.GetBytes(body)), new PdfDictionary(entries));
    }

    /// <summary>A PDF text string: ASCII as it is, anything else as UTF-16 big-endian with the byte order mark (7.9.2.2).</summary>
    internal static PdfString Text(string text)
    {
        if (text.All(static c => c is >= ' ' and <= '~'))
        {
            return PdfString.FromAscii(text);
        }

        var bytes = new byte[2 + (text.Length * 2)];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text, bytes.AsSpan(2));
        return new PdfString(bytes, PdfStringFormat.Literal);
    }

    private static string F(double value) => PdfSerializer.FormatReal(value);

    private static string Colour(float[] rgb, string op) => $"{F(rgb[0])} {F(rgb[1])} {F(rgb[2])} {op}";

    private static void Move(StringBuilder content, double x, double y) => content.Append(F(x)).Append(' ').Append(F(y)).Append(" m\n");

    private static void Line(StringBuilder content, double x, double y) => content.Append(F(x)).Append(' ').Append(F(y)).Append(" l\n");

    private static IEnumerable<float[]> Quads(float[] points)
    {
        for (var i = 0; i + 8 <= points.Length; i += 8)
        {
            yield return points[i..(i + 8)];
        }
    }

    private static void Ellipse(StringBuilder content, double cx, double cy, double rx, double ry)
    {
        var kx = rx * Kappa;
        var ky = ry * Kappa;
        Move(content, cx + rx, cy);
        Curve(content, cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        Curve(content, cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        Curve(content, cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        Curve(content, cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        content.Append("h S\n");
    }

    private static void Curve(StringBuilder content, double x1, double y1, double x2, double y2, double x3, double y3) =>
        content.Append(F(x1)).Append(' ').Append(F(y1)).Append(' ').Append(F(x2)).Append(' ').Append(F(y2)).Append(' ').Append(F(x3)).Append(' ').Append(F(y3)).Append(" c\n");

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(new PdfName(key), value);

    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] entries) =>
        new(entries.Select(static e => new KeyValuePair<PdfName, PdfObject>(new PdfName(e.Key), e.Value)));

    private static PdfArray Numbers(float[] values) => new(values.Select(static v => (PdfObject)new PdfReal(v)));
}
