using System.IO.Pipes;
using Quire.Broker;
using Quire.Protocol;

namespace Quire.Integration.Tests.Support;

/// <summary>
/// An in-process stand-in for the engine on the far end of a real named pipe, so a test can answer an
/// <see cref="EngineClient"/> with exactly the frames it likes, valid or forged. It answers like a correct engine unless the
/// test's script returns frames of its own.
/// </summary>
internal sealed class FakeEngine : IAsyncDisposable
{
    public const uint FirstDocumentId = 5;
    public const uint FakePageCount = 3;

    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;
    private readonly Func<FakeEngine, RequestEnvelope, IEnumerable<byte[]>?> _script;
    private readonly CancellationTokenSource _stop = new();
    private readonly nint _writableSection;
    private readonly nint _writableView;
    private readonly Task _loop;
    private uint _nextDocumentId = FirstDocumentId;

    private FakeEngine(NamedPipeServerStream server, NamedPipeClientStream client, SlotRing ring, Func<FakeEngine, RequestEnvelope, IEnumerable<byte[]>?> script)
    {
        _server = server;
        _client = client;
        Ring = ring;
        _script = script;
        using (var duplicate = ring.DuplicateWritableForTests())
        {
            _writableSection = duplicate.DangerousGetHandle();
            duplicate.SetHandleAsInvalid(); // keep the handle: it is closed in DisposeAsync
        }

        _writableView = NativeTestMethods.MapWritable(_writableSection);
        _loop = Task.Run(RunAsync);
    }

    public SlotRing Ring { get; }

    public List<RequestEnvelope> Received { get; } = [];

    /// <summary>The broker side of the pipe: hand this to an <see cref="EngineClient"/>.</summary>
    public Stream ClientStream => _client;

    public static async Task<FakeEngine> StartAsync(int slotCount = 4, int slotSize = 64 * 1024, Func<FakeEngine, RequestEnvelope, IEnumerable<byte[]>?>? script = null)
    {
        var name = "quire-fake-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        return new FakeEngine(server, client, new SlotRing(slotCount, slotSize), script ?? ((_, _) => null));
    }

    /// <summary>Writes a pattern a test can recognise into a slot, as the engine would after rendering.</summary>
    public unsafe void FillSlot(int index, int width, int height, int stride)
    {
        var slot = (byte*)_writableView + ((long)index * Ring.SlotSize);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = slot + (y * stride) + (x * 4);
                pixel[0] = (byte)x;
                pixel[1] = (byte)y;
                pixel[2] = (byte)index;
                pixel[3] = 255;
            }
        }
    }

    public async Task SendAsync(params byte[][] frames)
    {
        foreach (var frame in frames)
        {
            await _server.WriteAsync(frame);
        }

        await _server.FlushAsync();
    }

    /// <summary>The ends of the pipe go away: the engine "died".</summary>
    public async Task KillAsync()
    {
        await _server.DisposeAsync();
    }

    public static byte[] Response(ulong requestId, Status status, MessageType type, IReplyMessage? payload = null)
    {
        var frame = ProtocolCodec.EncodeResponseFrame(requestId, status, type, payload);
        Assert.True(frame.IsOk, frame.IsOk ? string.Empty : frame.Failure.Detail);
        return frame.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.DisposeAsync();
        await _client.DisposeAsync();
        try
        {
            await _loop;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        NativeTestMethods.Unmap(_writableView);
        NativeTestMethods.Close(_writableSection);
        Ring.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                await _server.ReadExactlyAsync(header, _stop.Token);
                Assert.Null(ProtocolCodec.ReadFrameLength(header, out var length));
                var body = new byte[length];
                await _server.ReadExactlyAsync(body, _stop.Token);
                var decoded = ProtocolCodec.DecodeRequestBody(body);
                Assert.True(decoded.IsOk, "the client sent a request its own codec rejects");
                var request = decoded.Value;
                lock (Received)
                {
                    Received.Add(request);
                }

                var frames = _script(this, request) ?? Default(request);
                await SendAsync([.. frames]);
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>What a correct engine would say.</summary>
    public IEnumerable<byte[]> Default(RequestEnvelope request)
    {
        switch (request.Payload)
        {
            case Hello:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion, PdfiumVersion = "fake" });
                break;
            case Ping ping:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new Pong { Nonce = ping.Nonce });
                break;
            case Shutdown:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new ShutdownReply());
                break;
            case OpenDocument:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new OpenDocumentReply { DocumentId = _nextDocumentId++, PageCount = FakePageCount });
                break;
            case CloseDocument:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new CloseDocumentReply());
                break;
            case GetDocumentInfo:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new GetDocumentInfoReply { PageCount = FakePageCount, PdfVersion = 17 });
                break;
            case GetPageSizes sizes:
                yield return Response(
                    request.RequestId,
                    Status.Ok,
                    request.MessageType,
                    new GetPageSizesReply
                    {
                        Widths = [.. Enumerable.Repeat(612f, (int)sizes.Count)],
                        Heights = [.. Enumerable.Repeat(792f, (int)sizes.Count)],
                        Rotations = new ushort[sizes.Count],
                    });
                break;
            case RenderTile tile:
                FillSlot((int)tile.SlotIndex, (int)tile.TileWidth, (int)tile.TileHeight, (int)tile.TileWidth * 4);
                yield return Response(
                    request.RequestId,
                    Status.Ok,
                    request.MessageType,
                    new RenderTileReply { SlotIndex = tile.SlotIndex, Width = tile.TileWidth, Height = tile.TileHeight, Stride = tile.TileWidth * 4, Generation = tile.Generation });
                break;
            case AttachSlotRing:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new AttachSlotRingReply());
                break;
            case ValidateStructure:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new ValidateStructureReply { Ok = true });
                break;
            case CancelBefore:
                yield return Response(request.RequestId, Status.Ok, request.MessageType, new CancelBeforeReply());
                break;
            default:
                break;
        }
    }
}
