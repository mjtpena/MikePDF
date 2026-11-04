using System.Text;

namespace MikePDF.Markup.Fonts;

/// <summary>
/// The 12 base fonts of free text and stamps (free-text 3.2): the three families in regular, bold, italic and bold italic, with Adobe's published widths
/// for WinAnsi codes 32 to 255. Nothing is embedded: every PDF reader has these fonts, which is why only WinAnsi characters can be drawn (free-text 3.4,
/// OQ-FT-1 stays open).
/// </summary>
public static class StandardFonts
{
    public const int Count = 12;

    /// <summary>The names of the fonts' resources in an appearance, which are also the names <c>/DA</c> uses (Acrobat's default resource names).</summary>
    private static readonly string[] ResourceNames = ["Helv", "HeBo", "HeOb", "HeBO", "TiRo", "TiBo", "TiIt", "TiBI", "Cour", "CoBo", "CoOb", "CoBO"];

    /// <summary>The font number for a family and style: the family times four, plus one for bold and two for italic.</summary>
    public static int FontId(FontFamily family, bool bold, bool italic) => ((int)family * 4) + (bold ? 1 : 0) + (italic ? 2 : 0);

    public static FontFamily FamilyOf(int fontId) => (FontFamily)(Check(fontId) / 4);

    public static bool IsBold(int fontId) => (Check(fontId) & 1) != 0;

    public static bool IsItalic(int fontId) => (Check(fontId) & 2) != 0;

    /// <summary>The PDF <c>/BaseFont</c> name.</summary>
    public static string BaseFont(int fontId) => StandardFontData.Names[Check(fontId)];

    /// <summary>The resource name used in <c>/DA</c> and in the appearance's font dictionary.</summary>
    public static string ResourceName(int fontId) => ResourceNames[Check(fontId)];

    /// <summary>The CSS family name for <c>/DS</c>.</summary>
    public static string CssFamily(int fontId) => FamilyOf(fontId) switch
    {
        FontFamily.Sans => "Helvetica",
        FontFamily.Serif => "Times",
        _ => "Courier",
    };

    /// <summary>Ascender in 1/1000 em (where the first baseline sits below the top of the text area).</summary>
    public static double Ascender(int fontId) => StandardFontData.Vertical[Check(fontId)][0];

    /// <summary>Descender in 1/1000 em, negative.</summary>
    public static double Descender(int fontId) => StandardFontData.Vertical[Check(fontId)][1];

    public static double CapHeight(int fontId) => StandardFontData.Vertical[Check(fontId)][2];

    /// <summary>Advance width of a WinAnsi code (32 to 255) in 1/1000 em.</summary>
    public static int Width(int fontId, byte code) => code < 32 ? 0 : StandardFontData.Widths[Check(fontId)][code - 32];

    /// <summary>The width of a string, in points, at a size. Characters outside WinAnsi count as the bullet the file would show.</summary>
    public static double Measure(string text, int fontId, double size)
    {
        ArgumentNullException.ThrowIfNull(text);
        var total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += Width(fontId, WinAnsi.CodeOrBullet(rune));
        }

        return total * size / 1000.0;
    }

    private static int Check(int fontId) => (uint)fontId < Count ? fontId : throw new ArgumentOutOfRangeException(nameof(fontId));
}

/// <summary>WinAnsiEncoding (Windows-1252, ISO 32000-1 annex D.2): which characters can be drawn with the standard 14 fonts.</summary>
public static class WinAnsi
{
    // Windows-1252 for 0x80 to 0x9F; 0 where PDF draws a bullet instead (the five unused codes).
    private static readonly char[] High =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];

    private static readonly Dictionary<int, byte> ReverseHigh = BuildReverse();

    private static Dictionary<int, byte> BuildReverse()
    {
        var map = new Dictionary<int, byte>();
        for (var i = 0; i < High.Length; i++)
        {
            if (High[i] != '\0')
            {
                map[High[i]] = (byte)(0x80 + i);
            }
        }

        return map;
    }

    /// <summary>The WinAnsi code for a character that can be drawn: printable ASCII, the Latin-1 letters and symbols, and the Windows-1252 additions (euro, quotes, dashes, bullet...).</summary>
    public static bool TryGetCode(Rune rune, out byte code)
    {
        var value = rune.Value;
        if (value is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF)
        {
            code = (byte)value;
            return true;
        }

        return ReverseHigh.TryGetValue(value, out code);
    }

    /// <summary>The code to draw, or the bullet code (0x95) for a character the encoding lacks.</summary>
    public static byte CodeOrBullet(Rune rune) => TryGetCode(rune, out var code) ? code : (byte)0x95;

    /// <summary>Whether a character can be typed into a text box. Line breaks and tabs are allowed (the layout handles them); other controls are not.</summary>
    public static bool IsSupported(Rune rune) => rune.Value is '\t' or '\n' or '\r' || TryGetCode(rune, out _);

    /// <summary>
    /// Removes the characters a text box cannot save and counts them (free-text 3.4: typed characters are refused, pasted ones are removed with a
    /// notice). Surrogate pairs and combining marks count as one removed character each. The result is NFC-normalised first so that "é" typed as two
    /// code points is kept.
    /// </summary>
    public static (string Text, int Removed) Filter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalised = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalised.Length);
        var removed = 0;
        foreach (var rune in normalised.EnumerateRunes())
        {
            if (IsSupported(rune))
            {
                builder.Append(rune.ToString());
            }
            else
            {
                removed++;
            }
        }

        return (builder.ToString(), removed);
    }

    /// <summary>The bytes of a string in WinAnsi (unsupported characters as the bullet), for a content stream's string operand.</summary>
    public static byte[] Encode(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            bytes.Add(CodeOrBullet(rune));
        }

        return [.. bytes];
    }

    /// <summary>A PDF literal string for the text, ASCII only: parentheses and backslash escaped, bytes above 126 and below 32 as octal.</summary>
    public static string ToLiteral(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('(');
        foreach (var b in Encode(text))
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    builder.Append('\\').Append((char)b);
                    break;
                case >= 32 and <= 126:
                    builder.Append((char)b);
                    break;
                default:
                    builder.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
            }
        }

        return builder.Append(')').ToString();
    }
}
