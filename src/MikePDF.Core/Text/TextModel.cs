using System.Text;
using MikePDF.Core.Geometry;

namespace MikePDF.Core.Text;

public enum TextUnitKind
{
    Character = 0,
    Format = 1,
    Word = 2,
    Line = 3,
    Paragraph = 4,
    Page = 5,
    Document = 6,
}

/// <summary>One addressable text position: one text element (a letter, a space, a surrogate pair), or a separator the model added.</summary>
public sealed class Atom
{
    public required string Text { get; init; }

    /// <summary>PDFium's character number, or -1 for the separators the model adds (a space between lines, a line break between paragraphs).</summary>
    public int Source { get; init; } = -1;

    /// <summary>The box in page space (the page's own /Rotate applied, the view rotation not), in points. Null for separators.</summary>
    public RectD? Rect { get; set; }

    public int Line { get; set; }

    public int Paragraph { get; set; }

    public int Page { get; init; }

    /// <summary>The size of the type, estimated from the height of the box.</summary>
    public double Size { get; init; }

    public bool IsWhitespace => Text.Length > 0 && char.IsWhiteSpace(Text, 0);
}

/// <summary>
/// The text of one page, or of several pages in a row, in reading order: atoms with their boxes, and the boundaries of every unit the
/// accessibility API asks about. Pure data and pure functions; no PDFium handles and no UI types. Built once, then only read.
/// </summary>
public sealed class TextModel
{
    private readonly int[][] _bounds = new int[7][];
    private readonly int[] _offsets;
    private readonly LineInfo[] _lines;
    private readonly int[] _pageStarts;
    private readonly int[] _pageNumbers;

    private readonly record struct LineInfo(int Page, int Start, int End, RectD Rect);

    /// <param name="atoms">In reading order. Atoms of one page are together.</param>
    /// <param name="pageStarts">The index of the first atom of each page, ascending; the pages they belong to are read from the atoms.</param>
    public TextModel(IReadOnlyList<Atom> atoms, IReadOnlyList<int> pageStarts)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(pageStarts);
        Atoms = atoms;
        var n = atoms.Count;
        _offsets = new int[n + 1];
        var text = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            _offsets[i] = text.Length;
            text.Append(atoms[i].Text);
        }

        _offsets[n] = text.Length;
        Text = text.ToString();

        _pageStarts = [.. pageStarts];
        _pageNumbers = new int[_pageStarts.Length];
        for (var p = 0; p < _pageStarts.Length; p++)
        {
            _pageNumbers[p] = _pageStarts[p] < n ? atoms[_pageStarts[p]].Page : -1;
        }

        _bounds[(int)TextUnitKind.Character] = [.. Enumerable.Range(0, n + 1)];
        _bounds[(int)TextUnitKind.Word] = Bounds(n, i => i == 0 || (atoms[i - 1].IsWhitespace && !atoms[i].IsWhitespace));
        _bounds[(int)TextUnitKind.Line] = Bounds(n, i => i == 0 || atoms[i].Line != atoms[i - 1].Line);
        _bounds[(int)TextUnitKind.Paragraph] = Bounds(n, i => i == 0 || atoms[i].Paragraph != atoms[i - 1].Paragraph);
        _bounds[(int)TextUnitKind.Format] = Bounds(n, i => i == 0 || Math.Abs(atoms[i].Size - atoms[i - 1].Size) > 0.01);
        _bounds[(int)TextUnitKind.Page] = n == 0 ? [0] : [.. _pageStarts.Where(s => s < n).Append(n).Distinct()];
        _bounds[(int)TextUnitKind.Document] = n == 0 ? [0] : [0, n];
        _lines = BuildLines(atoms);
    }

    /// <summary>A model of nothing.</summary>
    public static TextModel Empty { get; } = new([], []);

    public IReadOnlyList<Atom> Atoms { get; }

    public string Text { get; }

    public int Length => Atoms.Count;

    /// <summary>The pages this model covers, in order.</summary>
    public IEnumerable<int> Pages => _pageNumbers.Where(p => p >= 0);

    public int[] Boundaries(TextUnitKind unit) => _bounds[(int)unit];

    /// <summary>Joins page models into one, with a line break between pages (so the Page unit of the combined model is one page's text).</summary>
    public static TextModel Combine(IReadOnlyList<TextModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var atoms = new List<Atom>();
        var starts = new List<int>();
        var lineBase = 0;
        var paragraphBase = 0;
        foreach (var model in models.Where(m => m.Length > 0))
        {
            if (atoms.Count > 0)
            {
                var last = atoms[^1];
                atoms.Add(new Atom { Text = "\n", Line = last.Line, Paragraph = last.Paragraph, Page = last.Page, Size = last.Size });
            }

            starts.Add(atoms.Count);
            var maxLine = 0;
            var maxParagraph = 0;
            foreach (var atom in model.Atoms)
            {
                atoms.Add(new Atom
                {
                    Text = atom.Text,
                    Source = atom.Source,
                    Rect = atom.Rect,
                    Line = atom.Line + lineBase,
                    Paragraph = atom.Paragraph + paragraphBase,
                    Page = atom.Page,
                    Size = atom.Size,
                });
                maxLine = Math.Max(maxLine, atom.Line);
                maxParagraph = Math.Max(maxParagraph, atom.Paragraph);
            }

            lineBase += maxLine + 1;
            paragraphBase += maxParagraph + 1;
        }

        return new TextModel(atoms, starts);
    }

    private static int[] Bounds(int n, Func<int, bool> isStart)
    {
        var list = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (isStart(i))
            {
                list.Add(i);
            }
        }

        list.Add(n);
        return [.. list];
    }

    private static LineInfo[] BuildLines(IReadOnlyList<Atom> atoms)
    {
        var lines = new List<LineInfo>();
        var start = -1;
        RectD? box = null;
        for (var i = 0; i <= atoms.Count; i++)
        {
            var endsLine = i == atoms.Count || (start >= 0 && atoms[i].Line != atoms[start].Line);
            if (endsLine && start >= 0)
            {
                if (box is { } rect)
                {
                    lines.Add(new LineInfo(atoms[start].Page, start, i, rect));
                }

                start = -1;
                box = null;
            }

            if (i == atoms.Count)
            {
                break;
            }

            if (start < 0)
            {
                start = i;
            }

            if (atoms[i].Rect is { } r)
            {
                box = box is { } b ? b.Union(r) : r;
            }
        }

        return [.. lines];
    }

    /// <summary>The text of atoms <c>[start, end)</c>.</summary>
    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        return Text[_offsets[start].._offsets[end]];
    }

    /// <summary>The atom that holds UTF-16 position <paramref name="offset"/> of <see cref="Text"/>; <see cref="Length"/> for the end.</summary>
    public int AtomAtOffset(int offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        if (offset >= Text.Length)
        {
            return Length;
        }

        var index = Array.BinarySearch(_offsets, offset);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>The UTF-16 position in <see cref="Text"/> where atom <paramref name="atom"/> starts.</summary>
    public int OffsetOfAtom(int atom) => _offsets[Math.Clamp(atom, 0, Length)];

    /// <summary>The index in <see cref="Boundaries"/> of the unit that contains <paramref name="position"/>.</summary>
    public int UnitIndex(TextUnitKind unit, int position)
    {
        var bounds = _bounds[(int)unit];
        var index = Array.BinarySearch(bounds, position);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Max(0, index);
    }

    /// <summary>The atoms of a page: <c>[Start, End)</c>. Empty when the model holds none of it.</summary>
    public (int Start, int End) PageRange(int page)
    {
        for (var p = 0; p < _pageStarts.Length; p++)
        {
            if (_pageNumbers[p] == page)
            {
                var end = p + 1 < _pageStarts.Length ? _pageStarts[p + 1] : Length;
                // The separator between pages belongs to neither.
                if (p + 1 < _pageStarts.Length && end > _pageStarts[p] && Atoms[end - 1].Source < 0 && Atoms[end - 1].Text == "\n")
                {
                    end--;
                }

                return (_pageStarts[p], end);
            }
        }

        return (0, 0);
    }

    /// <summary>The atoms in <c>[start, end)</c> grouped by line, as one box per line per page, in page space.</summary>
    public IEnumerable<(int Page, RectD Rect)> LineRects(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        RectD? current = null;
        var currentLine = -1;
        var currentPage = -1;
        for (var i = start; i < end; i++)
        {
            var atom = Atoms[i];
            if (atom.Rect is not { } rect)
            {
                continue;
            }

            if (current is { } open && atom.Line == currentLine && atom.Page == currentPage)
            {
                current = open.Union(rect);
            }
            else
            {
                if (current is { } done)
                {
                    yield return (currentPage, done);
                }

                current = rect;
                currentLine = atom.Line;
                currentPage = atom.Page;
            }
        }

        if (current is { } last)
        {
            yield return (currentPage, last);
        }
    }

    /// <summary>
    /// The atom nearest to a point of a page (page space), or -1 if the page has no text here. The line nearest to the point is found first, then
    /// the atom nearest to it along the line, so a point in a margin or a gutter still lands on something sensible.
    /// </summary>
    public int HitTest(int page, PointD point)
    {
        var bestDistance = double.MaxValue;
        var best = -1;
        for (var i = 0; i < _lines.Length; i++)
        {
            if (_lines[i].Page != page)
            {
                continue;
            }

            var distance = _lines[i].Rect.DistanceTo(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                if (distance == 0)
                {
                    break; // inside a line's box: that line
                }
            }
        }

        return best < 0 ? -1 : AtomInLine(_lines[best], point.X);
    }

    private int AtomInLine(LineInfo line, double x)
    {
        var nearest = line.Start;
        var nearestDistance = double.MaxValue;
        for (var i = line.Start; i < line.End; i++)
        {
            if (Atoms[i].Rect is not { } rect)
            {
                continue;
            }

            // Half open: a point exactly on the shared edge of two characters belongs to the one on its right.
            var distance = x < rect.X ? rect.X - x : x >= rect.Right ? x - rect.Right + 1e-9 : 0;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = i;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return nearest;
    }

    /// <summary>The index of the line that holds atom <paramref name="atom"/>, among the lines that have boxes; -1 if none.</summary>
    public int LineOf(int atom)
    {
        var low = 0;
        var high = _lines.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (atom < _lines[mid].Start)
            {
                high = mid - 1;
            }
            else if (atom >= _lines[mid].End)
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    public int LineCount => _lines.Length;

    /// <summary>The atoms <c>[Start, End)</c> and the box of line <paramref name="line"/> (an index from <see cref="LineOf"/>).</summary>
    public (int Start, int End, RectD Rect) LineAt(int line) => (_lines[line].Start, _lines[line].End, _lines[line].Rect);

    /// <summary>The atom in a line nearest to x, for moving up and down a column of text.</summary>
    public int AtomInLineNear(int line, double x) => AtomInLine(_lines[line], x);
}
