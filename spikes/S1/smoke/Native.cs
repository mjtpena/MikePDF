using System.Runtime.InteropServices;

namespace Smoke;

/// <summary>Minimal P/Invoke surface over pdfium.dll (x64: stdcall == cdecl).</summary>
internal static unsafe class Native
{
    const string Lib = "pdfium";

    [StructLayout(LayoutKind.Sequential)]
    internal struct LibraryConfig            // FPDF_LIBRARY_CONFIG, versions 1..7 (fpdfview.h)
    {
        public int Version;
        public byte** UserFontPaths;
        public void* Isolate;
        public uint V8EmbedderSlot;
        public void* Platform;
        public int RendererType;             // 0 = AGG, 1 = Skia
        public int FontLibraryType;          // 0 = FreeType, 1 = Fontations
        public int BrotliEnabled;
        public int IsolatePerDocument;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdfFileAccess               // FPDF_FILEACCESS ("unsigned long" is 32-bit on Windows)
    {
        public uint FileLen;
        public nint GetBlock;                // int (*)(void* param, unsigned long pos, unsigned char* buf, unsigned long size)
        public nint Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileWrite                // FPDF_FILEWRITE
    {
        public int Version;
        public nint WriteBlock;              // int (*)(FPDF_FILEWRITE*, const void*, unsigned long)
    }

    [StructLayout(LayoutKind.Sequential)] internal struct FsSizeF { public float Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct FsMatrix { public float A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] internal struct FsRectF { public float Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetBlockFn(nint param, uint pos, byte* buf, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteBlockFn(FileWrite* self, byte* data, uint size);

    [DllImport(Lib)] internal static extern void FPDF_InitLibraryWithConfig(LibraryConfig* config);
    [DllImport(Lib)] internal static extern void FPDF_DestroyLibrary();
    [DllImport(Lib)] internal static extern nint FPDF_LoadCustomDocument(PdfFileAccess* access, byte* password);
    [DllImport(Lib)] internal static extern uint FPDF_GetLastError();
    [DllImport(Lib)] internal static extern void FPDF_CloseDocument(nint doc);
    [DllImport(Lib)] internal static extern int FPDF_GetPageCount(nint doc);
    [DllImport(Lib)] internal static extern int FPDF_GetPageSizeByIndexF(nint doc, int index, FsSizeF* size);
    [DllImport(Lib)] internal static extern nint FPDF_LoadPage(nint doc, int index);
    [DllImport(Lib)] internal static extern void FPDF_ClosePage(nint page);
    [DllImport(Lib)] internal static extern nint FPDFBitmap_CreateEx(int w, int h, int format, void* buf, int stride);
    [DllImport(Lib)] internal static extern void FPDFBitmap_FillRect(nint bmp, int l, int t, int w, int h, uint color);
    [DllImport(Lib)] internal static extern void FPDFBitmap_Destroy(nint bmp);
    [DllImport(Lib)] internal static extern void FPDF_RenderPageBitmap(nint bmp, nint page, int x, int y, int w, int h, int rotate, int flags);
    [DllImport(Lib)] internal static extern void FPDF_RenderPageBitmapWithMatrix(nint bmp, nint page, FsMatrix* m, FsRectF* clip, int flags);
    [DllImport(Lib)] internal static extern nint FPDFText_LoadPage(nint page);
    [DllImport(Lib)] internal static extern void FPDFText_ClosePage(nint tp);
    [DllImport(Lib)] internal static extern int FPDFText_CountChars(nint tp);
    [DllImport(Lib)] internal static extern uint FPDFText_GetUnicode(nint tp, int index);
    [DllImport(Lib)] internal static extern int FPDFText_GetCharBox(nint tp, int index, double* l, double* r, double* b, double* t);
    [DllImport(Lib)] internal static extern int FPDFText_GetText(nint tp, int start, int count, ushort* result);
    [DllImport(Lib)] internal static extern nint FPDFBookmark_GetFirstChild(nint doc, nint parent);
    [DllImport(Lib)] internal static extern nint FPDFBookmark_GetNextSibling(nint doc, nint bm);
    [DllImport(Lib)] internal static extern uint FPDFBookmark_GetTitle(nint bm, ushort* buf, uint len);
    [DllImport(Lib)] internal static extern uint FPDF_GetMetaText(nint doc, byte* tag, ushort* buf, uint len);
    [DllImport(Lib)] internal static extern int FPDF_GetSignatureCount(nint doc);
    [DllImport(Lib)] internal static extern nint FPDF_GetSignatureObject(nint doc, int index);
    [DllImport(Lib)] internal static extern uint FPDFSignatureObj_GetSubFilter(nint sig, byte* buf, uint len);
    [DllImport(Lib)] internal static extern int FPDF_SaveAsCopy(nint doc, FileWrite* writer, uint flags);
}
