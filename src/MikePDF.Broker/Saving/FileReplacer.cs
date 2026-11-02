using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace MikePDF.Broker.Saving;

/// <summary>Which file a handle or a path names: the volume serial number and the 128-bit file ID. Equal means the same file object.</summary>
public readonly record struct FileIdentity(ulong VolumeSerial, string FileId)
{
    public override string ToString() => $"{VolumeSerial:x}:{FileId}";
}

public enum ReplaceMechanism
{
    /// <summary><c>SetFileInformationByHandle(FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS)</c>: one system call, the old file freed when its last holder closes.</summary>
    PosixRename,

    /// <summary><c>ReplaceFileW</c> with a backup name: where the rename is not supported (SMB, FAT). Not atomic against process death.</summary>
    ReplaceFile,

    /// <summary>The target did not exist: a plain move.</summary>
    Move,
}

/// <summary>Why a target cannot be saved over (save-pipeline 3.8). Each is "Save is refused, Save As is offered".</summary>
public enum TargetProblem
{
    None,
    ReadOnly,

    /// <summary>More than one name for the file: every replace mechanism breaks the link, and the other names keep the old content.</summary>
    HardLinks,

    /// <summary>EFS-encrypted: the copy of the metadata cannot carry the encryption.</summary>
    Encrypted,
}

/// <summary>What a save needs to know about its target. <c>FinalPath</c> is the path with symbolic links resolved, which a replace must target so the link survives; <c>Exists</c> is false when there is nothing at the path (a new file).</summary>
public sealed record TargetInfo(string Requested, string FinalPath, bool Exists, TargetProblem Problem, FileIdentity? Identity, uint LinkCount, bool CloudPlaceholder);

public enum ReplaceFailure
{
    /// <summary>The path now names another file than the one the document was opened from (file ID differs): ask Overwrite, Save As, Cancel.</summary>
    ChangedOnDisk,

    /// <summary>The original was moved or deleted while it was open: report it and offer Save As.</summary>
    TargetGone,

    /// <summary>Still locked after the retries.</summary>
    InUse,

    /// <summary>Anything else; <see cref="ReplaceException.Win32Error"/> says what.</summary>
    Native,
}

public sealed class ReplaceException(ReplaceFailure failure, int win32Error, string message) : IOException(message)
{
    public ReplaceFailure Failure { get; } = failure;

    public int Win32Error { get; } = win32Error;
}

/// <summary>How a replace was done. <c>BackupPath</c> is the file ReplaceFileW left (the original's bytes) when the fallback ran; the caller deletes it after the commit. Null otherwise.</summary>
public sealed record ReplaceOutcome(ReplaceMechanism Mechanism, string FinalPath, int Attempts, string? BackupPath);

/// <summary>The two system calls a replace is made of, behind an interface so that a test can make the first fail the way SMB does (error 87) or die between a call's halves.</summary>
public interface IReplaceNative
{
    /// <summary>Renames the file behind <paramref name="temp"/> over <paramref name="target"/>. Returns 0 or the Win32 error.</summary>
    int PosixRename(SafeFileHandle temp, string target);

    /// <summary><c>ReplaceFileW(target, replacement, backup)</c>. Returns 0 or the Win32 error.</summary>
    int ReplaceFile(string target, string replacement, string? backup);
}

internal sealed class WindowsReplaceNative : IReplaceNative
{
    public unsafe int PosixRename(SafeFileHandle temp, string target)
    {
        var name = FileReplacer.LongPath(target);
        var size = 20 + (name.Length * 2) + 2;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            // FILE_RENAME_INFO: Flags (4), padding (4), RootDirectory (8), FileNameLength (4), FileName at offset 20.
            *(uint*)p = SaveNative.RenameReplaceIfExists | SaveNative.RenamePosixSemantics;
            *(uint*)(p + 16) = (uint)(name.Length * 2);
            name.AsSpan().CopyTo(new Span<char>(p + 20, name.Length));
            return SaveNative.SetFileInformationByHandle(temp, SaveNative.FileRenameInfoEx, p, (uint)size) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int ReplaceFile(string target, string replacement, string? backup) =>
        SaveNative.ReplaceFile(FileReplacer.LongPath(target), FileReplacer.LongPath(replacement), backup is null ? null : FileReplacer.LongPath(backup), SaveNative.ReplaceFileIgnoreMergeErrors, 0, 0)
            ? 0
            : Marshal.GetLastPInvokeError();
}

/// <summary>
/// Stage 6 of a save (save-pipeline 3.6, ADR-0008 amendment A1, S10): replaces a document with its validated temporary file. The document is open
/// in the broker and mapped by the engines, with <c>FileShare.Read | FileShare.Delete</c>, and stays so: the replace is a POSIX-semantics rename of
/// the temporary file over it, after the original's security descriptor, alternate data streams, attributes and creation time have been copied onto
/// the temporary file and its Hidden attribute cleared. Where that rename is not supported it falls back to <c>ReplaceFileW</c> with a backup name the
/// journal already records.
/// </summary>
public sealed class FileReplacer
{
    private readonly IReplaceNative _native;
    private readonly int _attempts;
    private readonly TimeSpan _delay;

    public FileReplacer(int attempts = 5, TimeSpan? delay = null)
        : this(new WindowsReplaceNative(), attempts, delay)
    {
    }

    public FileReplacer(IReplaceNative native, int attempts = 5, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _attempts = Math.Max(1, attempts);
        _delay = delay ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>Adds the extended-length prefix to a path that is long enough to need it.</summary>
    public static string LongPath(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>What a save has to know about the target before it starts (stage 0): where it really is, whether it can be replaced, and which file it is.</summary>
    public static TargetInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var handle = SaveNative.CreateFile(LongPath(path), SaveNative.FileReadAttributes, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, SaveNative.BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is SaveNative.ErrorFileNotFound or 3)
            {
                return new TargetInfo(path, Path.GetFullPath(path), false, TargetProblem.None, null, 0, false);
            }

            throw new Win32Exception(error, "The file could not be examined.");
        }

        var final = FinalPath(handle) ?? Path.GetFullPath(path);
        var attributes = File.GetAttributes(final);
        var links = LinkCount(handle);
        var problem = ProblemFor(attributes, links);
        const FileAttributes recall = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;
        return new TargetInfo(path, final, true, problem, IdentityOf(handle), links, (attributes & recall) != 0);
    }

    /// <summary>Whether a file with these attributes and this many names can be replaced. Read-only comes first, then EFS (decided on the attribute alone), then hard links.</summary>
    internal static TargetProblem ProblemFor(FileAttributes attributes, uint links) =>
        (attributes & FileAttributes.ReadOnly) != 0 ? TargetProblem.ReadOnly
        : (attributes & FileAttributes.Encrypted) != 0 ? TargetProblem.Encrypted
        : links > 1 ? TargetProblem.HardLinks
        : TargetProblem.None;

    public static unsafe FileIdentity? IdentityOf(SafeFileHandle handle)
    {
        var buffer = stackalloc byte[24];
        if (!SaveNative.GetFileInformationByHandleEx(handle, SaveNative.FileIdInfo, buffer, 24))
        {
            return null;
        }

        return new FileIdentity(*(ulong*)buffer, Convert.ToHexStringLower(new ReadOnlySpan<byte>(buffer + 8, 16)));
    }

    public static FileIdentity? IdentityOfPath(string path)
    {
        using var handle = SaveNative.CreateFile(LongPath(path), SaveNative.FileReadAttributes, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, SaveNative.BackupSemantics, 0);
        return handle.IsInvalid ? null : IdentityOf(handle);
    }

    private static unsafe uint LinkCount(SafeFileHandle handle)
    {
        var buffer = stackalloc byte[24];
        return SaveNative.GetFileInformationByHandleEx(handle, SaveNative.FileStandardInfo, buffer, 24) ? ((SaveNative.StandardInfo*)buffer)->NumberOfLinks : 1;
    }

    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = stackalloc char[1024];
        var length = SaveNative.GetFinalPathNameByHandle(handle, buffer, 1024, 0);
        if (length == 0 || length >= 1024)
        {
            return null;
        }

        var text = new string(buffer, 0, (int)length);
        return text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + text[8..]
            : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..]
            : text;
    }

    /// <summary>
    /// Creates the temporary file of a save: <c>.{name}.mikepdf-{8 hex}.tmp</c> in the target's directory, exclusive, hidden, readable and writable,
    /// with delete access so the same handle can be renamed. The caller owns the handle.
    /// </summary>
    public static SafeFileHandle CreateTemporary(string finalTarget, string name, out string path)
    {
        var directory = Path.GetDirectoryName(finalTarget) ?? throw new ArgumentException("The target has no directory.", nameof(finalTarget));
        path = Path.Combine(directory, name);
        const uint createNew = 1;
        const uint hiddenAttribute = 2;
        var handle = SaveNative.CreateFile(LongPath(path), SaveNative.GenericRead | SaveNative.GenericWrite | SaveNative.Delete, 1 | 4, 0, createNew, hiddenAttribute, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "The temporary file could not be created.");
        }

        return handle;
    }

    /// <summary>
    /// Closes the write handle of a finished temporary file and opens it again for reading and renaming only (delete access, no write), so that other
    /// processes, a fresh engine among them, can open it with the sharing every document gets. Fails if the path now names another file.
    /// </summary>
    public static SafeFileHandle ReopenForRename(SafeFileHandle writeHandle, string path)
    {
        ArgumentNullException.ThrowIfNull(writeHandle);
        var identity = IdentityOf(writeHandle);
        SaveNative.FlushFileBuffers(writeHandle);
        writeHandle.Dispose();
        var reopened = SaveNative.CreateFile(LongPath(path), SaveNative.GenericRead | SaveNative.Delete, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, 0, 0);
        if (reopened.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            reopened.Dispose();
            throw new ReplaceException(ReplaceFailure.Native, error, $"The temporary file could not be reopened (Win32 error {error}).");
        }

        if (identity is not null && IdentityOf(reopened) != identity)
        {
            reopened.Dispose();
            throw new ReplaceException(ReplaceFailure.Native, 0, "The temporary file was replaced while it was closed.");
        }

        return reopened;
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with the file behind <paramref name="temp"/>. Takes ownership of the handle and closes it. The handle needs delete
    /// access (a read-only one from <see cref="ReopenForRename"/> is what the save pipeline passes).
    /// </summary>
    /// <remarks>
    /// <paramref name="expected"/> is the identity the document was opened with; when given, a path that names another file is refused.
    /// <paramref name="backup"/> is where the fallback may leave the original's bytes; it was recorded in the journal before the temporary file was created.
    /// </remarks>
    public ReplaceOutcome Replace(string target, SafeFileHandle temp, string tempPath, FileIdentity? expected, string? backup)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(temp);
        using (temp)
        {
            var info = Inspect(target);
            if (info.Exists)
            {
                if (expected is { } wanted && info.Identity != wanted)
                {
                    throw new ReplaceException(ReplaceFailure.ChangedOnDisk, 0, "The file was replaced by another file since it was opened.");
                }
            }
            else if (expected is not null)
            {
                throw new ReplaceException(ReplaceFailure.TargetGone, SaveNative.ErrorFileNotFound, "The file was moved or deleted since it was opened.");
            }

            PrepareTemporary(info.Exists ? info.FinalPath : null, tempPath);
            if (!SaveNative.FlushFileBuffers(temp) && Marshal.GetLastPInvokeError() != SaveNative.ErrorAccessDenied)
            {
                // A handle without write access cannot flush; whoever wrote the file did (the host flushes to disk, and so does ReopenForRename).
                throw new ReplaceException(ReplaceFailure.Native, Marshal.GetLastPInvokeError(), "The temporary file could not be flushed.");
            }

            var final = info.FinalPath;
            var attempts = 0;
            var mechanism = ReplaceMechanism.PosixRename;
            while (true)
            {
                attempts++;
                int error;
                switch (mechanism)
                {
                    case ReplaceMechanism.PosixRename:
                        error = _native.PosixRename(temp, final);
                        break;
                    case ReplaceMechanism.ReplaceFile:
                        temp.Dispose(); // ReplaceFileW needs the replacement closed
                        error = _native.ReplaceFile(final, tempPath, backup);
                        break;
                    default:
                        temp.Dispose();
                        error = MoveNew(tempPath, final);
                        break;
                }

                if (error == 0)
                {
                    return new ReplaceOutcome(mechanism, final, attempts, mechanism == ReplaceMechanism.ReplaceFile ? backup : null);
                }

                if (mechanism == ReplaceMechanism.PosixRename && IsUnsupported(error))
                {
                    // Not supported here (SMB, FAT, WebDAV): no retry, another mechanism.
                    mechanism = info.Exists ? ReplaceMechanism.ReplaceFile : ReplaceMechanism.Move;
                    continue;
                }

                if (IsTransient(error) && attempts < _attempts)
                {
                    Thread.Sleep(_delay);
                    continue;
                }

                throw new ReplaceException(IsTransient(error) ? ReplaceFailure.InUse : ReplaceFailure.Native, error, "The file could not be replaced.");
            }
        }
    }

    private static bool IsUnsupported(int error) => error is SaveNative.ErrorInvalidParameter or SaveNative.ErrorNotSupported or SaveNative.ErrorInvalidFunction;

    /// <summary>Sharing violation, lock violation, access denied (OneDrive and scanners produce a transient one) and "unable to remove replaced" (seen once in S10).</summary>
    private static bool IsTransient(int error) => error is SaveNative.ErrorSharingViolation or SaveNative.ErrorLockViolation or SaveNative.ErrorAccessDenied or SaveNative.ErrorUnableToRemoveReplaced;

    private static int MoveNew(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return 0;
        }
        catch (IOException e)
        {
            return e.HResult & 0xFFFF;
        }
        catch (UnauthorizedAccessException)
        {
            return SaveNative.ErrorAccessDenied;
        }
    }

    /// <summary>
    /// Makes the temporary file carry what the user's file has (S10 section 5): the security descriptor, every alternate data stream (Zone.Identifier
    /// first), the attributes that belong to the file rather than to the temporary one, the compression state and the creation time. Hidden, which the
    /// temporary file was created with, is cleared unless the original has it: a rename moves the temporary file's attributes onto the user's file.
    /// </summary>
    internal static void PrepareTemporary(string? original, string tempPath)
    {
        if (original is null)
        {
            File.SetAttributes(tempPath, FileAttributes.Normal);
            return;
        }

        CopyStreams(original, tempPath);

        // A fresh descriptor loaded from the binary form: .NET writes only the sections marked as modified, so GetAccessControl then SetAccessControl copies nothing.
        var descriptor = new FileInfo(original).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        new FileInfo(tempPath).SetAccessControl(security);

        const FileAttributes kept = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed | FileAttributes.Temporary;
        var attributes = File.GetAttributes(original);
        var carried = attributes & kept;
        File.SetAttributes(tempPath, carried == 0 ? FileAttributes.Normal : carried);
        if ((attributes & FileAttributes.Compressed) != 0)
        {
            // Its own handle: the one the pipeline holds has no write access.
            using var writable = SaveNative.CreateFile(LongPath(tempPath), SaveNative.GenericRead | SaveNative.GenericWrite, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, 0, 0);
            if (!writable.IsInvalid)
            {
                SetCompression(writable);
            }
        }

        File.SetCreationTimeUtc(tempPath, File.GetCreationTimeUtc(original));
    }

    private static unsafe void SetCompression(SafeFileHandle file)
    {
        ushort format = 1; // COMPRESSION_FORMAT_DEFAULT
        SaveNative.DeviceIoControl(file, SaveNative.FsctlSetCompression, (byte*)&format, sizeof(ushort), null, 0, out _, 0);
    }

    /// <summary>Copies every named stream of <paramref name="from"/> onto <paramref name="to"/>, the default data stream excepted.</summary>
    private static unsafe void CopyStreams(string from, string to)
    {
        const int dataBytes = 8 + (296 * 2);
        var data = stackalloc byte[dataBytes];
        var find = SaveNative.FindFirstStream(LongPath(from), 0, data, 0);
        if (find == -1)
        {
            // No streams on this volume (ERROR_HANDLE_EOF), or the volume has none (FAT, some shares): nothing to copy.
            return;
        }

        var names = new List<string>();
        try
        {
            do
            {
                var name = new string((char*)(data + 8));
                if (name != "::$DATA")
                {
                    names.Add(name.EndsWith(":$DATA", StringComparison.Ordinal) ? name[..^6] : name);
                }
            }
            while (SaveNative.FindNextStream(find, data));
        }
        finally
        {
            SaveNative.FindClose(find);
        }

        // Zone.Identifier (the Mark of the Web) first: it is the one that matters if anything after it fails.
        foreach (var name in names.OrderBy(static n => n.Equals(":Zone.Identifier", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            using var source = SaveNative.CreateFile(LongPath(from + name), SaveNative.GenericRead, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, 0, 0);
            if (source.IsInvalid)
            {
                continue;
            }

            using var destination = SaveNative.CreateFile(LongPath(to + name), SaveNative.GenericWrite, SaveNative.FileShareAll, 0, 2 /* CREATE_ALWAYS */, 0, 0); // share-delete matters: the temporary file's own handle holds delete access
            if (destination.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new ReplaceException(ReplaceFailure.Native, error, $"A stream of the original could not be copied (Win32 error {error}).");
            }

            using var reader = new FileStream(source, FileAccess.Read, 1);
            using var writer = new FileStream(destination, FileAccess.Write, 1);
            reader.CopyTo(writer);
        }
    }
}
