using System.Globalization;
using System.Text.RegularExpressions;

namespace MikePDF.Core.Reading;

/// <summary>The things a PDF's /P entry can allow or deny, in the order the properties dialog lists them.</summary>
public enum PdfPermissionKind
{
    Print,
    PrintHighQuality,
    Copy,
    Modify,
    Annotate,
    FillForms,
    Assemble,
}

/// <summary>
/// What the document's owner allows (the /P entry of the standard security handler, ISO 32000 table 22). <see cref="Bits"/> is the value
/// PDFium reports for this session: everything after the owner password, or for a document with no protection; otherwise the file's /P.
/// Extraction for accessibility (bit 10) is not a permission here: PDF 2.0 deprecated it and screen readers are always served.
/// </summary>
public readonly record struct PdfPermissions(uint Bits, int SecurityRevision)
{
    /// <summary>A document with no protection.</summary>
    public static PdfPermissions Unrestricted { get; } = new(uint.MaxValue, 0);

    public bool IsEncrypted => SecurityRevision > 0;

    public bool CanPrint => Allows(0x4);

    public bool CanPrintHighQuality => Allows(0x800);

    public bool CanCopy => Allows(0x10);

    public bool CanModify => Allows(0x8);

    public bool CanAnnotate => Allows(0x20);

    public bool CanFillForms => Allows(0x100);

    public bool CanAssemble => Allows(0x400);

    /// <summary>Whether <paramref name="kind"/> is allowed.</summary>
    public bool Allows(PdfPermissionKind kind) => kind switch
    {
        PdfPermissionKind.Print => CanPrint,
        PdfPermissionKind.PrintHighQuality => CanPrintHighQuality,
        PdfPermissionKind.Copy => CanCopy,
        PdfPermissionKind.Modify => CanModify,
        PdfPermissionKind.Annotate => CanAnnotate,
        PdfPermissionKind.FillForms => CanFillForms,
        PdfPermissionKind.Assemble => CanAssemble,
        _ => false,
    };

    /// <summary>
    /// The permissions the document's security handler defines. Revision 2 defines print, modify, copy and annotate only; the others are
    /// not the file's to give, so they are not listed (and are not denied).
    /// </summary>
    public IReadOnlyList<PdfPermissionKind> Defined => SecurityRevision == 2
        ? [PdfPermissionKind.Print, PdfPermissionKind.Modify, PdfPermissionKind.Copy, PdfPermissionKind.Annotate]
        : [PdfPermissionKind.Print, PdfPermissionKind.PrintHighQuality, PdfPermissionKind.Copy, PdfPermissionKind.Modify, PdfPermissionKind.Annotate, PdfPermissionKind.FillForms, PdfPermissionKind.Assemble];

    private bool Allows(uint bit) => !IsEncrypted || (Bits & bit) != 0 || (SecurityRevision == 2 && bit is 0x800 or 0x100 or 0x400);
}

/// <summary>A font the document draws with. <see cref="Name"/> has the subset prefix (<c>ABCDEF+</c>) removed.</summary>
public sealed record DocumentFont(string Name, bool IsEmbedded, bool IsSubset)
{
    private static readonly Regex SubsetPrefix = new("^[A-Z]{6}\\+", RegexOptions.CultureInvariant | RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static DocumentFont From(string baseName, bool embedded)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        var subset = SubsetPrefix.IsMatch(baseName);
        return new DocumentFont(subset ? baseName[7..] : baseName, embedded, subset);
    }
}

public enum DocumentFormType
{
    None = 0,
    AcroForm = 1,
    Xfa = 2,
    XfaForeground = 3,
}

/// <summary>
/// Everything the properties dialog shows that comes from the document (the file size and the page size are the window's to add). Text has been
/// cleaned for display. <see cref="DetailsAvailable"/> is false when the engine could not answer the second question (dates, security, fonts): the
/// rest is still good.
/// </summary>
public sealed record DocumentProperties(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    string PdfVersion,
    int PageCount,
    bool IsTagged,
    bool HasJavaScript,
    int SignatureCount,
    PdfPermissions Permissions,
    bool DetailsAvailable,
    string CreatedText,
    DateTimeOffset? Created,
    string ModifiedText,
    DateTimeOffset? Modified,
    DocumentFormType FormType,
    uint UserPermissionBits,
    IReadOnlyList<DocumentFont> Fonts,
    bool FontsComplete,
    int FontPagesScanned)
{
    /// <summary>The session has more rights than an ordinary user of the file: it was opened with the owner password.</summary>
    public bool OpenedWithOwnerPassword => DetailsAvailable && Permissions.IsEncrypted && Permissions.Bits != UserPermissionBits;
}

/// <summary>Dates as PDF writes them: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, every part after the year optional (ISO 32000, 7.9.4).</summary>
public static class PdfDate
{
    /// <summary>Parses a PDF date. A date with no zone is taken as UTC. Never throws; false for anything that is not a date.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        var s = text.AsSpan().Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var at = 0;
        if (!Digits(s, ref at, 4, out var year) || year < 1)
        {
            return false;
        }

        int month = 1, day = 1, hour = 0, minute = 0, second = 0;
        if (!OptionalField(s, ref at, 1, ref month) || !OptionalField(s, ref at, 1, ref day) || !OptionalField(s, ref at, 0, ref hour)
            || !OptionalField(s, ref at, 0, ref minute) || !OptionalField(s, ref at, 0, ref second))
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        if (!TryReadZone(s, ref at, out var offset))
        {
            return false;
        }

        try
        {
            value = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // a time that does not exist at the very ends of the calendar with this offset
        }
    }

    // Nothing, "Z" (which the spec lets be followed by 00'00'), or a sign with hours and optional minutes: +10'00', +10'00, +1000, +10.
    private static bool TryReadZone(ReadOnlySpan<char> s, ref int at, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (at >= s.Length)
        {
            return true;
        }

        var sign = s[at++];
        if (sign is 'Z' or 'z')
        {
            return s.Length - at <= 8 && AllDigitsOrQuotes(s[at..]);
        }

        if (sign is not ('+' or '-') || !Digits(s, ref at, 2, out var hours))
        {
            return false;
        }

        var minutes = 0;
        if (at < s.Length && s[at] == '\'')
        {
            at++;
        }

        if (at < s.Length && char.IsAsciiDigit(s[at]) && !Digits(s, ref at, 2, out minutes))
        {
            return false;
        }

        if (at < s.Length && s[at] == '\'')
        {
            at++;
        }

        if (at != s.Length || hours > 23 || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (sign == '-')
        {
            offset = -offset;
        }

        return true;
    }

    private static bool AllDigitsOrQuotes(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c) && c != '\'')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsZoneStart(char c) => c is 'Z' or 'z' or '+' or '-';

    private static bool Digits(ReadOnlySpan<char> s, ref int at, int count, out int value)
    {
        value = 0;
        if (at + count > s.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!char.IsAsciiDigit(s[at + i]))
            {
                return false;
            }
        }

        value = int.Parse(s.Slice(at, count), NumberStyles.None, CultureInfo.InvariantCulture);
        at += count;
        return true;
    }

    // A field that may be missing (the date stops, or goes straight to its zone); a present field must be two digits.
    private static bool OptionalField(ReadOnlySpan<char> s, ref int at, int missing, ref int value)
    {
        if (at >= s.Length || IsZoneStart(s[at]))
        {
            value = missing;
            return true;
        }

        return Digits(s, ref at, 2, out value);
    }
}

/// <summary>The standard paper sizes, so a page of 595 x 842 points is called A4.</summary>
public static class PaperSizes
{
    private static readonly (string Name, double Short, double Long)[] Known =
    [
        ("A3", 841.89, 1190.55),
        ("A4", 595.28, 841.89),
        ("A5", 419.53, 595.28),
        ("Letter", 612, 792),
        ("Legal", 612, 1008),
        ("Tabloid", 792, 1224),
    ];

    /// <summary>The name of the paper size a page is, within a point, in either orientation; null when it is none of the standard ones.</summary>
    public static string? Name(double widthPt, double heightPt)
    {
        var shortSide = Math.Min(widthPt, heightPt);
        var longSide = Math.Max(widthPt, heightPt);
        foreach (var (name, knownShort, knownLong) in Known)
        {
            if (Math.Abs(shortSide - knownShort) <= 1.0 && Math.Abs(longSide - knownLong) <= 1.0)
            {
                return name;
            }
        }

        return null;
    }
}
