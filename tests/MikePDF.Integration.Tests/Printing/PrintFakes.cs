using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Core.Reading;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>A band of one colour that says which page and which band it is, so a test can tell what was drawn where.</summary>
internal sealed class FakeBand : IPrintBand
{
    private readonly byte[] _pixels;

    public FakeBand(int width, int height, byte blue, byte green, byte red, bool partial = false)
    {
        Width = width;
        Height = height;
        PartiallyRendered = partial;
        _pixels = new byte[width * height * 4];
        for (var i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = blue;
            _pixels[i + 1] = green;
            _pixels[i + 2] = red;
            _pixels[i + 3] = 255;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public bool PartiallyRendered { get; }

    public ReadOnlySpan<byte> Pixels => _pixels;

    public int Disposed { get; private set; }

    public void Dispose() => Disposed++;
}

/// <summary>An engine that is not one: the pages' sizes, answers with a script, and a count of what is in flight.</summary>
internal sealed class FakePrintSource : IPrintSource
{
    private readonly object _gate = new();
    private int _calls;
    private int _inFlight;

    public FakePrintSource(params SizeD[] pages) => Pages = pages;

    public SizeD[] Pages { get; }

    public PdfPermissions Permissions { get; set; } = PdfPermissions.Unrestricted;

    public int SlotBytes { get; set; } = 16 << 20;

    public TimeSpan RenderDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>Called for every band request with its sequence number (1-based, counting retries); a status other than Ok is returned as the engine's answer.</summary>
    public Func<RenderBandArgs, int, Status>? Script { get; set; }

    public List<RenderBandArgs> Requests { get; } = [];

    public List<FakeBand> Bands { get; } = [];

    public int InFlight => Volatile.Read(ref _inFlight);

    public int MaxInFlight { get; private set; }

    public HashSet<uint> PartialPages { get; } = [];

    public Task<(Status Status, PrintDocumentInfo? Info)> GetInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult((Status.Ok, (PrintDocumentInfo?)new PrintDocumentInfo(Pages.Length, Permissions)));

    public Task<(Status Status, IReadOnlyList<SizeD> Sizes)> GetPageSizesAsync(int first, int count, CancellationToken cancellationToken) =>
        Task.FromResult((Status.Ok, (IReadOnlyList<SizeD>)Pages.Skip(first).Take(count).ToArray()));

    public Task<GetViewerPreferencesReply?> GetViewerPreferencesAsync(CancellationToken cancellationToken) => Task.FromResult<GetViewerPreferencesReply?>(null);

    public async Task<BandResult> RenderBandAsync(RenderBandArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken)
    {
        int call;
        lock (_gate)
        {
            call = ++_calls;
            Requests.Add(args);
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
        }

        try
        {
            await Task.Delay(RenderDelay, cancellationToken).ConfigureAwait(false);
            if (Script?.Invoke(args, call) is { } status && status != Status.Ok)
            {
                return new BandResult(status, null, "scripted");
            }

            // The pixels name the page (blue) and the band's first row (green, in steps of 8): the device log can then say what went where.
            var band = new FakeBand((int)args.PageWidthPx, (int)args.BandHeight, (byte)(args.Page + 1), (byte)(args.BandTop / 8 % 256), 200, PartialPages.Contains(args.Page));
            lock (_gate)
            {
                Bands.Add(band);
            }

            return new BandResult(band.PartiallyRendered ? Status.PartiallyRendered : Status.Ok, band);
        }
        catch (OperationCanceledException)
        {
            return new BandResult(Status.Cancelled, null);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A printer that writes down every call it gets.</summary>
internal sealed class FakeDevice(PaperArea paper) : IPrintDevice
{
    public PaperArea Paper { get; } = paper;

    public List<string> Calls { get; } = [];

    public List<Draw> Draws { get; } = [];

    public bool Aborted { get; private set; }

    public bool Ended { get; private set; }

    public bool AbortRequested { get; private set; }

    public bool Disposed { get; private set; }

    public string? DocumentName { get; private set; }

    public string? OutputPath { get; private set; }

    /// <summary>Called after each band is drawn (for a test that cancels in the middle of a job).</summary>
    public Action<int>? AfterDraw { get; set; }

    public TimeSpan EndDocumentDelay { get; set; }

    /// <summary>The call that throws a <see cref="PrintDeviceException"/>: StartDoc, DrawBand or EndPage.</summary>
    public string? FailAt { get; set; }

    public int Pages { get; private set; }

    public readonly record struct Draw(int Sheet, int X, int Y, int Width, int Height, int SrcWidth, int SrcHeight, byte Blue, byte Green, byte Red);

    public void StartDocument(string documentName, string? outputPath)
    {
        DocumentName = documentName;
        OutputPath = outputPath;
        if (outputPath is not null)
        {
            File.WriteAllBytes(outputPath, []); // what a spooler leaves behind when a job to a file is aborted
        }

        Calls.Add("StartDoc");
        if (FailAt == "StartDoc")
        {
            throw new PrintDeviceException("StartDoc failed", 5);
        }
    }

    public void StartPage()
    {
        Pages++;
        Calls.Add("StartPage");
    }

    public void DrawBand(ReadOnlySpan<byte> bgra, int width, int height, int stride, int destX, int destY, int destWidth, int destHeight)
    {
        if (FailAt == "DrawBand")
        {
            throw new PrintDeviceException("StretchDIBits failed", 87);
        }

        Draws.Add(new Draw(Pages, destX, destY, destWidth, destHeight, width, height, bgra[0], bgra[1], bgra[2]));
        Calls.Add("DrawBand");
        AfterDraw?.Invoke(Draws.Count);
    }

    public void EndPage()
    {
        Calls.Add("EndPage");
        if (FailAt == "EndPage")
        {
            throw new PrintDeviceException("EndPage failed", 1);
        }
    }

    public void EndDocument()
    {
        if (EndDocumentDelay > TimeSpan.Zero)
        {
            Thread.Sleep(EndDocumentDelay);
        }

        Ended = true;
        Calls.Add("EndDoc");
    }

    public void AbortDocument()
    {
        Aborted = true;
        Calls.Add("AbortDoc");
    }

    public void RequestAbort() => AbortRequested = true;

    public void Dispose() => Disposed = true;
}

internal sealed class FakeDeviceFactory(FakeDevice device) : IPrintDeviceFactory
{
    public CopyPlan Copies { get; set; } = new(1, false, 1, 1);

    public DeviceOpenReport Report { get; set; } = new(true, true, true, true, false, false, "fake");

    public PrinterJobSettings? Settings { get; private set; }

    public int Opened { get; private set; }

    public DeviceOpenResult Open(PrinterJobSettings settings)
    {
        Settings = settings;
        Opened++;
        return new DeviceOpenResult(device, Copies, Report);
    }
}
