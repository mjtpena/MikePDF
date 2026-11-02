using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    [DllImport("advapi32.dll", SetLastError = true)] static extern int DuplicateTokenEx(nint tok, uint access, void* sa, int impLevel, int tokType, nint* newTok);

    /// <summary>Diagnostic: which restricting-SID sets let a trivial child (cmd.exe /c exit 7) start under a Low-IL restricted token?</summary>
    public static int LowIlDiag(string[] a)
    {
        nint self;
        N.OpenProcessToken(N.GetCurrentProcess(), N.TOKEN_DUPLICATE | N.TOKEN_QUERY | N.TOKEN_ASSIGN_PRIMARY | N.TOKEN_ADJUST_DEFAULT, &self);
        void* logon = LogonSid(self);
        void* low; N.ConvertStringSidToSidW("S-1-16-4096", &low);

        void* Sid(string s) { void* p; N.ConvertStringSidToSidW(s, &p); return p; }
        var sets = new (string name, void*[] sids)[]
        {
            ("none (DISABLE_MAX_PRIVILEGE only)", []),
            ("Users", [Sid("S-1-5-32-545")]),
            ("Users+RESTRICTED+logon", [Sid("S-1-5-32-545"), Sid("S-1-5-12"), logon]),
            ("Users+RESTRICTED+logon+Everyone", [Sid("S-1-5-32-545"), Sid("S-1-5-12"), logon, Sid("S-1-1-0")]),
            ("Users+RESTRICTED+logon+Everyone+INTERACTIVE+AuthUsers", [Sid("S-1-5-32-545"), Sid("S-1-5-12"), logon, Sid("S-1-1-0"), Sid("S-1-5-4"), Sid("S-1-5-11")]),
            ("Users+Everyone+RESTRICTED+logon+user", [Sid("S-1-5-32-545"), Sid("S-1-1-0"), Sid("S-1-5-12"), logon, UserSid(self)]),
        };
        foreach (var (name, sids) in sets)
        {
            for (int lowIl = 0; lowIl <= 1; lowIl++)
            {
                var rs = stackalloc SID_AND_ATTRIBUTES[8];
                for (int i = 0; i < sids.Length; i++) rs[i] = new SID_AND_ATTRIBUTES { Sid = (PSID)sids[i] };
                nint tok;
                if (N.CreateRestrictedToken(self, N.DISABLE_MAX_PRIVILEGE, 0, null, 0, null, (uint)sids.Length, sids.Length == 0 ? null : rs, &tok) == 0) { Console.WriteLine($"  {name}: CreateRestrictedToken failed {N.LastError}"); continue; }
                if (lowIl == 1)
                {
                    SID_AND_ATTRIBUTES lab = new() { Sid = (PSID)low, Attributes = N.SE_GROUP_INTEGRITY };
                    N.SetTokenInformation(tok, N.TokenIntegrityLevel, &lab, (uint)(sizeof(SID_AND_ATTRIBUTES) + N.GetLengthSid(low)));
                }
                PROCESS_INFORMATION pi; STARTUPINFOW si = default; si.cb = (uint)sizeof(STARTUPINFOW);
                char[] cmd = (a.Length > 0 ? string.Join(" ", a) : "C:\\Windows\\System32\\cmd.exe /c exit 7") .ToCharArray().Concat(new[] { (char)0 }).ToArray();
                int ok;
                fixed (char* c = cmd) ok = N.CreateProcessAsUserW(tok, null, c, null, null, 0, N.CREATE_NO_WINDOW, null, null, &si, &pi);
                int err = N.LastError;
                string r;
                if (ok != 0) { N.WaitForSingleObject(pi.hProcess, 3000); uint code; N.GetExitCodeProcess(pi.hProcess, &code); N.CloseHandle(pi.hProcess); N.CloseHandle(pi.hThread); r = $"started, exit code 0x{code:X}"; }
                else r = $"CreateProcessAsUser failed win32={err}";
                Console.WriteLine($"  [{(lowIl == 1 ? "Low IL " : "Medium ")}] {name,-60} {r}");
                N.CloseHandle(tok);
            }
        }
        return 0;
    }

    static void* LogonSid(nint tok)
    {
        byte* buf = (byte*)NativeMemory.Alloc(8192); uint ret;
        if (N.GetTokenInformation(tok, N.TokenGroups, buf, 8192, &ret) != 0)
        {
            uint n = *(uint*)buf; var g = (SID_AND_ATTRIBUTES*)(buf + 8);
            for (uint i = 0; i < n; i++) if ((g[i].Attributes & 0xC0000000) == 0xC0000000) return (void*)g[i].Sid;
        }
        return null;
    }

    static void* UserSid(nint tok)
    {
        byte* buf = (byte*)NativeMemory.Alloc(8192); uint ret;
        N.GetTokenInformation(tok, N.TokenUser, buf, 8192, &ret);
        return *(void**)buf;
    }
}
