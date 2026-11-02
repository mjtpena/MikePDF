using MikePDF.Core.Printing;
using MikePDF.Protocol;

namespace MikePDF.Printing;

/// <summary>What a job asks for. The page set comes from <see cref="PageSelection.Resolve"/>, the options from the print sheet.</summary>
public sealed record PrintJobRequest
{
    public required string PrinterName { get; init; }

    /// <summary>The spool job's name: the document title or file name. Windows shows it in the queue (print.md 7).</summary>
    public required string DocumentName { get; init; }

    public required PageSet Pages { get; init; }

    public PrintOptions Options { get; init; } = new();

    /// <summary>The <c>PrintingEnabled</c> policy as read when the job starts. The pipeline refuses to print when it is false.</summary>
    public bool PrintingAllowedByPolicy { get; init; } = true;

    /// <summary>Print to this file instead of to the printer (<c>DOCINFO.lpszOutput</c>): how Microsoft Print to PDF is driven without a dialog. An empty file left by a cancel is removed.</summary>
    public string? OutputPath { get; init; }

    /// <summary>The deadline of one <c>RenderBand</c> (print.md 6: 30 seconds).</summary>
    public TimeSpan BandDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The job fails when the engine has crashed this many times during it (print.md 6: "after 3 restarts").</summary>
    public int MaxEngineCrashes { get; init; } = 3;
}

public enum PrintStage
{
    /// <summary>A sheet is about to be drawn.</summary>
    Sheet,

    /// <summary>All sheets are sent; the spooler is converting the job (<c>EndDoc</c>).</summary>
    Finishing,
}

/// <summary>Progress, once per sheet (print.md 3.8: "Printing page {n} of {total}"), not per band.</summary>
/// <param name="Stage">Drawing a sheet, or waiting for the spooler to finish.</param>
/// <param name="Sheet">The sheet being drawn, 1-based, counting every copy MikePDF makes itself.</param>
/// <param name="TotalSheets">The sheets in the job.</param>
/// <param name="FirstPage">The first document page on the sheet.</param>
public readonly record struct PrintProgress(PrintStage Stage, int Sheet, int TotalSheets, int FirstPage);

public enum PrintOutcome
{
    Completed,
    Cancelled,
    Failed,

    /// <summary>The policy or the document's owner does not allow printing; nothing was sent to the printer.</summary>
    Disabled,
}

/// <summary>How a job ended and what the sheet should tell the person.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ResourceKey">The string to show: <c>Print_Disabled_Policy</c>, <c>Print_Disabled_Owner</c> or <c>Print_Failed</c> (with <paramref name="Detail"/> as {0}); null when it completed.</param>
/// <param name="Detail">The reason in words for the log and for {0}.</param>
/// <param name="EngineStatus">The engine status that ended the job, or <c>Ok</c>.</param>
/// <param name="SheetsPrinted">Sheets that were completed (ended with <c>EndPage</c>).</param>
/// <param name="TotalSheets">The sheets in the job, counting the copies MikePDF makes itself.</param>
/// <param name="PagesWithMissingContent">Pages the engine could only draw in part (print.md 6, <c>Print_Missing_Content</c>).</param>
/// <param name="FormLayerUnavailable">The engine could not draw the form layer, so form fields were printed as the page shows them without it.</param>
/// <param name="EngineCrashes">How many times the engine died during the job and the job went on.</param>
/// <param name="Device">What the driver did with the settings; null when the printer was never opened.</param>
/// <param name="Copies">Who made the copies: the driver, or MikePDF.</param>
/// <param name="BandsSent">Bands drawn on the printer.</param>
public sealed record PrintResult(
    PrintOutcome Outcome,
    string? ResourceKey,
    string? Detail,
    Status EngineStatus,
    int SheetsPrinted,
    int TotalSheets,
    IReadOnlyList<int> PagesWithMissingContent,
    bool FormLayerUnavailable,
    int EngineCrashes,
    DeviceOpenReport? Device,
    CopyPlan Copies,
    int BandsSent)
{
    public bool Completed => Outcome == PrintOutcome.Completed;
}

/// <summary>What the sheet needs before the person presses Print.</summary>
/// <param name="Eligibility">Whether printing is allowed and at what quality (policy first, then the document's permissions).</param>
/// <param name="TotalSheets">Sheets of paper sides the job would print, copies made by MikePDF included.</param>
/// <param name="CutOffPages">Pages that are larger than the printable area and would be cut off (the sheet shows <c>Print_CutOff_Notice</c> and asks).</param>
/// <param name="Paper">The printable area the printer reported.</param>
/// <param name="Device">What the driver did with the settings (what it cleared is shown as not applied).</param>
/// <param name="EngineStatus">The engine status that stopped the preflight, or <c>Ok</c>.</param>
/// <param name="Detail">The reason in words when it did not complete.</param>
public sealed record PrintPreflight(PrintEligibility Eligibility, int TotalSheets, IReadOnlyList<int> CutOffPages, MikePDF.Core.Printing.PaperArea Paper, DeviceOpenReport? Device, Status EngineStatus, string? Detail);
