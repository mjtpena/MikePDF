using System.Runtime.InteropServices;

namespace Quire.Integration.Tests.Support;

/// <summary>The few Win32 calls the tests need that the Broker (rightly) does not expose.</summary>
internal static partial class NativeTestMethods
{
    private const uint FileMapWrite = 0x2;
    private const uint FileMapRead = 0x4;

    /// <summary>Maps a section with write access, as the engine does. Returns the base address.</summary>
    public static nint MapWritable(nint section)
    {
        var view = MapViewOfFile(section, FileMapRead | FileMapWrite, 0, 0, 0);
        if (view == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "MapViewOfFile");
        }

        return view;
    }

    public static void Unmap(nint view) => UnmapViewOfFile(view);

    public static void Close(nint handle) => CloseHandle(handle);

    /// <summary>Freezes every thread of the process, as a hung engine would look from outside.</summary>
    public static void Suspend(nint process)
    {
        var status = NtSuspendProcess(process);
        if (status < 0)
        {
            throw new InvalidOperationException("NtSuspendProcess failed: 0x" + status.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Every handle value in this process whose inherit flag is set. The handle table is dense (values are multiples of 4), so probing each
    /// value up to a bound well past the current handle count finds them all; GetHandleInformation on a value that is not a handle just fails.
    /// </summary>
    public static HashSet<nint> InheritableHandles()
    {
        var found = new HashSet<nint>();
        var bound = 4 * (System.Diagnostics.Process.GetCurrentProcess().HandleCount + 4096);
        for (nint handle = 4; handle < bound; handle += 4)
        {
            if (GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0)
            {
                found.Add(handle);
            }
        }

        return found;
    }

    /// <summary>True if the handle is valid and its inherit flag is set.</summary>
    public static bool IsInheritable(nint handle) => GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0;

    /// <summary>True if the value is a live handle of this process.</summary>
    public static bool IsLive(nint handle) => GetHandleInformation(handle, out _);

    /// <summary>Makes a handle of this process inheritable (or not): a positive control for the scan.</summary>
    public static void SetInheritable(nint handle, bool inheritable)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, inheritable ? HandleFlagInherit : 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation");
        }
    }

    private const uint HandleFlagInherit = 1;

    /// <summary>GetFileType: 1 disk, 2 character device, 3 pipe, 0 other.</summary>
    public static uint FileTypeOf(nint handle) => GetFileType(handle);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFileType(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MapViewOfFile(nint section, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint view);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(nint process);
}
