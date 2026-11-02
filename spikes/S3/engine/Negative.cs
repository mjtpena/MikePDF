using Windows.Win32.System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Quire.S3.Engine;

/// <summary>
/// Negative-access suite. Runs inside the engine process; every attempt reports the raw Win32/Winsock error.
/// Outcome: ALLOWED = the operation succeeded; DENIED = access-denied class error; FAILED = failed for some other reason (must be explained).
/// </summary>
internal static unsafe class NegativeSuite
{
    // 5 ACCESS_DENIED, 65 NETWORK_ACCESS_DENIED, 1314 PRIVILEGE_NOT_HELD, 577 INVALID_IMAGE_HASH, 1260 DISABLED_BY_POLICY, 10013 WSAEACCES, 10060 WSAETIMEDOUT (loopback SYN silently dropped in an AppContainer; baseline connects instantly)
    static readonly HashSet<int> DeniedCodes = [5, 65, 367, 1314, 577, 1260, 10013, 10060];

    static NegResult Mk(string name, string expect, bool ok, int err, string detail)
    {
        string outcome = ok ? "ALLOWED" : DeniedCodes.Contains(err) ? "DENIED" : "FAILED";
        return new NegResult(name, expect, outcome, ok ? 0 : err, detail);
    }

    public static List<NegResult> Run(NegRequest q)
    {
        var res = new List<NegResult>();
        void T(string name, string expect, Func<(bool ok, int err, string detail)> f)
        {
            try { var (ok, err, d) = f(); res.Add(Mk(name, expect, ok, err, d)); }
            catch (Exception ex) { res.Add(new NegResult(name, expect, "FAILED", -1, "exception: " + ex.GetType().Name + " " + ex.Message)); }
        }

        // ---------- file system
        T("fs.read.documents.open", "deny", () =>
        {
            nint h = N.CreateFileW(q.Documents, 0x1 /*FILE_LIST_DIRECTORY*/, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (h == -1) return (false, N.LastError, q.Documents);
            N.CloseHandle(h); return (true, 0, q.Documents);
        });
        T("fs.read.documents.enumerate", "deny", () =>
        {
            byte* data = stackalloc byte[1024];
            nint h = N.FindFirstFileW(Path.Combine(q.Documents, "*"), data);
            if (h == -1) return (false, N.LastError, q.Documents + "\\*");
            N.FindClose(h); return (true, 0, q.Documents + "\\*");
        });
        T("fs.write.temp", "deny", () => TryWrite(Path.Combine(q.Temp, $"quire-s3-probe-{Environment.ProcessId}.tmp")));
        T("fs.write.repo-dir", "deny", () => TryWrite(Path.Combine(q.RepoDir, $"quire-s3-probe-{Environment.ProcessId}.tmp")));
        T("fs.write.engine-dir (package-dir analogue)", "deny", () => TryWrite(Path.Combine(q.EngineDir, $"quire-s3-probe-{Environment.ProcessId}.tmp")));
        T("fs.read.repo-file (outside engine dir)", "deny", () =>
        {
            string f = Path.Combine(q.RepoDir, "docs", "MASTER-PROMPT.md");
            nint h = N.CreateFileW(f, N.GENERIC_READ, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
            if (h == -1) return (false, N.LastError, f);
            N.CloseHandle(h); return (true, 0, f);
        });
        T("fs.read.unc", "deny", () =>
        {
            nint h = N.CreateFileW(q.UncPath, 0x1, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (h == -1) return (false, N.LastError, q.UncPath);
            N.CloseHandle(h); return (true, 0, q.UncPath);
        });

        // ---------- network
        T("net.tcp.connect-loopback", "deny", () => TcpConnect(IPAddress.Loopback, q.TcpLoopbackPort));
        T("net.tcp.connect-public (SYN only, no data)", "deny", () => TcpConnect(IPAddress.Parse(q.PublicIp), q.PublicPort));

        // ---------- processes
        T("proc.create.cmd.exe", "deny", () =>
        {
            PROCESS_INFORMATION pi;
            char[] cmd = "C:\\Windows\\System32\\cmd.exe /c exit 0\0".ToCharArray();
            STARTUPINFOW si = default; si.cb = (uint)sizeof(STARTUPINFOW);
            fixed (char* c = cmd)
            {
                if (N.CreateProcessW(null, c, null, null, 0, N.CREATE_NO_WINDOW, null, null, &si, &pi) == 0) return (false, N.LastError, "cmd.exe");
            }
            N.WaitForSingleObject(pi.hProcess, 2000);
            N.TerminateProcess(pi.hProcess, 0);
            N.CloseHandle(pi.hProcess); N.CloseHandle(pi.hThread);
            return (true, 0, "child process started and was reaped");
        });
        T("proc.open.launcher (QUERY_LIMITED_INFORMATION)", "deny", () =>
        {
            nint h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)q.LauncherPid);
            if (h == 0) return (false, N.LastError, "pid " + q.LauncherPid);
            N.CloseHandle(h); return (true, 0, "pid " + q.LauncherPid);
        });
        T("proc.open.launcher (VM_READ|DUP_HANDLE)", "deny", () =>
        {
            nint h = N.OpenProcess(N.PROCESS_VM_READ | N.PROCESS_DUP_HANDLE, 0, (uint)q.LauncherPid);
            if (h == 0) return (false, N.LastError, "pid " + q.LauncherPid);
            N.CloseHandle(h); return (true, 0, "pid " + q.LauncherPid);
        });
        T("proc.open.other-processes (any process other than self/conhost/cmd)", "deny", () =>
        {
            var all = System.Diagnostics.Process.GetProcesses();
            var cand = all.Where(p => p.Id > 4 && p.Id != Environment.ProcessId && p.ProcessName is not ("conhost" or "cmd")).ToArray();
            int q = 0, vmdup = 0; var ex = new List<string>();
            foreach (var p in cand)
            {
                nint h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)p.Id);
                if (h != 0) { q++; N.CloseHandle(h); }
                h = N.OpenProcess(N.PROCESS_VM_READ | N.PROCESS_DUP_HANDLE, 0, (uint)p.Id);
                if (h != 0) { vmdup++; N.CloseHandle(h); if (ex.Count < 4) ex.Add(p.ProcessName); }
            }
            string d = $"process list shows {all.Length} processes; {cand.Length} candidates: QUERY_LIMITED opened={q}, VM_READ|DUP_HANDLE opened={vmdup} {(ex.Count > 0 ? "(" + string.Join(",", ex) + ")" : "")}";
            return vmdup == 0 ? (false, 5, d) : (true, 0, d);
        });
        T("proc.enumerate-handles (NtQuerySystemInformation SystemExtendedHandleInformation)", "deny", () =>
        {
            uint size = 16 << 20; void* mem = null;
            try
            {
                while (true)
                {
                    mem = NativeMemory.Realloc(mem, size);
                    uint ret;
                    int st = N.NtQuerySystemInformation(64, mem, size, &ret);
                    if (st == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, ret + (1u << 20)); if (size > 512u << 20) return (false, st, "buffer cap hit"); continue; }
                    if (st < 0) return (false, st == unchecked((int)0xC0000022) ? 5 : st, $"NTSTATUS 0x{st:X8}");
                    // SYSTEM_HANDLE_INFORMATION_EX: ULONG_PTR NumberOfHandles; ULONG_PTR Reserved; entries[] (40 bytes each, PID at +8)
                    long n = *(long*)mem; int self = Environment.ProcessId; long other = 0;
                    byte* e = (byte*)mem + 16;
                    for (long i = 0; i < n; i++) { if (*(long*)(e + i * 40 + 8) != self) other++; }
                    return (true, 0, $"NTSTATUS success: {n} handles listed, {other} belong to other processes (information disclosure)");
                }
            }
            finally { NativeMemory.Free(mem); }
        });

        // ---------- UI / clipboard
        T("ui.clipboard.read", "deny", () =>
        {
            if (N.OpenClipboard(0) == 0) return (false, N.LastError, "OpenClipboard");
            string d = N.IsClipboardFormatAvailable(13) != 0 ? "unicode text present (content not read)" : "no unicode text on clipboard";
            nint h = N.GetClipboardData(13);
            int e = h == 0 ? N.LastError : 0;
            N.CloseClipboard();
            return (true, 0, "OpenClipboard succeeded; " + d + "; GetClipboardData handle " + (h != 0 ? "non-null" : "null err" + e));
        });

        // ---------- registry
        T("reg.write.hkcu", "deny", () =>
        {
            nint HKCU = unchecked((nint)0x80000001);
            nint key; uint disp;
            int rc = N.RegCreateKeyExW(HKCU, "Software\\QuireS3Probe", 0, null, 0, 0x20006 /*KEY_WRITE*/, null, &key, &disp);
            if (rc != 0) return (false, rc, "RegCreateKeyEx HKCU\\Software\\QuireS3Probe");
            byte v = 1; int rc2 = N.RegSetValueExW(key, "x", 0, 3, &v, 1);
            N.RegCloseKey(key);
            N.RegDeleteKeyW(HKCU, "Software\\QuireS3Probe");
            return (true, 0, $"key created, SetValue rc={rc2} (key removed again)");
        });

        // ---------- DLL loading
        T("dll.load.temp-copy", "deny", () => TryLoad(q.TempDll));
        T("dll.load.unsigned-in-engine-dir", "deny", () => TryLoad(q.LocalRogueDll));

        // ---------- positive controls: these must work or the sandbox is simply broken
        T("ctl.read.engine-dir", "allow", () =>
        {
            string f = Path.Combine(q.EngineDir, "pdfium.dll");
            nint h = N.CreateFileW(f, N.GENERIC_READ, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
            if (h == -1) return (false, N.LastError, f);
            N.CloseHandle(h); return (true, 0, f);
        });
        T("ctl.read.system-font", "allow", () =>
        {
            string f = Path.Combine(q.FontsDir, "arial.ttf");
            nint h = N.CreateFileW(f, N.GENERIC_READ, N.FILE_SHARE_ALL, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
            if (h == -1) return (false, N.LastError, f);
            N.CloseHandle(h); return (true, 0, f);
        });
        return res;
    }

    static (bool, int, string) TryWrite(string path)
    {
        nint h = N.CreateFileW(path, N.GENERIC_WRITE, 0, null, N.CREATE_ALWAYS, N.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1) return (false, N.LastError, path);
        N.CloseHandle(h); N.DeleteFileW(path);
        return (true, 0, path + " (created then deleted)");
    }

    static (bool, int, string) TryLoad(string path)
    {
        nint m = N.LoadLibraryExW(path, 0, 0);
        if (m == 0) return (false, N.LastError, path);
        N.FreeLibrary(m);
        return (true, 0, path);
    }

    static (bool, int, string) TcpConnect(IPAddress ip, int port)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var t = s.ConnectAsync(new IPEndPoint(ip, port));
            if (!t.Wait(4000)) return (false, 10060, $"{ip}:{port} timeout (no answer)");
            return (true, 0, $"{ip}:{port} connected (closed immediately, nothing sent)");
        }
        catch (AggregateException ae) when (ae.InnerException is SocketException se) { return (false, se.ErrorCode, $"{ip}:{port} {se.SocketErrorCode}"); }
        catch (SocketException se) { return (false, se.ErrorCode, $"{ip}:{port} {se.SocketErrorCode}"); }
    }
}
