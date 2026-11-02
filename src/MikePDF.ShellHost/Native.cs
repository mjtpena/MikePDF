using System.Runtime.InteropServices;

namespace MikePDF.ShellHost;

/// <summary>The few Win32 calls ShellHost makes itself: its pipe, and taking a section handle from the process that is on the other end of it.</summary>
internal static unsafe partial class Native
{
    public const uint ProcessDupHandle = 0x0040;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;
    public const int ErrorInvalidParameter = 87;
    public const uint DuplicateSameAccess = 2;
    public const uint FileMapRead = 4;
    public const uint FileMapWrite = 2;
    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void* MapViewOfFile(nint section, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* address);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, SecurityAttributes* attributes);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptor(string sddl, uint revision, out nint descriptor, nint size);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFamilyName(ref uint length, char* name);

    [LibraryImport("kernel32.dll")]
    private static partial int GetPackageFamilyName(nint process, ref uint length, char* name);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>The package family of this process, or null when it has no package identity.</summary>
    public static string? CurrentPackageFamily()
    {
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            return GetCurrentPackageFamilyName(ref length, p) == 0 ? new string(p, 0, (int)length - 1) : null;
        }
    }

    /// <summary>The package family of another process (opened with at least query access), or null when it has none or it cannot be read.</summary>
    public static string? PackageFamilyOf(nint process)
    {
        uint length = 0;
        if (GetPackageFamilyName(process, ref length, null) != ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            return GetPackageFamilyName(process, ref length, p) == 0 ? new string(p, 0, (int)length - 1) : null;
        }
    }

    public static string? ImagePathOf(nint process)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            return QueryFullProcessImageName(process, 0, p, ref size) ? new string(p, 0, (int)size) : null;
        }
    }
}
