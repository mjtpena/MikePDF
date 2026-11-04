using System.Globalization;

namespace MikePDF.Core.Editing.Recovery;

/// <summary>What one line of the recovery dialog can do (design 14 section 2.7).</summary>
public enum RecoveryRowState
{
    /// <summary>The file is as the edits were made against: Restore offers them back. Checked.</summary>
    Ready,

    /// <summary>The file changed after the edits were made: "The file changed after you made these edits. Some changes may not fit." Unchecked, and Restore is still possible.</summary>
    FileChanged,

    /// <summary>The file is gone (moved, deleted): nothing to open the edits in. Unchecked and not selectable; Discard is the only thing to do.</summary>
    FileMissing,

    /// <summary>The journal could not be read at all: "Your saved changes for {0} couldn't be read." Discard only.</summary>
    Unreadable,
}

/// <summary>One document in the recovery dialog.</summary>
/// <param name="Candidate">What recovery found.</param>
/// <param name="Name">The file name (never the folder: messages carry no paths); empty when the journal could not be read.</param>
/// <param name="State">What can be done with it.</param>
/// <param name="Partial">A record in the middle of the journal was damaged: only the valid prefix is offered ("Some of your changes couldn't be recovered").</param>
public sealed record RecoveryRow(RecoveryCandidate Candidate, string Name, RecoveryRowState State, bool Partial)
{
    public bool CanRestore => State is RecoveryRowState.Ready or RecoveryRowState.FileChanged;

    public bool DefaultChecked => State == RecoveryRowState.Ready;
}

/// <summary>
/// The rows and the words of the recovery dialog, built from what <see cref="RecoveryService"/> found (save-pipeline 3.5, design 14 section 2.7). Pure: the dialog only draws it.
/// </summary>
public static class RecoveryPrompt
{
    /// <summary>The resource keys the summary and the dialog use, so a test can keep each of them in the resources.</summary>
    public static IReadOnlyList<string> ResourceKeys { get; } =
    [
        "Recover_Title", "Recover_Body", "Recover_Summary", "Recover_Restore", "Recover_Discard", "Recover_NotNow", "Recover_Partial", "Recover_FileChanged", "Recover_Unreadable",
        "Recover_List_One", "Recover_List_Other", "Recover_Multi_Title", "Recover_Multi_Body", "Recover_Multi_Footer", "Recover_Moved", "Recover_DiscardConfirm", "Recover_Open", "Recover_Damaged",
        "Recover_Join", "Recover_Unnamed", "Recover_Session_One", "Recover_Session_Other", "Recover_Part_Markup_One", "Recover_Part_Markup_Other", "Recover_Part_Form_One", "Recover_Part_Form_Other", "Recover_Part_Pages_One", "Recover_Part_Pages_Other",
    ];

    /// <summary>The rows for these candidates, the most recent change first.</summary>
    public static IReadOnlyList<RecoveryRow> Rows(IEnumerable<RecoveryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return
        [
            .. candidates
                .Select(Row)
                .OrderByDescending(static r => r.Candidate.Summary.LastChange ?? DateTimeOffset.MinValue)
                .ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public static RecoveryRow Row(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var name = candidate.DocumentPath is { Length: > 0 } path ? Path.GetFileName(path) : string.Empty;
        var state = candidate.Action switch
        {
            RecoveryAction.Replay => RecoveryRowState.Ready,
            RecoveryAction.WarnFileChanged => RecoveryRowState.FileChanged,
            RecoveryAction.FileMissing => RecoveryRowState.FileMissing,
            _ => RecoveryRowState.Unreadable,
        };
        return new RecoveryRow(candidate, name, state, candidate.PartiallyReadable);
    }

    /// <summary>"8 markup changes, 3 form changes, 2 page changes" from the counts, with the words from <paramref name="text"/> (resource key to string).</summary>
    public static string Counts(RecoverySummary summary, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>(3);
        Add(parts, summary.AnnotationChanges, "Recover_Part_Markup", text);
        Add(parts, summary.FormChanges, "Recover_Part_Form", text);
        Add(parts, summary.PageChanges, "Recover_Part_Pages", text);
        return string.Join(text("Recover_Join"), parts);
    }

    /// <summary>"{counts}. Last change {time}." (the time as a short local time; omitted when the journal has no time).</summary>
    public static string Summary(RecoverySummary summary, Func<string, string> text)
    {
        var counts = Counts(summary, text);
        if (summary.LastChange is not { } last)
        {
            return counts;
        }

        return string.Format(CultureInfo.CurrentCulture, text("Recover_Summary"), counts, last.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    }

    private static void Add(List<string> parts, int count, string key, Func<string, string> text)
    {
        if (count > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, text(key + (count == 1 ? "_One" : "_Other")), count));
        }
    }
}
