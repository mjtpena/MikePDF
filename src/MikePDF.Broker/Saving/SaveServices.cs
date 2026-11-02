using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Broker.Saving;

public enum SaveMode
{
    /// <summary>Stages 0 to 6 on the open file.</summary>
    Save,

    /// <summary>Stages 0 to 5 into a temporary file in the chosen folder, then moved to the chosen name. The original is left as it was.</summary>
    SaveAs,

    /// <summary>The edits, then a full rewrite that flattens annotations into the pages; written to the chosen name. The document, its journal and its tab are untouched.</summary>
    FlattenedCopy,
}

/// <summary>The points between the steps of a save, named so that a test can stop the process at each of them (save-pipeline SP-16).</summary>
public enum SaveStep
{
    /// <summary>Preflight is done; nothing has been written anywhere.</summary>
    Start,

    /// <summary><c>SaveIntent</c> is on disk; the temporary file does not exist yet.</summary>
    IntentWritten,

    TempCreated,

    /// <summary>The writer host finished and flushed the output.</summary>
    OutputWritten,

    /// <summary><c>SaveBegin</c> (the output's hash) is on disk.</summary>
    BeginWritten,

    /// <summary>Stage 5 passed.</summary>
    Validated,

    /// <summary>The replace is done; <c>SaveCommitted</c> is not written yet.</summary>
    Replaced,

    CommittedWritten,

    /// <summary>Everything including the cleanup is done.</summary>
    Done,
}

/// <summary>A seam for fault injection. The product passes none.</summary>
public interface ISaveFaults
{
    /// <summary>Called as each step is reached. A test that throws here stops the save exactly as a kill would.</summary>
    void Reach(SaveStep point);
}

/// <summary>What the writer host said, reduced to what the pipeline decides on.</summary>
/// <remarks><c>Ok</c> says the operation succeeded; <c>Code</c> is a short, non-sensitive reason when it did not (the writer's error code, or a status name). For a structure check that found errors, <c>ErrorCodes</c> lists them; it is null when the host could not do the check at all.</remarks>
public sealed record HostResult(bool Ok, string? Code = null, int Added = 0, int Removed = 0, long Bytes = 0, IReadOnlyList<uint>? Mismatches = null, IReadOnlyList<uint>? ErrorCodes = null);

/// <summary>The writer host's part of a save. The real one is <see cref="WriterHostSaveHost"/>.</summary>
public interface ISaveHost
{
    Task<HostResult> AppendAsync(SafeFileHandle baseFile, ReadOnlyMemory<byte> blob, SafeFileHandle output, int pageCount, TimeSpan deadline, CancellationToken cancellationToken);

    Task<HostResult> RewriteAsync(SafeFileHandle baseFile, SafeFileHandle output, uint flags, TimeSpan deadline, CancellationToken cancellationToken);

    Task<HostResult> ValidateAsync(SafeFileHandle file, TimeSpan deadline, CancellationToken cancellationToken);

    Task<HostResult> VerifyAsync(SafeFileHandle baseFile, SafeFileHandle output, int pageCount, uint[] pages, uint[] added, uint[] removed, TimeSpan deadline, CancellationToken cancellationToken);
}

/// <summary>Stage 5's "fresh engine": opens a finished output in a new sandboxed engine and says what is wrong with it, or null.</summary>
public interface ISaveEngineCheck
{
    Task<string?> CheckAsync(string path, int expectedPages, CancellationToken cancellationToken);
}

/// <summary>The journal records of a save (save-pipeline 3.4).</summary>
public interface ISaveJournal
{
    void SaveIntent(string temp, string? backup, string target);

    void SaveBegin(string outputSha256, long outputLength);

    void SaveCommitted();

    /// <summary>The save succeeded and the edits are in the file: the journal and its blobs are removed (after the replace, never before).</summary>
    void Clear();
}

public sealed class NullSaveJournal : ISaveJournal
{
    public void SaveIntent(string temp, string? backup, string target)
    {
    }

    public void SaveBegin(string outputSha256, long outputLength)
    {
    }

    public void SaveCommitted()
    {
    }

    public void Clear()
    {
    }
}

/// <summary>The save records of an open document's journal. When the session has no journal (it could not be started, or it is full) the records are skipped: the save still runs.</summary>
public sealed class JournalSaveSink(EditSession session) : ISaveJournal
{
    public void SaveIntent(string temp, string? backup, string target) => session.Journal?.SaveIntent(temp, backup, target);

    public void SaveBegin(string outputSha256, long outputLength) => session.Journal?.SaveBegin(outputSha256, outputLength);

    public void SaveCommitted() => session.Journal?.SaveCommitted();

    public void Clear() => session.ClearJournal();
}

/// <summary>The timeouts of a save: 60 s plus 30 s per 100 MB for the engine and the writer host's stages, 10 s for the replace (save-pipeline 3.9).</summary>
public static class SaveDeadlines
{
    public static TimeSpan ForSize(long bytes) => TimeSpan.FromSeconds(60 + (30 * Math.Ceiling(Math.Max(0, bytes) / 100_000_000d)));

    public static readonly TimeSpan Replace = TimeSpan.FromSeconds(10);
}

/// <summary>Reads and hashes files by handle, in chunks. The broker reads bytes to hash and to compare; it never parses them.</summary>
internal static class FileBytes
{
    private const int Chunk = 1 << 20;

    public static string Sha256(SafeFileHandle file, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Chunk];
        for (long offset = 0; offset < length;)
        {
            var read = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(Chunk, length - offset)), offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The file is shorter than its length.");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void Copy(SafeFileHandle from, long length, SafeFileHandle to)
    {
        var buffer = new byte[Chunk];
        for (long offset = 0; offset < length;)
        {
            var read = RandomAccess.Read(from, buffer.AsSpan(0, (int)Math.Min(Chunk, length - offset)), offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The file is shorter than its length.");
            }

            RandomAccess.Write(to, buffer.AsSpan(0, read), offset);
            offset += read;
        }

        RandomAccess.FlushToDisk(to);
    }

    /// <summary>Whether the bytes in [<paramref name="start"/>, <paramref name="end"/>) of the file contain <paramref name="needle"/>.</summary>
    public static bool Contains(SafeFileHandle file, long start, long end, ReadOnlySpan<byte> needle)
    {
        var tail = needle.Length - 1;
        var buffer = new byte[Chunk + tail];
        var kept = 0; // bytes carried over at the start of the buffer, so a match across two reads is found
        for (var offset = start; offset < end;)
        {
            var read = RandomAccess.Read(file, buffer.AsSpan(kept, (int)Math.Min(Chunk, end - offset)), offset);
            if (read == 0)
            {
                break;
            }

            var window = buffer.AsSpan(0, kept + read);
            if (window.IndexOf(needle) >= 0)
            {
                return true;
            }

            kept = Math.Min(tail, window.Length);
            window[^kept..].CopyTo(buffer);
            offset += read;
        }

        return false;
    }
}
