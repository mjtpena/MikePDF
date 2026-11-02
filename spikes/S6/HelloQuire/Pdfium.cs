using System.Runtime.InteropServices;
using System.Text;

namespace HelloQuire;

/// <summary>Minimal PDFium P/Invoke (bblanchon chromium/8125) used to render a real page into a BGRA buffer.</summary>
internal static unsafe class Pdfium
{
    private const string Dll = "pdfium.dll";
    [DllImport(Dll)] static extern void FPDF_InitLibrary();
    [DllImport(Dll)] static extern nint FPDF_LoadMemDocument(byte* data, int size, byte* password);
    [DllImport(Dll)] static extern nint FPDF_LoadPage(nint doc, int index);
    [DllImport(Dll)] static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(Dll)] static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(Dll)] static extern nint FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport(Dll)] static extern void FPDFBitmap_FillRect(nint bmp, int l, int t, int w, int h, uint color);
    [DllImport(Dll)] static extern void* FPDFBitmap_GetBuffer(nint bmp);
    [DllImport(Dll)] static extern int FPDFBitmap_GetStride(nint bmp);
    [DllImport(Dll)] static extern void FPDFBitmap_Destroy(nint bmp);
    [DllImport(Dll)] static extern void FPDF_RenderPageBitmap(nint bmp, nint page, int sx, int sy, int sw, int sh, int rotate, int flags);
    [DllImport(Dll)] static extern void FPDF_ClosePage(nint page);
    [DllImport(Dll)] static extern void FPDF_CloseDocument(nint doc);
    [DllImport(Dll)] static extern uint FPDF_GetLastError();

    private static bool _init;

    public static byte[] MakeSamplePdf()
    {
        var sb = new StringBuilder();
        var offs = new List<int>();
        void Obj(string s) { offs.Add(sb.Length); sb.Append(offs.Count).Append(" 0 obj\n").Append(s).Append("\nendobj\n"); }
        sb.Append("%PDF-1.7\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 396] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>");
        string c = "0.9 0.95 1 rg 20 20 572 356 re f 0.1 0.3 0.7 RG 3 w 20 20 572 356 re S\n" +
                   "BT /F2 34 Tf 0 0 0 rg 50 320 Td (Quire spike S6 OCR test) Tj ET\n" +
                   "BT /F1 22 Tf 50 270 Td (The quick brown fox jumps over the lazy dog.) Tj ET\n" +
                   "BT /F1 22 Tf 50 235 Td (Windows App SDK 2.5.1 with Win2D 1.4.0) Tj ET\n" +
                   "BT /F1 22 Tf 50 200 Td (PDFium chromium 8125 - page 1 of 1 - 0123456789) Tj ET\n" +
                   "0.8 0.1 0.1 rg 50 100 120 50 re f 0.1 0.6 0.2 rg 200 100 120 50 re f\n";
        Obj($"<< /Length {c.Length} >>\nstream\n{c}\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Renders page 0 of the sample PDF at the given scale (pixels per point) to BGRA premultiplied-opaque bytes.</summary>
    public static (byte[] Bgra, int Width, int Height) RenderSample(double scale)
    {
        if (!_init) { FPDF_InitLibrary(); _init = true; }
        var pdf = MakeSamplePdf();
        fixed (byte* p = pdf)
        {
            nint doc = FPDF_LoadMemDocument(p, pdf.Length, null);
            if (doc == 0) throw new InvalidOperationException("FPDF_LoadMemDocument failed, FPDF_GetLastError=" + FPDF_GetLastError());
            nint page = FPDF_LoadPage(doc, 0);
            int w = (int)Math.Ceiling(FPDF_GetPageWidthF(page) * scale), h = (int)Math.Ceiling(FPDF_GetPageHeightF(page) * scale);
            nint bmp = FPDFBitmap_Create(w, h, 1);
            FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, 0x01 /*FPDF_ANNOT*/);
            int stride = FPDFBitmap_GetStride(bmp);
            var res = new byte[w * h * 4];
            byte* src = (byte*)FPDFBitmap_GetBuffer(bmp);
            for (int y = 0; y < h; y++) new ReadOnlySpan<byte>(src + (long)y * stride, w * 4).CopyTo(res.AsSpan(y * w * 4, w * 4));
            FPDFBitmap_Destroy(bmp); FPDF_ClosePage(page); FPDF_CloseDocument(doc);
            return (res, w, h);
        }
    }
}
