using MikePDF.Core.Printing;

namespace MikePDF.Printing;

/// <summary>
/// Opens a real printer (ADR-0011 decision 2): the driver's default DEVMODE, the job's settings applied, the driver's merge as the truth, then who makes the copies, then the device
/// context. Duplex, colour, paper and orientation that the driver cleared are reported, not raised.
/// </summary>
public sealed class GdiPrintDeviceFactory : IPrintDeviceFactory
{
    public static GdiPrintDeviceFactory Instance { get; } = new();

    public DeviceOpenResult Open(PrinterJobSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.Options.Normalize();
        var request = DevMode.GetDefault(settings.Printer);
        if (options.PaperSizeId > 0)
        {
            request.SetPaperSize(options.PaperSizeId);
        }

        request.SetOrientation(settings.Landscape);
        request.SetDuplex(options.Duplex);
        if (options.Grayscale)
        {
            request.SetColour(colour: false);
        }

        request.SetCopies(options.Copies);
        request.SetCollate(options.Collate && options.Copies > 1);

        var merged = DevMode.Merge(settings.Printer, request);
        var wantedDuplex = options.Duplex switch { PrintDuplex.LongEdge => DevMode.DuplexLongEdge, PrintDuplex.ShortEdge => DevMode.DuplexShortEdge, _ => DevMode.DuplexNone };
        var report = new DeviceOpenReport(
            PaperApplied: options.PaperSizeId == 0 || merged.PaperSize == options.PaperSizeId,
            OrientationApplied: !merged.Has(DevMode.FieldOrientation) || merged.Orientation == (settings.Landscape ? DevMode.Landscape : DevMode.Portrait),
            DuplexApplied: options.Duplex == PrintDuplex.None || merged.Duplex == wantedDuplex,
            ColourApplied: !options.Grayscale || merged.ColorMode == DevMode.Monochrome,
            CopiesByDriver: false,
            CollateByDriver: false,
            DevModeAfterMerge: merged.ToString());

        // What the driver kept decides who makes the copies. A driver that says nothing about copies makes none.
        var keepsCopies = merged.Has(DevMode.FieldCopies) && merged.Copies >= options.Copies;
        var keepsCollate = !options.Collate || (merged.Has(DevMode.FieldCollate) && merged.Collate == 1);
        var copies = CopyPlan.Resolve(options.Copies, options.Collate, keepsCopies, keepsCollate);
        var final = merged;
        if (copies.DeviceCopies != merged.Copies || copies.DeviceCollate != (merged.Collate == 1))
        {
            // We repeat what the driver would not: tell it to print once (or with the plan's numbers), and merge again so the DEVMODE it gets is one it produced.
            var again = merged.Clone();
            again.SetCopies(copies.DeviceCopies);
            again.SetCollate(copies.DeviceCollate);
            final = DevMode.Merge(settings.Printer, again);
        }

        var device = GdiPrintDevice.Create(settings.Printer, final);
        return new DeviceOpenResult(device, copies, report with { CopiesByDriver = copies.DeviceCopies > 1, CollateByDriver = copies.DeviceCollate });
    }
}
