using MikePDF.Core.Geometry;

namespace MikePDF.Markup;

/// <summary>One text line's quadrilateral, corners in the order readers expect: top left, top right, bottom left, bottom right (text-markup 2.3).</summary>
public readonly record struct Quad(PointD TopLeft, PointD TopRight, PointD BottomLeft, PointD BottomRight)
{
    /// <summary>The corners in drawing order around the outline: top left, top right, bottom right, bottom left.</summary>
    public PointD[] Outline() => [TopLeft, TopRight, BottomRight, BottomLeft];

    public PdfRect Bounds()
    {
        var xs = new[] { TopLeft.X, TopRight.X, BottomLeft.X, BottomRight.X };
        var ys = new[] { TopLeft.Y, TopRight.Y, BottomLeft.Y, BottomRight.Y };
        return new PdfRect(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    public double[] ToNumbers() => [TopLeft.X, TopLeft.Y, TopRight.X, TopRight.Y, BottomLeft.X, BottomLeft.Y, BottomRight.X, BottomRight.Y];

    public static Quad FromNumbers(ReadOnlySpan<double> n) =>
        new(new PointD(n[0], n[1]), new PointD(n[2], n[3]), new PointD(n[4], n[5]), new PointD(n[6], n[7]));

    /// <summary>An axis-aligned quad from a rectangle.</summary>
    public static Quad FromRect(PdfRect r) => new(new PointD(r.Left, r.Top), new PointD(r.Right, r.Top), new PointD(r.Left, r.Bottom), new PointD(r.Right, r.Bottom));
}

/// <summary>The kind-specific part of an annotation: quads, vertices, strokes, text style, stamp artwork (annotation-core 3.1 "Payload").</summary>
public abstract record AnnotationPayload;

/// <summary>Highlight, underline, strike-out and squiggly: the marked quadrilaterals.</summary>
public sealed record TextMarkupPayload(IReadOnlyList<Quad> Quads) : AnnotationPayload;

/// <summary>A sticky note: its icon.</summary>
public sealed record NotePayload(NoteIcon Icon) : AnnotationPayload;

/// <summary>A text box or typewriter text. The text is the annotation's <c>Contents</c>; the border is its colour and width; the fill is its interior colour.</summary>
public sealed record FreeTextPayload(FontFamily Family, bool Bold, bool Italic, double Size, Rgb TextColor, TextAlign Align, bool Typewriter) : AnnotationPayload;

/// <summary>A rectangle or an ellipse: the rectangle is the geometry.</summary>
public sealed record BoxPayload : AnnotationPayload;

/// <summary>A line or an arrow.</summary>
public sealed record LinePayload(PointD Start, PointD End, LineEnding StartEnding, LineEnding EndEnding) : AnnotationPayload;

/// <summary>A polygon (closed, endings ignored) or a polyline.</summary>
public sealed record PolyPayload(IReadOnlyList<PointD> Vertices, LineEnding StartEnding, LineEnding EndEnding) : AnnotationPayload;

/// <summary>A built-in stamp by its ISO 32000 name, turned by the user in quarter turns clockwise.</summary>
public sealed record StampPayload(string Name, int UserTurns) : AnnotationPayload;

/// <summary>One stroke of an ink annotation: x, y pairs (the simplified centreline) and one width in points for each point.</summary>
public sealed record InkStrokeData(float[] Xy, float[] Widths)
{
    public int PointCount => Xy.Length / 2;
}

/// <summary>An ink batch: strokes, and whether the tool was the highlighter (Multiply blend).</summary>
public sealed record InkPayload(IReadOnlyList<InkStrokeData> Strokes, bool Highlighter) : AnnotationPayload;

/// <summary>
/// An annotation in the edit model (annotation-core 3.1). Immutable: a change makes a new value, which is how undo restores the model exactly.
/// All geometry is in PDF user space of the page (points, origin bottom-left), the same space the file stores, whatever the page's rotation or
/// CropBox origin; conversions from the displayed page are in <see cref="PageFrame"/>.
/// </summary>
public sealed record Annotation
{
    /// <summary>The <c>/NM</c>: <c>MikePDF:</c> and 32 lower-case hex digits for annotations MikePDF creates (<see cref="AnnotationIds"/>).</summary>
    public required string Id { get; init; }

    public required MarkupKind Kind { get; init; }

    /// <summary>The page's ordinal in the document the annotation was made against. (The model of the specs keys annotations by page identity; the save core's commands use ordinals, see the deviations.)</summary>
    public required int Page { get; init; }

    /// <summary>Normalised: left below right, bottom below top. For kinds whose geometry decides it (text markup, lines, shapes, ink) it is computed from the geometry by <see cref="MarkupFactory"/>.</summary>
    public required PdfRect Rect { get; init; }

    /// <summary>Stroke colour; the highlight colour; the note colour; a text box's border colour; the stamp's colour.</summary>
    public required Rgb Color { get; init; }

    public double Opacity { get; init; } = 1;

    /// <summary>Stroke or border width in points; a text markup's thickness.</summary>
    public double Width { get; init; } = 1;

    /// <summary>The fill (<c>/IC</c>), or the interior colour of closed line endings.</summary>
    public Rgb? Interior { get; init; }

    public DashStyle Dash { get; init; }

    public string Contents { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset Modified { get; init; }

    /// <summary>The <c>/F</c> flags; 0 means the kind's default (<see cref="MarkupDefaults.DefaultFlags"/>).</summary>
    public uint Flags { get; init; }

    /// <summary>The id of the annotation this answers (a reply or a state change).</summary>
    public string? Parent { get; init; }

    public ReplyKind ReplyKind { get; init; }

    public ReviewState State { get; init; }

    /// <summary>The popup written for a note or a marked-up comment, if any.</summary>
    public PdfRect? PopupRect { get; init; }

    public AnnotationOrigin Origin { get; init; } = AnnotationOrigin.Created;

    public required AnnotationPayload Payload { get; init; }

    public bool IsOwned => AnnotationIds.IsOwned(Id);

    /// <summary>Text markup kinds are anchored to text and cannot be moved or resized.</summary>
    public bool IsTextAnchored => Kind is MarkupKind.Highlight or MarkupKind.Underline or MarkupKind.StrikeOut or MarkupKind.Squiggly;

    /// <summary>The subtype name the file carries.</summary>
    public string Subtype => Kind switch
    {
        MarkupKind.Highlight => "Highlight",
        MarkupKind.Underline => "Underline",
        MarkupKind.StrikeOut => "StrikeOut",
        MarkupKind.Squiggly => "Squiggly",
        MarkupKind.Note => "Text",
        MarkupKind.FreeText => "FreeText",
        MarkupKind.Ink => "Ink",
        MarkupKind.Rectangle => "Square",
        MarkupKind.Ellipse => "Circle",
        MarkupKind.Line or MarkupKind.Arrow => "Line",
        MarkupKind.Polygon => "Polygon",
        MarkupKind.PolyLine => "PolyLine",
        MarkupKind.Stamp => "Stamp",
        _ => throw new InvalidOperationException("Unknown kind."),
    };

    /// <summary>Whether an annotation of this value fills its interior (hit-testing and drawing).</summary>
    public bool IsFilled => Interior is not null && Kind is MarkupKind.Rectangle or MarkupKind.Ellipse or MarkupKind.Polygon or MarkupKind.FreeText;
}

/// <summary>Ids of annotations MikePDF creates and the test for "owned" (annotation-core 3.1, 3.2; OQ-AC-8).</summary>
public static class AnnotationIds
{
    public const string Prefix = "MikePDF:";

    /// <summary>A new id: <c>MikePDF:</c> and a GUID as 32 lower-case hex digits.</summary>
    public static string New() => Prefix + Guid.NewGuid().ToString("N");

    public static bool IsOwned(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>A valid owned id: the prefix and exactly 32 lower-case hex digits.</summary>
    public static bool IsWellFormedOwned(string? id) =>
        id is { Length: 40 } && IsOwned(id) && id.AsSpan(Prefix.Length).ToString().All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// The id a loaded annotation gets: its <c>/NM</c> if it has one and it is the first with that value, else a session id built from the page and the
    /// annotation's index (never starting with the owned prefix, so a hostile <c>/NM</c> is the only way to be "owned").
    /// </summary>
    public static string ForLoaded(string? nm, int page, int index, ISet<string> seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        if (!string.IsNullOrEmpty(nm) && seen.Add(nm))
        {
            return nm;
        }

        var session = $"session:{page}:{index}";
        while (!seen.Add(session))
        {
            session += "'";
        }

        return session;
    }
}
