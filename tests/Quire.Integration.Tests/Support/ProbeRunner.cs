using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Quire.Broker;

namespace Quire.Integration.Tests.Support;

internal sealed record ProbeRow(string Name, string Expect, string Outcome, int Win32, string Detail);

internal sealed record ProbeReport(Dictionary<string, string> Info, List<ProbeRow> Rows, bool Done, string Raw, uint ExitCode)
{
    public bool Flag(string key, int bit) => Info.TryGetValue(key, out var hex) && hex.StartsWith("0x", StringComparison.Ordinal)
        && (uint.Parse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & (1u << bit)) != 0;
}

/// <summary>
/// Runs the test-only sandbox probe twice with the same arguments: unsandboxed (a control, to prove each check can succeed) and
/// through the same <see cref="SandboxedProcessLauncher"/> profile as the engine.
/// </summary>
internal sealed class ProbeRunner : IDisposable
{
    private readonly SandboxFixture _sandbox;
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly string _scratch;
    private readonly string _tempDll;

    public ProbeRunner(SandboxFixture sandbox)
    {
        _sandbox = sandbox;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _scratch = Path.Combine(sandbox.RepositoryRoot, "build", "out", "probe-scratch");
        Directory.CreateDirectory(_scratch);
        _tempDll = Path.Combine(Path.GetTempPath(), "quire-probe-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        File.Copy(Path.Combine(sandbox.DeployDirectory, "rogue-copy.dll"), _tempDll);
    }

    public int LoopbackPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? LanEndpoint
    {
        get
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return address is null ? null : address + ":" + LoopbackPort.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task<ProbeReport> RunControlAsync()
    {
        var start = new ProcessStartInfo(_sandbox.ProbePath, Arguments(control: true))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = _sandbox.DeployDirectory,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return Parse(output, (uint)process.ExitCode);
    }

    public async Task<ProbeReport> RunSandboxedAsync(SandboxPolicy? policy = null)
    {
        var launcher = new SandboxedProcessLauncher(_sandbox.Profile, policy);
        using var process = launcher.Launch(new SandboxLaunchRequest(_sandbox.ProbePath, Arguments(control: false)));
        Assert.True(await process.WaitForExitAsync(TimeSpan.FromSeconds(60)), "the probe did not finish");
        return Parse(process.LogText, process.ExitCode);
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            File.Delete(_tempDll);
        }
        catch (IOException)
        {
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private string Arguments(bool control)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var parts = new List<string>
        {
            "--documents", Quote(documents),
            "--temp", Quote(Path.GetTempPath().TrimEnd('\\')),
            "--repo-dir", Quote(_scratch),
            "--repo-file", Quote(Path.Combine(_sandbox.RepositoryRoot, "Quire.slnx")),
            "--unc", Quote(@"\\localhost\c$"),
            "--tcp-loopback", LoopbackPort.ToString(CultureInfo.InvariantCulture),
            "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--temp-dll", Quote(_tempDll),
            "--rogue-dll", Quote(Path.Combine(_sandbox.DeployDirectory, "rogue-copy.dll")),
            "--engine-dir", Quote(_sandbox.DeployDirectory),
        };
        if (LanEndpoint is { } lan)
        {
            parts.AddRange(["--tcp-lan", lan]);
        }

        if (control)
        {
            parts.Add("--control");
        }

        return string.Join(' ', parts);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
        {
        }
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
