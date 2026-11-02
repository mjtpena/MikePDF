using System.Runtime.InteropServices;
using System.Text;

namespace S12Probe;

/// <summary>
/// Runs inside (or outside, as the control) a zero-capability AppContainer and reports which printing-related calls work, including a complete
/// one-page job whose output path is (a) a folder the container may write and (b) a folder the container may NOT write.
/// args: report file, allowed output pdf, denied output pdf, [signed-only]
/// </summary>
internal static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DOCINFO { public int cbSize; public string lpszDocName; public string? lpszOutput; public string? lpszDatatype; public int fwType; }
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool OpenPrinterW(string n, out nint h, nint d);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(nint h);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] static extern int DocumentPropertiesW(nint w, nint p, string d, byte* o, byte* i, int m);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool EnumPrintersW(int f, string? n, int l, byte* b, int cb, out int need, out int ret);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateDCW(string drv, string dev, string? port, nint dm);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int StartDocW(nint dc, ref DOCINFO di);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int StartPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int EndPage(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int EndDoc(nint dc);
    [DllImport("gdi32.dll")] static extern int Rectangle(nint dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("user32.dll")] static extern nint GetDC(nint w);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint p, uint access, out nint tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint tok, int cls, void* buf, int len, out int ret);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessMitigationPolicy(int policy, void* buf, nuint len);

    static readonly StringBuilder Sb = new();
    static void R(string name, string outcome) => Sb.AppendLine($"{name} | {outcome}");
    static string Err() => "win32=" + Marshal.GetLastWin32Error();

    static string TokenInfo()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var t)) return "token open failed " + Err();
        int isAc = 0; GetTokenInformation(t, 29 /*TokenIsAppContainer*/, &isAc, 4, out _);
        int il = 0; var buf = stackalloc byte[256]; GetTokenInformation(t, 25 /*TokenIntegrityLevel*/, buf, 256, out _);
        var sid = *(nint*)buf; var cnt = *((byte*)sid + 1); il = *(int*)((byte*)sid + 8 + 4 * (cnt - 1));
        int caps = 0; var cb = stackalloc byte[1024]; if (GetTokenInformation(t, 30 /*TokenCapabilities*/, cb, 1024, out _)) caps = *(int*)cb;
        return $"IsAppContainer={isAc}, integrity RID=0x{il:X}, capability count={caps}";
    }

    static void Job(string label, string printer, string outPath)
    {
        try
        {
            var dc = CreateDCW("WINSPOOL", printer, null, 0); if (dc == 0) { R(label + " CreateDC", "DENIED " + Err()); return; }
            var di = new DOCINFO { cbSize = Marshal.SizeOf<DOCINFO>(), lpszDocName = "S12 probe " + label, lpszOutput = outPath };
            var sd = StartDocW(dc, ref di); if (sd <= 0) { R(label + " StartDoc", "DENIED " + Err()); DeleteDC(dc); return; }
            StartPage(dc); Rectangle(dc, 200, 200, 2000, 1400); EndPage(dc); var ed = EndDoc(dc); DeleteDC(dc);
            // Give the spooler time to finish writing, then see whether the file exists (the unsandboxed launcher also checks).
            for (var i = 0; i < 100 && !File.Exists(outPath); i++) Thread.Sleep(100);
            R(label + " StartDoc/EndPage/EndDoc", $"completed (EndDoc={ed}); file exists from the container's own view: {File.Exists(outPath)}");
        }
        catch (Exception ex) { R(label, ex.GetType().Name + " " + ex.Message); }
    }

    static int Main(string[] args)
    {
        var report = args[0]; var allowed = args.Length > 1 ? args[1] : ""; var denied = args.Length > 2 ? args[2] : "";
        var signedOnly = args.Contains("signed-only");
        R("process", $"pid {Environment.ProcessId}; {TokenInfo()}");
        const string printer = "Microsoft Print to PDF";
        try
        {
            // Negative controls: things S3 showed an AppContainer cannot do
            try { var n = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)).Length; R("control: list Documents", $"ALLOWED ({n} files)"); } catch (Exception ex) { R("control: list Documents", "DENIED " + ex.GetType().Name); }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "s12-probe-write.txt"), "x"); R("control: write to %TEMP%", "ALLOWED"); } catch (Exception ex) { R("control: write to %TEMP%", "DENIED " + ex.GetType().Name); }
            if (signedOnly)
            {
                // Same runtime policy the engine applies after its DLLs are loaded (ADR-0004 decision 2): Microsoft-signed binaries only (MicrosoftSignedOnly = bit 0).
                uint flags = 1; var ok = SetProcessMitigationPolicy(8 /*ProcessSignaturePolicy*/, &flags, 4); R("SetProcessMitigationPolicy(MicrosoftSignedOnly)", ok ? "applied" : "failed " + Err());
            }
            var o = OpenPrinterW(printer, out var h, 0); R("OpenPrinter", o ? "ALLOWED" : "DENIED " + Err()); if (o) ClosePrinter(h);
            var ep = EnumPrintersW(0x2 | 0x4, null, 4, null, 0, out var need, out _); R("EnumPrinters(local)", ep || need > 0 ? $"ALLOWED (needs {need} bytes)" : "DENIED " + Err());
            var size = DocumentPropertiesW(0, 0, printer, null, null, 0); R("DocumentProperties(size query)", size > 0 ? $"ALLOWED ({size} bytes)" : "DENIED " + Err());
            var sdc = GetDC(0); var mdc = CreateCompatibleDC(sdc); var bmp = mdc != 0 ? CreateCompatibleBitmap(sdc, 64, 64) : 0;
            R("memory DC + bitmap (what an in-sandbox FPDF_RenderPage could use)", mdc != 0 && bmp != 0 ? "ALLOWED" : "DENIED " + Err());
            if (allowed != "") Job("A: output into a folder the container may write", printer, allowed);
            if (denied != "") Job("B: output into a folder the container may NOT write", printer, denied);
        }
        catch (Exception ex) { R("probe exception", ex.GetType().Name + " " + ex.Message); }
        File.WriteAllText(report, Sb.ToString());
        return 0;
    }
}
