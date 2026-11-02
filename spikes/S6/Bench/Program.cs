using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

// S6.2 startup bench harness.
// usage: Bench <exe> <label> <warmRuns> <csvOut> [--copy-fresh] [--launcher <alias-or-exe>] [extra app args...]
// For every run: T0 = QPC taken immediately before Process.Start, passed to the app in QUIRE_T0. The app records QPC
// milestones and writes them (as ms since T0) to QUIRE_TIMING_OUT when the first composition frame has been produced.
// Run 1 is labelled "first" (first launch of a freshly copied tree), runs 2..N+1 "warm".
// Also logs system-wide CPU busy% during each run (GetSystemTimes) so other agents' load is visible in the data.

static class P
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    static (long idle, long total) Cpu()
    {
        GetSystemTimes(out var i, out var k, out var u);
        return (i, k + u);
    }

    static int Main(string[] a)
    {
        string exe = a[0], label = a[1]; int warm = int.Parse(a[2]); string csv = a[3];
        bool copyFresh = a.Contains("--copy-fresh");
        bool poll = exe.EndsWith("quires6.exe", StringComparison.OrdinalIgnoreCase);
        var extra = new List<string>();
        for (int i = 4; i < a.Length; i++) if (!a[i].StartsWith("--copy-fresh")) extra.Add(a[i]);

        if (copyFresh)
        {
            var src = Path.GetDirectoryName(Path.GetFullPath(exe))!;
            var dst = Path.Combine(Path.GetTempPath(), "s6bench-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
            CopyDir(src, dst);
            exe = Path.Combine(dst, Path.GetFileName(exe));
            Console.WriteLine("fresh copy: " + dst);
        }

        var rows = new List<string> { "label,phase,run,main_ms,activated_ms,first_render_ms,idle_after_render_ms,sys_cpu_pct" };
        var firstRender = new List<double>(); var warmRender = new List<double>(); var warmIdle = new List<double>();
        for (int run = 0; run <= warm; run++)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "s6timing-" + Guid.NewGuid().ToString("N") + ".csv");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--bench");
            foreach (var e in extra) psi.ArgumentList.Add(e);
            var (i0, t0c) = Cpu();
            long t0 = Stopwatch.GetTimestamp();
            psi.Environment["QUIRE_T0"] = t0.ToString(CultureInfo.InvariantCulture);
            psi.Environment["QUIRE_TIMING_OUT"] = tmp;
            using var p = Process.Start(psi)!;
            if (poll)
            {
                // execution-alias launch: the alias stub may exit immediately; wait for the timing file instead and let the real process exit.
                for (int w = 0; w < 400 && !File.Exists(tmp); w++) Thread.Sleep(50);
                Thread.Sleep(150);
                if (!File.Exists(tmp)) { Console.WriteLine($"run {run}: TIMEOUT (alias)"); foreach (var hp in Process.GetProcessesByName("HelloQuire")) hp.Kill(true); continue; }
                for (int w = 0; w < 100 && Process.GetProcessesByName("HelloQuire").Length > 0; w++) Thread.Sleep(50);
            }
            else if (!p.WaitForExit(20000)) { p.Kill(true); Console.WriteLine($"run {run}: TIMEOUT"); continue; }
            var (i1, t1c) = Cpu();
            double cpu = 100.0 * (1.0 - (double)(i1 - i0) / Math.Max(1, t1c - t0c));
            if (!File.Exists(tmp)) { Console.WriteLine($"run {run}: no timing file"); continue; }
            var m = File.ReadAllLines(tmp).Select(l => l.Split(',')).ToDictionary(x => x[0], x => double.Parse(x[1], CultureInfo.InvariantCulture));
            double Get(string k) => m.TryGetValue(k, out var v) ? v : double.NaN;
            double act = m.Where(kv => kv.Key.StartsWith("Window.Activated.")).Select(kv => kv.Value).DefaultIfEmpty(double.NaN).Min();
            double main = Get("Main"), fr = Get("CompositionTarget.Rendering.first"), idle = Get("Dispatcher.idle.afterFirstRender");
            string phase = run == 0 ? "first" : "warm";
            rows.Add(string.Join(',', label, phase, run, F(main), F(act), F(fr), F(idle), F(cpu)));
            if (run == 0) firstRender.Add(fr); else { warmRender.Add(fr); warmIdle.Add(idle); }
            File.Delete(tmp);
            Thread.Sleep(400);
        }
        File.WriteAllLines(csv, rows);
        Console.WriteLine($"{label}: first-run first_render={F(firstRender.FirstOrDefault())} ms");
        Console.WriteLine($"{label}: warm n={warmRender.Count} first_render p50={F(Pct(warmRender, .5))} p95={F(Pct(warmRender, .95))} max={F(warmRender.DefaultIfEmpty().Max())} min={F(warmRender.DefaultIfEmpty().Min())}");
        Console.WriteLine($"{label}: warm n={warmIdle.Count} idle_after_render p50={F(Pct(warmIdle, .5))} p95={F(Pct(warmIdle, .95))} max={F(warmIdle.DefaultIfEmpty().Max())}");
        return 0;
    }

    static string F(double d) => d.ToString("F1", CultureInfo.InvariantCulture);
    static double Pct(List<double> v, double q)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        double pos = q * (s.Count - 1); int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
    static void CopyDir(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        foreach (var sub in Directory.GetDirectories(s)) CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)));
    }
}
