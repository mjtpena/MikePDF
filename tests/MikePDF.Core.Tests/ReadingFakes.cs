using MikePDF.Core.Geometry;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Core.Text;
using MikePDF.Protocol;

namespace MikePDF.Core.Tests;

/// <summary>Lays text out on a US Letter page the way a PDF would report it: PDF user space, y up, every character 6 points wide and 12 high.</summary>
internal static class TextFixtures
{
    public const double Advance = 6;
    public const double LineHeight = 12;

    public static PageGeometry Letter { get; } = new(new PdfRect(0, 0, 612, 792), null, 0);

    /// <summary>A run of text at X whose top edge is Top points below the top of the page.</summary>
    public sealed record Run(string Text, double X, double Top, byte Flags = 0);

    public static PageCharacters Chars(params Run[] runs) => Chars(false, runs);

    /// <summary>Content order is the order of <paramref name="runs"/>. With <paramref name="generateBreaks"/> a generated CR LF follows each run, as PDFium does.</summary>
    public static PageCharacters Chars(bool generateBreaks, params Run[] runs)
    {
        var units = new List<ushort>();
        var lefts = new List<float>();
        var bottoms = new List<float>();
        var rights = new List<float>();
        var tops = new List<float>();
        var flags = new List<byte>();
        void Add(ushort unit, double left, double top, byte flag)
        {
            units.Add(unit);
            lefts.Add((float)left);
            rights.Add((float)(left + Advance));
            tops.Add((float)(792 - top));
            bottoms.Add((float)(792 - top - LineHeight));
            flags.Add(flag);
        }

        foreach (var run in runs)
        {
            var x = run.X;
            foreach (var c in run.Text)
            {
                Add(c, x, run.Top, run.Flags);
                x += Advance;
            }

            if (generateBreaks)
            {
                Add('\r', x, run.Top, PageCharacters.Generated);
                Add('\n', x, run.Top, PageCharacters.Generated);
            }
        }

        return new PageCharacters([.. units], [.. lefts], [.. bottoms], [.. rights], [.. tops], [.. flags], false);
    }

    public static TextModel Model(params Run[] runs) => TextModelBuilder.Build(Chars(runs), Letter, 0);

    public static PageCharacters Unhyphenated(string text) => Chars(new Run(text, 72, 72));
}

/// <summary>An engine whose answers a test writes. Anything not set answers InvalidArgument.</summary>
internal sealed class ScriptedEngine : IReadingEngine
{
    public Func<IReadOnlyList<uint>, uint, uint, ReadReply<GetOutlineReply>>? Outline { get; set; }

    public Func<uint, uint, uint, ReadReply<GetPageLinksReply>>? Links { get; set; }

    public Func<uint, uint, ReadReply<GetLinkUriReply>>? Address { get; set; }

    public Func<uint, ReadReply<GetPageBoxesReply>>? Boxes { get; set; }

    public Func<uint, uint, uint, ReadReply<GetPageTextReply>>? Text { get; set; }

    public Func<uint, string, uint, CancellationToken, Task<ReadReply<SearchPageReply>>>? Search { get; set; }

    public int Calls;

    public Task<ReadReply<GetOutlineReply>> GetOutlineAsync(IReadOnlyList<uint> path, uint first, uint count, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Outline?.Invoke(path, first, count) ?? ReadReply.Fail<GetOutlineReply>(Status.InvalidArgument));
    }

    public Task<ReadReply<GetPageLinksReply>> GetPageLinksAsync(uint page, uint first, uint count, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Links?.Invoke(page, first, count) ?? ReadReply.Fail<GetPageLinksReply>(Status.InvalidArgument));
    }

    public Task<ReadReply<GetLinkUriReply>> GetLinkUriAsync(uint page, uint index, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Address?.Invoke(page, index) ?? ReadReply.Fail<GetLinkUriReply>(Status.InvalidArgument));
    }

    public Task<ReadReply<GetPageBoxesReply>> GetPageBoxesAsync(uint page, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Boxes?.Invoke(page) ?? ReadReply.Fail<GetPageBoxesReply>(Status.InvalidArgument));
    }

    public Task<ReadReply<GetPageTextReply>> GetPageTextAsync(uint page, uint first, uint count, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Text?.Invoke(page, first, count) ?? ReadReply.Fail<GetPageTextReply>(Status.InvalidArgument));
    }

    public Task<ReadReply<SearchPageReply>> SearchPageAsync(uint page, string query, uint flags, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Search?.Invoke(page, query, flags, cancellationToken) ?? Task.FromResult(ReadReply.Fail<SearchPageReply>(Status.InvalidArgument));
    }

    public static ReadReply<T> Ok<T>(T value)
        where T : class => new(Status.Ok, value);

    /// <summary>An honest text reply for a page whose characters are <paramref name="characters"/>.</summary>
    public static ReadReply<GetPageTextReply> TextWindow(PageCharacters characters, uint first, uint count)
    {
        if (first > characters.Count)
        {
            return ReadReply.Fail<GetPageTextReply>(Status.InvalidArgument);
        }

        var n = (int)Math.Min(count, (uint)characters.Count - first);
        return Ok(new GetPageTextReply
        {
            Total = (uint)characters.Count,
            Truncated = characters.Truncated,
            Units = characters.Units.AsSpan((int)first, n).ToArray(),
            Lefts = characters.Lefts.AsSpan((int)first, n).ToArray(),
            Bottoms = characters.Bottoms.AsSpan((int)first, n).ToArray(),
            Rights = characters.Rights.AsSpan((int)first, n).ToArray(),
            Tops = characters.Tops.AsSpan((int)first, n).ToArray(),
            Flags = characters.Flags.AsSpan((int)first, n).ToArray().Select(f => (ushort)f).ToArray(),
        });
    }
}

/// <summary>A search source whose answers are held until a test lets them go.</summary>
internal sealed class ScriptedSearch : ISearchSource
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<ReadReply<PageMatches>>> _held = [];

    public Dictionary<int, PageMatch[]> Matches { get; } = [];

    public Func<int, int, ReadReply<PageMatches>?>? Override { get; set; }

    public List<int> Asked { get; } = [];

    public HashSet<int> HoldPages { get; } = [];

    public Task<ReadReply<PageMatches>> SearchAsync(int page, SearchOptions options, CancellationToken cancellationToken)
    {
        int attempt;
        lock (_gate)
        {
            Asked.Add(page);
            attempt = Asked.Count(p => p == page);
        }

        if (Override?.Invoke(page, attempt) is { } forced)
        {
            return Task.FromResult(forced);
        }

        if (HoldPages.Contains(page))
        {
            var held = new TaskCompletionSource<ReadReply<PageMatches>>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => held.TrySetResult(ReadReply.Fail<PageMatches>(Status.Cancelled)));
            lock (_gate)
            {
                _held[page] = held;
            }

            return held.Task;
        }

        return Task.FromResult(Answer(page));
    }

    public ReadReply<PageMatches> Answer(int page) =>
        new(Status.Ok, new PageMatches(Matches.TryGetValue(page, out var m) ? m : [], false));

    public void Release(int page)
    {
        TaskCompletionSource<ReadReply<PageMatches>>? held;
        lock (_gate)
        {
            _held.TryGetValue(page, out held);
        }

        held?.TrySetResult(Answer(page));
    }

    public bool IsHeld(int page)
    {
        lock (_gate)
        {
            return _held.ContainsKey(page);
        }
    }
}
