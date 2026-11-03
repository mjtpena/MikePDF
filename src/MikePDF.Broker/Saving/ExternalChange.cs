namespace MikePDF.Broker.Saving;

/// <summary>What the file at a document's path looked like when the document was opened (or last saved): enough to notice that another program changed it.</summary>
public readonly record struct FileStamp(long Length, long LastWriteUtcTicks, FileIdentity? Identity)
{
    /// <summary>The stamp of the file now, or null when there is no file (or it cannot be looked at).</summary>
    public static FileStamp? Take(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, FileReplacer.IdentityOfPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="ExternalChanges.Check"/> found.</summary>
public enum ExternalChange
{
    /// <summary>The file is as it was (or cannot be told to be otherwise).</summary>
    None,

    /// <summary>Another program replaced or rewrote the file: "{name} changed on disk."</summary>
    Changed,

    /// <summary>The file was moved or deleted: "{name} was moved or deleted."</summary>
    Gone,
}

/// <summary>
/// The "changed on disk" check (save-pipeline 3.8, design 14 section 2.9). It runs when the window is activated or a tab is shown, never on a timer. A different file ID is a replacement; a different length or
/// last-write time is a rewrite. (The save itself does not rely on this: it compares file IDs, which a timestamp cannot stand in for, save-pipeline 3.6 item 3. This only says when to ask.)
/// </summary>
public static class ExternalChanges
{
    public static ExternalChange Check(FileStamp before, string path)
    {
        var now = FileStamp.Take(path);
        if (now is not { } after)
        {
            // A path that cannot be looked at because the folder is on a share that is away is not "gone": only a path that is certainly absent is.
            return File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))) ? ExternalChange.None : ExternalChange.Gone;
        }

        if (before.Identity is { } opened && after.Identity is { } current && opened != current)
        {
            return ExternalChange.Changed;
        }

        return before.Length != after.Length || before.LastWriteUtcTicks != after.LastWriteUtcTicks ? ExternalChange.Changed : ExternalChange.None;
    }
}
