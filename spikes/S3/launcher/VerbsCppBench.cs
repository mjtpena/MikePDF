using System.Text;
using Windows.Win32;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    /// <summary>S3b timings for the C++ stub: spawn to Hello, Ping, RenderTile transport, crash to restored tile.</summary>
    public static int CppBench(string[] a)
    {
        string run = Opt(a, "--run", "1");
        int spawnN = int.Parse(Opt(a, "--spawns", "40")), restartN = int.Parse(Opt(a, "--restarts", "30")), pingN = int.Parse(Opt(a, "--pings", "10000")), tileN = int.Parse(Opt(a, "--tiles", "1000"));
        PrepareDirs("cpp");
        string doc20 = PdfGen.BuildTwentyPage(Path.Combine(Env.Gen, "doc-20pages.pdf"));
        var hardM1 = M_DEP | M_ASLR | M_STRICT_HANDLE | M_EXTPOINT | M_IMAGELOAD | M_CFG | M_DYNCODE;
        var configs = new (string label, Isolation mode, ulong m1, ulong m2, bool child, uint ui)[]
        {
            ("C++ AppContainer, no extra mitigations", Isolation.AppContainer, 0, 0, false, 0),
            ("C++ AppContainer + hardened creation set (DEP/ASLR/strict handle/ext point/image load/CFG/dynamic-code/CET/child-process/UI limits)", Isolation.AppContainer, hardM1, M2_CET, true, 0x1FF),
            ("C++ no sandbox (control)", Isolation.None, 0, 0, false, 0),
        };
        SpawnOptions Opts(string label, Isolation mode, ulong m1, ulong m2, bool child, uint ui, string? file)
        {
            var o = Base(mode, label, "cpp"); o.ExeName = "engine-static.exe"; o.FilePath = file; o.Mit1 = m1; o.Mit2 = m2; o.ChildProcessRestricted = child; o.JobUiLimits = ui; return o;
        }
        string Cpu(CpuSampler c) { var (m, x, n) = c.Result(); return $"system CPU during run: mean {m:F1}% max {x:F1}% ({n} samples @250 ms)"; }
        var sb = new StringBuilder($"# C++ bench run {run} ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\n");

        // ---- spawn -> Hello
        foreach (var c in configs)
        {
            using var cpu = new CpuSampler();
            var total = new List<double>(); var toMain = new List<double>(); var toConn = new List<double>(); var hello = new List<double>(); var create = new List<double>(); double first = 0;
            for (int i = 0; i < spawnN + 1; i++)
            {
                using var ep = new EngineProcess(Opts(c.label, c.mode, c.m1, c.m2, c.child, c.ui, null)); ep.Start();
                double tot = Clock.Ms(ep.THelloDone - ep.TCreateStart);
                if (i == 0) { first = tot; continue; }
                total.Add(tot); create.Add(Clock.Ms(ep.TCreated - ep.TCreateStart)); toMain.Add(Clock.Ms(ep.EMain - ep.TCreated)); toConn.Add(Clock.Ms(ep.TConnected - ep.TCreated)); hello.Add(Clock.Ms(ep.THelloDone - ep.TConnected));
            }
            var s = Stats.Of(total);
            Console.WriteLine($"[cpp run {run}] spawn->Hello [{c.label}] first={first:F1} then {s}");
            Console.WriteLine($"      CreateProcess() {Stats.Of(create)} | created->Main {Stats.Of(toMain)} | created->pipe connected {Stats.Of(toConn)} | connected->Hello {Stats.Of(hello)}\n      {Cpu(cpu)}");
            sb.AppendLine($"| spawn -> Hello | {c.label} | {s.Row()} | first {first:F1} ms |");
        }

        // ---- ping + render (hardened config)
        {
            var c = configs[1];
            using var ep = new EngineProcess(Opts(c.label, c.mode, c.m1, c.m2, c.child, c.ui, null)); ep.Start();
            var op = ep.OpenDocumentFromPath(doc20);
            using var cpu = new CpuSampler();
            foreach (var (msg, nm) in new[] { (Msg.Ping, "Ping"), (Msg.PingEngine, "PingEngine") })
            {
                for (int i = 0; i < 1000; i++) ep.Call(msg, null);
                var us = new double[pingN];
                for (int i = 0; i < pingN; i++) { long t0 = Clock.Now(); ep.Call(msg, null); us[i] = Clock.Us(Clock.Now() - t0); }
                var st = Stats.Of(us);
                Console.WriteLine($"[cpp run {run}] {nm} RTT µs (hardened AC): {st} (>1 ms: {us.Count(x => x > 1000)})");
                sb.AppendLine($"| {nm} RTT µs | hardened AC | {st.Row()} | >1ms: {us.Count(x => x > 1000)} |");
            }
            foreach (float scale in new[] { 1.0f, 2.0f })
            {
                int pw = (int)(op.w * scale), ph = (int)(op.h * scale); var rnd = new Random(42);
                for (int i = 0; i < 100; i++) ep.RenderTile(i % 20, scale, 0, 0, 512, 512, i % 8);
                var rtt = new List<double>(); var eng = new List<double>(); var over = new List<double>();
                for (int i = 0; i < tileN; i++)
                {
                    int ox = rnd.Next(0, Math.Max(1, pw - 512) / 64 + 1) * 64, oy = rnd.Next(0, Math.Max(1, ph - 512) / 64 + 1) * 64;
                    var t = ep.RenderTile(i % 20, scale, ox, oy, 512, 512, i % 8);
                    if (t.Status != 0) throw new Exception("render " + t.Status);
                    rtt.Add(t.RttMs); eng.Add(t.LoadMs + t.RenderMs); over.Add(t.RttMs - t.LoadMs - t.RenderMs);
                }
                Console.WriteLine($"[cpp run {run}] RenderTile 512 @{scale:F0}x ms: RTT {Stats.Of(rtt)}\n      engine {Stats.Of(eng)}\n      transport overhead {Stats.Of(over)}");
                sb.AppendLine($"| RenderTile 512 @{scale:F0}x RTT ms | hardened AC | {Stats.Of(rtt).Row()} |");
                sb.AppendLine($"| RenderTile engine ms | hardened AC | {Stats.Of(eng).Row()} |");
                sb.AppendLine($"| RenderTile transport overhead ms | hardened AC | {Stats.Of(over).Row()} |");
            }
            Console.WriteLine("      " + Cpu(cpu));
        }

        // ---- crash -> respawn -> reopen 20-page -> first tile
        foreach (var c in configs.Take(2))
        {
            using var cpu = new CpuSampler();
            var detect = new List<double>(); var spawn = new List<double>(); var open = new List<double>(); var tile = new List<double>(); var total = new List<double>();
            var victim = new EngineProcess(Opts(c.label, c.mode, c.m1, c.m2, c.child, c.ui, doc20)); victim.Start(); victim.OpenDocument(0);
            for (int i = 0; i < restartN + 1; i++)
            {
                long tKill = Clock.Now(); victim.Kill(); victim.WaitExit(5000); long tDet = Clock.Now(); victim.Dispose();
                var ep = new EngineProcess(Opts(c.label, c.mode, c.m1, c.m2, c.child, c.ui, doc20)); ep.Start(); long tHello = Clock.Now();
                var op = ep.OpenDocument(0); long tOpen = Clock.Now();
                var t = ep.RenderTile(0, 1f, 0, 0, 512, 512, 0); long tTile = Clock.Now();
                if (op.status != 0 || t.Status != 0) throw new Exception("restart failed");
                if (i > 0) { detect.Add(Clock.Ms(tDet - tKill)); spawn.Add(Clock.Ms(tHello - tDet)); open.Add(Clock.Ms(tOpen - tHello)); tile.Add(Clock.Ms(tTile - tOpen)); total.Add(Clock.Ms(tTile - tKill)); }
                victim = ep;
            }
            victim.Dispose();
            var s = Stats.Of(total);
            Console.WriteLine($"[cpp run {run}] kill->respawn->reopen 20-page->first tile [{c.label}] total {s}\n      detect {Stats.Of(detect)} | spawn->Hello {Stats.Of(spawn)} | open {Stats.Of(open)} | tile {Stats.Of(tile)}\n      {Cpu(cpu)}");
            sb.AppendLine($"| crash -> restored first tile | {c.label} | {s.Row()} |");
            sb.AppendLine($"|   spawn -> Hello | {c.label} | {Stats.Of(spawn).Row()} |");
            sb.AppendLine($"|   OpenDocument 20 pages | {c.label} | {Stats.Of(open).Row()} |");
            sb.AppendLine($"|   first tile | {c.label} | {Stats.Of(tile).Row()} |");
        }
        File.WriteAllText(Path.Combine(Env.Out, $"cpp-bench-run{run}.md"), sb.ToString(), new UTF8Encoding(false));
        return 0;
    }
}
