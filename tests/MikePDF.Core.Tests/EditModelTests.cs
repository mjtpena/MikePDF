using MikePDF.Core.Editing;

namespace MikePDF.Core.Tests;

/// <summary>SP-01 and SP-02 (save-pipeline 14): the stage planner and the dirty state of the edit model.</summary>
public sealed class EditModelTests
{
    public static TheoryData<EditKinds, SaveStage[]> Plans => new()
    {
        { EditKinds.None, [] },
        { EditKinds.Annotations, [SaveStage.Preflight, SaveStage.AppendAnnotations, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Forms, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Pages, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Forms | EditKinds.Pages, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Annotations | EditKinds.Forms, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.AppendAnnotations, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Annotations | EditKinds.Pages, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.AppendAnnotations, SaveStage.Validate, SaveStage.Replace] },
        { EditKinds.Annotations | EditKinds.Forms | EditKinds.Pages, [SaveStage.Preflight, SaveStage.EngineSave, SaveStage.NormalizeTail, SaveStage.AppendAnnotations, SaveStage.Validate, SaveStage.Replace] },
    };

    [Theory]
    [MemberData(nameof(Plans))]
    public void EveryCombinationOfEditKindsSelectsTheSpecifiedStages(EditKinds kinds, SaveStage[] expected)
    {
        var plan = StagePlanner.Plan(kinds);

        Assert.Equal(expected, plan.Stages);
        Assert.Equal(kinds == EditKinds.None, plan.IsNothing);
    }

    [Fact]
    public void ANothingSavePlansNothingSoNoEngineSaveIsEverCalled()
    {
        var model = new EditModel();

        var snapshot = model.BeginSave();

        Assert.True(snapshot.IsEmpty);
        Assert.True(StagePlanner.Plan(snapshot).IsNothing);
        Assert.False(StagePlanner.Plan(snapshot).Runs(SaveStage.EngineSave));
    }

    [Fact]
    public void ThePlanComesFromTheCommandsInTheSnapshot()
    {
        var model = new EditModel();
        model.Apply(Edits.Add());
        model.Apply(Edits.Field());

        var plan = StagePlanner.Plan(model.BeginSave());

        Assert.True(plan.Runs(SaveStage.AppendAnnotations));
        Assert.True(plan.Runs(SaveStage.NormalizeTail));
    }

    [Fact]
    public void ADocumentIsDirtyAfterACommandAndCleanAfterASave()
    {
        var model = new EditModel();
        Assert.False(model.IsDirty);

        model.Apply(Edits.Add());
        Assert.True(model.IsDirty);

        var snapshot = model.BeginSave();
        model.MarkSaved(snapshot);

        Assert.False(model.IsDirty);
        Assert.Equal(0, model.PendingCount);
        Assert.True(model.CanUndo); // the stack is kept
    }

    [Fact]
    public void UndoingBackToTheCleanPointMakesTheDocumentCleanAgain()
    {
        var model = new EditModel();
        model.Apply(Edits.Add(name: "a"));
        model.MarkSaved(model.BeginSave());

        model.Apply(Edits.Add(name: "b"));
        Assert.True(model.IsDirty);

        Assert.Equal(UndoOutcome.Done, model.Undo());
        Assert.False(model.IsDirty);

        Assert.Equal(UndoOutcome.Done, model.Redo());
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void UndoingPastTheCleanPointMakesItDirtyThroughTheInverse()
    {
        var model = new EditModel();
        var inverse = Edits.Delete(name: "a");
        model.Apply(Edits.Add(name: "a"), inverse);
        model.MarkSaved(model.BeginSave());

        Assert.Equal(UndoOutcome.Done, model.Undo());

        Assert.True(model.IsDirty);
        Assert.Same(inverse, Assert.Single(model.PendingCommands));
    }

    [Fact]
    public void ACommandThatWasSavedAndHasNoInverseCannotBeUndone()
    {
        var model = new EditModel();
        model.Apply(Edits.Add());
        model.MarkSaved(model.BeginSave());

        Assert.Equal(UndoOutcome.NoInverse, model.Undo());
        Assert.False(model.IsDirty);
        Assert.True(model.CanUndo);
    }

    [Fact]
    public void RedoingASavedCommandThatWasUndoneCancelsTheInverse()
    {
        var model = new EditModel();
        model.Apply(Edits.Add(name: "a"), Edits.Delete(name: "a"));
        model.MarkSaved(model.BeginSave());
        model.Undo();
        Assert.True(model.IsDirty);

        model.Redo();

        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ANewCommandAfterAnUndoClearsTheRedoStack()
    {
        var model = new EditModel();
        model.Apply(Edits.Add(name: "a"));
        model.Undo();
        Assert.True(model.CanRedo);

        model.Apply(Edits.Add(name: "b"));

        Assert.False(model.CanRedo);
        Assert.Single(model.PendingCommands);
    }

    [Fact]
    public void EditsAreBlockedWhileASaveRunsAndAllowedAgainWhenItFails()
    {
        var model = new EditModel();
        model.Apply(Edits.Add());
        var snapshot = model.BeginSave();

        Assert.True(model.IsSaving);
        Assert.Throws<EditsBlockedException>(() => model.Apply(Edits.Add(name: "late")));
        Assert.Throws<EditsBlockedException>(() => model.Undo());
        Assert.Throws<InvalidOperationException>(() => model.BeginSave()); // a second save does not start a second pipeline

        model.AbortSave();

        Assert.False(model.IsSaving);
        Assert.True(model.IsDirty); // nothing was marked clean
        model.Apply(Edits.Add(name: "after"));
        Assert.Equal(2, model.PendingCount);
        Assert.Single(snapshot.Pending); // the snapshot is frozen
    }

    [Fact]
    public void ASnapshotThatIsNotTheOneBeingSavedIsRefused()
    {
        var model = new EditModel();
        model.Apply(Edits.Add());
        var first = model.BeginSave();
        model.AbortSave();
        model.Apply(Edits.Add(name: "b"));
        model.BeginSave();

        Assert.Throws<InvalidOperationException>(() => model.MarkSaved(first));
    }

    [Fact]
    public void TheJournalSeesACommandBeforeTheModelTakesIt()
    {
        var journal = new OrderJournal();
        var model = new EditModel(journal);

        model.Apply(Edits.Add());
        model.Undo();
        model.Redo();

        Assert.Equal(["command", "undo 1", "redo 1"], journal.Events);
    }

    [Fact]
    public void AJournalThatThrowsLeavesTheModelUnchanged()
    {
        var model = new EditModel(new OrderJournal { Throw = true });

        Assert.Throws<IOException>(() => model.Apply(Edits.Add()));

        Assert.False(model.IsDirty);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void ACommandOutsideTheCatalogIsRefused()
    {
        Assert.Throws<ArgumentException>(() => EditCommand.Create("FormatDisk", new { }));
        Assert.Throws<ArgumentException>(() => EditCommand.Create("AddAnnotation", new { seq = 1 })); // would shadow the envelope
    }

    private sealed class OrderJournal : IEditJournal
    {
        private long _seq;

        public bool Throw { get; init; }

        public List<string> Events { get; } = [];

        public long Command(EditCommand command)
        {
            if (Throw)
            {
                throw new IOException("injected");
            }

            Events.Add("command");
            return ++_seq;
        }

        public void Undo(long target) => Events.Add("undo " + target);

        public void Redo(long target) => Events.Add("redo " + target);
    }
}
