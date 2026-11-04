using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>Warnings and checks of text boxes that do not block anything (free-text 3.3, 5, 6).</summary>
public static class FreeTextRules
{
    /// <summary>Text against its fill (or white paper without one) below this contrast shows "Low contrast" (free-text 5, FT-17).</summary>
    public const double LowContrastRatio = 3;

    public static bool IsLowContrast(Rgb text, Rgb? fill) => Rgb.Contrast(text, fill ?? Rgb.White) < LowContrastRatio;

    public static bool IsLowContrast(Annotation a) => a.Payload is FreeTextPayload t && IsLowContrast(t.TextColor, a.Interior);

    /// <summary>Whether any of the box lies outside the visible box of the page: "Some text is outside the page." The file is not clipped (FT-18).</summary>
    public static bool IsOutsidePage(Annotation a, PdfRect pageBox) =>
        a.Rect.Left < pageBox.Left - 1e-6 || a.Rect.Bottom < pageBox.Bottom - 1e-6 || a.Rect.Right > pageBox.Right + 1e-6 || a.Rect.Top > pageBox.Top + 1e-6;

    /// <summary>Whether a text fits the limits of a text box: 10,000 characters and 200 lines (free-text 3.5, OQ-FT-5).</summary>
    public static bool WithinLimits(string text) =>
        text.EnumerateRunes().Count() <= TextLayout.MaxCharacters && TextLayout.Paragraphs(text).Count <= TextLayout.MaxLines;

    /// <summary>The user-space width a typewriter box needs for its text, given how far the page reaches to the right of its left edge (free-text 3.1).</summary>
    public static double TypewriterWidth(Annotation a, double roomToTheRight)
    {
        if (a.Payload is not FreeTextPayload t)
        {
            throw new ArgumentException("Not a text box.", nameof(a));
        }

        return TextLayout.TypewriterWidth(a.Contents, StandardFonts.FontId(t.Family, t.Bold, t.Italic), t.Size, roomToTheRight);
    }
}
