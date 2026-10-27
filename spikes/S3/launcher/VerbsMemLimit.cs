using System.Text;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    public static int MemLimit(string[] a)
    {
        PrepareDirs("fdd");
        var rows = new StringBuilder();
        rows.AppendLine("| Case | Limit | Engine result | Committed at failure (MB) | Win32 error | Job notifications (t+ms from request) | Engine alive after? | Exit code |");
        rows.AppendLine("|---|---|---|---|---|---|---|---|");
        var cases = new (string name, long procLimit, long jobLimit, int mode)[]
        {
            ("native VirtualAlloc, per-process limit", 512L << 20, 0, 0),
            ("native VirtualAlloc, per-process limit", 512L << 20, 0, 0),
            ("native VirtualAlloc, job-wide limit", 0, 512L << 20, 0),
            ("managed byte[] (GC hard limit auto-derived from the job)", 512L << 20, 0, 1),
            ("managed byte[], job-wide limit", 0, 512L << 20, 1),
            ("native VirtualAlloc, production default (2 GiB per process)", 2L << 30, 0, 0),
            ("no limit configured (control; stopped by the engine at 3 GiB)", 0, 0, 2),
        };
        foreach (var c in cases)
        {
            var o = Base(Isolation.AppContainer, "memlimit", "fdd");
            o.ProcessMemoryLimit = c.procLimit; o.JobMemoryLimit = c.jobLimit;
            using var ep = new EngineProcess(o);
            ep.Start();
            ep.OpenDocument(0);
            long t0 = Clock.Now();
            string result; ulong committed = 0; int err = 0;
            try
            {
                var r = ep.Call(Msg.AllocUntilLimit, new PayloadWriter().I32(c.mode == 2 ? 0 : c.mode).I32(16), 120000);
                r.I32(); committed = r.U64(); err = r.I32(); string what = r.Str();
                result = "engine survived and reported: " + what;
            }
            catch (Exception ex) { result = "engine died: " + ex.GetType().Name; }
            double tResp = Clock.Ms(Clock.Now() - t0);
            Thread.Sleep(300);
            ulong shmCommitted = ep.Shm != null ? *(ulong*)(ep.Shm + Quire.S3.Shm.OffCommittedMB) : 0;
            bool alive = !ep.HasExited; string post = "";
            if (alive)
            {
                try { ep.Call(Msg.Ping, null); var t = ep.RenderTile(0, 1f, 0, 0, 200, 200, 0); post = $"yes (ping ok, render status {t.Status})"; }
                catch (Exception ex) { post = "alive but unresponsive: " + ex.Message; }
            }
            else post = "no";
            string events; lock (ep.JobEvents) events = string.Join(", ", ep.JobEvents.Where(e => e.Name is not ("NEW_PROCESS")).Select(e => $"{e.Name}@+{Clock.Ms(e.Ticks - t0):F0}"));
            string exit = ep.HasExited ? $"0x{ep.ExitCode:X}" : "-";
            string lim = c.procLimit > 0 ? $"process {c.procLimit >> 20} MB" : c.jobLimit > 0 ? $"job {c.jobLimit >> 20} MB" : "none";
            Console.WriteLine($"{c.name,-62} limit={lim,-16} -> {result}; committed={(committed != 0 ? committed : shmCommitted)} MB err={err} (response after {tResp:F0} ms); notifications: [{events}]; alive after: {post}; exit={exit}");
            rows.AppendLine($"| {c.name} | {lim} | {result} | {(committed != 0 ? committed : shmCommitted)} | {err} | {(events.Length > 0 ? events : "none")} | {post} | {exit} |");
        }
        File.WriteAllText(Path.Combine(Env.Out, "memlimit.md"), rows.ToString(), new UTF8Encoding(false));
        return 0;
    }
}
