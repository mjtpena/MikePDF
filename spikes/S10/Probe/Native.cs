// S10 spike: the Win32 surface used to test replace semantics. Signatures checked against the Windows SDK headers (fileapi.h, winbase.h, memoryapi.h).
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static unsafe class N
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint CREATE_NEW = 1, CREATE_ALWAYS = 2, OPEN_EXISTING = 3, OPEN_ALWAYS = 4;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_HIDDEN = 2, FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint MOVEFILE_REPLACE_EXISTING = 1, MOVEFILE_COPY_ALLOWED = 2, MOVEFILE_WRITE_THROUGH = 8;
    public const uint REPLACEFILE_WRITE_THROUGH = 1, REPLACEFILE_IGNORE_MERGE_ERRORS = 2, REPLACEFILE_IGNORE_ACL_ERRORS = 4;
    public const uint PAGE_READONLY = 2, FILE_MAP_READ = 4;
    public const int FileRenameInfo = 3, FileDispositionInfoEx = 21, FileRenameInfoEx = 22, FileIdInfo = 18;
    public const uint FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 1, FILE_RENAME_FLAG_POSIX_SEMANTICS = 2, FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x40;
    public const uint FILE_DISPOSITION_FLAG_DELETE = 1, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 2, FILE_DISPOSITION_FLAG_FORCE_IMAGE_SECTION_CHECK = 4;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ReplaceFileW(string replaced, string replacement, string? backup, uint flags, IntPtr exclude, IntPtr reserved);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool MoveFileExW(string existing, string target, uint flags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool DeleteFileW(string name);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateHardLinkW(string newName, string existing, IntPtr sa);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, void* info, uint size);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* info, uint size);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle h);

    [DllImport("kernel32", SetLastError = true)]
    public static extern IntPtr CreateFileMappingW(SafeFileHandle file, IntPtr sa, uint protect, uint hi, uint lo, IntPtr name);

    [DllImport("kernel32", SetLastError = true)]
    public static extern void* MapViewOfFile(IntPtr mapping, uint access, uint hi, uint lo, nuint bytes);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool UnmapViewOfFile(void* view);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);

    [DllImport("kernel32")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool SetFileTime(SafeFileHandle h, long* creation, long* access, long* write);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstStreamW(string file, int level, out WIN32_FIND_STREAM_DATA data, uint flags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool FindNextStreamW(IntPtr h, out WIN32_FIND_STREAM_DATA data);

    [DllImport("kernel32")]
    public static extern bool FindClose(IntPtr h);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFileAttributesW(string name);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetFileAttributesW(string name, uint attrs);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }

    public static string Err(int code) => $"{code} ({new Win32Exception(code).Message.Trim()})";

    /// <summary>Open a path with raw CreateFileW flags; returns the handle and the Win32 error (0 on success).</summary>
    public static (SafeFileHandle? Handle, int Error) Open(string path, uint access, uint share, uint disposition = OPEN_EXISTING, uint flags = FILE_ATTRIBUTE_NORMAL)
    {
        var h = CreateFileW(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var e = Marshal.GetLastWin32Error();
            h.Dispose();
            return (null, e);
        }

        return (h, 0);
    }

    /// <summary>FILE_RENAME_INFO(Ex) rename of an open handle (which needs DELETE access) to a full path.</summary>
    public static int RenameByHandle(SafeFileHandle h, string newFullPath, uint flags, bool exClass)
    {
        var nameBytes = (newFullPath.Length) * 2;
        // x64 layout: union{Flags|ReplaceIfExists} (4) + pad (4) + RootDirectory (8) + FileNameLength (4) + FileName (offset 20)
        var size = 20 + nameBytes + 2;
        var buf = new byte[(size + 7) & ~7];
        fixed (byte* p = buf)
        {
            *(uint*)p = exClass ? flags : ((flags & FILE_RENAME_FLAG_REPLACE_IF_EXISTS) != 0 ? 1u : 0u);
            *(long*)(p + 8) = 0;
            *(uint*)(p + 16) = (uint)nameBytes;
            fixed (char* c = newFullPath)
            {
                Buffer.MemoryCopy(c, p + 20, nameBytes, nameBytes);
            }

            return SetFileInformationByHandle(h, exClass ? FileRenameInfoEx : FileRenameInfo, p, (uint)buf.Length) ? 0 : Marshal.GetLastWin32Error();
        }
    }

    public static int DisposeByHandle(SafeFileHandle h, uint flags)
    {
        return SetFileInformationByHandle(h, FileDispositionInfoEx, &flags, 4) ? 0 : Marshal.GetLastWin32Error();
    }
}

/// <summary>What the engine does with the handle it is given (engine_core: mapped_file.cpp): CreateFileMapping, MapViewOfFile, close the mapping and the file handle, keep the view.</summary>
internal sealed unsafe class EngineView : IDisposable
{
    private void* _view;
    public long Length { get; }

    /// <summary>Consumes <paramref name="given"/> (the engine's own duplicate of the broker's handle).</summary>
    public EngineView(SafeFileHandle given)
    {
        using var dupHandle = given;
        N.GetFileInformationByHandle(dupHandle, out var info);
        Length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var mapping = N.CreateFileMappingW(dupHandle, IntPtr.Zero, N.PAGE_READONLY, 0, 0, IntPtr.Zero);
        if (mapping == IntPtr.Zero) throw new Win32Exception();
        try
        {
            _view = N.MapViewOfFile(mapping, N.FILE_MAP_READ, 0, 0, 0);
            if (_view == null) throw new Win32Exception();
        }
        finally
        {
            N.CloseHandle(mapping);
        }
    }

    public static SafeFileHandle DuplicateInProcess(SafeFileHandle h)
    {
        if (!N.DuplicateHandle(N.GetCurrentProcess(), h.DangerousGetHandle(), N.GetCurrentProcess(), out var dup, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
            throw new Win32Exception();
        return new SafeFileHandle(dup, ownsHandle: true);
    }

    public byte[] ReadAll()
    {
        var b = new byte[Length];
        Marshal.Copy((IntPtr)_view, b, 0, (int)Length);
        return b;
    }

    public void Dispose()
    {
        if (_view != null)
        {
            N.UnmapViewOfFile(_view);
            _view = null;
        }
    }
}
