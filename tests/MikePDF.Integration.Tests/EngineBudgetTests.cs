using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// W2-engine item 1 (ADR-0004): one shared pool engine, a cap on document engines, least-recently-used suspension and a resume that does not need the caller to know. The
/// budget is exercised here at the broker level with several documents at once, as the tabs will use it; the app still opens one document at a time.
/// </summary>
public sealed class EngineBudgetTests(SandboxFixture sandbox)
{
    private static readonly RenderTileArgs Corner = new(0, 1f, 0, 0, 0, 200, 300);

    private EngineBudget NewBudget(int cap, TimeSpan? slotWait = null, Func<EngineSupervisorOptions, EngineSupervisorOptions>? document = null) => new(new EngineBudgetOptions
    {
        Document = sandbox.Options(o => document is null ? o with { SlotCount = 4, SlotSize = 1 << 20 } : document(o with { SlotCount = 4, SlotSize = 1 << 20 })),
        Pool = sandbox.Options(o => o with { SlotCount = 4, SlotSize = 1 << 20 }),
        MaxDocumentEngines = cap,
        SlotWaitTimeout = slotWait ?? TimeSpan.FromSeconds(60),
    });

    /// <summary>A document engine with the rectangles fixture open in it, drawn once (so it has been used).</summary>
    private static async Task<(DocumentEngine Engine, SupervisedDocument Document)> OpenAsync(EngineBudget budget, string fixture = "rectangles.pdf")
    {
        var engine = await budget.CreateDocumentEngineAsync();
        var opened = await engine.OpenDocumentAsync(SandboxFixture.FixturePath(fixture));
        Assert.Equal(Status.Ok, opened.Status);
        await RenderAsync(opened.Value!);
        return (engine, opened.Value!);
    }

    private static async Task<byte[]> RenderAsync(SupervisedDocument document, TimeSpan? deadline = null)
    {
        var result = await document.RenderTileAsync(Corner, 1, deadline);
        Assert.Equal(Status.Ok, result.Status);
        using var tile = result.Value!;
        return tile.Pixels.ToArray();
    }

    /// <summary>Counts the engine processes of this test run while it runs, as often as it can, and remembers the most there were at once.</summary>
    private sealed class ProcessMonitor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly string _directory;
        private int _max;

        public ProcessMonitor(string directory)
        {
            _directory = directory;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var now = NativeTestMethods.RunningEnginesIn(_directory);
                    int seen;
                    while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
                    {
                    }

                    await Task.Delay(2, CancellationToken.None);
                }
            });
        }

        public int Max => Volatile.Read(ref _max);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task TheDefaultCapIsSixDocumentEnginesAndTheBudgetRefusesNone()
    {
        Assert.Equal(6, new EngineBudgetOptions { Document = sandbox.Options(), Pool = sandbox.Options() }.MaxDocumentEngines);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineBudget(new EngineBudgetOptions { Document = sandbox.Options(), Pool = sandbox.Options(), MaxDocumentEngines = 0 }));
        await using var budget = new EngineBudget(new EngineBudgetOptions { Document = sandbox.Options(), Pool = sandbox.Options() });
        Assert.Equal(6, budget.MaxDocumentEngines);
    }

    [Fact]
    public async Task EveryDocumentSharesOneSingleSharedPoolEngine()
    {
        var before = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        await using var budget = NewBudget(cap: 3);
        var first = await OpenAsync(budget);
        var second = await OpenAsync(budget);
        var third = await OpenAsync(budget);

        var pool = await budget.GetPoolAsync();
        Assert.Same(pool, await budget.GetPoolAsync());
        Assert.Same(pool, await budget.GetPoolAsync());

        // Each document is open in the pool as well (thumbnails, text, search), all in the one pool process, with one process each for their own tiles.
        var inPool = new List<SupervisedDocument>();
        foreach (var path in new[] { "rectangles.pdf", "hello_world.pdf", "hello_world_2_pages.pdf" })
        {
            var opened = await pool.OpenDocumentAsync(SandboxFixture.FixturePath(path));
            Assert.Equal(Status.Ok, opened.Status);
            inPool.Add(opened.Value!);
        }

        Assert.All(inPool, d => Assert.True(d.PageCount >= 1));
        Assert.Equal(3 + 1, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) - before); // three document engines and the pool: not three pools
        Assert.NotEqual(pool.EngineProcessId, first.Engine.EngineProcessId);
        Assert.NotEqual(first.Engine.EngineProcessId, second.Engine.EngineProcessId);
        Assert.NotEqual(second.Engine.EngineProcessId, third.Engine.EngineProcessId);
        Assert.Equal(pool.EngineProcessId, budget.PoolProcessId);
    }

    [Fact]
    public async Task OverTheCapTheLeastRecentlyUsedDocumentIsSuspendedAndTheProcessesNeverExceedTheCapPlusThePool()
    {
        var before = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        await using var budget = NewBudget(cap: 3);
        await budget.GetPoolAsync();
        await using var monitor = new ProcessMonitor(sandbox.DeployDirectory);
        var a = await OpenAsync(budget);
        var b = await OpenAsync(budget);
        var c = await OpenAsync(budget);
        await RenderAsync(a.Document); // a was used last, so b is the one that has gone longest unused

        var d = await OpenAsync(budget);

        Assert.Equal(DocumentEngineState.Suspended, b.Engine.State);
        Assert.Equal(0u, b.Engine.EngineProcessId);
        Assert.All(new[] { a.Engine, c.Engine, d.Engine }, e => Assert.Equal(DocumentEngineState.Running, e.State));
        Assert.Equal(3, budget.LiveDocumentEngines);
        Assert.Equal(1, budget.SuspendedDocumentEngines);
        Assert.Equal(4, budget.DocumentEngines.Count);
        Assert.Equal(3 + 1, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) - before);
        Assert.True(monitor.Max - before <= 3 + 1, $"{monitor.Max - before} engine processes at once (cap 3 plus the pool)");
    }

    [Fact]
    public async Task ASuspendedDocumentResumesOnItsNextCallAndDrawsExactlyWhatItDrewBefore()
    {
        await using var budget = NewBudget(cap: 2);
        var a = await OpenAsync(budget);
        var b = await OpenAsync(budget);
        var drawnBefore = await RenderAsync(a.Document);
        await RenderAsync(b.Document); // a is now the least recently used
        var c = await OpenAsync(budget);
        Assert.Equal(DocumentEngineState.Suspended, a.Engine.State);
        var states = new List<DocumentEngineState>();
        a.Engine.StateChanged += states.Add;
        var crashes = 0;
        a.Engine.EngineCrashed += (_, _) => crashes++;

        var clock = Stopwatch.StartNew();
        var info = await a.Document.GetInfoAsync(); // the first call that needs the engine starts one
        var resumeMs = clock.ElapsedMilliseconds;
        var drawnAfter = await RenderAsync(a.Document);

        Assert.True(info.IsOk, info.Detail);
        Assert.Equal(drawnBefore, drawnAfter);
        Assert.Equal(a.Document.PageCount, info.Value!.PageCount);
        Assert.Equal(DocumentEngineState.Running, a.Engine.State);
        Assert.Equal([DocumentEngineState.Resuming, DocumentEngineState.Running], states);
        Assert.Equal(0, crashes);              // a resume is not a crash and shows no banner
        Assert.Equal(0, a.Engine.RestartCount);
        Assert.Equal(2, budget.LiveDocumentEngines);
        Assert.NotEqual(DocumentEngineState.Running, budget.DocumentEngines.Select(e => e.State).Single(s => s != DocumentEngineState.Running)); // exactly one other is suspended now
        Assert.Contains(budget.DocumentEngines, e => e.State == DocumentEngineState.Suspended);
        Timings.Record($"resume of a suspended document (a new sandboxed engine, the file reopened, the first call answered): {resumeMs} ms on this machine; c={c.Engine.State}");
        Assert.True(resumeMs < 5000, $"resuming took {resumeMs} ms");
    }

    [Fact]
    public async Task ADocumentWithACallInFlightIsNeverTheOneThatGivesWay()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var budget = NewBudget(cap: 2);
        var busy = await budget.CreateDocumentEngineAsync();
        var opened = await busy.OpenDocumentAsync(slow.Path);
        Assert.Equal(Status.Ok, opened.Status);
        var idle = await OpenAsync(budget); // used after the busy one was opened, so the busy engine is the older of the two

        var render = opened.Value!.RenderTileAsync(new RenderTileArgs(0, 4f, 0, 0, 0, 512, 512), 1, TimeSpan.FromMinutes(2));
        await Task.Delay(200); // the call is in flight now
        var newcomer = await OpenAsync(budget);

        Assert.Equal(DocumentEngineState.Running, busy.State);
        Assert.Equal(DocumentEngineState.Suspended, idle.Engine.State);
        Assert.Equal(DocumentEngineState.Running, newcomer.Engine.State);
        using var tile = (await render).Value!;
        Assert.NotNull(tile);
        Assert.Equal(0, busy.RestartCount);
    }

    [Fact]
    public async Task APinnedEngineIsNeverSuspendedAndAnEngineThatCannotGiveWayMakesTheNewcomerWaitThenTimeOut()
    {
        await using var budget = NewBudget(cap: 2, slotWait: TimeSpan.FromSeconds(1));
        var before = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        var a = await OpenAsync(budget);
        var b = await OpenAsync(budget);
        var pinA = a.Engine.Pin();
        var pinB = b.Engine.Pin();

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => budget.CreateDocumentEngineAsync());
        Assert.InRange(clock.ElapsedMilliseconds, 900, 10_000); // it waited for a slot before it gave up
        Assert.Equal(2, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) - before); // and started no third process
        Assert.Equal(DocumentEngineState.Running, a.Engine.State);
        Assert.Equal(DocumentEngineState.Running, b.Engine.State);

        pinA.Dispose();
        pinA.Dispose(); // idempotent: a lease is released once
        var c = await budget.CreateDocumentEngineAsync();
        Assert.Equal(DocumentEngineState.Suspended, a.Engine.State); // a was unpinned; b still is
        Assert.Equal(DocumentEngineState.Running, b.Engine.State);
        pinB.Dispose();
        Assert.Equal(2, budget.LiveDocumentEngines);
        Assert.Equal(DocumentEngineState.Running, c.State);
    }

    [Fact]
    public async Task AWaitingNewcomerGetsTheSlotTheMomentTheLastCallEnds()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var budget = NewBudget(cap: 1, slotWait: TimeSpan.FromSeconds(60));
        var busy = await budget.CreateDocumentEngineAsync();
        var opened = (await busy.OpenDocumentAsync(slow.Path)).Value!;
        var render = opened.RenderTileAsync(new RenderTileArgs(0, 4f, 0, 0, 0, 512, 512), 1, TimeSpan.FromMinutes(2));
        await Task.Delay(200);

        var waiting = budget.CreateDocumentEngineAsync(); // the only engine is busy: this waits
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        using var tile = (await render).Value!; // the call ends: the only engine is idle, and gives way
        var newcomer = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DocumentEngineState.Suspended, busy.State);
        Assert.Equal(DocumentEngineState.Running, newcomer.State);
        Assert.Equal(1, budget.LiveDocumentEngines);
    }

    [Fact]
    public async Task ACallThatCannotGetASlotFailsWithTimeoutLeavesTheDocumentSuspendedAndWorksOnceThereIsRoom()
    {
        await using var budget = NewBudget(cap: 1, slotWait: TimeSpan.FromSeconds(1));
        var a = await OpenAsync(budget);
        var b = await OpenAsync(budget); // a is suspended to make room
        Assert.Equal(DocumentEngineState.Suspended, a.Engine.State);
        using (b.Engine.Pin())
        {
            var result = await a.Document.GetInfoAsync();
            Assert.Equal(Status.Timeout, result.Status);
            Assert.Equal(DocumentEngineState.Suspended, a.Engine.State);
            Assert.Equal(DocumentEngineState.Running, b.Engine.State);
        }

        var later = await a.Document.GetInfoAsync(); // b is unpinned: it gives way
        Assert.True(later.IsOk, later.Detail);
        Assert.Equal(DocumentEngineState.Running, a.Engine.State);
        Assert.Equal(DocumentEngineState.Suspended, b.Engine.State);
    }

    [Fact]
    public async Task ClosingADocumentOfASuspendedEngineStartsNoProcessAndReleasesItsFile()
    {
        using var copy = new TempFile("closing.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("rectangles.pdf")));
        await using var budget = NewBudget(cap: 1);
        var engine = await budget.CreateDocumentEngineAsync();
        var document = (await engine.OpenDocumentAsync(copy.Path)).Value!;
        var other = await OpenAsync(budget); // the first engine is suspended
        Assert.Equal(DocumentEngineState.Suspended, engine.State);

        // While suspended the file is still held open, deny-write, by the broker: the document cannot change under a tab that is not on screen.
        Assert.ThrowsAny<IOException>(() => File.OpenHandle(copy.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());

        var running = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        await document.DisposeAsync();
        Assert.Equal(running, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory)); // closing it did not start an engine to say so
        Assert.Equal(DocumentEngineState.Suspended, engine.State);
        using (File.OpenHandle(copy.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            // and the file is free again
        }

        Assert.Equal(DocumentEngineState.Running, other.Engine.State);
    }

    [Fact]
    public async Task AnEngineThatGivesUpReleasesItsSlot()
    {
        await using var budget = NewBudget(cap: 1, document: o => o with { MaxRestartsPerWindow = 1, MaxStartAttempts = 1 });
        var doomed = await OpenAsync(budget);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doomed.Engine.Failed += (_, _) => failed.TrySetResult();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doomed.Engine.Restored += (_, _) => restored.TrySetResult();

        doomed.Engine.Supervisor.CurrentProcess!.Kill();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, budget.LiveDocumentEngines); // a restart after a crash keeps the slot
        doomed.Engine.Supervisor.CurrentProcess!.Kill();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DocumentEngineState.Failed, doomed.Engine.State);
        Assert.Equal(0, budget.LiveDocumentEngines);
        var next = await OpenAsync(budget);   // takes the free slot without suspending anything
        Assert.Equal(1, budget.LiveDocumentEngines);
        Assert.Equal(DocumentEngineState.Running, next.Engine.State);
        Assert.Equal(0, budget.SuspendedDocumentEngines);
    }

    [Fact]
    public async Task DisposingAnEngineFreesItsSlotAndDisposingTheBudgetLeavesNoProcessBehind()
    {
        var before = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        var budget = NewBudget(cap: 2);
        var a = await OpenAsync(budget);
        var b = await OpenAsync(budget);
        await budget.GetPoolAsync();
        Assert.Equal(3, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) - before);

        await a.Engine.DisposeAsync();
        Assert.Equal(1, budget.LiveDocumentEngines);
        Assert.Single(budget.DocumentEngines);

        await budget.DisposeAsync();
        await WaitForAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) == before);
        Assert.Equal(before, NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => budget.CreateDocumentEngineAsync());
        _ = b;
    }

    [Fact]
    public async Task TenDocumentsOverACapOfThreeAllKeepWorkingWhileTheyAreUsedAtOnce()
    {
        var before = NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory);
        await using var budget = NewBudget(cap: 3);
        await budget.GetPoolAsync();
        await using var monitor = new ProcessMonitor(sandbox.DeployDirectory);
        var documents = new List<(DocumentEngine Engine, SupervisedDocument Document)>();
        for (var i = 0; i < 10; i++)
        {
            documents.Add(await OpenAsync(budget));
        }

        Assert.Equal(3, budget.LiveDocumentEngines);
        Assert.Equal(7, budget.SuspendedDocumentEngines);
        var expected = await RenderAsync(documents[0].Document);

        for (var round = 0; round < 3; round++)
        {
            var drawn = await Task.WhenAll(documents.Select(d => RenderAsync(d.Document)));
            Assert.All(drawn, pixels => Assert.Equal(expected, pixels));
            Assert.True(budget.LiveDocumentEngines <= 3);
        }

        Assert.True(monitor.Max - before <= 3 + 1, $"{monitor.Max - before} engine processes at once (cap 3 plus the pool)");
        Assert.All(documents, d => Assert.Equal(0, d.Engine.RestartCount));
    }

    [Fact]
    public async Task ThePoolIsReplacedWhenItGivesUpAndOnlyThen()
    {
        await using var budget = NewBudget(cap: 2);
        var pool = await budget.GetPoolAsync();
        Assert.Same(pool, await budget.GetPoolAsync());
        Assert.False(pool.IsFailed);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(25);
        }
    }
}
