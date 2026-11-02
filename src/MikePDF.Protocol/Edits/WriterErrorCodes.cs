namespace MikePDF.Protocol.Edits;

/// <summary>
/// The writer host's error codes as they cross the pipe in <c>issueCodes</c>: the values of <c>PdfErrorCode</c> in <c>MikePDF.PdfWriter</c> (a test
/// in the integration project pins each to its enum member). The broker does not reference the parser, so it names the few codes its decisions
/// depend on here; any other code means the strict parser refused the file's structure.
/// </summary>
public static class WriterErrorCodes
{
    public const uint LimitExceeded = 0;
    public const uint UnsupportedEncryption = 1;
    public const uint InvalidEdit = 29;
    public const uint ExpectationMismatch = 30;
    public const uint PageCountMismatch = 31;

    /// <summary>The name the save pipeline decides on: the three edit codes, the two limits, or "Structure".</summary>
    public static string NameOf(uint code) => code switch
    {
        LimitExceeded => "LimitExceeded",
        UnsupportedEncryption => "UnsupportedEncryption",
        InvalidEdit => "InvalidEdit",
        ExpectationMismatch => "ExpectationMismatch",
        PageCountMismatch => "PageCountMismatch",
        _ => "Structure",
    };
}
