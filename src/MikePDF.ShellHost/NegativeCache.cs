using System.Security.Cryptography;
using MikePDF.Protocol;

namespace MikePDF.ShellHost;

/// <summary>
/// The negative cache of docs/features/shell-extensions.md 3.3 (SHELL-08): a file that failed is not tried again for ten minutes, so a file that crashes the engine
/// cannot become a crash loop every time Explorer repaints. With stream initialisation ShellHost has no file ID or last-write time, so the key is the size and the
/// SHA-256 of the bytes themselves, which are already in memory. The cache is in ShellHost's memory only, holds at most <see cref="MaxEntries"/> files and forgets the
/// oldest first.
/// </summary>
internal sealed class NegativeCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxEntries = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<FileKey, Entry> _entries = [];
    private readonly Queue<FileKey> _order = new();

    public readonly record struct FileKey(uint Size, string Hash)
    {
        public static FileKey Of(ReadOnlySpan<byte> bytes) => new((uint)bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private readonly record struct Entry(Status Status, DateTimeOffset Expires);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The failure remembered for this file, if it is still within its ten minutes.</summary>
    public Status? Lookup(FileKey key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (time.GetUtcNow() < entry.Expires)
                {
                    return entry.Status;
                }

                _entries.Remove(key);
            }

            return null;
        }
    }

    public void Remember(FileKey key, Status status)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                _order.Enqueue(key);
            }

            _entries[key] = new Entry(status, time.GetUtcNow() + Lifetime);
            while (_entries.Count > MaxEntries && _order.TryDequeue(out var oldest))
            {
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>Whether a failure is the <i>file's</i> doing and therefore worth remembering. A refusal for a reason that is not the file's (policy, a full queue, ShellHost leaving) is not.</summary>
    public static bool IsTheFilesFault(Status status) =>
        status is Status.Damaged or Status.Unsupported or Status.UnsupportedEncryption or Status.PasswordRequired or Status.PasswordIncorrect or Status.EngineCrashed or Status.Timeout;
}
