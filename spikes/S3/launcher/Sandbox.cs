using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Quire.S3.Launcher;

public enum Isolation { None, AppContainer, LowIL }

public sealed class SpawnOptions
{
    public Isolation Mode = Isolation.AppContainer;
    public string Label = "";
    public string EngineDir = "";
    public string ExtraArgs = "";
    public string? ExeName;                  // default quire-engine-stub.exe (.NET); S3b uses engine-static.exe / engine-delay.exe
    public ulong Mit1, Mit2;                 // PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY (two DWORD64s)
    public bool ChildProcessRestricted;      // PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY
    public bool OmitAcFromPipeDacl;           // pipe-ACL test: DACL = current user only
    public bool ChildUsesSecondContainer;     // pipe-ACL test: launch the child in a DIFFERENT AppContainer than the one named in the pipe DACL
    public bool AllAppPackagesOptOut;        // PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY
    public long ProcessMemoryLimit;          // JOB_OBJECT_LIMIT_PROCESS_MEMORY (0 = none)
    public long JobMemoryLimit;              // JOB_OBJECT_LIMIT_JOB_MEMORY (0 = none)
    public uint JobUiLimits;                 // JOBOBJECT_BASIC_UI_RESTRICTIONS flags (0 = none)
    public bool UseJobList = true;           // PROC_THREAD_ATTRIBUTE_JOB_LIST (atomic) vs CREATE_SUSPENDED + AssignProcessToJobObject
    public string FontMode = "gdi";
    public string? FilePath;                 // opened read-only by the broker and passed through the handle list
    public bool PassSection = true;
    public uint ExtraRuntimeMitigations;     // sent as ApplyRuntimeMitigations after Hello
    public override string ToString() => Label;
}

/// <summary>AppContainer profile lifetime + ACL helper.</summary>
public static unsafe class AppContainerProfile
{
    public const string Name = "Quire.S3.Probe";
    static void* _sid;
    public static string SidString { get; private set; } = "";
    public static SecurityIdentifier Sid => new(SidString);
    public static bool CreatedByUs { get; private set; }
    public static int CreateHr { get; private set; }

    public static void* EnsureCreated()
    {
        if (_sid != null) return _sid;
        void* sid;
        int hr = N.CreateAppContainerProfile(Name, "Quire S3 spike probe", "Quire M0 spike S3 (zero-capability AppContainer)", null, 0, &sid);
        CreateHr = hr;
        if (hr == unchecked((int)0x800700B7)) // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS): profile left over from an earlier run
        {
            hr = N.DeriveAppContainerSidFromAppContainerName(Name, &sid);
            if (hr != 0) throw new Win32Exception(hr, "DeriveAppContainerSidFromAppContainerName");
        }
        else if (hr != 0) throw new Win32Exception(hr, "CreateAppContainerProfile hr=0x" + hr.ToString("X8"));
        else CreatedByUs = true;
        _sid = sid;
        SidString = N.SidToString(sid);
        return sid;
    }

    public const string Name2 = "Quire.S3.Probe2";
    static void* _sid2;
    public static string Sid2String { get; private set; } = "";
    public static void* EnsureSecond()
    {
        if (_sid2 != null) return _sid2;
        void* sid;
        int hr = N.CreateAppContainerProfile(Name2, "Quire S3 spike probe 2", "second container for the pipe-ACL test", null, 0, &sid);
        if (hr == unchecked((int)0x800700B7)) hr = N.DeriveAppContainerSidFromAppContainerName(Name2, &sid);
        if (hr != 0) throw new Win32Exception(hr, "second AppContainer profile");
        _sid2 = sid; Sid2String = N.SidToString(sid);
        return sid;
    }

    public static void Delete()
    {
        if (_sid == null && !ProfileExists()) return;
        if (_sid2 != null) { int hr2 = N.DeleteAppContainerProfile(Name2); N.FreeSid(_sid2); _sid2 = null; Console.WriteLine($"[sandbox] DeleteAppContainerProfile({Name2}) hr=0x{hr2:X8}"); }
        int hr = N.DeleteAppContainerProfile(Name);
        if (_sid != null) { N.FreeSid(_sid); _sid = null; }
        Console.WriteLine($"[sandbox] DeleteAppContainerProfile hr=0x{hr:X8}");
    }

    static bool ProfileExists()
    {
        void* s; int hr = N.DeriveAppContainerSidFromAppContainerName(Name, &s);
        if (hr == 0) { N.FreeSid(s); return true; }   // Derive succeeds even if the profile doesn't exist; harmless extra delete
        return false;
    }

    /// <summary>Grant the container read+execute on a directory tree INSIDE THE REPO (never Program Files).</summary>
    public static void GrantReadExecute(string dir, SecurityIdentifier sid)
    {
        var di = new DirectoryInfo(dir);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }
}

public sealed unsafe class JobEvent
{
    public long Ticks; public uint Msg; public nuint Pid; public string Name = "";
}

/// <summary>One engine process + its pipe + job object. Everything the broker owns for a single engine.</summary>
public sealed unsafe class EngineProcess : IDisposable
{
    public readonly SpawnOptions Opt;
    public nint ProcessHandle, ThreadHandle, Job, IoPort, FileHandle, SectionHandle;
    public uint Pid;
    public byte* Shm;
    public NamedPipeServerStream Pipe = null!;
    public readonly StringBuilder StdErr = new();
    public readonly List<JobEvent> JobEvents = new();
    public string PipeName = "", PipeSddl = "";
    public Dictionary<string, string> Hello = new();
    public long TCreateStart, TCreated, TConnected, THelloDone, EMain, EPdfium, EPipe, EHello;
    public double EnginePdfiumInitMs;
    public bool ClientPidVerified;
    public string? SpawnFailure;
    public List<(string name, int err)> RuntimeMitResults = new();
    public int StartupCpuMs;
    uint _nextReq = 1;
    Thread? _jobThread, _errThread;
    volatile bool _stop;
    AnonymousPipeServerStream? _errPipe;
    readonly byte[] _hdr = new byte[Proto.HeaderSize];

    public EngineProcess(SpawnOptions opt) { Opt = opt; }

    public bool HasExited
    {
        get { uint c; return ProcessHandle == 0 || (N.GetExitCodeProcess(ProcessHandle, &c) != 0 && c != 259); }
    }
    public uint ExitCode { get { uint c; N.GetExitCodeProcess(ProcessHandle, &c); return c; } }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuf, uint inBuf, uint defTimeout, SECURITY_ATTRIBUTES* sa);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, void** sd, uint* size);

    [DllImport("kernel32.dll", SetLastError = true)] static extern int GetNamedPipeClientProcessId(nint pipe, uint* pid);

    static string RandomName() => "quire-s3-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Spawn, wait for the pipe connection and do the Hello exchange. Throws on failure, with stderr and exit code in the message.</summary>
    public void Start(int timeoutMs = 15000)
    {
        string exe = Environment.GetEnvironmentVariable("S3_EXE") ?? Path.Combine(Opt.EngineDir, Opt.ExeName ?? "quire-engine-stub.exe");   // S3_EXE: diagnostics only (native control processes)
        bool ac = Opt.Mode == Isolation.AppContainer, low = Opt.Mode == Isolation.LowIL;
        void* acSid = ac ? AppContainerProfile.EnsureCreated() : null;
        void* childSid = Opt.ChildUsesSecondContainer ? AppContainerProfile.EnsureSecond() : acSid;

        // ---- named pipe: random name, DACL = current user + (AppContainer SID | RESTRICTED for the low-IL variant). Nothing else.
        PipeName = RandomName();
        string userSid = WindowsIdentity.GetCurrent().User!.Value;
        string sddl = $"D:(A;;FRFW;;;{userSid})";
        if (ac && !Opt.OmitAcFromPipeDacl) sddl += $"(A;;FRFW;;;{AppContainerProfile.SidString})";
        if (low) sddl += "S:(ML;;NW;;;LW)";   // RC = RESTRICTED (S-1-5-12); label lets a Low-IL client write to it
        PipeSddl = sddl;
        // Created natively (not through NamedPipeServerStreamAcl): .NET requests ACCESS_SYSTEM_SECURITY when the SD has a SACL, which needs SeSecurityPrivilege.
        // A plain CreateNamedPipe with the SD in SECURITY_ATTRIBUTES lets a standard user attach a Low mandatory label to its own pipe.
        void* psd;
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, &psd, null) == 0) throw new Win32Exception(N.LastError, "ConvertStringSecurityDescriptor");
        SECURITY_ATTRIBUTES psa = default; psa.nLength = (uint)sizeof(SECURITY_ATTRIBUTES); psa.lpSecurityDescriptor = psd;
        // PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE ; PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS
        nint hPipe = CreateNamedPipeW(@"\\.\pipe\" + PipeName, 0x3 | 0x00080000, 0x0 | 0x0 | 0x0 | 0x8, 1, 1 << 16, 1 << 16, 0, &psa);
        int pipeErr = N.LastError;
        N.LocalFree((nint)psd);
        if (hPipe == -1) throw new Win32Exception(pipeErr, "CreateNamedPipe sddl=" + sddl);
        Pipe = new NamedPipeServerStream(PipeDirection.InOut, false, false, new Microsoft.Win32.SafeHandles.SafePipeHandle(hPipe, true));

        // ---- handles passed to the child: read-only file + anonymous-pipe stderr + shared-memory section (all inheritable, listed explicitly)
        SECURITY_ATTRIBUTES sa = default; sa.nLength = (uint)sizeof(SECURITY_ATTRIBUTES); sa.bInheritHandle = true;
        var inherit = new List<nint>();
        if (Opt.FilePath != null)
        {
            FileHandle = N.CreateFileW(Opt.FilePath, N.GENERIC_READ, 1 /*FILE_SHARE_READ*/, &sa, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
            if (FileHandle == -1) throw new Win32Exception(N.LastError, "open " + Opt.FilePath);
            inherit.Add(FileHandle);
        }
        if (Opt.PassSection)
        {
            SectionHandle = N.CreateFileMappingW(-1, &sa, N.PAGE_READWRITE, (uint)(Shm_Total >> 32), (uint)Shm_Total, null);
            if (SectionHandle == 0) throw new Win32Exception(N.LastError, "CreateFileMapping");
            inherit.Add(SectionHandle);
            Shm = (byte*)N.MapViewOfFile(SectionHandle, N.FILE_MAP_ALL_ACCESS, 0, 0, 0);   // broker keeps its own view to read tiles out of
            if (Shm == null) throw new Win32Exception(N.LastError, "MapViewOfFile(broker)");
        }
        _errPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint errClient = _errPipe.ClientSafePipeHandle.DangerousGetHandle();
        inherit.Add(errClient);

        // ---- job object (memory cap + kill-on-close), completion port for limit notifications
        Job = N.CreateJobObjectW(null, null);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jl = default;
        uint lf = N.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (Opt.ProcessMemoryLimit > 0) { lf |= N.JOB_OBJECT_LIMIT_PROCESS_MEMORY; jl.ProcessMemoryLimit = (nuint)Opt.ProcessMemoryLimit; }
        if (Opt.JobMemoryLimit > 0) { lf |= N.JOB_OBJECT_LIMIT_JOB_MEMORY; jl.JobMemoryLimit = (nuint)Opt.JobMemoryLimit; }
        jl.BasicLimitInformation.LimitFlags = (JOB_OBJECT_LIMIT)lf;
        if (N.SetInformationJobObject(Job, N.JobObjectExtendedLimitInformation, &jl, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0) throw new Win32Exception(N.LastError, "SetInformationJobObject(limits)");
        if (Opt.JobUiLimits != 0)
        {
            JOBOBJECT_BASIC_UI_RESTRICTIONS ui = default; ui.UIRestrictionsClass = (JOB_OBJECT_UILIMIT)Opt.JobUiLimits;
            if (N.SetInformationJobObject(Job, N.JobObjectBasicUIRestrictions, &ui, (uint)sizeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)) == 0) throw new Win32Exception(N.LastError, "SetInformationJobObject(ui)");
        }
        IoPort = N.CreateIoCompletionPort(-1, 0, 0, 1);
        JOBOBJECT_ASSOCIATE_COMPLETION_PORT cp = default; cp.CompletionKey = (void*)0x51; cp.CompletionPort = (HANDLE)IoPort;
        if (N.SetInformationJobObject(Job, N.JobObjectAssociateCompletionPortInformation, &cp, (uint)sizeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT)) == 0) throw new Win32Exception(N.LastError, "SetInformationJobObject(iocp)");
        _jobThread = new Thread(JobLoop) { IsBackground = true, Name = "job-events" };
        _jobThread.Start();

        // ---- attribute list
        int nAttr = 1 /*handles*/ + (ac ? 1 : 0) + ((Opt.Mit1 | Opt.Mit2) != 0 ? 1 : 0) + (Opt.ChildProcessRestricted ? 1 : 0) + (Opt.AllAppPackagesOptOut ? 1 : 0) + (Opt.UseJobList ? 1 : 0);
        nuint asz = 0;
        N.InitializeProcThreadAttributeList(null, (uint)nAttr, 0, &asz);
        void* attrs = NativeMemory.AllocZeroed(asz);
        if (N.InitializeProcThreadAttributeList(attrs, (uint)nAttr, 0, &asz) == 0) throw new Win32Exception(N.LastError, "InitializeProcThreadAttributeList");

        nint* handles = (nint*)NativeMemory.Alloc((nuint)(inherit.Count * sizeof(nint)));
        for (int i = 0; i < inherit.Count; i++) handles[i] = inherit[i];
        Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (nuint)(inherit.Count * sizeof(nint)), null, null), "UpdateProcThreadAttribute(HANDLE_LIST)");

        SECURITY_CAPABILITIES* secCaps = (SECURITY_CAPABILITIES*)NativeMemory.AllocZeroed((nuint)sizeof(SECURITY_CAPABILITIES));
        if (ac)
        {
            secCaps->AppContainerSid = (PSID)childSid;
            secCaps->Capabilities = null;        // ZERO capabilities
            secCaps->CapabilityCount = 0;
            Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, secCaps, (nuint)sizeof(SECURITY_CAPABILITIES), null, null), "UpdateProcThreadAttribute(SECURITY_CAPABILITIES)");
        }
        ulong* mit = (ulong*)NativeMemory.AllocZeroed(16);
        if ((Opt.Mit1 | Opt.Mit2) != 0)
        {
            mit[0] = Opt.Mit1; mit[1] = Opt.Mit2;
            Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY, mit, Opt.Mit2 != 0 ? 16u : 8u, null, null), "UpdateProcThreadAttribute(MITIGATION_POLICY)");
        }
        uint* child = (uint*)NativeMemory.AllocZeroed(8);
        if (Opt.ChildProcessRestricted)
        {
            child[0] = PInvoke.PROCESS_CREATION_CHILD_PROCESS_RESTRICTED;
            Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, child, 4, null, null), "UpdateProcThreadAttribute(CHILD_PROCESS_POLICY)");
        }
        uint* aap = (uint*)NativeMemory.AllocZeroed(8);
        if (Opt.AllAppPackagesOptOut)
        {
            aap[0] = PInvoke.PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT;
            Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY, aap, 4, null, null), "UpdateProcThreadAttribute(ALL_APPLICATION_PACKAGES_POLICY)");
        }
        nint* jobList = (nint*)NativeMemory.AllocZeroed(8);
        if (Opt.UseJobList)
        {
            jobList[0] = Job;
            Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, jobList, 8, null, null), "UpdateProcThreadAttribute(JOB_LIST)");
        }

        // ---- command line
        var cmd = new StringBuilder($"\"{exe}\" --pipe {PipeName} --fontmode {Opt.FontMode}");
        if (FileHandle != 0) cmd.Append($" --file {FileHandle}");
        if (SectionHandle != 0) cmd.Append($" --section {SectionHandle}");
        if (Opt.ExtraArgs.Length > 0) cmd.Append(" " + Opt.ExtraArgs);

        STARTUPINFOEXW si = default;
        si.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
        si.lpAttributeList = *(LPPROC_THREAD_ATTRIBUTE_LIST*)&attrs;
        si.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
        si.StartupInfo.hStdError = (HANDLE)errClient;
        si.StartupInfo.hStdOutput = (HANDLE)errClient;
        si.StartupInfo.hStdInput = default;
        PROCESS_INFORMATION pi;
        // DETACHED_PROCESS (no console) by default: a console-subsystem exe started with CREATE_NO_WINDOW gets a conhost.exe child, which the
        // child-process policy and Win32k lockdown both break (exit 0xC0000142). S3_CONSOLE=1 restores CREATE_NO_WINDOW for comparison.
        uint consoleFlag = Environment.GetEnvironmentVariable("S3_CONSOLE") == "1" ? N.CREATE_NO_WINDOW : 0x8u;
        uint prioFlag = Environment.GetEnvironmentVariable("S3_PRIORITY") == "high" ? 0x80u /*HIGH_PRIORITY_CLASS*/ : 0u;   // bench option: reduce scheduler noise on a shared machine
        uint flags = N.EXTENDED_STARTUPINFO_PRESENT | consoleFlag | prioFlag | (Opt.UseJobList ? 0u : N.CREATE_SUSPENDED);
        string cmdline = cmd.ToString();
        nint restricted = 0;
        TCreateStart = Clock.Now();
        int ok;
        fixed (char* c = cmdline + "\0")
        {
            if (low)
            {
                restricted = MakeLowIlRestrictedToken();
                var dm = Environment.GetEnvironmentVariable("S3_DIAG");
                if (dm == "plain") { STARTUPINFOW s0 = default; s0.cb = (uint)sizeof(STARTUPINFOW); ok = N.CreateProcessAsUserW(restricted, null, c, null, null, 0, N.CREATE_NO_WINDOW | N.CREATE_SUSPENDED, null, null, &s0, &pi); }
                else if (dm == "inherit") { STARTUPINFOW s0 = default; s0.cb = (uint)sizeof(STARTUPINFOW); ok = N.CreateProcessAsUserW(restricted, null, c, null, null, 1, N.CREATE_NO_WINDOW | N.CREATE_SUSPENDED, null, null, &s0, &pi); }
                else ok = N.CreateProcessAsUserW(restricted, null, c, null, null, 1, flags, null, Opt.EngineDir, &si, &pi);
            }
            else ok = N.CreateProcessW(null, c, null, null, 1, flags, null, Opt.EngineDir, &si, &pi);
        }
        int createErr = N.LastError;
        TCreated = Clock.Now();
        N.DeleteProcThreadAttributeList(attrs);
        NativeMemory.Free(attrs); NativeMemory.Free(handles); NativeMemory.Free(secCaps); NativeMemory.Free(mit); NativeMemory.Free(child); NativeMemory.Free(aap); NativeMemory.Free(jobList);
        if (restricted != 0) N.CloseHandle(restricted);
        _errPipe.DisposeLocalCopyOfClientHandle();      // child holds the only remaining client end
        if (ok == 0) { SpawnFailure = $"CreateProcess failed: Win32 {createErr} ({new Win32Exception(createErr).Message})"; throw new InvalidOperationException(SpawnFailure); }
        ProcessHandle = pi.hProcess; ThreadHandle = pi.hThread; Pid = pi.dwProcessId;
        if (!Opt.UseJobList && Environment.GetEnvironmentVariable("S3_DIAG_NOJOB") != "1")
        {
            if (N.AssignProcessToJobObject(Job, ProcessHandle) == 0) { int e = N.LastError; N.TerminateProcess(ProcessHandle, 1); throw new Win32Exception(e, "AssignProcessToJobObject"); }
            N.ResumeThread(ThreadHandle);
        }
        else if (!Opt.UseJobList) N.ResumeThread(ThreadHandle);
        // the child has its own copies now; the broker closes its copy of the file handle (it re-opens/duplicates for later documents)
        if (FileHandle != 0) { N.CloseHandle(FileHandle); FileHandle = 0; }

        _errThread = new Thread(() =>
        {
            try { var buf = new byte[4096]; int n; while ((n = _errPipe.Read(buf)) > 0) lock (StdErr) StdErr.Append(Encoding.UTF8.GetString(buf, 0, n)); } catch { }
        }) { IsBackground = true };
        _errThread.Start();

        // ---- wait for the engine to connect (or die)
        var wait = Pipe.WaitForConnectionAsync();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!wait.Wait(20))
        {
            if (HasExited) throw new InvalidOperationException($"engine exited before connecting: exit code 0x{ExitCode:X8}; stderr: {StdErrText()}");
            if (Environment.TickCount64 > deadline) { Kill(); throw new TimeoutException($"engine did not connect within {timeoutMs} ms; stderr: {StdErrText()}"); }
        }
        TConnected = Clock.Now();
        {   // pipe-squatting defence: the connected client must be the process we just created
            uint clientPid;
            if (GetNamedPipeClientProcessId(Pipe.SafePipeHandle.DangerousGetHandle(), &clientPid) == 0 || clientPid != Pid)
            { Kill(); throw new InvalidOperationException($"pipe client pid {clientPid} is not the engine we spawned (pid {Pid})"); }
            ClientPidVerified = true;
        }

        // ---- Hello
        var r = Call(Msg.Hello, new PayloadWriter().U32(Proto.Version), timeoutMs);
        THelloDone = Clock.Now();
        uint ver = r.U32(); EMain = r.I64(); EPdfium = r.I64(); EPipe = r.I64(); EHello = r.I64();
        string info = r.Str(1 << 20);
        foreach (var line in info.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) Hello[line[..eq]] = line[(eq + 1)..];
        }
        if (ver != Proto.Version) throw new ProtocolException("engine protocol version " + ver);
        if (Hello.TryGetValue("pdfiumInitMs", out var pm)) double.TryParse(pm, System.Globalization.CultureInfo.InvariantCulture, out EnginePdfiumInitMs);
        if (Opt.ExtraRuntimeMitigations != 0) RuntimeMitResults = ApplyRuntimeMitigations(Opt.ExtraRuntimeMitigations);
    }

    const long Shm_Total = Quire.S3.Shm.TotalSize;

    static void Check(int ok, string what) { if (ok == 0) throw new Win32Exception(N.LastError, what); }

    public string StdErrText() { lock (StdErr) return StdErr.ToString().Trim(); }

    // ------------------------------------------------------------------------------------------- RPC
    public PayloadReader Call(Msg type, PayloadWriter? payload, int timeoutMs = 30000)
    {
        uint id = _nextReq++;
        FrameIO.Write(Pipe, (ushort)type, id, payload == null ? ReadOnlySpan<byte>.Empty : payload.Span);
        Pipe.Flush();
        if (!FrameIO.Read(Pipe, _hdr, out ushort rt, out uint rid, out byte[] body)) throw new EndOfStreamException("engine closed the pipe");
        if (rid != id || rt != ((ushort)type | Proto.ResponseBit)) throw new ProtocolException($"unexpected response type={rt} id={rid} (wanted {(ushort)type | Proto.ResponseBit}/{id})");
        return new PayloadReader(body);
    }

    /// <summary>Send without waiting (for the timed Ping loop we call Call() directly; this is for fire-and-forget crash tests).</summary>
    public void Send(Msg type, PayloadWriter? payload)
    {
        FrameIO.Write(Pipe, (ushort)type, _nextReq++, payload == null ? ReadOnlySpan<byte>.Empty : payload.Span);
        Pipe.Flush();
    }

    public (int status, int pages, float w, float h, double ms, uint pdfErr, string note) OpenDocument(long handleValue)
    {
        long t0 = Clock.Now();
        var r = Call(Msg.OpenDocument, new PayloadWriter().I64(handleValue));
        double rtt = Clock.Ms(Clock.Now() - t0);
        int st = r.I32(), pages = r.I32(); float w = r.F32(), h = r.F32(); r.I64(); r.I64(); uint err = r.U32(); string note = r.Str();
        return (st, pages, w, h, rtt, err, note);
    }

    /// <summary>Duplicate a freshly opened read-only file handle into the engine (broker owns file I/O), then OpenDocument on it.</summary>
    public (int status, int pages, float w, float h, double ms, uint pdfErr, string note) OpenDocumentFromPath(string path)
    {
        nint local = N.CreateFileW(path, N.GENERIC_READ, 1, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
        if (local == -1) throw new Win32Exception(N.LastError, "open " + path);
        nint remote;
        // DUPLICATE_SAME_ACCESS = 2: the engine gets exactly GENERIC_READ-equivalent access; the file was opened read-only so it can't get more.
        if (N.DuplicateHandle(N.GetCurrentProcess(), local, ProcessHandle, &remote, 0, 0, 2) == 0) { int e = N.LastError; N.CloseHandle(local); throw new Win32Exception(e, "DuplicateHandle into engine"); }
        N.CloseHandle(local);
        return OpenDocument(remote);
    }

    public record RenderTiming(int Status, double RttMs, double LoadMs, double RenderMs, double OutMs, double QueueMs, double InMs, float PageW, float PageH);

    public RenderTiming RenderTile(int page, float scale, int ox, int oy, int w, int h, int slot, int flags = Pdfium_FPDF_ANNOT)
    {
        var pw = _renderWriter; pw.Reset();
        pw.I32(page).F32(scale).I32(ox).I32(oy).I32(w).I32(h).I32(slot).I32(flags);
        long t0 = Clock.Now();
        var r = Call(Msg.RenderTile, pw);
        long t1 = Clock.Now();
        int st = r.I32();
        if (st != 0) return new RenderTiming(st, Clock.Ms(t1 - t0), 0, 0, 0, 0, 0, 0, 0);
        r.I32(); long tRecv = r.I64(), tDeq = r.I64(), tR0 = r.I64(), tLoaded = r.I64(), tR1 = r.I64(), tSend = r.I64(); float pgw = r.F32(), pgh = r.F32();
        return new RenderTiming(0, Clock.Ms(t1 - t0), Clock.Ms(tLoaded - tR0), Clock.Ms(tR1 - tLoaded), Clock.Ms(tRecv - t0), Clock.Ms(tDeq - tRecv), Clock.Ms(t1 - tSend), pgw, pgh);
    }
    readonly PayloadWriter _renderWriter = new();
    const int Pdfium_FPDF_ANNOT = 1;

    public List<(string name, int err)> ApplyRuntimeMitigations(uint mask)
    {
        var r = Call(Msg.ApplyRuntimeMitigations, new PayloadWriter().U32(mask));
        r.I32(); int n = r.I32(); var l = new List<(string, int)>();
        for (int i = 0; i < n; i++) l.Add((r.Str(), r.I32()));
        return l;
    }

    public ReadOnlySpan<byte> SlotPixels(int slot, int w, int h) => new(Shm + Quire.S3.Shm.SlotOffset(slot), w * h * 4);

    // ------------------------------------------------------------------------------------------- job monitoring
    void JobLoop()
    {
        while (!_stop)
        {
            uint bytes; nuint key; void* ov;
            if (N.GetQueuedCompletionStatus(IoPort, &bytes, &key, &ov, 100) == 0) { if (N.LastError == 258) continue; break; }
            lock (JobEvents) JobEvents.Add(new JobEvent
            {
                Ticks = Clock.Now(), Msg = bytes, Pid = (nuint)ov,
                Name = bytes switch
                {
                    N.JOB_OBJECT_MSG_NEW_PROCESS => "NEW_PROCESS", N.JOB_OBJECT_MSG_EXIT_PROCESS => "EXIT_PROCESS",
                    N.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => "ABNORMAL_EXIT_PROCESS", N.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT => "PROCESS_MEMORY_LIMIT",
                    N.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT => "JOB_MEMORY_LIMIT", N.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => "ACTIVE_PROCESS_ZERO", _ => "msg" + bytes
                }
            });
        }
    }

    public string JobEventsText() { lock (JobEvents) return string.Join(", ", JobEvents.Select(e => $"{e.Name}(pid {e.Pid})")); }

    public bool WaitExit(int ms) => ProcessHandle != 0 && N.WaitForSingleObject(ProcessHandle, (uint)ms) == 0;

    public void Kill() { if (ProcessHandle != 0) N.TerminateProcess(ProcessHandle, 0xDEAD); }

    public double CpuMs()
    {
        ulong c, e, k, u;
        if (ProcessHandle == 0 || N.GetProcessTimes(ProcessHandle, &c, &e, &k, &u) == 0) return 0;
        return (k + u) / 10000.0;
    }

    public double WorkingSetMB()
    {
        byte* pmc = stackalloc byte[80];           // PROCESS_MEMORY_COUNTERS_EX is 72 bytes on x64
        *(uint*)pmc = 72;
        if (ProcessHandle == 0 || N.K32GetProcessMemoryInfo(ProcessHandle, pmc, 72) == 0) return 0;
        return *(nuint*)(pmc + 16) / 1048576.0;    // WorkingSetSize
    }

    // ------------------------------------------------------------------------------------------- low-IL restricted token
    static nint MakeLowIlRestrictedToken()
    {
        nint self;
        if (N.OpenProcessToken(N.GetCurrentProcess(), N.TOKEN_DUPLICATE | N.TOKEN_QUERY | N.TOKEN_ASSIGN_PRIMARY | N.TOKEN_ADJUST_DEFAULT, &self) == 0) throw new Win32Exception(N.LastError, "OpenProcessToken");
        // NOTE: no restricting SIDs. Diagnosis (lowildiag verb) showed that a restricting-SID set without the user SID makes even cmd.exe die with 0xC0000142/0xC0000022;
        // read isolation from restricting SIDs would need Chromium's initial-token/lockdown-token two-stage scheme, which is out of scope for this spike.
        nint tok;
        if (N.CreateRestrictedToken(self, N.DISABLE_MAX_PRIVILEGE, 0, null, 0, null, 0, null, &tok) == 0) throw new Win32Exception(N.LastError, "CreateRestrictedToken");
        // drop to Low integrity (S-1-16-4096)
        void* low; N.ConvertStringSidToSidW("S-1-16-4096", &low);
        SID_AND_ATTRIBUTES lab = new() { Sid = (PSID)low, Attributes = N.SE_GROUP_INTEGRITY };
        if (N.SetTokenInformation(tok, N.TokenIntegrityLevel, &lab, (uint)(sizeof(SID_AND_ATTRIBUTES) + N.GetLengthSid(low))) == 0) throw new Win32Exception(N.LastError, "SetTokenInformation(IntegrityLevel=Low)");
        N.CloseHandle(self);
        return tok;
    }

    public void Dispose()
    {
        _stop = true;
        try { if (!HasExited && Pipe.IsConnected) { Call(Msg.Shutdown, null, 2000); } } catch { }
        if (ProcessHandle != 0 && !WaitExit(1500)) Kill();
        WaitExit(2000);
        try { Pipe.Dispose(); } catch { }
        try { _errPipe?.Dispose(); } catch { }
        if (Shm != null) { N.UnmapViewOfFile(Shm); Shm = null; }
        if (SectionHandle != 0) N.CloseHandle(SectionHandle);
        if (FileHandle != 0) N.CloseHandle(FileHandle);
        if (ThreadHandle != 0) N.CloseHandle(ThreadHandle);
        if (ProcessHandle != 0) N.CloseHandle(ProcessHandle);
        if (Job != 0) N.CloseHandle(Job);               // KILL_ON_JOB_CLOSE: closing the last job handle kills anything still alive
        _jobThread?.Join(300);
        if (IoPort != 0) N.CloseHandle(IoPort);
        ProcessHandle = ThreadHandle = Job = IoPort = FileHandle = SectionHandle = 0;
    }
}
