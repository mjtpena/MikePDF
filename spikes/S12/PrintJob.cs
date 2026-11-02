using System.Diagnostics;
using System.Runtime.InteropServices;
using S9;

namespace S12;

internal sealed class JobOptions
{
    public string Printer = "Microsoft Print to PDF";
    public string Input = "";
    public string OutPath = "";
    public string DocName = "S12 spike job";
    public string Range = "";
    public string Subset = "all";
    public ScaleMode Mode = ScaleMode.Shrink;
    public double CustomPct = 100;
    public bool AutoRotate = true;
    public short Duplex = 1;            // 1 simplex, 2 long edge, 3 short edge
    public short Copies = 1;
    public short Collate = 0;
    public short Paper = 0;             // 0 = driver default; 1 Letter; 9 A4
    public short Orientation = 0;       // 0 = driver default; 1 portrait; 2 landscape
    public bool AsImage;                // "print as image": one bitmap per page at min(device dpi, ImageDpiCap)
    public int ImageDpiCap = 300;
    public bool Vector;                 // FPDF_RenderPage straight into the printer DC (what the sandbox forbids)
    public long BandBytes = 16L << 20;  // print.md 3.5 default
    public bool WholePage;              // no banding: allocate the full page bitmap (for the memory comparison)
    public int AbortAfterPages = -1;
    public int FlagsExtra;
}

internal sealed class JobResult
{
    public string Input = ""; public string Printer = ""; public string Range = ""; public string Subset = "";
    public int PagesInSource, PagesPrinted; public List<int> PageNumbers = [];
    public string DevModeRequested = "", DevModeAccepted = "", DevModeAtDc = "";
    public int DeviceDpiX, DeviceDpiY, HorzRes, VertRes, PhysW, PhysH, PhysOffX, PhysOffY;
    public double TotalMs, RenderMs, BlitMs, StartDocMs, EndDocMs, AbortMs;
    public long Bands; public long BytesBlitted; public int FailedBlits;
    public long PeakPrivateSelf, PeakWorkingSetSelf, BasePrivateSelf; public Dictionary<string, long> PeakPrivateOthers = [], BasePrivateOthers = [];
    public long OutputBytes; public string? Error; public bool Aborted;
    public List<string> Placements = [];
    public int StartDocResult, EndDocResult;
}

internal sealed class MemSampler : IDisposable
{
    static readonly string[] Others = ["spoolsv", "PrintIsolationHost", "PrintFilterPipelineSvc", "splwow64", "printfilterpipelinesvc", "PrintQueueActionCenter"];
    readonly Thread _t; volatile bool _stop;
    public long PeakPrivate, PeakWs; public readonly Dictionary<string, long> PeakOthers = [];
    public readonly long BasePrivate; public readonly Dictionary<string, long> BaseOthers;
    public MemSampler()
    {
        BasePrivate = Process.GetCurrentProcess().PrivateMemorySize64; BaseOthers = SampleOthers();
        foreach (var kv in BaseOthers) PeakOthers[kv.Key] = kv.Value;
        _t = new Thread(Loop) { IsBackground = true }; _t.Start();
    }
    static Dictionary<string, long> SampleOthers()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Others.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var p in Process.GetProcessesByName(n)) { try { d[n] = d.GetValueOrDefault(n) + p.PrivateMemorySize64; } catch { } finally { p.Dispose(); } }
        return d;
    }
    void Loop()
    {
        var tick = 0; var me = Process.GetCurrentProcess();
        while (!_stop)
        {
            me.Refresh(); PeakPrivate = Math.Max(PeakPrivate, me.PrivateMemorySize64); PeakWs = Math.Max(PeakWs, me.PeakWorkingSet64);
            if (tick++ % 4 == 0) foreach (var kv in SampleOthers()) PeakOthers[kv.Key] = Math.Max(PeakOthers.GetValueOrDefault(kv.Key), kv.Value);
            Thread.Sleep(25);
        }
    }
    public void Dispose() { _stop = true; _t.Join(); }
}

internal static unsafe class PrintJob
{
    static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
    const uint SRCCOPY = 0x00CC0020;

    public static JobResult Run(JobOptions o)
    {
        var r = new JobResult { Input = Path.GetFileName(o.Input), Printer = o.Printer, Range = o.Range, Subset = o.Subset };
        Pdfium.Init();
        using var doc = PdfDoc.FromFile(o.Input); r.PagesInSource = doc.PageCount;
        var (pages, err) = PrintLayout.ParseRange(o.Range, doc.PageCount, o.Subset);
        if (pages == null) { r.Error = "range: " + err; return r; }
        r.PageNumbers = pages;

        var dm = Spooler.Default(o.Printer);
        if (o.Copies != 1) dm.Apply(Native.DM_COPIES, Native.O_Copies, o.Copies);
        if (o.Duplex != 1) dm.Apply(Native.DM_DUPLEX, Native.O_Duplex, o.Duplex);
        if (o.Collate != 0) dm.Apply(Native.DM_COLLATE, Native.O_Collate, o.Collate);
        if (o.Paper != 0) dm.Apply(Native.DM_PAPERSIZE, Native.O_PaperSize, o.Paper);
        if (o.Orientation != 0) dm.Apply(Native.DM_ORIENTATION, Native.O_Orientation, o.Orientation);
        r.DevModeRequested = dm.Describe();
        var accepted = Spooler.Validate(o.Printer, dm); r.DevModeAccepted = accepted.Describe();

        var dmPtr = Marshal.AllocHGlobal(accepted.Bytes.Length); Marshal.Copy(accepted.Bytes, 0, dmPtr, accepted.Bytes.Length);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var mem = new MemSampler(); var total = Stopwatch.GetTimestamp();
        var hdc = Native.CreateDCW("WINSPOOL", o.Printer, null, dmPtr);
        if (hdc == 0) { r.Error = "CreateDC failed " + Marshal.GetLastWin32Error(); return r; }
        try
        {
            r.HorzRes = Native.GetDeviceCaps(hdc, Native.HORZRES); r.VertRes = Native.GetDeviceCaps(hdc, Native.VERTRES);
            r.DeviceDpiX = Native.GetDeviceCaps(hdc, Native.LOGPIXELSX); r.DeviceDpiY = Native.GetDeviceCaps(hdc, Native.LOGPIXELSY);
            r.PhysW = Native.GetDeviceCaps(hdc, Native.PHYSICALWIDTH); r.PhysH = Native.GetDeviceCaps(hdc, Native.PHYSICALHEIGHT);
            r.PhysOffX = Native.GetDeviceCaps(hdc, Native.PHYSICALOFFSETX); r.PhysOffY = Native.GetDeviceCaps(hdc, Native.PHYSICALOFFSETY);
            var di = new Native.DOCINFO { cbSize = Marshal.SizeOf<Native.DOCINFO>(), lpszDocName = o.DocName, lpszOutput = o.OutPath, lpszDatatype = null };
            var t0 = Stopwatch.GetTimestamp(); r.StartDocResult = Native.StartDocW(hdc, ref di); r.StartDocMs = (Stopwatch.GetTimestamp() - t0) * ToMs;
            if (r.StartDocResult <= 0) { r.Error = "StartDoc failed " + Marshal.GetLastWin32Error(); return r; }

            var done = 0;
            foreach (var pno in pages)
            {
                var page = Pdfium.FPDF_LoadPage(doc.Handle, pno - 1);
                double pw = Pdfium.FPDF_GetPageWidthF(page), ph = Pdfium.FPDF_GetPageHeightF(page);
                var dpi = (double)r.DeviceDpiX;
                var cap = o.AsImage ? Math.Min(dpi, o.ImageDpiCap) : dpi;
                var pl = PrintLayout.Place(pw, ph, r.HorzRes, r.VertRes, dpi, o.Mode, o.CustomPct, o.AutoRotate);
                int Xi = (int)Math.Round(pl.X), Yi = (int)Math.Round(pl.Y), Wi = Math.Max(1, (int)Math.Round(pl.W)), Hi = Math.Max(1, (int)Math.Round(pl.H));
                r.Placements.Add($"p{pno}: {pw:F1}x{ph:F1}pt rot90={pl.Rotate90} scale={pl.Scale:F4}px/pt dest=({Xi},{Yi},{Wi},{Hi}) clipped={pl.Clipped}");
                Native.StartPage(hdc);
                var rot = pl.Rotate90 ? 1 : 0; var flags = Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING | o.FlagsExtra;
                if (o.Vector)
                {
                    var tv = Stopwatch.GetTimestamp(); Native.FPDF_RenderPage(hdc, page, Xi, Yi, Wi, Hi, rot, flags); r.RenderMs += (Stopwatch.GetTimestamp() - tv) * ToMs;
                }
                else if (o.AsImage)
                {
                    var iw = Math.Max(1, (int)Math.Round(Wi * cap / dpi)); var ih = Math.Max(1, (int)Math.Round(Hi * cap / dpi));
                    var tr = Stopwatch.GetTimestamp();
                    var bmp = Pdfium.FPDFBitmap_Create(iw, ih, 0); Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, iw, ih, 0xFFFFFFFF);
                    Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, iw, ih, rot, flags); r.RenderMs += (Stopwatch.GetTimestamp() - tr) * ToMs;
                    var tb = Stopwatch.GetTimestamp(); var bi = Header(iw, ih);
                    Native.SetStretchBltMode(hdc, 4 /*HALFTONE*/);
                    if (Native.StretchDIBits(hdc, Xi, Yi, Wi, Hi, 0, 0, iw, ih, Pdfium.FPDFBitmap_GetBuffer(bmp), &bi, 0, SRCCOPY) <= 0) r.FailedBlits++;
                    r.BlitMs += (Stopwatch.GetTimestamp() - tb) * ToMs; r.Bands++; r.BytesBlitted += (long)iw * ih * 4; Pdfium.FPDFBitmap_Destroy(bmp);
                }
                else Bands(hdc, page, rot, flags, Xi, Yi, Wi, Hi, r.HorzRes, r.VertRes, o, r);
                Native.EndPage(hdc); Pdfium.FPDF_ClosePage(page); done++;
                if (o.AbortAfterPages > 0 && done >= o.AbortAfterPages)
                {
                    var ta = Stopwatch.GetTimestamp(); Native.AbortDoc(hdc); r.AbortMs = (Stopwatch.GetTimestamp() - ta) * ToMs; r.Aborted = true; r.PagesPrinted = done; return r;
                }
            }
            var te = Stopwatch.GetTimestamp(); r.EndDocResult = Native.EndDoc(hdc); r.EndDocMs = (Stopwatch.GetTimestamp() - te) * ToMs; r.PagesPrinted = done;
        }
        finally
        {
            Native.DeleteDC(hdc); Marshal.FreeHGlobal(dmPtr);
            r.TotalMs = (Stopwatch.GetTimestamp() - total) * ToMs;
        }
        mem.Dispose();
        r.PeakPrivateSelf = mem.PeakPrivate; r.PeakWorkingSetSelf = mem.PeakWs; r.BasePrivateSelf = mem.BasePrivate; r.PeakPrivateOthers = mem.PeakOthers; r.BasePrivateOthers = mem.BaseOthers;
        if (File.Exists(o.OutPath)) r.OutputBytes = new FileInfo(o.OutPath).Length;
        return r;
    }

    static Native.BITMAPINFOHEADER Header(int w, int h) => new() { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };

    /// <summary>print.md 3.5: bands of bandBytes / (4 * width) rows; render band k+1 on a worker while band k is sent. Two buffers.</summary>
    static void Bands(nint hdc, nint page, int rot, int flags, int Xi, int Yi, int Wi, int Hi, int horz, int vert, JobOptions o, JobResult r)
    {
        int x0 = Math.Max(0, Xi), x1 = Math.Min(horz, Xi + Wi), y0 = Math.Max(0, Yi), y1 = Math.Min(vert, Yi + Hi);
        var vw = x1 - x0; if (vw <= 0 || y1 <= y0) return;
        var bandH = o.WholePage ? (y1 - y0) : (int)Math.Max(1, Math.Min(y1 - y0, o.BandBytes / (4L * vw)));
        var count = (y1 - y0 + bandH - 1) / bandH;
        var bufs = new nint[2]; var tops = new int[2]; var hs = new int[2];
        for (var i = 0; i < 2 && i < count; i++) bufs[i] = Pdfium.FPDFBitmap_Create(vw, bandH, 0);
        var free = new SemaphoreSlim(Math.Min(2, count), 2); var ready = new SemaphoreSlim(0, 2);
        double renderMs = 0;
        var worker = Task.Run(() =>
        {
            for (var k = 0; k < count; k++)
            {
                free.Wait(); var s = k % 2; var top = y0 + k * bandH; var h = Math.Min(bandH, y1 - top);
                var t = Stopwatch.GetTimestamp();
                Pdfium.FPDFBitmap_FillRect(bufs[s], 0, 0, vw, bandH, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bufs[s], page, Xi - x0, Yi - top, Wi, Hi, rot, flags);
                renderMs += (Stopwatch.GetTimestamp() - t) * ToMs; tops[s] = top; hs[s] = h; ready.Release();
            }
        });
        for (var k = 0; k < count; k++)
        {
            ready.Wait(); var s = k % 2; var bi = Header(vw, hs[s]);
            var t = Stopwatch.GetTimestamp();
            // The bitmap was created with stride = 4 * vw and holds bandH rows; send only the rows of this band.
            if (Native.StretchDIBits(hdc, x0, tops[s], vw, hs[s], 0, 0, vw, hs[s], Pdfium.FPDFBitmap_GetBuffer(bufs[s]), &bi, 0, SRCCOPY) <= 0) r.FailedBlits++;
            r.BlitMs += (Stopwatch.GetTimestamp() - t) * ToMs; r.BytesBlitted += (long)vw * hs[s] * 4; r.Bands++;
            free.Release();
        }
        worker.Wait(); r.RenderMs += renderMs;
        for (var i = 0; i < 2; i++) if (bufs[i] != 0) Pdfium.FPDFBitmap_Destroy(bufs[i]);
    }
}
