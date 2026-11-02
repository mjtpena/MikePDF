namespace MikePDF.Core.Editing.Journal;

/// <summary>One journal found on disk.</summary>
public sealed record StoredJournal(string DocKey, string Path, long Length);

/// <summary>
/// The journals directory (save-pipeline 3.4: <c>LocalState\private\journals</c>, which only the signed-in user and SYSTEM can open: the caller
/// makes it with <c>PrivateDirectory.Ensure</c>). Names, sizes and the total budget; it reads and writes no journal content itself.
/// </summary>
public sealed class JournalStore
{
    public const string Extension = ".jsonl";

    private readonly IJournalFileSystem _files;

    public JournalStore(string directory, IJournalFileSystem? files = null, JournalLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _files = files ?? new PhysicalJournalFileSystem();
        Limits = limits ?? JournalLimits.Default;
        System.IO.Directory.CreateDirectory(directory);
        Budget = new JournalBudget(Limits.MaxTotalBytes);
        Budget.Add(List().Sum(static j => j.Length));
    }

    public string Directory { get; }

    public JournalLimits Limits { get; }

    public JournalBudget Budget { get; }

    public string PathFor(string documentPath) => System.IO.Path.Combine(Directory, JournalKey.DocKey(documentPath) + Extension);

    public string BlobDirectoryFor(string documentPath) => System.IO.Path.Combine(Directory, JournalKey.DocKey(documentPath) + ".blobs");

    /// <summary>Starts the journal of a document whose edits begin now. Fails when it already has one: recovery decides what happens to that first.</summary>
    public JournalWriter Create(string documentPath, JournalHeader header, TimeProvider? time = null) =>
        JournalWriter.Create(_files, PathFor(documentPath), header, Limits, Budget, time);

    public JournalWriter Resume(string documentPath, JournalReadResult read, TimeProvider? time = null) => ResumeAt(PathFor(documentPath), read, time);

    public JournalWriter ResumeAt(string journalPath, JournalReadResult read, TimeProvider? time = null) =>
        JournalWriter.Resume(_files, journalPath, read, Limits, Budget, time);

    public bool Exists(string documentPath) => File.Exists(PathFor(documentPath));

    public IReadOnlyList<StoredJournal> List()
    {
        var found = new List<StoredJournal>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
        {
            var key = System.IO.Path.GetFileNameWithoutExtension(path);
            if (key.Length == 32 && key.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                found.Add(new StoredJournal(key, path, new FileInfo(path).Length));
            }
        }

        return found;
    }

    /// <summary>Reads a journal with the store's limits. A file over the limit is not read at all.</summary>
    public JournalReadResult Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > Limits.MaxDocumentBytes + Limits.ControlReserveBytes)
        {
            return new JournalReadResult(JournalReadStatus.TooLarge, null, [], 0, length, "The journal is over its size limit.");
        }

        try
        {
            return JournalCodec.Parse(File.ReadAllBytes(path), Limits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new JournalReadResult(JournalReadStatus.Unreadable, null, [], 0, length, "The journal could not be read.");
        }
    }

    /// <summary>Deletes a document's journal and its blob folder (after a successful save, a discard, or Settings > Clear recovery data).</summary>
    public void Delete(string documentPath) => DeleteByPath(PathFor(documentPath));

    public void DeleteByPath(string journalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        if (File.Exists(journalPath))
        {
            var length = new FileInfo(journalPath).Length;
            File.Delete(journalPath);
            Budget.Release(length);
        }

        var blobs = System.IO.Path.ChangeExtension(journalPath, ".blobs");
        if (System.IO.Directory.Exists(blobs))
        {
            System.IO.Directory.Delete(blobs, recursive: true);
        }
    }

    /// <summary>Settings > Clear recovery data: every journal, nothing else.</summary>
    public int ClearAll()
    {
        var all = List();
        foreach (var journal in all)
        {
            DeleteByPath(journal.Path);
        }

        return all.Count;
    }

    /// <summary>The header every new journal of a document starts with: the file as it is now.</summary>
    public static JournalHeader HeaderFor(string documentPath, string appVersion, IReadOnlyList<string>? pages = null)
    {
        var info = new FileInfo(documentPath);
        return new JournalHeader(
            System.IO.Path.GetFullPath(documentPath),
            null,
            info.Length,
            info.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            JournalKey.Fingerprint(documentPath),
            appVersion,
            pages ?? []);
    }
}
