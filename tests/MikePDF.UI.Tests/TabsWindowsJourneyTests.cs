using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Microsoft.Win32;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// UI journeys for tabs, windows and session restore (docs/features/tabs-windows-session.md, criteria TAB-xx; docs/features/file-association-jumplist.md for the activation part).
/// They drive the real mouse and keyboard against the installed package. Each starts its own MikePDF; journeys that are not about restoring start with restore set to Never (the harness
/// passes that to the app), so a run that was killed never asks the next one to restore it.
/// </summary>
public sealed class TabsWindowsJourneyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-tabs-" + Guid.NewGuid().ToString("N")[..8]);
    private string? _policyKey;

    public TabsWindowsJourneyTests()
    {
        MikePDFApp.KillAll();
        AppData.DeleteSession();
    }

    public void Dispose()
    {
        MikePDFApp.KillAll();
        AppData.DeleteSession();
        Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", null);
        if (_policyKey is not null)
        {
            Registry.CurrentUser.DeleteSubKeyTree(_policyKey, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\MikePDF.Tests");
            if (parent is { SubKeyCount: 0 })
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\MikePDF.Tests", throwOnMissingSubKey: false);
            }
        }

        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's executable can stay locked for a moment after its process ends.
        }
    }

    private sealed class LiveText(List<(string Text, string Activity)> heard)
    {
        public void Clear()
        {
            lock (heard)
            {
                heard.Clear();
            }
        }

        public int Count(string text)
        {
            lock (heard)
            {
                return heard.Count(h => h.Activity == "TabAnnouncement" && h.Text == text);
            }
        }
    }

    private string Pdf(string name, int pages) => TestPdfs.Numbered(_work, name + ".pdf", pages);

    private static void WaitForTabs(MikePDFApp app, params string[] names) => app.WaitFor("the tabs " + string.Join(", ", names), () => app.TabNames().SequenceEqual(names));

    private static void WaitForTabs(MikePDFApp app, int count) => app.WaitFor(count + " tabs", () => app.TabNames().Count == count);

    private static void WaitForSelected(MikePDFApp app, string name) => app.WaitForEqual("the selected tab", name, app.SelectedTabName);

    private static IReadOnlyList<Window> SecondWindows(MikePDFApp app) => [.. app.AllWindows().Where(w => w.Properties.NativeWindowHandle.ValueOrDefault != app.Window.Properties.NativeWindowHandle.ValueOrDefault)];

    private static System.Drawing.Point Centre(AutomationElement e) => MikePDFApp.CenterOf(e.BoundingRectangle);

    // ---- the strip, TAB-02, TAB-27 ------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void TAB02_TAB27_AFileOpensInATabAndOpeningItAgainOnlyShowsItAndTheStripIsATabOfTabItems()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        using var app = MikePDFApp.Launch(alpha);
        app.WaitForDocument(3);
        WaitForTabs(app, "alpha");
        MikePDFApp.OpenInRunningApp(bravo);
        WaitForTabs(app, "alpha", "bravo");
        app.WaitForDocument(5);
        Assert.Equal("bravo", app.SelectedTabName());
        app.Capture("tabs-two");

        // TAB-27: the strip is a Tab, each item a TabItem with SelectionItem, a name, and the path as help text.
        var strip = app.Require("DocumentTabs");
        Assert.Equal(ControlType.Tab, strip.ControlType);
        Assert.Equal("Open documents", strip.Name);
        var items = app.TabItems();
        Assert.All(items, item =>
        {
            Assert.Equal(ControlType.TabItem, item.ControlType);
            Assert.True(item.Patterns.SelectionItem.IsSupported);
        });
        Assert.Equal([alpha, bravo], items.Select(i => i.HelpText));
        Assert.Equal("Tab_1", items[0].AutomationId);

        // TAB-02: the same file again activates its tab; there is no third tab and no second window.
        var engines = app.EngineIds();
        MikePDFApp.OpenInRunningApp(alpha);
        WaitForSelected(app, "alpha");
        app.WaitForDocument(3);
        Assert.Equal(2, app.TabItems().Count);
        Assert.Single(Process.GetProcessesByName("MikePDF"));
        Assert.Single(app.AllWindows());
        Assert.Equal(engines.Count, app.EngineIds().Count); // no second session, no new engine
    }

    // ---- keyboard only, TAB-26, with TAB-28 and the reopen of TAB-13 -----------------------------------------------------------------------------

    [UiFact]
    public void TAB26_TAB28_ThreeFilesSwitchReorderMoveToANewWindowCloseAndReopenWithNoMouse()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        var charlie = Pdf("charlie", 7);
        using var app = MikePDFApp.Launch(alpha);
        app.WaitForDocument(3);
        MikePDFApp.OpenInRunningApp(bravo);
        WaitForTabs(app, 2);
        MikePDFApp.OpenInRunningApp(charlie);
        WaitForTabs(app, "alpha", "bravo", "charlie");
        app.WaitForDocument(7);
        app.FocusPages();

        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);
        var live = new LiveText(heard);

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        WaitForSelected(app, "alpha"); // wraps round
        app.WaitForDocument(3);
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        WaitForSelected(app, "charlie");
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_2);
        WaitForSelected(app, "bravo");
        app.WaitForDocument(5);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_9);
        WaitForSelected(app, "charlie");

        // Reorder: move charlie left, and a TAB-28 announcement is made once.
        live.Clear();

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.PRIOR);
        WaitForTabs(app, "alpha", "charlie", "bravo");
        app.WaitFor("the move announced", () => live.Count("charlie moved to position 2 of 3") == 1);
        Thread.Sleep(400);
        Assert.Equal(1, live.Count("charlie moved to position 2 of 3")); // exactly once

        // Move bravo to a new window.
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_3);
        WaitForSelected(app, "bravo");
        var enginesBefore = app.EngineIds();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_N);
        app.WaitFor("a second window", () => app.AllWindows().Count == 2);
        WaitForTabs(app, "alpha", "charlie");
        var other = app.ForWindow(SecondWindows(app).Single());
        WaitForTabs(other, "bravo");
        other.WaitForDocument(5);
        var heardInOther = new List<(string Text, string Activity)>();
        using var listeningInOther = other.ListenForNotifications(heardInOther);
        var otherLive = new LiveText(heardInOther);
        Assert.Equal(enginesBefore, app.EngineIds()); // TAB-07: the same engine processes, nothing was opened again
        app.Capture("tabs-moved-window");

        // Close it with Ctrl+W (the window stays, with an empty tab) and bring it back with Ctrl+Shift+T.
        live.Clear();

        other.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        other.WaitFor("an empty tab", () => other.TabNames().SequenceEqual(["New tab"]));
        other.WaitFor("the close announced", () => otherLive.Count("bravo closed. No documents open") == 1);
        other.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_T);
        WaitForTabs(other, "bravo");
        other.WaitForDocument(5);
    }

    // ---- many files, TAB-04 and FA-05 ----------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void FA05_ThreeFilesOpenAsThreeTabsInOrderWithTheFirstShownAndNoMoreThanOneViewerStarted()
    {
        var files = new[] { Pdf("one", 2), Pdf("two", 3), Pdf("three", 4) };
        using var app = MikePDFApp.Launch();
        app.WaitFor("the empty state", () => app.Exists("EmptyState"));
        MikePDFApp.OpenInRunningApp(files);
        WaitForTabs(app, "one", "two", "three");
        WaitForSelected(app, "one");
        app.WaitForDocument(2);
        Assert.Single(app.AllWindows());
        Assert.Equal(2, app.EngineIds().Count); // the document's engine and the shared pool: the other two open when first shown
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_3);
        WaitForSelected(app, "three");
        app.WaitForDocument(4);
    }

    [UiFact]
    public void TAB04_MoreThanTenFilesAskFirstAndNothingOpensUntilTheAnswerIsYes()
    {
        var files = Enumerable.Range(1, 12).Select(i => Pdf("f" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), 2)).ToArray();
        using var app = MikePDFApp.Launch();
        app.WaitFor("the empty state", () => app.Exists("EmptyState"));
        MikePDFApp.OpenInRunningApp(files);
        app.WaitFor("the question", () => app.Exists("OpenManyDialog"));
        Assert.Equal(["New tab"], app.TabNames()); // nothing has opened
        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the question gone", () => !app.Exists("OpenManyDialog"));
        Thread.Sleep(500);
        Assert.Equal(["New tab"], app.TabNames());

        MikePDFApp.OpenInRunningApp(files);
        app.WaitFor("the question again", () => app.Exists("OpenManyDialog"));
        app.Press(VirtualKeyShort.ENTER); // Open is the default answer
        WaitForTabs(app, 12);
        WaitForSelected(app, "f01");
        app.WaitForDocument(2);
    }

    [UiFact]
    public void TAB29_AHostileActivationOpensNothingAndTheAppStaysResponsive()
    {
        var folder = Path.Combine(_work, "a folder");
        Directory.CreateDirectory(folder);
        using var app = MikePDFApp.Launch();
        app.WaitFor("the empty state", () => app.Exists("EmptyState"));
        MikePDFApp.OpenInRunningApp(@"\\.\pipe\mikepdf-never-opened", folder, Path.Combine(_work, "a.pdf") + ":stream", @"C:\Windows\NUL.pdf");
        app.WaitFor("a notice", () => app.Window.FindFirstDescendant(cf => cf.ByName("This location can't be opened.")) is not null);
        Assert.Equal(["New tab"], app.TabNames());
        Assert.Single(Process.GetProcessesByName("MikePDF"));
        app.Press(VirtualKeyShort.ESCAPE);
        app.PressWithControl(VirtualKeyShort.KEY_T); // still answers the keyboard
        WaitForTabs(app, 2);
    }

    // ---- close prompts, TAB-10 and TAB-11 (a fake dirty flag until the save pipeline exists) ----------------------------------------------------------------

    [UiFact]
    public void TAB10_ClosingATabWithUnsavedEditsAsksAndCancelKeepsItAndDontSaveClosesIt()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true);
        app.WaitForDocument(3);
        MikePDFApp.OpenInRunningApp(bravo);
        WaitForTabs(app, 2);
        app.WaitForDocument(5);
        Assert.Contains("unsaved changes", app.TabNames()[1], StringComparison.Ordinal);

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        app.WaitFor("the question", () => app.Exists("CloseDirtyDialog"));
        app.Capture("tabs-close-dirty");
        Assert.NotNull(app.Window.FindFirstDescendant(cf => cf.ByName("Cancel")));
        Assert.NotNull(app.Window.FindFirstDescendant(cf => cf.ByName("Don't save")));
        app.Press(VirtualKeyShort.ESCAPE); // Cancel
        app.WaitFor("the question gone", () => !app.Exists("CloseDirtyDialog"));
        Assert.Equal(2, app.TabItems().Count);

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        app.WaitFor("the question again", () => app.Exists("CloseDirtyDialog"));
        app.Window.FindFirstDescendant(cf => cf.ByName("Don't save"))!.AsButton().Invoke();
        WaitForTabs(app, 1);
        WaitForSelected(app, "alpha, unsaved changes");
    }

    [UiFact]
    public void TAB11_ClosingAWindowWithThreeUnsavedTabsAsksOnceListingThemAll()
    {
        using var app = MikePDFApp.Launch(Pdf("one", 2), fakeDirty: true);
        app.WaitForDocument(2);
        MikePDFApp.OpenInRunningApp(Pdf("two", 3));
        WaitForTabs(app, 2);
        app.WaitForDocument(3);
        MikePDFApp.OpenInRunningApp(Pdf("three", 4));
        WaitForTabs(app, 3);
        app.WaitForDocument(4);

        app.Window.Close();
        app.WaitFor("the question", () => app.Exists("CloseDirtyDialog"));
        Assert.Equal(3, app.Window.FindAllDescendants(cf => cf.ByAutomationId("CloseDirtyItem")).Length);
        app.Capture("tabs-close-window-dirty");
        app.Press(VirtualKeyShort.ESCAPE); // Cancel: the window stays
        app.WaitFor("the question gone", () => !app.Exists("CloseDirtyDialog"));
        Assert.Equal(3, app.TabItems().Count);

        app.Window.Close();
        app.WaitFor("the question again", () => app.Exists("CloseDirtyDialog"));
        app.Window.FindFirstDescendant(cf => cf.ByName("Don't save"))!.AsButton().Invoke();
        app.WaitFor("the app gone", () => Process.GetProcessesByName("MikePDF").Length == 0, TimeSpan.FromSeconds(30));
    }

    // ---- Settings is a tab ------------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void SettingsOpensAsASingletonTabAndEscapeGoesBackToTheDocument()
    {
        using var app = MikePDFApp.Launch(Pdf("alpha", 3));
        app.WaitForDocument(3);
        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);
        WaitForTabs(app, "alpha", "Settings");
        WaitForSelected(app, "Settings");
        Assert.True(app.Exists("SettingsTitle"));
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA); // again: the same tab
        Thread.Sleep(400);
        Assert.Equal(["alpha", "Settings"], app.TabNames());
        app.Press(VirtualKeyShort.ESCAPE);
        WaitForTabs(app, "alpha");
        WaitForSelected(app, "alpha");
        app.WaitFor("the pages back", () => app.PageCount == 3 && !app.Exists("SettingsTitle"));
    }

    // ---- mouse: the + button, selecting, the only tab drags the window (TAB-09), tear out and merge (TAB-07) ----------------------------------------------

    [UiFact]
    public void TheNewTabButtonAddsAnEmptyTabAndAClickSelectsATab()
    {
        using var app = MikePDFApp.Launch(Pdf("alpha", 3));
        app.WaitForDocument(3);
        app.Click("NewTabButton");
        WaitForTabs(app, "alpha", "New tab");
        Assert.True(app.Exists("EmptyState"));
        app.ClickAt(Centre(app.TabItems()[0]));
        WaitForSelected(app, "alpha");
        app.WaitForDocument(3);
        app.ClickAt(Centre(app.TabItems()[1]));
        WaitForSelected(app, "New tab");
        Assert.True(app.Exists("EmptyState"));
    }

    [UiFact]
    public void TAB09_DraggingTheOnlyTabMovesTheWindowAndMakesNoSecondWindow()
    {
        using var app = MikePDFApp.Launch(Pdf("alpha", 3));
        app.WaitForDocument(3);
        var handle = new IntPtr(app.Window.Properties.NativeWindowHandle.Value);
        GetWindowRect(handle, out var before);
        var tab = Centre(app.TabItems()[0]);
        app.Drag(tab, new System.Drawing.Point(tab.X + 220, tab.Y + 140));
        Thread.Sleep(1500);
        GetWindowRect(handle, out var after);
        Assert.True(Math.Abs(after.Left - before.Left) > 100 && Math.Abs(after.Top - before.Top) > 60, $"the window was at {before.Left},{before.Top} and is at {after.Left},{after.Top} after a drag of 220,140");
        Assert.Single(app.AllWindows());
        Assert.Equal(["alpha"], app.TabNames());
    }

    [UiFact]
    public void TAB07_ADraggedOutTabBecomesAWindowKeepingItsEngineAndDraggingItBackMergesIt()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        var charlie = Pdf("charlie", 7);
        using var app = MikePDFApp.Launch(alpha);
        app.WaitForDocument(3);
        MikePDFApp.OpenInRunningApp(bravo);
        WaitForTabs(app, 2);
        MikePDFApp.OpenInRunningApp(charlie);
        WaitForTabs(app, "alpha", "bravo", "charlie");
        app.WaitForDocument(7);
        var engines = app.EngineIds();

        var strip = app.Require("DocumentTabs").BoundingRectangle;
        var from = Centre(app.TabItems()[2]);
        app.Drag(from, new System.Drawing.Point(from.X, strip.Bottom + 220));
        app.WaitFor("a second window", () => app.AllWindows().Count == 2, TimeSpan.FromSeconds(20));
        WaitForTabs(app, "alpha", "bravo");
        var other = app.ForWindow(SecondWindows(app).Single());
        WaitForTabs(other, "charlie");
        other.WaitForDocument(7);
        Assert.Equal(engines, app.EngineIds()); // the same engines: the document was not opened again
        app.Capture("tabs-torn-out");

        // Drag it back onto the first window's strip. (A window with one tab is moved, not merged, so the second window gets a second tab first.)
        other.Window.SetForeground(); // a file from Explorer goes to the window used last
        Thread.Sleep(500);
        MikePDFApp.OpenInRunningApp(Pdf("delta", 2));
        WaitForTabs(other, "charlie", "delta");
        var stripOfFirst = app.Require("DocumentTabs").BoundingRectangle;
        var delta = Centre(other.TabItems()[1]);
        other.Drag(delta, new System.Drawing.Point(stripOfFirst.Left + 150, stripOfFirst.Top + (stripOfFirst.Height / 2)));
        app.WaitFor("delta merged into the first window", () => app.TabNames().Contains("delta"), TimeSpan.FromSeconds(20));
        WaitForTabs(other, "charlie");
    }

    // ---- the engine budget, TAB-22, and one tab's engine dying, TAB-25 --------------------------------------------------------------------------------------

    [UiFact]
    public void TAB22_WithTheCapAtTwoFiveOpenTabsNeverHaveMoreThanTwoDocumentEnginesAndEveryTabStillShows()
    {
        _policyKey = @"Software\MikePDF.Tests\tabs-" + Guid.NewGuid().ToString("N")[..8];
        using (var key = Registry.CurrentUser.CreateSubKey(_policyKey))
        {
            key.SetValue("MaxLiveTabs", 2, RegistryValueKind.DWord);
        }

        Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", _policyKey);
        var names = new[] { "t1", "t2", "t3", "t4", "t5" };
        using var app = MikePDFApp.Launch(Pdf(names[0], 2));
        app.WaitForDocument(2);
        var most = app.EngineIds().Count;
        for (var i = 1; i < names.Length; i++)
        {
            MikePDFApp.OpenInRunningApp(Pdf(names[i], 2 + i));
            WaitForTabs(app, names[..(i + 1)]);
            app.WaitForDocument(2 + i);
            most = Math.Max(most, app.EngineIds().Count);
            Assert.True(app.EngineIds().Count <= 3, $"{app.EngineIds().Count} engine processes with the cap at 2 (one shared pool and two document engines)");
        }

        Thread.Sleep(1000);
        Assert.True(app.EngineIds().Count <= 3);
        Assert.Equal(3, Math.Max(most, app.EngineIds().Count)); // the cap was reached, so the older tabs were suspended

        for (var i = 0; i < names.Length; i++)
        {
            app.Chord(VirtualKeyShort.ALT, (VirtualKeyShort)((int)VirtualKeyShort.KEY_1 + i));
            WaitForSelected(app, names[i]);
            app.WaitForDocument(i == 0 ? 2 : 2 + i);
            Assert.True(app.EngineIds().Count <= 3);
        }
    }

    [UiFact]
    public void TAB25_KillingTheEngineOfOneTabLeavesTheOtherTabsRendering()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        using var app = MikePDFApp.Launch(alpha);
        app.WaitForDocument(3);
        var first = app.EngineIds();
        MikePDFApp.OpenInRunningApp(bravo);
        WaitForTabs(app, 2);
        app.WaitForDocument(5);
        var all = app.EngineIds();
        var bravoEngine = all.Except(first).Single(); // the one started for the second document
        using (var process = Process.GetProcessById(bravoEngine))
        {
            process.Kill();
            process.WaitForExit(5000);
        }

        app.WaitFor("the banner for the tab that lost its viewer", () => app.Window.FindFirstDescendant(cf => cf.ByName("Back to normal")) is not null || app.Window.FindFirstDescendant(cf => cf.ByName("Restarting the viewer")) is not null, TimeSpan.FromSeconds(20));
        app.WaitFor("the viewer back, on the same page count", () => app.PageCount == 5);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1);
        WaitForSelected(app, "alpha");
        app.WaitForDocument(3);
        Assert.Null(app.Window.FindFirstDescendant(cf => cf.ByName("Restarting the viewer"))); // the other tab shows no banner of its own
        Assert.Null(app.Window.FindFirstDescendant(cf => cf.ByName("Back to normal")));
    }

    // ---- session restore, TAB-12, TAB-14 (end to end), TAB-15, TAB-17, TAB-18 ------------------------------------------------------------------------------------

    private static byte[] WaitForSession(Func<string, bool> condition)
    {
        byte[]? found = null;
        Retry(() => AppData.ReadSession() is { } bytes && condition(Encoding.UTF8.GetString(bytes)) && (found = bytes) is not null);
        return found ?? throw new TimeoutException("session.json never looked as expected");
    }

    private static void Retry(Func<bool> condition)
    {
        FlaUI.Core.Tools.Retry.WhileFalse(condition, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200));
    }

    [UiFact]
    public void TAB14_TAB15_TAB17_ASessionComesBackWithTheActiveTabOpenTheOthersWaitingAndAMissingFileKeptInPlace()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 5);
        var charlie = Pdf("charlie", 7);
        using (var app = MikePDFApp.Launch(alpha, restore: "Always"))
        {
            app.WaitForDocument(3);
            MikePDFApp.OpenInRunningApp(bravo);
            WaitForTabs(app, 2);
            MikePDFApp.OpenInRunningApp(charlie);
            WaitForTabs(app, "alpha", "bravo", "charlie");
            app.WaitForDocument(7);
            app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_2); // bravo is the one shown when it ends
            WaitForSelected(app, "bravo");
            app.WaitForDocument(5);
            app.CloseGracefully();
        }

        var written = Encoding.UTF8.GetString(WaitForSession(text => text.Contains("charlie", StringComparison.Ordinal) && text.Contains("\"cleanExit\":true", StringComparison.Ordinal)));
        Assert.Contains("\"activeTab\":1", written, StringComparison.Ordinal);
        File.Delete(charlie); // gone by the next start

        using (var app = MikePDFApp.Launch(restore: "Always"))
        {
            WaitForTabs(app, "alpha", "bravo", "charlie");
            WaitForSelected(app, "bravo");
            app.WaitForDocument(5);
            Assert.Single(app.AllWindows());
            Assert.Equal(2, app.EngineIds().Count); // TAB-15: one document engine and the pool; alpha and charlie are placeholders
            app.Capture("tabs-restored");

            app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1); // alpha opens when it is first shown
            WaitForSelected(app, "alpha");
            app.WaitForDocument(3);

            app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_3); // charlie is not there any more
            app.WaitFor("the placeholder", () => app.Exists("MissingName"));
            Assert.True(app.Exists("LocateButton"));
            Assert.True(app.Exists("MissingCloseButton"));
            Assert.Equal(3, app.TabItems().Count); // nothing was silently dropped
            Assert.Contains("can't be found", app.TabNames()[2], StringComparison.Ordinal);
            app.Capture("tabs-missing");
            app.Click("MissingCloseButton");
            WaitForTabs(app, "alpha", "bravo");
        }
    }

    [UiFact]
    public void TAB18_APasswordProtectedDocumentAsksAgainAfterARestoreAndTheSessionFileHasNoPassword()
    {
        var encrypted = MikePDFApp.Fixture("encrypted.pdf");
        using (var app = MikePDFApp.Launch(encrypted, restore: "Always"))
        {
            app.Require("PasswordDialog");
            app.Type("5678");
            app.Press(VirtualKeyShort.ENTER);
            app.WaitForDocument(1);
            WaitForSession(text => text.Contains("encrypted", StringComparison.Ordinal));
            app.CloseGracefully();
        }

        var bytes = WaitForSession(text => text.Contains("\"cleanExit\":true", StringComparison.Ordinal));
        var written = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("5678", written, StringComparison.Ordinal);
        Assert.DoesNotContain("password", written, StringComparison.OrdinalIgnoreCase);

        using (var app = MikePDFApp.Launch(restore: "Always"))
        {
            app.WaitFor("the password asked for again", () => app.Exists("PasswordDialog"));
            app.Press(VirtualKeyShort.ESCAPE);
        }
    }

    [UiFact]
    public void AfterAnUnexpectedEndTheAskModeOffersToRestoreAndStartFreshStartsEmpty()
    {
        var alpha = Pdf("alpha", 3);
        using (var app = MikePDFApp.Launch(alpha, restore: "Ask"))
        {
            app.WaitForDocument(3);
            MikePDFApp.OpenInRunningApp(Pdf("bravo", 5));
            WaitForTabs(app, 2);
            WaitForSession(text => text.Contains("bravo", StringComparison.Ordinal) && text.Contains("\"cleanExit\":false", StringComparison.Ordinal));
            MikePDFApp.KillAll(); // not a normal close
        }

        Thread.Sleep(1500); // the package can still be shutting down; a launch right now may be handed to it

        using (var app = MikePDFApp.Launch(restore: "Ask"))
        {
            app.WaitFor("the question", () => app.Exists("RestoreDialog"));
            app.Capture("tabs-restore-ask");
            app.Window.FindFirstDescendant(cf => cf.ByName("Start fresh"))!.AsButton().Invoke();
            app.WaitFor("an empty window", () => !app.Exists("RestoreDialog") && app.TabNames().SequenceEqual(["New tab"]));
            app.CloseGracefully();
        }

        using (var app = MikePDFApp.Launch(restore: "Ask"))
        {
            app.WaitFor("an empty window and no question", () => app.TabNames().SequenceEqual(["New tab"]));
            Assert.False(app.Exists("RestoreDialog"));
        }
    }

    [UiFact]
    public void TAB12_TheEndOfTheSessionAsksNothingAndLeavesTheSessionFileWritten()
    {
        using var app = MikePDFApp.Launch(Pdf("alpha", 3), restore: "Always");
        app.WaitForDocument(3);
        MikePDFApp.OpenInRunningApp(Pdf("bravo", 5));
        WaitForTabs(app, 2);
        WaitForSession(text => text.Contains("bravo", StringComparison.Ordinal));
        AppData.DeleteSession();
        var answer = SendMessageW(new IntPtr(app.Window.Properties.NativeWindowHandle.Value), 0x0011, IntPtr.Zero, IntPtr.Zero); // WM_QUERYENDSESSION
        Assert.Equal(1, answer.ToInt64()); // the window does not stand in the way
        var written = Encoding.UTF8.GetString(WaitForSession(text => text.Contains("bravo", StringComparison.Ordinal)));
        Assert.Contains("alpha", written, StringComparison.Ordinal);
        Assert.Contains("\"cleanExit\":false", written, StringComparison.Ordinal); // the next start offers to restore
        Assert.False(app.Exists("CloseDirtyDialog"));
        Assert.Equal(2, app.TabItems().Count);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ---- the Jump List and the shell's recent files, FA-13 (FA-14 is not tested: see the completion note) --------------------------------------------------------------------------------------

    [UiFact]
    public void FA13_TheJumpListHasTheTwoTasksWithTheRightArgumentsOnceTheWindowIsUp()
    {
        var dump = "jumplist-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        using var app = MikePDFApp.Launch(environment: new Dictionary<string, string> { ["MIKEPDF_TEST_JUMPLIST_DUMP"] = dump });
        var path = Path.Combine(AppData.PrivateFolder() ?? throw new InvalidOperationException("no private folder"), dump);
        app.WaitFor("the Jump List saved", () => File.Exists(path));
        var lines = File.ReadAllLines(path);
        File.Delete(path);
        Assert.Contains("--open-dialog|Open a PDF...|", lines);
        Assert.Contains("--new-window|New window|", lines);
        Assert.True(app.Exists("EmptyState")); // the window was there first
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
