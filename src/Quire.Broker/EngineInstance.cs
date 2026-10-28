using System.Globalization;
using Microsoft.Win32.SafeHandles;
using Quire.Protocol;
namespace Quire.Broker;

public enum EngineKind
{
    /// <summary>One engine per open document.</summary>
    Document,

    /// <summary>The shared engine for thumbnails and search.</summary>
    Pool,
}

public sealed class EngineStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One running engine: its sandboxed process, job, pipe and client. Owned by the supervisor.</summary>
internal sealed class EngineInstance : IAsyncDisposable
{
    private readonly EnginePipeServer _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _disposed;

    private EngineInstance(SandboxedProcess process, EnginePipeServer pipe, EngineClient client, Quire.Protocol.HelloReply hello)
    {
        _lifetimeToken = _lifetime.Token;
        Process = process;
        _pipe = pipe;
        Client = client;
        Hello = hello;
    }

    /// <summary>What the engine said in its Hello.</summary>
    public Quire.Protocol.HelloReply Hello { get; }

    public SandboxedProcess Process { get; }

    /// <summary>Every kernel handle the broker holds for this engine, for tests that check none is inheritable.</summary>
    public IReadOnlyList<nint> BrokerHandles => [.. Process.BrokerHandles, _pipe.NativeHandle];

    public EngineClient Client { get; }

    /// <summary>Cancelled when the instance is retired; the watchdog stops with it.</summary>
    public CancellationToken Lifetime => _lifetimeToken;

    /// <summary>Set before a deliberate shutdown, so the exit that follows is not mistaken for a crash.</summary>
    public bool Retiring { get; private set; }

    public static async Task<EngineInstance> StartAsync(
        EngineSupervisorOptions options,
        SandboxedProcessLauncher launcher,
        SlotRing? ring,
        EngineClientOptions clientOptions,
        CancellationToken cancellationToken)
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
                $"--pipe {pipe.Name} --parent {Environment.ProcessId}{(isEngine ? $" --kind {(options.Kind == EngineKind.Pool ? "pool" : "document")}" : string.Empty)}");

            // Suspended and with nothing inherited (R1): hand over the slot ring by duplication, then let the process run.
            process = launcher.Launch(new SandboxLaunchRequest(options.EnginePath, arguments) { StartSuspended = true });
            var ringHandle = isEngine ? ring!.DuplicateInto(process) : 0UL;
            process.Resume();

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(options.StartupTimeout);
            accept = pipe.AcceptAsync(process.ProcessId, startup.Token);
            var finished = await Task.WhenAny(accept, process.Exited).ConfigureAwait(false);
            if (finished != accept)
            {
                throw new EngineStartException($"The engine exited with code 0x{process.ExitCode:X} before it connected. Log: {process.LogText}");
            }

            var stream = await accept.ConfigureAwait(false);
            client = new EngineClient(stream, ring, clientOptions);
            var hello = await client.HelloAsync(options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (!hello.IsOk)
            {
                throw new EngineStartException($"The engine's Hello failed with status {hello.Status} ({hello.Detail}). Log: {process.LogText}");
            }

            if (isEngine)
            {
                // The engine refuses RenderTile until it has been told where the ring is.
                var attached = await client.AttachSlotRingAsync(ringHandle, ring!.SlotCount, ring.SlotSize, options.StartupTimeout, cancellationToken).ConfigureAwait(false);
                if (!attached.IsOk)
                {
                    throw new EngineStartException($"The engine did not accept the slot ring (status {attached.Status}). Log: {process.LogText}");
                }
            }

            return new EngineInstance(process, pipe, client, hello.Value!);
        }
        catch (Exception e) when (e is not EngineStartException)
        {
            Cleanup();
            throw new EngineStartException("Starting the engine failed: " + e.Message, e);
        }
        catch
        {
            Cleanup();
            throw;
        }

        void Cleanup()
        {
            process?.Kill();
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
