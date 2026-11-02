using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace S9;

/// <summary>What the screen would show: PDFium tile of the base file plus the Win2D overlay of the edit model. Offscreen only.</summary>
internal static class Screen
{
    public const float PW = 612, PH = 792;

    /// <summary>Page space (points, y up, unrotated) to displayed pixels for /Rotate 0/90/180/270 (clockwise).</summary>
    public static Matrix3x2 PageToPx(int rot, double s)
    {
        var k = (float)s;
        return rot switch
        {
            0 => new Matrix3x2(k, 0, 0, -k, 0, PH * k),
            90 => new Matrix3x2(0, k, k, 0, 0, 0),
            180 => new Matrix3x2(-k, 0, 0, k, PW * k, 0),
            270 => new Matrix3x2(0, -k, -k, 0, PH * k, PW * k),
            _ => throw new ArgumentOutOfRangeException(nameof(rot)),
        };
    }

    public static (int w, int h) DisplayedSize(int rot, double s) => rot is 0 or 180 ? ((int)Math.Round(PW * s), (int)Math.Round(PH * s)) : ((int)Math.Round(PH * s), (int)Math.Round(PW * s));

    public static Rect Roi(Matrix3x2 m, (float l, float b, float r, float t) rect, float pad = 3)
    {
        var pts = new[] { new Vector2(rect.l, rect.b), new Vector2(rect.r, rect.b), new Vector2(rect.l, rect.t), new Vector2(rect.r, rect.t) }.Select(p => Vector2.Transform(p, m)).ToArray();
        var x0 = pts.Min(p => p.X) - pad; var x1 = pts.Max(p => p.X) + pad; var y0 = pts.Min(p => p.Y) - pad; var y1 = pts.Max(p => p.Y) + pad;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <param name="multiplyFallbackAlpha">null = Multiply markups use BlendEffect(Multiply); a value = draw them SourceOver with that alpha instead (the OQ-INK-6 fallback).</param>
    public static Bgra Compose(CanvasDevice dev, Bgra baseImg, IReadOnlyList<Markup> annots, double s, int rot, float? multiplyFallbackAlpha = null)
    {
        var w = baseImg.W; var h = baseImg.H; var m = PageToPx(rot, s);
        using var tile = CanvasBitmap.CreateFromBytes(dev, baseImg.Px, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Ignore);
        var acc = new CanvasRenderTarget(dev, w, h, 96);
        using (var ds = acc.CreateDrawingSession()) ds.DrawImage(tile);
        foreach (var a in annots)
        {
            if (a.Multiply && multiplyFallbackAlpha == null)
            {
                using var layer = new CanvasRenderTarget(dev, w, h, 96);
                using (var ls = layer.CreateDrawingSession()) { ls.Clear(Microsoft.UI.Colors.Transparent); ls.Transform = m; a.Draw(ls, dev); }
                using var copy = new CanvasRenderTarget(dev, w, h, 96);
                using (var cs = copy.CreateDrawingSession()) cs.DrawImage(acc);
                using var next = new CanvasRenderTarget(dev, w, h, 96);
                using (var ns = next.CreateDrawingSession()) { using var fx = new BlendEffect { Background = copy, Foreground = layer, Mode = BlendEffectMode.Multiply }; ns.DrawImage(fx); }
                using (var ds = acc.CreateDrawingSession()) { ds.Blend = CanvasBlend.Copy; ds.DrawImage(next); }
            }
            else
            {
                using var ds = acc.CreateDrawingSession(); ds.Transform = m;
                if (a.Multiply && multiplyFallbackAlpha != null && a is HighlightM hm)
                {
                    var saved = hm.Opacity; hm.Opacity = multiplyFallbackAlpha.Value; a.Draw(ds, dev); hm.Opacity = saved;
                }
                else a.Draw(ds, dev);
            }
        }
        var bytes = acc.GetPixelBytes(); acc.Dispose();
        return Imaging.Opaque(new Bgra(w, h, bytes));
    }

    /// <summary>Reference: PDFium render of the saved file (page 0) at the same scale, with annotations.</summary>
    public static Bgra Reference(byte[] saved, double s, int flags = Pdfium.FPDF_ANNOT)
    {
        using var doc = new PdfDoc(saved); var page = Pdfium.FPDF_LoadPage(doc.Handle, 0);
        var img = PdfRender.Page(page, s, flags); Pdfium.FPDF_ClosePage(page); return img;
    }
}
