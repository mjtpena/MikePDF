using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// What the print sheet adds to the pipeline: the preview (drawn by the job's own planner at screen size), the driver's DEVMODE and the Win32 error of a failed job reaching the sheet, and the
/// printer facts the sheet reads. Against the scripted engine and a recording printer, plus the real Microsoft Print to PDF queue for the facts (skipped, with the reason, where it is not installed).
/// </summary>
public sealed class PrintSheetPipelineTests
{
    private static readonly SizeD A4 = new(595.28, 841.89);
    private static readonly SizeD Landscape = new(841.89, 595.28);

    // An A4 sheet at 600 dpi with a 100 pixel margin all round (what a real printer's printable area looks like).
    private static readonly PaperArea Printable = new(4760, 6816, 600, 600);
    private static readonly PhysicalPaper Physical = new(4960, 7016, 100, 100);

    private static PreviewRequest Preview(PrintOptions options, params int[] pages) => new(options, pages, Printable, Physical, LongEdgePx: 900);

    private static (byte Blue, byte Green, byte Red) At(PreviewImage image, int x, int y)
    {
        var i = ((y * image.Width) + x) * 4;
        return (image.Bgra[i], image.Bgra[i + 1], image.Bgra[i + 2]);
    }

    // ---- the picture: paper, margins, page ----

    [Fact]
    public async Task ThePreviewIsTheWholePaperWithItsMarginsAndThePageInsideThePrintableArea()
    {
        var source = new FakePrintSource(A4);
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { Scaling = PrintScaling.Fit }, 1), CancellationToken.None);

        Assert.InRange(image.Height, 880, 910); // the picture is as near 900 pixels as a whole dpi lets it be
        Assert.InRange(image.Width, (int)(900.0 * 4960 / 7016) - 1, (int)(900.0 * 4960 / 7016) + 1);
        Assert.False(image.Landscape);
        Assert.Equal(4 * image.Width * image.Height, image.Bgra.Length);
        Assert.All(image.Pages, p => Assert.False(p.Failed));
        // The middle is the page (blue names the page, red is the fake's ink); the outer edge of the paper is white: the margin the printer cannot print in.
        var centre = At(image, image.Width / 2, image.Height / 2);
        Assert.Equal(((byte)1, (byte)200), (centre.Blue, centre.Red));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(image, 3, image.Height / 2));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(image, image.Width - 3, image.Height / 2));
    }

    [Fact]
    public async Task ActualSizeOfASmallPageLeavesThePaperAroundItAndTheCentreStillHoldsThePage()
    {
        var source = new FakePrintSource(new SizeD(300, 400));
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { Scaling = PrintScaling.Actual }, 1), CancellationToken.None);
        Assert.Equal((byte)1, At(image, image.Width / 2, image.Height / 2).Blue);
        Assert.Equal((byte)255, At(image, image.Width / 2, 40).Blue); // well above a 400 pt page on 842 pt paper
    }

    [Fact]
    public async Task ThePreviewTurnsALandscapePageOnPortraitPaperOnlyWhenAutoRotateIsOn()
    {
        var on = new FakePrintSource(Landscape);
        var rotated = await PrintPreview.RenderAsync(on, Preview(new PrintOptions { Scaling = PrintScaling.Fit, AutoRotate = true }, 1), CancellationToken.None);
        Assert.True(rotated.Pages[0].Rotated);
        Assert.Equal((ushort)1, (ushort)on.Requests[0].Rotation);

        var off = new FakePrintSource(Landscape);
        var upright = await PrintPreview.RenderAsync(off, Preview(new PrintOptions { Scaling = PrintScaling.Fit, AutoRotate = false }, 1), CancellationToken.None);
        Assert.False(upright.Pages[0].Rotated);
        Assert.Equal((ushort)0, (ushort)off.Requests[0].Rotation);
    }

    [Fact]
    public async Task AGreyScalePreviewHasOnlyGreys()
    {
        var source = new FakePrintSource(A4);
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { Scaling = PrintScaling.Fit, Grayscale = true }, 1), CancellationToken.None);
        var (blue, green, red) = At(image, image.Width / 2, image.Height / 2);
        Assert.True(blue == green && green == red, $"({blue},{green},{red})");
        Assert.NotEqual(255, blue);
    }

    // ---- pages per sheet ----

    [Theory]
    [InlineData(SheetOrder.Horizontal, new[] { 1, 2, 3, 4 })]
    [InlineData(SheetOrder.Vertical, new[] { 1, 3, 2, 4 })]
    public async Task FourPagesOnASheetFillTheCellsInTheChosenOrder(SheetOrder order, int[] topLeftTopRightBottomLeftBottomRight)
    {
        var source = new FakePrintSource(A4, A4, A4, A4);
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { PagesPerSheet = 4, SheetOrder = order }, 1, 2, 3, 4), CancellationToken.None);

        var centres = new[] { (image.Width / 4, image.Height / 4), (3 * image.Width / 4, image.Height / 4), (image.Width / 4, 3 * image.Height / 4), (3 * image.Width / 4, 3 * image.Height / 4) };
        var seen = centres.Select(c => (int)At(image, c.Item1, c.Item2).Blue).ToArray();
        Assert.Equal(topLeftTopRightBottomLeftBottomRight, seen);
        Assert.Equal(4, image.Pages.Count);
    }

    [Fact]
    public async Task ALastSheetWithFewerPagesLeavesItsOtherCellsEmpty()
    {
        var source = new FakePrintSource(A4, A4, A4, A4, A4);
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { PagesPerSheet = 4 }, 5), CancellationToken.None);
        Assert.Equal(5, At(image, image.Width / 4, image.Height / 4).Blue);
        Assert.Equal(255, At(image, 3 * image.Width / 4, image.Height / 4).Blue);
        Assert.Equal(255, At(image, 3 * image.Width / 4, 3 * image.Height / 4).Blue);
    }

    // ---- what reaches the engine ----

    [Fact]
    public async Task TheAnnotationAndFormChoicesReachTheEngineAsTheJobSendsThemAndThePreviewIsNeverAnImage()
    {
        var source = new FakePrintSource(A4);
        var options = new PrintOptions { Annotations = false, FormFields = true, AsImage = true };
        await PrintPreview.RenderAsync(source, Preview(options, 1), CancellationToken.None);
        Assert.All(source.Requests, r => Assert.Equal(options.BandFlags(), r.Flags));
        Assert.All(source.Requests, r => Assert.True(r.Flags is 6u, "printing mode on, annotations off, form fields on"));
    }

    [Fact]
    public async Task EveryBandIsGivenBackToTheRingAndCancellingStopsTheWork()
    {
        var source = new FakePrintSource(A4) { SlotBytes = 1 << 20, RenderDelay = TimeSpan.FromMilliseconds(30) };
        using var cancel = new CancellationTokenSource();
        var running = PrintPreview.RenderAsync(source, Preview(new PrintOptions { Scaling = PrintScaling.Fit }, 1), cancel.Token);
        await Task.Delay(50);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(source.Bands.All(b => b.Disposed == 1));
        var count = source.Requests.Count;
        await Task.Delay(150);
        Assert.Equal(count, source.Requests.Count); // nothing is asked for after the cancel
    }

    [Fact]
    public async Task AFullRenderUsesTheBandsTheJobWouldAndDisposesThemAll()
    {
        var source = new FakePrintSource(A4) { SlotBytes = 1 << 20 };
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions { Scaling = PrintScaling.Fit }, 1), CancellationToken.None);
        Assert.NotEmpty(source.Bands);
        Assert.True(source.Bands.All(b => b.Disposed == 1));
        Assert.False(image.AnyFailed);
    }

    [Fact]
    public async Task APageTheEngineCannotDrawIsReportedAndThePaperIsStillReturned()
    {
        var source = new FakePrintSource(A4) { Script = (_, _) => Status.InvalidArgument };
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions(), 1), CancellationToken.None);
        Assert.True(image.AnyFailed);
        Assert.Equal(255, At(image, image.Width / 2, image.Height / 2).Blue);
    }

    [Fact]
    public async Task APartiallyDrawnPageIsMarkedPartial()
    {
        var source = new FakePrintSource(A4);
        source.PartialPages.Add(0);
        var image = await PrintPreview.RenderAsync(source, Preview(new PrintOptions(), 1), CancellationToken.None);
        Assert.True(image.Pages[0].Partial);
        Assert.False(image.AnyFailed);
    }

    [Fact]
    public async Task WhenTheDeviceDoesNotSayHowBigTheWholePaperIsThePrintableAreaIsTheWholePicture()
    {
        var source = new FakePrintSource(A4);
        var image = await PrintPreview.RenderAsync(source, new PreviewRequest(new PrintOptions { Scaling = PrintScaling.Fit }, [1], Printable, default), CancellationToken.None);
        Assert.InRange(image.Height, 880, 910); // the picture is as near 900 pixels as a whole dpi lets it be
        Assert.Equal(1, At(image, 3, image.Height / 2).Blue); // the page fills the width: no margin to draw
    }

    // ---- the driver's settings and the failure reach the sheet ----

    [Fact]
    public async Task TheDriversOwnSettingsFromThePreferencesDialogReachTheDevice()
    {
        var device = new FakeDevice(new PaperArea(2480, 3508, 300, 300));
        var factory = new FakeDeviceFactory(device);
        var devMode = new DevMode(MakeDevMode());
        var request = new PrintJobRequest { PrinterName = "Fake", DocumentName = "Test", Pages = PageSelection.Resolve(PrintScope.AllPages, 1, 1, null, null, PageSubset.All).Pages, PrinterDevMode = devMode };
        var result = await PrintJob.RunAsync(request, new FakePrintSource(A4), factory);
        Assert.True(result.Completed, result.Detail);
        Assert.Same(devMode, factory.Settings!.BaseDevMode);
    }

    [Fact]
    public async Task AJobTheSpoolerRefusesCarriesTheWin32ErrorSoTheSheetCanSayWhy()
    {
        var device = new FakeDevice(new PaperArea(2480, 3508, 300, 300)) { FailAt = "StartDoc" };
        var request = new PrintJobRequest { PrinterName = "Fake", DocumentName = "Test", Pages = PageSelection.Resolve(PrintScope.AllPages, 1, 1, null, null, PageSubset.All).Pages };
        var result = await PrintJob.RunAsync(request, new FakePrintSource(A4), new FakeDeviceFactory(device));
        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(5, result.Win32Error);
        Assert.Equal(PrintFailureKind.AccessDenied, PrintFailure.Classify(result.Win32Error, result.EngineStatus));
    }

    // ---- the printer ----

    [Fact]
    public void MicrosoftPrintToPdfAnswersWithPaperAndNoDuplexAndTheDriversDefaultPaper()
    {
        VirtualPrinter.Require();
        var probe = PrinterProbe.Read(VirtualPrinter.Name);
        Assert.True(probe.Answered);
        Assert.NotEmpty(probe.Abilities.Papers);
        Assert.Contains(probe.Abilities.Papers, p => p.Id == VirtualPrinter.PaperA4);
        Assert.False(probe.Abilities.Duplex);
        PrintEvidence.Note($"Microsoft Print to PDF abilities: colour {probe.Abilities.Colour}, collate {probe.Abilities.Collate}, max copies {probe.Abilities.MaxCopies}, papers {probe.Abilities.Papers.Count}, default paper {probe.DefaultPaperId}");
        Assert.Contains(probe.Abilities.Papers, p => p.Id == probe.DefaultPaperId);
    }

    [Fact]
    public void AnUnknownPrinterIsNotAnErrorItIsASheetWithNothingKnown()
    {
        var probe = PrinterProbe.Read("No such printer " + Guid.NewGuid().ToString("N"));
        Assert.False(probe.Answered);
        Assert.Equal(PrinterAbilities.Unknown, probe.Abilities);
    }

    [Fact]
    public async Task TheRealDeviceReportsTheWholePaperAndItsMargins()
    {
        VirtualPrinter.Require();
        var factory = GdiPrintDeviceFactory.Instance;
        var opened = factory.Open(new PrinterJobSettings(VirtualPrinter.Name, new PrintOptions { PaperSizeId = VirtualPrinter.PaperA4 }, Landscape: false));
        await Task.CompletedTask;
        using (opened.Device)
        {
            var physical = opened.Report.Physical;
            Assert.True(physical.WidthPx >= opened.Device.Paper.WidthPx, $"{physical}");
            Assert.True(physical.HeightPx >= opened.Device.Paper.HeightPx, $"{physical}");
            Assert.True(physical.OffsetX >= 0 && physical.OffsetY >= 0);
            Assert.InRange((double)physical.HeightPx / physical.WidthPx, 1.40, 1.43); // A4 portrait
        }
    }

    // A minimal DEVMODEW that DevMode accepts (it only checks the size fields): 156 bytes, dmSize 156, no private part.
    private static byte[] MakeDevMode()
    {
        var bytes = new byte[156];
        BitConverter.TryWriteBytes(bytes.AsSpan(68), (ushort)156);
        return bytes;
    }
}
