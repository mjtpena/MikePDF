using System.Diagnostics;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>The real engine, spoken to frame by frame: what it answers, what it refuses and when it ends the session.</summary>
public sealed class EngineProtocolTests(SandboxFixture sandbox)
{
    private const int ExitProtocolViolation = 5;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HelloNamesTheSchemaAndPdfiumVersion()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        var hello = await engine.HelloAsync();
        var reply = (HelloReply)hello.Payload!;
        Assert.Equal(ProtocolInfo.SchemaVersion, reply.SchemaVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", reply.PdfiumVersion);

        var pong = await engine.CallAsync(0, new Ping { Nonce = 0xFEEDFACE });
        Assert.Equal(0xFEEDFACEUL, ((Pong)pong.Payload!).Nonce);
        Assert.False(engine.Process.HasExited);
    }

    [Fact]
    public async Task AnotherSchemaVersionIsRefusedAndTheEngineLeaves()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        var response = await engine.CallAsync(0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion + 1, ProcessKind = ProcessKind.Broker, Pid = 1 });
        Assert.Equal(Status.Unsupported, response.Status);
        Assert.True(await engine.WaitForExitAsync(Wait));
        Assert.Equal((uint)ExitProtocolViolation, engine.Process.ExitCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("length above the frame limit")]
    [InlineData("zero length")]
    [InlineData("a response where a request belongs")]
    public async Task AMalformedFrameClosesThePipeAndEndsTheProcess(string kind)
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        var bad = kind switch
        {
            "garbage" => new byte[] { 5, 0, 0, 0, 0xC1, 0xC1, 0xC1, 0xC1, 0xC1 },
            "length above the frame limit" => [0x01, 0x00, 0x01, 0x00],
            "zero length" => [0, 0, 0, 0],
            _ => FakeEngine.Response(1, Status.Ok, MessageType.Ping, new Pong { Nonce = 1 }),
        };
        await engine.SendRawAsync(bad);

        Assert.Null(await engine.ReceiveAsync(Wait));
        Assert.True(await engine.WaitForExitAsync(Wait));
        Assert.Equal((uint)ExitProtocolViolation, engine.Process.ExitCode);
    }

    [Fact]
    public async Task ARequestBeforeHelloEndsTheProcess()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.SendAsync(0, new Ping { Nonce = 1 });
        Assert.Null(await engine.ReceiveAsync(Wait));
        Assert.True(await engine.WaitForExitAsync(Wait));
        Assert.Equal((uint)ExitProtocolViolation, engine.Process.ExitCode);
    }

    [Fact]
    public async Task ASecondHelloEndsTheProcess()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        await engine.SendAsync(0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Broker, Pid = 1 });
        Assert.Null(await engine.ReceiveAsync(Wait));
        Assert.True(await engine.WaitForExitAsync(Wait));
        Assert.Equal((uint)ExitProtocolViolation, engine.Process.ExitCode);
    }

    [Fact]
    public async Task APeerThatClaimsToBeAnEngineIsNotServed()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.SendAsync(0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Engine, Pid = 1 });
        Assert.Null(await engine.ReceiveAsync(Wait));
        Assert.True(await engine.WaitForExitAsync(Wait));
    }

    [Fact]
    public async Task ShutdownIsAnsweredAndTheEngineExitsCleanly()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        var response = await engine.CallAsync(0, new Shutdown());
        Assert.Equal(Status.Ok, response.Status);
        Assert.True(await engine.WaitForExitAsync(Wait));
        Assert.Equal(0u, engine.Process.ExitCode);
    }

    [Fact]
    public async Task RenderTileIsRefusedUntilTheRingIsAttachedAndTheRingAttachesOnce()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        var document = await engine.OpenAsync(SandboxFixture.FixturePath("rectangles.pdf"));
        var tile = new RenderTile { Page = 0, Scale = 1f, TileWidth = 16, TileHeight = 16 };

        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, tile)).Status); // no ring yet
        Assert.Equal(Status.Ok, (await engine.AttachRingAsync()).Status);
        Assert.Equal(Status.Ok, (await engine.CallAsync(document, tile)).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.AttachRingAsync()).Status);             // not twice
        Assert.Equal(Status.Ok, (await engine.CallAsync(document, tile)).Status);                  // and the first one still stands
    }

    [Fact]
    public async Task AttachRefusesAHandleThatIsNotAnHonestRingWithoutLosingTheProcess()
    {
        await using var engine = await RawEngine.StartAsync(sandbox, slotCount: 2, slotSize: 8192);
        await engine.HelloAsync();
        // Not a handle at all (strict handle checks would kill an engine that touched it unguarded), and a real handle that is not a section.
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(0, new AttachSlotRing { Handle = 12345, SlotCount = 2, SlotSize = 8192 })).Status);
        using var file = File.OpenHandle(SandboxFixture.FixturePath("hello_world.pdf"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(0, new AttachSlotRing { Handle = engine.Process.DuplicateFileInto(file), SlotCount = 2, SlotSize = 8192 })).Status);
        Assert.False(engine.Process.HasExited);

        // A real ring that is smaller than what is claimed for it: 2 slots of 8 KiB, announced as 8 slots.
        var tooBig = await engine.CallAsync(0, new AttachSlotRing { Handle = engine.RingHandle, SlotCount = 8, SlotSize = 8192 });
        Assert.Equal(Status.InvalidArgument, tooBig.Status);
        Assert.False(engine.Process.HasExited);
    }

    [Fact]
    public async Task ARenderIntoAnUnknownOrTooSmallSlotIsRefusedNotObeyed()
    {
        await using var engine = await RawEngine.StartAsync(sandbox, slotCount: 4, slotSize: 64 * 1024);
        await engine.HelloAsync();
        Assert.Equal(Status.Ok, (await engine.AttachRingAsync()).Status);
        var document = await engine.OpenAsync(SandboxFixture.FixturePath("rectangles.pdf"));

        RenderTile Tile(uint slot, uint width, uint height) => new() { Page = 0, Scale = 1f, TileWidth = width, TileHeight = height, SlotIndex = slot };

        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, Tile(4, 16, 16))).Status);      // the ring has slots 0 to 3
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, Tile(1023, 16, 16))).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, Tile(0, 1024, 1024))).Status);  // 4 MiB into a 64 KiB slot
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, Tile(0, 129, 128))).Status);    // one pixel over: 66048 bytes
        Assert.Equal(Status.Ok, (await engine.CallAsync(document, Tile(0, 128, 128))).Status);                 // exactly one slot
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(document, new RenderTile { Page = 1, Scale = 1f, TileWidth = 16, TileHeight = 16 })).Status);
        Assert.False(engine.Process.HasExited);
    }

    [Fact]
    public async Task UnknownDocumentsAndHandlesAreInvalidArgumentsNotCrashes()
    {
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(99, new GetDocumentInfo())).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(99, new CloseDocument())).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(99, new CancelBefore { Generation = 1 })).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(0, new OpenDocument { Handle = 12345 })).Status);
        Assert.Equal(Status.InvalidArgument, (await engine.CallAsync(0, new OpenDocument { Handle = ulong.MaxValue })).Status);
        Assert.False(engine.Process.HasExited);
        Assert.Equal(Status.Ok, (await engine.CallAsync(0, new Ping { Nonce = 1 })).Status);
    }

    [Fact]
    public async Task PingAndCancelBeforeAreAnsweredWhileTheEngineThreadIsBusyRendering()
    {
        using var slow = new TempFile("slow.pdf", TestPdf.SlowPage(300));
        await using var engine = await RawEngine.StartAsync(sandbox);
        await engine.HelloAsync();
        await engine.AttachRingAsync();
        {
            var document = await engine.OpenAsync(slow.Path);
            var clock = Stopwatch.StartNew();
            var render = await engine.SendAsync(document, new RenderTile { Page = 0, Scale = 4f, TileWidth = 512, TileHeight = 512, Generation = 1 });
            await Task.Delay(100);

            var pong = await engine.CallAsync(0, new Ping { Nonce = 5 });
            var pongAt = clock.ElapsedMilliseconds;
            Assert.Equal(5UL, ((Pong)pong.Payload!).Nonce);
            Assert.Equal(Status.Ok, (await engine.CallAsync(document, new CancelBefore { Generation = 2 })).Status);

            // The render, long after the heartbeat answered, ends as Cancelled.
            var last = await engine.ReceiveAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(last);
            Assert.Equal(render, last.RequestId);
            Assert.Equal(Status.Cancelled, last.Status);
            Assert.True(pongAt < 1000, $"the heartbeat took {pongAt} ms behind a render");
        }
    }
}
