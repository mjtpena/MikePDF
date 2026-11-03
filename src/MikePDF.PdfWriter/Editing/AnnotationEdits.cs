namespace MikePDF.PdfWriter.Editing;

/// <summary>The annotation kinds the writer creates. The numbers match the edit blob's (<c>MikePDF.Protocol.Edits.AnnotationKind</c>).</summary>
public enum NewAnnotationKind
{
    Highlight = 1,
    Underline = 2,
    StrikeOut = 3,
    Text = 4,
    Ink = 5,
    Square = 6,
    Circle = 7,
}

public enum AnnotationEditKind
{
    Add = 1,
    Delete = 2,
    Update = 3,
}

/// <summary>
/// One edit of stage 4 (save-pipeline 3.3), in the writer's own terms so that this library has no dependency on the wire format. The writer host
/// converts the decoded edit blob into these. Pages are ordinals in the base document, 0 based.
/// </summary>
public sealed record AnnotationChange
{
    public required AnnotationEditKind Kind { get; init; }

    public required int Page { get; init; }

    public NewAnnotationKind Annotation { get; init; }

    public float[]? Rect { get; init; }

    public float[]? Color { get; init; }

    public float Opacity { get; init; } = 1f;

    public float Width { get; init; } = 1f;

    public string? Contents { get; init; }

    public string? Author { get; init; }

    /// <summary>Add: the new annotation's <c>/NM</c>. Delete and Update without <see cref="TargetObject"/>: the loaded annotation's.</summary>
    public string? Name { get; init; }

    public uint Flags { get; init; }

    public float[]? QuadPoints { get; init; }

    public IReadOnlyList<float[]>? Strokes { get; init; }

    public int TargetObject { get; init; }

    public string? ExpectSubtype { get; init; }

    public float[]? ExpectRect { get; init; }
}

public sealed record AppendOptions
{
    /// <summary>The time written to <c>/M</c>. Null: now.</summary>
    public DateTimeOffset? Modified { get; init; }

    /// <summary>Second element of the new trailer /ID. Null: 16 random bytes.</summary>
    public ReadOnlyMemory<byte>? NewIdSecondElement { get; init; }
}

/// <param name="Output">The whole new file: the base bytes, unchanged, then the update.</param>
/// <param name="AnnotationsAdded">Annotations the update created.</param>
/// <param name="AnnotationsRemoved">Annotations the update took off their pages.</param>
/// <param name="ObjectsWritten">Objects in the update (annotations, appearance streams, rewritten pages or arrays).</param>
/// <param name="PagesTouched">Ordinals of the pages whose annotation list changed, and how many annotations each had before and has now.</param>
public sealed record AppendResult(byte[] Output, int AnnotationsAdded, int AnnotationsRemoved, int ObjectsWritten, IReadOnlyList<PageDelta> PagesTouched);

public sealed record PageDelta(int Page, int Before, int After);
