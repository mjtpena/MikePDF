using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Quire.Broker;

namespace Quire.PackageProbe;

/// <summary>
/// Runs inside an installed package and tests how the sandboxed engine can be launched from it:
/// a = directly from the package directory, k = copied to a protected directory under LocalState (AppContainer SID read+execute only).
/// Per mode: launch, Hello/AttachSlotRing/OpenDocument/RenderTile on a real PDF, the negative-access probe, and the unsigned-DLL load in
/// its own sandboxed process (so that a process death there is recorded and every other row still runs).
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
        Line($"Run at {DateTime.UtcNow:O}, OS {Environment.OSVersion.Version}, process architecture {RuntimeInformation.ProcessArchitecture}");
        Line();

        // ---- identity and token ----------------------------------------------------------------
        var fullName = Identity.Call(Identity.GetCurrentPackageFullName);
        var familyName = Identity.Call(Identity.GetCurrentPackageFamilyName);
        var packagePath = Identity.Call(Identity.GetCurrentPackagePath);
        var underWindowsApps = packagePath is not null && packagePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        var integrity = Native.IntegrityRid();
        Line("## Package identity and token of this process (the broker)");
        Line($"- GetCurrentPackageFullName: `{fullName ?? "(none: NOT PACKAGED)"}`");
        Line($"- GetCurrentPackageFamilyName: `{familyName ?? "(none)"}`");
        Line($"- GetCurrentPackagePath: `{packagePath ?? "(none)"}`");
        Line($"- Under WindowsApps: **{underWindowsApps}**");
        Line($"- Integrity level (RID): **0x{integrity:X}** ({Native.IntegrityName(integrity)}); administrator role: {Native.IsAdministrator()}; user {Environment.UserName}");
        Line();
        if (packagePath is null)
        {
            Line("**The process has no package identity. Nothing below is valid.**");
            Flush();
            Console.WriteLine(Md.ToString());
            return 3;
        }

        // ---- ACLs ----------------------------------------------------------------------------
        var localState = Identity.LocalStatePath();
        var acl = new StringBuilder();
        foreach (var path in new[] { packagePath, Path.Combine(packagePath, "Engine"), Path.Combine(packagePath, "Engine", "Quire.Engine.exe"), Path.GetDirectoryName(packagePath)!, localState })
        {
            acl.AppendLine("=== icacls " + path);
            acl.AppendLine(RunTool("icacls.exe", "\"" + path + "\""));
        }

        // ---- the protected private directory (what the broker keeps to itself) -----------------------
        var privateDir = Path.Combine(localState, "private-probe");
        Directory.CreateDirectory(privateDir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using (var me = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(privateDir).SetAccessControl(security);
        var protectedFile = Path.Combine(privateDir, "documents.json");
        File.WriteAllText(protectedFile, "{ \"recent\": [\"C:\\\\private\\\\secret.pdf\"] }");
        acl.AppendLine("=== icacls (protected private dir) " + privateDir);
        acl.AppendLine(RunTool("icacls.exe", "\"" + privateDir + "\""));
        File.WriteAllText(Path.Combine(OutDir, "acl.txt"), acl.ToString());
        Line("## ACL of the package directory and LocalState (all paths in acl.txt)");
        Line("```\n" + RunTool("icacls.exe", "\"" + packagePath + "\"") + "\n```");
        Line("Protected private directory:\n```\n" + RunTool("icacls.exe", "\"" + privateDir + "\"") + "\n```");

        // ---- inputs --------------------------------------------------------------------------
        var engineDir = Path.Combine(packagePath, "Engine");
        var fixtures = Path.Combine(packagePath, "Fixtures");
        var workspace = options.GetValueOrDefault("--workspace", OutDir);
        var otherPackageFile = options.GetValueOrDefault("--other-package-file", string.Empty);
        using var network = new NetworkFixture();
        var profile = AppContainerProfile.CreateOrOpen("Quire.PackageProbe", "Quire package probe", "AppContainer for the in-package sandbox probe");
        Line($"AppContainer profile `{profile.Name}` created by this call: {profile.CreatedByThisCall}; SID `{profile.Sid.Value}`");
        Line();

        var ownData = Path.Combine(localState, "parent-private-data.txt");
        File.WriteAllText(ownData, "data of the Quire package, in a folder the package capability can write");
        var context = new ProbeContext(profile, fixtures, workspace, otherPackageFile, ownData, protectedFile, privateDir, network);

        // Unsandboxed controls from the package directory: each check, and the unsigned-DLL marker, must be able to succeed.
        var control = await context.RunProbeControlAsync(Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe")).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(OutDir, "probe-control.txt"), control.Raw);
        var rogueControl = await context.RunRogueControlAsync(engineDir).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(OutDir, "rogue-control.txt"), rogueControl.Raw);
        var markerControl = rogueControl.Raw.Contains("ROGUE-CODE-RAN", StringComparison.Ordinal);
        Line($"Control (unsandboxed) probe: finished={control.Done}, rows={control.Rows.Count}, deny-rows that were ALLOWED in the control={control.Rows.Count(r => r.Expect == "deny" && r.Outcome == "ALLOWED")}");
        Line($"Control for the unsigned-DLL row: the DLL loaded and its code ran (marker seen) = **{markerControl}**");
        Line();

        var results = new List<ModeResult>();
        var only = options.GetValueOrDefault("--modes", "a,k").Split(',');

        if (only.Contains("a"))
        {
            results.Add(await context.RunModeAsync("a", "direct from the package directory", engineDir, Path.Combine(packagePath, "Fixtures"), new SandboxPolicy(), control, markerControl).ConfigureAwait(false));
            WriteMode(results[^1]);
        }

        if (only.Contains("k"))
        {
            var copy = Path.Combine(localState, "engine-copy-" + Guid.NewGuid().ToString("N")[..8]);
            var twinDirectory = string.Empty;
            try
            {
                Directory.CreateDirectory(copy);
                EngineDeployment.EnsureReadOnlyDeployment(copy, profile.Sid);
                foreach (var file in Directory.GetFiles(engineDir))
                {
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                }

                // A second, identical deployment that nothing runs from, so that "can the engine move its deployment directory" is a question about
                // access and not about the running executable locking its own folder.
                var twin = copy + "-twin";
                Directory.CreateDirectory(twin);
                EngineDeployment.EnsureReadOnlyDeployment(twin, profile.Sid);
                foreach (var file in Directory.GetFiles(engineDir))
                {
                    File.Copy(file, Path.Combine(twin, Path.GetFileName(file)));
                }

                twinDirectory = twin;
                Line($"Copy-out target (mode k): `{copy}` (twin for the rename row: `{twin}`)");
                File.WriteAllText(Path.Combine(OutDir, "acl-copy-k.txt"), RunTool("icacls.exe", "\"" + copy + "\""));
                results.Add(await context.RunModeAsync("k", "copied to LocalState, protected DACL", copy, twinDirectory, new SandboxPolicy(), control, markerControl).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                results.Add(ModeResult.Failed("k", "copy-out harness error: " + e));
            }
            finally
            {
                try
                {
                    EngineDeployment.RevokeAppContainerAccess(copy, profile.Sid);
                    Directory.Delete(copy, recursive: true);
                    if (twinDirectory.Length > 0 && Directory.Exists(twinDirectory))
                    {
                        Directory.Delete(twinDirectory, recursive: true);
                    }
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
        Line($"Broker integrity level 0x{integrity:X} ({Native.IntegrityName(integrity)}), arch {RuntimeInformation.ProcessArchitecture}, OS {Environment.OSVersion.Version}.");
        Line();
        Line("| mode | launched | tile rendered | rows run / all denied except the known package-data rows | other package denied | engine dir write denied | engine dir rename denied | protected private file read denied | protected private dir rename denied | unsigned DLL: denied, code did not run | package capability in the child's token |");
        Line("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            Line($"| {r.Mode} ({r.Title}) | {r.Launched} | {r.Rendered} | {r.RowsRun} / {r.OriginalSuiteDenied} | {r.OtherPackageDenied} | {r.EngineDirWriteDenied} | {r.EngineDirRenameDenied} | {r.ProtectedFileReadDenied} | {r.ProtectedDirRenameDenied} | {r.RogueDllDenied} | {r.Capabilities} |");
        }

        Line();
        foreach (var r in results.Where(r => r.Problems.Count > 0))
        {
            Line($"- mode {r.Mode} problems: {string.Join("; ", r.Problems.Take(8))}");
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

    private static void WriteMode(ModeResult r)
    {
        Line($"### Mode {r.Mode}: {r.Title}");
        Line($"- engine launched: **{r.Launched}** {r.LaunchDetail}");
        Line($"- Hello/AttachSlotRing/OpenDocument/RenderTile with a real PDF: **{r.Rendered}** {r.RenderDetail}");
        Line($"- main probe: finished={r.ProbeRan}, rows run={r.RowsRun}; token capabilities: {r.Capabilities}");
        Line($"- unsigned DLL in its own process: {r.RogueDetail}");
        foreach (var d in r.Problems)
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

    public int RowsRun { get; set; }

    public bool OriginalSuiteDenied { get; set; }

    public bool OtherPackageDenied { get; set; }

    public bool EngineDirWriteDenied { get; set; }

    public bool EngineDirRenameDenied { get; set; }

    public bool ProtectedFileReadDenied { get; set; }

    public bool ProtectedDirRenameDenied { get; set; }

    public bool RogueDllDenied { get; set; }

    public string RogueDetail { get; set; } = string.Empty;

    public string Capabilities { get; set; } = string.Empty;

    public List<string> Problems { get; } = [];

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

internal sealed class ProbeContext(AppContainerProfile profile, string fixtures, string workspace, string otherPackageFile, string ownDataFile, string protectedFile, string protectedDir, NetworkFixture network)
{
    private static readonly string[] KnownPackageDataRows = ["fs.read.parent-package-data", "fs.write.parent-package-data"];
    private static readonly string[] SeparatelyReported = ["fs.read.other-package-file", "fs.write.engine-dir", "fs.rename.engine-dir", "fs.read.protected-private-file", "fs.rename.protected-private-dir"];

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
            return await RunUnsandboxedAsync(probeExe, Arguments(Path.GetDirectoryName(probeExe)!, temp, control: true, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(probeExe)!)!, "Fixtures"))).ConfigureAwait(false);
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

    /// <summary>Loads the marker DLL in an ordinary process: its code must run and say so, or the sandboxed "it did not run" proves nothing.</summary>
    public async Task<ProbeReport> RunRogueControlAsync(string engineDir)
    {
        var probe = Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe");
        try
        {
            return await RunUnsandboxedAsync(probe, $"--rogue-only \"{Path.Combine(engineDir, "rogue-marker.dll")}\" --control").ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return new ProbeReport([], [], false, "rogue control failed: " + e, 0);
        }
    }

    private static async Task<ProbeReport> RunUnsandboxedAsync(string exe, string arguments)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        return Parse(output, (uint)process.ExitCode);
    }

    public async Task<ModeResult> RunModeAsync(string mode, string title, string engineDir, string renameTarget, SandboxPolicy policy, ProbeReport control, bool markerControl)
    {
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

        // ---- the main negative probe, same profile and policy ------------------------------------
        var probePath = Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe");
        var temp = Path.Combine(Path.GetTempPath(), "quire-probe-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        try
        {
            File.Copy(Path.Combine(engineDir, "rogue-copy.dll"), temp);
            var launcher = new SandboxedProcessLauncher(profile, policy);
            using var process = launcher.Launch(new SandboxLaunchRequest(probePath, Arguments(engineDir, temp, control: false, renameTarget)));
            var exited = await process.WaitForExitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            var report = Parse(process.LogText, process.ExitCode);
            File.WriteAllText(Path.Combine(Program.OutDir, $"probe-mode-{mode}.txt"), report.Raw);
            result.ProbeRan = report.Done;
            if (!exited || !report.Done)
            {
                result.Problems.Add($"the main probe did NOT finish (exit 0x{process.ExitCode:X}); last row: {report.Rows.LastOrDefault()?.Name}");
            }

            Assess(result, report, control);
        }
        catch (Exception e)
        {
            result.Problems.Add("probe launch failed: " + e.GetType().Name + ": " + e.Message + (e is SandboxLaunchException s ? $" (Win32 {s.Win32Error})" : string.Empty));
        }
        finally
        {
            File.Delete(temp);
        }

        // ---- the unsigned DLL, alone in its own sandboxed process ----------------------------------
        await RunRogueAsync(result, engineDir, policy, markerControl).ConfigureAwait(false);
        return result;
    }

    private async Task RunRogueAsync(ModeResult result, string engineDir, SandboxPolicy policy, bool markerControl)
    {
        var probePath = Path.Combine(engineDir, "Quire.Engine.SandboxProbe.exe");
        var dll = Path.Combine(engineDir, "rogue-marker.dll");
        var started = DateTime.UtcNow;
        try
        {
            var launcher = new SandboxedProcessLauncher(profile, policy);
            using var process = launcher.Launch(new SandboxLaunchRequest(probePath, $"--rogue-only \"{dll}\""));
            var exited = await process.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var log = process.LogText;
            var exitCode = process.ExitCode;
            File.WriteAllText(Path.Combine(Program.OutDir, $"rogue-mode-{result.Mode}.txt"), $"exit 0x{exitCode:X} exited={exited}\n{log}");
            var codeRan = log.Contains("ROGUE-CODE-RAN", StringComparison.Ordinal);
            var row = Parse(log, exitCode).Rows.FirstOrDefault(r => r.Name == "dll.load.rogue-marker");
            var rowDenied = row is { Outcome: "DENIED" };
            var died = exited && row is null;
            var events = CodeIntegrityEvents(started, Path.GetFileName(dll));
            File.WriteAllText(Path.Combine(Program.OutDir, $"rogue-events-{result.Mode}.txt"), events);
            result.RogueDllDenied = markerControl && !codeRan && (rowDenied || died);
            result.RogueDetail = $"row={(row is null ? "(none: the process ended first)" : row.Outcome + " Win32 " + row.Win32)}, process exited={exited} exit code 0x{exitCode:X}, DLL code ran (marker)={codeRan}, marker mechanism proven by the control={markerControl}, Code Integrity events naming the DLL: {(events.Contains("Event ID: 3033", StringComparison.Ordinal) || events.Contains("Event ID: 3077", StringComparison.Ordinal) || events.Contains("Event ID: 3034", StringComparison.Ordinal) ? "yes (see rogue-events-" + result.Mode + ".txt)" : "none read")}";
            if (!result.RogueDllDenied)
            {
                result.Problems.Add("the unsigned DLL was not shown to be blocked: " + result.RogueDetail);
            }
        }
        catch (Exception e)
        {
            result.RogueDetail = "launch failed: " + e.Message;
            result.Problems.Add("rogue-DLL process: " + e.Message);
        }
    }

    private static string CodeIntegrityEvents(DateTime since, string dllName)
    {
        // wevtutil is part of Windows; reading this log needs an administrator (the hosted runner is one; a medium-integrity run says so instead).
        var text = Program.RunTool("wevtutil.exe", "qe Microsoft-Windows-CodeIntegrity/Operational /rd:true /c:40 /f:text");
        var lines = text.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Where(e => e.Contains(dllName, StringComparison.OrdinalIgnoreCase)).Select(e => "Event[" + e.Trim()).ToList();
        _ = since;
        return lines.Count == 0 ? (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ? "(the Code Integrity log is not readable at this integrity level)" : string.Empty) : string.Join("\n", lines);
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

    private static void Assess(ModeResult result, ProbeReport report, ProbeReport control)
    {
        bool Is(string key, string value) => report.Info.TryGetValue(key, out var v) && v == value;
        if (!Is("isAppContainer", "1"))
        {
            result.Problems.Add("not an AppContainer: " + report.Info.GetValueOrDefault("isAppContainer"));
        }

        if (!Is("integrityRid", "0x1000"))
        {
            result.Problems.Add("integrity " + report.Info.GetValueOrDefault("integrityRid"));
        }

        if (!Is("runtimeSignaturePolicy", "ok"))
        {
            result.Problems.Add("runtime signature policy " + report.Info.GetValueOrDefault("runtimeSignaturePolicy"));
        }

        result.Capabilities = $"{report.Info.GetValueOrDefault("capabilityCount")} [{report.Info.GetValueOrDefault("capabilitySids")}]";
        result.RowsRun = report.Rows.Count;

        // Everything except the package-data rows and the separately reported rows must be as expected.
        var main = report.Rows.Where(r => !KnownPackageDataRows.Contains(r.Name) && !SeparatelyReported.Contains(r.Name)).ToList();
        foreach (var row in main.Where(r => r.Expect == "deny" && r.Outcome != "DENIED"))
        {
            result.Problems.Add($"{row.Name} was {row.Outcome} (Win32 {row.Win32}): {row.Detail}");
        }

        foreach (var row in main.Where(r => r.Expect == "allow" && r.Outcome != "ALLOWED"))
        {
            result.Problems.Add($"positive control {row.Name} failed (Win32 {row.Win32}): {row.Detail}");
        }

        var notDiscriminating = new HashSet<string> { "fs.read.unc", "ui.clipboard.read", "fs.rename.engine-dir" };
        foreach (var row in control.Rows.Where(r => r.Expect == "deny" && !notDiscriminating.Contains(r.Name) && r.Outcome != "ALLOWED"))
        {
            result.Problems.Add($"control: {row.Name} was {row.Outcome} outside the sandbox, so its denial proves nothing");
        }

        if (main.Count(r => r.Expect == "deny") < 14)
        {
            result.Problems.Add($"only {main.Count(r => r.Expect == "deny")} deny rows ran");
        }

        result.OriginalSuiteDenied = report.Done && result.Problems.Count == 0;

        bool Denied(string name) => report.Rows.Where(r => r.Name == name).Any() && report.Rows.Where(r => r.Name == name).All(r => r.Outcome == "DENIED");
        result.OtherPackageDenied = Denied("fs.read.other-package-file") || report.Rows.All(r => r.Name != "fs.read.other-package-file");
        result.EngineDirWriteDenied = Denied("fs.write.engine-dir");
        result.EngineDirRenameDenied = Denied("fs.rename.engine-dir");
        result.ProtectedFileReadDenied = Denied("fs.read.protected-private-file");
        result.ProtectedDirRenameDenied = Denied("fs.rename.protected-private-dir");
    }

    private string Arguments(string engineDir, string tempDll, bool control, string renameTarget)
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
            "--engine-dir", Quote(engineDir),
            "--own-data-file", Quote(ownDataFile),
            "--own-data-dir", Quote(Path.GetDirectoryName(ownDataFile)!),
            "--protected-file", Quote(protectedFile),
            "--rename-protected-dir", Quote(protectedDir),
            "--rename-engine-dir", Quote(renameTarget),
        };
        if (otherPackageFile.Length > 0)
        {
            parts.AddRange(["--other-package-file", Quote(otherPackageFile)]);
        }

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
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The integrity level RID of this process's token (0x2000 medium, 0x3000 high).</summary>
    public static int IntegrityRid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x8, out var token))
        {
            return -1;
        }

        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var needed); // TokenIntegrityLevel
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, needed, out _))
                {
                    return -1;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static string IntegrityName(int rid) => rid switch
    {
        0x1000 => "low",
        0x2000 => "medium",
        0x3000 => "high",
        0x4000 => "system",
        _ => "unknown",
    };
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
