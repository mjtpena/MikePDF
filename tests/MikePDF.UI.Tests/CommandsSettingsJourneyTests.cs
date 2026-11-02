using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using Microsoft.Win32;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// UI journeys for the command registry, the palette, remappable shortcuts, Settings and policy (docs/features/command-palette-shortcuts.md and
/// settings-policies.md). Each starts its own MikePDF. Policy is written under HKCU only (the app reads HKCU after HKLM and takes a user-level value if it is stricter),
/// at a key these tests create and delete, and never under HKLM.
/// </summary>
public sealed class CommandsSettingsJourneyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public CommandsSettingsJourneyTests()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings(); // each journey starts from the defaults, whatever an earlier one left
    }

    public void Dispose()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings();
        Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", null);
        PolicyKey.Delete();
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The decoy engine's executable can stay locked for a moment after its process ends.
        }
    }

    private MikePDFApp OpenTwelve(out string pdf)
    {
        pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        var app = MikePDFApp.Launch(pdf);
        app.WaitForDocument(12);
        app.FocusPages();
        return app;
    }

    // ---- the dispatcher with real focus -----------------------------------------------------------------------------------------------------------

    [UiFact]
    public void CMD05_AReaderShortcutDoesNotRunWhileATextBoxHasFocusAndCtrlCCopiesTheBoxText()
    {
        using var app = OpenTwelve(out _);
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);
        app.PressWithControl(VirtualKeyShort.KEY_F);
        app.WaitFor("the find box focused", () => app.HasFocus("FindBox"));
        app.Type("abc");
        app.WaitFor("the text typed", () => app.Require("FindBox").Patterns.Value.Pattern.Value.Value == "abc");

        MikePDFApp.ClearClipboard();
        app.PressWithControl(VirtualKeyShort.KEY_A);
        app.PressWithControl(VirtualKeyShort.KEY_C);
        app.WaitFor("the box's text on the clipboard, not the page's", () => MikePDFApp.ClipboardText() == "abc");

        // Zoom is a reader shortcut: typing in a box keeps its keys.
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_PLUS);
        System.Threading.Thread.Sleep(600);
        Assert.Equal(100, app.ZoomPercent);

        // A global shortcut still works from the box: Ctrl+D opens the document's properties.
        app.PressWithControl(VirtualKeyShort.KEY_D);
        app.WaitFor("the properties dialog from the find box", () => app.Exists("PropertiesDialog"));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the dialog closed", () => !app.Exists("PropertiesDialog"));
    }

    // ---- the palette ---------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void CMD12_ThePaletteOpensWithFocusInItsBoxRunsACommandAndGivesFocusBack()
    {
        using var app = OpenTwelve(out _);
        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);

        var palette = app.Palette;
        palette.Open();
        Assert.Equal("Type a command", palette.BoxName);
        Assert.False(app.HasFocus("PageSurface"));
        palette.Type("zoom in");
        app.WaitFor("Zoom in first", () => palette.Selected?.StartsWith("Zoom in,", StringComparison.Ordinal) == true);
        app.Capture("palette-zoom-in");
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("the palette closed", () => !palette.IsOpen);
        app.WaitFor("zoomed in", () => app.ZoomPercent > 100);
        app.WaitFor("focus back on the pages", () => app.HasFocus("PageSurface"));

        // Esc closes and gives focus back; the shortcut again closes too.
        palette.Open();
        palette.Close();
        app.WaitFor("focus back on the pages after Esc", () => app.HasFocus("PageSurface"));
        palette.Open();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_P);
        app.WaitFor("closed by the shortcut", () => !palette.IsOpen);
        app.WaitFor("focus back again", () => app.HasFocus("PageSurface"));

        // The first Esc clears a query, the second closes.
        palette.Open();
        palette.Type("fit");
        app.WaitFor("a query", () => palette.Query == "fit");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the query cleared", () => palette.Query.Length == 0);
        Assert.True(palette.IsOpen);
        palette.Close();
    }

    [UiFact]
    public void CMD13_CMD14_ADisabledCommandSaysWhyAndEnterOnItDoesNothing_AndEachMoveIsOneNotificationWithTheRowText()
    {
        using var app = MikePDFApp.Launch();
        var palette = app.Palette;
        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);

        palette.Open();
        palette.Type("zoom in");
        app.WaitFor("the unavailable row", () => palette.Selected?.StartsWith("Zoom in,", StringComparison.Ordinal) == true);
        var row = palette.Selected!;
        Assert.EndsWith(", unavailable, Open a document first", row, StringComparison.Ordinal);
        Assert.Matches(@"^Zoom in, .+, View, unavailable", row);

        lock (heard)
        {
            heard.Clear();
        }

        app.Press(VirtualKeyShort.ENTER);
        System.Threading.Thread.Sleep(700);
        Assert.True(palette.IsOpen); // Enter on an unavailable command does nothing
        lock (heard)
        {
            Assert.Single(heard, h => h.Activity == "PaletteUnavailable"); // and says why, once
            Assert.Contains("Open a document first", heard.Single(h => h.Activity == "PaletteUnavailable").Text, StringComparison.Ordinal);
        }

        app.Press(VirtualKeyShort.ENTER);
        System.Threading.Thread.Sleep(400);
        lock (heard)
        {
            Assert.Single(heard, h => h.Activity == "PaletteUnavailable"); // not again for the same row
        }

        // Moving the selection: exactly one notification each, and its text is the row's.
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Press(VirtualKeyShort.BACK);
        app.Type("zoom");
        app.WaitFor("results for zoom", () => palette.Rows().Count >= 2);
        System.Threading.Thread.Sleep(600);
        lock (heard)
        {
            heard.Clear();
        }

        var before = palette.Selected;
        app.Press(VirtualKeyShort.DOWN);
        app.WaitFor("the selection moved", () => palette.Selected != before);
        System.Threading.Thread.Sleep(600);
        string spoken;
        lock (heard)
        {
            var moves = heard.Where(h => h.Activity == "PaletteSelection").ToList();
            Assert.Single(moves);
            spoken = moves[0].Text;
        }

        Assert.Equal(palette.Selected, spoken);
        Assert.Matches(@"^.+, .+, View$|^.+, no shortcut, \w+", spoken.Replace(", unavailable, Open a document first", string.Empty));

        // The count comes after a pause in typing, in a live region.
        app.WaitFor("the count", () => Regex.IsMatch(palette.Status, @"^\d+ results?$"));
        palette.Type("qqqqzzzz");
        app.WaitFor("no matches said", () => palette.Status == "No matching commands");
    }

    [UiFact]
    public void CMD25_AKeyboardOnlyJourneyUsesThePaletteToGoToAPageAndToZoomAndOpensSettings()
    {
        using var app = OpenTwelve(out _);
        var palette = app.Palette;

        palette.Open();
        palette.Type("5");
        app.WaitFor("Go to page 5 first", () => palette.Selected?.StartsWith("Go to page 5,", StringComparison.Ordinal) == true);
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("page 5", () => app.CurrentPage == 5);

        app.PressWithControl(VirtualKeyShort.KEY_0);
        app.WaitFor("100%", () => app.ZoomPercent == 100);
        palette.Run("zoom in", "Zoom in,");
        app.WaitFor("zoomed", () => app.ZoomPercent > 100);
        palette.Run("fit width", "Fit width,");
        app.WaitFor("fit width", () => app.ZoomPercent != 100);

        palette.Run("settings", "Settings,");
        app.WaitFor("the settings page", () => app.Settings.IsOpen);
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("settings closed with Esc", () => !app.Settings.IsOpen);
        app.WaitFor("focus on the pages", () => app.HasFocus("PageSurface"));
    }

    // ---- accelerator text and remapping --------------------------------------------------------------------------------------------------------

    [UiFact]
    public void CMD15_EveryButtonShowsItsShortcutAsAcceleratorTextAndHelpTextWhenItCannotRun()
    {
        using var app = MikePDFApp.Launch();
        app.ResizeTo(1600, 960); // wide enough that no button is in the overflow menu
        Assert.Equal("Ctrl+O", app.AcceleratorKey("OpenButton"));
        Assert.Equal("Ctrl+F", app.AcceleratorKey("FindButton"));
        Assert.Equal("F4", app.AcceleratorKey("SidebarButton"));
        Assert.Equal("Alt+Left", app.AcceleratorKey("BackButton"));
        Assert.Equal("Ctrl+2", app.AcceleratorKey("FitWidthButton"));
        Assert.Equal("Ctrl+1", app.AcceleratorKey("FitPageButton"));
        Assert.Equal("Ctrl+Plus, Ctrl+Add", app.AcceleratorKey("ZoomInButton"));
        Assert.Equal("F11", app.AcceleratorKey("FullScreenButton"));
        Assert.Equal("Ctrl+D", app.AcceleratorKey("PropertiesButton"));
        Assert.False(app.Require("FitWidthButton").IsEnabled);
        Assert.Equal("Open a document first", app.HelpText("FitWidthButton"));
        Assert.True(app.Require("OpenButton").IsEnabled);
        Assert.Equal(string.Empty, app.HelpText("OpenButton"));
    }

    [UiFact]
    public void CMD16_RemappingAShortcutByTypingItMovesTheShortcutAndSurvivesARestart()
    {
        var pdf = TestPdfs.Numbered(_work, "twelve.pdf", 12);
        try
        {
            using (var app = MikePDFApp.Launch(pdf))
            {
                app.WaitForDocument(12);
                var settings = app.Settings;
                settings.Open();
                settings.GoTo("shortcuts");
                settings.Filter("Fit width");
                Assert.StartsWith("Fit width, Ctrl+2", settings.ShortcutRow("view.fitWidth"), StringComparison.Ordinal);
                settings.Change("view.fitWidth");
                Assert.NotNull(app.Window.FindFirstDescendant(cf => cf.ByName("Change shortcut for Fit width")));
                settings.TypeChord("ctrl+alt+k");
                app.WaitFor("the chord shown", () => settings.DialogLive == "Shortcut: Ctrl+Alt+K");
                settings.Press("Assign");
                app.WaitFor("the new shortcut in the row", () => settings.ShortcutRow("view.fitWidth").StartsWith("Fit width, Ctrl+Alt+K", StringComparison.Ordinal));
                Assert.True(settings.HasReset("view.fitWidth"));
                settings.Close();
                app.WaitFor("focus on the pages", () => app.HasFocus("PageSurface"));

                Assert.Equal("Ctrl+Alt+K", app.AcceleratorKey("FitWidthButton"));
                app.PressWithControl(VirtualKeyShort.KEY_0);
                app.WaitFor("100%", () => app.ZoomPercent == 100);
                app.PressWithControl(VirtualKeyShort.KEY_2); // the old shortcut
                System.Threading.Thread.Sleep(800);
                Assert.Equal(100, app.ZoomPercent);
                app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_K); // the new one
                app.WaitFor("fit width by the new shortcut", () => app.ZoomPercent != 100);
                app.CloseGracefully();
            }

            using (var again = MikePDFApp.Launch(pdf))
            {
                again.WaitForDocument(12);
                Assert.Equal("Ctrl+Alt+K", again.AcceleratorKey("FitWidthButton")); // restart keeps it
                again.FocusPages();
                again.PressWithControl(VirtualKeyShort.KEY_0);
                again.WaitFor("100%", () => again.ZoomPercent == 100);
                again.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_K);
                again.WaitFor("fit width after the restart", () => again.ZoomPercent != 100);
                ResetShortcut(again, "view.fitWidth", "Fit width");
                Assert.Equal("Ctrl+2", again.AcceleratorKey("FitWidthButton"));
            }
        }
        finally
        {
            MikePDFApp.KillAll();
        }
    }

    [UiFact]
    public void CMD16_RemappingByPressingTheKeysShowsThemLiveAndTheDialogNamesAReservedOne()
    {
        using var app = OpenTwelve(out _);
        var settings = app.Settings;
        settings.Open();
        settings.GoTo("shortcuts");
        settings.Filter("Rotate view right");
        settings.Change("view.rotateRight");

        settings.CaptureChord(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_J);
        app.WaitFor("the pressed keys shown live", () => settings.DialogLive == "Shortcut: Ctrl+Alt+J");
        Assert.Equal(string.Empty, settings.DialogProblem);

        settings.CaptureChord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_0);
        System.Threading.Thread.Sleep(600);
        // Windows takes Ctrl+Shift+0 before the window sees it: nothing arrives, and the previous chord stands. Typing it is the way to see the explanation.
        settings.TypeChord("Ctrl+Shift+0");
        app.WaitFor("the reserved chord explained", () => settings.DialogProblem.Contains("is reserved by Windows or by MikePDF", StringComparison.Ordinal));
        Assert.False(settings.DialogButton("Assign").IsEnabled);

        settings.TypeChord("Alt+F4");
        app.WaitFor("Alt+F4 explained", () => settings.DialogProblem.StartsWith("Alt+F4 is reserved", StringComparison.Ordinal));
        settings.TypeChord("Ctrl+Frobnicate");
        app.WaitFor("a bad chord explained", () => settings.DialogProblem.StartsWith("That isn't a shortcut", StringComparison.Ordinal));
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the dialog cancelled", () => !app.Exists("ShortcutDialog"));
        Assert.StartsWith("Rotate view right, Ctrl+Shift+", settings.ShortcutRow("view.rotateRight"), StringComparison.Ordinal); // unchanged
    }

    [UiFact]
    public void CMD17_ABindingThatClashesOffersReassignAndLeavesExactlyOneOwner()
    {
        using var app = OpenTwelve(out _);
        var settings = app.Settings;
        try
        {
            settings.Open();
            settings.GoTo("shortcuts");
            settings.Filter("Fit page");
            settings.Change("view.fitPage");
            settings.TypeChord("Ctrl+F"); // Find in document's
            app.WaitFor("the clash named", () => settings.DialogProblem.StartsWith("Ctrl+F is used by Find in document.", StringComparison.Ordinal));
            var reassign = settings.DialogButton("Reassign");
            Assert.True(reassign.IsEnabled);
            settings.Press("Reassign");
            app.WaitFor("fit page now Ctrl+F", () => settings.ShortcutRow("view.fitPage").StartsWith("Fit page, Ctrl+F", StringComparison.Ordinal));
            settings.Filter("Find in document");
            Assert.StartsWith("Find in document, None", settings.ShortcutRow("find.open"), StringComparison.Ordinal);
            settings.Close();

            // Exactly one owner: Ctrl+F now fits the page and no longer opens find.
            app.PressWithControl(VirtualKeyShort.KEY_0);
            app.WaitFor("100%", () => app.ZoomPercent == 100);
            app.PressWithControl(VirtualKeyShort.KEY_F);
            app.WaitFor("fit page by Ctrl+F", () => app.ZoomPercent != 100);
            Assert.False(app.Exists("FindBox"));

            // Unbound, a command can still be run from the palette.
            app.Palette.Run("find in document", "Find in document,");
            app.WaitFor("the find bar from the palette", () => app.Exists("FindBox"));
        }
        finally
        {
            ResetAllShortcuts(app);
        }
    }

    private static void ResetShortcut(MikePDFApp app, string commandId, string filter)
    {
        var settings = app.Settings;
        settings.Open();
        settings.GoTo("shortcuts");
        settings.Filter(filter);
        settings.Reset(commandId);
        app.WaitFor("reset", () => !settings.HasReset(commandId));
        settings.Close();
    }

    private static void ResetAllShortcuts(MikePDFApp app)
    {
        try
        {
            if (!app.Settings.IsOpen)
            {
                app.Settings.Open();
            }

            app.Settings.GoTo("shortcuts");
            var button = app.Find("ShortcutsResetAll");
            if (button is { IsEnabled: true })
            {
                button.Patterns.Invoke.Pattern.Invoke();
                app.WaitFor("the confirmation", () => app.Exists("ConfirmDialog"));
                app.Press(VirtualKeyShort.RETURN);
                app.WaitFor("confirmed", () => !app.Exists("ConfirmDialog"));
            }
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            // Best effort: the package's data is removed when the run unregisters it.
        }
    }

    // ---- settings and policy ------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void SET26_ChangingTheThemeAppliesAtOnceAnnouncesItAndSurvivesARestart()
    {
        try
        {
            using (var app = MikePDFApp.Launch())
            {
                var settings = app.Settings;
                settings.Open();
                settings.GoTo("general");
                Assert.True(settings.HasSetting("general.theme"));
                Assert.Equal("Theme", settings.Setting("general.theme").Name);
                settings.Choose("general.theme", "Dark");
                app.WaitFor("the announcement", () => settings.Announcement == "Theme changed to Dark");
                System.Threading.Thread.Sleep(500);
                var dark = BackgroundBrightness(app);
                Assert.True(dark < 100, "the settings page is dark: " + dark);
                app.Capture("settings-dark");
                app.CloseGracefully();
            }

            using (var again = MikePDFApp.Launch())
            {
                again.Settings.Open();
                again.Settings.GoTo("general");
                Assert.Equal("Dark", again.Settings.Setting("general.theme").AsComboBox().SelectedItem!.Name);
                again.Settings.Choose("general.theme", "Light");
                again.WaitFor("light", () => BackgroundBrightness(again) > 200);
                again.Settings.Choose("general.theme", "Use the Windows setting");
                again.WaitFor("the announcement of the system theme", () => again.Settings.Announcement.StartsWith("Theme changed to", StringComparison.Ordinal));
            }
        }
        finally
        {
            MikePDFApp.KillAll();
        }
    }

    private static double BackgroundBrightness(MikePDFApp app)
    {
        using var capture = FlaUI.Core.Capturing.Capture.Element(app.Window);
        var bitmap = capture.Bitmap;
        var c = bitmap.GetPixel(bitmap.Width - 40, bitmap.Height - 40);
        return (c.R + c.G + c.B) / 3.0;
    }

    [UiFact]
    public void SET12_ALockedSettingIsDisabledSaysWhyStaysReachableByKeyboardAndUpdatesInPlace()
    {
        PolicyKey.RequireFree();
        using var app = MikePDFApp.Launch();
        var settings = app.Settings;
        settings.Open();
        settings.GoTo("privacy");
        var control = settings.Setting("links.behaviour");
        Assert.True(control.IsEnabled);
        Assert.False(app.Exists("SettingLocked_links.behaviour"));

        // The organisation turns external links off; within five seconds the page shows it, without a restart.
        PolicyKey.Set("ExternalLinks", 0);
        app.WaitFor("the control locked", () => !settings.Setting("links.behaviour").IsEnabled, TimeSpan.FromSeconds(8));
        control = settings.Setting("links.behaviour");
        Assert.Equal("Managed by your organisation", app.HelpText("Setting_links.behaviour"));
        var note = app.Require("SettingLocked_links.behaviour");
        Assert.True(note.Properties.IsKeyboardFocusable.Value); // the reason is reachable by keyboard
        Assert.Equal("Links that lead outside the document: Block them. Locked. Managed by your organisation.", note.Name);
        Assert.Equal("Block them", control.AsComboBox().SelectedItem!.Name);
        note.Focus();
        Assert.True(note.Properties.HasKeyboardFocus.Value);
        app.Capture("settings-locked");

        // Importing and exporting turned off: the buttons are gone and the page says so; the palette says why.
        PolicyKey.Set("SettingsImportExportEnabled", 0);
        app.WaitFor("export hidden", () => !app.Exists("SettingsExport"), TimeSpan.FromSeconds(8));
        Assert.True(app.Exists("SettingsClearHistory"));
        settings.Close();
        var palette = app.Palette;
        palette.Open();
        palette.Type("export settings");
        app.WaitFor("export row unavailable", () => palette.Selected?.Contains("unavailable, Turned off by your organisation", StringComparison.Ordinal) == true);
        palette.Close();

        // Back to the person's own choice when the policy goes.
        PolicyKey.Delete();
        settings.Open();
        settings.GoTo("privacy");
        app.WaitFor("unlocked again", () => settings.Setting("links.behaviour").IsEnabled && !app.Exists("SettingLocked_links.behaviour"), TimeSpan.FromSeconds(8));
        app.WaitFor("export back", () => app.Exists("SettingsExport"), TimeSpan.FromSeconds(8));
    }

    [UiFact]
    public void SET11_ScenarioNineAboutShowsTheThreePoliciesAndTheirEffects()
    {
        PolicyKey.RequireFree();
        PolicyKey.Set("JavaScriptEnabled", 0);
        PolicyKey.Set("AiMode", 1);
        PolicyKey.Set("TelemetryEnabled", 0);
        PolicyKey.Set("PrintingEnabled", 9); // an invalid value is listed as ignored
        using var app = MikePDFApp.Launch();
        app.Palette.Run("about", "About MikePDF,");
        app.WaitFor("the dialog", () => app.Exists("AboutDialog"));
        Assert.Equal("Policies in effect: 3", app.Require("AboutPoliciesCount").Name);
        Assert.Equal("JavaScript in documents: set to 0. JavaScript: off (Registry policy, user)", app.Require("Policy_JavaScriptEnabled").Name);
        Assert.Equal("AI features: set to 1. AI: local only. Reserved for a future version (Registry policy, user)", app.Require("Policy_AiMode").Name);
        Assert.Equal("Telemetry: set to 0. Telemetry: off (this build has none) (Registry policy, user)", app.Require("Policy_TelemetryEnabled").Name);
        Assert.Equal("Printing: set to 9. Ignored: not a valid value (Registry policy, user)", app.Require("Policy_PrintingEnabled").Name);
        Assert.Equal("Enterprise policies", app.Require("PolicyList").Name);
        app.Capture("about-policies");
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the dialog closed", () => !app.Exists("AboutDialog"));

        // The same report is in Settings, Enterprise policies.
        app.Settings.Open();
        app.Settings.GoTo("policies");
        Assert.True(app.Exists("Policy_JavaScriptEnabled"));
        Assert.True(app.Exists("PoliciesCopy"));
    }

    [UiFact]
    public void AboutWithNoPolicyHasNoManagedSection()
    {
        PolicyKey.RequireFree();
        using var app = MikePDFApp.Launch();
        app.Palette.Run("about", "About MikePDF,");
        app.WaitFor("the dialog", () => app.Exists("AboutDialog"));
        Assert.StartsWith("Version ", app.Require("AboutVersion").Name, StringComparison.Ordinal);
        Assert.False(app.Exists("AboutPoliciesCount"));
        Assert.False(app.Exists("PolicyList"));
    }

    /// <summary>
    /// The policy key these tests use: a throwaway key under HKCU\Software\MikePDF.Tests, named to the app by MIKEPDF_TEST_POLICY_KEY (the app reads it as more user-level
    /// policy, which can only make things stricter). Created by a test and deleted by it. Windows does not let a standard user write HKCU\Software\Policies, and these tests
    /// never write HKLM.
    /// </summary>
    private static class PolicyKey
    {
        private static readonly string Path = @"Software\MikePDF.Tests\policy-" + Guid.NewGuid().ToString("N")[..8];

        public static void RequireFree() => Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", Path); // before the app starts: it inherits this

        public static void Set(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static void Delete()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\MikePDF.Tests");
            if (parent is { SubKeyCount: 0 })
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\MikePDF.Tests", throwOnMissingSubKey: false);
            }
        }
    }
}
