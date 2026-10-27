// S4 spike: minimal PDFium P/Invoke surface. Every signature below was checked against
// spikes/_shared/pdfium/pdfium-win-x64/include/*.h (PDFium 158.0.8125.0).
// Note: C `unsigned long` is 32-bit on Windows (LLP64), so it maps to uint. size_t maps to nuint.
using System.Runtime.InteropServices;

namespace S4;

[StructLayout(LayoutKind.Sequential)] public struct FS_RECTF { public float left, top, right, bottom; }
[StructLayout(LayoutKind.Sequential)] public struct FS_POINTF { public float x, y; }
[StructLayout(LayoutKind.Sequential)] public struct FS_QUADPOINTSF { public float x1, y1, x2, y2, x3, y3, x4, y4; }

public static unsafe class P
{
    const string Lib = "pdfium";

    static P()
    {
        NativeLibrary.SetDllImportResolver(typeof(P).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            return NativeLibrary.Load(FindDll());
        });
    }

    public static string FindDll()
    {
        var env = Environment.GetEnvironmentVariable("PDFIUM_DLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "spikes", "_shared", "pdfium", "pdfium-win-x64", "bin", "pdfium.dll");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("pdfium.dll not found; set PDFIUM_DLL");
    }

    // ---- fpdfview.h
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadCustomDocument(IntPtr fileAccess, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int sx, int sy, int w, int h, int rotate, int flags);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bmp);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bmp);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(IntPtr bmp);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bmp, int left, int top, int w, int h, uint color);
    public const int FPDF_ANNOT = 0x01;

    // ---- fpdf_save.h
    public const uint FPDF_INCREMENTAL = 1, FPDF_NO_INCREMENTAL = 2;
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr doc, IntPtr fileWrite, uint flags);

    // ---- fpdf_annot.h (all Experimental)
    public const int ST_TEXT = 1, ST_LINK = 2, ST_FREETEXT = 3, ST_LINE = 4, ST_SQUARE = 5, ST_CIRCLE = 6, ST_POLYGON = 7,
        ST_POLYLINE = 8, ST_HIGHLIGHT = 9, ST_UNDERLINE = 10, ST_SQUIGGLY = 11, ST_STRIKEOUT = 12, ST_STAMP = 13,
        ST_CARET = 14, ST_INK = 15, ST_POPUP = 16, ST_FILEATTACHMENT = 17, ST_WIDGET = 20;
    [DllImport(Lib)] public static extern int FPDFAnnot_IsSupportedSubtype(int subtype);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* pts, nuint count);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetObjectCount(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FS_QUADPOINTSF* q);
    [DllImport(Lib)] public static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint idx, FS_QUADPOINTSF* q);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* r);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetRect(IntPtr annot, FS_RECTF* r);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetVertices(IntPtr annot, FS_POINTF* buf, uint len);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint idx, FS_POINTF* buf, uint len);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetLine(IntPtr annot, FS_POINTF* start, FS_POINTF* end);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float h, float v, float w);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetBorder(IntPtr annot, float* h, float* v, float* w);
    [DllImport(Lib)] public static extern int FPDFAnnot_HasKey(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetValueType(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, byte* buf, uint len);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetAP(IntPtr annot, int mode, [MarshalAs(UnmanagedType.LPWStr)] string? value);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetAP(IntPtr annot, int mode, byte* buf, uint len);
    [DllImport(Lib)] public static extern IntPtr FPDFAnnot_GetLinkedAnnot(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetFlags(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    // form-field accessors on annot (take FPDF_FORMHANDLE)
    [DllImport(Lib)] public static extern int FPDFAnnot_GetFormFieldFlags(IntPtr form, IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetFormFieldName(IntPtr form, IntPtr annot, byte* buf, uint len);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetFormFieldType(IntPtr form, IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetFormFieldValue(IntPtr form, IntPtr annot, byte* buf, uint len);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetOptionCount(IntPtr form, IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_IsOptionSelected(IntPtr form, IntPtr annot, int idx);
    [DllImport(Lib)] public static extern int FPDFAnnot_IsChecked(IntPtr form, IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetFormFieldExportValue(IntPtr form, IntPtr annot, byte* buf, uint len);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetFormControlIndex(IntPtr form, IntPtr annot);

    // ---- fpdf_formfill.h
    [DllImport(Lib)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr formInfo);
    [DllImport(Lib)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifier, double x, double y);
    [DllImport(Lib)] public static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifier, double x, double y);
    [DllImport(Lib)] public static extern int FORM_OnChar(IntPtr form, IntPtr page, int ch, int modifier);
    [DllImport(Lib)] public static extern int FORM_OnKeyDown(IntPtr form, IntPtr page, int keyCode, int modifier);
    [DllImport(Lib)] public static extern int FORM_SelectAllText(IntPtr form, IntPtr page);
    [DllImport(Lib)] public static extern void FORM_ReplaceSelection(IntPtr form, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern int FORM_ForceToKillFocus(IntPtr form);
    [DllImport(Lib)] public static extern int FORM_SetFocusedAnnot(IntPtr form, IntPtr annot);
    [DllImport(Lib)] public static extern int FORM_SetIndexSelected(IntPtr form, IntPtr page, int index, int selected);
    [DllImport(Lib)] public static extern int FORM_IsIndexSelected(IntPtr form, IntPtr page, int index);
    [DllImport(Lib)] public static extern uint FORM_GetFocusedText(IntPtr form, IntPtr page, byte* buf, uint len);
    [DllImport(Lib)] public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int sx, int sy, int w, int h, int rotate, int flags);
    public const int FT_PUSH = 1, FT_CHECK = 2, FT_RADIO = 3, FT_COMBO = 4, FT_LIST = 5, FT_TEXT = 6, FT_SIG = 7;

    // ---- fpdf_edit.h
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [DllImport(Lib)] public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [DllImport(Lib)] public static extern int FPDFPath_Close(IntPtr path);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillmode, int stroke);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float w);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineCap(IntPtr obj, int cap);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineJoin(IntPtr obj, int join);
    [DllImport(Lib)] public static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr obj);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr doc, [MarshalAs(UnmanagedType.LPStr)] string font, float size);
    [DllImport(Lib)] public static extern int FPDFText_SetText(IntPtr textObj, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObj, IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetMatrix(IntPtr imageObj, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern int FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(IntPtr page);

    // ---- fpdf_text.h
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr tp);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr tp);
    [DllImport(Lib)] public static extern int FPDFText_GetCharBox(IntPtr tp, int index, double* left, double* right, double* bottom, double* top);
    [DllImport(Lib)] public static extern IntPtr FPDFText_FindStart(IntPtr tp, [MarshalAs(UnmanagedType.LPWStr)] string what, uint flags, int startIndex);
    [DllImport(Lib)] public static extern int FPDFText_FindNext(IntPtr sch);
    [DllImport(Lib)] public static extern int FPDFText_GetSchResultIndex(IntPtr sch);
    [DllImport(Lib)] public static extern int FPDFText_GetSchCount(IntPtr sch);
    [DllImport(Lib)] public static extern void FPDFText_FindClose(IntPtr sch);
    [DllImport(Lib)] public static extern int FPDFText_CountRects(IntPtr tp, int start, int count);
    [DllImport(Lib)] public static extern int FPDFText_GetRect(IntPtr tp, int rectIndex, double* left, double* top, double* right, double* bottom);
    [DllImport(Lib)] public static extern int FPDFText_GetBoundedText(IntPtr tp, double left, double top, double right, double bottom, ushort* buf, int buflen);

    // ---- fpdf_signature.h (Experimental, read-only)
    [DllImport(Lib)] public static extern int FPDF_GetSignatureCount(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_GetSignatureObject(IntPtr doc, int index);
    [DllImport(Lib)] public static extern uint FPDFSignatureObj_GetByteRange(IntPtr sig, int* buf, uint len);
    [DllImport(Lib)] public static extern uint FPDFSignatureObj_GetContents(IntPtr sig, byte* buf, uint len);
}
