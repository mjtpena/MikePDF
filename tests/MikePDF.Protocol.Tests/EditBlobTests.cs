#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Buffers;
using MessagePack;
using MikePDF.Protocol.Edits;

namespace MikePDF.Protocol.Tests;

/// <summary>SP-10 (save-pipeline 14): the edit blob codec: round trips, every limit one over, malformed and truncated blobs.</summary>
public sealed class EditBlobTests
{
    // ---- building blobs by hand, so a test can write what the encoder would refuse ----

    private static void WriteItem(ref MessagePackWriter w, object? item)
    {
        switch (item)
        {
            case null: w.WriteNil(); break;
            case bool b: w.Write(b); break;
            case ushort u: w.Write(u); break;
            case uint u: w.Write(u); break;
            case ulong u: w.Write(u); break;
            case int i: w.Write(i); break;
            case float f: w.Write(f); break;
            case double d: w.Write(d); break;
            case string s: w.Write(s); break;
            case byte[] raw: w.WriteRaw(raw); break;
            case float[] floats:
                w.WriteArrayHeader(floats.Length);
                foreach (var f in floats) w.Write(f);
                break;
            case object?[] items:
                w.WriteArrayHeader(items.Length);
                foreach (var inner in items) WriteItem(ref w, inner);
                break;
            default: throw new ArgumentException(item.GetType().Name);
        }
    }

    private static byte[] Raw(params object?[] items)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        WriteItem(ref w, items);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The nineteen fields the second version of the blob appended, all "not applicable".</summary>
    private static object?[] Tail() =>
    [
        null, null, null, null, (ushort)0, null, (ushort)0, (ushort)0, null, 0u, (ushort)0, 0f, null, (ushort)0, null, null, (ushort)0, (ushort)0, null,
    ];

    /// <summary>The thirty-five fields of a valid Add of a highlight, in wire order.</summary>
    private static object?[] ValidAdd() =>
    [
        (ushort)1, 0u, (ushort)1, new[] { 10f, 20f, 110f, 40f }, new[] { 1f, 0.9f, 0f }, 0.5f, 1f, "note", "Mike", "name-1", 0u,
        new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f }, null, 0u, null, null, .. Tail(),
    ];

    private static object?[] ValidDelete() =>
    [
        (ushort)2, 0u, (ushort)0, null, null, 1f, 1f, null, null, null, 0u, null, null, 12u, "Highlight", new[] { 10f, 20f, 110f, 40f }, .. Tail(),
    ];

    /// <summary>Replaces the appended fields starting at wire index 16.</summary>
    private static object?[] WithTail(object?[] fields, int tailIndex, object? value) => With(fields, 16 + tailIndex, value);

    private static byte[] Blob(object? magic, object? version, object? pageCount, params object?[][] edits) => Raw(magic, version, pageCount, edits.Cast<object?>().ToArray());

    private static byte[] ValidBlob(params object?[][] edits) => Blob(EditBlobLimits.Magic, 2u, 2u, edits.Length == 0 ? [ValidAdd()] : edits);

    private static object?[] With(object?[] fields, int index, object? value)
    {
        var copy = (object?[])fields.Clone();
        copy[index] = value;
        return copy;
    }

    private static bool Decodes(byte[] bytes) => EditBlobCodec.TryDecode(bytes, out _, out _);

    private static EditBlob Sample() => new()
    {
        PageCount = 3,
        Edits =
        [
            new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Highlight, Rect = [10, 20, 110, 40], Color = [1, 0.9f, 0], Opacity = 0.5f, Name = "h", Author = "Mike", Contents = "café ☃ \U0001F600", Flags = 4, QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20] },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Ink, Rect = [0, 0, 50, 50], Color = [0, 0, 1], Width = 2.5f, Name = "i", Strokes = [[0, 0, 10, 10, 20, 5], [30, 30, 40, 40]] },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 2, Kind = AnnotationKind.Text, Rect = [5, 5, 25, 25], Color = [1, 1, 0], Name = "t", Contents = "" },
            new AnnotationEdit { Operation = EditOperation.Delete, Page = 0, TargetObject = 12, ExpectSubtype = "Highlight", ExpectRect = [10, 20, 110, 40] },
            new AnnotationEdit { Operation = EditOperation.Update, Page = 2, Name = "nm", ExpectSubtype = "Text", ExpectRect = [5, 5, 25, 25], Rect = [6, 6, 26, 26], Contents = "moved" },
        ],
    };

    // ---- round trips ----

    [Fact]
    public void EveryFieldOfEveryOperationRoundTrips()
    {
        var bytes = EditBlobCodec.Encode(Sample());

        Assert.True(EditBlobCodec.TryDecode(bytes, out var blob, out var error), error);

        Assert.Equal(3u, blob.PageCount);
        Assert.Equal(5, blob.Edits.Count);
        var highlight = blob.Edits[0];
        Assert.Equal((EditOperation.Add, AnnotationKind.Highlight, "h", "Mike", "café ☃ \U0001F600", 4u), (highlight.Operation, highlight.Kind, highlight.Name, highlight.Author, highlight.Contents, highlight.Flags));
        Assert.Equal(new[] { 10f, 20f, 110f, 40f }, highlight.Rect);
        Assert.Equal(0.5f, highlight.Opacity);
        Assert.Equal(new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f }, highlight.QuadPoints);
        Assert.Equal(2, blob.Edits[1].Strokes!.Count);
        Assert.Equal(new[] { 30f, 30f, 40f, 40f }, blob.Edits[1].Strokes![1]);
        Assert.Equal(2.5f, blob.Edits[1].Width);
        Assert.Equal(string.Empty, blob.Edits[2].Contents);
        Assert.Equal((EditOperation.Delete, 12u, "Highlight"), (blob.Edits[3].Operation, blob.Edits[3].TargetObject, blob.Edits[3].ExpectSubtype));
        Assert.Equal(new[] { 6f, 6f, 26f, 26f }, blob.Edits[4].Rect);
        Assert.Equal("moved", blob.Edits[4].Contents);
        Assert.Equal(bytes, EditBlobCodec.Encode(blob)); // canonical: decoding and encoding again gives the same bytes
    }

    [Fact]
    public void AnEmptyBlobIsValid()
    {
        var bytes = EditBlobCodec.Encode(new EditBlob { PageCount = 0, Edits = [] });

        Assert.True(EditBlobCodec.TryDecode(bytes, out var blob, out _));
        Assert.Empty(blob.Edits);
    }

    [Fact]
    public void TheHandBuiltValidBlobsDecode()
    {
        // The test builders agree with the codec, so the malformed cases below are malformed in exactly the way they say.
        Assert.True(Decodes(ValidBlob()));
        Assert.True(Decodes(ValidBlob(ValidDelete())));
    }

    // ---- limits: the writer refuses one over, the reader refuses one over ----

    [Theory]
    [InlineData("contents", 16385)]
    [InlineData("author", 257)]
    [InlineData("name", 129)]
    [InlineData("subtype", 33)]
    public void TextOverItsLimitIsRefusedByBothSides(string field, int bytes)
    {
        var text = new string('x', bytes);
        var tooLong = field switch
        {
            "contents" => new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Square, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = "n", Contents = text },
            "author" => new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Square, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = "n", Author = text },
            "name" => new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Square, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = text },
            _ => new AnnotationEdit { Operation = EditOperation.Delete, TargetObject = 1, ExpectSubtype = text, ExpectRect = [0, 0, 1, 1] },
        };
        var index = field switch { "contents" => 7, "author" => 8, "name" => 9, _ => 14 };
        var good = ValidAdd();
        var rawEdit = field == "subtype" ? ValidDelete() : good;

        Assert.Throws<EditBlobException>(() => EditBlobCodec.Encode(new EditBlob { PageCount = 1, Edits = [tooLong] }));
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 2u, 2u, With(rawEdit, index, text))));
        Assert.True(Decodes(Blob(EditBlobLimits.Magic, 2u, 2u, With(rawEdit, index, text[..^1])))); // one under is fine
    }

    [Fact]
    public void TheLimitsAreExactlyWhatTheSpecSays()
    {
        Assert.Equal(268_435_456L, EditBlobLimits.MaxBytes);
        Assert.Equal(1_000_000, EditBlobLimits.MaxEdits);
        Assert.Equal(16 * 1024, EditBlobLimits.MaxContentsBytes);
        Assert.Equal(100_000, EditBlobLimits.MaxStrokesPerAnnotation);
        Assert.Equal(1_000_000 * 2, EditBlobLimits.MaxFloatsPerStroke);
    }

    [Fact]
    public void CountsThatClaimMoreThanTheBytesCanHoldAreRefusedBeforeAnythingIsAllocated()
    {
        // An array header that says 1,000,000 edits followed by nothing; and a stroke of two million floats followed by nothing.
        var edits = new byte[] { 0xDD, 0x00, 0x0F, 0x42, 0x40 };
        var header = Raw(EditBlobLimits.Magic, 2u, 2u);
        var claim = new byte[] { 0x94 }.Concat(header.Skip(1)).Concat(edits).ToArray();
        Assert.False(Decodes(claim));

        var hugeStroke = With(ValidAdd(), 12, new object?[] { new byte[] { 0xDD, 0x00, 0x1E, 0x84, 0x80 } });
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 2u, 2u, hugeStroke)));
    }

    [Fact]
    public void MoreEditsThanTheLimitAreRefusedByTheWriter()
    {
        var edits = Enumerable.Repeat(new AnnotationEdit { Operation = EditOperation.Delete, TargetObject = 1, ExpectSubtype = "Text", ExpectRect = [0, 0, 1, 1] }, EditBlobLimits.MaxEdits + 1).ToList();

        Assert.Throws<EditBlobException>(() => EditBlobCodec.Encode(new EditBlob { PageCount = 1, Edits = edits }));
    }

    [Fact]
    public void QuadPointsAndStrokesOverTheirLimitsAreRefused()
    {
        var quads = new float[EditBlobLimits.MaxQuadFloats + 8];
        Assert.Throws<EditBlobException>(() => EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 1,
            Edits = [new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Highlight, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = "n", QuadPoints = quads }],
        }));
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 2u, 2u, With(ValidAdd(), 11, quads))));

        var strokes = Enumerable.Repeat(new float[] { 0, 0, 1, 1 }, EditBlobLimits.MaxStrokesPerAnnotation + 1).ToList();
        Assert.Throws<EditBlobException>(() => EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 1,
            Edits = [new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Ink, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = "n", Strokes = strokes }],
        }));
    }

    [Fact]
    public void StrokesOverTheTotalLimitAreRefused()
    {
        // Ten strokes of 1.7 million floats are within each stroke's limit and over the 16 million total.
        var stroke = new float[1_700_000];
        var strokes = Enumerable.Range(0, 10).Select(_ => stroke).ToList();

        Assert.Throws<EditBlobException>(() => EditBlobCodec.Encode(new EditBlob
        {
            PageCount = 1,
            Edits = [new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Ink, Rect = [0, 0, 1, 1], Color = [0, 0, 0], Name = "n", Strokes = strokes }],
        }));
    }

    // ---- malformed ----

    [Theory]
    [InlineData("magic")]
    [InlineData("version-zero")]
    [InlineData("version-one")]
    [InlineData("pages-over-limit")]
    [InlineData("edit-not-an-array")]
    [InlineData("fifteen-fields")]
    [InlineData("seventeen-fields")]
    [InlineData("operation-zero")]
    [InlineData("operation-five")]
    [InlineData("page-not-in-document")]
    [InlineData("kind-fourteen")]
    [InlineData("rect-three-numbers")]
    [InlineData("rect-reversed")]
    [InlineData("rect-nan")]
    [InlineData("rect-float64")]
    [InlineData("rect-infinite")]
    [InlineData("colour-above-one")]
    [InlineData("colour-two-components")]
    [InlineData("opacity-zero")]
    [InlineData("opacity-above-one")]
    [InlineData("width-negative")]
    [InlineData("width-above-1000")]
    [InlineData("flags-1024")]
    [InlineData("name-nil-on-add")]
    [InlineData("name-empty-on-add")]
    [InlineData("name-with-nul")]
    [InlineData("name-invalid-utf8")]
    [InlineData("quads-nil-on-highlight")]
    [InlineData("quads-seven")]
    [InlineData("quads-not-multiple-of-eight")]
    [InlineData("ink-without-strokes")]
    [InlineData("ink-stroke-empty")]
    [InlineData("ink-stroke-odd")]
    [InlineData("add-with-a-target")]
    [InlineData("delete-without-expectation")]
    [InlineData("delete-without-target")]
    [InlineData("target-above-limit")]
    [InlineData("update-changes-nothing")]
    [InlineData("string-for-page")]
    [InlineData("negative-page")]
    public void AMalformedBlobIsRefusedWithoutThrowing(string what)
    {
        var add = ValidAdd();
        var delete = ValidDelete();
        var bytes = what switch
        {
            "magic" => Blob("not-mikepdf", 1u, 2u, add),
            "version-zero" => Blob(EditBlobLimits.Magic, 0u, 2u, add),
            "version-one" => Blob(EditBlobLimits.Magic, 1u, 2u, add),
            "pages-over-limit" => Blob(EditBlobLimits.Magic, 2u, 10_000_001u, add),
            "edit-not-an-array" => Raw(EditBlobLimits.Magic, 2u, 2u, new object?[] { 5u }),
            "fifteen-fields" => Blob(EditBlobLimits.Magic, 2u, 2u, add[..15]),
            "seventeen-fields" => Blob(EditBlobLimits.Magic, 2u, 2u, [.. add, null]),
            "operation-zero" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 0, (ushort)0)),
            "operation-five" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 0, (ushort)5)),
            "page-not-in-document" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 1, 2u)),
            "kind-fourteen" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 2, (ushort)14)),
            "rect-three-numbers" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 3, new[] { 1f, 2f, 3f })),
            "rect-reversed" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 3, new[] { 50f, 20f, 10f, 40f })),
            "rect-nan" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 3, new[] { float.NaN, 0f, 1f, 1f })),
            "rect-float64" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 3, new object?[] { 1d, 2d, 3d, 4d })),
            "rect-infinite" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 3, new[] { 0f, 0f, float.PositiveInfinity, 1f })),
            "colour-above-one" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 4, new[] { 1.1f, 0f, 0f })),
            "colour-two-components" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 4, new[] { 1f, 0f })),
            "opacity-zero" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 5, 0f)),
            "opacity-above-one" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 5, 1.01f)),
            "width-negative" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 6, -1f)),
            "width-above-1000" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 6, 1000.5f)),
            "flags-1024" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 10, 1024u)),
            "name-nil-on-add" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 9, null)),
            "name-empty-on-add" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 9, "")),
            "name-with-nul" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 9, "a\0b")),
            "name-invalid-utf8" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 9, new byte[] { 0xA2, 0xFF, 0xFE })),
            "quads-nil-on-highlight" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 11, null)),
            "quads-seven" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 11, new float[7])),
            "quads-not-multiple-of-eight" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 11, new float[12])),
            "ink-without-strokes" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(add, 2, (ushort)5), 11, null)),
            "ink-stroke-empty" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(With(add, 2, (ushort)5), 11, null), 12, new object?[] { Array.Empty<float>() })),
            "ink-stroke-odd" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(With(add, 2, (ushort)5), 11, null), 12, new object?[] { new[] { 1f, 2f, 3f } })),
            "add-with-a-target" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 13, 5u)),
            "delete-without-expectation" => Blob(EditBlobLimits.Magic, 2u, 2u, With(delete, 14, null)),
            "delete-without-target" => Blob(EditBlobLimits.Magic, 2u, 2u, With(delete, 13, 0u)),
            "target-above-limit" => Blob(EditBlobLimits.Magic, 2u, 2u, With(delete, 13, 4_194_304u)),
            "update-changes-nothing" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(delete, 0, (ushort)3), 3, null)),
            "string-for-page" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 1, "0")),
            "negative-page" => Blob(EditBlobLimits.Magic, 2u, 2u, With(add, 1, -1)),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        var accepted = EditBlobCodec.TryDecode(bytes, out _, out var error);

        Assert.False(accepted, what);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TrailingBytesAreRefused()
    {
        Assert.False(Decodes([.. ValidBlob(), 0xC0]));
    }

    [Fact]
    public void NothingTruncatedDecodesAndNothingThrows()
    {
        var bytes = EditBlobCodec.Encode(Sample());

        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.False(Decodes(bytes[..length]), $"decoded a blob cut to {length} bytes");
        }
    }

    [Fact]
    public void FlippingBitsNeverThrowsAndNeverYieldsAnInvalidBlob()
    {
        var original = EditBlobCodec.Encode(Sample());
        var random = new Random(20261103);

        for (var round = 0; round < 3000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var flips = 1 + (round % 4); flips > 0; flips--)
            {
                mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
            }

            if (EditBlobCodec.TryDecode(mutated, out var blob, out _))
            {
                EditBlobCodec.Validate(blob); // whatever decodes is a blob the rules accept
                Assert.True(EditBlobCodec.Encode(blob).Length > 0);
            }
        }
    }

    [Fact]
    public void RandomBytesAreRefused()
    {
        var random = new Random(7);
        for (var round = 0; round < 500; round++)
        {
            var noise = new byte[random.Next(1, 300)];
            random.NextBytes(noise);
            Assert.False(Decodes(noise));
        }
    }

    [Fact]
    public void TheWriterErrorCodesNameWhatTheBrokerDecidesOn()
    {
        Assert.Equal("UnsupportedEncryption", WriterErrorCodes.NameOf(1));
        Assert.Equal("ExpectationMismatch", WriterErrorCodes.NameOf(30));
        Assert.Equal("Structure", WriterErrorCodes.NameOf(12));
    }

    // ---- version 2: the fields of the annotation work package ----

    private static EditBlob EveryKind() => new()
    {
        PageCount = 2,
        Edits =
        [
            new AnnotationEdit
            {
                Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Squiggly, Rect = [10, 20, 110, 40], Color = [1, 0, 0], Name = "sq", QuadPoints = [10, 40, 110, 40, 10, 20, 110, 20],
                Subject = "Squiggly", Created = "D:20261104101530+01'00'", Modified = "D:20261104101530Z", PopupRect = [120, 20, 300, 120],
            },
            new AnnotationEdit
            {
                Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.FreeText, Rect = [10, 20, 190, 60], Color = [0, 0, 1], Name = "ft", Contents = "Hello", InteriorColor = [1, 1, 0.8f], Width = 2,
                FontId = 5, FontSize = 12, TextColor = [0, 0, 0], Align = 1, Traits = EditTraits.Typewriter,
            },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 0, Kind = AnnotationKind.Line, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "ln", Vertices = [0, 0, 100, 50], LineStart = LineEndKind.None, LineEnd = LineEndKind.OpenArrow, Dash = DashKind.Dashed },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Polygon, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "pg", Vertices = [0, 0, 100, 0, 50, 50], InteriorColor = [0, 1, 0], Dash = DashKind.Dotted },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.PolyLine, Rect = [0, 0, 100, 50], Color = [0, 0, 0], Name = "pl", Vertices = [0, 0, 100, 0], LineEnd = LineEndKind.Diamond },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Stamp, Rect = [0, 0, 150, 40], Color = [0, 0.5f, 0], Name = "st", StandardName = "Approved", Traits = (EditTraits)(2u << EditTraitBits.TurnsShift) },
            new AnnotationEdit
            {
                Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Ink, Rect = [0, 0, 50, 50], Color = [0, 0, 1], Name = "ink", Strokes = [[0, 0, 10, 10, 20, 5], [30, 30]], InkWidths = [[1, 2, 1], [3]], Traits = EditTraits.Highlighter,
            },
            new AnnotationEdit { Operation = EditOperation.Add, Page = 1, Kind = AnnotationKind.Text, Rect = [5, 5, 25, 25], Color = [1, 1, 0], Name = "rp", Contents = "Agreed", ParentName = "ink", ReplyKind = 2, State = 1, StandardName = "Comment" },
            new AnnotationEdit { Operation = EditOperation.Replace, Page = 0, Name = "ft", ExpectSubtype = "FreeText", ExpectRect = [10, 20, 190, 60], Kind = AnnotationKind.FreeText, Rect = [10, 20, 200, 70], Color = [0, 0, 1], FontSize = 14, TextColor = [0, 0, 0] },
        ],
    };

    [Fact]
    public void TheFieldsOfEveryNewKindRoundTrip()
    {
        var bytes = EditBlobCodec.Encode(EveryKind());

        Assert.True(EditBlobCodec.TryDecode(bytes, out var blob, out var error), error);

        Assert.Equal(9, blob.Edits.Count);
        Assert.Equal("D:20261104101530+01'00'", blob.Edits[0].Created);
        Assert.Equal(new[] { 120f, 20f, 300f, 120f }, blob.Edits[0].PopupRect);
        var text = blob.Edits[1];
        Assert.Equal((AnnotationKind.FreeText, (ushort)5, 12f, (ushort)1, EditTraits.Typewriter), (text.Kind, text.FontId, text.FontSize, text.Align, text.Traits));
        Assert.Equal(new[] { 1f, 1f, 0.8f }, text.InteriorColor);
        Assert.Equal((LineEndKind.OpenArrow, DashKind.Dashed), (blob.Edits[2].LineEnd, blob.Edits[2].Dash));
        Assert.Equal(new[] { 0f, 0f, 100f, 0f, 50f, 50f }, blob.Edits[3].Vertices);
        Assert.Equal(2, blob.Edits[5].StampTurns);
        Assert.Equal("Approved", blob.Edits[5].StandardName);
        Assert.Equal(new[] { 3f }, blob.Edits[6].InkWidths![1]);
        Assert.Equal((1, "ink", (ushort)2, (ushort)1), (blob.Edits[7].ReplyKind == 2 ? 1 : 0, blob.Edits[7].ParentName, blob.Edits[7].ReplyKind, blob.Edits[7].State));
        Assert.Equal(EditOperation.Replace, blob.Edits[8].Operation);
        Assert.Equal(bytes, EditBlobCodec.Encode(blob));
    }

    [Theory]
    [InlineData("squiggly-without-quads")]
    [InlineData("line-with-three-numbers")]
    [InlineData("polygon-with-two-vertices")]
    [InlineData("polyline-with-one-vertex")]
    [InlineData("polygon-with-an-odd-count")]
    [InlineData("stamp-without-a-name")]
    [InlineData("stamp-name-with-a-space")]
    [InlineData("free-text-without-a-size")]
    [InlineData("free-text-without-a-colour")]
    [InlineData("font-twelve")]
    [InlineData("align-three")]
    [InlineData("dash-three")]
    [InlineData("line-end-six")]
    [InlineData("traits-sixteen")]
    [InlineData("date-not-a-date")]
    [InlineData("date-month-text")]
    [InlineData("ink-widths-for-one-stroke-of-two")]
    [InlineData("ink-widths-of-the-wrong-length")]
    [InlineData("ink-width-negative")]
    [InlineData("reply-on-a-highlight")]
    [InlineData("reply-without-a-parent")]
    [InlineData("parent-without-a-reply")]
    [InlineData("state-without-a-review")]
    [InlineData("state-five")]
    [InlineData("popup-reversed")]
    [InlineData("replace-without-expectation")]
    [InlineData("interior-colour-two-components")]
    [InlineData("vertices-over-the-limit")]
    public void AMalformedNewFieldIsRefusedWithoutThrowing(string what)
    {
        var line = With(With(With(ValidAdd(), 2, (ushort)10), 11, null), 12, null);
        line = WithTail(line, 5, new[] { 0f, 0f, 10f, 10f });
        var ink = With(With(With(ValidAdd(), 2, (ushort)5), 11, null), 12, new object?[] { new[] { 0f, 0f, 1f, 1f }, new[] { 2f, 2f } });
        var reply = With(WithTail(ValidAdd(), 16, (ushort)1), 2, (ushort)4);
        reply = WithTail(WithTail(reply, 15, "parent"), 16, (ushort)1);
        var freeText = With(With(WithTail(ValidAdd(), 11, 12f), 2, (ushort)9), 11, null);
        freeText = WithTail(freeText, 12, new[] { 0f, 0f, 0f });
        var replace = With(With(ValidDelete(), 0, (ushort)4), 2, (ushort)1);
        var bytes = what switch
        {
            "squiggly-without-quads" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(ValidAdd(), 2, (ushort)8), 11, null)),
            "line-with-three-numbers" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(line, 5, new[] { 0f, 0f, 10f })),
            "polygon-with-two-vertices" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(With(line, 2, (ushort)12), 5, new[] { 0f, 0f, 10f, 10f })),
            "polyline-with-one-vertex" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(With(line, 2, (ushort)11), 5, new[] { 0f, 0f })),
            "polygon-with-an-odd-count" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(With(line, 2, (ushort)12), 5, new float[7])),
            "stamp-without-a-name" => Blob(EditBlobLimits.Magic, 2u, 2u, With(With(ValidAdd(), 2, (ushort)13), 11, null)),
            "stamp-name-with-a-space" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(With(With(ValidAdd(), 2, (ushort)13), 11, null), 8, "Not Approved")),
            "free-text-without-a-size" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(freeText, 11, 0f)),
            "free-text-without-a-colour" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(freeText, 12, null)),
            "font-twelve" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(freeText, 10, (ushort)12)),
            "align-three" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(freeText, 13, (ushort)3)),
            "dash-three" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 4, (ushort)3)),
            "line-end-six" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(line, 7, (ushort)6)),
            "traits-sixteen" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 9, 16u)),
            "date-not-a-date" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 2, "yesterday")),
            "date-month-text" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 3, "D:2026AB04101530Z")),
            "ink-widths-for-one-stroke-of-two" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ink, 14, new object?[] { new[] { 1f, 1f } })),
            "ink-widths-of-the-wrong-length" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ink, 14, new object?[] { new[] { 1f }, new[] { 1f } })),
            "ink-width-negative" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ink, 14, new object?[] { new[] { 1f, -1f }, new[] { 1f } })),
            "reply-on-a-highlight" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(WithTail(ValidAdd(), 15, "p"), 16, (ushort)1)),
            "reply-without-a-parent" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(reply, 15, null)),
            "parent-without-a-reply" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 15, "p")),
            "state-without-a-review" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(WithTail(reply, 16, (ushort)1), 17, (ushort)2)),
            "state-five" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(WithTail(reply, 16, (ushort)2), 17, (ushort)5)),
            "popup-reversed" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 18, new[] { 50f, 0f, 10f, 10f })),
            "replace-without-expectation" => Blob(EditBlobLimits.Magic, 2u, 2u, With(replace, 14, null)),
            "interior-colour-two-components" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(ValidAdd(), 0, new[] { 1f, 0f })),
            "vertices-over-the-limit" => Blob(EditBlobLimits.Magic, 2u, 2u, WithTail(line, 5, new float[EditBlobLimits.MaxVertexFloats + 2])),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        var accepted = EditBlobCodec.TryDecode(bytes, out _, out var error);

        Assert.False(accepted, what);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("D:20261104101530", true)]
    [InlineData("D:20261104101530Z", true)]
    [InlineData("D:20261104101530+01'00'", true)]
    [InlineData("D:20261104101530-05'30'", true)]
    [InlineData("D:20261104101530+0530", false)]
    [InlineData("D:20261104101530+05'60'", false)]
    [InlineData("D:20261104101530+25'00'", false)]
    [InlineData("D:2026110410153", false)]
    [InlineData("20261104101530", false)]
    [InlineData("D:20261104101530Zx", false)]
    public void ADateIsAPdfDateOrItIsNot(string text, bool expected)
    {
        Assert.Equal(expected, EditBlobCodec.IsPdfDate(text));
    }

    [Fact]
    public void AReplyBeforeItsParentIsNotTheBlobsConcernButTheEncoderStillWritesWhatTheRulesAccept()
    {
        var reply = new AnnotationEdit { Operation = EditOperation.Add, Kind = AnnotationKind.Text, Rect = [0, 0, 20, 20], Color = [1, 1, 0], Name = "r", ParentName = "root", ReplyKind = 1, Contents = "ok" };

        var bytes = EditBlobCodec.Encode(new EditBlob { PageCount = 1, Edits = [reply] });

        Assert.True(EditBlobCodec.TryDecode(bytes, out var blob, out _));
        Assert.Equal("root", blob.Edits[0].ParentName);
    }

    // ---- versions: a blob of another version is refused with a reason, never an exception ----

    [Fact]
    public void AVersionOneBlobIsRefusedAsAnUnsupportedVersionSoRecoveryCanSaySo()
    {
        // The first version: sixteen fields to an edit. Nothing shipped with it, so this build does not read it; from v1.0 on each version reads the ones before.
        var v1 = Raw(EditBlobLimits.Magic, 1u, 2u, new object?[]
        {
            new object?[] { (ushort)1, 0u, (ushort)1, new[] { 10f, 20f, 110f, 40f }, new[] { 1f, 0.9f, 0f }, 0.5f, 1f, "note", "Mike", "name-1", 0u, new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f }, null, 0u, null, null },
        });

        var result = EditBlobCodec.Decode(v1);

        Assert.False(result.IsOk);
        Assert.Equal((EditBlobFailure.UnsupportedVersion, 1u), (result.Failure, result.Version));
        Assert.Null(result.Blob);
        Assert.Contains("version 1", result.Message, StringComparison.Ordinal);
        Assert.False(Decodes(v1));
    }

    [Fact]
    public void ABlobOfAFutureVersionIsRefusedTheSameWay()
    {
        var result = EditBlobCodec.Decode(Blob(EditBlobLimits.Magic, 3u, 2u, ValidAdd()));

        Assert.Equal((EditBlobFailure.UnsupportedVersion, 3u), (result.Failure, result.Version));
    }

    [Fact]
    public void EveryOtherRefusalHasItsOwnTypedReason()
    {
        Assert.Equal(EditBlobFailure.None, EditBlobCodec.Decode(ValidBlob()).Failure);
        Assert.Equal(EditBlobFailure.NotAnEditBlob, EditBlobCodec.Decode(Blob("not-mikepdf", 2u, 2u, ValidAdd())).Failure);
        Assert.Equal(EditBlobFailure.Malformed, EditBlobCodec.Decode(Blob(EditBlobLimits.Magic, 2u, 2u, With(ValidAdd(), 5, 0f))).Failure);
        Assert.Equal(EditBlobFailure.Malformed, EditBlobCodec.Decode(new byte[] { 1, 2, 3 }).Failure);
        Assert.Equal(EditBlobFailure.Malformed, EditBlobCodec.Decode(ReadOnlyMemory<byte>.Empty).Failure);
        Assert.Equal(EditBlobFailure.TooLarge, EditBlobCodec.Decode(new byte[EditBlobLimits.MaxBytes + 1]).Failure);
        foreach (var length in new[] { 0, 1, 5, 20 })
        {
            Assert.False(EditBlobCodec.Decode(EditBlobCodec.Encode(Sample()).AsMemory(0, length)).IsOk); // nothing here throws either
        }
    }
}
