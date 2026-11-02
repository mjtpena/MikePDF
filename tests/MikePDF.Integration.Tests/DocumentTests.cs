using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>Documents through the whole stack: supervisor, sandboxed engine, PDFium, slot ring.</summary>
public sealed class DocumentTests(SandboxFixture sandbox)
{
    private static string Fixture(string name) => SandboxFixture.FixturePath(name);

    private static (byte B, byte G, byte R, byte A) Pixel(TileLease tile, int x, int y)
    {
        var at = (y * tile.Stride) + (x * 4);
        var pixels = tile.Pixels;
        return (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
    }

    private static async Task<SupervisedDocument> OpenAsync(EngineSupervisor supervisor, string path, string? password = null)
    {
        var opened = await supervisor.OpenDocumentAsync(path, password);
        Assert.Equal(Status.Ok, opened.Status);
        return opened.Value!;
    }

    [Fact]
    public async Task OpenAnswersWithThePageCountAndInfoAndPageSizesFollow()
    {
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, Fixture("hello_world_2_pages.pdf"));
        Assert.Equal(2u, document.PageCount);

        var info = await document.GetInfoAsync();
        Assert.Equal(Status.Ok, info.Status);
        Assert.Equal(2u, info.Value!.PageCount);
        Assert.Equal(17u, info.Value.PdfVersion);
        Assert.Equal(0xFFFFFFFFu, info.Value.Permissions);
        Assert.False(info.Value.IsTagged);
        Assert.False(info.Value.HasXfa);
        Assert.False(info.Value.HasJavaScript);
        Assert.Equal(0u, info.Value.SignatureCount);

        var sizes = await document.GetPageSizesAsync(0, 2);
        Assert.Equal(Status.Ok, sizes.Status);
        Assert.Equal([200f, 200f], sizes.Value!.Widths);
        Assert.Equal([200f, 200f], sizes.Value.Heights);
        Assert.Equal<ushort[]>([0, 0], sizes.Value.Rotations);

        Assert.Equal(Status.InvalidArgument, (await document.GetPageSizesAsync(1, 2)).Status);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task TheJavaScriptFlagIsReported()
    {
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, Fixture("js.pdf"));
        var info = await document.GetInfoAsync();
        Assert.True(info.Value!.HasJavaScript);
    }

    [Fact]
    public async Task ATileHasTheExpectedPixels()
    {
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, Fixture("rectangles.pdf")); // 200 x 300 pt: black, blue, green and red corner squares

        var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 200, 300), 1);
        Assert.Equal(Status.Ok, result.Status);
        using var tile = result.Value!;
        Assert.Equal((200, 300, 800), (tile.Width, tile.Height, tile.Stride));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixel(tile, 5, 5));          // top left: black
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Pixel(tile, 195, 5));      // top right: blue (BGRA)
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Pixel(tile, 195, 295));    // bottom right: green
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Pixel(tile, 5, 295));      // bottom left: red
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Pixel(tile, 100, 5));  // paper
        var nonWhite = 0;
        for (var i = 0; i < tile.Pixels.Length; i += 4)
        {
            if (tile.Pixels[i] != 255 || tile.Pixels[i + 1] != 255 || tile.Pixels[i + 2] != 255)
            {
                nonWhite++;
            }
        }

        Assert.InRange(nonWhite, 1000, 200 * 300 / 2); // not blank, and mostly paper
    }

    [Fact]
    public async Task TilesAtAnOffsetScaleAndRotationLandWhereTheyShould()
    {
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, Fixture("rectangles.pdf"));

        // 2x: the blue square covers x 380 to 400, y 0 to 20. This 64 x 64 tile starts at (336, 0).
        using (var tile = (await document.RenderTileAsync(new RenderTileArgs(0, 2f, 0, 336, 0, 64, 64), 1)).Value!)
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Pixel(tile, 60, 10));
            Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Pixel(tile, 5, 40));
        }

        // A quarter turn clockwise: the bottom-left (red) square becomes the top-left one.
        using (var tile = (await document.RenderTileAsync(new RenderTileArgs(0, 1f, 1, 0, 0, 300, 200), 2)).Value!)
        {
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), Pixel(tile, 5, 5));
            Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixel(tile, 295, 5));
        }
    }

    [Fact]
    public async Task ManyConcurrentTilesShareTheRingAndKeepTheirOwnPixels()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { SlotCount = 3, SlotSize = 64 * 1024 });
        await using var document = await OpenAsync(supervisor, Fixture("rectangles.pdf"));

        var tasks = Enumerable.Range(0, 30)
            .Select(async i =>
            {
                // Alternate the top-left (black) and the top-right (blue) 16 x 16 corner.
                var right = i % 2 == 1;
                var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, right ? 184 : 0, 0, 16, 16), (ulong)i + 1);
                Assert.Equal(Status.Ok, result.Status);
                using var tile = result.Value!;
                return (right, pixel: Pixel(tile, 8, 8));
            })
            .ToArray();
        foreach (var (right, pixel) in await Task.WhenAll(tasks))
        {
            Assert.Equal(right ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255), pixel);
        }
    }

    [Fact]
    public async Task AnEncryptedDocumentAsksForAPasswordAndAcceptsTheRightOne()
    {
        await using var supervisor = await sandbox.StartAsync();
        var path = Fixture("encrypted.pdf");

        Assert.Equal(Status.PasswordRequired, (await supervisor.OpenDocumentAsync(path)).Status);
        Assert.Equal(Status.PasswordIncorrect, (await supervisor.OpenDocumentAsync(path, "not the password")).Status);
        await using var document = await OpenAsync(supervisor, path, "5678");
        Assert.True((await document.GetInfoAsync()).IsOk);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task ADamagedFileIsReportedAsDamagedAndTheEngineCarriesOn()
    {
        using var garbage = new TempFile("garbage.pdf", "this is not a pdf, though it is a file"u8.ToArray());
        var truncated = File.ReadAllBytes(Fixture("hello_world.pdf"));
        using var cut = new TempFile("cut.pdf", truncated[..(truncated.Length / 3)]);
        using var empty = new TempFile("empty.pdf", []);
        await using var supervisor = await sandbox.StartAsync();
        var engine = supervisor.EngineProcessId;

        Assert.Equal(Status.Damaged, (await supervisor.OpenDocumentAsync(garbage.Path)).Status);
        Assert.Equal(Status.Damaged, (await supervisor.OpenDocumentAsync(cut.Path)).Status);
        Assert.Equal(Status.Damaged, (await supervisor.OpenDocumentAsync(empty.Path)).Status);

        await using var document = await OpenAsync(supervisor, Fixture("hello_world.pdf"));
        Assert.True((await document.GetInfoAsync()).IsOk);
        Assert.Equal(engine, supervisor.EngineProcessId);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task AMissingFileIsAnInvalidArgumentAndAFileHeldOpenForWritingIsPermissionDenied()
    {
        await using var supervisor = await sandbox.StartAsync();
        Assert.Equal(Status.InvalidArgument, (await supervisor.OpenDocumentAsync(Path.Combine(Path.GetTempPath(), "mikepdf-no-such-file.pdf"))).Status);

        var path = TestPdf.WriteTemp("locked.pdf", File.ReadAllBytes(Fixture("hello_world.pdf")));
        try
        {
            await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            Assert.Equal(Status.PermissionDenied, (await supervisor.OpenDocumentAsync(path)).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheBrokerHoldsTheFileDenyWriteForAsLongAsTheDocumentIsOpen()
    {
        var path = TestPdf.WriteTemp("held.pdf", File.ReadAllBytes(Fixture("hello_world.pdf")));
        try
        {
            await using var supervisor = await sandbox.StartAsync();
            var document = await OpenAsync(supervisor, path);
            Assert.Throws<IOException>(() => File.OpenWrite(path));
            await document.DisposeAsync();
            using var writer = File.OpenWrite(path); // released again
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ADocumentEngineHoldsOneDocumentAtATimeAndAClosedDocumentIsGone()
    {
        await using var supervisor = await sandbox.StartAsync();
        var first = await OpenAsync(supervisor, Fixture("hello_world.pdf"));
        Assert.Equal(Status.Unsupported, (await supervisor.OpenDocumentAsync(Fixture("rectangles.pdf"))).Status);

        await first.DisposeAsync();
        Assert.Equal(Status.InvalidArgument, (await first.GetInfoAsync()).Status);
        await using var second = await OpenAsync(supervisor, Fixture("rectangles.pdf"));
        Assert.True((await second.GetInfoAsync()).IsOk);
    }

    [Fact]
    public async Task APoolEngineHoldsSeveralDocuments()
    {
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Pool });
        await using var one = await OpenAsync(supervisor, Fixture("hello_world.pdf"));
        await using var two = await OpenAsync(supervisor, Fixture("rectangles.pdf"));
        await using var three = await OpenAsync(supervisor, Fixture("js.pdf"));
        Assert.True((await one.GetInfoAsync()).IsOk);
        Assert.True((await three.GetInfoAsync()).Value!.HasJavaScript);
        using var tile = (await two.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 16, 16), 1)).Value!;
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixel(tile, 5, 5));
    }
}
