using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Tiles;

namespace MikePDF.Core.Tests;

public sealed class TileSchedulerTests
{
    private static readonly SizeD Letter = new(612, 792);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ViewSnapshot View(double scrollY = 0, int pages = 20, double velocity = 0, double scale = 1.0) =>
        new(new DocumentLayout([.. Enumerable.Repeat(Letter, pages)], 0, scale, 8, 12), 0, scrollY, 800, 600, velocity);

    private static TileSchedulerOptions Fast => new() { FailureBackoff = TimeSpan.FromMilliseconds(60), EngineLostBackoff = TimeSpan.FromMilliseconds(40), TimeoutBackoff = TimeSpan.FromMilliseconds(40), CancelledBackoff = TimeSpan.FromMilliseconds(5) };

    [Fact]
    public async Task TheVisibleTilesAndPlaceholdersEndUpInTheCache()
    {
        var tiles = new FakeRenderer();
        var lowRes = new FakeRenderer();
        var cache = new TileCache(1L << 30);
        await using var scheduler = new TileScheduler(tiles, lowRes, new FakePresenter(), cache, Fast);

        var view = View();
        scheduler.Update(view);
        Assert.True(await scheduler.WaitIdleAsync(Wait));

        var plan = TilePlanner.Plan(view, cache);
        Assert.Empty(plan.VisibleTiles);
        Assert.Empty(plan.VisibleLowRes);
        Assert.True(cache.Contains(TileKey.LowRes(0, 0)));
        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
        Assert.All(tiles.Requests, r => Assert.False(r.Key.IsLowRes));
        Assert.All(lowRes.Requests, r => Assert.True(r.Key.IsLowRes));
        Assert.True(scheduler.Stats.Completed > 0);
    }

    [Fact]
    public async Task NoMoreThanTwoRequestsAreInFlightPerEngine()
    {
        var tiles = new FakeRenderer { Behaviour = async (r, ct) => { await Task.Delay(15, ct); return new RenderResult(RenderStatus.Ok, new FakePixels(r.Width, r.Height)); } };
        var lowRes = new FakeRenderer { Behaviour = async (r, ct) => { await Task.Delay(15, ct); return new RenderResult(RenderStatus.Ok, new FakePixels(r.Width, r.Height)); } };
        await using var scheduler = new TileScheduler(tiles, lowRes, new FakePresenter(), new TileCache(1L << 30), Fast);

        scheduler.Update(View());
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        Assert.InRange(tiles.MaxRunning, 1, 2);
        Assert.InRange(lowRes.MaxRunning, 1, 2);
        Assert.True(tiles.Requests.Count > 2);
    }

    [Fact]
    public async Task ObsoleteRequestsAreCancelledWhenTheViewMovesAway()
    {
        var started = new SemaphoreSlim(0);
        var cancelledKeys = new List<TileKey>();
        var tiles = new FakeRenderer
        {
            Behaviour = async (r, ct) =>
            {
                started.Release();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    lock (cancelledKeys)
                    {
                        cancelledKeys.Add(r.Key);
                    }

                    throw;
                }

                return new RenderResult(RenderStatus.Cancelled);
            },
        };
        var lowRes = new FakeRenderer();
        await using var scheduler = new TileScheduler(tiles, lowRes, new FakePresenter(), new TileCache(1L << 30), Fast);

        scheduler.Update(View(scrollY: 0));
        Assert.True(await started.WaitAsync(Wait));

        scheduler.Update(View(scrollY: 12000)); // far away: nothing from page 0 is wanted any more
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            lock (cancelledKeys)
            {
                if (cancelledKeys.Any(k => k.Page == 0))
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        lock (cancelledKeys)
        {
            Assert.Contains(cancelledKeys, k => k.Page == 0);
        }

        Assert.True(scheduler.Stats.Cancelled > 0);
    }

    [Fact]
    public async Task RequestsStillWantedAreNotCancelledByASmallScroll()
    {
        var cancelled = 0;
        var tiles = new FakeRenderer
        {
            Behaviour = async (r, ct) =>
            {
                try
                {
                    await Task.Delay(200, ct);
                    return new RenderResult(RenderStatus.Ok, new FakePixels(r.Width, r.Height));
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
            },
        };
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), new TileCache(1L << 30), Fast);

        scheduler.Update(View(scrollY: 0));
        await Task.Delay(50);
        scheduler.Update(View(scrollY: 20)); // still the same tiles
        scheduler.Update(View(scrollY: 40));
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        Assert.Equal(0, cancelled);
    }

    [Fact]
    public async Task TheGenerationGrowsWithEveryUpdateAndTravelsWithTheRequests()
    {
        var tiles = new FakeRenderer();
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), new TileCache(1L << 30), Fast);
        Assert.Equal(0UL, scheduler.Generation);
        scheduler.Update(View());
        scheduler.Update(View(scrollY: 5));
        Assert.Equal(2UL, scheduler.Generation);
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        Assert.All(tiles.Requests, r => Assert.Equal(2UL, r.Generation));
    }

    [Fact]
    public async Task AFailedTileIsReportedAndNotRetriedImmediately()
    {
        var statuses = new List<(int, RenderStatus)>();
        var attempts = 0;
        var tiles = new FakeRenderer { Behaviour = (_, _) => { Interlocked.Increment(ref attempts); return Task.FromResult(new RenderResult(RenderStatus.Failed, detail: "damaged")); } };
        var options = Fast with { FailureBackoff = TimeSpan.FromSeconds(30) };
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), new TileCache(1L << 30), options);
        scheduler.PageStatus += (page, status) => { lock (statuses) { statuses.Add((page, status)); } };

        scheduler.Update(View(pages: 1));
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        var first = Volatile.Read(ref attempts);
        await Task.Delay(150);
        Assert.Equal(first, Volatile.Read(ref attempts)); // backing off, no hot loop
        lock (statuses)
        {
            Assert.Contains(statuses, s => s.Item1 == 0 && s.Item2 == RenderStatus.Failed);
        }

        Assert.True(scheduler.Stats.Failed > 0);
    }

    [Fact]
    public async Task ARenderLostToACrashedEngineIsRepeatedAfterABriefPause()
    {
        var calls = 0;
        var tiles = new FakeRenderer
        {
            Behaviour = (r, _) => Task.FromResult(Interlocked.Increment(ref calls) <= 2
                ? new RenderResult(RenderStatus.EngineLost)
                : new RenderResult(RenderStatus.Ok, new FakePixels(r.Width, r.Height))),
        };
        var cache = new TileCache(1L << 30);
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), cache, Fast);

        scheduler.Update(View(pages: 1));
        var deadline = DateTime.UtcNow + Wait;
        while (!cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
        Assert.True(calls >= 3);
    }

    [Fact]
    public async Task APartialRenderIsShownAndFlagged()
    {
        var flagged = new List<RenderStatus>();
        var tiles = new FakeRenderer { Behaviour = (r, _) => Task.FromResult(new RenderResult(RenderStatus.Partial, new FakePixels(r.Width, r.Height))) };
        var cache = new TileCache(1L << 30);
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), cache, Fast);
        scheduler.PageStatus += (_, status) => { lock (flagged) { flagged.Add(status); } };

        scheduler.Update(View(pages: 1));
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
        lock (flagged)
        {
            Assert.Contains(RenderStatus.Partial, flagged);
        }
    }

    [Fact]
    public async Task PixelBuffersAreGivenBackAfterTheUpload()
    {
        var buffers = new List<FakePixels>();
        var tiles = new FakeRenderer
        {
            Behaviour = (r, _) =>
            {
                var pixels = new FakePixels(r.Width, r.Height);
                lock (buffers)
                {
                    buffers.Add(pixels);
                }

                return Task.FromResult(new RenderResult(RenderStatus.Ok, pixels));
            },
        };
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), new TileCache(1L << 30), Fast);
        scheduler.Update(View(pages: 1));
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        lock (buffers)
        {
            Assert.NotEmpty(buffers);
            Assert.All(buffers, b => Assert.True(b.Disposed)); // the ring slot is free again
        }
    }

    [Fact]
    public async Task TilesAreUploadedOnTheWorkerThatReceivedThemNotOnTheCallingThread()
    {
        var uploadThreads = new List<int>();
        var presenter = new ThreadRecordingPresenter(uploadThreads);
        await using var scheduler = new TileScheduler(new FakeRenderer(), new FakeRenderer(), presenter, new TileCache(1L << 30), Fast);

        // A dedicated thread that stays blocked, like the UI thread. A pool thread would not do: its id can come back as a worker's once it awaits.
        var callerThread = 0;
        var idle = false;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            scheduler.Update(View(pages: 1));
            idle = scheduler.WaitIdleAsync(Wait).GetAwaiter().GetResult();
        });
        caller.Start();
        caller.Join();

        Assert.True(idle);
        lock (uploadThreads)
        {
            Assert.NotEmpty(uploadThreads);
            Assert.DoesNotContain(callerThread, uploadThreads);
        }
    }

    [Fact]
    public async Task ZoomingRequestsTilesAtTheNewScaleAndKeepsTheOldOnesForTheFallback()
    {
        var tiles = new FakeRenderer();
        var cache = new TileCache(1L << 30);
        await using var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), cache, Fast);

        scheduler.Update(View(scale: 1.0));
        Assert.True(await scheduler.WaitIdleAsync(Wait));
        scheduler.Update(View(scale: 2.0));
        Assert.True(await scheduler.WaitIdleAsync(Wait));

        Assert.Contains(tiles.Requests, r => r.Scale == 2.0f);
        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
        Assert.True(cache.Contains(TileKey.ForTile(0, 2.0f, 0, 0, 0)));
    }

    [Fact]
    public async Task DisposingCancelsWhatIsInFlight()
    {
        var cancelled = new TaskCompletionSource();
        var started = new SemaphoreSlim(0);
        var tiles = new FakeRenderer
        {
            Behaviour = async (_, ct) =>
            {
                started.Release();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return new RenderResult(RenderStatus.Cancelled);
            },
        };
        var scheduler = new TileScheduler(tiles, new FakeRenderer(), new FakePresenter(), new TileCache(1L << 30), Fast);
        scheduler.Update(View());
        Assert.True(await started.WaitAsync(Wait));

        await scheduler.DisposeAsync();
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(0, tiles.Running);
        await scheduler.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task APresenterFailureDoesNotKillTheScheduler()
    {
        var presenter = new FlakyPresenter();
        var cache = new TileCache(1L << 30);
        await using var scheduler = new TileScheduler(new FakeRenderer(), new FakeRenderer(), presenter, cache, Fast);
        scheduler.Update(View(pages: 1));
        var deadline = DateTime.UtcNow + Wait;
        while (!cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
    }

    /// <summary>
    /// The root cause of the flake in APresenterFailureDoesNotKillTheScheduler (about 2 runs in 100 on x64, more on a busy ARM64 runner): the wake-up that ends a back-off was a
    /// timer; timers count whole milliseconds and the clock does not, so it could fire a hair before the back-off was over. The look-again then found the tile still backing off,
    /// started nothing, and nothing was left to look again. This clock makes that certain: every timer goes off 5 ms early.
    /// </summary>
    [Fact]
    public async Task ATimerThatFiresBeforeTheEndOfABackOffDoesNotLoseTheTile()
    {
        var cache = new TileCache(1L << 30);
        var options = Fast with { FailureBackoff = TimeSpan.FromMilliseconds(100) };
        await using var scheduler = new TileScheduler(new FakeRenderer(), new FakeRenderer(), new FlakyPresenter(), cache, options, new EarlyTimers());
        scheduler.Update(View(pages: 1));
        var deadline = DateTime.UtcNow + Wait;
        while ((!cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)) || !cache.Contains(TileKey.LowRes(0, 0))) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
        Assert.True(cache.Contains(TileKey.LowRes(0, 0)));
    }

    /// <summary>The real clock with timers that go off 5 ms before they were asked to.</summary>
    private sealed class EarlyTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime > TimeSpan.FromMilliseconds(5) ? dueTime - TimeSpan.FromMilliseconds(5) : dueTime, period);
    }

    private sealed class ThreadRecordingPresenter(List<int> threads) : ITilePresenter
    {
        public ITileSurface CreateSurface(IPixelBuffer pixels)
        {
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }

            return new FakeSurface(pixels.Width, pixels.Height);
        }

        public void Present(FrameComposition frame)
        {
        }
    }

    private sealed class FlakyPresenter : ITilePresenter
    {
        private int _calls;

        public ITileSurface CreateSurface(IPixelBuffer pixels)
        {
            if (Interlocked.Increment(ref _calls) <= 2)
            {
                throw new InvalidOperationException("device lost");
            }

            return new FakeSurface(pixels.Width, pixels.Height);
        }

        public void Present(FrameComposition frame)
        {
        }
    }
}

public sealed class FrameComposerTests
{
    private static readonly SizeD Letter = new(612, 792);

    private static ViewSnapshot View(double scale = 1.0) => new(new DocumentLayout([Letter, Letter, Letter], 0, scale, 8, 12), 0, 0, 800, 600, 0);

    [Fact]
    public void WithNothingCachedTheVisibleAreaIsBlank()
    {
        var frame = FrameComposer.Compose(View(), new TileCache(1 << 30));
        Assert.NotEmpty(frame.Pages);
        Assert.True(frame.BlankArea > 0);
        Assert.Equal(0, frame.SharpArea);
        Assert.All(frame.Pages, p => Assert.Empty(p.Layers));
    }

    [Fact]
    public void ThePlaceholderCoversThePageUntilSharpTilesArrive()
    {
        var cache = new TileCache(1L << 30);
        var low = new FakeSurface(160, 207);
        cache.Add(TileKey.LowRes(0, 0), low);

        var frame = FrameComposer.Compose(View(), cache);
        var page0 = frame.Pages.Single(p => p.Page == 0);
        Assert.Single(page0.Layers);
        Assert.Same(low, page0.Layers[0].Surface);
        Assert.Equal(page0.PageRect, page0.Layers[0].Destination);
        Assert.True(frame.FallbackArea > 0);
        Assert.Equal(0, frame.SharpArea);
    }

    [Fact]
    public void SharpTilesAreDrawnOverThePlaceholderAtTheirPlace()
    {
        var cache = new TileCache(1L << 30);
        cache.Add(TileKey.LowRes(0, 0), new FakeSurface(160, 207));
        var tile = new FakeSurface(512, 512);
        cache.Add(TileKey.ForTile(0, 1.0f, 0, 0, 0), tile);

        var frame = FrameComposer.Compose(View(), cache);
        var layers = frame.Pages.Single(p => p.Page == 0).Layers;
        Assert.Equal(2, layers.Count);
        Assert.Same(tile, layers[1].Surface); // on top
        var page = frame.Pages.Single(p => p.Page == 0).PageRect;
        Assert.Equal(page.X, layers[1].Destination.X);
        Assert.Equal(page.Y, layers[1].Destination.Y);
        Assert.Equal(512, layers[1].Destination.Width);
        Assert.True(frame.SharpArea > 0);
    }

    [Fact]
    public void AfterAZoomChangeTilesOfTheOldScaleStayAsAStretchedFallback()
    {
        var cache = new TileCache(1L << 30);
        var old = new FakeSurface(512, 512);
        cache.Add(TileKey.ForTile(0, 1.0f, 0, 0, 0), old); // the top-left 512 x 512 of the page at 100%: a bit over 0.8 of its width
        var frame = FrameComposer.Compose(View(scale: 2.0), cache);

        var page = frame.Pages.Single(p => p.Page == 0);
        var drawn = Assert.Single(page.Layers);
        Assert.Same(old, drawn.Surface);
        // 512 of 612 px of width, 512 of 792 of height, on a page now drawn at twice the size.
        Assert.Equal(page.PageRect.Width * 512 / 612, drawn.Destination.Width, 3);
        Assert.Equal(page.PageRect.Height * 512 / 792, drawn.Destination.Height, 3);
        Assert.Equal(page.PageRect.X, drawn.Destination.X, 3);
        Assert.True(frame.BlankArea < page.PageRect.Width * page.PageRect.Height * 3); // not everything is blank
        Assert.True(frame.FallbackArea > 0);
    }

    [Fact]
    public void TilesBeyondTheEngineLimitAreStretchedOntoTheirPlace()
    {
        var layout = new DocumentLayout([new SizeD(100, 100)], 0, layoutScale: 128, gapPx: 0, marginPx: 0);
        var view = new ViewSnapshot(layout, 0, 0, 800, 600, 0);
        var cache = new TileCache(1L << 30);
        cache.Add(TileKey.ForTile(0, 64f, 0, 0, 0), new FakeSurface(512, 512));
        var frame = FrameComposer.Compose(view, cache);
        var drawn = Assert.Single(frame.Pages[0].Layers);
        Assert.Equal(1024, drawn.Destination.Width); // 512 render pixels stretched x2
    }

    [Fact]
    public void WhatIsDrawnIsProtectedFromEvictionForTheFrame()
    {
        var cost = 512L * 512 * 4 * 2;
        var cache = new TileCache(cost * 2);
        var shown = new FakeSurface();
        cache.Add(TileKey.ForTile(0, 1.0f, 0, 0, 0), shown);
        FrameComposer.Compose(View(), cache);
        cache.Add(TileKey.ForTile(9, 1.0f, 0, 0, 0), new FakeSurface());
        cache.Add(TileKey.ForTile(9, 1.0f, 0, 1, 0), new FakeSurface());
        Assert.True(cache.Contains(TileKey.ForTile(0, 1.0f, 0, 0, 0)));
    }

    [Fact]
    public void TheGestureScaleIsPassedThrough()
    {
        var frame = FrameComposer.Compose(View(), new TileCache(1 << 20), gestureScale: 1.5, gestureOrigin: new PointD(10, 20));
        Assert.Equal(1.5, frame.GestureScale);
        Assert.Equal(new PointD(10, 20), frame.GestureOrigin);
    }

    [Fact]
    public void AnEmptyDocumentComposesAnEmptyFrame()
    {
        var frame = FrameComposer.Compose(new ViewSnapshot(new DocumentLayout([], 0, 1, 0, 0), 0, 0, 800, 600, 0), new TileCache(1 << 20));
        Assert.Empty(frame.Pages);
    }
}
