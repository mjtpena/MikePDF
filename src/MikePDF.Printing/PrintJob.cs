using System.Buffers;
using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Protocol;

namespace MikePDF.Printing;

/// <summary>
/// The print pipeline (ADR-0011 decision 3, print.md 3.5 and 6). It plans the sheets with <see cref="PrintPlanner"/>, asks the engine for each band (<c>RenderBand</c>) one band ahead of the one
/// being sent, and draws them through an <see cref="IPrintDevice"/> on a thread of its own. One printer device and one <c>StartDoc</c> per job. A cancel aborts the spool job; a crashed
/// engine is waited for and the same band asked again, up to <see cref="PrintJobRequest.MaxEngineCrashes"/>; a band that timed out is asked once more. Nothing here weakens the sandbox:
/// the engine is given band geometry and gets pixels back, and never sees a printer.
/// </summary>
public static class PrintJob
{
    private const int OrientationSample = 512;
    private const int SizeWindow = 256;
    private const int CutOffScanLimit = 100_000;

    /// <summary>
    /// Everything the sheet needs before printing: whether printing is allowed, how many sheets, which pages would be cut off, the printable area, and what the driver does with the
    /// settings. Opens the printer's device context for a moment (nothing is spooled) and closes it again.
    /// </summary>
    public static async Task<PrintPreflight> PreflightAsync(PrintJobRequest request, IPrintSource source, IPrintDeviceFactory devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new Run(request, source, devices, null, cancellationToken);
        try
        {
            var denied = await run.PrepareAsync().ConfigureAwait(false);
            if (denied is not null)
            {
                return new PrintPreflight(run.Eligibility ?? new PrintEligibility(false, denied.ResourceKey, false), 0, [], default, null, denied.EngineStatus, denied.Detail);
            }

            var cutOff = new List<int>();
            if (run.Options.PagesPerSheet == 1 && run.Options.Scaling is PrintScaling.Actual or PrintScaling.Custom)
            {
                foreach (var page in run.Request.Pages.Take(CutOffScanLimit))
                {
                    if (run.Planner!.IsCutOff(await run.SizeOfAsync(page).ConfigureAwait(false)))
                    {
                        cutOff.Add(page);
                    }
                }
            }

            return new PrintPreflight(run.Eligibility!, run.TotalSheets, cutOff, run.Paper, run.Opened!.Report, Status.Ok, null);
        }
        catch (RunFailure failure)
        {
            return new PrintPreflight(run.Eligibility ?? new PrintEligibility(true, null, false), 0, [], default, null, failure.Status, failure.Message);
        }
        catch (OperationCanceledException)
        {
            return new PrintPreflight(run.Eligibility ?? new PrintEligibility(true, null, false), 0, [], default, null, Status.Cancelled, null);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Prints. Never throws for a failure of the printer, the engine or the document: the result says how it ended. Cancelling <paramref name="cancellationToken"/> aborts the spool job.</summary>
    public static async Task<PrintResult> RunAsync(PrintJobRequest request, IPrintSource source, IPrintDeviceFactory devices, IProgress<PrintProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(devices);
        var run = new Run(request, source, devices, progress, cancellationToken);
        try
        {
            if (await run.PrepareAsync().ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            await run.PrintAsync().ConfigureAwait(false);
            return run.Finish(PrintOutcome.Completed, null, null, Status.Ok);
        }
        catch (RunFailure failure)
        {
            await run.AbortAsync().ConfigureAwait(false);
            return run.Finish(PrintOutcome.Failed, PrintStrings.Failed, failure.Message, failure.Status);
        }
        catch (PrintDeviceException e) when (!cancellationToken.IsCancellationRequested)
        {
            await run.AbortAsync().ConfigureAwait(false);
            return run.Finish(PrintOutcome.Failed, PrintStrings.Failed, e.Message, Status.Ok);
        }
        catch (Exception e) when (e is OperationCanceledException or PrintDeviceException && cancellationToken.IsCancellationRequested)
        {
            await run.AbortAsync().ConfigureAwait(false);
            return run.Finish(PrintOutcome.Cancelled, null, null, Status.Cancelled);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A job that cannot go on. <see cref="Status"/> is the engine status behind it, or <c>Ok</c> when it was not the engine.</summary>
    private sealed class RunFailure(Status status, string message) : Exception(message)
    {
        public Status Status { get; } = status;
    }

    private abstract record WorkItem;

    private sealed record StartSheet(int Number, int FirstPage) : WorkItem;

    private sealed record DrawBandItem(PlacedPage Placed, BandSpec Band) : WorkItem;

    private sealed record EndSheet : WorkItem;

    private sealed class Run : IAsyncDisposable
    {
        private readonly IPrintSource _source;
        private readonly IPrintDeviceFactory _devices;
        private readonly IProgress<PrintProgress>? _progress;
        private readonly CancellationToken _cancellation;
        private readonly CancellationTokenSource _render = new();
        private readonly DeviceThread _thread = new();
        private readonly List<Task<BandResult>> _outstanding = [];
        private readonly SortedSet<int> _missing = [];
        private readonly Dictionary<int, SizeD> _sizes = [];
        private CancellationTokenRegistration _abortRegistration;
        private PrintDocumentInfo? _info;
        private IPrintDevice? _device;
        private long _generation;
        private int _crashes;
        private int _epoch;
        private int _sheetsPrinted;
        private int _bands;
        private bool _formLayer = true;
        private bool _formLayerUnavailable;
        private byte[]? _grey;
        private int _disposed;

        public Run(PrintJobRequest request, IPrintSource source, IPrintDeviceFactory devices, IProgress<PrintProgress>? progress, CancellationToken cancellation)
        {
            Request = request;
            Options = request.Options.Normalize();
            _source = source;
            _devices = devices;
            _progress = progress;
            _cancellation = cancellation;
        }

        public PrintJobRequest Request { get; }

        public PrintOptions Options { get; }

        public PrintEligibility? Eligibility { get; private set; }

        public DeviceOpenResult? Opened { get; private set; }

        public PrintPlanner? Planner { get; private set; }

        public PaperArea Paper => Opened?.Device.Paper ?? default;

        public int TotalSheets { get; private set; }

        /// <summary>Checks the policy and the permissions, opens the device and plans. A job that may not print returns its result; one that cannot start throws.</summary>
        public async Task<PrintResult?> PrepareAsync()
        {
            _cancellation.ThrowIfCancellationRequested();
            var (status, info) = await _source.GetInfoAsync(_cancellation).ConfigureAwait(false);
            if (status != Status.Ok || info is null)
            {
                throw new RunFailure(status, "The document could not be read for printing.");
            }

            _info = info;
            Eligibility = PrintEligibility.Evaluate(Request.PrintingAllowedByPolicy, info.Permissions);
            if (!Eligibility.Allowed)
            {
                return Finish(PrintOutcome.Disabled, Eligibility.DisabledReasonKey, null, Status.PermissionDenied);
            }

            var pages = Request.Pages;
            if (pages.IsEmpty || pages.Runs[^1].Last > info.PageCount || pages.First < 1)
            {
                throw new RunFailure(Status.InvalidArgument, "The pages to print are not in the document.");
            }

            var sample = new List<SizeD>();
            foreach (var page in pages.Take(OrientationSample))
            {
                sample.Add(await SizeOfAsync(page).ConfigureAwait(false));
            }

            var landscape = SheetLayout.ChooseLandscape(Options.Orientation, sample, Options.PagesPerSheet);
            Opened = await _thread.RunAsync(() => _devices.Open(new PrinterJobSettings(Request.PrinterName, Options, landscape))).ConfigureAwait(false);
            _device = Opened.Device;
            _abortRegistration = _cancellation.Register(static state => ((IPrintDevice)state!).RequestAbort(), _device);

            var limits = RenderLimits.For(Options, Eligibility.LowQualityOnly, _source.SlotBytes);
            Planner = new PrintPlanner(Options, _device.Paper, limits);
            TotalSheets = Planner.SheetCount(pages.Count) * Opened.Copies.Repeats * Opened.Copies.RepeatEachSheet;
            return null;
        }

        public async Task PrintAsync()
        {
            var device = _device!;
            await _thread.RunAsync(() => device.StartDocument(Request.DocumentName, Request.OutputPath)).ConfigureAwait(false);

            await using var items = Items().GetAsyncEnumerator(_cancellation);
            try
            {
                var current = await NextAsync(items).ConfigureAwait(false);
                var currentRender = Begin(current);
                while (current is not null)
                {
                    // The next band is asked for before this one is drawn, so the engine renders while the spooler takes pixels (print.md 3.5).
                    var next = await NextAsync(items).ConfigureAwait(false);
                    var nextRender = Begin(next);
                    await HandleAsync(current, currentRender).ConfigureAwait(false);
                    current = next;
                    currentRender = nextRender;
                }
            }
            finally
            {
                await DrainAsync().ConfigureAwait(false);
            }

            _cancellation.ThrowIfCancellationRequested();
            _progress?.Report(new PrintProgress(PrintStage.Finishing, TotalSheets, TotalSheets, 0));
            await _thread.RunAsync(device.EndDocument).ConfigureAwait(false);
        }

        public PrintResult Finish(PrintOutcome outcome, string? key, string? detail, Status status) => new(
            outcome, key, detail, status, _sheetsPrinted, TotalSheets, [.. _missing], _formLayerUnavailable, _crashes, Opened?.Report, Opened?.Copies ?? default, _bands);

        /// <summary>Ends the spool job without printing what is left (cancel, failure), then removes the empty file that <c>AbortDoc</c> leaves behind when printing to a file.</summary>
        public async Task AbortAsync()
        {
            await _render.CancelAsync().ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            if (_device is { } device)
            {
                try
                {
                    await _thread.RunAsync(device.AbortDocument).ConfigureAwait(false);
                }
                catch (PrintDeviceException)
                {
                    // The job is being abandoned: nothing the spooler says now matters.
                }
            }

            RemoveEmptyOutput();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _abortRegistration.DisposeAsync().ConfigureAwait(false);
            await _render.CancelAsync().ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            if (_device is { } device)
            {
                try
                {
                    await _thread.RunAsync(device.Dispose).ConfigureAwait(false);
                }
                catch (PrintDeviceException)
                {
                    // see AbortAsync
                }
            }

            _thread.Dispose();
            _render.Dispose();
            if (_grey is not null)
            {
                ArrayPool<byte>.Shared.Return(_grey);
                _grey = null;
            }
        }

        public async Task<SizeD> SizeOfAsync(int page)
        {
            if (_sizes.TryGetValue(page, out var known))
            {
                return known;
            }

            var first = page - 1;
            var count = Math.Min(SizeWindow, _info!.PageCount - first);
            var sizes = await WithRecoveryAsync(async () =>
            {
                var (status, list) = await _source.GetPageSizesAsync(first, count, _cancellation).ConfigureAwait(false);
                return (status, (IReadOnlyList<SizeD>?)list);
            }).ConfigureAwait(false);
            if (sizes is null || sizes.Count != count)
            {
                throw new RunFailure(Status.ProtocolError, "The engine did not answer with the page sizes.");
            }

            if (_sizes.Count > 4 * SizeWindow)
            {
                _sizes.Clear(); // a long job only needs the sizes near the page it is on
            }

            for (var i = 0; i < count; i++)
            {
                _sizes[first + i + 1] = sizes[i];
            }

            return _sizes[page];
        }

        // ---- the stream of work: StartSheet, bands, EndSheet, for every sheet, repeated for the copies we make ourselves ----

        private async IAsyncEnumerable<WorkItem> Items()
        {
            var copies = Opened!.Copies;
            var planner = Planner!;
            var pages = Request.Pages;
            var perSheet = planner.PagesPerSheet;
            var sheetsInRun = planner.SheetCount(pages.Count);
            var number = 0;
            for (var repeat = 0; repeat < copies.Repeats; repeat++)
            {
                for (var sheet = 0; sheet < sheetsInRun; sheet++)
                {
                    var onSheet = new List<(int Page, SizeD Size)>(perSheet);
                    for (var i = sheet * perSheet; i < Math.Min(pages.Count, (sheet + 1) * perSheet); i++)
                    {
                        var page = pages[i];
                        onSheet.Add((page, await SizeOfAsync(page).ConfigureAwait(false)));
                    }

                    var plan = planner.PlanSheet(onSheet);
                    for (var each = 0; each < copies.RepeatEachSheet; each++)
                    {
                        yield return new StartSheet(++number, onSheet[0].Page);
                        foreach (var placed in plan.Pages)
                        {
                            foreach (var band in placed.Render.Bands)
                            {
                                yield return new DrawBandItem(placed, band);
                            }
                        }

                        yield return new EndSheet();
                    }
                }
            }
        }

        private static async Task<WorkItem?> NextAsync(IAsyncEnumerator<WorkItem> items) => await items.MoveNextAsync().ConfigureAwait(false) ? items.Current : null;

        private Task<BandResult>? Begin(WorkItem? item)
        {
            if (item is not DrawBandItem draw)
            {
                return null;
            }

            var task = RenderWithRecoveryAsync(draw);
            _outstanding.Add(task);
            return task;
        }

        private async Task HandleAsync(WorkItem item, Task<BandResult>? render)
        {
            _cancellation.ThrowIfCancellationRequested();
            var device = _device!;
            switch (item)
            {
                case StartSheet start:
                    _progress?.Report(new PrintProgress(PrintStage.Sheet, start.Number, TotalSheets, start.FirstPage));
                    await _thread.RunAsync(device.StartPage).ConfigureAwait(false);
                    break;

                case DrawBandItem draw:
                    var result = await render!.ConfigureAwait(false);
                    _outstanding.Remove(render);
                    using (var band = result.Band!)
                    {
                        if (band.PartiallyRendered)
                        {
                            _missing.Add(draw.Placed.Page);
                        }

                        var placement = draw.Placed.Placement;
                        await _thread.RunAsync(() => Draw(device, band, placement, draw.Band)).ConfigureAwait(false);
                        _bands++;
                    }

                    break;

                case EndSheet:
                    await _thread.RunAsync(device.EndPage).ConfigureAwait(false);
                    _sheetsPrinted++;
                    break;
            }
        }

        // On the device thread. The band is the engine's pixels in a slot the ring maps read-only, so grey scale goes into a copy.
        private void Draw(IPrintDevice device, IPrintBand band, PagePlacement placement, BandSpec spec)
        {
            var pixels = band.Pixels;
            if (Options.Grayscale)
            {
                var length = band.Stride * band.Height;
                if (_grey is null || _grey.Length < length)
                {
                    if (_grey is not null)
                    {
                        ArrayPool<byte>.Shared.Return(_grey);
                    }

                    _grey = ArrayPool<byte>.Shared.Rent(length);
                }

                Grayscale.Convert(pixels, _grey, band.Width, band.Height, band.Stride);
                device.DrawBand(_grey.AsSpan(0, length), band.Width, band.Height, band.Stride, placement.X, spec.DestTop, placement.Width, spec.DestHeight);
            }
            else
            {
                device.DrawBand(pixels, band.Width, band.Height, band.Stride, placement.X, spec.DestTop, placement.Width, spec.DestHeight);
            }
        }

        // ---- the engine: crashes, timeouts and the form layer ----

        private async Task<BandResult> RenderWithRecoveryAsync(DrawBandItem draw)
        {
            var timedOut = false;
            while (true)
            {
                _render.Token.ThrowIfCancellationRequested();
                var epoch = Volatile.Read(ref _epoch);
                var withFormLayer = Volatile.Read(ref _formLayer) && Options.FormFields;
                var render = draw.Placed.Render;
                var args = new RenderBandArgs(
                    (uint)(draw.Placed.Page - 1),
                    render.Dpi,
                    draw.Placed.Placement.Rotation,
                    (uint)render.WidthPx,
                    (uint)render.HeightPx,
                    (uint)draw.Band.Top,
                    (uint)draw.Band.Height,
                    Options.BandFlags(withFormLayer));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _render.Token);
                var result = await _source.RenderBandAsync(args, (ulong)Interlocked.Increment(ref _generation), Request.BandDeadline, linked.Token).ConfigureAwait(false);
                switch (result.Status)
                {
                    case Status.Ok or Status.PartiallyRendered when result.Band is not null:
                        return result;

                    case Status.Cancelled:
                        throw new OperationCanceledException();

                    case Status.EngineCrashed:
                        // The supervisor is starting a new engine and reopening the document; the same band is asked for again, so no page is lost or printed twice.
                        NoteCrash(epoch);
                        break;

                    case Status.Timeout when !timedOut:
                        timedOut = true; // once more at the same size, then the job fails (print.md 6)
                        break;

                    case Status.Unsupported when withFormLayer:
                        // The engine has no form layer yet (its RenderBand refuses flag bit 1): print without it, the widgets still draw as annotations. Two bands can be
                        // in flight with the bit when the first refusal comes, so what decides is whether this request had it.
                        Volatile.Write(ref _formLayer, false);
                        _formLayerUnavailable = true;
                        break;

                    default:
                        throw new RunFailure(result.Status, $"The engine could not draw page {draw.Placed.Page}: {result.Status}" + (result.Detail is null ? string.Empty : " (" + result.Detail + ")"));
                }
            }
        }

        // Two bands can be in flight when the engine dies and both come back EngineCrashed: that is one crash. Only the call that moves the epoch on counts it.
        private void NoteCrash(int epoch)
        {
            if (Interlocked.CompareExchange(ref _epoch, epoch + 1, epoch) != epoch)
            {
                return;
            }

            var crashes = Interlocked.Increment(ref _crashes);
            if (crashes >= Request.MaxEngineCrashes)
            {
                throw new RunFailure(Status.EngineCrashed, $"The engine stopped {Request.MaxEngineCrashes} times while printing.");
            }
        }

        // Control calls (page sizes) get the same treatment as bands: wait for a restarted engine, once more after a timeout.
        private async Task<T?> WithRecoveryAsync<T>(Func<Task<(Status Status, T? Value)>> call)
            where T : class
        {
            var timedOut = false;
            while (true)
            {
                _cancellation.ThrowIfCancellationRequested();
                var epoch = Volatile.Read(ref _epoch);
                var (status, value) = await call().ConfigureAwait(false);
                switch (status)
                {
                    case Status.Ok:
                        return value;
                    case Status.Cancelled:
                        throw new OperationCanceledException();
                    case Status.EngineCrashed:
                        NoteCrash(epoch);
                        break;
                    case Status.Timeout when !timedOut:
                        timedOut = true;
                        break;
                    default:
                        throw new RunFailure(status, "The engine could not answer: " + status);
                }
            }
        }

        // Cancels what is in flight and gives the slots back: every band that was asked for and not drawn.
        private async Task DrainAsync()
        {
            if (_outstanding.Count == 0)
            {
                return;
            }

            await _render.CancelAsync().ConfigureAwait(false);
            foreach (var task in _outstanding.ToArray())
            {
                try
                {
                    var result = await task.ConfigureAwait(false);
                    result.Band?.Dispose();
                }
                catch (Exception e) when (e is OperationCanceledException or RunFailure)
                {
                    // It was abandoned or had failed; the failure that matters was already raised.
                }
            }

            _outstanding.Clear();
        }

        private void RemoveEmptyOutput()
        {
            if (Request.OutputPath is not { } path)
            {
                return;
            }

            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length == 0)
                {
                    file.Delete(); // AbortDoc leaves a zero byte file when printing to a file (ADR-0011 decision 3)
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by the spooler for a moment; an empty file is harmless.
            }
        }
    }
}
