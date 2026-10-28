using System.Diagnostics;
using Quire.Broker;
using Quire.Integration.Tests.Support;
using Quire.Protocol;

namespace Quire.Integration.Tests;

/// <summary>§5.2 rule 4 and §6.4: renders can be cancelled by generation and have a deadline; neither costs the engine.</summary>
public sealed class CancellationAndDeadlineTests(SandboxFixture sandbox)
{
    private static readonly RenderTileArgs Heavy = new(0, 4f, 0, 0, 0, 512, 512);

    [Fact]
    public async Task CancelBeforeCancelsOlderGenerationsOfThatDocumentOnly()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Pool });
        var one = (await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"))).Value!;
        var two = (await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"))).Value!;
        var args = new RenderTileArgs(0, 1f, 0, 0, 0, 16, 16);

        Assert.Equal(Status.Ok, (await one.CancelBeforeAsync(10)).Status);
        Assert.Equal(Status.Cancelled, (await one.RenderTileAsync(args, 5)).Status);
        using var current = (await one.RenderTileAsync(args, 10)).Value!;
        using var other = (await two.RenderTileAsync(args, 5)).Value!; // the other document has its own floor
        Assert.NotNull(other);
        Assert.NotNull(current);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task CancellingTheTokenEndsTheCallAtOnceAndTheEngineStopsEarly()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var supervisor = await sandbox.StartAsync();
        await using var document = (await supervisor.OpenDocumentAsync(slow.Path)).Value!;

        var clock = Stopwatch.StartNew();
        using (var full = (await document.RenderTileAsync(Heavy, 1)).Value!)
        {
            Assert.NotNull(full);
        }

        var uncancelled = clock.ElapsedMilliseconds;

        clock.Restart();
        using var cancel = new CancellationTokenSource();
        var render = document.RenderTileAsync(Heavy, 2, cancellationToken: cancel.Token);
        await Task.Delay(100);
        await cancel.CancelAsync();
        var result = await render;
        var callEnded = clock.ElapsedMilliseconds;
        Assert.Equal(Status.Cancelled, result.Status);
        Assert.True(callEnded < uncancelled / 2, $"the call took {callEnded} ms to end; a full render takes {uncancelled} ms");

        // The slot is the engine's until it stops writing. It stops early, at its next check, long before a full render would end.
        var leases = new List<SlotLease>();
        while (leases.Count < supervisor.Ring.SlotCount && clock.ElapsedMilliseconds < 20_000)
        {
            if (supervisor.Ring.TryRent(out var lease) && lease is not null)
            {
                leases.Add(lease);
            }
            else
            {
                await Task.Delay(10);
            }
        }

        var freedAt = clock.ElapsedMilliseconds;
        leases.ForEach(l => l.Dispose());
        Assert.Equal(supervisor.Ring.SlotCount, leases.Count);
        TestContext.Current.SendDiagnosticMessage($"full render {uncancelled} ms; cancelled call ended after {callEnded} ms; engine released the slot after {freedAt} ms");
        Assert.True(freedAt < uncancelled * 3 / 4, $"the engine kept rendering for {freedAt} ms after the cancel; a full render takes {uncancelled} ms");

        // And it is the same healthy engine afterwards.
        Assert.Equal(0, supervisor.RestartCount);
        Assert.True((await document.GetInfoAsync()).IsOk);
    }

    [Fact]
    public async Task ADeadlineThatPassesIsATimeoutFromTheEngineItselfNotAKill()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var supervisor = await sandbox.StartAsync();
        var engine = supervisor.EngineProcessId;
        await using var document = (await supervisor.OpenDocumentAsync(slow.Path)).Value!;

        var clock = Stopwatch.StartNew();
        var result = await document.RenderTileAsync(Heavy, 1, TimeSpan.FromMilliseconds(50));
        Assert.Equal(Status.Timeout, result.Status);
        Assert.Null(result.Value);
        TestContext.Current.SendDiagnosticMessage($"a 50 ms deadline on a long render was answered with Timeout after {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.ElapsedMilliseconds < 2000, "the engine answered only after the client's own grace period ran out");

        Assert.Equal(engine, supervisor.EngineProcessId); // no restart: it was the engine's own Timeout reply
        Assert.Equal(0, supervisor.RestartCount);
        Assert.True((await document.GetInfoAsync()).IsOk);
    }

    [Fact]
    public async Task ADeadlineThatIsLongEnoughDoesNotInterrupt()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(30));
        await using var supervisor = await sandbox.StartAsync();
        await using var document = (await supervisor.OpenDocumentAsync(slow.Path)).Value!;
        var result = await document.RenderTileAsync(Heavy, 1, TimeSpan.FromSeconds(60));
        Assert.Equal(Status.Ok, result.Status);
        result.Value!.Dispose();
    }
}
