using System.Text;
using Quire.Core.Geometry;

namespace Quire.Core.Text;

/// <summary>
/// A position between two atoms of a page's text: <see cref="Atom"/> is the number of atoms before it (0 to the model's length).
/// <see cref="EndOfPage"/> stands for the end whatever the length turns out to be, so "select all" needs no page loaded.
/// </summary>
public readonly record struct TextPosition(int Page, int Atom) : IComparable<TextPosition>
{
    public const int EndOfPage = int.MaxValue;

    public static TextPosition StartOf(int page) => new(page, 0);

    public static TextPosition EndOf(int page) => new(page, EndOfPage);

    public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Atom.CompareTo(other.Atom);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>The selected text: from <see cref="Anchor"/>, where it began, to <see cref="Active"/>, the end that moves. Either may come first.</summary>
public readonly record struct TextSelection(TextPosition Anchor, TextPosition Active)
{
    public bool IsEmpty => Anchor == Active;

    public TextPosition Start => Anchor <= Active ? Anchor : Active;

    public TextPosition End => Anchor <= Active ? Active : Anchor;

    /// <summary>The atoms of <paramref name="page"/> that are selected, for a model of <paramref name="length"/> atoms: <c>[From, To)</c>; empty if the page is outside.</summary>
    public (int From, int To) RangeOnPage(int page, int length)
    {
        if (IsEmpty || page < Start.Page || page > End.Page)
        {
            return (0, 0);
        }

        var from = page == Start.Page ? Math.Min(Start.Atom, length) : 0;
        var to = page == End.Page ? Math.Min(End.Atom, length) : length;
        return (from, Math.Max(from, to));
    }
}

/// <summary>Gives the selection the text models that are loaded. Never waits: a page whose text has not arrived yet is null.</summary>
public interface ITextModelSource
{
    int PageCount { get; }

    TextModel? GetLoaded(int page);
}

public enum ExtendKind
{
    Character,
    Word,
    Line,
}

public enum ExtendOutcome
{
    /// <summary>The selection changed.</summary>
    Moved,

    /// <summary>It could not move (the start or the end of the document).</summary>
    AtEdge,

    /// <summary>The neighbouring page's text is not loaded yet: load <see cref="SelectionController.PageNeeded"/> and try again.</summary>
    NeedsPage,
}

/// <summary>
/// The selection of a document and the rules that change it: set by the pointer, grown by word, line or paragraph, moved by the keyboard with
/// Shift, cleared. It knows text positions only; where those are on the screen is the page surface's business. Not thread-safe: use it on the
/// UI thread.
/// </summary>
public sealed class SelectionController(ITextModelSource source)
{
    private double? _desiredX;
    private bool _hasCaret; // a click or a selection has put the active end somewhere, even if nothing is selected yet

    public TextSelection Selection { get; private set; }

    public bool HasSelection => !Selection.IsEmpty;

    /// <summary>The page <see cref="Extend"/> needs, after it answered <see cref="ExtendOutcome.NeedsPage"/>.</summary>
    public int PageNeeded { get; private set; } = -1;

    /// <summary>The selection changed (also when it was cleared).</summary>
    public event Action? Changed;

    public void Clear()
    {
        _desiredX = null;
        _hasCaret = false;
        Set(default);
    }

    /// <summary>Starts a selection at a position (a click): nothing is selected until the other end moves.</summary>
    public void Begin(TextPosition position)
    {
        _desiredX = null;
        _hasCaret = true;
        Set(new TextSelection(position, position));
    }

    /// <summary>Moves the active end (a drag).</summary>
    public void DragTo(TextPosition position)
    {
        _desiredX = null;
        _hasCaret = true;
        Set(new TextSelection(Selection.Anchor, position));
    }

    public void Select(TextPosition anchor, TextPosition active)
    {
        _desiredX = null;
        _hasCaret = true;
        Set(new TextSelection(anchor, active));
    }

    public void SelectAll()
    {
        _desiredX = null;
        _hasCaret = source.PageCount > 0;
        Set(source.PageCount == 0 ? default : new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(source.PageCount - 1)));
    }

    /// <summary>Selects the unit at an atom. Returns false if the page's text is not loaded or has no such atom.</summary>
    public bool SelectUnitAt(TextPosition position, TextUnitKind unit)
    {
        if (source.GetLoaded(position.Page) is not { Length: > 0 } model)
        {
            return false;
        }

        var atom = Math.Clamp(position.Atom, 0, model.Length - 1);
        var bounds = model.Boundaries(unit);
        var index = Math.Min(model.UnitIndex(unit, atom), bounds.Length - 2);
        var (start, end) = (bounds[index], bounds[index + 1]);
        // What is selected does not include the space (or the line break) after it, and a word leaves out the punctuation that clings to it:
        // "margin," is the word "margin".
        while (end > start + 1 && model.Atoms[end - 1].IsWhitespace)
        {
            end--;
        }

        // A word of punctuation alone ("--") is kept whole.
        if (unit == TextUnitKind.Word && Enumerable.Range(start, end - start).Any(i => !IsPunctuation(model.Atoms[i])))
        {
            var from = start;
            var to = end;
            while (to > from + 1 && IsPunctuation(model.Atoms[to - 1]))
            {
                to--;
            }

            while (from < to - 1 && IsPunctuation(model.Atoms[from]))
            {
                from++;
            }

            (start, end) = (from, to);
        }

        _desiredX = null;
        _hasCaret = true;
        Set(new TextSelection(new TextPosition(position.Page, start), new TextPosition(position.Page, end)));
        return true;
    }

    /// <summary>
    /// Moves the active end by a character, a word or a line (<paramref name="direction"/> is -1 or +1), as Shift with an arrow key does. With
    /// no selection it starts from <paramref name="startIfNone"/>.
    /// </summary>
    public ExtendOutcome Extend(ExtendKind kind, int direction, TextPosition startIfNone)
    {
        PageNeeded = -1;
        direction = Math.Sign(direction);
        if (direction == 0)
        {
            return ExtendOutcome.AtEdge;
        }

        var selection = _hasCaret ? Selection : new TextSelection(startIfNone, startIfNone);
        var active = selection.Active;
        if (source.GetLoaded(active.Page) is not { } model)
        {
            PageNeeded = active.Page;
            return ExtendOutcome.NeedsPage;
        }

        var position = new TextPosition(active.Page, Math.Min(active.Atom, model.Length));
        var next = kind == ExtendKind.Line ? MoveByLine(model, position, direction) : MoveHorizontally(model, position, kind, direction);
        if (next is null)
        {
            return ExtendOutcome.AtEdge;
        }

        if (next.Value.Page != position.Page)
        {
            if (source.GetLoaded(next.Value.Page) is null)
            {
                PageNeeded = next.Value.Page;
                return ExtendOutcome.NeedsPage;
            }

            // Arriving on another page by character or word is at its near edge; by line, at the line asked for.
            next = kind == ExtendKind.Line ? MoveByLine(source.GetLoaded(next.Value.Page)!, next.Value, direction, entering: true) : next;
            if (next is null)
            {
                return ExtendOutcome.AtEdge;
            }
        }

        if (kind != ExtendKind.Line)
        {
            _desiredX = null;
        }

        _hasCaret = true;
        Set(new TextSelection(selection.Anchor, next.Value));
        return ExtendOutcome.Moved;
    }

    private TextPosition? MoveHorizontally(TextModel model, TextPosition position, ExtendKind kind, int direction)
    {
        var atom = position.Atom;
        if (direction > 0 && atom >= model.Length)
        {
            return position.Page + 1 < source.PageCount ? TextPosition.StartOf(position.Page + 1) : null;
        }

        if (direction < 0 && atom <= 0)
        {
            return position.Page > 0 ? TextPosition.EndOf(position.Page - 1) : null;
        }

        if (kind == ExtendKind.Character)
        {
            return new TextPosition(position.Page, atom + direction);
        }

        var bounds = model.Boundaries(TextUnitKind.Word);
        var index = Array.BinarySearch(bounds, atom);
        if (direction > 0)
        {
            // To the end of the next word that ends after here (not the start of the one after, and not past the space that follows it).
            for (var i = index >= 0 ? index + 1 : ~index; i < bounds.Length; i++)
            {
                var end = bounds[i];
                while (end > bounds[i - 1] + 1 && model.Atoms[end - 1].IsWhitespace)
                {
                    end--;
                }

                if (end > atom)
                {
                    return new TextPosition(position.Page, end);
                }
            }

            return new TextPosition(position.Page, model.Length);
        }

        var previous = index >= 0 ? index - 1 : ~index - 1;
        return new TextPosition(position.Page, bounds[Math.Max(previous, 0)]);
    }

    private TextPosition? MoveByLine(TextModel model, TextPosition position, int direction, bool entering = false)
    {
        if (model.LineCount == 0)
        {
            return null;
        }

        var current = entering ? (direction > 0 ? -1 : model.LineCount) : model.LineOf(Math.Min(position.Atom, Math.Max(0, model.Length - 1)));
        if (current < 0 && !entering)
        {
            current = direction > 0 ? -1 : model.LineCount;
        }

        _desiredX ??= CaretX(model, position.Atom);

        var target = current + direction;
        if (target < 0)
        {
            return position.Page > 0 ? TextPosition.EndOf(position.Page - 1) : null;
        }

        if (target >= model.LineCount)
        {
            return position.Page + 1 < source.PageCount ? TextPosition.StartOf(position.Page + 1) : null;
        }

        return new TextPosition(position.Page, model.AtomInLineNear(target, _desiredX.Value));
    }

    private static bool IsPunctuation(Atom atom) => atom.Text.Length > 0 && (char.IsPunctuation(atom.Text, 0) || char.IsSymbol(atom.Text, 0));

    // Where a caret at this position would be drawn, along the line: the left edge of the atom after it, or the right edge of the one before.
    private static double CaretX(TextModel model, int atom)
    {
        if (atom < model.Length && model.Atoms[atom].Rect is { } after)
        {
            return after.X;
        }

        return atom > 0 && atom <= model.Length && model.Atoms[atom - 1].Rect is { } before ? before.Right : 0;
    }

    private void Set(TextSelection selection)
    {
        if (selection == Selection)
        {
            return;
        }

        Selection = selection;
        Changed?.Invoke();
    }
}

/// <summary>The text of a selection, for the clipboard.</summary>
public static class SelectionText
{
    /// <summary>Most characters one copy collects: Select all in a long book must not exhaust memory.</summary>
    public const int MaxCharacters = 4_000_000;

    /// <summary>
    /// Collects the selected text page by page, asking <paramref name="loadPage"/> for each page's text model (null for a page that cannot be
    /// read: it is skipped). Pages are separated by a blank line; paragraphs by a line break. <paramref name="newLine"/> is what a line break
    /// becomes (Windows text uses CR LF).
    /// </summary>
    public static async Task<(string Text, bool Truncated)> CollectAsync(
        TextSelection selection,
        Func<int, CancellationToken, Task<TextModel?>> loadPage,
        string newLine,
        CancellationToken cancellationToken,
        int maxCharacters = MaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        var text = new StringBuilder();
        var truncated = false;
        if (selection.IsEmpty)
        {
            return (string.Empty, false);
        }

        for (var page = selection.Start.Page; page <= selection.End.Page && !truncated; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = await loadPage(page, cancellationToken).ConfigureAwait(false);
            if (model is null || model.Length == 0)
            {
                continue;
            }

            var (from, to) = selection.RangeOnPage(page, model.Length);
            if (to <= from)
            {
                continue;
            }

            var piece = model.GetText(from, to);
            if (text.Length > 0)
            {
                text.Append(newLine).Append(newLine);
            }

            var room = maxCharacters - text.Length;
            if (piece.Length > room)
            {
                piece = piece[..Math.Max(0, room)];
                truncated = true;
            }

            text.Append(Clean(piece, newLine));
        }

        return (text.ToString(), truncated);
    }

    private static string Clean(string piece, string newLine)
    {
        var text = new StringBuilder(piece.Length);
        foreach (var c in piece)
        {
            if (c == '\n')
            {
                text.Append(newLine);
            }
            else if (c == '\0' || (char.IsControl(c) && c != '\t'))
            {
                continue;
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>The boxes to paint for a selection on one page (page space): one per line.</summary>
    public static IReadOnlyList<RectD> RectsOnPage(TextSelection selection, int page, TextModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (from, to) = selection.RangeOnPage(page, model.Length);
        return to <= from ? [] : [.. model.LineRects(from, to).Where(l => l.Page == page).Select(l => l.Rect)];
    }
}

/// <summary>
/// Maps between a selection (pages and atoms of those pages) and a range of the combined model the accessibility peer exposes (the text of the
/// pages in view, as one run of atoms). Pages outside the combined model count as before its start or after its end.
/// </summary>
public static class AccessibleSelectionMap
{
    /// <summary>The atoms of <paramref name="combined"/> that a selection covers: <c>[Start, End)</c>; a point at the caret when nothing is selected.</summary>
    public static (int Start, int End) ToAtoms(TextModel combined, TextSelection selection)
    {
        ArgumentNullException.ThrowIfNull(combined);
        return (ToAtom(combined, selection.Start), ToAtom(combined, selection.End));
    }

    /// <summary>The selection for atoms <c>[start, end)</c> of <paramref name="combined"/>.</summary>
    public static TextSelection ToSelection(TextModel combined, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(combined);
        start = Math.Clamp(start, 0, combined.Length);
        end = Math.Clamp(end, start, combined.Length);
        return new TextSelection(ToPosition(combined, start), ToPosition(combined, end));
    }

    private static int ToAtom(TextModel combined, TextPosition position)
    {
        var pages = combined.Pages.ToList();
        if (pages.Count == 0)
        {
            return 0;
        }

        if (position.Page < pages[0])
        {
            return 0;
        }

        if (position.Page > pages[^1])
        {
            return combined.Length;
        }

        foreach (var page in pages)
        {
            if (page < position.Page)
            {
                continue;
            }

            var (start, end) = combined.PageRange(page);
            return page == position.Page ? start + Math.Min(position.Atom, end - start) : start;
        }

        return combined.Length;
    }

    private static TextPosition ToPosition(TextModel combined, int atom)
    {
        TextPosition last = default;
        foreach (var page in combined.Pages)
        {
            var (start, end) = combined.PageRange(page);
            if (atom >= start && atom <= end)
            {
                return new TextPosition(page, atom - start);
            }

            last = new TextPosition(page, end - start);
        }

        return last;
    }
}
