using System.Runtime.InteropServices;

namespace MikePDF.UI.Tests.Pages;

/// <summary>
/// Which processes belong to which: a Toolhelp snapshot of every process and its parent. The journeys use it to find (and, for the engine-kill
/// journey, to kill) the engines of the MikePDF under test, and nothing else: another MikePDF.Engine on the machine is somebody else's (another
/// agent's integration test, the render tool, a person's own reader) and is never touched.
/// </summary>
internal static partial class ProcessTree
{
    private const uint SnapProcess = 0x00000002;
    private static readonly nint InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    // The entry holds a string, which source-generated marshalling does not handle: these two are classic imports.
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>Every process that is a child, grandchild and so on of <paramref name="rootProcessId"/> right now, with its executable name.</summary>
    public static IReadOnlyList<(int Id, string Exe)> DescendantsOf(int rootProcessId)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return [];
        }

        var parentOf = new Dictionary<int, int>();
        var exeOf = new Dictionary<int, string>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = string.Empty };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                parentOf[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                exeOf[(int)entry.ProcessId] = entry.ExeFile;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        // A parent id can be reused by an unrelated process after the parent exits, so a chain is only followed upwards through processes that
        // still exist and never loops; a descendant is one whose chain reaches the root.
        var result = new List<(int, string)>();
        foreach (var (id, _) in parentOf)
        {
            var at = id;
            for (var hops = 0; hops < 64 && parentOf.TryGetValue(at, out var parent); hops++)
            {
                if (parent == rootProcessId && id != rootProcessId)
                {
                    result.Add((id, exeOf[id]));
                    break;
                }

                if (parent == at || parent == 0)
                {
                    break;
                }

                at = parent;
            }
        }

        return result;
    }
}
