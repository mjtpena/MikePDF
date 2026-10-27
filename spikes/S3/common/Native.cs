using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Quire.S3;

/// <summary>
/// Hand-declared function signatures (blittable, nint handles). Struct layouts and constants come from the
/// Microsoft.Windows.CsWin32-generated metadata projection in this assembly (SECURITY_CAPABILITIES, STARTUPINFOEXW, JOBOBJECT_*, ...).
/// </summary>
public static unsafe class N
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_ALL = 7;
    public const uint CREATE_NEW = 1, CREATE_ALWAYS = 2, OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint PAGE_READWRITE = 4;
    public const uint FILE_MAP_ALL_ACCESS = 0xF001F, FILE_MAP_READ = 4, FILE_MAP_WRITE = 2;
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 4, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    public const uint STARTF_USESTDHANDLES = 0x100;
    public const uint PROCESS_TERMINATE = 1, PROCESS_VM_READ = 0x10, PROCESS_DUP_HANDLE = 0x40, PROCESS_QUERY_INFORMATION = 0x400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_ALL_ACCESS = 0x1FFFFF;
    public const uint HANDLE_FLAG_INHERIT = 1;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;
    public const uint TOKEN_ASSIGN_PRIMARY = 1, TOKEN_DUPLICATE = 2, TOKEN_QUERY = 8, TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_ADJUST_PRIVILEGES = 0x20;
    public const int TokenUser = 1, TokenGroups = 2, TokenIntegrityLevel = 25, TokenIsAppContainer = 29, TokenCapabilities = 30, TokenAppContainerSid = 31, TokenAppContainerNumber = 32, TokenElevation = 20;
    public const uint DISABLE_MAX_PRIVILEGE = 1, SANDBOX_INHERIT = 2, LUA_TOKEN = 4, WRITE_RESTRICTED = 8;
    public const uint SE_GROUP_INTEGRITY = 0x20, SE_GROUP_USE_FOR_DENY_ONLY = 0x10, SE_GROUP_ENABLED = 4;
    public const int JobObjectBasicUIRestrictions = 4, JobObjectAssociateCompletionPortInformation = 7, JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100, JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000, JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400, JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 8;
    public const uint JOB_OBJECT_MSG_NEW_PROCESS = 6, JOB_OBJECT_MSG_EXIT_PROCESS = 7, JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8, JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT = 9, JOB_OBJECT_MSG_JOB_MEMORY_LIMIT = 10, JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4;
    public const uint JOB_OBJECT_UILIMIT_ALL = 0x1FF; // HANDLES|READCLIPBOARD|WRITECLIPBOARD|SYSTEMPARAMETERS|DISPLAYSETTINGS|GLOBALATOMS|DESKTOP|EXITWINDOWS

    // ---- kernel32
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateFileW(string name, uint access, uint share, void* sa, uint disp, uint flags, nint tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetFileSizeEx(nint h, long* size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int ReadFile(nint h, byte* buf, uint n, uint* read, void* ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int WriteFile(nint h, byte* buf, uint n, uint* written, void* ov);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateFileMappingW(nint file, void* sa, uint prot, uint hi, uint lo, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern void* MapViewOfFile(nint h, uint access, uint hi, uint lo, nuint n);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int UnmapViewOfFile(void* p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int DuplicateHandle(nint srcProc, nint srcH, nint dstProc, nint* dstH, uint access, int inherit, uint options);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, int inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern void* VirtualAlloc(void* addr, nuint size, uint type, uint prot);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int VirtualFree(void* addr, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint LoadLibraryExW(string name, nint file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int FreeLibrary(nint h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint FindFirstFileW(string pattern, void* data);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int FindClose(nint h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int CreateProcessW(string? app, char* cmd, void* pa, void* ta, int inherit, uint flags, void* env, string? cwd, void* si, PROCESS_INFORMATION* pi);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int TerminateProcess(nint h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetExitCodeProcess(nint h, uint* code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(nint h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateJobObjectW(void* sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int SetInformationJobObject(nint job, int cls, void* info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int QueryInformationJobObject(nint job, int cls, void* info, uint len, uint* ret);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int AssignProcessToJobObject(nint job, nint proc);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateIoCompletionPort(nint file, nint port, nuint key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetQueuedCompletionStatus(nint port, uint* bytes, nuint* key, void** ov, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int InitializeProcThreadAttributeList(void* list, uint count, uint flags, nuint* size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int UpdateProcThreadAttribute(void* list, uint flags, nuint attr, void* value, nuint size, void* prev, nuint* ret);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(void* list);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int SetHandleInformation(nint h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetSystemTimes(ulong* idle, ulong* kernel, ulong* user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetProcessTimes(nint h, ulong* create, ulong* exit, ulong* kernel, ulong* user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int K32GetProcessMemoryInfo(nint h, void* counters, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetProcessMitigationPolicy(nint h, int policy, void* buf, nuint len);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int SetProcessMitigationPolicy(int policy, void* buf, nuint len);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint LocalFree(nint p);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern uint GetEnvironmentVariableW(string name, char* buf, uint n);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int DeleteFileW(string path);

    // ---- advapi32
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int OpenProcessToken(nint proc, uint access, nint* tok);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int GetTokenInformation(nint tok, int cls, void* buf, uint len, uint* ret);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int SetTokenInformation(nint tok, int cls, void* buf, uint len);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int CreateRestrictedToken(nint existing, uint flags, uint nDisable, SID_AND_ATTRIBUTES* disable, uint nDelPriv, void* delPriv, uint nRestrict, SID_AND_ATTRIBUTES* restrict, nint* newTok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int CreateProcessAsUserW(nint tok, string? app, char* cmd, void* pa, void* ta, int inherit, uint flags, void* env, string? cwd, void* si, PROCESS_INFORMATION* pi);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int ConvertStringSidToSidW(string s, void** sid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int ConvertSidToStringSidW(void* sid, char** s);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern int CreateWellKnownSid(int type, void* domain, void* sid, uint* cb);
    [DllImport("advapi32.dll")] public static extern uint GetLengthSid(void* sid);
    [DllImport("advapi32.dll")] public static extern void* FreeSid(void* sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegCreateKeyExW(nint hKey, string sub, uint res, string? cls, uint opt, uint sam, void* sa, nint* result, uint* disp);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegSetValueExW(nint hKey, string name, uint res, uint type, byte* data, uint cb);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegDeleteKeyW(nint hKey, string sub);
    [DllImport("advapi32.dll")] public static extern int RegCloseKey(nint hKey);

    // ---- userenv
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int CreateAppContainerProfile(string name, string disp, string desc, SID_AND_ATTRIBUTES* caps, uint ncaps, void** sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int DeriveAppContainerSidFromAppContainerName(string name, void** sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int GetAppContainerFolderPath(string sidStr, char** path);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(void* p);

    // ---- user32 / gdi32 / ntdll
    [DllImport("user32.dll", SetLastError = true)] public static extern int OpenClipboard(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint GetClipboardData(uint fmt);
    [DllImport("user32.dll", SetLastError = true)] public static extern int CloseClipboard();
    [DllImport("user32.dll")] public static extern int IsClipboardFormatAvailable(uint fmt);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern nint CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern int GetTextFaceW(nint hdc, int c, char* buf);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern uint GetFontData(nint hdc, uint table, uint offset, void* buf, uint cb);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int DeleteDC(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int DeleteObject(nint obj);
    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, void* buf, uint len, uint* ret);

    // ---- helpers
    public static int LastError => Marshal.GetLastPInvokeError();

    public static string SidToString(void* sid)
    {
        char* s;
        if (ConvertSidToStringSidW(sid, &s) == 0) return "?";
        var r = new string(s);
        LocalFree((nint)s);
        return r;
    }
}
