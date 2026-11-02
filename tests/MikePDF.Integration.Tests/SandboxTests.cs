using System.Globalization;
using System.Text;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;

namespace MikePDF.Integration.Tests;

/// <summary>
/// ADR-0004's negative-access suite, run from inside the same sandbox profile the engine gets. The probe is a test-only executable
/// that tries everything the sandbox must forbid; each denial is checked against an unsandboxed control run, so a check that
/// could never have succeeded cannot pass vacuously.
/// </summary>
public sealed class SandboxTests(SandboxFixture sandbox)
{
    [Fact]
    public async Task EveryNegativeAccessProbeIsDeniedInTheSandbox()
    {
        using var runner = new ProbeRunner(sandbox);
        var control = await runner.RunControlAsync();
        var sandboxed = await runner.RunSandboxedAsync();
        WriteReport(control, sandboxed);

        ProbeAssertions.AssertEverythingIsDenied(control, sandboxed);
    }

    [Fact]
    public async Task TheMitigationsOfAdr0004AreInEffectInTheLaunchedProcess()
    {
        using var runner = new ProbeRunner(sandbox);
        var report = await runner.RunSandboxedAsync();
        Assert.True(report.Done, report.Raw);

        ProbeAssertions.AssertMitigations(report, dynamicCodeProhibited: true);
    }

    [Fact]
    public async Task TheJobReportsTheProcessLeavingAndEmptiesOnce()
    {
        var events = new List<EngineJobEvent>();
        var launcher = new SandboxedProcessLauncher(sandbox.Profile);
        using var process = launcher.Launch(new SandboxLaunchRequest(sandbox.ProbePath, "--no-such-option 1") { OnJobEvent = e => { lock (events) { events.Add(e); } } });

        Assert.True(await process.WaitForExitAsync(TimeSpan.FromSeconds(30)));
        await process.Job.Emptied.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2u, process.ExitCode);
        lock (events)
        {
            Assert.Contains(events, e => e.Kind is EngineJobEventKind.ProcessExited or EngineJobEventKind.ProcessExitedAbnormally);
            Assert.Single(events, e => e.Kind == EngineJobEventKind.ActiveProcessZero);
        }

        Assert.False(process.Job.MemoryLimitExceeded);
    }

    [Fact]
    public async Task AnEngineThatCannotBeLaunchedInTheSandboxIsAnErrorNotAWeakerLaunch()
    {
        // A directory the AppContainer was never granted: the process cannot even start there. There is no fallback to a normal token.
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-no-acl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(sandbox.EnginePath, Path.Combine(directory, "MikePDF.Engine.exe"));
            File.Copy(Path.Combine(sandbox.DeployDirectory, "pdfium.dll"), Path.Combine(directory, "pdfium.dll"));
            var options = sandbox.Options(o => o with { EnginePath = Path.Combine(directory, "MikePDF.Engine.exe"), StartupTimeout = TimeSpan.FromSeconds(10) });
            var failure = await Assert.ThrowsAsync<EngineStartException>(() => EngineSupervisor.StartAsync(options));
            Assert.NotEmpty(failure.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Throws<SandboxLaunchException>(() => new SandboxedProcessLauncher(sandbox.Profile).Launch(new SandboxLaunchRequest(Path.Combine(directory, "missing.exe"), string.Empty)));
    }

    [Fact]
    public void AProfileIsCreatedThenDerivedThenDeleted()
    {
        var name = "MikePDF.IT." + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var created = AppContainerProfile.CreateOrOpen(name);
            Assert.True(created.CreatedByThisCall);
            var again = AppContainerProfile.CreateOrOpen(name);
            Assert.False(again.CreatedByThisCall);
            Assert.Equal(created.Sid, again.Sid);
            Assert.StartsWith("S-1-15-2-", created.Sid.Value, StringComparison.Ordinal); // an AppContainer SID

            AppContainerProfile.Delete(name);
            AppContainerProfile.Delete(name); // already gone: not an error
            Assert.True(AppContainerProfile.CreateOrOpen(name).CreatedByThisCall);
        }
        finally
        {
            AppContainerProfile.Delete(name);
        }

        Assert.Throws<ArgumentException>(() => AppContainerProfile.CreateOrOpen("x"));
        Assert.Throws<ArgumentException>(() => AppContainerProfile.CreateOrOpen("has space"));
        Assert.Throws<ArgumentException>(() => AppContainerProfile.CreateOrOpen(new string('a', 65)));
    }

    [Fact]
    public void ReadAccessIsGrantedToTheContainerOnlyAndCanBeRevoked()
    {
        var directory = Path.Combine(sandbox.RepositoryRoot, "build", "out", "acl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            Assert.DoesNotContain(HasAce(directory), s => s == sandbox.Profile.Sid.Value);
            EngineDeployment.EnsureAppContainerAccess(directory, sandbox.Profile);
            EngineDeployment.EnsureAppContainerAccess(directory, sandbox.Profile); // idempotent
            Assert.Single(HasAce(directory), s => s == sandbox.Profile.Sid.Value);
            EngineDeployment.RevokeAppContainerAccess(directory, sandbox.Profile.Sid);
            EngineDeployment.RevokeAppContainerAccess(directory, sandbox.Profile.Sid);
            Assert.DoesNotContain(HasAce(directory), s => s == sandbox.Profile.Sid.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static List<string> HasAce(string path)
        {
            var rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
            return [.. rules.Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value)];
        }
    }

    [Fact]
    public void ASlotRingRefusesNonsenseSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotRing(0, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotRing(1025, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotRing(1, 100));
        Assert.Throws<ArgumentException>(() => new SlotRing(1, 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotRing(1024, 64 << 20)); // 64 GiB
        using var ring = new SlotRing(2, 8192);
        Assert.True(ring.TryRent(out var a));
        Assert.True(ring.TryRent(out var b));
        Assert.False(ring.TryRent(out _));
        Assert.Equal(8192, a!.Bytes.Length);
        a.Dispose();
        b!.Dispose();
    }

    private void WriteReport(ProbeReport control, ProbeReport sandboxed)
    {
        var table = new StringBuilder();
        table.AppendLine("# Sandbox probe results");
        table.AppendLine();
        table.AppendLine("Generated by `SandboxTests.EveryNegativeAccessProbeIsDeniedInTheSandbox`. Same probe, same arguments: once unsandboxed (control), once through `SandboxedProcessLauncher` with the engine's profile.");
        table.AppendLine();
        table.AppendLine("| Check | Expect | Unsandboxed control | Sandboxed | Win32 (sandboxed) | Detail |");
        table.AppendLine("|---|---|---|---|---|---|");
        foreach (var row in sandboxed.Rows)
        {
            var controlRow = control.Rows.FirstOrDefault(c => c.Name == row.Name);
            table.AppendLine(CultureInfo.InvariantCulture, $"| `{row.Name}` | {row.Expect} | {controlRow?.Outcome ?? "n/a"} | **{row.Outcome}** | {row.Win32} | {row.Detail} |");
        }

        table.AppendLine();
        table.AppendLine(CultureInfo.InvariantCulture, $"Off-machine TCP check ({ProbeRunner.LanEndpoint}, a documentation address): the sandbox answers 10013, the control does not. Token: AppContainer={sandboxed.Info.GetValueOrDefault("isAppContainer")}, integrity RID={sandboxed.Info.GetValueOrDefault("integrityRid")}, capabilities={sandboxed.Info.GetValueOrDefault("capabilityCount")}, privileges={sandboxed.Info.GetValueOrDefault("privilegeCount")}, runtime signature policy={sandboxed.Info.GetValueOrDefault("runtimeSignaturePolicy")}.");
        table.AppendLine();
        table.AppendLine("| Mitigation | At creation | After the runtime policy |");
        table.AppendLine("|---|---|---|");
        foreach (var key in sandboxed.Info.Keys.Where(k => k.StartsWith("mit.creation.", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
        {
            var name = key["mit.creation.".Length..];
            table.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {sandboxed.Info[key]} | {sandboxed.Info.GetValueOrDefault("mit.runtime." + name)} |");
        }

        File.WriteAllText(Path.Combine(sandbox.RepositoryRoot, "build", "out", "sandbox-probe-results.md"), table.ToString());
        TestContext.Current.SendDiagnosticMessage(table.ToString());
    }
}
