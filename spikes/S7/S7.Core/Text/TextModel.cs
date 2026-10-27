using S7.Core.Geometry;

namespace S7.Core.Text;

public enum TextUnitKind { Character = 0, Format = 1, Word = 2, Line = 3, Paragraph = 4, Page = 5, Document = 6 }

/// <summary>One addressable text position. Sub-glyph for expanded ligatures; synthetic for separators.</summary>
public sealed class Atom
{
    public required string Text { get; init; }
    /// <summary>PDFium char index, or -1 for synthetic separators (space/newline added by the model).</summary>
    public int Source { get; init; } = -1;
    /// <summary>Rectangle in BASE page space (page /Rotate applied, view rotation 0), points, top-left origin. Null for synthetic atoms.</summary>
    public PageRect? Rect { get; set; }
    public int Line { get; set; }
    public int Para { get; set; }
    public double FontSize { get; init; }
    public bool Artifact { get; init; }
    public bool IsWhitespace => Text.Length > 0 && char.IsWhiteSpace(Text, 0);
    public bool IsNewline => Text == "\n";
}

/// <summary>
/// The accessible text of one page: atoms in READING ORDER plus precomputed unit boundaries.
/// Pure data and pure functions; no PDFium handles and no UI types.
/// </summary>
public sealed class TextModel
{
    private readonly int[][] _bounds = new int[7][];

    public TextModel(IReadOnlyList<Atom> atoms, double baseWidth, double baseHeight, bool tagged, string orderSource)
    {
        Atoms = atoms;
        BaseWidth = baseWidth;
        BaseHeight = baseHeight;
        IsTagged = tagged;
        OrderSource = orderSource;
        Text = string.Concat(atoms.Select(a => a.Text));
        int n = atoms.Count;
        _bounds[(int)TextUnitKind.Character] = Enumerable.Range(0, n + 1).ToArray();
        _bounds[(int)TextUnitKind.Word] = Bounds(n, i => i == 0 || (atoms[i - 1].IsWhitespace && !atoms[i].IsWhitespace));
        _bounds[(int)TextUnitKind.Line] = Bounds(n, i => i == 0 || atoms[i].Line != atoms[i - 1].Line);
        _bounds[(int)TextUnitKind.Paragraph] = Bounds(n, i => i == 0 || atoms[i].Para != atoms[i - 1].Para);
        _bounds[(int)TextUnitKind.Format] = Bounds(n, i => i == 0 || Math.Abs(atoms[i].FontSize - atoms[i - 1].FontSize) > 0.01 || atoms[i].Artifact != atoms[i - 1].Artifact);
        _bounds[(int)TextUnitKind.Page] = n == 0 ? new[] { 0 } : new[] { 0, n };
        _bounds[(int)TextUnitKind.Document] = _bounds[(int)TextUnitKind.Page];
    }

    private static int[] Bounds(int n, Func<int, bool> isStart)
    {
        var l = new List<int>();
        for (int i = 0; i < n; i++) if (isStart(i)) l.Add(i);
        l.Add(n);
        return l.ToArray();
    }

    public IReadOnlyList<Atom> Atoms { get; }
    public string Text { get; }
    public int Length => Atoms.Count;
    public double BaseWidth { get; }
    public double BaseHeight { get; }
    public bool IsTagged { get; }
    public string OrderSource { get; }

    public int[] Boundaries(TextUnitKind u) => _bounds[(int)u];

    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Length); end = Math.Clamp(end, start, Length);
        var sb = new System.Text.StringBuilder();
        for (int i = start; i < end; i++) sb.Append(Atoms[i].Text);
        return sb.ToString();
    }

    /// <summary>Index into the boundary list of the unit that contains <paramref name="pos"/> (pos==Length maps to the sentinel index m).</summary>
    public int UnitIndex(TextUnitKind u, int pos)
    {
        var b = _bounds[(int)u];
        int idx = Array.BinarySearch(b, pos);
        if (idx < 0) idx = ~idx - 1;
        return Math.Max(0, idx);
    }

    /// <summary>Number of units (boundary list is m+1 long).</summary>
    public int UnitCount(TextUnitKind u) => _bounds[(int)u].Length - 1;

    /// <summary>All atoms in [start,end) grouped per line with the union of their rects, in BASE page space.</summary>
    public IEnumerable<PageRect> LineRects(int start, int end)
    {
        PageRect? cur = null;
        int curLine = -1;
        for (int i = start; i < end; i++)
        {
            var a = Atoms[i];
            if (a.Rect is not { } r) continue;
            if (cur is { } c && a.Line == curLine) cur = PageRect.Union(c, r);
            else
            {
                if (cur is { } done) yield return done;
                cur = r; curLine = a.Line;
            }
        }
        if (cur is { } last) yield return last;
    }

    /// <summary>Rotate a base page space rect by additional view quarter turns (clockwise).</summary>
    public PageRect ToView(PageRect r, int viewQuarterTurns)
    {
        double W = BaseWidth, H = BaseHeight;
        switch (((viewQuarterTurns % 4) + 4) % 4)
        {
            case 1: return new PageRect(H - (r.Y + r.Height), r.X, r.Height, r.Width);
            case 2: return new PageRect(W - (r.X + r.Width), H - (r.Y + r.Height), r.Width, r.Height);
            case 3: return new PageRect(r.Y, W - (r.X + r.Width), r.Height, r.Width);
            default: return r;
        }
    }

    public (double w, double h) ViewSize(int viewQuarterTurns) => (viewQuarterTurns & 1) == 0 ? (BaseWidth, BaseHeight) : (BaseHeight, BaseWidth);

    public IReadOnlyList<PageRect> BoundingRects(int start, int end, int viewQuarterTurns)
        => LineRects(start, end).Select(r => ToView(r, viewQuarterTurns)).ToList();

    /// <summary>Nearest atom to a point in VIEW page space (points). Returns -1 if the model is empty.</summary>
    public int HitTest(double x, double y, int viewQuarterTurns)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < Atoms.Count; i++)
        {
            if (Atoms[i].Rect is not { } br) continue;
            var r = ToView(br, viewQuarterTurns);
            double dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
            double dy = y < r.Y ? r.Y - y : y > r.Bottom ? y - r.Bottom : 0;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return best;
    }
}
