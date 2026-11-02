using System.Text;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// The reading services through the whole stack: broker, sandboxed engine, PDFium, schema-3 codecs. What the engine says is checked twice (the
/// client's semantic checks, then these assertions), and a restart of the engine in the middle changes nothing for the caller.
/// </summary>
public sealed class ReadingServiceTests(SandboxFixture sandbox)
{
    private static async Task<SupervisedDocument> OpenAsync(EngineSupervisor supervisor, string path)
    {
        var opened = await supervisor.OpenDocumentAsync(path);
        Assert.Equal(Status.Ok, opened.Status);
        return opened.Value!;
    }

    private static string Letters(GetPageTextReply reply)
    {
        var text = new StringBuilder();
        for (var i = 0; i < reply.Units.Length; i++)
        {
            if ((reply.Flags[i] & 1) == 0)
            {
                text.Append((char)reply.Units[i]);
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task TheOutlineIsReadALevelAtATimeAndChildrenAreAddressedByPosition()
    {
        using var file = new TempFile("reader.pdf", ReaderPdf.Build());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var top = await document.GetOutlineAsync([], 0, 64);
        Assert.Equal(Status.Ok, top.Status);
        Assert.Equal(3u, top.Value!.Total);
        Assert.Equal(["Chapter One", "Chapter Two", "Web"], top.Value.Titles);
        Assert.Equal<ushort[]>([3, 1, 4], top.Value.Kinds);
        Assert.Equal<ushort[]>([1, 0, 0], top.Value.HasChildren);
        Assert.Equal(72f, top.Value.Xs[0]);
        Assert.Equal(700f, top.Value.Ys[0]);

        var children = await document.GetOutlineAsync([0], 0, 64);
        Assert.Equal(["Section 1.1", "Section 1.2"], children.Value!.Titles);
        Assert.Equal<uint[]>([1, 2], children.Value.Pages);

        Assert.Equal(Status.InvalidArgument, (await document.GetOutlineAsync([9], 0, 1)).Status);
        Assert.Equal(Status.InvalidArgument, (await document.GetOutlineAsync([], 0, 0)).Status); // refused by the encoder, never sent
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public async Task LinksComeWithTheirRectanglesAndAnExternalAddressIsAskedForOnItsOwn()
    {
        using var file = new TempFile("reader.pdf", ReaderPdf.Build());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var links = await document.GetPageLinksAsync(0, 0, 512);
        Assert.Equal(Status.Ok, links.Status);
        Assert.Equal(5u, links.Value!.Total);
        Assert.Equal<ushort[]>([3, 4, 2, 5, 1], links.Value.Kinds);
        Assert.Equal(72f, links.Value.Lefts[0]);
        Assert.Equal(712f, links.Value.Tops[0]);

        var address = await document.GetLinkUriAsync(0, 1);
        Assert.Equal(Status.Ok, address.Status);
        Assert.Equal("https://example.org/a%20b", address.Value!.Uri);
        Assert.Equal(Status.InvalidArgument, (await document.GetLinkUriAsync(0, 0)).Status); // internal
        Assert.Equal(Status.InvalidArgument, (await document.GetLinkUriAsync(0, 3)).Status); // a launch action: never offered
        Assert.Equal(Status.InvalidArgument, (await document.GetLinkUriAsync(5, 0)).Status); // refused locally: no such page
    }

    [Fact]
    public async Task PageBoxesAndTextAreReadInUserSpace()
    {
        using var file = new TempFile("reader.pdf", ReaderPdf.Build());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var boxes = await document.GetPageBoxesAsync(2);
        Assert.Equal(Status.Ok, boxes.Status);
        Assert.True(boxes.Value!.HasCropBox);
        Assert.Equal((100f, 150f, 500f, 650f), (boxes.Value.CropLeft, boxes.Value.CropBottom, boxes.Value.CropRight, boxes.Value.CropTop));
        Assert.Equal(1, boxes.Value.Rotation);

        var text = await document.GetPageTextAsync(0, 0, 2048);
        Assert.Equal(Status.Ok, text.Status);
        Assert.Contains(ReaderPdf.FirstLine, Letters(text.Value!), StringComparison.Ordinal);
        Assert.Contains(ReaderPdf.SecondLine, Letters(text.Value!), StringComparison.Ordinal);
        Assert.Equal(text.Value!.Total, (uint)text.Value.Units.Length);
        Assert.Equal((ushort)'H', text.Value.Units[0]);
        Assert.InRange(text.Value.Lefts[0], 71f, 73f);
    }

    [Fact]
    public async Task SearchReportsMatchesInTheNumberingOfPageText()
    {
        using var file = new TempFile("reader.pdf", ReaderPdf.Build());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var found = await document.SearchPageAsync(0, "world", 0);
        Assert.Equal(Status.Ok, found.Status);
        Assert.Equal<uint[]>([6], found.Value!.Starts);
        Assert.Equal<uint[]>([5], found.Value.Lengths);
        Assert.Empty((await document.SearchPageAsync(0, "world", 1)).Value!.Starts);                // match case
        Assert.Empty((await document.SearchPageAsync(0, "no such phrase", 0)).Value!.Starts);
        Assert.Equal(Status.InvalidArgument, (await document.SearchPageAsync(0, string.Empty, 0)).Status);
        Assert.Equal(Status.InvalidArgument, (await document.SearchPageAsync(0, new string('q', 513), 0)).Status);
    }

    [Fact]
    public async Task AThousandsCharacterPageIsReadInWindowsThatAddUpToTheWhole()
    {
        using var file = new TempFile("long.pdf", ReaderPdf.LongText(5000));
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);

        var first = await document.GetPageTextAsync(0, 0, 2048);
        Assert.Equal(5000u, first.Value!.Total);
        Assert.Equal(2048, first.Value.Units.Length);
        var second = await document.GetPageTextAsync(0, 2048, 2048);
        var third = await document.GetPageTextAsync(0, 4096, 2048);
        Assert.Equal(2048, second.Value!.Units.Length);
        Assert.Equal(904, third.Value!.Units.Length);
        Assert.True(first.Value.Units.Concat(second.Value.Units).Concat(third.Value.Units).All(u => u == 'a'));
        Assert.Equal(Status.InvalidArgument, (await document.GetPageTextAsync(0, 5001, 10)).Status);
    }

    [Fact]
    public async Task TheServicesKeepWorkingAfterTheEngineIsKilled()
    {
        using var file = new TempFile("reader.pdf", ReaderPdf.Build());
        await using var supervisor = await sandbox.StartAsync();
        await using var document = await OpenAsync(supervisor, file.Path);
        var before = await document.GetPageTextAsync(0, 0, 64);
        Assert.Equal(Status.Ok, before.Status);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Restored += (_, _) => restored.TrySetResult();
        supervisor.CurrentProcess!.Kill();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var after = await document.GetPageTextAsync(0, 0, 64);
        Assert.Equal(Status.Ok, after.Status);
        Assert.Equal(before.Value!.Units, after.Value!.Units);
        Assert.Equal(Status.Ok, (await document.GetOutlineAsync([], 0, 8)).Status);
        Assert.Equal(1, supervisor.RestartCount);
    }

    [Fact]
    public async Task ThePoolEngineServesTheReadingServicesToo()
    {
        // Thumbnails and search run in the pool engine, which holds several documents.
        await using var supervisor = await sandbox.StartAsync(o => o with { Kind = EngineKind.Pool });
        await using var document = await OpenAsync(supervisor, SandboxFixture.FixturePath("hello_world.pdf"));
        Assert.Equal(Status.Ok, (await document.GetPageTextAsync(0, 0, 8)).Status);
        Assert.Equal(Status.InvalidArgument, (await document.GetPageTextAsync(1, 0, 8)).Status);
        Assert.Equal(Status.Ok, (await document.GetPageBoxesAsync(0)).Status);
        Assert.Equal(0, supervisor.RestartCount);
    }

    [Fact]
    public void TheReplyChecksAcceptWhatAnHonestEngineSendsAndRefuseTheRest()
    {
        Assert.True(ReplyChecks.WindowFits(0, 64, 3, 3, allowStartPastEnd: true));
        Assert.True(ReplyChecks.WindowFits(2, 64, 3, 1, allowStartPastEnd: true));
        Assert.True(ReplyChecks.WindowFits(0, 2, 3, 2, allowStartPastEnd: true));
        Assert.True(ReplyChecks.WindowFits(9, 2, 3, 0, allowStartPastEnd: true));
        Assert.False(ReplyChecks.WindowFits(9, 2, 3, 0, allowStartPastEnd: false));
        Assert.True(ReplyChecks.WindowFits(3, 2, 3, 0, allowStartPastEnd: false));
        Assert.False(ReplyChecks.WindowFits(0, 2, 3, 3, allowStartPastEnd: true));
        Assert.False(ReplyChecks.WindowFits(0, 5, 3, 2, allowStartPastEnd: true));

        Assert.Null(ReplyChecks.CheckDestinations([0, 1, 4, 5], [0, 2, 0, 0], 3));
        Assert.NotNull(ReplyChecks.CheckDestinations([1], [3], 3));
        Assert.NotNull(ReplyChecks.CheckDestinations([5], [1], 3));
        Assert.Null(ReplyChecks.CheckMatches([0, 5, 9], [5, 4, 1]));
        Assert.NotNull(ReplyChecks.CheckMatches([5, 5], [1, 1]));
        Assert.NotNull(ReplyChecks.CheckMatches([199_999], [2]));

        Assert.True(ReplyChecks.IsPlainAddress("https://example.org/a%20b?q=ü"));
        foreach (var bad in new[] { "", "a b", "a" + (char)9 + "b", "a" + (char)0x85 + "b", "a" + (char)0x200B + "b", "a" + (char)0x202E + "b", "a" + (char)0x2028 + "b", "a" + (char)0xE000 + "b" })
        {
            Assert.False(ReplyChecks.IsPlainAddress(bad), bad);
        }
    }
}
