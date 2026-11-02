using MikePDF.Broker;

namespace MikePDF.Integration.Tests;

/// <summary>The numbers ADR-0004 and §5.2 fix. Tests that need faster timing override them explicitly and say so.</summary>
public sealed class DefaultsTests(Support.SandboxFixture sandbox)
{
    [Fact]
    public void TheWatchdogPingsEverySecondAndKillsAfterFiveSeconds()
    {
        var options = new EngineSupervisorOptions { EnginePath = "MikePDF.Engine.exe", Profile = sandbox.Profile };

        Assert.Equal(TimeSpan.FromSeconds(1), options.PingInterval);   // ADR-0004 decision 4: a ping every second ...
        Assert.Equal(TimeSpan.FromSeconds(5), options.WatchdogTimeout); // ... and TerminateProcess after 5 s with no reply

        // The shared test configuration does not shorten them either: only the tests that wait for the watchdog do.
        var shared = sandbox.Options();
        Assert.Equal(options.PingInterval, shared.PingInterval);
        Assert.Equal(options.WatchdogTimeout, shared.WatchdogTimeout);
        Assert.Equal(options.PingInterval, sandbox.WriterHostOptions().PingInterval);
        Assert.Equal(options.WatchdogTimeout, sandbox.WriterHostOptions().WatchdogTimeout);
    }

    [Fact]
    public void TheJobAndRestartDefaultsMatchTheAdr()
    {
        var options = new EngineSupervisorOptions { EnginePath = "MikePDF.Engine.exe", Profile = sandbox.Profile };
        Assert.Equal(2L << 30, options.Policy.ProcessMemoryLimitBytes); // §5.2 rule 5: 2 GiB per engine
        Assert.True(options.Policy.ProhibitDynamicCode);                // the C++ engine; only the WriterHost turns it off
        Assert.Equal(MikePDF.Protocol.ProcessKind.Engine, options.Peer);
        Assert.Equal(5, options.MaxRestartsPerWindow);
    }
}
