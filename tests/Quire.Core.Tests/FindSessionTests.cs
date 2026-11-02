using Quire.Core.Reading;
using Quire.Core.Search;
using Quire.Protocol;

namespace Quire.Core.Tests;

public sealed class FindSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static PageMatch[] Hits(params int[] starts) => [.. starts.Select(s => new PageMatch(s, 3))];

    private static async Task<FindSession> RunToEndAsync(ScriptedSearch source, int pageCount, int startPage, Func<FindSession, FindSession>? configure = null)
    {
        var session = new FindSession(source, pageCount, new SearchOptions("abc"), startPage) { RetryDelay = TimeSpan.FromMilliseconds(1) };
        session = configure?.Invoke(session) ?? session;
        await session.StartAsync().WaitAsync(Patience);
        return session;
    }

    [Fact]
    public async Task PagesAreSearchedFromTheStartingPageAndRoundThroughTheBeginning()
    {
        var source = new ScriptedSearch();
        using var session = await RunToEndAsync(source, 5, 3);
        Assert.Equal([3, 4, 0, 1, 2], source.Asked);
        Assert.True(session.Progress.IsComplete);
        Assert.Equal(5, session.Progress.PagesSearched);
        Assert.Equal(1.0, session.Progress.Fraction);
        Assert.Equal(0, session.Progress.MatchCount);
    }

    [Fact]
    public async Task MatchesAreNumberedInTheOrderTheyAreFoundAndNextGoesRoundTheEnd()
    {
        var source = new ScriptedSearch();
        source.Matches[1] = Hits(5, 20);
        source.Matches[3] = Hits(7);
        using var session = await RunToEndAsync(source, 5, 3);

        Assert.Equal(3, session.Progress.MatchCount);
        Assert.Equal(0, session.CurrentOrdinal);
        var first = await session.NextAsync();
        Assert.Equal(new FoundMatch(3, 0, new PageMatch(7, 3)), first);
        Assert.Equal(1, session.CurrentOrdinal);
        Assert.Equal(new FoundMatch(1, 0, new PageMatch(5, 3)), await session.NextAsync());
        Assert.Equal(new FoundMatch(1, 1, new PageMatch(20, 3)), await session.NextAsync());
        Assert.Equal(3, session.CurrentOrdinal);
        Assert.Equal(first, await session.NextAsync()); // round again
        Assert.Equal(new FoundMatch(1, 1, new PageMatch(20, 3)), session.Previous());
        Assert.Equal(new FoundMatch(1, 0, new PageMatch(5, 3)), session.Previous());
        Assert.Equal(first, session.Previous());
        Assert.Equal(new FoundMatch(1, 1, new PageMatch(20, 3)), session.Previous()); // before the first: the last
        Assert.Equal(2, session.MatchesOnPage(1).Count);
        Assert.Empty(session.MatchesOnPage(4));
    }

    [Fact]
    public async Task NextWaitsForTheSearchToFindAnotherMatchInsteadOfGoingRoundEarly()
    {
        var source = new ScriptedSearch();
        source.Matches[0] = Hits(1);
        source.Matches[2] = Hits(9);
        source.HoldPages.Add(2);
        using var session = new FindSession(source, 4, new SearchOptions("abc"), 0);
        _ = session.StartAsync();
        Assert.Equal(new FoundMatch(0, 0, new PageMatch(1, 3)), await session.NextAsync().WaitAsync(Patience));

        var next = session.NextAsync();
        await WaitUntilAsync(() => source.IsHeld(2));
        Assert.False(next.IsCompleted); // nothing later is known yet, and the search is not over
        source.Release(2);
        Assert.Equal(new FoundMatch(2, 0, new PageMatch(9, 3)), await next.WaitAsync(Patience));
    }

    [Fact]
    public async Task NextOnASearchWithNoMatchesEndsWithNull()
    {
        var source = new ScriptedSearch();
        using var session = new FindSession(source, 3, new SearchOptions("abc"), 0);
        _ = session.StartAsync();
        Assert.Null(await session.NextAsync().WaitAsync(Patience));
        Assert.Null(session.Previous());
        Assert.True(session.Progress.IsComplete);
    }

    [Fact]
    public async Task CancellingStopsAskingAndKeepsWhatWasFound()
    {
        var source = new ScriptedSearch();
        source.Matches[0] = Hits(4);
        source.HoldPages.Add(1);
        using var session = new FindSession(source, 100, new SearchOptions("abc"), 0);
        var changes = 0;
        session.Changed += () => Interlocked.Increment(ref changes);
        var run = session.StartAsync();
        await WaitUntilAsync(() => source.IsHeld(1));

        session.Cancel();
        await run.WaitAsync(Patience);

        Assert.Equal([0, 1], source.Asked); // nothing after the page in flight
        Assert.True(session.Progress.IsCancelled);
        Assert.False(session.Progress.IsComplete);
        Assert.Equal(1, session.Progress.MatchCount);
        Assert.Equal(new FoundMatch(0, 0, new PageMatch(4, 3)), await session.NextAsync().WaitAsync(Patience)); // what was found can still be walked
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task ACancelledSessionNeverWaitsForever()
    {
        var source = new ScriptedSearch();
        source.HoldPages.Add(0);
        using var session = new FindSession(source, 10, new SearchOptions("abc"), 0);
        _ = session.StartAsync();
        await WaitUntilAsync(() => source.IsHeld(0));
        var next = session.NextAsync();
        session.Cancel();
        Assert.Null(await next.WaitAsync(Patience));

        using var abandoned = new CancellationTokenSource();
        using var other = new FindSession(source, 10, new SearchOptions("abc"), 0);
        _ = other.StartAsync();
        var waiting = other.NextAsync(abandoned.Token);
        await abandoned.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
    }

    [Fact]
    public async Task AnEngineThatRestartsIsAskedAgainAndAPageThatKeepsFailingIsSkipped()
    {
        var source = new ScriptedSearch();
        source.Matches[0] = Hits(1);
        source.Matches[1] = Hits(2);
        source.Matches[3] = Hits(3);
        source.Override = (page, attempt) => page switch
        {
            0 when attempt <= 2 => ReadReply.Fail<PageMatches>(Status.EngineCrashed),
            1 => ReadReply.Fail<PageMatches>(Status.Timeout),
            2 => ReadReply.Fail<PageMatches>(Status.Damaged),
            _ => null,
        };
        using var session = await RunToEndAsync(source, 4, 0);
        Assert.Equal(3, source.Asked.Count(p => p == 0));       // two crashes, then the answer
        Assert.Equal(4, source.Asked.Count(p => p == 1));       // the first try and three more
        Assert.Equal(1, source.Asked.Count(p => p == 2));       // damage is final
        Assert.Equal(2, session.Progress.PagesFailed);
        Assert.Equal(2, session.Progress.MatchCount);           // pages 0 and 3
        Assert.True(session.Progress.IsComplete);
    }

    [Fact]
    public async Task AQueryTheEngineRefusesEndsTheSearchAtOnce()
    {
        var source = new ScriptedSearch { Override = (_, _) => ReadReply.Fail<PageMatches>(Status.InvalidArgument) };
        using var session = await RunToEndAsync(source, 50, 0);
        Assert.Single(source.Asked);
        Assert.True(session.Progress.QueryRefused);
        Assert.True(session.Progress.IsComplete);
    }

    [Fact]
    public async Task TruncationOnAnyPageIsReported()
    {
        var source = new ScriptedSearch { Override = (page, _) => page == 1 ? new ReadReply<PageMatches>(Status.Ok, new PageMatches(Hits(1), true)) : null };
        using var session = await RunToEndAsync(source, 3, 0);
        Assert.True(session.Progress.AnyTruncated);
    }

    [Fact]
    public async Task AnEmptyDocumentCompletesAtOnce()
    {
        using var session = await RunToEndAsync(new ScriptedSearch(), 0, 7);
        Assert.True(session.Progress.IsComplete);
        Assert.Equal(1.0, session.Progress.Fraction);
        Assert.Null(await session.NextAsync().WaitAsync(Patience));
    }

    [Fact]
    public async Task AStartingPageOutsideTheDocumentIsClamped()
    {
        var source = new ScriptedSearch();
        using var session = await RunToEndAsync(source, 3, 99);
        Assert.Equal([2, 0, 1], source.Asked);
        using var negative = await RunToEndAsync(new ScriptedSearch(), 3, -4);
        Assert.True(negative.Progress.IsComplete);
    }

    [Fact]
    public async Task StartingTwiceIsTheSameRunAndDisposingIsSafeAnyTime()
    {
        var source = new ScriptedSearch();
        var session = new FindSession(source, 3, new SearchOptions("abc"), 0);
        Assert.Same(session.StartAsync(), session.StartAsync());
        await session.StartAsync().WaitAsync(Patience);
        session.Dispose();
        session.Dispose();
        Assert.Throws<ArgumentNullException>(() => new FindSession(null!, 1, new SearchOptions("x"), 0));
    }

    [Fact]
    public async Task ManyThreadsReadingWhileTheSearchRunsSeeConsistentNumbers()
    {
        var source = new ScriptedSearch();
        for (var page = 0; page < 200; page++)
        {
            source.Matches[page] = Hits(1, 2, 3);
        }

        using var session = new FindSession(source, 200, new SearchOptions("abc"), 0);
        var run = session.StartAsync();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!run.IsCompleted)
            {
                var progress = session.Progress;
                Assert.Equal(progress.PagesSearched * 3, progress.MatchCount);
                var current = session.Current;
                Assert.True(current is null || current.Value.Page >= 0);
            }
        })).ToArray();
        await Task.WhenAll(readers).WaitAsync(Patience);
        Assert.Equal(600, session.Progress.MatchCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}
