namespace Quire.Integration.Tests.Support;

/// <summary>What the negative-access probe must show, for any sandboxed process that gets the engine's profile.</summary>
internal static class ProbeAssertions
{
    // Rows that cannot be shown to succeed unsandboxed on an ordinary developer machine: an administrative share, and a clipboard
    // that a non-interactive session may refuse. They are still required to be denied in the sandbox.
    private static readonly HashSet<string> NotDiscriminating = ["fs.read.unc", "ui.clipboard.read"];

    public static void AssertEverythingIsDenied(ProbeReport control, ProbeReport sandboxed)
    {
        Assert.True(control.Done, "the control run did not finish:\n" + control.Raw);
        Assert.True(sandboxed.Done, $"the sandboxed run did not finish (exit 0x{sandboxed.ExitCode:X}):\n{sandboxed.Raw}");

        // The probe really is in a zero-capability AppContainer at low integrity.
        Assert.Equal("1", sandboxed.Info["isAppContainer"]);
        Assert.Equal("0", sandboxed.Info["capabilityCount"]);
        Assert.Equal("0x1000", sandboxed.Info["integrityRid"]);
        Assert.Equal("ok", sandboxed.Info["runtimeSignaturePolicy"]);

        var denyRows = sandboxed.Rows.Where(r => r.Expect == "deny").ToList();
        Assert.True(denyRows.Count >= 14, "the probe ran fewer checks than the suite has");
        foreach (var row in denyRows)
        {
            Assert.True(row.Outcome == "DENIED", $"{row.Name} was {row.Outcome} in the sandbox (Win32 {row.Win32}): {row.Detail}");
        }

        foreach (var row in sandboxed.Rows.Where(r => r.Expect == "allow"))
        {
            Assert.True(row.Outcome == "ALLOWED", $"positive control {row.Name} failed in the sandbox (Win32 {row.Win32}): {row.Detail}");
        }

        // Each check could have succeeded: unsandboxed, the same row is ALLOWED.
        foreach (var row in control.Rows.Where(r => r.Expect == "deny" && !NotDiscriminating.Contains(r.Name)))
        {
            Assert.True(row.Outcome == "ALLOWED", $"control: {row.Name} was {row.Outcome} outside the sandbox (Win32 {row.Win32}), so denying it proves nothing: {row.Detail}");
        }

        Assert.Equal(control.Rows.Select(r => r.Name), sandboxed.Rows.Select(r => r.Name));
    }

    public static void AssertMitigations(ProbeReport report, bool dynamicCodeProhibited)
    {
        Assert.True(report.Done, report.Raw);

        foreach (var phase in new[] { "creation", "runtime" })
        {
            Assert.True(report.Flag($"mit.{phase}.DEP", 0), $"DEP ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ASLR", 0), $"bottom-up ASLR ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ASLR", 1), $"force-relocate images ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ASLR", 2), $"high-entropy ASLR ({phase})");
            Assert.Equal(dynamicCodeProhibited, report.Flag($"mit.{phase}.DynamicCode", 0));
            Assert.True(report.Flag($"mit.{phase}.StrictHandleCheck", 0), $"strict handle checks ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ExtensionPointDisable", 0), $"extension points disabled ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ControlFlowGuard", 0), $"control flow guard ({phase})");
            Assert.True(report.Flag($"mit.{phase}.FontDisable", 0), $"non-system fonts disabled ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ImageLoad", 0), $"no remote images ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ImageLoad", 1), $"no low-label images ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ImageLoad", 2), $"prefer System32 images ({phase})");
            Assert.True(report.Flag($"mit.{phase}.ChildProcess", 0), $"no child processes ({phase})");

            // ADR-0004 D-1: Win32k lockdown is deliberately not applied in v1.0 (stock PDFium cannot start under it).
            Assert.False(report.Flag($"mit.{phase}.SystemCallDisable", 0), $"Win32k lockdown ({phase})");
        }

        // The signature policy is a runtime policy: absent at creation, applied by the process itself before it touches a document.
        Assert.False(report.Flag("mit.creation.Signature", 0));
        Assert.True(report.Flag("mit.runtime.Signature", 0));
    }
}
