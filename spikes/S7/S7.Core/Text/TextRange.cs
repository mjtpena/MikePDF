namespace S7.Core.Text;

public enum RangeEndpoint { Start = 0, End = 1 }

/// <summary>
/// UIA-style text range over a <see cref="TextModel"/>: [Start, End) in atom indices. Mutable like a UIA range;
/// the WinUI adapter wraps one of these per ITextRangeProvider.
/// Semantics follow the UIA documentation and the behaviour of the Win32 edit control as far as it is documented;
/// where the docs are silent the choice is recorded in docs/spikes/S7.md.
/// </summary>
public sealed class TextRange
{
    public TextModel Model { get; }
    public int Start { get; private set; }
    public int End { get; private set; }

    public TextRange(TextModel model, int start, int end)
    {
        Model = model;
        Start = Math.Clamp(start, 0, model.Length);
        End = Math.Clamp(end, Start, model.Length);
    }

    public bool IsDegenerate => Start == End;
    public TextRange Clone() => new(Model, Start, End);
    public string GetText(int maxLength = -1)
    {
        var t = Model.GetText(Start, End);
        return maxLength >= 0 && t.Length > maxLength ? t[..maxLength] : t;
    }

    public bool Compare(TextRange other) => ReferenceEquals(Model, other.Model) && Start == other.Start && End == other.End;

    public int CompareEndpoints(RangeEndpoint mine, TextRange other, RangeEndpoint theirs)
    {
        int a = mine == RangeEndpoint.Start ? Start : End;
        int b = theirs == RangeEndpoint.Start ? other.Start : other.End;
        return a.CompareTo(b);
    }

    public void Select(int start, int end) { Start = Math.Clamp(start, 0, Model.Length); End = Math.Clamp(end, Start, Model.Length); }

    /// <summary>Range becomes the single unit that contains its start point.</summary>
    public void ExpandToEnclosingUnit(TextUnitKind unit)
    {
        int n = Model.Length;
        if (n == 0) return;
        var b = Model.Boundaries(unit);
        int pos = Math.Min(Start, n - 1);           // the end-of-document position expands to the last unit
        int idx = Math.Min(Model.UnitIndex(unit, pos), b.Length - 2);
        Start = b[idx];
        End = b[idx + 1];
    }

    /// <summary>Move the whole range by <paramref name="count"/> units. Returns the number of units actually moved.</summary>
    public int Move(TextUnitKind unit, int count)
    {
        int n = Model.Length;
        if (count == 0 || n == 0) return 0;
        var b = Model.Boundaries(unit);
        int m = b.Length - 1;
        if (IsDegenerate)
        {
            var (np, moved) = MovePos(b, Start, count, n);
            Start = End = np;
            return moved;
        }
        // Non-degenerate: normalise to the unit at Start, then step whole units. Cannot step onto the end sentinel.
        int idx = Math.Min(Model.UnitIndex(unit, Start), m - 1);
        int nidx = Math.Clamp(idx + count, 0, m - 1);
        Start = b[nidx];
        End = b[nidx + 1];
        return nidx - idx;
    }

    /// <summary>Move one endpoint by <paramref name="count"/> unit boundaries. If the endpoints would cross, the other one follows.</summary>
    public int MoveEndpointByUnit(RangeEndpoint ep, TextUnitKind unit, int count)
    {
        int n = Model.Length;
        if (count == 0) return 0;
        var b = Model.Boundaries(unit);
        int pos = ep == RangeEndpoint.Start ? Start : End;
        var (np, moved) = MovePos(b, pos, count, n);
        if (ep == RangeEndpoint.Start) { Start = np; if (End < Start) End = Start; }
        else { End = np; if (Start > End) Start = End; }
        return moved;
    }

    public void MoveEndpointByRange(RangeEndpoint mine, TextRange other, RangeEndpoint theirs)
    {
        int p = theirs == RangeEndpoint.Start ? other.Start : other.End;
        if (mine == RangeEndpoint.Start) { Start = p; if (End < Start) End = Start; }
        else { End = p; if (Start > End) Start = End; }
    }

    /// <summary>
    /// Moves a single position across unit boundaries. A position strictly inside a unit counts the boundary it reaches
    /// (forward: the next boundary, backward: the start of its own unit) as one move.
    /// </summary>
    private static (int pos, int moved) MovePos(int[] b, int pos, int count, int n)
    {
        int m = b.Length - 1;
        int k = Array.BinarySearch(b, pos);
        bool onBoundary = k >= 0;
        if (!onBoundary) k = ~k - 1;             // k = index of the boundary at or before pos
        if (k < 0) k = 0;
        if (count > 0)
        {
            int target = Math.Min(k + count, m);
            return (b[target], target - k);
        }
        int s = -count;
        if (onBoundary)
        {
            int target = Math.Max(k - s, 0);
            return (b[target], -(k - target));
        }
        else
        {
            int target = Math.Max(k - (s - 1), 0);
            return (b[target], -(k - target + 1));
        }
    }
}
