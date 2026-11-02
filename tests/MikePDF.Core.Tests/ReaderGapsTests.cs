using System.Diagnostics;
using MikePDF.Core.Colour;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Navigation;
using MikePDF.Core.Persistence;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Core.Text;
using MikePDF.Protocol;

namespace MikePDF.Core.Tests;

/// <summary>Back and forward (docs/features/m2-reader-gaps.md, H1 to H6).</summary>
public sealed class NavigationHistoryTests
{
    private static ViewLocation At(int page, double fraction = 0) => new(page, fraction);

    [Fact]
    public void H1_ABackReturnsTheLocationBeforeTheJumpAndForwardReturnsWhereBackLeft()
    {
        var history = new NavigationHistory();
        Assert.True(history.Record(At(2)));                  // jumped away from page 2
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);

        Assert.Equal(At(2), history.Back(At(7)));            // now at page 7
        Assert.False(history.CanGoBack);
        Assert.True(history.CanGoForward);
        Assert.Equal(At(7), history.Forward(At(2)));
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void H1_SeveralJumpsComeBackInReverseOrder()
    {
        var history = new NavigationHistory();
        history.Record(At(0));
        history.Record(At(5, 0.5));
        history.Record(At(9));
        Assert.Equal(At(9), history.Back(At(12)));
        Assert.Equal(At(5, 0.5), history.Back(At(9)));
        Assert.Equal(At(0), history.Back(At(5, 0.5)));
        Assert.Null(history.Back(At(0)));
        Assert.Equal(At(5, 0.5), history.Forward(At(0)));
        Assert.Equal(At(9), history.Forward(At(5, 0.5)));
        Assert.Equal(At(12), history.Forward(At(9)));
        Assert.Null(history.Forward(At(12)));
    }

    [Fact]
    public void H2_ANewJumpAfterABackClearsForward()
    {
        var history = new NavigationHistory();
        history.Record(At(1));
        history.Record(At(4));
        _ = history.Back(At(8));
        Assert.True(history.CanGoForward);
        Assert.True(history.Record(At(3)));
        Assert.False(history.CanGoForward);
        Assert.Equal(0, history.ForwardCount);
    }

    [Fact]
    public void H3_TheHistoryIsBoundedAndDropsTheOldest()
    {
        var history = new NavigationHistory();
        for (var i = 0; i < 200; i++)
        {
            history.Record(At(i));
        }

        Assert.Equal(NavigationHistory.DefaultCapacity, history.BackCount);
        ViewLocation? last = null;
        var current = At(500);
        for (var i = 0; i < 1000 && history.Back(current) is { } step; i++)
        {
            last = step;
            current = step;
        }

        Assert.Equal(At(200 - NavigationHistory.DefaultCapacity), last); // 136 was the oldest kept
        Assert.Equal(NavigationHistory.DefaultCapacity, history.ForwardCount);
    }

    [Fact]
    public void H3_TheForwardListIsBoundedToo()
    {
        var history = new NavigationHistory(4);
        for (var i = 0; i < 4; i++)
        {
            history.Record(At(i));
        }

        var current = At(100);
        for (var i = 0; i < 4; i++)
        {
            current = history.Back(current)!.Value;
        }

        Assert.Equal(4, history.ForwardCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory(0));
    }

    [Fact]
    public void H4_TheSamePlaceTwiceInARowIsNotRecordedAgain()
    {
        var history = new NavigationHistory();
        Assert.True(history.Record(At(3, 0.2)));
        Assert.False(history.Record(At(3, 0.205)));  // within 1% of a page
        Assert.True(history.Record(At(3, 0.5)));
        Assert.True(history.Record(At(4, 0.5)));
        Assert.Equal(3, history.BackCount);
    }

    [Fact]
    public void H4_AnEntryThatIsWhereTheViewAlreadyIsIsSkippedByBack()
    {
        var history = new NavigationHistory();
        history.Record(At(1));
        history.Record(At(6));
        // The view came back to page 6 by scrolling: Back should not "go" to where it already is.
        Assert.Equal(At(1), history.Back(At(6)));
    }

    [Fact]
    public void H5_TheJumpsOfOneSearchAreOneEntryAndAnotherKeyStartsANewRun()
    {
        var history = new NavigationHistory();
        Assert.True(history.Record(At(2), "find:1"));   // the first match: remembered
        Assert.False(history.Record(At(9), "find:1"));  // the next matches: not
        Assert.False(history.Record(At(14), "find:1"));
        Assert.Equal(1, history.BackCount);
        Assert.True(history.Record(At(14), "find:2"));  // a new search
        Assert.False(history.Record(At(20), "find:2"));
        Assert.True(history.Record(At(20)));            // a jump of another kind ends the run
        Assert.True(history.Record(At(30), "find:2"));  // and the same key starts a new one after it
        Assert.Equal(4, history.BackCount);
    }

    [Fact]
    public void H5_ResetKeyEndsARun()
    {
        var history = new NavigationHistory();
        history.Record(At(1), "find:1");
        history.ResetKey();
        Assert.True(history.Record(At(8), "find:1"));
    }

    [Fact]
    public void H6_OnAnEmptyHistoryBackAndForwardReturnNothingAndChangeNothing()
    {
        var history = new NavigationHistory();
        Assert.Null(history.Back(At(3)));
        Assert.Null(history.Forward(At(3)));
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        history.Record(At(1));
        history.Clear();
        Assert.False(history.CanGoBack);
    }
}

/// <summary>Page colours (C1 to C4).</summary>
public sealed class PageColourTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mikepdf-colour-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void C1_DarkPagesTurnPaperDarkAndInkLightAndKeepHue()
    {
        var m = PageColourMatrix.For(PageColourMode.Inverted);
        var (wr, wg, wb) = m.Apply((byte)255, (byte)255, (byte)255);
        Assert.InRange(wr, 10, 30);
        Assert.Equal(wr, wg);
        Assert.Equal(wg, wb);

        var (kr, kg, kb) = m.Apply((byte)0, (byte)0, (byte)0);
        Assert.InRange(kr, 225, 250);
        Assert.Equal(kr, kg);
        Assert.Equal(kg, kb);

        // Black text is lighter than the paper it is on, by a wide margin (readable).
        Assert.True(kr - wr > 180);

        // A strongly red pixel stays red (R is the largest, G and B are equal), and a blue one stays blue.
        var (rr, rg, rb) = m.Apply((byte)200, (byte)30, (byte)30);
        Assert.True(rr > rg && rg == rb, $"red became ({rr},{rg},{rb})");
        var (br, bg, bb) = m.Apply((byte)30, (byte)30, (byte)200);
        Assert.True(bb > br && br == bg, $"blue became ({br},{bg},{bb})");

        // Lightness is turned over for a colour as well: a dark red becomes a light one.
        var (_, lg, _) = m.Apply((byte)40, (byte)0, (byte)0);
        var (_, dg, _) = m.Apply((byte)255, (byte)200, (byte)200);
        Assert.True(lg > dg);
    }

    [Fact]
    public void C1_GreysStayGreyAndTheMappingIsDeterministic()
    {
        var m = PageColourMatrix.For(PageColourMode.Inverted);
        for (byte v = 0; v < 255; v += 15)
        {
            var (r, g, b) = m.Apply(v, v, v);
            Assert.True(Math.Abs(r - g) <= 1 && Math.Abs(g - b) <= 1, $"grey {v} became ({r},{g},{b})");
            Assert.Equal((r, g, b), m.Apply(v, v, v));
        }

        // Darker in, lighter out: monotonic over the greys.
        Assert.True(m.Apply((byte)10, (byte)10, (byte)10).R > m.Apply((byte)200, (byte)200, (byte)200).R);
        Assert.Equal(20, PageColourMatrix.For(PageColourMode.Inverted).Values.Count);
    }

    [Fact]
    public void C2_SepiaMakesWhitePaperWarmAndKeepsBlackDark()
    {
        var m = PageColourMatrix.For(PageColourMode.Sepia);
        var (r, g, b) = m.Apply((byte)255, (byte)255, (byte)255);
        Assert.InRange(r, 243, 245); // #F4ECD8
        Assert.InRange(g, 235, 237);
        Assert.InRange(b, 215, 217);
        Assert.Equal((0, 0, 0), m.Apply((byte)0, (byte)0, (byte)0));
        // Warm: red above green above blue for a neutral grey.
        var (gr, gg, gb) = m.Apply((byte)128, (byte)128, (byte)128);
        Assert.True(gr > gg && gg > gb);
    }

    [Fact]
    public void C3_NormalIsTheIdentity()
    {
        var m = PageColourMatrix.For(PageColourMode.Normal);
        Assert.True(m.IsIdentity);
        Assert.Equal((12, 99, 250), m.Apply((byte)12, (byte)99, (byte)250));
        Assert.False(PageColourMatrix.For(PageColourMode.Inverted).IsIdentity);
        Assert.Same(PageColourMatrix.Identity, PageColourMatrix.For((PageColourMode)99));
    }

    [Fact]
    public void C4_ThePageColourIsStoredAndReadBack()
    {
        var path = Path.Combine(_directory, "documents.json");
        var store = new DocumentStore(path);
        Assert.Equal(PageColourMode.Normal, store.PageColour);
        store.SetPageColour(PageColourMode.Inverted);
        Assert.Equal(PageColourMode.Inverted, new DocumentStore(path).PageColour);
        store.SetPageColour(PageColourMode.Sepia);
        Assert.Equal(PageColourMode.Sepia, new DocumentStore(path).PageColour);
        store.SetPageColour((PageColourMode)42); // not a mode: stored as Normal
        Assert.Equal(PageColourMode.Normal, new DocumentStore(path).PageColour);
    }

    [Theory]
    [InlineData("""{ "Recent": [], "Positions": [], "PageColour": "Neon" }""")]
    [InlineData("""{ "Recent": [], "Positions": [], "PageColour": "7" }""")]
    [InlineData("""{ "Recent": [], "Positions": [], "PageColour": null }""")]
    [InlineData("""{ "Recent": [], "Positions": [] }""")]
    public void C4_AMissingOrUnknownValueReadsAsNormalAndDoesNotLoseTheRest(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "documents.json");
        File.WriteAllText(path, json);
        var store = new DocumentStore(path);
        Assert.Equal(PageColourMode.Normal, store.PageColour);
        store.AddRecent(@"C:\a.pdf");
        Assert.Single(store.Recent);
    }

    [Fact]
    public void C4_AStoreWrittenByTheVersionBeforeLoadsAndKeepsItsRecentFiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "documents.json");
        File.WriteAllText(path, """{ "Recent": [ { "Path": "C:\\old.pdf", "LastOpened": "2026-01-01T00:00:00+00:00" } ], "Positions": [] }""");
        var store = new DocumentStore(path);
        Assert.Single(store.Recent);
        Assert.Equal(PageColourMode.Normal, store.PageColour);
    }
}

/// <summary>Zoom and keys (Z1, Z2).</summary>
public sealed class ZoomKeyTests
{
    [Theory]
    [InlineData(96.0, 1.0, 1.0)]        // a monitor at exactly 96 pixels per inch, 100% scale
    [InlineData(144.0, 1.5, 1.0)]       // 150% scale on a 144 ppi monitor: 100% is the physical size
    [InlineData(157.0, 1.25, 157.0 / 120.0)] // a 14 inch 1080p laptop at 125%: a true inch needs more than 100%
    [InlineData(220.0, 2.0, 220.0 / 192.0)]
    [InlineData(300.0, 1.0, 300.0 / 96.0)]
    public void Z1_ActualSizeIsThePhysicalDensityOverWhatOneHundredPercentMeans(double rawDpi, double scale, double expected) =>
        Assert.Equal(expected, ZoomMath.ActualSizeZoom(rawDpi, scale), 9);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-96.0)]
    [InlineData(40.0)]
    [InlineData(450.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Z1_AnImplausibleDensityFallsBackToOneHundredPercent(double rawDpi) => Assert.Equal(1.0, ZoomMath.ActualSizeZoom(rawDpi, 1.25));

    [Fact]
    public void Z1_TheScaleMustMakeSenseAndTheResultStaysInRange()
    {
        Assert.Equal(1.0, ZoomMath.ActualSizeZoom(150, 0));
        Assert.Equal(1.0, ZoomMath.ActualSizeZoom(150, double.NaN));
        Assert.InRange(ZoomMath.ActualSizeZoom(400, 0.5), ZoomMath.Min, ZoomMath.Max);
    }

    private static ViewportController Controller(int pages, double width, double height, double viewportWidth = 1000, double viewportHeight = 800)
    {
        var controller = new ViewportController();
        controller.SetViewport(viewportWidth, viewportHeight, 1.0);
        controller.SetPageSizes([.. Enumerable.Repeat(new SizeD(width, height), pages)]);
        return controller;
    }

    [Fact]
    public void Z1_PhysicalSizeSetsTheZoomFromTheMonitorDensityAndLeavesFitMode()
    {
        var controller = Controller(3, 612, 792);
        controller.PhysicalSize(144);
        Assert.Equal(144.0 / 96.0, controller.Zoom, 9);
        Assert.Equal(FitMode.None, controller.Fit);
        controller.PhysicalSize(double.NaN);
        Assert.Equal(1.0, controller.Zoom);
    }

    [Fact]
    public void Z2_WhenThePageFitsPageDownAndPageUpMoveAWholePage()
    {
        var controller = Controller(5, 612, 792);
        controller.FitPage();
        Assert.Equal(0, controller.CurrentPage);
        controller.PageDown();
        Assert.Equal(1, controller.CurrentPage);
        controller.PageDown();
        controller.PageDown();
        Assert.Equal(3, controller.CurrentPage);
        controller.PageUp();
        Assert.Equal(2, controller.CurrentPage);
    }

    [Fact]
    public void Z2_PageDownAndPageUpStopAtTheFirstAndLastPage()
    {
        var controller = Controller(3, 612, 792);
        controller.FitPage();
        controller.PageUp();
        Assert.Equal(0, controller.CurrentPage);
        for (var i = 0; i < 10; i++)
        {
            controller.PageDown();
        }

        Assert.Equal(2, controller.CurrentPage);
    }

    [Fact]
    public void Z2_WhenThePageIsTallerThanTheWindowPageDownScrollsAScreen()
    {
        var controller = Controller(3, 612, 792);
        controller.FitWidth(); // a 612 x 792 page fitted to 1000 px wide is far taller than 800 px
        var before = controller.ScrollY;
        controller.PageDown();
        Assert.Equal(before + (controller.ViewportHeight * 0.9), controller.ScrollY, 6);
        controller.PageUp();
        Assert.Equal(before, controller.ScrollY, 6);
        Assert.Equal(0, controller.CurrentPage);
    }

    [Fact]
    public void Z2_AnEmptyDocumentDoesNothing()
    {
        var controller = new ViewportController();
        controller.PageDown();
        controller.PageUp();
        Assert.Equal(0, controller.ScrollY);
    }
}

/// <summary>Regular expressions in find (F2 to F7).</summary>
public sealed class RegexFindTests
{
    private static PageCharacters Page(string text) => TextFixtures.Unhyphenated(text);

    [Fact]
    public void F2_AValidPatternCompilesAndABadOneIsInvalid()
    {
        Assert.True(RegexQuery.TryCompile(@"marg(in|ins)\b", matchCase: false).IsOk);
        Assert.Equal(RegexProblem.Invalid, RegexQuery.TryCompile("(unclosed", false).Problem);
        Assert.Equal(RegexProblem.Invalid, RegexQuery.TryCompile("[z-a]", false).Problem);
        Assert.Equal(RegexProblem.Invalid, RegexQuery.TryCompile("*oops", false).Problem);
        Assert.Equal(RegexProblem.Empty, RegexQuery.TryCompile("   ", false).Problem);
    }

    [Theory]
    [InlineData(@"(?<=a)b")]
    [InlineData(@"(?!x)y")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?>a+)b")]
    public void F2_SyntaxTheLinearEngineCannotDoIsUnsupportedNotACrash(string pattern) =>
        Assert.Equal(RegexProblem.Unsupported, RegexQuery.TryCompile(pattern, false).Problem);

    [Theory]
    [InlineData(1001)]
    [InlineData(999999)]
    public void F3_ARepeatBoundAboveTheLimitIsTooComplexWithoutBeingCompiled(int bound) =>
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile("a{" + bound + "}", false).Problem);

    [Fact]
    public void F3_NestedRepeatsMultiplyAndALongPatternIsTooComplex()
    {
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile("(a{1000}){1000}", false).Problem);
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile("(a{50}){50}", false).Problem); // 2500
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile("a{500}b{3}", false).Problem); // 1500
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile(new string('a', RegexQuery.MaxPatternLength + 1), false).Problem);
        Assert.Equal(RegexProblem.TooComplex, RegexQuery.TryCompile("a{99999999999}", false).Problem);
        Assert.True(RegexQuery.TryCompile(new string('a', RegexQuery.MaxPatternLength), false).IsOk);
        Assert.True(RegexQuery.TryCompile(@"\d{3}-\d{3}-\d{4}", false).IsOk);
        Assert.True(RegexQuery.TryCompile("[A-Z]{2,3}", false).IsOk);
        Assert.True(RegexQuery.TryCompile("x{1000}", false).IsOk);
    }

    [Fact]
    public void F3_TheLargestPatternThatIsAllowedCompilesAndMatchesQuickly()
    {
        var clock = Stopwatch.StartNew();
        var compiled = RegexQuery.TryCompile("(a{31}){31}", matchCase: true); // 961
        Assert.True(compiled.IsOk);
        var page = Page(new string('a', 5000));
        var result = RegexSearchSource.Search(page, compiled.Regex!);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(RegexQuery.DefaultMatchTimeout.TotalSeconds * 2), $"took {clock.Elapsed}");
        Assert.True(result.IsOk);
    }

    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("(a|aa)+$")]
    [InlineData("(x+x+)+y")]
    [InlineData("(.*a){20}")]
    public void F4_ACatastrophicPatternFinishesQuicklyOnALargePage(string pattern)
    {
        var compiled = RegexQuery.TryCompile(pattern, matchCase: true);
        Assert.True(compiled.IsOk, pattern);
        var text = new string('a', 100_000) + "b" + new string('a', 99_000);
        var page = Page(text);
        var clock = Stopwatch.StartNew();
        var result = RegexSearchSource.Search(page, compiled.Regex!);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{pattern} took {clock.Elapsed}");
        Assert.True(result.IsOk || result.Status == Status.Unsupported, $"{pattern}: {result.Status}");
    }

    [Fact]
    public void F4_TheMatchesOfACatastrophicPatternAreStillRight()
    {
        var compiled = RegexQuery.TryCompile("(a+)+$", matchCase: true);
        var result = RegexSearchSource.Search(Page(new string('a', 50_000) + "b" + new string('a', 7)), compiled.Regex!);
        Assert.True(result.IsOk);
        var match = Assert.Single(result.Value!.Matches);
        Assert.Equal(new PageMatch(50_001, 7), match);
    }

    [Fact]
    public void F5_AMatchTimeoutIsAFailedPageNotAnExceptionAndIsNotRetried()
    {
        var compiled = RegexQuery.TryCompile("(.*a){20}", matchCase: true, matchTimeout: TimeSpan.FromTicks(1));
        Assert.True(compiled.IsOk);
        var result = RegexSearchSource.Search(Page(new string('a', 150_000)), compiled.Regex!);
        Assert.Equal(Status.Unsupported, result.Status); // Timeout and EngineCrashed are what a find session retries; this is neither
        Assert.Null(result.Value);
    }

    [Fact]
    public void F6_MatchesCarryTheEnginesCharacterNumbering()
    {
        var compiled = RegexQuery.TryCompile(@"marg\w+", matchCase: true);
        var page = Page("the margin of margins and Margin");
        var result = RegexSearchSource.Search(page, compiled.Regex!);
        Assert.Equal([new PageMatch(4, 6), new PageMatch(14, 7)], result.Value!.Matches);
    }

    [Fact]
    public void F6_MatchCaseOffIgnoresCaseAndEmptyMatchesAreSkipped()
    {
        var ignoring = RegexQuery.TryCompile("margin", matchCase: false);
        var page = Page("Margin MARGIN margin");
        Assert.Equal(3, RegexSearchSource.Search(page, ignoring.Regex!).Value!.Matches.Count);
        var respecting = RegexQuery.TryCompile("margin", matchCase: true);
        Assert.Single(RegexSearchSource.Search(page, respecting.Regex!).Value!.Matches);

        var empty = RegexQuery.TryCompile("x*", matchCase: true); // matches the empty string everywhere
        var found = RegexSearchSource.Search(Page("axxb"), empty.Regex!).Value!;
        Assert.Equal([new PageMatch(1, 2)], found.Matches);
    }

    [Fact]
    public void F6_TextIsLineOrientedAndPositionsAreOneToOne()
    {
        // PDFium's made-up line break is CR LF; a pattern sees one LF, so ^ and $ and \s work on lines and . stops at the end of one.
        var characters = TextFixtures.Chars(generateBreaks: true, new TextFixtures.Run("first line", 72, 72), new TextFixtures.Run("second line", 72, 90));
        var text = RegexSearchSource.TextOf(characters);
        Assert.Equal(characters.Count, text.Length);
        Assert.Equal("first line\n\nsecond line\n\n", text);

        var ends = RegexQuery.TryCompile("line$", matchCase: true);
        Assert.Equal(2, RegexSearchSource.Search(characters, ends.Regex!).Value!.Matches.Count);
        var dot = RegexQuery.TryCompile("first.*", matchCase: true);
        Assert.Equal(new PageMatch(0, 10), Assert.Single(RegexSearchSource.Search(characters, dot.Regex!).Value!.Matches));
    }

    [Fact]
    public void F6_ControlCharactersAndTheHyphenMarkerBecomeSpacesAndHyphens()
    {
        var characters = new PageCharacters([(ushort)'a', 2, (ushort)'b', 7, (ushort)'c'], [0, 1, 2, 3, 4], [0, 0, 0, 0, 0], [1, 2, 3, 4, 5], [1, 1, 1, 1, 1], [0, PageCharacters.Hyphen, 0, 0, 0], false);
        Assert.Equal("a-b c", RegexSearchSource.TextOf(characters));
    }

    [Fact]
    public void F6_AtMost2048MatchesAPageAreReportedAndTheRestIsMarkedTruncated()
    {
        var compiled = RegexQuery.TryCompile("a", matchCase: true);
        var result = RegexSearchSource.Search(Page(new string('a', 5000)), compiled.Regex!);
        Assert.Equal(RegexSearchSource.MaxMatchesPerPage, result.Value!.Matches.Count);
        Assert.True(result.Value.Truncated);
        var exact = RegexSearchSource.Search(Page(new string('a', RegexSearchSource.MaxMatchesPerPage)), compiled.Regex!);
        Assert.False(exact.Value!.Truncated);
    }

    [Fact]
    public async Task F7_AFindSessionOverARegexSourceSearchesFromTheCurrentPageAndCountsFailedPages()
    {
        var pages = new[] { "nothing here", "an ab1 and ab22", "ab333", "" };
        var asked = new List<int>();
        var source = new RegexSearchSource(
            (page, _) =>
            {
                asked.Add(page);
                return Task.FromResult(page == 3
                    ? ReadReply.Fail<PageCharacters>(Status.Damaged)
                    : new ReadReply<PageCharacters>(Status.Ok, Page(pages[page])));
            },
            RegexQuery.TryCompile(@"ab\d+", matchCase: false).Regex!);
        using var session = new FindSession(source, 4, new SearchOptions(@"ab\d+", UseRegex: true), startPage: 2);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([2, 3, 0, 1], asked);
        var progress = session.Progress;
        Assert.True(progress.IsComplete);
        Assert.Equal(3, progress.MatchCount);
        Assert.Equal(1, progress.PagesFailed);
        var first = await session.NextAsync();
        Assert.Equal(new FoundMatch(2, 0, new PageMatch(0, 5)), first);
    }

    [Fact]
    public async Task F7_ASlowPageIsCountedAsFailedAndTheSearchGoesOn()
    {
        var source = new RegexSearchSource(
            (_, _) => Task.FromResult(new ReadReply<PageCharacters>(Status.Ok, Page(new string('a', 150_000)))),
            RegexQuery.TryCompile("(.*a){20}", matchCase: true, matchTimeout: TimeSpan.FromTicks(1)).Regex!);
        using var session = new FindSession(source, 3, new SearchOptions("(.*a){20}", UseRegex: true), startPage: 0) { RetryDelay = TimeSpan.FromMilliseconds(1) };
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(session.Progress.IsComplete);
        Assert.Equal(3, session.Progress.PagesFailed);
        Assert.Equal(0, session.Progress.MatchCount);
    }

    [Fact]
    public async Task F5_AfterThreeSlowPagesInARowTheRestAreGivenUpWithoutBeingTried()
    {
        var read = 0;
        var source = new RegexSearchSource(
            (_, _) =>
            {
                Interlocked.Increment(ref read);
                return Task.FromResult(new ReadReply<PageCharacters>(Status.Ok, Page(new string('a', 150_000))));
            },
            RegexQuery.TryCompile("(.*a){20}", matchCase: true, matchTimeout: TimeSpan.FromTicks(1)).Regex!);
        using var session = new FindSession(source, 50, new SearchOptions("(.*a){20}", UseRegex: true), startPage: 0);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RegexSearchSource.GiveUpAfterSlowPages, read);
        Assert.Equal(50, session.Progress.PagesFailed);
        Assert.True(session.Progress.IsComplete);
    }

    [Fact]
    public async Task F7_TheEnginesReaderRefusesARegexItWasNotMeantToSend()
    {
        var reader = new DocumentReader(new ScriptedEngine(), 3);
        var reply = await reader.SearchAsync(0, new SearchOptions("a.c", UseRegex: true), CancellationToken.None);
        Assert.Equal(Status.InvalidArgument, reply.Status);
    }
}

/// <summary>The properties dialog's data (P5 to P8).</summary>
public sealed class PropertiesTests
{
    [Theory]
    [InlineData("D:20240115103000+10'00'", "2024-01-15T10:30:00+10:00")]
    [InlineData("D:20240115103000+10'00", "2024-01-15T10:30:00+10:00")]
    [InlineData("D:20240115103000+1000", "2024-01-15T10:30:00+10:00")]
    [InlineData("D:20240115103000+10", "2024-01-15T10:30:00+10:00")]
    [InlineData("D:20240115103000-05'30'", "2024-01-15T10:30:00-05:30")]
    [InlineData("D:20240115103000Z", "2024-01-15T10:30:00+00:00")]
    [InlineData("D:20240115103000Z00'00'", "2024-01-15T10:30:00+00:00")]
    [InlineData("D:20240115103000", "2024-01-15T10:30:00+00:00")]
    [InlineData("D:202401151030", "2024-01-15T10:30:00+00:00")]
    [InlineData("D:2024011510", "2024-01-15T10:00:00+00:00")]
    [InlineData("D:20240115", "2024-01-15T00:00:00+00:00")]
    [InlineData("D:202401", "2024-01-01T00:00:00+00:00")]
    [InlineData("D:2024", "2024-01-01T00:00:00+00:00")]
    [InlineData("20240115103000Z", "2024-01-15T10:30:00+00:00")]
    [InlineData("  D:20240229  ", "2024-02-29T00:00:00+00:00")]
    public void P5_PdfDatesInEveryFormParse(string text, string expected)
    {
        Assert.True(PdfDate.TryParse(text, out var value), text);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).Offset, value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D:")]
    [InlineData("D:2")]
    [InlineData("D:202")]
    [InlineData("D:0000")]
    [InlineData("D:20241315")]
    [InlineData("D:20240230")]
    [InlineData("D:20230229")]
    [InlineData("D:2024011524")]
    [InlineData("D:202401151060")]
    [InlineData("D:20240115103060")]
    [InlineData("D:20240115103000+24'00'")]
    [InlineData("D:20240115103000+10'60'")]
    [InlineData("D:20240115103000+")]
    [InlineData("D:20240115103000Q")]
    [InlineData("D:20240115103000Zjunk")]
    [InlineData("D:2024011")]
    [InlineData("D:20240115103000+10'00'extra")]
    [InlineData("yesterday")]
    public void P5_NothingThatIsNotADateParsesAndNothingThrows(string? text) => Assert.False(PdfDate.TryParse(text, out _));

    [Fact]
    public void P5_AVeryLongOrHostileStringIsRefusedQuickly()
    {
        Assert.False(PdfDate.TryParse("D:" + new string('1', 10_000), out _));
        Assert.False(PdfDate.TryParse(new string('\0', 40), out _));
        _ = PdfDate.TryParse("D:99991231235959-23'59'", out _); // the very end of the calendar: either answer, but never an exception
        _ = PdfDate.TryParse("D:00010101000000+23'59'", out _);
    }

    [Fact]
    public void P6_AnUnprotectedDocumentAllowsEverything()
    {
        var p = PdfPermissions.Unrestricted;
        foreach (var kind in Enum.GetValues<PdfPermissionKind>())
        {
            Assert.True(p.Allows(kind), kind.ToString());
        }

        Assert.False(p.IsEncrypted);
        Assert.True(new PdfPermissions(0, 0).CanCopy); // revision 0 is no protection, whatever the bits
    }

    [Fact]
    public void P6_EachBitOfTheStandardHandlerIsRead()
    {
        // PDFium's encrypted.pdf: /P -3392 = 0xFFFFF2C0: no print, modify, copy, annotate, fill forms, assemble, print in high quality.
        var restricted = new PdfPermissions(0xFFFFF2C0, 4);
        foreach (var kind in Enum.GetValues<PdfPermissionKind>())
        {
            Assert.False(restricted.Allows(kind), kind.ToString());
        }

        Assert.True(new PdfPermissions(0x4, 4).CanPrint);
        Assert.True(new PdfPermissions(0x8, 4).CanModify);
        Assert.True(new PdfPermissions(0x10, 4).CanCopy);
        Assert.True(new PdfPermissions(0x20, 4).CanAnnotate);
        Assert.True(new PdfPermissions(0x100, 4).CanFillForms);
        Assert.True(new PdfPermissions(0x400, 4).CanAssemble);
        Assert.True(new PdfPermissions(0x800, 4).CanPrintHighQuality);
        Assert.False(new PdfPermissions(0x4, 4).CanCopy);
        Assert.True(new PdfPermissions(uint.MaxValue, 6).CanCopy);
    }

    [Fact]
    public void P6_ARevisionTwoDocumentHasOnlyTheFourDefinedBitsAndDeniesNothingElse()
    {
        var r2 = new PdfPermissions(0xFFFFF0C0, 2);
        Assert.Equal(4, r2.Defined.Count);
        Assert.False(r2.CanPrint);
        Assert.False(r2.CanCopy);
        Assert.True(r2.CanFillForms);
        Assert.True(r2.CanAssemble);
        Assert.True(r2.CanPrintHighQuality);
        Assert.Equal(7, new PdfPermissions(0, 4).Defined.Count);
    }

    [Theory]
    [InlineData(595.28, 841.89, "A4")]
    [InlineData(595, 842, "A4")]
    [InlineData(841.89, 595.28, "A4")]
    [InlineData(612, 792, "Letter")]
    [InlineData(792, 612, "Letter")]
    [InlineData(612, 1008, "Legal")]
    [InlineData(841.89, 1190.55, "A3")]
    [InlineData(419.53, 595.28, "A5")]
    [InlineData(792, 1224, "Tabloid")]
    public void P8_StandardPaperIsNamedWithinAPointInEitherOrientation(double width, double height, string name) => Assert.Equal(name, PaperSizes.Name(width, height));

    [Theory]
    [InlineData(612, 794)]
    [InlineData(200, 200)]
    [InlineData(600, 800)]
    [InlineData(0, 0)]
    public void P8_OtherSizesHaveNoName(double width, double height) => Assert.Null(PaperSizes.Name(width, height));

    [Fact]
    public void P7_FontsHaveTheirSubsetPrefixRemoved()
    {
        Assert.Equal(new DocumentFont("Arial-BoldMT", true, true), DocumentFont.From("ABCDEF+Arial-BoldMT", true));
        Assert.Equal(new DocumentFont("Helvetica", false, false), DocumentFont.From("Helvetica", false));
        Assert.Equal(new DocumentFont("abcdef+Lower", false, false), DocumentFont.From("abcdef+Lower", false));
        Assert.Equal(new DocumentFont("ABC+Short", false, false), DocumentFont.From("ABC+Short", false));
    }

    private static GetDocumentInfoReply Info(uint pages = 3) => new()
    {
        PageCount = pages, PdfVersion = 17, Title = "A\u202eTitle\u0007", Author = "Writer", Subject = "", Keywords = "k", Creator = "Word", Producer = "Lib",
        Permissions = 0xFFFFFFFF, IsTagged = true, HasXfa = false, HasJavaScript = true, SignatureCount = 2,
    };

    private static GetDocumentDetailsReply Details() => new()
    {
        CreationDate = "D:20240115103000+10'00'", ModificationDate = "not a date", SecurityRevision = 0, UserPermissions = 0xFFFFFFFF, FormType = 1,
        FontNames = ["ABCDEF+Arial", "Helvetica"], FontFlags = [1, 0], FontsComplete = true, FontPagesScanned = 3,
    };

    [Fact]
    public async Task P7_PropertiesCombineInfoAndDetailsAndCleanTheText()
    {
        var engine = new ScriptedEngine { Info = () => ScriptedEngine.Ok(Info()), Details = () => ScriptedEngine.Ok(Details()) };
        var reply = await new DocumentReader(engine, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.True(reply.IsOk);
        var p = reply.Value!;
        Assert.Equal("ATitle", p.Title);            // the direction override and the bell are gone
        Assert.Equal("1.7", p.PdfVersion);
        Assert.Equal(3, p.PageCount);
        Assert.True(p.IsTagged);
        Assert.True(p.HasJavaScript);
        Assert.Equal(2, p.SignatureCount);
        Assert.True(p.DetailsAvailable);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(10)), p.Created);
        Assert.Null(p.Modified);                    // not a date: the text is kept
        Assert.Equal("not a date", p.ModifiedText);
        Assert.Equal(DocumentFormType.AcroForm, p.FormType);
        Assert.False(p.Permissions.IsEncrypted);
        Assert.True(p.Permissions.CanCopy);
        Assert.Equal([new DocumentFont("Arial", true, true), new DocumentFont("Helvetica", false, false)], p.Fonts);
        Assert.True(p.FontsComplete);
        Assert.False(p.OpenedWithOwnerPassword);
    }

    [Fact]
    public async Task P7_AnEncryptedDocumentOpenedWithTheOwnerPasswordSaysSo()
    {
        var details = Details();
        details.SecurityRevision = 4;
        details.UserPermissions = 0xFFFFF2C0;
        var info = Info();
        info.Permissions = 0xFFFFFFFF; // PDFium: the owner may do anything
        var reply = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(info), Details = () => ScriptedEngine.Ok(details) }, 3).GetPropertiesAsync(CancellationToken.None);
        var p = reply.Value!;
        Assert.True(p.Permissions.IsEncrypted);
        Assert.True(p.Permissions.CanCopy);
        Assert.True(p.OpenedWithOwnerPassword);
        Assert.False(new PdfPermissions(p.UserPermissionBits, 4).CanCopy);

        info.Permissions = 0xFFFFF2C0; // opened as an ordinary user
        var user = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(info), Details = () => ScriptedEngine.Ok(details) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.False(user.Value!.Permissions.CanCopy);
        Assert.False(user.Value.OpenedWithOwnerPassword);
    }

    [Fact]
    public async Task P7_WhenTheDetailsFailTheRestStillShows()
    {
        var engine = new ScriptedEngine { Info = () => ScriptedEngine.Ok(Info()), Details = () => ReadReply.Fail<GetDocumentDetailsReply>(Status.Timeout) };
        var reply = await new DocumentReader(engine, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.True(reply.IsOk);
        Assert.False(reply.Value!.DetailsAvailable);
        Assert.Empty(reply.Value.Fonts);
        Assert.Equal("ATitle", reply.Value.Title);
        Assert.Equal(string.Empty, reply.Value.CreatedText);
        Assert.True(reply.Value.Permissions.CanCopy || !reply.Value.Permissions.IsEncrypted);
    }

    [Fact]
    public async Task P7_HostileDetailsAreRefusedAndHostileInfoFailsTheCall()
    {
        var unequal = Details();
        unequal.FontFlags = [1];
        var reply = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(Info()), Details = () => ScriptedEngine.Ok(unequal) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.True(reply.IsOk);
        Assert.False(reply.Value!.DetailsAvailable);

        var moreThanTheDocument = Details();
        moreThanTheDocument.FontPagesScanned = 9;
        var second = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(Info()), Details = () => ScriptedEngine.Ok(moreThanTheDocument) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.False(second.Value!.DetailsAvailable);

        var wrongPageCount = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(Info(7)), Details = () => ScriptedEngine.Ok(Details()) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.Equal(Status.Damaged, wrongPageCount.Status);

        var noInfo = await new DocumentReader(new ScriptedEngine { Info = () => ReadReply.Fail<GetDocumentInfoReply>(Status.EngineCrashed) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.Equal(Status.EngineCrashed, noInfo.Status);
    }

    [Fact]
    public async Task P7_AFontNameThatIsOnlyControlCharactersIsDroppedAndAVersionOfZeroIsEmpty()
    {
        var info = Info();
        info.PdfVersion = 0;
        var details = Details();
        details.FontNames = ["\u0001\u0002", "Real"];
        details.FontFlags = [0, 0];
        var reply = await new DocumentReader(new ScriptedEngine { Info = () => ScriptedEngine.Ok(info), Details = () => ScriptedEngine.Ok(details) }, 3).GetPropertiesAsync(CancellationToken.None);
        Assert.Equal(string.Empty, reply.Value!.PdfVersion);
        Assert.Equal(["Real"], reply.Value.Fonts.Select(f => f.Name));
    }
}
