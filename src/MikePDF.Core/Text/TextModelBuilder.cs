using System.Text;
using MikePDF.Core.Geometry;

namespace MikePDF.Core.Text;

/// <summary>
/// Turns the characters of a page (<see cref="PageCharacters"/>) into a reading-ordered <see cref="TextModel"/>. PDFium's own order is the
/// order of the content stream, which is not reading order (S7: a two-column page comes out row by row); only the boxes and the Unicode are
/// trusted, and the order is worked out from the boxes.
/// <para>
/// The order (the S7 heuristic, validated on a synthetic two-column page and an invariant sweep, not yet on a real corpus): a recursive XY-cut
/// on the glyph boxes in page space (the page's /Rotate applied, so a rotated page is read the way it is seen). Find the widest empty vertical
/// band of at least 1.0 em that no glyph crosses and cut there (left, then right); otherwise the widest empty horizontal band of at least half
/// an em (or 1.5 times the usual gap between lines of the region, up to three em) and cut there (top, then bottom); otherwise the region is one
/// block: cluster its glyphs into lines by vertical centre, order each line left to right, and start a new paragraph where the pitch between
/// lines is over 1.45 times the median.
/// </para>
/// <para>
/// Not handled: the structure tree of a tagged file (the engine does not send it yet), right-to-left and vertical writing, tables (read in the
/// order the cuts produce, not row by row), text turned within an upright page.
/// </para>
/// </summary>
public static class TextModelBuilder
{
    private const int MaxCutDepth = 24;

    private sealed class Glyph
    {
        public required int Source { get; init; }

        public required string Text { get; init; }

        public required RectD Rect { get; set; }

        public bool IsHyphen { get; init; }

        public bool IsSpace => Text.Length > 0 && Text.All(char.IsWhiteSpace);

        public double Em => Math.Max(1, Rect.Height / 1.12);

        public double CenterY => Rect.Y + (Rect.Height / 2);
    }

    private sealed class Paragraph
    {
        public List<List<Glyph>> Lines { get; } = [];
    }

    public static TextModel Build(PageCharacters characters, PageGeometry geometry, int page)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(geometry);
        var glyphs = CollectGlyphs(characters, geometry);
        var paragraphs = GroupIntoParagraphs(glyphs);
        return Assemble(paragraphs, page);
    }

    // ---- glyphs --------------------------------------------------------------------------------------------------------------

    private static List<Glyph> CollectGlyphs(PageCharacters characters, PageGeometry geometry)
    {
        var size = geometry.Size;
        var list = new List<Glyph>();
        for (var i = 0; i < characters.Count; i++)
        {
            var flags = characters.Flags[i];
            var unit = characters.Units[i];
            var generated = (flags & PageCharacters.Generated) != 0;
            var text = char.ConvertFromUtf32(unit is >= 0xD800 and <= 0xDFFF ? 0xFFFD : unit);

            // A surrogate pair is one text element: the next entry is its other half (PDFium gives both the same box).
            if (unit is >= 0xD800 and <= 0xDBFF && i + 1 < characters.Count && characters.Units[i + 1] is >= 0xDC00 and <= 0xDFFF && !generated)
            {
                text = new string([(char)unit, (char)characters.Units[i + 1]]);
                i++;
            }

            if (generated && text != " ")
            {
                continue; // PDFium's made-up CR and LF: the model adds its own separators
            }

            var rect = geometry.PdfToPage(characters.BoxOf(i));
            if ((flags & PageCharacters.Hyphen) != 0)
            {
                list.Add(new Glyph { Source = i, Text = string.Empty, Rect = rect, IsHyphen = true });
                continue;
            }

            if (text.Length == 0 || (text[0] < 0x20 && text[0] != '\t') || text is "­" or "​" or "﻿")
            {
                continue;
            }

            var centerX = rect.X + (rect.Width / 2);
            var centerY = rect.Y + (rect.Height / 2);
            // PDFium also returns text outside the visible box.
            if (centerX < -1 || centerX > size.Width + 1 || centerY < -1 || centerY > size.Height + 1)
            {
                continue;
            }

            foreach (var piece in ExpandLigature(text))
            {
                list.Add(new Glyph { Source = i, Text = piece, Rect = rect });
            }
        }

        return list;
    }

    // Alphabetic Presentation Forms U+FB00..FB06 only: a blanket NFKC would also turn superscripts and full-width forms into other characters,
    // which changes what a screen reader says.
    private static IEnumerable<string> ExpandLigature(string text)
    {
        if (text.Length == 1 && text[0] is >= 'ﬀ' and <= 'ﬆ')
        {
            foreach (var c in text.Normalize(NormalizationForm.FormKC))
            {
                yield return c.ToString();
            }
        }
        else
        {
            yield return text;
        }
    }

    // ---- reading order -------------------------------------------------------------------------------------------------------

    private static List<Paragraph> GroupIntoParagraphs(List<Glyph> glyphs)
    {
        var leaves = new List<List<Glyph>>();
        Cut(glyphs, leaves, 0);
        var paragraphs = new List<Paragraph>();
        foreach (var leaf in leaves)
        {
            var lines = ClusterLines(leaf);
            if (lines.Count == 0)
            {
                continue;
            }

            var centres = lines.Select(l => l.Where(g => g.Rect.Height > 0).Select(g => g.CenterY).DefaultIfEmpty(0).Average()).ToList();
            var pitches = new List<double>();
            for (var i = 1; i < centres.Count; i++)
            {
                pitches.Add(centres[i] - centres[i - 1]);
            }

            var median = pitches.Count == 0 ? 0 : Median(pitches);
            var paragraph = new Paragraph();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0 && median > 0 && pitches[i - 1] > 1.45 * median)
                {
                    paragraphs.Add(paragraph);
                    paragraph = new Paragraph();
                }

                paragraph.Lines.Add(lines[i]);
            }

            paragraphs.Add(paragraph);
        }

        return paragraphs;
    }

    private static void Cut(List<Glyph> glyphs, List<List<Glyph>> leaves, int depth)
    {
        if (glyphs.Count == 0)
        {
            return;
        }

        if (glyphs.Count < 2 || depth > MaxCutDepth)
        {
            leaves.Add(glyphs);
            return;
        }

        var em = Median(glyphs.Select(g => g.Em).ToList());
        var solid = glyphs.Where(g => !g.IsSpace && g.Rect.Width > 0).ToList();
        if (solid.Count < 2)
        {
            leaves.Add(glyphs);
            return;
        }

        if (FindGap(solid, vertical: true, minimumGap: em) is { } x)
        {
            Split(glyphs, g => g.Rect.X + (g.Rect.Width / 2) < x, out var left, out var right);
            if (left.Count > 0 && right.Count > 0)
            {
                Cut(left, leaves, depth + 1);
                Cut(right, leaves, depth + 1);
                return;
            }
        }

        if (FindGap(solid, vertical: false, minimumGap: HorizontalCutThreshold(solid, em)) is { } y)
        {
            Split(glyphs, g => g.CenterY < y, out var top, out var bottom);
            if (top.Count > 0 && bottom.Count > 0)
            {
                Cut(top, leaves, depth + 1);
                Cut(bottom, leaves, depth + 1);
                return;
            }
        }

        leaves.Add(glyphs);
    }

    // A horizontal band only counts as a cut when it is clearly larger than the ordinary gap between lines of this region (1.5 times the
    // median gap), but never smaller than half an em, and a gap of three em always counts. That keeps generously leaded text from being
    // sliced line by line.
    private static double HorizontalCutThreshold(List<Glyph> solid, double em)
    {
        var lines = ClusterLines(solid);
        if (lines.Count < 2)
        {
            return 0.5 * em;
        }

        var spans = lines.Select(l => (Top: l.Min(g => g.Rect.Y), Bottom: l.Max(g => g.Rect.Bottom))).OrderBy(s => s.Top).ToList();
        var gaps = new List<double>();
        var maxBottom = spans[0].Bottom;
        for (var i = 1; i < spans.Count; i++)
        {
            gaps.Add(Math.Max(0, spans[i].Top - maxBottom));
            maxBottom = Math.Max(maxBottom, spans[i].Bottom);
        }

        return Math.Max(0.5 * em, Math.Min(1.5 * Median(gaps), 3 * em));
    }

    private static void Split(List<Glyph> glyphs, Func<Glyph, bool> first, out List<Glyph> a, out List<Glyph> b)
    {
        a = [];
        b = [];
        foreach (var glyph in glyphs)
        {
            (first(glyph) ? a : b).Add(glyph);
        }
    }

    /// <summary>The widest empty band along one axis that is at least <paramref name="minimumGap"/> wide; returns the cut coordinate (the band's centre) or null.</summary>
    private static double? FindGap(List<Glyph> glyphs, bool vertical, double minimumGap)
    {
        var items = glyphs.Select(g => vertical ? (Start: g.Rect.X, End: g.Rect.Right) : (Start: g.Rect.Y, End: g.Rect.Bottom)).OrderBy(i => i.Start).ToList();
        var maxEnd = items[0].End;
        var best = 0.0;
        var cut = double.NaN;
        for (var i = 1; i < items.Count; i++)
        {
            var gap = items[i].Start - maxEnd;
            if (gap > best && gap >= minimumGap)
            {
                best = gap;
                cut = maxEnd + (gap / 2);
            }

            maxEnd = Math.Max(maxEnd, items[i].End);
        }

        return double.IsNaN(cut) ? null : cut;
    }

    private static List<List<Glyph>> ClusterLines(List<Glyph> glyphs)
    {
        var sorted = glyphs.OrderBy(g => g.CenterY).ThenBy(g => g.Rect.X).ToList();
        var lines = new List<List<Glyph>>();
        List<Glyph>? current = null;
        double sumCentre = 0;
        double tallest = 0;
        foreach (var glyph in sorted)
        {
            var height = Math.Max(glyph.Rect.Height, 0.1);
            if (current is not null && Math.Abs(glyph.CenterY - (sumCentre / current.Count)) <= 0.5 * Math.Max(tallest, height))
            {
                current.Add(glyph);
                sumCentre += glyph.CenterY;
                tallest = Math.Max(tallest, height);
            }
            else
            {
                current = [glyph];
                lines.Add(current);
                sumCentre = glyph.CenterY;
                tallest = height;
            }
        }

        foreach (var line in lines)
        {
            line.Sort((a, b) => a.Rect.X != b.Rect.X ? a.Rect.X.CompareTo(b.Rect.X) : a.Source.CompareTo(b.Source));
        }

        return lines;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // ---- assembling the model ------------------------------------------------------------------------------------------------

    // PDFium inserts spaces between text objects, and they land in gutters: drop whitespace at both ends of a line.
    private static List<Glyph> TrimSpaces(List<Glyph> line)
    {
        var a = 0;
        var b = line.Count;
        while (a < b && line[a].IsSpace)
        {
            a++;
        }

        while (b > a && line[b - 1].IsSpace)
        {
            b--;
        }

        return line.GetRange(a, b - a);
    }

    private static TextModel Assemble(List<Paragraph> paragraphs, int page)
    {
        var atoms = new List<Atom>();
        var lineId = -1;
        var paragraphId = -1;
        for (var pi = 0; pi < paragraphs.Count; pi++)
        {
            var paragraph = paragraphs[pi];
            if (paragraph.Lines.Count == 0)
            {
                continue;
            }

            paragraphId++;
            for (var li = 0; li < paragraph.Lines.Count; li++)
            {
                var line = TrimSpaces(paragraph.Lines[li]);
                if (line.Count == 0)
                {
                    continue;
                }

                lineId++;
                var lineStart = atoms.Count;
                var join = false;
                for (var gi = 0; gi < line.Count; gi++)
                {
                    var glyph = line[gi];
                    if (glyph.IsHyphen)
                    {
                        // Line-end hyphenation: the hyphen is not part of the word. Its box goes to the previous atom, and no separator follows.
                        if (atoms.Count > lineStart && atoms[^1].Rect is { } previous)
                        {
                            atoms[^1].Rect = previous.Union(glyph.Rect);
                        }

                        join = gi == line.Count - 1 || line.Skip(gi + 1).All(g => g.IsSpace);
                        continue;
                    }

                    atoms.Add(new Atom { Text = glyph.Text, Source = glyph.Source, Rect = glyph.Rect, Line = lineId, Paragraph = paragraphId, Page = page, Size = glyph.Em });
                }

                SplitSharedRects(atoms, lineStart);
                var lastLineOfParagraph = li == paragraph.Lines.Count - 1;
                if (!lastLineOfParagraph && !join && atoms.Count > lineStart && !atoms[^1].IsWhitespace)
                {
                    atoms.Add(new Atom { Text = " ", Line = lineId, Paragraph = paragraphId, Page = page, Size = atoms[^1].Size });
                }
            }

            if (pi < paragraphs.Count - 1 && atoms.Count > 0)
            {
                atoms.Add(new Atom { Text = "\n", Line = lineId, Paragraph = paragraphId, Page = page, Size = atoms[^1].Size });
            }
        }

        // A page that ends with a separator (the last paragraph was empty) must not.
        while (atoms.Count > 0 && atoms[^1].Source < 0)
        {
            atoms.RemoveAt(atoms.Count - 1);
        }

        return new TextModel(atoms, atoms.Count == 0 ? [] : [0]);
    }

    // PDFium reports every character of an expanded ligature with the same box. Give each an equal share of the width, so the boxes tile the
    // glyph instead of overlapping. (Lines are horizontal in page space.)
    private static void SplitSharedRects(List<Atom> atoms, int from)
    {
        var i = from;
        while (i < atoms.Count)
        {
            var j = i + 1;
            while (j < atoms.Count && atoms[j].Rect is { } rj && atoms[i].Rect is { } ri && Same(ri, rj))
            {
                j++;
            }

            var n = j - i;
            if (n > 1 && atoms[i].Rect is { } r)
            {
                var step = r.Width / n;
                for (var k = 0; k < n; k++)
                {
                    atoms[i + k].Rect = new RectD(r.X + (k * step), r.Y, step, r.Height);
                }
            }

            i = j;
        }
    }

    private static bool Same(RectD a, RectD b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01 && a.Width > 0;
}
