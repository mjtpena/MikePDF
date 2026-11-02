namespace MikePDF.Core.Printing;

/// <summary>
/// The resource keys of print.md section 12 that the pipeline decides between. The text lives in the app's .resw files (the print sheet adds them); this is the key and,
/// where the text has a placeholder, what fills it.
/// </summary>
public static class PrintStrings
{
    public const string RangeErrorSyntax = "Print_Range_Error_Syntax";
    public const string RangeErrorLimit = "Print_Range_Error_Limit";
    public const string RangeErrorOrder = "Print_Range_Error_Order";

    /// <summary>"No pages to print": a selection or an odd/even subset that leaves nothing. Not in print.md 12 (it only had the three range errors); the sheet adds the string.</summary>
    public const string RangeErrorNone = "Print_Range_Error_None";

    public const string CutOffNotice = "Print_CutOff_Notice";
    public const string LowQualityNotice = "Print_LowQuality_Notice";
    public const string DisabledOwner = "Print_Disabled_Owner";
    public const string DisabledPolicy = "Print_Disabled_Policy";
    public const string MissingContent = "Print_Missing_Content";
    public const string Failed = "Print_Failed";

    public static string ForRangeError(PageRangeError error) => error switch
    {
        PageRangeError.OutOfRange => RangeErrorLimit,
        PageRangeError.OutOfOrder => RangeErrorOrder,
        PageRangeError.NoPages => RangeErrorNone,
        _ => RangeErrorSyntax,
    };
}
