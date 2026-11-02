using System.IO;
using System.Text;

namespace A11yTest;

public sealed record CheckResult(string Suite, string Fixture, string Rotation, string Check, bool Pass, string Detail);

public static class Results
{
    private static readonly List<CheckResult> List = new();
    public static IReadOnlyList<CheckResult> All => List;

    public static void Add(string suite, string fixture, string rotation, string check, bool pass, string detail = "")
    {
        lock (List) List.Add(new CheckResult(suite, fixture, rotation, check, pass, detail));
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {suite} | {Short(fixture)} | {rotation} | {check}{(detail.Length > 0 ? " | " + detail : "")}");
    }

    public static string Short(string f) => f.Length > 0 ? f[..1] : f;

    public static void Equal(string suite, string fixture, string rotation, string check, string expected, string actual)
    {
        bool ok = expected == actual;
        Add(suite, fixture, rotation, check, ok, ok ? "" : $"expected '{Esc(expected)}' actual '{Esc(actual)}'");
    }

    public static void SequenceEqual(string suite, string fixture, string rotation, string check, IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        bool ok = expected.Count == actual.Count && expected.Zip(actual).All(p => p.First == p.Second);
        string detail = ok ? $"{expected.Count} items" : $"expected {expected.Count} items, actual {actual.Count}; first diff: {FirstDiff(expected, actual)}";
        Add(suite, fixture, rotation, check, ok, detail);
    }

    private static string FirstDiff(IReadOnlyList<string> e, IReadOnlyList<string> a)
    {
        for (int i = 0; i < Math.Max(e.Count, a.Count); i++)
        {
            string x = i < e.Count ? e[i] : "<none>", y = i < a.Count ? a[i] : "<none>";
            if (x != y) return $"#{i} expected '{Esc(x)}' actual '{Esc(y)}'";
        }
        return "";
    }

    public static string Esc(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    public static int Failures => List.Count(r => !r.Pass);

    public static void WriteMarkdown(string path, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"{List.Count(r => r.Pass)} passed, {Failures} failed, {List.Count} checks.");
        sb.AppendLine();
        sb.AppendLine("| Suite | Fixture | Rotation | Check | Result | Detail |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in List)
            sb.AppendLine($"| {r.Suite} | {r.Fixture} | {r.Rotation} | {r.Check} | {(r.Pass ? "pass" : "**FAIL**")} | {r.Detail.Replace("|", "\\|")} |");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
    }
}
