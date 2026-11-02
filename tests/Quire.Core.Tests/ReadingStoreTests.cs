using Quire.Core.Geometry;
using Quire.Core.Reading;
using Quire.Core.Text;
using Quire.Protocol;
using static Quire.Core.Tests.ScriptedEngine;
using static Quire.Core.Tests.TextFixtures;

namespace Quire.Core.Tests;

public sealed class PageDataCacheTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task APageAskedForTwiceAtOnceIsFetchedOnce()
    {
        var gate = new TaskCompletionSource();
        var fetches = 0;
        using var cache = new PageDataCache<string>(async (page, _) =>
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            return Ok("page " + page);
        }, 4);

        var a = cache.GetAsync(2);
        var b = cache.GetAsync(2);
        gate.SetResult();
        Assert.Equal("page 2", (await a.WaitAsync(Patience)).Value);
        Assert.Equal("page 2", (await b.WaitAsync(Patience)).Value);
        Assert.Equal("page 2", (await cache.GetAsync(2)).Value);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task TheLeastRecentlyUsedPageGoesFirst()
    {
        var fetches = new List<int>();
        using var cache = new PageDataCache<string>((page, _) =>
        {
            lock (fetches)
            {
                fetches.Add(page);
            }

            return Task.FromResult(Ok("p" + page));
        }, 2);
        await cache.GetAsync(1);
        await cache.GetAsync(2);
        await cache.GetAsync(1);   // 2 is now the oldest
        await cache.GetAsync(3);   // evicts 2
        Assert.True(cache.TryGetLoaded(1, out _));
        Assert.True(cache.TryGetLoaded(3, out var three));
        Assert.Equal("p3", three);
        Assert.False(cache.TryGetLoaded(2, out _));
        await cache.GetAsync(2);
        Assert.Equal([1, 2, 3, 2], fetches);
    }

    [Fact]
    public async Task OnlyFailuresThatWouldRepeatAreRemembered()
    {
        var calls = 0;
        var status = Status.Damaged;
        using var cache = new PageDataCache<string>((_, _) =>
        {
            calls++;
            return Task.FromResult(ReadReply.Fail<string>(status));
        }, 4);

        Assert.Equal(Status.Damaged, (await cache.GetAsync(0)).Status);
        Assert.Equal(Status.Damaged, (await cache.GetAsync(0)).Status);
        Assert.Equal(1, calls); // damage is final

        status = Status.EngineCrashed;
        await cache.GetAsync(1);
        await cache.GetAsync(1);
        Assert.Equal(3, calls); // a crash is not: the next ask tries again
        Assert.False(cache.TryGetLoaded(1, out _));
    }

    [Fact]
    public async Task AWaiterCanStopWaitingWithoutStoppingTheFetch()
    {
        var gate = new TaskCompletionSource();
        using var cache = new PageDataCache<string>(async (_, _) =>
        {
            await gate.Task;
            return Ok("done");
        }, 4);
        using var impatient = new CancellationTokenSource();
        var waiter = cache.GetAsync(0, impatient.Token);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        gate.SetResult();
        Assert.Equal("done", (await cache.GetAsync(0).WaitAsync(Patience)).Value);
    }

    [Fact]
    public async Task LoadedIsRaisedOnceForASuccessAndClearForgetsEverything()
    {
        var loaded = new List<int>();
        using var cache = new PageDataCache<string>((page, _) => Task.FromResult(Ok("p" + page)), 4);
        cache.Loaded += page =>
        {
            lock (loaded)
            {
                loaded.Add(page);
            }
        };
        await cache.GetAsync(5);
        await cache.GetAsync(5);
        Assert.Equal([5], loaded);
        cache.Clear();
        Assert.False(cache.TryGetLoaded(5, out _));
        Assert.Throws<ArgumentNullException>(() => new PageDataCache<string>(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageDataCache<string>((_, _) => Task.FromResult(Ok("x")), 0));
    }

    [Fact]
    public async Task DisposingCancelsTheFetchesInFlight()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        var cache = new PageDataCache<string>(async (_, token) =>
        {
            seen = token;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Ok("never");
        }, 4);
        var pending = cache.GetAsync(0);
        await started.Task.WaitAsync(Patience);
        cache.Dispose();
        Assert.Equal(Status.Cancelled, (await pending.WaitAsync(Patience)).Status);
        Assert.True(seen.IsCancellationRequested);
    }
}

public sealed class ReadingStoreTests
{
    private static ReadingStore StoreFor(PageCharacters page, ScriptedEngine? engine = null)
    {
        engine ??= new ScriptedEngine();
        engine.Text ??= (_, first, count) => TextWindow(page, first, count);
        engine.Boxes ??= _ => Ok(new GetPageBoxesReply { MediaRight = 612, MediaTop = 792 });
        return new ReadingStore(new DocumentReader(engine, 10));
    }

    [Fact]
    public async Task APagesTextModelIsBuiltFromItsCharactersAndGeometryAndKept()
    {
        var engine = new ScriptedEngine();
        using var store = StoreFor(Chars(new Run("hello there", 72, 72)), engine);
        var loaded = new TaskCompletionSource<int>();
        store.ModelLoaded += page => loaded.TrySetResult(page);

        Assert.Null(store.GetLoaded(3));
        var model = (await store.GetModelAsync(3)).Value!;
        Assert.Equal("hello there", model.Text);
        Assert.Equal(3, await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(model, store.GetLoaded(3));
        Assert.Equal(10, store.PageCount);
        var calls = engine.Calls;
        await store.GetModelAsync(3);
        Assert.Equal(calls, engine.Calls); // no second trip to the engine
    }

    [Fact]
    public async Task AFailureInTheCharactersOrTheGeometryIsAFailureOfTheModel()
    {
        using var noText = StoreFor(Chars(), new ScriptedEngine { Text = (_, _, _) => ReadReply.Fail<GetPageTextReply>(Status.Damaged) });
        Assert.Equal(Status.Damaged, (await noText.GetModelAsync(0)).Status);

        using var noBoxes = StoreFor(Chars(new Run("x", 72, 72)), new ScriptedEngine { Boxes = _ => ReadReply.Fail<GetPageBoxesReply>(Status.Damaged) });
        Assert.Equal(Status.Damaged, (await noBoxes.GetModelAsync(0)).Status);
    }

    [Fact]
    public async Task LinksAreKeptAndAnnounced()
    {
        var engine = new ScriptedEngine
        {
            Links = (_, first, count) => Ok(new GetPageLinksReply
            {
                Total = 1, Lefts = [1], Bottoms = [1], Rights = [2], Tops = [2], Kinds = [0], Pages = [0], Xs = [0], Ys = [0],
            }),
        };
        using var store = StoreFor(Chars(), engine);
        var announced = new TaskCompletionSource<int>();
        store.LinksLoaded += page => announced.TrySetResult(page);
        var links = await store.Links.GetAsync(2);
        Assert.Single(links.Value!.Links);
        Assert.Equal(2, await announced.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

public sealed class PageGeometryAndMatchTests
{
    [Fact]
    public void AMatchHasOneBoxPerLineItCrosses()
    {
        var chars = Chars(new Run("find me here", 72, 72), new Run("and here", 72, 88));
        var rects = MatchGeometry.RectsFor(chars, Letter, 5, 12); // "me here" + the first of the next line
        Assert.Equal(2, rects.Count);
        Assert.Equal(72 + (5 * Advance), rects[0].X, 3);
        Assert.Equal(7 * Advance, rects[0].Width, 3);
        Assert.Equal(72, rects[1].X, 3);
        Assert.Equal(88, rects[1].Y, 3);
    }

    [Fact]
    public void MadeUpCharactersAndOutOfRangeRequestsDoNotWidenOrBreakAMatch()
    {
        var chars = Chars(true, new Run("one", 72, 72), new Run("two", 72, 88));
        var rects = MatchGeometry.RectsFor(chars, Letter, 0, 3);
        Assert.Single(rects);
        Assert.Equal(3 * Advance, rects[0].Width, 3);
        Assert.Empty(MatchGeometry.RectsFor(chars, Letter, 9999, 5));
        Assert.Empty(MatchGeometry.RectsFor(chars, Letter, -5, 0));
        Assert.Single(MatchGeometry.RectsFor(chars, Letter, -2, 5)); // clamped to the start
        Assert.Equal(2, MatchGeometry.RectsFor(chars, Letter, 0, int.MaxValue).Count);
    }

    [Fact]
    public void ABoxIsFollowedOnARotatedPage()
    {
        var chars = Chars(new Run("hello", 72, 72));
        var rotated = new PageGeometry(new PdfRect(0, 0, 612, 792), null, 90);
        var rect = Assert.Single(MatchGeometry.RectsFor(chars, rotated, 0, 5));
        Assert.True(rect.Height > rect.Width); // upright text on a page turned a quarter: the match runs down
    }

    [Fact]
    public void CharactersMustHaveOneEntryEach()
    {
        Assert.Throws<ArgumentException>(() => new PageCharacters([1], [], [], [], [], [], false));
        Assert.Throws<ArgumentNullException>(() => new PageCharacters(null!, [], [], [], [], [], false));
        Assert.Equal(PdfRect.FromCorners(1, 2, 3, 4), new PageCharacters([65], [1], [2], [3], [4], [0], false).BoxOf(0));
    }
}
