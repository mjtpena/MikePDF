using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;
using MikePDF.Protocol.Edits;

namespace MikePDF.Broker.Saving;

/// <summary>
/// The writer host behind <see cref="ISaveHost"/>: every call hands the host duplicates of file handles and nothing else (ADR-0008 decision 2), and
/// every reply has already been through the generated decoder and <see cref="ReplyChecks"/>. A refusal with a reason is an <c>Ok</c> reply with
/// <c>ok</c> false; a status other than Ok is a host that could not do the job.
/// </summary>
public sealed class WriterHostSaveHost(WriterHostClient client) : ISaveHost
{
    public async Task<HostResult> AppendAsync(SafeFileHandle baseFile, ReadOnlyMemory<byte> blob, SafeFileHandle output, int pageCount, TimeSpan deadline, CancellationToken cancellationToken)
    {
        // The blob goes in a file only this process and the host can open, deleted when the last handle closes (also if the process dies).
        using var edits = File.OpenHandle(Path.Combine(Path.GetTempPath(), "mikepdf-edits-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, FileOptions.DeleteOnClose);
        RandomAccess.Write(edits, blob.Span, 0);
        RandomAccess.FlushToDisk(edits);
        var result = await client.AppendAnnotationsAsync(baseFile, edits, blob.Length, output, pageCount, deadline, cancellationToken).ConfigureAwait(false);
        if (result.Status != Status.Ok || result.Value is not { } reply)
        {
            return Failure(result.Status);
        }

        return reply.Ok
            ? new HostResult(true, null, (int)reply.AnnotationsAdded, (int)reply.AnnotationsRemoved, (long)reply.BytesWritten)
            : new HostResult(false, WriterErrorCodes.NameOf(reply.IssueCodes[0]));
    }

    public async Task<HostResult> RewriteAsync(SafeFileHandle baseFile, SafeFileHandle output, uint flags, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var result = await client.RewriteDocumentAsync(baseFile, output, flags, deadline, cancellationToken).ConfigureAwait(false);
        if (result.Status != Status.Ok || result.Value is not { } reply)
        {
            return Failure(result.Status);
        }

        return reply.Ok ? new HostResult(true, null, Bytes: (long)reply.BytesWritten) : new HostResult(false, WriterErrorCodes.NameOf(reply.IssueCodes[0]));
    }

    public async Task<HostResult> ValidateAsync(SafeFileHandle file, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var result = await client.ValidateStructureAsync(file, deadline, cancellationToken).ConfigureAwait(false);
        if (result.Status != Status.Ok || result.Value is not { } reply)
        {
            return Failure(result.Status);
        }

        // Warnings (a dangling reference in a file that already had them) do not fail a save; errors that the original did not have do (the coordinator compares).
        var errors = reply.IssueCodes.Where((_, i) => reply.IssueMessages[i].StartsWith("error: ", StringComparison.Ordinal)).ToArray();
        return reply.Ok ? new HostResult(true) : new HostResult(false, "structure", ErrorCodes: errors);
    }

    public async Task<HostResult> VerifyAsync(SafeFileHandle baseFile, SafeFileHandle output, int pageCount, uint[] pages, uint[] added, uint[] removed, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var result = await client.VerifyAnnotationsAsync(baseFile, output, pageCount, pages, added, removed, deadline, cancellationToken).ConfigureAwait(false);
        if (result.Status != Status.Ok || result.Value is not { } reply)
        {
            return Failure(result.Status);
        }

        return new HostResult(reply.Ok, reply.Ok ? null : "verify", Mismatches: reply.Mismatches);
    }

    private static HostResult Failure(Status status) => new(false, status switch
    {
        Status.Timeout => "Timeout",
        Status.EngineCrashed => "HostCrashed",
        Status.Unsupported => "LimitExceeded",
        _ => "HostFailed",
    });
}

/// <summary>Stage 5's fresh engine: a new sandboxed engine process, started for the check and gone after it.</summary>
public sealed class EngineSaveCheck(Func<EngineSupervisorOptions> options) : ISaveEngineCheck
{
    public async Task<string?> CheckAsync(string path, int expectedPages, CancellationToken cancellationToken)
    {
        await using var supervisor = await EngineSupervisor.StartAsync(options(), cancellationToken).ConfigureAwait(false);
        var opened = await supervisor.OpenDocumentAsync(path, null, cancellationToken).ConfigureAwait(false);
        if (opened.Status != Status.Ok || opened.Value is not { } document)
        {
            return "engine-open-" + opened.Status;
        }

        await using (document.ConfigureAwait(false))
        {
            if (document.PageCount != expectedPages)
            {
                return "page-count";
            }

            var sizes = await document.GetPageSizesAsync(0, (uint)Math.Min(expectedPages, 256), cancellationToken).ConfigureAwait(false);
            return sizes.Status == Status.Ok ? null : "page-sizes";
        }
    }
}
