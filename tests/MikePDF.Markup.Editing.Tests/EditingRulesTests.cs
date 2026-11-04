#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>AC-01, AC-02, AC-09, SN-02, SN-03, SN-06 and the field round trip of the journal (annotation-core 9).</summary>
public sealed class EditingRulesTests
{
    [Theory]
    [InlineData("Highlight", "MikePDF:abc", AnnotationClass.Owned)]
    [InlineData("Ink", "MikePDF:abc", AnnotationClass.Owned)]
    [InlineData("Highlight", "AcrobatNM", AnnotationClass.ForeignSupported)]
    [InlineData("Text", null, AnnotationClass.ForeignSupported)]
    [InlineData("Stamp", "", AnnotationClass.ForeignSupported)]
    [InlineData("Link", "MikePDF:abc", AnnotationClass.Unsupported)] // a hostile claim on something that is not ours to draw
    [InlineData("Widget", null, AnnotationClass.Unsupported)]
    [InlineData("Popup", null, AnnotationClass.Unsupported)]
    [InlineData("FileAttachment", null, AnnotationClass.Unsupported)]
    [InlineData("Redact", null, AnnotationClass.Unsupported)]
    [InlineData(null, null, AnnotationClass.Unsupported)]
    public void AC02_AnAnnotationIsOwnedForeignOrUnsupported(string? subtype, string? nm, AnnotationClass expected)
    {
        Assert.Equal(expected, AnnotationRules.Classify(subtype, nm));
    }

    [Fact]
    public void AC02_TheAllowedOperationsOfEachClassAreEnforcedWithTheRightReason()
    {
        var samples = new Samples();
        var owned = samples.Rectangle();
        var foreign = samples.Rectangle() with { Id = "someone-else", Author = "Alex" };
        var foreignMarkup = samples.Highlight() with { Id = "someone-else-2", Author = "Alex" };
        var unsupported = samples.Rectangle() with { Id = "x" }; // stands in for a loaded kind; the matrix is keyed on the class below

        foreach (var operation in Enum.GetValues<AnnotationOperation>())
        {
            Assert.True(AnnotationRules.Check(owned, operation, "Mike").Allowed || operation == AnnotationOperation.Move && owned.IsTextAnchored, operation.ToString());
        }

        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Select, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Delete, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Reply, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.SetReviewState, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Move, "Mike").Allowed);
        Assert.Equal(new OperationCheck(false, "Annot_Disabled_Foreign"), AnnotationRules.Check(foreign, AnnotationOperation.Restyle, "Mike"));
        Assert.Equal(new OperationCheck(false, "Annot_Disabled_Foreign"), AnnotationRules.Check(foreign, AnnotationOperation.Resize, "Mike"));
        Assert.Equal(new OperationCheck(false, "Note_Cannot_Edit_Others"), AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "Mike"));
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "Alex").Allowed); // the same author may edit the comment
        Assert.False(AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "").Allowed);
        Assert.Equal("Annot_Disabled_TextAnchored", AnnotationRules.Check(foreignMarkup, AnnotationOperation.Move, "Alex").ReasonKey); // text markup is anchored
        Assert.Equal("Annot_Disabled_TextAnchored", AnnotationRules.Check(samples.Highlight(), AnnotationOperation.Move, "Mike").ReasonKey);
        Assert.Equal("Annot_Disabled_NotResizable", AnnotationRules.Check(samples.Note(), AnnotationOperation.Resize, "Mike").ReasonKey);
        Assert.NotNull(unsupported);
    }

    [Fact]
    public void AC02_AnUnsupportedKindCanOnlyBeSelected()
    {
        // Only the class decides: build the check by classifying a link-like annotation.
        Assert.Equal(AnnotationClass.Unsupported, AnnotationRules.Classify("Link", null));
        Assert.Null(AnnotationRules.DeleteConfirmKey(0));
        Assert.Equal("Note_Delete_Thread_Confirm_One", AnnotationRules.DeleteConfirmKey(1));
        Assert.Equal("Note_Delete_Thread_Confirm_Other", AnnotationRules.DeleteConfirmKey(7));
    }

    [Fact]
    public void SN02_TheCurrentStateIsTheNewestStateAnnotationAndNoneResets()
    {
        var samples = new Samples();
        var note = samples.Session.Add(samples.Note());
        Assert.Equal(ReviewState.None, samples.Session.CurrentState(note.Id));

        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.Accepted);
        Assert.Equal(ReviewState.Accepted, samples.Session.CurrentState(note.Id));
        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.Rejected);
        Assert.Equal(ReviewState.Rejected, samples.Session.CurrentState(note.Id));
        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.None);
        Assert.Equal(ReviewState.None, samples.Session.CurrentState(note.Id)); // None is a state too
        Assert.Equal(3, samples.Session.Store.RepliesTo(note.Id).Count(static a => a.ReplyKind == ReplyKind.State)); // history is kept
    }

    [Fact]
    public void SN02_TwoStatesAtTheSameInstantBreakTiesByOrderInThePage()
    {
        var samples = new Samples();
        var note = samples.Session.Add(samples.Note());
        samples.Session.SetReviewState(note.Id, ReviewState.Completed);
        samples.Session.SetReviewState(note.Id, ReviewState.Cancelled); // same clock reading, later in /Annots

        Assert.Equal(ReviewState.Cancelled, samples.Session.CurrentState(note.Id));
    }

    [Fact]
    public void SN03_RepliesAreRefusedToAnnotationsWithNoNameAndTheThreadLimitIsKept()
    {
        var samples = new Samples();
        var loaded = samples.Note() with { Id = "session:0:3", Origin = AnnotationOrigin.Loaded };
        samples.Session.Load([loaded]);

        Assert.Null(samples.Session.Reply(loaded.Id, "hello"));
        Assert.Equal(AnnotationRules.ReasonUnsupported, samples.Session.LastReason);
        var note = samples.Session.Add(samples.Note(contents: "root"));
        Assert.NotNull(samples.Session.Reply(note.Id, "fine"));
        Assert.Null(samples.Session.Reply("no such id", "x"));
        Assert.Equal(["root"], new[] { note.Contents });
    }

    [Fact]
    public void EveryKindSurvivesTheFlatFieldsOfACommandUnchanged()
    {
        var samples = new Samples();
        var everything = samples.OfEveryKind().Append(samples.Ink(highlighter: true)).Append(samples.Stamp(name: "NotForPublicRelease"))
            .Append(samples.Factory.Reply(samples.Note(), "reply")).Append(samples.Factory.ReviewStateChange(samples.Note(), ReviewState.Completed)).ToList();

        foreach (var a in everything)
        {
            var json = AnnotationFields.ToFields(a).ToElement();
            var back = AnnotationFields.FromFields(json);

            Assert.True(AnnotationFields.Same(a, back), $"{a.Kind}: {json}");
            Assert.Equal(a.Kind, back.Kind);
            Assert.Equal(a.Rect, back.Rect);
            Assert.Equal(a.Created, back.Created);
        }
    }

    [Fact]
    public void ACommandWithMissingOrWrongFieldsIsRefusedNotGuessed()
    {
        var samples = new Samples();
        foreach (var missing in new[] { "kind", "rect", "color", "name", "page" })
        {
            var copy = AnnotationFields.ToFields(samples.Rectangle());
            copy.Remove(missing);
            Assert.Throws<FormatException>(() => AnnotationFields.FromFields(copy.ToElement()));
        }

        var wrong = AnnotationFields.ToFields(samples.Rectangle());
        wrong["kind"] = "Hologram";
        Assert.Throws<FormatException>(() => AnnotationFields.FromFields(wrong.ToElement()));
        var badStamp = AnnotationFields.ToFields(samples.Stamp());
        badStamp["stamp"] = null;
        Assert.Throws<FormatException>(() => AnnotationFields.FromFields(badStamp.ToElement()));
    }

    [Fact]
    public void TheMapperReadsEveryFieldTheModelWritesAndTheBlobIsOneTheCodecAccepts()
    {
        var samples = new Samples();
        foreach (var a in samples.OfEveryKind())
        {
            samples.Session.Add(a);
        }

        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1);

        Assert.Equal(15, blob.Edits.Count);
        Assert.All(blob.Edits, static e => Assert.Equal(MikePDF.Protocol.Edits.EditOperation.Add, e.Operation));
        Assert.True(MikePDF.Protocol.Edits.EditBlobCodec.TryDecode(MikePDF.Protocol.Edits.EditBlobCodec.Encode(blob), out var decoded, out var error), error);
        var byName = decoded.Edits.ToDictionary(static e => e.Name!);
        foreach (var a in samples.Session.Store.OnPage(0))
        {
            var edit = byName[a.Id];
            Assert.Equal(new[] { (float)a.Rect.Left, (float)a.Rect.Bottom, (float)a.Rect.Right, (float)a.Rect.Top }, edit.Rect);
            Assert.Equal((float)a.Opacity, edit.Opacity);
            Assert.Equal(a.Author, edit.Author);
            Assert.Equal(PdfDates.Format(a.Created), edit.Created);
        }

        Assert.Equal(MikePDF.Protocol.Edits.AnnotationKind.Squiggly, byName.Values.Single(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Squiggly).Kind);
        Assert.Equal(2, byName.Values.Count(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.FreeText));
        Assert.Equal(1, byName.Values.Count(static e => (e.Traits & MikePDF.Protocol.Edits.EditTraits.Typewriter) != 0));
        Assert.Equal(2, byName.Values.Count(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Line));
        Assert.Equal("Approved", byName.Values.Single(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Stamp).StandardName);
    }

    [Fact]
    public void ThePageFrameKeepsTheUserSpaceRectOfARotatedPageNormalised()
    {
        var samples = new Samples();
        var frame = Samples.Frame(90, [50, 30, 562, 762]);
        var a = samples.Factory.Box(MarkupKind.Rectangle, 0, frame.ToUser(new RectD(10, 20, 100, 40)));

        Assert.True(a.Rect.Left < a.Rect.Right && a.Rect.Bottom < a.Rect.Top);
        Assert.Equal((40, 100), (Math.Round(a.Rect.Width, 6), Math.Round(a.Rect.Height, 6))); // turned a quarter: the sides swap
    }

    [Fact]
    public void SN01_AThreadWithACycleOrALongChainIsWalkedOnceAndTerminates()
    {
        var samples = new Samples();
        var store = new AnnotationStore();
        var a = samples.Note() with { Id = "a", Parent = "c" }; // a answers c, c answers b, b answers a: a loop
        var b = samples.Note() with { Id = "b", Parent = "a" };
        var c = samples.Note() with { Id = "c", Parent = "b" };
        store.Add(a);
        store.Add(b);
        store.Add(c);

        var thread = store.Thread("a");

        Assert.Equal(["a", "b", "c"], thread.Select(static x => x.Id).Order());
        Assert.Empty(store.Thread("missing"));
        Assert.Equal(["b"], store.RepliesTo("a").Select(static x => x.Id));
    }
}
