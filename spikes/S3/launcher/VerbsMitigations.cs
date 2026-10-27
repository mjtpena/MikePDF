using System.Text;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    public sealed record MitCase(string Name, ulong M1, ulong M2, bool Child, bool AapOut, uint Runtime, string Engine = "fdd", Isolation Mode = Isolation.AppContainer);

    public sealed record ProbeResult(string Case, bool Started, string StartInfo, bool Ping, bool Open, bool Render, int NonWhite, bool GdiOk, string Gdi, string Extra, double StartMs, string EffectivePolicies);

    public static ProbeResult Probe(SpawnOptions o, string label, bool negativeSubset = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ep = new EngineProcess(o);
        try { ep.Start(); }
        catch (Exception ex)
        {
            string err = ep.StdErrText();
            return new ProbeResult(label, false, ex.Message.Replace("\r", " ").Replace("\n", " ") + (err.Length > 0 ? " | stderr: " + err.Replace("\n", " ") : ""), false, false, false, 0, false, "", "", sw.Elapsed.TotalMilliseconds, "");
        }
        double startMs = Clock.Ms(ep.THelloDone - ep.TCreateStart);
        string rt = ep.RuntimeMitResults.Count > 0 ? "runtime-apply: " + string.Join(", ", ep.RuntimeMitResults.Select(x => x.name + "=" + (x.err == 0 ? "ok" : "err" + x.err))) + "; " : "";
        string eff = string.Join(" ", ep.Hello.Where(k => k.Key.StartsWith("mit.") && !k.Value.StartsWith("err") && k.Value != "0x0" && !k.Key.Contains("OptionsMask")).Select(k => k.Key[4..] + "=" + k.Value));
        bool ping = false, open = false, render = false, gdiOk = false; int nonWhite = 0; string gdi = "", extra = rt;
        try
        {
            ep.Call(Msg.Ping, null); ping = true;
            var op = ep.OpenDocument(0); open = op.status == 0;
            if (!open) extra += $"open status {op.status} pdfErr {op.pdfErr} {op.note}; ";
            if (open)
            {
                var t = ep.RenderTile(0, 1f, 0, 0, 200, 200, 0); render = t.Status == 0;
                var px = ep.SlotPixels(0, 200, 200);
                for (int i = 0; i < px.Length; i += 4) if (px[i] != 255 || px[i + 1] != 255 || px[i + 2] != 255) nonWhite++;
            }
            var g = ep.Call(Msg.GdiProbe, null); g.I32(); int n = g.I32(); var lines = new List<string>(); for (int i = 0; i < n; i++) lines.Add(g.Str());
            gdiOk = lines.Take(4).All(l => !l.Contains("FAIL") && !l.Contains("GDI_ERROR"));
            gdi = string.Join("; ", lines.Select(l => l.Replace("err=0", "").Trim()));
            if (negativeSubset)
            {
                var req = MakeNegRequest(o.EngineDir, 1);
                var w = new PayloadWriter().Str(req.Documents).Str(req.Temp).Str(req.RepoDir).Str(req.UncPath).I32(1).Str("203.0.113.1").I32(9).I32(req.LauncherPid).Str(req.TempDll).Str(req.LocalRogueDll).Str(req.EngineDir).Str(req.FontsDir);
                var r = ep.Call(Msg.RunNegative, w, 120000); r.I32(); int cnt = r.I32();
                for (int i = 0; i < cnt; i++)
                {
                    var res = new NegResult(r.Str(), r.Str(), r.Str(), r.I32(), r.Str());
                    if (res.Name.StartsWith("dll.load.unsigned") || res.Name.StartsWith("proc.create")) extra += $"{res.Name}={res.Outcome}({res.Win32}); ";
                }
            }
        }
        catch (Exception ex) { extra += "FAILED mid-test: " + ex.GetType().Name + " " + ex.Message + " | exited=" + ep.HasExited + (ep.HasExited ? $" code=0x{ep.ExitCode:X}" : "") + " stderr: " + ep.StdErrText().Replace("\n", " "); }
        return new ProbeResult(label, true, $"hello in {startMs:F0} ms", ping, open, render, nonWhite, gdiOk, gdi, extra, startMs, eff);
    }

    public static int Mitigations(string[] a)
    {
        PrepareDirs("fdd"); PrepareDirs("sc"); PrepareDirs("scl");
        var cases = new List<MitCase>
        {
            new("control: no mitigation flags", 0, 0, false, false, 0),
            new("DEP", M_DEP, 0, false, false, 0),
            new("ASLR: bottom-up + high-entropy + force-relocate", M_ASLR, 0, false, false, 0),
            new("strict handle checks", M_STRICT_HANDLE, 0, false, false, 0),
            new("Win32k system call disable", M_WIN32K, 0, false, false, 0),
            new("extension points disable", M_EXTPOINT, 0, false, false, 0),
            new("prohibit dynamic code", M_DYNCODE, 0, false, false, 0),
            new("CFG always on", M_CFG, 0, false, false, 0),
            new("block non-Microsoft binaries (at creation)", M_MSONLY, 0, false, false, 0),
            new("font disable (non-system fonts)", M_FONT, 0, false, false, 0),
            new("image load: no remote + no low label + prefer System32", M_IMAGELOAD, 0, false, false, 0),
            new("CET user shadow stack (always on)", 0, M2_CET, false, false, 0),
            new("child process policy: restricted", 0, 0, true, false, 0),
            new("ALL_APPLICATION_PACKAGES opt-out", 0, 0, false, true, 0),
            new("runtime: Microsoft-signed-only applied after init", 0, 0, false, false, RT_SIGNATURE),
            new("runtime: child process no-create applied after init", 0, 0, false, false, RT_CHILD),
            new("runtime: extension points disable", 0, 0, false, false, RT_EXTPOINT),
            new("runtime: image load restrictions", 0, 0, false, false, RT_IMAGELOAD),
            new("runtime: font disable", 0, 0, false, false, RT_FONT),
            new("runtime: strict handle checks", 0, 0, false, false, RT_STRICTHANDLE),
            new("runtime: dynamic code prohibited (after JIT warm-up)", 0, 0, false, false, RT_DYNCODE),
            new("runtime: Win32k disable", 0, 0, false, false, RT_WIN32K),
            // self-contained engine (runtime copied into the repo) for the policies that failed with the framework-dependent one
            new("[self-contained] control", 0, 0, false, false, 0, "sc"),
            new("[self-contained] Win32k system call disable", M_WIN32K, 0, false, false, 0, "sc"),
            new("[self-contained] prohibit dynamic code", M_DYNCODE, 0, false, false, 0, "sc"),
            new("[self-contained] block non-Microsoft binaries", M_MSONLY, 0, false, false, 0, "sc"),
            new("[self-contained] ALL_APPLICATION_PACKAGES opt-out", 0, 0, false, true, 0, "sc"),
            new("[self-contained] CET user shadow stack", 0, M2_CET, false, false, 0, "sc"),
            new("[low-IL, self-contained] control", 0, 0, false, false, 0, "scl", Isolation.LowIL),
            new("[low-IL, self-contained] child process policy", 0, 0, true, false, 0, "scl", Isolation.LowIL),
        };
        string only = Opt(a, "--only", "");
        var results = new List<ProbeResult>();
        foreach (var c in cases)
        {
            if (only != "" && !c.Name.Contains(only)) continue;
            var o = Base(c.Mode, c.Name, c.Engine);
            o.Mit1 = c.M1; o.Mit2 = c.M2; o.ChildProcessRestricted = c.Child; o.AllAppPackagesOptOut = c.AapOut; o.ExtraRuntimeMitigations = c.Runtime;
            if (c.Mode == Isolation.LowIL) o.UseJobList = false;
            bool neg = c.Runtime == RT_SIGNATURE || c.M1 == M_MSONLY || c.Child || c.Runtime == RT_CHILD;
            var r = Probe(o, c.Name, neg);
            results.Add(r);
            Console.WriteLine($"{(r.Started && r.Ping && r.Open && r.Render && r.GdiOk ? "SURVIVES" : r.Started ? "PARTIAL " : "FAILS   ")} | {c.Name,-62} | start={r.Started} ping={r.Ping} open={r.Open} render={r.Render}({r.NonWhite}px) gdi={r.GdiOk} | {r.StartInfo} {r.Extra}");
            if (r.Started && !r.GdiOk) Console.WriteLine($"           gdi: {r.Gdi}");
        }
        Json.Save("mitigations.json", results);
        var sb = new StringBuilder();
        sb.AppendLine("| Case | Engine starts | Ping | Open+render | GDI font path | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Case} | {(r.Started ? "yes" : "NO")} | {(r.Ping ? "yes" : "no")} | {(r.Open && r.Render ? "yes (" + r.NonWhite + " px)" : "no")} | {(r.GdiOk ? "ok" : r.Started ? "FAIL" : "-")} | {(r.Started ? r.Extra : r.StartInfo).Replace("|", "/")} |");
        File.WriteAllText(Path.Combine(Env.Out, "mitigations.md"), sb.ToString(), new UTF8Encoding(false));
        return 0;
    }
}
