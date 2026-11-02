using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// The reader gaps as a person meets them (docs/features/m2-reader-gaps.md): back and forward, named destinations, the zoom and page keys, the
/// find options, page colours, the properties dialog, the copy restriction and full screen. Each journey starts its own MikePDF.
/// </summary>
public sealed partial class ReaderGapsJourneyTests : IDisposable
{
    private const int Width = 1600;
    private const int Height = 960;

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

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    private MikePDFApp Open(out ArticlePdf article)
    {
        article = ArticlePdf.Create(_work, "handbook.pdf", out var path);
        var app = MikePDFApp.Launch(path);
        app.WaitForDocument(article.PageCount);
        app.ResizeTo(Width, Height);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_1); // the whole page in view
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
        app.WaitFor("the page box focused", () => box.FindAllDescendants().Any(e => e.Properties.HasKeyboardFocus.ValueOrDefault));
        app.Type(page.ToString(CultureInfo.InvariantCulture));
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor($"page {page}", () => app.CurrentPage == page);
        app.FocusPages();
    }

    private static void Back(MikePDFApp app) => app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.LEFT);

    private static void Forward(MikePDFApp app) => app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.RIGHT);

    private static bool Enabled(MikePDFApp app, string id) => app.Require(id).IsEnabled;

    // ---- 1. back and forward -------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void H7_H8_H9_BackAndForwardFollowJumpsAndLinksButNotReading()
    {
        using var app = Open(out var article);
        Assert.Equal("Back", app.Require("BackButton").Name);
        Assert.Equal("Forward", app.Require("ForwardButton").Name);
        Assert.False(Enabled(app, "BackButton"));   // nothing to go back to yet
        Assert.False(Enabled(app, "ForwardButton"));

        GoTo(app, ArticlePdf.ContentsPage);          // a jump, from page 1
        app.WaitFor("Back enabled", () => Enabled(app, "BackButton"));
        Assert.False(Enabled(app, "ForwardButton"));

        var chapter = article.ChapterPages.Single(c => c.Title == "Paper, Ink and Time");
        app.ClickAt(MikePDFApp.CenterOf(Box(app, chapter.Title))); // a link in the contents: another jump, from page 2
        app.WaitFor($"page {chapter.Page}", () => app.CurrentPage == chapter.Page, TimeSpan.FromSeconds(20));

        // Reading is not a jump: Page Down moves whole pages here (the page fits), and none of it is remembered.
        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("a page further", () => app.CurrentPage == chapter.Page + 1);
        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("two pages further", () => app.CurrentPage == chapter.Page + 2);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("three pages further", () => app.CurrentPage == chapter.Page + 3);

        Back(app);
        app.WaitFor("back at the contents, not at a page that was merely read", () => app.CurrentPage == ArticlePdf.ContentsPage);
        Assert.True(Enabled(app, "ForwardButton"));
        Assert.True(Enabled(app, "BackButton"));

        Back(app);
        app.WaitFor("back at the cover", () => app.CurrentPage == 1);
        app.WaitFor("Back dimmed at the start", () => !Enabled(app, "BackButton"));

        Forward(app);
        app.WaitFor("forward to the contents", () => app.CurrentPage == ArticlePdf.ContentsPage);
        Forward(app);
        app.WaitFor("forward to where Back was pressed", () => app.CurrentPage == chapter.Page + 3);
        app.WaitFor("Forward dimmed at the end", () => !Enabled(app, "ForwardButton"));

        // The toolbar buttons do the same.
        app.Click("BackButton");
        app.WaitFor("the button went back", () => app.CurrentPage == ArticlePdf.ContentsPage);
        app.Click("ForwardButton");
        app.WaitFor("the button went forward", () => app.CurrentPage == chapter.Page + 3);
        app.Capture("history-buttons");

        // A new jump after going back ends the forward list.
        Back(app);
        app.WaitFor("back once more", () => app.CurrentPage == ArticlePdf.ContentsPage);
        GoTo(app, 5);
        app.WaitFor("Forward dimmed after a new jump", () => !Enabled(app, "ForwardButton"));
    }

    [UiFact]
    public void H8_GoToPageAThumbnailAndABookmarkAreJumpsToo()
    {
        using var app = Open(out var article);
        app.Click("SidebarButton");
        var list = app.Require("ThumbnailList");
        MikePDFApp.ClickItem(MikePDFApp.WaitForItem(list, "Page 5"));
        app.WaitFor("page 5", () => app.CurrentPage == 5);
        Back(app);
        app.WaitFor("back from the thumbnail", () => app.CurrentPage == 1);

        app.Click("BookmarksTab");
        var tree = app.Require("OutlineTree");
        var chapter = MikePDFApp.WaitForItem(tree, "Paper, Ink and Time", ControlType.TreeItem);
        MikePDFApp.ClickItem(chapter);
        var target = article.ChapterPages.Single(c => c.Title == "Paper, Ink and Time").Page;
        app.WaitFor($"page {target}", () => app.CurrentPage == target);
        Back(app);
        app.WaitFor("back from the bookmark", () => app.CurrentPage == 1);
        Forward(app);
        app.WaitFor("forward to the bookmark's place", () => app.CurrentPage == target);
    }

    [UiFact]
    public void H10_TheMouseBackAndForwardButtonsNavigate()
    {
        using var app = Open(out _);
        GoTo(app, 4);
        var surface = app.Require("PageSurface").BoundingRectangle;
        var over = new Point(surface.Left + (surface.Width / 2), surface.Top + (surface.Height / 2));
        NativeMouse.SideButton(over, forward: false);
        app.WaitFor("the mouse's back button went back", () => app.CurrentPage == 1);
        NativeMouse.SideButton(over, forward: true);
        app.WaitFor("the mouse's forward button went forward", () => app.CurrentPage == 4);

        // Over the toolbar as well, not only over the pages.
        var toolbar = app.Require("PageIndicator").BoundingRectangle;
        NativeMouse.SideButton(new Point(toolbar.Left + 4, toolbar.Top + 4), forward: false);
        app.WaitFor("back again, with the pointer over the toolbar", () => app.CurrentPage == 1);
    }

    [UiFact]
    public void H11_MovingThroughTheMatchesOfOneSearchIsOneStepBack()
    {
        using var app = Open(out _);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Require("FindBox");
        app.Type(ArticlePdf.SearchWord);
        app.WaitFor("a first match", () => app.FindStatus.StartsWith("1 of", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        app.WaitFor("the first match is on another page", () => app.CurrentPage > 1);
        for (var i = 0; i < 4; i++)
        {
            app.Press(VirtualKeyShort.ENTER);
        }

        app.WaitFor("the fifth match", () => app.FindStatus.StartsWith("5 of", StringComparison.Ordinal));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the find bar closed", () => app.Find("FindBar") is null);
        Back(app);
        app.WaitFor("back where the search began", () => app.CurrentPage == 1);
        app.WaitFor("and nothing more to go back to", () => !Enabled(app, "BackButton"));
    }

    // ---- 2. named destinations -----------------------------------------------------------------------------------------------------------

    [UiFact]
    public void N8_LinksAndBookmarksThatNameTheirPlaceGoThere()
    {
        var pdf = NamedDestinationPdf.Create(_work, "named.pdf");
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(4);
        app.ResizeTo(Width, Height);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_1);
        Thread.Sleep(600);

        void Follow(string line, int page)
        {
            GoTo(app, 1);
            app.ClickAt(MikePDFApp.CenterOf(Box(app, line)));
            app.WaitFor($"'{line}' to page {page}", () => app.CurrentPage == page, TimeSpan.FromSeconds(20));
        }

        Follow(NamedDestinationPdf.AlphaLine, NamedDestinationPdf.AlphaPage);       // a name in the name tree, with a point
        Follow(NamedDestinationPdf.BetaLine, NamedDestinationPdf.BetaPage);         // a GoTo action, and a dictionary value
        Follow(NamedDestinationPdf.OldLine, NamedDestinationPdf.OldPage);           // the old catalog /Dests dictionary

        // A name that does not exist leads nowhere: a click does nothing and the page stays.
        GoTo(app, 1);
        app.ClickAt(MikePDFApp.CenterOf(Box(app, NamedDestinationPdf.NowhereLine)));
        Thread.Sleep(800);
        Assert.Equal(1, app.CurrentPage);

        // The same names in the bookmarks.
        app.Click("SidebarButton");
        app.Click("BookmarksTab");
        var tree = app.Require("OutlineTree");
        MikePDFApp.ClickItem(MikePDFApp.WaitForItem(tree, "Alpha place", ControlType.TreeItem));
        app.WaitFor("the bookmark by name", () => app.CurrentPage == NamedDestinationPdf.AlphaPage);
        GoTo(app, 1);
        app.Click("BookmarksTab");
        MikePDFApp.ClickItem(MikePDFApp.WaitForItem(app.Require("OutlineTree"), "Beta place", ControlType.TreeItem));
        app.WaitFor("the bookmark by action", () => app.CurrentPage == NamedDestinationPdf.BetaPage);
        GoTo(app, 1);
        MikePDFApp.ClickItem(MikePDFApp.WaitForItem(app.Require("OutlineTree"), "Nowhere", ControlType.TreeItem));
        Thread.Sleep(800);
        Assert.Equal(1, app.CurrentPage);
    }

    // ---- 3. zoom and page keys ---------------------------------------------------------------------------------------------------------

    [UiFact]
    public void Z3_Z4_ZoomStepKeysAndPageKeys()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();

        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);
        app.PressWithControl(VirtualKeyShort.OEM_PLUS);
        app.WaitFor("Ctrl+= zooms in", () => app.ZoomPercent == 125);
        app.PressWithControl(VirtualKeyShort.OEM_MINUS);
        app.WaitFor("Ctrl+- zooms out", () => app.ZoomPercent == 100);
        app.PressWithControl(VirtualKeyShort.OEM_PLUS);
        app.PressWithControl(VirtualKeyShort.OEM_PLUS);
        app.WaitFor("two steps in", () => app.ZoomPercent == 150);
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("Ctrl+0 resets", () => app.ZoomPercent == 100);

        app.PressWithControl(VirtualKeyShort.KEY_2);
        app.WaitFor("fit width", () => app.ZoomPercent != 100);
        var width = app.ZoomPercent;
        app.PressWithControl(VirtualKeyShort.KEY_1);
        app.WaitFor("fit page is smaller than fit width", () => app.ZoomPercent < width);

        // The page fits: Page Down and Page Up move a whole page.
        app.Press(VirtualKeyShort.HOME);
        app.WaitFor("the first page", () => app.CurrentPage == 1);
        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("Page Down is the next page", () => app.CurrentPage == 2);
        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("and the next", () => app.CurrentPage == 3);
        app.Press(VirtualKeyShort.PRIOR);
        app.WaitFor("Page Up is the previous page", () => app.CurrentPage == 2);

        // The page is taller than the window: Page Down scrolls a screen, within the page, and gets to the next in the end.
        app.PressWithControl(VirtualKeyShort.KEY_2);
        app.Press(VirtualKeyShort.HOME);
        app.WaitFor("the first page again", () => app.CurrentPage == 1);
        app.Press(VirtualKeyShort.NEXT);
        Thread.Sleep(500);
        Assert.Equal(1, app.CurrentPage);
        for (var i = 0; i < 4; i++)
        {
            app.Press(VirtualKeyShort.NEXT);
        }

        app.WaitFor("the second page after a few screens", () => app.CurrentPage >= 2);
        app.Press(VirtualKeyShort.END);
        app.WaitFor("the last page", () => app.CurrentPage == 12);
    }

    [UiFact]
    public void Z5_ActualSizeIsThePhysicalSizeOfTheMonitor()
    {
        var pdf = TestPdfs.Numbered(_work, "actual.pdf", 2);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(2);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);

        var handle = new IntPtr(app.Window.Properties.NativeWindowHandle.Value);
        var monitor = MonitorFromWindow(handle, 2);
        Assert.NotEqual(0, monitor);
        var raw = GetDpiForMonitor(monitor, 2, out var rawDpi, out _) == 0 ? rawDpi : 0;
        var scale = GetDpiForWindow(handle) / 96.0;
        // What Windows reports for the monitor's own density; outside a believable range it is not trusted and the answer is 100%.
        var expected = raw is >= 72 and <= 400 ? (int)Math.Round(raw / (96.0 * scale) * 100) : 100;

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_0);
        app.WaitFor($"the physical size ({expected}% for {raw} ppi at {scale * 100:0}% scale)", () => app.ZoomPercent == expected);

        // Through the menu as well.
        app.PressWithControl(VirtualKeyShort.OEM_PLUS);
        app.WaitFor("zoomed away from it", () => app.ZoomPercent != expected);
        app.Click("ZoomButton");
        var item = app.Require("ZoomActualSize");
        Assert.Equal("Actual size", item.Name);
        app.Click("ZoomActualSize");
        app.WaitFor("the menu's actual size", () => app.ZoomPercent == expected);
    }

    // ---- 4. find options ---------------------------------------------------------------------------------------------------------------------

    private static void SetFindText(MikePDFApp app, string text)
    {
        var box = app.Require("FindBox").AsTextBox();
        box.Focus();
        box.Text = text;
        FlaUI.Core.Input.Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(400));
    }

    private static bool IsOn(MikePDFApp app, string id) => app.Require(id).Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;

    private static string CountAfter(string status) => status.Contains(" of ", StringComparison.Ordinal) ? status[(status.IndexOf(" of ", StringComparison.Ordinal) + 4)..] : status;

    [UiFact]
    public void F8_F10_WholeWordAndRegularExpressionChangeTheCountInTheLiveRegion()
    {
        using var app = Open(out var article);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        foreach (var (id, name) in new[] { ("FindWholeWordToggle", "Whole word"), ("FindRegexToggle", "Regular expression"), ("FindMatchCaseToggle", "Match case") })
        {
            Assert.Equal(name, app.Require(id).Name);
            Assert.False(IsOn(app, id));
        }

        app.Type(ArticlePdf.SearchWord);
        app.WaitFor("all the occurrences", () => app.FindStatus == $"1 of {article.SearchWordCount}", TimeSpan.FromSeconds(30));

        app.Click("FindWholeWordToggle");
        Assert.True(IsOn(app, "FindWholeWordToggle"));
        app.WaitFor("only the whole words", () => app.FindStatus == $"1 of {article.SearchWordCountWhole}", TimeSpan.FromSeconds(30));
        Assert.True(article.SearchWordCountWhole < article.SearchWordCount);
        app.Capture("find-whole-word");

        // A pattern: the same word and its plural, the way a pattern writes it. Whole word is set aside while patterns are on.
        app.Click("FindRegexToggle");
        Assert.True(IsOn(app, "FindRegexToggle"));
        Assert.False(app.Require("FindWholeWordToggle").IsEnabled);
        SetFindText(app, @"margins?\b");
        var pattern = article.CountMatches(@"margins?\b", ignoreCase: true);
        app.WaitFor($"{pattern} matches of the pattern", () => app.FindStatus == $"1 of {pattern}", TimeSpan.FromSeconds(30));
        Assert.True(pattern > article.SearchWordCountWhole);

        SetFindText(app, @"mar[a-z]+\s+of");
        var phrase = article.CountMatches(@"mar[a-z]+\s+of", ignoreCase: true);
        app.WaitFor("a pattern that spans a space", () => CountAfter(app.FindStatus) == phrase.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(30));

        // Match case still applies to a pattern.
        SetFindText(app, "MARGIN");
        var caseless = article.CountMatches("MARGIN", ignoreCase: true);
        app.WaitFor("a pattern without case", () => app.FindStatus == $"1 of {caseless}", TimeSpan.FromSeconds(30));
        app.Click("FindMatchCaseToggle");
        app.WaitFor("no match for the upper-case pattern", () => app.FindStatus == "No matches", TimeSpan.FromSeconds(30));
        app.Click("FindMatchCaseToggle");

        // Turning patterns off gives whole word back.
        app.Click("FindRegexToggle");
        Assert.True(app.Require("FindWholeWordToggle").IsEnabled);
    }

    [UiFact]
    public void F8_APatternThatIsNotValidIsSaidSoAndAHostileOneDoesNotHangTheWindow()
    {
        using var app = Open(out _);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Click("FindRegexToggle");

        SetFindText(app, "(unclosed");
        app.WaitFor("the invalid message", () => app.FindStatus == "That pattern isn't valid");
        SetFindText(app, "(?<=a)b");
        app.WaitFor("the unsupported message", () => app.FindStatus == "That pattern uses features that aren't supported");
        SetFindText(app, "(a{50}){50}");
        app.WaitFor("the too-complex message", () => app.FindStatus == "That pattern is too complex");

        // A pattern that would take a backtracking engine for ever: the answer comes, and the window keeps answering.
        SetFindText(app, "(a+)+$");
        app.WaitFor("an answer to the hostile pattern", () => app.FindStatus != string.Empty && !app.FindStatus.StartsWith("Searching", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the bar closed", () => app.Find("FindBar") is null);
        app.FocusPages();
        app.Press(VirtualKeyShort.END);
        app.WaitFor("the window still answers", () => app.CurrentPage == app.PageCount);
    }

    [UiFact]
    public void F9_F3AndShiftF3MoveBetweenMatchesFromThePagesAndOpenTheBarWhenClosed()
    {
        using var app = Open(out var article);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Type(ArticlePdf.SearchWord);
        var total = article.SearchWordCount;
        app.WaitFor("a first match", () => app.FindStatus == $"1 of {total}", TimeSpan.FromSeconds(30));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the bar closed", () => app.Find("FindBar") is null);

        // Closed: F3 opens it and goes to the first match.
        app.Press(VirtualKeyShort.F3);
        app.WaitFor("the bar open again", () => app.Find("FindBar") is not null);
        app.WaitFor("at the first match", () => app.FindStatus == $"1 of {total}", TimeSpan.FromSeconds(30));

        // With the keyboard on the pages: F3 is next, Shift+F3 is previous.
        app.FocusPages();
        app.Press(VirtualKeyShort.F3);
        app.WaitFor("F3 moved on", () => app.FindStatus == $"2 of {total}");
        app.Press(VirtualKeyShort.F3);
        app.WaitFor("and on", () => app.FindStatus == $"3 of {total}");
        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.F3);
        app.WaitFor("Shift+F3 went back", () => app.FindStatus == $"2 of {total}");
        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.F3);
        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.F3);
        app.WaitFor("round the start to the last", () => app.FindStatus == $"{total} of {total}");
    }

    // ---- 5. page colours -------------------------------------------------------------------------------------------------------------------

    /// <summary>The average colour of a few points on the paper at the middle of the page surface (the page is white; no text there).</summary>
    private static (double R, double G, double B) PaperColour(AutomationElement surface)
    {
        using var capture = FlaUI.Core.Capturing.Capture.Element(surface);
        var bitmap = capture.Bitmap;
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var dx = -30; dx <= 30; dx += 15)
        {
            for (var dy = -30; dy <= 30; dy += 15)
            {
                var c = bitmap.GetPixel((bitmap.Width / 2) + dx, (bitmap.Height / 2) + dy);
                r += c.R;
                g += c.G;
                b += c.B;
                count++;
            }
        }

        return (r / count, g / count, b / count);
    }

    private static void ChoosePageColour(MikePDFApp app, string itemId)
    {
        app.Click("PageColourButton");
        app.Click(itemId);
        Thread.Sleep(600);
    }

    [UiFact]
    public void C5_C6_C7_PageColoursChangeThePagesAndTheChoiceSurvivesARestart()
    {
        var pdf = TestPdfs.Numbered(_work, "colours.pdf", 3);
        using (var app = MikePDFApp.Launch(pdf))
        {
            app.WaitForDocument(3);
            app.ResizeTo(Width, Height);
            app.FocusPages();
            app.PressWithControl(VirtualKeyShort.KEY_2);
            Thread.Sleep(1500);
            var surface = app.Require("PageSurface");

            // The menu and its items have names and a checked state.
            Assert.Equal("Page colour", app.Require("PageColourButton").Name);
            app.Click("PageColourButton");
            Assert.Equal("Normal", app.Require("PageColourNormal").Name);
            Assert.Equal("Dark pages", app.Require("PageColourInverted").Name);
            Assert.Equal("Sepia", app.Require("PageColourSepia").Name);
            Assert.True(app.Require("PageColourNormal").Patterns.SelectionItem.IsSupported || app.Require("PageColourNormal").Patterns.Toggle.IsSupported);
            app.Press(VirtualKeyShort.ESCAPE);

            var (nr, ng, nb) = PaperColour(surface);
            Assert.True(nr > 235 && ng > 235 && nb > 235, $"normal paper is ({nr:0},{ng:0},{nb:0})");

            ChoosePageColour(app, "PageColourInverted");
            app.WaitFor("dark paper", () =>
            {
                var (r, g, b) = PaperColour(surface);
                return r < 60 && g < 60 && b < 60;
            });
            app.Capture("page-colour-dark");

            ChoosePageColour(app, "PageColourSepia");
            app.WaitFor("warm paper", () =>
            {
                var (r, g, b) = PaperColour(surface);
                return r > 225 && g > 215 && b is > 190 and < 230 && r - b > 15;
            });
            app.Capture("page-colour-sepia");

            ChoosePageColour(app, "PageColourInverted"); // leave it dark for the restart
            app.WaitFor("dark again", () => PaperColour(surface).R < 60);
            app.CloseGracefully();
        }

        using (var again = MikePDFApp.Launch(pdf))
        {
            again.WaitForDocument(3);
            again.ResizeTo(Width, Height);
            again.FocusPages();
            Thread.Sleep(1500);
            again.WaitFor("the dark pages came back by themselves", () => PaperColour(again.Require("PageSurface")).R < 60, TimeSpan.FromSeconds(20));
            ChoosePageColour(again, "PageColourNormal");
            again.WaitFor("normal paper again", () => PaperColour(again.Require("PageSurface")).R > 235);
        }
    }

    // ---- 6. document properties ------------------------------------------------------------------------------------------------------

    [UiFact]
    public void P9_P11_TheDialogShowsTheDocumentsPropertiesAndEveryRowIsNamed()
    {
        var info = "/Title (Quarterly report) /Author (A. Writer) /Subject (Figures) /Keywords (q3, finance) /Creator (Writer 7) /Producer (MikePDF test suite) "
            + "/CreationDate (D:20240115103000+10'00') /ModDate (D:20240201)";
        var pdf = TestPdfs.Numbered(_work, "props.pdf", 3, info: info);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(3);
        app.ResizeTo(Width, Height);
        app.FocusPages();
        Assert.Equal("Properties", app.Require("PropertiesButton").Name);

        app.PressWithControl(VirtualKeyShort.KEY_D);
        app.Require("PropertiesDialog");
        string Row(string id) => app.Require(id).Name;
        app.WaitFor("the rows", () => app.Find("Prop_Title") is not null, TimeSpan.FromSeconds(20));
        Assert.Equal("Title: Quarterly report", Row("Prop_Title"));
        Assert.Equal("Author: A. Writer", Row("Prop_Author"));
        Assert.Equal("Subject: Figures", Row("Prop_Subject"));
        Assert.Equal("Keywords: q3, finance", Row("Prop_Keywords"));
        Assert.Equal("File name: props.pdf", Row("Prop_FileName"));
        Assert.Equal("Pages: 3", Row("Prop_Pages"));
        Assert.Equal("PDF version: 1.7", Row("Prop_Version"));
        Assert.Equal("Tagged PDF: No", Row("Prop_Tagged"));
        Assert.Equal("Created with: Writer 7", Row("Prop_Creator"));
        Assert.Equal("PDF producer: MikePDF test suite", Row("Prop_Producer"));
        Assert.Equal("Contains scripts: No", Row("Prop_Scripts"));
        Assert.Equal("Encryption: None", Row("Prop_Encryption"));
        Assert.Equal("Copy text: Allowed", Row("Prop_Perm_Copy"));
        Assert.Contains("612 x 792 pt", Row("Prop_PageSize"), StringComparison.Ordinal);
        Assert.Contains("Letter", Row("Prop_PageSize"), StringComparison.Ordinal);

        // Dates in the user's own format (long date and short time, local time zone).
        var created = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(10)).ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        var modified = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        Assert.Equal("Created: " + created, Row("Prop_Created"));
        Assert.Equal("Modified: " + modified, Row("Prop_Modified"));

        // The file size is the file's.
        var bytes = new FileInfo(pdf).Length;
        Assert.EndsWith(bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes)", Row("Prop_FileSize"), StringComparison.Ordinal);
        Assert.Contains("Helvetica", Row("Prop_Fonts"), StringComparison.Ordinal);
        app.Capture("properties-dialog");

        // Close dismisses it, and so does Esc.
        app.Window.FindFirstDescendant(cf => cf.ByName("Close"))!.AsButton().Invoke();
        app.WaitFor("the dialog closed", () => app.Find("PropertiesDialog") is null);
        app.Click("PropertiesButton");
        app.Require("PropertiesDialog");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the dialog closed with Esc", () => app.Find("PropertiesDialog") is null);
        Assert.True(app.Require("PageSurface").Properties.HasKeyboardFocus.ValueOrDefault);
    }

    [UiFact]
    public void P10_ADocumentThatForbidsCopyingStopsCtrlCAndSaysWhyUnlessTheOwnerPasswordWasUsed()
    {
        using (var app = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf")))
        {
            app.Require("PasswordDialog");
            app.Type("1234"); // the user password: the file's restrictions apply
            app.Press(VirtualKeyShort.ENTER);
            app.WaitForDocument(1);
            app.FocusPages();
            MikePDFApp.ClearClipboard();
            app.PressWithControl(VirtualKeyShort.KEY_A);
            app.PressWithControl(VirtualKeyShort.KEY_C);
            app.WaitFor("the banner that says why", () => app.Window.FindFirstDescendant(cf => cf.ByName("Copying isn't allowed")) is not null, TimeSpan.FromSeconds(10));
            Thread.Sleep(700);
            Assert.Null(MikePDFApp.ClipboardText());

            app.PressWithControl(VirtualKeyShort.KEY_D);
            app.WaitFor("the rows", () => app.Find("Prop_Encryption") is not null, TimeSpan.FromSeconds(20));
            Assert.Equal("Copy text: Not allowed", app.Require("Prop_Perm_Copy").Name);
            Assert.Equal("Encryption: Password security, 128-bit RC4 or AES (revision 4)", app.Require("Prop_Encryption").Name);
            Assert.False(app.Exists("Prop_OwnerPassword"));
            app.Press(VirtualKeyShort.ESCAPE);
        }

        using var owner = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf"));
        owner.Require("PasswordDialog");
        owner.Type("5678"); // the owner password: nothing is forbidden
        owner.Press(VirtualKeyShort.ENTER);
        owner.WaitForDocument(1);
        owner.FocusPages();
        MikePDFApp.ClearClipboard();
        owner.PressWithControl(VirtualKeyShort.KEY_A);
        owner.PressWithControl(VirtualKeyShort.KEY_C);
        owner.WaitFor("the text on the clipboard", () => !string.IsNullOrEmpty(MikePDFApp.ClipboardText()), TimeSpan.FromSeconds(10));
        owner.PressWithControl(VirtualKeyShort.KEY_D);
        owner.WaitFor("the rows", () => owner.Find("Prop_Encryption") is not null, TimeSpan.FromSeconds(20));
        Assert.Equal("Copy text: Allowed", owner.Require("Prop_Perm_Copy").Name);
        Assert.Equal("Opened with: The owner password: no restrictions apply", owner.Require("Prop_OwnerPassword").Name);
    }

    // ---- 7. full screen -----------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void S1_S4_F11HidesTheChromeAndEscLeaves()
    {
        var pdf = TestPdfs.Numbered(_work, "full.pdf", 3);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(3);
        app.ResizeTo(Width, Height);
        app.FocusPages();
        Assert.True(app.Exists("OpenButton"));
        var normal = app.Window.BoundingRectangle;

        app.Press(VirtualKeyShort.F11);
        app.WaitFor("the chrome gone", () => !app.Exists("OpenButton") && !app.Exists("ZoomButton"));
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        app.WaitFor("the window covering the monitor", () => app.Window.BoundingRectangle.Width >= screenWidth && app.Window.BoundingRectangle.Height >= screenHeight);
        app.WaitFor("the announcement", () => app.Window.FindFirstDescendant(cf => cf.ByName("Full screen")) is not null);
        app.Capture("full-screen");
        Assert.True(app.Exists("PageSurface"));

        // The find bar is still there; the first Esc closes it, the second leaves.
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Require("FindBox");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the find bar closed", () => app.Find("FindBar") is null);
        Assert.False(app.Exists("OpenButton"));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the chrome back", () => app.Exists("OpenButton") && app.Exists("ZoomButton"));
        app.WaitFor("the window smaller than the monitor again", () => app.Window.BoundingRectangle.Width < screenWidth || app.Window.BoundingRectangle.Height < screenHeight);
        Assert.InRange(app.Window.BoundingRectangle.Width, normal.Width - 40, normal.Width + 40);

        // F11 toggles, and the toolbar button does the same.
        app.Press(VirtualKeyShort.F11);
        app.WaitFor("full screen by F11", () => !app.Exists("OpenButton"));
        app.Press(VirtualKeyShort.F11);
        app.WaitFor("and out by F11", () => app.Exists("OpenButton"));
        app.Click("FullScreenButton");
        app.WaitFor("full screen by the button", () => !app.Exists("OpenButton"));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("out by Esc", () => app.Exists("OpenButton"));
        Assert.Equal("Full screen", app.Require("FullScreenButton").Name);
    }
}
