using System.Runtime.InteropServices;
using System.Reflection;

namespace Quire.S3.Engine;

/// <summary>Minimal P/Invoke surface of pdfium.dll (bblanchon chromium/8125, x64). Names checked against include/fpdfview.h and fpdf_sysfontinfo.h.</summary>
internal static unsafe class Pdfium
{
    const string Lib = "pdfium";

    static Pdfium()
    {
        NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return 0;
            string p = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");
            return NativeLibrary.Load(p);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int version;           // 2..7
        public byte** userFontPaths;
        public void* isolate;
        public uint v8EmbedderSlot;
        public void* platform;
        public int rendererType;      // v4
        public int fontLibraryType;   // v5
        public int brotliEnabled;     // v6
        public int isolatePerDocument;// v7
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public uint fileLen;                                       // unsigned long == 32-bit on Windows: files >= 4 GiB cannot use this API
        public delegate* unmanaged<void*, uint, byte*, uint, int> getBlock;
        public void* param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SysFontInfo
    {
        public int version;
        public delegate* unmanaged<SysFontInfo*, void> release;
        public delegate* unmanaged<SysFontInfo*, void*, void> enumFonts;
        public delegate* unmanaged<SysFontInfo*, int, int, int, int, byte*, int*, void*> mapFont;
        public delegate* unmanaged<SysFontInfo*, byte*, void*> getFont;
        public delegate* unmanaged<SysFontInfo*, void*, uint, byte*, uint, uint> getFontData;
        public delegate* unmanaged<SysFontInfo*, void*, byte*, uint, uint> getFaceName;
        public delegate* unmanaged<SysFontInfo*, void*, int> getFontCharset;
        public delegate* unmanaged<SysFontInfo*, void*, void> deleteFont;
    }

    [DllImport(Lib)] public static extern void FPDF_InitLibraryWithConfig(LibraryConfig* config);
    [DllImport(Lib)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Lib)] public static extern void* FPDF_LoadCustomDocument(FileAccess* access, byte* password);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(void* doc);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(void* doc);
    [DllImport(Lib)] public static extern void* FPDF_LoadPage(void* doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(void* page);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(void* page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(void* page);
    [DllImport(Lib)] public static extern void* FPDFBitmap_CreateEx(int w, int h, int format, void* firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(void* bmp, int left, int top, int w, int h, uint color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(void* bmp);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(void* bmp, void* page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern void FPDF_SetSystemFontInfo(SysFontInfo* info);
    [DllImport(Lib)] public static extern void FPDF_AddInstalledFont(void* mapper, byte* face, int charset);
    [DllImport(Lib)] public static extern void* FPDFText_LoadPage(void* page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(void* tp);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(void* tp);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(void* tp, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetCharBox(void* tp, int index, double* left, double* right, double* bottom, double* top);

    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
}

/// <summary>FPDF_FILEACCESS backed by a Win32 file HANDLE inherited/duplicated by the broker. The engine never sees a path.</summary>
internal static unsafe class HandleFile
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ReadFile(nint h, byte* buf, uint n, uint* read, System.Threading.NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int GetFileSizeEx(nint h, long* size);

    public static long GetSize(nint h) { long s; return GetFileSizeEx(h, &s) != 0 ? s : -1; }

    [UnmanagedCallersOnly]
    static int GetBlock(void* param, uint position, byte* buf, uint size)
    {
        // Positional read through OVERLAPPED.Offset on a synchronous handle: does not touch the shared file pointer,
        // so a handle duplicated into this process is safe even if the broker also uses it.
        System.Threading.NativeOverlapped ov = default;
        ov.OffsetLow = (int)position;
        ov.OffsetHigh = 0;
        uint total = 0;
        while (total < size)
        {
            uint read;
            ov.OffsetLow = (int)(position + total);
            if (ReadFile((nint)param, buf + total, size - total, &read, &ov) == 0 || read == 0) return 0;
            total += read;
        }
        return 1;
    }

    public static Pdfium.FileAccess* Create(nint handle, uint len)
    {
        var fa = (Pdfium.FileAccess*)NativeMemory.Alloc((nuint)sizeof(Pdfium.FileAccess));
        fa->fileLen = len;
        fa->getBlock = &GetBlock;
        fa->param = (void*)handle;
        return fa;
    }
}
