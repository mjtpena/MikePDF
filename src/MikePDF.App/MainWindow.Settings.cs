using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MikePDF.App.Services;
using MikePDF.App.Views;
using MikePDF.Core.Colour;
using MikePDF.Core.Commands;
using MikePDF.Core.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MikePDF.App;

/// <summary>Settings, About and the command palette as the window shows them, and the actions behind their commands (export, import, reset, clear).</summary>
public sealed partial class MainWindow : ISettingsHost
{
    // ---- applying settings ----------------------------------------------------------------------------------------------------------------

    private void OnSettingsChanged(IReadOnlyList<string> keys) => ApplySettings(keys);

    /// <summary>Makes the window follow the settings. <paramref name="keys"/> are the ones that changed, or null for all (at startup).</summary>
    private void ApplySettings(IReadOnlyList<string>? keys)
    {
        bool Has(string key) => keys is null || keys.Contains(key);
        var settings = _services.Settings;
        if (Has("general.theme"))
        {
            var theme = settings.Value<string>("general.theme") switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            Root.RequestedTheme = theme;
            ApplyCaptionButtonColours(theme);
        }

        if (Has("view.pageColour"))
        {
            Pages.PageColour = Enum.TryParse<PageColourMode>(settings.Value<string>("view.pageColour"), out var mode) && Enum.IsDefined(mode) ? mode : PageColourMode.Normal;
            ApplyPageColourMenu();
        }

        if (Has("recent.maxItems") || Has("view.restorePosition") || Has("view.defaultArrangement") || Has("view.defaultFlow"))
        {
            _services.ApplyToDocuments();
            RefreshRecent();
        }

        RefreshCommands();
    }

    // The caption buttons are drawn by Windows in the system theme unless told, so a theme chosen in Settings needs them told.
    private void ApplyCaptionButtonColours(ElementTheme theme)
    {
        var bar = AppWindow.TitleBar;
        Windows.UI.Color? colour = theme switch { ElementTheme.Light => Colors.Black, ElementTheme.Dark => Colors.White, _ => null };
        bar.ButtonForegroundColor = colour;
        bar.ButtonHoverForegroundColor = colour;
        bar.ButtonPressedForegroundColor = colour;
        bar.ButtonInactiveForegroundColor = colour is null ? null : Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    }

    // ---- the settings page ------------------------------------------------------------------------------------------------------------------

    private void ShowSettings(string? section) => ShowSettingsTab(section);

    private void CloseSettings() => CloseSettingsTab();

    // ---- the palette ---------------------------------------------------------------------------------------------------------------------------

    private void TogglePalette()
    {
        if (Palette.IsOpen)
        {
            Palette.Close();
            return;
        }

        var context = BuildContext();
        var engine = new PaletteEngine(
            [
                new GoToPagePaletteSource(() => context.PageCount, Strings.Get),
                new CommandPaletteSource(_services.Commands, () => context, Strings.Get, chord => ChordDisplay.Format(chord, null, KeyNames.CharacterFor)),
                new TabsPaletteSource(() => [.. _app.Windows.SelectMany(w => w.TabListing())], Strings.Get),
                new RecentFilesPaletteSource(() => [.. _store.Recent.Select(r => r.Path)], Strings.Get),
                new SettingsPagesPaletteSource(() => [.. SettingsView.Sections.Select(s => (s.Id, Strings.Get(s.TitleKey)))], Strings.Get),
            ],
            id => _services.History.Rank(id));
        engine.SourceFailed += (source, e) => System.Diagnostics.Debug.WriteLine("Palette source " + source.GetType().Name + " failed: " + e.GetType().Name);
        Palette.Open(engine, ExecutePaletteItem);
    }

    private void ExecutePaletteItem(PaletteItem item)
    {
        switch (item.Kind)
        {
            case PaletteItemKind.Command:
                DispatcherQueue.TryEnqueue(() => _ = RunCommandAsync(item.Key));
                break;
            case PaletteItemKind.Tab:
                if (Guid.TryParseExact(item.Key, "N", out var tabId) && _app.WindowOfTab(tabId) is { } owner)
                {
                    owner.ShowTab(tabId);
                }

                break;
            case PaletteItemKind.RecentFile:
                _ = OpenPathAsync(item.Key);
                break;
            case PaletteItemKind.SettingsPage:
                ShowSettings(item.Key);
                break;
            case PaletteItemKind.GoToPage:
                if (_session is { IsOpen: true } session && int.TryParse(item.Key, out var page))
                {
                    session.JumpToPage(page - 1);
                    Pages.FocusPages();
                }

                break;
            default:
                break;
        }
    }

    private void OnPaletteClosed(bool focusRestored)
    {
        if (!focusRestored && SettingsPage.Visibility != Visibility.Visible && _session is { IsOpen: true })
        {
            Pages.FocusPages();
        }

        RefreshCommands();
    }

    // ---- About -----------------------------------------------------------------------------------------------------------------------------------

    private async Task ShowAboutAsync()
    {
        var dialog = NewDialog();
        dialog.Title = Strings.Get("About_Title");
        dialog.Content = new ScrollViewer { Content = AboutContent.Build(_services), MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.CloseButtonText = Strings.Get("Dialog_Close");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "AboutDialog");
        await ShowDialogAsync(dialog);
        if (_session is { IsOpen: true } && SettingsPage.Visibility != Visibility.Visible)
        {
            Pages.FocusPages();
        }
    }

    // ---- ISettingsHost -----------------------------------------------------------------------------------------------------------------------

    ElementTheme ISettingsHost.Theme => Root.RequestedTheme;

    Task<ContentDialogResult> ISettingsHost.ShowDialogAsync(ContentDialog dialog) => ShowDialogAsync(dialog);

    Task ISettingsHost.RunCommandAsync(string commandId, object? parameter) => RunCommandAsync(commandId, parameter);

    CommandAvailability ISettingsHost.Availability(string commandId) => _services.Commands.Find(commandId)?.CanExecute(BuildContext()) ?? CommandAvailability.Disabled(Reasons.Policy);

    Task<bool> ISettingsHost.ChangeShortcutAsync(CommandDefinition command) =>
        ShortcutDialog.ShowAsync(Root.XamlRoot, Root.RequestedTheme, _services.Commands, command, KeyNames.CharacterFor, SettingsPage.Announce, ShowDialogAsync);

    string? ISettingsHost.CharacterFor(int key) => KeyNames.CharacterFor(key);

    // ---- the settings commands -------------------------------------------------------------------------------------------------------------------

    private void RegisterSettingsActions(Action<string, Action> sync, Action<string, Func<Task>> async)
    {
        async("app.clearRecent", ClearRecentAsync);
        async("app.clearHistory", ClearHistoryAsync);
        sync("app.clearUsageHistory", () => _services.History.Clear());
        async("app.exportSettings", ExportSettingsAsync);
        async("app.importSettings", ImportSettingsAsync);
        async("app.undoImportSettings", UndoImportAsync);
        async("app.resetSettings", ResetSettingsAsync);
        async("app.exportShortcuts", ExportShortcutsAsync);
        async("app.importShortcuts", ImportShortcutsAsync);
        async("app.resetShortcuts", ResetShortcutsAsync);
        sync("app.copyShortcuts", CopyShortcutList);
    }

    private async Task<bool> ConfirmAsync(string messageKey, string confirmKey)
    {
        var dialog = NewDialog();
        dialog.Content = new TextBlock { Text = Strings.Get(messageKey), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get(confirmKey);
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "ConfirmDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private void Said(string message)
    {
        if (SettingsPage.Visibility == Visibility.Visible)
        {
            SettingsPage.Announce(message);
        }
        else
        {
            ShowBannerText(InfoBarSeverity.Informational, message, string.Empty);
            _bannerTimer.Interval = TimeSpan.FromSeconds(4);
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
    }

    private async Task ClearRecentAsync()
    {
        if (await ConfirmAsync("Settings_ClearRecent_Confirm", "Dialog_Confirm"))
        {
            _store.ClearRecent();
            _hasRecent = false;
            RefreshRecent();
            Said(Strings.Get("Settings_ClearHistory_Done"));
        }
    }

    private async Task ClearHistoryAsync()
    {
        if (!await ConfirmAsync("Settings_ClearHistory_Confirm", "Dialog_Confirm"))
        {
            return;
        }

        _store.ClearRecent();
        _store.ClearPositions();
        _services.History.Clear();
        _hasRecent = false;
        RefreshRecent();
        Said(Strings.Get("Settings_ClearHistory_Done"));
    }

    private async Task ResetSettingsAsync()
    {
        if (await ConfirmAsync("Settings_Reset_Confirm", "Dialog_Confirm"))
        {
            _services.Settings.ResetAll();
            Said(Strings.Get("Settings_Reset_Done"));
        }
    }

    private async Task ResetShortcutsAsync()
    {
        if (await ConfirmAsync("Shortcuts_ResetAll_Confirm", "Dialog_Confirm"))
        {
            _services.Commands.ResetAll();
            Said(Strings.Get("Shortcuts_ResetAll"));
        }
    }

    private void CopyShortcutList()
    {
        var text = new StringBuilder();
        foreach (var command in _services.Commands.Commands)
        {
            var chords = _services.Commands.EffectiveChords(command.Id);
            text.Append(Strings.Get(command.NameKey)).Append('\t').AppendLine(chords.Count == 0 ? Strings.Get("Shortcuts_None") : ChordDisplay.Format(chords, null, KeyNames.CharacterFor));
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text.ToString());
        try
        {
            Clipboard.SetContent(package);
            Said(Strings.Get("Shortcuts_Copied"));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; the person can try again.
        }
    }

    // ---- export and import -----------------------------------------------------------------------------------------------------------------------

    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<byte[]?> PickAndReadAsync(Action<ImportRejection> refused)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(file.Path);
            var bytes = SettingsTransfer.ReadLimited(stream, out var rejection);
            if (bytes is null)
            {
                refused(rejection);
            }

            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            refused(ImportRejection.NotJson);
            return null;
        }
    }

    private async Task ExportSettingsAsync()
    {
        var path = await PickSavePathAsync("MikePDF-settings");
        if (path is not null)
        {
            await WriteExportAsync(path, SettingsTransfer.ExportSettings(_services.Settings), "Settings_Export_Done", "Settings_Export_Failed");
        }
    }

    private async Task ExportShortcutsAsync()
    {
        var path = await PickSavePathAsync("MikePDF-shortcuts");
        if (path is not null)
        {
            await WriteExportAsync(path, SettingsTransfer.ExportShortcuts(_services.Commands), "Shortcuts_ExportDone", "Shortcuts_ExportFailed");
        }
    }

    private async Task WriteExportAsync(string path, byte[] bytes, string doneKey, string failedKey)
    {
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            Said(Strings.Get(doneKey));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Said(Strings.Get(failedKey));
        }
    }

    private async Task ImportSettingsAsync()
    {
        var rejected = ImportRejection.None;
        var bytes = await PickAndReadAsync(r => rejected = r);
        if (bytes is null)
        {
            if (rejected != ImportRejection.None)
            {
                await ShowMessageAsync(Strings.Get("Settings_Import_Invalid"));
            }

            return;
        }

        var preview = SettingsTransfer.Preview(bytes, _services.Settings, _services.Commands);
        if (!preview.IsAccepted)
        {
            await ShowMessageAsync(Strings.Get(preview.Rejection == ImportRejection.PolicyBlocked ? "Settings_Import_Blocked" : "Settings_Import_Invalid"));
            return;
        }

        var summary = new StringBuilder(Strings.Format("Settings_Import_Summary", preview.Changes.Count, preview.Locked.Count, preview.Unknown.Count, preview.Invalid.Count));
        if (preview.Keybindings is { } shortcuts)
        {
            summary.Append(' ').Append(Strings.Format("Settings_Import_Shortcuts", shortcuts.Count));
        }

        if (preview.NewerVersion)
        {
            summary.Append(' ').Append(Strings.Get("Settings_Import_Newer"));
        }

        if (await ConfirmImportAsync("Settings_Import_Title", summary.ToString(), SkippedLines(preview)))
        {
            SettingsTransfer.Apply(preview, _services.Settings);
            _canUndoImport = SettingsTransfer.CanUndo(_services.Settings);
            Said(Strings.Get("Settings_Import_Done"));
        }
    }

    private async Task ImportShortcutsAsync()
    {
        var rejected = ImportRejection.None;
        var bytes = await PickAndReadAsync(r => rejected = r);
        if (bytes is null)
        {
            if (rejected != ImportRejection.None)
            {
                await ShowMessageAsync(Strings.Get("Shortcuts_ImportInvalid"));
            }

            return;
        }

        var preview = SettingsTransfer.PreviewShortcuts(bytes, _services.Settings, _services.Commands);
        if (!preview.IsAccepted)
        {
            await ShowMessageAsync(Strings.Get(preview.Rejection == ImportRejection.PolicyBlocked ? "Settings_Import_Blocked" : "Shortcuts_ImportInvalid"));
            return;
        }

        var summary = Strings.Format("Shortcuts_ImportSummary", preview.Keybindings!.Count, preview.BindingProblems.Count);
        if (await ConfirmImportAsync("Shortcuts_ImportTitle", summary, SkippedLines(preview)))
        {
            _services.Commands.LoadOverrides(preview.Keybindings!);
            Said(Strings.Get("Shortcuts_ImportDone"));
        }
    }

    private static IEnumerable<string> SkippedLines(SettingsImportPreview preview) =>
        preview.BindingProblems.Select(p => Strings.Format("Shortcuts_ImportSkip_" + p.Kind, p.Command, p.Text, p.Other ?? string.Empty));

    private async Task<bool> ConfirmImportAsync(string titleKey, string summary, IEnumerable<string> details)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 480 };
        var text = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(text, "ImportSummary");
        panel.Children.Add(text);
        foreach (var line in details.Take(20))
        {
            panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        }

        var dialog = NewDialog();
        dialog.Title = Strings.Get(titleKey);
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 400 };
        dialog.PrimaryButtonText = Strings.Get("Settings_Import_Confirm");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "ImportDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string message)
    {
        var dialog = NewDialog();
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        dialog.CloseButtonText = Strings.Get("Dialog_Close");
        AutomationProperties.SetAutomationId(dialog, "MessageDialog");
        await ShowDialogAsync(dialog);
    }

    private Task UndoImportAsync()
    {
        if (SettingsTransfer.Undo(_services.Settings))
        {
            Said(Strings.Get("Settings_Import_Undone"));
        }

        _canUndoImport = SettingsTransfer.CanUndo(_services.Settings);
        return Task.CompletedTask;
    }
}
