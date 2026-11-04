using System.Globalization;
using System.Text;
using MikePDF.Core.Geometry;
using MikePDF.Markup;

namespace MikePDF.PdfWriter.Editing;

/// <summary>What <see cref="AnnotationBuilder.Build"/> made: the appearance stream, the annotation dictionary (without <c>/AP</c>, <c>/Popup</c> and <c>/IRT</c>) and the popup's dictionary.</summary>
internal sealed record BuiltAnnotation(PdfStream Appearance, PdfDictionary Annotation, PdfDictionary? Popup);

/// <summary>
/// Builds the dictionary and the appearance stream of a new annotation from the model's values (ADR-0008 decision 1: the writer owns every annotation
/// dictionary and appearance stream MikePDF creates; PDFium writes no appearance for most kinds, S4). The geometry and the drawing come from
/// <c>MikePDF.Markup</c> (the same code the overlay uses); this class only turns its <see cref="AnnotationSpec"/> into PDF objects. The appearance is
/// drawn in the form's own space, which the form's <c>/BBox</c> and <c>/Matrix</c> map onto the annotation's <c>/Rect</c>.
/// </summary>
internal static class AnnotationBuilder
{
    public static BuiltAnnotation Build(AnnotationChange change, PdfReference page, DateTimeOffset modified, int pageRotation)
    {
        AnnotationSpec spec;
        try
        {
            spec = AnnotationSpecBuilder.Build(ToModel(change, modified, pageRotation), pageRotation);
        }
        catch (Exception e) when (e is not PdfParseException)
        {
            // Whatever the values were, the host must refuse the edit, not die: the model code throws for values it cannot draw.
            ParseContext.Fatal(PdfErrorCode.InvalidEdit, "an annotation's appearance cannot be generated: " + e.Message);
            throw;
        }

        var dictionary = ToDictionary(spec.Dictionary, page);
        var popup = spec.Popup is { } p ? ToDictionary(p, page) : null;
        return new BuiltAnnotation(ToStream(spec.Appearance), dictionary, popup);
    }

    /// <summary>The model's view of an edit: the values the Markup library draws from.</summary>
    internal static Annotation ToModel(AnnotationChange c, DateTimeOffset modified, int pageRotation = 0)
    {
        var rect = c.Rect!;
        var colour = c.Color!;
        var interior = c.InteriorColor is { Length: 3 } ic ? Rgb.FromFloats(ic) : (Rgb?)null;
        var kind = KindOf(c);
        AnnotationPayload payload = c.Annotation switch
        {
            NewAnnotationKind.Highlight or NewAnnotationKind.Underline or NewAnnotationKind.StrikeOut or NewAnnotationKind.Squiggly =>
                new TextMarkupPayload(Quads(c.QuadPoints!)),
            NewAnnotationKind.Text => new NotePayload(Enum.TryParse<NoteIcon>(c.StandardName, out var icon) && Enum.IsDefined(icon) ? icon : NoteIcon.Comment),
            NewAnnotationKind.Ink => new InkPayload(
                [.. c.Strokes!.Select((s, i) => new InkStrokeData(s, c.InkWidths is not null && i < c.InkWidths.Count ? c.InkWidths[i] : Enumerable.Repeat(c.Width, s.Length / 2).ToArray()))],
                (c.Traits & 2) != 0),
            NewAnnotationKind.Square or NewAnnotationKind.Circle => new BoxPayload(),
            NewAnnotationKind.FreeText => new FreeTextPayload(
                (FontFamily)(c.FontId / 4),
                (c.FontId & 1) != 0,
                (c.FontId & 2) != 0,
                c.FontSize,
                c.TextColor is { Length: 3 } tc ? Rgb.FromFloats(tc) : Rgb.Black,
                (TextAlign)c.Align,
                (c.Traits & 1) != 0,
                pageRotation),
            NewAnnotationKind.Line => new LinePayload(
                new PointD(c.Vertices![0], c.Vertices[1]),
                new PointD(c.Vertices[2], c.Vertices[3]),
                (LineEnding)c.LineStart,
                (LineEnding)c.LineEnd),
            NewAnnotationKind.Polygon or NewAnnotationKind.PolyLine => new PolyPayload(Points(c.Vertices!), (LineEnding)c.LineStart, (LineEnding)c.LineEnd),
            NewAnnotationKind.Stamp => new StampPayload(c.StandardName!, (int)((c.Traits >> 2) & 3)),
            _ => throw new ArgumentOutOfRangeException(nameof(c), "unknown annotation kind"),
        };
        return new Annotation
        {
            Id = c.Name!,
            Kind = kind,
            Page = c.Page,
            Rect = new PdfRect(rect[0], rect[1], rect[2], rect[3]),
            Color = Rgb.FromFloats(colour),
            Opacity = c.Opacity,
            Width = c.Width,
            Interior = interior,
            Dash = (DashStyle)c.Dash,
            Contents = c.Contents ?? string.Empty,
            Author = c.Author ?? string.Empty,
            Subject = c.Subject ?? string.Empty,
            Created = PdfDates.TryParse(c.Created, out var created) ? created : default,
            Modified = PdfDates.TryParse(c.Modified, out var m) ? m : modified,
            Flags = c.Flags,
            Parent = c.ParentName,
            ReplyKind = (ReplyKind)c.ReplyKind,
            State = (ReviewState)c.State,
            PopupRect = c.PopupRect is { Length: 4 } pr ? new PdfRect(pr[0], pr[1], pr[2], pr[3]) : null,
            Payload = payload,
        };
    }

    private static MarkupKind KindOf(AnnotationChange c) => c.Annotation switch
    {
        NewAnnotationKind.Highlight => MarkupKind.Highlight,
        NewAnnotationKind.Underline => MarkupKind.Underline,
        NewAnnotationKind.StrikeOut => MarkupKind.StrikeOut,
        NewAnnotationKind.Squiggly => MarkupKind.Squiggly,
        NewAnnotationKind.Text => MarkupKind.Note,
        NewAnnotationKind.Ink => MarkupKind.Ink,
        NewAnnotationKind.Square => MarkupKind.Rectangle,
        NewAnnotationKind.Circle => MarkupKind.Ellipse,
        NewAnnotationKind.FreeText => MarkupKind.FreeText,
        NewAnnotationKind.Line => c.LineStart == 0 && c.LineEnd == 1 ? MarkupKind.Arrow : MarkupKind.Line,
        NewAnnotationKind.Polygon => MarkupKind.Polygon,
        NewAnnotationKind.PolyLine => MarkupKind.PolyLine,
        NewAnnotationKind.Stamp => MarkupKind.Stamp,
        _ => throw new ArgumentOutOfRangeException(nameof(c), "unknown annotation kind"),
    };

    private static IReadOnlyList<Quad> Quads(float[] n) =>
        [.. Enumerable.Range(0, n.Length / 8).Select(i => Quad.FromNumbers(n.Select(static v => (double)v).Skip(i * 8).Take(8).ToArray()))];

    private static IReadOnlyList<PointD> Points(float[] n) => [.. Enumerable.Range(0, n.Length / 2).Select(i => new PointD(n[2 * i], n[(2 * i) + 1]))];

    private static PdfDictionary ToDictionary(SpecMap map, PdfReference page) =>
        new(map.Entries.Select(e => new KeyValuePair<PdfName, PdfObject>(new PdfName(e.Key), ToObject(e.Value, page))));

    private static PdfObject ToObject(SpecValue value, PdfReference page) => value switch
    {
        SpecName n => new PdfName(n.Value),
        SpecInt i => new PdfInteger(i.Value),
        SpecReal r => new PdfReal(Math.Round(r.Value, 4)),
        SpecText t => Text(t.Value),
        SpecDate d => PdfString.FromAscii(d.Value),
        SpecBool b => PdfBoolean.From(b.Value),
        SpecArray a => new PdfArray(a.Items.Select(item => ToObject(item, page))),
        SpecMap m => ToDictionary(m, page),
        SpecPageReference => page,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PdfStream ToStream(AppearanceForm form)
    {
        var graphicsState = Dictionary(
            ("Type", new PdfName("ExtGState")),
            ("CA", new PdfReal(form.Opacity)),
            ("ca", new PdfReal(form.Opacity)),
            ("BM", new PdfName(form.Multiply ? "Multiply" : "Normal")));
        var resources = new List<(string, PdfObject)> { ("ExtGState", Dictionary((AppearanceForm.GraphicsStateName, graphicsState))) };
        if (form.Fonts.Count > 0)
        {
            resources.Add(("Font", Dictionary([.. form.Fonts.Select(f => (f.ResourceName, (PdfObject)Dictionary(
                ("Type", new PdfName("Font")),
                ("Subtype", new PdfName("Type1")),
                ("BaseFont", new PdfName(f.BaseFont)),
                ("Encoding", new PdfName("WinAnsiEncoding")))))])));
        }

        var entries = new List<(string, PdfObject)>
        {
            ("Type", new PdfName("XObject")),
            ("Subtype", new PdfName("Form")),
            ("BBox", Numbers([form.BBox.Left, form.BBox.Bottom, form.BBox.Right, form.BBox.Top])),
            ("Resources", Dictionary([.. resources])),
        };
        if (form.Matrix.Count == 6 && !(form.Matrix[0] == 1 && form.Matrix[1] == 0 && form.Matrix[2] == 0 && form.Matrix[3] == 1 && form.Matrix[4] == 0 && form.Matrix[5] == 0))
        {
            entries.Add(("Matrix", Numbers([.. form.Matrix])));
        }

        if (form.TransparencyGroup)
        {
            entries.Add(("Group", Dictionary(("S", new PdfName("Transparency")), ("K", PdfBoolean.True))));
        }

        return new PdfStream(Dictionary([.. entries]), Encoding.ASCII.GetBytes(form.Content));
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

    /// <summary>The <c>/M</c> value for an update the writer makes itself.</summary>
    internal static string ModifiedText(DateTimeOffset modified) =>
        "D:" + modified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z00'00'";

    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] entries) =>
        new(entries.Select(static e => new KeyValuePair<PdfName, PdfObject>(new PdfName(e.Key), e.Value)));

    private static PdfArray Numbers(double[] values) => new(values.Select(static v => (PdfObject)new PdfReal(Math.Round(v, 4))));
}
