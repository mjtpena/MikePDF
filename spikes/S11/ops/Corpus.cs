// S11: sweep of real-world-ish PDFs (the PDFium test corpus under spikes/_shared/corpus and the repo's own fixtures): what do PDFium's page operations do to the structure that is actually in them?
// One child process per file (PDFium on hostile input can crash), 40 s timeout. Light profile: first 200 pages, no render.
using System.Diagnostics;
using System.Text.Json;
using MikePDF.PdfWriter;
using S4;

namespace S11;

internal static class Corpus
{
    public static int Run(string dir, string outFile)
    {
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"{files.Count} files in {dir}");
        var lines = new List<string>();
        var n = 0;
        foreach (var f in files)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"corpus-one \"{f}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var task = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(40000)) { p.Kill(); lines.Add(JsonSerializer.Serialize(new { file = Path.GetFileName(f), status = "timeout" })); continue; }
            var text = task.Result.Trim();
            if (p.ExitCode != 0 || text.Length == 0) lines.Add(JsonSerializer.Serialize(new { file = Path.GetFileName(f), status = "crash", exit = p.ExitCode }));
            else lines.Add(text.Split('\n').Last());
            if (++n % 100 == 0) Console.WriteLine($"  {n}/{files.Count}");
        }

        File.WriteAllLines(outFile, lines);
        Summarise(lines);
        return 0;
    }

    private sealed record Counts(int Pages, int Outline, int OutlineResolving, int Named, int NamedResolving, int Links, int LinksResolving, int Widgets, int WidgetsRecognised, bool Labels, int Annots, string Catalog);

    private static Counts Count(byte[] bytes)
    {
        var p = Profile.Read(bytes, light: true);
        var isPage = (string t) => t.StartsWith("page#", StringComparison.Ordinal);
        return new Counts(p.PageCount, p.Outline.Count, p.Outline.Count(o => isPage(o.Target)), p.Named.Count, p.Named.Values.Count(isPage),
            p.Pages.Sum(x => x.Links.Count), p.Pages.Sum(x => x.Links.Count(isPage)), p.Pages.Sum(x => x.Widgets.Count), p.Pages.Sum(x => x.Widgets.Count(w => w.Type >= 0 && w.Name.Length > 0)),
            p.Pages.Any(x => x.Label.Length > 0), p.Pages.Sum(x => x.AnnotCounts.Where(kv => kv.Key != 2 && kv.Key != 16).Sum(kv => kv.Value)), p.Catalog.ToString());
    }

    /// <summary>True when the last startxref points at a cross-reference stream (an object) rather than a classic table.</summary>
    private static bool IsXrefStream(byte[] b)
    {
        var tail = System.Text.Encoding.Latin1.GetString(b, Math.Max(0, b.Length - 1100), Math.Min(b.Length, 1100));
        var i = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (i < 0) return false;
        var numText = new string(tail[(i + 9)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsAsciiDigit).ToArray());
        if (!long.TryParse(numText, out var off) || off < 0 || off >= b.Length) return false;
        return !(off + 4 <= b.Length && b[off] == 'x' && b[off + 1] == 'r' && b[off + 2] == 'e' && b[off + 3] == 'f');
    }

    public static int One(string file)
    {
        var res = new Dictionary<string, object?> { ["file"] = Path.GetFileName(file) };
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 30_000_000) { res["status"] = "skipped (over 30 MB)"; Console.WriteLine(JsonSerializer.Serialize(res)); return 0; }
            using (var probe = PdfDoc.Open(bytes, true, out var err))
            {
                if (probe is null || probe.Doc == IntPtr.Zero) { res["status"] = "does not open: " + err; Console.WriteLine(JsonSerializer.Serialize(res)); return 0; }
                if (P.FPDF_GetPageCount(probe.Doc) < 1) { res["status"] = "no pages"; Console.WriteLine(JsonSerializer.Serialize(res)); return 0; }
            }

            var src = Count(bytes);
            res["status"] = "ok";
            res["src"] = src;
            var strict = StrictDocument.Open(bytes).IsSuccess;
            res["srcStrictParse"] = strict;
            res["srcXrefStream"] = IsXrefStream(bytes);
            {
                var t0 = Path.Combine(Path.GetTempPath(), "s11c0-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
                File.WriteAllBytes(t0, bytes);
                res["srcQpdf"] = Qpdf.Run("--check", t0).code;
                File.Delete(t0);
            }

            // X1: copy every page into a new document (what extract / merge / split do to a document)
            var dest = P2.FPDF_CreateNewDocument();
            bool imported;
            using (var s = PdfDoc.Open(bytes, false, out _))
            {
                var all = Enumerable.Range(0, src.Pages).ToArray();
                imported = P2.ImportByIndex(dest, s!.Doc, all, 0);
            }

            var x1 = Scenarios.SaveRaw(dest, Scenarios.Full, out _);
            P.FPDF_CloseDocument(dest);
            res["x1Ok"] = imported && x1 is not null;
            if (x1 is not null) res["x1"] = Count(x1);

            // X2: delete the last page in place and save incrementally (what the page organiser's Delete does)
            if (src.Pages >= 2)
            {
                using var d = PdfDoc.Open((byte[])bytes.Clone(), true, out _);
                P2.FPDFPage_Delete(d!.Doc, src.Pages - 1);
                var x2 = d.Save(Scenarios.Inc, out _);
                res["x2Ok"] = x2 is not null;
                if (x2 is not null)
                {
                    res["x2"] = Count(x2);
                    res["x2PrefixOk"] = x2.Length >= bytes.Length && x2.AsSpan(0, bytes.Length).SequenceEqual(bytes);
                    var tmp = Path.Combine(Path.GetTempPath(), "s11c-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
                    File.WriteAllBytes(tmp, x2);
                    res["x2Qpdf"] = Qpdf.Run("--check", tmp).code;
                    File.Delete(tmp);
                    res["x2PdfWriterValid"] = StructureValidator.Validate(x2).IsValid;
                    res["x2StrictAfter"] = StrictDocument.Open(x2).IsSuccess;
                }
            }
        }
        catch (Exception e)
        {
            res["status"] = "exception: " + e.Message;
        }

        // X3: the assembler prototype copies every page into a new file (the strict parser may refuse a file PDFium opens: R-39)
        if (res.TryGetValue("src", out var srcObj) && srcObj is Counts sc)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var opened = StrictDocument.Open(bytes);
                if (opened.TryGetError(out var perr)) res["x3"] = "strict parse refused: " + perr.Code;
                else
                {
                    var n = Assembler.CountPages(opened.Value);
                    var asm = new Assembler([new AsmSource { Name = "x", Doc = opened.Value, Pages = Enumerable.Range(0, n).ToArray() }]);
                    var ob = asm.Build(false);
                    var tmp3 = Path.Combine(Path.GetTempPath(), "s11c3-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
                    File.WriteAllBytes(tmp3, ob);
                    res["x3Qpdf"] = Qpdf.Run("--check", tmp3).code;
                    File.Delete(tmp3);
                    res["x3Pages"] = n;
                    res["x3Counts"] = Count(ob);
                    res["x3"] = "ok";
                }
            }
            catch (Exception e)
            {
                res["x3"] = "exception: " + e.GetType().Name + ": " + e.Message;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(res));
        return 0;
    }

    private static void Summarise(List<string> lines)
    {
        var rows = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        int Cnt(Func<JsonElement, bool> f) => rows.Count(f);
        var ok = rows.Where(r => r.TryGetProperty("status", out var s) && s.GetString() == "ok").ToList();
        Console.WriteLine($"files: {rows.Count}; opened and measured: {ok.Count}; did not open or skipped: {rows.Count - ok.Count - Cnt(r => r.TryGetProperty("status", out var s) && s.GetString() is "crash" or "timeout")}; crashed/timeout: {Cnt(r => r.TryGetProperty("status", out var s) && s.GetString() is "crash" or "timeout")}");
        int G(JsonElement e, string a, string b) => e.GetProperty(a).GetProperty(b).GetInt32();
        bool Has(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind is not JsonValueKind.Null;
        var withOutline = ok.Where(r => G(r, "src", "Outline") > 0).ToList();
        var withNamed = ok.Where(r => G(r, "src", "Named") > 0).ToList();
        var withLinks = ok.Where(r => G(r, "src", "LinksResolving") > 0).ToList();
        var withWidgets = ok.Where(r => G(r, "src", "WidgetsRecognised") > 0).ToList();
        var withLabels = ok.Where(r => r.GetProperty("src").GetProperty("Labels").GetBoolean()).ToList();
        Console.WriteLine($"files with an outline: {withOutline.Count}; after copying all pages to a new doc: outline kept (any) in {withOutline.Count(r => Has(r, "x1") && G(r, "x1", "Outline") > 0)}");
        Console.WriteLine($"files with named destinations: {withNamed.Count}; kept after copy: {withNamed.Count(r => Has(r, "x1") && G(r, "x1", "Named") > 0)}");
        Console.WriteLine($"files with page-to-page links: {withLinks.Count}; links still resolving to a page after copy (all of them): {withLinks.Count(r => Has(r, "x1") && G(r, "x1", "LinksResolving") == G(r, "src", "LinksResolving"))}; some: {withLinks.Count(r => Has(r, "x1") && G(r, "x1", "LinksResolving") > 0)}");
        Console.WriteLine($"files with form widgets: {withWidgets.Count}; widgets still recognised as fields after copy: all in {withWidgets.Count(r => Has(r, "x1") && G(r, "x1", "WidgetsRecognised") == G(r, "src", "WidgetsRecognised"))}");
        Console.WriteLine($"files with page labels: {withLabels.Count}; labels kept after copy: {withLabels.Count(r => Has(r, "x1") && r.GetProperty("x1").GetProperty("Labels").GetBoolean())}");
        var x2 = ok.Where(r => Has(r, "x2")).ToList();
        var x3all = ok.Where(r => Has(r, "x3")).ToList();
        var x3ok = x3all.Where(r => r.GetProperty("x3").GetString() == "ok").ToList();
        Console.WriteLine($"assembler prototype, copy every page: tried {x3all.Count}; strict parser refused {x3all.Count(r => r.GetProperty("x3").GetString()!.StartsWith("strict", StringComparison.Ordinal))}; threw {x3all.Count(r => r.GetProperty("x3").GetString()!.StartsWith("exception", StringComparison.Ordinal))}; produced output {x3ok.Count}");
        foreach (var g in x3all.Where(r => r.GetProperty("x3").GetString() != "ok").GroupBy(r => r.GetProperty("x3").GetString()).OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"    {g.Count()} x {g.Key}");
        Console.WriteLine($"  of those {x3ok.Count}: page count equals PDFium's source count: {x3ok.Count(r => G(r, "x3Counts", "Pages") == G(r, "src", "Pages"))}; outline kept: {x3ok.Count(r => G(r, "src", "Outline") > 0 && G(r, "x3Counts", "Outline") > 0)} of {x3ok.Count(r => G(r, "src", "Outline") > 0)}; named dests kept: {x3ok.Count(r => G(r, "src", "Named") > 0 && G(r, "x3Counts", "Named") > 0)} of {x3ok.Count(r => G(r, "src", "Named") > 0)}; page labels kept: {x3ok.Count(r => r.GetProperty("src").GetProperty("Labels").GetBoolean() && r.GetProperty("x3Counts").GetProperty("Labels").GetBoolean())} of {x3ok.Count(r => r.GetProperty("src").GetProperty("Labels").GetBoolean())}; all page links still resolving: {x3ok.Count(r => G(r, "src", "LinksResolving") > 0 && G(r, "x3Counts", "LinksResolving") == G(r, "src", "LinksResolving"))} of {x3ok.Count(r => G(r, "src", "LinksResolving") > 0)}; widgets recognised: {x3ok.Count(r => G(r, "src", "WidgetsRecognised") > 0 && G(r, "x3Counts", "WidgetsRecognised") == G(r, "src", "WidgetsRecognised"))} of {x3ok.Count(r => G(r, "src", "WidgetsRecognised") > 0)}; output qpdf exit 0: {x3ok.Count(r => r.GetProperty("x3Qpdf").GetInt32() == 0)} (source exit 0 in {x3ok.Count(r => r.GetProperty("srcQpdf").GetInt32() == 0)}), exit 2 (errors): {x3ok.Count(r => r.GetProperty("x3Qpdf").GetInt32() == 2)}");
        var clean = x2.Where(r => r.GetProperty("srcStrictParse").GetBoolean() && r.GetProperty("srcQpdf").GetInt32() == 0).ToList();
        foreach (var (label, xs) in new[] { ("classic xref", false), ("xref stream", true) })
        {
            var g = clean.Where(r => r.GetProperty("srcXrefStream").GetBoolean() == xs).ToList();
            Console.WriteLine($"  clean sources ({label}: strict parse OK and qpdf exit 0): {g.Count}; after delete-last-page + incremental: qpdf exit 0 in {g.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 0)}, warnings (3) in {g.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 3)}, errors (2) in {g.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 2)}; PdfWriter validator valid in {g.Count(r => r.GetProperty("x2PdfWriterValid").GetBoolean())}");
        }

        Console.WriteLine($"delete-last-page + incremental save: {x2.Count} files; original is exact prefix: {x2.Count(r => r.GetProperty("x2PrefixOk").GetBoolean())}; qpdf --check exit 0: {x2.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 0)}, 3 (warnings): {x2.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 3)}, 2 (errors): {x2.Count(r => r.GetProperty("x2Qpdf").GetInt32() == 2)}; PdfWriter validator valid: {x2.Count(r => r.GetProperty("x2PdfWriterValid").GetBoolean())}; of those whose SOURCE strict-parses: {x2.Count(r => r.GetProperty("srcStrictParse").GetBoolean())} -> still strict-parse after: {x2.Count(r => r.GetProperty("srcStrictParse").GetBoolean() && r.GetProperty("x2StrictAfter").GetBoolean())}");
    }
}
