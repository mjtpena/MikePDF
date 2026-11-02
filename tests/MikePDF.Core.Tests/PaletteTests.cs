using System.Diagnostics;
using MikePDF.Core.Commands;

namespace MikePDF.Core.Tests;

/// <summary>Palette search and sources: CMD-09 ranking (with a table of 40 queries), CMD-10 speed, CMD-11 sources, CMD-14 spoken text (unit part).</summary>
public sealed class PaletteTests
{
    private sealed class ListSource(IReadOnlyList<PaletteItem> items, bool pinned = false) : IPaletteSource
    {
        public bool Pinned => pinned;

        public IReadOnlyList<PaletteItem> Items(string query) => items;
    }

    private static PaletteItem Command(string id, string title, string category, string keywords = "", bool enabled = true) =>
        new(PaletteItemKind.Command, id, title, category, string.Empty, string.Empty, enabled, enabled ? null : "Open a document first", keywords);

    // A fixed registry, so the ranking can be read off the page.
    private static readonly PaletteItem[] Fixed =
    [
        Command("view.zoomIn", "Zoom in", "View", "magnify enlarge"),
        Command("view.zoomOut", "Zoom out", "View", "shrink"),
        Command("view.zoomActual", "Zoom to 100%", "View"),
        Command("view.fitWidth", "Fit width", "View"),
        Command("view.fitPage", "Fit page", "View"),
        Command("view.rotateRight", "Rotate view right", "View", "turn clockwise"),
        Command("view.rotateLeft", "Rotate view left", "View"),
        Command("nav.goToPage", "Go to page...", "Navigate"),
        Command("nav.nextPage", "Next page", "Navigate"),
        Command("nav.previousPage", "Previous page", "Navigate"),
        Command("find.open", "Find in document", "Navigate", "search"),
        Command("find.next", "Find next", "Navigate"),
        Command("file.open", "Open...", "File", "browse"),
        Command("file.properties", "Document properties", "File"),
        Command("app.settings", "Settings", "Settings", "preferences"),
        Command("app.about", "About MikePDF", "Help"),
        Command("edit.copy", "Copy", "Edit", enabled: false),
        Command("edit.selectAll", "Select all text", "Edit"),
        Command("app.exportSettings", "Export settings...", "Settings"),
        Command("app.importSettings", "Import settings...", "Settings"),
        Command("test.resume", "R\u00E9sum\u00E9 reading", "View"),
    ];

    private static PaletteEngine Engine(Func<string, int>? recent = null, params PaletteItem[] items) =>
        new([new ListSource(items.Length > 0 ? items : Fixed)], recent);

    private static string[] Ids(IReadOnlyList<PaletteResult> results, int take = 100) => [.. results.Take(take).Select(r => r.Item.Key)];

    public static TheoryData<string, string> Golden => new()
    {
        // exact name first
        { "settings", "app.settings,app.exportSettings,app.importSettings" },
        { "copy", "edit.copy" },
        { "Settings", "app.settings,app.exportSettings,app.importSettings" },
        // name prefix
        { "zoom", "view.zoomIn,view.zoomOut,view.zoomActual" },
        { "rot", "view.rotateLeft,view.rotateRight" },
        { "open", "file.open" },
        { "about", "app.about" },
        { "sel", "edit.selectAll" },
        { "im", "app.importSettings" },
        { "fit", "view.fitPage,view.fitWidth" },
        { "set", "app.settings,app.exportSettings,app.importSettings" },
        { "co", "edit.copy" },
        { "doc", "file.properties,find.open" },
        // word start
        { "page", "view.fitPage,nav.goToPage,nav.nextPage,nav.previousPage" },
        { "all", "edit.selectAll" },
        { "next", "nav.nextPage,find.next" },
        { "right", "view.rotateRight" },
        // acronyms
        { "zi", "view.zoomIn" },
        { "fw", "view.fitWidth" },
        { "ex", "app.exportSettings" },
        { "gtp", "nav.goToPage" },
        { "pp", "nav.previousPage" },
        // several words must all match
        { "zoom in", "view.zoomIn" },
        { "zoom out", "view.zoomOut" },
        { "ZOOM IN", "view.zoomIn" },
        { "go to page", "nav.goToPage" },
        { "doc prop", "file.properties" },
        { "rotate left", "view.rotateLeft" },
        { "fit  width", "view.fitWidth" },
        // substring
        { "x", "app.exportSettings,find.next,nav.nextPage,edit.selectAll" },
        // keywords and categories
        { "pref", "app.settings" },
        { "magnify", "view.zoomIn" },
        { "turn", "view.rotateRight" },
        { "bro", "file.open" },
        { "search", "find.open" },
        { "help", "app.about" },
        // diacritics and width
        { "resume", "test.resume" },
        { "R\u00E9sum\u00E9", "test.resume" },
        { "\uFF3A\uFF2F\uFF2F\uFF2D", "view.zoomIn,view.zoomOut,view.zoomActual" },
        // nothing
        { "xyzzy", "" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void CMD09_TheRankingOnAFixedRegistryMatchesTheGoldenTable(string query, string expectedLeadingIds)
    {
        var results = Ids(Engine().Search(query));
        var expected = expectedLeadingIds.Length == 0 ? [] : expectedLeadingIds.Split(',');
        if (expected.Length == 0)
        {
            Assert.Empty(results);
            return;
        }

        Assert.True(results.Length >= expected.Length, $"'{query}' gave {string.Join(",", results)}");
        Assert.Equal(expected, results.Take(expected.Length));
    }

    [Fact]
    public void CMD09_TheTableHasFortyQueries()
    {
        Assert.Equal(40, Golden.Count);
    }

    [Fact]
    public void CMD09_ExactBeatsPrefixBeatsWordStartBeatsSubstringBeatsSubsequence()
    {
        PaletteItem Item(string id, string title) => Command(id, title, "Test");
        var engine = Engine(null, Item("a.subsequence", "Sale of pickled onions"), Item("a.substring", "Xshotx"), Item("a.wordstart", "Big shot"), Item("a.prefix", "Shotgun"), Item("a.exact", "Shot"));
        Assert.Equal(["a.exact", "a.prefix", "a.wordstart", "a.substring"], Ids(engine.Search("shot")));
        var subsequence = Engine(null, Item("a.one", "Sale of pickled onions"));
        Assert.Equal(["a.one"], Ids(subsequence.Search("sop")));
    }

    [Fact]
    public void CMD09_EveryWordHasToMatchSomewhere()
    {
        Assert.Empty(Engine().Search("zoom banana"));
        Assert.NotEmpty(Engine().Search("zoom view")); // one in the name, one in the category
    }

    [Fact]
    public void CMD09_RecentlyUsedCommandsAndCommandsThatCanRunRankHigher()
    {
        var recent = new Dictionary<string, int> { ["nav.previousPage"] = 1 };
        var withRecent = Engine(id => recent.GetValueOrDefault(id)).Search("page");
        Assert.Equal("nav.previousPage", withRecent[0].Item.Key);

        var items = Fixed.Select(i => i.Key == "nav.nextPage" ? i with { Enabled = false, DisabledReason = "Open a document first" } : i).ToArray();
        var order = Ids(Engine(null, items).Search("page"));
        Assert.Equal(["view.fitPage", "nav.goToPage", "nav.previousPage", "nav.nextPage"], order); // the one that can't run comes last of its rank
    }

    [Fact]
    public void CMD09_TiesBreakOnTheNameAndTheOrderIsStableBetweenRuns()
    {
        var first = Ids(Engine().Search("e"));
        var second = Ids(Engine().Search("e"));
        Assert.Equal(first, second);
        var items = new[] { Command("z.b", "Alpha", "Test"), Command("z.a", "Alpha", "Test"), Command("z.c", "Aardvark", "Test") };
        Assert.Equal(["z.c", "z.a", "z.b"], Ids(Engine(null, items).Search("a"))); // same score: by name, then by id
    }

    [Fact]
    public void CMD09_AnEmptyBoxShowsTheRecentCommandsFirstThenAFewOfEachCategory()
    {
        var recent = new Dictionary<string, int> { ["view.fitWidth"] = 1, ["file.open"] = 2, ["app.settings"] = 3 };
        var suggestions = Engine(id => recent.GetValueOrDefault(id)).Suggestions(recentCount: 8, perCategory: 2);
        Assert.Equal(["view.fitWidth", "file.open", "app.settings"], Ids(suggestions, 3));
        var rest = suggestions.Skip(3).Select(r => r.Item.Category).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        Assert.All(rest.Values, count => Assert.True(count <= 2));
        Assert.Equal(suggestions.Count, suggestions.Select(r => r.Item.Key).Distinct().Count());
    }

    [Fact]
    public void CMD09_AQueryLongerThanTheLimitIsCutNotRefused()
    {
        // The first 256 characters are used: here that is "zoom" and spaces, so "in" is cut off and the three Zoom commands come first.
        var results = Engine().Search("zoom" + new string(' ', 1000) + "in");
        Assert.Equal(["view.zoomIn", "view.zoomOut", "view.zoomActual"], Ids(results, 3));
        Assert.Empty(Engine().Search(new string('z', 5000))); // does not throw, and nothing has that many z
    }

    [Fact]
    public void CMD10_RankingFiveHundredCommandsIsFasterThanFiveMillisecondsAKeystroke()
    {
        var items = Enumerable.Range(0, 500).Select(i => Command("bench.cmd" + i, $"Command number {i} for {(i % 7 == 0 ? "zoom" : i % 5 == 0 ? "page" : "thing")} and {i * 13}", "Bench", i % 3 == 0 ? "keyword" + i : string.Empty)).ToArray();
        var engine = Engine(null, items);
        _ = engine.Search("warm"); // the rows are folded once per engine, which is when the palette opens
        string[] queries = ["z", "zo", "zoo", "zoom", "zoom p", "page", "cn", "cmd 4", "thing 9", "number 12", "comm", "keyword", "k", "for page", "13", "pa", "x", "and"];
        var worst = 0.0;
        var median = new List<double>();
        foreach (var query in queries)
        {
            var times = new List<double>();
            for (var run = 0; run < 9; run++)
            {
                var watch = Stopwatch.StartNew();
                _ = engine.Search(query);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            median.Add(times[4]);
            worst = Math.Max(worst, times[4]);
        }

        // The number is recorded in the test output (dotnet test --logger "console;verbosity=detailed"); the bound is the proposed 5 ms.
        TestContext.Current.SendDiagnosticMessage($"palette benchmark: 500 commands, worst median per keystroke {worst:0.000} ms, mean {median.Average():0.000} ms");
        Assert.True(worst < 5.0, $"worst median per keystroke was {worst:0.000} ms");
    }

    [Fact]
    public void CMD11_TheDynamicSourcesAppearForMatchingQueriesAndRespectTheirLimits()
    {
        var tabs = new TabsPaletteSource(() => [.. Enumerable.Range(1, 30).Select(i => ("t" + i, "Report " + i))], ResourceText.Get);
        var recents = new RecentFilesPaletteSource(() => [.. Enumerable.Range(1, 30).Select(i => @"C:\docs\file" + i + ".pdf")], ResourceText.Get);
        var pages = new SettingsPagesPaletteSource(() => [("shortcuts", "Keyboard shortcuts"), ("policies", "Enterprise policies")], ResourceText.Get);
        var goTo = new GoToPagePaletteSource(() => 12, ResourceText.Get);
        var engine = new PaletteEngine([goTo, new ListSource(Fixed), tabs, recents, pages]);

        var tabRows = engine.Search("report").Where(r => r.Item.Kind == PaletteItemKind.Tab).ToList();
        Assert.Equal(TabsPaletteSource.Limit, tabRows.Count);
        Assert.Equal("Switch to tab: Report 1", tabRows[0].Item.Title);

        var recentRows = engine.Search("file").Where(r => r.Item.Kind == PaletteItemKind.RecentFile).ToList();
        Assert.Equal(RecentFilesPaletteSource.Limit, recentRows.Count);
        Assert.Equal(@"C:\docs", recentRows[0].Item.Detail);
        Assert.StartsWith("Open recent: file", recentRows[0].Item.Title, StringComparison.Ordinal);

        var settings = engine.Search("shortcuts").Single(r => r.Item.Kind == PaletteItemKind.SettingsPage);
        Assert.Equal("Settings: Keyboard shortcuts", settings.Item.Title);
        Assert.Equal("shortcuts", settings.Item.Key);

        var go = engine.Search("7");
        Assert.Equal(PaletteItemKind.GoToPage, go[0].Item.Kind); // first
        Assert.Equal("Go to page 7", go[0].Item.Title);
        Assert.Equal("7", go[0].Item.Key);
        Assert.Equal("Go to page 12", engine.Search(":12")[0].Item.Title);
        Assert.DoesNotContain(engine.Search("13"), r => r.Item.Kind == PaletteItemKind.GoToPage); // past the last page
        Assert.DoesNotContain(engine.Search("0"), r => r.Item.Kind == PaletteItemKind.GoToPage);
        Assert.DoesNotContain(engine.Search("zoom"), r => r.Item.Kind == PaletteItemKind.GoToPage);
        Assert.DoesNotContain(engine.Search("99999999999999999999"), r => r.Item.Kind == PaletteItemKind.GoToPage);
    }

    [Fact]
    public void CMD11_ASourceThatThrowsIsSkippedAndTheOthersStillShow()
    {
        var failures = new List<IPaletteSource>();
        var bad = new TabsPaletteSource(() => throw new InvalidOperationException("boom"), ResourceText.Get);
        var badPinned = new GoToPagePaletteSource(() => throw new InvalidOperationException("boom"), ResourceText.Get);
        var engine = new PaletteEngine([bad, badPinned, new ListSource(Fixed)]);
        engine.SourceFailed += (source, _) => failures.Add(source);
        Assert.Equal("view.zoomIn", engine.Search("zoom in")[0].Item.Key);
        Assert.Equal("view.zoomIn", engine.Search("zoom in")[0].Item.Key);
        Assert.Contains(bad, failures);
        Assert.Contains(badPinned, failures);
    }

    [Fact]
    public void CMD14_ARowIsReadOutAsNameShortcutCategoryAndWhenDisabledWhy()
    {
        var item = new PaletteItem(PaletteItemKind.Command, "view.zoomIn", "Zoom in", "View", string.Empty, "Ctrl+=", true, null);
        Assert.Equal("Zoom in, Ctrl+=, View", item.Spoken(ResourceText.Get));
        var none = item with { Shortcut = string.Empty };
        Assert.Equal("Zoom in, no shortcut, View", none.Spoken(ResourceText.Get));
        var off = none with { Enabled = false, DisabledReason = "Open a document first" };
        Assert.Equal("Zoom in, no shortcut, View, unavailable, Open a document first", off.Spoken(ResourceText.Get));
    }

    [Fact]
    public void CMD09_TheRealRegistryFeedsThePaletteWithShortcutsAndReasons()
    {
        var registry = new CommandRegistry();
        var source = new CommandPaletteSource(registry, () => CommandContext.Empty, ResourceText.Get, c => ChordDisplay.Format(c, KeyLayout.EnUs));
        var engine = new PaletteEngine([source]);
        var zoom = engine.Search("zoom in")[0].Item;
        Assert.Equal("view.zoomIn", zoom.Key);
        Assert.Equal("Ctrl+=", zoom.Shortcut);
        Assert.False(zoom.Enabled);
        Assert.Equal("Open a document first", zoom.DisabledReason);
        Assert.Equal("View", zoom.Category);
        var open = engine.Search("open")[0].Item;
        Assert.Equal(("file.open", "Open...", "Ctrl+O", true), (open.Key, open.Title, open.Shortcut, open.Enabled));
        Assert.Equal(registry.Commands.Count, engine.Search(string.Empty).Count); // every command is in the palette
    }

    // ---- history -----------------------------------------------------------------------------------------------------------------------

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "mikepdf-hist-" + Guid.NewGuid().ToString("N")[..8], "palette-history.json");

    [Fact]
    public void TheHistoryKeepsTheLastTwentyNewestFirstAndSurvivesARestart()
    {
        var path = TempFile();
        try
        {
            var history = new CommandHistory(path);
            for (var i = 0; i < 25; i++)
            {
                history.Record("test.cmd" + i);
            }

            history.Record("test.cmd10");
            Assert.Equal(CommandHistory.Capacity, history.Recent.Count);
            Assert.Equal("test.cmd10", history.Recent[0]);
            Assert.Equal(1, history.Rank("test.cmd10"));
            Assert.Equal(2, history.Rank("test.cmd24"));
            Assert.Equal(0, history.Rank("test.cmd0"));
            Assert.Equal(history.Recent, new CommandHistory(path).Recent);
            history.Clear();
            Assert.True(history.IsEmpty);
            Assert.Empty(new CommandHistory(path).Recent);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"recent\": 5}")]
    [InlineData("{\"recent\": [1, null, \"bad id!\", \"view.zoomIn\"]}")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[")]
    public void AHistoryFileThatCannotBeReadMeansNoHistoryNotAnError(string content)
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            var history = new CommandHistory(path);
            Assert.All(history.Recent, id => Assert.Matches(CommandDefinition.IdPattern, id));
            Assert.True(history.Recent.Count <= 1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AnOversizedHistoryFileIsNotRead()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"recent\": [\"view.zoomIn\"], \"pad\": \"" + new string('x', 40_000) + "\"}");
            Assert.True(new CommandHistory(path).IsEmpty);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
