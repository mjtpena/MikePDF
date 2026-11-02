using System.Diagnostics;
using System.IO;
using S7.Core.Engine;
using S7.Core.Text;

namespace A11yTest;

/// <summary>
/// Robustness sweep over the PDFium test corpus (pdfium-resources). Not a correctness oracle (no expected text exists for most
/// files); it checks that the extraction and the text model never throw and keep their invariants on files we did not write.
/// </summary>
public static class Sweep
{
    public static void Run(string corpusDir)
    {
        var files = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        int openFail = 0, loadFail = 0, pages = 0, withText = 0, tagged = 0, taggedUsed = 0, invariantFail = 0, exceptions = 0;
        var swAll = Stopwatch.StartNew();
        var times = new List<double>();
        var notes = new List<string>();
        var fallbacks = new List<string>();
        foreach (var f in files)
        {
            PdfiumEngine? eng = null;
            try { eng = PdfiumEngine.Open(f); }
            catch { openFail++; continue; }
            using (eng)
            {
                int n = Math.Min(eng.PageCount, 2);
                for (int p = 0; p < n; p++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var data = eng.ExtractPage(p);
                        var m = TextModelBuilder.Build(data);
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        pages++;
                        if (m.Length > 0) withText++;
                        if (data.IsTagged) tagged++;
                        if (m.OrderSource.StartsWith("structure tree")) taggedUsed++;
                        else if (data.IsTagged) fallbacks.Add($"{Path.GetFileName(f)} p{p}: {m.OrderSource}, chars={data.Chars.Count}, blocks={data.StructBlocks.Count}");
                        string? bad = Invariants(m);
                        if (bad != null) { invariantFail++; if (notes.Count < 12) notes.Add($"{Path.GetFileName(f)} p{p}: {bad}"); }
                    }
                    catch (InvalidOperationException e) when (e.Message.StartsWith("FPDF_LoadPage"))
                    {
                        loadFail++;   // PDFium itself cannot load the page (damaged page tree by design); a typed engine error in production
                    }
                    catch (Exception e)
                    {
                        exceptions++;
                        if (notes.Count < 12) notes.Add($"{Path.GetFileName(f)} p{p}: EXCEPTION {e.GetType().Name} {e.Message}");
                    }
                }
            }
        }
        times.Sort();
        double P(double q) => times.Count == 0 ? 0 : times[(int)Math.Min(times.Count - 1, Math.Floor(q * times.Count))];
        Console.WriteLine($"files={files.Count} openFailed={openFail} pagesProcessed={pages} pagesWithText={withText} taggedPages={tagged} usedStructTree={taggedUsed}");
        Console.WriteLine($"pageLoadFailedInPdfium={loadFail} invariantFailures={invariantFail} exceptions={exceptions}");
        Console.WriteLine($"extract+build per page ms: n={times.Count} p50={P(0.5):F2} p95={P(0.95):F2} max={(times.Count > 0 ? times[^1] : 0):F2} (single run, in-process, includes PDFium text-page load, NOT a benchmark)");
        Console.WriteLine($"total {swAll.Elapsed.TotalSeconds:F1} s");
        foreach (var n in notes) Console.WriteLine("  " + n);
        Console.WriteLine("tagged pages that fell back to the heuristic:");
        foreach (var n in fallbacks) Console.WriteLine("  " + n);
        Results.Add("sweep", "corpus", "-", "no exceptions and no invariant violations over pdfium-resources", exceptions == 0 && invariantFail == 0,
            $"{pages} pages from {files.Count - openFail} openable files ({openFail} did not open: encrypted/damaged by design; {loadFail} pages PDFium itself could not load), {withText} with text, {tagged} tagged ({taggedUsed} read via struct tree)");
    }

    private static string? Invariants(TextModel m)
    {
        // Every unit's boundaries must partition [0, Length] strictly increasing.
        foreach (TextUnitKind u in Enum.GetValues<TextUnitKind>())
        {
            var b = m.Boundaries(u);
            if (b[0] != 0 || b[^1] != m.Length) return $"{u} boundaries do not span the text";
            for (int i = 1; i < b.Length; i++) if (b[i] <= b[i - 1] && m.Length > 0) return $"{u} boundaries not strictly increasing";
        }
        // Concatenating word ranges reproduces the text.
        var words = new TextRange(m, 0, 0);
        var sb = new System.Text.StringBuilder();
        if (m.Length > 0)
        {
            words.ExpandToEnclosingUnit(TextUnitKind.Word);
            while (true) { sb.Append(words.GetText()); if (words.Move(TextUnitKind.Word, 1) == 0) break; }
            if (sb.ToString() != m.GetText(0, m.Length)) return "word walk does not reproduce the text";
        }
        // Rects finite and inside a generous page frame.
        foreach (var a in m.Atoms)
        {
            if (a.Rect is not { } r) continue;
            if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height)) return "non-finite rect";
            if (r.Right < -50 || r.Bottom < -50 || r.X > m.BaseWidth + 50 || r.Y > m.BaseHeight + 50) return $"rect far outside page: ({r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}) page {m.BaseWidth:F0}x{m.BaseHeight:F0}";
        }
        return null;
    }
}
