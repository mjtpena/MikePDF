using System.Buffers;
using MessagePack;

namespace MikePDF.Protocol.Edits;

/// <summary>An edit blob that breaks a limit or a rule, on write or on read.</summary>
public sealed class EditBlobException(string message) : Exception(message);

/// <summary>Why a blob was not read (<see cref="EditBlobCodec.Decode"/>).</summary>
public enum EditBlobFailure
{
    None = 0,

    /// <summary>Over the size limit.</summary>
    TooLarge,

    /// <summary>Does not start with the magic: not one of ours.</summary>
    NotAnEditBlob,

    /// <summary>One of ours, of a version this build does not read. Until v1.0 ships an older version is refused; from v1.0 on every later version reads the earlier blobs.</summary>
    UnsupportedVersion,

    /// <summary>The right version, broken or over a limit.</summary>
    Malformed,
}

/// <summary>The outcome of <see cref="EditBlobCodec.Decode"/>: the blob, or why not (and the version the blob claimed, when it got that far).</summary>
public sealed record EditBlobDecodeResult(EditBlob? Blob, EditBlobFailure Failure, string Message, uint Version)
{
    public bool IsOk => Failure == EditBlobFailure.None;
}

/// <summary>The limits of the edit blob (save-pipeline 11.3). Both the writer and the reader enforce all of them.</summary>
public static class EditBlobLimits
{
    public const int Version = 2;
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

    public const int MaxSubjectBytes = 64;
    public const int MaxDateBytes = 32;
    public const int MaxStandardNameBytes = 64;

    /// <summary>A polygon or polyline: 1,000 vertices, two numbers each (shapes 3.1).</summary>
    public const int MaxVertexFloats = 2_000;

    public const float MaxFontSize = 1000f;
    public const ushort MaxFontId = 11;
    public const uint MaxTraits = 15;
    public const ushort MaxLineEnd = 5;
}

/// <summary>
/// MessagePack, strict, with every limit checked before anything is allocated: <c>[magic, version, pageCount, [edit...]]</c>, each edit an array of
/// thirty-five fields in a fixed order (nil where a field does not apply). The reader is a parser of broker-produced data inside the sandbox; it is
/// still fuzzed, as defence in depth. It reuses the protocol's strict primitives, so a value of the wrong type or outside its range is refused
/// the same way as in a frame. Nothing here recurses.
/// </summary>
public static class EditBlobCodec
{
    private const int EditFields = 35;

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
            WriteFloats(ref writer, edit.InteriorColor);
            WriteString(ref writer, edit.Subject);
            WriteString(ref writer, edit.Created);
            WriteString(ref writer, edit.Modified);
            writer.Write((ushort)edit.Dash);
            WriteFloats(ref writer, edit.Vertices);
            writer.Write((ushort)edit.LineStart);
            writer.Write((ushort)edit.LineEnd);
            WriteString(ref writer, edit.StandardName);
            writer.Write((uint)edit.Traits);
            writer.Write(edit.FontId);
            writer.Write(edit.FontSize);
            WriteFloats(ref writer, edit.TextColor);
            writer.Write(edit.Align);
            if (edit.InkWidths is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.WriteArrayHeader(edit.InkWidths.Count);
                foreach (var widths in edit.InkWidths)
                {
                    WriteFloats(ref writer, widths);
                }
            }

            WriteString(ref writer, edit.ParentName);
            writer.Write(edit.ReplyKind);
            writer.Write(edit.State);
            WriteFloats(ref writer, edit.PopupRect);
        }

        writer.Flush();
        if (buffer.WrittenCount > EditBlobLimits.MaxBytes)
        {
            throw new EditBlobException("The edit blob is over 256 MiB.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a blob. Never throws for bad content: the reason is in <paramref name="error"/>.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> data, out EditBlob blob, out string error) => TryDecode(data, out blob, out error, out _, out _);

    /// <summary>
    /// Reads a blob and says why it was refused, as a value a caller can act on (recovery tells the person that a blob from an older build is not readable,
    /// rather than that "something is malformed"). Never throws for bad content.
    /// </summary>
    public static EditBlobDecodeResult Decode(ReadOnlyMemory<byte> data) =>
        TryDecode(data, out var blob, out var error, out var failure, out var version) ? new EditBlobDecodeResult(blob, EditBlobFailure.None, string.Empty, version) : new EditBlobDecodeResult(null, failure, error, version);

    /// <summary>The same, with the reason as a value. <paramref name="version"/> is the version the blob says it is, when it got that far (0 before).</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> data, out EditBlob blob, out string error, out EditBlobFailure failure, out uint version)
    {
        blob = null!;
        failure = EditBlobFailure.Malformed;
        version = 0;
        if (data.Length > EditBlobLimits.MaxBytes)
        {
            error = "The edit blob is over its size limit.";
            failure = EditBlobFailure.TooLarge;
            return false;
        }

        try
        {
            var reader = new MessagePackReader(data);
            StrictReader.ExpectArray(ref reader, 4);
            if (StrictReader.ReadString(ref reader, 64) != EditBlobLimits.Magic)
            {
                error = "Not an edit blob.";
                failure = EditBlobFailure.NotAnEditBlob;
                return false;
            }

            version = StrictReader.ReadU32(ref reader, 0, uint.MaxValue);
            if (version != EditBlobLimits.Version)
            {
                error = $"Edit blob version {version} is not one this build reads.";
                failure = EditBlobFailure.UnsupportedVersion;
                return false;
            }

            var pageCount = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxPages);
            var count = StrictReader.ReadArrayHeader(ref reader, EditBlobLimits.MaxEdits);
            var edits = new List<AnnotationEdit>(Math.Min(count, 4096));
            long strokeFloats = 0;
            for (var i = 0; i < count; i++)
            {
                StrictReader.ExpectArray(ref reader, EditFields);
                var operation = (EditOperation)StrictReader.ReadU16(ref reader, 1, 4);
                var page = StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxPages - 1);
                var kind = (AnnotationKind)StrictReader.ReadU16(ref reader, 0, 13);
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
                var interior = ReadFloats(ref reader, 3, 3, 0f, 1f);
                var subject = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxSubjectBytes);
                var created = ReadDate(ref reader);
                var modified = ReadDate(ref reader);
                var dash = (DashKind)StrictReader.ReadU16(ref reader, 0, 2);
                var vertices = ReadFloats(ref reader, 0, EditBlobLimits.MaxVertexFloats);
                var lineStart = (LineEndKind)StrictReader.ReadU16(ref reader, 0, EditBlobLimits.MaxLineEnd);
                var lineEnd = (LineEndKind)StrictReader.ReadU16(ref reader, 0, EditBlobLimits.MaxLineEnd);
                var standardName = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxStandardNameBytes);
                var traits = (EditTraits)StrictReader.ReadU32(ref reader, 0, EditBlobLimits.MaxTraits);
                var fontId = StrictReader.ReadU16(ref reader, 0, EditBlobLimits.MaxFontId);
                var fontSize = StrictReader.ReadF32(ref reader, 0f, EditBlobLimits.MaxFontSize, minExclusive: false);
                var textColor = ReadFloats(ref reader, 3, 3, 0f, 1f);
                var align = StrictReader.ReadU16(ref reader, 0, 2);
                float[][]? inkWidths = null;
                if (!reader.TryReadNil())
                {
                    var widthCount = StrictReader.ReadArrayHeader(ref reader, EditBlobLimits.MaxStrokesPerAnnotation);
                    inkWidths = new float[widthCount][];
                    for (var s = 0; s < widthCount; s++)
                    {
                        inkWidths[s] = ReadFloats(ref reader, 0, EditBlobLimits.MaxFloatsPerStroke / 2, 0f, EditBlobLimits.MaxWidth) ?? throw new EditBlobException("A width list is nil.");
                        strokeFloats += inkWidths[s].Length;
                        if (strokeFloats > EditBlobLimits.MaxStrokeFloatsTotal)
                        {
                            error = "The strokes are over their total limit.";
                            return false;
                        }
                    }
                }

                var parentName = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxNameBytes);
                var replyKind = StrictReader.ReadU16(ref reader, 0, 2);
                var state = StrictReader.ReadU16(ref reader, 0, 4);
                var popupRect = ReadFloats(ref reader, 4, 4);
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
                    InteriorColor = interior,
                    Subject = subject,
                    Created = created,
                    Modified = modified,
                    Dash = dash,
                    Vertices = vertices,
                    LineStart = lineStart,
                    LineEnd = lineEnd,
                    StandardName = standardName,
                    Traits = traits,
                    FontId = fontId,
                    FontSize = fontSize,
                    TextColor = textColor,
                    Align = align,
                    InkWidths = inkWidths,
                    ParentName = parentName,
                    ReplyKind = replyKind,
                    State = state,
                    PopupRect = popupRect,
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
                    if (edit.TargetObject != 0 || edit.ExpectSubtype is not null || edit.ExpectRect is not null)
                    {
                        throw new EditBlobException("An added annotation has no target.");
                    }

                    if (string.IsNullOrEmpty(edit.Name))
                    {
                        throw new EditBlobException("An added annotation needs a colour and a name.");
                    }

                    strokeFloats += ValidateContent(edit);
                    break;
                case EditOperation.Replace:
                    RequireTarget(edit);
                    strokeFloats += ValidateContent(edit);
                    break;
                case EditOperation.Delete:
                case EditOperation.Update:
                    RequireTarget(edit);
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

    private static void RequireTarget(AnnotationEdit edit)
    {
        if (edit.TargetObject == 0 && string.IsNullOrEmpty(edit.Name))
        {
            throw new EditBlobException("An edit of a loaded annotation names its object or its /NM.");
        }

        if (string.IsNullOrEmpty(edit.ExpectSubtype) || edit.ExpectRect is null)
        {
            throw new EditBlobException("An edit of a loaded annotation carries what it expects to find.");
        }
    }

    /// <summary>What an Add or a Replace has to carry for its kind. Returns the number of stroke floats it holds, for the blob's total.</summary>
    private static long ValidateContent(AnnotationEdit edit)
    {
        RequireNormalisedRect(edit.Rect, "An added annotation needs a rectangle.");
        if (edit.Kind is < AnnotationKind.Highlight or > AnnotationKind.Stamp)
        {
            throw new EditBlobException("An added annotation needs a known kind.");
        }

        if (edit.Color is null)
        {
            throw new EditBlobException("An added annotation needs a colour and a name.");
        }

        if (edit.ReplyKind != 0)
        {
            if (edit.Kind != AnnotationKind.Text || string.IsNullOrEmpty(edit.ParentName))
            {
                throw new EditBlobException("A reply is a text annotation that names the annotation it answers.");
            }
        }
        else if (edit.ParentName is not null || edit.State != 0)
        {
            throw new EditBlobException("Only a reply names a parent or carries a state.");
        }

        if (edit.State != 0 && edit.ReplyKind != 2)
        {
            throw new EditBlobException("Only a review state carries a state.");
        }

        long strokeFloats = 0;
        switch (edit.Kind)
        {
            case AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly:
                if (edit.QuadPoints is null || edit.QuadPoints.Length == 0 || edit.QuadPoints.Length % 8 != 0)
                {
                    throw new EditBlobException("A text markup needs its quadrilaterals.");
                }

                break;
            case AnnotationKind.Ink:
                if (edit.Strokes is null || edit.Strokes.Count == 0)
                {
                    throw new EditBlobException("An ink annotation needs strokes.");
                }

                foreach (var stroke in edit.Strokes)
                {
                    strokeFloats += stroke.Length;
                    if (stroke.Length < 2 || stroke.Length % 2 != 0)
                    {
                        throw new EditBlobException("A stroke needs at least one point.");
                    }
                }

                if (edit.InkWidths is not null)
                {
                    if (edit.InkWidths.Count != edit.Strokes.Count)
                    {
                        throw new EditBlobException("Ink widths need one list for each stroke.");
                    }

                    for (var i = 0; i < edit.Strokes.Count; i++)
                    {
                        if (edit.InkWidths[i].Length * 2 != edit.Strokes[i].Length)
                        {
                            throw new EditBlobException("Ink widths need one width for each point.");
                        }

                        strokeFloats += edit.InkWidths[i].Length;
                    }
                }

                break;
            case AnnotationKind.Line:
                if (edit.Vertices is not { Length: 4 })
                {
                    throw new EditBlobException("A line needs two end points.");
                }

                break;
            case AnnotationKind.Polygon:
                if (edit.Vertices is null || edit.Vertices.Length < 6 || edit.Vertices.Length % 2 != 0)
                {
                    throw new EditBlobException("A polygon needs at least three vertices.");
                }

                break;
            case AnnotationKind.PolyLine:
                if (edit.Vertices is null || edit.Vertices.Length < 4 || edit.Vertices.Length % 2 != 0)
                {
                    throw new EditBlobException("A polyline needs at least two vertices.");
                }

                break;
            case AnnotationKind.Stamp:
                if (string.IsNullOrEmpty(edit.StandardName) || !edit.StandardName.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')))
                {
                    throw new EditBlobException("A stamp needs its standard name.");
                }

                break;
            case AnnotationKind.FreeText:
                if (!(edit.FontSize >= 1f) || edit.TextColor is null)
                {
                    throw new EditBlobException("A text box needs a font size and a text colour.");
                }

                break;
        }

        return strokeFloats;
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
        Check(edit.Subject, EditBlobLimits.MaxSubjectBytes, "The subject");
        Check(edit.StandardName, EditBlobLimits.MaxStandardNameBytes, "The standard name");
        Check(edit.ParentName, EditBlobLimits.MaxNameBytes, "The parent name");
        if ((edit.Created is not null && !IsPdfDate(edit.Created)) || (edit.Modified is not null && !IsPdfDate(edit.Modified)))
        {
            throw new EditBlobException("A date is not a PDF date.");
        }

        if (edit.QuadPoints is { Length: > EditBlobLimits.MaxQuadFloats } || edit.Strokes is { Count: > EditBlobLimits.MaxStrokesPerAnnotation }
            || edit.InkWidths is { Count: > EditBlobLimits.MaxStrokesPerAnnotation } || edit.Vertices is { Length: > EditBlobLimits.MaxVertexFloats })
        {
            throw new EditBlobException("Too many quadrilaterals, vertices or strokes.");
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

        if ((uint)edit.Traits > EditBlobLimits.MaxTraits || edit.FontId > EditBlobLimits.MaxFontId || edit.Align > 2 || edit.ReplyKind > 2 || edit.State > 4
            || (ushort)edit.Dash > 2 || (ushort)edit.LineStart > EditBlobLimits.MaxLineEnd || (ushort)edit.LineEnd > EditBlobLimits.MaxLineEnd
            || !(edit.FontSize >= 0f && edit.FontSize <= EditBlobLimits.MaxFontSize))
        {
            throw new EditBlobException("A style value is out of range.");
        }

        static bool InRange(float[]? values, float min, float max, int? count = null) =>
            values is null || ((count is null || values.Length == count) && values.All(v => v >= min && v <= max));
        if (!InRange(edit.Rect, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate, 4)
            || !InRange(edit.ExpectRect, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate, 4)
            || !InRange(edit.PopupRect, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate, 4)
            || !InRange(edit.Vertices, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate)
            || !InRange(edit.Color, 0f, 1f, 3)
            || !InRange(edit.InteriorColor, 0f, 1f, 3)
            || !InRange(edit.TextColor, 0f, 1f, 3)
            || !InRange(edit.QuadPoints, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate)
            || (edit.Strokes is not null && !edit.Strokes.All(s => InRange(s, -EditBlobLimits.MaxCoordinate, EditBlobLimits.MaxCoordinate)))
            || (edit.InkWidths is not null && !edit.InkWidths.All(w => InRange(w, 0f, EditBlobLimits.MaxWidth))))
        {
            throw new EditBlobException("A coordinate or colour is out of range or not finite.");
        }

        if (edit.PopupRect is { } popup && (popup[0] > popup[2] || popup[1] > popup[3]))
        {
            throw new EditBlobException("A popup's rectangle is not normalised.");
        }
    }

    /// <summary>D:YYYYMMDDHHmmSS, then nothing, or Z, or +HH'mm' or -HH'mm' (the last apostrophe optional; 7.9.4).</summary>
    public static bool IsPdfDate(string text)
    {
        if (text.Length is < 16 or > EditBlobLimits.MaxDateBytes || text[0] != 'D' || text[1] != ':')
        {
            return false;
        }

        for (var i = 2; i < 16; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        var rest = text.AsSpan(16);
        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest[0] == 'Z')
        {
            return rest.Length == 1 || rest[1..] is "00'00'" or "00'00" or "00";
        }

        if (rest[0] is not ('+' or '-') || rest.Length < 3 || rest[1] is < '0' or > '2' || rest[2] is < '0' or > '9' || (rest[1] == '2' && rest[2] > '3'))
        {
            return false;
        }

        var tail = rest[3..];
        if (tail.IsEmpty || tail is "'")
        {
            return true;
        }

        if (tail[0] != '\'' || tail.Length < 3 || tail[1] is < '0' or > '5' || tail[2] is < '0' or > '9')
        {
            return false;
        }

        return tail.Length == 3 || (tail.Length == 4 && tail[3] == '\'');
    }

    private static void RequireNormalisedRect(float[]? rect, string message)
    {
        if (rect is null || rect.Length != 4 || !rect.All(static v => float.IsFinite(v) && Math.Abs(v) <= EditBlobLimits.MaxCoordinate) || rect[0] > rect[2] || rect[1] > rect[3])
        {
            throw new EditBlobException(message);
        }
    }

    private static string? ReadDate(ref MessagePackReader reader)
    {
        var text = StrictReader.ReadOptionalString(ref reader, EditBlobLimits.MaxDateBytes);
        return text is not null && !IsPdfDate(text) ? throw new EditBlobException("A date is not a PDF date.") : text;
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
