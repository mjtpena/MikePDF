// S11 harness. Usage:
//   S11.Ops run   <fixturesDir> <outDir>        apply every PDFium page operation, evaluate the results, write results.json and the output PDFs
//   S11.Ops pdfjs <outDir>                      read outDir/pdfjs.jsonl (from pdfjs/inspect.mjs over the outputs) and compare with PDFium's view
using System.Text.Json;
using S4;
using S11;

if (args.Length < 2) { Console.WriteLine("see header of Program.cs"); return 1; }
Qpdf.Exe = FindQpdf();
PdfDoc.Init();
switch (args[0])
{
    case "run": return Runner.Run(args[1], args[2]);
    case "pdfjs": return Runner.PdfJs(args[1]);
    case "perf": return Perf.Run(args[1], args[2]);
    case "perf-one": return Perf.One(args[1], args[2]);
    case "corpus-one": return Corpus.One(args[1]);
    case "corpus": return Corpus.Run(args[1], args[2]);
    case "assemble": return Assembler.Cli(args[1..]);
    case "inspect": return Runner.Inspect(args[1]);
    default: Console.Error.WriteLine("unknown command"); return 1;
}

static string FindQpdf()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var cand = Path.Combine(dir.FullName, "spikes", "_shared", "tools", "qpdf", "qpdf-12.4.2-msvc64", "bin", "qpdf.exe");
        if (File.Exists(cand)) return cand;
        dir = dir.Parent;
    }

    throw new FileNotFoundException("qpdf.exe not found under spikes/_shared/tools");
}

internal static class Runner
{
    public static int Run(string fixtures, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var xs = Path.Combine(fixtures, "A.xs.pdf");
        if (!File.Exists(xs))
        {
            var (code, text) = Qpdf.Run("--object-streams=generate", "--stream-data=preserve", Path.Combine(fixtures, "A.pdf"), xs);
            if (code > 3) throw new InvalidOperationException("qpdf could not make A.xs.pdf: " + text);
        }

        var sources = new Dictionary<string, Profile>();
        foreach (var n in new[] { "A", "B", "C" }) sources[n] = Profile.Read(File.ReadAllBytes(Path.Combine(fixtures, n + ".pdf")));
        foreach (var (n, p) in sources) Console.WriteLine($"source {n}: {p.PageCount} pages, {p.Outline.Count} outline entries, {p.Named.Count} named dests, catalog: {p.Catalog}");

        var results = new List<Dictionary<string, object?>>();
        foreach (var c in Scenarios.All(fixtures, outDir).Concat(Scenarios.AssemblerCases(fixtures)))
        {
            var file = $"{c.Name}.{c.Mode}.pdf";
            Dictionary<string, object?> row = new() { ["case"] = c.Name, ["mode"] = c.Mode, ["description"] = c.Description, ["file"] = file, ["notes"] = c.Notes, ["bytes"] = c.Output.Length };
            if (c.Output.Length == 0) { row["fields"] = new Dictionary<string, string> { ["open"] = "no output: " + c.Notes }; results.Add(row); Console.WriteLine($"{c.Name,-22} {c.Mode,-5} NO OUTPUT {c.Notes}"); continue; }
            File.WriteAllBytes(Path.Combine(outDir, file), c.Output);
            var src = c.Sources.ToDictionary(s => s, s => sources[s]);
            // sources are consulted in the order given (first definition wins for clashing names)
            var ev = Eval.Evaluate(c.Output, c.PrefixBase, src, c.ExpectKeys, c.Rot, compareRender: true);
            row["fields"] = ev.Fields;
            row["catalog"] = Profile.Read(c.Output).Catalog.ToString();
            results.Add(row);
            Console.WriteLine($"{c.Name,-22} {c.Mode,-5} {c.Output.Length,9} B  " + string.Join("  ", ev.Fields.Select(kv => $"{kv.Key}={(kv.Value.Length > 60 ? kv.Value[..60] + "..." : kv.Value)}")));
        }

        File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int Inspect(string file)
    {
        var p = Profile.Read(File.ReadAllBytes(file));
        Console.WriteLine($"{file}: opened={p.Opened} pages={p.PageCount} catalog: {p.Catalog}");
        for (var i = 0; i < p.Pages.Count; i++)
        {
            var pg = p.Pages[i];
            Console.WriteLine($"  [{i}] {pg.Key} rot={pg.Rotation} label='{pg.Label}' annots={string.Join(",", pg.AnnotCounts.Select(kv => kv.Key + "x" + kv.Value))} struct={pg.StructChildren}");
            Console.WriteLine($"       links: {string.Join(" | ", pg.Links)}");
            if (pg.Widgets.Count > 0) Console.WriteLine($"       widgets: {string.Join(" | ", pg.Widgets.Select(w => $"{w.Name}/{w.Type}='{w.Value}'"))}");
        }

        foreach (var o in p.Outline) Console.WriteLine($"  outline {new string(' ', o.Depth * 2)}{o.Title} -> {o.Target}");
        foreach (var n in p.Named) Console.WriteLine($"  named {n.Key} -> {n.Value}");
        return 0;
    }

    static string N(string? t) => t is null ? "" : (t.StartsWith("unresolved", StringComparison.Ordinal) || t == "none") ? "inert" : t;   // both readers say 'this goes nowhere'; the wording differs

    public static int PdfJs(string outDir)
    {
        var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "results.json"))).RootElement;
        var js = File.ReadAllLines(Path.Combine(outDir, "pdfjs.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(e => Path.GetFileName(e.GetProperty("file").GetString()!), e => e);
        var summary = new List<Dictionary<string, string>>();
        foreach (var r in results.EnumerateArray())
        {
            var file = r.GetProperty("file").GetString()!;
            var row = new Dictionary<string, string> { ["file"] = file };
            if (!js.TryGetValue(file, out var j)) { row["pdfjs"] = "not run"; summary.Add(row); continue; }
            if (!j.GetProperty("ok").GetBoolean()) { row["pdfjs"] = "FAILED to open: " + j.GetProperty("error").GetString(); summary.Add(row); continue; }
            var bytes = File.ReadAllBytes(Path.Combine(outDir, file));
            var p = Profile.Read(bytes);
            var notes = new List<string>();
            var pages = j.GetProperty("pages").EnumerateArray().ToList();
            if (pages.Count != p.PageCount) notes.Add($"page count {pages.Count} vs PDFium {p.PageCount}");
            var jsKeys = pages.Select(x => x.GetProperty("key").ValueKind == JsonValueKind.String ? x.GetProperty("key").GetString() : null).ToList();
            if (!jsKeys.SequenceEqual(p.Pages.Select(x => x.Key))) notes.Add("page order differs");
            var jsOutline = j.GetProperty("outline").EnumerateArray().Select(x => (x.GetProperty("title").GetString(), N(x.GetProperty("target").GetString()))).ToList();
            var pdOutline = p.Outline.Select(x => (x.Title, N(x.Target))).ToList();
            if (!jsOutline.SequenceEqual(pdOutline)) notes.Add($"outline differs (pdf.js {jsOutline.Count} entries, PDFium {pdOutline.Count})");
            var jsNamed = j.GetProperty("namedDests").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
            var nameDiff = p.Named.Count(kv => !jsNamed.TryGetValue(kv.Key, out var v) || N(v) != N(kv.Value));
            if (nameDiff > 0) notes.Add($"{nameDiff} named dest(s) resolve differently");
            var linkDiff = 0;
            for (var i = 0; i < Math.Min(pages.Count, p.PageCount); i++)
            {
                var jl = pages[i].GetProperty("links").EnumerateArray().Select(x => N(x.GetString())).OrderBy(x => x).ToList();
                var pl = p.Pages[i].Links.Select(N).OrderBy(x => x).ToList();
                if (!jl.SequenceEqual(pl)) linkDiff++;
            }

            if (linkDiff > 0) notes.Add($"links differ on {linkDiff} page(s)");
            var labels = j.GetProperty("labels");
            if (labels.ValueKind == JsonValueKind.Array)
            {
                var jl = labels.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                var pl = p.Pages.Select(x => x.Label).ToList();
                if (!jl.SequenceEqual(pl.Select(x => x))) notes.Add("page labels differ");
            }
            else if (p.Pages.Any(x => x.Label.Length > 0)) notes.Add("pdf.js sees no page labels, PDFium does");

            row["pdfjs"] = notes.Count == 0 ? "ok (same pages, outline, named dests, links and labels as PDFium)" : string.Join("; ", notes);
            summary.Add(row);
            Console.WriteLine($"{file,-34} {row["pdfjs"]}");
        }

        File.WriteAllText(Path.Combine(outDir, "pdfjs-compare.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
