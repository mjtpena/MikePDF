using Windows.Win32;
using System.Text;

namespace Quire.S3.Launcher;

/// <summary>S3b: the same launcher and protocol driving the C++20 engine stub (spikes/S3/engine-cpp).</summary>
public static unsafe partial class Verbs
{
    sealed record CppCase(string Name, string Exe, ulong M1 = 0, ulong M2 = 0, bool Child = false, uint Runtime = 0, string FontMode = "gdi", bool SendFonts = false,
                          uint UiLimits = 0, Isolation Mode = Isolation.AppContainer, string? Fixture = null, string Args = "", string Dir = "cpp");

    sealed record CppResult(string Case, bool Started, string Detail, string PdfiumError, string LoadLog, bool Open, bool Render, int NonWhite, string Gdi, string Extra, double SsimVsRef);

    const ulong M2_CET_STRICT = 3UL << 28;

    static readonly (string key, string file, int style, string face)[] BrokerFonts =
    [
        ("arial", "arial.ttf", 0, "Arial"), ("arial", "arialbd.ttf", 1, "Arial Bold"), ("arial", "ariali.ttf", 2, "Arial Italic"),
        ("times", "times.ttf", 0, "Times New Roman"), ("times", "timesbd.ttf", 1, "Times New Roman Bold"), ("courier", "cour.ttf", 0, "Courier New"), ("georgia", "georgia.ttf", 0, "Georgia"), ("segoe", "segoeui.ttf", 0, "Segoe UI"), ("verdana", "verdana.ttf", 0, "Verdana"), ("gothic", "msgothic.ttc", 0, "MS Gothic"),
    ];

    static readonly List<string> SkippedFonts = new();
    static (int added, int bytes) SendBrokerFonts(EngineProcess ep)
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        int added = 0, total = 0;
        foreach (var (key, file, style, face) in BrokerFonts)       // one font per message: the frame cap is 8 MB and msgothic.ttc is 9.4 MB, see report
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, file));
            if (bytes.Length > Proto.MaxFrame - 4096) { SkippedFonts.Add($"{file} ({bytes.Length / 1048576.0:F1} MB > {Proto.MaxFrame >> 20} MB frame cap)"); continue; }
            var w = new PayloadWriter().I32(1).Str(key).I32(style).I32(bytes.Length).Str(face).Raw(bytes);
            var r = ep.Call(Msg.SetFonts, w, 30000);
            int st = r.I32(); added += r.I32(); total += r.I32();
            if (st != 0) throw new Exception("SetFonts failed " + st);
        }
        return (added, total);
    }

    static byte[]? _cppRef; static int _cppRefW, _cppRefH;

    static (byte[] px, int w, int h, string note) RenderFixture(EngineProcess ep, string fixture)
    {
        var op = ep.OpenDocumentFromPath(fixture);
        if (op.status != 0) throw new Exception($"open failed {op.status} err {op.pdfErr} {op.note}");
        float scale = Math.Min(800f / op.w, 1000f / op.h);
        int w = (int)Math.Ceiling(op.w * scale), h = (int)Math.Ceiling(op.h * scale);
        var t = ep.RenderTile(0, scale, 0, 0, w, h, 0);
        if (t.Status != 0) throw new Exception("render status " + t.Status);
        return (ep.SlotPixels(0, w, h).ToArray(), w, h, "");
    }

    static CppResult RunCpp(CppCase c)
    {
        var o = Base(c.Mode, c.Name, c.Dir);
        o.ExeName = c.Exe; o.ExtraArgs = c.Args; o.FilePath = null; o.FontMode = c.FontMode;
        o.Mit1 = c.M1; o.Mit2 = c.M2; o.ChildProcessRestricted = c.Child; o.JobUiLimits = c.UiLimits; o.ExtraRuntimeMitigations = 0;
        if (c.Mode == Isolation.LowIL) o.UseJobList = false;
        using var ep = new EngineProcess(o);
        try { ep.Start(); }
        catch (Exception ex)
        {
            Thread.Sleep(300);
            return new CppResult(c.Name, false, ex.Message.Replace("\r", " ").Replace("\n", " ") + (ep.HasExited ? $" | exit code 0x{ep.ExitCode:X8}" : " | still running") + (ep.StdErrText().Length > 0 ? " | stderr: " + ep.StdErrText().Replace("\n", " ") : ""), "", "", false, false, 0, "", "", 0);
        }
        string pdfErr = ep.Hello.GetValueOrDefault("pdfiumError", ""), load = ep.Hello.GetValueOrDefault("loadLog", "");
        var extra = new StringBuilder();
        extra.Append($"AC={ep.Hello.GetValueOrDefault("isAppContainer")} win32k-policy={ep.Hello.GetValueOrDefault("mit.SystemCallDisable(Win32k)")} dyncode-policy={ep.Hello.GetValueOrDefault("mit.DynamicCode")} sig-policy={ep.Hello.GetValueOrDefault("mit.Signature")} cet={ep.Hello.GetValueOrDefault("mit.UserShadowStack")}; ");
        if (ep.Hello.TryGetValue("modules", out var mods)) extra.Append("modules: " + mods + "; ");
        bool open = false, render = false; int nonWhite = 0; string gdi = ""; double ssim = 0;
        try
        {
            ep.Call(Msg.Ping, null);
            if (c.SendFonts) { var (n, b) = SendBrokerFonts(ep); extra.Append($"broker sent {n} fonts / {b / 1024} KB{(SkippedFonts.Count > 0 ? ", skipped " + string.Join(",", SkippedFonts) : "")}; "); SkippedFonts.Clear(); }
            if (c.Runtime != 0)
            {
                var res = ep.ApplyRuntimeMitigations(c.Runtime);
                extra.Append("runtime-apply: " + string.Join(", ", res.Select(x => $"{x.name}={(x.err == 0 ? "ok" : "err" + x.err)}")) + "; ");
                foreach (var dll in new[] { Path.Combine(Env.EngineDirFor("cpp"), "rogue-copy.dll"), "dwrite.dll" })
                {
                    var lr = ep.Call(Msg.LoadDll, new PayloadWriter().Str(dll)); int e = lr.I32();
                    extra.Append($"LoadLibrary({Path.GetFileName(dll)})={(e == 0 ? "ok" : "err" + e)}; ");
                }
            }
            if (pdfErr == "")
            {
                if (c.Fixture != null)
                {
                    var (px, w, h, _) = RenderFixture(ep, c.Fixture);
                    open = render = true;
                    for (int i = 0; i < px.Length; i += 4) if (px[i] < 128) nonWhite++;
                    if (_cppRef == null) { _cppRef = px; _cppRefW = w; _cppRefH = h; ssim = 1; }
                    else ssim = Imaging.Compare(_cppRef, px, _cppRefW, _cppRefH).Ssim;
                    Imaging.WritePng(Path.Combine(Env.Out, "fonts", $"cpp-{Math.Abs(c.Name.GetHashCode()) % 100000}.png"), px, w, h);
                }
                else
                {
                    var op = ep.OpenDocumentFromPath(Env.Fixture("hello_world.pdf")); open = op.status == 0;
                    if (open)
                    {
                        var t = ep.RenderTile(0, 1f, 0, 0, 200, 200, 0); render = t.Status == 0;
                        var px = ep.SlotPixels(0, 200, 200);
                        for (int i = 0; i < px.Length; i += 4) if (px[i] != 255 || px[i + 1] != 255 || px[i + 2] != 255) nonWhite++;
                    }
                    else extra.Append($"open status {op.status} pdfErr {op.pdfErr} {op.note}; ");
                }
            }
            var g = ep.Call(Msg.GdiProbe, null); g.I32(); int n2 = g.I32(); var lines = new List<string>(); for (int i = 0; i < n2; i++) lines.Add(g.Str());
            gdi = string.Join("; ", lines.Select(l => l.Replace(" err=0", "")));
        }
        catch (Exception ex) { extra.Append($"FAILED mid-test: {ex.GetType().Name} {ex.Message} | exited={ep.HasExited}{(ep.HasExited ? $" code=0x{ep.ExitCode:X}" : "")} stderr: {ep.StdErrText().Replace("\n", " ")}"); }
        return new CppResult(c.Name, true, $"hello in {Clock.Ms(ep.THelloDone - ep.TCreateStart):F0} ms", pdfErr, load, open, render, nonWhite, gdi, extra.ToString(), ssim);
    }

    public static int Cpp(string[] a)
    {
        PrepareDirs("cpp"); if (Directory.Exists(Env.EngineDirFor("cppsigned"))) PrepareDirs("cppsigned");
        Directory.CreateDirectory(Path.Combine(Env.Out, "fonts"));
        string fontsFx = PdfGen.BuildFontsFixture(Path.Combine(Env.Gen, "fonts-test.pdf"));
        string only = Opt(a, "--only", "");
        const uint UI = 0x1FF;
        var hardenedM1 = M_DEP | M_ASLR | M_STRICT_HANDLE | M_EXTPOINT | M_IMAGELOAD | M_CFG;
        var cases = new List<CppCase>
        {
            // reference render for the font comparison: same C++ engine, no sandbox, default GDI
            new("REF  C++ unsandboxed, GDI (font reference)", "engine-static.exe", Mode: Isolation.None, Fixture: fontsFx),
            new("ctl  AppContainer, static pdfium.dll import, GDI", "engine-static.exe"),
            new("ctl  AppContainer, delay-loaded pdfium.dll, GDI", "engine-delay.exe"),
            new("ctl  AppContainer, GDI, fonts fixture", "engine-static.exe", Fixture: fontsFx),
            // Q1
            new("Q1a Win32k lockdown, static import, GDI", "engine-static.exe", M1: M_WIN32K),
            new("Q1a Win32k lockdown, delay-load, GDI", "engine-delay.exe", M1: M_WIN32K),
            new("Q1a Win32k lockdown WITHOUT AppContainer (medium IL), delay-load, plain load", "engine-delay.exe", M1: M_WIN32K, Args: "--loadmode plain", Mode: Isolation.None),
            new("Q1a Win32k lockdown, delay-load, LoadLibraryW(pdfium.dll) plain", "engine-delay.exe", M1: M_WIN32K, Args: "--loadmode plain"),
            new("Q1a Win32k lockdown, delay-load, map pdfium.dll without resolving imports", "engine-delay.exe", M1: M_WIN32K, Args: "--loadmode probe"),
            new("Q1b Win32k lockdown, delay-load, broker font provider (fonts sent by broker)", "engine-delay.exe", M1: M_WIN32K, FontMode: "broker", SendFonts: true),
            new("Q1b Win32k lockdown, delay-load, broker provider, fonts fixture", "engine-delay.exe", M1: M_WIN32K, FontMode: "broker", SendFonts: true, Fixture: fontsFx),
            new("Q1b no lockdown, delay-load, broker provider, fonts fixture (isolates the provider)", "engine-delay.exe", FontMode: "broker", SendFonts: true, Fixture: fontsFx),
            // Q2
            new("Q2  prohibit dynamic code at creation, static import", "engine-static.exe", M1: M_DYNCODE),
            new("Q2  prohibit dynamic code, fonts fixture", "engine-static.exe", M1: M_DYNCODE, Fixture: fontsFx),
            new("Q2  runtime ProhibitDynamicCode after init", "engine-static.exe", Runtime: RT_DYNCODE),
            // Q3
            new("Q3  Microsoft-signed-only at creation, static import", "engine-static.exe", M1: M_MSONLY),
            new("Q3  Microsoft-signed-only at creation, delay-load", "engine-delay.exe", M1: M_MSONLY),
            new("Q3  Microsoft-signed-only at creation, delay-load, map-only probe", "engine-delay.exe", M1: M_MSONLY, Args: "--loadmode probe"),
            new("Q3  Microsoft-signed-only at creation, delay-load, plain LoadLibrary", "engine-delay.exe", M1: M_MSONLY, Args: "--loadmode plain"),
            new("Q3  SELF-SIGNED exe+pdfium.dll (cert not installed anywhere), MS-signed-only at creation, static", "engine-static.exe", M1: M_MSONLY, Dir: "cppsigned"),
            new("Q3  SELF-SIGNED, MS-signed-only at creation, delay-load, plain LoadLibrary", "engine-delay.exe", M1: M_MSONLY, Dir: "cppsigned", Args: "--loadmode plain"),
            new("Q3  SELF-SIGNED, MS-signed-or-Store-signed at creation (ALLOW_STORE), static", "engine-static.exe", M1: PInvoke.PROCESS_CREATION_MITIGATION_POLICY_BLOCK_NON_MICROSOFT_BINARIES_ALLOW_STORE, Dir: "cppsigned"),
            new("Q3  UNSIGNED, ALLOW_STORE at creation, static", "engine-static.exe", M1: PInvoke.PROCESS_CREATION_MITIGATION_POLICY_BLOCK_NON_MICROSOFT_BINARIES_ALLOW_STORE),
            new("Q3  SELF-SIGNED + runtime Microsoft-signed-only after init (does the signed rogue DLL load?)", "engine-static.exe", Runtime: RT_SIGNATURE, Dir: "cppsigned"),
            new("Q3  Microsoft-signed-only applied at runtime after PDFium init", "engine-static.exe", Runtime: RT_SIGNATURE, Fixture: fontsFx),
            // CET
            new("CET shadow stack ALWAYS_ON (compat mode)", "engine-static.exe", M2: M2_CET),
            new("CET shadow stack STRICT_MODE", "engine-static.exe", M2: M2_CET_STRICT),
            // combined
            new("hardened set (child policy, DEP, ASLR, strict handle, ext point, image load, CFG, CET compat, UI limits, runtime MS-signed-only)", "engine-static.exe", M1: hardenedM1, M2: M2_CET, Child: true, UiLimits: UI, Runtime: RT_SIGNATURE, Fixture: fontsFx),
            new("hardened set + dynamic-code prohibition", "engine-static.exe", M1: hardenedM1 | M_DYNCODE, M2: M2_CET, Child: true, UiLimits: UI, Runtime: RT_SIGNATURE, Fixture: fontsFx),
            new("hardened set + dynamic-code + Win32k lockdown (delay-load, broker fonts)", "engine-delay.exe", M1: hardenedM1 | M_DYNCODE | M_WIN32K, M2: M2_CET, Child: true, UiLimits: UI, Runtime: RT_SIGNATURE, FontMode: "broker", SendFonts: true, Fixture: fontsFx),
        };
        var results = new List<CppResult>(); var sb = new StringBuilder();
        foreach (var c in cases)
        {
            if (only != "" && !c.Name.Contains(only)) continue;
            Environment.SetEnvironmentVariable("S3_MODULES", c.Name.StartsWith("Q1a") ? "1" : null);
            var r = RunCpp(c);
            results.Add(r);
            string verdict = !r.Started ? "NO START" : r.PdfiumError != "" ? "NO PDFIUM" : r.Open && r.Render ? "WORKS   " : "PARTIAL ";
            Console.WriteLine($"{verdict} | {c.Name}");
            Console.WriteLine($"           started={r.Started} {r.Detail}");
            if (r.PdfiumError != "") Console.WriteLine($"           pdfiumError: {r.PdfiumError}");
            if (r.LoadLog != "") Console.WriteLine($"           loadLog: {r.LoadLog}");
            if (r.Started) Console.WriteLine($"           open={r.Open} render={r.Render} ink/nonwhite px={r.NonWhite} ssim-vs-ref={r.SsimVsRef:F4}");
            if (r.Gdi != "") Console.WriteLine($"           gdi: {r.Gdi}");
            if (r.Extra != "") Console.WriteLine($"           {r.Extra}");
            sb.AppendLine($"| {c.Name} | {verdict.Trim()} | {(r.Started ? "yes" : "no")} | {r.PdfiumError.Replace("|", "/")} | {r.LoadLog} | {(r.Open && r.Render ? "yes" : "no")} | {r.Detail.Replace("|", "/")} {r.Extra.Replace("|", "/")} |");
        }
        Json.Save("cpp-matrix.json", results);
        File.WriteAllText(Path.Combine(Env.Out, "cpp-matrix.md"), "| Case | Verdict | Process starts | pdfium error | load log | open+render | detail |\n|---|---|---|---|---|---|---|\n" + sb, new UTF8Encoding(false));
        return 0;
    }
}
