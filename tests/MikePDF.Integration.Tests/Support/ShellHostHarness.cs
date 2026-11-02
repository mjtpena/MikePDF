using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using MikePDF.Core.Settings;
using MikePDF.Protocol;
using MikePDF.ShellHost;

namespace MikePDF.Integration.Tests.Support;

/// <summary>An unnamed section the test made, standing in for the handler's: its handle value is valid in this process, which is also the "client" ShellHost duplicates from.</summary>
internal sealed class TestSection : IDisposable
{
    private readonly MemoryMappedFile _file;

    public TestSection(long bytes)
    {
        Bytes = bytes;
        _file = MemoryMappedFile.CreateNew(null, bytes, MemoryMappedFileAccess.ReadWrite);
    }

    public long Bytes { get; }

    public ulong Handle => (ulong)_file.SafeMemoryMappedFileHandle.DangerousGetHandle();

    public static TestSection Of(byte[] content)
    {
        var section = new TestSection(content.Length);
        section.Write(content);
        return section;
    }

    public void Write(byte[] content)
    {
        using var view = _file.CreateViewAccessor(0, content.Length, MemoryMappedFileAccess.Write);
        view.WriteArray(0, content, 0, content.Length);
    }

    public byte[] Read(int count)
    {
        using var view = _file.CreateViewAccessor(0, count, MemoryMappedFileAccess.Read);
        var bytes = new byte[count];
        view.ReadArray(0, bytes, 0, count);
        return bytes;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>The handler's side of the pipe, written by hand: frames, Hello, the three messages, and whatever else a test wants to send.</summary>
internal sealed class ShellTestClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private ulong _nextId;

    public ShellTestClient(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(10_000);
    }

    public NamedPipeClientStream Pipe => _pipe;

    public async Task<ulong> SendAsync(IRequestMessage payload, uint documentId = 0, uint deadlineMs = 1700)
    {
        var frame = ProtocolCodec.EncodeRequestFrame(++_nextId, documentId, deadlineMs, payload);
        Assert.True(frame.IsOk, frame.IsOk ? string.Empty : frame.Failure.Detail);
        await _pipe.WriteAsync(frame.Value);
        await _pipe.FlushAsync();
        return _nextId;
    }

    public async Task SendRawAsync(byte[] bytes)
    {
        await _pipe.WriteAsync(bytes);
        await _pipe.FlushAsync();
    }

    /// <summary>The next response, or null if ShellHost closed the pipe (or nothing came within <paramref name="timeout"/>).</summary>
    public async Task<ResponseEnvelope?> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var header = new byte[4];
        try
        {
            await _pipe.ReadExactlyAsync(header, cts.Token);
            Assert.Null(ProtocolCodec.ReadFrameLength(header, out var length));
            var body = new byte[length];
            await _pipe.ReadExactlyAsync(body, cts.Token);
            var decoded = ProtocolCodec.DecodeResponseBody(body);
            Assert.True(decoded.IsOk);
            return decoded.Value;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async Task<ResponseEnvelope> CallAsync(IRequestMessage payload, uint documentId = 0, uint deadlineMs = 1700)
    {
        var id = await SendAsync(payload, documentId, deadlineMs);
        var response = await ReceiveAsync();
        Assert.NotNull(response);
        Assert.Equal(id, response.RequestId);
        Assert.Equal(payload.MessageType, response.MessageType);
        return response;
    }

    public async Task<ResponseEnvelope> HelloAsync()
    {
        var response = await CallAsync(new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.ShellHandler, Pid = (uint)Environment.ProcessId });
        Assert.Equal(Status.Ok, response.Status);
        return response;
    }

    public async Task<ResponseEnvelope> OpenAsync(TestSection source, uint deadlineMs = 1700) =>
        await CallAsync(new ShellOpen { Section = source.Handle, Bytes = (uint)source.Bytes }, 0, deadlineMs);

    public async Task<ResponseEnvelope> RenderAsync(uint session, TestSection pixels, uint maxWidth, uint maxHeight, uint page = 0, uint deadlineMs = 1700) =>
        await CallAsync(new ShellRenderPage { Page = page, MaxWidth = maxWidth, MaxHeight = maxHeight, Flags = 1, Pixels = pixels.Handle, PixelBytes = (uint)pixels.Bytes }, session, deadlineMs);

    /// <summary>True when ShellHost has closed its end: the next read ends at once.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout) => await ReceiveAsync(timeout) is null;

    public async ValueTask DisposeAsync() => await _pipe.DisposeAsync();
}

/// <summary>A ShellHost server running in this process on pipes of its own, with the knobs a test turns.</summary>
internal sealed class ShellHostHarness : IAsyncDisposable
{
    private readonly Task _run;
    private readonly string _prefix = "mikepdf-test-shell-" + Guid.NewGuid().ToString("N")[..12];

    private ShellHostHarness(IShellEngineFactory engines, TimeSpan? idle, TimeProvider? time)
    {
        StagingDirectory = Path.Combine(Path.GetTempPath(), "mikepdf-shell-staging-" + Guid.NewGuid().ToString("N")[..8]);
        Server = new ShellHostServer(new ShellHostOptions
        {
            UserSid = ShellPipe.CurrentUserSid(),
            StagingDirectory = StagingDirectory,
            Engines = engines,
            PipeName = feature => _prefix + (feature == ShellFeature.Preview ? ".Preview" : string.Empty),
            Switches = () => Switches,
            IdleTimeout = idle ?? TimeSpan.FromMinutes(5),
            RequireClientPackageFamily = false, // unpackaged: nothing has a family
            Time = time ?? TimeProvider.System,
            Log = line => Log.Add(line),
            HelloTimeout = TimeSpan.FromSeconds(2),
        });
        _run = Server.RunAsync(); // the pipes exist when this returns
    }

    public ShellHostServer Server { get; }

    public string StagingDirectory { get; }

    public ShellSwitches Switches { get; set; } = ShellSwitches.Defaults;

    public System.Collections.Concurrent.ConcurrentBag<string> Log { get; } = [];

    public Task Run => _run;

    public string PipeNameOf(ShellFeature feature) => _prefix + (feature == ShellFeature.Preview ? ".Preview" : string.Empty);

    public static ShellHostHarness Start(IShellEngineFactory engines, TimeSpan? idle = null, TimeProvider? time = null) => new(engines, idle, time);

    public ShellTestClient Connect(ShellFeature feature = ShellFeature.Thumbnails) => new(PipeNameOf(feature));

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
