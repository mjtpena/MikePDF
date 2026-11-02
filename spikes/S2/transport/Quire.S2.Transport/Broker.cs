using System.Collections.Concurrent;
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

namespace Quire.S2;

public enum Isolation { None, AppContainer }

public enum Msg : ushort { Hello = 1, Ping = 2, OpenDocument = 4, CloseDocument = 5, RenderTile = 6, Shutdown = 7, GetPageSizes = 20, CancelBefore = 21, SetOptions = 22 }

/// <summary>AppContainer profile lifetime (copied in spirit from spikes/S3/launcher/Sandbox.cs). Zero capabilities.</summary>
public static unsafe class AppContainerProfile
{
    public const string Name = "Quire.S2.Probe";
    static void* _sid;
    public static string SidString { get; private set; } = "";
    public static bool CreatedByUs { get; private set; }

    public static void* EnsureCreated()
    {
        if (_sid != null) return _sid;
        void* sid;
        int hr = N.CreateAppContainerProfile(Name, "Quire S2 spike probe", "Quire M0 spike S2 (zero-capability AppContainer)", null, 0, &sid);
        if (hr == unchecked((int)0x800700B7)) { hr = N.DeriveAppContainerSidFromAppContainerName(Name, &sid); if (hr != 0) throw new Win32Exception(hr, "Derive"); }
        else if (hr != 0) throw new Win32Exception(hr, "CreateAppContainerProfile hr=0x" + hr.ToString("X8"));
        else CreatedByUs = true;
        _sid = sid; SidString = N.SidToString(sid);
        return sid;
    }

    public static int Delete()
    {
        int hr = N.DeleteAppContainerProfile(Name);
        if (_sid != null) { N.FreeSid(_sid); _sid = null; }
        return hr;
    }

    /// <summary>Grant the container read+execute on a directory inside the repo (never Program Files).</summary>
    public static void GrantReadExecute(string dir)
    {
        var sid = new SecurityIdentifier(SidString);
        var di = new DirectoryInfo(dir);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }
}

public readonly record struct RenderDone(uint ReqId, int Status, int Slot, long TSubmit, long TEngRecv, long TEngDeq, long TLoaded, long TRendered, long TEngSend, long TUiRecv, long Tag);

/// <summary>
/// Broker side of the S2 transport: spawns the C++ engine (AppContainer by default), owns the pipe + the shared tile section, and multiplexes
/// concurrent RenderTile requests (reader thread dispatches completions by request id).
/// </summary>
public sealed unsafe class EngineClient : IDisposable
{
    public readonly Isolation Mode;
    public uint Pid;
    public byte* Shm_;
    public NamedPipeServerStream Pipe = null!;
    nint _proc, _thread, _job, _section;
    AnonymousPipeServerStream? _errPipe;
    readonly StringBuilder _stderr = new();
    uint _nextReq = 1;
    readonly object _writeLock = new();
    readonly ConcurrentDictionary<uint, (Action<RenderDone>? cb, TaskCompletionSource<PayloadReader>? tcs, long tSubmit, long tag)> _pending = new();
    Thread? _reader;
    volatile bool _stop;
    readonly ConcurrentStack<int> _freeSlots = new();
    public int SlotCount => Quire.S2.Shm.SlotCount;
    public long TSpawn, TConnected, THello;
    public double PdfiumInitMs;
    public int PendingCount => _pending.Count;
    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuf, uint inBuf, uint defTimeout, SECURITY_ATTRIBUTES* sa);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, void** sd, uint* size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern int GetNamedPipeClientProcessId(nint pipe, uint* pid);

    EngineClient(Isolation mode) { Mode = mode; }

    public bool HasExited { get { uint c; return _proc == 0 || (N.GetExitCodeProcess(_proc, &c) != 0 && c != 259); } }

    public static EngineClient Start(string exePath, Isolation mode, int pageCache, int timeoutMs = 15000)
    {
        var c = new EngineClient(mode);
        c.StartInternal(exePath, pageCache, timeoutMs);
        return c;
    }

    void StartInternal(string exe, int pageCache, int timeoutMs)
    {
        bool ac = Mode == Isolation.AppContainer;
        void* acSid = null;
        if (ac) { acSid = AppContainerProfile.EnsureCreated(); AppContainerProfile.GrantReadExecute(Path.GetDirectoryName(exe)!); }
        string pipeName = "quire-s2-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string sddl = $"D:(A;;FRFW;;;{WindowsIdentity.GetCurrent().User!.Value})";
        if (ac) sddl += $"(A;;FRFW;;;{AppContainerProfile.SidString})";
        void* psd;
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, &psd, null) == 0) throw new Win32Exception(N.LastError, "ConvertStringSecurityDescriptor");
        SECURITY_ATTRIBUTES psa = default; psa.nLength = (uint)sizeof(SECURITY_ATTRIBUTES); psa.lpSecurityDescriptor = psd;
        // PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED (concurrent reader thread + writers on one handle);  byte mode, reject remote
        nint hPipe = CreateNamedPipeW(@"\\.\pipe\" + pipeName, 0x3 | 0x00080000 | 0x40000000, 0x8, 1, 1 << 16, 1 << 16, 0, &psa);
        int pipeErr = N.LastError; N.LocalFree((nint)psd);
        if (hPipe == -1) throw new Win32Exception(pipeErr, "CreateNamedPipe");
        Pipe = new NamedPipeServerStream(PipeDirection.InOut, true, true, new Microsoft.Win32.SafeHandles.SafePipeHandle(hPipe, true));

        SECURITY_ATTRIBUTES sa = default; sa.nLength = (uint)sizeof(SECURITY_ATTRIBUTES); sa.bInheritHandle = true;
        var inherit = new List<nint>();
        _section = N.CreateFileMappingW(-1, &sa, N.PAGE_READWRITE, (uint)(Quire.S2.Shm.TotalSize >> 32), (uint)Quire.S2.Shm.TotalSize, null);
        if (_section == 0) throw new Win32Exception(N.LastError, "CreateFileMapping");
        inherit.Add(_section);
        Shm_ = (byte*)N.MapViewOfFile(_section, N.FILE_MAP_ALL_ACCESS, 0, 0, 0);
        if (Shm_ == null) throw new Win32Exception(N.LastError, "MapViewOfFile");
        _errPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint errClient = _errPipe.ClientSafePipeHandle.DangerousGetHandle(); inherit.Add(errClient);

        _job = N.CreateJobObjectW(null, null);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jl = default; jl.BasicLimitInformation.LimitFlags = (JOB_OBJECT_LIMIT)N.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (N.SetInformationJobObject(_job, N.JobObjectExtendedLimitInformation, &jl, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0) throw new Win32Exception(N.LastError, "SetInformationJobObject");

        int nAttr = 1 + (ac ? 1 : 0) + 1;
        nuint asz = 0; N.InitializeProcThreadAttributeList(null, (uint)nAttr, 0, &asz);
        void* attrs = NativeMemory.AllocZeroed(asz);
        if (N.InitializeProcThreadAttributeList(attrs, (uint)nAttr, 0, &asz) == 0) throw new Win32Exception(N.LastError, "InitializeProcThreadAttributeList");
        nint* handles = (nint*)NativeMemory.Alloc((nuint)(inherit.Count * sizeof(nint)));
        for (int i = 0; i < inherit.Count; i++) handles[i] = inherit[i];
        Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (nuint)(inherit.Count * sizeof(nint)), null, null), "HANDLE_LIST");
        SECURITY_CAPABILITIES* sc = (SECURITY_CAPABILITIES*)NativeMemory.AllocZeroed((nuint)sizeof(SECURITY_CAPABILITIES));
        if (ac) { sc->AppContainerSid = (PSID)acSid; sc->Capabilities = null; sc->CapabilityCount = 0; Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, sc, (nuint)sizeof(SECURITY_CAPABILITIES), null, null), "SECURITY_CAPABILITIES"); }
        nint* jobList = (nint*)NativeMemory.AllocZeroed(8); jobList[0] = _job;
        Check(N.UpdateProcThreadAttribute(attrs, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, jobList, 8, null, null), "JOB_LIST");

        string cmdline = $"\"{exe}\" --pipe {pipeName} --section {_section} --slots {Quire.S2.Shm.SlotCount} --pagecache {pageCache}";
        STARTUPINFOEXW si = default; si.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW); si.lpAttributeList = *(LPPROC_THREAD_ATTRIBUTE_LIST*)&attrs;
        si.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES; si.StartupInfo.hStdError = (HANDLE)errClient; si.StartupInfo.hStdOutput = (HANDLE)errClient;
        PROCESS_INFORMATION pi;
        uint flags = N.EXTENDED_STARTUPINFO_PRESENT | 0x8u /*DETACHED_PROCESS: see S3 report, no conhost*/;
        TSpawn = Clock.Now();
        int ok;
        fixed (char* cp = cmdline + "\0") ok = N.CreateProcessW(null, cp, null, null, 1, flags, null, Path.GetDirectoryName(exe), &si, &pi);
        int createErr = N.LastError;
        N.DeleteProcThreadAttributeList(attrs);
        NativeMemory.Free(attrs); NativeMemory.Free(handles); NativeMemory.Free(sc); NativeMemory.Free(jobList);
        _errPipe.DisposeLocalCopyOfClientHandle();
        if (ok == 0) throw new InvalidOperationException($"CreateProcess failed: Win32 {createErr} ({new Win32Exception(createErr).Message})");
        _proc = pi.hProcess; _thread = pi.hThread; Pid = pi.dwProcessId;
        new Thread(() => { try { var buf = new byte[4096]; int n; while ((n = _errPipe.Read(buf)) > 0) lock (_stderr) _stderr.Append(Encoding.UTF8.GetString(buf, 0, n)); } catch { } }) { IsBackground = true }.Start();

        var wait = Pipe.WaitForConnectionAsync();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!wait.Wait(5))
        {
            if (HasExited) { uint ec; N.GetExitCodeProcess(_proc, &ec); throw new InvalidOperationException($"engine exited before connecting: 0x{ec:X8}; stderr: {StdErr}"); }
            if (Environment.TickCount64 > deadline) { Kill(); throw new TimeoutException("engine did not connect"); }
        }
        TConnected = Clock.Now();
        uint clientPid;
        if (GetNamedPipeClientProcessId(Pipe.SafePipeHandle.DangerousGetHandle(), &clientPid) == 0 || clientPid != Pid) { Kill(); throw new InvalidOperationException("pipe client is not the engine we spawned"); }

        for (int i = Quire.S2.Shm.SlotCount - 1; i >= 0; i--) _freeSlots.Push(i);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "engine-reader" };
        _reader.Start();
        var hello = Call(Msg.Hello, new PayloadWriter().U32(Proto.Version), timeoutMs);
        THello = Clock.Now();
        hello.U32(); hello.I64(); PdfiumInitMs = Clock.Ms(hello.I64());
    }

    static void Check(int ok, string what) { if (ok == 0) throw new Win32Exception(N.LastError, what); }

    // ------------------------------------------------------------------------------------------------ slots
    public int AcquireSlot() => _freeSlots.TryPop(out int s) ? s : -1;
    public void ReleaseSlot(int s) => _freeSlots.Push(s);
    public int FreeSlots => _freeSlots.Count;
    public byte* SlotPtr(int slot) => Shm_ + Quire.S2.Shm.SlotOffset(slot);

    // ------------------------------------------------------------------------------------------------ RPC
    void SendFrame(Msg type, uint id, ReadOnlySpan<byte> payload)
    {
        lock (_writeLock) { FrameIO.Write(Pipe, (ushort)type, id, payload); Pipe.Flush(); }
    }

    public PayloadReader Call(Msg type, PayloadWriter? payload, int timeoutMs = 30000)
    {
        uint id = Interlocked.Increment(ref _nextReq);
        var tcs = new TaskCompletionSource<PayloadReader>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (null, tcs, Clock.Now(), 0);
        SendFrame(type, id, payload == null ? ReadOnlySpan<byte>.Empty : payload.Span);
        if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException($"{type} timed out");
        return tcs.Task.Result;
    }

    /// <summary>Submit a tile request; <paramref name="onDone"/> runs on the reader thread (keep it cheap).</summary>
    public uint SubmitRender(int page, float scale, int ox, int oy, int w, int h, int slot, int flags, uint gen, long tag, Action<RenderDone> onDone)
    {
        uint id = Interlocked.Increment(ref _nextReq);
        long t = Clock.Now();
        _pending[id] = (onDone, null, t, tag);
        Span<byte> p = stackalloc byte[36];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p, page);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(p[4..], scale);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[8..], ox);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[12..], oy);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[16..], w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[20..], h);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[24..], slot);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(p[28..], flags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p[32..], gen);
        SendFrame(Msg.RenderTile, id, p);
        return id;
    }

    /// <summary>Fire-and-forget: the engine drops queued requests older than gen and aborts the in-flight render.</summary>
    public void CancelBefore(uint gen)
    {
        uint id = Interlocked.Increment(ref _nextReq);
        _pending[id] = (null, null, Clock.Now(), 0);
        Span<byte> p = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p, gen);
        SendFrame(Msg.CancelBefore, id, p);
    }

    void ReadLoop()
    {
        var hdr = new byte[Proto.HeaderSize];
        try
        {
            while (!_stop)
            {
                if (!FrameIO.Read(Pipe, hdr, out ushort type, out uint id, out byte[] body)) break;
                long now = Clock.Now();
                if (!_pending.TryRemove(id, out var p)) continue;
                if (p.tcs != null) { p.tcs.SetResult(new PayloadReader(body)); continue; }
                if (p.cb == null) continue;
                var r = new PayloadReader(body);
                int st = r.I32(), slot = r.I32();
                long a = 0, b = 0, c = 0, d = 0, e = 0;
                if (r.Remaining >= 40) { a = r.I64(); b = r.I64(); c = r.I64(); d = r.I64(); e = r.I64(); }
                p.cb(new RenderDone(id, st, slot, p.tSubmit, a, b, c, d, e, now, p.tag));
            }
        }
        catch (Exception) when (_stop) { }
        catch (Exception ex) { lock (_stderr) _stderr.Append("[reader] " + ex.Message); }
        foreach (var kv in _pending) { if (_pending.TryRemove(kv.Key, out var p)) p.tcs?.TrySetException(new EndOfStreamException("engine closed the pipe")); }
    }

    // ------------------------------------------------------------------------------------------------ convenience
    public (int pages, double ms) Open(string path)
    {
        nint local = N.CreateFileW(path, N.GENERIC_READ, 1, null, N.OPEN_EXISTING, N.FILE_ATTRIBUTE_NORMAL, 0);
        if (local == -1) throw new Win32Exception(N.LastError, "open " + path);
        nint remote;
        if (N.DuplicateHandle(N.GetCurrentProcess(), local, _proc, &remote, 0, 0, 2) == 0) { int e = N.LastError; N.CloseHandle(local); throw new Win32Exception(e, "DuplicateHandle"); }
        N.CloseHandle(local);
        long t0 = Clock.Now();
        var r = Call(Msg.OpenDocument, new PayloadWriter().I64(remote));
        double ms = Clock.Ms(Clock.Now() - t0);
        int st = r.I32(), pages = r.I32();
        if (st != 0) throw new InvalidOperationException("OpenDocument status " + st);
        return (pages, ms);
    }

    public (float w, float h)[] PageSizes()
    {
        var r = Call(Msg.GetPageSizes, null);
        int n = r.I32(); var a = new (float, float)[n];
        for (int i = 0; i < n; i++) a[i] = (r.F32(), r.F32());
        return a;
    }

    public void SetOptions(int pageCache, bool progressive) => Call(Msg.SetOptions, new PayloadWriter().I32(pageCache).I32(progressive ? 1 : 0));

    public double PingMs()
    {
        long t0 = Clock.Now(); Call(Msg.Ping, null); return Clock.Ms(Clock.Now() - t0);
    }

    public void Kill() { if (_proc != 0) N.TerminateProcess(_proc, 0xDEAD); }

    public void Dispose()
    {
        _stop = true;
        try { if (!HasExited && Pipe.IsConnected) Call(Msg.Shutdown, null, 2000); } catch { }
        if (_proc != 0 && N.WaitForSingleObject(_proc, 1500) != 0) Kill();
        N.WaitForSingleObject(_proc, 2000);
        try { Pipe.Dispose(); } catch { }
        try { _errPipe?.Dispose(); } catch { }
        if (Shm_ != null) { N.UnmapViewOfFile(Shm_); Shm_ = null; }
        if (_section != 0) N.CloseHandle(_section);
        if (_thread != 0) N.CloseHandle(_thread);
        if (_proc != 0) N.CloseHandle(_proc);
        if (_job != 0) N.CloseHandle(_job);
        _proc = _thread = _job = _section = 0;
    }
}
