using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// Starting an engine on a machine that is busy (W2-engine item 3). W1-perf saw a start fail five times out of five, "Hello failed ... Pipe is broken",
/// with a busy thread on every core, and never once in 3,000 starts on an idle machine.
/// <para>
/// <b>What is established and what is not.</b> The failure was not reproduced here by saturating the cores (threads and processes, normal and higher
/// priority, up to 72 busy threads and processes on 24 cores, 600 parallel starts: no start ever failed; they only got slower), so the cause of that
/// observation is not proven. What these tests pin down: the shape of the failure is an engine process lost while it starts (the broker's write finds
/// the pipe broken because the other end is already gone); a start that meets it now says how the process ended and is retried the way a restart always
/// was, and a broker that is slow to listen, which is what a starved broker thread looks like to the engine, is served.
/// </para>
/// </summary>
public sealed class EngineStartUnderLoadTests(SandboxFixture sandbox)
{
    [Fact]
    public async Task EnginesStartWhileEveryCoreIsBusy()
    {
        const int Cycles = 8;
        var clock = Stopwatch.StartNew();
        using (CpuLoad.OnEveryCore(threadsPerCore: 2))
        {
            for (var i = 0; i < Cycles; i++)
            {
                await using var supervisor = await sandbox.StartAsync();
                var opened = await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"));
                Assert.Equal(Status.Ok, opened.Status);
                Assert.Equal(0, supervisor.RestartCount);
                await opened.Value!.DisposeAsync();
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{Cycles} engine starts with two busy threads on each of {Environment.ProcessorCount} cores took {clock.ElapsedMilliseconds} ms in all");
    }

    [Fact]
    public async Task AnEngineThatConnectsLongBeforeTheBrokerListensIsServed()
    {
        // The engine is quick and the broker's thread was preempted: by the time the broker waits for a connection, the engine has been connected for a while.
        await using var engine = await RawEngine.StartAsync(sandbox, brokerStallAfterResume: TimeSpan.FromMilliseconds(1500));
        Assert.Equal(Status.Ok, (await engine.HelloAsync()).Status);
        Assert.Equal(Status.Ok, (await engine.CallAsync(0, new Ping { Nonce = 1 })).Status);
    }

    [Fact]
    public async Task AnEngineLostWhileItStartsIsReplacedAndTheStartSucceeds()
    {
        var attempts = new List<int>();
        await using var supervisor = await sandbox.StartAsync(o => o with
        {
            AfterLaunch = (process, attempt) =>
            {
                attempts.Add(attempt);
                if (attempt == 1)
                {
                    process.Kill(); // gone before its first instruction
                }
            },
        });

        Assert.Equal([1, 2], attempts);
        var opened = await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("rectangles.pdf"));
        Assert.Equal(Status.Ok, opened.Status);
        await opened.Value!.DisposeAsync();
        Assert.Equal(0, supervisor.RestartCount); // a start that took two tries is not a restart
    }

    [Fact]
    public async Task AnEngineThatIsLostEveryTimeFailsTheStartWithTheAttemptsUsedUpAndSaysHowItEnded()
    {
        var attempts = 0;
        var failure = await Assert.ThrowsAsync<EngineStartException>(() => sandbox.StartAsync(o => o with
        {
            MaxStartAttempts = 3,
            AfterLaunch = (process, _) =>
            {
                attempts++;
                process.Kill();
            },
        }));

        Assert.Equal(3, attempts);
        Assert.True(failure.IsTransient);
        Assert.Contains("0xDEAD", failure.Message, StringComparison.OrdinalIgnoreCase); // the exit code of the kill, so a lost engine is told apart from a crash
    }

    [Fact]
    public async Task AFailureThatWouldRepeatItselfIsNotRetried()
    {
        var launched = 0;
        var missing = Path.Combine(sandbox.DeployDirectory, "no-such-engine.exe");
        var failure = await Assert.ThrowsAsync<EngineStartException>(() => sandbox.StartAsync(o => o with { EnginePath = missing, AfterLaunch = (_, _) => launched++ }));
        Assert.False(failure.IsTransient);
        Assert.Equal(0, launched);
    }

    [Fact]
    public async Task ALostEngineIsReportedWithItsExitCodeNotJustABrokenPipe()
    {
        // The message a user's bug report carries. A lone "Pipe is broken" said nothing about whether the engine had crashed, been killed or never run.
        var failure = await Assert.ThrowsAsync<EngineStartException>(() => sandbox.StartAsync(o => o with { MaxStartAttempts = 1, AfterLaunch = (process, _) => process.Kill(0x51) }));
        Assert.Contains("0x51", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
