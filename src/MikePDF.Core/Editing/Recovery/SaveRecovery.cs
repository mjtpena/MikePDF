using System.Security.Cryptography;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Core.Editing.Recovery;

public enum InterruptedSave
{
    /// <summary>The journal records no save in progress.</summary>
    None,

    /// <summary>The save never reached the replace; its temporary file was removed and the edits are still unsaved.</summary>
    RolledBack,

    /// <summary>The replace happened and only the cleanup did not: the target holds the validated output. The journal can be cleared silently.</summary>
    AlreadySaved,

    /// <summary>The save had committed; the backup or temporary file it had not yet removed was removed. Says nothing about the journal.</summary>
    CleanedAfterCommit,

    /// <summary>The target was missing and was put back from the recorded backup (the <c>ReplaceFileW</c> fallback was killed between its two renames).</summary>
    TargetRestored,

    /// <summary>The target was missing and the validated output was moved into place.</summary>
    RolledForward,

    /// <summary>The target is missing and nothing recorded can restore it. Nothing was deleted.</summary>
    TargetMissing,
}

public sealed record InterruptedSaveReport(InterruptedSave Outcome, IReadOnlyList<string> RemovedFiles);

/// <summary>
/// What recovery does with a save that was interrupted (save-pipeline 3.5 item 5, from S10 sections 8 and 9). It acts only on what a
/// <c>SaveIntent</c> or <c>SaveBegin</c> record names, and only on names that have the exact form the save pipeline gives its files, in the
/// directory of the recorded target: a journal is untrusted input, and recovery must not delete whatever path a damaged one points at.
/// </summary>
public static class SaveRecovery
{
    /// <summary>The temporary file of a save: <c>.{name}.mikepdf-{8 hex}.tmp</c> beside the target.</summary>
    public static string TempNameFor(string targetFileName, string eightHex) => $".{targetFileName}.mikepdf-{eightHex}.tmp";

    /// <summary>The backup name the <c>ReplaceFileW</c> fallback uses: <c>.{name}.mikepdf-{8 hex}.bak</c> beside the target.</summary>
    public static string BackupNameFor(string targetFileName, string eightHex) => $".{targetFileName}.mikepdf-{eightHex}.bak";

    /// <summary>Whether <paramref name="path"/> is a file this pipeline names beside <paramref name="target"/>, with the given extension.</summary>
    public static bool IsPipelineFile(string target, string path, string extension)
    {
        try
        {
            if (!Path.IsPathFullyQualified(target) || !Path.IsPathFullyQualified(path) || target.Contains('\0', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
            {
                return false;
            }

            var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(target));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (targetDirectory is null || directory is null || !string.Equals(targetDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var targetName = Path.GetFileName(target);
            var name = Path.GetFileName(path);
            var prefix = "." + targetName + ".mikepdf-";
            var suffix = extension;
            return name.Length == prefix.Length + 8 + suffix.Length
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && name.Substring(prefix.Length, 8).All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static InterruptedSaveReport Run(JournalReadResult journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var removed = new List<string>();
        try
        {
            return RunCore(journal, removed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something holds a file (an antivirus scan, the document itself): leave everything and look again at the next start.
            return new InterruptedSaveReport(InterruptedSave.None, removed);
        }
    }

    private static InterruptedSaveReport RunCore(JournalReadResult journal, List<string> removed)
    {
        var all = journal.Entries.ToList();
        var intentIndex = all.FindLastIndex(static e => e is SaveIntentEntry);
        if (intentIndex < 0 || journal.Header is null)
        {
            return new InterruptedSaveReport(InterruptedSave.None, removed);
        }

        var intent = (SaveIntentEntry)all[intentIndex];
        var after = all.Skip(intentIndex + 1).ToList();
        var begin = after.OfType<SaveBeginEntry>().LastOrDefault();
        var target = intent.Target;
        var temp = IsPipelineFile(target, intent.Temp, ".tmp") ? intent.Temp : null;
        var backup = intent.Backup is not null && IsPipelineFile(target, intent.Backup, ".bak") ? intent.Backup : null;
        if (!Path.IsPathFullyQualified(target))
        {
            return new InterruptedSaveReport(InterruptedSave.None, removed);
        }

        if (after.Any(static e => e is SaveCommittedEntry))
        {
            // The save finished and said so; only the cleanup after it may not have run (the backup of the ReplaceFileW fallback is removed after the
            // commit record). Act only while the target is exactly the output the save validated.
            if (begin is null || !File.Exists(target) || HashOf(target) != begin.OutputSha256)
            {
                return new InterruptedSaveReport(InterruptedSave.None, removed);
            }

            Remove(temp, removed);
            Remove(backup, removed);
            return new InterruptedSaveReport(InterruptedSave.CleanedAfterCommit, removed);
        }

        var isSaveAs = !string.Equals(Path.GetFullPath(target), Path.GetFullPath(journal.Header.DocPath), StringComparison.OrdinalIgnoreCase);
        var outcome = InterruptedSave.RolledBack;

        if (!File.Exists(target))
        {
            if (!isSaveAs && FindBackup(target, backup) is { } found)
            {
                File.Move(found, target);
                outcome = InterruptedSave.TargetRestored;
            }
            else if (!isSaveAs && begin is not null && temp is not null && File.Exists(temp) && HashOf(temp) == begin.OutputSha256)
            {
                File.Move(temp, target);
                outcome = InterruptedSave.RolledForward;
            }
            else if (!isSaveAs)
            {
                // The original is gone and nothing recorded brings it back. Keep every file: the temporary one may be all there is.
                return new InterruptedSaveReport(InterruptedSave.TargetMissing, removed);
            }
        }
        else if (begin is not null && HashOf(target) == begin.OutputSha256)
        {
            outcome = InterruptedSave.AlreadySaved;
        }

        if (outcome != InterruptedSave.RolledForward)
        {
            Remove(temp, removed);
        }

        if (backup is not null && File.Exists(backup) && File.Exists(target)
            && (outcome == InterruptedSave.AlreadySaved || HashOf(backup) == HashOf(target)))
        {
            Remove(backup, removed);
        }

        return new InterruptedSaveReport(outcome, removed);
    }

    private static string? FindBackup(string target, string? recorded)
    {
        if (recorded is not null && File.Exists(recorded))
        {
            return recorded;
        }

        // ReplaceFileW without a backup name leaves the old file as "<name>~RF<hex>.TMP" beside the target (S10 8.2).
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var name = Path.GetFileName(target);
        var matches = Directory.EnumerateFiles(directory, name + "~RF*.TMP").ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void Remove(string? path, List<string> removed)
    {
        if (path is not null && File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            removed.Add(path);
        }
    }

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The eight hex digits of a fresh temporary or backup name.</summary>
    public static string NewSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
