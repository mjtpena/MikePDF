#if MIKEPDF_SELFTEST
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MikePDF.App.Services;
using MikePDF.Broker;

namespace MikePDF.App;

/// <summary>
/// TEST BUILDS ONLY, part of <c>MikePDF.exe --selftest</c>. Runs <c>Probe\MikePDF.Engine.SandboxProbe.exe</c> in the very AppContainer the engine runs in
/// (same profile, same <see cref="SandboxPolicy"/>, launched by the same <see cref="SandboxedProcessLauncher"/>, from the package directory) and fails unless
/// the rows are the ones ADR-0004 amendment A2 records: every negative-access row denied, the engine unable to write to or rename its own directory,
/// the broker's protected directory unreadable and unmovable, the runtime signature policy applied, and the package's own capability the only one.
/// The unsigned DLL gets a process of its own, because the loader ends the process that loads it in the package directory (A2): the DLL is a marker, whose
/// code says so through the probe's channel if it runs, and a second run without the signature policy proves that the marker can say so in this sandbox.
/// </summary>
internal static class SandboxProbeCheck
{
    private static readonly string[] DenyRows =
    [
        "fs.read.documents.open",
        "fs.read.documents.enumerate",
        "fs.write.temp",
        "fs.write.repo-dir",
        "fs.read.repo-file",
        "fs.write.engine-dir",
        "fs.rename.engine-dir",
        "fs.read.protected-private-file",
        "fs.rename.protected-private-dir",
        "fs.read.unc",
        "net.tcp.connect-loopback",
        "proc.create.cmd.exe",
        "proc.open.parent.query-limited",
        "proc.open.parent.vm-read-dup-handle",
        "ui.clipboard.read",
        "reg.write.hkcu",
        "dll.load.unsigned-temp-copy",
    ];

    private static readonly string[] AllowRows = ["ctl.read.engine-dir", "ctl.dll.load.microsoft-signed"];

    private const string Marker = "ROGUE-CODE-RAN";
    private const string RogueRow = "dll.load.rogue-marker";

    private sealed record Row(string Name, string Expect, string Outcome, int Win32, string Detail);

    private sealed record Report(Dictionary<string, string> Info, List<Row> Rows, bool Done, string Raw, uint ExitCode, bool Exited);

    /// <summary>Runs the probe three times (the rows, the unsigned DLL alone, the same DLL without the signature policy) and returns what is wrong, if anything.</summary>
    public static List<string> Run(EngineSupervisorOptions options, string pdf, Action<string, string> field)
    {
        var problems = new List<string>();
        var probeDirectory = Path.Combine(AppContext.BaseDirectory, "Probe");
        var probe = Path.Combine(probeDirectory, "MikePDF.Engine.SandboxProbe.exe");
        var rogueCopy = Path.Combine(probeDirectory, "rogue-copy.dll");
        var marker = Path.Combine(probeDirectory, "rogue-marker.dll");
        foreach (var file in new[] { probe, rogueCopy, marker }.Where(f => !File.Exists(f)))
        {
            problems.Add(Path.GetFileName(file) + " is missing from the Probe folder of the package (build/package.ps1 -TestBuild puts it there)");
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var launcher = new SandboxedProcessLauncher(options.Profile, options.Policy);
        var engineDirectory = Path.GetDirectoryName(options.EnginePath)!;

        // A file in the directory only the broker may read. The probe tries to read it and to move the directory.
        MikePDF.Broker.PrivateDirectory.Ensure(AppPaths.PrivateDirectory);
        var protectedFile = Path.Combine(AppPaths.PrivateDirectory, "selftest-protected.txt");
        File.WriteAllText(protectedFile, "broker only");
        var temp = Path.Combine(Path.GetTempPath(), "mikepdf-probe-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        File.Copy(rogueCopy, temp);

        // Something must be listening on the loopback address, or "denied" cannot be told from "nobody there": the probe itself, started as an
        // ordinary process in listening mode (native code; the app opens no socket), names its port in a file and ends when this process does.
        var portFile = Path.Combine(Path.GetTempPath(), "mikepdf-probe-port-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        using var listener = Process.Start(new ProcessStartInfo(probe, $"--listen {Quote(portFile)} --parent {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The loopback listener did not start.");
        try
        {
            var port = 0;
            for (var attempt = 0; attempt < 100 && port == 0; attempt++)
            {
                Thread.Sleep(100);
                _ = File.Exists(portFile) && int.TryParse(File.ReadAllText(portFile), NumberStyles.None, CultureInfo.InvariantCulture, out port);
            }

            if (port == 0)
            {
                problems.Add("the helper that listens on the loopback address did not report its port");
                return problems;
            }

            var arguments = string.Join(' ', new[]
            {
                "--documents", Quote(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                "--temp", Quote(Path.GetTempPath()),
                "--repo-dir", Quote(Path.GetDirectoryName(Path.GetFullPath(pdf))!),
                "--repo-file", Quote(Path.GetFullPath(pdf)),
                "--unc", Quote(@"\\localhost\c$"),
                "--tcp-loopback", port.ToString(CultureInfo.InvariantCulture),
                "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--temp-dll", Quote(temp),
                "--engine-dir", Quote(engineDirectory),
                "--protected-file", Quote(protectedFile),
                "--rename-protected-dir", Quote(AppPaths.PrivateDirectory),
                "--rename-engine-dir", Quote(engineDirectory),
            });

            var main = RunProbe(launcher, probe, arguments, TimeSpan.FromSeconds(90), "rows", problems, field);
            if (main is not null)
            {
                Assess(main, problems);
                field("probeRows", main.Rows.Count.ToString(CultureInfo.InvariantCulture));
                field("probeCapabilities", main.Info.GetValueOrDefault("capabilitySids", string.Empty));
            }

            // The marker can speak in this sandbox: without the signature policy (--control) its code runs and says so.
            var control = RunProbe(launcher, probe, $"--rogue-only {Quote(marker)} --control", TimeSpan.FromSeconds(60), "control", problems, field);

            // The unsigned DLL under the policy, last and in a process of its own: the loader ends the process, and Windows may then treat the
            // package as damaged and refuse to start anything more from it until it is reinstalled.
            var rogue = RunProbe(launcher, probe, $"--rogue-only {Quote(marker)}", TimeSpan.FromSeconds(60), "rogue", problems, field);
            if (control is null || rogue is null)
            {
                return problems;
            }

            var rogueRow = rogue.Rows.FirstOrDefault(r => r.Name == RogueRow);
            var codeRan = rogue.Raw.Contains(Marker, StringComparison.Ordinal);
            var proven = control.Raw.Contains(Marker, StringComparison.Ordinal);
            field("rogueDll", $"row={(rogueRow is null ? "(none: the process ended first)" : rogueRow.Outcome + " Win32 " + rogueRow.Win32.ToString(CultureInfo.InvariantCulture))}, exit code 0x{rogue.ExitCode:X}, code ran={codeRan}, marker audible without the policy={proven}");
            if (!proven)
            {
                problems.Add("the marker DLL did not run even without the signature policy, so its silence under the policy proves nothing: " + Brief(control.Raw));
            }

            if (codeRan)
            {
                problems.Add("the unsigned DLL's code ran under the runtime signature policy");
            }

            if (!rogue.Exited)
            {
                problems.Add("the process that loaded the unsigned DLL neither finished nor was ended by the loader");
            }

            if (rogueRow is not null && rogueRow.Outcome != "DENIED")
            {
                problems.Add($"{RogueRow} was {rogueRow.Outcome} (Win32 {rogueRow.Win32})");
            }
        }
        finally
        {
            listener.Kill();
            File.Delete(portFile);
            File.Delete(temp);
            File.Delete(protectedFile);
        }

        return problems;
    }

    private static void Assess(Report report, List<string> problems)
    {
        if (!report.Exited || !report.Done)
        {
            problems.Add($"the probe did not finish (exit code 0x{report.ExitCode:X}, last row {report.Rows.LastOrDefault()?.Name ?? "(none)"}): {Brief(report.Raw)}");
        }

        void Info(string key, string expected)
        {
            if (!report.Info.TryGetValue(key, out var value) || value != expected)
            {
                problems.Add($"{key} is {(report.Info.TryGetValue(key, out value) ? value : "(missing)")}, expected {expected}");
            }
        }

        Info("isAppContainer", "1");
        Info("integrityRid", "0x1000");
        Info("runtimeSignaturePolicy", "ok");

        // A2: the one capability a packaged AppContainer child has is its package's own (S-1-15-3-...).
        var capabilities = report.Info.GetValueOrDefault("capabilitySids", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (report.Info.GetValueOrDefault("capabilityCount") != "1" || capabilities.Length != 1 || !capabilities[0].StartsWith("S-1-15-3-", StringComparison.Ordinal))
        {
            problems.Add($"capabilities: {report.Info.GetValueOrDefault("capabilityCount")} [{string.Join(',', capabilities)}], expected exactly the package's own");
        }

        foreach (var name in DenyRows)
        {
            Expect(report, problems, name, "deny", "DENIED");
        }

        foreach (var name in AllowRows)
        {
            Expect(report, problems, name, "allow", "ALLOWED");
        }
    }

    private static void Expect(Report report, List<string> problems, string name, string expect, string outcome)
    {
        var rows = report.Rows.Where(r => r.Name == name).ToList();
        if (rows.Count != 1)
        {
            problems.Add($"{name}: {rows.Count} rows reported, expected 1");
        }
        else if (rows[0].Expect != expect || rows[0].Outcome != outcome)
        {
            problems.Add($"{name} was {rows[0].Outcome} (Win32 {rows[0].Win32}, {rows[0].Detail}), expected {outcome}");
        }
    }

    /// <summary>Runs one probe process. A launch failure becomes a problem rather than an exception, so the runs before it still report; the
    /// package's status after each run is recorded, because a launch refused with "try reinstalling" means Windows judged the package damaged.</summary>
    private static Report? RunProbe(SandboxedProcessLauncher launcher, string probe, string arguments, TimeSpan timeout, string name, List<string> problems, Action<string, string> field)
    {
        try
        {
            using var process = launcher.Launch(new SandboxLaunchRequest(probe, arguments));
            var exited = process.WaitForExitAsync(timeout).GetAwaiter().GetResult();
            return Parse(process.LogText, process.ExitCode, exited);
        }
        catch (SandboxLaunchException e)
        {
            problems.Add($"the {name} run did not start: {e.Message} (Win32 {e.Win32Error.ToString(CultureInfo.InvariantCulture)})");
            return null;
        }
        finally
        {
            field("packageStatusAfter_" + name, PackageStatus());
        }
    }

    private static string PackageStatus()
    {
        var status = Windows.ApplicationModel.Package.Current.Status;
        var flags = new[]
        {
            (status.VerifyIsOK(), "ok"), (status.Tampered, "tampered"), (status.Modified, "modified"), (status.NotAvailable, "notAvailable"),
            (status.DataOffline, "dataOffline"), (status.DependencyIssue, "dependencyIssue"), (status.LicenseIssue, "licenseIssue"), (status.Servicing, "servicing"),
            (status.NeedsRemediation, "needsRemediation"), (status.DeploymentInProgress, "deploymentInProgress"),
        };
        return string.Join(',', flags.Where(f => f.Item1).Select(f => f.Item2));
    }

    private static Report Parse(string text, uint exitCode, bool exited)
    {
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<Row>();
        var done = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            switch (fields[0])
            {
                case "INFO" when fields.Length >= 3:
                    info[fields[1]] = fields[2];
                    break;
                case "RESULT" when fields.Length >= 6 && int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var win32):
                    rows.Add(new Row(fields[1], fields[2], fields[3], win32, fields[5]));
                    break;
                case "DONE":
                    done = true;
                    break;
                default:
                    break;
            }
        }

        return new Report(info, rows, done, text, exitCode, exited);
    }

    private static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

    private static string Brief(string text) => new StringBuilder(text.Length > 600 ? text[^600..] : text).Replace('\n', ' ').Replace('\r', ' ').ToString();
}
#endif
