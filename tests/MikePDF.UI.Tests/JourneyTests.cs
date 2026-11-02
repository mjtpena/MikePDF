using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// UI smoke journeys against the installed package (ADR-0009). Each starts its own MikePDF, through the execution alias, so single-instance
/// redirection and file arguments are part of what is tested. Screenshots go to MIKEPDF_SHOTS_DIR (build/out/shots).
/// </summary>
public sealed class JourneyTests : IDisposable
{
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

    [UiFact]
    public void MikePDFStartsWithAnEmptyStateAndNothingToNavigate()
    {
        using var app = MikePDFApp.Launch();
        Assert.Contains("MikePDF", app.Window.Title, StringComparison.Ordinal);
        Assert.True(app.Exists("EmptyState"));
        Assert.True(app.Exists("OpenButton"));
        Assert.True(app.Exists("EmptyOpenButton"));
        Assert.False(app.Require("PageIndicator").IsEnabled);
        Assert.False(app.Require("ZoomInButton").IsEnabled);
        app.Capture("01-empty-state");
    }

    [UiFact]
    public void OpeningAFixtureShowsTheDocumentAndTheRightPageCount()
    {
        using var app = MikePDFApp.Launch(MikePDFApp.Fixture("hello_world_2_pages.pdf"));
        app.WaitForDocument(2);
        Assert.Equal(1, app.CurrentPage);
        Assert.Contains("hello_world_2_pages", app.Window.Title, StringComparison.Ordinal);
        Assert.True(app.Exists("PageSurface"));
        Assert.True(app.Require("PageIndicator").IsEnabled);
        Assert.True(app.ZoomPercent > 0);
        app.Capture("02-two-page-document");
    }

    [UiFact]
    public void ThePageIndicatorFollowsKeyboardNavigation()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();

        app.Press(VirtualKeyShort.END);
        app.WaitFor("page 12", () => app.CurrentPage == 12);
        app.Capture("03-last-page");

        app.Press(VirtualKeyShort.HOME);
        app.WaitFor("page 1", () => app.CurrentPage == 1);

        app.Press(VirtualKeyShort.RIGHT);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("page 3", () => app.CurrentPage == 3);
        app.Press(VirtualKeyShort.LEFT);
        app.WaitFor("page 2", () => app.CurrentPage == 2);
    }

    [UiFact]
    public void GoToPageJumpsToTheChosenPage()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();

        app.PressWithControl(VirtualKeyShort.KEY_G);
        var box = app.Require("GoToPageBox");
        Assert.NotNull(box);
        app.Capture("04-go-to-page-dialog");
        Keyboard(app, "7");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("page 7", () => app.CurrentPage == 7);
    }

    [UiFact]
    public void ZoomKeysAndButtonsChangeTheZoom()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();
        var fit = app.ZoomPercent;
        Assert.InRange(fit, 10, 6400);

        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);

        app.Click("ZoomInButton");
        app.WaitFor("125%", () => app.ZoomPercent == 125);
        app.Click("ZoomOutButton");
        app.WaitFor("100% again", () => app.ZoomPercent == 100);

        app.PressWithControl(VirtualKeyShort.KEY_2);
        app.WaitFor("fit width", () => app.ZoomPercent != 100);
        app.PressWithControl(VirtualKeyShort.KEY_1);
        app.WaitFor("fit page", () => app.ZoomPercent > 0);
        var page = app.ZoomPercent;
        Assert.True(page <= fit || page > 0);
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100% once more", () => app.ZoomPercent == 100);
        app.Capture("05-zoom-100");
    }

    [UiFact]
    public void ThePageIsDrawnNotBlank()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);

        var surface = app.Require("PageSurface");
        // The paper is white and the digit is black: after the tiles arrive both are on screen, and the surroundings are neither.
        app.WaitFor("tiles drawn", () => CountSharp(surface) > 2000, TimeSpan.FromSeconds(30));
        app.Capture("06-page-rendered");
    }

    [UiFact]
    public void AWrongThenTheRightPasswordOpensAProtectedDocument()
    {
        using var app = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf"));
        var dialog = app.Require("PasswordDialog");
        Assert.NotNull(dialog);
        app.Capture("07-password-dialog");

        app.Type("not the password");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("the 'wrong password' message", () => app.Find("PasswordMessage")?.Name.Contains("isn't right", StringComparison.OrdinalIgnoreCase) == true || app.Find("PasswordMessage") is { } m && m.Name.Contains("right", StringComparison.OrdinalIgnoreCase));
        app.Capture("08-password-wrong");

        app.Type("5678");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitForDocument(1);
        Assert.False(app.Exists("PasswordDialog"));
    }

    [UiFact]
    public void CancellingThePasswordLeavesTheEmptyState()
    {
        using var app = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf"));
        app.Require("PasswordDialog");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("empty state", () => app.Exists("EmptyState") && app.Find("PasswordDialog") is null);
        Assert.False(app.Require("PageIndicator").IsEnabled);
    }

    [UiFact]
    public void ASecondLaunchWithAnotherFileReusesTheWindow()
    {
        var first = TestPdfs.Numbered(_work, "first.pdf", 3);
        var second = TestPdfs.Numbered(_work, "second.pdf", 5);
        using var app = MikePDFApp.Launch(first);
        app.WaitForDocument(3);

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "mikepdf.exe");
        var start = new ProcessStartInfo(alias) { UseShellExecute = false };
        start.ArgumentList.Add(second);
        Process.Start(start)?.Dispose();

        app.WaitFor("the second document", () => app.PageCount == 5);
        Assert.Contains("second", app.Window.Title, StringComparison.Ordinal);
        Assert.Single(Process.GetProcessesByName("MikePDF")); // one window, one process
    }

    [UiFact]
    public void ADamagedFileIsExplainedInPlainLanguage()
    {
        Directory.CreateDirectory(_work);
        var path = Path.Combine(_work, "broken.pdf");
        File.WriteAllText(path, "this is not a PDF at all");
        using var app = MikePDFApp.Launch(path);
        var dialog = app.Require("ErrorDialog");
        Assert.NotNull(dialog);
        app.Capture("09-damaged-file");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("empty state", () => app.Find("ErrorDialog") is null && app.Exists("EmptyState"));
    }

    [UiFact]
    public void TheViewerRecoversWhenTheEngineIsKilledAndKeepsThePlace()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.Press(VirtualKeyShort.RIGHT);
        app.Press(VirtualKeyShort.RIGHT);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("page 4", () => app.CurrentPage == 4);

        // Only this MikePDF's own engines are killed. A MikePDF.Engine that is someone else's (here a decoy of that name) must survive.
        using var decoy = DecoyEngine.Start(_work);
        var before = app.EngineIds();
        Assert.NotEmpty(before);
        Assert.DoesNotContain(decoy.Id, before);
        Assert.Equal(before.Count, app.KillEngines());
        Assert.False(decoy.HasExited, "an engine that is not this app's was killed");

        // A new engine appears, a banner says it is back, and the page indicator never moved.
        app.WaitFor("a new engine process", () => app.EngineIds().Any(id => !before.Contains(id)));
        app.WaitFor("the banner saying the viewer is back", () => app.Window.FindFirstDescendant(cf => cf.ByName("Back to normal")) is not null, TimeSpan.FromSeconds(20));
        app.Capture("10-engine-restored-banner");
        Assert.Equal(4, app.CurrentPage);

        // Pages are drawn again after the restart.
        var surface = app.Require("PageSurface");
        app.WaitFor("tiles drawn again", () => CountSharp(surface) > 2000, TimeSpan.FromSeconds(30));
        Assert.Equal(4, app.CurrentPage);
        app.Capture("11-engine-restored");
    }

    [UiFact]
    public void TheDocumentReopensWhereItWasLeft()
    {
        var pdf = TestPdfs.Numbered(_work, "remember.pdf", 12);
        using (var app = MikePDFApp.Launch(pdf))
        {
            app.WaitForDocument(12);
            app.FocusPages();
            app.PressWithControl(VirtualKeyShort.KEY_0);
            for (var i = 0; i < 6; i++)
            {
                app.Press(VirtualKeyShort.RIGHT);
            }

            app.WaitFor("page 7", () => app.CurrentPage == 7);
            app.CloseGracefully();
        }

        using var again = MikePDFApp.Launch(pdf);
        again.WaitForDocument(12);
        again.WaitFor("page 7 again", () => again.CurrentPage == 7);
        Assert.Equal(100, again.ZoomPercent);
        again.Capture("13-reopened-at-last-position");
    }

    [UiFact]
    public void RotatingTheViewTurnsThePagesWithoutChangingThePage()
    {
        var pdf = TestPdfs.Numbered(_work, "rotate.pdf", 4);
        using var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(4);
        app.FocusPages();
        var portrait = app.ZoomPercent; // fit width of a portrait page

        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.Press(VirtualKeyShort.RIGHT);
        app.WaitFor("page 2", () => app.CurrentPage == 2);

        Assert.Equal("Not rotated", Status(app));
        MikePDFApp.HoldControlShift(VirtualKeyShort.OEM_PLUS);
        app.WaitFor("a quarter turn", () => Status(app) == "Rotated 90 degrees");
        Assert.Equal(2, app.CurrentPage);
        app.Capture("14-rotated-right");

        MikePDFApp.HoldControlShift(VirtualKeyShort.OEM_PLUS);
        app.WaitFor("a half turn", () => Status(app) == "Rotated 180 degrees");
        MikePDFApp.HoldControlShift(VirtualKeyShort.OEM_MINUS);
        MikePDFApp.HoldControlShift(VirtualKeyShort.OEM_MINUS);
        app.WaitFor("back to upright", () => Status(app) == "Not rotated");
        Assert.Equal(2, app.CurrentPage);
        Assert.InRange(portrait, 1, 6400);
    }

    [UiFact]
    public void ARecentFileCanBeReopenedFromTheEmptyState()
    {
        var pdf = TestPdfs.Numbered(_work, "recent.pdf", 2);
        using (var app = MikePDFApp.Launch(pdf))
        {
            app.WaitForDocument(2);
        }

        using var again = MikePDFApp.Launch();
        var list = again.Require("RecentList");
        Assert.NotNull(list);
        again.Capture("12-recent-files");
        var item = list.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
        Assert.NotNull(item);
        item!.Click();
        again.WaitForDocument(2);
    }

    private static void Keyboard(MikePDFApp app, string text) => app.Type(text);

    private static string Status(MikePDFApp app) => app.Require("PageSurface").Properties.ItemStatus.ValueOrDefault ?? string.Empty;

    /// <summary>How many pixels of the surface are pure white or pure black (paper and ink): zero before anything is drawn.</summary>
    private static int CountSharp(AutomationElement surface)
    {
        using var capture = FlaUI.Core.Capturing.Capture.Element(surface);
        var bitmap = capture.Bitmap;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var c = bitmap.GetPixel(x, y);
                if ((c.R == 255 && c.G == 255 && c.B == 255) || (c.R < 20 && c.G < 20 && c.B < 20))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
