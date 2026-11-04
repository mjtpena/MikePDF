using System.Text;

namespace MikePDF.Markup.Fonts;

/// <summary>One laid-out line: its text, the x offset from the left of the inner box, and the baseline's distance below the top of the inner box.</summary>
public sealed record TextLine(string Text, double X, double Baseline, double Width);

/// <summary>The result of <see cref="TextLayout.Layout"/>.</summary>
public sealed record TextLayoutResult(IReadOnlyList<TextLine> Lines, double Height, double MaxLineWidth);

/// <summary>
/// The one text layout of the product (free-text 3.3): the editor's committed view, the overlay and the saved appearance all use it, so they agree.
/// Greedy fit at spaces with Adobe's advance widths, no kerning, no hyphenation; a word longer than the line breaks between characters; explicit
/// line breaks are kept; a tab is four spaces; the line height is 1.2 times the size; the first baseline puts the font's ascender at the top of the box.
/// </summary>
public static class TextLayout
{
    /// <summary>Space between the border and the text, on each side (free-text 3.3).</summary>
    public const double Padding = 2;

    public const double LineHeightFactor = 1.2;

    public const int MaxLines = 200;

    public const int MaxCharacters = 10_000;

    /// <summary>The inner width of a box: its width less the padding and the border on both sides.</summary>
    public static double InnerWidth(double boxWidth, double borderWidth) => boxWidth - (2 * Padding) - (2 * borderWidth);

    /// <summary>The height a box needs for its text: it never shrinks below this (free-text 3.3).</summary>
    public static double MinimumBoxHeight(TextLayoutResult layout, double borderWidth) => layout.Height + (2 * Padding) + (2 * borderWidth);

    public static TextLayoutResult Layout(string text, int fontId, double size, double innerWidth, TextAlign align)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!(size > 0) || double.IsInfinity(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var width = double.IsNaN(innerWidth) ? 0 : Math.Max(innerWidth, 0);
        var ascent = StandardFonts.Ascender(fontId) * size / 1000.0;
        var lineHeight = LineHeightFactor * size;
        var rows = new List<string>();
        foreach (var paragraph in Paragraphs(text))
        {
            Wrap(paragraph, fontId, size, width, rows);
            if (rows.Count > MaxLines)
            {
                rows.RemoveRange(MaxLines, rows.Count - MaxLines);
                break;
            }
        }

        var lines = new List<TextLine>(rows.Count);
        var maxWidth = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            var w = StandardFonts.Measure(rows[i], fontId, size);
            maxWidth = Math.Max(maxWidth, w);
            var x = align switch
            {
                TextAlign.Center => (width - w) / 2,
                TextAlign.Right => width - w,
                _ => 0,
            };
            lines.Add(new TextLine(rows[i], x, ascent + (i * lineHeight), w));
        }

        // The box has to hold the descender of the last line too; a line is 1.2 em tall whatever its glyphs.
        return new TextLayoutResult(lines, rows.Count * lineHeight, maxWidth);
    }

    /// <summary>A typewriter's width: the widest line of its hard-broken text, limited to the room to the page's right edge, plus padding (free-text 3.1).</summary>
    public static double TypewriterWidth(string text, int fontId, double size, double availableWidth)
    {
        var widest = 0.0;
        foreach (var paragraph in Paragraphs(text))
        {
            widest = Math.Max(widest, StandardFonts.Measure(paragraph, fontId, size));
        }

        return Math.Min(widest + (2 * Padding), Math.Max(availableWidth, 2 * Padding));
    }

    /// <summary>The paragraphs of a text: line breaks (CRLF, CR, LF) split, tabs become four spaces.</summary>
    public static IReadOnlyList<string> Paragraphs(string text)
    {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);
        return normalised.Split('\n');
    }

    private static void Wrap(string paragraph, int fontId, double size, double width, List<string> rows)
    {
        if (paragraph.Length == 0)
        {
            rows.Add(string.Empty);
            return;
        }

        var current = new StringBuilder();
        var currentWidth = 0.0;
        var index = 0;
        var firstLine = true;
        while (index < paragraph.Length)
        {
            // A run of spaces, then a word.
            var spaceStart = index;
            while (index < paragraph.Length && paragraph[index] == ' ')
            {
                index++;
            }

            var spaces = paragraph[spaceStart..index];
            var wordStart = index;
            while (index < paragraph.Length && paragraph[index] != ' ')
            {
                index++;
            }

            var word = paragraph[wordStart..index];
            var spacesWidth = StandardFonts.Measure(spaces, fontId, size);
            var wordWidth = StandardFonts.Measure(word, fontId, size);
            if (current.Length == 0)
            {
                // The start of the paragraph keeps its spaces (they are the author's indent); the start of a wrapped line does not.
                var lead = firstLine ? spaces : string.Empty;
                current.Append(lead);
                currentWidth = StandardFonts.Measure(lead, fontId, size);
            }
            else if (currentWidth + spacesWidth + wordWidth <= width + 1e-9)
            {
                current.Append(spaces);
                currentWidth += spacesWidth;
            }
            else
            {
                rows.Add(current.ToString().TrimEnd(' '));
                firstLine = false;
                current.Clear();
                currentWidth = 0;
            }

            if (word.Length == 0)
            {
                continue;
            }

            if (currentWidth + wordWidth <= width + 1e-9 || (current.Length == 0 && wordWidth <= width + 1e-9))
            {
                current.Append(word);
                currentWidth += wordWidth;
                continue;
            }

            // The word does not fit on a line of its own either (or not on what is left): break it between characters.
            foreach (var rune in word.EnumerateRunes())
            {
                var piece = rune.ToString();
                var pieceWidth = StandardFonts.Measure(piece, fontId, size);
                if (current.Length > 0 && currentWidth + pieceWidth > width + 1e-9)
                {
                    rows.Add(current.ToString().TrimEnd(' '));
                    firstLine = false;
                    current.Clear();
                    currentWidth = 0;
                }

                current.Append(piece);
                currentWidth += pieceWidth;
            }
        }

        rows.Add(current.ToString().TrimEnd(' '));
    }
}
