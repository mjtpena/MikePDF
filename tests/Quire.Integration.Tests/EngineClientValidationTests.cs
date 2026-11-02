using Quire.Broker;
using Quire.Integration.Tests.Support;
using Quire.Protocol;

namespace Quire.Integration.Tests;

/// <summary>
/// A.9: every response from the engine is untrusted. These tests put an <see cref="EngineClient"/> on one end of a real named pipe
/// and a script on the other, and check that anything the engine must never send ends the session instead of reaching the caller.
/// </summary>
public sealed class EngineClientValidationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed record Scenario(
        string Name,
        MessageType Target,
        int Occurrence,
        Func<EngineClient, uint, Task<Status>> Act,
        Func<RequestEnvelope, byte[][]> Forge);

    private static byte[] Reply(RequestEnvelope r, Status status, MessageType type, IReplyMessage? payload = null) =>
        FakeEngine.Response(r.RequestId, status, type, payload);

    private static RenderTileArgs Tile() => new(0, 1f, 0, 0, 0, 16, 16);

    private static GetOutlineReply Outline(uint total, int count, ushort kind = 1, uint page = 0) => new()
    {
        Total = total,
        Titles = [.. Enumerable.Repeat("entry", count)],
        Kinds = [.. Enumerable.Repeat(kind, count)],
        Pages = [.. Enumerable.Repeat(page, count)],
        Xs = new float[count],
        Ys = new float[count],
        HasChildren = new ushort[count],
    };

    private static GetPageLinksReply Links(uint total, int count, ushort kind = 1, uint page = 0, float left = 0, float right = 10) => new()
    {
        Total = total,
        Lefts = [.. Enumerable.Repeat(left, count)],
        Bottoms = new float[count],
        Rights = [.. Enumerable.Repeat(right, count)],
        Tops = [.. Enumerable.Repeat(10f, count)],
        Kinds = [.. Enumerable.Repeat(kind, count)],
        Pages = [.. Enumerable.Repeat(page, count)],
        Xs = new float[count],
        Ys = new float[count],
    };

    private static GetPageTextReply Text(uint total, int count) => new()
    {
        Total = total,
        Units = [.. Enumerable.Repeat((ushort)'a', count)],
        Lefts = new float[count],
        Bottoms = new float[count],
        Rights = [.. Enumerable.Repeat(1f, count)],
        Tops = [.. Enumerable.Repeat(1f, count)],
        Flags = new ushort[count],
    };

    private static Func<RequestEnvelope, byte[][]> ForgedTile(Action<RenderTile, RenderTileReply> change) => r =>
    {
        var asked = (RenderTile)r.Payload;
        var reply = new RenderTileReply { SlotIndex = asked.SlotIndex, Width = asked.TileWidth, Height = asked.TileHeight, Stride = asked.TileWidth * 4, Generation = asked.Generation };
        change(asked, reply);
        return [Reply(r, Status.Ok, MessageType.RenderTile, reply)];
    };

    private static Func<EngineClient, uint, Task<Status>> RenderCall() =>
        async (client, document) =>
        {
            var result = await client.RenderTileAsync(document, Tile(), 3, Wait);
            result.Value?.Dispose();
            return result.Status;
        };

    private static readonly Scenario[] AllScenarios =
    [
        new("echo-mismatch", MessageType.GetPageSizes, 1, async (c, d) => (await c.GetPageSizesAsync(d, 0, 2, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.CloseDocument, new CloseDocumentReply())]),
        new("unknown-request-id", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            r => [FakeEngine.Response(r.RequestId + 1000, Status.Ok, MessageType.Ping, new Pong { Nonce = 1 })]),
        new("ping-answered-with-an-error", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            r => [Reply(r, Status.Damaged, MessageType.Ping)]),
        new("engine-claims-it-crashed", MessageType.GetDocumentInfo, 1, async (c, d) => (await c.GetDocumentInfoAsync(d, Wait)).Status,
            r => [Reply(r, Status.EngineCrashed, MessageType.GetDocumentInfo)]),
        new("protocol-error-status", MessageType.GetDocumentInfo, 1, async (c, d) => (await c.GetDocumentInfoAsync(d, Wait)).Status,
            r => [Reply(r, Status.ProtocolError, MessageType.GetDocumentInfo)]),
        new("partial-render-for-an-info-request", MessageType.GetDocumentInfo, 1, async (c, d) => (await c.GetDocumentInfoAsync(d, Wait)).Status,
            r => [Reply(r, Status.PartiallyRendered, MessageType.GetDocumentInfo, new GetDocumentInfoReply { PageCount = FakeEngine.FakePageCount })]),
        new("wrong-pong-nonce", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(7, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.Ping, new Pong { Nonce = 8 })]),
        new("tile-wider-than-asked", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.Width += 1)),
        new("tile-taller-than-asked", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.Height += 1)),
        new("tile-in-another-slot", MessageType.RenderTile, 1, RenderCall(), ForgedTile((asked, reply) => reply.SlotIndex = (asked.SlotIndex + 1) % 4)),
        new("tile-in-a-slot-outside-the-ring", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.SlotIndex = 1000)),
        new("stride-below-one-row", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.Stride = 60)),
        new("tile-larger-than-its-slot", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.Stride = 8192)),
        new("tile-of-another-generation", MessageType.RenderTile, 1, RenderCall(), ForgedTile((_, reply) => reply.Generation += 1)),
        new("too-few-page-sizes", MessageType.GetPageSizes, 1, async (c, d) => (await c.GetPageSizesAsync(d, 0, 2, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageSizes, new GetPageSizesReply { Widths = [1f], Heights = [1f], Rotations = [0] })]),
        new("document-id-reused", MessageType.OpenDocument, 2, async (c, _) => (await c.OpenDocumentAsync(2, null, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.OpenDocument, new OpenDocumentReply { DocumentId = FakeEngine.FirstDocumentId, PageCount = FakeEngine.FakePageCount })]),
        new("page-count-changed", MessageType.GetDocumentInfo, 1, async (c, d) => (await c.GetDocumentInfoAsync(d, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply { PageCount = FakeEngine.FakePageCount + 1 })]),
        new("other-schema-version", MessageType.Hello, 1, async (c, _) => (await c.HelloAsync(Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.Hello, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion + 1, PdfiumVersion = "x" })]),
        new("attach-answered-with-damaged", MessageType.AttachSlotRing, 1, async (c, _) => (await c.AttachSlotRingAsync(1, 4, 65536, Wait)).Status,
            r => [Reply(r, Status.Damaged, MessageType.AttachSlotRing)]),
        new("validate-structure-answered-with-cancelled", MessageType.ValidateStructure, 1, async (c, _) => (await c.ValidateStructureAsync(1, Wait)).Status,
            r => [Reply(r, Status.Cancelled, MessageType.ValidateStructure)]),
        // ---- the reading services (WP-S2) ----
        new("outline-window-longer-than-asked", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetOutline, Outline(total: 5, count: 2))]),
        new("outline-window-shorter-than-the-list-allows", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 0, 2, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetOutline, Outline(total: 5, count: 1))]),
        new("outline-entries-for-a-start-past-the-end", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 9, 2, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetOutline, Outline(total: 5, count: 1))]),
        new("outline-destination-beyond-the-document", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetOutline, Outline(total: 1, count: 1, kind: 1, page: FakeEngine.FakePageCount))]),
        new("outline-page-on-an-external-destination", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetOutline, Outline(total: 1, count: 1, kind: 4, page: 1))]),
        new("outline-answered-with-unsupported", MessageType.GetOutline, 1, async (c, d) => (await c.GetOutlineAsync(d, [], 0, 1, Wait)).Status,
            r => [Reply(r, Status.Unsupported, MessageType.GetOutline)]),
        new("links-window-longer-than-asked", MessageType.GetPageLinks, 1, async (c, d) => (await c.GetPageLinksAsync(d, 0, 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageLinks, Links(total: 4, count: 2))]),
        new("links-reversed-rectangle", MessageType.GetPageLinks, 1, async (c, d) => (await c.GetPageLinksAsync(d, 0, 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageLinks, Links(total: 1, count: 1, left: 50, right: 10))]),
        new("links-destination-beyond-the-document", MessageType.GetPageLinks, 1, async (c, d) => (await c.GetPageLinksAsync(d, 0, 0, 1, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageLinks, Links(total: 1, count: 1, kind: 2, page: 99))]),
        new("link-address-with-a-line-break", MessageType.GetLinkUri, 1, async (c, d) => (await c.GetLinkUriAsync(d, 0, 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetLinkUri, new GetLinkUriReply { Uri = "https://example.org/\nevil" })]),
        new("link-address-that-reverses-the-text", MessageType.GetLinkUri, 1, async (c, d) => (await c.GetLinkUriAsync(d, 0, 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetLinkUri, new GetLinkUriReply { Uri = "https://example.org/" + (char)0x202E + "gpj.exe" })]),
        new("link-address-with-a-space", MessageType.GetLinkUri, 1, async (c, d) => (await c.GetLinkUriAsync(d, 0, 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetLinkUri, new GetLinkUriReply { Uri = "https://example.org/a b" })]),
        new("link-address-empty", MessageType.GetLinkUri, 1, async (c, d) => (await c.GetLinkUriAsync(d, 0, 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetLinkUri, new GetLinkUriReply { Uri = "" })]),
        new("page-boxes-answered-with-cancelled", MessageType.GetPageBoxes, 1, async (c, d) => (await c.GetPageBoxesAsync(d, 0, Wait)).Status,
            r => [Reply(r, Status.Cancelled, MessageType.GetPageBoxes)]),
        new("text-window-longer-than-asked", MessageType.GetPageText, 1, async (c, d) => (await c.GetPageTextAsync(d, 0, 0, 2, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageText, Text(total: 10, count: 3))]),
        new("text-window-shorter-than-the-text-allows", MessageType.GetPageText, 1, async (c, d) => (await c.GetPageTextAsync(d, 0, 0, 5, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageText, Text(total: 10, count: 4))]),
        new("text-start-past-the-end", MessageType.GetPageText, 1, async (c, d) => (await c.GetPageTextAsync(d, 0, 20, 5, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.GetPageText, Text(total: 10, count: 0))]),
        new("search-matches-out-of-order", MessageType.SearchPage, 1, async (c, d) => (await c.SearchPageAsync(d, 0, "x", 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.SearchPage, new SearchPageReply { Starts = [10, 4], Lengths = [1, 1] })]),
        new("search-matches-that-repeat", MessageType.SearchPage, 1, async (c, d) => (await c.SearchPageAsync(d, 0, "x", 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.SearchPage, new SearchPageReply { Starts = [4, 4], Lengths = [1, 1] })]),
        new("search-match-past-the-end-of-any-text", MessageType.SearchPage, 1, async (c, d) => (await c.SearchPageAsync(d, 0, "x", 0, Wait)).Status,
            r => [Reply(r, Status.Ok, MessageType.SearchPage, new SearchPageReply { Starts = [199_999], Lengths = [2] })]),
        new("garbage-bytes", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            _ => [[5, 0, 0, 0, 0xC1, 0xC1, 0xC1, 0xC1, 0xC1]]),
        new("length-prefix-above-the-frame-limit", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            _ => [[0x01, 0x00, 0x01, 0x00]]),
        new("zero-length-frame", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            _ => [[0, 0, 0, 0]]),
        new("envelope-with-the-wrong-shape", MessageType.Ping, 1, async (c, _) => (await c.PingAsync(1, Wait)).Status,
            _ => [[4, 0, 0, 0, 0x93, 0x01, 0x02, 0x03]]),
    ];

    private static readonly Dictionary<string, Scenario> ByName = AllScenarios.ToDictionary(s => s.Name);

    public static TheoryData<string> ScenarioNames => [.. AllScenarios.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task AForgedResponseEndsTheSessionAndNeverReachesTheCaller(string name)
    {
        var scenario = ByName[name];
        var seen = 0;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.MessageType == scenario.Target && ++seen == scenario.Occurrence ? scenario.Forge(r) : null);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var faulted = new TaskCompletionSource<EngineFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += fault => faulted.TrySetResult(fault);

        var opened = await client.OpenDocumentAsync(1, null, Wait);
        Assert.Equal(Status.Ok, opened.Status);

        var status = await scenario.Act(client, opened.Value!.DocumentId).WaitAsync(Wait);

        var fault = await faulted.Task.WaitAsync(Wait);
        Assert.Equal(EngineFaultKind.ProtocolViolation, fault.Kind);
        Assert.Equal(Status.EngineCrashed, status);
        Assert.True(client.IsFaulted);
        Assert.Equal(Status.EngineCrashed, (await client.PingAsync(1, Wait)).Status); // nothing more is sent to or accepted from this engine
        AssertEverySlotIsFree(fake.Ring);
    }

    [Fact]
    public async Task ARepeatedResponseIsAViolationEvenThoughTheFirstCopyWasFine()
    {
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is Ping ping
            ? [Reply(r, Status.Ok, MessageType.Ping, new Pong { Nonce = ping.Nonce }), Reply(r, Status.Ok, MessageType.Ping, new Pong { Nonce = ping.Nonce })]
            : null);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var faulted = new TaskCompletionSource<EngineFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += fault => faulted.TrySetResult(fault);

        var pong = await client.PingAsync(9, Wait);
        Assert.Equal(Status.Ok, pong.Status);
        Assert.Equal(EngineFaultKind.ProtocolViolation, (await faulted.Task.WaitAsync(Wait)).Kind);
    }

    [Fact]
    public async Task AValidSessionIsAcceptedAndPixelsArriveThroughTheSlot()
    {
        await using var fake = await FakeEngine.StartAsync();
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);

        var hello = await client.HelloAsync(Wait);
        Assert.Equal(Status.Ok, hello.Status);
        Assert.Equal("fake", hello.Value!.PdfiumVersion);
        Assert.Equal(77UL, (await client.PingAsync(77, Wait)).Value!.Nonce);

        var opened = await client.OpenDocumentAsync(1, "secret", Wait);
        var document = opened.Value!.DocumentId;
        Assert.Equal(FakeEngine.FirstDocumentId, document);
        Assert.Equal(FakeEngine.FakePageCount, (await client.GetDocumentInfoAsync(document, Wait)).Value!.PageCount);
        var sizes = await client.GetPageSizesAsync(document, 1, 2, Wait);
        Assert.Equal(2, sizes.Value!.Widths.Length);

        var tile = await client.RenderTileAsync(document, new RenderTileArgs(2, 1.5f, 1, 10, 20, 16, 8), 4, Wait);
        Assert.Equal(Status.Ok, tile.Status);
        using var lease = tile.Value!;
        Assert.Equal((16, 8, 64), (lease.Width, lease.Height, lease.Stride));
        var pixels = lease.Pixels;
        Assert.Equal(64 * 8, pixels.Length);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Assert.Equal(x, pixels[(y * 64) + (x * 4)]);
                Assert.Equal(y, pixels[(y * 64) + (x * 4) + 1]);
                Assert.Equal(255, pixels[(y * 64) + (x * 4) + 3]);
            }
        }

        Assert.Equal(Status.Ok, (await client.CloseDocumentAsync(document, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetDocumentInfoAsync(document, Wait)).Status); // refused locally now
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task RequestsThatCannotBeRightAreRefusedBeforeTheyAreSent()
    {
        await using var fake = await FakeEngine.StartAsync();
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;
        var sent = fake.Received.Count;

        Assert.Equal(Status.InvalidArgument, (await client.GetPageSizesAsync(document, 2, 2, Wait)).Status); // pages 2 and 3 of 3
        Assert.Equal(Status.InvalidArgument, (await client.GetPageSizesAsync(document, 0, 0, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetPageSizesAsync(document + 1, 0, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.RenderTileAsync(document, new RenderTileArgs(3, 1f, 0, 0, 0, 16, 16), 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.RenderTileAsync(document, new RenderTileArgs(0, 1f, 0, 0, 0, 1024, 1024), 1, Wait)).Status); // 4 MiB tile, 64 KiB slots
        Assert.Equal(Status.InvalidArgument, (await client.RenderTileAsync(document, new RenderTileArgs(0, 0f, 0, 0, 0, 16, 16), 1, Wait)).Status); // scale outside the schema
        Assert.Equal(Status.InvalidArgument, (await client.RenderTileAsync(document, new RenderTileArgs(0, 1f, 4, 0, 0, 16, 16), 1, Wait)).Status); // rotation 4
        Assert.Equal(sent, fake.Received.Count);
        AssertEverySlotIsFree(fake.Ring);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task APeerThatGoesAwayEndsEveryPendingRequestWithEngineCrashed()
    {
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload is Ping ? [] : null);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var faulted = new TaskCompletionSource<EngineFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += fault => faulted.TrySetResult(fault);

        var pending = client.PingAsync(1, TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        await fake.KillAsync();

        Assert.Equal(Status.EngineCrashed, (await pending.WaitAsync(Wait)).Status);
        Assert.Equal(EngineFaultKind.PipeClosed, (await faulted.Task.WaitAsync(Wait)).Kind);
    }

    [Fact]
    public async Task CancellingARenderEndsTheCallAtOnceAndAsksTheEngineToStopBeforeTheNextGeneration()
    {
        RequestEnvelope? held = null;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) =>
        {
            if (r.Payload is RenderTile)
            {
                held = r;
                return [];
            }

            return null;
        });
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;

        using var cancel = new CancellationTokenSource();
        var render = client.RenderTileAsync(document, Tile(), 7, TimeSpan.FromSeconds(30), cancel.Token);
        await WaitUntil(() => held is not null);
        await cancel.CancelAsync();

        Assert.Equal(Status.Cancelled, (await render.WaitAsync(Wait)).Status);
        await WaitUntil(() => fake.Received.Any(r => r.Payload is CancelBefore { Generation: 8 }));

        // The engine still owns the slot until it answers: the ring is one short ...
        var leases = new List<SlotLease>();
        while (fake.Ring.TryRent(out var lease))
        {
            leases.Add(lease!);
        }

        Assert.Equal(fake.Ring.SlotCount - 1, leases.Count);
        leases.ForEach(l => l.Dispose());

        // ... and a late answer to the abandoned request is accepted quietly and frees it.
        await fake.SendAsync(Reply(held!, Status.Cancelled, MessageType.RenderTile));
        await WaitUntil(() => fake.Ring.TryRent(out var l) && Free(l!));
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task AnUnansweredRequestTimesOutIsReportedOverdueAndItsLateAnswerIsHarmless()
    {
        RequestEnvelope? held = null;
        await using var fake = await FakeEngine.StartAsync(script: (_, r) =>
        {
            if (r.Payload is GetDocumentInfo)
            {
                held = r;
                return [];
            }

            return null;
        });
        await using var client = new EngineClient(fake.ClientStream, fake.Ring, new EngineClientOptions { OverdueGrace = TimeSpan.FromMilliseconds(150) });
        var overdue = new TaskCompletionSource<MessageType>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RequestOverdue += type => overdue.TrySetResult(type);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;

        var info = await client.GetDocumentInfoAsync(document, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);
        Assert.Equal(Status.Timeout, info.Status);
        Assert.Equal(MessageType.GetDocumentInfo, await overdue.Task.WaitAsync(Wait));

        await fake.SendAsync(Reply(held!, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply { PageCount = FakeEngine.FakePageCount }));
        Assert.Equal(Status.Ok, (await client.PingAsync(1, Wait)).Status);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task ManyMoreRendersThanSlotsShareTheRing()
    {
        await using var fake = await FakeEngine.StartAsync(slotCount: 2);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;

        var renders = Enumerable.Range(0, 24)
            .Select(async i =>
            {
                var tile = await client.RenderTileAsync(document, new RenderTileArgs(0, 1f, 0, 0, 0, (uint)(8 + i), 8), (ulong)i, Wait);
                using var lease = tile.Value!;
                Assert.Equal(8 + i, lease.Width);
                Assert.Equal((byte)(7 + i), lease.Pixels[(7 * lease.Stride) + ((lease.Width - 1) * 4)]); // the blue channel of the last pixel of the last row is its x
                return tile.Status;
            })
            .ToArray();
        Assert.All(await Task.WhenAll(renders), status => Assert.Equal(Status.Ok, status));
        AssertEverySlotIsFree(fake.Ring);
    }

    private static void AssertEverySlotIsFree(SlotRing ring)
    {
        var leases = new List<SlotLease>();
        while (ring.TryRent(out var lease))
        {
            leases.Add(lease!);
        }

        Assert.Equal(ring.SlotCount, leases.Count);
        leases.ForEach(l => l.Dispose());
    }

    private static bool Free(SlotLease lease)
    {
        lease.Dispose();
        return true;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting for a condition");
            await Task.Delay(20);
        }
    }
}
