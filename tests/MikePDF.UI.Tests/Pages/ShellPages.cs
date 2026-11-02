using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace MikePDF.UI.Tests.Pages;

/// <summary>The command palette as a person sees it: a search box, a list of results and a count. Part of the page objects, so FlaUI stays in this folder (ADR-0009).</summary>
public sealed class PalettePage(MikePDFApp app)
{
    public bool IsOpen => app.Exists("CommandPalette");

    public void Open()
    {
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_P);
        app.WaitFor("the palette", () => IsOpen);
        app.WaitFor("focus in the palette's box", () => HasFocus);
    }

    public void Close()
    {
        if (Query.Length > 0)
        {
            app.Press(VirtualKeyShort.ESCAPE); // the first Esc clears the query
            app.WaitFor("the query cleared", () => Query.Length == 0);
        }

        app.Press(VirtualKeyShort.ESCAPE);
        app.WaitFor("the palette closed", () => !IsOpen);
    }

    public bool HasFocus => app.Find("PaletteBox")?.Properties.HasKeyboardFocus.ValueOrDefault ?? false;

    public string Query => app.Require("PaletteBox").Patterns.Value.Pattern.Value.Value;

    public string BoxName => app.Require("PaletteBox").Name;

    public void Type(string text) => app.Type(text);

    /// <summary>The rows, by their spoken names ("Zoom in, Ctrl+=, View").</summary>
    public IReadOnlyList<string> Rows() =>
        [.. app.Require("PaletteList").FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(i => i.Name)];

    public string? Selected => app.Require("PaletteList").Patterns.Selection.Pattern.Selection.ValueOrDefault is { Length: > 0 } selection ? selection[0].Name : null;

    public string Status => app.Find("PaletteStatus")?.Properties.Name.ValueOrDefault ?? string.Empty;

    public void WaitForRow(string startsWith) => app.WaitFor("a row starting " + startsWith, () => Rows().Any(r => r.StartsWith(startsWith, StringComparison.Ordinal)));

    /// <summary>Runs the first row for a query, from the keyboard alone: open, type, Enter.</summary>
    public void Run(string query, string firstRowStartsWith)
    {
        Open();
        Type(query);
        app.WaitFor("the right first row for " + query, () => Selected?.StartsWith(firstRowStartsWith, StringComparison.Ordinal) == true);
        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("the palette closed", () => !IsOpen);
    }
}

/// <summary>The Settings page, its sections, controls, the shortcut table and the shortcut dialog.</summary>
public sealed class SettingsPage(MikePDFApp app)
{
    public bool IsOpen => app.Exists("SettingsBack");

    public void Open()
    {
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);
        app.WaitFor("the settings page", () => IsOpen);
    }

    public void Close()
    {
        app.Click("SettingsBack");
        app.WaitFor("settings closed", () => !IsOpen);
    }

    public void GoTo(string section)
    {
        app.Click("SettingsSection_" + section);
        app.WaitFor("the " + section + " section", () => SectionHeading != string.Empty);
        System.Threading.Thread.Sleep(300);
    }

    public string SectionHeading => app.Find("SectionHeading")?.Properties.Name.ValueOrDefault ?? string.Empty;

    public AutomationElement Setting(string key) => app.Require("Setting_" + key);

    public bool HasSetting(string key) => app.Exists("Setting_" + key);

    public void Choose(string key, string item)
    {
        var combo = Setting(key).AsComboBox();
        combo.Select(item);
    }

    public string Announcement => app.Find("SettingsAnnouncement")?.Properties.Name.ValueOrDefault ?? string.Empty;

    // ---- shortcuts ---------------------------------------------------------------------------------------------------------------------------

    public void Filter(string text)
    {
        var box = app.Require("ShortcutFilter");
        box.Focus();
        box.Patterns.Value.Pattern.SetValue(text);
        System.Threading.Thread.Sleep(400);
    }

    public string ShortcutRow(string commandId) => app.Require("Shortcut_" + commandId).Name;

    public void Change(string commandId)
    {
        app.Click("ShortcutChange_" + commandId);
        app.WaitFor("the shortcut dialog", () => app.Exists("ShortcutDialog"));
    }

    public void Reset(string commandId) => app.Click("ShortcutReset_" + commandId);

    public bool HasReset(string commandId) => app.Exists("ShortcutReset_" + commandId);

    public void TypeChord(string text) => app.Require("ShortcutTyped").Patterns.Value.Pattern.SetValue(text);

    public void CaptureChord(params VirtualKeyShort[] keys)
    {
        app.Require("ShortcutCaptureBox").Focus();
        System.Threading.Thread.Sleep(200);
        app.Chord(keys);
    }

    public string DialogLive => app.Find("ShortcutLive")?.Properties.Name.ValueOrDefault ?? string.Empty;

    public string DialogProblem => app.Find("ShortcutProblem")?.Properties.Name.ValueOrDefault ?? string.Empty;

    /// <summary>The dialog's buttons by their words: Assign (or Reassign), Remove shortcut, Cancel.</summary>
    public AutomationElement DialogButton(string name) =>
        Retry.WhileNull(() => app.Require("ShortcutDialog").FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))), MikePDFApp.Patience, TimeSpan.FromMilliseconds(150), ignoreException: true).Result
        ?? throw new InvalidOperationException("No dialog button " + name);

    public void Press(string buttonName)
    {
        DialogButton(buttonName).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("the dialog closed", () => !app.Exists("ShortcutDialog"));
    }
}

/// <summary>The packaged app's own data, which stays between launches within a run: the journeys that change settings start from none and leave none.</summary>
public static class AppData
{
    /// <summary>Deletes settings.json (and the history of commands) from the registered package's private folder. The app must not be running.</summary>
    public static void ResetSettings()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages))
        {
            return;
        }

        foreach (var package in Directory.EnumerateDirectories(packages, "*MikePDF*"))
        {
            var folder = Path.Combine(package, "LocalState", "private");
            foreach (var name in new[] { "settings.json", "settings.prev.json", "palette-history.json" })
            {
                try
                {
                    File.Delete(Path.Combine(folder, name));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Left for the next run; a journey that depended on it would say so.
                }
            }
        }
    }
}
