using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>A value in an annotation dictionary, before it becomes a PDF object. Closed set, so the writer's mapping is exhaustive.</summary>
public abstract record SpecValue;

public sealed record SpecName(string Value) : SpecValue;

public sealed record SpecInt(long Value) : SpecValue;

public sealed record SpecReal(double Value) : SpecValue;

/// <summary>A PDF text string: PDFDocEncoding when the text allows it, else UTF-16BE with a byte order mark (7.9.2.2).</summary>
public sealed record SpecText(string Value) : SpecValue;

/// <summary>A PDF date string (ASCII).</summary>
public sealed record SpecDate(string Value) : SpecValue;

public sealed record SpecBool(bool Value) : SpecValue;

public sealed record SpecArray(IReadOnlyList<SpecValue> Items) : SpecValue;

public sealed record SpecMap(IReadOnlyList<KeyValuePair<string, SpecValue>> Entries) : SpecValue
{
    public SpecValue? this[string key] => Entries.FirstOrDefault(e => e.Key == key).Value;
}

/// <summary>The page object reference (<c>/P</c>). The writer knows the page; the model does not.</summary>
public sealed record SpecPageReference : SpecValue;

/// <summary>An annotation as the writer needs it: the dictionary (without <c>/AP</c>, <c>/IRT</c> and <c>/Popup</c>, which need object numbers), the appearance, and the popup.</summary>
public sealed record AnnotationSpec(string Subtype, SpecMap Dictionary, AppearanceForm Appearance, SpecMap? Popup);

/// <summary>
/// Builds the annotation dictionary and appearance of an annotation (annotation-core 3.9, and the "Save behaviour" section of each kind's spec). This
/// is the one place that knows which keys each kind carries; <c>MikePDF.PdfWriter</c> maps the result to PDF objects and adds the references.
/// </summary>
public static class AnnotationSpecBuilder
{
    /// <summary>The private key that carries the per-point widths of ink strokes, so a later session can erase part of a stroke (ink-pen 3.8, OQ-INK-3). Ignored by other readers.</summary>
    public const string InkWidthsKey = "MikePDF_InkWidths";

    /// <summary>Width samples above this count are not stored (ink-pen 3.8).</summary>
    public const int MaxStoredWidthSamples = 20_000;

    public static AnnotationSpec Build(Annotation a, int pageRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(a);
        var scene = SceneBuilder.Build(a, pageRotation);
        var appearance = AppearanceWriter.Write(scene);
        var entries = new List<KeyValuePair<string, SpecValue>>
        {
            Entry("Type", new SpecName("Annot")),
            Entry("Subtype", new SpecName(a.Subtype)),
            Entry("Rect", Numbers(Rect(a.Kind == MarkupKind.Note && a.ReplyKind == ReplyKind.None ? NoteGeometry.SavedRect(a.Rect, pageRotation) : a.Rect))),
            Entry("P", new SpecPageReference()),
            Entry("NM", new SpecText(a.Id)),
        };
        if (a.Modified != default)
        {
            entries.Add(Entry("M", new SpecDate(PdfDates.Format(a.Modified))));
        }

        if (a.Created != default)
        {
            entries.Add(Entry("CreationDate", new SpecDate(PdfDates.Format(a.Created))));
        }

        entries.Add(Entry("C", Numbers(Colour(a.Color))));
        entries.Add(Entry("CA", new SpecReal(a.Opacity)));
        if (a.Author.Length > 0)
        {
            entries.Add(Entry("T", new SpecText(a.Author)));
        }

        if (a.Contents.Length > 0 && a.ReplyKind != ReplyKind.State)
        {
            entries.Add(Entry("Contents", new SpecText(a.Kind == MarkupKind.FreeText ? a.Contents.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r') : a.Contents)));
        }

        var subject = a.Subject.Length > 0 ? a.Subject : MarkupDefaults.SubjectFor(a.Kind);
        if (subject.Length > 0)
        {
            entries.Add(Entry("Subj", new SpecText(subject)));
        }

        entries.Add(Entry("F", new SpecInt(MarkupDefaults.EffectiveFlags(a))));

        if (a.ReplyKind != ReplyKind.None)
        {
            AddReplyKeys(a, entries);
        }
        else
        {
            AddKindKeys(a, entries);
        }

        SpecMap? popup = null;
        if (a.PopupRect is { } p)
        {
            popup = Dictionary(
                Entry("Type", new SpecName("Annot")),
                Entry("Subtype", new SpecName("Popup")),
                Entry("Rect", Numbers(Rect(p))),
                Entry("Open", new SpecBool(false)),
                Entry("F", new SpecInt(0)));
        }

        return new AnnotationSpec(a.Subtype, new SpecMap(entries), appearance, popup);
    }

    private static void AddReplyKeys(Annotation a, List<KeyValuePair<string, SpecValue>> entries)
    {
        entries.Add(Entry("Name", new SpecName("Comment")));
        entries.Add(Entry("Open", new SpecBool(false)));
        entries.Add(Entry("RT", new SpecName("R")));
        if (a.ReplyKind == ReplyKind.State)
        {
            entries.Add(Entry("StateModel", new SpecText("Review")));
            entries.Add(Entry("State", new SpecText(a.State.ToString())));
        }
    }

    private static void AddKindKeys(Annotation a, List<KeyValuePair<string, SpecValue>> entries)
    {
        switch (a.Payload)
        {
            case TextMarkupPayload markup:
                entries.Add(Entry("QuadPoints", Numbers(markup.Quads.SelectMany(static q => q.ToNumbers()))));
                break;
            case NotePayload note:
                entries.Add(Entry("Name", new SpecName(NoteArtwork.NameOf(note.Icon))));
                entries.Add(Entry("Open", new SpecBool(false)));
                break;
            case FreeTextPayload text:
                AddFreeTextKeys(a, text, entries);
                break;
            case BoxPayload:
                AddBorder(a, entries);
                AddInterior(a, entries);
                break;
            case LinePayload line:
                entries.Add(Entry("L", Numbers([line.Start.X, line.Start.Y, line.End.X, line.End.Y])));
                entries.Add(Entry("LE", new SpecArray([new SpecName(EndingName(line.StartEnding)), new SpecName(EndingName(line.EndEnding))])));
                AddBorder(a, entries);
                AddInterior(a, entries);
                break;
            case PolyPayload poly:
                entries.Add(Entry("Vertices", Numbers(poly.Vertices.SelectMany(static v => new[] { v.X, v.Y }))));
                if (a.Kind == MarkupKind.PolyLine)
                {
                    entries.Add(Entry("LE", new SpecArray([new SpecName(EndingName(poly.StartEnding)), new SpecName(EndingName(poly.EndEnding))])));
                }

                AddBorder(a, entries);
                AddInterior(a, entries);
                break;
            case StampPayload stamp:
                entries.Add(Entry("Name", new SpecName(stamp.Name)));
                break;
            case InkPayload ink:
                entries.Add(Entry("InkList", new SpecArray([.. ink.Strokes.Select(static s => Numbers(s.Xy.Select(static v => (double)v)))])));
                entries.Add(Entry("BS", Dictionary(Entry("W", new SpecReal(a.Width)), Entry("S", new SpecName("S")))));
                if (ink.Strokes.Sum(static s => s.PointCount) <= MaxStoredWidthSamples)
                {
                    entries.Add(Entry(InkWidthsKey, new SpecArray([.. ink.Strokes.Select(static s => Numbers(s.Widths.Select(static v => Math.Round((double)v, 2))))])));
                }

                break;
        }
    }

    private static void AddFreeTextKeys(Annotation a, FreeTextPayload text, List<KeyValuePair<string, SpecValue>> entries)
    {
        var fontId = StandardFonts.FontId(text.Family, text.Bold, text.Italic);
        var colour = text.TextColor;
        entries.Add(Entry("DA", new SpecText($"/{StandardFonts.ResourceName(fontId)} {Num.Format(text.Size)} Tf {Num.Format(colour.R)} {Num.Format(colour.G)} {Num.Format(colour.B)} rg")));
        var align = text.Align switch { TextAlign.Center => "center", TextAlign.Right => "right", _ => "left" };
        entries.Add(Entry("DS", new SpecText($"font: {StandardFonts.CssFamily(fontId)}{(text.Bold ? " bold" : string.Empty)}{(text.Italic ? " italic" : string.Empty)} {Num.Format(text.Size)}pt; text-align:{align}; color:#{(int)Math.Round(colour.R * 255):X2}{(int)Math.Round(colour.G * 255):X2}{(int)Math.Round(colour.B * 255):X2}")));
        entries.Add(Entry("Q", new SpecInt((int)text.Align)));
        if (text.Typewriter)
        {
            entries.Add(Entry("IT", new SpecName("FreeTextTypeWriter")));
        }
        else
        {
            AddInterior(a, entries);
        }

        entries.Add(Entry("BS", Dictionary(Entry("W", new SpecReal(text.Typewriter ? 0 : a.Width)))));
    }

    private static void AddBorder(Annotation a, List<KeyValuePair<string, SpecValue>> entries)
    {
        var bs = new List<KeyValuePair<string, SpecValue>>
        {
            Entry("W", new SpecReal(a.Width)),
            Entry("S", new SpecName(a.Dash == DashStyle.Solid ? "S" : "D")),
        };
        if (ShapeGeometry.DashArray(a.Dash, a.Width) is { } dash)
        {
            bs.Add(Entry("D", Numbers(dash)));
        }

        entries.Add(Entry("BS", new SpecMap(bs)));
    }

    private static void AddInterior(Annotation a, List<KeyValuePair<string, SpecValue>> entries)
    {
        if (a.Interior is { } fill)
        {
            entries.Add(Entry("IC", Numbers(Colour(fill))));
        }
    }

    public static string EndingName(LineEnding ending) => ending switch
    {
        LineEnding.OpenArrow => "OpenArrow",
        LineEnding.ClosedArrow => "ClosedArrow",
        LineEnding.Square => "Square",
        LineEnding.Circle => "Circle",
        LineEnding.Diamond => "Diamond",
        _ => "None",
    };

    private static double[] Rect(PdfRect r) => [r.Left, r.Bottom, r.Right, r.Top];

    private static double[] Colour(Rgb c) => [c.R, c.G, c.B];

    private static SpecArray Numbers(IEnumerable<double> values) => new([.. values.Select(static v => (SpecValue)new SpecReal(v))]);

    private static KeyValuePair<string, SpecValue> Entry(string key, SpecValue value) => new(key, value);

    private static SpecMap Dictionary(params KeyValuePair<string, SpecValue>[] entries) => new(entries);
}
