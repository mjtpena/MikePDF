using MikePDF.Core.Reading;

namespace MikePDF.Core.Printing;

/// <summary>Whether a document may be printed now, and under what limit (print.md 3.7, settings-policies <c>PrintingEnabled</c>).</summary>
/// <param name="Allowed">The print command is on.</param>
/// <param name="DisabledReasonKey">The resource key of the reason when it is off (the palette entry, the tooltip, the UIA help text).</param>
/// <param name="LowQualityOnly">Printing is allowed at low quality only: the render resolution is capped at 150 dpi and the sheet shows <see cref="PrintStrings.LowQualityNotice"/>.</param>
public sealed record PrintEligibility(bool Allowed, string? DisabledReasonKey, bool LowQualityOnly)
{
    /// <summary>
    /// The policy comes first (an organisation that turned printing off does not want the document's reason shown instead); then the document's permissions: bit 3 clear is no
    /// printing at all, bit 3 set and bit 12 clear is low quality (ISO 32000-1 table 22). A document with no protection is normal.
    /// </summary>
    public static PrintEligibility Evaluate(bool printingAllowedByPolicy, PdfPermissions permissions)
    {
        if (!printingAllowedByPolicy)
        {
            return new PrintEligibility(false, PrintStrings.DisabledPolicy, false);
        }

        if (!permissions.CanPrint)
        {
            return new PrintEligibility(false, PrintStrings.DisabledOwner, false);
        }

        return new PrintEligibility(true, null, !permissions.CanPrintHighQuality);
    }
}
