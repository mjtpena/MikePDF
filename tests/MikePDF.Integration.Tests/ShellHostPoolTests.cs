using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;
using MikePDF.ShellHost;

namespace MikePDF.Integration.Tests;

/// <summary>ADR-0018 decision 5 and spec 3.3: the engine budget (one fresh engine per file, one spare, at most <c>shell.maxEngines</c>), the queue, and the negative cache (SHELL-08, SHELL-12). No process: engine doubles.</summary>
public sealed class ShellHostPoolTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        for (var waited = 0; waited < milliseconds; waited += 10)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task EngineProcessesNeverExceedTheCapEvenUnderAFlood()
    {
        var factory = new FakeShellEngineFactory { StartDelay = TimeSpan.FromMilliseconds(5) };
        await using var pool = new ShellEnginePool(factory, () => 3);
        pool.Prewarm();

        var done = 0;
        // At most 60 requests are waiting at a time (the queue holds 64 and drops the oldest beyond that): 200 requests in all, through 60 at once.
        using var admitted = new SemaphoreSlim(60);
        var tasks = Enumerable.Range(0, 200).Select(async _ =>
        {
            await admitted.WaitAsync();
            try
            {
                await using var lease = await pool.AcquireAsync(CancellationToken.None);
                await Task.Delay(2);
                Interlocked.Increment(ref done);
            }
            finally
            {
                admitted.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).WaitAsync(Wait);

        Assert.Equal(200, done);
        Assert.True(factory.Peak <= 3, $"{factory.Peak} engines were alive at once");
        Assert.True(pool.Peak <= 3);
        Assert.True(factory.Started >= 200, "a fresh engine for every file: " + factory.Started);
    }

    [Fact]
    public async Task EveryLeaseKillsItsEngineAndNothingIsLeftBehind()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 3);
        for (var i = 0; i < 10; i++)
        {
            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            Assert.True(lease.Engine.IsAlive);
        }

        await pool.DisposeAsync();
        Assert.True(await UntilAsync(() => factory.Alive == 0), "alive: " + factory.Alive);
        Assert.Equal(factory.Started, factory.Kills);
    }

    [Fact]
    public async Task TheSpareStartedAheadSavesTheNextRequestTheProcessStart()
    {
        var factory = new FakeShellEngineFactory { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var pool = new ShellEnginePool(factory, () => 3);
        pool.Prewarm();
        Assert.True(await UntilAsync(() => pool.HasSpare, 5000));

        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var lease = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(started.ElapsedMilliseconds < 150, $"the spare was not used: {started.ElapsedMilliseconds} ms");
        // And the next spare is already on its way.
        Assert.True(await UntilAsync(() => pool.HasSpare, 5000));
    }

    [Fact]
    public async Task TheSpareCountsTowardTheCap()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 2);
        pool.Prewarm();
        Assert.True(await UntilAsync(() => pool.HasSpare));
        await using var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(await UntilAsync(() => pool.HasSpare), "the spare is replaced while there is room");
        Assert.Equal(2, pool.Live);
        // Two engines exist (one leased, one spare): a second request takes the spare, and no third is started.
        await using var second = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(factory.Peak <= 2);
        Assert.Equal(0, pool.Waiting);
    }

    [Fact]
    public async Task RequestsThatCannotHaveAnEngineYetAreServedNewestFirst()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 1);
        var held = await pool.AcquireAsync(CancellationToken.None);
        var served = new System.Collections.Concurrent.ConcurrentQueue<string>();

        async Task Waiter(string name)
        {
            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            served.Enqueue(name);
        }

        var a = Waiter("A");
        await UntilAsync(() => pool.Waiting == 1);
        var b = Waiter("B");
        await UntilAsync(() => pool.Waiting == 2);
        var c = Waiter("C");
        await UntilAsync(() => pool.Waiting == 3);

        await held.DisposeAsync();
        await Task.WhenAll(a, b, c).WaitAsync(Wait);
        Assert.Equal(["C", "B", "A"], served.ToArray());
    }

    [Fact]
    public async Task ARequestWhoseDeadlinePassesWhileQueuedIsDroppedWithoutAnyWorkBeingDoneForIt()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 1);
        await using var held = await pool.AcquireAsync(CancellationToken.None);
        var startedBefore = factory.Started;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pool.AcquireAsync(cts.Token));
        Assert.Equal(0, pool.Waiting);
        Assert.Equal(startedBefore, factory.Started);
    }

    [Fact]
    public async Task TheQueueHoldsAtMostSixtyFourAndTheOldestGivesWay()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 1);
        await using var held = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiters = new List<Task<EngineLease>>();
        for (var i = 0; i < ShellEnginePool.MaxWaiting + 1; i++)
        {
            waiters.Add(pool.AcquireAsync(cts.Token));
            await UntilAsync(() => pool.Waiting == Math.Min(i + 1, ShellEnginePool.MaxWaiting));
        }

        Assert.Equal(ShellEnginePool.MaxWaiting, pool.Waiting);
        var dropped = await Assert.ThrowsAsync<EngineWaitException>(async () => await waiters[0]);
        Assert.Equal(EngineWaitFailure.Dropped, dropped.Failure);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ASpareThatDiedWhileItWaitedIsNotUsed()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 3);
        pool.Prewarm();
        Assert.True(await UntilAsync(() => pool.HasSpare));
        await factory.Engines.Single(e => e.IsAlive).KillAsync(); // the spare is killed from outside

        await using var lease = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(lease.Engine.IsAlive);
    }

    [Fact]
    public async Task AnEngineThatCannotStartFailsTheRequestAndFreesTheSlot()
    {
        var factory = new FakeShellEngineFactory { FailToStart = true };
        await using var pool = new ShellEnginePool(factory, () => 1);
        var failure = await Assert.ThrowsAsync<EngineWaitException>(async () => await pool.AcquireAsync(CancellationToken.None));
        Assert.Equal(EngineWaitFailure.StartFailed, failure.Failure);

        // The next request is not failed with the spare's failure: it starts an engine of its own, now that one can start.
        factory.FailToStart = false;
        await using var lease = await pool.AcquireAsync(CancellationToken.None);
        Assert.True(lease.Engine.IsAlive);
    }

    // SHELL-12 on a hosted runner once saw four engine processes with the cap at three. A kill is a request: a second caller returns before the first has finished, and a process
    // that has been killed is still a process until it has exited. The cap is on processes, so a slot goes back only when the process is gone.

    [Fact]
    public async Task ASlotIsGivenBackOnlyWhenTheKilledProcessHasExited()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 1); // one slot: no spare, the next request needs this one's slot
        var first = await pool.AcquireAsync(CancellationToken.None);
        factory.HoldExits(); // killed engines linger, as processes do for a moment

        // What ShellConnection does when the client goes away mid-request: a background kill, while the session is ended and the lease returned. The lease's own kill is
        // the second caller and returns at once, which is what the real engine's does.
        await first.Engine.KillAsync();
        var returned = first.DisposeAsync().AsTask();
        var second = pool.AcquireAsync(CancellationToken.None);

        await Task.Delay(300);
        Assert.False(returned.IsCompleted, "the lease was returned while its process was still there");
        Assert.False(second.IsCompleted, "the next engine was started while the killed one was still there");
        Assert.Equal(1, factory.Started);
        Assert.Equal(1, factory.Alive);

        factory.ReleaseExits();
        await returned.WaitAsync(Wait);
        await using var next = await second.WaitAsync(Wait);
        Assert.Equal(2, factory.Started);
        Assert.Equal(1, factory.Peak);
        Assert.Equal(1, pool.Peak);
    }

    [Fact]
    public async Task TheCapHoldsWhileEveryKilledEngineIsStillExiting()
    {
        var factory = new FakeShellEngineFactory { StartDelay = TimeSpan.FromMilliseconds(2) };
        await using var pool = new ShellEnginePool(factory, () => 3);
        pool.Prewarm();
        factory.HoldExits();

        // Three requests hold the three slots and end; the three that wait behind them may not start before an exit, however many kills have been asked for.
        var held = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pool.AcquireAsync(CancellationToken.None)));
        var waiting = Enumerable.Range(0, 3).Select(_ => pool.AcquireAsync(CancellationToken.None)).ToArray();
        var endings = held.Select(h => h.DisposeAsync().AsTask()).ToArray();
        await Task.Delay(300);

        Assert.All(waiting, w => Assert.False(w.IsCompleted));
        Assert.True(factory.Peak <= 3, $"{factory.Peak} engines were alive at once");
        Assert.Equal(3, factory.Alive);

        factory.ReleaseExits();
        await Task.WhenAll(endings).WaitAsync(Wait);
        var leases = await Task.WhenAll(waiting).WaitAsync(Wait);
        Assert.True(factory.Peak <= 3, $"{factory.Peak} engines were alive at once");
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task AProcessThatDoesNotExitDoesNotKeepTheSlotForever()
    {
        var factory = new FakeShellEngineFactory();
        await using var pool = new ShellEnginePool(factory, () => 1, exitWait: TimeSpan.FromMilliseconds(300));
        var first = await pool.AcquireAsync(CancellationToken.None);
        factory.HoldExits();
        var returned = first.DisposeAsync().AsTask();
        var second = pool.AcquireAsync(CancellationToken.None);

        // After the bound the slot is given back anyway: a wedged process must not wedge ShellHost with it.
        await returned.WaitAsync(Wait);
        await using var next = await second.WaitAsync(Wait);
        factory.ReleaseExits();
    }

    [Fact]
    public async Task ShuttingDownEndsEveryWaiterAndKillsTheSpare()
    {
        var factory = new FakeShellEngineFactory();
        var pool = new ShellEnginePool(factory, () => 1);
        var held = await pool.AcquireAsync(CancellationToken.None);
        var waiter = pool.AcquireAsync(CancellationToken.None);
        await UntilAsync(() => pool.Waiting == 1);
        await pool.DisposeAsync();
        var closed = await Assert.ThrowsAsync<EngineWaitException>(async () => await waiter);
        Assert.Equal(EngineWaitFailure.Closed, closed.Failure);
        await held.DisposeAsync();
    }

    [Fact]
    public async Task TheCapFollowsThePolicyAtTheTimeOfTheRequest()
    {
        var factory = new FakeShellEngineFactory();
        var cap = 1;
        await using var pool = new ShellEnginePool(factory, () => cap);
        await using var first = await pool.AcquireAsync(CancellationToken.None);
        var second = pool.AcquireAsync(CancellationToken.None);
        await UntilAsync(() => pool.Waiting == 1);
        Assert.False(second.IsCompleted);
        cap = 6; // a policy raised it; the next request to find room takes it
        await using var third = await pool.AcquireAsync(CancellationToken.None);
        await first.DisposeAsync();
        await using var secondLease = await second.WaitAsync(Wait);
    }

    // ---- the negative cache -------------------------------------------------------------------------------------------------------------------------

    private static NegativeCache.FileKey Key(byte seed) => NegativeCache.FileKey.Of(new byte[] { seed, 1, 2, 3 });

    [Fact]
    public void AFailureIsRememberedForTenMinutesAndThenForgotten()
    {
        var clock = new ManualTimeProvider();
        var cache = new NegativeCache(clock);
        cache.Remember(Key(1), Status.EngineCrashed);
        Assert.Equal(Status.EngineCrashed, cache.Lookup(Key(1)));
        Assert.Null(cache.Lookup(Key(2)));

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(Status.EngineCrashed, cache.Lookup(Key(1)));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(cache.Lookup(Key(1)));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TheKeyIsTheSizeAndTheHashOfTheBytes()
    {
        Assert.Equal(NegativeCache.FileKey.Of(new byte[] { 1, 2, 3 }), NegativeCache.FileKey.Of(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(NegativeCache.FileKey.Of(new byte[] { 1, 2, 3 }), NegativeCache.FileKey.Of(new byte[] { 1, 2, 4 }));
        Assert.NotEqual(NegativeCache.FileKey.Of(new byte[] { 1, 2, 3 }), NegativeCache.FileKey.Of(new byte[] { 1, 2, 3, 0 }));
    }

    [Fact]
    public void TheCacheHoldsAtMost1024FilesAndForgetsTheOldestFirst()
    {
        var cache = new NegativeCache(new ManualTimeProvider());
        for (var i = 0; i < NegativeCache.MaxEntries + 10; i++)
        {
            cache.Remember(NegativeCache.FileKey.Of(BitConverter.GetBytes(i)), Status.Damaged);
        }

        Assert.Equal(NegativeCache.MaxEntries, cache.Count);
        Assert.Null(cache.Lookup(NegativeCache.FileKey.Of(BitConverter.GetBytes(0))));
        Assert.Equal(Status.Damaged, cache.Lookup(NegativeCache.FileKey.Of(BitConverter.GetBytes(NegativeCache.MaxEntries + 9))));
    }

    [Theory]
    [InlineData(Status.Damaged, true)]
    [InlineData(Status.PasswordRequired, true)]
    [InlineData(Status.EngineCrashed, true)]
    [InlineData(Status.Timeout, true)]
    [InlineData(Status.PermissionDenied, false)]
    [InlineData(Status.Cancelled, false)]
    [InlineData(Status.InvalidArgument, false)]
    [InlineData(Status.Ok, false)]
    public void OnlyTheFilesOwnFailuresAreRemembered(Status status, bool remembered) => Assert.Equal(remembered, NegativeCache.IsTheFilesFault(status));

    // ---- the fit rule ----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(612, 792, 256, 256, 198, 256)]
    [InlineData(612, 792, 1024, 1024, 791, 1024)]
    [InlineData(200, 300, 32, 32, 21, 32)]
    [InlineData(792, 612, 256, 256, 256, 198)]
    public void APageFitsTheBoxWithItsAspectRatio(float pageWidth, float pageHeight, uint boxWidth, uint boxHeight, uint expectedWidth, uint expectedHeight)
    {
        var fit = FitMath.Fit(pageWidth, pageHeight, boxWidth, boxHeight, FitMath.DefaultMaxScale)!.Value;
        Assert.Equal((expectedWidth, expectedHeight), (fit.Width, fit.Height));
    }

    [Fact]
    public void AThumbnailIsNeverUpscaledBeyondTwiceItsNaturalSize()
    {
        var small = FitMath.Fit(50, 50, 1024, 1024, FitMath.DefaultMaxScale)!.Value;
        Assert.Equal(2f, small.Scale);
        Assert.Equal((100u, 100u), (small.Width, small.Height));
        var preview = FitMath.Fit(50, 50, 1024, 1024, 100f)!.Value;
        Assert.Equal((1024u, 1024u), (preview.Width, preview.Height));
    }

    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(100f, 0f)]
    [InlineData(float.NaN, 100f)]
    [InlineData(float.PositiveInfinity, 100f)]
    [InlineData(-5f, 100f)]
    public void APageWithNoUsableSizeFitsNothing(float width, float height) => Assert.Null(FitMath.Fit(width, height, 256, 256, 2f));
}
