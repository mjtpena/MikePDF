using MikePDF.Core.Geometry;
using MikePDF.Core.Reading;
using MikePDF.Core.Text;
using MikePDF.Protocol;
using static MikePDF.Core.Tests.ScriptedEngine;

namespace MikePDF.Core.Tests;

public sealed class DocumentReaderTests
{
    private const int Pages = 40;

    private static GetOutlineReply OutlineWindow(uint total, uint first, uint count, Func<int, string>? title = null, ushort kind = 1, uint page = 3)
    {
        var n = (int)(first >= total ? 0 : Math.Min(count, total - first));
        return new GetOutlineReply
        {
            Total = total,
            Titles = [.. Enumerable.Range((int)first, n).Select(i => title?.Invoke(i) ?? "Entry " + i)],
            Kinds = [.. Enumerable.Repeat(kind, n)],
            Pages = [.. Enumerable.Repeat(page, n)],
            Xs = new float[n],
            Ys = [.. Enumerable.Repeat(100f, n)],
            HasChildren = [.. Enumerable.Range(0, n).Select(i => (ushort)(i % 2))],
        };
    }

    private static GetPageLinksReply LinkWindow(uint total, uint first, uint count, ushort kind = 3, uint page = 5)
    {
        var n = (int)(first >= total ? 0 : Math.Min(count, total - first));
        return new GetPageLinksReply
        {
            Total = total,
            Lefts = [.. Enumerable.Range(0, n).Select(i => (float)i)],
            Bottoms = new float[n],
            Rights = [.. Enumerable.Range(0, n).Select(i => i + 10f)],
            Tops = [.. Enumerable.Repeat(10f, n)],
            Kinds = [.. Enumerable.Repeat(kind, n)],
            Pages = [.. Enumerable.Repeat(page, n)],
            Xs = [.. Enumerable.Repeat(20f, n)],
            Ys = [.. Enumerable.Repeat(30f, n)],
        };
    }

    // ---- outline -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOutlineLevelIsReadWindowByWindowAndTitlesAreCleaned()
    {
        var engine = new ScriptedEngine
        {
            Outline = (path, first, count) => Ok(OutlineWindow(150, first, count, i => i == 0 ? "Intro\n\t  to\u0007 it" : "Entry " + i)),
        };
        var reader = new DocumentReader(engine, Pages);
        var level = await reader.GetOutlineLevelAsync([], CancellationToken.None);
        Assert.True(level.IsOk);
        Assert.Equal(150, level.Value!.Total);
        Assert.Equal(150, level.Value.Entries.Count);
        Assert.Equal(3, engine.Calls); // 64 + 64 + 22
        Assert.Equal("Intro to it", level.Value.Entries[0].Title);
        Assert.Equal(new Destination(DestinationKind.Page, 3, 0, 100), level.Value.Entries[0].Destination);
        Assert.True(level.Value.Entries[1].HasChildren);
        Assert.False(level.Value.Entries[0].HasChildren);
    }

    [Fact]
    public async Task AVeryLongLevelIsCutAtTheLimit()
    {
        var reader = new DocumentReader(new ScriptedEngine { Outline = (_, first, count) => Ok(OutlineWindow(50_000, first, count)) }, Pages);
        var level = await reader.GetOutlineLevelAsync([2, 1], CancellationToken.None);
        Assert.Equal(DocumentReader.MaxOutlineEntriesPerLevel, level.Value!.Entries.Count);
        Assert.Equal(50_000, level.Value.Total); // the person is told there are more than are shown
    }

    [Fact]
    public async Task APathAndAWindowOutsideTheLimitsAreRefusedWithoutAsking()
    {
        var engine = new ScriptedEngine();
        var reader = new DocumentReader(engine, Pages);
        Assert.Equal(Status.InvalidArgument, (await reader.GetOutlineWindowAsync(new int[17], 0, 1, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetOutlineWindowAsync([-1], 0, 1, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetOutlineWindowAsync([], -1, 1, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetOutlineWindowAsync([], 0, 0, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetOutlineWindowAsync([], 0, 65, CancellationToken.None)).Status);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task AnOutlineThatDoesNotAddUpIsDamagedNotTrusted()
    {
        var wrongWindow = new DocumentReader(new ScriptedEngine { Outline = (_, first, _) => Ok(OutlineWindow(10, first, 3)) }, Pages);
        Assert.Equal(Status.Damaged, (await wrongWindow.GetOutlineWindowAsync([], 0, 5, CancellationToken.None)).Status);

        var pageBeyondTheDocument = new DocumentReader(new ScriptedEngine { Outline = (_, first, count) => Ok(OutlineWindow(3, first, count, page: Pages)) }, Pages);
        Assert.Equal(Status.Damaged, (await pageBeyondTheDocument.GetOutlineWindowAsync([], 0, 5, CancellationToken.None)).Status);

        var unknownKind = new DocumentReader(new ScriptedEngine { Outline = (_, first, count) => Ok(OutlineWindow(3, first, count, kind: 9)) }, Pages);
        Assert.Equal(Status.Damaged, (await unknownKind.GetOutlineWindowAsync([], 0, 5, CancellationToken.None)).Status);

        var ragged = new DocumentReader(new ScriptedEngine
        {
            Outline = (_, first, count) =>
            {
                var window = OutlineWindow(3, first, count);
                window.Xs = [];
                return Ok(window);
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await ragged.GetOutlineWindowAsync([], 0, 5, CancellationToken.None)).Status);

        var shifting = 0u;
        var changing = new DocumentReader(new ScriptedEngine { Outline = (_, first, count) => Ok(OutlineWindow(100 + shifting++, first, count)) }, Pages);
        Assert.Equal(Status.Damaged, (await changing.GetOutlineLevelAsync([], CancellationToken.None)).Status);
    }

    [Fact]
    public async Task AnEnginesFailureStatusPassesThrough()
    {
        var reader = new DocumentReader(new ScriptedEngine { Outline = (_, _, _) => ReadReply.Fail<GetOutlineReply>(Status.EngineCrashed, "gone") }, Pages);
        var reply = await reader.GetOutlineLevelAsync([], CancellationToken.None);
        Assert.Equal(Status.EngineCrashed, reply.Status);
        Assert.Equal("gone", reply.Detail);
        Assert.False(reply.IsOk);
    }

    [Fact]
    public async Task AnEmptyLevelIsFineAndEndsTheLoop()
    {
        var engine = new ScriptedEngine { Outline = (_, first, count) => Ok(OutlineWindow(0, first, count)) };
        var level = await new DocumentReader(engine, Pages).GetOutlineLevelAsync([], CancellationToken.None);
        Assert.Empty(level.Value!.Entries);
        Assert.Equal(1, engine.Calls);
    }

    // ---- links ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LinksAreReadInWindowsAndNumberedInTheEnginesOrder()
    {
        var engine = new ScriptedEngine { Links = (_, first, count) => Ok(LinkWindow(700, first, count)) };
        var links = await new DocumentReader(engine, Pages).GetLinksAsync(2, CancellationToken.None);
        Assert.Equal(700, links.Value!.Links.Count);
        Assert.False(links.Value.Truncated);
        Assert.Equal(2, engine.Calls);
        Assert.Equal(Enumerable.Range(0, 700), links.Value.Links.Select(l => l.Index));
        Assert.Equal(new PdfRect(3, 0, 13, 10), links.Value.Links[3].Rect);
        Assert.Equal(new Destination(DestinationKind.PageXY, 5, 20, 30), links.Value.Links[3].Destination);
    }

    [Fact]
    public async Task APageWithManyMoreLinksThanWeKeepIsCutAndSaysSo()
    {
        var engine = new ScriptedEngine { Links = (_, first, count) => Ok(LinkWindow(100_000, first, count)) };
        var links = await new DocumentReader(engine, Pages).GetLinksAsync(0, CancellationToken.None);
        Assert.Equal(DocumentReader.MaxLinksPerPage, links.Value!.Links.Count);
        Assert.True(links.Value.Truncated);
    }

    [Fact]
    public async Task LinkWindowsThatDisagreeOrPointOutsideTheDocumentAreDamaged()
    {
        var shifting = 0u;
        var changing = new DocumentReader(new ScriptedEngine { Links = (_, first, count) => Ok(LinkWindow(700 + shifting++, first, count)) }, Pages);
        Assert.Equal(Status.Damaged, (await changing.GetLinksAsync(0, CancellationToken.None)).Status);

        var outside = new DocumentReader(new ScriptedEngine { Links = (_, first, count) => Ok(LinkWindow(3, first, count, page: 99)) }, Pages);
        Assert.Equal(Status.Damaged, (await outside.GetLinksAsync(0, CancellationToken.None)).Status);

        var ragged = new DocumentReader(new ScriptedEngine
        {
            Links = (_, first, count) =>
            {
                var window = LinkWindow(3, first, count);
                window.Tops = [1f];
                return Ok(window);
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await ragged.GetLinksAsync(0, CancellationToken.None)).Status);

        var reader = new DocumentReader(new ScriptedEngine(), Pages);
        Assert.Equal(Status.InvalidArgument, (await reader.GetLinksAsync(Pages, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetLinksAsync(-1, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ThePlainLinkKindsKeepTheirMeaning()
    {
        foreach (var (kind, expected) in new (ushort, DestinationKind)[] { (0, DestinationKind.None), (1, DestinationKind.Page), (2, DestinationKind.PageY), (4, DestinationKind.External), (5, DestinationKind.Other) })
        {
            var reader = new DocumentReader(new ScriptedEngine { Links = (_, first, count) => Ok(LinkWindow(1, first, count, kind, kind is 1 or 2 ? 4u : 0u)) }, Pages);
            var link = (await reader.GetLinksAsync(0, CancellationToken.None)).Value!.Links[0];
            Assert.Equal(expected, link.Destination.Kind);
            Assert.Equal(expected is DestinationKind.Page or DestinationKind.PageY, link.Destination.IsInternal);
        }
    }

    [Fact]
    public async Task AnAddressIsAskedForOnItsOwnAndSorted()
    {
        var reader = new DocumentReader(new ScriptedEngine { Address = (_, _) => Ok(new GetLinkUriReply { Uri = "https://example.org/x" }) }, Pages);
        var address = await reader.GetAddressAsync(1, 4, CancellationToken.None);
        Assert.True(address.Value!.IsWebOrMail);
        Assert.Equal("example.org", address.Value.Host);
        Assert.Equal(Status.InvalidArgument, (await reader.GetAddressAsync(1, -1, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await reader.GetAddressAsync(Pages, 0, CancellationToken.None)).Status);
        var refused = new DocumentReader(new ScriptedEngine { Address = (_, _) => ReadReply.Fail<GetLinkUriReply>(Status.Unsupported) }, Pages);
        Assert.Equal(Status.Unsupported, (await refused.GetAddressAsync(1, 4, CancellationToken.None)).Status);
    }

    // ---- geometry ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PageBoxesBecomeAGeometry()
    {
        var reader = new DocumentReader(new ScriptedEngine
        {
            Boxes = _ => Ok(new GetPageBoxesReply
            {
                MediaRight = 612, MediaTop = 792, HasCropBox = true, CropLeft = 100, CropBottom = 150, CropRight = 500, CropTop = 650, Rotation = 1,
            }),
        }, Pages);
        var geometry = (await reader.GetGeometryAsync(0, CancellationToken.None)).Value!;
        Assert.Equal(1, geometry.RotateTurns);
        Assert.Equal(new SizeD(500, 400), geometry.Size);
    }

    [Fact]
    public async Task APageWithNoUsableBoxesIsTreatedAsLetter()
    {
        var reader = new DocumentReader(new ScriptedEngine { Boxes = _ => Ok(new GetPageBoxesReply()) }, Pages);
        Assert.Equal(new SizeD(612, 792), (await reader.GetGeometryAsync(0, CancellationToken.None)).Value!.Size);
        Assert.Equal(Status.InvalidArgument, (await reader.GetGeometryAsync(99, CancellationToken.None)).Status);
    }

    // ---- characters ----------------------------------------------------------------------------------------------------------

    private static PageCharacters Long(int n) => TextFixtures.Chars(new TextFixtures.Run(new string('a', n), 0, 72));

    [Fact]
    public async Task TheCharactersOfAPageAreAssembledFromWindows()
    {
        var source = Long(5000);
        var engine = new ScriptedEngine { Text = (_, first, count) => TextWindow(source, first, count) };
        var characters = (await new DocumentReader(engine, Pages).GetCharactersAsync(0, CancellationToken.None)).Value!;
        Assert.Equal(3, engine.Calls);
        Assert.Equal(5000, characters.Count);
        Assert.Equal(source.Units, characters.Units);
        Assert.Equal(source.Lefts, characters.Lefts);
        Assert.Equal(source.Tops, characters.Tops);
        Assert.Equal(new string('a', 5000), characters.PlainText());
    }

    [Fact]
    public async Task APageWithNoTextHasNoCharacters()
    {
        var engine = new ScriptedEngine { Text = (_, first, count) => TextWindow(PageCharacters.Empty, first, count) };
        var characters = (await new DocumentReader(engine, Pages).GetCharactersAsync(0, CancellationToken.None)).Value!;
        Assert.Equal(0, characters.Count);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task TextWindowsThatDisagreeAreDamaged()
    {
        var source = Long(5000);
        var calls = 0;
        var changing = new DocumentReader(new ScriptedEngine
        {
            Text = (_, first, count) =>
            {
                var reply = TextWindow(source, first, count);
                if (++calls == 2)
                {
                    reply.Value!.Total = 4000;
                }

                return reply;
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await changing.GetCharactersAsync(0, CancellationToken.None)).Status);

        var truncatedLate = 0;
        var flipping = new DocumentReader(new ScriptedEngine
        {
            Text = (_, first, count) =>
            {
                var reply = TextWindow(source, first, count);
                reply.Value!.Truncated = truncatedLate++ > 0;
                return reply;
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await flipping.GetCharactersAsync(0, CancellationToken.None)).Status);

        var short_ = new DocumentReader(new ScriptedEngine
        {
            Text = (_, first, count) =>
            {
                var reply = TextWindow(source, first, count);
                reply.Value!.Flags = [];
                return reply;
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await short_.GetCharactersAsync(0, CancellationToken.None)).Status);

        var huge = new DocumentReader(new ScriptedEngine
        {
            Text = (_, first, count) =>
            {
                var reply = TextWindow(source, first, count);
                reply.Value!.Total = 200_001;
                return reply;
            },
        }, Pages);
        Assert.Equal(Status.Damaged, (await huge.GetCharactersAsync(0, CancellationToken.None)).Status);

        var empty = new DocumentReader(new ScriptedEngine { Text = (_, _, _) => Ok(new GetPageTextReply { Total = 10 }) }, Pages);
        Assert.Equal(Status.Damaged, (await empty.GetCharactersAsync(0, CancellationToken.None)).Status);
        Assert.Equal(Status.InvalidArgument, (await empty.GetCharactersAsync(Pages, CancellationToken.None)).Status);
    }

    // ---- search --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SearchReturnsTheMatchesAndRefusesQueriesTheEngineCouldNotTake()
    {
        string? asked = null;
        uint flagsAsked = 99;
        var engine = new ScriptedEngine
        {
            Search = (_, query, flags, _) =>
            {
                asked = query;
                flagsAsked = flags;
                return Task.FromResult(Ok(new SearchPageReply { Starts = [3, 10], Lengths = [2, 2], Truncated = true }));
            },
        };
        var reader = new DocumentReader(engine, Pages);
        var found = (await reader.SearchAsync(4, new SearchOptions("na\u00EFve", MatchCase: true, WholeWord: true), CancellationToken.None)).Value!;
        Assert.Equal([new PageMatch(3, 2), new PageMatch(10, 2)], found.Matches);
        Assert.True(found.Truncated);
        Assert.Equal("na\u00EFve", asked);
        Assert.Equal(3u, flagsAsked);

        foreach (var bad in new[] { "", "   ", "a\0b", new string('x', 513), new string('\u00E9', 257), "lone" + (char)0xD800 })
        {
            Assert.Equal(Status.InvalidArgument, (await reader.SearchAsync(0, new SearchOptions(bad), CancellationToken.None)).Status);
        }

        Assert.Equal(Status.InvalidArgument, (await reader.SearchAsync(Pages, new SearchOptions("x"), CancellationToken.None)).Status);
        Assert.True((await reader.SearchAsync(0, new SearchOptions(new string('x', 512)), CancellationToken.None)).IsOk);
    }

    [Fact]
    public async Task MatchesOutOfOrderOrBeyondAnyPageAreDamaged()
    {
        foreach (var (starts, lengths) in new (uint[], uint[])[] { ([5, 5], [1, 1]), ([9, 2], [1, 1]), ([199_999], [2]), ([1], [1, 2]) })
        {
            var reader = new DocumentReader(new ScriptedEngine
            {
                Search = (_, _, _, _) => Task.FromResult(Ok(new SearchPageReply { Starts = starts, Lengths = lengths })),
            }, Pages);
            Assert.Equal(Status.Damaged, (await reader.SearchAsync(0, new SearchOptions("x"), CancellationToken.None)).Status);
        }
    }

    // ---- display text and addresses ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Plain title", "Plain title")]
    [InlineData("  spaced   out \t\n title  ", "spaced out title")]
    [InlineData("a\u202Eb", "ab")]            // right-to-left override
    [InlineData("a\u200Bb\uFEFFc", "abc")]    // zero width space and byte order mark
    [InlineData("a\u0007b", "a b")]
    [InlineData("\u2028\u2029", "")]
    [InlineData("caf\u00E9 \u2713", "caf\u00E9 \u2713")]
    public void DisplayTextIsFitToShow(string input, string expected) => Assert.Equal(expected, DisplayText.Clean(input));

    [Theory]
    [InlineData("https://example.org/path?q=1", true, "https", "example.org")]
    [InlineData("HTTP://Example.ORG", true, "http", "example.org")]
    [InlineData("mailto:someone@example.org", true, "mailto", "")]
    [InlineData("javascript:alert(1)", false, "javascript", "")]
    [InlineData("file:///C:/Windows/System32/calc.exe", false, "file", "")]
    [InlineData("ms-settings:privacy", false, "ms-settings", "")]
    [InlineData("calc.exe", false, "", "")]
    [InlineData("https://", false, "", "")]
    [InlineData("", false, "", "")]
    public void OnlyWebAndMailAddressesAreDescribedAsSuch(string text, bool canOpen, string scheme, string host)
    {
        var address = ExternalAddress.Classify(text);
        Assert.Equal(canOpen, address.IsWebOrMail);
        Assert.Equal(scheme, address.Scheme);
        Assert.Equal(host, address.Host);
        Assert.Equal(text, address.Text);
    }

    [Fact]
    public void AnInternationalHostIsAlsoShownInItsAsciiForm()
    {
        var address = ExternalAddress.Classify("https://b\u00FCcher.example/");
        Assert.True(address.IsWebOrMail);
        Assert.Equal("xn--bcher-kva.example", address.AsciiHost);
        Assert.Null(ExternalAddress.Classify("https://example.org/").AsciiHost);
        Assert.False(ExternalAddress.Classify("https://example.org/" + new string('a', 2100)).IsWebOrMail);
    }
}
