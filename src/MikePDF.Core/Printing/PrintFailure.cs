using MikePDF.Protocol;

namespace MikePDF.Core.Printing;

/// <summary>Why a job stopped, in the few kinds a person can do something about (design 11-print section 5, "Error"). Each has one plain sentence in the resources.</summary>
public enum PrintFailureKind
{
    /// <summary>Something went wrong that none of the others says. The sentence names the printer and offers another.</summary>
    Unknown,

    /// <summary>The printer is off, unplugged, deleted or on a network that is not there: "The printer didn't accept the job."</summary>
    PrinterOffline,

    /// <summary>The printer is there and refused the person.</summary>
    AccessDenied,

    /// <summary>The spooler is not running, or the print service stopped answering.</summary>
    SpoolerUnavailable,

    /// <summary>The page renderer stopped while drawing, three times: the job gave up (print.md 6).</summary>
    EngineFailed,

    /// <summary>A page took too long to draw, twice.</summary>
    PageTooSlow,

    /// <summary>The disk the output goes to is full or not writable (printing to a file).</summary>
    OutputUnwritable,

    /// <summary>The person closed the system's own dialog (the save box of a PDF printer): nothing went wrong, nothing was printed.</summary>
    UserDeclined,
}

/// <summary>Turns what a failed job reports (a Win32 error from the spooler or driver, or the engine's status) into a <see cref="PrintFailureKind"/>, and that into the resource key of its sentence.</summary>
public static class PrintFailure
{
    // Win32 error values (winerror.h). Only the ones a person can act on are listed; everything else is Unknown.
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorBadNetPath = 53;
    private const int ErrorDiskFull = 112;
    private const int ErrorWriteFault = 29;
    private const int ErrorNotFound = 1168;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorInvalidPrinterName = 1801;
    private const int ErrorPrinterDeleted = 1905;
    private const int RpcServerUnavailable = 1722;
    private const int RpcCallFailed = 1726;
    private const int ErrorSpoolerNotRunning = 1062;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorSharingPaused = 70;
    private const int ErrorSessionExpired = 1203;
    private const int ErrorPrintCancelled = 63; // ERROR_PRINT_CANCELLED
    private const int ErrorCancelled = 1223;

    /// <summary>The kind of failure for a Win32 error (0 when the failure was not a Win32 call) and the engine status behind the job.</summary>
    public static PrintFailureKind Classify(int win32Error, Status engineStatus)
    {
        switch (engineStatus)
        {
            case Status.EngineCrashed:
                return PrintFailureKind.EngineFailed;
            case Status.Timeout:
                return PrintFailureKind.PageTooSlow;
            default:
                break;
        }

        return win32Error switch
        {
            ErrorCancelled or ErrorPrintCancelled => PrintFailureKind.UserDeclined,
            ErrorAccessDenied => PrintFailureKind.AccessDenied,
            ErrorDiskFull or ErrorWriteFault => PrintFailureKind.OutputUnwritable,
            RpcServerUnavailable or RpcCallFailed or ErrorSpoolerNotRunning => PrintFailureKind.SpoolerUnavailable,
            ErrorNotReady or ErrorBadNetPath or ErrorDeviceNotConnected or ErrorInvalidPrinterName or ErrorPrinterDeleted or ErrorNotFound
                or ErrorNetworkUnreachable or ErrorNetNameDeleted or ErrorBadNetName or ErrorSharingPaused or ErrorSessionExpired
                => PrintFailureKind.PrinterOffline,
            _ => PrintFailureKind.Unknown,
        };
    }

    /// <summary>The resource key of the sentence for a kind; {0} is the printer's name where the sentence has one.</summary>
    public static string ResourceKey(PrintFailureKind kind) => kind switch
    {
        PrintFailureKind.PrinterOffline => "Print_Error_Offline",
        PrintFailureKind.AccessDenied => "Print_Error_AccessDenied",
        PrintFailureKind.SpoolerUnavailable => "Print_Error_Spooler",
        PrintFailureKind.EngineFailed => "Print_Error_Engine",
        PrintFailureKind.PageTooSlow => "Print_Error_Slow",
        PrintFailureKind.OutputUnwritable => "Print_Error_Output",
        PrintFailureKind.UserDeclined => "Print_Error_Declined",
        _ => "Print_Error_Unknown",
    };
}
