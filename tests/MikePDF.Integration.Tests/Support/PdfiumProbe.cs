using System.Runtime.InteropServices;

namespace MikePDF.Integration.Tests.Support;

/// <summary>One annotation as a fresh in-process PDFium reads it.</summary>
internal sealed record PdfiumAnnotation(int Subtype, float Left, float Bottom, float Right, float Top, bool HasAppearance);

/// <summary>
/// A fresh PDFium in this process, for reading what a saved file holds (annotation subtypes, rectangles, whether an appearance is there). The pixels in
/// these tests come from the real sandboxed engine; this reads properties the engine has no message for yet. Signatures are from fpdf_annot.h of the
/// pinned build (chromium/8125). In CI a missing DLL is a failure, not a skip.
/// </summary>
internal static partial class PdfiumProbe
{
    public const int Text = 1;
    public const int Link = 2;
    public const int FreeText = 3;
    public const int Line = 4;
    public const int Square = 5;
    public const int Circle = 6;
    public const int Polygon = 7;
    public const int PolyLine = 8;
    public const int Highlight = 9;
    public const int Underline = 10;
    public const int Squiggly = 11;
    public const int StrikeOut = 12;
    public const int Stamp = 13;
    public const int Ink = 15;
    public const int Popup = 16;

    private const string Library = "pdfium";
    private static readonly object Gate = new();
    private static readonly string? DllPath = Locate();
    private static bool _initialised;

    public static string? UnavailableReason => DllPath is null
        ? "pdfium.dll not found at build/out/pdfium/<arch>/bin (run build/fetch-pdfium.ps1)"
        : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct FsRectF
    {
        public float Left;
        public float Bottom;
        public float Right;
        public float Top;
    }

    public static IReadOnlyList<PdfiumAnnotation>? Annotations(byte[] file, int pageIndex)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var handle = GCHandle.Alloc(file, GCHandleType.Pinned);
            try
            {
                var document = FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(), (nuint)file.Length, nint.Zero);
                if (document == nint.Zero)
                {
                    return null;
                }

                try
                {
                    var page = FPDF_LoadPage(document, pageIndex);
                    if (page == nint.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        var result = new List<PdfiumAnnotation>();
                        var count = FPDFPage_GetAnnotCount(page);
                        for (var i = 0; i < count; i++)
                        {
                            var annot = FPDFPage_GetAnnot(page, i);
                            if (annot == nint.Zero)
                            {
                                continue;
                            }

                            try
                            {
                                var subtype = FPDFAnnot_GetSubtype(annot);
                                var rect = default(FsRectF);
                                _ = FPDFAnnot_GetRect(annot, ref rect);
                                result.Add(new PdfiumAnnotation(subtype, rect.Left, rect.Bottom, rect.Right, rect.Top, FPDFAnnot_HasKey(annot, "AP") != 0));
                            }
                            finally
                            {
                                FPDFPage_CloseAnnot(annot);
                            }
                        }

                        return result;
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                }
                finally
                {
                    FPDF_CloseDocument(document);
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
            {
                return;
            }

            if (DllPath is null)
            {
                throw new InvalidOperationException(UnavailableReason);
            }

            NativeLibrary.SetDllImportResolver(typeof(PdfiumProbe).Assembly, (name, _, _) => name == Library ? NativeLibrary.Load(DllPath) : nint.Zero);
            FPDF_InitLibrary();
            _initialised = true;
        }
    }

    private static string? Locate()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MikePDF.slnx")))
            {
                var candidate = Path.Combine(dir.FullName, "build", "out", "pdfium", arch, "bin", "pdfium.dll");
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
    private static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    private static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    private static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Library)]
    private static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Library)]
    private static partial void FPDFPage_CloseAnnot(nint annot);

    [LibraryImport(Library)]
    private static partial int FPDFAnnot_GetSubtype(nint annot);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FPDFAnnot_GetRect(nint annot, ref FsRectF rect);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FPDFAnnot_HasKey(nint annot, string key);
}

/// <summary>A minimal PNG writer, so that a run can leave pictures to look at (set MIKEPDF_MARKUP_DUMP to a folder).</summary>
internal static class PngDump
{
    public static string? Folder => Environment.GetEnvironmentVariable("MIKEPDF_MARKUP_DUMP");

    public static void Write(string name, byte[] bgra, int width, int height)
    {
        if (Folder is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                raw.WriteByte(bgra[i + 2]);
                raw.WriteByte(bgra[i + 1]);
                raw.WriteByte(bgra[i]);
            }
        }

        using var file = File.Create(Path.Combine(folder, name + ".png"));
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(file, "IHDR", [.. Be(width), .. Be(height), 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        }

        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        stream.Write(Be(data.Length));
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        stream.Write(body);
        stream.Write(Be((int)Crc32(body)));
    }
}
