namespace MikePDF.Core.Editing;

/// <summary>Where the model reports what it is about to do, before it does it. The journal is the real implementation (save-pipeline 3.4: flushed before the command is acknowledged).</summary>
public interface IEditJournal
{
    /// <summary>Records a command and returns the sequence number it was given. May throw: the model then changes nothing.</summary>
    long Command(EditCommand command);

    /// <summary>Records that the command with sequence number <paramref name="target"/> was undone.</summary>
    void Undo(long target);

    /// <summary>Records that the command with sequence number <paramref name="target"/> was redone.</summary>
    void Redo(long target);
}

/// <summary>A journal that records nothing and numbers commands itself.</summary>
public sealed class NullEditJournal : IEditJournal
{
    private long _next;

    public long Command(EditCommand command) => ++_next;

    public void Undo(long target)
    {
    }

    public void Redo(long target)
    {
    }
}

/// <summary>An edit arrived while a save was running. Editing is blocked for that time (save-pipeline 3.2, OQ-SP-5).</summary>
public sealed class EditsBlockedException() : InvalidOperationException("The document is being saved; edits are blocked until the save ends.");

/// <summary>What <see cref="EditModel.Undo"/> or <see cref="EditModel.Redo"/> did.</summary>
public enum UndoOutcome
{
    /// <summary>Nothing to undo or redo.</summary>
    None,

    /// <summary>Done.</summary>
    Done,

    /// <summary>The command was saved earlier, so undoing it needs an inverse command, and the command did not come with one.</summary>
    NoInverse,
}

/// <summary>
/// A frozen view of what a save has to write: the commands applied since the last save and still in force, in order. Its
/// <see cref="Sequence"/> is the new clean point (save-pipeline 3.3 stage 0).
/// </summary>
public sealed record EditSnapshot(long Sequence, IReadOnlyList<EditCommand> Pending, EditKinds Kinds)
{
    public bool IsEmpty => Pending.Count == 0;
}

/// <summary>
/// The edit model of one document (save-pipeline 3.7): a linear undo and redo stack, the list of commands a save still has to write, and the
/// clean point. It lives in the broker so it survives engine restarts and tab suspension.
/// <para>
/// A command is <i>pending</i> until a save has written it. Undoing a pending command just removes it, so undoing back to the last save makes the
/// document clean again. Undoing a command a save already wrote cannot remove it from the file: it applies the command's inverse as a new pending
/// command (save-pipeline 3.7, "an inverse change relative to the saved file"), and is refused when the command came without an inverse.
/// </para>
/// Not thread-safe: one owner, normally the document's session.
/// </summary>
public sealed class EditModel
{
    private sealed class Entry(long seq, EditCommand command, EditCommand? inverse)
    {
        public long Seq { get; set; } = seq;

        public EditCommand Command { get; } = command;

        public EditCommand? Inverse { get; } = inverse;

        public bool Saved { get; set; }
    }

    private readonly IEditJournal _journal;
    private readonly List<Entry> _done = [];
    private readonly Stack<Entry> _undone = new();
    private readonly List<(Entry? Owner, EditCommand Command)> _pending = [];
    private long _lastSeq;
    private long _snapshotSeq = -1;

    public EditModel(IEditJournal? journal = null) => _journal = journal ?? new NullEditJournal();

    /// <summary>Commands are waiting to be written: the dot on the tab.</summary>
    public bool IsDirty => _pending.Count > 0;

    public bool IsSaving => _snapshotSeq >= 0;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public int PendingCount => _pending.Count;

    /// <summary>The sequence number of the command an undo would undo (0 when none).</summary>
    public long TopSequence => _done.Count == 0 ? 0 : _done[^1].Seq;

    /// <summary>The sequence number of the command a redo would redo (0 when none).</summary>
    public long NextRedoSequence => _undone.Count == 0 ? 0 : _undone.Peek().Seq;

    /// <summary>The commands a save would write now, in order.</summary>
    public IReadOnlyList<EditCommand> PendingCommands => [.. _pending.Select(static p => p.Command)];

    /// <summary>The sequence number of the latest command or inverse the model took (0 before any).</summary>
    public long LastSequence => _lastSeq;

    public EditKinds PendingKinds => _pending.Aggregate(EditKinds.None, static (kinds, p) => kinds | p.Command.Kind);

    /// <summary>Applies a command: journals it first, then changes the model. Clears the redo stack.</summary>
    /// <param name="command">The change.</param>
    /// <param name="inverse">The command that undoes this one against a file that already contains it. Needed only to undo it after a save.</param>
    public long Apply(EditCommand command, EditCommand? inverse = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        GuardNotSaving();
        var seq = _journal.Command(command);
        Take(seq, command, inverse);
        return seq;
    }

    /// <summary>Undoes the latest command in force.</summary>
    public UndoOutcome Undo()
    {
        GuardNotSaving();
        if (_done.Count == 0)
        {
            return UndoOutcome.None;
        }

        var top = _done[^1];
        if (!top.Saved)
        {
            _journal.Undo(top.Seq);
            _done.RemoveAt(_done.Count - 1);
            _pending.RemoveAll(p => ReferenceEquals(p.Owner, top));
            _undone.Push(top);
            return UndoOutcome.Done;
        }

        if (top.Inverse is null)
        {
            return UndoOutcome.NoInverse;
        }

        var seq = _journal.Command(top.Inverse);
        _lastSeq = Math.Max(_lastSeq, seq);
        _done.RemoveAt(_done.Count - 1);
        _pending.Add((null, top.Inverse));
        _undone.Push(top);
        return UndoOutcome.Done;
    }

    /// <summary>Redoes the latest undone command.</summary>
    public UndoOutcome Redo()
    {
        GuardNotSaving();
        if (_undone.Count == 0)
        {
            return UndoOutcome.None;
        }

        var entry = _undone.Peek();
        if (entry.Saved)
        {
            // The file already has it; the inverse put on top of it is pending. Redoing cancels that pair when it is the last pending change.
            if (entry.Inverse is not null && _pending.Count > 0 && ReferenceEquals(_pending[^1].Command, entry.Inverse))
            {
                _journal.Redo(entry.Seq);
                _pending.RemoveAt(_pending.Count - 1);
            }
            else
            {
                var seq = _journal.Command(entry.Command);
                _lastSeq = Math.Max(_lastSeq, seq);
                _pending.Add((null, entry.Command));
            }
        }
        else
        {
            _journal.Redo(entry.Seq);
            _pending.Add((entry, entry.Command));
        }

        _undone.Pop();
        _done.Add(entry);
        return UndoOutcome.Done;
    }

    /// <summary>
    /// Freezes what a save has to write and blocks edits until <see cref="MarkSaved"/> or <see cref="AbortSave"/>. The snapshot's sequence number
    /// becomes the clean point when the save succeeds.
    /// </summary>
    public EditSnapshot BeginSave()
    {
        if (IsSaving)
        {
            throw new InvalidOperationException("A save is already running for this document.");
        }

        _snapshotSeq = _lastSeq;
        var commands = _pending.Select(static p => p.Command).ToList();
        return new EditSnapshot(_snapshotSeq, commands, commands.Aggregate(EditKinds.None, static (k, c) => k | c.Kind));
    }

    /// <summary>The save succeeded: everything in the snapshot is in the file. The undo stack is kept; the pending list is emptied.</summary>
    public void MarkSaved(EditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsSaving || snapshot.Sequence != _snapshotSeq)
        {
            throw new InvalidOperationException("This snapshot is not the one being saved.");
        }

        if (_lastSeq != _snapshotSeq || _pending.Count != snapshot.Pending.Count)
        {
            // Edits are blocked during a save, so this means the guard was bypassed. Saving would mark commands clean that no file holds.
            throw new InvalidOperationException("The model changed while it was being saved.");
        }

        foreach (var entry in _done)
        {
            entry.Saved = true;
        }

        _pending.Clear();
        _snapshotSeq = -1;
    }

    /// <summary>The save failed or was cancelled: the pending commands stay, and editing is allowed again.</summary>
    public void AbortSave() => _snapshotSeq = -1;

    /// <summary>Applies a command read back from a journal, with the sequence number it had there. Nothing is written.</summary>
    internal void ReplayCommand(long seq, EditCommand command) => Take(seq, command, inverse: null);

    internal bool ReplayUndo(long target)
    {
        if (_done.Count == 0 || _done[^1].Seq != target || _done[^1].Saved)
        {
            return false;
        }

        var top = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _pending.RemoveAll(p => ReferenceEquals(p.Owner, top));
        _undone.Push(top);
        return true;
    }

    internal bool ReplayRedo(long target)
    {
        if (_undone.Count == 0 || _undone.Peek().Seq != target)
        {
            return false;
        }

        var entry = _undone.Pop();
        _done.Add(entry);
        _pending.Add((entry, entry.Command));
        return true;
    }

    private void Take(long seq, EditCommand command, EditCommand? inverse)
    {
        var entry = new Entry(seq, command, inverse);
        _done.Add(entry);
        _undone.Clear();
        _pending.Add((entry, command));
        _lastSeq = Math.Max(_lastSeq, seq);
    }

    private void GuardNotSaving()
    {
        if (IsSaving)
        {
            throw new EditsBlockedException();
        }
    }
}
