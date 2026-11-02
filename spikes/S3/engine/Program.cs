using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Quire.S3.Engine;

/// <summary>
/// S3 engine stub (.NET, NOT the production C++ engine). Roles:
///  - I/O thread (Main): reads frames, answers Hello/Ping/Shutdown directly, queues everything else.
///  - Engine thread: the only thread that touches PDFium. Single work queue.
/// </summary>
internal static unsafe class Program
{
    static readonly long TMain = Clock.Now();
    static Stream _pipe = null!;
    static readonly object WriteLock = new();
    static byte* _shm;
    static nint _fileHandleFromSpawn;
    static string _fontMode = "gdi";
    static string _argsRaw = "";
    static readonly BlockingCollection<Work> Queue = new();
    static readonly ManualResetEventSlim EngineReady = new(false);
    static string _pdfiumError = "";
    static double _pdfiumInitMs;
    static long _tPdfiumLoaded, _tPipeConnected;

    record struct Work(ushort Type, uint ReqId, byte[] Payload, long TRecv);

    static int Main(string[] args)
    {
        string? pipeName = null; nint sectionHandle = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe": pipeName = args[++i]; break;
                case "--file": _fileHandleFromSpawn = (nint)long.Parse(args[++i]); break;
                case "--section": sectionHandle = (nint)long.Parse(args[++i]); break;
                case "--fontmode": _fontMode = args[++i]; break;
            }
        }
        _argsRaw = string.Join(' ', args);
        if (pipeName == null) { Console.Error.WriteLine("missing --pipe"); return 2; }

        try
        {
            // Map the shared-memory section inherited through PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
            if (sectionHandle != 0)
            {
                _shm = (byte*)N.MapViewOfFile(sectionHandle, N.FILE_MAP_ALL_ACCESS, 0, 0, 0);
                if (_shm == null) { Console.Error.WriteLine($"MapViewOfFile failed: {N.LastError}"); return 3; }
            }

            var engine = new Thread(EngineThread) { Name = "pdfium-engine", IsBackground = true };
            engine.Start();

            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(10_000);
            _pipe = client;
            _tPipeConnected = Clock.Now();

            Span<byte> hdr = stackalloc byte[Proto.HeaderSize];
            while (true)
            {
                if (!FrameIO.Read(_pipe, hdr, out ushort type, out uint reqId, out byte[] payload)) break;
                long tRecv = Clock.Now();
                switch ((Msg)type)
                {
                    case Msg.Hello:
                        EngineReady.Wait();
                        Respond(Msg.Hello, reqId, BuildHello());
                        break;
                    case Msg.Ping:
                    {
                        var w = new PayloadWriter();
                        w.I64(tRecv).I64(Clock.Now());
                        Respond(Msg.Ping, reqId, w);
                        break;
                    }
                    case Msg.Shutdown:
                        Respond(Msg.Shutdown, reqId, new PayloadWriter());
                        Queue.CompleteAdding();
                        return 0;
                    case Msg.Crash:
                        Marshal.WriteInt32(0, 1); // access violation
                        break;
                    default:
                        Queue.Add(new Work(type, reqId, payload, tRecv));
                        break;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("engine fatal: " + ex);
            return 1;
        }
    }

    static void Respond(Msg type, uint reqId, PayloadWriter w)
    {
        lock (WriteLock)
        {
            FrameIO.Write(_pipe, (ushort)((ushort)type | Proto.ResponseBit), reqId, w.Span);
            _pipe.Flush();
        }
    }

    // ------------------------------------------------------------------ engine thread
    static void* _doc;
    static Pdfium.FileAccess* _fa;
    static nint _docHandle;

    static void EngineThread()
    {
        try
        {
            long t0 = Clock.Now();
            var cfg = new Pdfium.LibraryConfig { version = 2 };
            Pdfium.FPDF_InitLibraryWithConfig(&cfg);
            FontProvider.Install(_fontMode);
            _tPdfiumLoaded = Clock.Now();
            _pdfiumInitMs = Clock.Ms(_tPdfiumLoaded - t0);
        }
        catch (Exception ex) { _pdfiumError = ex.GetType().Name + ": " + ex.Message; }
        finally { EngineReady.Set(); }

        foreach (var w in Queue.GetConsumingEnumerable())
        {
            long tDeq = Clock.Now();
            var r = new PayloadReader(w.Payload);
            var o = new PayloadWriter();
            try
            {
                switch ((Msg)w.Type)
                {
                    case Msg.PingEngine:
                        o.I64(w.TRecv).I64(tDeq).I64(Clock.Now());
                        break;
                    case Msg.OpenDocument: DoOpen(r, o); break;
                    case Msg.CloseDocument: DoClose(); o.I32(0); break;
                    case Msg.RenderTile: DoRender(r, o, w.TRecv, tDeq); break;
                    case Msg.GetCharBoxes: DoCharBoxes(r, o); break;
                    case Msg.RunNegative: DoNegative(r, o); break;
                    case Msg.AllocUntilLimit: DoAlloc(r, o); break;
                    case Msg.GdiProbe: GdiProbe.Run(o); break;
                    case Msg.ApplyRuntimeMitigations: RuntimeMitigations.Apply(r, o); break;
                    default: throw new ProtocolException("unknown message " + w.Type);
                }
            }
            catch (Exception ex)
            {
                o.Reset();
                o.I32(-1).Str(ex.GetType().Name + ": " + ex.Message);
            }
            Respond((Msg)w.Type, w.ReqId, o);
        }
    }

    static void DoOpen(PayloadReader r, PayloadWriter o)
    {
        long handleValue = r.I64();
        if (_doc != null) DoClose();
        nint h = handleValue == 0 ? _fileHandleFromSpawn : (nint)handleValue;
        long t0 = Clock.Now();
        long len = HandleFile.GetSize(h);
        if (len < 0 || len > uint.MaxValue)
        {
            o.I32(-2).I32(0).F32(0).F32(0).I64(t0).I64(Clock.Now()).U32(0).Str(len < 0 ? $"GetFileSizeEx failed {N.LastError}" : "file >= 4 GiB: FPDF_FILEACCESS.m_FileLen is a 32-bit unsigned long on Windows");
            return;
        }
        _fa = HandleFile.Create(h, (uint)len);
        _docHandle = h;
        _doc = Pdfium.FPDF_LoadCustomDocument(_fa, null);
        uint err = _doc == null ? Pdfium.FPDF_GetLastError() : 0;
        int pages = 0; float pw = 0, ph = 0;
        if (_doc != null)
        {
            pages = Pdfium.FPDF_GetPageCount(_doc);
            void* p0 = Pdfium.FPDF_LoadPage(_doc, 0);
            if (p0 != null) { pw = Pdfium.FPDF_GetPageWidthF(p0); ph = Pdfium.FPDF_GetPageHeightF(p0); Pdfium.FPDF_ClosePage(p0); }
        }
        o.I32(_doc != null ? 0 : -3).I32(pages).F32(pw).F32(ph).I64(t0).I64(Clock.Now()).U32(err).Str("");
    }

    static void DoClose()
    {
        if (_doc != null) { Pdfium.FPDF_CloseDocument(_doc); _doc = null; }
        if (_fa != null) { NativeMemory.Free(_fa); _fa = null; }
        if (_docHandle != 0 && _docHandle != _fileHandleFromSpawn) { N.CloseHandle(_docHandle); }
        _docHandle = 0;
    }

    static void DoRender(PayloadReader r, PayloadWriter o, long tRecv, long tDeq)
    {
        int page = r.I32(); float scale = r.F32(); int ox = r.I32(), oy = r.I32(), w = r.I32(), h = r.I32(), slot = r.I32(), flags = r.I32();
        // validate: everything in the request is untrusted from the engine's point of view too (defence in depth)
        if (_doc == null) { o.I32(-10); return; }
        if (slot < 0 || slot >= Shm.SlotCount || w <= 0 || h <= 0 || w > 1024 || h > 1024 || (long)w * h * 4 > Shm.SlotSize || _shm == null) { o.I32(-11); return; }
        long t0 = Clock.Now();
        void* pg = Pdfium.FPDF_LoadPage(_doc, page);
        if (pg == null) { o.I32(-12); return; }
        long tLoaded = Clock.Now();
        float pw = Pdfium.FPDF_GetPageWidthF(pg), ph = Pdfium.FPDF_GetPageHeightF(pg);
        byte* dst = _shm + Shm.SlotOffset(slot);
        void* bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.FPDFBitmap_BGRA, dst, w * 4);   // renders straight into the shared slot: zero copy
        Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        Pdfium.FPDF_RenderPageBitmap(bmp, pg, -ox, -oy, (int)MathF.Round(pw * scale), (int)MathF.Round(ph * scale), 0, flags);
        Pdfium.FPDFBitmap_Destroy(bmp);
        Pdfium.FPDF_ClosePage(pg);
        long t1 = Clock.Now();
        o.I32(0).I32(slot).I64(tRecv).I64(tDeq).I64(t0).I64(tLoaded).I64(t1).I64(Clock.Now()).F32(pw).F32(ph);
    }

    static void DoCharBoxes(PayloadReader r, PayloadWriter o)
    {
        int page = r.I32();
        if (_doc == null) { o.I32(-10); return; }
        void* pg = Pdfium.FPDF_LoadPage(_doc, page);
        void* tp = Pdfium.FPDFText_LoadPage(pg);
        int n = Pdfium.FPDFText_CountChars(tp);
        o.I32(0).I32(n);
        for (int i = 0; i < n; i++)
        {
            double l, rr, b, t;
            Pdfium.FPDFText_GetCharBox(tp, i, &l, &rr, &b, &t);
            o.U32(Pdfium.FPDFText_GetUnicode(tp, i)).F32((float)l).F32((float)t).F32((float)rr).F32((float)b);
        }
        Pdfium.FPDFText_ClosePage(tp);
        Pdfium.FPDF_ClosePage(pg);
    }

    static void DoNegative(PayloadReader r, PayloadWriter o)
    {
        var req = new NegRequest(r.Str(), r.Str(), r.Str(), r.Str(), r.I32(), r.Str(), r.I32(), r.I32(), r.Str(), r.Str(), r.Str(), r.Str());
        var res = NegativeSuite.Run(req);
        o.I32(0).I32(res.Count);
        foreach (var x in res) o.Str(x.Name).Str(x.Expect).Str(x.Outcome).I32(x.Win32).Str(x.Detail);
    }

    static void DoAlloc(PayloadReader r, PayloadWriter o)
    {
        int mode = r.I32(); int chunkMB = r.I32();
        ulong committed = 0; int err = 0; string what = "";
        if (_shm != null) *(ulong*)(_shm + Shm.OffCommittedMB) = 0;
        if (mode == 0)
        {
            while (true)
            {
                byte* p = (byte*)N.VirtualAlloc(null, (nuint)chunkMB << 20, N.MEM_COMMIT | N.MEM_RESERVE, N.PAGE_READWRITE);
                if (p == null) { err = N.LastError; what = "VirtualAlloc failed"; break; }
                for (long i = 0; i < (long)chunkMB << 20; i += 4096) p[i] = 1;   // touch so the pages are really charged
                committed += (ulong)chunkMB;
                if (_shm != null) *(ulong*)(_shm + Shm.OffCommittedMB) = committed;
                if (committed > 3 * 1024) { what = "gave up at 3 GiB (no limit stopped it)"; break; }
            }
        }
        else
        {
            var keep = new List<byte[]>();
            try
            {
                while (true)
                {
                    var b = new byte[chunkMB << 20];
                    for (int i = 0; i < b.Length; i += 4096) b[i] = 1;
                    keep.Add(b);
                    committed += (ulong)chunkMB;
                    if (_shm != null) *(ulong*)(_shm + Shm.OffCommittedMB) = committed;
                    if (committed > 3 * 1024) { what = "gave up at 3 GiB (no limit stopped it)"; break; }
                }
            }
            catch (OutOfMemoryException) { what = "managed OutOfMemoryException"; }
        }
        o.I32(0).U64(committed).I32(err).Str(what);
    }

    static PayloadWriter BuildHello()
    {
        var sb = new StringBuilder();
        void L(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');
        L("pid", Environment.ProcessId.ToString());
        L("clr", Environment.Version.ToString());
        L("arch", RuntimeInformation.ProcessArchitecture.ToString());
        L("args", _argsRaw);
        L("pdfiumError", _pdfiumError);
        L("pdfiumInitMs", _pdfiumInitMs.ToString("F2"));
        L("fontMode", _fontMode);
        L("baseDir", AppContext.BaseDirectory);
        if (Environment.GetEnvironmentVariable("S3_MODULES") == "1") try { L("modules", string.Join(",", System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => m.ModuleName.ToLowerInvariant()))); } catch (Exception ex) { L("modules", "err " + ex.Message); }
        L("USERPROFILE", Environment.GetEnvironmentVariable("USERPROFILE") ?? "");
        L("TEMP", Environment.GetEnvironmentVariable("TEMP") ?? "");
        TokenInfo.Describe(L);
        MitigationInfo.Describe(L);
        var w = new PayloadWriter();
        w.U32(Proto.Version).I64(TMain).I64(_tPdfiumLoaded).I64(_tPipeConnected).I64(Clock.Now()).Str(sb.ToString());
        return w;
    }
}
