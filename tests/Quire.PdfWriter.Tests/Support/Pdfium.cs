using System.Runtime.InteropServices;

namespace Quire.PdfWriter.Tests.Support;

/// <summary>
/// Test-only P/Invoke surface of PDFium, used by the R-31 differential tests. Quire.PdfWriter itself never references PDFium.
/// Signatures are from fpdfview.h / fpdf_annot.h of the pinned build (chromium/8125); on Windows <c>unsigned long</c> is 32 bits.
/// </summary>
internal static partial class Pdfium
{
    private const string Library = "pdfium";
    private static readonly object Gate = new();
    private static readonly string? DllPath = Locate();
    private static bool _initialised;

    /// <summary>Why the differential tests cannot run, or null when PDFium is available.</summary>
    // In CI a missing DLL is a failure, not a skip: the differential tests must never disappear silently.
    public static string? UnavailableReason => DllPath is null && Environment.GetEnvironmentVariable("CI") != "true"
        ? $"pdfium.dll not found at build/out/pdfium/{ProcessArch}/bin (run build/fetch-pdfium.ps1); the PDFium differential tests are skipped"
        : null;

    private static string ProcessArch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public sealed record Info(int PageCount, int Page0AnnotCount);

    /// <summary>Loads <paramref name="bytes"/> in PDFium and reports page count and the first page's annotation count; null if PDFium refuses the file.</summary>
    public static Info? Open(byte[] bytes)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                nint doc = FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(), (nuint)bytes.Length, nint.Zero);
                if (doc == nint.Zero)
                    return null;
                try
                {
                    int pages = FPDF_GetPageCount(doc);
                    int annots = 0;
                    if (pages > 0)
                    {
                        nint page = FPDF_LoadPage(doc, 0);
                        if (page != nint.Zero)
                        {
                            annots = FPDFPage_GetAnnotCount(page);
                            FPDF_ClosePage(page);
                        }
                    }

                    return new Info(pages, annots);
                }
                finally
                {
                    FPDF_CloseDocument(doc);
                }
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_initialised)
                return;
            NativeLibrary.SetDllImportResolver(typeof(Pdfium).Assembly, (name, _, _) =>
                name == Library && DllPath is not null ? NativeLibrary.Load(DllPath) : nint.Zero);
            FPDF_InitLibrary();
            _initialised = true;
        }
    }

    private static string? Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Quire.slnx")))
            {
                string candidate = Path.Combine(dir.FullName, "build", "out", "pdfium", ProcessArch, "bin", "pdfium.dll");
                return File.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadMemDocument64(nint dataBuf, nuint size, nint password);

    [LibraryImport(Library)]
    private static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    private static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    private static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    private static partial int FPDFPage_GetAnnotCount(nint page);
}
