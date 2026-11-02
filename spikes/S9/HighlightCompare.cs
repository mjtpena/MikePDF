using System.Globalization;
using System.Text;

namespace S9;

/// <summary>S9 question 4: does Multiply over the tile match how PDFium renders a highlight's appearance stream?</summary>
internal static class HighlightCompare
{
    record Bg(string Name, Quad Q);
    static readonly Bg[] Bgs =
    [
        new("white paper + black text", new Quad(54, 704, 300, 716.5f)),
        new("salmon block", new Quad(54, 550, 254, 562.5f)),
        new("light green block", new Quad(300, 480, 480, 492.5f)),
        new("mid grey + white text", new Quad(80, 340, 300, 352.5f)),
        new("dark grey + black text", new Quad(340, 270, 530, 282.5f)),
    ];
    static readonly (string n, Rgb c)[] Cols = [("yellow", new(1, 1, 0)), ("green", new(0.5f, 1, 0.5f)), ("pink", new(1, 0.6f, 0.8f)), ("cyan", new(0, 1, 1)), ("orange", new(1, 0.6f, 0))];

    public static void Run()
    {
        using var dev = Gpu.Create(1);
        var csv = new StringBuilder("scale,background,colour,opacity,ssim_multiply_vs_pdfium_ap,ssim_sourceover_fallback_vs_pdfium_ap,ssim_pdfium_generated_ap_vs_ours,ssim_no_overlay_vs_pdfium_ap,max_abs_multiply,mean_abs_multiply,patch_ref,patch_multiply,patch_expected\n");
        var doc = BaseDoc.Build(1);
        var sMul = new List<double>(); var sSrc = new List<double>(); var sGen = new List<double>(); var sNone = new List<double>();
        var bySomething = new Dictionary<string, List<double>>();
        var sSnap = new List<double>(); var mxSnap = new List<int>();

        foreach (var s in new[] { 96.0 / 72, 384.0 / 72 })
        {
            using var bdoc = new PdfDoc(doc.Bytes); var bp = Pdfium.FPDF_LoadPage(bdoc.Handle, 0);
            var baseImg = PdfRender.Page(bp, s, 0); Pdfium.FPDF_ClosePage(bp);
            var m = Screen.PageToPx(0, s);
            var big = s > 3;
            foreach (var bg in Bgs.Take(big ? 3 : 5))
                foreach (var (cn, c) in big ? Cols.Where(x => x.n is "yellow" or "pink") : Cols)
                    foreach (var op in new[] { 1f, 0.5f })
                    {
                        var h = new HighlightM { Quads = [bg.Q], Color = c, Opacity = op, Nm = "MikePDF:h" };
                        var (saved, _) = doc.WithAnnotations([h]);
                        var (savedNoAp, _) = doc.WithAnnotations([h], withAp: false);
                        var reference = Screen.Reference(saved, s);
                        var generated = Screen.Reference(savedNoAp, s);
                        HighlightM.Snap = false; var multiply = Screen.Compose(dev, baseImg, [h], s, 0);
                        HighlightM.Snap = true; var snapped = Screen.Compose(dev, baseImg, [h], s, 0); HighlightM.Snap = false;
                        var fallback = Screen.Compose(dev, baseImg, [h], s, 0, multiplyFallbackAlpha: 0.4f * op);
                        var roi = Screen.Roi(m, h.Rect(), 2);
                        Bgra Cr(Bgra i) => Imaging.Crop(i, (int)roi.X, (int)roi.Y, (int)Math.Ceiling(roi.Width), (int)Math.Ceiling(roi.Height));
                        var r = Cr(reference); var a = Cr(multiply); var f = Cr(fallback); var g = Cr(generated); var n = Cr(baseImg);
                        var sn = Cr(snapped); var ssS = Imaging.Ssim(r, sn); var dS = Imaging.Diff(r, sn); sSnap.Add(ssS); mxSnap.Add(dS.MaxAbs);
                        var ssM = Imaging.Ssim(r, a); var ssF = Imaging.Ssim(r, f); var ssG = Imaging.Ssim(r, g); var ssN = Imaging.Ssim(r, n);
                        var d = Imaging.Diff(r, a);
                        // a small patch with no glyph ink: take the top-left 3x3 inside the highlight (between lines) and compare colours
                        var px = (int)(bg.Q.L * s) + 1; var py = (int)((Screen.PH - bg.Q.T) * s) + 1;
                        string P(Bgra i) { var o = (py * i.W + px) * 4; return $"{i.Px[o + 2]}/{i.Px[o + 1]}/{i.Px[o]}"; }
                        // expected = (1-a)*B + a*B*C  on the unmodified background pixel
                        var o2 = (py * baseImg.W + px) * 4; float B(int ch) => baseImg.Px[o2 + ch] / 255f;
                        string E() { float[] cc = [c.R, c.G, c.B]; var parts = new[] { 2, 1, 0 }.Select((ch, ci) => (int)Math.Round(255 * ((1 - op) * B(ch) + op * B(ch) * cc[ci]))); return string.Join('/', parts); }
                        csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{s:F3},{bg.Name},{cn},{op},{ssM:F4},{ssF:F4},{ssG:F4},{ssN:F4},{d.MaxAbs},{d.MeanAbs:F3},{P(reference)},{P(multiply)},{E()}"));
                        sMul.Add(ssM); sSrc.Add(ssF); sGen.Add(ssG); sNone.Add(ssN);
                        var key = $"{(big ? "4x" : "1x")} {bg.Name}"; if (!bySomething.TryGetValue(key, out var lst)) bySomething[key] = lst = []; lst.Add(ssM);
                        if (cn == "yellow" && op == 1f && !big) { Imaging.SavePng(Path.Combine(Program.Out, $"hl-{bg.Name.Replace(' ', '_').Replace("+", "and")}-ref.png"), r); Imaging.SavePng(Path.Combine(Program.Out, $"hl-{bg.Name.Replace(' ', '_').Replace(",", "")}-multiply.png"), a); Imaging.SavePng(Path.Combine(Program.Out, $"hl-{bg.Name.Replace(' ', '_').Replace(",", "")}-fallback.png"), f); }
                    }
        }
        File.WriteAllText(Path.Combine(Program.Out, "s9-highlight-compare.csv"), csv.ToString());
        string Sum(string n, List<double> v) => string.Create(CultureInfo.InvariantCulture, $"{n,-48} n={v.Count} min {v.Min():F4} mean {v.Average():F4}");
        var lines = new List<string> { Sum("Win2D Multiply (BlendEffect) vs PDFium AP", sMul), Sum("Win2D SourceOver alpha 0.4*CA vs PDFium AP", sSrc), Sum("PDFium-generated AP (no /AP) vs our AP", sGen), Sum("no overlay vs PDFium AP (control)", sNone) };
        lines.Insert(1, Sum("Win2D Multiply + PDFium rect pixel snapping", sSnap) + $" max abs channel difference over all samples: {mxSnap.Max()}");
        foreach (var (k, v) in bySomething) lines.Add(Sum("  Multiply: " + k, v));
        File.WriteAllLines(Path.Combine(Program.Out, "s9-highlight-summary.txt"), lines);
        foreach (var l in lines) Console.WriteLine(l);
    }
}
