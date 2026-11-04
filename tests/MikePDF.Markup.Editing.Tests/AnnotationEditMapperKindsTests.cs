#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Core.Editing;
using MikePDF.Ink;
using MikePDF.Protocol.Edits;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>SP-09 for the kinds and commands the annotation work package added: every field is read, strokes are folded, a restyle becomes a Replace, a composite is expanded.</summary>
public sealed class AnnotationEditMapperKindsTests
{
    private static EditCommand Add(object fields) => EditCommand.Create("AddAnnotation", fields);

    private static string Stroke(params (float X, float Y, float P)[] points) => StrokeCodec.ToBase64([.. points.Select(static p => new InkSample(p.X, p.Y, p.P))]);

    [Fact]
    public void EveryFieldOfEveryKindIsRead()
    {
        var blob = AnnotationEditMapper.ToBlob(
            [
                Add(new { page = 0, kind = "Squiggly", name = "a", rect = new[] { 1f, 2f, 30f, 12f }, color = new[] { 1f, 0f, 0f }, quads = new[] { 1f, 12f, 30f, 12f, 1f, 2f, 30f, 2f }, subj = "Squiggly Underline", created = "D:20261104093015+01'00'", modified = "D:20261104093015+01'00'", popup = new[] { 40f, 2f, 220f, 102f } }),
                Add(new { page = 0, kind = "FreeText", name = "b", rect = new[] { 1f, 2f, 180f, 42f }, color = new[] { 1f, 0f, 0f }, width = 1.5f, ic = new[] { 1f, 1f, 0.8f }, family = "Serif", bold = true, italic = true, size = 14f, tcolor = new[] { 0f, 0f, 0.5f }, align = "right", typewriter = true, contents = "x" }),
                Add(new { page = 0, kind = "Line", name = "c", rect = new[] { 0f, 0f, 50f, 50f }, color = new[] { 0f, 0f, 1f }, vertices = new[] { 1f, 2f, 40f, 45f }, le = new[] { "Circle", "ClosedArrow" }, dash = "dashed", ic = new[] { 1f, 1f, 0f } }),
                Add(new { page = 0, kind = "Polygon", name = "d", rect = new[] { 0f, 0f, 50f, 50f }, color = new[] { 0f, 0f, 1f }, vertices = new[] { 1f, 2f, 40f, 2f, 20f, 45f }, dash = "dotted" }),
                Add(new { page = 0, kind = "PolyLine", name = "e", rect = new[] { 0f, 0f, 50f, 50f }, color = new[] { 0f, 0f, 1f }, vertices = new[] { 1f, 2f, 40f, 2f }, le = new[] { "OpenArrow", "Diamond" } }),
                Add(new { page = 0, kind = "Stamp", name = "f", rect = new[] { 0f, 0f, 150f, 40f }, color = new[] { 0f, 0.5f, 0f }, stamp = "Approved", turns = 3 }),
                Add(new { page = 0, kind = "Text", name = "g", rect = new[] { 0f, 0f, 20f, 20f }, color = new[] { 1f, 1f, 0f }, icon = "Key" }),
                Add(new { page = 0, kind = "Ink", name = "h", rect = new[] { 0f, 0f, 50f, 50f }, color = new[] { 0f, 0f, 1f }, strokes = new[] { new[] { 1f, 2f, 10f, 20f } }, widths = new[] { new[] { 2f, 3f } }, highlighter = true }),
            ],
            1);

        var e = blob.Edits;
        Assert.Equal(8, e.Count);
        Assert.Equal((AnnotationKind.Squiggly, "Squiggly Underline", "D:20261104093015+01'00'"), (e[0].Kind, e[0].Subject, e[0].Created));
        Assert.Equal(new[] { 40f, 2f, 220f, 102f }, e[0].PopupRect);
        Assert.Equal((AnnotationKind.FreeText, 7, 14f, (ushort)2, EditTraits.Typewriter), (e[1].Kind, (int)e[1].FontId, e[1].FontSize, e[1].Align, e[1].Traits));
        Assert.Equal(new[] { 0f, 0f, 0.5f }, e[1].TextColor);
        Assert.Equal(new[] { 1f, 1f, 0.8f }, e[1].InteriorColor);
        Assert.Equal((LineEndKind.Circle, LineEndKind.ClosedArrow, DashKind.Dashed), (e[2].LineStart, e[2].LineEnd, e[2].Dash));
        Assert.Equal(DashKind.Dotted, e[3].Dash);
        Assert.Equal(new[] { 1f, 2f, 40f, 2f, 20f, 45f }, e[3].Vertices);
        Assert.Equal((LineEndKind.OpenArrow, LineEndKind.Diamond), (e[4].LineStart, e[4].LineEnd));
        Assert.Equal(("Approved", 3), (e[5].StandardName, e[5].StampTurns));
        Assert.Equal("Key", e[6].StandardName);
        Assert.Equal(EditTraits.Highlighter, e[7].Traits);
        Assert.Equal(new[] { 2f, 3f }, e[7].InkWidths![0]);
        Assert.NotEmpty(EditBlobCodec.Encode(blob)); // and the blob is one the codec accepts
    }

    [Fact]
    public void FontNumbersFollowFamilyBoldAndItalic()
    {
        var blob = AnnotationEditMapper.ToBlob(
            [
                Add(new { page = 0, kind = "FreeText", name = "a", rect = new[] { 0f, 0f, 10f, 10f }, color = new[] { 0f, 0f, 0f }, size = 12f, tcolor = new[] { 0f, 0f, 0f } }),
                Add(new { page = 0, kind = "FreeText", name = "b", rect = new[] { 0f, 0f, 10f, 10f }, color = new[] { 0f, 0f, 0f }, family = "Mono", italic = true, size = 12f, tcolor = new[] { 0f, 0f, 0f } }),
                Add(new { page = 0, kind = "FreeText", name = "c", rect = new[] { 0f, 0f, 10f, 10f }, color = new[] { 0f, 0f, 0f }, family = "Serif", bold = true, size = 12f, tcolor = new[] { 0f, 0f, 0f } }),
            ],
            1);

        Assert.Equal([(ushort)0, (ushort)10, (ushort)5], blob.Edits.Select(static x => x.FontId));
    }

    [Fact]
    public void ARepliesAndAStateAreTextAnnotationsThatNameTheirParent()
    {
        var reply = EditCommand.Create("AddReply", new { page = 0, kind = "Text", name = "r1", rect = new[] { 0f, 0f, 20f, 20f }, color = new[] { 1f, 1f, 0f }, parent = "root", contents = "ok" });
        var state = EditCommand.Create("SetReviewState", new { page = 0, kind = "Text", name = "r2", rect = new[] { 0f, 0f, 20f, 20f }, color = new[] { 1f, 1f, 0f }, parent = "root", state = "Rejected" });

        var blob = AnnotationEditMapper.ToBlob([reply, state], 1);

        Assert.Equal([(1, "root", 0), (2, "root", 2)], blob.Edits.Select(static x => ((int)x.ReplyKind, x.ParentName, (int)x.State)));
        Assert.NotEmpty(EditBlobCodec.Encode(blob));
    }

    [Fact]
    public void AReviewStateOfAnUnknownNameIsRefused()
    {
        var state = EditCommand.Create("SetReviewState", new { page = 0, kind = "Text", name = "r2", rect = new[] { 0f, 0f, 20f, 20f }, color = new[] { 1f, 1f, 0f }, parent = "root", state = "Maybe" });

        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([state], 1));
    }

    // ---- ink ----

    private static EditCommand InkStroke(string batch, string id, string data, bool newBatch, int page = 0) => EditCommand.Create("AddInkStroke", new
    {
        annotation = batch,
        page,
        tool = "pen",
        color = new[] { 0f, 0f, 1f },
        opacity = 1f,
        width = 2f,
        curve = "linear",
        minRatio = 0.35f,
        stroke = new { id, n = 2, data },
        newBatch,
    });

    [Fact]
    public void StrokesOfABatchAreFoldedIntoOneInkAnnotationWithTheirWidths()
    {
        var first = Stroke((10, 10, 0.2f), (50, 40, 1f));
        var second = Stroke((10, 80, 0.5f), (60, 90, 0.5f));

        var blob = AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", first, newBatch: true), InkStroke("MikePDF:b1", "s2", second, newBatch: false)], 1);

        var edit = Assert.Single(blob.Edits);
        Assert.Equal((EditOperation.Add, AnnotationKind.Ink, "MikePDF:b1"), (edit.Operation, edit.Kind, edit.Name));
        Assert.Equal(2, edit.Strokes!.Count);
        Assert.Equal(2, edit.InkWidths!.Count);
        Assert.Equal(edit.Strokes[0].Length / 2, edit.InkWidths[0].Length); // a width for each point
        Assert.True(edit.InkWidths[0][0] < edit.InkWidths[0][^1]); // the pressure of the first stroke rises
        Assert.True(edit.Rect![0] < 10 && edit.Rect[3] > 90); // the rectangle holds both outlines
        Assert.Equal(2f, edit.Width);
        Assert.NotEmpty(EditBlobCodec.Encode(blob));
    }

    [Fact]
    public void AStrokeAddedToABatchTheFileAlreadyHoldsIsRefusedAndABatchThatWasDeletedIsNotWritten()
    {
        var data = Stroke((10, 10, 1f), (50, 40, 1f));

        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", data, newBatch: false)], 1));
        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", "not base64!!", newBatch: true)], 1));
        var blob = AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", data, newBatch: true), EditCommand.Create("DeleteAnnotation", new { page = 0, name = "MikePDF:b1" })], 1);
        Assert.Empty(blob.Edits);
    }

    [Fact]
    public void AnEraseOfAPendingBatchRewritesItAndOneWithoutTheNewFieldsIsRefused()
    {
        var data = Stroke((10, 10, 1f), (50, 40, 1f));
        var replacement = new
        {
            page = 0, kind = "Ink", name = "MikePDF:b1", rect = new[] { 5f, 5f, 55f, 45f }, color = new[] { 0f, 0f, 1f }, width = 2f,
            strokes = new[] { new[] { 10f, 10f, 20f, 20f } }, widths = new[] { new[] { 2f, 2f } },
        };
        var erase = EditCommand.Create("EraseInk", new { annotation = "MikePDF:b1", page = 0, name = "MikePDF:b1", subtype = "Ink", rect = new[] { 5f, 5f, 55f, 45f }, replace = replacement });

        var blob = AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", data, newBatch: true), erase], 1);

        var edit = Assert.Single(blob.Edits);
        Assert.Equal(new[] { 10f, 10f, 20f, 20f }, Assert.Single(edit.Strokes!));
        var bare = EditCommand.Create("EraseInk", new { annotation = "MikePDF:b1", page = 0, name = "MikePDF:b1", subtype = "Ink", rect = new[] { 5f, 5f, 55f, 45f } });
        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([InkStroke("MikePDF:b1", "s1", data, newBatch: true), bare], 1));
    }

    // ---- replace ----

    private static object FullFields(string name, float width) => new
    {
        page = 0, kind = "Square", name, rect = new[] { 100f, 100f, 150f, 140f }, color = new[] { 0f, 1f, 0f }, width, opacity = 1f,
    };

    [Fact]
    public void AReplaceOfAnAnnotationAddedInTheSameSnapshotRewritesTheAdd()
    {
        var add = Add(FullFields("a", 2f));
        var restyle = EditCommand.Create("UpdateAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f }, newRect = new[] { 100f, 100f, 150f, 140f }, replace = FullFields("a", 7f) });

        var edit = Assert.Single(AnnotationEditMapper.ToBlob([add, restyle], 1).Edits);

        Assert.Equal((EditOperation.Add, 7f), (edit.Operation, edit.Width));
    }

    [Fact]
    public void ASeriesOfRestylesOfALoadedAnnotationIsOneReplaceThatKeepsTheFirstExpectationAndTheLatestFields()
    {
        var first = EditCommand.Create("UpdateAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f }, newRect = new[] { 100f, 100f, 150f, 140f }, replace = FullFields("a", 5f) });
        var move = EditCommand.Create("MoveAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f }, newRect = new[] { 110f, 110f, 160f, 150f } });
        var last = EditCommand.Create("UpdateAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 110f, 110f, 160f, 150f }, newRect = new[] { 110f, 110f, 160f, 150f }, replace = FullFields("a", 9f) });

        var edit = Assert.Single(AnnotationEditMapper.ToBlob([first, move, last], 1).Edits);

        Assert.Equal((EditOperation.Replace, "Square", 9f), (edit.Operation, edit.ExpectSubtype, edit.Width));
        Assert.Equal(new[] { 100f, 100f, 150f, 140f }, edit.ExpectRect); // what the file holds, not an intermediate state
        Assert.NotEmpty(EditBlobCodec.Encode(AnnotationEditMapper.ToBlob([first, move, last], 1)));
    }

    [Fact]
    public void ADeleteAfterAReplaceIsJustTheDelete()
    {
        var restyle = EditCommand.Create("UpdateAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f }, newRect = new[] { 100f, 100f, 150f, 140f }, replace = FullFields("a", 5f) });
        var delete = EditCommand.Create("DeleteAnnotation", new { page = 0, name = "a", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f } });

        var edit = Assert.Single(AnnotationEditMapper.ToBlob([restyle, delete], 1).Edits);

        Assert.Equal(EditOperation.Delete, edit.Operation);
    }

    // ---- composites ----

    private static object Composite(params (string Cmd, object Fields)[] items) => new { commands = items.Select(static i => new { c = i.Cmd, f = i.Fields }).ToArray() };

    [Fact]
    public void ACompositeIsExpandedInOrder()
    {
        var composite = EditCommand.Create("CompositeAnnotation", Composite(("AddAnnotation", FullFields("a", 2f)), ("AddAnnotation", FullFields("b", 3f))));

        var blob = AnnotationEditMapper.ToBlob([composite], 1);

        Assert.Equal(["a", "b"], blob.Edits.Select(static e => e.Name));
        Assert.Equal(EditKinds.Annotations, composite.Kind);
    }

    [Fact]
    public void ACompositeInsideACompositeOrWithoutItsCommandsIsRefused()
    {
        var nested = EditCommand.Create("CompositeAnnotation", Composite(("CompositeAnnotation", new { commands = Array.Empty<object>() })));
        var empty = EditCommand.Create("CompositeAnnotation", new { nothing = 1 });
        var malformed = EditCommand.Create("CompositeAnnotation", new { commands = new[] { new { c = "AddAnnotation" } } });

        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([nested], 1));
        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([empty], 1));
        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([malformed], 1));
    }

    [Fact]
    public void AMultiSelectDeleteAsACompositeCancelsWhatWasAddedAndDeletesWhatTheFileHolds()
    {
        var add = Add(FullFields("new", 2f));
        var delete = EditCommand.Create("CompositeAnnotation", Composite(
            ("DeleteAnnotation", new { page = 0, name = "new", subtype = "Square", rect = new[] { 100f, 100f, 150f, 140f } }),
            ("DeleteAnnotation", new { page = 0, name = "old", subtype = "Circle", rect = new[] { 5f, 5f, 25f, 25f } })));

        var edit = Assert.Single(AnnotationEditMapper.ToBlob([add, delete], 1).Edits);

        Assert.Equal((EditOperation.Delete, "old"), (edit.Operation, edit.Name));
    }
}
