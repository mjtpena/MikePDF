namespace MikePDF.Protocol.Edits;

/// <summary>
/// The annotation kinds the writer host can create (save-pipeline 11.3). A feature work package adds a kind here, in the generator and in the codec's range check.
/// An arrow is a <see cref="Line"/> with an arrow ending, and a typewriter is a <see cref="FreeText"/> with the typewriter trait: the file cannot tell them apart.
/// </summary>
public enum AnnotationKind : ushort
{
    Highlight = 1,
    Underline = 2,
    StrikeOut = 3,
    Text = 4,
    Ink = 5,
    Square = 6,
    Circle = 7,
    Squiggly = 8,
    FreeText = 9,
    Line = 10,
    PolyLine = 11,
    Polygon = 12,
    Stamp = 13,
}

public enum EditOperation : ushort
{
    /// <summary>Create an annotation on a page.</summary>
    Add = 1,

    /// <summary>Remove a loaded annotation from its page.</summary>
    Delete = 2,

    /// <summary>Change a loaded annotation's rectangle and contents.</summary>
    Update = 3,

    /// <summary>
    /// Rebuild a loaded annotation, dictionary and appearance, from the fields of an Add, in place (same object, same place in <c>/Annots</c>, same
    /// popup). This is a restyle, a vertex edit or an ink erase of an annotation that an earlier save already wrote.
    /// </summary>
    Replace = 4,
}

/// <summary>How a stroke is dashed (shapes 3.2). The dash lengths follow from the width.</summary>
public enum DashKind : ushort
{
    Solid = 0,
    Dashed = 1,
    Dotted = 2,
}

/// <summary>The end of a line or polyline (shapes 3.2).</summary>
public enum LineEndKind : ushort
{
    None = 0,
    OpenArrow = 1,
    ClosedArrow = 2,
    Square = 3,
    Circle = 4,
    Diamond = 5,
}

/// <summary>Bits of <see cref="AnnotationEdit.Traits"/>.</summary>
[Flags]
public enum EditTraits : uint
{
    None = 0,

    /// <summary>FreeText: a typewriter (<c>/IT /FreeTextTypeWriter</c>, no border or fill).</summary>
    Typewriter = 1,

    /// <summary>Ink: a highlighter stroke (Multiply blend, flat translucent ink).</summary>
    Highlighter = 2,

    /// <summary>Stamp: the user's quarter turns, two bits (0 to 3), shifted by <see cref="EditTraitBits.TurnsShift"/>.</summary>
    TurnsMask = 12,
}

/// <summary>
/// One annotation edit of the edit blob. Pages are ordinals in the base document (0 based): the broker translates page identities before it writes
/// the blob. Every coordinate is in default user space, with no conversion in the writer (save-pipeline 7).
/// </summary>
public sealed class AnnotationEdit
{
    public EditOperation Operation { get; init; }

    /// <summary>The page's ordinal in the base document.</summary>
    public uint Page { get; init; }

    /// <summary>Add: the annotation to create.</summary>
    public AnnotationKind Kind { get; init; }

    /// <summary>Left, bottom, right, top. Add: the annotation's rectangle. Update: the new one, when it moves or resizes.</summary>
    public float[]? Rect { get; init; }

    /// <summary>Red, green, blue, each 0 to 1 (Add).</summary>
    public float[]? Color { get; init; }

    /// <summary>Opacity above 0 and at most 1 (Add).</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Stroke or border width in points (Add).</summary>
    public float Width { get; init; } = 1f;

    /// <summary>The annotation's text (Add, and Update to change it). Null in an Update leaves it as it is.</summary>
    public string? Contents { get; init; }

    public string? Author { get; init; }

    /// <summary>
    /// Add: the annotation's <c>/NM</c>, which must be unique on the page. Delete and Update when <see cref="TargetObject"/> is 0: the <c>/NM</c>
    /// of the loaded annotation to act on.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The <c>/F</c> flags (Add). 0 means the kind's default.</summary>
    public uint Flags { get; init; }

    /// <summary>Highlight, Underline, StrikeOut: eight numbers per marked quadrilateral, in the order PDF readers write them (top left, top right, bottom left, bottom right).</summary>
    public float[]? QuadPoints { get; init; }

    /// <summary>Ink: each stroke is x, y pairs.</summary>
    public IReadOnlyList<float[]>? Strokes { get; init; }

    /// <summary>Delete and Update: the loaded annotation's object number, or 0 to find it by <see cref="Name"/>.</summary>
    public uint TargetObject { get; init; }

    /// <summary>Delete and Update: the expectation the writer checks before touching the object (save-pipeline 3.3 stage 4). A mismatch fails the whole stage.</summary>
    public string? ExpectSubtype { get; init; }

    /// <summary>Delete and Update: the rectangle the annotation is expected to have, within 0.01 point.</summary>
    public float[]? ExpectRect { get; init; }

    // ---- fields added by the annotation work package (blob version 2). They follow the Add operation's meaning; a Replace carries them too. ----

    /// <summary>Red, green, blue of the interior (<c>/IC</c>): a shape's fill, the fill of a closed line ending, a text box's fill.</summary>
    public float[]? InteriorColor { get; init; }

    /// <summary><c>/Subj</c>: the tool's name.</summary>
    public string? Subject { get; init; }

    /// <summary><c>/CreationDate</c> as a PDF date string.</summary>
    public string? Created { get; init; }

    /// <summary><c>/M</c> as a PDF date string. Null: the writer's clock.</summary>
    public string? Modified { get; init; }

    public DashKind Dash { get; init; }

    /// <summary>Line: x1 y1 x2 y2. Polygon and PolyLine: x, y pairs.</summary>
    public float[]? Vertices { get; init; }

    public LineEndKind LineStart { get; init; }

    public LineEndKind LineEnd { get; init; }

    /// <summary>Text: the icon name (<c>/Name</c>). Stamp: the standard stamp name. A name object in the file.</summary>
    public string? StandardName { get; init; }

    public EditTraits Traits { get; init; }

    /// <summary>FreeText: the font, 0 to 11 (Helvetica, Times, Courier, each regular, bold, italic, bold italic).</summary>
    public ushort FontId { get; init; }

    /// <summary>FreeText: the font size in points; 0 for kinds without text.</summary>
    public float FontSize { get; init; }

    /// <summary>FreeText: the text colour.</summary>
    public float[]? TextColor { get; init; }

    /// <summary>FreeText: 0 left, 1 centre, 2 right (<c>/Q</c>).</summary>
    public ushort Align { get; init; }

    /// <summary>Ink: one width in points for each point of each stroke, in the order of <see cref="Strokes"/>.</summary>
    public IReadOnlyList<float[]>? InkWidths { get; init; }

    /// <summary>A reply or a review state: the <c>/NM</c> of the annotation it answers (<c>/IRT</c>).</summary>
    public string? ParentName { get; init; }

    /// <summary>0 an ordinary annotation, 1 a reply (<c>/RT /R</c>), 2 a review state (<c>/StateModel /Review</c>).</summary>
    public ushort ReplyKind { get; init; }

    /// <summary>For <see cref="ReplyKind"/> 2: 0 None, 1 Accepted, 2 Rejected, 3 Cancelled, 4 Completed.</summary>
    public ushort State { get; init; }

    /// <summary>The rectangle of the popup written with a note or a marked-up comment; null for no popup.</summary>
    public float[]? PopupRect { get; init; }

    /// <summary>The stamp's quarter turns (0 to 3), kept in <see cref="Traits"/>.</summary>
    public int StampTurns => (int)(((uint)Traits & (uint)EditTraits.TurnsMask) >> EditTraitBits.TurnsShift);
}

/// <summary>Where the quarter turns sit in <see cref="AnnotationEdit.Traits"/>.</summary>
public static class EditTraitBits
{
    public const int TurnsShift = 2;
}

/// <summary>The edits of stage 4 and the page count of the base they were made against.</summary>
public sealed class EditBlob
{
    public uint PageCount { get; init; }

    public IReadOnlyList<AnnotationEdit> Edits { get; init; } = [];
}
