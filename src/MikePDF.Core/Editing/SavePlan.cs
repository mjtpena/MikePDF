namespace MikePDF.Core.Editing;

/// <summary>The stages of a save (save-pipeline 3.1). The numbers are the spec's: stage 1 ("nothing to do") is the planner's empty answer.</summary>
public enum SaveStage
{
    Preflight = 0,
    EngineSave = 2,
    NormalizeTail = 3,
    AppendAnnotations = 4,
    Validate = 5,
    Replace = 6,
}

/// <summary>The stages a save runs, in order. An empty plan means there is nothing to do: no engine save and no file is touched.</summary>
public sealed record SavePlan(IReadOnlyList<SaveStage> Stages)
{
    public static SavePlan Nothing { get; } = new([]);

    public bool IsNothing => Stages.Count == 0;

    public bool Runs(SaveStage stage) => Stages.Contains(stage);
}

public static class StagePlanner
{
    /// <summary>
    /// Form or page changes need PDFium's incremental save and the writer host's tail normalisation (stages 2 and 3); annotation changes need
    /// the writer host's own update (stage 4); every save that does anything is preflighted, validated and replaced (0, 5, 6).
    /// </summary>
    public static SavePlan Plan(EditKinds kinds)
    {
        if (kinds == EditKinds.None)
        {
            return SavePlan.Nothing;
        }

        var stages = new List<SaveStage> { SaveStage.Preflight };
        if ((kinds & (EditKinds.Forms | EditKinds.Pages)) != 0)
        {
            stages.Add(SaveStage.EngineSave);
            stages.Add(SaveStage.NormalizeTail);
        }

        if ((kinds & EditKinds.Annotations) != 0)
        {
            stages.Add(SaveStage.AppendAnnotations);
        }

        stages.Add(SaveStage.Validate);
        stages.Add(SaveStage.Replace);
        return new SavePlan(stages);
    }

    public static SavePlan Plan(EditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsEmpty ? SavePlan.Nothing : Plan(snapshot.Kinds);
    }
}
