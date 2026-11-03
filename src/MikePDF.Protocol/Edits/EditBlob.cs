namespace MikePDF.Protocol.Edits;

/// <summary>The annotation kinds the writer host can create (save-pipeline 11.3). A feature work package adds a kind here, in the generator and in the codec's range check.</summary>
public enum AnnotationKind : ushort
{
    Highlight = 1,
    Underline = 2,
    StrikeOut = 3,
    Text = 4,
    Ink = 5,
    Square = 6,
    Circle = 7,
}

public enum EditOperation : ushort
{
    /// <summary>Create an annotation on a page.</summary>
    Add = 1,

    /// <summary>Remove a loaded annotation from its page.</summary>
    Delete = 2,

    /// <summary>Change a loaded annotation's rectangle and contents.</summary>
    Update = 3,
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
}

/// <summary>The edits of stage 4 and the page count of the base they were made against.</summary>
public sealed class EditBlob
{
    public uint PageCount { get; init; }

    public IReadOnlyList<AnnotationEdit> Edits { get; init; } = [];
}
