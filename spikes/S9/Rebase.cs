using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;

namespace S9;

/// <summary>S9 questions 3 and 4 (drift): overlay-over-old-tile versus a fresh PDFium render of the saved file, per kind, scale and rotation; plus the cost of reopening.</summary>
internal static class Rebase
{
    static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

    public static List<Markup> Kinds()
    {
        var list = new List<Markup>
        {
            new HighlightM { Kind = "Highlight yellow CA1", Quads = [new Quad(54, 704, 300, 716.5f)], Color = new(1, 1, 0), Opacity = 1f, Nm = "MikePDF:k01" },
            new HighlightM { Kind = "Highlight green CA0.5 over salmon block", Quads = [new Quad(54, 550, 254, 562.5f)], Color = new(0.5f, 1, 0.5f), Opacity = 0.5f, Nm = "MikePDF:k02" },
            new HighlightM { Kind = "Underline", Sub = "Underline", Quads = [new Quad(54, 662, 260, 674.5f)], Color = new(0.9f, 0, 0), Nm = "MikePDF:k03" },
            new HighlightM { Kind = "StrikeOut", Sub = "StrikeOut", Quads = [new Quad(54, 648, 300, 660.5f)], Color = new(0, 0, 0.9f), Nm = "MikePDF:k04" },
            new ShapeM { Kind = "Square", Sub = "Square", L = 330, B = 600, R = 480, T = 660, W = 2, Color = new(0.9f, 0, 0), Nm = "MikePDF:k05" },
            new ShapeM { Kind = "Circle", Sub = "Circle", L = 330, B = 520, R = 460, T = 580, W = 1.5f, Color = new(0, 0, 0.9f), Nm = "MikePDF:k06" },
            new ShapeM { Kind = "Line", Sub = "Line", X1 = 60, Y1 = 300, X2 = 260, Y2 = 240, W = 3, Color = new(0, 0.6f, 0), Nm = "MikePDF:k07" },
            new InkM { Kind = "Ink variable width (3 strokes)", Strokes = Gen.Strokes(3, 5), Color = new(0.1f, 0.1f, 0.1f), Nm = "MikePDF:k08" },
            new InkM { Kind = "Ink highlighter Multiply CA0.5", Highlighter = true, Opacity = 0.5f, Color = new(1, 0.4f, 0.7f), Strokes = [HighlighterStroke()], Nm = "MikePDF:k09" },
            new FreeTextM { Kind = "FreeText 12pt (Helvetica vs Arial)", L = 330, B = 100, R = 520, T = 140, Text = "Hello MikePDF 123", Size = 12, Color = new(0, 0, 0), Nm = "MikePDF:k10" },
        };
        return list;
    }

    static InkStroke HighlighterStroke()
    {
        var pts = new Vector2[60]; var w = new float[60];
        for (var i = 0; i < 60; i++) { pts[i] = new Vector2(80 + i * 4f, 458 + 4 * MathF.Sin(i * 0.3f)); w[i] = 14; }
        return new InkStroke { Pts = pts, W = w };
    }

    public static void Run()
    {
        using var dev = Gpu.Create(1);   // iGPU: whatever the machine's integrated adapter does is the closer proxy for R1
        var rows = new StringBuilder("kind,rotate,scale,roi_ssim,roi_mean_abs,roi_max_abs,page_ssim,page_mean_abs\n");
        var kinds = Kinds();
        var results = new List<string>();

        // ---- 1. per kind, rotation 0, three scales
        var baseDoc = BaseDoc.Build(1);
        foreach (var s in new[] { 96.0 / 72, 192.0 / 72, 384.0 / 72 })
        {
            using var bdoc = new PdfDoc(baseDoc.Bytes); var bp = Pdfium.FPDF_LoadPage(bdoc.Handle, 0);
            var baseImg = PdfRender.Page(bp, s, 0); Pdfium.FPDF_ClosePage(bp);
            foreach (var k in kinds)
            {
                var (saved, _) = baseDoc.WithAnnotations([k]);
                var reference = Screen.Reference(saved, s);
                var screen = Screen.Compose(dev, baseImg, [k], s, 0);
                var roi = Screen.Roi(Screen.PageToPx(0, s), k.Rect());
                var rc = Imaging.Crop(reference, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var sc = Imaging.Crop(screen, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var ssim = Imaging.Ssim(rc, sc); var d = Imaging.Diff(rc, sc);
                double pageSsim = double.NaN, pageMean = double.NaN;
                if (s < 3) { pageSsim = Imaging.Ssim(reference, screen); pageMean = Imaging.Diff(reference, screen).MeanAbs; }
                // Control: how different is the tile WITHOUT the overlay (i.e. what the user sees if the overlay is missing)?
                var noOverlay = Imaging.Crop(baseImg, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var ssimNone = Imaging.Ssim(rc, noOverlay);
                rows.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{k.Kind},0,{s:F3},{ssim:F4},{d.MeanAbs:F3},{d.MaxAbs},{pageSsim:F4},{pageMean:F4}"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"scale {s:F2} {k.Kind,-42} ROI SSIM {ssim:F4} (no overlay at all: {ssimNone:F4}) meanAbs {d.MeanAbs:F2} max {d.MaxAbs}  page SSIM {pageSsim:F4}"));
                if (Math.Abs(s - 96.0 / 72) < 1e-6) { Imaging.SavePng(Path.Combine(Program.Out, $"rebase-{k.Nm[^3..]}-ref.png"), rc); Imaging.SavePng(Path.Combine(Program.Out, $"rebase-{k.Nm[^3..]}-overlay.png"), sc); }
            }
            Console.WriteLine();
        }

        // ---- 2. all kinds at once, four rotations, 100% (this is AC-12's rotation sweep)
        foreach (var rot in new[] { 0, 90, 180, 270 })
        {
            var s = 96.0 / 72;
            var bd = BaseDoc.Build(1, rot);
            using var bdoc = new PdfDoc(bd.Bytes); var bp = Pdfium.FPDF_LoadPage(bdoc.Handle, 0);
            var baseImg = PdfRender.Page(bp, s, 0); Pdfium.FPDF_ClosePage(bp);
            var (saved, _) = bd.WithAnnotations(kinds);
            var reference = Screen.Reference(saved, s);
            var screen = Screen.Compose(dev, baseImg, kinds, s, rot);
            var ssim = Imaging.Ssim(reference, screen);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"rotation {rot,3}: all {kinds.Count} kinds, whole page SSIM {ssim:F4}"));
            var worst = double.MaxValue; var worstKind = "";
            var m = Screen.PageToPx(rot, s);
            foreach (var k in kinds)
            {
                var roi = Screen.Roi(m, k.Rect());
                var a = Imaging.Crop(reference, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var b = Imaging.Crop(screen, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var sk = Imaging.Ssim(a, b);
                rows.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{k.Kind},{rot},{s:F3},{sk:F4},,,{ssim:F4},"));
                if (sk < worst) { worst = sk; worstKind = k.Kind; }
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {k.Kind,-42} ROI SSIM {sk:F4}"));
            }
            Console.WriteLine($"    worst: {worstKind} {worst:F4}");
            Imaging.SavePng(Path.Combine(Program.Out, $"rebase-all-rot{rot}-ref.png"), reference);
            Imaging.SavePng(Path.Combine(Program.Out, $"rebase-all-rot{rot}-overlay.png"), screen);
        }

        // ---- 2b. highlight rectangles snapped to the pixel grid the way PDFium fills them, all rotations and 3 scales
        HighlightM.Snap = true;
        foreach (var rot in new[] { 0, 90, 180, 270 })
            foreach (var s in new[] { 96.0 / 72, 192.0 / 72, 384.0 / 72 })
            {
                var bd = BaseDoc.Build(1, rot);
                using var bdoc = new PdfDoc(bd.Bytes); var bp = Pdfium.FPDF_LoadPage(bdoc.Handle, 0);
                var baseImg = PdfRender.Page(bp, s, 0); Pdfium.FPDF_ClosePage(bp);
                var m = Screen.PageToPx(rot, s);
                foreach (var idx in new[] { 0, 1 })
                {
                    var k = kinds[idx]; var (saved, _) = bd.WithAnnotations([k]);
                    var reference = Screen.Reference(saved, s); var screen = Screen.Compose(dev, baseImg, [k], s, rot);
                    var roi = Screen.Roi(m, k.Rect());
                    var a = Imaging.Crop(reference, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                    var b = Imaging.Crop(screen, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                    var line = string.Create(CultureInfo.InvariantCulture, $"SNAPPED rects rot {rot,3} scale {s:F2} {k.Kind,-42} ROI SSIM {Imaging.Ssim(a, b):F4} meanAbs {Imaging.Diff(a, b).MeanAbs:F3} max {Imaging.Diff(a, b).MaxAbs}");
                    Console.WriteLine(line); results.Add(line);
                }
            }
        HighlightM.Snap = false;

        // ---- 3. the swap: overlay left on top of the NEW tile (which already contains the annotation) = double drawing
        {
            var s = 96.0 / 72;
            foreach (var idx in new[] { 0, 1, 8 })
            {
                var k = kinds[idx];
                var (saved, _) = baseDoc.WithAnnotations([k]);
                var newTile = Screen.Reference(saved, s);
                var doubled = Screen.Compose(dev, newTile, [k], s, 0);
                var roi = Screen.Roi(Screen.PageToPx(0, s), k.Rect());
                var a = Imaging.Crop(newTile, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var b = Imaging.Crop(doubled, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                var line = string.Create(CultureInfo.InvariantCulture, $"double drawing (overlay kept on the rebased tile): {k.Kind}: ROI SSIM vs correct {Imaging.Ssim(a, b):F4}, mean abs {Imaging.Diff(a, b).MeanAbs:F2}");
                Console.WriteLine(line); results.Add(line);
            }
        }

        // ---- 4. cost of reopening (option 3 / rebase) as a function of document size
        Console.WriteLine();
        var tmp = Path.Combine(Program.Out, "tmp-rebase"); Directory.CreateDirectory(tmp);
        var timing = new StringBuilder("pages,base_bytes,annots_on_page0,appended_bytes,build_update_ms,write_ms,open_ms_p50,first_tile_ms_p50,reopen_to_first_tile_ms_p50,reopen_to_first_tile_ms_max\n");
        foreach (var pages in new[] { 100, 1000, 5000 })
        {
            var bd = BaseDoc.Build(pages);
            foreach (var na in new[] { 20, 500 })
            {
                var ann = Gen.InkAnnots(na, 31);
                var t0 = Stopwatch.GetTimestamp();
                var (saved, _) = bd.WithAnnotations(ann);
                var tBuild = (Stopwatch.GetTimestamp() - t0) * ToMs;
                var path = Path.Combine(tmp, $"r{pages}-{na}.pdf");
                t0 = Stopwatch.GetTimestamp(); File.WriteAllBytes(path, saved); var tWrite = (Stopwatch.GetTimestamp() - t0) * ToMs;
                var open = new List<double>(); var first = new List<double>(); var both = new List<double>();
                for (var rep = 0; rep < 9; rep++)
                {
                    t0 = Stopwatch.GetTimestamp();
                    var (d, _) = PdfDoc.OpenPath(path); var t1 = Stopwatch.GetTimestamp();
                    var pg = Pdfium.FPDF_LoadPage(d, 0);
                    var tile = PdfRender.Viewport(pg, 96.0 / 72, 0, 0, 512, 512);
                    var t2 = Stopwatch.GetTimestamp();
                    Pdfium.FPDF_ClosePage(pg); Pdfium.FPDF_CloseDocument(d);
                    if (rep > 0) { open.Add((t1 - t0) * ToMs); first.Add((t2 - t1) * ToMs); both.Add((t2 - t0) * ToMs); }
                }
                open.Sort(); first.Sort(); both.Sort();
                var line = string.Create(CultureInfo.InvariantCulture, $"{pages},{bd.Bytes.Length},{na},{saved.Length - bd.Bytes.Length},{tBuild:F1},{tWrite:F1},{open[open.Count / 2]:F2},{first[first.Count / 2]:F2},{both[both.Count / 2]:F2},{both[^1]:F2}");
                timing.AppendLine(line); Console.WriteLine("reopen: " + line);
                File.Delete(path);
            }
        }
        try { Directory.Delete(tmp, true); } catch { }
        File.WriteAllText(Path.Combine(Program.Out, "s9-rebase-ssim.csv"), rows.ToString());
        File.WriteAllText(Path.Combine(Program.Out, "s9-rebase-reopen.csv"), timing.ToString());
        File.WriteAllLines(Path.Combine(Program.Out, "s9-rebase-doubledraw.txt"), results);
    }
}
