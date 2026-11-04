using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// UI journeys for saving and recovering (docs/features/save-pipeline.md SP-25, SP-28 to SP-30, SP-32, SP-35; docs/design/screens/14-errors-and-recovery.md). No tool creates edits yet, so the journeys use the
/// test seam MIKEPDF_TEST_FAKE_DIRTY, which applies one real highlight through the document's edit model once it is open: the save, the journal, the recovery and the close prompts then run for real, through the
/// sandboxed writer host and a fresh engine. Save as uses MIKEPDF_TEST_SAVE_AS_PATH instead of the file picker. They drive the real keyboard and mouse against the installed package.
/// </summary>
public sealed class SaveJourneyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-save-" + Guid.NewGuid().ToString("N")[..8]);

    public SaveJourneyTests()
    {
        MikePDFApp.KillAll();
        AppData.DeleteSession();
        AppData.DeleteJournals();
    }

    public void Dispose()
    {
        MikePDFApp.KillAll();
        AppData.DeleteSession();
        AppData.DeleteJournals();
        try
        {
            if (Directory.Exists(_work))
            {
                foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's executable can stay locked for a moment after its process ends.
        }
    }

    private string Pdf(string name, int pages) => TestPdfs.Numbered(_work, name + ".pdf", pages);

    private static string Latin1(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static void WaitForTabs(MikePDFApp app, params string[] names) => app.WaitFor("the tabs " + string.Join(", ", names), () => app.TabNames().SequenceEqual(names));

    private static AutomationElement? DialogButton(MikePDFApp app, string automationId) => app.Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private static void PressDialog(MikePDFApp app, string automationId) => DialogButton(app, automationId)!.AsButton().Invoke();

    // ---- SP-30, journey 3: edit, save, the file has it, the document stays where it was ---------------------------------------------------------------

    [UiFact]
    public void SP30_CtrlSSavesTheEditsWritesThemIntoTheFileAndTheTabIsCleanAgain()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true);
        app.WaitForDocument(3);

        // Dirty is in the tab's name, the window's name and the command state, not by a dot alone (SP-35).
        WaitForTabs(app, "alpha, unsaved changes");
        Assert.Contains("unsaved changes", app.Window.Name, StringComparison.Ordinal);
        Assert.True(app.Exists("SaveButton"));
        Assert.True(app.Require("SaveButton").IsEnabled);
        Assert.Equal("Ctrl+S", app.AcceleratorKey("SaveButton"));
        app.Capture("save-dirty");

        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);
        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

        WaitForTabs(app, "alpha");
        app.WaitFor("Save gone from the bar (nothing to save)", () => app.Find("SaveButton") is not { } button || button.Properties.IsOffscreen.ValueOrDefault);
        Assert.DoesNotContain("unsaved changes", app.Window.Name, StringComparison.Ordinal);
        var saved = File.ReadAllBytes(alpha);
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes are an exact prefix (an incremental update)");
        Assert.Contains("/Highlight", Latin1(alpha), StringComparison.Ordinal);
        app.WaitForDocument(3); // reopened on the saved file, at the same place
        lock (heard)
        {
            Assert.Equal(1, heard.Count(h => h.Activity == "SaveAnnouncement" && h.Text == "Saved alpha")); // "Saved", once (SP-35)
        }

        Assert.Empty(AppData.Journals()); // the journal goes after the replace
        Assert.Empty(Directory.EnumerateFiles(_work, ".*").ToList()); // and no temporary file is left in the folder
        app.Capture("save-clean");
    }

    [UiFact]
    public void SP30_SaveIsOffWhenThereIsNothingToSaveAndSaysWhy()
    {
        using var app = MikePDFApp.Launch(Pdf("alpha", 2));
        app.WaitForDocument(2);

        Assert.False(app.Find("SaveButton") is { } button && !button.Properties.IsOffscreen.ValueOrDefault);
        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

        app.WaitFor("the reason", () => app.Window.FindFirstDescendant(cf => cf.ByName("There are no unsaved changes")) is not null);
    }

    // ---- SP-25: Save as ------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void SP25_SaveAsWritesTheCopySwitchesTheTabToItAndLeavesTheOriginal()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        var copy = Path.Combine(_work, "copy.pdf");
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true, environment: new Dictionary<string, string> { ["MIKEPDF_TEST_SAVE_AS_PATH"] = copy });
        app.WaitForDocument(3);
        WaitForTabs(app, "alpha, unsaved changes");

        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_S);

        WaitForTabs(app, "copy");
        app.WaitForDocument(3);
        Assert.Equal(original, File.ReadAllBytes(alpha));
        Assert.Contains("/Highlight", Latin1(copy), StringComparison.Ordinal);
        Assert.Empty(AppData.Journals());
        Assert.Equal("copy", app.SelectedTabName());
    }

    // ---- save did not happen: what happened, the changes are still here, and what to do (SP-18, SP-35) ------------------------------------------------

    [UiFact]
    public void SP18_ASaveIntoAReadOnlyFileSaysSoKeepsTheEditsAndOffersASaveAsThatWorks()
    {
        var alpha = Pdf("alpha", 2);
        var copy = Path.Combine(_work, "writable.pdf");
        File.SetAttributes(alpha, FileAttributes.ReadOnly);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true, environment: new Dictionary<string, string> { ["MIKEPDF_TEST_SAVE_AS_PATH"] = copy });
        app.WaitForDocument(2);
        WaitForTabs(app, "alpha, unsaved changes");

        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

        app.WaitFor("the strip", () => app.Exists("SaveSaveAs"));
        Assert.Contains("can't be changed here", app.Require("SaveStatus").Name, StringComparison.Ordinal);
        Assert.Contains("Your changes are still here", app.Require("SaveStatus").Name, StringComparison.Ordinal);
        Assert.Equal(["alpha, unsaved changes"], app.TabNames()); // the changes are still here
        Assert.Single(AppData.Journals());
        app.Capture("save-readonly");

        app.Click("SaveSaveAs");

        WaitForTabs(app, "writable");
        Assert.Contains("/Highlight", Latin1(copy), StringComparison.Ordinal);
        Assert.DoesNotContain("/Highlight", Latin1(alpha), StringComparison.Ordinal); // the read-only original was never written
    }

    // ---- TAB-10, the Save button of the close prompt really saves ---------------------------------------------------------------------------------------

    [UiFact]
    public void TAB10_TheSaveButtonOfTheClosePromptSavesAndThenClosesTheTab()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true);
        app.WaitForDocument(3);
        WaitForTabs(app, "alpha, unsaved changes");

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        app.WaitFor("the question", () => app.Exists("CloseDirtyDialog"));
        Assert.True(DialogButton(app, "PrimaryButton")!.IsEnabled); // Save is on now
        PressDialog(app, "PrimaryButton");

        WaitForTabs(app, "New tab");
        Assert.True(File.ReadAllBytes(alpha).Length > original.Length);
        Assert.Contains("/Highlight", Latin1(alpha), StringComparison.Ordinal);
        Assert.Empty(AppData.Journals());
    }

    [UiFact]
    public void TAB10_DontSaveLeavesTheFileAndDeletesTheJournal()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true);
        app.WaitForDocument(3);
        WaitForTabs(app, "alpha, unsaved changes");
        Assert.Single(AppData.Journals()); // flushed before the edit was acknowledged

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        app.WaitFor("the question", () => app.Exists("CloseDirtyDialog"));
        PressDialog(app, "SecondaryButton");

        WaitForTabs(app, "New tab");
        Assert.Equal(original, File.ReadAllBytes(alpha));
        Assert.Empty(AppData.Journals());
    }

    // ---- SP-28, journey 8: the app dies with unsaved edits; the next start offers them back --------------------------------------------------------------

    [UiFact]
    public void SP28_AnAppKilledWithUnsavedEditsOffersThemBackAndRestoreOpensTheDocumentModified()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        using (var first = MikePDFApp.Launch(alpha, fakeDirty: true))
        {
            first.WaitForDocument(3);
            WaitForTabs(first, "alpha, unsaved changes");
            Assert.Single(AppData.Journals());
            MikePDFApp.KillAll(); // the process dies: nothing is saved, nothing is cleared
        }

        using var second = MikePDFApp.Launch(keepJournals: true);
        second.WaitFor("the question", () => second.Exists("RecoverDialog"));
        Assert.NotNull(second.Window.FindFirstDescendant(cf => cf.ByName("Not now")));
        Assert.False(second.Exists("RecoverReopenTabs")); // restore is Never: there is no session question to carry, but the unsaved work is still asked about
        second.Capture("recover-dialog");
        var item = second.Require("RecoverItem_1");
        Assert.Contains("alpha.pdf", item.Name, StringComparison.Ordinal);
        Assert.Contains("1 markup change", item.Name, StringComparison.Ordinal);
        PressDialog(second, "PrimaryButton"); // Restore is the default

        second.WaitFor("the document back, modified", () => second.TabNames().SequenceEqual(["alpha, unsaved changes"]));
        second.WaitForDocument(3);
        Assert.Equal(original, File.ReadAllBytes(alpha)); // nothing was saved by recovery

        // And it can be saved from there.
        second.FocusPages();
        second.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        WaitForTabs(second, "alpha");
        Assert.Contains("/Highlight", Latin1(alpha), StringComparison.Ordinal);
        Assert.Empty(AppData.Journals());
    }

    [UiFact]
    public void SP29_NotNowKeepsTheJournalAndTheEmptyWindowListsItAndOpeningItAsksAboutThatDocument()
    {
        var alpha = Pdf("alpha", 3);
        using (var first = MikePDFApp.Launch(alpha, fakeDirty: true))
        {
            first.WaitForDocument(3);
            WaitForTabs(first, "alpha, unsaved changes");
            MikePDFApp.KillAll();
        }

        using var second = MikePDFApp.Launch(keepJournals: true);
        second.WaitFor("the question", () => second.Exists("RecoverDialog"));
        second.Press(VirtualKeyShort.ESCAPE); // Esc is Not now: a keystroke never throws anything away
        second.WaitFor("the question gone", () => !second.Exists("RecoverDialog"));
        Assert.Single(AppData.Journals());

        second.WaitFor("the list in the empty window", () => second.Exists("RecoverList") && second.Require("RecoverHeading").Name.Contains("1 document", StringComparison.Ordinal));
        second.Capture("recover-list");
        var entry = second.Require("RecoverList").FindFirstDescendant(cf => cf.ByName("Open alpha.pdf: 1 markup change"));
        Assert.NotNull(entry);
        entry!.Click();

        second.WaitFor("the question about this document", () => second.Exists("RecoverDialog") && second.Exists("RecoverSummary"));
        PressDialog(second, "PrimaryButton");
        second.WaitFor("the document modified", () => second.TabNames().SequenceEqual(["alpha, unsaved changes"]));
    }

    [UiFact]
    public void SP29_DiscardAsksOnceMoreDeletesTheJournalAndNothingIsAskedAgain()
    {
        var alpha = Pdf("alpha", 3);
        var original = File.ReadAllBytes(alpha);
        using (var first = MikePDFApp.Launch(alpha, fakeDirty: true))
        {
            first.WaitForDocument(3);
            WaitForTabs(first, "alpha, unsaved changes");
            MikePDFApp.KillAll();
        }

        using (var second = MikePDFApp.Launch(keepJournals: true))
        {
            second.WaitFor("the question", () => second.Exists("RecoverDialog"));
            PressDialog(second, "SecondaryButton"); // Discard
            second.WaitFor("the confirmation", () => second.Exists("RecoverDiscardDialog"));
            second.Press(VirtualKeyShort.ESCAPE); // Cancel: nothing is discarded by an accident
            second.WaitFor("the confirmation gone", () => !second.Exists("RecoverDiscardDialog"));
            Assert.Single(AppData.Journals());
            MikePDFApp.KillAll();
        }

        using (var third = MikePDFApp.Launch(keepJournals: true))
        {
            third.WaitFor("the question", () => third.Exists("RecoverDialog"));
            PressDialog(third, "SecondaryButton");
            third.WaitFor("the confirmation", () => third.Exists("RecoverDiscardDialog"));
            PressDialog(third, "PrimaryButton");
            third.WaitFor("the journal gone", () => AppData.Journals().Count == 0);
            Assert.Equal(original, File.ReadAllBytes(alpha));
        }

        using var fourth = MikePDFApp.Launch(Pdf("beta", 2), keepJournals: true);
        fourth.WaitForDocument(2);
        Assert.False(fourth.Exists("RecoverDialog"));
        Assert.Equal(["beta"], fourth.TabNames());
    }

    // ---- one dialog after a crash, not two (save-pipeline section 16) ----------------------------------------------------------------------------------

    private static bool SessionNames(string text, params string[] names) => names.All(n => text.Contains(n, StringComparison.Ordinal));

    [UiFact]
    public void AfterACrashWithBothUnsavedChangesAndASessionToAskAboutThereIsOneDialogWhichCarriesTheSessionChoice()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 2);
        using (var first = MikePDFApp.Launch(alpha, restore: "Ask", fakeDirty: true))
        {
            first.WaitForDocument(3);
            MikePDFApp.OpenInRunningApp(bravo);
            first.WaitFor("two tabs", () => first.TabNames().Count == 2);
            first.WaitFor("the session written", () => AppData.ReadSession() is { } bytes && SessionNames(Encoding.UTF8.GetString(bytes), "alpha", "bravo"), TimeSpan.FromSeconds(40));
            MikePDFApp.KillAll();
        }

        using var second = MikePDFApp.Launch(restore: "Ask", keepJournals: true);
        second.WaitFor("the question", () => second.Exists("RecoverDialog"));
        Assert.True(second.Exists("RecoverReopenTabs")); // the session question rides in it
        Assert.Contains("2 documents", second.Require("RecoverReopenTabs").Name, StringComparison.Ordinal);
        Assert.False(second.Exists("RestoreDialog")); // and there is no second dialog
        second.Capture("recover-with-session");
        PressDialog(second, "PrimaryButton");

        second.WaitFor("both documents back, modified", () => second.TabNames().Count == 2 && second.TabNames().All(n => n.EndsWith(", unsaved changes", StringComparison.Ordinal)), TimeSpan.FromSeconds(40));
        Assert.False(second.Exists("RestoreDialog")); // the session question was answered by the check box
    }

    [UiFact]
    public void UncheckingTheSessionChoiceStartsFreshButStillRestoresTheChosenDocuments()
    {
        var alpha = Pdf("alpha", 3);
        var bravo = Pdf("bravo", 2);
        using (var first = MikePDFApp.Launch(alpha, restore: "Ask", fakeDirty: true))
        {
            first.WaitForDocument(3);
            MikePDFApp.OpenInRunningApp(bravo);
            first.WaitFor("two tabs", () => first.TabNames().Count == 2);
            first.WaitFor("the session written", () => AppData.ReadSession() is { } bytes && SessionNames(Encoding.UTF8.GetString(bytes), "alpha", "bravo"), TimeSpan.FromSeconds(40));
            MikePDFApp.KillAll();
        }

        using var second = MikePDFApp.Launch(restore: "Ask", keepJournals: true);
        second.WaitFor("the question", () => second.Exists("RecoverDialog"));
        second.Click("RecoverReopenTabs"); // unchecked
        PressDialog(second, "PrimaryButton");

        // The documents with unsaved changes are the person's work and come back whatever the session choice; the session itself (the layout) is not restored and nothing else is asked.
        second.WaitFor("the documents back", () => second.TabNames().Count >= 1 && second.TabNames().All(n => n.EndsWith(", unsaved changes", StringComparison.Ordinal)), TimeSpan.FromSeconds(40));
        Assert.False(second.Exists("RestoreDialog"));
    }

    // ---- the file changed on disk (SP-21, design 14 section 2.9) ----------------------------------------------------------------------------------------

    [UiFact]
    public void SP21_AFileChangedByAnotherProgramRaisesTheBannerWhenTheTabIsShownAgainAndReloadOrKeepAnswersIt()
    {
        var alpha = Pdf("alpha", 3);
        using var app = MikePDFApp.Launch(alpha);
        app.WaitForDocument(3);
        Assert.False(app.Exists("ChangedBar") && app.Require("ChangedBar").Properties.IsOffscreen.ValueOrDefault is false);

        File.AppendAllText(alpha, "\n% another program was here\n"); // another program rewrote the file
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        WaitForTabs(app, "alpha", "New tab");
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1); // back to the document: the file is looked at now, not on a timer

        app.WaitFor("the banner", () => app.Exists("ChangedReload") && app.Exists("ChangedKeep"));
        Assert.Contains("changed on disk", app.Require("ChangedBar").Name, StringComparison.Ordinal);
        app.Capture("changed-on-disk");
        app.Click("ChangedKeep");
        app.WaitFor("the banner gone", () => !app.Exists("ChangedReload"));

        // Kept: it stays quiet until the file changes again.
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_2);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1);
        Assert.False(app.Exists("ChangedReload"));

        File.AppendAllText(alpha, "% and again\n");
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_2);
        app.Chord(VirtualKeyShort.ALT, VirtualKeyShort.KEY_1);
        app.WaitFor("the banner again", () => app.Exists("ChangedReload"));
        app.Click("ChangedReload");
        app.WaitFor("the banner gone after the reload", () => !app.Exists("ChangedReload"));
        app.WaitForDocument(3);
    }

    // ---- reload with unsaved changes asks first (commands.md file.reload) -------------------------------------------------------------------------------

    [UiFact]
    public void ReloadWithUnsavedChangesAsksAndDropsThemOnlyWhenTheyAreGivenUp()
    {
        var alpha = Pdf("alpha", 3);
        using var app = MikePDFApp.Launch(alpha, fakeDirty: true);
        app.WaitForDocument(3);
        WaitForTabs(app, "alpha, unsaved changes");

        app.FocusPages();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_R);
        app.WaitFor("the question", () => app.Exists("ReloadDirtyDialog"));
        app.Press(VirtualKeyShort.ESCAPE); // Cancel is the default
        app.WaitFor("the question gone", () => !app.Exists("ReloadDirtyDialog"));
        Assert.Equal(["alpha, unsaved changes"], app.TabNames());

        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_R);
        app.WaitFor("the question again", () => app.Exists("ReloadDirtyDialog"));
        PressDialog(app, "PrimaryButton");

        WaitForTabs(app, "alpha");
        app.WaitForDocument(3);
        Assert.Empty(AppData.Journals());
    }
}
