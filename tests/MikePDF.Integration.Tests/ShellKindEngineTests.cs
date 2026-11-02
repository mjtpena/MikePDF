using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>ADR-0018: the real engine started with <c>--kind shell</c> holds one document and answers only the messages a shell host needs.</summary>
public sealed class ShellKindEngineTests(SandboxFixture sandbox)
{
    private static readonly string Rectangles = SandboxFixture.FixturePath("rectangles.pdf");

    [Fact]
    public async Task AShellEngineOpensOneDocumentAndRendersOneTile()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Shell, SlotCount = 2, SlotSize = 4 << 20 });
        var opened = await supervisor.OpenDocumentAsync(Rectangles);
        Assert.True(opened.IsOk, opened.Detail);
        var document = opened.Value!;
        Assert.Equal(1u, document.PageCount);

        var sizes = await document.GetPageSizesAsync(0, 1);
        Assert.True(sizes.IsOk);
        var tile = await document.RenderTileAsync(new RenderTileArgs(0, 0.5f, 0, 0, 0, 100, 150), 1, TimeSpan.FromSeconds(10));
        Assert.True(tile.IsOk, tile.Detail);
        Assert.Equal(100, tile.Value!.Width);
        tile.Value.Dispose();
    }

    [Fact]
    public async Task AShellEngineRefusesASecondDocument()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Shell, SlotCount = 2, SlotSize = 4 << 20 });
        var first = await supervisor.OpenDocumentAsync(Rectangles);
        Assert.True(first.IsOk, first.Detail);
        var second = await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("hello_world.pdf"));
        Assert.Equal(Status.Unsupported, second.Status);
        // The first document is untouched.
        Assert.True((await first.Value!.GetInfoAsync()).IsOk);
    }

    [Fact]
    public async Task AShellEngineAnswersUnsupportedToEverythingOffItsAllowList()
    {
        // Frame by frame: the broker's client (rightly) treats Unsupported to a reading message as a protocol violation, and ShellHost never sends one. What matters is what the engine says.
        await using var engine = await RawEngine.StartAsync(sandbox, slotCount: 2, slotSize: 4 << 20, kind: "shell");
        await engine.HelloAsync();
        Assert.Equal(Status.Ok, (await engine.AttachRingAsync()).Status);
        var id = await engine.OpenAsync(Rectangles);

        IRequestMessage[] offTheList =
        [
            new GetOutline { Path = [], First = 0, Count = 10 },
            new GetPageLinks { Page = 0, First = 0, Count = 10 },
            new GetLinkUri { Page = 0, Index = 0 },
            new GetPageBoxes { Page = 0 },
            new GetPageText { Page = 0, First = 0, Count = 10 },
            new SearchPage { Page = 0, Query = "a", Flags = 0 },
            new GetDocumentDetails(),
            new GetViewerPreferences(),
            new RenderBand { Page = 0, Dpi = 72, Rotation = 0, PageWidthPx = 100, PageHeightPx = 100, BandTop = 0, BandHeight = 10, Flags = 1, SlotIndex = 0, Generation = 1 },
        ];
        foreach (var message in offTheList)
        {
            var reply = await engine.CallAsync(id, message);
            Assert.Equal(Status.Unsupported, reply.Status);
        }

        // And the engine is still alive and serving what it should.
        Assert.Equal(Status.Ok, (await engine.CallAsync(id, new GetDocumentInfo())).Status);
        Assert.False(engine.Process.HasExited);
    }
}
