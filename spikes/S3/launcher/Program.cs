using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Quire.S3.Launcher;

public static class Env
{
    public static readonly string RepoRoot = FindRoot();
    public static readonly string S3 = Path.Combine(RepoRoot, "spikes", "S3");
    public static readonly string Out = Path.Combine(S3, "out");
    public static readonly string EngineFdd = Path.Combine(Out, "engine-fdd");
    public static readonly string EngineSc = Path.Combine(Out, "engine-sc");
    public static readonly string Corpus = Path.Combine(RepoRoot, "spikes", "_shared", "corpus", "pdfium-resources");
    public static readonly string Gen = Path.Combine(Out, "gen");        // generated fixtures (read by the broker only; handed to the engine as handles)

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "docs", "MASTER-PROMPT.md"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static string Fixture(string name) => Path.Combine(Corpus, name);

    public static readonly string EngineScLow = Path.Combine(Out, "engine-sc-low");   // same bits as engine-sc but WITHOUT the AppContainer ACE (that ACE breaks the Low-IL apphost, see report)
    public static string EngineDirFor(string kind) => kind switch { "cpp" => Path.Combine(Out, "engine-cpp"), "cppsigned" => Path.Combine(Out, "engine-cpp-signed"), "sc" => EngineSc, "scl" => EngineScLow, _ => EngineFdd };
}

public static class Program
{
    public static int Main(string[] args)
    {
        string verb = args.Length > 0 ? args[0] : "help";
        string[] rest = args.Skip(1).ToArray();
        Directory.CreateDirectory(Env.Out);
        if (Environment.GetEnvironmentVariable("S3_PRIORITY") == "high") Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        try
        {
            return verb switch
            {
                "smoke" => Verbs.Smoke(rest),
                "negative" => Verbs.Negative(rest),
                "mitigations" => Verbs.Mitigations(rest),
                "memlimit" => Verbs.MemLimit(rest),
                "fonts" => Verbs.Fonts(rest),
                "bench" => Verbs.Bench(rest),
                "msgpack" => Verbs.MsgPackBench(rest),
                "cleanup" => Verbs.Cleanup(),
                "lowildiag" => Verbs.LowIlDiag(rest),
                "pipeacl" => Verbs.PipeAcl(rest),
                "cpp" => Verbs.Cpp(rest),
                "cppbench" => Verbs.CppBench(rest),
                _ => Help()
            };
        }
        finally
        {
            try { AppContainerProfile.Delete(); } catch (Exception ex) { Console.WriteLine("[sandbox] profile cleanup failed: " + ex.Message); }
        }
    }

    static int Help()
    {
        Console.WriteLine("quire-s3 <smoke|negative|mitigations|memlimit|fonts|bench|msgpack|cleanup> [options]");
        return 1;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static void Save(string name, object o)
    {
        File.WriteAllText(Path.Combine(Env.Out, name), JsonSerializer.Serialize(o, Opts), new UTF8Encoding(false));
    }
}
