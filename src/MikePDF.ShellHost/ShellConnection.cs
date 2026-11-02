using System.IO.Pipes;
using MikePDF.Protocol;

namespace MikePDF.ShellHost;

/// <summary>
/// One handler on the other end of a pipe: the framing of ADR-0005, the handshake, and the three messages (ShellOpen, ShellRenderPage, ShellClose). The connection is the
/// session. Everything the peer sends is untrusted: a malformed or out-of-sequence frame closes the connection, a request the schema allows but the state does not gets
/// <c>InvalidArgument</c>, and what the engine answers is checked again by the broker's client before it reaches here.
/// </summary>
internal sealed class ShellConnection(ShellHostServer server, NamedPipeServerStream pipe, ShellFeature feature, CancellationToken serverStop)
{
    private const float PreviewMaxScale = 100f;

    private sealed class Session(uint id, EngineLease lease, string stagedPath, NegativeCache.FileKey key, ShellDocumentInfo info)
    {
        public uint Id { get; } = id;

        public EngineLease Lease { get; } = lease;

        public string StagedPath { get; } = stagedPath;

        public NegativeCache.FileKey Key { get; } = key;

        public ShellDocumentInfo Info { get; } = info;
    }

    private Session? _session;
    private EngineLease? _current; // the engine of this connection while a request uses it (before the session exists, during ShellOpen)
    private bool _helloDone;
    private ClientProcess? _client;

    public async Task ServeAsync()
    {
        try
        {
            _client = ClientProcess.OpenFor(pipe.SafePipeHandle.DangerousGetHandle());
            if (_client is null)
            {
                server.Log("connection refused: the client process cannot be opened");
                return;
            }

            // Only this package's own handler may use ShellHost (SHELL-17). Nothing has been read from it yet.
            if (server.Options.RequireClientPackageFamily && (Native.CurrentPackageFamily() is not { } own || !string.Equals(own, _client.Family, StringComparison.OrdinalIgnoreCase)))
            {
                server.Log($"connection refused: pid {_client.ProcessId} ({_client.Image}) is not in this package family");
                return;
            }

            server.Log($"client pid={_client.ProcessId} image={_client.Image} family={_client.Family ?? "(none)"} feature={feature}");
            await ReadLoopAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away, or ShellHost is leaving: the session ends with the connection.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            server.Log("connection failed: " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            await EndSessionAsync().ConfigureAwait(false);
            _client?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[ProtocolCodec.FrameHeaderBytes];
        while (!serverStop.IsCancellationRequested)
        {
            // Before Hello the peer gets a few seconds; after it, a preview may sit open for as long as a person reads.
            using var helloClock = new CancellationTokenSource(_helloDone ? Timeout.InfiniteTimeSpan : server.Options.HelloTimeout);
            using var readToken = CancellationTokenSource.CreateLinkedTokenSource(serverStop, helloClock.Token);
            if (!await ReadFullyAsync(header, readToken.Token).ConfigureAwait(false))
            {
                return;
            }

            if (ProtocolCodec.ReadFrameLength(header, out var length) is not null)
            {
                server.Log("closing: bad frame length");
                return;
            }

            var body = new byte[length];
            if (!await ReadFullyAsync(body, readToken.Token).ConfigureAwait(false))
            {
                return;
            }

            server.Touch();
            var decoded = ProtocolCodec.DecodeRequestBody(body);
            if (!decoded.IsOk)
            {
                server.Log("closing: malformed request (" + decoded.Failure.Detail + ")");
                return;
            }

            if (!await HandleAsync(decoded.Value).ConfigureAwait(false))
            {
                return;
            }

            server.Touch();
        }
    }

    private async Task<bool> ReadFullyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int n;
            try
            {
                n = await pipe.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>Answers one request. False ends the connection.</summary>
    private async Task<bool> HandleAsync(RequestEnvelope request)
    {
        if (!_helloDone)
        {
            if (!ShellRequestRules.IsHandlerHello(request, out var hello))
            {
                // Anything but a ShellHandler Hello first (an engine's, a broker's, a request without one) is not a handler: it gets no answer.
                server.Log("closing: the first message is not a ShellHandler Hello");
                return false;
            }

            if (hello.SchemaVersion != ProtocolInfo.SchemaVersion)
            {
                await ReplyAsync(request, Status.Unsupported, null).ConfigureAwait(false);
                return false;
            }

            _helloDone = true;
            return await ReplyAsync(request, Status.Ok, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion, PdfiumVersion = string.Empty }).ConfigureAwait(false);
        }

        if (!ShellRequestRules.IsServedAfterHello(request.Payload))
        {
            // Hello twice, Shutdown, the engine's messages: not served on this pipe.
            server.Log("closing: message " + request.MessageType + " is not served to a handler");
            return false;
        }

        switch (request.Payload)
        {
            case Ping ping:
                return await ReplyAsync(request, Status.Ok, new Pong { Nonce = ping.Nonce, Progress = 0 }).ConfigureAwait(false);
            case ShellOpen open:
                return await OpenAsync(request, open).ConfigureAwait(false);
            case ShellRenderPage render:
                return await RenderAsync(request, render).ConfigureAwait(false);
            case ShellClose:
                if (_session is null || request.DocumentId != _session.Id)
                {
                    return await ReplyAsync(request, Status.InvalidArgument, null).ConfigureAwait(false);
                }

                await EndSessionAsync().ConfigureAwait(false);
                return await ReplyAsync(request, Status.Ok, new ShellCloseReply()).ConfigureAwait(false);
            default:
                return false; // unreachable: IsServedAfterHello named exactly these four
        }
    }

    // ---- ShellOpen ----------------------------------------------------------------------------------------------------------------------------------

    private bool SwitchedOff()
    {
        var switches = server.Options.Switches();
        return feature == ShellFeature.Thumbnails ? !switches.ThumbnailsEnabled : !switches.PreviewEnabled;
    }

    private async Task<bool> OpenAsync(RequestEnvelope request, ShellOpen open)
    {
        if (_session is not null)
        {
            return await ReplyAsync(request, Status.InvalidArgument, null).ConfigureAwait(false); // one document per connection; ShellClose first
        }

        // SHELL-16: a switched-off feature answers at once, before a byte is read from the client.
        if (SwitchedOff())
        {
            return await ReplyAsync(request, Status.PermissionDenied, null).ConfigureAwait(false);
        }

        var phase = Phase.Waiting;
        NegativeCache.FileKey? key = null;
        var answeredFromCache = false;
        var outcome = await RunWatchedAsync(request, async token =>
        {
            using var source = _client!.Map(open.Section, open.Bytes, writable: false, out var failure);
            if (source is null)
            {
                server.Log("ShellOpen refused: " + failure);
                return (Status.InvalidArgument, (IReplyMessage?)null);
            }

            var fileKey = NegativeCache.FileKey.Of(source.Bytes);
            key = fileKey;
            if (server.Cache.Lookup(fileKey) is { } remembered)
            {
                server.Log("ShellOpen answered from the negative cache: " + remembered);
                answeredFromCache = true;
                return (remembered, null);
            }

            EngineLease lease;
            try
            {
                lease = await server.Pool.AcquireAsync(token).ConfigureAwait(false);
            }
            catch (EngineWaitException e)
            {
                // Not the file's doing: a full queue, a deadline that passed in the queue, a shutdown, an engine that would not start.
                return (e.Failure switch { EngineWaitFailure.Closed => Status.Cancelled, EngineWaitFailure.StartFailed => Status.EngineCrashed, _ => Status.Timeout }, null);
            }

            _current = lease;
            var staged = Path.Combine(server.Options.StagingDirectory, Guid.NewGuid().ToString("N") + ".pdf");
            var keep = false;
            try
            {
                // The engine opens a disk file through a handle the broker duplicates into it (ADR-0018: staging costs 5 ms at 5 MB, 51 ms at 30 MB).
                using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
                {
                    file.Write(source.Bytes);
                }

                phase = Phase.Opening;
                var opened = await lease.Engine.OpenAsync(staged, token).ConfigureAwait(false);
                if (!opened.IsOk || opened.Value is null)
                {
                    return (opened.Status == Status.Ok ? Status.Damaged : opened.Status, null);
                }

                var info = opened.Value;
                var session = new Session(server.NextSessionId(), lease, staged, fileKey, info);
                _session = session;
                keep = true;
                return (Status.Ok, new ShellOpenReply
                {
                    DocumentId = session.Id,
                    PageCount = Math.Min(info.PageCount, 10_000_000u),
                    FirstWidth = Math.Clamp(info.FirstWidth, 1f, 14400f),
                    FirstHeight = Math.Clamp(info.FirstHeight, 1f, 14400f),
                });
            }
            finally
            {
                if (!keep)
                {
                    _current = null;
                    await lease.DisposeAsync().ConfigureAwait(false);
                    ShellHostServer.TryDelete(staged);
                }
            }
        }, () => phase == Phase.Opening).ConfigureAwait(false);

        // Remembered by the key of the bytes. An answer that came from the cache is not remembered again: its entry keeps its own ten minutes.
        if (outcome.Status != Status.Ok && outcome.FileFault && !answeredFromCache && key is { } fileKey2 && NegativeCache.IsTheFilesFault(outcome.Status))
        {
            server.Cache.Remember(fileKey2, outcome.Status);
        }

        return await FinishAsync(request, outcome).ConfigureAwait(false);
    }

    // ---- ShellRenderPage ----------------------------------------------------------------------------------------------------------------------------

    private async Task<bool> RenderAsync(RequestEnvelope request, ShellRenderPage render)
    {
        var session = _session;
        if (session is null || request.DocumentId != session.Id || render.Page >= session.Info.PageCount || SwitchedOff())
        {
            return await ReplyAsync(request, SwitchedOff() ? Status.PermissionDenied : Status.InvalidArgument, null).ConfigureAwait(false);
        }

        // The schema bounds both edges and pixelBytes separately; their relation is ours to check (64-bit, so nothing wraps).
        if (!ShellRequestRules.PixelsCoverTheBox(render))
        {
            return await ReplyAsync(request, Status.InvalidArgument, null).ConfigureAwait(false);
        }

        var outcome = await RunWatchedAsync(request, async token =>
        {
            var rendered = await session.Lease.Engine.RenderAsync(
                render.Page, render.MaxWidth, render.MaxHeight, (render.Flags & 1) != 0, feature == ShellFeature.Thumbnails ? FitMath.DefaultMaxScale : PreviewMaxScale, token).ConfigureAwait(false);
            if (!rendered.IsOk || rendered.Value is null)
            {
                return (rendered.Status == Status.Ok ? Status.Damaged : rendered.Status, (IReplyMessage?)null);
            }

            using var page = rendered.Value;
            // The pixels go straight into the section the handler made, which the handler will check again before it builds a bitmap.
            using var target = _client!.Map(render.Pixels, render.PixelBytes, writable: true, out var failure);
            if (target is null)
            {
                server.Log("ShellRenderPage refused: " + failure);
                return (Status.InvalidArgument, null);
            }

            var bytes = (long)page.Stride * page.Height;
            if (page.Width < 1 || page.Height < 1 || page.Width > render.MaxWidth || page.Height > render.MaxHeight || page.Stride != 4 * page.Width || bytes > target.Length)
            {
                return (Status.ProtocolError, null);
            }

            page.Pixels[..(int)bytes].CopyTo(target.Writable);
            return (Status.Ok, new ShellRenderPageReply { Width = (uint)page.Width, Height = (uint)page.Height, Stride = (uint)page.Stride });
        }, () => true).ConfigureAwait(false);

        if (outcome.FileFault && outcome.Status is Status.EngineCrashed or Status.Timeout)
        {
            server.Cache.Remember(session.Key, outcome.Status);
        }

        return await FinishAsync(request, outcome).ConfigureAwait(false);
    }

    // ---- shared -------------------------------------------------------------------------------------------------------------------------------------

    private enum Phase
    {
        Waiting,
        Opening,
    }

    private readonly record struct Outcome(Status Status, IReplyMessage? Reply, bool ClientGone, bool FileFault);

    /// <summary>
    /// Runs the work of one request under its deadline (the envelope's, between 1 ms and 5 s) and while watching the pipe: this protocol never has the client send while it waits
    /// for an answer, so a read that completes means it is gone (it gave up at its own deadline, or crashed), and the engine working for it is killed at once, not when it
    /// finishes. A deadline kills the engine the same way. <paramref name="engineWasRunning"/> says whether the work had got as far as an engine, which decides whether a
    /// timeout is the file's doing.
    /// </summary>
    private async Task<Outcome> RunWatchedAsync(RequestEnvelope request, Func<CancellationToken, Task<(Status Status, IReplyMessage? Reply)>> work, Func<bool> engineWasRunning)
    {
        var budget = ShellRequestRules.Budget(request.DeadlineMs);
        using var deadline = new CancellationTokenSource(budget);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, serverStop);
        using var watchStop = new CancellationTokenSource();
        var oneByte = new byte[1];
        var clientGone = false;
        var violation = false;
        var watch = Task.Run(async () =>
        {
            try
            {
                var n = await pipe.ReadAsync(oneByte, watchStop.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    Volatile.Write(ref clientGone, true);
                }
                else
                {
                    Volatile.Write(ref violation, true);
                }

                await requestCts.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The request finished first.
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Volatile.Write(ref clientGone, true);
                await requestCts.CancelAsync().ConfigureAwait(false);
            }
        });

        // Cancelling the request ends the engine, whatever it is doing: a hung engine never answers a cancel by itself.
        using var killOnCancel = requestCts.Token.Register(() => _ = KillEngineAsync());
        (Status Status, IReplyMessage? Reply) result;
        var timedOut = false;
        try
        {
            result = await work(requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = deadline.IsCancellationRequested;
            result = (timedOut ? Status.Timeout : Status.Cancelled, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            server.Log("request failed: " + e.GetType().Name + ": " + e.Message);
            result = (Status.EngineCrashed, null);
        }

        await watchStop.CancelAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);
        var gone = Volatile.Read(ref clientGone) || Volatile.Read(ref violation);
        if (gone)
        {
            server.Log(Volatile.Read(ref clientGone) ? "client gone mid-request; the engine was killed" : "closing: the client sent a request before it had its answer");
        }

        var wasRunning = engineWasRunning();
        var fault = !timedOut || wasRunning;
        return new Outcome(result.Status, result.Reply, gone, fault && !gone);
    }

    private async Task<bool> FinishAsync(RequestEnvelope request, Outcome outcome)
    {
        if (outcome.ClientGone)
        {
            await EndSessionAsync().ConfigureAwait(false);
            return false;
        }

        if (outcome.Status != Status.Ok)
        {
            // A session whose engine timed out or died cannot go on; the handler is told and the connection ends.
            if (outcome.Status is Status.Timeout or Status.EngineCrashed or Status.Cancelled && _session is not null)
            {
                await EndSessionAsync().ConfigureAwait(false);
                await ReplyAsync(request, outcome.Status, null).ConfigureAwait(false);
                return false;
            }

            return await ReplyAsync(request, outcome.Status, null).ConfigureAwait(false);
        }

        return await ReplyAsync(request, Status.Ok, outcome.Reply).ConfigureAwait(false);
    }

    private async Task KillEngineAsync()
    {
        var lease = Volatile.Read(ref _current);
        if (lease is not null)
        {
            await lease.Engine.KillAsync().ConfigureAwait(false);
        }
    }

    private async Task EndSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        Volatile.Write(ref _current, null);
        if (session is null)
        {
            return;
        }

        await session.Lease.DisposeAsync().ConfigureAwait(false);
        ShellHostServer.TryDelete(session.StagedPath);
    }

    private async Task<bool> ReplyAsync(RequestEnvelope request, Status status, IReplyMessage? payload)
    {
        var frame = ProtocolCodec.EncodeResponseFrame(request.RequestId, status, request.MessageType, payload);
        if (!frame.IsOk)
        {
            // Our own reply broke a schema limit: say that something went wrong rather than go silent.
            frame = ProtocolCodec.EncodeResponseFrame(request.RequestId, Status.Unsupported, request.MessageType, null);
        }

        // Replies are written one at a time: only the read loop of this connection ever calls this, after the work of a request has finished.
        try
        {
            await pipe.WriteAsync(frame.Value, serverStop).ConfigureAwait(false);
            await pipe.FlushAsync(serverStop).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }

        return true;
    }
}
