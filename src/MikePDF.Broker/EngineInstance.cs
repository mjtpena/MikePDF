using System.Globalization;
using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;
namespace MikePDF.Broker;

public enum EngineKind
{
    /// <summary>One engine per open document.</summary>
    Document,

    /// <summary>The shared engine for thumbnails and search.</summary>
    Pool,

    /// <summary>ShellHost's engine for one Explorer thumbnail or preview (ADR-0018): one document, and only the messages a shell host needs; everything else is answered <c>Unsupported</c>.</summary>
    Shell,
}

/// <summary>
/// An engine could not be brought up. <see cref="IsTransient"/> says the engine process was lost during its start-up (it exited, or closed its
/// pipe, before the handshake was over), which a second attempt may get past; every other cause (no sandbox, a missing executable, another schema
/// version, a start-up that merely took too long) would fail again the same way and is not retried.
/// </summary>
public sealed class EngineStartException(string message, Exception? inner = null, bool isTransient = false) : Exception(message, inner)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>One running engine: its sandboxed process, job, pipe and client. Owned by the supervisor.</summary>
internal sealed class EngineInstance : IAsyncDisposable
{
    private readonly EnginePipeServer _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _disposed;

    private EngineInstance(SandboxedProcess process, EnginePipeServer pipe, EngineClient client, MikePDF.Protocol.HelloReply hello)
    {
        _lifetimeToken = _lifetime.Token;
        Process = process;
        _pipe = pipe;
        Client = client;
        Hello = hello;
    }

    /// <summary>What the engine said in its Hello.</summary>
    public MikePDF.Protocol.HelloReply Hello { get; }

    public SandboxedProcess Process { get; }

    /// <summary>Every kernel handle the broker holds for this engine, for tests that check none is inheritable.</summary>
    public IReadOnlyList<nint> BrokerHandles => [.. Process.BrokerHandles, _pipe.NativeHandle];

    public EngineClient Client { get; }

    /// <summary>Cancelled when the instance is retired; the watchdog stops with it.</summary>
    public CancellationToken Lifetime => _lifetimeToken;

    /// <summary>Set before a deliberate shutdown, so the exit that follows is not mistaken for a crash.</summary>
    public bool Retiring { get; private set; }

    private static string KindArgument(EngineKind kind) => kind switch { EngineKind.Pool => "pool", EngineKind.Shell => "shell", _ => "document" };

    public static async Task<EngineInstance> StartAsync(
        EngineSupervisorOptions options,
        SandboxedProcessLauncher launcher,
        SlotRing? ring,
        EngineClientOptions clientOptions,
        CancellationToken cancellationToken,
        int attempt = 1)
    {
        EnginePipeServer? pipe = null;
        SandboxedProcess? process = null;
        EngineClient? client = null;
        Task<System.IO.Pipes.NamedPipeServerStream>? accept = null;
        try
        {
            pipe = EnginePipeServer.Create(options.Profile.Sid);
            var isEngine = options.Peer == ProcessKind.Engine;
            var arguments = string.Create(
                CultureInfo.InvariantCulture,
                $"--pipe {pipe.Name} --parent {Environment.ProcessId}{(isEngine ? $" --kind {KindArgument(options.Kind)}" : string.Empty)}");

            // Suspended and with nothing inherited (R1): hand over the slot ring by duplication, then let the process run.
            process = launcher.Launch(new SandboxLaunchRequest(options.EnginePath, arguments) { StartSuspended = true });
            var ringHandle = isEngine ? ring!.DuplicateInto(process) : 0UL;
            options.AfterLaunch?.Invoke(process, attempt);
            process.Resume();

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(options.StartupTimeout);
            accept = pipe.AcceptAsync(process.ProcessId, startup.Token);
            var finished = await Task.WhenAny(accept, process.Exited).ConfigureAwait(false);
            if (finished != accept)
            {
                throw new EngineStartException($"The engine exited with code 0x{process.ExitCode:X} before it connected. Log: {process.LogText}", isTransient: true);
            }

            var stream = await accept.ConfigureAwait(false);
            client = new EngineClient(stream, ring, clientOptions);
            var hello = await client.HelloAsync(options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (!hello.IsOk)
            {
                throw await LostDuringHandshakeAsync(process, hello.Status, $"The engine's Hello failed with status {hello.Status} ({hello.Detail})").ConfigureAwait(false);
            }

            if (isEngine)
            {
                // The engine refuses RenderTile until it has been told where the ring is.
                var attached = await client.AttachSlotRingAsync(ringHandle, ring!.SlotCount, ring.SlotSize, options.StartupTimeout, cancellationToken).ConfigureAwait(false);
                if (!attached.IsOk)
                {
                    throw await LostDuringHandshakeAsync(process, attached.Status, $"The engine did not accept the slot ring (status {attached.Status})").ConfigureAwait(false);
                }
            }

            return new EngineInstance(process, pipe, client, hello.Value!);
        }
        catch (System.ComponentModel.Win32Exception e) when (process is not null)
        {
            // The process exists but a step of setting it up failed (duplicating the ring into it, resuming it): it was lost while it was being made.
            var fate = Fate(process); // before Cleanup, which kills and closes the process
            await CleanupAsync().ConfigureAwait(false);
            throw new EngineStartException($"Starting the engine failed: {e.Message}; the process {fate}.", e, isTransient: true);
        }
        catch (Exception e) when (e is not EngineStartException)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new EngineStartException("Starting the engine failed: " + e.Message, e);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }

        // A handshake that ends with EngineCrashed means the pipe broke under it: the process died (or was killed) while it started. The exit code and
        // what the process logged say how; wait a moment for the exit to be visible, since the pipe breaks a little before the process is gone.
        static async Task<EngineStartException> LostDuringHandshakeAsync(SandboxedProcess process, Status status, string what)
        {
            if (status != Status.EngineCrashed)
            {
                return new EngineStartException($"{what}. Log: {process.LogText}");
            }

            await process.WaitForExitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            return new EngineStartException($"{what}; the engine {Fate(process)}. Log: {process.LogText}", isTransient: true);
        }

        // GetExitCodeProcess answers 259 (STILL_ACTIVE) while the process runs.
        static string Fate(SandboxedProcess process) => process.ExitCode is 259 ? "was still running" : $"had exited with code 0x{process.ExitCode:X}";

        // A process that failed to start is killed and then waited for: whoever starts the next engine (the supervisor's retry, ShellHost's cap on engine processes) must not
        // find this one still running.
        async Task CleanupAsync()
        {
            if (process is not null)
            {
                process.Kill();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            pipe?.Dispose();
            _ = accept?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (client is not null)
            {
                _ = client.DisposeAsync().AsTask();
            }

            process?.Dispose();
        }
    }

    /// <summary>Duplicates a file handle into the engine process. Returns the value in the engine's handle table.</summary>
    public ulong DuplicateIntoEngine(SafeFileHandle file) => Process.DuplicateFileInto(file);

    /// <summary>Asks the engine to shut down, then kills it if it does not leave in time.</summary>
    public async Task ShutdownAsync(TimeSpan grace)
    {
        Retiring = true;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (!Client.IsFaulted && !Process.HasExited)
        {
            await Client.ShutdownAsync(grace).ConfigureAwait(false);
        }

        if (!await Process.WaitForExitAsync(grace).ConfigureAwait(false))
        {
            Process.Kill();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Retiring = true;
        _lifetime.Cancel();
        Process.Kill();
        await Client.DisposeAsync().ConfigureAwait(false);
        _pipe.Dispose();
        await Process.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Process.Dispose();
        _lifetime.Dispose();
    }
}
