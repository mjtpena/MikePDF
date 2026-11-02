using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// W2-engine item 2: a hang is an engine that stopped making progress, not a request that took longer than its deadline. PDFium looks at its deadline only about every
/// 100 drawing operations, so a heavy page can overshoot it by more than the old 2 s grace while the engine does exactly what it should, and the old watchdog killed it
/// as hung. These tests put a heavy but alive engine and a truly stuck one side by side, first against a scripted engine (exact timing, no PDFium) and then against the real one.
/// </summary>
public sealed class HangDetectionTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static byte[] Reply(RequestEnvelope r, Status status, MessageType type, IReplyMessage? payload = null) => FakeEngine.Response(r.RequestId, status, type, payload);

    /// <summary>Pings the client every <paramref name="every"/> until the token ends, as the supervisor's watchdog does.</summary>
    private static Task PingLoopAsync(EngineClient client, TimeSpan every, CancellationToken stop) => Task.Run(async () =>
    {
        ulong nonce = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await client.PingAsync(++nonce, TimeSpan.FromSeconds(5), stop);
                await Task.Delay(every, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }, stop);

    private static EngineClientOptions Options(int stallMs, int hardMs = 60_000) => new()
    {
        OverdueGrace = TimeSpan.FromMilliseconds(100),
        StallTimeout = TimeSpan.FromMilliseconds(stallMs),
        HardCallLimit = TimeSpan.FromMilliseconds(hardMs),
        LivenessInterval = TimeSpan.FromMilliseconds(50),
    };

    // ---- scripted engine: exact timing --------------------------------------------------------

    [Fact]
    public async Task AnEngineThatKeepsMovingItsCounterIsNotHungHoweverLateTheAnswerIs()
    {
        RequestEnvelope? held = null;
        long progress = 0;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) =>
        {
            if (r.Payload is GetDocumentInfo && held is null)
            {
                held = r; // the first one is held; later ones are answered as usual
                return [];
            }

            return null;
        });
        fake.PongProgress = () => (ulong)Interlocked.Increment(ref progress);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 400));
        var overdue = false;
        client.RequestOverdue += _ => overdue = true;
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        using var stop = new CancellationTokenSource();
        var pings = PingLoopAsync(client, TimeSpan.FromMilliseconds(50), stop.Token);

        // The deadline is 100 ms and the answer comes 5 s later: overdue for twelve times the stall timeout, with the counter moving all the while.
        var info = await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        Assert.Equal(Status.Timeout, info.Status);
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(overdue, "an engine whose counter moves was reported hung");
        Assert.False(client.IsFaulted);

        await fake.SendAsync(Reply(held!, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply { PageCount = FakeEngine.FakePageCount }));
        Assert.Equal(Status.Ok, (await client.GetDocumentInfoAsync(document, Wait)).Status); // the late answer was absorbed and the client still works
        await stop.CancelAsync();
        await pings;
        Assert.False(overdue);
    }

    [Fact]
    public async Task AnEngineWhoseCounterStandsStillIsHungOnceTheStallTimeoutHasPassedAndStillRespondsToPings()
    {
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is GetDocumentInfo ? [] : null);
        fake.PongProgress = () => 41; // the I/O thread answers; the work thread does nothing
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 600));
        var overdue = new TaskCompletionSource<MessageType>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestOverdue += type => overdue.TrySetResult(type);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        using var stop = new CancellationTokenSource();
        var pings = PingLoopAsync(client, TimeSpan.FromMilliseconds(50), stop.Token);
        await Task.Delay(300); // idle for a while first: an idle counter standing still must not count against the next request

        var clock = Stopwatch.StartNew();
        var info = await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        Assert.Equal(Status.Timeout, info.Status);
        Assert.Equal(MessageType.GetDocumentInfo, await overdue.Task.WaitAsync(Wait));
        var after = clock.ElapsedMilliseconds;

        Assert.InRange(after, 550, 3000); // not at the deadline (100 ms) or the end of the grace period (200 ms): the stall timeout (600 ms) after the request began
        await stop.CancelAsync();
        await pings;
        Assert.False(client.IsFaulted); // the pings were answered the whole time: only the counter gave the hang away
    }

    [Fact]
    public async Task TheStallClockStartsWhenWorkReachesAnIdleEngineNotWhenTheEngineLastMoved()
    {
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is GetDocumentInfo ? [] : null);
        fake.PongProgress = () => 7;
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 1500));
        var overdue = new TaskCompletionSource<MessageType>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestOverdue += type => overdue.TrySetResult(type);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        using var stop = new CancellationTokenSource();
        var pings = PingLoopAsync(client, TimeSpan.FromMilliseconds(50), stop.Token);
        await Task.Delay(2500); // the counter has stood still for longer than the stall timeout, because nothing was asked of the engine

        var clock = Stopwatch.StartNew();
        await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        await Task.Delay(700); // overdue since 200 ms, and the counter has been still for 2.5 s + 0.7 s, but the request is 0.7 s old
        Assert.False(overdue.Task.IsCompleted, "the idle time before the request was counted as a stall");
        Assert.Equal(MessageType.GetDocumentInfo, await overdue.Task.WaitAsync(Wait)); // and 1.5 s after the request began, it is a hang
        Assert.InRange(clock.ElapsedMilliseconds, 1400, 4000);
        await stop.CancelAsync();
        await pings;
    }

    [Fact]
    public async Task ARequestThatStaysOverdueForTheHardLimitIsHungEvenIfTheCounterKeepsMoving()
    {
        long progress = 0;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is GetDocumentInfo ? [] : null);
        fake.PongProgress = () => (ulong)Interlocked.Increment(ref progress); // an endless loop that happens to poll
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 400, hardMs: 1500));
        var overdue = new TaskCompletionSource<MessageType>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestOverdue += type => overdue.TrySetResult(type);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        using var stop = new CancellationTokenSource();
        var pings = PingLoopAsync(client, TimeSpan.FromMilliseconds(50), stop.Token);

        var clock = Stopwatch.StartNew();
        await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        await Task.Delay(1000);
        Assert.False(overdue.Task.IsCompleted, "reported before the hard limit although the counter moves");
        Assert.Equal(MessageType.GetDocumentInfo, await overdue.Task.WaitAsync(Wait));
        Assert.InRange(clock.ElapsedMilliseconds, 1500 + 200 - 100, 6000); // grace (100 ms) + limit (1.5 s) after the deadline (100 ms)
        await stop.CancelAsync();
        await pings;
    }

    [Fact]
    public async Task ARequestThatTheIoThreadShouldHaveAnsweredIsStillReportedAtOnce()
    {
        // Ping, Hello and the like are answered by the engine's I/O thread. No progress counter excuses that thread: unanswered, the engine is hung.
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is Ping ? [] : null);
        fake.PongProgress = () => 1;
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 60_000));
        var overdue = new TaskCompletionSource<MessageType>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestOverdue += type => overdue.TrySetResult(type);

        var pong = await client.PingAsync(1, TimeSpan.FromMilliseconds(200)).WaitAsync(Wait);
        Assert.Equal(Status.Timeout, pong.Status);
        Assert.Equal(MessageType.Ping, await overdue.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AnAnswerThatArrivesBeforeTheStallTimeoutEndsTheSuspicion()
    {
        RequestEnvelope? held = null;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) =>
        {
            if (r.Payload is GetDocumentInfo)
            {
                held = r;
                return [];
            }

            return null;
        });
        fake.PongProgress = () => 5; // standing still: a stall is coming, unless the answer gets there first
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, Options(stallMs: 900));
        var overdue = false;
        client.RequestOverdue += _ => overdue = true;
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        using var stop = new CancellationTokenSource();
        var pings = PingLoopAsync(client, TimeSpan.FromMilliseconds(50), stop.Token);

        await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        await Task.Delay(400);
        await fake.SendAsync(Reply(held!, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply { PageCount = FakeEngine.FakePageCount }));
        await Task.Delay(1500); // well past the stall timeout: nothing is overdue any more
        Assert.False(overdue);
        await stop.CancelAsync();
        await pings;
        Assert.False(client.IsFaulted);
    }

    // ---- the real engine --------------------------------------------------------------------

    /// <summary>A page that takes the engine a while to draw, found by trying: at least <paramref name="atLeast"/> for the tile below on this machine.</summary>
    private async Task<(TempFile File, TimeSpan Duration)> HeavyPageAsync(TimeSpan atLeast)
    {
        // At least 1,000 layers whatever the machine: PDFium polls every 100 objects, so the gap between two polls is a tenth of the render, and it has to stay well inside the stall timeout.
        for (var layers = 1000; ; layers = layers * 3 / 2)
        {
            var file = new TempFile($"heavy-{layers}.pdf", TestPdf.SlowPage(layers));
            await using var supervisor = await sandbox.StartAsync();
            await using var document = (await supervisor.OpenDocumentAsync(file.Path)).Value!;
            var clock = Stopwatch.StartNew();
            using var tile = (await document.RenderTileAsync(HeavyTile, 1, TimeSpan.FromMinutes(5))).Value!;
            if (clock.Elapsed >= atLeast || layers >= 40_000)
            {
                return (file, clock.Elapsed);
            }

            file.Dispose();
        }
    }

    private static readonly RenderTileArgs HeavyTile = new(0, 4f, 0, 0, 0, 512, 512);

    [Fact]
    public async Task AHeavyButAlivePageThatOvershootsItsDeadlineIsNotKilledAsHung()
    {
        // The engine believes the deadline is 20 s, the client's clock says "overdue" after 0.3 s (the negative grace stands for an engine that overshoots its deadline by
        // seconds, as PDFium does between two polls): the call ends with Timeout, and the engine, which is alive and drawing, must be left alone.
        const int StallMs = 1500;
        var (page, duration) = await HeavyPageAsync(TimeSpan.FromMilliseconds(3 * StallMs));
        using var _ = page;
        Timings.Record($"heavy page: {duration.TotalMilliseconds:F0} ms for the tile; the client calls it overdue after 300 ms and hung only if the counter stands still for {StallMs} ms");
        await using var supervisor = await sandbox.StartAsync(o => o with
        {
            Client = new EngineClientOptions { OverdueGrace = TimeSpan.FromMilliseconds(-19_700), StallTimeout = TimeSpan.FromMilliseconds(StallMs), LivenessInterval = TimeSpan.FromMilliseconds(100) },
            PingInterval = TimeSpan.FromMilliseconds(200),
        });
        var crashes = 0;
        supervisor.EngineCrashed += (_, _) => Interlocked.Increment(ref crashes);
        await using var document = (await supervisor.OpenDocumentAsync(page.Path)).Value!;
        var engine = supervisor.EngineProcessId;

        var clock = Stopwatch.StartNew();
        var render = await document.RenderTileAsync(HeavyTile, 1, TimeSpan.FromSeconds(20));
        var calledOverdueAt = clock.ElapsedMilliseconds;
        Assert.Equal(Status.Timeout, render.Status);
        Assert.InRange(calledOverdueAt, 100, 1500);

        // Wait for the engine to finish the page on its own (its reply is the proof that it was alive) and then ask it something else.
        await Task.Delay(duration + TimeSpan.FromSeconds(1));
        var info = await document.GetInfoAsync();
        Assert.True(info.IsOk, info.Detail);
        Assert.Equal(0, crashes);
        Assert.Equal(engine, supervisor.EngineProcessId);
        Assert.Equal(0, supervisor.RestartCount);
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(StallMs + 300), "the render finished before the stall timeout could have mattered; the page is too light for this test");
    }

    [Fact]
    public async Task AnEngineWhoseWorkThreadIsStuckIsStillKilledWhileItsHeartbeatBeats()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with
        {
            Client = new EngineClientOptions { OverdueGrace = TimeSpan.FromMilliseconds(200), StallTimeout = TimeSpan.FromMilliseconds(1000), LivenessInterval = TimeSpan.FromMilliseconds(100) },
            PingInterval = TimeSpan.FromMilliseconds(200),
            WatchdogTimeout = TimeSpan.FromSeconds(30), // the heartbeat is not what finds this one
        });
        var crashed = new TaskCompletionSource<EngineCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource<EngineRestoredEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.EngineCrashed += (_, e) => crashed.TrySetResult(e);
        supervisor.Restored += (_, e) => restored.TrySetResult(e);
        await using var document = (await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"))).Value!;
        var stuck = supervisor.CurrentProcess!;
        var frozen = NativeTestMethods.SuspendAllThreadsButTheOldest(stuck.ProcessId);
        Assert.True(frozen >= 1, "no thread to freeze");

        // The I/O thread is alive, so every ping is answered (the watchdog's own timeout is 30 s); the work thread cannot answer the render.
        var clock = Stopwatch.StartNew();
        var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 64, 64), 1, TimeSpan.FromMilliseconds(300));
        Assert.Equal(Status.Timeout, result.Status);

        var event1 = await crashed.Task.WaitAsync(Wait);
        var detected = clock.ElapsedMilliseconds;
        await restored.Task.WaitAsync(Wait);
        Assert.Equal(EngineFailureReason.Hung, event1.Reason);
        Assert.Contains("overdue", event1.Detail, StringComparison.Ordinal); // found through the request and the counter, not through a missed ping
        Assert.InRange(detected, 900, 10_000);                               // the stall timeout (1 s) after the request began, not at its deadline (0.3 s)
        Timings.Record($"stuck work thread: found after {detected} ms (stall timeout 1000 ms)");
        Assert.True(stuck.HasExited || await stuck.WaitForExitAsync(TimeSpan.FromSeconds(5)), "the stuck engine was not killed");
        Assert.NotEqual(stuck.ProcessId, supervisor.EngineProcessId);
        Assert.True((await document.GetInfoAsync()).IsOk); // the document is back in the new engine
    }
}
