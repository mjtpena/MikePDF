using System.Runtime.InteropServices;

namespace MikePDF.WriterHost;

/// <summary>The few Win32 calls the host needs. Everything else is managed.</summary>
internal static partial class Native
{
    private const int ProcessSignaturePolicy = 8;

    /// <summary>Microsoft-signed-only for images loaded from now on. Returns 0 or the Win32 error.</summary>
    public static int ApplyMicrosoftSignedOnly()
    {
        uint flags = 1; // MicrosoftSignedOnly
        return SetProcessMitigationPolicy(ProcessSignaturePolicy, ref flags, sizeof(uint)) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>The first DWORD of the signature policy as the OS reports it, or null.</summary>
    public static uint? ReadSignaturePolicy()
    {
        uint flags = 0;
        return GetProcessMitigationPolicy(GetCurrentProcess(), ProcessSignaturePolicy, ref flags, sizeof(uint)) ? flags : null;
    }

    public static uint ServerProcessId(nint pipe) => GetNamedPipeServerProcessId(pipe, out var pid) ? pid : 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(nint process, int policy, ref uint buffer, nuint length);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);
}
