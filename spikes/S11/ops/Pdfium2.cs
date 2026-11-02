// S11: PDFium calls beyond the S4 surface. Every signature checked against spikes/_shared/pdfium/pdfium-win-x64/include/*.h (158.0.8125.0).
// `unsigned long` is 32-bit on Windows, so it is uint here.
using System.Runtime.InteropServices;
using System.Text;

namespace S11;

internal static unsafe class P2
{
    private const string Lib = "pdfium";

    // fpdf_ppo.h
    [DllImport(Lib)] public static extern int FPDF_ImportPagesByIndex(IntPtr dest, IntPtr src, int* indices, uint length, int index);
    [DllImport(Lib)] public static extern int FPDF_ImportPages(IntPtr dest, IntPtr src, [MarshalAs(UnmanagedType.LPStr)] string? range, int index);
    // fpdf_edit.h
    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern void FPDFPage_Delete(IntPtr doc, int index);
    [DllImport(Lib)] public static extern int FPDF_MovePages(IntPtr doc, int* indices, uint len, int dest);
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr doc, int index, double w, double h);
    // fpdf_transformpage.h
    [DllImport(Lib)] public static extern int FPDFPage_GetMediaBox(IntPtr page, float* l, float* b, float* r, float* t);
    [DllImport(Lib)] public static extern int FPDFPage_GetCropBox(IntPtr page, float* l, float* b, float* r, float* t);
    // fpdf_doc.h
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bm);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bm);
    [DllImport(Lib)] public static extern uint FPDFBookmark_GetTitle(IntPtr bm, byte* buf, uint len);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bm);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bm);
    [DllImport(Lib)] public static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Lib)] public static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [DllImport(Lib)] public static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
    [DllImport(Lib)] public static extern int FPDFLink_Enumerate(IntPtr page, int* startPos, IntPtr* link);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetDest(IntPtr doc, IntPtr link);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport(Lib)] public static extern uint FPDF_GetPageLabel(IntPtr doc, int page, byte* buf, uint len);
    // fpdfview.h
    [DllImport(Lib)] public static extern uint FPDF_CountNamedDests(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_GetNamedDest(IntPtr doc, int index, byte* buf, int* buflen);
    // fpdf_structtree.h
    [DllImport(Lib)] public static extern IntPtr FPDF_StructTree_GetForPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_StructTree_Close(IntPtr tree);
    [DllImport(Lib)] public static extern int FPDF_StructTree_CountChildren(IntPtr tree);
    // fpdf_text.h
    [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);

    public static bool Move(IntPtr doc, int[] idx, int dest)
    {
        fixed (int* p = idx) return FPDF_MovePages(doc, p, (uint)idx.Length, dest) != 0;
    }

    public static bool ImportByIndex(IntPtr dest, IntPtr src, int[] idx, int at)
    {
        fixed (int* p = idx) return FPDF_ImportPagesByIndex(dest, src, p, (uint)idx.Length, at) != 0;
    }

    public static string Utf16(byte[] buf, int len) => len < 2 ? "" : Encoding.Unicode.GetString(buf, 0, len - 2);

    public static string BookmarkTitle(IntPtr bm)
    {
        var n = FPDFBookmark_GetTitle(bm, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) FPDFBookmark_GetTitle(bm, b, n);
        return Utf16(buf, (int)n);
    }

    public static string PageLabel(IntPtr doc, int page)
    {
        var n = FPDF_GetPageLabel(doc, page, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) FPDF_GetPageLabel(doc, page, b, n);
        return Utf16(buf, (int)n);
    }

    public static string PageText(IntPtr textPage, int maxChars = 200)
    {
        var buf = new ushort[maxChars + 1];
        fixed (ushort* b = buf)
        {
            var n = FPDFText_GetText(textPage, 0, maxChars, b);
            return n <= 1 ? "" : new string((char*)b, 0, n - 1);
        }
    }
}
