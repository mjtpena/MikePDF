using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// The page view modes as a person meets them (docs/features/page-view-modes.md, PV-xx): single, two pages and two pages with a cover, continuous or one page at a time, the
/// reading direction, and presentation. Each journey starts its own MikePDF.
/// </summary>
public sealed class ViewModesJourneyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ViewModesJourneyTests()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings(); // each journey starts from the defaults, whatever an earlier one left (the dark-pages journey leaves page colour dark)
    }

    public void Dispose()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings();
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (IOException)
        {
            // Still held for a moment by the process that just ended.
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private MikePDFApp Open(string name, int pages, string catalogExtra = "")
    {
        var pdf = TestPdfs.Numbered(_work, name, pages, catalogExtra: catalogExtra);
        var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(pages);
        app.ResizeTo(1600, 960);
        app.FocusPages();
        return app;
    }

    private static string Layout(MikePDFApp app) => app.HelpText("ViewButton");

    private static void WaitForLayout(MikePDFApp app, string layout) =>
        app.WaitFor("the layout \"" + layout + "\"", () => Layout(app).Contains(layout, StringComparison.Ordinal));

    private static void WaitForPages(MikePDFApp app, int first, int last) =>
        app.WaitFor($"pages {first}-{last}", () =>
        {
            var (a, b, _) = app.PagesInView();
            return a == first && b == last;
        });

    /// <summary>The box of the big page number on the screen; waits until the page's text has come.</summary>
    private static Rectangle Box(MikePDFApp app, string number)
    {
        app.WaitFor($"'{number}' in the text of the pages in view", () => app.AccessibleText.Contains(number, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Rectangle? box = null;
        app.WaitFor($"a box for '{number}'", () => (box = app.ScreenBoxOf(number)) is not null);
        return box!.Value;
    }

    // Moves the pointer the way a mouse does, in steps WinUI sees as movement (a jump by SetCursorPos is not one).
    private static void MoveMouseTo(Point target)
    {
        NativeMouse.MoveTo(new Point(target.X - 12, target.Y - 6));
        Thread.Sleep(80);
        NativeMouse.MoveTo(new Point(target.X - 4, target.Y - 2));
        Thread.Sleep(80);
        NativeMouse.MoveTo(target);
        Thread.Sleep(120);
    }

    private static Point Centre(AutomationElement element)
    {
        var r = element.BoundingRectangle;
        return new Point(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
    }

    // ---- the modes ---------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PV20_PV01_TheModesAreChosenWithTheKeyboardAndFacingPagesStandSideBySide()
    {
        using var app = Open("modes.pdf", 8);
        WaitForLayout(app, "Single page, continuous");
        WaitForPages(app, 1, 1);

        app.PressWithControl(VirtualKeyShort.KEY_5);
        WaitForLayout(app, "Two pages, continuous");
        WaitForPages(app, 1, 2);
        var one = Box(app, "1");
        var two = Box(app, "2");
        Assert.True(two.Left > one.Right, $"page 2 ({two}) is right of page 1 ({one})");
        Assert.InRange(Math.Abs((one.Top + (one.Height / 2)) - (two.Top + (two.Height / 2))), 0, 30);
        app.Capture("view-two-pages");

        // A cover: page 1 alone, on the right half, as in a book.
        app.PressWithControl(VirtualKeyShort.KEY_6);
        WaitForLayout(app, "Two pages with cover, continuous");
        WaitForPages(app, 1, 1);
        var middle = Centre(app.Require("PageSurface")).X;
        Assert.True(Box(app, "1").Left + (Box(app, "1").Width / 2) > middle, "the cover is on the right half");
        app.Capture("view-two-pages-with-cover");

        app.PressWithControl(VirtualKeyShort.KEY_4);
        WaitForLayout(app, "Single page, continuous");
        WaitForPages(app, 1, 1);

        // One page at a time, and back to scrolling.
        app.PressWithControl(VirtualKeyShort.KEY_7);
        WaitForLayout(app, "Single page, one page at a time");
        app.PressWithControl(VirtualKeyShort.KEY_7);
        WaitForLayout(app, "Single page, continuous");
    }

    [UiFact]
    public void PV06_TheFirstVisiblePageStaysInViewWhateverTheModeChangesTo()
    {
        using var app = Open("stay.pdf", 9);
        app.PressWithControl(VirtualKeyShort.KEY_G);
        app.Require("GoToPageBox");
        app.Type("7");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("page 7", () => app.CurrentPage == 7);
        app.FocusPages();

        foreach (var key in new[] { VirtualKeyShort.KEY_5, VirtualKeyShort.KEY_6, VirtualKeyShort.KEY_7, VirtualKeyShort.KEY_4, VirtualKeyShort.KEY_7, VirtualKeyShort.KEY_6, VirtualKeyShort.KEY_5 })
        {
            app.PressWithControl(key);
            app.WaitFor("the layout settled", () => app.PagesInView().First > 0);
            Thread.Sleep(300);
            var (first, last, _) = app.PagesInView();
            Assert.InRange(7, first, last);
        }
    }

    [UiFact]
    public void PV01_TheViewMenuHoldsTheModesAsARadioGroupAndAToggleAndNamesThem()
    {
        using var app = Open("menu.pdf", 6);
        app.Click("ViewButton");
        Assert.Equal("Single page", app.Require("LayoutSingle").Name);
        Assert.Equal("Two pages", app.Require("LayoutTwo").Name);
        Assert.Equal("Two pages with cover", app.Require("LayoutTwoCover").Name);
        Assert.Equal("Continuous scrolling", app.Require("LayoutContinuous").Name);
        Assert.True(IsChecked(app.Require("LayoutSingle")));
        Assert.False(IsChecked(app.Require("LayoutTwo")));
        Assert.True(IsChecked(app.Require("LayoutContinuous")));
        Assert.False(app.Require("LayoutCoverPage").IsEnabled); // only meaningful with two pages
        Assert.Equal("Ctrl+5", app.Require("LayoutTwo").Properties.AcceleratorKey.ValueOrDefault);
        Assert.Equal("Ctrl+L", app.Require("ViewPresentation").Properties.AcceleratorKey.ValueOrDefault);
        app.Click("LayoutTwo");
        WaitForLayout(app, "Two pages, continuous");

        app.Click("ViewButton");
        Assert.True(IsChecked(app.Require("LayoutTwo")));
        Assert.False(IsChecked(app.Require("LayoutSingle")));
        Assert.True(app.Require("LayoutCoverPage").IsEnabled);
        app.Click("LayoutCoverPage"); // the cover toggle: the first page alone
        WaitForLayout(app, "Two pages with cover, continuous");
        WaitForPages(app, 1, 1);

        app.Click("ViewButton");
        Assert.True(IsChecked(app.Require("LayoutTwoCover")));
        Assert.True(IsChecked(app.Require("LayoutCoverPage")));
        app.Click("LayoutContinuous");
        WaitForLayout(app, "Two pages with cover, one page at a time");
        Assert.Equal("View", app.Require("ViewButton").Name);
    }

    private static bool IsChecked(AutomationElement item) =>
        item.Patterns.Toggle.IsSupported ? item.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On : item.Patterns.SelectionItem.Pattern.IsSelected.Value;

    // ---- one page at a time -----------------------------------------------------------------------------------------------------------------------------

    [UiFact(ciQuarantine: "the mouse-wheel notch does not reach the app on the hosted runners (the first notch never turned the page in 4 of 5 runs, with the pointer over the page and the app idle); keys, clicks and drags work. Roadmap: CI UI journeys")]
    public void PV08_PV20_InThePagedFlowTheKeysTurnPagesAndTheWheelNeedsOneNotchMore()
    {
        using var app = Open("paged.pdf", 6);
        app.PressWithControl(VirtualKeyShort.KEY_7);
        WaitForLayout(app, "one page at a time");
        app.PressWithControl(VirtualKeyShort.KEY_1); // the whole page

        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("page 2 by Page Down", () => app.CurrentPage == 2);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("page 3 by Right", () => app.CurrentPage == 3);
        app.Press(VirtualKeyShort.DOWN);
        app.WaitFor("page 4 by Down (the page fits)", () => app.CurrentPage == 4);
        app.Press(VirtualKeyShort.SPACE);
        app.WaitFor("page 5 by Space", () => app.CurrentPage == 5);
        app.Press(VirtualKeyShort.PRIOR);
        app.WaitFor("page 4 by Page Up", () => app.CurrentPage == 4);
        app.Press(VirtualKeyShort.LEFT);
        app.WaitFor("page 3 by Left", () => app.CurrentPage == 3);
        app.Press(VirtualKeyShort.END);
        app.WaitFor("the last page", () => app.CurrentPage == 6);
        app.Press(VirtualKeyShort.NEXT);
        Thread.Sleep(400);
        Assert.Equal(6, app.CurrentPage); // nothing after the last
        app.Press(VirtualKeyShort.HOME);
        app.WaitFor("the first page", () => app.CurrentPage == 1);

        // The wheel: a page that fits turns at each notch (never twice in a flick).
        MoveMouseTo(Centre(app.Require("PageSurface")));
        Mouse.Scroll(-1);
        app.WaitFor("page 2 by the wheel", () => app.CurrentPage == 2);
        Mouse.Scroll(1);
        Thread.Sleep(400);
        Assert.Equal(2, app.CurrentPage); // within the debounce: ignored
        Thread.Sleep(400);
        Mouse.Scroll(1);
        app.WaitFor("back to page 1 by the wheel", () => app.CurrentPage == 1);

        // At 100% a page is taller than the window: the wheel scrolls within it, and only the notch after the end turns the page.
        app.PressWithControl(VirtualKeyShort.KEY_0);
        Thread.Sleep(500);
        var notches = 0;
        while (app.CurrentPage == 1 && notches < 20)
        {
            Mouse.Scroll(-1);
            Thread.Sleep(350);
            notches++;
        }

        Assert.Equal(2, app.CurrentPage);
        Assert.True(notches >= 3, $"the page scrolled before it turned ({notches} notches)");
    }

    [UiFact]
    public void PV08_PV10_AFacingSpreadIsTurnedWholeAndBothPagesAreInTheTextAndTheThumbnailsAreMarked()
    {
        using var app = Open("spreads.pdf", 8);
        app.PressWithControl(VirtualKeyShort.KEY_5);
        app.PressWithControl(VirtualKeyShort.KEY_7);
        app.PressWithControl(VirtualKeyShort.KEY_1);
        WaitForLayout(app, "Two pages, one page at a time");
        WaitForPages(app, 1, 2);
        Box(app, "1");
        Box(app, "2");
        Assert.Contains("1", app.AccessibleText, StringComparison.Ordinal); // the text of both pages, in page order
        Assert.True(app.AccessibleText.IndexOf('1', StringComparison.Ordinal) < app.AccessibleText.IndexOf('2', StringComparison.Ordinal));

        app.Press(VirtualKeyShort.NEXT);
        WaitForPages(app, 3, 4);
        Assert.Contains("Pages 3 and 4 of 8", app.PageIndicatorName, StringComparison.Ordinal);
        app.Press(VirtualKeyShort.END);
        WaitForPages(app, 7, 8);
        app.Press(VirtualKeyShort.HOME);
        WaitForPages(app, 1, 2);

        // The thumbnail pane marks both pages of the spread.
        app.Press(VirtualKeyShort.F4);
        var list = app.Require("ThumbnailList");
        app.WaitFor("two thumbnails selected", () => list.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)).Count(i => i.Patterns.SelectionItem.IsSupported && i.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault) == 2);
    }

    // ---- the reading direction -----------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PV18_ARightToLeftDocumentPutsPageOneOnTheRightAndTheUsersChoiceWins()
    {
        using var app = Open("rtl.pdf", 6, "/ViewerPreferences << /Direction /R2L >>");
        app.PressWithControl(VirtualKeyShort.KEY_5);
        WaitForPages(app, 1, 2);
        var one = Box(app, "1");
        var two = Box(app, "2");
        Assert.True(two.Right < one.Left, $"page 2 ({two}) is left of page 1 ({one}) for a right-to-left binding");

        app.Click("ViewButton");
        app.Click("DirectionMenu");
        app.Click("DirectionLtr");
        app.WaitFor("left to right", () => Box(app, "1").Left < Box(app, "2").Left);

        app.Click("ViewButton");
        app.Click("DirectionMenu");
        app.Click("DirectionAuto");
        app.WaitFor("the document's own direction again", () => Box(app, "1").Left > Box(app, "2").Left);
    }

    [UiFact]
    public void PV18_InARightToLeftSpreadTheRightArrowGoesBack()
    {
        using var app = Open("rtl-keys.pdf", 6, "/ViewerPreferences << /Direction /R2L >>");
        app.PressWithControl(VirtualKeyShort.KEY_5);
        app.PressWithControl(VirtualKeyShort.KEY_7);
        app.PressWithControl(VirtualKeyShort.KEY_1);
        WaitForPages(app, 1, 2);
        app.Press(VirtualKeyShort.LEFT); // the next spread lies to the left
        WaitForPages(app, 3, 4);
        app.Press(VirtualKeyShort.RIGHT);
        WaitForPages(app, 1, 2);
    }

    // ---- remembered ----------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PV07_TheModeIsRememberedForTheDocumentAndInTheSessionFile()
    {
        var pdf = TestPdfs.Numbered(_work, "remember.pdf", 8);
        var other = TestPdfs.Numbered(_work, "other.pdf", 3);
        using (var app = MikePDFApp.Launch(pdf, restore: "Always"))
        {
            app.WaitForDocument(8);
            app.ResizeTo(1600, 960);
            app.FocusPages();
            app.PressWithControl(VirtualKeyShort.KEY_6);
            app.PressWithControl(VirtualKeyShort.KEY_7);
            WaitForLayout(app, "Two pages with cover, one page at a time");
            app.CloseGracefully();
        }

        var session = System.Text.Encoding.UTF8.GetString(WaitForSession(text => text.Contains("remember.pdf", StringComparison.Ordinal) && text.Contains("\"cleanExit\":true", StringComparison.Ordinal)));
        Assert.Contains("\"arrangement\":\"FacingWithCover\"", session, StringComparison.Ordinal);
        Assert.Contains("\"flow\":\"Paged\"", session, StringComparison.Ordinal);

        // The session brings it back, with no file named...
        using (var app = MikePDFApp.Launch(restore: "Always"))
        {
            app.WaitForDocument(8);
            WaitForLayout(app, "Two pages with cover, one page at a time");
            app.CloseGracefully();
        }

        // ...and so does the document, opened by name with no session; another document is untouched.
        AppData.DeleteSession();
        using (var app = MikePDFApp.Launch(pdf))
        {
            app.WaitForDocument(8);
            WaitForLayout(app, "Two pages with cover, one page at a time");
        }

        using (var app = MikePDFApp.Launch(other))
        {
            app.WaitForDocument(3);
            WaitForLayout(app, "Single page, continuous");
        }
    }

    private static byte[] WaitForSession(Func<string, bool> condition)
    {
        byte[]? found = null;
        FlaUI.Core.Tools.Retry.WhileFalse(() => AppData.ReadSession() is { } bytes && condition(System.Text.Encoding.UTF8.GetString(bytes)) && (found = bytes) is not null, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200));
        return found ?? throw new TimeoutException("session.json never looked as expected");
    }

    // ---- announcements -------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PV19_AModeChangeAndEachTurnAreAnnouncedOnceEach()
    {
        using var app = Open("announce.pdf", 8);
        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);

        app.PressWithControl(VirtualKeyShort.KEY_6);
        app.WaitFor("the mode announced", () => Heard(heard, "Two pages with cover, continuous").Count == 1);
        Assert.Contains("Page 1 of 8", Heard(heard, "Two pages with cover, continuous")[0], StringComparison.Ordinal);
        Thread.Sleep(600);
        Assert.Single(Heard(heard, "Two pages with cover"));

        app.PressWithControl(VirtualKeyShort.KEY_7);
        app.WaitFor("the flow announced", () => Heard(heard, "one page at a time").Count == 1);
        app.PressWithControl(VirtualKeyShort.KEY_1);
        Thread.Sleep(400);
        lock (heard)
        {
            heard.Clear();
        }

        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("a turn announced", () => Heard(heard, "Pages 2 and 3 of 8").Count == 1);
        Thread.Sleep(800);
        Assert.Single(Heard(heard, "Pages 2 and 3 of 8"));
        app.Press(VirtualKeyShort.NEXT);
        app.WaitFor("the next turn", () => Heard(heard, "Pages 4 and 5 of 8").Count == 1);
        Thread.Sleep(800);
        lock (heard)
        {
            Assert.Equal(2, heard.Count(h => h.Activity == "ViewAnnouncement"));
        }
    }

    private static List<string> Heard(List<(string Text, string Activity)> heard, string fragment)
    {
        lock (heard)
        {
            return [.. heard.Where(h => h.Text.Contains(fragment, StringComparison.Ordinal)).Select(h => h.Text)];
        }
    }

    // ---- presentation --------------------------------------------------------------------------------------------------------------------------------

    private static string PresentationName(MikePDFApp app) => app.Find("PresentationSurface")?.Name ?? string.Empty;

    private static void WaitForSlide(MikePDFApp app, int page, int total) =>
        app.WaitFor($"slide {page}", () => PresentationName(app) == $"Presentation, page {page} of {total}");

    [UiFact]
    public void PV14_PV16_PV20_PresentationCoversTheScreenWithNothingElseAndEscGivesBackTheModePageAndZoom()
    {
        var dump = "present-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        var pdf = TestPdfs.Numbered(_work, "slides.pdf", 5);
        using var app = MikePDFApp.Launch(pdf, environment: new Dictionary<string, string> { ["MIKEPDF_TEST_PRESENT_DUMP"] = dump });
        app.WaitForDocument(5);
        app.ResizeTo(1600, 960);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_6);
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.PressWithControl(VirtualKeyShort.KEY_G);
        app.Require("GoToPageBox");
        app.Type("3");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("page 3", () => app.PagesInView().First == 2 || app.PagesInView().First == 3);
        app.FocusPages();
        var zoomBefore = app.ZoomPercent;
        var layoutBefore = Layout(app);
        var pageBefore = app.CurrentPage;

        app.PressWithControl(VirtualKeyShort.KEY_L);
        app.WaitFor("the presentation", () => app.Exists("PresentationSurface"));
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        app.WaitFor("the window covering the monitor", () => app.Window.BoundingRectangle.Width >= screenWidth && app.Window.BoundingRectangle.Height >= screenHeight);
        WaitForSlide(app, pageBefore, 5);

        // PV-16: no title bar, command bar, tabs, side pane or scroll bars: only the surface (and, for a moment, the bar with the page number and Exit).
        foreach (var id in new[] { "OpenButton", "ZoomButton", "PageIndicator", "DocumentTabs", "SidebarButton", "ViewButton", "FindButton" })
        {
            Assert.False(app.Exists(id), id + " is not in the presentation");
        }

        Assert.Empty(app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ScrollBar)));
        app.Capture("presentation");

        // The page sits on black, centred.
        var surface = app.Require("PresentationSurface");
        using (var capture = FlaUI.Core.Capturing.Capture.Element(surface))
        {
            var corner = capture.Bitmap.GetPixel(10, capture.Bitmap.Height / 2);
            Assert.True(corner.R < 12 && corner.G < 12 && corner.B < 12, $"the surround is black, not ({corner.R},{corner.G},{corner.B})");
        }

        // The display is kept awake while presenting.
        var dumpPath = Path.Combine(AppData.PrivateFolder() ?? throw new InvalidOperationException("no private folder"), dump);
        app.WaitFor("keep-awake asked for", () => ReadLines(dumpPath).SequenceEqual(["awake"]));

        // Commands that edit or change the view are off in a presentation; the palette is closed to it.
        app.PressWithControl(VirtualKeyShort.KEY_5);
        Thread.Sleep(300);
        Assert.True(app.Exists("PresentationSurface"));

        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("out of the presentation", () => app.Exists("OpenButton") && !app.Exists("PresentationSurface"));
        app.WaitFor("the window smaller than the monitor again", () => app.Window.BoundingRectangle.Width < screenWidth || app.Window.BoundingRectangle.Height < screenHeight);
        app.WaitFor("keep-awake given back", () => ReadLines(dumpPath).SequenceEqual(["awake", "asleep"]));
        Assert.Equal(layoutBefore, Layout(app));
        Assert.Equal(zoomBefore, app.ZoomPercent);
        Assert.Equal(pageBefore, app.CurrentPage);
        Assert.True(app.Exists("PageSurface"));
        File.Delete(dumpPath);
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    [UiFact]
    public void PV15_EveryKeyOfThePresentationMapDoesItsJob()
    {
        var pdf = TestPdfs.Numbered(_work, "keys.pdf", 9);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(9);
        app.FocusPages();
        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);
        app.PressWithControl(VirtualKeyShort.KEY_L);
        WaitForSlide(app, 1, 9);
        Assert.Single(Heard(heard, "Presentation mode. Page 1 of 9. Press Escape to exit."));

        void Forward(string description, Action press, int to)
        {
            press();
            WaitForSlide(app, to, 9);
            Assert.True(to > 0, description);
        }

        Forward("Right", () => app.Press(VirtualKeyShort.RIGHT), 2);
        Forward("Down", () => app.Press(VirtualKeyShort.DOWN), 3);
        Forward("Page Down", () => app.Press(VirtualKeyShort.NEXT), 4);
        Forward("Space", () => app.Press(VirtualKeyShort.SPACE), 5);
        Forward("Enter", () => app.Press(VirtualKeyShort.ENTER), 6);
        Forward("N", () => app.Press(VirtualKeyShort.KEY_N), 7);
        Forward("Left", () => app.Press(VirtualKeyShort.LEFT), 6);
        Forward("Up", () => app.Press(VirtualKeyShort.UP), 5);
        Forward("Page Up", () => app.Press(VirtualKeyShort.PRIOR), 4);
        Forward("Shift+Space", () => app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.SPACE), 3);
        Forward("Backspace", () => app.Press(VirtualKeyShort.BACK), 2);
        Forward("P", () => app.Press(VirtualKeyShort.KEY_P), 1);
        Forward("End", () => app.Press(VirtualKeyShort.END), 9);
        Forward("Home", () => app.Press(VirtualKeyShort.HOME), 1);

        // Digits then Enter go to a page; Esc drops a number being typed instead of leaving.
        app.Type("5");
        app.Press(VirtualKeyShort.ENTER);
        WaitForSlide(app, 5, 9);
        app.Type("8");
        app.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
        Assert.True(app.Exists("PresentationSurface"), "the first Esc only drops the typed number");
        lock (heard)
        {
            heard.Clear();
        }

        app.Press(VirtualKeyShort.ENTER);
        WaitForSlide(app, 6, 9); // and the 8 was dropped: Enter is just "next"

        // A page turn is announced once: "Page 6 of 9".
        app.WaitFor("the turn announced", () => Heard(heard, "Page 6 of 9").Count >= 1);
        Thread.Sleep(600);
        Assert.Single(Heard(heard, "Page 6 of 9"));

        // Black and white screens, and any navigation key brings the page back.
        app.Press(VirtualKeyShort.KEY_B);
        app.WaitFor("the screen black", () => Heard(heard, "Screen black").Count == 1);
        app.Press(VirtualKeyShort.KEY_W);
        app.WaitFor("the screen white", () => Heard(heard, "Screen white").Count == 1);
        app.Press(VirtualKeyShort.OEM_PERIOD);
        app.WaitFor("black by the full stop", () => Heard(heard, "Screen black").Count == 2);
        app.Press(VirtualKeyShort.OEM_COMMA);
        app.WaitFor("white by the comma", () => Heard(heard, "Screen white").Count == 2);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("the page restored", () => Heard(heard, "Screen restored").Count == 1);
        WaitForSlide(app, 6, 9); // the key brought the page back; it did not also turn it

        app.Press(VirtualKeyShort.F11);
        app.WaitFor("F11 leaves", () => !app.Exists("PresentationSurface") && app.Exists("OpenButton"));
        Assert.Single(Heard(heard, "Presentation ended. Page 6 of 9."));
    }

    [UiFact]
    public void PV15_TheMouseTurnsPagesAndAnInternalLinkIsFollowed()
    {
        var pdf = NamedDestinationPdf.Create(_work, "links.pdf");
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(4);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_L);
        WaitForSlide(app, 1, 4);
        var link = app.ScreenBoxOf(NamedDestinationPdf.AlphaLine);
        app.WaitFor("the link text", () => (link = app.ScreenBoxOf(NamedDestinationPdf.AlphaLine)) is not null);
        Thread.Sleep(500); // the links of the page have come
        Mouse.Click(MikePDFApp.CenterOf(link!.Value));
        WaitForSlide(app, NamedDestinationPdf.AlphaPage, 4);

        var surface = Centre(app.Require("PresentationSurface"));
        Mouse.RightClick(new Point(surface.X, surface.Y + 300));
        WaitForSlide(app, NamedDestinationPdf.AlphaPage - 1, 4);
        Mouse.Click(new Point(surface.X, surface.Y + 300));
        WaitForSlide(app, NamedDestinationPdf.AlphaPage, 4);

        Mouse.Scroll(-1);
        WaitForSlide(app, NamedDestinationPdf.AlphaPage + 1, 4);
        Thread.Sleep(400);
        Mouse.Scroll(1);
        WaitForSlide(app, NamedDestinationPdf.AlphaPage, 4);
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("out", () => app.Exists("OpenButton"));
    }

    [UiFact]
    public void PV15_TheExitButtonAppearsWithTheMouseAndEndsThePresentation()
    {
        using var app = Open("exit.pdf", 3);
        MoveMouseTo(Centre(app.Require("PageSurface"))); // away from where the bar will be
        app.PressWithControl(VirtualKeyShort.KEY_L);
        WaitForSlide(app, 1, 3);
        app.WaitFor("the bar with Exit, at the start", () => app.Exists("PresentationExit"));
        app.WaitFor("the bar gone again", () => !app.Exists("PresentationExit"), TimeSpan.FromSeconds(10));
        MoveMouseTo(Centre(app.Require("PresentationSurface")));
        MoveMouseTo(new Point(Centre(app.Require("PresentationSurface")).X + 40, Centre(app.Require("PresentationSurface")).Y));
        app.WaitFor("the bar back on pointer movement", () => app.Exists("PresentationExit"));
        Assert.Equal("Exit presentation", app.Require("PresentationExit").Name);
        Assert.Contains("1 / 3", app.Require("PresentationPage").Name, StringComparison.Ordinal);
        app.Click("PresentationExit");
        app.WaitFor("out of the presentation", () => app.Exists("OpenButton") && !app.Exists("PresentationSurface"));
    }

    // ---- dark pages (the engine draws them; the GPU must not invert them again) --------------------------------------------------------------------------------

    private static (int Green, double Brightness) Look(AutomationElement surface)
    {
        using var capture = FlaUI.Core.Capturing.Capture.Element(surface);
        var bitmap = capture.Bitmap;
        var green = 0;
        double total = 0;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y += 3)
        {
            for (var x = 0; x < bitmap.Width; x += 3)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.G > c.R + 60 && c.G > c.B + 60)
                {
                    green++;
                }

                total += (c.R + c.G + c.B) / 3.0;
                count++;
            }
        }

        return (green, total / count);
    }

    [UiFact]
    public void DarkPagesAreDrawnByTheEngineSoAPhotographKeepsItsColoursAndSepiaStaysOnTheGpu()
    {
        var pdf = TestPdfs.WithPhoto(_work, "photo.pdf");
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(1);
        app.ResizeTo(1600, 960);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_1);
        var surface = app.Require("PageSurface");
        Thread.Sleep(1200);
        var (normalGreen, normalLight) = Look(surface);
        Assert.True(normalGreen > 100, $"the photograph's green corner shows ({normalGreen} pixels)");

        app.Click("PageColourButton");
        app.Click("PageColourInverted");
        app.WaitFor("dark pages", () => Look(surface).Brightness < normalLight - 40, TimeSpan.FromSeconds(20));
        Thread.Sleep(800);
        var (darkGreen, darkLight) = Look(surface);
        Assert.True(darkGreen > normalGreen / 2, $"the photograph kept its colours: {darkGreen} green pixels dark against {normalGreen} normal (inverted twice or on the GPU it would have none)");
        app.Capture("dark-pages-photo");

        // Back to normal, and sepia (the GPU's) still tints the photograph.
        app.Click("PageColourButton");
        app.Click("PageColourNormal");
        app.WaitFor("normal again", () => Look(surface).Brightness > normalLight - 15, TimeSpan.FromSeconds(20));
        Assert.True(Look(surface).Green > normalGreen / 2);
        app.Click("PageColourButton");
        app.Click("PageColourSepia");
        app.WaitFor("sepia", () => Look(surface).Green < normalGreen, TimeSpan.FromSeconds(20));
        _ = darkLight;
    }

    [UiFact]
    public void AboutShowsThePdfiumVersionWithNoDocumentOpen()
    {
        using var app = MikePDFApp.Launch();
        app.Palette.Run("about", "About MikePDF,");
        app.WaitFor("the dialog", () => app.Exists("AboutDialog"));
        app.WaitFor("the version", () => app.Require("AboutPdfium").Name.StartsWith("PDFium version ", StringComparison.Ordinal) && !app.Require("AboutPdfium").Name.Contains("reading", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Assert.Matches(@"PDFium version \d+\.\d+\.\d+", app.Require("AboutPdfium").Name);
        app.Press(VirtualKeyShort.ESCAPE);
    }

    [UiFact]
    public void PV14_PresentationFromAFullScreenReadingGoesBackToFullScreenAndATabSwitchEndsIt()
    {
        var a = TestPdfs.Numbered(_work, "tab-a.pdf", 3);
        var b = TestPdfs.Numbered(_work, "tab-b.pdf", 4);
        using var app = MikePDFApp.Launch(a);
        app.WaitForDocument(3);
        app.FocusPages();
        app.Press(VirtualKeyShort.F11);
        app.WaitFor("full screen", () => !app.Exists("OpenButton"));
        app.PressWithControl(VirtualKeyShort.KEY_L);
        WaitForSlide(app, 1, 3);
        app.Press(VirtualKeyShort.F11); // F11 leaves the presentation, not the full screen it came from
        app.WaitFor("back to the full-screen reading", () => !app.Exists("PresentationSurface") && app.Exists("PageSurface"));
        Assert.False(app.Exists("OpenButton"));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("windowed", () => app.Exists("OpenButton"));

        // A talk belongs to its document: another tab ends it.
        MikePDFApp.OpenInRunningApp(b);
        app.WaitFor("two tabs", () => app.TabItems().Count == 2);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1);
        app.WaitForDocument(3);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_L);
        WaitForSlide(app, 1, 3);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_2);
        app.WaitFor("tab b shown and the presentation over", () => !app.Exists("PresentationSurface") && app.Exists("OpenButton"));
        app.WaitForDocument(4);
    }
}
