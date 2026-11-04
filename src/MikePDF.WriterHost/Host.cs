using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using MikePDF.Protocol;

namespace MikePDF.WriterHost;

/// <summary>
/// The host's two lanes, as in the engine (ADR-0005 rule 2): the reading lane answers Hello, Ping and Shutdown itself, so the broker's
/// heartbeat keeps working during a long validation; ValidateStructure and the save messages (AppendAnnotations, RewriteDocument, VerifyAnnotations, NormalizeTail) run on one worker. Anything malformed or out of sequence ends the
/// process; the broker restarts it.
/// </summary>
internal static class Host
{
    private const int MaxQueuedRequests = 64;
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    public static int Die(int code, string reason)
    {
        LogPipe.Write("writerhost: " + reason);
        Environment.Exit(code);
        return code;
    }

    public static async Task<int> RunAsync(Arguments arguments)
    {
        await using var pipe = new NamedPipeClientStream(".", arguments.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return Die(ExitCode.PipeFailed, "cannot connect to the broker pipe");
        }

        if (Native.ServerProcessId(pipe.SafePipeHandle.DangerousGetHandle()) != arguments.ParentProcessId)
        {
            return Die(ExitCode.PipeFailed, "the pipe server is not the broker");
        }

        // Everything the process will need is loaded now; then it is locked, before any document byte can arrive.
        WarmUp.Run();
        SignaturePolicy.Apply();

        using var session = new Session(pipe);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private sealed class Session(Stream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Channel<(RequestEnvelope Request, DateTime Received)> _queue =
            Channel.CreateBounded<(RequestEnvelope, DateTime)>(new BoundedChannelOptions(MaxQueuedRequests) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public void Dispose() => _writeGate.Dispose();

        public async Task<int> RunAsync()
        {
            var worker = Task.Factory.StartNew(WorkAsync, TaskCreationOptions.LongRunning).Unwrap();
            var header = new byte[ProtocolCodec.FrameHeaderBytes];
            var helloDone = false;
            var exit = ExitCode.Ok;
            try
            {
                while (true)
                {
                    try
                    {
                        await pipe.ReadExactlyAsync(header).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
                    {
                        exit = ExitCode.BrokerGone;
                        break;
                    }

                    var received = DateTime.UtcNow;
                    if (ProtocolCodec.ReadFrameLength(header, out var length) is not null)
                    {
                        return Die(ExitCode.ProtocolViolation, "bad frame length");
                    }

                    var body = new byte[length];
                    try
                    {
                        await pipe.ReadExactlyAsync(body).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
                    {
                        exit = ExitCode.BrokerGone;
                        break;
                    }

                    var decoded = ProtocolCodec.DecodeRequestBody(body);
                    if (!decoded.IsOk)
                    {
                        return Die(ExitCode.ProtocolViolation, "malformed request");
                    }

                    var request = decoded.Value;
                    if (!helloDone)
                    {
                        if (request.Payload is not Hello hello || hello.ProcessKind != ProcessKind.Broker)
                        {
                            return Die(ExitCode.ProtocolViolation, "request before Hello");
                        }

                        if (hello.SchemaVersion != ProtocolInfo.SchemaVersion)
                        {
                            await ReplyAsync(request, Status.Unsupported, null).ConfigureAwait(false);
                            return Die(ExitCode.ProtocolViolation, "schema version mismatch");
                        }

                        await ReplyAsync(request, Status.Ok, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion, PdfiumVersion = string.Empty }).ConfigureAwait(false);
                        helloDone = true;
                        continue;
                    }

                    switch (request.Payload)
                    {
                        case Ping ping:
                            await ReplyAsync(request, Status.Ok, new Pong { Nonce = ping.Nonce }).ConfigureAwait(false);
                            break;
                        case Shutdown:
                            await ReplyAsync(request, Status.Ok, new ShutdownReply()).ConfigureAwait(false);
                            return await StopAsync(worker, ExitCode.Ok).ConfigureAwait(false);
                        case ValidateStructure or AppendAnnotations or RewriteDocument or VerifyAnnotations or NormalizeTail:
                            if (!_queue.Writer.TryWrite((request, received)))
                            {
                                return Die(ExitCode.ProtocolViolation, "request queue overflow");
                            }

                            break;
                        default:
                            return Die(ExitCode.ProtocolViolation, "message not served by this host");
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                exit = ExitCode.BrokerGone;
            }

            return await StopAsync(worker, exit).ConfigureAwait(false);
        }

        private async Task<int> StopAsync(Task worker, int exit)
        {
            _queue.Writer.TryComplete();
            if (await Task.WhenAny(worker, Task.Delay(StopGrace)).ConfigureAwait(false) != worker)
            {
                return Die(exit, "worker did not stop");
            }

            return exit;
        }

        private async Task WorkAsync()
        {
            await foreach (var (request, received) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                (Status Status, IReplyMessage? Reply) outcome = request.Payload switch
                {
                    ValidateStructure validate => Validate(validate, request.DeadlineMs, received),
                    AppendAnnotations append => SaveOperations.Append(append, request.DeadlineMs, received),
                    RewriteDocument rewrite => SaveOperations.Rewrite(rewrite, request.DeadlineMs, received),
                    VerifyAnnotations verify => SaveOperations.Verify(verify, request.DeadlineMs, received),
                    NormalizeTail normalize => SaveOperations.NormalizeTail(normalize, request.DeadlineMs, received),
                    _ => (Status.Unsupported, null),
                };
                await ReplyAsync(request, outcome.Status, outcome.Reply).ConfigureAwait(false);
            }
        }

        private static (Status Status, ValidateStructureReply? Reply) Validate(ValidateStructure message, uint deadlineMs, DateTime received)
        {
            // The handle is the broker's gift: it is consumed on every path, including a request that already ran out of time.
            if (message.Handle > (ulong)nint.MaxValue)
            {
                return (Status.InvalidArgument, null);
            }

            var handle = new SafeFileHandle((nint)message.Handle, ownsHandle: true);
            if (deadlineMs != 0 && DateTime.UtcNow - received > TimeSpan.FromMilliseconds(deadlineMs))
            {
                handle.Dispose();
                return (Status.Timeout, null);
            }

            using var mapped = MappedFile.Open(handle, out var failure);
            if (mapped is null)
            {
                return (failure == MapFailure.TooLarge ? Status.Unsupported : Status.InvalidArgument, null);
            }

            return (Status.Ok, Validation.Run(mapped.Data));
        }

        private async Task ReplyAsync(RequestEnvelope request, Status status, IReplyMessage? payload)
        {
            var frame = ProtocolCodec.EncodeResponseFrame(request.RequestId, status, request.MessageType, payload);
            if (!frame.IsOk)
            {
                // Our own reply broke a schema limit: say something went wrong rather than go silent.
                frame = ProtocolCodec.EncodeResponseFrame(request.RequestId, Status.Unsupported, request.MessageType, null);
            }

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await pipe.WriteAsync(frame.Value).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Die(ExitCode.BrokerGone, "pipe write failed");
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }
}
