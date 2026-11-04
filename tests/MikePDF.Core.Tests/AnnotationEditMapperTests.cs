#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Core.Editing;
using MikePDF.Protocol.Edits;

namespace MikePDF.Core.Tests;

/// <summary>SP-09 (save-pipeline 14): the pending commands become the net edits of stage 4.</summary>
public sealed class AnnotationEditMapperTests
{
    private static EditCommand AddHighlight(string name, int page = 0, string contents = "c") => EditCommand.Create("AddAnnotation", new
    {
        page,
        kind = "Highlight",
        name,
        rect = new[] { 10f, 20f, 110f, 40f },
        color = new[] { 1f, 0.9f, 0f },
        opacity = 0.5f,
        contents,
        author = "Mike",
        quads = new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f },
    });

    private static EditCommand Move(string name, float[] newRect, int page = 0) =>
        EditCommand.Create("MoveAnnotation", new { page, name, subtype = "Highlight", rect = new[] { 10f, 20f, 110f, 40f }, newRect });

    [Fact]
    public void AnAddedAnnotationBecomesAnAddWithItsFields()
    {
        var blob = AnnotationEditMapper.ToBlob([AddHighlight("a", page: 2, contents: "café")], pageCount: 3);

        var edit = Assert.Single(blob.Edits);
        Assert.Equal(3u, blob.PageCount);
        Assert.Equal((EditOperation.Add, 2u, AnnotationKind.Highlight, "a", "café", "Mike"), (edit.Operation, edit.Page, edit.Kind, edit.Name, edit.Contents, edit.Author));
        Assert.Equal(new[] { 10f, 20f, 110f, 40f }, edit.Rect);
        Assert.Equal(0.5f, edit.Opacity);
        Assert.Equal(8, edit.QuadPoints!.Length);
        Assert.NotEmpty(EditBlobCodec.Encode(blob)); // and the blob is one the codec accepts
    }

    [Fact]
    public void AnAnnotationAddedAndThenDeletedInTheSameSnapshotIsNeverWritten()
    {
        var blob = AnnotationEditMapper.ToBlob(
            [AddHighlight("a"), AddHighlight("b"), EditCommand.Create("DeleteAnnotation", new { page = 0, name = "a" })],
            pageCount: 1);

        Assert.Equal("b", Assert.Single(blob.Edits).Name);
    }

    [Fact]
    public void AnAnnotationAddedAndThenMovedIsWrittenOnceAtItsFinalPlace()
    {
        var blob = AnnotationEditMapper.ToBlob([AddHighlight("a"), Move("a", [30f, 40f, 130f, 60f])], pageCount: 1);

        var edit = Assert.Single(blob.Edits);
        Assert.Equal(EditOperation.Add, edit.Operation);
        Assert.Equal(new[] { 30f, 40f, 130f, 60f }, edit.Rect);
    }

    [Fact]
    public void SeveralChangesToOneLoadedAnnotationBecomeOneUpdateThatKeepsTheFirstExpectation()
    {
        var blob = AnnotationEditMapper.ToBlob(
            [
                EditCommand.Create("MoveAnnotation", new { page = 0, @object = 12, subtype = "Highlight", rect = new[] { 10f, 20f, 110f, 40f }, newRect = new[] { 11f, 21f, 111f, 41f } }),
                EditCommand.Create("UpdateAnnotation", new { page = 0, @object = 12, subtype = "Highlight", rect = new[] { 11f, 21f, 111f, 41f }, contents = "later" }),
                EditCommand.Create("MoveAnnotation", new { page = 0, @object = 12, subtype = "Highlight", rect = new[] { 11f, 21f, 111f, 41f }, newRect = new[] { 12f, 22f, 112f, 42f } }),
            ],
            pageCount: 1);

        var edit = Assert.Single(blob.Edits);
        Assert.Equal((EditOperation.Update, 12u, "Highlight"), (edit.Operation, edit.TargetObject, edit.ExpectSubtype));
        Assert.Equal(new[] { 10f, 20f, 110f, 40f }, edit.ExpectRect); // what the file holds, not the intermediate state
        Assert.Equal(new[] { 12f, 22f, 112f, 42f }, edit.Rect);
        Assert.Equal("later", edit.Contents);
    }

    [Fact]
    public void ADeleteAfterAnUpdateOfALoadedAnnotationIsJustTheDelete()
    {
        var blob = AnnotationEditMapper.ToBlob(
            [
                EditCommand.Create("UpdateAnnotation", new { page = 0, @object = 12, subtype = "Highlight", rect = new[] { 10f, 20f, 110f, 40f }, contents = "x" }),
                EditCommand.Create("DeleteAnnotation", new { page = 0, @object = 12, subtype = "Highlight", rect = new[] { 10f, 20f, 110f, 40f } }),
            ],
            pageCount: 1);

        Assert.Equal(EditOperation.Delete, Assert.Single(blob.Edits).Operation);
    }

    [Fact]
    public void ACommandNoGeneratorExistsForIsRefusedByName()
    {
        var reply = EditCommand.Create("AddReply", new { page = 0 });

        var error = Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([reply], 1));

        Assert.Equal("AddReply", error.Command);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("rect")]
    [InlineData("color")]
    [InlineData("name")]
    [InlineData("page")]
    public void AnAddMissingAFieldItNeedsIsRefusedNotGuessed(string missing)
    {
        var fields = new Dictionary<string, object>
        {
            ["page"] = 0,
            ["kind"] = "Highlight",
            ["name"] = "a",
            ["rect"] = new[] { 1f, 2f, 3f, 4f },
            ["color"] = new[] { 1f, 0f, 0f },
            ["quads"] = new[] { 1f, 4f, 3f, 4f, 1f, 2f, 3f, 2f },
        };
        fields.Remove(missing);

        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([EditCommand.Create("AddAnnotation", fields)], 1));
    }

    [Fact]
    public void AKindThatDoesNotExistIsRefused()
    {
        var add = EditCommand.Create("AddAnnotation", new { page = 0, kind = "Hologram", name = "a", rect = new[] { 1f, 2f, 3f, 4f }, color = new[] { 1f, 0f, 0f } });

        Assert.Throws<UnsupportedEditException>(() => AnnotationEditMapper.ToBlob([add], 1));
    }
}
