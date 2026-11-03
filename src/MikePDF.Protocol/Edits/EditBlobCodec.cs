using System.Buffers;
using MessagePack;

namespace MikePDF.Protocol.Edits;

/// <summary>An edit blob that breaks a limit or a rule, on write or on read.</summary>
public sealed class EditBlobException(string message) : Exception(message);

/// <summary>The limits of the edit blob (save-pipeline 11.3). Both the writer and the reader enforce all of them.</summary>
public static class EditBlobLimits
{
    public const int Version = 1;
    public const string Magic = "MikePDF.edits";

    /// <summary>The blob as a whole: 256 MiB.</summary>
    public const long MaxBytes = 256L << 20;

    public const int MaxEdits = 1_000_000;
    public const uint MaxPages = 10_000_000;
    public const int MaxContentsBytes = 16 * 1024;
    public const int MaxAuthorBytes = 256;
    public const int MaxNameBytes = 128;
    public const int MaxSubtypeBytes = 32;

    /// <summary>Quadrilaterals per markup annotation: 10,000, eight numbers each.</summary>
    public const int MaxQuadFloats = 80_000;

    public const int MaxStrokesPerAnnotation = 100_000;

    /// <summary>One stroke: 1,000,000 points.</summary>
    public const int MaxFloatsPerStroke = 2_000_000;

    /// <summary>Every stroke of the blob together; bounds what the reader allocates (64 MiB of floats).</summary>
    public const long MaxStrokeFloatsTotal = 16_000_000;

    /// <summary>Coordinates are finite and within this magnitude (PDF user space tops out around 14,400 points; real files have more).</summary>
    public const float MaxCoordinate = 10_000_000f;

    public const float MaxWidth = 1000f;
    public const uint MaxFlags = 1023;
    public const uint MaxObjectNumber = 4_194_303;
}

/// <summary>
/// MessagePack, strict, with every limit checked before anything is allocated: <c>[magic, version, pageCount, [edit...]]</c>, each edit an array of
/// sixteen fields in a fixed order (nil where a field does not apply). The reader is a parser of broker-produced data inside the sandbox; it is
/// still fuzzed, as defence in depth. It reuses the protocol's strict primitives, so a value of the wrong type or outside its range is refused
/// the same way as in a frame. Nothing here recurses.
/// </summary>
public static class EditBlobCodec
{
    private const int EditFields = 16;

    public static byte[] Encode(EditBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        Validate(blob);
        var buffer = new ArrayBufferWriter<byte>(1024);
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(EditBlobLimits.Magic);
        writer.Write((uint)EditBlobLimits.Version);
        writer.Write(blob.PageCount);
        writer.WriteArrayHeader(blob.Edits.Count);
        foreach (var edit in blob.Edits)
        {
            writer.WriteArrayHeader(EditFields);
            writer.Write((ushort)edit.Operation);
            writer.Write(edit.Page);
            writer.Write((ushort)edit.Kind);
            WriteFloats(ref writer, edit.Rect);
            WriteFloats(ref writer, edit.Color);
            writer.Write(edit.Opacity);
            writer.Write(edit.Width);
            WriteString(ref writer, edit.Contents);
            WriteString(ref writer, edit.Author);
            WriteString(ref writer, edit.Name);
            writer.Write(edit.Flags);
            WriteFloats(ref writer, edit.QuadPoints);
            if (edit.Strokes is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.WriteArrayHeader(edit.Strokes.Count);
                foreach (var stroke in edit.Strokes)
                {
                    WriteFloats(ref writer, stroke);
                }
            }

            writer.Write(edit.TargetObject);
            WriteString(ref writer, edit.ExpectSubtype);
            WriteFloats(ref writer, edit.ExpectRect);
        }

        writer.Flush();
        if (buffer.WrittenCount > EditBlobLimits.MaxBytes)
        {
            throw new EditBlobException("The edit blob is over 256 MiB.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a blob. Never throws for bad content: the reason is in <paramref name="error"/>.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> data, out EditBlob blob, out string error)
    {
        blob = null!;
        if (data.Length > EditBlobLimits.MaxBytes)
        {
            error = "The edit blob is over its size limit.";
            return false;
        }

        try
        {
            var reader = new MessagePackReader(data);
            StrictReader.ExpectArray(ref reader, 4);
            if (StrictReader.ReadString(ref reader, 64) != EditBlobLimits.Magic)
            {
                error = "Not an edit blob.";
                return false;
            }

            if (StrictReader.ReadU32(ref reader, EditBlobLimits.Version, EditBlobLimits.Version) != EditBlobLimits.Version)
            {
                error = "Unsupported edit blob version.";
                return false;
            }

            var pageCount = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxPages);
            var count = StrictReader.ReadArrayHeader(ref reader, EditBlobLimits.MaxEdits);
            var edits = new List<AnnotationEdit>(Math.Min(count, 4096));
            long strokeFloats = 0;
            for (var i = 0; i < count; i++)
            {
                StrictReader.ExpectArray(ref reader, EditFields);
                var operation = (EditOperation)StrictReader.ReadU16(ref reader, 1, 3);
                var page = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxPages - 1);
                var kind = (AnnotationKind)StrictReader.ReadU16(ref reader, 0, 7);
                var rect = ReadFloats(ref reader, 4, 4);
                var color = ReadFloats(ref reader, 3, 3, 0f, 1f);
                var opacity = StrictReader.ReadF32(ref reader, 0f, 1f, minExclusive: true);
                var width = StrictReader.ReadF32(ref reader, 0f, EditBlobLimits.MaxWidth, minExclusive: false);
                var contents = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxContentsBytes);
                var author = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxAuthorBytes);
                var name = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxNameBytes);
                var flags = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxFlags);
                var quads = ReadFloats(ref reader, 0, EditBlobLimits.MaxQuadFloats);
                float[][]? strokes = null;
                if (!reader.TryReadNil())
                {
                    var strokeCount = StrictReader.ReadArrayHeader(ref reader, EditBlobLimits.MaxStrokesPerAnnotation);
                    strokes = new float[strokeCount][];
                    for (var s = 0; s < strokeCount; s++)
                    {
                        strokes[s] = ReadFloats(ref reader, 0, EditBlobLimits.MaxFloatsPerStroke) ?? throw new EditBlobException("A stroke is nil.");
                        strokeFloats += strokes[s].Length;
                        if (strokeFloats > EditBlobLimits.MaxStrokeFloatsTotal)
                        {
                            error = "The strokes are over their total limit.";
                            return false;
                        }
                    }
                }

                var target = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxObjectNumber);
                var expectSubtype = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxSubtypeBytes);
                var expectRect = ReadFloats(ref reader, 4, 4);
                edits.Add(new AnnotationEdit
                {
                    Operation = operation,
                    Page = page,
                    Kind = kind,
                    Rect = rect,
                    Color = color,
                    Opacity = opacity,
                    Width = width,
                    Contents = contents,
                    Author = author,
                    Name = name,
                    Flags = flags,
                    QuadPoints = quads,
                    Strokes = strokes,
                    TargetObject = target,
                    ExpectSubtype = expectSubtype,
                    ExpectRect = expectRect,
                });
            }

            if (!reader.End)
            {
                error = "Bytes follow the edit blob.";
                return false;
            }

            blob = new EditBlob { PageCount = pageCount, Edits = edits };
            Validate(blob);
            error = string.Empty;
            return true;
        }
        catch (Exception e) when (e is MessagePackSerializationException or ProtocolLimitException or EditBlobException or EndOfStreamException or OverflowException or InvalidOperationException or ArgumentException)
        {
            error = e is EditBlobException ? e.Message : "The edit blob is malformed or over a limit.";
            blob = null!;
            return false;
        }
    }

    /// <summary>The rules a well-formed blob obeys beyond field ranges: what each operation needs, and that pages exist.</summary>
    public static void Validate(EditBlob blob)
    {
        if (blob.Edits.Count > EditBlobLimits.MaxEdits)
        {
            throw new EditBlobException("Too many edits.");
        }

        long strokeFloats = 0;
        foreach (var edit in blob.Edits)
        {
            if (edit.Page >= blob.PageCount)
            {
                throw new EditBlobException("An edit names a page the document does not have.");
            }

            CheckLengths(edit);
            switch (edit.Operation)
            {
                case EditOperation.Add:
                    RequireNormalisedRect(edit.Rect, "An added annotation needs a rectangle.");
                    if (edit.Kind is < AnnotationKind.Highlight or > AnnotationKind.Circle)
                    {
                        throw new EditBlobException("An added annotation needs a known kind.");
                    }

                    if (edit.Color is null || string.IsNullOrEmpty(edit.Name))
                    {
                        throw new EditBlobException("An added annotation needs a colour and a name.");
                    }

                    if (edit.TargetObject != 0 || edit.ExpectSubtype is not null || edit.ExpectRect is not null)
                    {
                        throw new EditBlobException("An added annotation has no target.");
                    }

                    if (edit.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut
                        && (edit.QuadPoints is null || edit.QuadPoints.Length == 0 || edit.QuadPoints.Length % 8 != 0))
                    {
                        throw new EditBlobException("A text markup needs its quadrilaterals.");
                    }

                    if (edit.Kind == AnnotationKind.Ink)
                    {
                        if (edit.Strokes is null || edit.Strokes.Count == 0)
                        {
                            throw new EditBlobException("An ink annotation needs strokes.");
                        }

                        foreach (var stroke in edit.Strokes)
                        {
                            strokeFloats += stroke.Length;
                            if (stroke.Length < 4 || stroke.Length % 2 != 0)
                            {
                                throw new EditBlobException("A stroke needs at least two points.");
                            }
                        }
                    }

                    break;
                case EditOperation.Delete:
                case EditOperation.Update:
                    if (edit.TargetObject == 0 && string.IsNullOrEmpty(edit.Name))
                    {
                        throw new EditBlobException("An edit of a loaded annotation names its object or its /NM.");
                    }

                    if (string.IsNullOrEmpty(edit.ExpectSubtype) || edit.ExpectRect is null)
                    {
                        throw new EditBlobException("An edit of a loaded annotation carries what it expects to find.");
                    }

                    if (edit.Operation == EditOperation.Update)
                    {
                        if (edit.Rect is null && edit.Contents is null)
                        {
                            throw new EditBlobException("An update changes something.");
                        }

                        if (edit.Rect is not null)
                        {
                            RequireNormalisedRect(edit.Rect, "An update's rectangle is not valid.");
                        }
                    }

                    break;
                default:
                    throw new EditBlobException("Unknown operation.");
            }
        }

        if (strokeFloats > EditBlobLimits.MaxStrokeFloatsTotal)
        {
            throw new EditBlobException("The strokes are over their total limit.");
        }
    }

    private static void CheckLengths(AnnotationEdit edit)
    {
        static void Check(string? text, int max, string what)
        {
            if (text is not null && System.Text.Encoding.UTF8.GetByteCount(text) > max)
            {
                throw new EditBlobException(what + " is too long.");
            }
        }

        Check(edit.Contents, EditBlobLimits.MaxContentsBytes, "Contents");
        Check(edit.Author, EditBlobLimits.MaxAuthorBytes, "The author");
        Check(edit.Name, EditBlobLimits.MaxNameBytes, "The name");
        Check(edit.ExpectSubtype, EditBlobLimits.MaxSubtypeBytes, "The expected subtype");
        if (edit.QuadPoints is { Length: > EditBlobLimits.MaxQuadFloats } || edit.Strokes is { Count: > EditBlobLimits.MaxStrokesPerAnnotation })
        {
            throw new EditBlobException("Too many quadrilaterals or strokes.");
        }

        if (edit.Strokes is not null && edit.Strokes.Any(static s => s.Length > EditBlobLimits.MaxFloatsPerStroke))
        {
            throw new EditBlobException("A stroke is too long.");
        }

        if (edit.Flags > EditBlobLimits.MaxFlags || edit.TargetObject > EditBlobLimits.MaxObjectNumber)
        {
            throw new EditBlobException("A flag or object number is out of range.");
        }

        if (!(edit.Opacity > 0f && edit.Opacity <= 1f) || !(edit.Width >= 0f && edit.Width <= EditBlobLimits.MaxWidth))
        {
            throw new EditBlobException("An opacity or width is out of range.");
        }

        static bool InRange(float[]? values, float min, float max, int? count = null) =>
            values is null || ((count is null || values.Length == count) && values.All(v => v >= min && v <= max));
        if (!InRange(edit.Rect, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate, 4)
            || !InRange(edit.ExpectRect, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate, 4)
            || !InRange(edit.Color, 0f, 1f, 3)
            || !InRange(edit.QuadPoints, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate)
            || (edit.Strokes is not null && !edit.Strokes.All(s => InRange(s, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate))))
        {
            throw new EditBlobException("A coordinate or colour is out of range or not finite.");
        }
    }

    private static void RequireNormalisedRect(float[]? rect, string message)
    {
        if (rect is null || rect.Length != 4 || !rect.All(static v => float.IsFinite(v) && Math.Abs(v) <= EditBlobLimits.MaxCoordinate) || rect[0] > rect[2] || rect[1] > rect[3])
        {
            throw new EditBlobException(message);
        }
    }

    private static void WriteString(ref MessagePackWriter writer, string? text)
    {
        if (text is null)
        {
            writer.WriteNil();
        }
        else
        {
            writer.Write(text);
        }
    }

    private static void WriteFloats(ref MessagePackWriter writer, float[]? values)
    {
        if (values is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    // A nil, or an array of float32 with a count in [minCount, maxCount] (the exact count when equal) and every value finite and in range.
    private static float[]? ReadFloats(ref MessagePackReader reader, int minCount, int maxCount, float min = -EditBlobLimits.MaxCoordinate, float max = EditBlobLimits.MaxCoordinate)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = StrictReader.ReadArrayHeader(ref reader, maxCount);
        if (count < minCount)
        {
            throw new EditBlobException("An array is shorter than its minimum.");
        }

        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = StrictReader.ReadF32(ref reader, min, max, minExclusive: false);
        }

        return values;
    }
}
