using System.Runtime.InteropServices;
using System.Text;

namespace S9;

/// <summary>PDFium (bblanchon chromium/8125) P/Invoke: just what the spike needs. Throwaway.</summary>
internal static unsafe class Pdfium
{
    const string Dll = "pdfium.dll";
    [DllImport(Dll)] public static extern void FPDF_InitLibrary();
    [DllImport(Dll)] public static extern nint FPDF_LoadMemDocument(byte* data, int size, byte* password);
    [DllImport(Dll)] public static extern nint FPDF_LoadDocument(byte* utf8Path, byte* password);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(nint doc);
    [DllImport(Dll)] public static extern nint FPDF_LoadPage(nint doc, int index);
    [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(Dll)] public static extern nint FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport(Dll)] public static extern void FPDFBitmap_FillRect(nint bmp, int l, int t, int w, int h, uint color);
    [DllImport(Dll)] public static extern void* FPDFBitmap_GetBuffer(nint bmp);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(nint bmp);
    [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(nint bmp);
    [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(nint bmp, nint page, int sx, int sy, int sw, int sh, int rotate, int flags);
    [DllImport(Dll)] public static extern void FPDF_ClosePage(nint page);
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(nint doc);
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();

    // Annotations (Experimental in fpdf_annot.h; R-12)
    [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(nint page);
    [DllImport(Dll)] public static extern nint FPDFPage_GetAnnot(nint page, int index);
    [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(nint annot);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveAnnot(nint page, int index);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(nint annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFlags(nint annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetFlags(nint annot, int flags);

    // Page objects (to build an annotation-only sprite)
    [DllImport(Dll)] public static extern int FPDFPage_CountObjects(nint page);
    [DllImport(Dll)] public static extern nint FPDFPage_GetObject(nint page, int index);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveObject(nint page, nint obj);
    [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(nint obj);

    // Save
    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite { public int Version; public delegate* unmanaged[Cdecl]<FileWrite*, void*, uint, int> WriteBlock; }
    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(nint doc, FileWrite* fw, uint flags);

    // Form fill (to test widgets)
    [StructLayout(LayoutKind.Sequential)]
    public struct FormFillInfo { public int Version; public nint Release; public nint FFI_Invalidate; public nint FFI_OutputSelectedRect; public nint FFI_SetCursor; public nint FFI_SetTimer; public nint FFI_KillTimer; public nint FFI_GetLocalTime; public nint FFI_OnChange; public nint FFI_GetPage; public nint FFI_GetCurrentPage; public nint FFI_GetRotation; public nint FFI_ExecuteNamedAction; public nint FFI_SetTextFieldFocus; public nint FFI_DoURIAction; public nint FFI_DoGoToAction; public nint m_pJsPlatform; public nint pad1; public nint pad2; public nint pad3; public nint pad4; public nint pad5; public nint pad6; public nint pad7; public nint pad8; }
    [DllImport(Dll)] public static extern nint FPDFDOC_InitFormFillEnvironment(nint doc, FormFillInfo* info);
    [DllImport(Dll)] public static extern void FPDFDOC_ExitFormFillEnvironment(nint form);
    [DllImport(Dll)] public static extern void FPDF_FFLDraw(nint form, nint bmp, nint page, int sx, int sy, int sw, int sh, int rotate, int flags);
    [DllImport(Dll)] public static extern void FORM_OnAfterLoadPage(nint page, nint form);
    [DllImport(Dll)] public static extern void FORM_OnBeforeClosePage(nint page, nint form);

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;
    public const int ANNOT_HIDDEN = 2;

    static bool _init;
    public static void Init() { if (!_init) { FPDF_InitLibrary(); _init = true; } }

    static readonly MemoryStream SaveSink = new();
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static int WriteBlock(FileWrite* self, void* data, uint size) { SaveSink.Write(new ReadOnlySpan<byte>(data, (int)size)); return 1; }

    /// <summary>FPDF_SaveAsCopy to memory. flags: 0 = full rewrite, 1 = FPDF_INCREMENTAL, 2 = FPDF_NO_INCREMENTAL.</summary>
    public static byte[] Save(nint doc, uint flags)
    {
        SaveSink.SetLength(0);
        FileWrite fw = default; fw.Version = 1; fw.WriteBlock = &WriteBlock;
        if (FPDF_SaveAsCopy(doc, &fw, flags) == 0) throw new InvalidOperationException("FPDF_SaveAsCopy failed");
        return SaveSink.ToArray();
    }
}

/// <summary>An open PDFium document held from a native copy of the bytes (so the managed array can be dropped).</summary>
internal sealed unsafe class PdfDoc : IDisposable
{
    readonly nint _mem; public readonly nint Handle; public readonly int PageCount; public readonly int ByteLength;
    public PdfDoc(byte[] bytes)
    {
        Pdfium.Init();
        _mem = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, _mem, bytes.Length); ByteLength = bytes.Length;
        Handle = Pdfium.FPDF_LoadMemDocument((byte*)_mem, bytes.Length, null);
        if (Handle == 0) throw new InvalidOperationException("FPDF_LoadMemDocument failed, error " + Pdfium.FPDF_GetLastError());
        PageCount = Pdfium.FPDF_GetPageCount(Handle);
    }
    public static PdfDoc FromFile(string path) => new(File.ReadAllBytes(path));
    public static (nint doc, int pages) OpenPath(string path)
    {
        Pdfium.Init();
        var b = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = b) { var d = Pdfium.FPDF_LoadDocument(p, null); if (d == 0) throw new InvalidOperationException("FPDF_LoadDocument failed " + Pdfium.FPDF_GetLastError()); return (d, Pdfium.FPDF_GetPageCount(d)); }
    }
    public void Dispose() { Pdfium.FPDF_CloseDocument(Handle); Marshal.FreeHGlobal(_mem); }
}

/// <summary>Tightly packed BGRA image, top-down, straight alpha as produced by PDFium (alpha = 255 when rendered onto an opaque fill).</summary>
internal sealed class Bgra
{
    public readonly int W, H; public readonly byte[] Px;
    public Bgra(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }
    public Bgra(int w, int h, byte[] px) { W = w; H = h; Px = px; }
}

internal static unsafe class PdfRender
{
    /// <summary>Render a viewport of a page. scale = pixels per point; (viewX, viewY) = top-left of the viewport in page pixels.</summary>
    public static Bgra Viewport(nint page, double scale, int viewX, int viewY, int w, int h, int flags = Pdfium.FPDF_ANNOT, uint fill = 0xFFFFFFFF)
    {
        var pw = (int)Math.Round(Pdfium.FPDF_GetPageWidthF(page) * scale);
        var ph = (int)Math.Round(Pdfium.FPDF_GetPageHeightF(page) * scale);
        var bmp = Pdfium.FPDFBitmap_Create(w, h, 1);
        Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, fill);
        Pdfium.FPDF_RenderPageBitmap(bmp, page, -viewX, -viewY, pw, ph, 0, flags);
        var stride = Pdfium.FPDFBitmap_GetStride(bmp);
        var img = new Bgra(w, h);
        var src = (byte*)Pdfium.FPDFBitmap_GetBuffer(bmp);
        for (var y = 0; y < h; y++) new ReadOnlySpan<byte>(src + (long)y * stride, w * 4).CopyTo(img.Px.AsSpan(y * w * 4, w * 4));
        Pdfium.FPDFBitmap_Destroy(bmp);
        return img;
    }

    /// <summary>Whole page at a scale.</summary>
    public static Bgra Page(nint page, double scale, int flags = Pdfium.FPDF_ANNOT, uint fill = 0xFFFFFFFF)
    {
        var pw = (int)Math.Round(Pdfium.FPDF_GetPageWidthF(page) * scale);
        var ph = (int)Math.Round(Pdfium.FPDF_GetPageHeightF(page) * scale);
        return Viewport(page, scale, 0, 0, pw, ph, flags, fill);
    }
}
