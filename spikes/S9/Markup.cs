using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace S9;

/// <summary>Annotation geometry shared by the overlay (Win2D) and the appearance-stream generator (PDF operators): the "MikePDF.Markup" idea of annotation-core 3.3.</summary>
internal static class Fmt
{
    public static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

internal readonly record struct Rgb(float R, float G, float B)
{
    public Color ToColor(float alpha = 1f) => Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(R * 255), (byte)Math.Round(G * 255), (byte)Math.Round(B * 255));
    public string Rg => $"{Fmt.F(R)} {Fmt.F(G)} {Fmt.F(B)}";
}

internal sealed class InkStroke
{
    public Vector2[] Pts = [];
    public float[] W = [];

    /// <summary>Filled outline of a variable-width stroke with round caps (ink-pen spec: the appearance stream is a filled outline). Used for both drawing paths.</summary>
    public List<Vector2> Outline()
    {
        var n = Pts.Length; var poly = new List<Vector2>(n * 2 + 14);
        if (n == 1) { for (var i = 0; i < 12; i++) { var a = i * MathF.PI / 6; poly.Add(Pts[0] + (W[0] / 2) * new Vector2(MathF.Cos(a), MathF.Sin(a))); } return poly; }
        var left = new Vector2[n]; var right = new Vector2[n]; var dirs = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var d = Pts[Math.Min(n - 1, i + 1)] - Pts[Math.Max(0, i - 1)];
            d = d.LengthSquared() < 1e-12f ? new Vector2(1, 0) : Vector2.Normalize(d);
            dirs[i] = d; var nrm = new Vector2(-d.Y, d.X); var hw = W[i] / 2;
            left[i] = Pts[i] + nrm * hw; right[i] = Pts[i] - nrm * hw;
        }
        poly.AddRange(left);
        { var d = dirs[n - 1]; var nrm = new Vector2(-d.Y, d.X); var hw = W[n - 1] / 2; for (var k = 1; k < 6; k++) { var t = k * MathF.PI / 6; poly.Add(Pts[n - 1] + hw * (MathF.Cos(t) * nrm + MathF.Sin(t) * d)); } }
        for (var i = n - 1; i >= 0; i--) poly.Add(right[i]);
        { var d = dirs[0]; var nrm = new Vector2(-d.Y, d.X); var hw = W[0] / 2; for (var k = 1; k < 6; k++) { var t = k * MathF.PI / 6; poly.Add(Pts[0] + hw * (-MathF.Cos(t) * nrm - MathF.Sin(t) * d)); } }
        return poly;
    }

    public CanvasGeometry ToGeometry(ICanvasResourceCreator rc)
    {
        var poly = Outline();
        using var pb = new CanvasPathBuilder(rc);
        pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);   // PDF "f" is non-zero winding; Win2D defaults to alternate
        pb.BeginFigure(poly[0]);
        for (var i = 1; i < poly.Count; i++) pb.AddLine(poly[i]);
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
    }

    public (float l, float b, float r, float t) Bounds()
    {
        float l = float.MaxValue, b = float.MaxValue, r = float.MinValue, t = float.MinValue;
        for (var i = 0; i < Pts.Length; i++) { var h = W[i] / 2 + 0.5f; l = Math.Min(l, Pts[i].X - h); r = Math.Max(r, Pts[i].X + h); b = Math.Min(b, Pts[i].Y - h); t = Math.Max(t, Pts[i].Y + h); }
        return (l, b, r, t);
    }
}

internal readonly record struct Quad(float L, float B, float R, float T);

/// <summary>One annotation in the "edit model" with the three things the display strategies need: PDF dictionary, appearance stream, and a Win2D drawer.</summary>
internal abstract class Markup
{
    public string Kind = "";
    public string Nm = "";
    public abstract (float l, float b, float r, float t) Rect();
    public abstract string Subtype { get; }
    public virtual string ExtraDict() => "";
    public abstract string ApContent();
    public virtual string ApExtGState => "";
    public virtual bool Multiply => false;
    public virtual float Ca => 1f;
    /// <summary>Draw in page space (the caller has set Transform to page->pixels).</summary>
    public abstract void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc);
}

internal sealed class HighlightM : Markup
{
    /// <summary>Spike switch: draw highlight rectangles on the pixel grid the way PDFium does.</summary>
    public static bool Snap;
    public List<Quad> Quads = []; public Rgb Color; public float Opacity = 1f; public string Sub = "Highlight";
    public HighlightM() { Kind = "Highlight"; }
    public override string Subtype => Sub;
    public override bool Multiply => Sub == "Highlight";
    public override float Ca => Opacity;
    public override (float, float, float, float) Rect() => (Quads.Min(q => q.L) - 1, Quads.Min(q => q.B) - 1, Quads.Max(q => q.R) + 1, Quads.Max(q => q.T) + 1);
    public override string ExtraDict()
    {
        var sb = new StringBuilder(" /QuadPoints [");
        foreach (var q in Quads) sb.Append($"{Fmt.F(q.L)} {Fmt.F(q.T)} {Fmt.F(q.R)} {Fmt.F(q.T)} {Fmt.F(q.L)} {Fmt.F(q.B)} {Fmt.F(q.R)} {Fmt.F(q.B)} ");
        return sb.Append($"] /C [{Color.Rg}] /CA {Fmt.F(Opacity)}").ToString();
    }
    public override string ApExtGState => Multiply ? $"/ExtGState << /GS << /Type /ExtGState /BM /Multiply /CA {Fmt.F(Opacity)} /ca {Fmt.F(Opacity)} >> >>" : $"/ExtGState << /GS << /Type /ExtGState /CA {Fmt.F(Opacity)} /ca {Fmt.F(Opacity)} >> >>";
    public override string ApContent()
    {
        var sb = new StringBuilder("/GS gs\n");
        switch (Sub)
        {
            case "Highlight":
                sb.Append(Color.Rg).Append(" rg\n");
                foreach (var q in Quads) sb.Append($"{Fmt.F(q.L)} {Fmt.F(q.B)} m {Fmt.F(q.R)} {Fmt.F(q.B)} l {Fmt.F(q.R)} {Fmt.F(q.T)} l {Fmt.F(q.L)} {Fmt.F(q.T)} l h f\n");
                break;
            case "Underline":
                sb.Append(Color.Rg).Append(" RG 1 w\n");
                foreach (var q in Quads) sb.Append($"{Fmt.F(q.L)} {Fmt.F(q.B + 1)} m {Fmt.F(q.R)} {Fmt.F(q.B + 1)} l S\n");
                break;
            case "StrikeOut":
                sb.Append(Color.Rg).Append(" RG 1 w\n");
                foreach (var q in Quads) sb.Append($"{Fmt.F(q.L)} {Fmt.F((q.B + q.T) / 2)} m {Fmt.F(q.R)} {Fmt.F((q.B + q.T) / 2)} l S\n");
                break;
        }
        return sb.ToString();
    }
    public override void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        var col = Color.ToColor(Opacity);
        foreach (var q in Quads)
        {
            switch (Sub)
            {
                case "Highlight":
                    if (!Snap) { ds.FillRectangle(q.L, q.B, q.R - q.L, q.T - q.B, col); break; }
                    {
                        // PDFium fills an axis-aligned rectangle with no anti-aliasing on a pixel grid (CFX_RenderDevice::DrawPathWithBlend:
                        // outer rect, then trimmed to ceil(width) x ceil(height)). Reproduce that rule in device space.
                        var m = ds.Transform; var a = Vector2.Transform(new(q.L, q.B), m); var b = Vector2.Transform(new(q.R, q.T), m);
                        float l = Math.Min(a.X, b.X), r = Math.Max(a.X, b.X), t = Math.Min(a.Y, b.Y), bt = Math.Max(a.Y, b.Y);
                        int il = (int)MathF.Floor(l), ir = (int)MathF.Ceiling(r), it = (int)MathF.Floor(t), ib = (int)MathF.Ceiling(bt);
                        var w = (int)MathF.Ceiling(r - l); var h = (int)MathF.Ceiling(bt - t);
                        if (w < 1) { w = 1; if (il == ir) ir++; }
                        if (h < 1) { h = 1; if (it == ib) ib++; }
                        if (ir - il >= w + 1) { if (l - il > ir - r) il++; else ir--; }
                        if (ib - it >= h + 1) { if (t - it > ib - bt) it++; else ib--; }
                        ds.Transform = Matrix3x2.Identity; ds.FillRectangle(il, it, ir - il, ib - it, col); ds.Transform = m;
                    }
                    break;
                case "Underline": ds.DrawLine(q.L, q.B + 1, q.R, q.B + 1, col, 1f); break;
                case "StrikeOut": ds.DrawLine(q.L, (q.B + q.T) / 2, q.R, (q.B + q.T) / 2, col, 1f); break;
            }
        }
    }
}

internal sealed class InkM : Markup
{
    public List<InkStroke> Strokes = []; public Rgb Color; public float Opacity = 1f; public bool Highlighter;
    List<List<Vector2>>? _outlines;
    public InkM() { Kind = "Ink"; }
    public override string Subtype => "Ink";
    public override bool Multiply => Highlighter;
    public override float Ca => Opacity;
    public override (float, float, float, float) Rect()
    {
        float l = float.MaxValue, b = float.MaxValue, r = float.MinValue, t = float.MinValue;
        foreach (var s in Strokes) { var x = s.Bounds(); l = Math.Min(l, x.l); b = Math.Min(b, x.b); r = Math.Max(r, x.r); t = Math.Max(t, x.t); }
        return (l, b, r, t);
    }
    public override string ExtraDict()
    {
        var sb = new StringBuilder(" /InkList [");
        foreach (var s in Strokes) { sb.Append('['); foreach (var p in s.Pts) sb.Append($"{Fmt.F(p.X)} {Fmt.F(p.Y)} "); sb.Append("] "); }
        return sb.Append($"] /BS << /W 1 >> /C [{Color.Rg}] /CA {Fmt.F(Opacity)}").ToString();
    }
    public override string ApExtGState => Highlighter ? $"/ExtGState << /GS << /Type /ExtGState /BM /Multiply /CA {Fmt.F(Opacity)} /ca {Fmt.F(Opacity)} >> >>" : $"/ExtGState << /GS << /Type /ExtGState /CA {Fmt.F(Opacity)} /ca {Fmt.F(Opacity)} >> >>";
    public override string ApContent()
    {
        var sb = new StringBuilder("/GS gs\n").Append(Color.Rg).Append(" rg\n");
        _outlines ??= Strokes.Select(s => s.Outline()).ToList();
        foreach (var o in _outlines)
        {
            sb.Append($"{Fmt.F(o[0].X)} {Fmt.F(o[0].Y)} m ");
            for (var i = 1; i < o.Count; i++) sb.Append($"{Fmt.F(o[i].X)} {Fmt.F(o[i].Y)} l ");
            sb.Append("h f\n");
        }
        return sb.ToString();
    }
    public override void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        var col = Color.ToColor(Opacity);
        foreach (var s in Strokes) { using var g = s.ToGeometry(rc); ds.FillGeometry(g, col); }
    }
}

internal sealed class ShapeM : Markup
{
    public string Sub = "Square"; public float L, B, R, T; public float W = 2; public Rgb Color; public float X1, Y1, X2, Y2;
    public override string Subtype => Sub;
    public override (float, float, float, float) Rect() => Sub == "Line" ? (Math.Min(X1, X2) - W - 1, Math.Min(Y1, Y2) - W - 1, Math.Max(X1, X2) + W + 1, Math.Max(Y1, Y2) + W + 1) : (L, B, R, T);
    public override string ExtraDict() => Sub == "Line" ? $" /L [{Fmt.F(X1)} {Fmt.F(Y1)} {Fmt.F(X2)} {Fmt.F(Y2)}] /BS << /W {Fmt.F(W)} >> /C [{Color.Rg}]" : $" /BS << /W {Fmt.F(W)} >> /C [{Color.Rg}]";
    const float K = 0.5522847498f;
    static void Ellipse(Action<string> emit, float l, float b, float r, float t)
    {
        float cx = (l + r) / 2, cy = (b + t) / 2, rx = (r - l) / 2, ry = (t - b) / 2;
        emit($"{Fmt.F(cx + rx)} {Fmt.F(cy)} m {Fmt.F(cx + rx)} {Fmt.F(cy + K * ry)} {Fmt.F(cx + K * rx)} {Fmt.F(cy + ry)} {Fmt.F(cx)} {Fmt.F(cy + ry)} c ");
        emit($"{Fmt.F(cx - K * rx)} {Fmt.F(cy + ry)} {Fmt.F(cx - rx)} {Fmt.F(cy + K * ry)} {Fmt.F(cx - rx)} {Fmt.F(cy)} c ");
        emit($"{Fmt.F(cx - rx)} {Fmt.F(cy - K * ry)} {Fmt.F(cx - K * rx)} {Fmt.F(cy - ry)} {Fmt.F(cx)} {Fmt.F(cy - ry)} c ");
        emit($"{Fmt.F(cx + K * rx)} {Fmt.F(cy - ry)} {Fmt.F(cx + rx)} {Fmt.F(cy - K * ry)} {Fmt.F(cx + rx)} {Fmt.F(cy)} c h S\n");
    }
    public override string ApContent()
    {
        var sb = new StringBuilder($"{Color.Rg} RG {Fmt.F(W)} w\n"); var h = W / 2;
        switch (Sub)
        {
            case "Square": sb.Append($"{Fmt.F(L + h)} {Fmt.F(B + h)} {Fmt.F(R - L - W)} {Fmt.F(T - B - W)} re S\n"); break;
            case "Circle": Ellipse(s => sb.Append(s), L + h, B + h, R - h, T - h); break;
            case "Line": sb.Append($"{Fmt.F(X1)} {Fmt.F(Y1)} m {Fmt.F(X2)} {Fmt.F(Y2)} l S\n"); break;
        }
        return sb.ToString();
    }
    public override void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        var col = Color.ToColor(); var h = W / 2;
        switch (Sub)
        {
            case "Square": ds.DrawRectangle(L + h, B + h, R - L - W, T - B - W, col, W); break;
            case "Circle":
                {
                    using var pb = new CanvasPathBuilder(rc); float l = L + h, b = B + h, r = R - h, t = T - h;
                    float cx = (l + r) / 2, cy = (b + t) / 2, rx = (r - l) / 2, ry = (t - b) / 2;
                    pb.BeginFigure(cx + rx, cy);
                    pb.AddCubicBezier(new(cx + rx, cy + K * ry), new(cx + K * rx, cy + ry), new(cx, cy + ry));
                    pb.AddCubicBezier(new(cx - K * rx, cy + ry), new(cx - rx, cy + K * ry), new(cx - rx, cy));
                    pb.AddCubicBezier(new(cx - rx, cy - K * ry), new(cx - K * rx, cy - ry), new(cx, cy - ry));
                    pb.AddCubicBezier(new(cx + K * rx, cy - ry), new(cx + rx, cy - K * ry), new(cx + rx, cy));
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var g = CanvasGeometry.CreatePath(pb); ds.DrawGeometry(g, col, W);
                    break;
                }
            case "Line": ds.DrawLine(X1, Y1, X2, Y2, col, W); break;
        }
    }
}

internal sealed class FreeTextM : Markup
{
    public float L, B, R, T; public string Text = ""; public float Size = 12; public Rgb Color;
    public FreeTextM() { Kind = "FreeText"; }
    public override string Subtype => "FreeText";
    public override (float, float, float, float) Rect() => (L, B, R, T);
    public override string ExtraDict() => $" /DA (/Helv {Fmt.F(Size)} Tf {Color.Rg} rg) /Contents ({Text})";
    public override string ApContent() => $"{Color.Rg} RG 0.5 w {Fmt.F(L + 0.25f)} {Fmt.F(B + 0.25f)} {Fmt.F(R - L - 0.5f)} {Fmt.F(T - B - 0.5f)} re S\nBT /Helv {Fmt.F(Size)} Tf {Color.Rg} rg {Fmt.F(L + 4)} {Fmt.F(T - 4 - Size * 0.905f)} Td ({Text}) Tj ET\n";
    public override string ApExtGState => "";
    public string ApFont => "/Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>";
    public override void Draw(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        var col = Color.ToColor();
        ds.DrawRectangle(L + 0.25f, B + 0.25f, R - L - 0.5f, T - B - 0.5f, col, 0.5f);
        // Page space has y up; text must be drawn upright, so flip locally around the baseline.
        var fmt = new CanvasTextFormat { FontFamily = "Arial", FontSize = Size, WordWrapping = CanvasWordWrapping.NoWrap };
        using var lay = new CanvasTextLayout(rc, Text, fmt, 10000, 1000);
        var baseline = lay.LineMetrics[0].Baseline;
        var old = ds.Transform;
        // local: origin at (L+4, T-4-Size*0.905) = PDF baseline; flip y; the layout's top is "baseline" above that.
        ds.Transform = Matrix3x2.CreateScale(1, -1) * Matrix3x2.CreateTranslation(L + 4, T - 4 - Size * 0.905f) * old;
        ds.DrawTextLayout(lay, 0, -(float)baseline, col);
        ds.Transform = old;
    }
}

/// <summary>Deterministic content generators.</summary>
internal static class Gen
{
    public static readonly Rgb[] InkColours = [new(0.1f, 0.1f, 0.1f), new(0.8f, 0.1f, 0.1f), new(0.1f, 0.3f, 0.8f)];
    public static readonly Rgb[] HlColours = [new(1f, 1f, 0f), new(0.5f, 1f, 0.5f), new(1f, 0.6f, 0.8f)];

    public static List<InkStroke> Strokes(int n, int seed, int pointsPer = 48)
    {
        var rnd = new Random(seed); var list = new List<InkStroke>(n);
        for (var i = 0; i < n; i++)
        {
            var pts = new Vector2[pointsPer]; var w = new float[pointsPer];
            var p = new Vector2(30 + (float)rnd.NextDouble() * 540, 30 + (float)rnd.NextDouble() * 730); var th = (float)(rnd.NextDouble() * Math.PI * 2);
            var phase = (float)(rnd.NextDouble() * 6.28); var baseW = 0.8f + (float)rnd.NextDouble() * 1.2f;
            for (var k = 0; k < pointsPer; k++)
            {
                pts[k] = p; w[k] = baseW * (0.5f + 0.5f * MathF.Abs(MathF.Sin(phase + k * 0.15f))) + 0.3f;
                th += ((float)rnd.NextDouble() - 0.5f) * 0.5f;
                p += new Vector2(MathF.Cos(th), MathF.Sin(th)) * 1.4f;
                p = new Vector2(Math.Clamp(p.X, 20, 592), Math.Clamp(p.Y, 20, 772));
            }
            list.Add(new InkStroke { Pts = pts, W = w });
        }
        return list;
    }

    /// <summary>n ink annotations of one stroke each (as the batching in ink-pen 3.x would produce for a short session, worst case).</summary>
    public static List<Markup> InkAnnots(int n, int seed) { var s = Strokes(n, seed); return s.Select((x, i) => (Markup)new InkM { Strokes = [x], Color = InkColours[i % 3], Nm = $"MikePDF:ink{i:D5}" }).ToList(); }

    public static List<Quad> Quads(int n, int seed)
    {
        var rnd = new Random(seed); var q = new List<Quad>(n);
        for (var i = 0; i < n; i++)
        {
            var line = rnd.Next(0, 44); var x = 54 + (float)rnd.NextDouble() * 330; var w = 30 + (float)rnd.NextDouble() * 150;
            var baseline = 735 - 14 * line; q.Add(new Quad(x, baseline - 3, Math.Min(558, x + w), baseline + 9.5f));
        }
        return q;
    }

    public static List<Markup> HighlightAnnots(int n, int seed, float opacity = 1f)
    {
        var qs = Quads(n, seed);
        return qs.Select((q, i) => (Markup)new HighlightM { Quads = [q], Color = HlColours[i % 3], Opacity = opacity, Nm = $"MikePDF:hl{i:D5}" }).ToList();
    }
}
