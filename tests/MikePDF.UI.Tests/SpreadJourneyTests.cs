using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// What works on a page works on both pages of a spread, the layout defaults, pseudo-RTL, and the turn time of a presentation (docs/features/page-view-modes.md: PV-09, PV-10, PV-11, PV-17,
/// PV-23, and the defaults of settings-policies.md). Each journey starts its own MikePDF.
/// </summary>
public sealed class SpreadJourneyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-ui-" + Guid.NewGuid().ToString("N")[..8]);

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

    private static MikePDFApp Launch(string pdf, int pages, IReadOnlyDictionary<string, string>? environment = null)
    {
        var app = MikePDFApp.Launch(pdf, environment: environment);
        app.WaitForDocument(pages);
        app.ResizeTo(1600, 960);
        app.FocusPages();
        return app;
    }

    private static Rectangle Box(MikePDFApp app, string phrase)
    {
        app.WaitFor($"'{phrase}' in the text of the pages in view", () => app.AccessibleText.Contains(phrase, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Rectangle? box = null;
        app.WaitFor($"a box for '{phrase}'", () => (box = app.ScreenBoxOf(phrase)) is not null);
        return box!.Value;
    }

    private static Point Centre(AutomationElement element)
    {
        var r = element.BoundingRectangle;
        return new Point(r.Left + (r.Width / 2), r.Top + (r.Height / 2));
    }

    private static void WaitForPages(MikePDFApp app, int first, int last) =>
        app.WaitFor($"pages {first}-{last}", () =>
        {
            var (a, b, _) = app.PagesInView();
            return a == first && b == last;
        });

    private static void WaitForLayout(MikePDFApp app, string layout) =>
        app.WaitFor("the layout \"" + layout + "\"", () => app.HelpText("ViewButton").Contains(layout, StringComparison.Ordinal));

    // ---- PV-09: a selection across the spread ------------------------------------------------------------------------------------------

    [UiFact]
    public void PV09_ADragFromOnePageOfASpreadToTheOtherSelectsBothInPageOrderLeftToRightAndRightToLeftAndBackwards()
    {
        foreach (var (name, extra) in new[] { ("ltr.pdf", ""), ("rtl.pdf", "/ViewerPreferences << /Direction /R2L >>") })
        {
            var pdf = TestPdfs.WithText(_work, name, ["alpha one", "beta two", "gamma three", "delta four"], catalogExtra: extra);
            using var app = Launch(pdf, 4);
            app.PressWithControl(VirtualKeyShort.KEY_5);
            WaitForPages(app, 1, 2);
            var first = Box(app, "alpha one");
            var second = Box(app, "beta two");
            Assert.Equal(extra.Length > 0, second.Left < first.Left); // page 1 is on the right for a right-to-left binding

            // From the start of page 1's text to the end of page 2's, and then the other way round: the text comes in page order both times.
            foreach (var backwards in new[] { false, true })
            {
                app.ClickAt(new Point(first.Left, first.Top - 200)); // empty page: clears the selection
                var (from, to) = backwards ? (MikePDFApp.RightOf(second), MikePDFApp.LeftOf(first)) : (MikePDFApp.LeftOf(first), MikePDFApp.RightOf(second));
                app.Drag(from, to);
                app.WaitFor("both pages selected", () => app.SelectedText.Contains("alpha", StringComparison.Ordinal) && app.SelectedText.Contains("two", StringComparison.Ordinal));
                var selected = app.SelectedText;
                Assert.True(selected.IndexOf("alpha", StringComparison.Ordinal) < selected.IndexOf("beta", StringComparison.Ordinal), $"page order in [{selected}] ({name}, backwards {backwards})");
            }

            MikePDFApp.ClearClipboard();
            app.PressWithControl(VirtualKeyShort.KEY_C);
            app.WaitFor("the text on the clipboard", () => MikePDFApp.ClipboardText() is { } t && t.Contains("beta", StringComparison.Ordinal));
            var copied = MikePDFApp.ClipboardText()!;
            Assert.True(copied.IndexOf("alpha", StringComparison.Ordinal) < copied.IndexOf("beta", StringComparison.Ordinal));
            Assert.DoesNotContain("gamma", copied, StringComparison.Ordinal);
            app.Capture("selection-across-spread-" + (extra.Length > 0 ? "rtl" : "ltr"));
        }
    }

    // ---- PV-10: highlights and next match across spreads --------------------------------------------------------------------------------

    // The share of a screen box that is tinted like a search highlight (orange or yellow over white), which text and plain paper are not.
    private static double Tinted(MikePDFApp app, Rectangle box)
    {
        var surface = app.Require("PageSurface").BoundingRectangle;
        using var capture = FlaUI.Core.Capturing.Capture.Element(app.Require("PageSurface"));
        var bitmap = capture.Bitmap;
        var tinted = 0;
        var total = 0;
        for (var y = Math.Max(0, box.Top - surface.Top); y < Math.Min(bitmap.Height, box.Bottom - surface.Top); y += 2)
        {
            for (var x = Math.Max(0, box.Left - surface.Left); x < Math.Min(bitmap.Width, box.Right - surface.Left); x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (c.R > 200 && c.B < 190 && c.R - c.B > 40)
                {
                    tinted++;
                }
            }
        }

        return total == 0 ? 0 : (double)tinted / total;
    }

    [UiFact]
    public void PV10_SearchHighlightsBothPagesOfASpreadAndNextMatchCrossesToTheNextSpread()
    {
        var pdf = TestPdfs.WithText(_work, "needles.pdf", ["needle 1", "needle 2", "needle 3", "needle 4", "needle 5", "needle 6"]);
        using var app = Launch(pdf, 6);
        app.PressWithControl(VirtualKeyShort.KEY_5);
        app.PressWithControl(VirtualKeyShort.KEY_7); // one spread at a time
        app.PressWithControl(VirtualKeyShort.KEY_1);
        WaitForLayout(app, "Two pages, one page at a time");
        WaitForPages(app, 1, 2);

        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.Require("FindBox");
        app.Type("needle");
        app.WaitFor("six matches, the first current", () => app.FindStatus.Contains('6', StringComparison.Ordinal));
        Thread.Sleep(500);
        WaitForPages(app, 1, 2);
        var left = Box(app, "needle 1");
        var right = Box(app, "needle 2");
        Assert.True(Tinted(app, left) > 0.1, "the match on the left page is highlighted");
        Assert.True(Tinted(app, right) > 0.1, "and the one on the right page, in the right place");
        app.Capture("find-in-spread");

        app.Press(VirtualKeyShort.ENTER);                    // page 2, the right half of the same spread
        Thread.Sleep(400);
        WaitForPages(app, 1, 2);
        app.Press(VirtualKeyShort.ENTER);                    // page 3: the next spread
        WaitForPages(app, 3, 4);
        Assert.True(Tinted(app, Box(app, "needle 3")) > 0.1);
        Assert.True(Tinted(app, Box(app, "needle 4")) > 0.1);

        app.Chord(VirtualKeyShort.SHIFT, VirtualKeyShort.ENTER); // and back across the boundary
        WaitForPages(app, 1, 2);
    }

    // ---- PV-11: a link on the right-hand page ------------------------------------------------------------------------------------------

    [UiFact]
    public void PV11_ALinkOnTheRightHandPageOfASpreadIsFollowed()
    {
        var pdf = TestPdfs.WithText(_work, "links.pdf", ["one", "go to four", "three", "four"], link: (1, 3));
        using var app = Launch(pdf, 4);
        app.PressWithControl(VirtualKeyShort.KEY_5);
        WaitForPages(app, 1, 2);
        var link = Box(app, "go to four");
        var one = Box(app, "one");
        Assert.True(link.Left > one.Right, "the link is on the right-hand page");
        Thread.Sleep(800); // the links of the pages come
        app.ClickAt(MikePDFApp.CenterOf(link));
        WaitForPages(app, 3, 4);
        Assert.Contains("four", app.AccessibleText, StringComparison.Ordinal);
    }

    // ---- PV-23: pseudo-RTL ------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PV23_APseudoRtlWindowMirrorsTheMenusAndThePresentationBarButNotThePages()
    {
        var pdf = TestPdfs.Numbered(_work, "rtl-ui.pdf", 4);
        using var app = Launch(pdf, 4, new Dictionary<string, string> { ["MIKEPDF_TEST_PSEUDO_RTL"] = "1" });

        // The command bar runs the other way: the first button is at the right.
        Assert.True(app.Require("OpenButton").BoundingRectangle.Left > app.Require("ViewButton").BoundingRectangle.Left, "Open is right of View");
        Assert.True(app.Require("PageIndicator").BoundingRectangle.Left > app.Window.BoundingRectangle.Left + (app.Window.BoundingRectangle.Width / 2), "the page box is on the right");
        app.Capture("pseudo-rtl-command-bar");

        // The View menu opens, with its items and shortcuts as ever.
        app.OpenMenu("ViewButton", "LayoutSingle");
        Assert.Equal("Two pages", app.Require("LayoutTwo").Name);
        Assert.Equal("Ctrl+5", app.Require("LayoutTwo").Properties.AcceleratorKey.ValueOrDefault);
        var item = app.Require("LayoutTwo").BoundingRectangle;
        Assert.InRange(item.Left, app.Window.BoundingRectangle.Left - 2, app.Window.BoundingRectangle.Right + 2);
        app.Capture("pseudo-rtl-view-menu");
        app.Click("LayoutTwo");

        // The pages are the document's, not the window's: a left-to-right document stays left to right.
        WaitForPages(app, 1, 2);
        var one = Box(app, "1");
        var two = Box(app, "2");
        Assert.True(two.Left > one.Right, "page 2 is still right of page 1");

        // The bar of the presentation sits at the other corner.
        app.StartPresentation();
        app.WaitFor("the presentation bar", () => app.Exists("PresentationHint"));
        var bar = app.Require("PresentationHint").BoundingRectangle;
        var surface = Centre(app.Require("PresentationSurface"));
        Assert.True(bar.Left + (bar.Width / 2) < surface.X, "the bar is at the left in a right-to-left window");
        Assert.Equal("Exit presentation", app.Require("PresentationExit").Name);
        app.Capture("pseudo-rtl-presentation");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("out", () => app.Exists("OpenButton"));
    }

    // ---- the defaults of settings ---------------------------------------------------------------------------------------------------------

    [UiFact]
    public void TheDefaultLayoutOfSettingsIsUsedForADocumentWithNoRecordAndNotForOneThatHasOne()
    {
        AppData.ResetSettings();
        var seen = TestPdfs.Numbered(_work, "seen.pdf", 4);
        var fresh = TestPdfs.Numbered(_work, "fresh.pdf", 4);
        using (var app = Launch(seen, 4))
        {
            app.Settings.Open();
            app.Settings.GoTo("reading");
            Assert.Equal("Pages shown for a new document", app.Settings.Setting("view.defaultArrangement").Name);
            app.Settings.Choose("view.defaultArrangement", "Two pages with cover");
            app.Settings.Choose("view.defaultFlow", "One page at a time");
            app.Settings.Close();
            app.CloseGracefully();
        }

        using (var app = Launch(fresh, 4))
        {
            WaitForLayout(app, "Two pages with cover, one page at a time");
            app.CloseGracefully();
        }

        using (var app = Launch(seen, 4))
        {
            WaitForLayout(app, "Single page, continuous"); // its own record wins
        }
    }

    // ---- PV-17: the turn of a presentation, with and without the next pages drawn ahead ------------------------------------------------

    private static (double FirstFrame, double Sharp)[] TurnTimes(string pdf, int pages, bool prefetch, int turns)
    {
        var dump = "turns-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        var environment = new Dictionary<string, string> { ["MIKEPDF_TEST_PRESENT_DUMP"] = dump };
        if (!prefetch)
        {
            environment["MIKEPDF_TEST_NO_PREFETCH"] = "1";
        }

        using var app = MikePDFApp.Launch(pdf, environment: environment);
        app.WaitForDocument(pages);
        app.FocusPages();
        var path = Path.Combine(AppData.PrivateFolder() ?? throw new InvalidOperationException("no private folder"), dump);
        app.Press(VirtualKeyShort.HOME); // the position of the last run was remembered: start at the first page
        app.WaitFor("the first page", () => app.CurrentPage == 1);
        app.StartPresentation();
        app.WaitFor("the presentation", () => app.Exists("PresentationSurface"));
        Thread.Sleep(2500); // the first page sharp, the pages beside it drawn (or not)
        var lines = new List<string>();
        for (var i = 1; i <= turns; i++)
        {
            app.Press(i < pages ? VirtualKeyShort.RIGHT : VirtualKeyShort.LEFT); // forward through the deck, then back through it
            app.WaitFor($"turn {i} measured", () =>
            {
                try
                {
                    lines = File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.StartsWith("turn ", StringComparison.Ordinal))] : [];
                }
                catch (IOException)
                {
                    return false;
                }

                return lines.Count >= i;
            }, TimeSpan.FromSeconds(20));
            Thread.Sleep(1500); // a presenter talks between slides: time for the pages ahead
        }

        app.Press(VirtualKeyShort.ESCAPE);
        File.Delete(path);
        return [.. lines.Select(l => l.Split(' ')).Select(p => (double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture)))];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
    }

    [UiFact]
    public void PV17_APageTurnInAPresentationIsSharpSoonerWithThePagesBesideItDrawnAhead()
    {
        var article = ArticlePdf.Create(_work, "deck.pdf", out var pdf);
        var pages = article.PageCount;
        var turns = 2 * (article.PageCount - 1);
        var without = TurnTimes(pdf, pages, prefetch: false, turns);
        var with = TurnTimes(pdf, pages, prefetch: true, turns);

        string Line(string label, (double FirstFrame, double Sharp)[] t) =>
            $"{label}: sharp median {Median(t.Select(x => x.Sharp)):F1} ms, max {t.Max(x => x.Sharp):F1} ms; first frame median {Median(t.Select(x => x.FirstFrame)):F1} ms; sharp turns {string.Join(' ', t.Select(x => x.Sharp.ToString("F0", CultureInfo.InvariantCulture)))}";
        var report = new[]
        {
            $"DEV machine, not reference hardware. Screen {GetSystemMetrics(0)} x {GetSystemMetrics(1)}; {turns} turns (forward through, then back) of a {pages}-page article with 1.5 s between turns; the key press to the frame handed to the presenter with every tile sharp.",
            Line("without drawing ahead", without),
            Line("with drawing ahead   ", with),
        };
        var directory = Environment.GetEnvironmentVariable("MIKEPDF_SHOTS_DIR") ?? Path.GetTempPath();
        File.WriteAllLines(Path.Combine(directory, "turn-times.txt"), report);

        Assert.Equal(turns, with.Length);
        Assert.Equal(turns, without.Length);
        Assert.True(Median(with.Select(x => x.Sharp)) <= Median(without.Select(x => x.Sharp)) + 25, string.Join(Environment.NewLine, report)); // never worse (25 ms of noise allowed)
    }
}
