using System.ComponentModel;
using System.Runtime.InteropServices;

namespace S12;

/// <summary>Starts a probe in an EXISTING zero-capability AppContainer profile (derives its SID by name; creates nothing) and waits for it.</summary>
internal static unsafe class AppContainerLaunch
{
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_CAPABILITIES { public void* AppContainerSid; public void* Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public nint lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeriveAppContainerSidFromAppContainerName(string name, out void* sid);
    [DllImport("advapi32.dll")] static extern void FreeSid(void* sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool ConvertSidToStringSidW(void* sid, out nint str);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(nint list, int flags, nuint attr, void* value, nuint size, void* prev, void* ret);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string? app, string cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string? cwd, ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern int WaitForSingleObject(nint h, int ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint h, out int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint h);

    public static string SidOf(string profile)
    {
        var hr = DeriveAppContainerSidFromAppContainerName(profile, out var sid); if (hr != 0) throw new Win32Exception(hr, "Derive SID");
        ConvertSidToStringSidW(sid, out var s); var str = Marshal.PtrToStringUni(s)!; LocalFree(s); FreeSid(sid); return str;
    }

    // PROCESS_CREATION_MITIGATION_POLICY_* (winbase.h). The set below is SandboxPolicy.CreationMitigations1 / 2 from src/MikePDF.Broker, minus PROHIBIT_DYNAMIC_CODE
    // (the .NET probe needs the JIT; the engine, being C++, sets it).
    const ulong DEP = 0x1, FORCE_RELOCATE = 1UL << 8, BOTTOM_UP = 1UL << 16, HIGH_ENTROPY = 1UL << 20, STRICT_HANDLE = 1UL << 24, EXT_POINT = 1UL << 32, CFG = 1UL << 40, FONT_DISABLE = 1UL << 48,
        IMG_NO_REMOTE = 1UL << 52, IMG_NO_LOW_LABEL = 1UL << 56, IMG_PREFER_SYS32 = 1UL << 60;
    const ulong CET2 = 1UL << 28, DYNCODE = 1UL << 36;

    /// <param name="hardened">Adds the engine's creation-time mitigation policies and the restricted child-process policy (ADR-0004 decision 2).</param>
    public static readonly (string name, ulong v1, ulong v2)[] Flags =
    [
        ("DEP", DEP, 0), ("FORCE_RELOCATE", FORCE_RELOCATE, 0), ("BOTTOM_UP_ASLR", BOTTOM_UP, 0), ("HIGH_ENTROPY_ASLR", HIGH_ENTROPY, 0), ("STRICT_HANDLE_CHECKS", STRICT_HANDLE, 0),
        ("EXTENSION_POINT_DISABLE", EXT_POINT, 0), ("CONTROL_FLOW_GUARD", CFG, 0), ("FONT_DISABLE", FONT_DISABLE, 0), ("IMAGE_LOAD_NO_REMOTE", IMG_NO_REMOTE, 0),
        ("IMAGE_LOAD_NO_LOW_LABEL", IMG_NO_LOW_LABEL, 0), ("IMAGE_LOAD_PREFER_SYSTEM32", IMG_PREFER_SYS32, 0), ("PROHIBIT_DYNAMIC_CODE", DYNCODE, 0), ("CET_USER_SHADOW_STACKS", 0, CET2),
    ];

    public static int RunMasks(string profile, string exe, string args, string cwd, int timeoutMs, ulong m1, ulong m2, bool childRestricted, out string note)
    {
        var hr = DeriveAppContainerSidFromAppContainerName(profile, out var sid);
        if (hr != 0) { note = "no profile"; return -1; }
        var sc = new SECURITY_CAPABILITIES { AppContainerSid = sid };
        var count = 3; nuint size = 0; InitializeProcThreadAttributeList(0, count, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size); var mit = stackalloc ulong[2]; mit[0] = m1; mit[1] = m2; uint child = childRestricted ? 1u : 0u;
        try
        {
            InitializeProcThreadAttributeList(list, count, 0, ref size);
            UpdateProcThreadAttribute(list, 0, 0x00020009, &sc, (nuint)sizeof(SECURITY_CAPABILITIES), null, null);
            UpdateProcThreadAttribute(list, 0, 0x00020007, mit, 16, null, null);
            UpdateProcThreadAttribute(list, 0, 0x0002000E, &child, 4, null, null);
            var si = new STARTUPINFOEXW { lpAttributeList = list }; si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            if (!CreateProcessW(null, $"\"{exe}\" {args}", 0, 0, false, 0x00080000 | 0x00000008, 0, cwd, ref si, out var pi)) { note = "CreateProcess failed win32=" + Marshal.GetLastWin32Error(); return -2; }
            var w = WaitForSingleObject(pi.hProcess, timeoutMs); GetExitCodeProcess(pi.hProcess, out var code); CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
            note = w == 0 ? $"exit 0x{code:X8}" : "timeout"; return w == 0 ? code : -3;
        }
        finally { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); FreeSid(sid); }
    }

    public static int Run(string profile, string exe, string args, string cwd, int timeoutMs, bool hardened, bool prohibitDynamicCode, out string note)
    {
        var hr = DeriveAppContainerSidFromAppContainerName(profile, out var sid);
        if (hr != 0) { note = $"profile '{profile}' not found (0x{hr:X8}); nothing was created"; return -1; }
        var sc = new SECURITY_CAPABILITIES { AppContainerSid = sid, Capabilities = null, CapabilityCount = 0 };   // zero capabilities
        var count = hardened ? 3 : 1;
        nuint size = 0; InitializeProcThreadAttributeList(0, count, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var mit = stackalloc ulong[2]; mit[0] = DEP | FORCE_RELOCATE | BOTTOM_UP | HIGH_ENTROPY | STRICT_HANDLE | EXT_POINT | CFG | FONT_DISABLE | IMG_NO_REMOTE | IMG_NO_LOW_LABEL | IMG_PREFER_SYS32 | (prohibitDynamicCode ? DYNCODE : 0UL); mit[1] = CET2;
        uint child = 1;   // PROCESS_CREATION_CHILD_PROCESS_RESTRICTED
        try
        {
            if (!InitializeProcThreadAttributeList(list, count, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(list, 0, 0x00020009 /*SECURITY_CAPABILITIES*/, &sc, (nuint)sizeof(SECURITY_CAPABILITIES), null, null)) throw new Win32Exception();
            if (hardened)
            {
                if (!UpdateProcThreadAttribute(list, 0, 0x00020007 /*MITIGATION_POLICY*/, mit, 16, null, null)) throw new Win32Exception();
                if (!UpdateProcThreadAttribute(list, 0, 0x0002000E /*CHILD_PROCESS_POLICY*/, &child, 4, null, null)) throw new Win32Exception();
            }
            var si = new STARTUPINFOEXW { lpAttributeList = list }; si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            if (!CreateProcessW(null, $"\"{exe}\" {args}", 0, 0, false, 0x00080000 /*EXTENDED_STARTUPINFO_PRESENT*/ | 0x00000008 /*DETACHED_PROCESS, as the engine is launched*/, 0, cwd, ref si, out var pi)) { note = "CreateProcess failed win32=" + Marshal.GetLastWin32Error(); return -2; }
            var w = WaitForSingleObject(pi.hProcess, timeoutMs); GetExitCodeProcess(pi.hProcess, out var code);
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
            note = w == 0 ? $"exited with {code} (0x{code:X8})" : "timeout"; return w == 0 ? code : -3;
        }
        finally { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); FreeSid(sid); }
    }
}
