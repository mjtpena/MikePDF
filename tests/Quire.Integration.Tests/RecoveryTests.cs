using System.Diagnostics;
using Quire.Broker;
using Quire.Integration.Tests.Support;
using Quire.Protocol;

namespace Quire.Integration.Tests;

/// <summary>§5.2 rule 3: an engine that dies, hangs or runs out of memory is killed, replaced, and the documents come back.</summary>
public sealed class RecoveryTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Collects the supervisor's events as tasks, so a test can await "the n-th crash" and "the n-th restore".</summary>
    private sealed class Watch
    {
        private readonly List<TaskCompletionSource<EngineCrashedEventArgs>> _crashed = [];
        private readonly List<TaskCompletionSource<EngineRestoredEventArgs>> _restored = [];
        private readonly TaskCompletionSource<EngineCrashedEventArgs> _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Watch(EngineSupervisor supervisor)
        {
            for (var i = 0; i < 8; i++)
            {
                _crashed.Add(new TaskCompletionSource<EngineCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
                _restored.Add(new TaskCompletionSource<EngineRestoredEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            supervisor.EngineCrashed += (_, e) => _crashed[e.RestartNumber - 1].TrySetResult(e);
            supervisor.Restored += (_, e) => _restored[e.RestartNumber - 1].TrySetResult(e);
            supervisor.Failed += (_, e) => _failed.TrySetResult(e);
        }

        public Task<EngineCrashedEventArgs> Crashed(int number = 1) => _crashed[number - 1].Task.WaitAsync(Wait);

        public Task<EngineRestoredEventArgs> Restored(int number = 1) => _restored[number - 1].Task.WaitAsync(Wait);

        public Task<EngineCrashedEventArgs> Failed() => _failed.Task.WaitAsync(Wait);
    }

    private static void Record(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "restore-timings.txt"), line + Environment.NewLine);
    }

    private static async Task<SupervisedDocument> OpenAsync(EngineSupervisor supervisor, string path, string? password = null)
    {
        var opened = await supervisor.OpenDocumentAsync(path, password);
        Assert.Equal(Status.Ok, opened.Status);
        return opened.Value!;
    }

    private static async Task AssertRendersTheCorners(SupervisedDocument document)
    {
        var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 200, 300), 1);
        Assert.Equal(Status.Ok, result.Status);
        using var tile = result.Value!;
        Assert.Equal(0, tile.Pixels[(5 * tile.Stride) + 20]); // top left: black (blue channel)
        Assert.Equal(255, tile.Pixels[(5 * tile.Stride) + (195 * 4)]); // top right: blue
    }

    [Fact]
    public async Task KillingTheEngineMidSessionRestoresTheDocument()
    {
        await using var supervisor = await sandbox.StartAsync();
        var watch = new Watch(supervisor);
        await using var document = await OpenAsync(supervisor, SandboxFixture.FixturePath("rectangles.pdf"));
        await AssertRendersTheCorners(document);
        var firstEngine = supervisor.EngineProcessId;

        supervisor.CurrentProcess!.Kill(1);

        var crashed = await watch.Crashed();
        var restored = await watch.Restored();
        Assert.Contains(crashed.Reason, new[] { EngineFailureReason.ProcessExited, EngineFailureReason.PipeClosed });
        Assert.Equal(1, restored.DocumentsRestored);
        Assert.Equal(0, restored.DocumentsLost);
        Assert.NotEqual(firstEngine, supervisor.EngineProcessId);
        Record($"kill: crash to restored in {restored.Elapsed.TotalMilliseconds:F0} ms (reason {crashed.Reason}, exit code 0x{crashed.ExitCode:X})");

        // The same SupervisedDocument works in the new engine: its engine-side id changed, its id here did not.
        await AssertRendersTheCorners(document);
        Assert.Single((await document.GetPageSizesAsync(0, 1)).Value!.Widths);
        Assert.Equal(1, supervisor.RestartCount);
    }

    [Fact]
    public async Task ARequestInFlightWhenTheEngineDiesEndsWithEngineCrashed()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var supervisor = await sandbox.StartAsync();
        var watch = new Watch(supervisor);
        await using var document = await OpenAsync(supervisor, slow.Path);

        var render = document.RenderTileAsync(new RenderTileArgs(0, 4f, 0, 0, 0, 512, 512), 1);
        await Task.Delay(200);
        supervisor.CurrentProcess!.Kill();

        Assert.Equal(Status.EngineCrashed, (await render.WaitAsync(Wait)).Status);
        await watch.Restored();
        Assert.True((await document.GetInfoAsync()).IsOk);

        // The slot the dead engine was writing came back to the ring.
        var leases = new List<SlotLease>();
        while (supervisor.Ring.TryRent(out var lease))
        {
            leases.Add(lease!);
        }

        Assert.Equal(supervisor.Ring.SlotCount, leases.Count);
        leases.ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task EveryOpenDocumentComesBackIncludingItsPassword()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Pool });
        var watch = new Watch(supervisor);
        await using var plain = await OpenAsync(supervisor, SandboxFixture.FixturePath("rectangles.pdf"));
        await using var locked = await OpenAsync(supervisor, SandboxFixture.FixturePath("encrypted.pdf"), "5678");
        await using var closed = await OpenAsync(supervisor, SandboxFixture.FixturePath("hello_world.pdf"));
        await closed.DisposeAsync();

        supervisor.CurrentProcess!.Kill();
        var restored = await watch.Restored();

        Assert.Equal(2, restored.DocumentsRestored); // the closed one is not reopened
        await AssertRendersTheCorners(plain);
        Assert.True((await locked.GetInfoAsync()).IsOk);
        Assert.Equal(Status.InvalidArgument, (await closed.GetInfoAsync()).Status);
    }

    [Fact]
    public async Task AHungEngineIsKilledByTheWatchdogAndReplaced()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { PingInterval = TimeSpan.FromMilliseconds(200), WatchdogTimeout = TimeSpan.FromSeconds(1.5) } /* test-only override: the defaults are 1 s and 5 s (DefaultsTests) */);
        var watch = new Watch(supervisor);
        await using var document = await OpenAsync(supervisor, SandboxFixture.FixturePath("rectangles.pdf"));
        var hung = supervisor.CurrentProcess!;
        var clock = Stopwatch.StartNew();

        NativeTestMethods.Suspend(hung.NativeHandle); // every thread frozen: no pong, no matter what the I/O lane would do

        var crashed = await watch.Crashed();
        var detectedAfter = clock.ElapsedMilliseconds;
        var restored = await watch.Restored();
        Assert.Equal(EngineFailureReason.Hung, crashed.Reason);
        Assert.InRange(detectedAfter, 1000, 8000);
        Assert.True(hung.HasExited || await hung.WaitForExitAsync(TimeSpan.FromSeconds(5)), "the frozen engine was not killed");
        Record($"hang: noticed after {detectedAfter} ms (watchdog 1500 ms, ping every 200 ms), restored {restored.Elapsed.TotalMilliseconds:F0} ms after that");
        await AssertRendersTheCorners(document);
    }

    [Fact]
    public async Task TheMemoryLimitKillsTheEngineAndTheDocumentIsRestoredInANewOne()
    {
        // 256 MiB of whitespace in a page's content stream: PDFium decodes a content stream in one piece, and the job allows 128 MiB.
        using var hungry = new TempFile("hungry.pdf", TestPdf.HungryPage(256 << 20));
        await using var supervisor = await sandbox.StartAsync(o => o with { Policy = new SandboxPolicy { ProcessMemoryLimitBytes = 128L << 20 } });
        var watch = new Watch(supervisor);
        await using var document = await OpenAsync(supervisor, hungry.Path);
        Assert.True((await document.GetInfoAsync()).IsOk); // opening is cheap; drawing is what decodes the content stream
        var firstEngine = supervisor.EngineProcessId;

        var render = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 64, 64), 1);
        Assert.Equal(Status.EngineCrashed, render.Status);

        var crashed = await watch.Crashed();
        var restored = await watch.Restored();
        Assert.Equal(EngineFailureReason.MemoryLimit, crashed.Reason);
        Assert.NotEqual(firstEngine, supervisor.EngineProcessId);
        Assert.Equal(1, restored.DocumentsRestored);
        Record($"memory limit: crash to restored in {restored.Elapsed.TotalMilliseconds:F0} ms (exit code 0x{crashed.ExitCode:X})");
        Assert.True((await document.GetInfoAsync()).IsOk);
    }

    [Fact]
    public async Task ACrashLoopEndsWithTheSupervisorFailedAndEveryCallSaysSo()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { MaxRestartsPerWindow = 2, MaxStartAttempts = 1 });
        var watch = new Watch(supervisor);
        await using var document = await OpenAsync(supervisor, SandboxFixture.FixturePath("rectangles.pdf"));

        supervisor.CurrentProcess!.Kill();
        await watch.Restored(1);
        supervisor.CurrentProcess!.Kill();
        await watch.Restored(2);
        supervisor.CurrentProcess!.Kill();

        var failed = await watch.Failed();
        Assert.True(supervisor.IsFailed);
        Assert.Contains("restarts", failed.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Status.EngineCrashed, (await document.GetInfoAsync()).Status);
        Assert.Equal(Status.EngineCrashed, (await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("hello_world.pdf"))).Status);
    }

    [Fact]
    public async Task DisposingTheSupervisorLeavesNoEngineBehind()
    {
        var supervisor = await sandbox.StartAsync();
        var process = supervisor.CurrentProcess!;
        var job = process.Job;
        await supervisor.DisposeAsync();

        Assert.True(process.HasExited || await process.WaitForExitAsync(TimeSpan.FromSeconds(5)));
        await job.Emptied.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(job.MemoryLimitExceeded);
    }
}
