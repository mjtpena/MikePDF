#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Core.Editing;
using MikePDF.Ink;
using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>AC-06, AC-07, AC-08, TM-07, TM-16, SN-13, FT-12, INK-05/06 through the session: commands, their inverses, undo and redo, and what the edit model is left holding.</summary>
public sealed class SessionTests
{
    /// <summary>The store as a list of canonical strings in stacking order, so two states can be compared exactly.</summary>
    private static string[] Snapshot(AnnotationStore store) =>
        [.. store.Pages.SelectMany(store.OnPage).Select(static a => a.Origin + "|" + AnnotationFields.ToFields(a).ToJson())];

    private static void AssertUndoRestoresAndRedoReapplies(Samples samples, Action command)
    {
        var before = Snapshot(samples.Session.Store);
        var pendingBefore = samples.Model.PendingCount;

        command();
        var after = Snapshot(samples.Session.Store);
        Assert.NotEqual(before, after);

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.Equal(before, Snapshot(samples.Session.Store));
        Assert.Equal(pendingBefore, samples.Model.PendingCount);

        Assert.Equal(UndoOutcome.Done, samples.Session.Redo());
        Assert.Equal(after, Snapshot(samples.Session.Store));
    }

    [Fact]
    public void AC06_ApplyThenUndoRestoresTheModelExactlyAndRedoReappliesForEveryCommandClass()
    {
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        var line = samples.Session.Add(samples.Line());
        var text = samples.Session.Add(samples.FreeText());
        var note = samples.Session.Add(samples.Note());
        var ink = samples.Session.Add(samples.Ink());
        var poly = samples.Session.Add(samples.Polygon());
        var other = new Samples();

        AssertUndoRestoresAndRedoReapplies(samples, () => samples.Session.Add(samples.Highlight())); // AddAnnotation
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.Delete(rect.Id))); // DeleteAnnotation
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.Move(line.Id, 12, -7))); // MoveAnnotation
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.ResizeTo(poly.Id, new PdfRect(90, 140, 260, 260)))); // ResizeAnnotation
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.Update(text.Id, a => a with { Contents = "Changed text, longer now than it was", Interior = Rgb.White }))); // UpdateAnnotation
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.SetContents(note.Id, "A different comment"))); // UpdateAnnotation (contents)
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.NotNull(samples.Session.Reply(note.Id, "reply"))); // AddReply
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.NotNull(samples.Session.SetReviewState(note.Id, ReviewState.Accepted))); // SetReviewState
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.EraseInk(ink.Id, EraserMode.Point, [(140, 113.6), (144, 114.5)], 4))); // EraseInk
        AssertUndoRestoresAndRedoReapplies(samples, () => samples.Session.AddInkStroke(0, new InkStroke("z", [new InkSample(10, 10, 1), new InkSample(40, 40, 1)]), InkStyle.DefaultPen, Rgb.Black)); // AddInkStroke
        AssertUndoRestoresAndRedoReapplies(samples, () => Assert.True(samples.Session.UpdateMany([rect.Id, line.Id], a => a with { Color = new Rgb(0, 1, 0) }))); // a restyle of a selection
        Assert.NotNull(other);
    }

    [Fact]
    public void AC06_ANewCommandClearsTheRedoStack()
    {
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        Assert.True(samples.Session.Move(rect.Id, 5, 5));
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.True(samples.Model.CanRedo);

        samples.Session.Add(samples.Ellipse());

        Assert.False(samples.Model.CanRedo);
        Assert.Equal(UndoOutcome.None, samples.Session.Redo());
        Assert.Null(samples.Session.RedoLabel);
    }

    [Fact]
    public void AC06_ACompositeIsAtomicAndUndoingEverythingLeavesAnEmptyDocument()
    {
        var samples = new Samples();
        var added = samples.Session.AddMany([samples.Highlight(0), samples.Highlight(1), samples.Highlight(2)]);
        Assert.Equal(3, samples.Session.Store.Count);
        Assert.Equal(1, samples.Model.PendingCount); // one command in the journal

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());

        Assert.Equal(0, samples.Session.Store.Count);
        Assert.False(samples.Model.IsDirty);
        Assert.Equal(UndoOutcome.Done, samples.Session.Redo());
        Assert.Equal(added.Select(static a => a.Id), samples.Session.Store.All().Select(static a => a.Id).Order());
    }

    // ---- TM-07 ----

    [Fact]
    public void TM07_AMarkAcrossThreePagesIsOneStepAndUndoAndRedoKeepTheSameIds()
    {
        var samples = new Samples();
        var marks = samples.Session.AddMany([samples.Highlight(3), samples.Highlight(4), samples.Highlight(5)]);
        var ids = marks.Select(static m => m.Id).ToArray();

        Assert.Equal("Highlight added", samples.Session.UndoLabel);
        samples.Session.Undo();
        Assert.Equal(0, samples.Session.Store.Count);
        samples.Session.Redo();

        Assert.Equal(ids.Order(), samples.Session.Store.All().Select(static a => a.Id).Order());
        Assert.Equal([3, 4, 5], samples.Session.Store.Pages);
        Assert.Equal(1, AnnotationEditMapperBlobPages(samples)); // and the save sees them as additions on their own pages
    }

    private static int AnnotationEditMapperBlobPages(Samples samples)
    {
        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 6);
        Assert.Equal([3u, 4u, 5u], blob.Edits.Select(static e => e.Page));
        return 1;
    }

    // ---- AC-07: undo after a save ----

    [Fact]
    public void AC07_UndoingAnAddThatWasSavedBecomesADeleteRelativeToTheSavedFile()
    {
        var samples = new Samples();
        var note = samples.Session.Add(samples.Note());
        var snapshot = samples.Model.BeginSave();
        samples.Model.MarkSaved(snapshot);
        Assert.False(samples.Model.IsDirty);

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());

        Assert.False(samples.Session.Store.Contains(note.Id));
        Assert.True(samples.Model.IsDirty);
        var pending = Assert.Single(samples.Model.PendingCommands);
        Assert.Equal("DeleteAnnotation", pending.Cmd); // an ordinary command in the journal
        Assert.Equal(note.Id, pending.Payload.GetProperty("name").GetString());
        Assert.Equal("Text", pending.Payload.GetProperty("subtype").GetString());
        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1);
        Assert.Equal(MikePDF.Protocol.Edits.EditOperation.Delete, Assert.Single(blob.Edits).Operation);

        Assert.Equal(UndoOutcome.Done, samples.Session.Redo()); // redoing it cancels the pair
        Assert.False(samples.Model.IsDirty);
        Assert.True(samples.Session.Store.Contains(note.Id));
    }

    [Fact]
    public void AC07_UndoingADeleteThatWasSavedAddsTheAnnotationAgainWithTheSameIdAndPlace()
    {
        var samples = new Samples();
        var first = samples.Session.Add(samples.Rectangle());
        var second = samples.Session.Add(samples.Ellipse());
        var third = samples.Session.Add(samples.Line());
        samples.Model.MarkSaved(samples.Model.BeginSave());
        Assert.True(samples.Session.Delete(second.Id));
        samples.Model.MarkSaved(samples.Model.BeginSave());

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());

        Assert.Equal([first.Id, second.Id, third.Id], samples.Session.Store.OnPage(0).Select(static a => a.Id)); // back in its place in the stacking order
        Assert.Equal("AddAnnotation", Assert.Single(samples.Model.PendingCommands).Cmd);
    }

    [Fact]
    public void AC07_ARestyleThatWasSavedIsUndoneByAnotherRestyleNotByForgettingIt()
    {
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        samples.Model.MarkSaved(samples.Model.BeginSave());
        Assert.True(samples.Session.Update(rect.Id, a => a with { Width = 9 }));
        samples.Model.MarkSaved(samples.Model.BeginSave());

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());

        Assert.Equal(rect.Width, samples.Session.Store.Get(rect.Id)!.Width);
        var command = Assert.Single(samples.Model.PendingCommands);
        Assert.Equal("UpdateAnnotation", command.Cmd);
        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1);
        Assert.Equal(MikePDF.Protocol.Edits.EditOperation.Replace, Assert.Single(blob.Edits).Operation); // written as a rebuild of the object the file holds
    }

    [Fact]
    public void AC07_DeletingAForeignAnnotationAfterASaveCannotBeUndoneUntilTheFileIsReread()
    {
        var samples = new Samples();
        var foreign = samples.Rectangle() with { Id = "other-app-1", Origin = AnnotationOrigin.Loaded, Author = "Alex" };
        samples.Session.Load([foreign]);
        Assert.True(samples.Session.Delete(foreign.Id));
        Assert.False(samples.Session.Store.Contains(foreign.Id));

        // Before a save, undo just takes the command back, and the annotation comes back exactly as it was.
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.True(samples.Session.Store.Contains(foreign.Id));
        Assert.Equal(AnnotationOrigin.Loaded, samples.Session.Store.Get(foreign.Id)!.Origin);

        // After a save the file has lost it, and there is no inverse that could draw it again.
        Assert.True(samples.Session.Delete(foreign.Id));
        samples.Model.MarkSaved(samples.Model.BeginSave());
        Assert.Equal(UndoOutcome.NoInverse, samples.Session.Undo());
        Assert.False(samples.Session.Store.Contains(foreign.Id));
    }

    // ---- AC-08: coalescing ----

    [Fact]
    public void AC08_NudgesWithinSevenHundredFiftyMillisecondsAreOneUndoStepAndALaterOneIsAnother()
    {
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        var start = rect.Rect;
        var commandsBefore = samples.Model.PendingCount;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(samples.Session.Nudge(rect.Id, 1, 0));
            samples.Time.Advance(TimeSpan.FromMilliseconds(200)); // 1,000 ms in all, but never 750 between two nudges
        }

        Assert.Equal(start.Left + 5, samples.Session.Store.Get(rect.Id)!.Rect.Left, 9);
        Assert.Equal(commandsBefore + 1, samples.Model.PendingCount); // one command for the whole run
        samples.Time.Advance(TimeSpan.FromMilliseconds(800));
        Assert.True(samples.Session.Nudge(rect.Id, 0, 10));
        Assert.Equal(commandsBefore + 2, samples.Model.PendingCount);

        Assert.Equal(UndoOutcome.Done, samples.Session.Undo()); // the second step
        Assert.Equal(start.Left + 5, samples.Session.Store.Get(rect.Id)!.Rect.Left, 9);
        Assert.Equal(start.Bottom, samples.Session.Store.Get(rect.Id)!.Rect.Bottom, 9);
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo()); // the whole run at once
        Assert.Equal(start, samples.Session.Store.Get(rect.Id)!.Rect);
    }

    [Fact]
    public void AC08_NudgesOfTwoAnnotationsOrAfterASaveAreNotMerged()
    {
        var samples = new Samples();
        var a = samples.Session.Add(samples.Rectangle());
        var b = samples.Session.Add(samples.Ellipse());
        var baseline = samples.Model.PendingCount;

        samples.Session.Nudge(a.Id, 1, 0);
        samples.Session.Nudge(b.Id, 1, 0);
        samples.Session.Nudge(b.Id, 1, 0);
        Assert.Equal(baseline + 2, samples.Model.PendingCount); // a's step, then b's run

        samples.Model.MarkSaved(samples.Model.BeginSave());
        samples.Session.Nudge(b.Id, 1, 0); // within the window of b's last nudge, but a save came between
        Assert.Equal(1, samples.Model.PendingCount);
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.Equal(a.Rect.Left + 1, samples.Session.Store.Get(a.Id)!.Rect.Left, 9); // the saved nudges were not touched
        Assert.Equal(b.Rect.Left + 2, samples.Session.Store.Get(b.Id)!.Rect.Left, 9);
    }

    [Fact]
    public void AC08_ADragIsOneCommandAndAMoveIsClampedToTheBox()
    {
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        var baseline = samples.Model.PendingCount;

        Assert.True(samples.Session.Move(rect.Id, -5_000, 0, new PdfRect(0, 0, 612, 792)));

        Assert.Equal(baseline + 1, samples.Model.PendingCount);
        Assert.Equal(8, samples.Session.Store.Get(rect.Id)!.Rect.Right, 9); // 8 points stay on the page
        Assert.True(samples.Session.Move(rect.Id, 0, 0)); // nothing to do is no command
        Assert.Equal(baseline + 1, samples.Model.PendingCount);
    }

    // ---- permissions in the commands ----

    [Fact]
    public void AC02_ForeignAnnotationsCanBeDeletedMovedAndRepliedToButNotRestyledOrResized()
    {
        var samples = new Samples();
        var foreign = samples.Rectangle() with { Id = "other-app-1", Origin = AnnotationOrigin.Loaded, Author = "Alex", Contents = "theirs" };
        samples.Session.Load([foreign]);

        Assert.False(samples.Session.Update(foreign.Id, a => a with { Color = Rgb.Black }));
        Assert.Equal("Annot_Disabled_Foreign", samples.Session.LastReason);
        Assert.False(samples.Session.ResizeTo(foreign.Id, new PdfRect(0, 0, 50, 50)));
        Assert.False(samples.Session.SetContents(foreign.Id, "mine now"));
        Assert.Equal("Note_Cannot_Edit_Others", samples.Session.LastReason);
        Assert.Equal(0, samples.Model.PendingCount);

        Assert.True(samples.Session.Move(foreign.Id, 10, 10));
        var move = Assert.Single(samples.Model.PendingCommands);
        Assert.Equal("MoveAnnotation", move.Cmd);
        Assert.False(move.Payload.TryGetProperty("replace", out _)); // the rectangle only: the other app's appearance stays as it is
        Assert.Equal(foreign.Rect.Left + 10, samples.Session.Store.Get(foreign.Id)!.Rect.Left, 9);
        Assert.NotNull(samples.Session.Reply(foreign.Id, "a reply to someone else's mark"));
        Assert.True(samples.Session.Delete(foreign.Id));
    }

    [Fact]
    public void AC02_ForeignCommentsCanBeEditedByTheirAuthorWithoutRedrawingTheAnnotation()
    {
        var samples = new Samples();
        var mine = samples.Note() with { Id = "other-app-2", Origin = AnnotationOrigin.Loaded, Author = "Mike", Contents = "first" };
        samples.Session.Load([mine]);

        Assert.True(samples.Session.SetContents(mine.Id, "second"));

        var command = Assert.Single(samples.Model.PendingCommands);
        Assert.Equal("UpdateAnnotation", command.Cmd);
        Assert.Equal("second", command.Payload.GetProperty("contents").GetString());
        Assert.False(command.Payload.TryGetProperty("replace", out _));
        Assert.Equal("second", samples.Session.Store.Get(mine.Id)!.Contents);
        samples.Session.Undo();
        Assert.Equal("first", samples.Session.Store.Get(mine.Id)!.Contents);
    }

    [Fact]
    public void AC02_AnnotationsWithNoNameCannotBeTargetedByTheSave()
    {
        var samples = new Samples();
        var nameless = samples.Rectangle() with { Id = "session:0:4", Origin = AnnotationOrigin.Loaded };
        var withObject = nameless with { Id = "session:0:5", ObjectNumber = 23 };
        samples.Session.Load([nameless, withObject]);

        Assert.False(samples.Session.Delete(nameless.Id));
        Assert.Equal(AnnotationRules.ReasonUnsupported, samples.Session.LastReason);
        Assert.True(samples.Session.Delete(withObject.Id));
        Assert.Equal(23, Assert.Single(samples.Model.PendingCommands).Payload.GetProperty("object").GetInt32());
    }

    // ---- sticky notes: threads ----

    [Fact]
    public void SN13_DeletingAThreadIsOneConfirmOneUndoStepAndUndoRestoresAllFourWithTheSameIdsAndLinks()
    {
        var samples = new Samples();
        var root = samples.Session.Add(samples.Note());
        var one = samples.Session.Reply(root.Id, "one")!;
        var two = samples.Session.Reply(one.Id, "two, to the first reply")!;
        var state = samples.Session.SetReviewState(root.Id, ReviewState.Accepted)!;
        Assert.Equal(3, samples.Session.Store.Thread(root.Id).Count - 1);
        var before = Snapshot(samples.Session.Store);
        var steps = samples.Model.PendingCount;

        Assert.True(samples.Session.Delete(root.Id));

        Assert.Equal(0, samples.Session.Store.Count);
        Assert.Equal(steps + 1, samples.Model.PendingCount);
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.Equal(before, Snapshot(samples.Session.Store));
        Assert.Equal(root.Id, samples.Session.Store.Get(one.Id)!.Parent);
        Assert.Equal(one.Id, samples.Session.Store.Get(two.Id)!.Parent);
        Assert.Equal(root.Id, samples.Session.Store.Get(state.Id)!.Parent);
    }

    [Fact]
    public void SN13_ADeleteOfAThreadBeforeASaveWritesNothingForTheAnnotationsItNeverSaved()
    {
        var samples = new Samples();
        var root = samples.Session.Add(samples.Note());
        samples.Session.Reply(root.Id, "one");
        Assert.True(samples.Session.Delete(root.Id));

        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1);

        Assert.Empty(blob.Edits);
    }

    [Fact]
    public void TM16_AMarkIsRecolouredResizedInThicknessAndCommentedAsOneStepEach()
    {
        var samples = new Samples();
        var underline = samples.Session.Add(samples.Underline());
        var baseline = samples.Model.PendingCount;

        Assert.True(samples.Session.Update(underline.Id, a => a with { Color = new Rgb(0, 0, 1), Width = 3, Opacity = 0.5 }));
        Assert.True(samples.Session.SetContents(underline.Id, "see page 4"));

        Assert.Equal(baseline + 2, samples.Model.PendingCount); // two commands in the journal, folded into the Add when the file is written
        var edit = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1).Edits.Single();
        Assert.Equal(new[] { 0f, 0f, 1f }, edit.Color);
        Assert.Equal(3f, edit.Width);
        Assert.Equal("see page 4", edit.Contents);
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.Equal("", samples.Session.Store.Get(underline.Id)!.Contents);
        Assert.Equal(3, samples.Session.Store.Get(underline.Id)!.Width); // only the comment was undone
        Assert.Equal(UndoOutcome.Done, samples.Session.Undo());
        Assert.Equal(underline.Width, samples.Session.Store.Get(underline.Id)!.Width);
    }

    // ---- ink: batches, strokes, erasing ----

    [Fact]
    public void INK05_StrokesOfOneStyleJoinABatchAndAStyleOrPageChangeStartsAnother()
    {
        var samples = new Samples();
        InkStroke Stroke(string id, float x) => new(id, [new InkSample(x, 10, 1), new InkSample(x + 30, 40, 1)]);

        var a = samples.Session.AddInkStroke(0, Stroke("1", 10), InkStyle.DefaultPen, Rgb.Black);
        samples.Time.Advance(TimeSpan.FromSeconds(2));
        var b = samples.Session.AddInkStroke(0, Stroke("2", 100), InkStyle.DefaultPen, Rgb.Black);
        var c = samples.Session.AddInkStroke(0, Stroke("3", 200), InkStyle.DefaultPen, new Rgb(1, 0, 0)); // another colour
        var d = samples.Session.AddInkStroke(1, Stroke("4", 10), InkStyle.DefaultPen, new Rgb(1, 0, 0)); // another page
        samples.Time.Advance(TimeSpan.FromSeconds(11)); // a pause
        var e = samples.Session.AddInkStroke(1, Stroke("5", 300), InkStyle.DefaultPen, new Rgb(1, 0, 0));

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(4, new[] { a.Id, c.Id, d.Id, e.Id }.Distinct().Count());
        Assert.Equal(2, ((InkPayload)samples.Session.Store.Get(a.Id)!.Payload).Strokes.Count);
        Assert.Equal(4, samples.Session.Store.Count);
        Assert.Equal(5, samples.Model.PendingCount); // a command for each stroke
        Assert.All(samples.Model.PendingCommands, static c => Assert.Equal("AddInkStroke", c.Cmd));
    }

    [Fact]
    public void INK05_UndoIsPerStrokeAndUndoingTheFirstStrokeOfABatchRemovesTheAnnotation()
    {
        var samples = new Samples();
        InkStroke Stroke(string id, float x) => new(id, [new InkSample(x, 10, 1), new InkSample(x + 30, 40, 1)]);
        var first = samples.Session.AddInkStroke(0, Stroke("1", 10), InkStyle.DefaultPen, Rgb.Black);
        samples.Session.AddInkStroke(0, Stroke("2", 100), InkStyle.DefaultPen, Rgb.Black);

        samples.Session.Undo();
        Assert.Single(((InkPayload)samples.Session.Store.Get(first.Id)!.Payload).Strokes);
        samples.Session.Undo();
        Assert.False(samples.Session.Store.Contains(first.Id));

        // After an undo the next stroke starts a new batch.
        var next = samples.Session.AddInkStroke(0, Stroke("3", 10), InkStyle.DefaultPen, Rgb.Black);
        Assert.NotEqual(first.Id, next.Id);
    }

    [Fact]
    public void INK05_ABatchThatASaveWroteIsClosedAndTheNextStrokeStartsANewOne()
    {
        var samples = new Samples();
        InkStroke Stroke(string id, float x) => new(id, [new InkSample(x, 10, 1), new InkSample(x + 30, 40, 1)]);
        var first = samples.Session.AddInkStroke(0, Stroke("1", 10), InkStyle.DefaultPen, Rgb.Black);
        samples.Model.MarkSaved(samples.Model.BeginSave());

        var second = samples.Session.AddInkStroke(0, Stroke("2", 100), InkStyle.DefaultPen, Rgb.Black);

        Assert.NotEqual(first.Id, second.Id);
        var edit = Assert.Single(AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1).Edits);
        Assert.Equal(second.Id, edit.Name);
    }

    [Fact]
    public void INK06_AnEraseGestureIsOneStepStrokeModeRemovesWholeStrokesAndPointModeSplitsThem()
    {
        var samples = new Samples();
        var samplesA = Enumerable.Range(0, 21).Select(i => new InkSample(100 + (i * 5), 100, 1)).ToArray();
        var samplesB = Enumerable.Range(0, 21).Select(i => new InkSample(100 + (i * 5), 200, 1)).ToArray();
        samples.Session.AddInkStroke(0, new InkStroke("a", samplesA), InkStyle.DefaultPen, Rgb.Black);
        var batch = samples.Session.AddInkStroke(0, new InkStroke("b", samplesB), InkStyle.DefaultPen, Rgb.Black);
        var steps = samples.Model.PendingCount;

        // Point mode across the middle of the lower stroke: two pieces.
        Assert.True(samples.Session.EraseInk(batch.Id, EraserMode.Point, [(150, 100), (152, 100)], 4));
        var afterPoint = (InkPayload)samples.Session.Store.Get(batch.Id)!.Payload;
        Assert.Equal(3, afterPoint.Strokes.Count);
        Assert.Equal(steps + 1, samples.Model.PendingCount);

        // Stroke mode on the upper stroke: it is removed whole.
        Assert.True(samples.Session.EraseInk(batch.Id, EraserMode.Stroke, [(120, 200)], 3));
        Assert.Equal(2, ((InkPayload)samples.Session.Store.Get(batch.Id)!.Payload).Strokes.Count);

        Assert.False(samples.Session.EraseInk(batch.Id, EraserMode.Point, [(400, 400)], 3)); // touching nothing is no command
        Assert.Equal(steps + 2, samples.Model.PendingCount);
        samples.Session.Undo();
        Assert.Equal(3, ((InkPayload)samples.Session.Store.Get(batch.Id)!.Payload).Strokes.Count);
        samples.Session.Undo();
        Assert.Equal(2, ((InkPayload)samples.Session.Store.Get(batch.Id)!.Payload).Strokes.Count);
        Assert.Equal(3, afterPoint.Strokes.Count);
    }

    [Fact]
    public void INK06_ErasingTheLastStrokeDeletesTheAnnotationAndUndoBringsItBack()
    {
        var samples = new Samples();
        var batch = samples.Session.AddInkStroke(0, new InkStroke("a", [new InkSample(100, 100, 1), new InkSample(140, 100, 1)]), InkStyle.DefaultPen, Rgb.Black);

        Assert.True(samples.Session.EraseInk(batch.Id, EraserMode.Stroke, [(120, 100)], 3));

        Assert.False(samples.Session.Store.Contains(batch.Id));
        Assert.Empty(AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1).Edits); // added and deleted in one snapshot: never written
        samples.Session.Undo();
        Assert.True(samples.Session.Store.Contains(batch.Id));
    }

    [Fact]
    public void INK13_AForeignInkCannotBeErasedPartially()
    {
        var samples = new Samples();
        var foreign = samples.Ink() with { Id = "other-app-ink", Origin = AnnotationOrigin.Loaded };
        samples.Session.Load([foreign]);

        Assert.False(samples.Session.EraseInk(foreign.Id, EraserMode.Point, [(110, 100)], 5));
        Assert.Equal(AnnotationRules.ReasonForeign, samples.Session.LastReason);
        Assert.True(samples.Session.Delete(foreign.Id)); // it can be deleted whole
    }

    [Fact]
    public void INK19_AJournalIsReplayedIntoTheSameDocument()
    {
        // The commands the session wrote, applied to a fresh store by the replay recovery uses, give the same annotations.
        var samples = new Samples();
        var rect = samples.Session.Add(samples.Rectangle());
        samples.Session.Add(samples.Highlight(0, "with a comment"));
        var batch = samples.Session.AddInkStroke(0, new InkStroke("a", [new InkSample(10, 10, 0.5f), new InkSample(60, 40, 1)]), InkStyle.DefaultPen, Rgb.Black);
        samples.Session.AddInkStroke(0, new InkStroke("b", [new InkSample(10, 80, 1), new InkSample(60, 90, 1)]), InkStyle.DefaultPen, Rgb.Black);
        samples.Session.Move(rect.Id, 20, 5);
        samples.Session.Update(rect.Id, a => a with { Width = 6 });
        var note = samples.Session.Add(samples.Note());
        samples.Session.Reply(note.Id, "r");
        samples.Session.EraseInk(batch.Id, EraserMode.Point, [(35, 25)], 3);
        samples.Session.Delete(note.Id);
        samples.Session.AddMany([samples.Underline(), samples.Squiggly()]);

        var fresh = new AnnotationStore();
        foreach (var command in samples.Model.PendingCommands)
        {
            MarkupReplay.Apply(fresh, command);
        }

        Assert.Equal(Snapshot(samples.Session.Store), Snapshot(fresh));
        Assert.Equal(samples.Session.Store.Count, fresh.Count);
    }
}
