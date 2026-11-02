using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>
/// The engine memory-limit policy, from the policy file to the app's engine host. The window and the engines need a desktop and a sandbox; these read the source the way
/// AppWiringTests does. The half that needs the sandbox (the value reaching the job object) is EnginePolicyTests in the integration tests.
/// </summary>
public sealed class EngineWiringTests
{
    private static string AppSource(string relative) => File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.App", relative));

    [Fact]
    public void ThePolicyValueIsReadWithItsDefaultAndItsBounds()
    {
        var absent = new PolicyProvider(new FixedPolicySource()).Current;
        Assert.Equal(2048, absent.EngineMemoryLimitMb);

        foreach (var mb in new[] { 256, 1024, 8192 })
        {
            Assert.Equal(mb, Set(mb).EngineMemoryLimitMb);
        }

        // Out of range values are not accepted, whatever they were: the engine is never started with a limit the catalogue does not allow.
        foreach (var mb in new[] { 0, 255, 8193, -1 })
        {
            var value = Set(mb).EngineMemoryLimitMb;
            Assert.InRange(value, 256, 8192);
        }
    }

    [Fact]
    public void TheEngineHostHandsThePolicyLimitToTheBudgetAndToEveryEngineItStarts()
    {
        var host = AppSource(Path.Combine("Services", "EngineHost.cs"));

        // The snapshot's value is what the host keeps ...
        Assert.Matches(@"new EngineHost\(profile, enginePath, policy\.EngineMemoryLimitMb\)", host);
        // ... the budget (document engines and the pool engine) gets it ...
        Assert.Matches(@"new EngineBudgetOptions\s*\{[^}]*EngineMemoryLimitMb\s*=\s*engineMemoryLimitMb", host);
        // ... and so does every options object the host makes for a supervisor.
        Assert.Matches(@"ProcessMemoryLimitBytes\s*=\s*\(long\)EngineMemoryLimitMb\s*<<\s*20", host);
        // Starting without a snapshot reads the registry itself rather than falling back to the broker's built-in 2 GiB.
        Assert.Matches(@"public static EngineHost Create\(\)\s*=>\s*Create\(AppServices\.OpenPolicy\(\)\.Current\)", host);
    }

    private static PolicySnapshot Set(int megabytes)
    {
        var source = new FixedPolicySource();
        source.Values[PolicyScope.Machine].Add(new RawPolicyValue("EngineMemoryLimitMB", RawPolicyKind.Dword, megabytes));
        return new PolicyProvider(source).Current;
    }
}
