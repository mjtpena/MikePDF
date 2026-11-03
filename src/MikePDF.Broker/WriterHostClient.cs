using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;

namespace MikePDF.Broker;

/// <summary>
/// The broker's door to <c>MikePDF.WriterHost</c> (ADR-0008): the sandboxed process that parses untrusted PDF structure so the broker never does.
/// It is the same machinery as the engine: the same launcher and AppContainer profile, the same <see cref="EngineClient"/> (so every reply is
/// validated), the same watchdog and restart rules. What differs is exactly what ADR-0004 allows: no prohibit-dynamic-code, because the JIT needs it.
/// </summary>
public sealed class WriterHostClient : IAsyncDisposable
{
    private readonly EngineSupervisor _supervisor;
    private readonly TimeSpan _defaultDeadline;

    private WriterHostClient(EngineSupervisor supervisor, TimeSpan defaultDeadline)
    {
        _supervisor = supervisor;
        _defaultDeadline = defaultDeadline;
    }

    /// <summary>Raised when the host died, hung or was killed. The next call runs in a fresh one.</summary>
    public event EventHandler<EngineCrashedEventArgs>? HostCrashed
    {
        add => _supervisor.EngineCrashed += value;
        remove => _supervisor.EngineCrashed -= value;
    }

    /// <summary>A new host is up.</summary>
    public event EventHandler<EngineRestoredEventArgs>? Restored
    {
        add => _supervisor.Restored += value;
        remove => _supervisor.Restored -= value;
    }

    public event EventHandler<EngineCrashedEventArgs>? Failed
    {
        add => _supervisor.Failed += value;
        remove => _supervisor.Failed -= value;
    }

    public int RestartCount => _supervisor.RestartCount;

    public uint ProcessId => _supervisor.EngineProcessId;

    internal EngineSupervisor Supervisor => _supervisor;

    /// <param name="options">
    /// <c>EnginePath</c> is <c>MikePDF.WriterHost.exe</c>. <c>Peer</c> and the dynamic-code policy are set here, whatever the caller passed.
    /// </param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<WriterHostClient> StartAsync(EngineSupervisorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hostOptions = options with { Peer = ProcessKind.WriterHost, Policy = options.Policy with { ProhibitDynamicCode = false } };
        var supervisor = await EngineSupervisor.StartAsync(hostOptions, cancellationToken).ConfigureAwait(false);
        return new WriterHostClient(supervisor, hostOptions.DefaultRequestDeadline);
    }

    /// <summary>
    /// Validates the structure of a PDF in the host. The broker opens the file read-only and deny-write, duplicates the handle into the host, and
    /// never reads a byte of it. A file that is not a valid PDF is an <c>Ok</c> reply with issues, not an error; an error status means the host could not
    /// do the job (and, for <c>EngineCrashed</c>, was replaced; the caller may retry).
    /// </summary>
    public async Task<EngineResult<ValidateStructureReply>> ValidateStructureAsync(string path, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
        {
            return EngineResult<ValidateStructureReply>.Fail(Status.InvalidArgument, e.Message);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return EngineResult<ValidateStructureReply>.Fail(Status.PermissionDenied, e.Message);
        }

        using (file)
        {
            var instance = (await _supervisor.AcquireAsync(null, cancellationToken).ConfigureAwait(false)).Instance;
            if (instance is null)
            {
                return EngineResult<ValidateStructureReply>.Fail(Status.EngineCrashed, "No writer host is available.");
            }

            ulong remote;
            try
            {
                remote = instance.DuplicateIntoEngine(file);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return EngineResult<ValidateStructureReply>.Fail(Status.EngineCrashed, e.Message);
            }

            return await instance.Client.ValidateStructureAsync(remote, deadline ?? _defaultDeadline, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stage 4 of a save: the host appends the annotation edits in <paramref name="edits"/> to <paramref name="baseFile"/> and writes the result to <paramref name="output"/> (an empty file the caller made). The handles stay the caller's; the host gets duplicates.</summary>
    public Task<EngineResult<AppendAnnotationsReply>> AppendAnnotationsAsync(SafeFileHandle baseFile, SafeFileHandle edits, long editsLength, SafeFileHandle output, int pageCount, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        WithHandlesAsync<AppendAnnotationsReply>([baseFile, edits, output], (client, remote) => client.AppendAnnotationsAsync(remote[0], remote[1], (ulong)editsLength, remote[2], (uint)pageCount, deadline, cancellationToken), cancellationToken);

    /// <summary>A full rewrite of <paramref name="baseFile"/> into <paramref name="output"/>; flags 1 flatten form fields, 2 flatten comments.</summary>
    public Task<EngineResult<RewriteDocumentReply>> RewriteDocumentAsync(SafeFileHandle baseFile, SafeFileHandle output, uint flags, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        WithHandlesAsync<RewriteDocumentReply>([baseFile, output], (client, remote) => client.RewriteDocumentAsync(remote[0], remote[1], flags, deadline, cancellationToken), cancellationToken);

    /// <summary>Stage 5: an independent strict parse of the base and the output, comparing annotation counts of the named pages with the model's.</summary>
    public Task<EngineResult<VerifyAnnotationsReply>> VerifyAnnotationsAsync(SafeFileHandle baseFile, SafeFileHandle output, int pageCount, uint[] pages, uint[] added, uint[] removed, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        WithHandlesAsync<VerifyAnnotationsReply>([baseFile, output], (client, remote) => client.VerifyAnnotationsAsync(remote[0], remote[1], (uint)pageCount, pages, added, removed, deadline, cancellationToken), cancellationToken);

    /// <summary>Validates the structure of a file that is already open (the host gets a duplicate of the handle).</summary>
    public Task<EngineResult<ValidateStructureReply>> ValidateStructureAsync(SafeFileHandle file, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        WithHandlesAsync<ValidateStructureReply>([file], (client, remote) => client.ValidateStructureAsync(remote[0], deadline, cancellationToken), cancellationToken);

    private async Task<EngineResult<T>> WithHandlesAsync<T>(SafeFileHandle[] handles, Func<EngineClient, ulong[], Task<EngineResult<T>>> call, CancellationToken cancellationToken)
        where T : class
    {
        var instance = (await _supervisor.AcquireAsync(null, cancellationToken).ConfigureAwait(false)).Instance;
        if (instance is null)
        {
            return EngineResult<T>.Fail(Status.EngineCrashed, "No writer host is available.");
        }

        ulong[] remote;
        try
        {
            remote = [.. handles.Select(instance.DuplicateIntoEngine)];
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return EngineResult<T>.Fail(Status.EngineCrashed, e.Message);
        }

        return await call(instance.Client, remote).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _supervisor.DisposeAsync();
}
