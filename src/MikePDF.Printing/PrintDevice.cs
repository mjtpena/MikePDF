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

/// <summary>What the printer's driver did with the settings the job asked for (ADR-0011 decision 2): what it cleared is "not applied" and the sheet says so.</summary>
public sealed record DeviceOpenReport(
    bool PaperApplied,
    bool OrientationApplied,
    bool DuplexApplied,
    bool ColourApplied,
    bool CopiesByDriver,
    bool CollateByDriver,
    string DevModeAfterMerge);

/// <summary>An open device and what is known about it.</summary>
public sealed record DeviceOpenResult(IPrintDevice Device, CopyPlan Copies, DeviceOpenReport Report);

/// <summary>What a job asks the printer for.</summary>
/// <param name="Printer">The queue's name.</param>
/// <param name="Options">The sheet's choices; the device takes copies, collate, duplex, paper size and colour from them.</param>
/// <param name="Landscape">The paper's orientation for the job.</param>
public sealed record PrinterJobSettings(string Printer, PrintOptions Options, bool Landscape);

/// <summary>Opens devices. Real jobs use <see cref="GdiPrintDeviceFactory"/>; tests substitute their own.</summary>
public interface IPrintDeviceFactory
{
    /// <summary>Gets the driver's defaults, applies the settings, lets the driver merge them, decides who makes the copies and creates the device. Throws <see cref="PrintDeviceException"/>.</summary>
    DeviceOpenResult Open(PrinterJobSettings settings);
}
