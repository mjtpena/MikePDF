using MikePDF.Core.Printing;
using MikePDF.Printing;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// The spooler side (ADR-0011 decision 2): the printers, what a driver says it can do, and what it does to a DEVMODE it is asked to merge. Against the real Microsoft Print to PDF queue; skipped,
/// with the reason, on a machine that does not have it. Read-only: nothing here adds a printer, changes a queue's settings or touches the default printer.
/// </summary>
public sealed class PrinterTests
{
    [Fact]
    public void ThePrintersAreListedWithTheDefaultFirstAndTheDefaultIsOnlyRead()
    {
        VirtualPrinter.Require();
        var before = PrinterCatalog.GetDefaultName();
        var printers = PrinterCatalog.Enumerate();

        Assert.Contains(printers, p => string.Equals(p.Name, VirtualPrinter.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(printers.Count, printers.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(printers.Count(p => p.IsDefault) <= 1);
        if (before is not null)
        {
            Assert.True(printers[0].IsDefault);
            Assert.Equal(before, printers[0].Name, ignoreCase: true);
        }

        Assert.Equal(before, PrinterCatalog.GetDefaultName()); // nothing above changed it
        Assert.False(PrinterCatalog.Exists("No such printer " + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ThePrintToPdfQueueReportsPaperSizesAndResolutionAndNoDuplex()
    {
        VirtualPrinter.Require();
        var capabilities = PrinterCapabilities.Read(VirtualPrinter.Name);

        var a4 = Assert.Single(capabilities.PaperSizes, p => p.Id == VirtualPrinter.PaperA4);
        Assert.InRange(a4.WidthTenthsMm, 2090, 2110);
        Assert.InRange(a4.HeightTenthsMm, 2960, 2980);
        Assert.False(string.IsNullOrWhiteSpace(a4.Name));
        var letter = Assert.Single(capabilities.PaperSizes, p => p.Id == VirtualPrinter.PaperLetter);
        Assert.InRange(letter.WidthTenthsMm, 2150, 2170);
        Assert.Contains((600, 600), capabilities.Resolutions); // S12: the queue offers 600 x 600 only
        Assert.False(capabilities.SupportsDuplex); // a PDF queue has no duplex: the sheet hides the option
        Assert.True(capabilities.MaxCopies >= 1);
        Assert.Throws<ArgumentException>(() => PrinterCapabilities.Read(string.Empty));
    }

    [Fact]
    public void AnUnknownPrinterIsAnErrorNotACrash()
    {
        VirtualPrinter.Require();
        Assert.Throws<PrintDeviceException>(() => DevMode.GetDefault("No such printer " + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void TheDriversMergedDevModeIsTheTruth()
    {
        VirtualPrinter.Require();
        var request = DevMode.GetDefault(VirtualPrinter.Name);
        Assert.True(request.Bytes.Length >= 156);
        request.SetPaperSize(VirtualPrinter.PaperA4);
        request.SetOrientation(landscape: true);
        request.SetCopies(2);
        request.SetCollate(true);
        request.SetDuplex(PrintDuplex.LongEdge);
        var asked = request.Clone();

        var merged = DevMode.Merge(VirtualPrinter.Name, request);

        Assert.Equal(VirtualPrinter.PaperA4, merged.PaperSize); // kept
        Assert.Equal(DevMode.Landscape, merged.Orientation); // kept
        Assert.Equal(2, merged.Copies); // kept, although DC_COPIES says 1 (S12)
        Assert.NotEqual(DevMode.DuplexLongEdge, merged.Duplex); // cleared: the queue has no duplex. Not an error, "not applied"
        Assert.Equal(DevMode.DuplexLongEdge, asked.Duplex); // and the request object was not changed by the merge
        Assert.NotSame(request, merged);
    }

    [Fact]
    public void ADevModeIsEditedAtTheRightOffsetsAndRejectsGarbage()
    {
        VirtualPrinter.Require();
        var mode = DevMode.GetDefault(VirtualPrinter.Name);
        mode.SetPaperSize(1);
        mode.SetCopies(500); // clamped to 99
        mode.SetColour(colour: false);
        Assert.Equal(1, mode.PaperSize);
        Assert.Equal(99, mode.Copies);
        Assert.Equal(DevMode.Monochrome, mode.ColorMode);
        Assert.True(mode.Has(DevMode.FieldPaperSize | DevMode.FieldCopies | DevMode.FieldColor));
        // The copy the driver gets back from a merge of this one agrees with what was written.
        var merged = DevMode.Merge(VirtualPrinter.Name, mode);
        Assert.Equal(1, merged.PaperSize);

        Assert.Throws<ArgumentException>(() => new DevMode(new byte[10]));
        var lying = new byte[200];
        BitConverter.TryWriteBytes(lying.AsSpan(68), (ushort)200);
        BitConverter.TryWriteBytes(lying.AsSpan(70), (ushort)60000); // the private part claims to be larger than the buffer
        Assert.Throws<ArgumentException>(() => new DevMode(lying));
    }

    [Fact]
    public void TheFactoryAppliesTheSettingsAndReportsWhatTheDriverClearedAndWhoMakesTheCopies()
    {
        VirtualPrinter.Require();
        var options = new PrintOptions { PaperSizeId = VirtualPrinter.PaperA4, Duplex = PrintDuplex.LongEdge, Copies = 3, Collate = true, Grayscale = true };
        var opened = GdiPrintDeviceFactory.Instance.Open(new PrinterJobSettings(VirtualPrinter.Name, options, Landscape: false));
        using var device = opened.Device;

        Assert.True(opened.Report.PaperApplied);
        Assert.True(opened.Report.OrientationApplied);
        Assert.False(opened.Report.DuplexApplied, "the PDF queue clears duplex and the sheet is told");
        Assert.Equal(3, opened.Copies.Multiplier); // however it is made, a copy count of 3 is 3
        Assert.True(device.Paper.WidthPx > 1000 && device.Paper.HeightPx > device.Paper.WidthPx, "portrait A4");
        Assert.Equal(600, device.Paper.DpiX);
        Assert.InRange(device.Paper.WidthPx * 72.0 / device.Paper.DpiX, 590, 600); // A4 is 595 points wide
    }
}
