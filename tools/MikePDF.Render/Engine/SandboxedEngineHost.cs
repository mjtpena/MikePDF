using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MikePDF.Broker;
using MikePDF.Render.Reporting;

namespace MikePDF.Render.Engine;

/// <summary>
/// The sandbox for a run: an AppContainer profile, a private copy of the engine that the profile may read (unpackaged runs, ADR-0004 A2), and an
/// <see cref="EngineSupervisor"/> over the real <c>MikePDF.Engine.exe</c>. The supervisor restarts a dead or hung engine by itself; when it gives up
/// (a crash loop) the next call here starts a fresh supervisor, so one hostile file cannot end the run. Everything the supervisor reports is
/// queued as <see cref="EngineEvent"/>s with the context the caller named, for the report.
/// </summary>
internal sealed class SandboxedEngineHost : IAsyncDisposable
{
    /// <summary>One profile for the tool. It is not deleted afterwards: it costs nothing, and two runs at once must not remove each other's.</summary>
    public const string ProfileName = "MikePDF.Render";

    private readonly string _deployDirectory;
    private readonly AppContainerProfile _profile;
    private readonly TimeSpan _openDeadline;
    private readonly Stopwatch _clock;
    private readonly ConcurrentQueue<EngineEvent> _events = new();
    private EngineSupervisor? _supervisor;
    private volatile string? _context;
    private int _generation;

    private SandboxedEngineHost(string deployDirectory, AppContainerProfile profile, TimeSpan openDeadline, Stopwatch clock)
    {
        _deployDirectory = deployDirectory;
        _profile = profile;
        _openDeadline = openDeadline;
        _clock = clock;
    }

    /// <summary>The engine processes started so far (a fresh supervisor counts as one).</summary>
    public int SupervisorsStarted => Volatile.Read(ref _generation);

    public string EnginePath => Path.Combine(_deployDirectory, "MikePDF.Engine.exe");

    /// <summary>Deploys the engine into a private directory the AppContainer can read. Nothing is started yet.</summary>
    public static SandboxedEngineHost Create(string engineDirectory, string workDirectory, TimeSpan openDeadline, Stopwatch clock, string profileName = ProfileName)
    {
        var profile = AppContainerProfile.CreateOrOpen(profileName, "MikePDF render CLI", "Zero-capability AppContainer for the mikepdf-render tool");
        var parent = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(parent);
        SweepStaleDeployments(parent, profile);
        var deploy = Path.Combine(parent, "engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(deploy);
        EngineDeployment.EnsureAppContainerAccess(deploy, profile); // before the copy, so the files inherit it
        foreach (var file in new[] { "MikePDF.Engine.exe", "pdfium.dll" })
        {
            File.Copy(Path.Combine(engineDirectory, file), Path.Combine(deploy, file));
        }

        return new SandboxedEngineHost(deploy, profile, openDeadline, clock);
    }

    /// <summary>What the run says it is doing now (see <see cref="SetContext"/>).</summary>
    public string? Context => _context;

    /// <summary>Names what the run is doing, so events the engine raises on its own thread can be attributed.</summary>
    public void SetContext(string? context) => _context = context;

    /// <summary>Takes the events that arrived since the last call.</summary>
    public List<EngineEvent> DrainEvents()
    {
        var list = new List<EngineEvent>();
        while (_events.TryDequeue(out var engineEvent))
        {
            list.Add(engineEvent);
        }

        return list;
    }

    /// <summary>The live supervisor, or a new one when there is none or it gave up. Throws <see cref="EngineStartException"/> if no engine can be started.</summary>
    public async Task<EngineSupervisor> GetSupervisorAsync(CancellationToken cancellationToken)
    {
        if (_supervisor is { IsFailed: false } alive)
        {
            return alive;
        }

        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            _supervisor = null;
        }

        var supervisor = await EngineSupervisor.StartAsync(
            new EngineSupervisorOptions
            {
                EnginePath = EnginePath,
                Profile = _profile,
                SlotCount = 8,
                SlotSize = 4 << 20, // a 1024 x 1024 BGRA tile, the largest the protocol allows
                DefaultRequestDeadline = _openDeadline,
            },
            cancellationToken).ConfigureAwait(false);
        supervisor.EngineCrashed += (_, e) => Record(new EngineEvent
        {
            Kind = "crashed",
            Reason = e.Reason.ToString(),
            ExitCode = string.Create(CultureInfo.InvariantCulture, $"0x{e.ExitCode:X}"),
            Detail = e.Detail,
            RestartNumber = e.RestartNumber,
        });
        supervisor.Restored += (_, e) => Record(new EngineEvent
        {
            Kind = "restored",
            RestartNumber = e.RestartNumber,
            RestoreMs = (long)e.Elapsed.TotalMilliseconds,
            DocumentsRestored = e.DocumentsRestored,
            DocumentsLost = e.DocumentsLost,
        });
        supervisor.Failed += (_, e) => Record(new EngineEvent
        {
            Kind = "failed",
            Reason = e.Reason.ToString(),
            ExitCode = string.Create(CultureInfo.InvariantCulture, $"0x{e.ExitCode:X}"),
            Detail = e.Detail,
            RestartNumber = e.RestartNumber,
        });
        Interlocked.Increment(ref _generation);
        _supervisor = supervisor;
        return supervisor;
    }

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            _supervisor = null;
        }

        TryDo(() => EngineDeployment.RevokeAppContainerAccess(_deployDirectory, _profile.Sid));
        TryDo(() => Directory.Delete(_deployDirectory, recursive: true));
    }

    private void Record(EngineEvent engineEvent) =>
        _events.Enqueue(engineEvent with { AtMs = _clock.ElapsedMilliseconds, During = _context });

    /// <summary>An earlier run that was killed leaves its deployment (and an ACE for the profile) behind; take away the ones that are hours old, never a run that may still be starting.</summary>
    private static void SweepStaleDeployments(string parent, AppContainerProfile profile)
    {
        foreach (var directory in Directory.GetDirectories(parent, "engine-*").Where(d => DateTime.UtcNow - Directory.GetCreationTimeUtc(d) > TimeSpan.FromHours(2)))
        {
            TryDo(() => EngineDeployment.RevokeAppContainerAccess(directory, profile.Sid));
            TryDo(() => Directory.Delete(directory, recursive: true)); // fails, harmlessly, while another run still uses it
        }
    }

    private static void TryDo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best effort: the next run sweeps what is left.
        }
    }
}
