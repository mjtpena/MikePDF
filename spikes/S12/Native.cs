using System.Runtime.InteropServices;
using System.Text;

namespace S12;

/// <summary>GDI, winspool and prntvpt P/Invoke for the print prototype. Throwaway. All blittable except the strings (CharSet.Unicode).</summary>
internal static unsafe class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFO { public int cbSize; public string lpszDocName; public string? lpszOutput; public string? lpszDatatype; public int fwType; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateDCW(string driver, string device, string? port, nint devmode);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int StartDocW(nint hdc, ref DOCINFO di);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int StartPage(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int EndPage(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int EndDoc(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int AbortDoc(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern nint ResetDCW(nint hdc, nint devmode);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(nint hdc, int index);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern int StretchDIBits(nint hdc, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, void* bits, BITMAPINFOHEADER* bmi, uint usage, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool OpenPrinterW(string name, out nint h, nint defaults);
    [DllImport("winspool.drv", SetLastError = true)] public static extern bool ClosePrinter(nint h);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int DocumentPropertiesW(nint hwnd, nint hPrinter, string device, byte* devmodeOut, byte* devmodeIn, int mode);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int DeviceCapabilitiesW(string device, string port, short cap, byte* output, byte* devmode);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool EnumPrintersW(int flags, string? name, int level, byte* buf, int cb, out int needed, out int returned);

    [DllImport("prntvpt.dll")] public static extern int PTOpenProvider([MarshalAs(UnmanagedType.LPWStr)] string printer, int version, out nint provider);
    [DllImport("prntvpt.dll")] public static extern int PTCloseProvider(nint provider);
    [DllImport("prntvpt.dll")] public static extern int PTGetPrintCapabilities(nint provider, nint ticketStream, nint capsStream, out nint error);
    [DllImport("prntvpt.dll")] public static extern int PTConvertDevModeToPrintTicket(nint provider, int cbDevmode, byte* devmode, int scope, nint ticketStream);
    [DllImport("ole32.dll")] public static extern int CreateStreamOnHGlobal(nint hglobal, bool deleteOnRelease, out nint stream);
    [DllImport("ole32.dll")] public static extern int GetHGlobalFromStream(nint stream, out nint hglobal);
    [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll")] public static extern nuint GlobalSize(nint h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

    // PDFium vector path (FPDF_RenderPage takes an HDC; Windows only).
    [DllImport("pdfium.dll")] public static extern void FPDF_RenderPage(nint hdc, nint page, int sx, int sy, int sw, int sh, int rotate, int flags);

    public const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90, PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113, HORZSIZE = 4, VERTSIZE = 6;
    public const int DM_OUT_BUFFER = 2, DM_IN_BUFFER = 8, DM_PROMPT = 4;
    public const int DM_ORIENTATION = 0x1, DM_PAPERSIZE = 0x2, DM_COPIES = 0x100, DM_COLOR = 0x800, DM_DUPLEX = 0x1000, DM_YRESOLUTION = 0x2000, DM_COLLATE = 0x8000, DM_PRINTQUALITY = 0x400;
    public const short DC_FIELDS = 1, DC_PAPERS = 2, DC_COPIES = 18, DC_DUPLEX = 7, DC_ENUMRESOLUTIONS = 13, DC_COLLATE = 22, DC_PAPERNAMES = 16, DC_ORIENTATION = 17, DC_COLORDEVICE = 32, DC_PRINTRATE = 25, DC_MAXEXTENT = 5, DC_MINEXTENT = 4;

    // DEVMODEW offsets (x64, spec version 0x0401)
    public const int O_Size = 68, O_DriverExtra = 70, O_Fields = 72, O_Orientation = 76, O_PaperSize = 78, O_Copies = 86, O_PrintQuality = 90, O_Color = 92, O_Duplex = 94, O_YRes = 96, O_Collate = 100;

    public static string PrinterName(string name) => name;

    public static string ReadStream(nint stream)
    {
        GetHGlobalFromStream(stream, out var h); var p = GlobalLock(h); var n = (int)GlobalSize(h);
        // The stream may be larger than its content (HGLOBAL granularity); the XML is NUL terminated.
        var span = new ReadOnlySpan<byte>((void*)p, n); var z = span.IndexOf((byte)0); if (z >= 0) span = span[..z];
        var s = Encoding.UTF8.GetString(span); GlobalUnlock(h); return s;
    }
}
