namespace MikePDF.Core.Editing.Journal;

/// <summary>How well the journal is doing, as the status chip shows it (save-pipeline 3.4 and 5).</summary>
public enum JournalHealth
{
    Healthy,

    /// <summary>Over 90% of the per-document limit: "save soon".</summary>
    NearLimit,

    /// <summary>At the limit or a write failed: commands still apply but are not backed up (OQ-SP-1, degraded).</summary>
    Unavailable,
}

/// <summary>A command could not be written to the journal. The writer is now <see cref="JournalHealth.Unavailable"/> for commands.</summary>
public sealed class JournalUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The total of every journal against the 512 MiB limit (save-pipeline 3.4). Shared by the writers of one store.</summary>
public sealed class JournalBudget(long maxTotalBytes)
{
    private long _used;

    public long Used => Interlocked.Read(ref _used);

    public long Max { get; } = maxTotalBytes;

    internal bool TryReserve(long bytes)
    {
        if (Interlocked.Add(ref _used, bytes) <= Max)
        {
            return true;
        }

        Interlocked.Add(ref _used, -bytes);
        return false;
    }

    internal void Add(long bytes) => Interlocked.Add(ref _used, bytes);

    internal void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}

/// <summary>
/// Appends a document's journal (save-pipeline 3.4). Each record is one write followed by a flush to disk, and the method returns only after
/// both, which is what "flushed before the command is acknowledged" means (§7.4). Not thread-safe: one writer per document.
/// <para>
/// When the limit is reached or a write fails the writer stops taking commands (<see cref="JournalUnavailableException"/>) but keeps taking the
/// save control records, within a small reserve, because a save must be able to say what files it is about to create.
/// </para>
/// </summary>
public sealed class JournalWriter : IEditJournal, IDisposable
{
    private readonly IJournalFile _file;
    private readonly JournalLimits _limits;
    private readonly JournalBudget? _budget;
    private readonly TimeProvider _time;
    private long _nextSeq;
    private bool _failed;
    private bool _disposed;

    private JournalWriter(IJournalFile file, long lastSeq, JournalLimits limits, JournalBudget? budget, TimeProvider time)
    {
        _file = file;
        _nextSeq = lastSeq + 1;
        _limits = limits;
        _budget = budget;
        _time = time;
    }

    /// <summary>Raised when <see cref="Health"/> changes.</summary>
    public event EventHandler? HealthChanged;

    public JournalHealth Health { get; private set; }

    public long LastSequence => _nextSeq - 1;

    public long Length => _file.Length;

    /// <summary>Creates a new journal and writes its header (flushed).</summary>
    public static JournalWriter Create(IJournalFileSystem files, string path, JournalHeader header, JournalLimits? limits = null, JournalBudget? budget = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        limits ??= JournalLimits.Default;
        var bytes = JournalCodec.EncodeHeader(header, limits);
        if (budget is not null && !budget.TryReserve(bytes.Length))
        {
            throw new JournalUnavailableException("The journals are over their total size limit.");
        }

        IJournalFile file;
        try
        {
            file = files.Create(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            budget?.Release(bytes.Length);
            throw new JournalUnavailableException("The journal could not be created.", e);
        }

        try
        {
            file.Append(bytes);
            file.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            file.Dispose();
            budget?.Release(bytes.Length);
            throw new JournalUnavailableException("The journal header could not be written.", e);
        }

        return new JournalWriter(file, 0, limits, budget, time ?? TimeProvider.System);
    }

    /// <summary>Continues a journal that was read back (after a recovery that keeps working on the same edits).</summary>
    public static JournalWriter Resume(IJournalFileSystem files, string path, JournalReadResult read, JournalLimits? limits = null, JournalBudget? budget = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        if (read.Status is not (JournalReadStatus.Ok or JournalReadStatus.TornTail) || read.Header is null)
        {
            throw new ArgumentException("Only a journal that was read without corruption can be continued.", nameof(read));
        }

        IJournalFile file;
        try
        {
            if (read.IgnoredBytes > 0)
            {
                // Whatever followed the valid prefix was not readable and is not wanted: a new record must not land after half of an old one.
                files.Truncate(path, new FileInfo(path).Length - read.IgnoredBytes);
                budget?.Release(read.IgnoredBytes);
            }

            file = files.OpenAppend(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new JournalUnavailableException("The journal could not be opened.", e);
        }

        return new JournalWriter(file, read.LastSeq, limits ?? JournalLimits.Default, budget, time ?? TimeProvider.System);
    }

    public long Command(EditCommand command) => Append(seq => new CommandEntry(seq, Now, command), isControl: false);

    public void Undo(long target) => Append(seq => new UndoEntry(seq, Now, target), isControl: false);

    public void Redo(long target) => Append(seq => new RedoEntry(seq, Now, target), isControl: false);

    public long SaveIntent(string temp, string? backup, string target) => Append(seq => new SaveIntentEntry(seq, Now, temp, backup, target), isControl: true);

    public long SaveBegin(string outputSha256, long outputLength) => Append(seq => new SaveBeginEntry(seq, Now, outputSha256, outputLength), isControl: true);

    public long SaveCommitted() => Append(seq => new SaveCommittedEntry(seq, Now), isControl: true);

    public long Discard() => Append(seq => new DiscardEntry(seq, Now), isControl: true);

    private DateTimeOffset Now => _time.GetUtcNow();

    private long Append(Func<long, JournalEntry> make, bool isControl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed)
        {
            throw new JournalUnavailableException("The journal is not available.");
        }

        var seq = _nextSeq;
        var line = JournalCodec.EncodeEntry(make(seq), _limits);
        var ceiling = _limits.MaxDocumentBytes + (isControl ? _limits.ControlReserveBytes : 0);
        if (_file.Length + line.Length > ceiling || (_budget is not null && !isControl && !_budget.TryReserve(line.Length)))
        {
            SetHealth(JournalHealth.Unavailable);
            throw new JournalUnavailableException("The journal is full.");
        }

        if (_budget is not null && isControl)
        {
            _budget.Add(line.Length);
        }

        try
        {
            _file.Append(line);
            _file.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // What reached the disk is unknown; a later record would follow a possibly torn line, so stop here.
            _failed = true;
            SetHealth(JournalHealth.Unavailable);
            throw new JournalUnavailableException("The journal could not be written.", e);
        }

        _nextSeq = seq + 1;
        if (Health == JournalHealth.Healthy && _file.Length >= _limits.MaxDocumentBytes * _limits.WarnFraction)
        {
            SetHealth(JournalHealth.NearLimit);
        }

        return seq;
    }

    private void SetHealth(JournalHealth health)
    {
        if (Health != health)
        {
            Health = health;
            HealthChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _file.Dispose();
        }
    }
}
