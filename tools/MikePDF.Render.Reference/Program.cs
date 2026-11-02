// mikepdf-refrender: the reference side of the visual diff (master prompt 12.3, ADR-0006).
//
// It renders pages with the very pdfium.dll the sandboxed engine links, called directly from this process, outside the sandbox and with the
// same call sequence as MikePDF.Engine.Pdfium (BGRA bitmap over white, FPDF_ANNOT, progressive start/continue). Test tooling only: nothing
// shipped references it, and MikePDF.App never calls PDFium (CONTRIBUTING, security boundaries).
//
//   mikepdf-refrender --pdfium <pdfium.dll> --pdf <file> --out <dir> --jobs 1:1,1:2,5:1.5 [--password <pw>] [--rotation 0..3]
//
// A job is <page 1-based>:<scale>. Standard output, one tab-separated line per event:
//   DOC  <status>  <pageCount>  <detail>
//   PAGE <page>  <jobIndex>  <status>  <width>  <height>  <milliseconds>  <detail>
// and for a rendered page the file <out>/job<jobIndex:D5>.bgra: "MBGR", int32 width, int32 height, then width*height*4 bytes (BGRA, no padding).
// A crash of this process (a hostile file can do that) is the caller's to observe; it is never a MikePDF-owned process.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MikePDF.Render.Reference;

internal static unsafe partial class Program
{
    private const string Library = "pdfium";
    private const int FpdfAnnot = 0x01;
    private const int BitmapBgra = 4;
    private const int RenderToBeContinued = 1;
    private const int RenderDone = 2;
    private const float MinPageSide = 1f;
    private const float MaxPageSide = 14400f;
    private const long MaxDeviceSide = 1L << 24;

    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options is null)
            {
                Console.Error.WriteLine("usage: mikepdf-refrender --pdfium <pdfium.dll> --pdf <file> --out <dir> --jobs 1:1,1:2,5:1.5 [--password <pw>] [--rotation 0..3]");
                return 2;
            }

            return Run(options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Run(Options options)
    {
        var dll = NativeLibrary.Load(options.PdfiumPath);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) => name == Library ? dll : 0);
        var bytes = File.ReadAllBytes(options.PdfPath);
        Directory.CreateDirectory(options.OutDirectory);

        var config = stackalloc byte[128]; // FPDF_LIBRARY_CONFIG, version 2 (the same as the engine: no V8, no Skia); the rest stays zero
        new Span<byte>(config, 128).Clear();
        *(int*)config = 2;
        FPDF_InitLibraryWithConfig(config);

        // The buffer must outlive the document: PDFium reads from it lazily.
        fixed (byte* data = bytes)
        {
            var password = Encoding.UTF8.GetBytes(options.Password + "\0");
            nint document;
            fixed (byte* passwordPointer = password)
            {
                document = bytes.Length == 0 ? 0 : FPDF_LoadMemDocument64(data, (nuint)bytes.Length, passwordPointer);
            }

            if (document == 0)
            {
                var error = bytes.Length == 0 ? 3 : (int)FPDF_GetLastError();
                Console.WriteLine($"DOC\t{OpenStatus(error, options.Password.Length > 0)}\t0\tFPDF_GetLastError={error}");
                return 0;
            }

            var pageCount = FPDF_GetPageCount(document);
            Console.WriteLine($"DOC\tOk\t{pageCount}\t");
            Console.Out.Flush();
            for (var job = 0; job < options.Jobs.Count; job++)
            {
                RenderPage(options, document, pageCount, options.Jobs[job].Page, options.Jobs[job].Scale, job);
                Console.Out.Flush();
            }

            FPDF_CloseDocument(document);
        }

        FPDF_DestroyLibrary();
        return 0;
    }

    private static string OpenStatus(int error, bool hadPassword) => error switch
    {
        4 => hadPassword ? "PasswordIncorrect" : "PasswordRequired",
        5 => "UnsupportedEncryption",
        _ => "Damaged",
    };

    private static void RenderPage(Options options, nint document, int pageCount, int page, float scaleValue, int job)
    {
        void Report(string status, int width, int height, long milliseconds, string detail) =>
            Console.WriteLine($"PAGE\t{page}\t{job}\t{status}\t{width}\t{height}\t{milliseconds}\t{detail}");

        if (page < 1 || page > pageCount)
        {
            Report("Skipped", 0, 0, 0, "page outside the document");
            return;
        }

        var clock = Stopwatch.StartNew();
        var loaded = FPDF_LoadPage(document, page - 1);
        if (loaded == 0)
        {
            Report("Damaged", 0, 0, clock.ElapsedMilliseconds, "FPDF_LoadPage failed");
            return;
        }

        try
        {
            // Exactly the engine's arithmetic: float page sides clamped to [1, 14400], multiplied by the float scale in double, rounded half away from zero.
            double width = ClampSide(FPDF_GetPageWidthF(loaded));
            double height = ClampSide(FPDF_GetPageHeightF(loaded));
            if ((options.Rotation & 1) != 0)
            {
                (width, height) = (height, width);
            }

            double scale = scaleValue;
            var deviceWidth = (long)Math.Round(width * scale, MidpointRounding.AwayFromZero);
            var deviceHeight = (long)Math.Round(height * scale, MidpointRounding.AwayFromZero);
            if (deviceWidth < 1 || deviceHeight < 1 || deviceWidth > MaxDeviceSide || deviceHeight > MaxDeviceSide || deviceWidth * deviceHeight * 4 > int.MaxValue)
            {
                Report("InvalidArgument", 0, 0, clock.ElapsedMilliseconds, $"device size {deviceWidth} x {deviceHeight} is outside what the reference renders");
                return;
            }

            var stride = (int)deviceWidth * 4;
            var buffer = new byte[stride * (int)deviceHeight];
            var progress = RenderDone;
            fixed (byte* pixels = buffer)
            {
                var bitmap = FPDFBitmap_CreateEx((int)deviceWidth, (int)deviceHeight, BitmapBgra, pixels, stride);
                if (bitmap == 0)
                {
                    Report("InvalidArgument", 0, 0, clock.ElapsedMilliseconds, "FPDFBitmap_CreateEx failed");
                    return;
                }

                FPDFBitmap_FillRect(bitmap, 0, 0, (int)deviceWidth, (int)deviceHeight, 0xFFFFFFFFu);

                // IFSDK_PAUSE { int version; int (*NeedToPauseNow)(IFSDK_PAUSE*); void* user; }. The engine passes one; a null pause makes PDFium report a partial render and leave the page blank.
                var pause = stackalloc nint[3];
                pause[0] = 1; // version, in the low half of the first word
                pause[1] = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&NeverPause;
                pause[2] = 0;
                progress = FPDF_RenderPageBitmap_Start(bitmap, loaded, 0, 0, (int)deviceWidth, (int)deviceHeight, options.Rotation, FpdfAnnot, pause);
                while (progress == RenderToBeContinued)
                {
                    progress = FPDF_RenderPage_Continue(loaded, pause);
                }

                FPDF_RenderPage_Close(loaded);
                FPDFBitmap_Destroy(bitmap);
            }

            WritePixels(options, job, (int)deviceWidth, (int)deviceHeight, buffer);
            Report(progress == RenderDone ? "Ok" : "PartiallyRendered", (int)deviceWidth, (int)deviceHeight, clock.ElapsedMilliseconds, progress == RenderDone ? "" : $"FPDF render status {progress}");
        }
        finally
        {
            FPDF_ClosePage(loaded);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int NeverPause(void* self) => 0;

    private static void WritePixels(Options options, int job, int width, int height, byte[] buffer)
    {
        var path = Path.Combine(options.OutDirectory, FormattableString.Invariant($"job{job:D5}.bgra"));
        using var file = File.Create(path);
        file.Write("MBGR"u8);
        file.Write(BitConverter.GetBytes(width));
        file.Write(BitConverter.GetBytes(height));
        file.Write(buffer);
    }

    private static float ClampSide(float value) => !float.IsFinite(value) || value < MinPageSide ? MinPageSide : Math.Min(value, MaxPageSide);

    private readonly record struct Job(int Page, float Scale);

    private sealed record Options(string PdfiumPath, string PdfPath, string OutDirectory, string Password, int Rotation, IReadOnlyList<Job> Jobs)
    {
        public static Options? Parse(string[] args)
        {
            string? pdfium = null, pdf = null, output = null, jobs = null;
            var password = "";
            var rotation = 0;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + args[i]);
                switch (args[i])
                {
                    case "--pdfium": pdfium = Next(); break;
                    case "--pdf": pdf = Next(); break;
                    case "--out": output = Next(); break;
                    case "--jobs": jobs = Next(); break;
                    case "--password": password = Next(); break;
                    case "--rotation": rotation = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("unknown argument " + args[i]);
                }
            }

            if (pdfium is null || pdf is null || output is null || jobs is null || rotation is < 0 or > 3)
            {
                return null;
            }

            return new Options(
                Path.GetFullPath(pdfium),
                Path.GetFullPath(pdf),
                Path.GetFullPath(output),
                password,
                rotation,
                [.. jobs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseJob)]);
        }

        private static Job ParseJob(string text)
        {
            var parts = text.Split(':');
            return parts.Length == 2
                ? new Job(int.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture))
                : throw new FormatException("a job is <page>:<scale>, got " + text);
        }
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibraryWithConfig(void* config);

    [LibraryImport(Library)]
    private static partial void FPDF_DestroyLibrary();

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadMemDocument64(void* data, nuint size, byte* password);

    [LibraryImport(Library)]
    private static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    private static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    private static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    private static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    private static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    private static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    private static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Library)]
    private static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Library)]
    private static partial void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    private static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    private static partial int FPDF_RenderPageBitmap_Start(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, void* pause);

    [LibraryImport(Library)]
    private static partial int FPDF_RenderPage_Continue(nint page, void* pause);

    [LibraryImport(Library)]
    private static partial void FPDF_RenderPage_Close(nint page);
}
