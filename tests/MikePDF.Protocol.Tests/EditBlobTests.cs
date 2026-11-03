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

    /// <summary>The sixteen fields of a valid Add of a highlight, in wire order.</summary>
    private static object?[] ValidAdd() =>
    [
        (ushort)1, 0u, (ushort)1, new[] { 10f, 20f, 110f, 40f }, new[] { 1f, 0.9f, 0f }, 0.5f, 1f, "note", "Mike", "name-1", 0u,
        new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f }, null, 0u, null, null,
    ];

    private static object?[] ValidDelete() =>
    [
        (ushort)2, 0u, (ushort)0, null, null, 1f, 1f, null, null, null, 0u, null, null, 12u, "Highlight", new[] { 10f, 20f, 110f, 40f },
    ];

    private static byte[] Blob(object? magic, object? version, object? pageCount, params object?[][] edits) => Raw(magic, version, pageCount, edits.Cast<object?>().ToArray());

    private static byte[] ValidBlob(params object?[][] edits) => Blob(EditBlobLimits.Magic, 1u, 2u, edits.Length == 0 ? [ValidAdd()] : edits);

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
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 1u, 2u, With(rawEdit, index, text))));
        Assert.True(Decodes(Blob(EditBlobLimits.Magic, 1u, 2u, With(rawEdit, index, text[..^1])))); // one under is fine
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
        var header = Raw(EditBlobLimits.Magic, 1u, 2u);
        var claim = new byte[] { 0x94 }.Concat(header.Skip(1)).Concat(edits).ToArray();
        Assert.False(Decodes(claim));

        var hugeStroke = With(ValidAdd(), 12, new object?[] { new byte[] { 0xDD, 0x00, 0x1E, 0x84, 0x80 } });
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 1u, 2u, hugeStroke)));
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
        Assert.False(Decodes(Blob(EditBlobLimits.Magic, 1u, 2u, With(ValidAdd(), 11, quads))));

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
    [InlineData("version-two")]
    [InlineData("pages-over-limit")]
    [InlineData("edit-not-an-array")]
    [InlineData("fifteen-fields")]
    [InlineData("seventeen-fields")]
    [InlineData("operation-zero")]
    [InlineData("operation-four")]
    [InlineData("page-not-in-document")]
    [InlineData("kind-eight")]
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
    [InlineData("ink-stroke-one-point")]
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
            "version-two" => Blob(EditBlobLimits.Magic, 2u, 2u, add),
            "pages-over-limit" => Blob(EditBlobLimits.Magic, 1u, 10_000_001u, add),
            "edit-not-an-array" => Raw(EditBlobLimits.Magic, 1u, 2u, new object?[] { 5u }),
            "fifteen-fields" => Blob(EditBlobLimits.Magic, 1u, 2u, add[..15]),
            "seventeen-fields" => Blob(EditBlobLimits.Magic, 1u, 2u, [.. add, null]),
            "operation-zero" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 0, (ushort)0)),
            "operation-four" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 0, (ushort)4)),
            "page-not-in-document" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 1, 2u)),
            "kind-eight" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 2, (ushort)8)),
            "rect-three-numbers" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 3, new[] { 1f, 2f, 3f })),
            "rect-reversed" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 3, new[] { 50f, 20f, 10f, 40f })),
            "rect-nan" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 3, new[] { float.NaN, 0f, 1f, 1f })),
            "rect-float64" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 3, new object?[] { 1d, 2d, 3d, 4d })),
            "rect-infinite" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 3, new[] { 0f, 0f, float.PositiveInfinity, 1f })),
            "colour-above-one" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 4, new[] { 1.1f, 0f, 0f })),
            "colour-two-components" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 4, new[] { 1f, 0f })),
            "opacity-zero" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 5, 0f)),
            "opacity-above-one" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 5, 1.01f)),
            "width-negative" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 6, -1f)),
            "width-above-1000" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 6, 1000.5f)),
            "flags-1024" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 10, 1024u)),
            "name-nil-on-add" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 9, null)),
            "name-empty-on-add" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 9, "")),
            "name-with-nul" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 9, "a\0b")),
            "name-invalid-utf8" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 9, new byte[] { 0xA2, 0xFF, 0xFE })),
            "quads-nil-on-highlight" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 11, null)),
            "quads-seven" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 11, new float[7])),
            "quads-not-multiple-of-eight" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 11, new float[12])),
            "ink-without-strokes" => Blob(EditBlobLimits.Magic, 1u, 2u, With(With(add, 2, (ushort)5), 11, null)),
            "ink-stroke-one-point" => Blob(EditBlobLimits.Magic, 1u, 2u, With(With(With(add, 2, (ushort)5), 11, null), 12, new object?[] { new[] { 1f, 2f } })),
            "ink-stroke-odd" => Blob(EditBlobLimits.Magic, 1u, 2u, With(With(With(add, 2, (ushort)5), 11, null), 12, new object?[] { new[] { 1f, 2f, 3f } })),
            "add-with-a-target" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 13, 5u)),
            "delete-without-expectation" => Blob(EditBlobLimits.Magic, 1u, 2u, With(delete, 14, null)),
            "delete-without-target" => Blob(EditBlobLimits.Magic, 1u, 2u, With(delete, 13, 0u)),
            "target-above-limit" => Blob(EditBlobLimits.Magic, 1u, 2u, With(delete, 13, 4_194_304u)),
            "update-changes-nothing" => Blob(EditBlobLimits.Magic, 1u, 2u, With(With(delete, 0, (ushort)3), 3, null)),
            "string-for-page" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 1, "0")),
            "negative-page" => Blob(EditBlobLimits.Magic, 1u, 2u, With(add, 1, -1)),
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
}
