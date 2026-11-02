using System.Text;

namespace Quire.S3.Launcher;

public sealed class Stats
{
    public int N; public double Min, P50, P95, P99, Max, Mean;
    public static Stats Of(IReadOnlyList<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        double Q(double p) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1 < 0 ? 0 : (int)Math.Ceiling(p * s.Length) - 1)];
        return new Stats { N = s.Length, Min = s[0], P50 = Q(0.50), P95 = Q(0.95), P99 = Q(0.99), Max = s[^1], Mean = s.Average() };
    }
    public override string ToString() => $"n={N} min={Min:F3} p50={P50:F3} p95={P95:F3} p99={P99:F3} max={Max:F3} mean={Mean:F3}";
    public string Row() => $"{N} | {Min:F3} | {P50:F3} | {P95:F3} | {P99:F3} | {Max:F3}";
}

/// <summary>Samples whole-machine CPU use (GetSystemTimes) on a background thread while a measurement runs. Other agents share this machine, so the load matters.</summary>
public sealed unsafe class CpuSampler : IDisposable
{
    readonly Thread _t; volatile bool _stop; readonly List<double> _samples = new();
    public CpuSampler()
    {
        _t = new Thread(() =>
        {
            ulong i0, k0, u0; N.GetSystemTimes(&i0, &k0, &u0);
            while (!_stop)
            {
                Thread.Sleep(250);
                ulong i1, k1, u1; N.GetSystemTimes(&i1, &k1, &u1);
                double idle = i1 - i0, total = (k1 - k0) + (u1 - u0);   // kernel time includes idle
                if (total > 0) lock (_samples) _samples.Add(100.0 * (1 - idle / total));
                i0 = i1; k0 = k1; u0 = u1;
            }
        }) { IsBackground = true };
        _t.Start();
    }
    public (double mean, double max, int n) Result() { lock (_samples) return _samples.Count == 0 ? (0, 0, 0) : (_samples.Average(), _samples.Max(), _samples.Count); }
    public void Dispose() { _stop = true; _t.Join(); }
}

public static unsafe partial class Verbs
{
    public static int Bench(string[] a)
    {
        string run = Opt(a, "--run", "1");
        int pingN = int.Parse(Opt(a, "--pings", "10000"));
        int tileN = int.Parse(Opt(a, "--tiles", "1000"));
        int spawnN = int.Parse(Opt(a, "--spawns", "40"));
        int restartN = int.Parse(Opt(a, "--restarts", "30"));
        PrepareDirs("fdd"); PrepareDirs("sc");
        Directory.CreateDirectory(Env.Gen);
        string doc20 = PdfGen.BuildTwentyPage(Path.Combine(Env.Gen, "doc-20pages.pdf"));
        var sb = new StringBuilder();
        var json = new Dictionary<string, object>();
        string Cpu(CpuSampler c) { var (m, x, n) = c.Result(); return $"system CPU during run: mean {m:F1}% max {x:F1}% ({n} samples @250 ms)"; }
        sb.AppendLine($"# Bench run {run}  ({DateTime.Now:yyyy-MM-dd HH:mm:ss}, logical CPUs {Environment.ProcessorCount})");

        // ------------------------------------------------------------------ 1. Ping RTT
        foreach (var (label, mode, eng) in new[] { ("AppContainer (fdd engine)", Isolation.AppContainer, "fdd"), ("no sandbox (control)", Isolation.None, "fdd") })
        {
            var o = Base(mode, label, eng);
            using var ep = new EngineProcess(o); ep.Start();
            using var cpu = new CpuSampler();
            foreach (var (msg, nm) in new[] { (Msg.Ping, "Ping (answered on the I/O thread)"), (Msg.PingEngine, "PingEngine (through the engine work queue)") })
            {
                for (int i = 0; i < 1000; i++) ep.Call(msg, null);                    // warm-up, discarded
                var us = new double[pingN];
                for (int i = 0; i < pingN; i++) { long t0 = Clock.Now(); ep.Call(msg, null); us[i] = Clock.Us(Clock.Now() - t0); }
                var st = Stats.Of(us);
                int over1ms = us.Count(x => x > 1000);
                Console.WriteLine($"[run {run}] {nm,-48} {label,-28} RTT µs: {st}  (>1 ms: {over1ms})");
                sb.AppendLine($"| {nm} | {label} | {st.Row()} | {over1ms} |");
                json[$"ping/{msg}/{mode}"] = st;
            }
            Console.WriteLine("   " + Cpu(cpu)); sb.AppendLine("    " + Cpu(cpu));
        }

        // ------------------------------------------------------------------ 2. RenderTile
        {
            var o = Base(Isolation.AppContainer, "tile", "fdd"); o.FilePath = null;
            using var ep = new EngineProcess(o); ep.Start();
            var op = ep.OpenDocumentFromPath(doc20);
            Console.WriteLine($"[run {run}] opened 20-page doc: pages={op.pages} size={op.w}x{op.h} in {op.ms:F2} ms");
            using var cpu = new CpuSampler();
            foreach (float scale in new[] { 1.0f, 2.0f })
            {
                int pw = (int)(op.w * scale), ph = (int)(op.h * scale);
                for (int i = 0; i < 100; i++) ep.RenderTile(i % 20, scale, 0, 0, 512, 512, i % 8);   // warm-up
                var rtt = new List<double>(); var eng = new List<double>(); var over = new List<double>(); var outb = new List<double>(); var q = new List<double>(); var inb = new List<double>();
                var rnd = new Random(42);
                for (int i = 0; i < tileN; i++)
                {
                    int ox = rnd.Next(0, Math.Max(1, pw - 512) / 64 + 1) * 64, oy = rnd.Next(0, Math.Max(1, ph - 512) / 64 + 1) * 64;
                    var t = ep.RenderTile(i % 20, scale, ox, oy, 512, 512, i % 8);
                    if (t.Status != 0) throw new Exception("render failed " + t.Status);
                    rtt.Add(t.RttMs); eng.Add(t.LoadMs + t.RenderMs); over.Add(t.RttMs - t.LoadMs - t.RenderMs);
                    outb.Add(t.OutMs); q.Add(t.QueueMs); inb.Add(t.InMs);
                }
                var sR = Stats.Of(rtt); var sE = Stats.Of(eng); var sO = Stats.Of(over);
                Console.WriteLine($"[run {run}] RenderTile 512x512 @{scale:F0}x (page {pw}x{ph}px) ms: RTT {sR}");
                Console.WriteLine($"                                          engine LoadPage+Render: {sE}");
                Console.WriteLine($"                                          transport overhead (RTT - engine): {sO}");
                Console.WriteLine($"                                          of which request in-flight {Stats.Of(outb)}, queue wait {Stats.Of(q)}, response in-flight {Stats.Of(inb)}");
                sb.AppendLine($"| RenderTile 512x512 @{scale:F0}x RTT | AppContainer | {sR.Row()} |");
                sb.AppendLine($"| RenderTile engine (LoadPage+Render) | AppContainer | {sE.Row()} |");
                sb.AppendLine($"| RenderTile transport overhead (RTT-engine) | AppContainer | {sO.Row()} |");
                json[$"tile/{scale}"] = new { rtt = sR, engine = sE, overhead = sO };
            }
            Console.WriteLine("   " + Cpu(cpu)); sb.AppendLine("    " + Cpu(cpu));
        }

        // ------------------------------------------------------------------ 3. cold spawn to Hello
        foreach (var (label, mode, eng) in new[] { ("AppContainer, framework-dependent engine", Isolation.AppContainer, "fdd"), ("AppContainer, self-contained engine", Isolation.AppContainer, "sc"), ("no sandbox, framework-dependent engine", Isolation.None, "fdd") })
        {
            using var cpu = new CpuSampler();
            var total = new List<double>(); var create = new List<double>(); var toMain = new List<double>(); var pdfium = new List<double>(); var toConn = new List<double>(); var hello = new List<double>();
            double first = 0;
            for (int i = 0; i < spawnN + 1; i++)
            {
                var o = Base(mode, label, eng); o.FilePath = null;
                using var ep = new EngineProcess(o); ep.Start();
                double tot = Clock.Ms(ep.THelloDone - ep.TCreateStart);
                if (i == 0) { first = tot; continue; }       // first spawn of this configuration reported separately
                total.Add(tot);
                create.Add(Clock.Ms(ep.TCreated - ep.TCreateStart));
                toMain.Add(Clock.Ms(ep.EMain - ep.TCreated));
                pdfium.Add(ep.EnginePdfiumInitMs);
                toConn.Add(Clock.Ms(ep.TConnected - ep.TCreated));
                hello.Add(Clock.Ms(ep.THelloDone - ep.TConnected));
            }
            var sT = Stats.Of(total);
            Console.WriteLine($"[run {run}] spawn->Hello {label,-45} first={first:F1} ms; then {sT}");
            Console.WriteLine($"          CreateProcess call {Stats.Of(create)}  |  CreateProcess returned -> engine Main entered {Stats.Of(toMain)}  |  PDFium init {Stats.Of(pdfium)}  |  returned -> pipe connected {Stats.Of(toConn)}  |  connected -> Hello reply {Stats.Of(hello)}");
            Console.WriteLine("   " + Cpu(cpu));
            sb.AppendLine($"| spawn -> Hello (warm file cache) | {label} | {sT.Row()} | first spawn {first:F1} ms |");
            sb.AppendLine($"|   of which CreateProcess() call | {label} | {Stats.Of(create).Row()} |");
            sb.AppendLine($"|   of which CreateProcess returned -> Main entered | {label} | {Stats.Of(toMain).Row()} |");
            sb.AppendLine($"|   of which PDFium init (engine-measured) | {label} | {Stats.Of(pdfium).Row()} |");
            sb.AppendLine("    " + Cpu(cpu));
            json[$"spawn/{label}"] = new { first, total = sT };
        }

        // ------------------------------------------------------------------ 4. crash -> respawn -> reopen 20-page doc -> first tile
        foreach (var (label, eng) in new[] { ("AppContainer, framework-dependent engine", "fdd"), ("AppContainer, self-contained engine", "sc") })
        {
            using var cpu = new CpuSampler();
            var detect = new List<double>(); var spawn = new List<double>(); var open = new List<double>(); var tile = new List<double>(); var total = new List<double>();
            var o0 = Base(Isolation.AppContainer, "restart", eng); o0.FilePath = null;
            var victim = new EngineProcess(o0); victim.Start(); victim.OpenDocumentFromPath(doc20);
            for (int i = 0; i < restartN + 1; i++)
            {
                long tKill = Clock.Now();
                victim.Kill();                                     // TerminateProcess: the "crash"
                victim.WaitExit(5000);
                long tDetected = Clock.Now();                      // process handle signalled
                victim.Dispose();
                var o = Base(Isolation.AppContainer, "restart", eng); o.FilePath = doc20;   // file handle passed in the handle list again
                var ep = new EngineProcess(o);
                ep.Start();
                long tHello = Clock.Now();
                var op = ep.OpenDocument(0);
                long tOpen = Clock.Now();
                var t = ep.RenderTile(0, 1.0f, 0, 0, 512, 512, 0);
                long tTile = Clock.Now();
                if (op.status != 0 || t.Status != 0) throw new Exception("restart failed");
                if (i > 0)   // first iteration is a warm-up
                {
                    detect.Add(Clock.Ms(tDetected - tKill)); spawn.Add(Clock.Ms(tHello - tDetected)); open.Add(Clock.Ms(tOpen - tHello)); tile.Add(Clock.Ms(tTile - tOpen)); total.Add(Clock.Ms(tTile - tKill));
                }
                victim = ep;
            }
            victim.Dispose();
            var sT = Stats.Of(total);
            Console.WriteLine($"[run {run}] kill -> respawn -> reopen 20-page -> first 512px tile  [{label}]  total ms: {sT}");
            Console.WriteLine($"          kill detected {Stats.Of(detect)} | spawn->Hello {Stats.Of(spawn)} | OpenDocument {Stats.Of(open)} | first tile {Stats.Of(tile)}");
            Console.WriteLine("   " + Cpu(cpu));
            sb.AppendLine($"| crash -> restored first tile (total) | {label} | {sT.Row()} |");
            sb.AppendLine($"|   kill detected (WaitForSingleObject) | {label} | {Stats.Of(detect).Row()} |");
            sb.AppendLine($"|   spawn -> Hello | {label} | {Stats.Of(spawn).Row()} |");
            sb.AppendLine($"|   OpenDocument (20 pages) | {label} | {Stats.Of(open).Row()} |");
            sb.AppendLine($"|   first 512px tile | {label} | {Stats.Of(tile).Row()} |");
            sb.AppendLine("    " + Cpu(cpu));
            json[$"restart/{label}"] = new { total = sT, spawn = Stats.Of(spawn), open = Stats.Of(open), tile = Stats.Of(tile) };
        }

        File.WriteAllText(Path.Combine(Env.Out, $"bench-run{run}.md"), sb.ToString(), new UTF8Encoding(false));
        Json.Save($"bench-run{run}.json", json);
        return 0;
    }
}
