using A11yTest;
using System.IO;
using S7.Core.Engine;
using S7.Core.Fixtures;
using S7.Core.Text;

string fixtures = Paths.Fixtures;
switch (args.FirstOrDefault())
{
    case "gen":
        FixtureGenerator.WriteAll(fixtures);
        Console.WriteLine("wrote fixtures to " + fixtures);
        break;
    case "dump":
        Dump(args[1]);
        break;
    case "model-tests":
        ModelTests.Run();
        Results.WriteMarkdown(Path.Combine(Paths.Out, "model-results.md"), "S7 model-level results");
        Console.WriteLine($"{Results.All.Count} checks, {Results.Failures} failures");
        return Results.Failures == 0 ? 0 : 1;
    case "uia-tests":
        if (args.Length > 3) UiaTests.Zoom = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        UiaTests.Run(args.Length > 1 && args[1] != "-" ? args[1] : null, args.Length > 2 && args[2] != "-" ? int.Parse(args[2]) : null);
        Results.WriteMarkdown(Path.Combine(Paths.Out, "uia-results.md"), "S7 UIA client results");
        Console.WriteLine($"{Results.All.Count} checks, {Results.Failures} failures");
        return Results.Failures == 0 ? 0 : 1;
    case "sweep":
        Sweep.Run(args.Length > 1 ? args[1] : Path.Combine(Paths.Root, "..", "_shared", "corpus", "pdfium-resources"));
        Results.WriteMarkdown(Path.Combine(Paths.Out, "sweep-results.md"), "S7 corpus sweep");
        return Results.Failures == 0 ? 0 : 1;
    case "model":
        Model(args[1]);
        break;
    default:
        Console.WriteLine("usage: gen | dump <pdf> | model <pdf> | model-tests | uia-tests");
        break;
}
return 0;

static void Dump(string path)
{
    using var e = PdfiumEngine.Open(path);
    var p = e.ExtractPage(0);
    Console.WriteLine($"tagged={p.IsTagged} rotate={p.Box.Rotate} media={p.Box.Media} crop={p.Box.Crop} chars={p.Chars.Count}");
    Console.WriteLine("raw: " + Esc(p.PdfiumRawText));
    foreach (var c in p.Chars)
    {
        string t = c.Text.Length == 0 ? "<none>" : Esc(c.Text);
        Console.WriteLine($"{c.Index,3} U+{(c.Text.Length > 0 ? char.ConvertToUtf32(c.Text, 0) : 0):X4} '{t}' gen={(c.Generated ? 1 : 0)} hy={(c.Hyphen ? 1 : 0)} mcid={c.Mcid} fs={c.FontSize:F1} box=({c.Box.Left:F1},{c.Box.Bottom:F1},{c.Box.Right:F1},{c.Box.Top:F1}) loose=({c.Loose.Left:F1},{c.Loose.Bottom:F1},{c.Loose.Right:F1},{c.Loose.Top:F1})");
    }
    foreach (var b in p.StructBlocks) Console.WriteLine($"block {b.Type} mcids=[{string.Join(",", b.Mcids)}] alt={b.Alt}");
}

static string Esc(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u0002", "\\x02");

static void Model(string path)
{
    using var e = PdfiumEngine.Open(path);
    var m = TextModelBuilder.Build(e.ExtractPage(0));
    Console.WriteLine($"order source: {m.OrderSource}; atoms={m.Length}");
    foreach (var u in new[] { TextUnitKind.Paragraph, TextUnitKind.Line, TextUnitKind.Word })
    {
        Console.WriteLine("--- " + u);
        var b = m.Boundaries(u);
        for (int i = 0; i + 1 < b.Length; i++) Console.WriteLine($"[{b[i]},{b[i + 1]}) '{Esc(m.GetText(b[i], b[i + 1]))}'");
    }
}

static class Paths
{
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !(d.Name == "S7" && d.Parent?.Name == "spikes")) d = d.Parent;
            return d!.FullName;
        }
    }
    public static string Fixtures => Path.Combine(Root, "fixtures");
    public static string Out => Path.Combine(Root, "out");
}
