using System.Runtime.InteropServices;

namespace MikePDF.Integration.Tests.Support;

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
    /// How many <c>MikePDF.Engine.exe</c> processes are running right now whose image is in <paramref name="directory"/> (this test run's deployment, so another run on the
    /// machine does not count). A process that has exited but is still held open by a handle somewhere is not running.
    /// </summary>
    public static int RunningEnginesIn(string directory)
    {
        const uint QueryLimitedInformation = 0x1000;
        var running = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("MikePDF.Engine"))
        {
            using (process)
            {
                var handle = OpenProcess(QueryLimitedInformation, false, (uint)process.Id);
                if (handle == 0)
                {
                    continue;
                }

                try
                {
                    var path = new char[1024];
                    var length = (uint)path.Length;
                    if (QueryFullProcessImageNameW(handle, 0, path, ref length)
                        && new string(path, 0, (int)length).StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                        && GetExitCodeProcess(handle, out var code)
                        && code == 259)
                    {
                        running++;
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        return running;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] path, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    /// <summary>
    /// Freezes every thread of the process except the one that was created first, which is the engine's I/O thread (it reads the pipe and answers Ping on its own, ADR-0005
    /// rule 2): what is left is an engine whose work thread is stuck and whose heartbeat still beats, the case the progress counter exists for. Returns how many were frozen.
    /// </summary>
    public static int SuspendAllThreadsButTheOldest(uint processId)
    {
        const uint SnapshotThread = 0x4;
        const uint ThreadSuspendResume = 0x2;
        const uint ThreadQueryLimitedInformation = 0x800;
        var snapshot = CreateToolhelp32Snapshot(SnapshotThread, 0);
        if (snapshot == -1)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "CreateToolhelp32Snapshot");
        }

        var threads = new List<(nint Handle, long Created)>();
        try
        {
            var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
            for (var ok = Thread32First(snapshot, ref entry); ok; ok = Thread32Next(snapshot, ref entry))
            {
                if (entry.OwnerProcessId != processId)
                {
                    continue;
                }

                var thread = OpenThread(ThreadSuspendResume | ThreadQueryLimitedInformation, false, entry.ThreadId);
                if (thread == 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"OpenThread({entry.ThreadId})");
                }

                GetThreadTimes(thread, out var created, out _, out _, out _);
                threads.Add((thread, created));
            }

            var oldest = threads.MinBy(t => t.Created);
            var frozen = 0;
            foreach (var (handle, _) in threads.Where(t => t.Handle != oldest.Handle))
            {
                if (SuspendThread(handle) == uint.MaxValue)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "SuspendThread");
                }

                frozen++;
            }

            return frozen;
        }
        finally
        {
            foreach (var (handle, _) in threads)
            {
                CloseHandle(handle);
            }

            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32First(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32Next(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SuspendThread(nint thread);

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
