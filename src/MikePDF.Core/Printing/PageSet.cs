using System.Collections;

namespace MikePDF.Core.Printing;

/// <summary>Which of the chosen pages are printed (print.md 3.2 "Subset"). Applied after the range, so "2-9" with <see cref="Odd"/> prints 3, 5, 7 and 9.</summary>
public enum PageSubset
{
    All,
    Odd,
    Even,
}

/// <summary>Pages <see cref="First"/>, <see cref="First"/> + <see cref="Step"/>, ... up to <see cref="Last"/> (1-based, inclusive). <see cref="Step"/> is 1 or 2.</summary>
public readonly record struct PageRun(int First, int Last, int Step)
{
    public int Count => Last < First ? 0 : ((Last - First) / Step) + 1;
}

/// <summary>
/// A set of 1-based page numbers kept as ascending, non-overlapping runs, so that "1-" on a document of ten million pages costs one run and not a list. Enumerates in
/// ascending order and never prints a page twice.
/// </summary>
public sealed class PageSet : IReadOnlyCollection<int>
{
    private readonly PageRun[] _runs;

    internal PageSet(PageRun[] runs)
    {
        _runs = runs;
        long count = 0;
        foreach (var run in runs)
        {
            count += run.Count;
        }

        Count = (int)Math.Min(count, int.MaxValue);
    }

    public static PageSet Empty { get; } = new([]);

    public IReadOnlyList<PageRun> Runs => _runs;

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    public int First => _runs.Length == 0 ? 0 : _runs[0].First;

    /// <summary>The page <paramref name="index"/> places into the set (0 is the lowest page).</summary>
    public int this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            foreach (var run in _runs)
            {
                var count = run.Count;
                if (index < count)
                {
                    return run.First + (index * run.Step);
                }

                index -= count;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public bool Contains(int page)
    {
        foreach (var run in _runs)
        {
            if (page >= run.First && page <= run.Last && (page - run.First) % run.Step == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The set restricted to odd or even pages. An interval turns into one run of step 2, so this costs nothing in the size of the set.</summary>
    public PageSet Filter(PageSubset subset)
    {
        if (subset == PageSubset.All)
        {
            return this;
        }

        var wanted = subset == PageSubset.Odd ? 1 : 0;
        var runs = new List<PageRun>(_runs.Length);
        foreach (var run in _runs)
        {
            if (run.Step == 2)
            {
                if (run.First % 2 == wanted)
                {
                    runs.Add(run);
                }

                continue;
            }

            var first = run.First % 2 == wanted ? run.First : run.First + 1;
            var last = run.Last % 2 == wanted ? run.Last : run.Last - 1;
            if (first <= last)
            {
                runs.Add(new PageRun(first, last, 2));
            }
        }

        return new PageSet([.. runs]);
    }

    public IEnumerator<int> GetEnumerator()
    {
        foreach (var run in _runs)
        {
            for (var page = run.First; page <= run.Last; page += run.Step)
            {
                yield return page;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(", ", _runs.Select(r => r.Step == 2 ? $"{r.First}-{r.Last} step 2" : r.First == r.Last ? r.First.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{r.First}-{r.Last}"));
}
