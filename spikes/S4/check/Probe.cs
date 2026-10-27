using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S4;

/// <summary>
/// Incremental-save behaviour on foreign files (PDFium test corpus): is the original an exact prefix, what does PDFium
/// rewrite on a no-op save, does an edit round-trip on files with xref streams / object streams, and does qpdf accept it.
/// Warnings that the ORIGINAL file already produces are subtracted so only warnings introduced by the save are reported.
/// </summary>
public static unsafe class Probe
{
    static string Norm(string w)
    {
        w = Regex.Replace(w, @"^(WARNING|ERROR): .*?\.pdf", "$1: <f>");
        w = Regex.Replace(w, @"offset \d+", "offset N");
        w = Regex.Replace(w, @"object \d+ \d+", "object N G");
        return w.Trim();
    }
    static List<string> Warn(string path)
        => Qpdf.Run("--check", path).output.Split('\n').Select(s => s.Trim()).Where(s => s.StartsWith("WARNING") || s.StartsWith("ERROR")).ToList();

    public static void Run(string root, string outDir)
    {
        var corpus = Path.Combine(root, "spikes", "_shared", "corpus", "pdfium-resources");
        var files = new[] { "hello_world.pdf", "hello_world_compressed_stream.pdf", "rectangles.pdf", "text_form.pdf", "multiple_form_types.pdf",
            "annotation_highlight_square_with_ap.pdf", "annots.pdf", "links_highlights_annots.pdf", "click_form.pdf", "two_signatures.pdf", "signature_reason.pdf",
            "bug_717.pdf", "annotation_stamp_with_ap.pdf", "bug_1029.pdf", "bug_1399.pdf", "bug_781804.pdf", "embedded_attachments.pdf", "bug_707673.pdf", "bug_757705.pdf", "embedded_images.pdf" };
        Directory.CreateDirectory(Path.Combine(outDir, "probe"));
        var rows = new List<Dictionary<string, object?>>();
        foreach (var f in files)
        {
            var src = Path.Combine(corpus, f);
            if (!File.Exists(src)) { rows.Add(new() { ["file"] = f, ["error"] = "missing" }); continue; }
            var orig = File.ReadAllBytes(src);
            var origWarn = Warn(src).Select(Norm).ToHashSet();
            string origText = Encoding.Latin1.GetString(orig);
            bool origXrefStream = origText.Contains("/Type/XRef") || origText.Contains("/Type /XRef");
            foreach (var edit in new[] { false, true })
            {
                var row = new Dictionary<string, object?> { ["file"] = f, ["edit"] = edit ? "add Square annot" : "none (open + save)", ["origBytes"] = orig.Length, ["origUsesXrefStream"] = origXrefStream };
                try
                {
                    using var d = PdfDoc.Open(orig, true, out var err);
                    if (d == null) { row["error"] = err; rows.Add(row); continue; }
                    var pg = d.LoadPage(0);
                    row["annotsBefore"] = P.FPDFPage_GetAnnotCount(pg);
                    row["sigsBefore"] = P.FPDF_GetSignatureCount(d.Doc);
                    if (edit)
                    {
                        var an = P.FPDFPage_CreateAnnot(pg, P.ST_SQUARE);
                        var r = new FS_RECTF { left = 20, bottom = 20, right = 80, top = 60 };
                        P.FPDFAnnot_SetRect(an, &r);
                        P.FPDFAnnot_SetColor(an, 0, 255, 0, 0, 255);
                        P.FPDFAnnot_SetBorder(an, 0, 0, 2);
                        P.FPDFPage_CloseAnnot(an);
                    }
                    var inc = d.Save(P.FPDF_INCREMENTAL, out var e1);
                    if (inc == null) { row["error"] = "save: " + e1; rows.Add(row); continue; }
                    var rel = $"probe/{Path.GetFileNameWithoutExtension(f)}.{(edit ? "edit" : "noop")}.inc.pdf";
                    var outPath = Path.Combine(outDir, rel);
                    File.WriteAllBytes(outPath, inc);
                    row["outBytes"] = inc.Length;
                    row["origIsExactPrefix"] = inc.Length >= orig.Length && inc.AsSpan(0, orig.Length).SequenceEqual(orig);
                    var appended = Encoding.Latin1.GetString(inc, Math.Min(orig.Length, inc.Length), Math.Max(0, inc.Length - orig.Length));
                    var objs = Regex.Matches(appended, @"(?m)^(\d+) 0 obj").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                    row["objectsWritten"] = objs;
                    bool classic = Regex.IsMatch(appended, @"(?m)^xref\s*$");
                    row["appendedKind"] = classic ? "classic xref" : appended.Contains("/W[") ? "xref stream" : "?";
                    row["xrefStreamHasTypeXRef"] = !classic && (appended.Contains("/Type/XRef") || appended.Contains("/Type /XRef"));
                    // an xref-stream object must end with endobj: look at what follows the last endstream
                    int li = appended.LastIndexOf("endstream", StringComparison.Ordinal);
                    row["lastStreamFollowedByEndobj"] = li >= 0 && Regex.IsMatch(appended.Substring(li + 9, Math.Min(20, appended.Length - li - 9)), @"^\s*endobj");
                    if (!classic)
                    {
                        // repair experiment: add /Type /XRef to the xref-stream dictionary and the missing endobj (both only touch the last object)
                        var all = Encoding.Latin1.GetString(inc);
                        int hdr = all.LastIndexOf(" 0 obj", StringComparison.Ordinal);
                        int lt = all.IndexOf("<<", hdr, StringComparison.Ordinal);
                        var rep = all.Substring(0, lt + 2) + "/Type/XRef" + all.Substring(lt + 2);
                        int le = rep.LastIndexOf("endstream", StringComparison.Ordinal) + 9;
                        if (!Regex.IsMatch(rep.Substring(le, Math.Min(20, rep.Length - le)), @"^\s*endobj")) rep = rep.Substring(0, le) + "\nendobj" + rep.Substring(le);
                        var repBytes = Encoding.Latin1.GetBytes(rep);
                        var repPath = Path.Combine(outDir, "probe", Path.GetFileNameWithoutExtension(f) + "." + (edit ? "edit" : "noop") + ".repaired.pdf");
                        File.WriteAllBytes(repPath, repBytes);
                        row["repairedQpdfExit"] = Qpdf.Run("--check", repPath).code;
                        row["repairedXrefReconstructed"] = Warn(repPath).Any(w => w.Contains("reconstruct") || w.Contains("xref not found"));
                        row["repairedWarnings"] = Warn(repPath).Select(Norm).Where(w => !origWarn.Contains(w)).Distinct().ToList();
                        row["repairedOrigIsPrefix"] = repBytes.AsSpan(0, orig.Length).SequenceEqual(orig);
                        using var d3 = PdfDoc.Open(repBytes, true, out _);
                        row["repairedPdfiumAnnots"] = d3 == null ? -1 : P.FPDFPage_GetAnnotCount(d3.LoadPage(0));
                    }
                    var outWarn = Warn(outPath);
                    var introduced = outWarn.Where(w => !origWarn.Contains(Norm(w))).Select(Norm).Distinct().ToList();
                    row["qpdfExitOut"] = Qpdf.Run("--check", outPath).code;
                    row["qpdfExitOrig"] = Qpdf.Run("--check", src).code;
                    row["warningsIntroduced"] = introduced;
                    row["xrefReconstructed"] = outWarn.Any(w => w.Contains("reconstruct"));
                    if (!edit)
                    {
                        // full copy of the same untouched source: structural validity and what survives
                        var full = d.Save(P.FPDF_NO_INCREMENTAL, out _);
                        if (full != null)
                        {
                            var fp = Path.Combine(outDir, "probe", Path.GetFileNameWithoutExtension(f) + ".noop.full.pdf");
                            File.WriteAllBytes(fp, full);
                            row["fullBytes"] = full.Length;
                            row["fullQpdfExit"] = Qpdf.Run("--check", fp).code;
                            row["fullWarningsIntroduced"] = Warn(fp).Select(Norm).Where(w => !origWarn.Contains(w)).Distinct().ToList();
                            var fullText = Encoding.Latin1.GetString(full);
                            row["fullXrefKind"] = Regex.IsMatch(fullText, @"(?m)^xref\s*$") ? "classic xref" : fullText.Contains("/W[") ? "xref stream" : "?";
                            using var df = PdfDoc.Open(full, true, out _);
                            if (df != null) { var pf = df.LoadPage(0); row["fullAnnots"] = P.FPDFPage_GetAnnotCount(pf); row["fullSigs"] = P.FPDF_GetSignatureCount(df.Doc); }
                        }
                    }
                    using var d2 = PdfDoc.Open(inc, true, out _);
                    if (d2 != null)
                    {
                        var p2 = d2.LoadPage(0);
                        row["annotsAfter"] = P.FPDFPage_GetAnnotCount(p2);
                        row["sigsAfter"] = P.FPDF_GetSignatureCount(d2.Doc);
                    }
                }
                catch (Exception ex) { row["exception"] = ex.Message; }
                rows.Add(row);
            }
        }
        File.WriteAllText(Path.Combine(outDir, "probe.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder();
        sb.AppendLine("| File | Orig xref | Edit | Orig/out bytes | Orig is prefix | Objects rewritten | Appended xref | xref-stream: /Type /XRef present, endobj present | qpdf exit orig/out | after adding both (qpdf exit; no reconstruction) | Warnings introduced by the save | Annots before/after | Sigs before/after |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            if (r.ContainsKey("error")) { sb.AppendLine($"| {r["file"]} | | {r["edit"]} | {r.GetValueOrDefault("origBytes")} | ERROR {r["error"]} |||||||"); continue; }
            var objs = r.GetValueOrDefault("objectsWritten") as List<int>;
            var intro = r.GetValueOrDefault("warningsIntroduced") as List<string>;
            sb.AppendLine($"| {r["file"]} | {((bool)r["origUsesXrefStream"]! ? "stream" : "classic")} | {r["edit"]} | {r["origBytes"]}/{r.GetValueOrDefault("outBytes")} | {r.GetValueOrDefault("origIsExactPrefix")} | {(objs == null ? "?" : objs.Count)} | {r.GetValueOrDefault("appendedKind")} | {((string?)r.GetValueOrDefault("appendedKind") == "xref stream" ? r.GetValueOrDefault("xrefStreamHasTypeXRef") + " / " + r.GetValueOrDefault("lastStreamFollowedByEndobj") : "n/a")} | {r.GetValueOrDefault("qpdfExitOrig")}/{r.GetValueOrDefault("qpdfExitOut")} | {((string?)r.GetValueOrDefault("appendedKind") == "xref stream" ? "exit " + r.GetValueOrDefault("repairedQpdfExit") + "; reconstructed=" + r.GetValueOrDefault("repairedXrefReconstructed") + "; prefix kept=" + r.GetValueOrDefault("repairedOrigIsPrefix") + "; new warnings: " + string.Join(" / ", (r.GetValueOrDefault("repairedWarnings") as List<string>) ?? new()) : "n/a")} | {(intro == null || intro.Count == 0 ? "none" : string.Join(" ; ", intro.Take(3)))} | {r.GetValueOrDefault("annotsBefore")}/{r.GetValueOrDefault("annotsAfter")} | {r.GetValueOrDefault("sigsBefore")}/{r.GetValueOrDefault("sigsAfter")} |");
        }
        File.WriteAllText(Path.Combine(outDir, "probe.md"), sb.ToString());
    }
}

/// <summary>What PDFium leaves in the dictionary when only the mandatory calls are made (no SetStringValue / SetFlags).</summary>
public static unsafe class DefaultsProbe
{
    public static void Run(string outDir)
    {
        var bytes = File.ReadAllBytes(Path.Combine(outDir, "inputs", "base.pdf"));
        using var d = PdfDoc.Open(bytes, true, out _);
        var pg = d.LoadPage(0);
        FS_RECTF R(float l, float b, float r, float t) => new() { left = l, bottom = b, right = r, top = t };
        void Make(int st, bool quad = false, bool ink = false)
        {
            var an = P.FPDFPage_CreateAnnot(pg, st);
            var rc = R(100, 400, 200, 440);
            P.FPDFAnnot_SetRect(an, &rc);
            if (quad) { var q = new FS_QUADPOINTSF { x1 = 100, y1 = 440, x2 = 200, y2 = 440, x3 = 100, y3 = 400, x4 = 200, y4 = 400 }; P.FPDFAnnot_AppendAttachmentPoints(an, &q); }
            if (ink) { var pts = new[] { new FS_POINTF { x = 100, y = 400 }, new FS_POINTF { x = 150, y = 440 }, new FS_POINTF { x = 200, y = 400 } }; fixed (FS_POINTF* p = pts) P.FPDFAnnot_AddInkStroke(an, p, 3); }
            P.FPDFPage_CloseAnnot(an);
        }
        Make(P.ST_HIGHLIGHT, quad: true); Make(P.ST_UNDERLINE, quad: true); Make(P.ST_STRIKEOUT, quad: true); Make(P.ST_SQUIGGLY, quad: true);
        Make(P.ST_TEXT); Make(P.ST_FREETEXT); Make(P.ST_SQUARE); Make(P.ST_CIRCLE); Make(P.ST_INK, ink: true); Make(P.ST_STAMP); Make(P.ST_POPUP);
        var saved = d.Save(P.FPDF_INCREMENTAL, out _)!;
        var path = Path.Combine(outDir, "probe", "defaults.inc.pdf");
        File.WriteAllBytes(path, saved);
        var q2 = new Qpdf(path);
        var sb = new StringBuilder("| Subtype | Keys PDFium wrote when only /Rect (and QuadPoints / InkList) were set |\n|---|---|\n");
        foreach (var a in Program.ReadAnnots(q2)) sb.AppendLine($"| {a.Subtype} | {string.Join(" ", a.KeyNames)} |");
        File.WriteAllText(Path.Combine(outDir, "probe-defaults.md"), sb.ToString());
    }
}
