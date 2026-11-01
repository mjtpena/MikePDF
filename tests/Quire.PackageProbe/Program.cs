using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Quire.Broker;

namespace Quire.PackageProbe;

/// <summary>
/// Runs inside an installed package and tests three ways of launching the sandboxed engine:
/// a = direct from the package directory (zero capabilities), b = direct, plus the packageContents capability,
/// c = copied to the package's LocalState folder with an ACE for the AppContainer SID only.
/// For each mode: launch, Hello/AttachSlotRing/OpenDocument/RenderTile, then the full negative-access probe.
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Md = new();

    internal static string OutDir { get; private set; } = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);
        OutDir = Path.GetFullPath(options.GetValueOrDefault("--out", "probe-out"));
        Directory.CreateDirectory(OutDir);
        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Line("## HARNESS CRASH");
            Line("```\n" + e + "\n```");
            Flush();
            Console.WriteLine(Md.ToString());
            return 1;
        }
    }

    private static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        Line("# Quire sandbox-in-package probe");
        Line($"Run at {DateTime.UtcNow:O}, OS {Environment.OSVersion.Version}, arch {RuntimeInformation.ProcessArchitecture}");
        Line();

        // ---- identity ------------------------------------------------------------------------
        var fullName = Identity.Call(Identity.GetCurrentPackageFullName);
        var familyName = Identity.Call(Identity.GetCurrentPackageFamilyName);
        var packagePath = Identity.Call(Identity.GetCurrentPackagePath);
        var underWindowsApps = packagePath is not null && packagePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        Line("## Package identity (this process)");
        Line($"- GetCurrentPackageFullName: `{fullName ?? "(none: NOT PACKAGED)"}`");
        Line($"- GetCurrentPackageFamilyName: `{familyName ?? "(none)"}`");
        Line($"- GetCurrentPackagePath: `{packagePath ?? "(none)"}`");
        Line($"- Under WindowsApps: **{underWindowsApps}**");
        Line($"- Administrator role: {Native.IsAdministrator()}, user {Environment.UserName}");
        Line();
        if (packagePath is null)
        {
            Line("**The process has no package identity. Nothing below is valid.**");
            Flush();
            Console.WriteLine(Md.ToString());
            return 3;
        }

        // ---- ACLs ----------------------------------------------------------------------------
        var acl = new StringBuilder();
        foreach (var path in new[] { packagePath, Path.Combine(packagePath, "Engine"), Path.Combine(packagePath, "Engine", "Quire.Engine.exe"), Path.GetDirectoryName(packagePath)! })
        {
            acl.AppendLine("=== icacls " + path);
            acl.AppendLine(RunTool("icacls.exe", "\"" + path + "\""));
            try
            {
                FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                acl.AppendLine("SDDL: " + security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
            catch (Exception e)
            {
                acl.AppendLine("SDDL unavailable: " + e.Message);
            }

            acl.AppendLine();
        }

        acl.AppendLine("=== icacls LocalState " + Identity.LocalStatePath());
        acl.AppendLine(RunTool("icacls.exe", "\"" + Identity.LocalStatePath() + "\""));
        File.WriteAllText(Path.Combine(OutDir, "acl.txt"), acl.ToString());
        Line("## ACL of the package directory (all paths in acl.txt)");
        Line("```\n" + RunTool("icacls.exe", "\"" + packagePath + "\"") + "\n```");

        // ---- inputs --------------------------------------------------------------------------
        var engineDir = Path.Combine(packagePath, "Engine");
        var fixtures = Path.Combine(packagePath, "Fixtures");
        var workspace = options.GetValueOrDefault("--workspace", OutDir);
        var otherPackageFile = options.GetValueOrDefault("--other-package-file", string.Empty);
        using var network = new NetworkFixture();
        Line($"- Engine directory: `{engineDir}`; other-package file given to the probe: `{(otherPackageFile.Length == 0 ? "(none)" : otherPackageFile)}`");
        Line();

        var profile = AppContainerProfile.CreateOrOpen("Quire.PackageProbe", "Quire package probe", "Zero-capability AppContainer for the in-package sandbox probe");
        Line($"AppContainer profile `{profile.Name}` created by this call: {profile.CreatedByThisCall}; SID `{profile.Sid.Value}`");
        Line();

        var ownData = Path.Combine(Identity.LocalStatePath(), "parent-private-data.txt");
        File.WriteAllText(ownData, "private data of the Quire package; the sandbox must not read this");
        var context = new ProbeContext(profile, fixtures, workspace, otherPackageFile, ownData, network);

        // Unsandboxed control from the package directory: proves each negative check could succeed.
        var control = await context.RunProbeControlAsync(Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe")).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(OutDir, "probe-control.txt"), control.Raw);
        Line($"Control (unsandboxed) probe: finished={control.Done}, rows={control.Rows.Count}, deny-rows that were ALLOWED in the control={control.Rows.Count(r => r.Expect == "deny" && r.Outcome == "ALLOWED")}");
        Line();

        var results = new List<ModeResult>();
        var only = options.GetValueOrDefault("--modes", "a,b,c,g,h,i,j").Split(',');

        if (only.Contains("a"))
        {
            results.Add(await context.RunModeAsync("a", "direct from the package directory, zero capabilities", engineDir, new SandboxPolicy(), 0, control).ConfigureAwait(false));
            WriteMode(results[^1]);
        }

        if (only.Contains("b"))
        {
            var policy = new SandboxPolicy { NamedCapabilities = ["packageContents"] };
            results.Add(await context.RunModeAsync("b", "direct from the package directory, only the packageContents capability", engineDir, policy, 1, control).ConfigureAwait(false));
            WriteMode(results[^1]);
        }

        if (only.Contains("e"))
        {
            results.Add(await context.RunModeAsync("e", "direct from the package directory, child breaks away from the package", engineDir, new SandboxPolicy { BreakawayFromPackage = true }, 0, control).ConfigureAwait(false));
            WriteMode(results[^1]);
        }

        var copyTargets = new (string Mode, string Title, string Base, bool Breakaway)[]
        {
            ("c", "copied to LocalState, AppContainer SID read+execute only", Identity.LocalStatePath(), false),
            ("g", "copied to ProgramData\\Quire-probe, AppContainer SID read+execute only", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Quire-probe"), false),
            ("h", "copied to LOCALAPPDATA\\Quire-probe (MSIX may redirect this), AppContainer SID read+execute only", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quire-probe"), false),
            ("d", "copied to LocalState, child breaks away from the package", Identity.LocalStatePath(), true),
            ("i", "copied to ProgramData\\Quire-probe, free-standing (non-child) AppContainer SID", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Quire-probe"), false),
            ("j", "direct from the package directory, free-standing (non-child) AppContainer SID", engineDir, false),
        };
        foreach (var target in copyTargets)
        {
            if (!only.Contains(target.Mode))
            {
                continue;
            }

            var modeName = target.Mode;
            var modeProfile = modeName is "i" or "j" ? AppContainerProfile.Unregistered("Quire.PackageProbe.Free") : profile;
            if (modeName == "j")
            {
                results.Add(await context.RunModeAsync(modeName, target.Title, engineDir, new SandboxPolicy(), 0, control, modeProfile).ConfigureAwait(false));
                WriteMode(results[^1]);
                continue;
            }

            var copy = Path.Combine(target.Base, "engine-copy-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(copy);
                EngineDeployment.EnsureAppContainerAccess(copy, modeProfile); // before the copy, so the files inherit it
                foreach (var file in Directory.GetFiles(engineDir))
                {
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                }

                Line($"Copy-out target (mode {modeName}): `{copy}`");
                File.WriteAllText(Path.Combine(OutDir, "acl-copy-" + modeName + ".txt"), RunTool("icacls.exe", "\"" + copy + "\""));
                if (modeName == "h")
                {
                    var physical = Path.GetFullPath(Path.Combine(Identity.LocalStatePath(), "..", "LocalCache", "Local", "Quire-probe"));
                    Line($"- where MSIX redirection put it: `{physical}` exists={Directory.Exists(physical)}");
                }

                results.Add(await context.RunModeAsync(modeName, target.Title, copy, new SandboxPolicy { BreakawayFromPackage = target.Breakaway }, 0, control, modeProfile).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                results.Add(ModeResult.Failed(modeName, "copy-out harness error: " + e));
            }
            finally
            {
                try
                {
                    EngineDeployment.RevokeAppContainerAccess(copy, modeProfile.Sid);
                    Directory.Delete(copy, recursive: true);
                }
                catch (Exception e)
                {
                    Line("(cleanup of the copy failed: " + e.Message + ")");
                }
            }

            WriteMode(results[^1]);
        }

        // ---- summary -------------------------------------------------------------------------
        Line("## Summary");
        Line("| mode | engine launched | real tile rendered | original negative suite all denied | other-package read denied | own package data read+write denied | token capabilities | notes |");
        Line("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            Line($"| {r.Mode} ({r.Title}) | {r.Launched} | {r.Rendered} | {r.OriginalSuiteDenied} | {r.OtherPackageDenied} | {r.OwnDataDenied} | {r.Capabilities} | {Short(r.Summary)} |");
        }

        Line();
        try
        {
            profile.Delete();
        }
        catch (Exception e)
        {
            Line("(profile delete failed: " + e.Message + ")");
        }

        Flush();
        Console.WriteLine(Md.ToString());
        return 0;
    }

    internal static void Line(string text = "") => Md.AppendLine(text);

    internal static string RunTool(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return text;
        }
        catch (Exception e)
        {
            return "(could not run " + exe + ": " + e.Message + ")";
        }
    }

    private static string Short(string text) => (text.Length > 300 ? text[..300] : text).Replace("\n", " ").Replace("\t", " ").Replace("|", "/");

    private static void WriteMode(ModeResult r)
    {
        Line($"### Mode {r.Mode}: {r.Title}");
        Line($"- engine launched: **{r.Launched}** {r.LaunchDetail}");
        Line($"- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **{r.Rendered}** {r.RenderDetail}");
        Line($"- negative probe: finished={r.ProbeRan}, original suite all denied=**{r.OriginalSuiteDenied}**, other-package denied={r.OtherPackageDenied}, own-package-data denied={r.OwnDataDenied}; token capabilities: {r.Capabilities}{(r.CapabilityNote.Length > 0 ? " **(differs from the request: " + r.CapabilityNote + ")**" : string.Empty)}");
        foreach (var d in r.ProbeProblems)
        {
            Line("  - " + d);
        }

        Line();
        Flush();
    }

    private static void Flush() => File.WriteAllText(Path.Combine(OutDir, "results.md"), Md.ToString());

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            d[args[i]] = args[i + 1];
        }

        return d;
    }
}

internal sealed record ProbeRow(string Name, string Expect, string Outcome, int Win32, string Detail);

internal sealed record ProbeReport(Dictionary<string, string> Info, List<ProbeRow> Rows, bool Done, string Raw, uint ExitCode);

internal sealed class ModeResult
{
    public string Mode { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool Launched { get; set; }

    public string LaunchDetail { get; set; } = string.Empty;

    public bool Rendered { get; set; }

    public string RenderDetail { get; set; } = string.Empty;

    public bool ProbeRan { get; set; }

    public bool AllDenied { get; set; }

    public string Capabilities { get; set; } = string.Empty;

    public string CapabilityNote { get; set; } = string.Empty;

    public List<string> ProbeProblems { get; } = [];

    public bool OriginalSuiteDenied { get; set; }

    public bool OtherPackageDenied { get; set; }

    public bool OwnDataDenied { get; set; }

    public string Summary => string.Join("; ", ProbeProblems.Take(3));

    public static ModeResult Failed(string mode, string detail) => new() { Mode = mode, Title = detail, LaunchDetail = detail };
}

internal sealed class NetworkFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, 0);

    public NetworkFixture()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
            {
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? LanEndpoint
    {
        get
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return address is null ? null : address + ":" + Port.ToString(CultureInfo.InvariantCulture);
        }
    }

    public void Dispose() => _listener.Stop();
}

internal sealed class ProbeContext(AppContainerProfile defaultProfile, string fixtures, string workspace, string otherPackageFile, string ownDataFile, NetworkFixture network)
{
    private string _scratch = string.Empty;

    private string Scratch
    {
        get
        {
            if (_scratch.Length == 0)
            {
                _scratch = Path.Combine(workspace, "probe-scratch");
                Directory.CreateDirectory(_scratch);
            }

            return _scratch;
        }
    }

    public async Task<ProbeReport> RunProbeControlAsync(string probeExe)
    {
        var temp = Path.Combine(Path.GetTempPath(), "quire-probe-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        try
        {
            File.Copy(Path.Combine(Path.GetDirectoryName(probeExe)!, "rogue-copy.dll"), temp);
            var start = new ProcessStartInfo(probeExe, Arguments(Path.GetDirectoryName(probeExe)!, temp, control: true))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(probeExe)!,
            };
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            return Parse(output, (uint)process.ExitCode);
        }
        catch (Exception e)
        {
            return new ProbeReport([], [], false, "control run failed: " + e, 0);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task<ModeResult> RunModeAsync(string mode, string title, string engineDir, SandboxPolicy policy, int expectedCapabilities, ProbeReport control, AppContainerProfile? profileOverride = null)
    {
        var profile = profileOverride ?? defaultProfile;
        var result = new ModeResult { Mode = mode, Title = title };
        var enginePath = Path.Combine(engineDir, "Quire.Engine.exe");

        // ---- engine: launch + the real flow -----------------------------------------------------
        EngineSupervisor? supervisor = null;
        try
        {
            supervisor = await EngineSupervisor.StartAsync(new EngineSupervisorOptions
            {
                EnginePath = enginePath,
                Profile = profile,
                Policy = policy,
                SlotCount = 4,
                SlotSize = 1 << 20,
                StartupTimeout = TimeSpan.FromSeconds(20),
            }).ConfigureAwait(false);
            result.Launched = true;
            result.LaunchDetail = $"(engine pid {supervisor.EngineProcessId}; Hello and AttachSlotRing succeeded)";
        }
        catch (Exception e)
        {
            result.LaunchDetail = "-> " + e.GetType().Name + ": " + e.Message + (e.InnerException is null ? string.Empty : " | inner: " + e.InnerException.Message + (e.InnerException is SandboxLaunchException s ? $" (Win32 {s.Win32Error})" : string.Empty));
        }

        if (supervisor is not null)
        {
            try
            {
                var opened = await supervisor.OpenDocumentAsync(Path.Combine(fixtures, "rectangles.pdf")).ConfigureAwait(false);
                if (!opened.IsOk || opened.Value is null)
                {
                    result.RenderDetail = $"OpenDocument -> {opened.Status} {opened.Detail}";
                }
                else
                {
                    await using var document = opened.Value;
                    var tile = await document.RenderTileAsync(new RenderTileArgs(0, 1.0f, 0, 0, 0, 200, 300), 1).ConfigureAwait(false);
                    if (tile.Value is null)
                    {
                        result.RenderDetail = $"RenderTile -> {tile.Status} {tile.Detail}";
                    }
                    else
                    {
                        using var lease = tile.Value;
                        var nonWhite = CountNonWhite(lease);
                        result.Rendered = nonWhite > 200;
                        result.RenderDetail = $"({lease.Width}x{lease.Height}, {nonWhite} non-white pixels; corners TL {Pixel(lease, 5, 5):X8} TR {Pixel(lease, lease.Width - 6, 5):X8} BL {Pixel(lease, 5, lease.Height - 6):X8} BR {Pixel(lease, lease.Width - 6, lease.Height - 6):X8})";
                    }
                }
            }
            catch (Exception e)
            {
                result.RenderDetail = "exception: " + e;
            }

            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        // ---- negative probe in the same profile and policy ---------------------------------------
        var probePath = Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe");
        var temp = Path.Combine(Path.GetTempPath(), "quire-probe-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        try
        {
            File.Copy(Path.Combine(engineDir, "rogue-copy.dll"), temp);
            var launcher = new SandboxedProcessLauncher(profile, policy);
            using var process = launcher.Launch(new SandboxLaunchRequest(probePath, Arguments(engineDir, temp, control: false)));
            var exited = await process.WaitForExitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            var report = Parse(process.LogText, process.ExitCode);
            File.WriteAllText(Path.Combine(Program.OutDir, $"probe-mode-{mode}.txt"), report.Raw);
            result.ProbeRan = report.Done;
            if (!exited || !report.Done)
            {
                result.ProbeProblems.Add($"the probe did NOT finish (exit 0x{process.ExitCode:X}); last row printed: {report.Rows.LastOrDefault()?.Name}");
            }

            Assess(result, report, control, expectedCapabilities);
        }
        catch (Exception e)
        {
            result.ProbeProblems.Add("probe launch failed: " + e.GetType().Name + ": " + e.Message + (e is SandboxLaunchException s ? $" (Win32 {s.Win32Error})" : string.Empty));
        }
        finally
        {
            File.Delete(temp);
        }

        return result;
    }

    private static int CountNonWhite(TileLease lease)
    {
        var pixels = lease.Pixels;
        var count = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                count++;
            }
        }

        return count;
    }

    private static uint Pixel(TileLease lease, int x, int y) => BitConverter.ToUInt32(lease.Pixels.Slice((y * lease.Stride) + (x * 4), 4));

    private static void Assess(ModeResult result, ProbeReport report, ProbeReport control, int expectedCapabilities)
    {
        bool Is(string key, string value) => report.Info.TryGetValue(key, out var v) && v == value;
        if (!Is("isAppContainer", "1"))
        {
            result.ProbeProblems.Add("not an AppContainer: " + report.Info.GetValueOrDefault("isAppContainer"));
        }

        result.Capabilities = $"{report.Info.GetValueOrDefault("capabilityCount")} [{report.Info.GetValueOrDefault("capabilitySids")}]";
        if (!Is("capabilityCount", expectedCapabilities.ToString(CultureInfo.InvariantCulture)))
        {
            result.CapabilityNote = $"token capabilities {result.Capabilities}, requested {expectedCapabilities}";
        }

        if (!Is("integrityRid", "0x1000"))
        {
            result.ProbeProblems.Add("integrity " + report.Info.GetValueOrDefault("integrityRid"));
        }

        if (!Is("runtimeSignaturePolicy", "ok"))
        {
            result.ProbeProblems.Add("runtime signature policy " + report.Info.GetValueOrDefault("runtimeSignaturePolicy"));
        }

        var denyRows = report.Rows.Where(r => r.Expect == "deny").ToList();
        foreach (var row in denyRows.Where(r => r.Outcome != "DENIED"))
        {
            result.ProbeProblems.Add($"{row.Name} was {row.Outcome} (Win32 {row.Win32}): {row.Detail}");
        }

        foreach (var row in report.Rows.Where(r => r.Expect == "allow" && r.Outcome != "ALLOWED"))
        {
            result.ProbeProblems.Add($"positive control {row.Name} failed (Win32 {row.Win32}): {row.Detail}");
        }

        // A denial only means something if the same check succeeds unsandboxed.
        var notDiscriminating = new HashSet<string> { "fs.read.unc", "ui.clipboard.read" };
        foreach (var row in control.Rows.Where(r => r.Expect == "deny" && !notDiscriminating.Contains(r.Name) && r.Outcome != "ALLOWED"))
        {
            result.ProbeProblems.Add($"control: {row.Name} was {row.Outcome} outside the sandbox, so its denial proves nothing");
        }

        if (denyRows.Count < 14)
        {
            result.ProbeProblems.Add($"only {denyRows.Count} deny rows ran");
        }

        string[] extra = ["fs.read.other-package-file", "fs.read.parent-package-data", "fs.write.parent-package-data"];
        result.OriginalSuiteDenied = report.Done && denyRows.Where(r => !extra.Contains(r.Name)).All(r => r.Outcome == "DENIED") && denyRows.Count(r => !extra.Contains(r.Name)) >= 14;
        result.OtherPackageDenied = report.Rows.Where(r => r.Name == "fs.read.other-package-file").All(r => r.Outcome == "DENIED");
        result.OwnDataDenied = report.Rows.Where(r => r.Name.EndsWith("parent-package-data", StringComparison.Ordinal)).All(r => r.Outcome == "DENIED");
        result.AllDenied = result.ProbeProblems.Count == 0;
    }

    private static string Trim(string text) => text.Length > 600 ? text[..600] : text;

    private string Arguments(string engineDir, string tempDll, bool control)
    {
        static string Quote(string value) => "\"" + value + "\"";
        var parts = new List<string>
        {
            "--documents", Quote(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            "--temp", Quote(Path.GetTempPath().TrimEnd('\\')),
            "--repo-dir", Quote(Scratch),
            "--repo-file", Quote(Path.Combine(workspace, "Quire.slnx")),
            "--unc", Quote(@"\\localhost\c$"),
            "--tcp-loopback", network.Port.ToString(CultureInfo.InvariantCulture),
            "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--temp-dll", Quote(tempDll),
            "--rogue-dll", Quote(Path.Combine(engineDir, "rogue-copy.dll")),
            "--engine-dir", Quote(engineDir),
        };
        if (otherPackageFile.Length > 0)
        {
            parts.AddRange(["--other-package-file", Quote(otherPackageFile)]);
        }

        parts.AddRange(["--own-data-file", Quote(ownDataFile), "--own-data-dir", Quote(Path.GetDirectoryName(ownDataFile)!)]);
        if (network.LanEndpoint is { } lan)
        {
            parts.AddRange(["--tcp-lan", lan]);
        }

        if (control)
        {
            parts.Add("--control");
        }

        return string.Join(' ', parts);
    }

    private static ProbeReport Parse(string text, uint exitCode)
    {
        var info = new Dictionary<string, string>();
        var rows = new List<ProbeRow>();
        var done = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            switch (fields[0])
            {
                case "INFO" when fields.Length >= 3:
                    info[fields[1]] = fields[2];
                    break;
                case "RESULT" when fields.Length >= 6:
                    rows.Add(new ProbeRow(fields[1], fields[2], fields[3], int.Parse(fields[4], CultureInfo.InvariantCulture), fields[5]));
                    break;
                case "DONE":
                    done = true;
                    break;
                default:
                    break;
            }
        }

        return new ProbeReport(info, rows, done, text, exitCode);
    }
}

internal static class Native
{
    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

internal static class Identity
{
    public delegate int Getter(ref uint length, StringBuilder? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackagePath(ref uint length, StringBuilder? buffer);

    public static string? Call(Getter getter)
    {
        uint length = 0;
        var rc = getter(ref length, null);
        if (rc != 122)
        {
            return null; // 15700 = APPMODEL_ERROR_NO_PACKAGE; 122 = ERROR_INSUFFICIENT_BUFFER is the "have identity" answer
        }

        var buffer = new StringBuilder((int)length);
        return getter(ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    public static string LocalStatePath() => Windows.Storage.ApplicationData.Current.LocalFolder.Path;
}
