using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MikePDF.Broker.Saving;

/// <summary>
/// The few Win32 calls the replace step needs, declared by hand because they are not in the broker's CsWin32 set and one of them
/// (<c>FileRenameInfoEx</c>) takes a variable-length structure. Layouts are those of the Windows SDK for 64-bit targets (x64 and ARM64), the only
/// ones MikePDF builds.
/// </summary>
internal static unsafe partial class SaveNative
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint Delete = 0x00010000;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileShareAll = 7;
    internal const uint OpenExisting = 3;
    internal const uint BackupSemantics = 0x02000000;

    internal const int FileStandardInfo = 1;
    internal const int FileIdInfo = 18;
    internal const int FileRenameInfoEx = 22;

    internal const uint RenameReplaceIfExists = 0x1;
    internal const uint RenamePosixSemantics = 0x2;

    internal const uint ReplaceFileIgnoreMergeErrors = 0x2;

    internal const uint FsctlGetCompression = 0x9003C;
    internal const uint FsctlSetCompression = 0x9C040;

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotSupported = 50;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorLockViolation = 33;
    internal const int ErrorInvalidFunction = 1;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorUnableToRemoveReplaced = 1175;
    internal const int ErrorHandleEof = 38;
    internal const int ErrorNoMoreFiles = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, byte* information, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "ReplaceFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReplaceFile(string replaced, string replacement, string? backup, uint flags, nint exclude, nint reserved);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstStreamW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindFirstStream(string fileName, int infoLevel, byte* data, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextStreamW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextStream(nint find, byte* data);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(nint find);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte* input, uint inputSize, byte* output, uint outputSize, out uint returned, nint overlapped);

    /// <summary>The structure <c>FILE_ID_INFO</c>: the volume serial number and the 128-bit file ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileId
    {
        public ulong VolumeSerialNumber;
        public fixed byte Identifier[16];
    }

    /// <summary>The structure <c>FILE_STANDARD_INFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }
}
