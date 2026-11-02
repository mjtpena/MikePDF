namespace MikePDF.Core.Text;

public enum RangeEndpoint
{
    Start = 0,
    End = 1,
}

/// <summary>
/// A UI Automation style text range over a <see cref="TextModel"/>: <c>[Start, End)</c> in atom indices. Mutable like the API it serves; the
/// adapter in the app wraps one per <c>ITextRangeProvider</c>. Where the UIA documentation is silent the choices follow the Win32 edit
/// control and are recorded in docs/spikes/S7.md.
/// </summary>
public sealed class TextRange
{
    public TextRange(TextModel model, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        Start = Math.Clamp(start, 0, model.Length);
        End = Math.Clamp(end, Start, model.Length);
    }

    public TextModel Model { get; }

    public int Start { get; private set; }

    public int End { get; private set; }

    public bool IsDegenerate => Start == End;

    public TextRange Clone() => new(Model, Start, End);

    public string GetText(int maxLength = -1)
    {
        var text = Model.GetText(Start, End);
        return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    public bool Compare(TextRange other) => ReferenceEquals(Model, other.Model) && Start == other.Start && End == other.End;

    public int CompareEndpoints(RangeEndpoint mine, TextRange other, RangeEndpoint theirs)
    {
        var a = mine == RangeEndpoint.Start ? Start : End;
        var b = theirs == RangeEndpoint.Start ? other.Start : other.End;
        return a.CompareTo(b);
    }

    public void Select(int start, int end)
    {
        Start = Math.Clamp(start, 0, Model.Length);
        End = Math.Clamp(end, Start, Model.Length);
    }

    /// <summary>The range becomes the single unit that contains its start point.</summary>
    public void ExpandToEnclosingUnit(TextUnitKind unit)
    {
        var n = Model.Length;
        if (n == 0)
        {
            return;
        }

        var bounds = Model.Boundaries(unit);
        var position = Math.Min(Start, n - 1); // the end-of-document position expands to the last unit
        var index = Math.Min(Model.UnitIndex(unit, position), bounds.Length - 2);
        Start = bounds[index];
        End = bounds[index + 1];
    }

    /// <summary>Moves the whole range by <paramref name="count"/> units. Returns the number of units actually moved.</summary>
    public int Move(TextUnitKind unit, int count)
    {
        var n = Model.Length;
        if (count == 0 || n == 0)
        {
            return 0;
        }

        var bounds = Model.Boundaries(unit);
        var m = bounds.Length - 1;
        if (IsDegenerate)
        {
            var (position, moved) = MovePosition(bounds, Start, count);
            Start = End = position;
            return moved;
        }

        // A range with text: normalise to the unit at Start, then step whole units. It cannot step onto the end-of-document sentinel.
        var index = Math.Min(Model.UnitIndex(unit, Start), m - 1);
        var next = Math.Clamp(index + count, 0, m - 1);
        Start = bounds[next];
        End = bounds[next + 1];
        return next - index;
    }

    /// <summary>Moves one endpoint by <paramref name="count"/> unit boundaries. If the endpoints would cross, the other one follows.</summary>
    public int MoveEndpointByUnit(RangeEndpoint endpoint, TextUnitKind unit, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var bounds = Model.Boundaries(unit);
        var position = endpoint == RangeEndpoint.Start ? Start : End;
        var (next, moved) = MovePosition(bounds, position, count);
        if (endpoint == RangeEndpoint.Start)
        {
            Start = next;
            if (End < Start)
            {
                End = Start;
            }
        }
        else
        {
            End = next;
            if (Start > End)
            {
                Start = End;
            }
        }

        return moved;
    }

    public void MoveEndpointByRange(RangeEndpoint mine, TextRange other, RangeEndpoint theirs)
    {
        var position = theirs == RangeEndpoint.Start ? other.Start : other.End;
        if (mine == RangeEndpoint.Start)
        {
            Start = position;
            if (End < Start)
            {
                End = Start;
            }
        }
        else
        {
            End = position;
            if (Start > End)
            {
                Start = End;
            }
        }
    }

    /// <summary>
    /// Moves one position across unit boundaries. A position strictly inside a unit counts the boundary it reaches (forward: the next
    /// boundary, backward: the start of its own unit) as one move.
    /// </summary>
    private static (int Position, int Moved) MovePosition(int[] bounds, int position, int count)
    {
        var m = bounds.Length - 1;
        var k = Array.BinarySearch(bounds, position);
        var onBoundary = k >= 0;
        if (!onBoundary)
        {
            k = ~k - 1; // the boundary at or before the position
        }

        if (k < 0)
        {
            k = 0;
        }

        if (count > 0)
        {
            var target = (int)Math.Min((long)k + count, m);
            return (bounds[target], target - k);
        }

        var steps = -(long)count;
        if (onBoundary)
        {
            var target = (int)Math.Max(k - steps, 0);
            return (bounds[target], -(k - target));
        }
        else
        {
            var target = (int)Math.Max(k - (steps - 1), 0);
            return (bounds[target], -(k - target + 1));
        }
    }
}
