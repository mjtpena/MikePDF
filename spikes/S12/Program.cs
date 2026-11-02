using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using S9;

namespace S12;

internal static unsafe class Program
{
    public static string Out = "";
    const string PDF_PRINTER = "Microsoft Print to PDF";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("pdfium.dll")] static extern nint FPDFText_LoadPage(nint page);
    [DllImport("pdfium.dll")] static extern int FPDFText_CountChars(nint tp);
    [DllImport("pdfium.dll")] static extern void FPDFText_ClosePage(nint tp);
    [DllImport("pdfium.dll")] static extern int FPDFPage_CountObjects(nint page);
    [DllImport("pdfium.dll")] static extern nint FPDFPage_GetObject(nint page, int i);
    [DllImport("pdfium.dll")] static extern int FPDFPageObj_GetType(nint obj);

    public static int Main(string[] args)
    {
        Out = Path.GetFullPath(Environment.GetEnvironmentVariable("S12_OUT") ?? Path.Combine(AppContext.BaseDirectory, "out"));
        Directory.CreateDirectory(Out); Directory.CreateDirectory(Path.Combine(Out, "fixtures"));
        var cmd = args.Length > 0 ? args[0] : "info";
        try
        {
            switch (cmd)
            {
                case "info": Info(); break;
                case "layout": LayoutTests(); break;
                case "jobs": Jobs(); break;
                case "vector": VectorCompare(); break;
                case "perf": Perf(args.Skip(1).ToArray()); break;
                case "cancel": Cancel(); break;
                case "xps": { var f = Path.Combine(Out, "xps-probe.xps"); P(XpsProbe.Run(f)); Thread.Sleep(1500); P($"output stream file after cancel: exists={File.Exists(f)}" + (File.Exists(f) ? $" ({new FileInfo(f).Length} bytes)" : "")); if (File.Exists(f)) File.Delete(f); Save("s12-xps-probe.txt"); break; }
                case "peek": Peek(); break;
                case "perfone": PerfOne(args[1]); break;
                case "acbisect": AcBisect(args[1]); break;
                case "ac": AppContainerProbe(args.Skip(1).ToArray()); break;
                default: Console.Error.WriteLine("unknown " + cmd); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        return 0;
    }

    static StringBuilder Log = new();
    static void P(string s) { Console.WriteLine(s); Log.AppendLine(s); }
    static void Save(string name) { File.WriteAllText(Path.Combine(Out, name), Log.ToString()); Log = new(); }
    static string Fx(string name) => Path.Combine(Out, "fixtures", name);
    static void Json(string name, object o) => File.WriteAllText(Path.Combine(Out, name), JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

    static void EnsureFixtures()
    {
        if (!File.Exists(Fx("mix6.pdf"))) File.WriteAllBytes(Fx("mix6.pdf"), Fixtures.Mix6());
        if (!File.Exists(Fx("text100.pdf"))) File.WriteAllBytes(Fx("text100.pdf"), Fixtures.Text(100));
    }

    // ------------------------------------------------------------------ info
    static void Info()
    {
        P("## Environment");
        P($"OS {Environment.OSVersion}  64-bit process {Environment.Is64BitProcess}  cores {Environment.ProcessorCount}");
        P("## Printers (EnumPrinters level 4, read only)");
        foreach (var n in Spooler.EnumPrinters()) P("  " + n);
        P("## Candidate (a): Windows.Graphics.Printing.PrintManager.IsSupported() from an unpackaged console process (no window, no UI)");
        try { P("  IsSupported() = " + Windows.Graphics.Printing.PrintManager.IsSupported()); } catch (Exception ex) { P("  threw " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message); }
        try
        {
            // A window that is created but never shown (no WS_VISIBLE, no ShowWindow): enough for PrintManagerInterop.GetForWindow, which shows nothing.
            var hwnd = CreateWindowExW(0, "STATIC", "S12 hidden", unchecked((int)0x80000000) /*WS_POPUP*/, 0, 0, 0, 0, 0, 0, 0, 0);
            P($"  hidden HWND created: {hwnd != 0}, visible: {IsWindowVisible(hwnd)}");
            var pm = Windows.Graphics.Printing.PrintManagerInterop.GetForWindow(hwnd);
            P("  PrintManagerInterop.GetForWindow(hwnd) returned " + (pm != null ? "a PrintManager (no UI shown; ShowPrintUIForWindowAsync deliberately NOT called)" : "null"));
            DestroyWindow(hwnd);
        }
        catch (Exception ex) { P("  PrintManagerInterop.GetForWindow threw " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message); }
        P($"## {PDF_PRINTER}: DeviceCapabilities");
        P(Spooler.Capabilities(PDF_PRINTER));
        var dm = Spooler.Default(PDF_PRINTER); P("default DEVMODE: " + dm.Describe());
        var req = Spooler.Default(PDF_PRINTER); req.Apply(Native.DM_DUPLEX, Native.O_Duplex, 2); req.Apply(Native.DM_COPIES, Native.O_Copies, 3); req.Apply(Native.DM_COLLATE, Native.O_Collate, 1); req.Apply(Native.DM_PAPERSIZE, Native.O_PaperSize, 9);
        P("requested : " + req.Describe()); P("driver kept: " + Spooler.Validate(PDF_PRINTER, req).Describe());
        P("## PrintTicket capabilities (prntvpt PTGetPrintCapabilities) - features only");
        var xml = Spooler.PrintCapabilitiesXml(PDF_PRINTER);
        File.WriteAllText(Path.Combine(Out, "s12-print-capabilities.xml"), xml);
        foreach (var f in System.Text.RegularExpressions.Regex.Matches(xml, "<(?:psf:)?Feature name=\"(psk:[A-Za-z]+)\"").Select(m => m.Groups[1].Value).Distinct()) P("  feature " + f);
        P($"  (full XML: {xml.Length} chars in s12-print-capabilities.xml)");
        Save("s12-info.txt");
    }

    // ------------------------------------------------------------------ pure-function tests
    static void LayoutTests()
    {
        P("## Range grammar (print.md 3.3 / PRINT-01, PRINT-02) on a 10-page document");
        foreach (var t in new[] { "1", "1-3", "5-", "-5", "1-3,5,8-", " 2 , 4 ", "", "a", "3-1", "0", "-", "1,,2", "11", "1-11" })
        {
            var (p, e) = PrintLayout.ParseRange(t, 10);
            P($"  \"{t}\" -> {(p == null ? "ERROR " + e : string.Join(",", p))}");
        }
        foreach (var sub in new[] { "odd", "even" }) { var (p, _) = PrintLayout.ParseRange("2-9", 10, sub); P($"  \"2-9\" {sub} -> {string.Join(",", p!)}"); }
        P("## PrintLayout (A4 portrait paper at 600 dpi: 4960 x 7016 px printable)");
        foreach (var (w, h, name) in new[] { (612.0, 792.0, "Letter"), (792.0, 612.0, "Letter landscape"), (595.28, 841.89, "A4"), (2160.0, 2880.0, "30x40in poster"), (72.0, 72.0, "1 inch") })
            foreach (var mode in new[] { ScaleMode.Fit, ScaleMode.Shrink, ScaleMode.Actual })
            {
                var pl = PrintLayout.Place(w, h, 4960, 7016, 600, mode, 100, true);
                P($"  {name,-17} {mode,-7} rot90={pl.Rotate90,-5} scale={pl.Scale:F3} dest=({pl.X:F0},{pl.Y:F0},{pl.W:F0},{pl.H:F0}) clipped={pl.Clipped}");
            }
        Save("s12-layout-tests.txt");
    }

    // ------------------------------------------------------------------ fidelity check of an output PDF
    sealed record PageCheck(int OutPage, int SourcePage, double WidthPt, double HeightPt, double Ssim, double MeanAbs, int Chars, int TextObjects, int ImageObjects, int PathObjects, double Ssim600Boxed, double MeanAbs600, int MaxAbs600, double PctDiffer600);

    static Bgra Box(Bgra a, int f)
    {
        var w = a.W / f; var h = a.H / f; var r = new Bgra(w, h);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) for (var c = 0; c < 4; c++)
        { var s = 0; for (var dy = 0; dy < f; dy++) for (var dx = 0; dx < f; dx++) s += a.Px[((y * f + dy) * a.W + x * f + dx) * 4 + c]; r.Px[(y * w + x) * 4 + c] = (byte)((s + f * f / 2) / (f * f)); }
        return r;
    }

    /// <summary>Renders source page <paramref name="srcPage"/> with the job's placement into a white canvas of the output page's pixel size at <paramref name="dpi"/>.</summary>
    static Bgra Expected(PdfDoc src, int srcPage, JobOptions o, JobResult r, int w, int h, double dpi)
    {
        var sp = Pdfium.FPDF_LoadPage(src.Handle, srcPage - 1); double pw = Pdfium.FPDF_GetPageWidthF(sp), ph = Pdfium.FPDF_GetPageHeightF(sp);
        var pl = PrintLayout.Place(pw, ph, r.HorzRes, r.VertRes, r.DeviceDpiX, o.Mode, o.CustomPct, o.AutoRotate); var ratio = dpi / r.DeviceDpiX;
        var bmp = Pdfium.FPDFBitmap_Create(w, h, 0); Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        Pdfium.FPDF_RenderPageBitmap(bmp, sp, (int)Math.Round((r.PhysOffX + Math.Round(pl.X)) * ratio), (int)Math.Round((r.PhysOffY + Math.Round(pl.Y)) * ratio), (int)Math.Round(Math.Round(pl.W) * ratio), (int)Math.Round(Math.Round(pl.H) * ratio), pl.Rotate90 ? 1 : 0, Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING);
        var e = new Bgra(w, h); var stride = Pdfium.FPDFBitmap_GetStride(bmp); var sb = (byte*)Pdfium.FPDFBitmap_GetBuffer(bmp);
        for (var y = 0; y < h; y++) new ReadOnlySpan<byte>(sb + (long)y * stride, w * 4).CopyTo(e.Px.AsSpan(y * w * 4, w * 4));
        Pdfium.FPDFBitmap_Destroy(bmp); Pdfium.FPDF_ClosePage(sp); return Imaging.Opaque(e);
    }

    static List<PageCheck> Verify(JobOptions o, JobResult r, bool savePng, int maxPages = 6, int hiResPages = 3)
    {
        var res = new List<PageCheck>();
        using var outDoc = PdfDoc.FromFile(o.OutPath); using var src = PdfDoc.FromFile(o.Input);
        P($"  output: {outDoc.PageCount} pages (job reported {r.PagesPrinted}; copies requested {o.Copies}, collate {o.Collate}), {r.OutputBytes / 1024.0:F0} KiB");
        for (var i = 0; i < outDoc.PageCount && i < maxPages; i++)
        {
            var srcPage = r.PageNumbers[i % r.PageNumbers.Count];   // collated copies: 1,3,5,1,3,5
            var op = Pdfium.FPDF_LoadPage(outDoc.Handle, i); double ow = Pdfium.FPDF_GetPageWidthF(op), oh = Pdfium.FPDF_GetPageHeightF(op);
            var actual = PdfRender.Page(op, 150.0 / 72, Pdfium.FPDF_ANNOT);
            int chars = 0; var tp = FPDFText_LoadPage(op); if (tp != 0) { chars = FPDFText_CountChars(tp); FPDFText_ClosePage(tp); }
            int nText = 0, nImg = 0, nPath = 0; for (var k = 0; k < FPDFPage_CountObjects(op); k++) { var t = FPDFPageObj_GetType(FPDFPage_GetObject(op, k)); if (t == 1) nText++; else if (t == 3) nImg++; else if (t == 2) nPath++; }
            var expected = Expected(src, srcPage, o, r, actual.W, actual.H, 150.0);
            Imaging.Opaque(actual);
            var ssim = Imaging.Ssim(expected, actual); var d = Imaging.Diff(expected, actual);
            double s600 = double.NaN, m600 = double.NaN, pct600 = double.NaN; int x600 = -1;
            if (i < hiResPages)
            {
                // Same placement at the printer's own resolution (1 device pixel = 1 image pixel), then the same 4x box filter on both sides.
                var a6 = Imaging.Opaque(PdfRender.Page(op, r.DeviceDpiX / 72.0, Pdfium.FPDF_ANNOT));
                var e6 = Expected(src, srcPage, o, r, a6.W, a6.H, r.DeviceDpiX);
                var d6 = Imaging.Diff(e6, a6); m600 = d6.MeanAbs; x600 = d6.MaxAbs; pct600 = 100.0 * d6.Differing / (a6.W * (double)a6.H);
                s600 = Imaging.Ssim(Box(e6, 4), Box(a6, 4));
                // Control 1: the method's own floor. Engine at 600 dpi box-filtered to 150 dpi versus the engine at 150 dpi direct: no printing involved.
                var ctl = Imaging.Ssim(Box(e6, 4), expected);
                // Control 2: is the output the engine's pixels shifted by whole pixels? Try +-2 px on a 1200 x 1200 crop from the middle.
                double best = double.MaxValue; string bestAt = "";
                for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
                {
                    var c = Imaging.Crop(e6, e6.W / 2 - 600, e6.H / 2 - 600, 1200, 1200); var q = Imaging.Crop(a6, e6.W / 2 - 600 + dx, e6.H / 2 - 600 + dy, 1200, 1200);
                    var m = Imaging.Diff(c, q).MeanAbs; if (m < best) { best = m; bestAt = $"({dx},{dy})"; }
                }
                P(string.Create(CultureInfo.InvariantCulture, $"    control: 150 dpi direct vs 600 dpi box-filtered engine render (no printing) SSIM {ctl:F4}; best whole-pixel shift of the output {bestAt} gives mean abs {best:F3} on a 1200x1200 centre crop"));
            }
            Pdfium.FPDF_ClosePage(op);
            res.Add(new PageCheck(i + 1, srcPage, ow, oh, ssim, d.MeanAbs, chars, nText, nImg, nPath, s600, m600, x600, pct600));
            P(string.Create(CultureInfo.InvariantCulture, $"  out p{i + 1} <- src p{srcPage}: {ow:F1} x {oh:F1} pt, SSIM {ssim:F4} @150 dpi (print.md PRINT-13 method), mean abs {d.MeanAbs:F2}; at 600 dpi: mean abs {m600:F3}, max {x600}, {pct600:F2}% pixels differ, SSIM after 4x box {s600:F4}; text chars {chars}, objects text/image/path {nText}/{nImg}/{nPath}"));
            if (savePng) { Imaging.SavePng(Path.Combine(Out, $"{Path.GetFileNameWithoutExtension(o.OutPath)}-p{i + 1}-expected.png"), expected); Imaging.SavePng(Path.Combine(Out, $"{Path.GetFileNameWithoutExtension(o.OutPath)}-p{i + 1}-actual.png"), actual); }
        }
        return res;
    }

    static JobResult RunAndVerify(string title, JobOptions o, bool savePng = false, int maxPages = 6, int hi = 3)
    {
        P($"### {title}");
        if (File.Exists(o.OutPath)) File.Delete(o.OutPath);
        var r = PrintJob.Run(o);
        P($"  printer {r.Printer}; source pages {r.PagesInSource}; range \"{o.Range}\" subset {o.Subset} -> pages {string.Join(",", r.PageNumbers)}; printed {r.PagesPrinted}");
        P($"  DEVMODE requested: {r.DevModeRequested}"); P($"  DEVMODE driver kept: {r.DevModeAccepted}");
        P($"  device: {r.DeviceDpiX}x{r.DeviceDpiY} dpi, printable {r.HorzRes}x{r.VertRes} px, physical {r.PhysW}x{r.PhysH}, offset {r.PhysOffX},{r.PhysOffY}");
        foreach (var pl in r.Placements) P("  " + pl);
        P(string.Create(CultureInfo.InvariantCulture, $"  time total {r.TotalMs:F0} ms (StartDoc {r.StartDocMs:F0}, render {r.RenderMs:F0}, blit {r.BlitMs:F0}, EndDoc {r.EndDocMs:F0}); bands {r.Bands}; sent {r.BytesBlitted / 1048576.0:F0} MiB; failed blits {r.FailedBlits}; error {r.Error ?? "none"}"));
        P($"  own process private bytes: base {r.BasePrivateSelf / 1048576.0:F0} MiB peak {r.PeakPrivateSelf / 1048576.0:F0} MiB (peak working set {r.PeakWorkingSetSelf / 1048576.0:F0} MiB)");
        foreach (var kv in r.PeakPrivateOthers) P($"  {kv.Key}: base {r.BasePrivateOthers.GetValueOrDefault(kv.Key) / 1048576.0:F0} MiB peak {kv.Value / 1048576.0:F0} MiB");
        if (r.Error == null && File.Exists(o.OutPath) && !r.Aborted) Verify(o, r, savePng, maxPages, hi);
        return r;
    }

    // ------------------------------------------------------------------ the three-page jobs
    static void Jobs()
    {
        EnsureFixtures(); var all = new List<object>();
        string O(string n) => Path.Combine(Out, n);
        // A: range 2-4 of the 6-page mixed document, fit to page, duplex long edge, A4
        var a = RunAndVerify("Job A: range 2-4, Fit, duplex long edge, A4, 1 copy", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("jobA.pdf"), Range = "2-4", Mode = ScaleMode.Fit, Duplex = 2, Paper = 9 }, true);
        // B: pages 1,3,5- odd only, actual size, Letter, 2 copies collated
        var b = RunAndVerify("Job B: range 1,3,5- odd only, Actual size, Letter, short-edge duplex, 2 copies collated", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("jobB.pdf"), Range = "1,3,5-", Subset = "odd", Mode = ScaleMode.Actual, Paper = 1, Duplex = 3, Copies = 2, Collate = 1 });
        // C: all, custom 50%, auto-rotate off
        var c = RunAndVerify("Job C: pages 1-3, Custom 50%, auto-rotate off, A4", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("jobC.pdf"), Range = "1-3", Mode = ScaleMode.Custom, CustomPct = 50, AutoRotate = false, Paper = 9 });
        // D: print as image
        var d = RunAndVerify("Job D: pages 2-4, Fit, A4, print as image at <= 300 dpi", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("jobD.pdf"), Range = "2-4", Mode = ScaleMode.Fit, Paper = 9, AsImage = true }, false, 2, 0);
        // E: mixed orientation: Letter paper, landscape page 3 auto-rotated
        var e = RunAndVerify("Job E: pages 1-3, Shrink, Letter paper portrait, auto-rotate on (page 3 is landscape)", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("jobE.pdf"), Range = "1-3", Mode = ScaleMode.Shrink, Paper = 1, Orientation = 1 }, true);
        // F: per-page paper size change? (print.md says unsupported in v1): not attempted via ResetDC here; see report
        all.AddRange([a, b, c, d, e]); Json("s12-jobs.json", all); Save("s12-jobs.txt");
        foreach (var n in new[] { "jobA.pdf", "jobB.pdf", "jobC.pdf", "jobD.pdf", "jobE.pdf" }) File.Delete(O(n));
    }

    // ------------------------------------------------------------------ vector path vs raster path
    static void VectorCompare()
    {
        EnsureFixtures(); string O(string n) => Path.Combine(Out, n);
        var v = RunAndVerify("Vector: FPDF_RenderPage into the printer DC (unsandboxed, for comparison only), pages 2-4, Fit, A4", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("vec.pdf"), Range = "2-4", Mode = ScaleMode.Fit, Paper = 9, Vector = true }, true);
        var r = RunAndVerify("Raster bands (same pages, same options)", new JobOptions { Input = Fx("mix6.pdf"), OutPath = O("ras.pdf"), Range = "2-4", Mode = ScaleMode.Fit, Paper = 9 }, false, 2, 0);
        Json("s12-vector-vs-raster.json", new { vector = v, raster = r }); Save("s12-vector.txt");
        File.Delete(O("vec.pdf")); File.Delete(O("ras.pdf"));
    }

    // ------------------------------------------------------------------ cost of N pages
    static void Perf(string[] args)
    {
        EnsureFixtures(); string O(string n) => Path.Combine(Out, n);
        var which = args.Length > 0 ? args[0] : "all"; var results = new Dictionary<string, JobResult>();
        // Memory shape: whole-page bitmaps against 16 MiB bands, 10 pages at the printer's native dpi.
        if (which is "all" or "mem")
        {
            results["10 pages whole-page bitmap"] = RunAndVerify("10 pages, whole-page bitmap (no banding)", new JobOptions { Input = Fx("text100.pdf"), OutPath = O("p10w.pdf"), Range = "1-10", Mode = ScaleMode.Fit, Paper = 9, WholePage = true }, false, 2, 0);
            results["10 pages 16 MiB bands"] = RunAndVerify("10 pages, 16 MiB bands", new JobOptions { Input = Fx("text100.pdf"), OutPath = O("p10b.pdf"), Range = "1-10", Mode = ScaleMode.Fit, Paper = 9 }, false, 2, 0);
            results["10 pages 4 MiB bands"] = RunAndVerify("10 pages, 4 MiB bands", new JobOptions { Input = Fx("text100.pdf"), OutPath = O("p10c.pdf"), Range = "1-10", Mode = ScaleMode.Fit, Paper = 9, BandBytes = 4L << 20 }, false, 2, 0);
        }
        if (which is "all" or "100")
        {
            results["100 pages 16 MiB bands"] = RunAndVerify("100 pages, 16 MiB bands, Fit, A4", new JobOptions { Input = Fx("text100.pdf"), OutPath = O("p100.pdf"), Mode = ScaleMode.Fit, Paper = 9 }, false, 2, 0);
            results["100 pages as image"] = RunAndVerify("100 pages, print as image (<= 300 dpi), Fit, A4", new JobOptions { Input = Fx("text100.pdf"), OutPath = O("p100i.pdf"), Mode = ScaleMode.Fit, Paper = 9, AsImage = true }, false, 2, 0);
        }
        Json($"s12-perf-{which}.json", results); Save($"s12-perf-{which}.txt");
        foreach (var f in Directory.GetFiles(Out, "p10*.pdf")) File.Delete(f);
    }

    // ------------------------------------------------------------------ candidate (c): is a printer DC reachable from the engine's sandbox?
    static void AppContainerProbe(string[] args)
    {
        // args: root folder for the probe (the run script has granted the container modify rights on <root>\\probe only), profile name
        var root = Path.GetFullPath(args[0]); var profile = args.Length > 1 ? args[1] : "mikepdf.render";
        var dir = Path.Combine(root, "probe"); var denied = Path.Combine(root, "denied"); Directory.CreateDirectory(denied);
        P($"profile {profile}, SID {AppContainerLaunch.SidOf(profile)}; folder the container may write: {dir}; folder it may not: {denied}");
        var managed = Path.Combine(dir, "S12Probe.exe"); var native = Path.Combine(dir, "S12ProbeCpp.exe");
        var modes = new (string name, string exe, bool sandbox, bool hardened, bool dyn, bool signedOnly)[]
        {
            ("C#   control: unsandboxed", managed, false, false, false, false),
            ("C#   AppContainer, zero capabilities (existing profile mikepdf.render), no other mitigation", managed, true, false, false, false),
            ("C++  control: unsandboxed", native, false, false, false, false),
            ("C++  AppContainer, zero capabilities, no other mitigation", native, true, false, false, false),
            ("C++  AppContainer + every creation-time mitigation in SandboxPolicy EXCEPT prohibit-dynamic-code (DEP, ASLR x3, strict handles, extension points, CFG, fonts, image-load x3, CET) + restricted child processes", native, true, true, false, false),
            ("C++  as above, plus the engine's runtime Microsoft-signed-only policy applied before the printing calls", native, true, true, false, true),
            ("C++  the engine's REAL profile: all of the above INCLUDING prohibit-dynamic-code (ADR-0004 decision 2)", native, true, true, true, false),
        };
        var n = 0;
        foreach (var m in modes)
        {
            n++; var report = Path.Combine(dir, $"report-{n}.txt"); var okPdf = Path.Combine(dir, $"out-{n}.pdf"); var noPdf = Path.Combine(denied, $"out-{n}.pdf");
            foreach (var f in new[] { report, okPdf, noPdf }) if (File.Exists(f)) File.Delete(f);
            var a = $"\"{report}\" \"{okPdf}\" \"{noPdf}\"" + (m.signedOnly ? " signed-only" : "");
            string note; int rc;
            if (!m.sandbox) { var p = Process.Start(new ProcessStartInfo(m.exe, a) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(90000); rc = p.ExitCode; note = "exit " + rc; }
            else rc = AppContainerLaunch.Run(profile, m.exe, a, dir, 90000, m.hardened, m.dyn, out note);
            P($"### {m.name}: {note}");
            P(File.Exists(report) ? File.ReadAllText(report) : "(no report written)");
            Thread.Sleep(500);   // judged from OUTSIDE the container: did the spooler create the files?
            P($"  seen from outside: allowed-folder pdf exists = {File.Exists(okPdf)}{(File.Exists(okPdf) ? $" ({new FileInfo(okPdf).Length} bytes)" : "")}; denied-folder pdf exists = {File.Exists(noPdf)}{(File.Exists(noPdf) ? $" ({new FileInfo(noPdf).Length} bytes)" : "")}");
            foreach (var f in new[] { okPdf, noPdf }) if (File.Exists(f)) File.Delete(f);
        }
        Save("s12-appcontainer-probe.txt");
    }

    // ------------------------------------------------------------------ one job per fresh process, so the baseline is clean
    static void PerfOne(string variant)
    {
        EnsureFixtures(); var path = Path.Combine(Out, $"perf-{variant}.pdf");
        var o = new JobOptions { Input = Fx("text100.pdf"), OutPath = path, Mode = ScaleMode.Fit, Paper = 9 };
        switch (variant)
        {
            case "w10": o.Range = "1-10"; o.WholePage = true; break;
            case "b10": o.Range = "1-10"; break;
            case "c10": o.Range = "1-10"; o.BandBytes = 4L << 20; break;
            case "b1": o.Range = "1"; break;
            case "b30": o.Range = "1-30"; break;
            case "b100": break;
            case "i100": o.AsImage = true; break;
            case "v10": o.Range = "1-10"; o.Vector = true; break;
            case "v100": o.Vector = true; break;
            default: throw new ArgumentException(variant);
        }
        var r = RunAndVerify($"perf {variant}", o, false, 2, 0);
        Json($"s12-perfone-{variant}.json", r); Save($"s12-perfone-{variant}.txt");
    }

    // ------------------------------------------------------------------ what does the output PDF contain?
    static void Peek()
    {
        EnsureFixtures(); var path = Path.Combine(Out, "peek.pdf"); if (File.Exists(path)) File.Delete(path);
        var r = PrintJob.Run(new JobOptions { Input = Fx("mix6.pdf"), OutPath = path, Range = "1", Mode = ScaleMode.Fit, Paper = 9 });
        var b = File.ReadAllBytes(path); var t = System.Text.Encoding.Latin1.GetString(b);
        P($"output {b.Length} bytes; producer/creator strings:");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"/(Producer|Creator)\s*\(([^)]*)\)")) P("  " + m.Value);
        foreach (var f in new[] { "/DCTDecode", "/FlateDecode", "/JPXDecode", "/CCITTFaxDecode", "/Subtype/Image", "/Subtype /Image", "/SMask", "/Interpolate", "/ColorSpace" })
            P($"  {f}: {System.Text.RegularExpressions.Regex.Matches(t, System.Text.RegularExpressions.Regex.Escape(f)).Count}");
        File.Delete(path); Save("s12-peek.txt");
    }

    // ------------------------------------------------------------------ which creation-time mitigation stops a C++ probe from starting in the container?
    static void AcBisect(string root)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "probe"); var exe = Path.Combine(dir, "S12ProbeCpp.exe"); var report = Path.Combine(dir, "bisect.txt");
        var okPdf = Path.Combine(dir, "bisect.pdf"); var noPdf = Path.Combine(Path.GetFullPath(root), "denied", "bisect.pdf");
        string Try(string name, ulong m1, ulong m2, bool child)
        {
            foreach (var f in new[] { report, okPdf, noPdf }) if (File.Exists(f)) File.Delete(f);
            var rc = AppContainerLaunch.RunMasks("mikepdf.render", exe, $"\"{report}\" \"{okPdf}\" \"{noPdf}\"", dir, 60000, m1, m2, child, out var note);
            var ok = File.Exists(report); var detail = ok ? string.Join(" / ", File.ReadAllLines(report).Where(l => l.StartsWith("A:") || l.StartsWith("B:") || l.StartsWith("OpenPrinter") || l.StartsWith("mitigation"))) : "";
            foreach (var f in new[] { report, okPdf, noPdf }) if (File.Exists(f)) File.Delete(f);
            var line = $"{name,-46} -> {note,-16} {(ok ? "started; " + detail : "DID NOT RUN")}"; P(line); return line;
        }
        Try("none (AppContainer only)", 0, 0, false);
        Try("child-process policy restricted only", 0, 0, true);
        foreach (var f in AppContainerLaunch.Flags) Try(f.name, f.v1, f.v2, false);
        ulong a1 = 0, a2 = 0; foreach (var f in AppContainerLaunch.Flags) { a1 |= f.v1; a2 |= f.v2; }
        Try("all of the above", a1, a2, true);
        ulong b1 = 0, b2 = 0; foreach (var f in AppContainerLaunch.Flags) { if (f.name is "FONT_DISABLE" or "IMAGE_LOAD_NO_LOW_LABEL" or "IMAGE_LOAD_NO_REMOTE" or "IMAGE_LOAD_PREFER_SYSTEM32" or "PROHIBIT_DYNAMIC_CODE") continue; b1 |= f.v1; b2 |= f.v2; }
        Try("all except font, image-load x3, dynamic code", b1, b2, true);
        Save("s12-appcontainer-bisect.txt");
    }

    // ------------------------------------------------------------------ cancel
    static void Cancel()
    {
        EnsureFixtures(); string O(string n) => Path.Combine(Out, n);
        var path = O("cancel.pdf"); if (File.Exists(path)) File.Delete(path);
        var r = PrintJob.Run(new JobOptions { Input = Fx("text100.pdf"), OutPath = path, Mode = ScaleMode.Fit, Paper = 9, AbortAfterPages = 3, DocName = "S12 cancel test" });
        P($"aborted={r.Aborted} after {r.PagesPrinted} pages; AbortDoc took {r.AbortMs:F1} ms; total {r.TotalMs:F0} ms; error {r.Error ?? "none"}");
        Thread.Sleep(1500);
        P($"output file exists after abort: {File.Exists(path)}" + (File.Exists(path) ? $" ({new FileInfo(path).Length} bytes)" : ""));
        if (File.Exists(path)) File.Delete(path);
        Save("s12-cancel.txt");
    }
}
