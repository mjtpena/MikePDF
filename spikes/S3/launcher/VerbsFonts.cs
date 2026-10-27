using System.Text;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    sealed record FontConfig(string Label, Isolation Mode, string FontMode);

    public static int Fonts(string[] a)
    {
        PrepareDirs("fdd");
        Directory.CreateDirectory(Env.Gen);
        string shots = Path.Combine(Env.Out, "fonts"); Directory.CreateDirectory(shots);
        var fixtures = new[]
        {
            ("fonts-fixture (generated: 13 non-embedded Latin fonts + MS Mincho CJK)", PdfGen.BuildFontsFixture(Path.Combine(Env.Gen, "fonts-test.pdf"))),
            ("hello_world.pdf (corpus; Helvetica/Times non-embedded)", Env.Fixture("hello_world.pdf")),
            ("font_weight.pdf (corpus)", Env.Fixture("font_weight.pdf")),
        };
        var configs = new[]
        {
            new FontConfig("A  outside sandbox, PDFium default (GDI)  [REFERENCE]", Isolation.None, "gdi"),
            new FontConfig("B  AppContainer, PDFium default (GDI)", Isolation.AppContainer, "gdi"),
            new FontConfig("C  AppContainer, provider returns nothing (built-in Foxit fonts only)", Isolation.AppContainer, "none"),
            new FontConfig("D  AppContainer, custom FPDF_SYSFONTINFO (reads C:\\Windows\\Fonts files, no GDI)", Isolation.AppContainer, "custom"),
            new FontConfig("E  outside sandbox, custom FPDF_SYSFONTINFO", Isolation.None, "custom"),
            new FontConfig("F  outside sandbox, provider returns nothing", Isolation.None, "none"),
            new FontConfig("G  Low-IL, PDFium default (GDI)", Isolation.LowIL, "gdi"),
        };
        var sb = new StringBuilder();
        var allRows = new List<object>();
        int fi = 0;
        foreach (var (fname, fpath) in fixtures)
        {
            fi++;
            Console.WriteLine($"\n=== fixture {fi}: {fname}");
            byte[]? reference = null; int rw = 0, rh = 0;
            foreach (var cfg in configs)
            {
                var o = Base(cfg.Mode, cfg.Label, cfg.Mode == Isolation.LowIL ? "scl" : "fdd");
                o.FilePath = null; o.FontMode = cfg.FontMode; if (cfg.Mode == Isolation.LowIL) o.UseJobList = false;
                using var ep = new EngineProcess(o);
                try { ep.Start(); }
                catch (Exception ex) { Console.WriteLine($"  [{cfg.Label}] START FAILED: {ex.Message} {ep.StdErrText()}"); continue; }
                (int status, int pages, float w, float h, double ms, uint pdfErr, string note) op;
                try { op = ep.OpenDocumentFromPath(fpath); }          // handle is duplicated into the engine: it never sees a path
                catch (Exception ex) { Console.WriteLine($"  [{cfg.Label}] ENGINE DIED during open: {ex.Message}; exited={ep.HasExited} code=0x{(ep.HasExited ? ep.ExitCode : 0):X}; stderr: {ep.StdErrText()}"); continue; }
                if (op.status != 0) { Console.WriteLine($"  [{cfg.Label}] open failed {op.status} {op.pdfErr}"); continue; }
                float scale = Math.Min(800f / op.w, 1000f / op.h);
                int w = (int)Math.Ceiling(op.w * scale), h = (int)Math.Ceiling(op.h * scale);
                EngineProcess.RenderTiming t;
                try { t = ep.RenderTile(0, scale, 0, 0, w, h, 0); }
                catch (Exception ex) { Console.WriteLine($"  [{cfg.Label}] ENGINE DIED during render: {ex.Message}; exited={ep.HasExited} code=0x{(ep.HasExited ? ep.ExitCode : 0):X}; stderr: {ep.StdErrText()}"); continue; }
                var px = ep.SlotPixels(0, w, h).ToArray();
                var g = ep.Call(Msg.GdiProbe, null); g.I32(); int n = g.I32(); string last = ""; for (int i = 0; i < n; i++) last = g.Str();
                Imaging.WritePng(Path.Combine(shots, $"fx{fi}-{cfg.Label[0]}.png"), px, w, h);
                if (reference == null) { reference = px; rw = w; rh = h; }
                var d = Imaging.Compare(reference, px, rw, rh);
                int ink = 0; for (int i = 0; i < px.Length; i += 4) if (px[i] < 128) ink++;
                string line = $"  {cfg.Label,-86} {w}x{h} ink={ink,6}  differing px={d.DifferentPixels,7} ({100.0 * d.DifferentPixels / d.Pixels:F2}%) maxΔ={d.MaxChannelDiff,3} MAE={d.MeanAbsErr:F3} SSIM={d.Ssim:F4} | {last}";
                Console.WriteLine(line);
                sb.AppendLine($"| {fname.Split(' ')[0]} | {cfg.Label} | {w}x{h} | {ink} | {d.DifferentPixels} ({100.0 * d.DifferentPixels / d.Pixels:F2}%) | {d.MaxChannelDiff} | {d.MeanAbsErr:F3} | {d.Ssim:F4} | {last.Replace("fontProvider: ", "")} |");
                allRows.Add(new { fixture = fname, config = cfg.Label, w, h, ink, d.DifferentPixels, d.MaxChannelDiff, d.MeanAbsErr, d.Ssim, provider = last });
            }
        }
        File.WriteAllText(Path.Combine(Env.Out, "fonts.md"), "| Fixture | Config | Size | Ink px | Pixels differing from A | Max channel diff | MAE | SSIM | Provider counters |\n|---|---|---|---|---|---|---|---|---|\n" + sb, new UTF8Encoding(false));
        Json.Save("fonts.json", allRows);
        return 0;
    }
}
