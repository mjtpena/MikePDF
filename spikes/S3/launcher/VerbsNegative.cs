using System.Net;
using System.Net.Sockets;
using System.Text;
using Windows.Win32;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    // ---- mitigation flag shortcuts (values come from the CsWin32 projection of winbase.h)
    public const ulong M_DEP = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_DEP_ENABLE;
    public const ulong M_ASLR = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_BOTTOM_UP_ASLR_ALWAYS_ON | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_HIGH_ENTROPY_ASLR_ALWAYS_ON | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_FORCE_RELOCATE_IMAGES_ALWAYS_ON;
    public const ulong M_STRICT_HANDLE = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_STRICT_HANDLE_CHECKS_ALWAYS_ON;
    public const ulong M_WIN32K = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_WIN32K_SYSTEM_CALL_DISABLE_ALWAYS_ON;
    public const ulong M_EXTPOINT = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_EXTENSION_POINT_DISABLE_ALWAYS_ON;
    public const ulong M_DYNCODE = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_PROHIBIT_DYNAMIC_CODE_ALWAYS_ON;
    public const ulong M_CFG = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_ON;
    public const ulong M_MSONLY = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_BLOCK_NON_MICROSOFT_BINARIES_ALWAYS_ON;
    public const ulong M_FONT = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_FONT_DISABLE_ALWAYS_ON;
    public const ulong M_IMAGELOAD = PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_REMOTE_ALWAYS_ON | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_LOW_LABEL_ALWAYS_ON | PInvoke.PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_PREFER_SYSTEM32_ALWAYS_ON;
    public const ulong M2_CET = PInvoke.PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_ON;

    public const uint RT_SIGNATURE = 1, RT_CHILD = 2, RT_EXTPOINT = 4, RT_FONT = 8, RT_IMAGELOAD = 16, RT_DYNCODE = 32, RT_STRICTHANDLE = 64, RT_WIN32K = 128;

    public sealed record NegProfile(string Name, SpawnOptions Opt);

    public static List<NegProfile> NegProfiles(string eng)
    {
        SpawnOptions O(Isolation m, string l, string? e = null) => Base(m, l, e ?? eng);
        var hardenedAc = O(Isolation.AppContainer, "ac-hardened");
        hardenedAc.ChildProcessRestricted = true; hardenedAc.JobUiLimits = N.JOB_OBJECT_UILIMIT_ALL; hardenedAc.ExtraRuntimeMitigations = RT_SIGNATURE;
        hardenedAc.Mit1 = M_DEP | M_ASLR | M_STRICT_HANDLE | M_EXTPOINT | M_IMAGELOAD | M_CFG; hardenedAc.Mit2 = M2_CET;
        var lowPlain = O(Isolation.LowIL, "lowil", "scl"); lowPlain.UseJobList = false;
        var hardenedLow = O(Isolation.LowIL, "lowil-hardened", "scl"); hardenedLow.UseJobList = false;
        hardenedLow.ChildProcessRestricted = true; hardenedLow.JobUiLimits = N.JOB_OBJECT_UILIMIT_ALL; hardenedLow.ExtraRuntimeMitigations = RT_SIGNATURE;
        hardenedLow.Mit1 = M_DEP | M_ASLR | M_STRICT_HANDLE | M_EXTPOINT | M_IMAGELOAD | M_CFG; hardenedLow.Mit2 = M2_CET;
        return
        [
            new("baseline-none (control: no sandbox, same stub)", O(Isolation.None, "baseline-none")),
            new("ac-plain (zero-capability AppContainer + Job Object memory cap)", O(Isolation.AppContainer, "ac-plain")),
            new("ac-hardened (+ child-process policy, job UI limits, DEP/ASLR/strict-handle/ext-point/image-load/CFG/CET, runtime Microsoft-signed-only)", hardenedAc),
            new("lowil (restricted token: DISABLE_MAX_PRIVILEGE + Low IL, no restricting SIDs)", lowPlain),
            new("lowil-hardened (same extras as ac-hardened)", hardenedLow),
        ];
    }

    public static NegRequest MakeNegRequest(string engineDir, int tcpPort)
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string tmp = Path.GetTempPath().TrimEnd('\\');
        string probeDir = Path.Combine(tmp, "quire-s3-probe");
        Directory.CreateDirectory(probeDir);
        string tempDll = Path.Combine(probeDir, "temp-copy.dll");
        File.Copy(Path.Combine(engineDir, "pdfium.dll"), tempDll, true);
        return new NegRequest(docs, tmp, Env.RepoRoot, @"\\localhost\c$", tcpPort, "1.1.1.1", 443, Environment.ProcessId, tempDll,
            Path.Combine(engineDir, "rogue-copy.dll"), engineDir, Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
    }

    public sealed record NegOutcome(string Profile, string? StartError, string? ExitInfo, Dictionary<string, string> Hello, List<NegResult> Results, List<(string, int)> RuntimeMit);

    public static NegOutcome RunNegative(NegProfile p, int tcpPort)
    {
        using var ep = new EngineProcess(p.Opt);
        try { ep.Start(); }
        catch (Exception ex) { return new NegOutcome(p.Name, ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? "").Split((char)10).FirstOrDefault()?.Trim() + " | stderr: " + ep.StdErrText(), null, ep.Hello, [], []); }
        var req = MakeNegRequest(p.Opt.EngineDir, tcpPort);
        var w = new PayloadWriter().Str(req.Documents).Str(req.Temp).Str(req.RepoDir).Str(req.UncPath).I32(req.TcpLoopbackPort).Str(req.PublicIp).I32(req.PublicPort).I32(req.LauncherPid).Str(req.TempDll).Str(req.LocalRogueDll).Str(req.EngineDir).Str(req.FontsDir);
        var r = ep.Call(Msg.RunNegative, w, 120000);
        int st = r.I32(); int n = r.I32();
        var list = new List<NegResult>();
        for (int i = 0; i < n; i++) list.Add(new NegResult(r.Str(), r.Str(), r.Str(), r.I32(), r.Str()));
        // After the suite: is the engine still functional? (a sandbox that kills the engine on every call is not a sandbox we can use)
        string post = "";
        try { var o = ep.OpenDocument(0); post = $"post-suite open status={o.status}"; var t = ep.RenderTile(0, 1f, 0, 0, 256, 256, 0); post += $", render status={t.Status}"; var g = ep.Call(Msg.GdiProbe, null); g.I32(); int gn = g.I32(); int gf = 0; for (int i = 0; i < gn; i++) if (g.Str().Contains("FAIL")) gf++; post += $", GDI probe failures={gf}, pipe client pid verified={ep.ClientPidVerified}"; }
        catch (Exception ex) { post = "post-suite call failed: " + ex.Message; }
        return new NegOutcome(p.Name, null, post, ep.Hello, list, []);
    }

    public static int Negative(string[] a)
    {
        string eng = Opt(a, "--engine", "fdd");
        PrepareDirs("fdd"); PrepareDirs("sc"); PrepareDirs("scl");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepter = new Thread(() => { try { while (true) listener.AcceptTcpClient().Dispose(); } catch { } }) { IsBackground = true };
        accepter.Start();
        var outcomes = new List<NegOutcome>();
        string only = Opt(a, "--only", "");
        foreach (var p in NegProfiles(eng))
        {
            if (only != "" && !p.Name.StartsWith(only)) continue;
            Console.WriteLine($"\n=== {p.Name}");
            var o = RunNegative(p, port);
            outcomes.Add(o);
            if (o.StartError != null) { Console.WriteLine("  START FAILED: " + o.StartError); continue; }
            Console.WriteLine($"  token: AC={o.Hello.GetValueOrDefault("isAppContainer")} integrity={o.Hello.GetValueOrDefault("integrityRid")} caps={o.Hello.GetValueOrDefault("capabilityCount")} privs={o.Hello.GetValueOrDefault("privilegeCount")} restrictedSids={o.Hello.GetValueOrDefault("restrictedSidCount")}  {o.ExitInfo}");
            foreach (var r in o.Results)
                Console.WriteLine($"  [{(r.Expect == "deny" ? (r.Outcome == "ALLOWED" ? "FAIL" : "ok  ") : (r.Outcome == "ALLOWED" ? "ok  " : "FAIL"))}] {r.Name,-70} {r.Outcome,-8} win32={r.Win32,-6} {r.Detail}");
        }
        listener.Stop();
        Json.Save("negative.json", outcomes);
        File.WriteAllText(Path.Combine(Env.Out, "negative.md"), NegMarkdown(outcomes), new UTF8Encoding(false));
        return 0;
    }

    static string NegMarkdown(List<NegOutcome> outcomes)
    {
        var sb = new StringBuilder();
        var names = outcomes.SelectMany(o => o.Results).Select(r => r.Name).Distinct().ToList();
        sb.Append("| Test | Expect |");
        foreach (var o in outcomes) sb.Append($" {o.Profile.Split(' ')[0]} |");
        sb.AppendLine();
        sb.Append("|---|---|"); foreach (var _ in outcomes) sb.Append("---|"); sb.AppendLine();
        foreach (var n in names)
        {
            string exp = outcomes.SelectMany(o => o.Results).First(r => r.Name == n).Expect;
            sb.Append($"| {n} | {exp} |");
            foreach (var o in outcomes)
            {
                var r = o.Results.FirstOrDefault(x => x.Name == n);
                sb.Append(r == null ? " n/a |" : $" {r.Outcome}{(r.Outcome == "ALLOWED" ? "" : " (" + r.Win32 + ")")} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
