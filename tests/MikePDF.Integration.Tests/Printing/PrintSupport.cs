using System.Runtime.InteropServices;
using MikePDF.Broker;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;
using MikePDF.Render.Imaging;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>A printer that keeps the sheets as pixels in memory, so a test can look at what the pipeline drew and where, without a printer or a driver.</summary>
internal sealed class BitmapDevice(PaperArea paper) : IPrintDevice
{
    private byte[]? _sheet;

    public PaperArea Paper { get; } = paper;

    public List<byte[]> Sheets { get; } = [];

    public int Draws { get; private set; }

    public List<(int SrcWidth, int SrcHeight, int DestWidth, int DestHeight)> Blits { get; } = [];

    public bool Ended { get; private set; }

    public bool Aborted { get; private set; }

    /// <summary>Called after each band, with the number of bands drawn so far (a test that kills the engine or cancels in the middle of a job).</summary>
    public Action<int>? AfterDraw { get; set; }

    public void StartDocument(string documentName, string? outputPath)
    {
    }

    public void StartPage()
    {
        _sheet = new byte[Paper.WidthPx * Paper.HeightPx * 4];
        Array.Fill(_sheet, (byte)255);
        Sheets.Add(_sheet);
    }

    public void DrawBand(ReadOnlySpan<byte> bgra, int width, int height, int stride, int destX, int destY, int destWidth, int destHeight)
    {
        Blits.Add((width, height, destWidth, destHeight));
        for (var dy = 0; dy < destHeight; dy++)
        {
            var y = destY + dy;
            if (y < 0 || y >= Paper.HeightPx)
            {
                continue;
            }

            var sy = destHeight == height ? dy : (int)((long)dy * height / destHeight);
            for (var dx = 0; dx < destWidth; dx++)
            {
                var x = destX + dx;
                if (x < 0 || x >= Paper.WidthPx)
                {
                    continue;
                }

                var sx = destWidth == width ? dx : (int)((long)dx * width / destWidth);
                bgra.Slice((sy * stride) + (sx * 4), 4).CopyTo(_sheet!.AsSpan(((y * Paper.WidthPx) + x) * 4));
            }
        }

        Draws++;
        AfterDraw?.Invoke(Draws);
    }

    public void EndPage()
    {
    }

    public void EndDocument() => Ended = true;

    public void AbortDocument() => Aborted = true;

    public void RequestAbort()
    {
    }

    public void Dispose()
    {
    }

    public (byte Blue, byte Green, byte Red) Pixel(int sheet, int x, int y)
    {
        var offset = ((y * Paper.WidthPx) + x) * 4;
        var pixels = Sheets[sheet];
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    /// <summary>The sheet as an image, for comparing.</summary>
    public BgraImage Image(int sheet) => new(Paper.WidthPx, Paper.HeightPx, (byte[])Sheets[sheet].Clone());
}

/// <summary>A printer that draws nothing and counts (for a job whose memory is being watched).</summary>
internal sealed class NullDevice(PaperArea paper) : IPrintDevice
{
    public PaperArea Paper { get; } = paper;

    public int Pages { get; private set; }

    public Action<int>? AfterPage { get; set; }

    public void StartDocument(string documentName, string? outputPath)
    {
    }

    public void StartPage() => Pages++;

    public void DrawBand(ReadOnlySpan<byte> bgra, int width, int height, int stride, int destX, int destY, int destWidth, int destHeight)
    {
    }

    public void EndPage() => AfterPage?.Invoke(Pages);

    public void EndDocument()
    {
    }

    public void AbortDocument()
    {
    }

    public void RequestAbort()
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class FixedDeviceFactory(IPrintDevice device) : IPrintDeviceFactory
{
    public CopyPlan Copies { get; set; } = new(1, false, 1, 1);

    public DeviceOpenResult Open(PrinterJobSettings settings) => new(device, Copies, new DeviceOpenReport(true, true, true, true, false, false, "fixed"));
}

/// <summary>A budget with real sandboxed engines, print sessions on it, and the temp files of a test.</summary>
internal sealed class PrintHarness(SandboxFixture sandbox) : IAsyncDisposable
{
    private readonly List<string> _files = [];

    public EngineBudget Budget { get; } = new(new EngineBudgetOptions { Document = sandbox.Options(), Pool = sandbox.Options(), MaxDocumentEngines = 4, SlotWaitTimeout = TimeSpan.FromSeconds(20) });

    public string Temp(string name, byte[]? contents = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-print-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);
        if (contents is not null)
        {
            File.WriteAllBytes(path, contents);
        }

        _files.Add(path);
        return path;
    }

    public async Task<PrintSession> OpenAsync(byte[] pdf, PrintOptions? options = null, string name = "fixture.pdf")
    {
        var path = Temp(name, pdf);
        var opened = await PrintSession.OpenAsync(Budget, path, null, options ?? new PrintOptions(), printingAllowedByPolicy: true);
        Assert.True(opened.IsOk, $"the print session did not open: {opened.Status} {opened.Detail}");
        return opened.Value!;
    }

    /// <summary>Another engine, outside the print session, with a document open in it (to read the pages that were printed, or to render the reference).</summary>
    public async Task<(EngineSupervisor Supervisor, SupervisedDocument Document)> OpenReaderAsync(string path)
    {
        var supervisor = await sandbox.StartAsync();
        var opened = await supervisor.OpenDocumentAsync(path);
        Assert.True(opened.IsOk, $"{opened.Status} {opened.Detail}");
        return (supervisor, opened.Value!);
    }

    public async ValueTask DisposeAsync()
    {
        await Budget.DisposeAsync();
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The OS temp directory is cleaned eventually.
            }
        }
    }
}

/// <summary>The Print To PDF queue, and the questions about it the tests ask.</summary>
internal static partial class VirtualPrinter
{
    public const string Name = "Microsoft Print to PDF";

    /// <summary>The A4 paper size id of DEVMODE (DMPAPER_A4) and Letter (DMPAPER_LETTER).</summary>
    public const int PaperA4 = 9;
    public const int PaperLetter = 1;

    public static string? UnavailableReason
    {
        get
        {
            try
            {
                return PrinterCatalog.Exists(Name) ? null : $"The printer '{Name}' is not installed on this machine, so the print-to-file tests cannot run (it is an optional Windows feature).";
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return "The Windows print spooler could not be queried: " + e.Message;
            }
        }
    }

    public static void Require() => Assert.SkipWhen(UnavailableReason is not null, UnavailableReason ?? string.Empty);

    /// <summary>The jobs the spooler holds for the queue right now (<c>PRINTER_INFO_2.cJobs</c>). -1 when it cannot be read.</summary>
    public static int JobsInQueue(string printer)
    {
        if (!OpenPrinterW(printer, out var handle, 0))
        {
            return -1;
        }

        try
        {
            GetPrinterW(handle, 2, 0, 0, out var needed);
            if (needed == 0)
            {
                return -1;
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                return GetPrinterW(handle, 2, buffer, needed, out _) ? Marshal.ReadInt32(buffer, 128) : -1; // PRINTER_INFO_2W: 13 pointers, then Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string name, out nint handle, nint defaults);

    [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinterW(nint handle, uint level, nint buffer, uint size, out uint needed);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint handle);
}
