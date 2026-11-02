using MikePDF.Core.Printing;

namespace MikePDF.Printing;

/// <summary>What the print sheet learns about one printer when it is chosen: its abilities and the paper its driver starts with.</summary>
/// <param name="Abilities">Paper sizes, duplex, colour, collate (<see cref="PrinterAbilities.Unknown"/> when the printer did not answer).</param>
/// <param name="DefaultPaperId">The <c>DMPAPER_*</c> id of the driver's default paper, or 0.</param>
/// <param name="Answered">The spooler answered. False for a printer that is offline or removed: the sheet still lets the person choose, and says why a job may fail.</param>
public sealed record PrinterProbe(PrinterAbilities Abilities, int DefaultPaperId, bool Answered)
{
    public static PrinterProbe None { get; } = new(PrinterAbilities.Unknown, 0, false);

    /// <summary>
    /// Asks the spooler and the driver. Can take seconds for a network printer, so call it off the UI thread. Read only: no default printer or queue setting is changed, and the printer's state is not
    /// polled (print.md 6).
    /// </summary>
    public static PrinterProbe Read(string printer)
    {
        ArgumentException.ThrowIfNullOrEmpty(printer);
        try
        {
            var abilities = PrinterCapabilities.Read(printer).Abilities;
            var paper = 0;
            try
            {
                paper = DevMode.GetDefault(printer).PaperSize;
            }
            catch (PrintDeviceException)
            {
                // The capabilities answered and the driver's defaults did not: the sheet falls back to the first paper in the list.
            }

            return new PrinterProbe(abilities, paper, true);
        }
        catch (Exception e) when (e is PrintDeviceException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return None;
        }
    }
}
