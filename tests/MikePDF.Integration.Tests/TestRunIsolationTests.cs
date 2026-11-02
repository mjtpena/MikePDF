using System.Diagnostics;
using System.Globalization;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;

namespace MikePDF.Integration.Tests;

/// <summary>
/// Two runs of this suite on one machine (the same checkout twice, or two git worktrees) must not share an AppContainer profile: a profile is a
/// machine-wide object, and the first run to finish used to delete the one the others were still using.
/// </summary>
public sealed class TestRunIsolationTests(SandboxFixture sandbox)
{
    [Fact]
    public void ThisRunsProfileIsNamedForThisRunAndNotTheOldSharedName()
    {
        Assert.NotEqual("MikePDF.IntegrationTests", sandbox.Profile.Name);
        Assert.StartsWith(SandboxFixture.ProfilePrefix + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".", sandbox.Profile.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNameIsDifferentAndFitsWhatAProfileMayBeCalled()
    {
        var names = Enumerable.Range(0, 100).Select(_ => TestProfiles.NewName(SandboxFixture.ProfilePrefix)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.InRange(name.Length, 2, 64));
        Assert.All(names, name => Assert.Matches("^[A-Za-z0-9._-]+$", name));
    }

    [Fact]
    public void TheSweepRemovesWhatADeadRunLeftAndKeepsWhatALiveRunOwns()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mikepdf-sweep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        var deadProfile = TestProfiles.NewName(SandboxFixture.ProfilePrefix);
        var liveProfile = TestProfiles.NewName(SandboxFixture.ProfilePrefix);
        try
        {
            AppContainerProfile.CreateOrOpen(deadProfile);
            AppContainerProfile.CreateOrOpen(liveProfile);

            // A run that was killed: its process id and start time name a process that has gone.
            var dead = Directory.CreateDirectory(Path.Combine(parent, "dead")).FullName;
            File.WriteAllText(Path.Combine(dead, "owner.txt"), $"{ExitedProcessId()} 1\n{deadProfile}\n");
            // A run that is going: this process.
            var live = Directory.CreateDirectory(Path.Combine(parent, "live")).FullName;
            TestProfiles.WriteOwner(live, liveProfile);
            // A directory with no record, made just now: possibly a run that has not written its record yet.
            var unknown = Directory.CreateDirectory(Path.Combine(parent, "unknown")).FullName;

            Assert.Equal(1, TestProfiles.SweepStale(parent));

            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(unknown));
            Assert.True(AppContainerProfile.CreateOrOpen(deadProfile).CreatedByThisCall, "the dead run's profile should have been deleted");
            Assert.False(AppContainerProfile.CreateOrOpen(liveProfile).CreatedByThisCall, "the live run's profile must not be touched");
        }
        finally
        {
            AppContainerProfile.Delete(deadProfile);
            AppContainerProfile.Delete(liveProfile);
            Directory.Delete(parent, recursive: true);
        }
    }

    private static int ExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }
}
