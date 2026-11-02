using MikePDF.Core.Printing;

namespace MikePDF.Printing;

/// <summary>
/// A printer to draw on: one document, its pages, and bands of pixels. Calls are made one at a time from the job's device thread, never from the UI thread (<c>EndDoc</c> blocks for
/// seconds on a PDF queue). A real one wraps a GDI device context (<see cref="GdiPrintDevice"/>); tests use a fake that records what it was given.
/// </summary>
public interface IPrintDevice : IDisposable
{
    /// <summary>The printable area of the paper in device pixels and the device's resolution.</summary>
    PaperArea Paper { get; }

    /// <summary>Starts the spool job. <paramref name="outputPath"/> prints to a file (the PDF queues use it so no dialog appears); null prints to the printer.</summary>
    void StartDocument(string documentName, string? outputPath);

    void StartPage();

    /// <summary>
    /// Draws one band: <paramref name="bgra"/> is <paramref name="height"/> rows of <paramref name="stride"/> bytes with <paramref name="width"/> pixels each, drawn at the paper's
    /// (<paramref name="destX"/>, <paramref name="destY"/>) and stretched to <paramref name="destWidth"/> x <paramref name="destHeight"/> when that differs from the bitmap.
    /// </summary>
    void DrawBand(ReadOnlySpan<byte> bgra, int width, int height, int stride, int destX, int destY, int destWidth, int destHeight);

    void EndPage();

    /// <summary>Ends the job normally. The spooler may take a long time to convert it.</summary>
    void EndDocument();

    /// <summary>Terminates the job and discards what was drawn (<c>AbortDoc</c>). Safe to call after nothing was started.</summary>
    void AbortDocument();

    /// <summary>
    /// Asks the device to stop as soon as it can, from any thread: a call that is in progress is cancelled when the spooler next checks (the abort procedure), and the next call fails.
    /// <see cref="AbortDocument"/> is still what ends the job, on the device thread.
    /// </summary>
    void RequestAbort();
}

/// <summary>
/// The whole sheet of paper in device pixels (<c>PHYSICALWIDTH</c>, <c>PHYSICALHEIGHT</c>) and where the printable area starts on it (<c>PHYSICALOFFSETX</c>, <c>PHYSICALOFFSETY</c>): the margins the
/// printer cannot print in. Zero width means the device did not say.
/// </summary>
public readonly record struct PhysicalPaper(int WidthPx, int HeightPx, int OffsetX, int OffsetY);

/// <summary>What the printer's driver did with the settings the job asked for (ADR-0011 decision 2): what it cleared is "not applied" and the sheet says so.</summary>
/// <param name="PaperApplied">The driver kept the paper size asked for (or none was asked for).</param>
/// <param name="OrientationApplied">The driver kept the orientation.</param>
/// <param name="DuplexApplied">The driver kept the sides asked for (or one-sided was).</param>
/// <param name="ColourApplied">The driver kept black and white (or colour was asked for).</param>
/// <param name="CopiesByDriver">The driver makes the copies.</param>
/// <param name="CollateByDriver">The driver collates them.</param>
/// <param name="DevModeAfterMerge">The driver's merged settings in words, for the log.</param>
/// <param name="Physical">The whole paper and the margins, for the preview. Default (all zero) when the device did not say; the printable area is then taken as the whole paper.</param>
public sealed record DeviceOpenReport(
    bool PaperApplied,
    bool OrientationApplied,
    bool DuplexApplied,
    bool ColourApplied,
    bool CopiesByDriver,
    bool CollateByDriver,
    string DevModeAfterMerge,
    PhysicalPaper Physical = default);

/// <summary>An open device and what is known about it.</summary>
public sealed record DeviceOpenResult(IPrintDevice Device, CopyPlan Copies, DeviceOpenReport Report);

/// <summary>What a job asks the printer for.</summary>
/// <param name="Printer">The queue's name.</param>
/// <param name="Options">The sheet's choices; the device takes copies, collate, duplex, paper size and colour from them.</param>
/// <param name="Landscape">The paper's orientation for the job.</param>
/// <param name="BaseDevMode">The settings the person made in the driver's own dialog ("Printer preferences"): tray, quality, finishing, whatever the driver keeps. The job's options are applied on top of
/// them. Null starts from the driver's defaults.</param>
public sealed record PrinterJobSettings(string Printer, PrintOptions Options, bool Landscape, DevMode? BaseDevMode = null);

/// <summary>Opens devices. Real jobs use <see cref="GdiPrintDeviceFactory"/>; tests substitute their own.</summary>
public interface IPrintDeviceFactory
{
    /// <summary>Gets the driver's defaults, applies the settings, lets the driver merge them, decides who makes the copies and creates the device. Throws <see cref="PrintDeviceException"/>.</summary>
    DeviceOpenResult Open(PrinterJobSettings settings);
}
