using System.Security.Cryptography;
using System.Text;

namespace MikePDF.Core.Editing.Journal;

/// <summary>A journal file open for appending. The broker holds it exclusively (save-pipeline 3.4).</summary>
public interface IJournalFile : IDisposable
{
    long Length { get; }

    /// <summary>Writes the bytes in one operation, so a crash leaves at most one torn line.</summary>
    void Append(ReadOnlySpan<byte> bytes);

    /// <summary>Makes everything appended so far durable (FlushFileBuffers).</summary>
    void Flush();
}

/// <summary>The journal's door to the disk. The tests put a fault-injecting one here (save-pipeline SP-04).</summary>
public interface IJournalFileSystem
{
    /// <summary>Creates a new journal file; fails when it exists.</summary>
    IJournalFile Create(string path);

    /// <summary>Opens an existing journal to append to it.</summary>
    IJournalFile OpenAppend(string path);

    /// <summary>Cuts a journal back to <paramref name="length"/> bytes: the valid prefix, before work continues on a journal that ended in a torn or corrupt record.</summary>
    void Truncate(string path, long length);
}

/// <summary>The real thing: a file opened with no sharing, write-through, and no buffer between a <see cref="IJournalFile.Append"/> and WriteFile.</summary>
public sealed class PhysicalJournalFileSystem : IJournalFileSystem
{
    public IJournalFile Create(string path) => new PhysicalJournalFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough));

    public IJournalFile OpenAppend(string path) => new PhysicalJournalFile(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough));

    public void Truncate(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        stream.Flush(flushToDisk: true);
    }

    private sealed class PhysicalJournalFile : IJournalFile
    {
        private readonly FileStream _stream;

        public PhysicalJournalFile(FileStream stream)
        {
            _stream = stream;
            _stream.Seek(0, SeekOrigin.End);
        }

        public long Length => _stream.Length;

        public void Append(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void Flush() => _stream.Flush(flushToDisk: true);

        public void Dispose() => _stream.Dispose();
    }
}

/// <summary>Which journal belongs to which file, and the fingerprint that says whether a file is still the one the edits were made against.</summary>
public static class JournalKey
{
    public const int FingerprintWindow = 64 * 1024;

    /// <summary>The first 32 hex characters of SHA-256 over the lower-cased, normalised full path in UTF-16 little-endian (save-pipeline 3.4).</summary>
    public static string DocKey(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path).Replace('/', '\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(full));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>SHA-256 over the first 64 KiB, the last 64 KiB and the size: cheap, and any real edit to the file changes it.</summary>
    public static string Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Fingerprint(stream);
    }

    public static string Fingerprint(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var window = new byte[FingerprintWindow];
        stream.Position = 0;
        var head = stream.ReadAtLeast(window, (int)Math.Min(length, FingerprintWindow), throwOnEndOfStream: false);
        hash.AppendData(window, 0, head);
        if (length > FingerprintWindow)
        {
            var tailStart = Math.Max(FingerprintWindow, length - FingerprintWindow);
            stream.Position = tailStart;
            var tail = stream.ReadAtLeast(window, (int)(length - tailStart), throwOnEndOfStream: false);
            hash.AppendData(window, 0, tail);
        }

        hash.AppendData(BitConverter.GetBytes(length));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
