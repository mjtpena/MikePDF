// S6b prototype ShellHost (THROWAWAY). A per-user broker for shell handlers: a named pipe, a copy of the document the handler hands over as a
// section, a fresh sandboxed engine (MikePDF.Broker, unchanged) per request, and the pixels back in a section the handler created.
// The wire format is the spike's own (s6b_proto.h); the product would use the engine framing (MessagePack) and the messages in the S6b report.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker;
using MikePDF.Protocol;

namespace S6b;

internal static class Wire
{
    public const uint Magic = 0x31423653; // 'S6B1'
    public const int ReqSize = 72;
    public const int RepSize = 72;
    public const uint OpHello = 1, OpOpen = 2, OpRender = 3, OpClose = 4, OpShutdown = 9;
    public const uint FlagInline = 1;
    public const uint StOk = 0, StBadRequest = 101, StHandle = 102, StBusy = 103, StTimeout = 104, StInternal = 105;
}

internal sealed class Args
{
    public string PipeName = "mikepdf-s6b-shell";
    public string? EngineDir;
    public int IdleSeconds = 60;
    public int MaxEngines = 3;
    public string Mode = "fresh"; // fresh | spare | reuse
    public bool AllowLowIl = true;
    public string? Log;
    public long MemLimitMb = 256;
    public int SlotKb = 1024;
    public bool FileCopyViaSection = true;

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (var i = 0; i < argv.Length; i++)
        {
            string Next() => argv[++i];
            switch (argv[i])
            {
                case "--pipe": a.PipeName = Next(); break;
                case "--engine-dir": a.EngineDir = Next(); break;
                case "--idle-seconds": a.IdleSeconds = int.Parse(Next()); break;
                case "--max-engines": a.MaxEngines = int.Parse(Next()); break;
                case "--mode": a.Mode = Next(); break;
                case "--no-low-il": a.AllowLowIl = false; break;
                case "--log": a.Log = Next(); break;
                case "--mem-mb": a.MemLimitMb = long.Parse(Next()); break;
                case "--slot-kb": a.SlotKb = int.Parse(Next()); break;
            }
        }

        return a;
    }
}

internal static class Log
{
    private static string? _path;
    private static readonly object Gate = new();
    public static void Init(string? path) => _path = path;

    public static void Write(string message)
    {
        if (_path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [shellhost {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}

internal static unsafe partial class Native
{
    public const uint PROCESS_DUP_HANDLE = 0x0040, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint DUPLICATE_SAME_ACCESS = 2, FILE_MAP_READ = 4, FILE_MAP_WRITE = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint srcProcess, nint srcHandle, nint dstProcess, out nint dstHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void* MapViewOfFile(nint section, uint access, uint hi, uint lo, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(void* p);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQuery(void* p, out MEMORY_BASIC_INFORMATION mbi, nuint len);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(nint pipe, out uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuf, uint inBuf, uint defaultTimeout, SECURITY_ATTRIBUTES* sa);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSddl(string sddl, uint revision, out nint sd, nint size);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint p);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetCurrentPackageFullName(ref uint length, char* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetPackageFamilyName(nint process, ref uint length, char* name);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect, PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    public static string? PackageFullName()
    {
        uint len = 0;
        if (GetCurrentPackageFullName(ref len, null) != 122) // ERROR_INSUFFICIENT_BUFFER
        {
            return null;
        }

        var buf = new char[len];
        fixed (char* p = buf)
        {
            return GetCurrentPackageFullName(ref len, p) == 0 ? new string(p) : null;
        }
    }

    public static string? PackageFamilyOf(nint process)
    {
        uint len = 0;
        if (GetPackageFamilyName(process, ref len, null) != 122)
        {
            return null;
        }

        var buf = new char[len];
        fixed (char* p = buf)
        {
            return GetPackageFamilyName(process, ref len, p) == 0 ? new string(p, 0, (int)len - 1) : null;
        }
    }

    public static string? ImagePathOf(nint process)
    {
        var buf = new char[1024];
        uint size = (uint)buf.Length;
        fixed (char* p = buf)
        {
            return QueryFullProcessImageName(process, 0, p, ref size) ? new string(p, 0, (int)size) : null;
        }
    }
}

/// <summary>Engines for requests. fresh: one per request, killed after it. spare: one is always started ahead, used once. reuse: one shared.</summary>
internal sealed class EnginePool(Args args, AppContainerProfile profile, string enginePath)
{
    private readonly SemaphoreSlim _limit = new(args.MaxEngines);
    private readonly object _gate = new();
    private Task<EngineSupervisor>? _spare;
    private Task<EngineSupervisor>? _shared;

    public int Live;
    public int PeakLive;

    private Task<EngineSupervisor> Start() => EngineSupervisor.StartAsync(new EngineSupervisorOptions
    {
        EnginePath = enginePath,
        Profile = profile,
        Policy = new SandboxPolicy { ProcessMemoryLimitBytes = args.MemLimitMb << 20 },
        Kind = EngineKind.Pool,
        SlotCount = 2,
        SlotSize = args.SlotKb << 10,
        StartupTimeout = TimeSpan.FromSeconds(10),
        DefaultRequestDeadline = TimeSpan.FromSeconds(5),
    });

    public async Task<(EngineSupervisor Engine, bool Reused)> AcquireAsync(CancellationToken ct)
    {
        await _limit.WaitAsync(ct).ConfigureAwait(false);
        var live = Interlocked.Increment(ref Live);
        InterlockedMax(ref PeakLive, live);
        try
        {
            switch (args.Mode)
            {
                case "reuse":
                    Task<EngineSupervisor> shared;
                    lock (_gate)
                    {
                        shared = _shared ??= Start();
                    }

                    return (await shared.ConfigureAwait(false), true);
                case "spare":
                    Task<EngineSupervisor> spare;
                    lock (_gate)
                    {
                        spare = _spare ?? Start();
                        _spare = Start(); // the next request's engine starts now, in the background
                    }

                    return (await spare.ConfigureAwait(false), true);
                default:
                    return (await Start().ConfigureAwait(false), false);
            }
        }
        catch
        {
            Interlocked.Decrement(ref Live);
            _limit.Release();
            throw;
        }
    }

    public async Task ReleaseAsync(EngineSupervisor engine)
    {
        if (args.Mode != "reuse")
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        Interlocked.Decrement(ref Live);
        _limit.Release();
    }

    public void Prewarm()
    {
        if (args.Mode == "spare")
        {
            lock (_gate)
            {
                _spare ??= Start();
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}

internal sealed class Session : IAsyncDisposable
{
    public EngineSupervisor? Engine;
    public SupervisedDocument? Document;
    public string? TempFile;
    public EnginePool? Pool;
    public float PageW, PageH;
    public uint PageCount;
    public long EngineSpawnUs, OpenUs;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Document is not null)
            {
                await Document.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Log.Write("close document: " + e.Message);
        }

        if (Engine is not null && Pool is not null)
        {
            await Pool.ReleaseAsync(Engine).ConfigureAwait(false);
        }

        if (TempFile is not null)
        {
            try
            {
                File.Delete(TempFile);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal static class Program
{
    private static long _lastActivity = Stopwatch.GetTimestamp();
    private static int _active;
    private static readonly CancellationTokenSource Stop = new();
    private static string? _tempDir;

    private static long Us(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;

    [STAThread]
    private static async Task<int> Main(string[] argv)
    {
        var started = Stopwatch.GetTimestamp();
        var args = Args.Parse(argv);
        Log.Init(args.Log);
        var package = Native.PackageFullName();
        Log.Write($"start pipe={args.PipeName} mode={args.Mode} package={package ?? "(none)"} maxEngines={args.MaxEngines} mem={args.MemLimitMb}MB args={string.Join(' ', argv)}");

        using var mutex = new Mutex(true, @"Local\MikePDF-S6b-ShellHost-" + args.PipeName, out var first);
        if (!first)
        {
            Log.Write("another ShellHost owns the mutex; exiting");
            return 0;
        }

        var profile = AppContainerProfile.CreateOrOpen("MikePDF.S6bShell", "S6b shell host", "S6b spike");
        string enginePath;
        string? deployed = null;
        if (package is not null)
        {
            // Packaged: in place (ADR-0004 A2).
            enginePath = Path.Combine(AppContext.BaseDirectory, "..", "Engine", "MikePDF.Engine.exe");
            enginePath = Path.GetFullPath(enginePath);
        }
        else
        {
            var src = args.EngineDir ?? throw new ArgumentException("--engine-dir is required when unpackaged");
            // A persistent private copy (unpackaged development only): the packaged build runs in place, so copying on every start would overstate cold start.
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(src, "MikePDF.Engine.exe")).Ticks.ToString("x") + File.GetLastWriteTimeUtc(Path.Combine(src, "pdfium.dll")).Ticks.ToString("x");
            deployed = Path.Combine(Path.GetTempPath(), "s6b-engine-persist-" + stamp);
            if (!File.Exists(Path.Combine(deployed, "MikePDF.Engine.exe")))
            {
                Directory.CreateDirectory(deployed);
                EngineDeployment.EnsureAppContainerAccess(deployed, profile);
                foreach (var f in new[] { "MikePDF.Engine.exe", "pdfium.dll" })
                {
                    File.Copy(Path.Combine(src, f), Path.Combine(deployed, f), true);
                }
            }

            enginePath = Path.Combine(deployed, "MikePDF.Engine.exe");
            deployed = null; // kept between runs
        }

        _tempDir = Path.Combine(Path.GetTempPath(), "s6b-docs");
        Directory.CreateDirectory(_tempDir);
        var pool = new EnginePool(args, profile, enginePath);
        pool.Prewarm();

        _ = Task.Run(async () =>
        {
            while (!Stop.IsCancellationRequested)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (Volatile.Read(ref _active) == 0 && Us(Stopwatch.GetTimestamp() - Volatile.Read(ref _lastActivity)) > args.IdleSeconds * 1_000_000L)
                {
                    Log.Write("idle; exiting");
                    Stop.Cancel();
                }
            }
        });

        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var sddl = $"D:P(A;;GA;;;{userSid})(A;;GA;;;SY)" + (args.AllowLowIl ? "S:(ML;;NW;;;LW)" : string.Empty);
        Log.Write($"ready after {Us(Stopwatch.GetTimestamp() - started)} us; sddl={sddl}");
        var firstInstance = true;
        try
        {
            while (!Stop.IsCancellationRequested)
            {
                var pipe = CreatePipe(args.PipeName, sddl, firstInstance);
                firstInstance = false;
                try
                {
                    await pipe.WaitForConnectionAsync(Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    break;
                }

                _ = Task.Run(() => ServeAsync(pipe, args, pool), Stop.Token);
            }
        }
        finally
        {
            Log.Write($"exit; peakLiveEngines={pool.PeakLive}");
            if (deployed is not null)
            {
                try
                {
                    EngineDeployment.RevokeAppContainerAccess(deployed, profile.Sid);
                    Directory.Delete(deployed, true);
                }
                catch (Exception e)
                {
                    Log.Write("cleanup: " + e.Message);
                }
            }
        }

        return 0;
    }

    private static unsafe NamedPipeServerStreamAdapter CreatePipe(string name, string sddl, bool first)
    {
        if (!Native.ConvertSddl(sddl, 1, out var sd, 0))
        {
            throw new InvalidOperationException("SDDL: " + Marshal.GetLastWin32Error());
        }

        try
        {
            var sa = new Native.SECURITY_ATTRIBUTES { nLength = (uint)sizeof(Native.SECURITY_ATTRIBUTES), lpSecurityDescriptor = sd };
            // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED (| FIRST_PIPE_INSTANCE); BYTE | READMODE_BYTE | WAIT | REJECT_REMOTE_CLIENTS
            var mode = 0x00000003u | 0x40000000u | (first ? 0x00080000u : 0u);
            var h = Native.CreateNamedPipe(@"\\.\pipe\" + name, mode, 0x0u | 0x0u | 0x0u | 0x8u, 255, 1 << 20, 1 << 20, 0, &sa);
            if (h == -1 || h == 0)
            {
                throw new InvalidOperationException("CreateNamedPipe: " + Marshal.GetLastWin32Error());
            }

            return new NamedPipeServerStreamAdapter(new SafePipeHandle(h, true));
        }
        finally
        {
            Native.LocalFree(sd);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStreamAdapter pipe, Args args, EnginePool pool)
    {
        Interlocked.Increment(ref _active);
        Session? session = null;
        var req = new byte[Wire.ReqSize];
        try
        {
            Native.GetNamedPipeClientProcessId(pipe.Handle, out var clientPid);
            var clientProcess = Native.OpenProcess(Native.PROCESS_DUP_HANDLE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, clientPid);
            var dupErr = clientProcess == 0 ? Marshal.GetLastWin32Error() : 0;
            if (clientProcess == 0)
            {
                // A low-integrity client cannot be opened for DUP_HANDLE by... nothing: we are the higher-IL side. A failure here is logged, not guessed at.
                clientProcess = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, clientPid);
            }

            var clientImage = clientProcess == 0 ? "?" : Native.ImagePathOf(clientProcess);
            var clientFamily = clientProcess == 0 ? null : Native.PackageFamilyOf(clientProcess);
            Log.Write($"client pid={clientPid} image={clientImage} packageFamily={clientFamily ?? "(none)"} openForDup={(dupErr == 0 ? "ok" : "error " + dupErr)}");
            var canDup = dupErr == 0;

            while (await pipe.ReadExactlyAsync(req, Stop.Token).ConfigureAwait(false))
            {
                Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
                var t0 = Stopwatch.GetTimestamp();
                var rep = new byte[Wire.RepSize];
                var inline = Array.Empty<byte>();
                WriteU32(rep, 0, Wire.Magic);
                WriteU32(rep, 10 * 4, (uint)Environment.ProcessId);
                var op = ReadU32(req, 4);
                try
                {
                    if (ReadU32(req, 0) != Wire.Magic)
                    {
                        WriteU32(rep, 4, Wire.StBadRequest);
                    }
                    else if (op == Wire.OpHello)
                    {
                        WriteU32(rep, 4, Wire.StOk);
                        WriteU32(rep, 7 * 4, canDup ? 1u : 0u);
                    }
                    else if (op == Wire.OpShutdown)
                    {
                        WriteU32(rep, 4, Wire.StOk);
                        await pipe.WriteAsync(rep).ConfigureAwait(false);
                        Stop.Cancel();
                        return;
                    }
                    else if (op == Wire.OpOpen)
                    {
                        if (session is not null)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                        }

                        session = new Session { Pool = pool };
                        var st = await OpenAsync(req, rep, session, clientPid, clientProcess, canDup, args, pool).ConfigureAwait(false);
                        WriteU32(rep, 4, st);
                        if (st != Wire.StOk)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            session = null;
                        }
                    }
                    else if (op == Wire.OpRender)
                    {
                        var (st, bytesInline) = await RenderAsync(req, rep, session, clientProcess, canDup).ConfigureAwait(false);
                        inline = bytesInline;
                        WriteU32(rep, 4, st);
                    }
                    else if (op == Wire.OpClose)
                    {
                        if (session is not null)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            session = null;
                        }

                        WriteU32(rep, 4, Wire.StOk);
                    }
                    else
                    {
                        WriteU32(rep, 4, Wire.StBadRequest);
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"op {op} failed: {e.GetType().Name}: {e.Message}");
                    WriteU32(rep, 4, Wire.StInternal);
                }

                // total server time of this op goes in slot 17
                WriteU32(rep, 17 * 4, (uint)Us(Stopwatch.GetTimestamp() - t0));
                await pipe.WriteAsync(rep).ConfigureAwait(false);
                if (inline.Length > 0)
                {
                    await pipe.WriteAsync(inline).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // client went away: the session ends with the connection
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            pipe.Dispose();
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _active);
        }
    }

    private static unsafe nint DupFromClient(nint clientProcess, ulong remote)
    {
        if (!Native.DuplicateHandle(clientProcess, (nint)remote, Native.GetCurrentProcess(), out var local, 0, false, Native.DUPLICATE_SAME_ACCESS))
        {
            return 0;
        }

        return local;
    }

    private static async Task<uint> OpenAsync(byte[] req, byte[] rep, Session s, uint clientPid, nint clientProcess, bool canDup, Args args, EnginePool pool)
    {
        var tStage = Stopwatch.GetTimestamp();
        var srcSection = ReadU64(req, 32 + 8);
        var srcBytes = ReadU64(req, 32 + 16);
        var pid = ReadU64(req, 32);
        if (pid != clientPid || !canDup || srcSection == 0 || srcBytes == 0 || srcBytes > (256UL << 20))
        {
            return Wire.StBadRequest;
        }

        var local = DupFromClient(clientProcess, srcSection);
        if (local == 0)
        {
            return Wire.StHandle;
        }

        s.TempFile = Path.Combine(_tempDir!, Guid.NewGuid().ToString("N") + ".pdf");
        unsafe
        {
            var view = Native.MapViewOfFile(local, Native.FILE_MAP_READ, 0, 0, 0);
            Native.CloseHandle(local);
            if (view is null)
            {
                return Wire.StHandle;
            }

            try
            {
                Native.VirtualQuery(view, out var mbi, (nuint)sizeof(Native.MEMORY_BASIC_INFORMATION));
                if ((ulong)mbi.RegionSize < srcBytes)
                {
                    return Wire.StBadRequest; // the section is smaller than the client said
                }

                // The unchanged engine wants a disk-file handle (MappedFile::from_handle), so the bytes go to a file the supervisor opens by path.
                // The product would give the engine the section itself (see the report: ShellOpen.source).
                using var fs = new FileStream(s.TempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None);
                fs.Write(new ReadOnlySpan<byte>(view, checked((int)srcBytes)));
            }
            finally
            {
                Native.UnmapViewOfFile(view);
            }
        }

        var tSpawn = Stopwatch.GetTimestamp();
        var (engine, _) = await pool.AcquireAsync(Stop.Token).ConfigureAwait(false);
        s.Engine = engine;
        var tOpen = Stopwatch.GetTimestamp();
        var opened = await engine.OpenDocumentAsync(s.TempFile, null, Stop.Token).ConfigureAwait(false);
        if (!opened.IsOk || opened.Value is null)
        {
            return 200 + (uint)opened.Status;
        }

        s.Document = opened.Value;
        s.PageCount = opened.Value.PageCount;
        var sizes = await opened.Value.GetPageSizesAsync(0, 1).ConfigureAwait(false);
        if (!sizes.IsOk || sizes.Value is null || sizes.Value.Widths.Length == 0)
        {
            return 200 + (uint)sizes.Status;
        }

        s.PageW = sizes.Value.Widths[0];
        s.PageH = sizes.Value.Heights[0];
        var tDone = Stopwatch.GetTimestamp();
        WriteU32(rep, 5 * 4, s.PageCount);
        WriteU32(rep, 6 * 4, 1);
        WriteF32(rep, 8 * 4, s.PageW);
        WriteF32(rep, 9 * 4, s.PageH);
        WriteU32(rep, 11 * 4, (uint)Us(tSpawn - tStage));   // stage: section -> file
        WriteU32(rep, 12 * 4, (uint)Us(tOpen - tSpawn));    // engine acquire (spawn + Hello + ring)
        WriteU32(rep, 13 * 4, (uint)Us(tDone - tOpen));     // OpenDocument + page sizes
        Log.Write($"session open: engine pid {engine.EngineProcessId}, pages {s.PageCount}, first page {s.PageW}x{s.PageH}");
        return Wire.StOk;
    }

    private static async Task<(uint Status, byte[] Inline)> RenderAsync(byte[] req, byte[] rep, Session? s, nint clientProcess, bool canDup)
    {
        byte[] inline = [];
        if (s?.Document is null)
        {
            return (Wire.StBadRequest, inline);
        }

        var cx = ReadU32(req, 16);
        var cy = ReadU32(req, 20);
        var page = ReadU32(req, 24);
        var flags = ReadU32(req, 12);
        if (cx is < 16 or > 1024 || cy is < 16 or > 1024 || page >= s.PageCount)
        {
            return (Wire.StBadRequest, inline);
        }

        float pw = s.PageW, ph = s.PageH;
        if (page != 0)
        {
            var sizes = await s.Document.GetPageSizesAsync(page, 1).ConfigureAwait(false);
            if (!sizes.IsOk || sizes.Value is null || sizes.Value.Widths.Length == 0)
            {
                return (200 + (uint)sizes.Status, inline);
            }

            pw = sizes.Value.Widths[0];
            ph = sizes.Value.Heights[0];
        }

        // Fit inside cx x cy, never more than 2x natural size (spec 3.3).
        var scale = MathF.Min(MathF.Min(cx / pw, cy / ph), 2f);
        var w = (int)Math.Max(1, Math.Round(pw * scale));
        var h = (int)Math.Max(1, Math.Round(ph * scale));
        var t0 = Stopwatch.GetTimestamp();
        var rendered = await s.Document.RenderTileAsync(new RenderTileArgs(page, scale, 0, 0, 0, (uint)w, (uint)h, RenderTileArgs.Annotations), 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var t1 = Stopwatch.GetTimestamp();
        if (!rendered.IsOk || rendered.Value is null)
        {
            return (200 + (uint)rendered.Status, inline);
        }

        using var tile = rendered.Value;
        var stride = tile.Stride;
        var bytes = stride * tile.Height;
        if (tile.Width != w || tile.Height != h || stride != 4 * w)
        {
            return (Wire.StInternal, inline);
        }

        if ((flags & Wire.FlagInline) != 0)
        {
            inline = tile.Pixels.ToArray();
        }
        else
        {
            var dst = ReadU64(req, 32 + 24);
            var dstBytes = ReadU64(req, 32 + 32);
            if (!canDup || dst == 0 || dstBytes < (ulong)bytes)
            {
                return (Wire.StBadRequest, inline);
            }

            var local = DupFromClient(clientProcess, dst);
            if (local == 0)
            {
                return (Wire.StHandle, inline);
            }

            unsafe
            {
                var view = Native.MapViewOfFile(local, Native.FILE_MAP_WRITE, 0, 0, (nuint)bytes);
                Native.CloseHandle(local);
                if (view is null)
                {
                    return (Wire.StHandle, inline);
                }

                try
                {
                    tile.Pixels.CopyTo(new Span<byte>(view, bytes));
                }
                finally
                {
                    Native.UnmapViewOfFile(view);
                }
            }
        }

        var t2 = Stopwatch.GetTimestamp();
        WriteU32(rep, 2 * 4, (uint)w);
        WriteU32(rep, 3 * 4, (uint)h);
        WriteU32(rep, 4 * 4, (uint)stride);
        WriteU32(rep, 5 * 4, s.PageCount);
        WriteU32(rep, 14 * 4, (uint)Us(t1 - t0)); // render through the engine
        WriteU32(rep, 15 * 4, (uint)Us(t2 - t1)); // copy to the client's section (or inline buffer)
        var engineKb = Environment.GetEnvironmentVariable("S6B_PEAK") == "1" ? EnginePeakKb(s.Engine!.EngineProcessId) : 0u;
        WriteU32(rep, 16 * 4, engineKb);
        return (Wire.StOk, inline);
    }

    private static uint EnginePeakKb(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return (uint)(p.PeakWorkingSet64 / 1024);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static uint ReadU32(byte[] b, int o) => BitConverter.ToUInt32(b, o);

    private static ulong ReadU64(byte[] b, int o) => BitConverter.ToUInt64(b, o);

    private static void WriteU32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static void WriteF32(byte[] b, int o, float v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);
}

/// <summary>A server pipe over a handle created natively, so the SDDL (with a low-integrity label) is exactly what was asked for.</summary>
internal sealed class NamedPipeServerStreamAdapter : IDisposable
{
    private readonly System.IO.Pipes.NamedPipeServerStream _stream;

    public NamedPipeServerStreamAdapter(SafePipeHandle handle)
    {
        _stream = new System.IO.Pipes.NamedPipeServerStream(System.IO.Pipes.PipeDirection.InOut, true, false, handle);
    }

    public nint Handle => _stream.SafePipeHandle.DangerousGetHandle();

    public Task WaitForConnectionAsync(CancellationToken ct) => _stream.WaitForConnectionAsync(ct);

    public async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data) => _stream.WriteAsync(data);

    public void Dispose() => _stream.Dispose();
}
