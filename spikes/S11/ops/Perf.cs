// S11 item 4: cost of merging 5 PDFs of 100 pages and of splitting a 600-page PDF by bookmark, through PDFium and through the assembler prototype,
// plus how much an incremental save of a page operation appends. Each repetition runs in its own process so the peak working set is that run's alone.
using System.Diagnostics;
using System.Text.Json;
using MikePDF.PdfWriter;
using S4;

namespace S11;

internal static class Perf
{
    public static readonly string[] Scenarios_ =
    [
        "baseline-idle", "pdfium-merge5x100", "assembler-merge5x100", "pdfium-split600-by-bookmark", "assembler-split600-by-bookmark",
        "pdfium-inc-rotate1-of-600", "pdfium-inc-delete50-of-600", "pdfium-inc-move10-of-600", "pdfium-inc-noop-600",
    ];

    public static int Run(string fixtures, string outFile)
    {
        var rows = new List<string>();
        foreach (var sc in Scenarios_)
        {
            for (var rep = 0; rep < 5; rep++)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"perf-one {sc} \"{fixtures}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                var line = text.Split('\n').LastOrDefault() ?? "";
                rows.Add(line);
                Console.WriteLine(line);
            }
        }

        File.WriteAllLines(outFile, rows);
        Console.WriteLine("---- medians (ms; MB)");
        foreach (var g in rows.Select(r => JsonDocument.Parse(r).RootElement).GroupBy(r => r.GetProperty("scenario").GetString()))
        {
            double Med(string k) => g.Select(r => r.GetProperty(k).GetDouble()).OrderBy(x => x).ElementAt(g.Count() / 2);
            Console.WriteLine($"{g.Key,-34} total {Med("totalMs"),8:0} ms   peak WS {Med("peakWsMB"),7:0.0} MB   growth over start {Med("peakWsMB") - Med("startWsMB"),7:0.0} MB   private peak {Med("peakPrivateMB"),7:0.0} MB   output {Med("outputBytes") / 1e6,7:0.00} MB   {g.First().GetProperty("detail").GetString()}");
        }

        return 0;
    }

    public static int One(string scenario, string fixtures)
    {
        var proc = Process.GetCurrentProcess();
        PdfDoc.Init();
        var startWs = proc.WorkingSet64 / 1048576.0;
        var phases = new Dictionary<string, double>();
        long outBytes = 0;
        var detail = "";
        var sw = Stopwatch.StartNew();
        double Lap(string name) { var t = sw.Elapsed.TotalMilliseconds; phases[name] = t; sw.Restart(); return t; }
        var tmp = Path.Combine(Path.GetTempPath(), "s11perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var total = Stopwatch.StartNew();
        try
        {
            switch (scenario)
            {
                case "baseline-idle":
                    break;
                case "pdfium-merge5x100":
                {
                    var files = Enumerable.Range(1, 5).Select(i => File.ReadAllBytes(Path.Combine(fixtures, $"big-{i}.pdf"))).ToList();
                    Lap("read");
                    var dest = P2.FPDF_CreateNewDocument();
                    var docs = files.Select(f => PdfDoc.Open(f, false, out _)!).ToList();
                    Lap("open");
                    var idx = Enumerable.Range(0, 100).ToArray();
                    foreach (var d in docs) P2.ImportByIndex(dest, d.Doc, idx, P.FPDF_GetPageCount(dest));
                    Lap("import");
                    var bytes = Scenarios.SaveRaw(dest, Scenarios.Full, out _)!;
                    Lap("save");
                    File.WriteAllBytes(Path.Combine(tmp, "merged.pdf"), bytes);
                    Lap("write");
                    outBytes = bytes.Length;
                    detail = $"{P.FPDF_GetPageCount(dest)} pages";
                    P.FPDF_CloseDocument(dest);
                    foreach (var d in docs) d.Dispose();
                    break;
                }
                case "assembler-merge5x100":
                {
                    var files = Enumerable.Range(1, 5).Select(i => File.ReadAllBytes(Path.Combine(fixtures, $"big-{i}.pdf"))).ToList();
                    Lap("read");
                    var srcs = files.Select((f, i) => new AsmSource { Name = $"big-{i + 1}", Doc = StrictDocument.Open(f).Value, Pages = Enumerable.Range(0, 100).ToArray() }).ToList();
                    Lap("parse");
                    var asm = new Assembler(srcs);
                    var bytes = asm.Build(wrapOutlines: true);
                    Lap("assemble");
                    File.WriteAllBytes(Path.Combine(tmp, "merged.pdf"), bytes);
                    Lap("write");
                    outBytes = bytes.Length;
                    detail = "500 pages";
                    break;
                }
                case "pdfium-split600-by-bookmark":
                {
                    var bytes = File.ReadAllBytes(Path.Combine(fixtures, "big-600.pdf"));
                    Lap("read");
                    using var src = PdfDoc.Open(bytes, false, out _);
                    Lap("open");
                    var starts = TopLevelBookmarkPages(src!);
                    Lap("read outline");
                    var count = P.FPDF_GetPageCount(src!.Doc);
                    long total2 = 0;
                    for (var i = 0; i < starts.Count; i++)
                    {
                        var from = starts[i];
                        var to = i + 1 < starts.Count ? starts[i + 1] - 1 : count - 1;
                        var dest = P2.FPDF_CreateNewDocument();
                        P2.ImportByIndex(dest, src.Doc, Enumerable.Range(from, to - from + 1).ToArray(), 0);
                        var part = Scenarios.SaveRaw(dest, Scenarios.Full, out _)!;
                        P.FPDF_CloseDocument(dest);
                        File.WriteAllBytes(Path.Combine(tmp, $"part-{i:000}.pdf"), part);
                        total2 += part.Length;
                    }

                    Lap("split+write");
                    outBytes = total2;
                    detail = $"{starts.Count} parts";
                    break;
                }
                case "assembler-split600-by-bookmark":
                {
                    var bytes = File.ReadAllBytes(Path.Combine(fixtures, "big-600.pdf"));
                    Lap("read");
                    var strict = StrictDocument.Open(bytes).Value;
                    Lap("parse");
                    int[] starts;
                    using (var s2 = PdfDoc.Open(bytes, false, out _)) starts = [.. TopLevelBookmarkPages(s2!)];
                    Lap("read outline (PDFium)");
                    long total2 = 0;
                    for (var i = 0; i < starts.Length; i++)
                    {
                        var from = starts[i];
                        var to = i + 1 < starts.Length ? starts[i + 1] - 1 : 599;
                        var asm = new Assembler([new AsmSource { Name = "L", Doc = strict, Pages = Enumerable.Range(from, to - from + 1).ToArray() }]);
                        var part = asm.Build(false);
                        File.WriteAllBytes(Path.Combine(tmp, $"part-{i:000}.pdf"), part);
                        total2 += part.Length;
                    }

                    Lap("split+write");
                    outBytes = total2;
                    detail = $"{starts.Length} parts";
                    break;
                }
                case "pdfium-inc-rotate1-of-600":
                case "pdfium-inc-delete50-of-600":
                case "pdfium-inc-move10-of-600":
                case "pdfium-inc-noop-600":
                {
                    var bytes = File.ReadAllBytes(Path.Combine(fixtures, "big-600.pdf"));
                    Lap("read");
                    using var d = PdfDoc.Open(bytes, true, out _);
                    Lap("open");
                    if (scenario.Contains("rotate")) { var pg = d!.LoadPage(300); P2.FPDFPage_SetRotation(pg, 1); }
                    else if (scenario.Contains("delete")) { for (var i = 0; i < 50; i++) P2.FPDFPage_Delete(d!.Doc, 100 + i); }
                    else if (scenario.Contains("move")) P2.Move(d!.Doc, Enumerable.Range(200, 10).ToArray(), 5);
                    Lap("operate");
                    var inc = d!.Save(Scenarios.Inc, out _)!;
                    Lap("save incremental");
                    outBytes = inc.Length;
                    detail = $"original {bytes.Length / 1e6:0.00} MB; appended {(inc.Length - bytes.Length) / 1e6:0.00} MB ({100.0 * (inc.Length - bytes.Length) / bytes.Length:0.0}%); prefix intact: {inc.AsSpan(0, bytes.Length).SequenceEqual(bytes)}";
                    break;
                }
            }
        }
        finally
        {
            total.Stop();
            proc.Refresh();
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario,
            totalMs = total.Elapsed.TotalMilliseconds,
            startWsMB = startWs,
            peakWsMB = proc.PeakWorkingSet64 / 1048576.0,
            peakPrivateMB = proc.PeakPagedMemorySize64 / 1048576.0,
            outputBytes = outBytes,
            detail,
            phases,
        }));
        return 0;
    }

    private static unsafe List<int> TopLevelBookmarkPages(PdfDoc d)
    {
        var starts = new List<int>();
        var bm = P2.FPDFBookmark_GetFirstChild(d.Doc, IntPtr.Zero);
        while (bm != IntPtr.Zero)
        {
            var dest = P2.FPDFBookmark_GetDest(d.Doc, bm);
            if (dest != IntPtr.Zero)
            {
                var idx = P2.FPDFDest_GetDestPageIndex(d.Doc, dest);
                if (idx >= 0) starts.Add(idx);
            }

            bm = P2.FPDFBookmark_GetNextSibling(d.Doc, bm);
        }

        starts.Sort();
        return starts.Distinct().ToList();
    }
}
