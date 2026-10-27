using System.Globalization;
using System.Text;

namespace Quire.ProtocolGen;

/// <summary>Produces the checked-in protocol code from the schema, and verifies that it is current.</summary>
public static class Generator
{
    public const string SchemaPath = "src/Quire.Protocol/schema/quire.protocol.json";

    // Directories whose generated files are owned by this tool: anything in them it does not produce is stale.
    private static readonly (string Dir, string Pattern)[] OwnedDirs =
    [
        ("src/Quire.Protocol/generated", "*.g.cs"),
        ("src/Quire.Protocol/generated/cpp/quire/protocol", "*"),
    ];

    /// <summary>Finds the repository root (the directory holding Quire.slnx) at or above <paramref name="start"/>.</summary>
    public static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Quire.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Quire.slnx not found above " + start);
    }

    public static Schema LoadSchema(string root) => Schema.Parse(File.ReadAllText(Path.Combine(root, SchemaPath), Encoding.UTF8));

    /// <summary>The complete expected output: repository-relative path (forward slashes) to LF-terminated content.</summary>
    public static IReadOnlyDictionary<string, string> Render(Schema schema)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return CSharpEmitter.Emit(schema).Concat(CppEmitter.Emit(schema)).ToDictionary(o => o.Path, o => o.Content);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    public static void Generate(string root)
    {
        var outputs = Render(LoadSchema(root));
        foreach (var (path, content) in outputs)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        foreach (var orphan in Orphans(root, outputs)) File.Delete(Path.Combine(root, orphan));
    }

    /// <summary>Returns a description of every stale, missing or orphaned generated file. Empty means up to date.</summary>
    public static IReadOnlyList<string> Check(string root)
    {
        var outputs = Render(LoadSchema(root));
        var problems = new List<string>();
        foreach (var (path, content) in outputs)
        {
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) problems.Add($"missing: {path}");
            // git may check text files out with CRLF; the generator always writes LF.
            else if (File.ReadAllText(full, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal) != content) problems.Add($"stale: {path}");
        }

        problems.AddRange(Orphans(root, outputs).Select(o => $"orphan: {o}"));
        return problems;
    }

    private static IEnumerable<string> Orphans(string root, IReadOnlyDictionary<string, string> outputs)
    {
        foreach (var (dir, pattern) in OwnedDirs)
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full, pattern, SearchOption.TopDirectoryOnly))
            {
                var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!outputs.ContainsKey(rel)) yield return rel;
            }
        }
    }
}
