using System.Runtime.InteropServices;

namespace S7.Core.Engine;

// P/Invoke surface for the subset of PDFium used by S7. Every signature below was checked against the
// pinned headers (spikes/_shared/pdfium/pdfium-win-x64/include, PDFium 158.0.8125.0).
// Type mapping on Windows x64: FPDF_BOOL=int, unsigned long=uint, FPDF_WCHAR=ushort, handles=IntPtr.
internal static unsafe class Pdfium
{
    private const string Lib = "pdfium";

    static Pdfium()
    {
        NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var p in CandidatePaths())
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var env = Environment.GetEnvironmentVariable("S7_PDFIUM");
        if (!string.IsNullOrEmpty(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "pdfium.dll");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(dir.FullName, "spikes", "_shared", "pdfium", "pdfium-win-x64", "bin", "pdfium.dll");
            if (File.Exists(c)) yield return c;
            c = Path.Combine(dir.FullName, "_shared", "pdfium", "pdfium-win-x64", "bin", "pdfium.dll");
            if (File.Exists(c)) yield return c;
            dir = dir.Parent;
        }
    }

    // ---- fpdfview.h
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Lib, EntryPoint = "FPDF_LoadMemDocument")] public static extern IntPtr FPDF_LoadMemDocument(byte* data, int size, byte* password);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, FsRectF* rect);
    [DllImport(Lib)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);
    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bmp, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] public static extern void* FPDFBitmap_GetBuffer(IntPtr bmp);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(IntPtr bmp);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bmp);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bmp, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---- fpdf_edit.h / fpdf_transformpage.h (experimental)
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern int FPDFPage_GetMediaBox(IntPtr page, float* l, float* b, float* r, float* t);
    [DllImport(Lib)] public static extern int FPDFPage_GetCropBox(IntPtr page, float* l, float* b, float* r, float* t);
    [DllImport(Lib)] public static extern int FPDFPageObj_CountMarks(IntPtr obj);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_GetMark(IntPtr obj, uint index);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_GetName(IntPtr mark, ushort* buffer, uint buflen, uint* outBuflen);
    [DllImport(Lib)] public static extern int FPDFPageObjMark_GetParamIntValue(IntPtr mark, byte* key, int* outValue);

    // ---- fpdf_text.h
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern IntPtr FPDFText_GetTextObject(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_IsGenerated(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_IsHyphen(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_HasUnicodeMapError(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern float FPDFText_GetCharAngle(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, double* left, double* right, double* bottom, double* top);
    [DllImport(Lib)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, FsRectF* rect);
    [DllImport(Lib)] public static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTol, double yTol);
    [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);

    // ---- fpdf_structtree.h
    [DllImport(Lib)] public static extern IntPtr FPDF_StructTree_GetForPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_StructTree_Close(IntPtr tree);
    [DllImport(Lib)] public static extern int FPDF_StructTree_CountChildren(IntPtr tree);
    [DllImport(Lib)] public static extern IntPtr FPDF_StructTree_GetChildAtIndex(IntPtr tree, int index);
    [DllImport(Lib)] public static extern uint FPDF_StructElement_GetType(IntPtr el, void* buffer, uint buflen);
    [DllImport(Lib)] public static extern uint FPDF_StructElement_GetAltText(IntPtr el, void* buffer, uint buflen);
    [DllImport(Lib)] public static extern uint FPDF_StructElement_GetActualText(IntPtr el, void* buffer, uint buflen);
    [DllImport(Lib)] public static extern int FPDF_StructElement_CountChildren(IntPtr el);
    [DllImport(Lib)] public static extern IntPtr FPDF_StructElement_GetChildAtIndex(IntPtr el, int index);
    [DllImport(Lib)] public static extern int FPDF_StructElement_GetChildMarkedContentID(IntPtr el, int index);
    [DllImport(Lib)] public static extern int FPDF_StructElement_GetMarkedContentID(IntPtr el);
    [DllImport(Lib)] public static extern int FPDF_StructElement_GetMarkedContentIdCount(IntPtr el);
    [DllImport(Lib)] public static extern int FPDF_StructElement_GetMarkedContentIdAtIndex(IntPtr el, int index);

    [StructLayout(LayoutKind.Sequential)]
    public struct FsRectF { public float Left, Top, Right, Bottom; }

}
