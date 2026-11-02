using System.IO.Pipes;
using MikePDF.Broker;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Support;

/// <summary>
/// The real engine, launched through the real launcher and pipe server, but talked to frame by frame with no
/// <see cref="EngineClient"/> in between, so a test can send what a correct client never would.
/// </summary>
internal sealed class RawEngine : IAsyncDisposable
{
    private readonly EnginePipeServer _server;
    private readonly SlotRing? _ring;
    private ulong _nextId;

    private readonly ulong _ringHandle;

    private RawEngine(SandboxedProcess process, EnginePipeServer server, NamedPipeServerStream pipe, SlotRing? ring, ulong ringHandle)
    {
        _ringHandle = ringHandle;
        Process = process;
        _server = server;
        Pipe = pipe;
        _ring = ring;
    }

    /// <summary>Every kernel handle the broker holds for this process, for tests that check none is inheritable.</summary>
    public IReadOnlyList<nint> BrokerHandles => [.. Process.BrokerHandles, _server.NativeHandle, .. _ring is null ? [] : new[] { _ring.SectionHandle }];

    /// <summary>The ring's section as the engine's handle table knows it (duplicated in at launch).</summary>
    public ulong RingHandle => _ringHandle;

    public SandboxedProcess Process { get; }

    public NamedPipeServerStream Pipe { get; }

    /// <summary>The real WriterHost, same launcher and profile, same raw access. It has no slot ring.</summary>
    public static async Task<RawEngine> StartWriterHostAsync(SandboxFixture sandbox)
    {
        var launcher = new SandboxedProcessLauncher(sandbox.Profile, new SandboxPolicy { ProhibitDynamicCode = false });
        var server = EnginePipeServer.Create(sandbox.Profile.Sid);
        var process = launcher.Launch(new SandboxLaunchRequest(sandbox.WriterHostPath, $"--pipe {server.Name} --parent {Environment.ProcessId}"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipe = await server.AcceptAsync(process.ProcessId, timeout.Token);
        return new RawEngine(process, server, pipe, null, 0);
    }

    public static async Task<RawEngine> StartAsync(SandboxFixture sandbox, int slotCount = 8, int slotSize = 4 << 20)
    {
        var launcher = new SandboxedProcessLauncher(sandbox.Profile);
        var ring = new SlotRing(slotCount, slotSize);
        var server = EnginePipeServer.Create(sandbox.Profile.Sid);
        var process = launcher.Launch(new SandboxLaunchRequest(sandbox.EnginePath, $"--pipe {server.Name} --parent {Environment.ProcessId} --kind document") { StartSuspended = true });
        var ringHandle = ring.DuplicateInto(process); // nothing is inherited: the ring is duplicated in, and announced by AttachSlotRing
        process.Resume();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipe = await server.AcceptAsync(process.ProcessId, timeout.Token);
        return new RawEngine(process, server, pipe, ring, ringHandle);
    }

    /// <summary>Sends one request and returns its requestId.</summary>
    public async Task<ulong> SendAsync(uint documentId, IRequestMessage payload, uint deadlineMs = 0)
    {
        var frame = ProtocolCodec.EncodeRequestFrame(++_nextId, documentId, deadlineMs, payload);
        Assert.True(frame.IsOk, frame.IsOk ? string.Empty : frame.Failure.Detail);
        await SendRawAsync(frame.Value);
        return _nextId;
    }

    public async Task SendRawAsync(byte[] bytes)
    {
        await Pipe.WriteAsync(bytes);
        await Pipe.FlushAsync();
    }

    /// <summary>The next response, or null if the engine closed the pipe.</summary>
    public async Task<ResponseEnvelope?> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var header = new byte[4];
        try
        {
            await Pipe.ReadExactlyAsync(header, cts.Token);
            Assert.Null(ProtocolCodec.ReadFrameLength(header, out var length));
            var body = new byte[length];
            await Pipe.ReadExactlyAsync(body, cts.Token);
            var decoded = ProtocolCodec.DecodeResponseBody(body);
            Assert.True(decoded.IsOk);
            return decoded.Value;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<ResponseEnvelope> CallAsync(uint documentId, IRequestMessage payload, uint deadlineMs = 0)
    {
        var id = await SendAsync(documentId, payload, deadlineMs);
        var response = await ReceiveAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(response);
        Assert.Equal(id, response.RequestId);
        Assert.Equal(payload.MessageType, response.MessageType);
        return response;
    }

    public async Task<ResponseEnvelope> HelloAsync()
    {
        var response = await CallAsync(0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Broker, Pid = (uint)Environment.ProcessId });
        Assert.Equal(Status.Ok, response.Status);
        return response;
    }

    /// <summary>Announces the ring that was duplicated into the engine at launch.</summary>
    public async Task<ResponseEnvelope> AttachRingAsync() =>
        await CallAsync(0, new AttachSlotRing { Handle = _ringHandle, SlotCount = (uint)_ring!.SlotCount, SlotSize = (uint)_ring.SlotSize });

    public async Task<uint> OpenAsync(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var response = await CallAsync(0, new OpenDocument { Handle = Process.DuplicateFileInto(file) });
        Assert.Equal(Status.Ok, response.Status);
        return ((OpenDocumentReply)response.Payload!).DocumentId;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout) => await Process.WaitForExitAsync(timeout);

    public async ValueTask DisposeAsync()
    {
        Process.Kill();
        await Pipe.DisposeAsync();
        _server.Dispose();
        await Process.WaitForExitAsync(TimeSpan.FromSeconds(5));
        Process.Dispose();
        _ring?.Dispose();
    }
}
