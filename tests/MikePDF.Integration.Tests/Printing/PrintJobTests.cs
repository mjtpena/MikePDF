using System.Diagnostics;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Core.Reading;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// The print pipeline against a scripted engine and a recording printer: the order and the places of what is drawn, progress, cancel, the engine dying, the policy and
/// permissions, copies, N-up, grey scale. Criteria PRINT-09, -11, -12, -14 (flags), -16, -18, -24 at the pipeline level; the real printer and the real engine are in
/// <c>PrintToPdfTests</c>.
/// </summary>
public sealed class PrintJobTests
{
    private static readonly SizeD Letter = new(612, 792);
    private static readonly SizeD LetterLandscape = new(792, 612);
    private static readonly PaperArea A4At300 = new(2480, 3508, 300, 300);

    private static SizeD[] Letters(int count) => [.. Enumerable.Repeat(Letter, count)];

    private static PageSet All(int count) => PageSelection.Resolve(PrintScope.AllPages, count, 1, null, null, PageSubset.All).Pages;

    private static PrintJobRequest Request(PageSet pages, PrintOptions? options = null) => new() { PrinterName = "Fake", DocumentName = "Test document", Pages = pages, Options = options ?? new PrintOptions() };

    private static Task<PrintResult> RunAsync(PrintJobRequest request, FakePrintSource source, FakeDevice device, FakeDeviceFactory? factory = null, IProgress<PrintProgress>? progress = null, CancellationToken cancellationToken = default) =>
        PrintJob.RunAsync(request, source, factory ?? new FakeDeviceFactory(device), progress, cancellationToken);

    private sealed class Recorder : IProgress<PrintProgress>
    {
        public List<PrintProgress> Events { get; } = [];

        public void Report(PrintProgress value) => Events.Add(value);
    }

    // ---- the happy path ----

    [Fact]
    public async Task OnlyTheChosenPagesArePrintedInOrderInOneJobWithOneSheetPerPage()
    {
        var source = new FakePrintSource(Letters(6));
        var device = new FakeDevice(A4At300);
        var progress = new Recorder();
        var pages = PageSelection.Resolve(PrintScope.Range, 6, 1, "2-4", null, PageSubset.All).Pages;
        var result = await RunAsync(Request(pages), source, device, progress: progress);

        Assert.True(result.Completed, result.Detail);
        Assert.Equal(3, result.SheetsPrinted);
        Assert.Equal("StartDoc", device.Calls[0]);
        Assert.Equal(1, device.Calls.Count(c => c == "StartDoc"));
        Assert.Equal(3, device.Calls.Count(c => c == "StartPage"));
        Assert.Equal(3, device.Calls.Count(c => c == "EndPage"));
        Assert.Equal("EndDoc", device.Calls[^1]);
        Assert.True(device.Ended && !device.Aborted && device.Disposed);
        Assert.Equal("Test document", device.DocumentName);
        // The pixels name the page: blue is the 0-based page plus one.
        Assert.Equal([2, 3, 4], device.Draws.Select(d => (int)d.Blue).Distinct());
        Assert.Equal([2u, 3u, 4u], source.Requests.Select(r => r.Page + 1).Distinct());
        Assert.Equal([1, 2, 3], progress.Events.Where(e => e.Stage == PrintStage.Sheet).Select(e => e.Sheet));
        Assert.Equal([2, 3, 4], progress.Events.Where(e => e.Stage == PrintStage.Sheet).Select(e => e.FirstPage));
        Assert.All(progress.Events, e => Assert.Equal(3, e.TotalSheets));
        Assert.Equal(PrintStage.Finishing, progress.Events[^1].Stage);
        Assert.Equal(4, progress.Events.Count); // once per sheet and once at the end, not per band
        Assert.True(source.Bands.All(b => b.Disposed == 1), "every band went back to the ring exactly once");
    }

    [Fact]
    public async Task TheBandsOfAPageMeetOnThePaperWithNoGapAndNoOverlap()
    {
        var source = new FakePrintSource(Letter) { SlotBytes = 1 << 20 };
        var device = new FakeDevice(A4At300);
        var request = Request(All(1), new PrintOptions { Scaling = PrintScaling.Fit });
        var result = await RunAsync(request, source, device, new FakeDeviceFactory(device));

        Assert.True(result.Completed);
        Assert.True(device.Draws.Count > 3, "a 16 MiB band is not the whole page, so there are several bands");
        var placement = PrintLayout.Place(Letter, A4At300, PrintScaling.Fit, 100, true);
        Assert.Equal(placement.Y, device.Draws[0].Y);
        for (var i = 1; i < device.Draws.Count; i++)
        {
            Assert.Equal(device.Draws[i - 1].Y + device.Draws[i - 1].Height, device.Draws[i].Y);
        }

        Assert.Equal(placement.Y + placement.Height, device.Draws[^1].Y + device.Draws[^1].Height);
        Assert.All(device.Draws, d => Assert.Equal(placement.X, d.X));
        Assert.All(device.Draws, d => Assert.Equal(placement.Width, d.Width));
        Assert.All(device.Draws, d => Assert.Equal(d.SrcHeight, d.Height)); // 300 dpi device: a 1:1 blit, no stretching
    }

    [Fact]
    public async Task TheEngineIsAskedForTheNextBandWhileTheCurrentOneIsBeingDrawnButNeverMoreThanTwoAhead()
    {
        var source = new FakePrintSource(Letters(2)) { SlotBytes = 1 << 20, RenderDelay = TimeSpan.FromMilliseconds(2) };
        var device = new FakeDevice(A4At300);
        var requestsWhenFirstBandWasDrawn = 0;
        device.AfterDraw = n =>
        {
            if (n == 1)
            {
                requestsWhenFirstBandWasDrawn = source.Requests.Count;
            }

            Thread.Sleep(15);
        };
        var result = await RunAsync(Request(All(2)), source, device);

        Assert.True(result.Completed);
        Assert.True(requestsWhenFirstBandWasDrawn >= 2, "band 2 was requested before band 1 was finished");
        Assert.True(source.MaxInFlight <= 2, $"{source.MaxInFlight} bands were in flight; the ring has three slots, one of them being drawn");
    }

    [Fact]
    public async Task EveryBandIsRequestedWithTheGeometryTheEngineIsToldAbout()
    {
        var source = new FakePrintSource(Letter);
        var device = new FakeDevice(A4At300);
        var options = new PrintOptions { Scaling = PrintScaling.Fit, Annotations = true, FormFields = false };
        await RunAsync(Request(All(1), options), source, device);

        var placement = PrintLayout.Place(Letter, A4At300, PrintScaling.Fit, 100, true);
        Assert.All(source.Requests, r =>
        {
            Assert.Equal(0u, r.Page);
            Assert.Equal((uint)placement.Width, r.PageWidthPx);
            Assert.Equal((uint)placement.Height, r.PageHeightPx);
            Assert.Equal(300u, r.Dpi);
            Assert.Equal(0, r.Rotation);
            Assert.Equal(5u, r.Flags); // annotations and printing mode, no form fields
        });
        Assert.Equal(0u, source.Requests.Min(r => r.BandTop));
        Assert.Equal(source.Requests.Max(r => r.BandTop + r.BandHeight), (uint)placement.Height);
    }

    // ---- options that reach the engine ----

    [Theory]
    [InlineData(true, true, 7u)]
    [InlineData(true, false, 5u)]
    [InlineData(false, true, 6u)]
    [InlineData(false, false, 4u)]
    public async Task TheAnnotationAndFormOptionsAreTheEngineFlagsAndPrintingModeIsAlwaysOn(bool annotations, bool forms, uint expected)
    {
        var source = new FakePrintSource(Letter);
        await RunAsync(Request(All(1), new PrintOptions { Annotations = annotations, FormFields = forms }), source, new FakeDevice(A4At300));
        Assert.All(source.Requests, r => Assert.Equal(expected, r.Flags));
    }

    [Fact]
    public async Task AnEngineWithoutAFormLayerIsAskedAgainWithoutItAndTheResultSaysSo()
    {
        var source = new FakePrintSource(Letters(2)) { Script = (args, _) => (args.Flags & 2) != 0 ? Status.Unsupported : Status.Ok };
        var result = await RunAsync(Request(All(2)), source, new FakeDevice(A4At300));

        Assert.True(result.Completed);
        Assert.True(result.FormLayerUnavailable);
        Assert.Equal(7u, source.Requests[0].Flags); // the first try had it
        Assert.Contains(source.Requests, r => r.Flags == 5u);
        Assert.Equal(source.Requests.Count(r => r.Flags == 5u), source.Requests.Skip(source.Requests.FindIndex(r => r.Flags == 5u)).Count(r => r.Flags == 5u)); // and once dropped it stays dropped
        Assert.Equal(0, result.EngineCrashes);
    }

    [Fact]
    public async Task UnsupportedWithoutTheFormLayerIsAFailureNotARetry()
    {
        var source = new FakePrintSource(Letter) { Script = (_, _) => Status.Unsupported };
        var result = await RunAsync(Request(All(1), new PrintOptions { FormFields = false }), source, new FakeDevice(A4At300));
        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(Status.Unsupported, result.EngineStatus);
    }

    [Fact]
    public async Task AutoRotateTurnsOnlyThePagesWhoseOrientationDiffersFromThePaper()
    {
        SizeD[] pages = [Letter, LetterLandscape, Letter, LetterLandscape];
        foreach (var auto in new[] { true, false })
        {
            var source = new FakePrintSource(pages);
            var device = new FakeDevice(A4At300);
            var factory = new FakeDeviceFactory(device);
            await RunAsync(Request(All(4), new PrintOptions { AutoRotate = auto, Orientation = PaperOrientation.Portrait }), source, device, factory);
            Assert.False(factory.Settings!.Landscape);
            var rotated = source.Requests.GroupBy(r => r.Page).OrderBy(g => g.Key).Select(g => g.First().Rotation).ToArray();
            Assert.Equal(auto ? [0, 1, 0, 1] : [0, 0, 0, 0], rotated);
        }
    }

    [Fact]
    public async Task AutoPaperOrientationIsChosenFromThePagesAndHandedToTheDriver()
    {
        var landscapeDevice = new FakeDevice(new PaperArea(3508, 2480, 300, 300));
        var landscape = new FakeDeviceFactory(landscapeDevice);
        await RunAsync(Request(All(3)), new FakePrintSource(LetterLandscape, LetterLandscape, Letter), landscapeDevice, landscape);
        Assert.True(landscape.Settings!.Landscape);

        var portraitDevice = new FakeDevice(A4At300);
        var portrait = new FakeDeviceFactory(portraitDevice);
        await RunAsync(Request(All(3)), new FakePrintSource(Letter, Letter, LetterLandscape), portraitDevice, portrait);
        Assert.False(portrait.Settings!.Landscape);
    }

    [Fact]
    public async Task GrayscaleSendsGreyPixelsToThePrinter()
    {
        var source = new FakePrintSource(Letter);
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(1), new PrintOptions { Grayscale = true }), source, device);
        Assert.True(result.Completed);
        Assert.NotEmpty(device.Draws);
        Assert.All(device.Draws, d => Assert.True(d.Blue == d.Green && d.Green == d.Red, "grey has equal channels"));
        Assert.All(device.Draws, d => Assert.NotEqual(200, d.Red)); // the fake band is red 200; grey is the luminance of it, not the red

        var colour = new FakeDevice(A4At300);
        await RunAsync(Request(All(1)), new FakePrintSource(Letter), colour);
        Assert.All(colour.Draws, d => Assert.Equal(200, d.Red));
    }

    [Fact]
    public async Task PrintAsImageAsksForOneBandPerPageAtNoMoreThan300Dpi()
    {
        var source = new FakePrintSource(Letters(3)) { SlotBytes = 64 << 20 };
        var device = new FakeDevice(new PaperArea(4960, 7016, 600, 600)); // a 600 dpi printer
        var result = await RunAsync(Request(All(3), new PrintOptions { AsImage = true, Scaling = PrintScaling.Fit }), source, device);

        Assert.True(result.Completed);
        Assert.Equal(3, source.Requests.Count); // one request, so one bitmap, per page
        Assert.All(source.Requests, r =>
        {
            Assert.Equal(0u, r.BandTop);
            Assert.Equal(r.PageHeightPx, r.BandHeight);
            Assert.True(r.Dpi <= 300);
            Assert.True(4L * r.PageWidthPx * r.PageHeightPx <= 64 << 20);
        });
        Assert.Equal(3, device.Draws.Count);
        Assert.All(device.Draws, d => Assert.True(d.Width > d.SrcWidth, "the 300 dpi bitmap is stretched onto the 600 dpi paper"));
    }

    // ---- policy and permissions (PRINT-09) ----

    [Fact]
    public async Task APolicyThatTurnsPrintingOffStopsTheJobBeforeThePrinterIsOpened()
    {
        var source = new FakePrintSource(Letter);
        var device = new FakeDevice(A4At300);
        var factory = new FakeDeviceFactory(device);
        var result = await RunAsync(Request(All(1)) with { PrintingAllowedByPolicy = false }, source, device, factory);

        Assert.Equal(PrintOutcome.Disabled, result.Outcome);
        Assert.Equal("Print_Disabled_Policy", result.ResourceKey);
        Assert.Equal(0, factory.Opened);
        Assert.Empty(device.Calls);
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task ADocumentWhoseOwnerForbidsPrintingIsNotPrinted()
    {
        var source = new FakePrintSource(Letter) { Permissions = new PdfPermissions(0x800, 3) }; // bit 12 set, bit 3 clear
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(1)), source, device);

        Assert.Equal(PrintOutcome.Disabled, result.Outcome);
        Assert.Equal("Print_Disabled_Owner", result.ResourceKey);
        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task LowQualityOnlyCapsTheRenderResolutionAt150Dpi()
    {
        var source = new FakePrintSource(Letter) { Permissions = new PdfPermissions(0x4, 3) }; // bit 3 set, bit 12 clear
        var device = new FakeDevice(new PaperArea(4960, 7016, 600, 600));
        var result = await RunAsync(Request(All(1), new PrintOptions { Scaling = PrintScaling.Fit }), source, device);

        Assert.True(result.Completed);
        Assert.NotEmpty(source.Requests);
        Assert.All(source.Requests, r => Assert.Equal(150u, r.Dpi));
        Assert.All(device.Draws, d => Assert.True(d.Width >= 3 * d.SrcWidth, "150 dpi bitmaps stretched onto 600 dpi paper")); // 4x for the width of every band
    }

    [Fact]
    public async Task AHighQualityDocumentIsRenderedAtThePrintersResolution()
    {
        var source = new FakePrintSource(Letter) { Permissions = new PdfPermissions(0x4 | 0x800, 3) };
        await RunAsync(Request(All(1), new PrintOptions { Scaling = PrintScaling.Fit }), source, new FakeDevice(new PaperArea(4960, 7016, 600, 600)));
        Assert.All(source.Requests, r => Assert.Equal(600u, r.Dpi));
    }

    // ---- cancel (PRINT-11) ----

    [Fact]
    public async Task CancellingMidJobAbortsTheSpoolJobStopsTheRendersAndRemovesTheEmptyFile()
    {
        var source = new FakePrintSource(Letters(20)) { SlotBytes = 1 << 20 };
        var device = new FakeDevice(A4At300);
        var output = Path.Combine(Path.GetTempPath(), "mikepdf-print-cancel-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        using var cancel = new CancellationTokenSource();
        device.AfterDraw = n =>
        {
            if (n == 5)
            {
                cancel.Cancel();
            }
        };
        var clock = Stopwatch.StartNew();
        var result = await RunAsync(Request(All(20)) with { OutputPath = output }, source, device, cancellationToken: cancel.Token);
        clock.Stop();

        Assert.Equal(PrintOutcome.Cancelled, result.Outcome);
        Assert.True(device.Aborted && !device.Ended, "AbortDoc, not EndDoc");
        Assert.True(device.AbortRequested, "the abort procedure was told, for a call stuck in the spooler");
        Assert.True(device.Disposed);
        Assert.Equal(0, source.InFlight);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"cancel took {clock.ElapsedMilliseconds} ms to settle");
        Assert.True(source.Requests.Count < 20, "the job did not render the rest");
        Assert.True(source.Bands.All(b => b.Disposed == 1), "every slot was given back, drawn or not");
        Assert.False(File.Exists(output), "the empty file AbortDoc leaves behind is deleted");
        Assert.True(result.SheetsPrinted < 20);
    }

    [Fact]
    public async Task AJobCancelledBeforeItStartsDoesNothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var device = new FakeDevice(A4At300);
        var factory = new FakeDeviceFactory(device);
        var result = await RunAsync(Request(All(1)), new FakePrintSource(Letter), device, factory, cancellationToken: cancel.Token);
        Assert.Equal(PrintOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, factory.Opened);
    }

    // ---- the engine dying and being slow (PRINT-12) ----

    [Fact]
    public async Task AnEngineCrashMidJobIsWaitedForAndTheSameBandIsAskedAgainWithNoPageLostOrDoubled()
    {
        var reference = new FakeDevice(A4At300);
        await RunAsync(Request(All(4)), new FakePrintSource(Letters(4)) { SlotBytes = 1 << 20 }, reference);

        // The engine dies while call 5 is being served. Call 6, the next band, is in flight with it and, being slow, reports the death long after the job has handled call 5's crash and asked
        // again: it is the same death, so one crash.
        var source = new FakePrintSource(Letters(4))
        {
            SlotBytes = 1 << 20,
            DieDuringCall = (_, call) => call == 5,
            DelayFor = (_, call) => call == 6 ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromMilliseconds(1),
        };
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(4)), source, device);

        Assert.True(result.Completed, result.Detail);
        Assert.Equal(1, source.Deaths);
        Assert.Equal(1, result.EngineCrashes);
        Assert.Equal(reference.Draws, device.Draws); // the same pixels in the same places in the same order: nothing lost, nothing printed twice
        Assert.Equal(4, device.Calls.Count(c => c == "StartPage"));
        Assert.True(source.Requests.Count > reference.Draws.Count, "the crashed bands were requested again");
    }

    [Fact]
    public async Task ACrashThatTheJobHearsOfWhileTheEngineIsStillBeingReplacedCountsOnce()
    {
        // The engine has died but the restart generation has not moved on yet: every call issued meanwhile meets the dead engine and ends EngineCrashed, as many times as the job asks.
        // That is still the one death.
        var source = new FakePrintSource(Letters(3)) { SlotBytes = 1 << 20, DieDuringCall = (_, call) => call == 5, RestartDelay = TimeSpan.FromMilliseconds(40) };
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(3)), source, device);

        Assert.True(result.Completed, result.Detail);
        Assert.Equal(1, source.Deaths);
        Assert.Equal(1, result.EngineCrashes);
        Assert.True(source.Requests.Count > device.Draws.Count + 3, "the job asked again while the engine was down");
    }

    [Fact]
    public async Task TheJobFailsAfterThreeCrashesAndAbortsTheSpoolJob()
    {
        // The engine dies while calls 3, 6 and 9 are being served: three deaths, each with a restart before the next.
        var source = new FakePrintSource(Letters(4)) { RenderDelay = TimeSpan.FromMilliseconds(5), DieDuringCall = (_, call) => call % 3 == 0 };
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(4)), source, device);

        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.True(source.Deaths >= 3);
        Assert.Equal(Status.EngineCrashed, result.EngineStatus);
        Assert.Equal("Print_Failed", result.ResourceKey);
        Assert.Contains("3", result.Detail);
        Assert.True(device.Aborted && !device.Ended && device.Disposed);
        Assert.True(source.Bands.All(b => b.Disposed == 1));
        Assert.Equal(0, source.InFlight);
    }

    [Fact]
    public async Task TwoCrashesStillFinishTheJob()
    {
        // The engine dies while the first band of page 1 is served and again while the first band of page 3 is: two deaths, with a whole page between them.
        var died = new HashSet<uint>();
        var source = new FakePrintSource(Letters(3))
        {
            DieDuringCall = (args, _) =>
            {
                lock (died)
                {
                    return args.BandTop == 0 && args.Page is 0 or 2 && died.Add(args.Page);
                }
            },
        };
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(3)), source, device);
        Assert.True(result.Completed, result.Detail);
        Assert.Equal(2, result.EngineCrashes);
        Assert.Equal(3, device.Calls.Count(c => c == "StartPage"));
    }

    [Fact]
    public async Task ABandThatTimesOutIsAskedOnceMoreAndThenTheJobFails()
    {
        var once = new FakePrintSource(Letters(2)) { Script = (_, call) => call == 2 ? Status.Timeout : Status.Ok };
        var done = await RunAsync(Request(All(2)), once, new FakeDevice(A4At300));
        Assert.True(done.Completed);

        var twice = new FakePrintSource(Letters(2)) { Script = (args, call) => call >= 2 && args.BandTop == 0 && args.Page == 1 ? Status.Timeout : Status.Ok };
        var device = new FakeDevice(A4At300);
        var failed = await RunAsync(Request(All(2)), twice, device);
        Assert.Equal(PrintOutcome.Failed, failed.Outcome);
        Assert.Equal(Status.Timeout, failed.EngineStatus);
        Assert.True(device.Aborted);
    }

    [Fact]
    public async Task AnEngineThatRefusesTheBandFailsTheJobWithItsStatus()
    {
        var source = new FakePrintSource(Letter) { Script = (_, _) => Status.Damaged };
        var result = await RunAsync(Request(All(1)), source, new FakeDevice(A4At300));
        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(Status.Damaged, result.EngineStatus);
    }

    [Fact]
    public async Task APageThePrinterCouldOnlyBeToldInPartIsListedForTheNotice()
    {
        var source = new FakePrintSource(Letters(5));
        source.PartialPages.Add(1);
        source.PartialPages.Add(3);
        var result = await RunAsync(Request(All(5)), source, new FakeDevice(A4At300));
        Assert.True(result.Completed);
        Assert.Equal([2, 4], result.PagesWithMissingContent); // 1-based, for "Some content couldn't be printed on pages 2, 4"
    }

    // ---- the printer failing ----

    [Theory]
    [InlineData("StartDoc")]
    [InlineData("DrawBand")]
    [InlineData("EndPage")]
    public async Task APrinterThatFailsEndsTheJobWithItsReasonAndAbortsTheSpoolJob(string at)
    {
        var source = new FakePrintSource(Letters(2));
        var device = new FakeDevice(A4At300) { FailAt = at };
        var result = await RunAsync(Request(All(2)), source, device);

        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal("Print_Failed", result.ResourceKey);
        Assert.Contains("failed", result.Detail);
        Assert.True(device.Disposed);
        Assert.False(device.Ended);
        Assert.Equal(0, source.InFlight);
        Assert.True(source.Bands.All(b => b.Disposed == 1), "no slot leaked");
    }

    [Fact]
    public async Task APrinterThatCannotBeOpenedFailsTheJobCleanly()
    {
        var factory = new ThrowingFactory();
        var result = await PrintJob.RunAsync(Request(All(1)), new FakePrintSource(Letter), factory);
        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Contains("offline", result.Detail);
    }

    private sealed class ThrowingFactory : IPrintDeviceFactory
    {
        public DeviceOpenResult Open(PrinterJobSettings settings) => throw new PrintDeviceException("The printer is offline", 1722);
    }

    // ---- copies, N-up ----

    [Fact]
    public async Task CopiesTheDriverDoesNotMakeAreMadeByRepeatingTheRun()
    {
        var collated = new FakeDevice(A4At300);
        await RunAsync(Request(All(2), new PrintOptions { Copies = 3, Collate = true }), new FakePrintSource(Letters(2)), collated, new FakeDeviceFactory(collated) { Copies = new CopyPlan(1, false, 3, 1) });
        Assert.Equal([1, 2, 1, 2, 1, 2], SheetPages(collated));

        var uncollated = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(2), new PrintOptions { Copies = 3, Collate = false }), new FakePrintSource(Letters(2)), uncollated, new FakeDeviceFactory(uncollated) { Copies = new CopyPlan(1, false, 1, 3) });
        Assert.Equal([1, 1, 1, 2, 2, 2], SheetPages(uncollated));
        Assert.Equal(6, result.SheetsPrinted);
        Assert.Equal(6, result.TotalSheets);
    }

    [Fact]
    public async Task CopiesTheDriverKeptAreNotMultipliedByUs()
    {
        var device = new FakeDevice(A4At300);
        var result = await RunAsync(Request(All(2), new PrintOptions { Copies = 3 }), new FakePrintSource(Letters(2)), device, new FakeDeviceFactory(device) { Copies = new CopyPlan(3, true, 1, 1) });
        Assert.Equal(2, device.Calls.Count(c => c == "StartPage"));
        Assert.Equal(2, result.TotalSheets);
        Assert.Equal(3, result.Copies.DeviceCopies);
    }

    private static int[] SheetPages(FakeDevice device) => [.. device.Draws.GroupBy(d => d.Sheet).OrderBy(g => g.Key).Select(g => g.First().Blue)];

    [Fact]
    public async Task NUpPutsSeveralPagesOnOneSheetInTheirCells()
    {
        var source = new FakePrintSource(Letters(6));
        var device = new FakeDevice(A4At300);
        var progress = new Recorder();
        var result = await RunAsync(Request(All(6), new PrintOptions { PagesPerSheet = 4 }), source, device, progress: progress);

        Assert.True(result.Completed);
        Assert.Equal(2, result.TotalSheets);
        Assert.Equal(2, device.Calls.Count(c => c == "StartPage"));
        Assert.Equal(4, device.Draws.Where(d => d.Sheet == 1).Select(d => d.Blue).Distinct().Count()); // four pages on sheet one
        Assert.Equal(2, device.Draws.Where(d => d.Sheet == 2).Select(d => d.Blue).Distinct().Count()); // two on the second
        var firstOfEach = device.Draws.Where(d => d.Sheet == 1).GroupBy(d => d.Blue).Select(g => g.First()).ToList();
        Assert.Equal(4, firstOfEach.Select(d => (d.X, d.Y)).Distinct().Count()); // four different places
        Assert.Equal(2, progress.Events.Count(e => e.Stage == PrintStage.Sheet));
    }

    // ---- hostile input (PRINT-24) ----

    [Fact]
    public async Task AHostilePageSizeAndCopyCountCannotAllocateBeyondTheCapsOrPrintMoreThan99Copies()
    {
        SizeD[] pages = [new(14400, 14400), new(1, 14400), new(14400, 1), new(double.NaN, -5)];
        foreach (var scaling in new[] { PrintScaling.Actual, PrintScaling.Custom, PrintScaling.Fit, PrintScaling.Shrink })
        {
            var source = new FakePrintSource(pages) { SlotBytes = 16 << 20 };
            var device = new FakeDevice(new PaperArea(4960, 7016, 600, 600));
            var factory = new FakeDeviceFactory(device);
            var options = new PrintOptions { Scaling = scaling, CustomPercent = 400, Copies = int.MaxValue };
            var result = await RunAsync(Request(All(4), options), source, device, factory);

            Assert.True(result.Completed, $"{scaling}: {result.Detail}");
            Assert.Equal(99, factory.Settings!.Options.Copies);
            Assert.NotEmpty(source.Requests);
            Assert.All(source.Requests, r =>
            {
                Assert.InRange(r.PageWidthPx, 1u, 65536u);
                Assert.InRange(r.PageHeightPx, 1u, 65536u);
                Assert.True((long)r.PageWidthPx * r.PageHeightPx <= RenderLimits.DefaultMaxPagePixels, $"{scaling}: a {r.PageWidthPx} x {r.PageHeightPx} page");
                Assert.True(4L * r.PageWidthPx * r.BandHeight <= 16 << 20, "a band fits the slot");
            });
        }
    }

    [Fact]
    public async Task APageRangeThatDoesNotFitTheDocumentFailsBeforeThePrinterIsOpened()
    {
        var device = new FakeDevice(A4At300);
        var factory = new FakeDeviceFactory(device);
        var tooMany = PageSelection.Resolve(PrintScope.AllPages, 10, 1, null, null, PageSubset.All).Pages;
        var result = await RunAsync(Request(tooMany), new FakePrintSource(Letters(3)), device, factory);
        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(Status.InvalidArgument, result.EngineStatus);
        Assert.Equal(0, factory.Opened);
    }

    // ---- preflight ----

    [Fact]
    public async Task PreflightNamesThePagesThatWouldBeCutOffAndNothingIsSpooled()
    {
        var source = new FakePrintSource(Letter, new SizeD(300, 300), new SizeD(2000, 2000));
        var device = new FakeDevice(A4At300);
        var factory = new FakeDeviceFactory(device);
        var actual = await PrintJob.PreflightAsync(Request(All(3), new PrintOptions { Scaling = PrintScaling.Actual }), source, factory);

        Assert.True(actual.Eligibility.Allowed);
        Assert.Equal([1, 3], actual.CutOffPages); // Letter is 2,550 px wide at 300 dpi on a 2,480 px area
        Assert.Equal(3, actual.TotalSheets);
        Assert.Equal(A4At300, actual.Paper);
        Assert.Empty(device.Calls); // no StartDoc

        var fit = await PrintJob.PreflightAsync(Request(All(3), new PrintOptions { Scaling = PrintScaling.Fit }), source, factory);
        Assert.Empty(fit.CutOffPages);
        var nup = await PrintJob.PreflightAsync(Request(All(3), new PrintOptions { Scaling = PrintScaling.Actual, PagesPerSheet = 2 }), source, factory);
        Assert.Empty(nup.CutOffPages);
        Assert.Equal(2, nup.TotalSheets);
    }

    [Fact]
    public async Task PreflightReportsADisabledPrintAndTheReason()
    {
        var factory = new FakeDeviceFactory(new FakeDevice(A4At300));
        var policy = await PrintJob.PreflightAsync(Request(All(1)) with { PrintingAllowedByPolicy = false }, new FakePrintSource(Letter), factory);
        Assert.False(policy.Eligibility.Allowed);
        Assert.Equal("Print_Disabled_Policy", policy.Eligibility.DisabledReasonKey);
        Assert.Equal(0, factory.Opened);

        var owner = await PrintJob.PreflightAsync(Request(All(1)), new FakePrintSource(Letter) { Permissions = new PdfPermissions(0, 3) }, factory);
        Assert.Equal("Print_Disabled_Owner", owner.Eligibility.DisabledReasonKey);

        var low = await PrintJob.PreflightAsync(Request(All(1)), new FakePrintSource(Letter) { Permissions = new PdfPermissions(0x4, 3) }, factory);
        Assert.True(low.Eligibility.LowQualityOnly);
    }
}
