using MikePDF.Core.Editing.Journal;

namespace MikePDF.Core.Editing.Recovery;

/// <summary>What recovery found out about one journal and its file (save-pipeline 3.5 item 3, SP-07).</summary>
public enum RecoveryAction
{
    /// <summary>Nothing unsaved: an empty journal, or one whose save finished. Removed without a word.</summary>
    ClearSilently,

    /// <summary>The file is as the edits were made against: offer Restore, Discard, Not now.</summary>
    Replay,

    /// <summary>The file is neither the one the edits were made against nor the output of a save: warn, and let the user choose.</summary>
    WarnFileChanged,

    /// <summary>The file is gone.</summary>
    FileMissing,

    /// <summary>The journal cannot be read at all: offer Discard.</summary>
    Unreadable,
}

/// <summary>The counts the recovery prompt shows ("8 comments, 3 form fields, 2 page changes").</summary>
public sealed record RecoverySummary(int AnnotationChanges, int FormChanges, int PageChanges, DateTimeOffset? LastChange)
{
    public int Total => AnnotationChanges + FormChanges + PageChanges;
}

public sealed record ReplayReport(int Commands, int Applied, int Skipped, int UnknownCommands, int PendingAfter);

public sealed record RecoveryCandidate(string JournalPath, string? DocumentPath, RecoveryAction Action, JournalReadResult Read, RecoverySummary Summary, InterruptedSaveReport Interrupted)
{
    /// <summary>Some of the user's changes could not be recovered because a record in the middle of the journal was damaged.</summary>
    public bool PartiallyReadable => Read.Status == JournalReadStatus.Corrupt;
}

/// <summary>
/// Looks at every journal when the app starts or a document opens (save-pipeline 3.5): finishes or rolls back a save that was interrupted,
/// decides per journal what is to be offered, and rebuilds the model and the undo and redo stacks on Restore.
/// </summary>
public sealed class RecoveryService(JournalStore store)
{
    /// <summary>Examines every journal. A journal with nothing unsaved is removed; the rest come back as candidates.</summary>
    public IReadOnlyList<RecoveryCandidate> Scan()
    {
        var candidates = new List<RecoveryCandidate>();
        foreach (var journal in store.List())
        {
            if (Examine(journal.Path) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>The candidate for the journal of one document, if it has unsaved changes.</summary>
    public RecoveryCandidate? Find(string documentPath) => store.Exists(documentPath) ? Examine(store.PathFor(documentPath)) : null;

    public RecoveryCandidate? Examine(string journalPath)
    {
        var read = store.Read(journalPath);
        var none = new InterruptedSaveReport(InterruptedSave.None, []);
        if (read.Status == JournalReadStatus.Empty)
        {
            store.DeleteByPath(journalPath);
            return null;
        }

        if (read.Problem == JournalStore.InUse)
        {
            return null; // being edited right now by another instance of the app
        }

        if (read.Status is JournalReadStatus.Unreadable or JournalReadStatus.TooLarge || read.Header is null)
        {
            return new RecoveryCandidate(journalPath, null, RecoveryAction.Unreadable, read, new RecoverySummary(0, 0, 0, null), none);
        }

        var interrupted = SaveRecovery.Run(read);
        var summary = Summarise(read);
        var documentPath = read.Header.DocPath;
        if (interrupted.Outcome == InterruptedSave.AlreadySaved || summary.Total == 0)
        {
            store.DeleteByPath(journalPath);
            return null;
        }

        var action = Classify(read, documentPath);
        if (action == RecoveryAction.ClearSilently)
        {
            store.DeleteByPath(journalPath);
            return null;
        }

        return new RecoveryCandidate(journalPath, documentPath, action, read, summary, interrupted);
    }

    /// <summary>
    /// The idempotency table (SP-07). The file's hash equals a <c>SaveBegin</c> output: the save completed, only the cleanup did not.
    /// It matches the header's fingerprint: the edits are still unsaved, replay them. It matches neither: the file changed. No file: error.
    /// </summary>
    public static RecoveryAction Classify(JournalReadResult read, string documentPath)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!File.Exists(documentPath))
        {
            return RecoveryAction.FileMissing;
        }

        var begins = read.SinceLastCommit.OfType<SaveBeginEntry>().ToList();
        if (begins.Count > 0)
        {
            var hash = JournalKey.Sha256Hex(documentPath);
            if (begins.Any(b => b.OutputSha256 == hash))
            {
                return RecoveryAction.ClearSilently;
            }
        }

        return read.Header is { } header && JournalKey.Fingerprint(documentPath) == header.Fingerprint ? RecoveryAction.Replay : RecoveryAction.WarnFileChanged;
    }

    public static RecoverySummary Summarise(JournalReadResult read)
    {
        var model = new EditModel(new NullEditJournal());
        Replay(read, model, canApply: null);
        var annotations = 0;
        var forms = 0;
        var pages = 0;
        foreach (var command in model.PendingCommands)
        {
            switch (command.Kind)
            {
                case EditKinds.Annotations:
                    annotations++;
                    break;
                case EditKinds.Forms:
                    forms++;
                    break;
                case EditKinds.Pages:
                    pages++;
                    break;
            }
        }

        var last = read.SinceLastCommit.OfType<CommandEntry>().Select(static c => (DateTimeOffset?)c.Time).LastOrDefault();
        return new RecoverySummary(annotations, forms, pages, last);
    }

    /// <summary>
    /// Applies the records of the unsaved session, in order, to <paramref name="model"/>, rebuilding the undo and redo stacks (an <c>Undo</c> pops,
    /// a <c>Redo</c> pushes). A command <paramref name="canApply"/> refuses (a page that no longer exists, a form field not found) is skipped and
    /// counted; so is an undo or redo that no longer fits. Nothing is written.
    /// </summary>
    public static ReplayReport Replay(JournalReadResult read, EditModel model, Func<EditCommand, bool>? canApply)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(model);
        var commands = 0;
        var applied = 0;
        var skipped = 0;
        foreach (var entry in read.SinceLastCommit)
        {
            switch (entry)
            {
                case CommandEntry command:
                    commands++;
                    if (canApply is not null && !canApply(command.Command))
                    {
                        skipped++;
                        break;
                    }

                    model.ReplayCommand(command.Seq, command.Command);
                    applied++;
                    break;
                case UndoEntry undo:
                    if (!model.ReplayUndo(undo.Target))
                    {
                        skipped++;
                    }

                    break;
                case RedoEntry redo:
                    if (!model.ReplayRedo(redo.Target))
                    {
                        skipped++;
                    }

                    break;
            }
        }

        return new ReplayReport(commands, applied, skipped, read.UnknownCommands, model.PendingCount);
    }

    /// <summary>
    /// Restore: the journal is continued (cut back to its valid prefix first) and the model rebuilt from it. The document is then dirty and
    /// nothing is saved. The caller owns the returned session.
    /// </summary>
    public (EditSession Session, ReplayReport Report) Restore(RecoveryCandidate candidate, Func<EditCommand, bool>? canApply = null, JournalFailureMode mode = JournalFailureMode.Degraded)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.DocumentPath is null || candidate.Read.Header is null)
        {
            throw new InvalidOperationException("This journal cannot be restored.");
        }

        var writer = store.ResumeAt(candidate.JournalPath, candidate.Read);
        var session = new EditSession(store, candidate.DocumentPath, string.Empty, mode, writer);
        var report = Replay(candidate.Read, session.Model, canApply);
        return (session, report);
    }

    /// <summary>Discard: the journal and its blobs are deleted.</summary>
    public void Discard(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        store.DeleteByPath(candidate.JournalPath);
    }
}
