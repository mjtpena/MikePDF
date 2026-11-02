using MikePDF.Core.Thumbnails;

namespace MikePDF.Core.Tests;

public sealed class ThumbnailSchedulerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Renders that finish when a test says so, and remember the order they were started in.</summary>
    private sealed class Renders
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<string?>> _held = [];

        public List<int> Started { get; } = [];

        public Task<string?> Render(int page, CancellationToken token)
        {
            var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                Started.Add(page);
                _held[page] = held;
            }

            return held.Task;
        }

        public void Finish(int page, string? result = null)
        {
            TaskCompletionSource<string?> held;
            lock (_gate)
            {
                held = _held[page];
            }

            held.SetResult(result ?? "thumb " + page);
        }

        public int StartedCount
        {
            get
            {
                lock (_gate)
                {
                    return Started.Count;
                }
            }
        }

        public List<int> StartedSnapshot()
        {
            lock (_gate)
            {
                return [.. Started];
            }
        }
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

    [Fact]
    public async Task NoMoreThanTheLimitAreDrawnAtOnceAndTheNewestRequestGoesFirst()
    {
        var renders = new Renders();
        using var scheduler = new ThumbnailScheduler<string>(renders.Render, maxInFlight: 2);
        foreach (var page in new[] { 1, 2, 3, 4, 5 })
        {
            scheduler.Request(page);
        }

        await WaitUntilAsync(() => renders.StartedCount == 2);
        await Task.Delay(50);
        Assert.Equal(2, renders.StartedCount);
        Assert.Equal(3, scheduler.PendingCount);
        // 1 started immediately; by the time 2 was asked for, 1 was already running, so 2 started too. The rest queue newest first.
        renders.Finish(1);
        await WaitUntilAsync(() => renders.StartedCount == 3);
        Assert.Equal(5, renders.StartedSnapshot()[2]);
        renders.Finish(2);
        await WaitUntilAsync(() => renders.StartedCount == 4);
        Assert.Equal(4, renders.StartedSnapshot()[3]);
    }

    [Fact]
    public async Task AnItemThatScrolledAwayBeforeItsTurnIsNeverDrawn()
    {
        var renders = new Renders();
        using var scheduler = new ThumbnailScheduler<string>(renders.Render, maxInFlight: 1);
        scheduler.Request(1);
        await WaitUntilAsync(() => renders.StartedCount == 1);
        scheduler.Request(2);
        scheduler.Request(3);
        scheduler.Cancel(3);
        scheduler.Cancel(99); // nothing to cancel
        renders.Finish(1);
        await WaitUntilAsync(() => renders.StartedCount == 2);
        renders.Finish(2);
        await Task.Delay(50);
        Assert.Equal([1, 2], renders.StartedSnapshot());
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task AFinishedThumbnailIsKeptAnnouncedAndNotDrawnAgain()
    {
        var renders = new Renders();
        using var scheduler = new ThumbnailScheduler<string>(renders.Render);
        var ready = new TaskCompletionSource<(int, string)>();
        scheduler.Ready += (page, thumbnail) => ready.TrySetResult((page, thumbnail));
        scheduler.Request(7);
        await WaitUntilAsync(() => renders.StartedCount == 1);
        Assert.False(scheduler.TryGet(7, out _));
        renders.Finish(7);
        Assert.Equal((7, "thumb 7"), await ready.Task.WaitAsync(Patience));
        Assert.True(scheduler.TryGet(7, out var thumbnail));
        Assert.Equal("thumb 7", thumbnail);
        scheduler.Request(7);
        await Task.Delay(30);
        Assert.Equal(1, renders.StartedCount);
    }

    [Fact]
    public async Task AskingAgainForAWaitingPageMovesItUpAndAPageBeingDrawnIsNotQueuedTwice()
    {
        var renders = new Renders();
        using var scheduler = new ThumbnailScheduler<string>(renders.Render, maxInFlight: 1);
        scheduler.Request(1);
        await WaitUntilAsync(() => renders.StartedCount == 1);
        scheduler.Request(1);           // being drawn: ignored
        scheduler.Request(2);
        scheduler.Request(3);
        scheduler.Request(2);           // back to the front
        Assert.Equal(2, scheduler.PendingCount);
        renders.Finish(1);
        await WaitUntilAsync(() => renders.StartedCount == 2);
        Assert.Equal(2, renders.StartedSnapshot()[1]);
    }

    [Fact]
    public async Task OnlyTheMostRecentOnesAreKept()
    {
        using var scheduler = new ThumbnailScheduler<string>((page, _) => Task.FromResult<string?>("t" + page), maxInFlight: 1, capacity: 3);
        var done = new TaskCompletionSource();
        scheduler.Ready += (page, _) =>
        {
            if (page == 5)
            {
                done.TrySetResult();
            }
        };
        for (var page = 1; page <= 5; page++)
        {
            scheduler.Request(page);
            await WaitUntilAsync(() => scheduler.TryGet(page, out _));
        }

        await done.Task.WaitAsync(Patience);
        Assert.False(scheduler.TryGet(1, out _));
        Assert.False(scheduler.TryGet(2, out _));
        Assert.True(scheduler.TryGet(3, out _));
        Assert.True(scheduler.TryGet(5, out _));
    }

    [Fact]
    public async Task AFailedRenderLeavesNothingAndCanBeRequestedAgain()
    {
        var attempts = 0;
        using var scheduler = new ThumbnailScheduler<string>((page, _) => Task.FromResult<string?>(Interlocked.Increment(ref attempts) == 1 ? null : "ok"));
        scheduler.Request(1);
        await WaitUntilAsync(() => attempts == 1);
        await Task.Delay(30);
        Assert.False(scheduler.TryGet(1, out _));
        scheduler.Request(1);
        await WaitUntilAsync(() => scheduler.TryGet(1, out _));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ClearForgetsEverythingAndDisposeStopsTheWork()
    {
        var renders = new Renders();
        var scheduler = new ThumbnailScheduler<string>(renders.Render, maxInFlight: 1);
        scheduler.Request(1);
        scheduler.Request(2);
        await WaitUntilAsync(() => renders.StartedCount == 1);
        scheduler.Clear();
        Assert.Equal(0, scheduler.PendingCount);
        renders.Finish(1);
        await WaitUntilAsync(() => scheduler.TryGet(1, out _));
        scheduler.Clear();
        Assert.False(scheduler.TryGet(1, out _));

        scheduler.Request(3);
        await WaitUntilAsync(() => renders.StartedCount == 2);
        scheduler.Dispose();
        renders.Finish(3);
        scheduler.Request(4);
        await Task.Delay(50);
        Assert.False(scheduler.TryGet(3, out _));
        Assert.Equal(2, renders.StartedCount);
        Assert.Throws<ArgumentNullException>(() => new ThumbnailScheduler<string>(null!));
    }

    [Fact]
    public async Task ARenderThatThrowsBecauseWeAreClosingIsNotAnError()
    {
        var started = new TaskCompletionSource();
        var scheduler = new ThumbnailScheduler<string>(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        });
        scheduler.Request(0);
        await started.Task.WaitAsync(Patience);
        scheduler.Dispose();
        await Task.Delay(50);
        Assert.False(scheduler.TryGet(0, out _));
    }
}
