using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// The reader features of WP-S2 as a person uses them: thumbnails, bookmarks, links, text selection and copy, find, and what a screen reader is
/// offered. Each journey starts its own MikePDF on a document with real content (<see cref="ArticlePdf"/>); the Text pattern of the page surface
/// is how the tests find where a phrase is on the screen, which tests the accessible text at the same time. The window is made 1600 x 960 so
/// the screenshots (MIKEPDF_SHOTS_DIR, build/out/shots) are fit for a Store listing.
/// </summary>
public sealed class ReaderJourneyTests : IDisposable
{
    private const int Width = 1600;
    private const int Height = 960;

    private static readonly (string Id, string Name)[] SidebarNames =
    [
        ("SidebarButton", "Sidebar"), ("FindButton", "Find"), ("ThumbnailList", "Page thumbnails"), ("ThumbnailsTab", "Thumbnails"), ("BookmarksTab", "Bookmarks"),
    ];

    private static readonly (string Id, string Name)[] FindNames =
    [
        ("FindBox", "Find in document"), ("FindPreviousButton", "Previous match"), ("FindNextButton", "Next match"), ("FindMatchCaseToggle", "Match case"), ("FindCloseButton", "Close find"),
    ];

    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        MikePDFApp.KillAll();
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private MikePDFApp Open(out ArticlePdf article)
    {
        article = ArticlePdf.Create(_work, "handbook.pdf", out var path);
        var app = MikePDFApp.Launch(path);
        app.WaitForDocument(article.PageCount);
        app.ResizeTo(Width, Height);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_1); // the whole page in view: every line of it is on screen to click
        Thread.Sleep(600);
        return app;
    }

    private static void ShowText(MikePDFApp app, string phrase) =>
        app.WaitFor($"'{phrase}' in the text of the pages in view", () => app.AccessibleText.Contains(phrase, StringComparison.Ordinal), TimeSpan.FromSeconds(30));

    private static Rectangle Box(MikePDFApp app, string phrase)
    {
        ShowText(app, phrase);
        return app.ScreenBoxOf(phrase) ?? throw new InvalidOperationException("No box for " + phrase);
    }

    private static void GoTo(MikePDFApp app, int page)
    {
        app.PressWithControl(VirtualKeyShort.KEY_G);
        var box = app.Require("GoToPageBox");
        // The box exists before the dialog gives it focus, and keys sent in between are lost. The focus is on the text box inside the NumberBox.
        app.WaitFor("the page box focused", () => box.FindAllDescendants().Any(e => e.Properties.HasKeyboardFocus.ValueOrDefault));
        app.Type(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor($"page {page}", () => app.CurrentPage == page);
        app.FocusPages();
    }

    /// <summary>Pixels that are neither grey nor white nor black: the teal and the yellow of the sample pages, or a highlight.</summary>
    private static int CountColoured(AutomationElement element, Func<Color, bool>? match = null)
    {
        using var capture = FlaUI.Core.Capturing.Capture.Element(element);
        var bitmap = capture.Bitmap;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                if (match?.Invoke(c) ?? Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 60)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Writes the sample documents to the folder named by MIKEPDF_SAMPLE_DIR, for trying MikePDF by hand. Skipped otherwise.</summary>
    [Fact]
    public void WritesTheSampleDocumentsForManualRuns()
    {
        var directory = Environment.GetEnvironmentVariable("MIKEPDF_SAMPLE_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(directory), "Set MIKEPDF_SAMPLE_DIR to write the sample documents.");
        _ = ArticlePdf.Create(directory!, "handbook.pdf", out _);
        _ = TestPdfs.Numbered(directory!, "numbered-600.pdf", 600);
    }

    // ---- thumbnails ------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void ThumbnailsShowThePagesAndATapTakesYouThere()
    {
        using var app = Open(out var article);
        app.Click("SidebarButton");
        var list = app.Require("ThumbnailList");
        Assert.Equal("Page thumbnails", list.Name);

        // The cover is teal and yellow: its thumbnail is drawn when colour shows up in the first item. The pane draws only what is on screen.
        var first = MikePDFApp.WaitForItem(list, "Page 1");
        app.WaitFor("the cover thumbnail drawn", () => CountColoured(first) > 300, TimeSpan.FromSeconds(30));
        app.Capture("thumbnails-pane");

        var fifth = MikePDFApp.WaitForItem(list, "Page 5");
        MikePDFApp.ClickItem(fifth);
        app.WaitFor("page 5", () => app.CurrentPage == 5);
        Assert.True(article.PageCount >= 6);
    }

    [UiFact]
    public void ThumbnailsAreReachableFromTheKeyboardAndFollowThePageInView()
    {
        using var app = Open(out var article);
        app.Press(VirtualKeyShort.F4);
        var list = app.Require("ThumbnailList");
        MikePDFApp.WaitForItem(list, "Page 1");

        // F4 put the keyboard in the pane, on the page in view.
        app.Press(VirtualKeyShort.DOWN);
        app.Press(VirtualKeyShort.DOWN);
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("page 3", () => app.CurrentPage == 3);

        app.Press(VirtualKeyShort.ESCAPE);
        app.Press(VirtualKeyShort.END);
        app.WaitFor("the last page", () => app.CurrentPage == article.PageCount);
        app.WaitFor("the selected thumbnail follows the page", () => MikePDFApp.WaitForItemOrNull(list, $"Page {article.PageCount}")?.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault == true);

        app.Press(VirtualKeyShort.F4);
        app.WaitFor("the pane closed", () => app.Find("ThumbnailList") is null);
    }

    [UiFact]
    public void ThumbnailsOfALongDocumentAreMadeOnlyForWhatIsOnScreen()
    {
        var pdf = TestPdfs.Numbered(_work, "long.pdf", 600);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(600);
        app.ResizeTo(Width, Height);
        app.Click("SidebarButton");
        var list = app.Require("ThumbnailList");
        MikePDFApp.WaitForItem(list, "Page 1");
        // Only a screenful of the 600 items exists: the list is virtual.
        Assert.InRange(list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length, 1, 40);
        app.FocusPages();
        app.Press(VirtualKeyShort.END);
        app.WaitFor("page 600", () => app.CurrentPage == 600);
        app.WaitFor("its thumbnail", () => MikePDFApp.WaitForItemOrNull(list, "Page 600") is not null, TimeSpan.FromSeconds(30));
    }

    // ---- bookmarks -------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void BookmarksAreReadWhenAParentIsOpenedAndGoToTheirPlace()
    {
        using var app = Open(out var article);
        app.Click("SidebarButton");
        app.Click("BookmarksTab");
        var tree = app.Require("OutlineTree");
        Assert.Equal("Bookmarks", tree.Name);

        var chapter = MikePDFApp.WaitForItem(tree, "Type and Its Habits", ControlType.TreeItem);
        // The children are not read until their parent is opened.
        Assert.Null(MikePDFApp.WaitForItemOrNull(tree, "Serifs and Sans", ControlType.TreeItem, TimeSpan.FromMilliseconds(500)));
        app.Capture("bookmarks-collapsed");
        chapter.Patterns.ExpandCollapse.Pattern.Expand();
        var section = MikePDFApp.WaitForItem(tree, "The Rhythm of a Line", ControlType.TreeItem);
        app.Capture("bookmarks-pane");

        MikePDFApp.ClickItem(section);
        var page = article.SectionPages.Single(s => s.Title == "The Rhythm of a Line").Page;
        // The heading is put at the top of the view, so the page in view is that page or, when the heading is low on it, the next.
        app.WaitFor($"page {page} or {page + 1}", () => app.CurrentPage == page || app.CurrentPage == page + 1);
        // The bookmark has a position on the page: the heading is near the top of the view, not the middle of the page.
        var surfaceTop = app.Require("PageSurface").BoundingRectangle.Top;
        var heading = Box(app, "The Rhythm of a Line");
        Assert.InRange(heading.Top - surfaceTop, -4, 160);
    }

    [UiFact]
    public void BookmarksCanBeWalkedWithTheKeyboard()
    {
        using var app = Open(out var article);
        app.Press(VirtualKeyShort.F4);
        app.Click("BookmarksTab");
        var tree = app.Require("OutlineTree");
        var chapter = MikePDFApp.WaitForItem(tree, "Paper, Ink and Time", ControlType.TreeItem);
        chapter.Focus();
        app.Press(VirtualKeyShort.RIGHT);                       // open
        MikePDFApp.WaitForItem(tree, "A Short History of the Leaf", ControlType.TreeItem);
        app.Press(VirtualKeyShort.DOWN);                        // first child
        app.Press(VirtualKeyShort.ENTER);                       // go
        var page = article.SectionPages.Single(s => s.Title == "A Short History of the Leaf").Page;
        app.WaitFor($"page {page} or {page + 1}", () => app.CurrentPage == page || app.CurrentPage == page + 1);
    }

    // ---- links ---------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void ALinkInTheContentsGoesToItsChapterAndASectionLinkLandsOnTheHeading()
    {
        using var app = Open(out var article);
        GoTo(app, ArticlePdf.ContentsPage);
        var chapter = article.ChapterPages.Single(c => c.Title == "Paper, Ink and Time");
        var contentsLine = Box(app, chapter.Title);
        app.Capture("contents-links");
        app.ClickAt(MikePDFApp.CenterOf(contentsLine));
        app.WaitFor($"chapter 3 on page {chapter.Page}", () => app.CurrentPage == chapter.Page, TimeSpan.FromSeconds(20));
        ShowText(app, "Keeping Things Well");

        GoTo(app, ArticlePdf.ContentsPage);
        var section = article.SectionPages.Single(s => s.Title == "Numbers in Tables");
        app.ClickAt(MikePDFApp.CenterOf(Box(app, section.Title)));
        app.WaitFor($"the table section on page {section.Page} or {section.Page + 1}", () => app.CurrentPage == section.Page || app.CurrentPage == section.Page + 1, TimeSpan.FromSeconds(20));
        var surfaceTop = app.Require("PageSurface").BoundingRectangle.Top;
        Assert.InRange(Box(app, section.Title).Top - surfaceTop, -4, 160);
    }

    [UiFact]
    public void AnExternalLinkShowsItsAddressAndOpensNothing()
    {
        using var app = Open(out var article);
        var browsers = BrowserWindows();
        GoTo(app, article.AddressPage);
        app.ClickAt(MikePDFApp.CenterOf(Box(app, ArticlePdf.Address)));
        var dialog = app.Require("ExternalLinkDialog");
        Assert.NotNull(dialog);
        var address = app.Require("ExternalLinkAddress");
        Assert.Equal("Link address: " + ArticlePdf.Address, address.Name);
        app.Capture("external-link-address");

        MikePDFApp.ClearClipboard();
        app.Window.FindFirstDescendant(cf => cf.ByName("Copy address"))!.AsButton().Invoke();
        app.WaitFor("the address on the clipboard", () => MikePDFApp.ClipboardText() == ArticlePdf.Address);
        app.WaitFor("the dialog gone", () => app.Find("ExternalLinkDialog") is null);
        Assert.Equal(browsers, BrowserWindows()); // nothing was opened: no browser window appeared, and none was sent to a new address
    }

    private static readonly string[] Browsers = ["msedge", "chrome", "firefox", "iexplore", "brave", "opera"];

    // The browsers' windows and what they show. Not the process count: a browser starts and ends background processes of its own at any time.
    private static string BrowserWindows() =>
        string.Join(" | ", Browsers.SelectMany(System.Diagnostics.Process.GetProcessesByName).Where(p => p.MainWindowHandle != 0).Select(p => p.Id + ":" + p.MainWindowTitle).Order(StringComparer.Ordinal));

    // ---- text: select and copy -----------------------------------------------------------------------------------------------------

    [UiFact]
    public void DraggingSelectsTextAndCtrlCCopiesExactlyThat()
    {
        using var app = Open(out var article);
        GoTo(app, article.ChapterPages[0].Page);
        const string phrase = "A page is more than the words";
        var box = Box(app, phrase);
        MikePDFApp.ClearClipboard();
        app.Drag(MikePDFApp.LeftOf(box), MikePDFApp.JustPast(box));
        app.WaitForEqual("the selection", phrase, () => app.SelectedText);
        app.Capture("text-selection");

        app.PressWithControl(VirtualKeyShort.KEY_C);
        app.WaitForEqual("the text on the clipboard", phrase, () => MikePDFApp.ClipboardText() ?? string.Empty);

        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the selection cleared", () => app.SelectedText.Length == 0);
    }

    [UiFact]
    public void DoubleClickSelectsAWordTripleClickALineAndShiftArrowsGrowTheSelection()
    {
        using var app = Open(out var article);
        GoTo(app, article.ChapterPages[0].Page);
        var word = Box(app, "thumb somewhere");
        // The first word of "thumb somewhere".
        var point = new Point(word.Left + 12, word.Top + (word.Height / 2));
        app.DoubleClickAt(point);
        app.WaitForEqual("the word", "thumb", () => app.SelectedText);

        app.TripleClickAt(point);
        app.WaitFor("the line", () => app.SelectedText.Contains("thumb", StringComparison.Ordinal) && app.SelectedText.Length > 40);

        // From a click: Shift+Right five times is five letters, Ctrl+Shift+Right goes by words.
        app.ClickAt(MikePDFApp.LeftOf(Box(app, "Wide margins")));
        for (var i = 0; i < 5; i++)
        {
            app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        }

        app.WaitForEqual("five letters", "Wide ", () => app.SelectedText);
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        app.WaitForEqual("a word more", "Wide margins", () => app.SelectedText);
    }

    [UiFact]
    public void SelectAllAndCopyTakeTheWholeDocumentInReadingOrder()
    {
        using var app = Open(out var article);
        MikePDFApp.ClearClipboard();
        app.PressWithControl(VirtualKeyShort.KEY_A);
        app.WaitFor("everything on screen selected", () => app.SelectedText.Length > 100);
        app.PressWithControl(VirtualKeyShort.KEY_C);
        app.WaitFor("the whole document on the clipboard", () => MikePDFApp.ClipboardText() is { } text && text.Contains("Reading Without Hurry", StringComparison.Ordinal) && text.Contains(ArticlePdf.Address, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        var copied = MikePDFApp.ClipboardText()!;
        Assert.Contains("\r\n", copied, StringComparison.Ordinal);
        // The chapters come in the order they are read, not the order of the content streams.
        Assert.True(copied.IndexOf("Why Margins Matter", StringComparison.Ordinal) < copied.IndexOf("Reading Without Hurry", StringComparison.Ordinal));
        Assert.True(article.PageCount > 5);
    }

    // ---- find ------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void FindCountsAndWalksTheMatchesAndHighlightsThem()
    {
        using var app = Open(out var article);
        GoTo(app, article.ChapterPages[0].Page);
        ShowText(app, "A page is more than the words");
        Thread.Sleep(1500);
        var surface = app.Require("PageSurface");
        var baseline = CountColoured(surface, IsMatchFill);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        var box = app.Require("FindBox");
        Assert.Equal("Find in document", box.Name);
        app.Type(ArticlePdf.SearchWord);

        var total = article.SearchWordCount;
        app.WaitFor($"1 of {total}", () => app.FindStatus == $"1 of {total}", TimeSpan.FromSeconds(30));
        Assert.True(total > 8);

        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor($"2 of {total}", () => app.FindStatus == $"2 of {total}");
        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.ENTER);
        app.WaitFor($"1 of {total} again", () => app.FindStatus == $"1 of {total}");
        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.ENTER);
        app.WaitFor($"{total} of {total} (round the start)", () => app.FindStatus == $"{total} of {total}");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor($"1 of {total} (round the end)", () => app.FindStatus == $"1 of {total}");

        // The matches on the page in view are painted: amber over the paper.
        app.WaitFor("highlights on the page", () => CountColoured(surface, IsMatchFill) > baseline + 40, TimeSpan.FromSeconds(20));
        app.Capture("find-highlights");

        app.Click("FindMatchCaseToggle");
        app.WaitFor("the count that matches case", () => app.FindStatus.EndsWith($" of {article.SearchWordCountExact}", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Assert.True(article.SearchWordCountExact < total);

        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the find bar closed", () => app.Find("FindBar") is null);
        app.WaitFor("the highlights gone", () => CountColoured(surface, IsMatchFill) <= baseline + 5);
        Assert.True(app.Require("PageSurface").Properties.HasKeyboardFocus.ValueOrDefault);
    }

    [UiFact]
    public void FindSaysWhenThereIsNothingAndCancellingALongSearchLeavesTheWindowAlive()
    {
        var pdf = TestPdfs.Numbered(_work, "many.pdf", 3000);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(3000);
        app.ResizeTo(Width, Height);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Require("FindBox");
        app.Type("zzzz");
        // 3,000 pages take several seconds: the count of progress shows, and Esc ends it at once.
        app.WaitFor("the progress", () => app.FindStatus.StartsWith("Searching", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the bar closed", () => app.Find("FindBar") is null);
        Assert.Equal(string.Empty, app.FindStatus);

        // The window is still alive: the page indicator answers.
        app.FocusPages();
        app.Press(VirtualKeyShort.END);
        app.WaitFor("the last page", () => app.CurrentPage == 3000);

        // A short search that finds nothing says so.
        var small = TestPdfs.Numbered(_work, "few.pdf", 5);
        using var second = MikePDFApp.Launch(small);
        second.WaitForDocument(5);
        second.PressWithControl(VirtualKeyShort.KEY_F);
        second.Require("FindBox");
        second.Type("nothing like this");
        second.WaitFor("no matches", () => second.FindStatus == "No matches", TimeSpan.FromSeconds(20));
    }

    // The fill of a search match over white paper (the page itself has a yellow rule, which is a different yellow: it is counted before the search as the baseline).
    private static bool IsMatchFill(Color c) => c.R > 235 && c.G is > 205 and < 236 && c.B is > 120 and < 185;

    // ---- what a screen reader is offered ---------------------------------------------------------------------------------------------

    [UiFact]
    public void ThePagesOfferTheirTextToAScreenReaderAndTheNewControlsHaveNames()
    {
        using var app = Open(out var article);
        Assert.True(app.PageTextSupported);
        var surface = app.Require("PageSurface");
        Assert.Equal(ControlType.Document, surface.ControlType);
        Assert.Equal("Document pages", surface.Name);
        ShowText(app, ArticlePdf.Title);
        Assert.Contains(ArticlePdf.Subtitle, app.AccessibleText, StringComparison.Ordinal);

        // Moving to another page changes what is offered.
        GoTo(app, article.ChapterPages[1].Page);
        ShowText(app, "Serif letters carry small feet");
        Assert.DoesNotContain("Notes on paper, type and patience", app.AccessibleText, StringComparison.Ordinal);

        // A selection made through the pattern is the selection the person sees, and the other way round.
        var range = app.SelectPhrase("Serif letters carry small feet");
        Assert.Equal("Serif letters carry small feet", range);
        app.WaitFor("it selected on screen", () => app.SelectedText == "Serif letters carry small feet");

        app.Press(VirtualKeyShort.F4);
        foreach (var (id, name) in SidebarNames)
        {
            Assert.Equal(name, app.Require(id).Name);
        }

        app.PressWithControl(VirtualKeyShort.KEY_F);
        foreach (var (id, name) in FindNames)
        {
            Assert.Equal(name, app.Require(id).Name);
        }

        // No resource key leaks into the UI as a name.
        foreach (var element in app.Window.FindAllDescendants())
        {
            var name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert.DoesNotMatch(@"^(Find|Sidebar|Link|Outline|Thumbnail|Banner)_\w+$", name);
        }
    }

    // ---- the pictures for the Store listing ---------------------------------------------------------------------------------------------

    [UiFact]
    public void ScreenshotsForTheStoreListing()
    {
        using var app = Open(out var article);
        GoTo(app, article.ChapterPages[0].Page);
        ShowText(app, "A page is more than the words");
        Thread.Sleep(1500); // tiles sharpen
        app.ClickPageMargin();
        app.Capture("store-01-reading");

        GoTo(app, 1);
        Thread.Sleep(1200);
        app.Click("SidebarButton");
        var list = app.Require("ThumbnailList");
        MikePDFApp.WaitForItem(list, "Page 1");
        app.WaitFor("thumbnails drawn", () => CountColoured(list) > 300, TimeSpan.FromSeconds(30));
        Thread.Sleep(1200);
        app.Capture("store-02-thumbnails");

        app.Click("BookmarksTab");
        var tree = app.Require("OutlineTree");
        MikePDFApp.WaitForItem(tree, "Why Margins Matter", ControlType.TreeItem).Patterns.ExpandCollapse.Pattern.Expand();
        MikePDFApp.WaitForItem(tree, "Room to Think", ControlType.TreeItem);
        MikePDFApp.WaitForItem(tree, "Type and Its Habits", ControlType.TreeItem).Patterns.ExpandCollapse.Pattern.Expand();
        MikePDFApp.WaitForItem(tree, "Serifs and Sans", ControlType.TreeItem);
        app.Capture("store-03-bookmarks");

        app.Click("SidebarButton");
        GoTo(app, article.ChapterPages[1].Page);
        var box = Box(app, "Serif letters carry small feet at the ends of their strokes");
        app.Drag(MikePDFApp.LeftOf(box), MikePDFApp.JustPast(box));
        app.WaitFor("a selection", () => app.SelectedText.Length > 20);
        Thread.Sleep(600);
        app.Capture("store-04-text-selection");

        app.Press(VirtualKeyShort.ESCAPE);
        GoTo(app, article.ChapterPages[0].Page);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Type(ArticlePdf.SearchWord);
        app.WaitFor("matches", () => app.FindStatus.StartsWith("1 of", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        app.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(1200);
        app.Capture("store-05-find");

        app.Press(VirtualKeyShort.ESCAPE);
        GoTo(app, ArticlePdf.ContentsPage);
        ShowText(app, "Paper, Ink and Time");
        Thread.Sleep(1200);
        app.ClickPageMargin();
        app.Capture("store-06-contents-links");
    }
}
