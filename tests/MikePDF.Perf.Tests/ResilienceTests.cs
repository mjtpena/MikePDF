using MikePDF.Broker;
using MikePDF.Perf.Scenarios;

namespace MikePDF.Perf.Tests;

public sealed class ResilienceTests(HarnessFixture fixture)
{
    [Fact]
    public async Task AnIterationThatLosesItsEngineIsRepeatedAndTheDeathsAreCounted()
    {
        var context = fixture.Context(PerfProfile.Quick, TimeSpan.Zero);
        var attempts = 0;
        var value = await context.Resilient(() =>
        {
            attempts++;
            return attempts < 3 ? throw new EngineCrashedException("the engine closed the pipe") : Task.FromResult(42);
        }, "test");

        Assert.Equal(42, value);
        Assert.Equal(3, attempts);
        Assert.Equal(2, context.UnexpectedEngineCrashes);
    }

    [Fact]
    public async Task AnEngineThatKeepsDyingEndsTheMetricAsAFailureAfterTheRepeatsAreUsedUp()
    {
        var context = fixture.Context(PerfProfile.Quick, TimeSpan.Zero);
        var attempts = 0;
        await Assert.ThrowsAsync<EngineCrashedException>(() => context.Resilient<int>(() =>
        {
            attempts++;
            throw new EngineCrashedException("again");
        }, "test"));

        Assert.Equal(PerfContext.MaxCrashRepeats, attempts);
        Assert.Equal(PerfContext.MaxCrashRepeats, context.UnexpectedEngineCrashes);
    }

    [Fact]
    public async Task AnEngineThatFailsToStartCountsAsADeathButAWrongAnswerIsNeverRepeated()
    {
        var context = fixture.Context(PerfProfile.Quick, TimeSpan.Zero);
        var starts = 0;
        Assert.Equal("up", await context.Resilient(() => ++starts == 1 ? throw new EngineStartException("Hello failed") : Task.FromResult("up"), "test"));
        Assert.Equal(1, context.UnexpectedEngineCrashes);

        var wrong = 0;
        await Assert.ThrowsAsync<PerfException>(() => context.Resilient<int>(() =>
        {
            wrong++;
            throw new PerfException("The first match was on the wrong page.");
        }, "test"));
        Assert.Equal(1, wrong);
        Assert.Equal(1, context.UnexpectedEngineCrashes);
    }

    [Fact]
    public async Task TheWaitBetweenRepeatsGrowsWithEachRepeat()
    {
        var context = fixture.Context(PerfProfile.Quick, TimeSpan.FromMilliseconds(100));
        var attempts = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await context.Resilient(() => ++attempts < 3 ? throw new EngineCrashedException("x") : Task.FromResult(0), "test");
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(290), $"100 ms and then 200 ms, took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task OnlyAnEngineCrashStatusBecomesACrashExceptionAndOtherFailuresStayPlain()
    {
        await using var engine = await fixture.Engine.StartDocumentEngineAsync();
        var missing = await engine.OpenDocumentAsync(Path.Combine(Path.GetTempPath(), "mikepdf-perf-no-such-file.pdf"));
        var plain = Assert.Throws<PerfException>(() => Checks.Require(missing, "OpenDocument"));
        Assert.IsNotType<EngineCrashedException>(plain);
    }
}
