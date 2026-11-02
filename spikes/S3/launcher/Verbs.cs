using System.Text;

namespace Quire.S3.Launcher;

public static unsafe partial class Verbs
{
    static string Opt(string[] a, string name, string def) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }
    static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

    public static SpawnOptions Base(Isolation mode, string label, string engine = "fdd") => new()
    {
        Mode = mode, Label = label, EngineDir = Env.EngineDirFor(engine),
        ProcessMemoryLimit = 2L << 30,                           // §5.2 rule 5: 2 GB default
        FilePath = Env.Fixture("hello_world.pdf"),
    };

    public static int Smoke(string[] a)
    {
        var mode = Enum.Parse<Isolation>(Opt(a, "--mode", "AppContainer"), true);
        string eng = Opt(a, "--engine", "fdd");
        PrepareDirs(eng);
        using var ep = new EngineProcess(Base(mode, "smoke", eng));
        try
        {
            ep.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("START FAILED: " + ex.Message);
            Console.WriteLine("stderr: " + ep.StdErrText());
            return 1;
        }
        Console.WriteLine($"hello ok: spawn->connect {Clock.Ms(ep.TConnected - ep.TCreated):F1} ms, ->hello {Clock.Ms(ep.THelloDone - ep.TCreateStart):F1} ms");
        Console.WriteLine($"engine-side timeline (ms): Main->PDFium ready {Clock.Ms(ep.EPdfium - ep.EMain):F1}, ->pipe connected {Clock.Ms(ep.EPipe - ep.EPdfium):F1}, ->Hello built {Clock.Ms(ep.EHello - ep.EPipe):F1}; broker: connected->Hello reply {Clock.Ms(ep.THelloDone - ep.TConnected):F1}");
        foreach (var kv in ep.Hello) Console.WriteLine($"  {kv.Key} = {kv.Value}");
        var o = ep.OpenDocument(0);
        Console.WriteLine($"open: status={o.status} pages={o.pages} size={o.w}x{o.h} err={o.pdfErr} {o.note} ({o.ms:F2} ms)");
        var t = ep.RenderTile(0, 1.0f, 0, 0, 512, 512, 0);
        Console.WriteLine($"render: {t}");
        var px = ep.SlotPixels(0, 512, 512);
        int nonWhite = 0; for (int i = 0; i < px.Length; i += 4) if (px[i] != 255 || px[i + 1] != 255 || px[i + 2] != 255) nonWhite++;
        Console.WriteLine($"non-white pixels: {nonWhite}");
        var g = ep.Call(Msg.GdiProbe, null); g.I32(); int n = g.I32(); for (int i = 0; i < n; i++) Console.WriteLine("  gdi: " + g.Str());
        return 0;
    }

    /// <summary>Make sure the container can read the engine directory (inside the repo), once per profile.</summary>
    public static void PrepareDirs(string engineKind)
    {
        string dir = Env.EngineDirFor(engineKind);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir + " (run publish.ps1 first)");
        AppContainerProfile.EnsureCreated();
        if (engineKind != "scl") AppContainerProfile.GrantReadExecute(dir, AppContainerProfile.Sid);   // never on the Low-IL copy
        string rogue = Path.Combine(dir, "rogue-copy.dll");
        if (!File.Exists(rogue)) File.Copy(Path.Combine(dir, "pdfium.dll"), rogue);
    }

    public static int Cleanup() { AppContainerProfile.Delete(); return 0; }
}
