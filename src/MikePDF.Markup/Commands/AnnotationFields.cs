using System.Text.Json;
using MikePDF.Core.Geometry;

namespace MikePDF.Markup;

/// <summary>
/// The fields of an annotation as the journal stores them and the edit mapper reads them: one flat JSON object (the save core's convention, which the
/// feature specs leave to the work packages; annotation-core 9 describes a nested <c>style</c> and <c>payload</c>, see the deviations). The same
/// fields describe an annotation in <c>AddAnnotation</c>, in the <c>replace</c> of an update, and in the inverse of a delete, so undo can restore any
/// annotation exactly.
/// <list type="bullet">
/// <item><c>page</c>, <c>kind</c> (the wire kind: Highlight, Underline, StrikeOut, Squiggly, Text, FreeText, Ink, Square, Circle, Line, Polygon, PolyLine, Stamp), <c>name</c> (the id), <c>rect</c>, <c>color</c>, <c>opacity</c>, <c>width</c></item>
/// <item><c>contents</c>, <c>author</c>, <c>subj</c>, <c>created</c>, <c>modified</c> (PDF dates), <c>flags</c> (omitted when it is the kind's default), <c>ic</c>, <c>dash</c></item>
/// <item>text markup <c>quads</c>; ink <c>strokes</c>, <c>widths</c>, <c>highlighter</c>; line and polyline <c>le</c>; line, polygon and polyline <c>vertices</c></item>
/// <item>note <c>icon</c>, <c>popup</c>; stamp <c>stamp</c>, <c>turns</c>; text box <c>typewriter</c>, <c>family</c>, <c>bold</c>, <c>italic</c>, <c>size</c>, <c>tcolor</c>, <c>align</c></item>
/// <item>replies <c>parent</c>, <c>reply</c> (1 reply, 2 review state), <c>state</c></item>
/// </list>
/// </summary>
public static class AnnotationFields
{
    /// <summary>The kind name the blob and the edit mapper use.</summary>
    public static string WireKind(MarkupKind kind) => kind switch
    {
        MarkupKind.Note => "Text",
        MarkupKind.Rectangle => "Square",
        MarkupKind.Ellipse => "Circle",
        MarkupKind.Arrow => "Line",
        _ => kind.ToString(),
    };

    public static Dictionary<string, object?> ToFields(Annotation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var f = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["page"] = a.Page,
            ["kind"] = WireKind(a.Kind),
            ["name"] = a.Id,
            ["rect"] = Rect(a.Rect),
            ["color"] = Colour(a.Color),
            ["opacity"] = a.Opacity,
            ["width"] = a.Width,
        };
        if (a.Contents.Length > 0)
        {
            f["contents"] = a.Contents;
        }

        if (a.Author.Length > 0)
        {
            f["author"] = a.Author;
        }

        if (a.Subject.Length > 0)
        {
            f["subj"] = a.Subject;
        }

        if (a.Created != default)
        {
            f["created"] = PdfDates.Format(a.Created);
        }

        if (a.Modified != default)
        {
            f["modified"] = PdfDates.Format(a.Modified);
        }

        if (a.Flags != 0 && a.Flags != MarkupDefaults.DefaultFlags(a.Kind))
        {
            f["flags"] = a.Flags;
        }

        if (a.Interior is { } ic)
        {
            f["ic"] = Colour(ic);
        }

        if (a.Dash != DashStyle.Solid)
        {
            f["dash"] = a.Dash.ToString().ToLowerInvariant();
        }

        if (a.Parent is not null)
        {
            f["parent"] = a.Parent;
        }

        if (a.ReplyKind != ReplyKind.None)
        {
            f["reply"] = (int)a.ReplyKind;
        }

        if (a.ReplyKind == ReplyKind.State)
        {
            f["state"] = a.State.ToString();
        }

        if (a.PopupRect is { } popup)
        {
            f["popup"] = Rect(popup);
        }

        switch (a.Payload)
        {
            case TextMarkupPayload m:
                f["quads"] = m.Quads.SelectMany(static q => q.ToNumbers()).ToArray();
                break;
            case NotePayload n:
                f["icon"] = n.Icon.ToString();
                break;
            case FreeTextPayload t:
                f["family"] = t.Family.ToString();
                f["bold"] = t.Bold;
                f["italic"] = t.Italic;
                f["size"] = t.Size;
                f["tcolor"] = Colour(t.TextColor);
                f["align"] = t.Align.ToString().ToLowerInvariant();
                if (t.Typewriter)
                {
                    f["typewriter"] = true;
                }

                if (t.PageRotation != 0)
                {
                    f["rot"] = t.PageRotation;
                }

                break;
            case LinePayload l:
                f["vertices"] = new[] { l.Start.X, l.Start.Y, l.End.X, l.End.Y };
                f["le"] = new[] { l.StartEnding.ToString(), l.EndEnding.ToString() };
                break;
            case PolyPayload p:
                f["vertices"] = p.Vertices.SelectMany(static v => new[] { v.X, v.Y }).ToArray();
                if (a.Kind == MarkupKind.PolyLine)
                {
                    f["le"] = new[] { p.StartEnding.ToString(), p.EndEnding.ToString() };
                }

                break;
            case StampPayload s:
                f["stamp"] = s.Name;
                if (s.UserTurns != 0)
                {
                    f["turns"] = s.UserTurns;
                }

                break;
            case InkPayload i:
                f["strokes"] = i.Strokes.Select(static s => s.Xy).ToArray();
                f["widths"] = i.Strokes.Select(static s => s.Widths).ToArray();
                if (i.Highlighter)
                {
                    f["highlighter"] = true;
                }

                break;
        }

        return f;
    }

    /// <summary>Reads an annotation back from fields. Throws <see cref="FormatException"/> for a field that is missing or not what it should be.</summary>
    public static Annotation FromFields(JsonElement f, AnnotationOrigin origin = AnnotationOrigin.Created)
    {
        try
        {
            return Read(f, origin);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            throw new FormatException("The annotation's fields are missing or out of range: " + e.Message, e);
        }
    }

    private static Annotation Read(JsonElement f, AnnotationOrigin origin)
    {
        var wire = Str(f, "kind") ?? throw new KeyNotFoundException("kind");
        var reply = (ReplyKind)(Int(f, "reply") ?? 0);
        var rect = Floats(f, "rect") ?? throw new KeyNotFoundException("rect");
        var color = Floats(f, "color") ?? throw new KeyNotFoundException("color");
        var vertices = Floats(f, "vertices");
        var le = f.TryGetProperty("le", out var leElement) && leElement.ValueKind == JsonValueKind.Array ? leElement.EnumerateArray().Select(static e => Enum.Parse<LineEnding>(e.GetString()!)).ToArray() : [LineEnding.None, LineEnding.None];
        var kind = wire switch
        {
            "Highlight" => MarkupKind.Highlight,
            "Underline" => MarkupKind.Underline,
            "StrikeOut" => MarkupKind.StrikeOut,
            "Squiggly" => MarkupKind.Squiggly,
            "Text" => MarkupKind.Note,
            "FreeText" => MarkupKind.FreeText,
            "Ink" => MarkupKind.Ink,
            "Square" => MarkupKind.Rectangle,
            "Circle" => MarkupKind.Ellipse,
            "Line" => le[0] == LineEnding.None && le[1] == LineEnding.OpenArrow ? MarkupKind.Arrow : MarkupKind.Line,
            "Polygon" => MarkupKind.Polygon,
            "PolyLine" => MarkupKind.PolyLine,
            "Stamp" => MarkupKind.Stamp,
            _ => throw new FormatException("kind"),
        };
        AnnotationPayload payload = kind switch
        {
            MarkupKind.Highlight or MarkupKind.Underline or MarkupKind.StrikeOut or MarkupKind.Squiggly => new TextMarkupPayload(Quads(Floats(f, "quads") ?? throw new KeyNotFoundException("quads"))),
            MarkupKind.Note => new NotePayload(Enum.TryParse<NoteIcon>(Str(f, "icon"), out var icon) ? icon : NoteIcon.Comment),
            MarkupKind.FreeText => new FreeTextPayload(
                Enum.Parse<FontFamily>(Str(f, "family") ?? "Sans"),
                Bool(f, "bold"),
                Bool(f, "italic"),
                Float(f, "size") ?? 12,
                Rgb.FromNumbers(Floats(f, "tcolor") ?? [0, 0, 0]),
                Enum.Parse<TextAlign>(Str(f, "align") ?? "left", ignoreCase: true),
                Bool(f, "typewriter"),
                Int(f, "rot") ?? 0),
            MarkupKind.Rectangle or MarkupKind.Ellipse => new BoxPayload(),
            MarkupKind.Line or MarkupKind.Arrow => new LinePayload(new PointD(vertices![0], vertices[1]), new PointD(vertices[2], vertices[3]), le[0], le[1]),
            MarkupKind.Polygon or MarkupKind.PolyLine => new PolyPayload(Points(vertices ?? throw new KeyNotFoundException("vertices")), le[0], le[1]),
            MarkupKind.Stamp => new StampPayload(Str(f, "stamp") ?? throw new KeyNotFoundException("stamp"), Int(f, "turns") ?? 0),
            MarkupKind.Ink => new InkPayload(InkStrokes(f), Bool(f, "highlighter")),
            _ => throw new FormatException("kind"),
        };
        var flags = (uint)(Int(f, "flags") ?? 0);
        return new Annotation
        {
            Id = Str(f, "name") ?? throw new KeyNotFoundException("name"),
            Kind = kind,
            Page = Int(f, "page") ?? throw new KeyNotFoundException("page"),
            Rect = new PdfRect(rect[0], rect[1], rect[2], rect[3]),
            Color = Rgb.FromNumbers(color),
            Opacity = Dbl(f, "opacity") ?? 1,
            Width = Dbl(f, "width") ?? 1,
            Interior = Floats(f, "ic") is { } ic ? Rgb.FromNumbers(ic) : null,
            Dash = Enum.Parse<DashStyle>(Str(f, "dash") ?? "solid", ignoreCase: true),
            Contents = Str(f, "contents") ?? string.Empty,
            Author = Str(f, "author") ?? string.Empty,
            Subject = Str(f, "subj") ?? string.Empty,
            Created = PdfDates.TryParse(Str(f, "created"), out var created) ? created : default,
            Modified = PdfDates.TryParse(Str(f, "modified"), out var modified) ? modified : default,
            Flags = flags,
            Parent = Str(f, "parent"),
            ReplyKind = reply,
            State = reply == ReplyKind.State ? Enum.Parse<ReviewState>(Str(f, "state") ?? "None") : ReviewState.None,
            PopupRect = Floats(f, "popup") is { } popup ? new PdfRect(popup[0], popup[1], popup[2], popup[3]) : null,
            Origin = origin,
            Payload = payload,
        };
    }

    /// <summary>Two annotations hold the same values (arrays compared by content, not by reference). The check undo and redo are tested with.</summary>
    public static bool Same(Annotation? a, Annotation? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Origin == b.Origin && JsonSerializer.Serialize(ToFields(a)) == JsonSerializer.Serialize(ToFields(b));
    }

    private static IReadOnlyList<Quad> Quads(double[] numbers) =>
        numbers.Length % 8 != 0 || numbers.Length == 0 ? throw new FormatException("quads") : [.. Enumerable.Range(0, numbers.Length / 8).Select(i => Quad.FromNumbers(numbers.AsSpan(i * 8, 8)))];

    private static IReadOnlyList<PointD> Points(double[] numbers) =>
        numbers.Length % 2 != 0 ? throw new FormatException("vertices") : [.. Enumerable.Range(0, numbers.Length / 2).Select(i => new PointD(numbers[2 * i], numbers[(2 * i) + 1]))];

    private static IReadOnlyList<InkStrokeData> InkStrokes(JsonElement f)
    {
        var strokes = f.GetProperty("strokes").EnumerateArray().Select(static s => s.EnumerateArray().Select(static v => v.GetSingle()).ToArray()).ToArray();
        var widths = f.TryGetProperty("widths", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(static s => s.EnumerateArray().Select(static v => v.GetSingle()).ToArray()).ToArray()
            : null;
        return [.. strokes.Select((s, i) => new InkStrokeData(s, widths is not null && i < widths.Length ? widths[i] : Enumerable.Repeat(1f, s.Length / 2).ToArray()))];
    }

    private static double[] Rect(PdfRect r) => [r.Left, r.Bottom, r.Right, r.Top];

    private static double[] Colour(Rgb c) => [c.R, c.G, c.B];

    private static string? Str(JsonElement f, string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement f, string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? Dbl(JsonElement f, string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static float? Float(JsonElement f, string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

    private static bool Bool(JsonElement f, string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double[]? Floats(JsonElement f, string name) =>
        f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(static x => x.GetDouble())] : null;
}
