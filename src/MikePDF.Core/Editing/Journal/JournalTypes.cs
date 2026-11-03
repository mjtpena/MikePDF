namespace MikePDF.Core.Editing.Journal;

/// <summary>
/// The limits of the journal, on read and on write (save-pipeline 3.4). The file is untrusted when it is read back: it is a file in a
/// directory the engine cannot reach, but a damaged disk, another program or an older version can leave anything in it.
/// </summary>
public sealed record JournalLimits
{
    /// <summary>Largest journal of one document: 64 MiB.</summary>
    public long MaxDocumentBytes { get; init; } = 64L << 20;

    /// <summary>Largest total of every journal: 512 MiB.</summary>
    public long MaxTotalBytes { get; init; } = 512L << 20;

    /// <summary>The warning chip appears at this fraction of the per-document limit.</summary>
    public double WarnFraction { get; init; } = 0.9;

    /// <summary>One command or control record, in bytes. A larger payload belongs in the blob folder.</summary>
    public int MaxRecordBytes { get; init; } = 1 << 20;

    /// <summary>The header line, which carries the page identities.</summary>
    public int MaxHeaderBytes { get; init; } = 8 << 20;

    public int MaxRecords { get; init; } = 2_000_000;

    /// <summary>JSON nesting inside a record.</summary>
    public int MaxJsonDepth { get; init; } = 16;

    /// <summary>Properties in one JSON object and elements in one array, counted over the whole record.</summary>
    public int MaxJsonElements { get; init; } = 100_000;

    public int MaxPathChars { get; init; } = 32_768;

    public int MaxPageIdentities { get; init; } = 100_000;

    /// <summary>Bytes the save control records may still use after the limit is reached: a save must be able to say what it is about to create.</summary>
    public int ControlReserveBytes { get; init; } = 1 << 20;

    public static JournalLimits Default { get; } = new();
}

/// <summary>The first line of a journal: which file the edits were made against (save-pipeline 3.4).</summary>
public sealed record JournalHeader(string DocPath, string? FileId, long Size, string? Modified, string Fingerprint, string App, IReadOnlyList<string> Pages);

public abstract record JournalEntry(long Seq, DateTimeOffset Time);

/// <summary>A command record.</summary>
public sealed record CommandEntry(long Seq, DateTimeOffset Time, EditCommand Command) : JournalEntry(Seq, Time);

public sealed record UndoEntry(long Seq, DateTimeOffset Time, long Target) : JournalEntry(Seq, Time);

public sealed record RedoEntry(long Seq, DateTimeOffset Time, long Target) : JournalEntry(Seq, Time);

/// <summary>
/// Written and flushed before the save creates any file, and naming every file it may create (S10: without it a crash while the output is being
/// written leaks a temporary file no record names).
/// </summary>
public sealed record SaveIntentEntry(long Seq, DateTimeOffset Time, string Temp, string? Backup, string Target) : JournalEntry(Seq, Time);

/// <summary>The output is complete and flushed; its hash lets recovery tell "the replace happened" from "it did not".</summary>
public sealed record SaveBeginEntry(long Seq, DateTimeOffset Time, string OutputSha256, long OutputLength) : JournalEntry(Seq, Time);

public sealed record SaveCommittedEntry(long Seq, DateTimeOffset Time) : JournalEntry(Seq, Time);

public sealed record DiscardEntry(long Seq, DateTimeOffset Time) : JournalEntry(Seq, Time);

/// <summary>A record with a valid envelope and checksum whose command this version does not know: skipped and counted, never an error.</summary>
public sealed record UnknownEntry(long Seq, DateTimeOffset Time, string Cmd) : JournalEntry(Seq, Time);

public enum JournalReadStatus
{
    /// <summary>Every record was valid.</summary>
    Ok,

    /// <summary>The file was empty: it was created and the process died before the header.</summary>
    Empty,

    /// <summary>The last line was cut off (no line end). Normal after a crash; silent.</summary>
    TornTail,

    /// <summary>A record in the middle, or a complete last one, failed to parse, its checksum or the sequence. The valid prefix is offered.</summary>
    Corrupt,

    /// <summary>The header is missing or invalid: nothing can be recovered.</summary>
    Unreadable,

    /// <summary>The file or a record is over a limit.</summary>
    TooLarge,
}

/// <param name="Status">How reading ended.</param>
/// <param name="Header">Null when the file is empty or unreadable.</param>
/// <param name="Entries">The valid prefix, in order, control and unknown records included.</param>
/// <param name="UnknownCommands">How many of <paramref name="Entries"/> are <see cref="UnknownEntry"/>.</param>
/// <param name="IgnoredBytes">Bytes after the valid prefix that were not read.</param>
/// <param name="Problem">A short, non-sensitive description of why reading stopped; null when it did not.</param>
public sealed record JournalReadResult(JournalReadStatus Status, JournalHeader? Header, IReadOnlyList<JournalEntry> Entries, int UnknownCommands, long IgnoredBytes, string? Problem)
{
    public int ValidRecords => Entries.Count;

    /// <summary>The records after the last <see cref="SaveCommittedEntry"/>: the unsaved session.</summary>
    public IEnumerable<JournalEntry> SinceLastCommit
    {
        get
        {
            var start = 0;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (Entries[i] is SaveCommittedEntry or DiscardEntry)
                {
                    start = i + 1;
                }
            }

            return Entries.Skip(start);
        }
    }

    public long LastSeq => Entries.Count == 0 ? 0 : Entries[^1].Seq;
}
