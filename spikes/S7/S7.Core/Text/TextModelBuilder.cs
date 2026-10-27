using System.Text;
using S7.Core.Engine;
using S7.Core.Geometry;

namespace S7.Core.Text;

/// <summary>
/// Turns engine data (<see cref="PageTextData"/>) into a reading-ordered <see cref="TextModel"/>.
///
/// Reading order:
///  * Tagged pages: the structure tree is authoritative. Struct blocks are visited in tree order; each block's text is the
///    glyphs whose marked-content id (MCID) is listed by the block. Glyphs that no block claims (artifacts: headers,
///    footers, page numbers) are ordered with the untagged heuristic and appended at the end, flagged Artifact.
///  * Untagged pages: recursive XY-cut on glyph rectangles in BASE page space (rotation by /Rotate applied, so a rotated
///    page is analysed the way the user sees it).
///      1. Find the widest empty vertical band of at least 1.0 em that no glyph crosses: cut there, recurse left then right.
///      2. Otherwise find the widest empty horizontal band of at least 0.5 em: cut there, recurse top then bottom.
///      3. Otherwise the region is a leaf: cluster glyphs into lines by vertical centre (tolerance 0.5 line height), order
///         each line left to right, and split paragraphs where the line pitch exceeds 1.45 x the median pitch.
///    Vertical cuts are tried first so that a two-column body is not sliced row by row; a full-width title or footer
///    prevents a vertical cut of the whole page and therefore falls through to rule 2.
///  * Not handled (documented limits): RTL, vertical writing, rotated text runs inside an upright page, tables (cells
///    are read in the order the cut produces, not row by row).
/// </summary>
public static class TextModelBuilder
{
    private sealed class Glyph
    {
        public required PdfChar C { get; init; }
        public required string Text { get; init; }
        public PageRect R { get; init; }
        public bool Hyphen { get; init; }
        public bool IsSpace => Text.Length > 0 && Text.All(char.IsWhiteSpace);
        public double Em => C.FontSize > 0.5 ? C.FontSize : Math.Max(1, R.Height / 1.12);
        public double Cy => R.Y + R.Height / 2;
    }

    private sealed class Para { public List<List<Glyph>> Lines = new(); public bool Artifact; }

    public static TextModel Build(PageTextData d)
    {
        var geo = new PageGeometry(d.Box, 0);
        var glyphs = CollectGlyphs(d, geo);
        var paras = new List<Para>();
        string source;

        if (d.IsTagged && d.StructBlocks.Count > 0)
        {
            var byMcid = new Dictionary<int, List<Glyph>>();
            foreach (var g in glyphs) if (g.C.Mcid >= 0) (byMcid.TryGetValue(g.C.Mcid, out var l) ? l : byMcid[g.C.Mcid] = new()).Add(g);
            var claimed = new HashSet<Glyph>();
            foreach (var blk in d.StructBlocks)
            {
                var gl = new List<Glyph>();
                foreach (int m in blk.Mcids)
                    if (byMcid.TryGetValue(m, out var l)) foreach (var g in l) if (claimed.Add(g)) gl.Add(g);
                if (gl.Count == 0) continue;
                paras.Add(new Para { Lines = LinesInContentOrder(gl) });
            }
            var rest = glyphs.Where(g => !claimed.Contains(g)).ToList();
            bool anyClaimed = paras.Count > 0;
            if (rest.Count > 0)
                foreach (var p in Untagged(rest)) { p.Artifact = anyClaimed; paras.Add(p); }
            source = !anyClaimed ? "xy-cut heuristic (struct tree matched no text)"
                : rest.Count == 0 ? "structure tree" : $"structure tree + xy-cut for {rest.Count} unclaimed glyphs (artifacts)";
        }
        else
        {
            paras.AddRange(Untagged(glyphs));
            source = d.IsTagged ? "xy-cut heuristic (empty struct tree)" : "xy-cut heuristic (untagged)";
        }

        return Assemble(paras, geo.PageWidth, geo.PageHeight, d.IsTagged, source);
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static List<Glyph> CollectGlyphs(PageTextData d, PageGeometry geo)
    {
        var list = new List<Glyph>();
        foreach (var c in d.Chars)
        {
            string t = c.Text;
            if (c.Generated && t != " ") continue;            // PDFium's synthetic CR/LF: replaced by the model's own separators
            if (c.Hyphen)
            {
                list.Add(new Glyph { C = c, Text = "", R = geo.UserRectToPage(c.Loose), Hyphen = true });
                continue;
            }
            if (t.Length == 0) continue;
            if (t[0] < 0x20 && t[0] != '\t') continue;
            if (t == "­" || t == "​" || t == "﻿") continue;
            var r = geo.UserRectToPage(c.Loose);
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            // Characters outside the visible box (PDFium returns them from FPDFText_GetText regardless of CropBox).
            if (cx < -1 || cx > geo.PageWidth + 1 || cy < -1 || cy > geo.PageHeight + 1) continue;
            foreach (var piece in ExpandLigature(t))
                list.Add(new Glyph { C = c, Text = piece, R = r });
        }
        return list;
    }

    // Alphabetic Presentation Forms U+FB00..FB06 only. A blanket NFKC would also turn superscripts and
    // full-width forms into different characters, which changes what a screen reader says.
    private static IEnumerable<string> ExpandLigature(string t)
    {
        if (t.Length == 1 && t[0] >= 'ﬀ' && t[0] <= 'ﬆ')
        {
            foreach (var ch in t.Normalize(NormalizationForm.FormKC)) yield return ch.ToString();
        }
        else yield return t;
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static List<List<Glyph>> LinesInContentOrder(List<Glyph> gl)
    {
        var lines = new List<List<Glyph>>();
        List<Glyph>? cur = null;
        double lineCy = 0, lineH = 0;
        Glyph? prev = null;
        foreach (var g in gl)
        {
            if (cur == null) { cur = new(); lines.Add(cur); }
            else if (!g.Hyphen && g.R.Height > 0)
            {
                bool newLine = Math.Abs(g.Cy - lineCy) > 0.5 * Math.Max(lineH, g.R.Height);
                if (!newLine && prev != null && g.R.Right <= prev.R.X - 0.2 * g.Em && prev.R.Width > 0 && g.R.Width > 0) newLine = true;
                if (newLine) { cur = new(); lines.Add(cur); lineH = 0; }
            }
            cur.Add(g);
            if (g.R.Height > 0 && !g.Hyphen) { if (cur.Count == 1 || lineH == 0) lineCy = g.Cy; lineH = Math.Max(lineH, g.R.Height); }
            prev = g;
        }
        return lines;
    }

    private static List<Para> Untagged(List<Glyph> glyphs)
    {
        var leaves = new List<List<Glyph>>();
        XyCut(glyphs, leaves, 0);
        var paras = new List<Para>();
        foreach (var leaf in leaves)
        {
            var lines = ClusterLines(leaf);
            if (lines.Count == 0) continue;
            var cys = lines.Select(l => l.Where(g => g.R.Height > 0).Select(g => g.Cy).DefaultIfEmpty(0).Average()).ToList();
            var pitches = new List<double>();
            for (int i = 1; i < cys.Count; i++) pitches.Add(cys[i] - cys[i - 1]);
            double med = pitches.Count == 0 ? 0 : Median(pitches);
            var p = new Para();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0 && med > 0 && pitches[i - 1] > 1.45 * med) { paras.Add(p); p = new Para(); }
                p.Lines.Add(lines[i]);
            }
            paras.Add(p);
        }
        return paras;
    }

    private static void XyCut(List<Glyph> g, List<List<Glyph>> leaves, int depth)
    {
        if (g.Count == 0) return;
        if (g.Count < 2 || depth > 24) { leaves.Add(g); return; }
        double em = Median(g.Select(x => x.Em).ToList());
        var solid = g.Where(x => !x.IsSpace && x.R.Width > 0).ToList();
        if (solid.Count < 2) { leaves.Add(g); return; }

        if (FindGap(solid, vertical: true, minGap: 1.0 * em) is { } vx)
        {
            Split(g, x => x.R.X + x.R.Width / 2 < vx, out var a, out var b);
            if (a.Count > 0 && b.Count > 0) { XyCut(a, leaves, depth + 1); XyCut(b, leaves, depth + 1); return; }
        }
        if (FindGap(solid, vertical: false, minGap: HorizontalCutThreshold(solid, em)) is { } hy)
        {
            Split(g, x => x.Cy < hy, out var a, out var b);
            if (a.Count > 0 && b.Count > 0) { XyCut(a, leaves, depth + 1); XyCut(b, leaves, depth + 1); return; }
        }
        leaves.Add(g);
    }

    /// <summary>
    /// A horizontal band only counts as a cut when it is clearly larger than the ordinary gap between lines of this region
    /// (1.5 x the median inter-line gap), but never smaller than half an em, and a gap of 3 em always counts.
    /// This keeps generously leaded body text from being sliced line by line.
    /// </summary>
    private static double HorizontalCutThreshold(List<Glyph> solid, double em)
    {
        var lines = ClusterLines(solid);
        if (lines.Count < 2) return 0.5 * em;
        var spans = lines.Select(l => (top: l.Min(g => g.R.Y), bottom: l.Max(g => g.R.Bottom))).OrderBy(x => x.top).ToList();
        var gaps = new List<double>();
        double maxBottom = spans[0].bottom;
        for (int i = 1; i < spans.Count; i++) { gaps.Add(Math.Max(0, spans[i].top - maxBottom)); maxBottom = Math.Max(maxBottom, spans[i].bottom); }
        double med = Median(gaps);
        return Math.Max(0.5 * em, Math.Min(1.5 * med, 3 * em));
    }

    private static void Split(List<Glyph> g, Func<Glyph, bool> first, out List<Glyph> a, out List<Glyph> b)
    {
        a = new(); b = new();
        foreach (var x in g) (first(x) ? a : b).Add(x);
    }

    /// <summary>Widest empty band along one axis that is at least minGap wide; returns the cut coordinate (band centre) or null.</summary>
    private static double? FindGap(List<Glyph> g, bool vertical, double minGap)
    {
        var items = g.Select(x => vertical ? (s: x.R.X, e: x.R.Right) : (s: x.R.Y, e: x.R.Bottom)).OrderBy(i => i.s).ToList();
        double maxEnd = items[0].e, best = 0, cut = double.NaN;
        for (int i = 1; i < items.Count; i++)
        {
            double gap = items[i].s - maxEnd;
            if (gap > best && gap >= minGap) { best = gap; cut = maxEnd + gap / 2; }
            maxEnd = Math.Max(maxEnd, items[i].e);
        }
        return double.IsNaN(cut) ? null : cut;
    }

    private static List<List<Glyph>> ClusterLines(List<Glyph> g)
    {
        var sorted = g.OrderBy(x => x.Cy).ThenBy(x => x.R.X).ToList();
        var lines = new List<List<Glyph>>();
        List<Glyph>? cur = null;
        double sumCy = 0, hMax = 0;
        foreach (var x in sorted)
        {
            double h = Math.Max(x.R.Height, 0.1);
            if (cur != null && Math.Abs(x.Cy - sumCy / cur.Count) <= 0.5 * Math.Max(hMax, h)) { cur.Add(x); sumCy += x.Cy; hMax = Math.Max(hMax, h); }
            else { cur = new() { x }; lines.Add(cur); sumCy = x.Cy; hMax = h; }
        }
        foreach (var l in lines) l.Sort((a, b) => a.R.X != b.R.X ? a.R.X.CompareTo(b.R.X) : a.C.Index.CompareTo(b.C.Index));
        return lines;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    // ---------------------------------------------------------------------------------------------------------------
    /// <summary>Drop whitespace glyphs at both ends of a line (PDFium inserts spaces between text objects, which land in gutters).</summary>
    private static List<Glyph> TrimSpaces(List<Glyph> line)
    {
        int a = 0, b = line.Count;
        while (a < b && line[a].IsSpace) a++;
        while (b > a && (line[b - 1].IsSpace)) b--;
        return line.GetRange(a, b - a);
    }

    private static TextModel Assemble(List<Para> paras, double w, double h, bool tagged, string source)
    {
        var atoms = new List<Atom>();
        int lineId = -1, paraId = -1;
        for (int pi = 0; pi < paras.Count; pi++)
        {
            var p = paras[pi];
            if (p.Lines.Count == 0) continue;
            paraId++;
            for (int li = 0; li < p.Lines.Count; li++)
            {
                var line = TrimSpaces(p.Lines[li]);
                if (line.Count == 0) continue;
                lineId++;
                int lineStartAtom = atoms.Count;
                bool join = false;
                for (int gi = 0; gi < line.Count; gi++)
                {
                    var g = line[gi];
                    if (g.Hyphen)
                    {
                        // Line-end hyphenation: the hyphen is not part of the word. Cover its rectangle with the previous atom.
                        if (atoms.Count > lineStartAtom && atoms[^1].Rect is { } pr) atoms[^1].Rect = PageRect.Union(pr, g.R);
                        join = gi == line.Count - 1 || line.Skip(gi + 1).All(x => x.IsSpace);
                        continue;
                    }
                    atoms.Add(new Atom { Text = g.Text, Source = g.C.Index, Rect = g.R, Line = lineId, Para = paraId, FontSize = g.C.FontSize, Artifact = p.Artifact });
                }
                SplitSharedRects(atoms, lineStartAtom);
                bool lastLineOfPara = li == p.Lines.Count - 1;
                if (!lastLineOfPara && !join && atoms.Count > lineStartAtom && !atoms[^1].IsWhitespace)
                    atoms.Add(new Atom { Text = " ", Line = lineId, Para = paraId, FontSize = atoms[^1].FontSize, Artifact = p.Artifact });
            }
            if (pi < paras.Count - 1 && atoms.Count > 0)
                atoms.Add(new Atom { Text = "\n", Line = lineId, Para = paraId, FontSize = atoms[^1].FontSize, Artifact = p.Artifact });
        }
        return new TextModel(atoms, w, h, tagged, source);
    }

    /// <summary>
    /// PDFium reports every character of an expanded ligature with the SAME box. Give each one an equal horizontal share
    /// so that per-character rectangles tile the glyph instead of overlapping. (Lines are horizontal in base page space.)
    /// </summary>
    private static void SplitSharedRects(List<Atom> atoms, int from)
    {
        int i = from;
        while (i < atoms.Count)
        {
            int j = i + 1;
            while (j < atoms.Count && atoms[j].Rect is { } rj && atoms[i].Rect is { } ri && Same(ri, rj)) j++;
            int n = j - i;
            if (n > 1 && atoms[i].Rect is { } r)
            {
                double step = r.Width / n;
                for (int k = 0; k < n; k++) atoms[i + k].Rect = new PageRect(r.X + k * step, r.Y, step, r.Height);
            }
            i = j;
        }
    }

    private static bool Same(PageRect a, PageRect b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01 && a.Width > 0;
}
