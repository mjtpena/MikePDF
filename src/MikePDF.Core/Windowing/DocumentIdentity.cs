using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MikePDF.Core.Windowing;

/// <summary>Where the stable identity of a file on disk comes from. A seam so the tab model can be tested without a disk.</summary>
public interface IFileIdSource
{
    /// <summary>The volume serial number and 128-bit file ID of the file, as one string, or null when the file cannot be asked (missing, a share that has no file IDs).</summary>
    string? TryGetFileId(string fullPath);
}

/// <summary>
/// Which document a path names (docs/features/tabs-windows-session.md 3.1): the <b>file ID</b> (volume serial number plus 128-bit file ID from
/// <c>GetFileInformationByHandleEx(FileIdInfo)</c>), so that <c>C:\a\x.pdf</c>, <c>C:\A\X.PDF</c>, an 8.3 name, a mapped drive versus a UNC path and a symbolic link are one document. When the
/// file ID is not available the normalised full path (upper-cased, no <c>\\?\</c> prefix) stands in. Two files with the same name in different folders are never the same.
/// <para>
/// Resolving an identity opens the path (attributes only). Call it only for a path that has passed <see cref="ActivationPathValidator"/>: opening a device path such as
/// <c>\\.\pipe\x</c> can block for ever.
/// </para>
/// </summary>
public readonly record struct DocumentIdentity(string Key)
{
    /// <summary>The identity of the file at <paramref name="path"/> (a full or relative path; it is made full first).</summary>
    public static DocumentIdentity Of(string path, IFileIdSource? source = null)
    {
        var full = StripExtendedPrefix(System.IO.Path.GetFullPath(path));
        var id = (source ?? NativeFileIdSource.Instance).TryGetFileId(full);
        return new DocumentIdentity(id is not null ? "id:" + id : "path:" + full.ToUpperInvariant());
    }

    /// <summary>Whether this is the identity of an unopened or unknown document (the empty tab).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Key);

    internal static string StripExtendedPrefix(string full)
    {
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + full[8..];
        }

        return full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Length > 6 && full[5] == ':' ? full[4..] : full;
    }
}

/// <summary>The Windows file ID: <c>CreateFileW</c> for attributes only (no read access, every sharing mode, so a file open elsewhere or locked still answers), then <c>FileIdInfo</c>.</summary>
public sealed class NativeFileIdSource : IFileIdSource
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;

    public static NativeFileIdSource Instance { get; } = new();

    public string? TryGetFileId(string fullPath)
    {
        try
        {
            using var handle = CreateFileW(ToExtended(DocumentIdentity.StripExtendedPrefix(fullPath)), FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                return null;
            }

            return info.VolumeSerialNumber.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(info.FileId);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // The extended-length form, so a long path (and a name with a trailing space or dot) is taken literally.
    private static string ToExtended(string full) => full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo info, uint size);
}
