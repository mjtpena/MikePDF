using System.IO.Pipes;
using MikePDF.Core.Settings;

namespace MikePDF.ShellHost;

internal sealed class ShellHostOptions
{
    /// <summary>The user's SID: the pipes are per user and the DACL names it.</summary>
    public required string UserSid { get; init; }

    /// <summary>Where the bytes of the file being opened are staged for the engine. Must be a directory only the user and SYSTEM can open (PrivateDirectory).</summary>
    public required string StagingDirectory { get; init; }

    public required IShellEngineFactory Engines { get; init; }

    /// <summary>The names of the two pipes. The default is <see cref="ShellPipe.NameFor"/>; a test gives its own so that runs never meet.</summary>
    public Func<ShellFeature, string>? PipeName { get; init; }

    /// <summary>Read on every request, so a change of setting or policy applies without restarting ShellHost (SHELL-16).</summary>
    public Func<ShellSwitches> Switches { get; init; } = () => ShellSwitches.Defaults;

    /// <summary>ShellHost leaves this long after its last request, if no connection is open (ADR-0018 decision 6).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A client that is not in this process's package family is closed (SHELL-17). Off only for unpackaged test runs, where nothing has a family.</summary>
    public bool RequireClientPackageFamily { get; init; } = true;

    /// <summary>The clock of the negative cache (its ten minutes). Deadlines and the idle timer always use the real clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Action<string>? Log { get; init; }

    /// <summary>A connection that has not said Hello within this time is dropped.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string PipeNameOf(ShellFeature feature) => PipeName?.Invoke(feature) ?? ShellPipe.NameFor(UserSid, feature);
}

/// <summary>
/// ShellHost's server: two per-user pipes (one for the thumbnail provider, one for the preview handler), one connection per handler instance, the engines, the negative
/// cache, and the idle timer. A connection is a session: closing it ends the session, kills the engine and removes everything staged for it, so a handler that crashed
/// needs no cleanup protocol.
/// </summary>
internal sealed class ShellHostServer : IAsyncDisposable
{
    private readonly ShellHostOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly object _gate = new();
    private readonly string _sddl;
    private long _lastActivity;
    private int _active;
    private uint _sessionIds;
    private Task? _run;

    public ShellHostServer(ShellHostOptions options)
    {
        _options = options;
        _sddl = ShellPipe.Sddl(options.UserSid);
        Cache = new NegativeCache(options.Time);
        Pool = new ShellEnginePool(options.Engines, () => _options.Switches().MaxEngines);
        _lastActivity = TimeProvider.System.GetTimestamp();
    }

    public NegativeCache Cache { get; }

    public ShellEnginePool Pool { get; }

    /// <summary>Connections open now.</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    internal ShellHostOptions Options => _options;

    internal uint NextSessionId() => Interlocked.Increment(ref _sessionIds);

    internal void Touch() => Interlocked.Exchange(ref _lastActivity, TimeProvider.System.GetTimestamp());

    internal void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>
    /// Creates both pipes (the first instances: a process that already owns either name makes this throw, and ShellHost does not serve), clears what an earlier run staged,
    /// starts the spare engine, and then serves until <see cref="StopAsync"/>, the cancellation token or the idle timeout. Completes when it has stopped.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.StagingDirectory);
        foreach (var leftover in Directory.EnumerateFiles(_options.StagingDirectory, "*.pdf"))
        {
            TryDelete(leftover);
        }

        var thumbnails = ShellPipe.Create(_options.PipeNameOf(ShellFeature.Thumbnails), _sddl, first: true);
        NamedPipeServerStream preview;
        try
        {
            preview = ShellPipe.Create(_options.PipeNameOf(ShellFeature.Preview), _sddl, first: true);
        }
        catch
        {
            thumbnails.Dispose();
            throw;
        }

        Pool.Prewarm();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        _run = Task.WhenAll(AcceptLoopAsync(thumbnails, ShellFeature.Thumbnails, linked.Token), AcceptLoopAsync(preview, ShellFeature.Preview, linked.Token), IdleWatchAsync(linked));
        try
        {
            await _run.ConfigureAwait(false);
        }
        finally
        {
            // However this ends (idle, a stop, a failure), no engine of ours is left running: the spare goes now, not when the process is torn down.
            await Pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // That is how it stops.
            }
        }

        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task IdleWatchAsync(CancellationTokenSource stop)
    {
        var check = TimeSpan.FromMilliseconds(Math.Clamp(_options.IdleTimeout.TotalMilliseconds / 4, 20, 1000));
        using var timer = new PeriodicTimer(check);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                var idle = TimeProvider.System.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                if (Volatile.Read(ref _active) == 0 && idle >= _options.IdleTimeout)
                {
                    Log("idle; leaving");
                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by someone else.
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe, ShellFeature feature, CancellationToken stop)
    {
        var name = _options.PipeNameOf(feature);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    return;
                }
                catch (IOException)
                {
                    pipe.Dispose();
                    pipe = ShellPipe.Create(name, _sddl, first: false);
                    continue;
                }

                var connected = pipe;
                // The next instance exists before this connection is served, so a client never finds the pipe busy for longer than it takes to call this.
                pipe = ShellPipe.Create(name, _sddl, first: false);
                Touch();
                Interlocked.Increment(ref _active);
                var connection = new ShellConnection(this, connected, feature, stop);
                var served = Task.Run(async () =>
                {
                    try
                    {
                        await connection.ServeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Touch();
                        Interlocked.Decrement(ref _active);
                    }
                }, CancellationToken.None);
                lock (_gate)
                {
                    _connections.RemoveAll(static t => t.IsCompleted);
                    _connections.Add(served);
                }
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be deleted now is deleted by the next run.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Pool.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
