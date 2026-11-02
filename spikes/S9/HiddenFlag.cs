using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace S9;

/// <summary>S9 question 2: can the engine hide originals the user moved or deleted by setting /F Hidden on its in-memory copy, and what does it cost?</summary>
internal static class HiddenFlag
{
    static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MikePDF.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static double P(List<double> v, double p) { var c = v.OrderBy(x => x).ToList(); return c[Math.Min(c.Count - 1, (int)Math.Ceiling(p * c.Count) - 1)]; }

    public static void Run()
    {
        var res = Path.Combine(Root(), "spikes", "_shared", "corpus", "pdfium-resources");
        var files = new[] { "annotation_highlight_square_with_ap.pdf", "annotation_ink_multiple.pdf", "annotation_stamp_with_ap.pdf", "annots.pdf", "ink_annot.pdf", "line_annot.pdf", "polygon_annot.pdf", "links_highlights_annots.pdf", "annotation_markup_multiline_no_ap.pdf", "annotation_highlight_long_content.pdf", "freetext_annotation_without_da.pdf" };
        var sb = new StringBuilder();
        void Log(string s) { Console.WriteLine(s); sb.AppendLine(s); }

        // ---------------- A. correctness: hidden == removed? ----------------
        Log("## A. Hidden flag versus removing the annotation (page 0, 1.5 scale, FPDF_ANNOT)");
        Log("file | idx | subtype | /F | pixels change when hidden | hidden render == removed render | SSIM(hidden, removed) | meanAbs");
        int total = 0, visible = 0, same = 0;
        foreach (var f in files)
        {
            var path = Path.Combine(res, f); if (!File.Exists(path)) { Log($"{f}: MISSING"); continue; }
            var bytes = File.ReadAllBytes(path);
            int n; Bgra baseline;
            using (var d0 = new PdfDoc(bytes)) { var p0 = Pdfium.FPDF_LoadPage(d0.Handle, 0); n = Pdfium.FPDFPage_GetAnnotCount(p0); baseline = PdfRender.Page(p0, 1.5); Pdfium.FPDF_ClosePage(p0); }
            for (var i = 0; i < n; i++)
            {
                int subtype, flags; Bgra hidden;
                using (var d1 = new PdfDoc(bytes))
                {
                    var p1 = Pdfium.FPDF_LoadPage(d1.Handle, 0); var a = Pdfium.FPDFPage_GetAnnot(p1, i);
                    subtype = Pdfium.FPDFAnnot_GetSubtype(a); flags = Pdfium.FPDFAnnot_GetFlags(a);
                    Pdfium.FPDFAnnot_SetFlags(a, flags | Pdfium.ANNOT_HIDDEN); Pdfium.FPDFPage_CloseAnnot(a);
                    hidden = PdfRender.Page(p1, 1.5); Pdfium.FPDF_ClosePage(p1);
                }
                Bgra removed;
                using (var d2 = new PdfDoc(bytes))
                {
                    var p2 = Pdfium.FPDF_LoadPage(d2.Handle, 0); Pdfium.FPDFPage_RemoveAnnot(p2, i);
                    removed = PdfRender.Page(p2, 1.5); Pdfium.FPDF_ClosePage(p2);
                }
                var changes = Imaging.Hash(hidden) != Imaging.Hash(baseline);
                var eq = Imaging.Hash(hidden) == Imaging.Hash(removed);
                var ssim = Imaging.Ssim(hidden, removed); var diff = Imaging.Diff(hidden, removed);
                total++; if (changes) visible++; if (eq) same++;
                Log(string.Create(CultureInfo.InvariantCulture, $"{f} | {i} | {SubtypeName(subtype)} | {flags} | {changes} | {eq} | {ssim:F5} | {diff.MeanAbs:F4}"));
            }
        }
        Log($"summary A: {total} annotations; {visible} change the page pixels when hidden; hidden render equals removed render for {same}/{total}");

        // ---------------- B. persistence and the save leak (AC-14) ----------------
        Log("");
        Log("## B. Persistence across page reload, and does the flag leak into a save?");
        {
            var bytes = File.ReadAllBytes(Path.Combine(res, "annotation_ink_multiple.pdf"));
            using var doc = new PdfDoc(bytes);
            var page = Pdfium.FPDF_LoadPage(doc.Handle, 0); var n = Pdfium.FPDFPage_GetAnnotCount(page);
            var baseline = Imaging.Hash(PdfRender.Page(page, 1.5));
            var a = Pdfium.FPDFPage_GetAnnot(page, 0); var f0 = Pdfium.FPDFAnnot_GetFlags(a);
            Pdfium.FPDFAnnot_SetFlags(a, f0 | Pdfium.ANNOT_HIDDEN); Pdfium.FPDFPage_CloseAnnot(a);
            var hiddenHash = Imaging.Hash(PdfRender.Page(page, 1.5));
            Pdfium.FPDF_ClosePage(page);
            var page2 = Pdfium.FPDF_LoadPage(doc.Handle, 0);
            var reloaded = Imaging.Hash(PdfRender.Page(page2, 1.5));
            Log($"annotations on page: {n}; original /F of #0 = {f0}; render changed by hiding: {hiddenHash != baseline}; still hidden after ClosePage+LoadPage: {reloaded == hiddenHash}");
            int FlagsOf(byte[] saved, int idx) { using var d = new PdfDoc(saved); var p = Pdfium.FPDF_LoadPage(d.Handle, 0); var an = Pdfium.FPDFPage_GetAnnot(p, idx); var fl = Pdfium.FPDFAnnot_GetFlags(an); Pdfium.FPDFPage_CloseAnnot(an); Pdfium.FPDF_ClosePage(p); return fl; }
            var full = Pdfium.Save(doc.Handle, 0); var inc = Pdfium.Save(doc.Handle, 1);
            Log($"FPDF_SaveAsCopy full rewrite while hidden: saved /F of #0 = {FlagsOf(full, 0)} (leak = {(FlagsOf(full, 0) & 2) != 0}); incremental while hidden: saved /F = {FlagsOf(inc, 0)} (leak = {(FlagsOf(inc, 0) & 2) != 0}; incremental length {inc.Length} vs source {bytes.Length})");
            var a2 = Pdfium.FPDFPage_GetAnnot(page2, 0); Pdfium.FPDFAnnot_SetFlags(a2, f0); Pdfium.FPDFPage_CloseAnnot(a2);
            var full2 = Pdfium.Save(doc.Handle, 0); var inc2 = Pdfium.Save(doc.Handle, 1);
            Log($"after restoring /F={f0}: full save /F = {FlagsOf(full2, 0)}, incremental save /F = {FlagsOf(inc2, 0)} (incremental length {inc2.Length}: PDFium still writes the touched object; harmless but not byte-identical to the source)");
            Pdfium.FPDF_ClosePage(page2);
        }

        // ---------------- C. cost with many annotations on the page ----------------
        Log("");
        Log("## C. Tile render and hide cost versus annotation count (ink annotations with /AP, one stroke each, one page)");
        Log("annots | doc bytes | LoadMemDocument ms | LoadPage ms | tile 1.33x p50/p95 FPDF_ANNOT | same, no annots flag | tile 5.33x p50/p95 FPDF_ANNOT | GetAnnot+SetFlags+Close us (p50) | tile after hiding 1 | tile after hiding 50");
        foreach (var n in new[] { 0, 10, 100, 1000, 5000 })
        {
            var bd = BaseDoc.Build(1);
            var bytes = n == 0 ? bd.Bytes : bd.WithAnnotations(Gen.InkAnnots(n, 99)).bytes;
            var t0 = Stopwatch.GetTimestamp(); using var doc = new PdfDoc(bytes); var tLoad = (Stopwatch.GetTimestamp() - t0) * ToMs;
            t0 = Stopwatch.GetTimestamp(); var page = Pdfium.FPDF_LoadPage(doc.Handle, 0); var tPage = (Stopwatch.GetTimestamp() - t0) * ToMs;
            List<double> Time(double s, int vx, int vy, int flags, int reps = 25) { var l = new List<double>(); for (var r = 0; r < reps; r++) { var a = Stopwatch.GetTimestamp(); PdfRender.Viewport(page, s, vx, vy, 512, 512, flags); l.Add((Stopwatch.GetTimestamp() - a) * ToMs); } l.RemoveAt(0); return l; }
            var a1 = Time(96.0 / 72, 100, 100, Pdfium.FPDF_ANNOT); var a0 = Time(96.0 / 72, 100, 100, 0); var a4 = Time(384.0 / 72, 800, 1500, Pdfium.FPDF_ANNOT);
            var setUs = new List<double>();
            var rnd = new Random(3);
            double h1 = double.NaN, h50 = double.NaN;
            if (n > 0)
            {
                for (var r = 0; r < 50; r++) { var idx = rnd.Next(n); var a = Stopwatch.GetTimestamp(); var an = Pdfium.FPDFPage_GetAnnot(page, idx); var fl = Pdfium.FPDFAnnot_GetFlags(an); Pdfium.FPDFAnnot_SetFlags(an, fl | 2); Pdfium.FPDFPage_CloseAnnot(an); setUs.Add((Stopwatch.GetTimestamp() - a) * ToMs * 1000); }
                // the 50 above are now hidden; time a tile with 50 hidden
                h50 = P(Time(96.0 / 72, 100, 100, Pdfium.FPDF_ANNOT, 15), .5);
                // unhide, hide exactly one
                for (var idx = 0; idx < n; idx++) { var an = Pdfium.FPDFPage_GetAnnot(page, idx); var fl = Pdfium.FPDFAnnot_GetFlags(an); if ((fl & 2) != 0) Pdfium.FPDFAnnot_SetFlags(an, fl & ~2); Pdfium.FPDFPage_CloseAnnot(an); }
                { var an = Pdfium.FPDFPage_GetAnnot(page, 0); Pdfium.FPDFAnnot_SetFlags(an, Pdfium.FPDFAnnot_GetFlags(an) | 2); Pdfium.FPDFPage_CloseAnnot(an); }
                h1 = P(Time(96.0 / 72, 100, 100, Pdfium.FPDF_ANNOT, 15), .5);
            }
            Log(string.Create(CultureInfo.InvariantCulture, $"{n} | {bytes.Length} | {tLoad:F1} | {tPage:F1} | {P(a1, .5):F2}/{P(a1, .95):F2} | {P(a0, .5):F2} | {P(a4, .5):F2}/{P(a4, .95):F2} | {(setUs.Count > 0 ? P(setUs, .5).ToString("F1", CultureInfo.InvariantCulture) : "-")} | {h1:F2} | {h50:F2}"));
            Pdfium.FPDF_ClosePage(page);
        }

        // ---------------- D. annotation-only sprite for a moved foreign annotation ----------------
        Log("");
        Log("## D. Can the engine produce a sprite of ONE loaded annotation (so the overlay can draw it moved)?");
        Log("method: reload page, hide every other annotation, FPDFPage_RemoveObject all page objects (in memory only, no GenerateContent), render onto a transparent bitmap");
        Log("file | idx | subtype | sprite ms | composite SourceOver SSIM vs visible | composite Multiply SSIM vs visible | page restored after (render hash == baseline)");
        foreach (var f in new[] { "annotation_ink_multiple.pdf", "annotation_highlight_square_with_ap.pdf", "annotation_stamp_with_ap.pdf", "annots.pdf" })
        {
            var path = Path.Combine(res, f); if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path); using var doc = new PdfDoc(bytes);
            var page = Pdfium.FPDF_LoadPage(doc.Handle, 0); var n = Pdfium.FPDFPage_GetAnnotCount(page);
            var baseline = PdfRender.Page(page, 1.5); var baseHash = Imaging.Hash(baseline); Pdfium.FPDF_ClosePage(page);
            for (var i = 0; i < n; i++)
            {
                // visible vs hidden renders of the live page
                var pv = Pdfium.FPDF_LoadPage(doc.Handle, 0); var an = Pdfium.FPDFPage_GetAnnot(pv, i); var fl = Pdfium.FPDFAnnot_GetFlags(an); var st = Pdfium.FPDFAnnot_GetSubtype(an);
                Pdfium.FPDFAnnot_SetFlags(an, fl | 2); var hiddenRender = PdfRender.Page(pv, 1.5); Pdfium.FPDFAnnot_SetFlags(an, fl); Pdfium.FPDFPage_CloseAnnot(an); Pdfium.FPDF_ClosePage(pv);
                if (Imaging.Hash(hiddenRender) == baseHash) continue;   // no visible effect: nothing to move
                var t0 = Stopwatch.GetTimestamp();
                var ps = Pdfium.FPDF_LoadPage(doc.Handle, 0);
                var saved = new int[n];
                for (var j = 0; j < n; j++) { var aj = Pdfium.FPDFPage_GetAnnot(ps, j); saved[j] = Pdfium.FPDFAnnot_GetFlags(aj); if (j != i) Pdfium.FPDFAnnot_SetFlags(aj, saved[j] | 2); Pdfium.FPDFPage_CloseAnnot(aj); }
                for (var k = Pdfium.FPDFPage_CountObjects(ps) - 1; k >= 0; k--) { var o = Pdfium.FPDFPage_GetObject(ps, k); Pdfium.FPDFPage_RemoveObject(ps, o); Pdfium.FPDFPageObj_Destroy(o); }
                var sprite = PdfRender.Page(ps, 1.5, Pdfium.FPDF_ANNOT, 0x00FFFFFF);
                var tSprite = (Stopwatch.GetTimestamp() - t0) * ToMs;
                for (var j = 0; j < n; j++) { var aj = Pdfium.FPDFPage_GetAnnot(ps, j); Pdfium.FPDFAnnot_SetFlags(aj, saved[j]); Pdfium.FPDFPage_CloseAnnot(aj); }
                Pdfium.FPDF_ClosePage(ps);
                var pr = Pdfium.FPDF_LoadPage(doc.Handle, 0); var restored = Imaging.Hash(PdfRender.Page(pr, 1.5)) == baseHash; Pdfium.FPDF_ClosePage(pr);
                var normal = new Bgra(hiddenRender.W, hiddenRender.H); var mult = new Bgra(hiddenRender.W, hiddenRender.H);
                for (var p = 0; p < hiddenRender.W * hiddenRender.H; p++)
                {
                    var al = sprite.Px[p * 4 + 3] / 255.0;
                    for (var c = 0; c < 3; c++)
                    {
                        var b = hiddenRender.Px[p * 4 + c] / 255.0; var s = sprite.Px[p * 4 + c] / 255.0;
                        normal.Px[p * 4 + c] = (byte)Math.Round(255 * (s * al + b * (1 - al)));
                        mult.Px[p * 4 + c] = (byte)Math.Round(255 * ((1 - al) * b + al * b * s));
                    }
                    normal.Px[p * 4 + 3] = 255; mult.Px[p * 4 + 3] = 255;
                }
                Log(string.Create(CultureInfo.InvariantCulture, $"{f} | {i} | {SubtypeName(st)} | {tSprite:F1} | {Imaging.Ssim(baseline, normal):F5} | {Imaging.Ssim(baseline, mult):F5} | {restored}"));
            }
        }
        File.WriteAllText(Path.Combine(Program.Out, "s9-hidden-flag.txt"), sb.ToString());
    }

    /// <summary>Follow-up to C: why did the tile get slower after a flag change? Separate the effects (first render after a change, GetAnnot calls alone, hide, unhide).</summary>
    public static void RunCost()
    {
        var sb = new StringBuilder();
        void Log(string s) { Console.WriteLine(s); sb.AppendLine(s); }
        Log("## C2. 5000 and 1000 ink annotations: what changes tile cost after touching flags? (tile 512x512 at 1.33x, FPDF_ANNOT, 31 renders each, p50 / p95 / first-after-change ms)");
        foreach (var n in new[] { 1000, 5000 })
        {
            var bd = BaseDoc.Build(1); var bytes = bd.WithAnnotations(Gen.InkAnnots(n, 99)).bytes;
            using var doc = new PdfDoc(bytes); var page = Pdfium.FPDF_LoadPage(doc.Handle, 0);
            (double first, double p50, double p95) Time(int reps = 31)
            {
                var l = new List<double>(); for (var r = 0; r < reps; r++) { var a = Stopwatch.GetTimestamp(); PdfRender.Viewport(page, 96.0 / 72, 100, 100, 512, 512, Pdfium.FPDF_ANNOT); l.Add((Stopwatch.GetTimestamp() - a) * ToMs); }
                var first = l[0]; l.RemoveAt(0); return (first, P(l, .5), P(l, .95));
            }
            string F((double first, double p50, double p95) t) => string.Create(CultureInfo.InvariantCulture, $"{t.p50:F1} / {t.p95:F1} / first {t.first:F1}");
            Log($"N={n}: cold (nothing touched)                : {F(Time())}");
            Log($"N={n}: warm again                            : {F(Time())}");
            for (var i = 0; i < n; i++) { var a = Pdfium.FPDFPage_GetAnnot(page, i); Pdfium.FPDFAnnot_GetFlags(a); Pdfium.FPDFPage_CloseAnnot(a); }
            Log($"N={n}: after GetAnnot/GetFlags on all (no set): {F(Time())}");
            { var a = Pdfium.FPDFPage_GetAnnot(page, 0); Pdfium.FPDFAnnot_SetFlags(a, Pdfium.FPDFAnnot_GetFlags(a) | 2); Pdfium.FPDFPage_CloseAnnot(a); }
            Log($"N={n}: after hiding annotation 0              : {F(Time())}");
            { var a = Pdfium.FPDFPage_GetAnnot(page, 0); Pdfium.FPDFAnnot_SetFlags(a, Pdfium.FPDFAnnot_GetFlags(a) & ~2); Pdfium.FPDFPage_CloseAnnot(a); }
            Log($"N={n}: after un-hiding annotation 0           : {F(Time())}");
            Pdfium.FPDF_ClosePage(page); var p2 = Pdfium.FPDF_LoadPage(doc.Handle, 0);
            var l2 = new List<double>(); for (var r = 0; r < 11; r++) { var a = Stopwatch.GetTimestamp(); PdfRender.Viewport(p2, 96.0 / 72, 100, 100, 512, 512, Pdfium.FPDF_ANNOT); l2.Add((Stopwatch.GetTimestamp() - a) * ToMs); }
            Log($"N={n}: after ClosePage + LoadPage (annotation flags restored): first {l2[0]:F1}, p50 {P(l2.Skip(1).ToList(), .5):F1}");
            Pdfium.FPDF_ClosePage(p2);
        }
        File.WriteAllText(Path.Combine(Program.Out, "s9-hidden-cost.txt"), sb.ToString());
    }

    static string SubtypeName(int s) => s switch { 1 => "Text", 2 => "Link", 3 => "FreeText", 4 => "Line", 5 => "Square", 6 => "Circle", 7 => "Polygon", 8 => "PolyLine", 9 => "Highlight", 10 => "Underline", 11 => "Squiggly", 12 => "StrikeOut", 13 => "Stamp", 14 => "Caret", 15 => "Ink", 16 => "Popup", 17 => "FileAttachment", 20 => "Widget", _ => "type" + s };
}
