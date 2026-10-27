using System.Security.Principal;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    /// <summary>Does the pipe DACL (current user + this AppContainer SID) actually exclude everybody else?</summary>
    public static int PipeAcl(string[] a)
    {
        PrepareDirs("fdd");
        AppContainerProfile.EnsureSecond();
        AppContainerProfile.GrantReadExecute(Env.EngineFdd, new SecurityIdentifier(AppContainerProfile.Sid2String));   // so any failure below is about the pipe, not the exe
        var cases = new (string name, Action<SpawnOptions> cfg, bool expectConnect)[]
        {
            ("DACL = user + engine's own AppContainer SID (the design)", o => { }, true),
            ("DACL = user only (AppContainer SID omitted)", o => o.OmitAcFromPipeDacl = true, false),
            ("child runs in a DIFFERENT AppContainer than the DACL names", o => o.ChildUsesSecondContainer = true, false),
        };
        foreach (var (name, cfg, expect) in cases)
        {
            var o = Base(Isolation.AppContainer, name); o.FilePath = null; cfg(o);
            using var ep = new EngineProcess(o);
            string outcome;
            try { ep.Start(8000); outcome = $"CONNECTED (client pid verified={ep.ClientPidVerified}); pipe SDDL {ep.PipeSddl}"; }
            catch (Exception ex) { outcome = $"NOT connected: {ex.Message.Replace("\r", " ").Replace("\n", " ")}"; }
            bool connected = outcome.StartsWith("CONNECTED");
            Console.WriteLine($"[{(connected == expect ? "ok  " : "FAIL")}] {name,-70} {outcome}");
        }
        return 0;
    }
}
