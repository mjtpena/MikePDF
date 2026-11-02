using System.Globalization;
using System.Text;
using MikePDF.Protocol;

namespace MikePDF.Broker;

/// <summary>
/// The semantic checks on the engine's replies that the schema cannot express (A.9): the decoder has already enforced every length, count and range;
/// these relate a reply to the request it answers and to the document. Pure functions, so tests can feed them anything.
/// </summary>
internal static class ReplyChecks
{
    private const int MaxMatchEnd = 200_000; // a page's text is cut at this many characters (GetPageTextReply.total)

    /// <summary>
    /// A window of <paramref name="length"/> entries must be exactly what was asked for from a list of <paramref name="total"/>: the engine
    /// returns every entry from <paramref name="first"/> on, up to <paramref name="count"/>. A start past the end gives an empty window when
    /// <paramref name="allowStartPastEnd"/> (outline and links); text refuses it with a status instead.
    /// </summary>
    public static bool WindowFits(uint first, uint count, uint total, int length, bool allowStartPastEnd)
    {
        if (first >= total)
        {
            return length == 0 && (allowStartPastEnd || first == total);
        }

        return (ulong)length == Math.Min(count, total - first);
    }

    /// <summary>Kinds 1 to 3 name a page that must exist; the other kinds name none. Returns the problem, or null.</summary>
    public static string? CheckDestinations(ushort[] kinds, uint[] pages, uint pageCount)
    {
        for (var i = 0; i < kinds.Length; i++)
        {
            var namesAPage = kinds[i] is >= 1 and <= 3;
            if (namesAPage ? pages[i] >= pageCount : pages[i] != 0)
            {
                return "a destination names a page the document does not have";
            }
        }

        return null;
    }

    /// <summary>
    /// The font list of GetDocumentDetails: the schema has bounded its length and its flags, and made the two arrays the same length; what is
    /// left is that a font name is printable ASCII (the engine replaces everything else), that no name is empty, and that a reply which says it
    /// walked every page names exactly the document's page count (the engine only claims that for a document short enough to be walked whole).
    /// </summary>
    public static string? CheckDetails(GetDocumentDetailsReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(reply);
        foreach (var name in reply.FontNames)
        {
            if (name.Length == 0 || name.Any(c => c < ' ' || c > '~'))
            {
                return "a font name holds characters a font name never has";
            }
        }

        if (reply.FontPagesScanned > pageCount)
        {
            return "the engine walked more pages than the document has";
        }

        return reply.FontsComplete && reply.FontPagesScanned != pageCount ? "the engine says it walked every page but names another number" : null;
    }

    /// <summary>
    /// The print page ranges of GetViewerPreferences: the schema has bounded the counts and the values and made the arrays the same length; what is left is that
    /// every range is ordered and lies inside the document (the engine drops the ones that do not). Returns the problem, or null.
    /// </summary>
    public static string? CheckPreferences(GetViewerPreferencesReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(reply);
        for (var i = 0; i < reply.RangeFirsts.Length; i++)
        {
            if (reply.RangeFirsts[i] > reply.RangeLasts[i] || reply.RangeLasts[i] >= pageCount)
            {
                return "a print page range is reversed or names a page the document does not have";
            }
        }

        return null;
    }

    /// <summary>Matches arrive in order, never overlapping the end of the text. Returns the problem, or null.</summary>
    public static string? CheckMatches(uint[] starts, uint[] lengths)
    {
        for (var i = 0; i < starts.Length; i++)
        {
            if ((ulong)starts[i] + lengths[i] > MaxMatchEnd)
            {
                return "a search match runs past the end of any page's text";
            }

            if (i > 0 && starts[i] <= starts[i - 1])
            {
                return "search matches are not in order";
            }
        }

        return null;
    }

    /// <summary>
    /// An address the person will be shown and asked about. Control characters, format characters (which include the ones that reverse the
    /// direction of text), separators and unassigned or private code points never belong in one: the engine already refuses to send them, so
    /// finding one here means the engine is not what it should be.
    /// </summary>
    public static bool IsPlainAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        foreach (var rune in address.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.Surrogate:
                    return false;
                default:
                    break;
            }
        }

        return true;
    }
}
