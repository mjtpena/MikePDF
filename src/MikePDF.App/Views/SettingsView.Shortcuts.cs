using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MikePDF.App.Services;
using MikePDF.Core.Commands;

namespace MikePDF.App.Views;

/// <summary>The Keyboard shortcuts page: every command with its shortcuts, Change and Reset for each, and the export, import and reset-all buttons.</summary>
internal sealed partial class SettingsView
{
    private ListView? _shortcutList;
    private TextBox? _shortcutFilter;

    private void BuildShortcuts()
    {
        var host = _host!;
        var registry = _services!.Commands;
        var filter = new TextBox { PlaceholderText = Strings.Get("Shortcuts_Filter"), MaxLength = 64, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(filter, Strings.Get("Shortcuts_Filter"));
        AutomationProperties.SetAutomationId(filter, "ShortcutFilter");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MaxHeight = 460 };
        AutomationProperties.SetName(list, Strings.Get("Settings_Section_Shortcuts"));
        AutomationProperties.SetAutomationId(list, "ShortcutTable");
        list.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: CommandDefinition command })
            {
                await ChangeAsync(command);
            }
        };
        filter.TextChanged += (_, _) => FillShortcuts();
        _shortcutFilter = filter;
        _shortcutList = list;
        SectionPanel.Children.Add(Caption(Strings.Get("Settings_Section_Shortcuts")));
        SectionPanel.Children.Add(filter);
        SectionPanel.Children.Add(list);
        FillShortcuts();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(CommandButton("app.resetShortcuts", "Shortcuts_ResetAll", "ShortcutsResetAll"));
        buttons.Children.Add(CommandButton("app.copyShortcuts", "Shortcuts_Copy", "ShortcutsCopy"));
        SectionPanel.Children.Add(buttons);
        if (host.Availability("app.exportShortcuts") is { IsEnabled: false, Reason: Reasons.Policy })
        {
            SectionPanel.Children.Add(Caption(Strings.Get("Settings_Import_Blocked")));
        }
        else
        {
            var transfer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            transfer.Children.Add(CommandButton("app.exportShortcuts", "Shortcuts_Export", "ShortcutsExport"));
            transfer.Children.Add(CommandButton("app.importShortcuts", "Shortcuts_Import", "ShortcutsImport"));
            SectionPanel.Children.Add(transfer);
        }

        if (registry.UpdateConflicts.Count > 0)
        {
            var title = new TextBlock { Text = Strings.Get("Shortcuts_UpdateConflicts"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
            AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            SectionPanel.Children.Add(title);
            foreach (var conflict in registry.UpdateConflicts)
            {
                var text = Strings.Format("Shortcuts_UpdateConflict_Line", Show(conflict.Chord), NameOf(conflict.ClaimedBy), NameOf(conflict.Command));
                var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(line, "ShortcutUpdateConflict_" + conflict.Command);
                SectionPanel.Children.Add(line);
            }
        }

        foreach (var problem in _services.ShortcutProblems)
        {
            SectionPanel.Children.Add(Caption(Strings.Format("Shortcuts_Unreadable", problem.Command)));
        }
    }

    private static string NameOf(string commandId) => CommandCatalog.ById.TryGetValue(commandId, out var command) ? Strings.Get(command.NameKey) : commandId;

    private string Show(Chord chord) => ChordDisplay.Format(chord, null, _host!.CharacterFor);

    private async Task ChangeAsync(CommandDefinition command)
    {
        await _host!.ChangeShortcutAsync(command);
        if (_section == "shortcuts")
        {
            FillShortcuts();
            _shortcutList?.Focus(FocusState.Programmatic);
        }
    }

    private void FillShortcuts()
    {
        if (_shortcutList is null || _services is null)
        {
            return;
        }

        var registry = _services.Commands;
        var folded = PaletteText.Fold(_shortcutFilter?.Text ?? string.Empty).Trim();
        var tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var selected = (_shortcutList.SelectedItem as FrameworkElement)?.Tag as CommandDefinition;
        _shortcutList.Items.Clear();
        foreach (var command in registry.Commands)
        {
            var name = Strings.Get(command.NameKey);
            var chords = registry.EffectiveChords(command.Id);
            var chordText = chords.Count == 0 ? Strings.Get("Shortcuts_None") : string.Join(", ", chords.Select(Show));
            var haystack = PaletteText.Fold(name + " " + chordText + " " + Strings.Get(CommandDefinition.CategoryKey(command.Category)));
            if (tokens.Any(t => !haystack.Contains(t, StringComparison.Ordinal)))
            {
                continue;
            }

            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 4, 4, 4), Tag = command };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(label, name);
            var shown = new TextBlock { Text = chordText + (registry.IsCustomised(command.Id) ? " (" + Strings.Get("Shortcuts_Changed") + ")" : string.Empty), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(shown, 1);
            AutomationProperties.SetAccessibilityView(label, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(shown, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var change = new Button { Content = Strings.Get("Shortcuts_Change") };
            Grid.SetColumn(change, 2);
            AutomationProperties.SetAutomationId(change, "ShortcutChange_" + command.Id);
            AutomationProperties.SetName(change, Strings.Format("Shortcuts_Change_Title", name));
            change.Click += async (_, _) => await ChangeAsync(command);
            row.Children.Add(label);
            row.Children.Add(shown);
            row.Children.Add(change);
            if (registry.IsCustomised(command.Id))
            {
                var reset = new Button { Content = Strings.Get("Shortcuts_Reset") };
                Grid.SetColumn(reset, 3);
                AutomationProperties.SetAutomationId(reset, "ShortcutReset_" + command.Id);
                AutomationProperties.SetName(reset, Strings.Format("Shortcuts_Reset_Name", name));
                reset.Click += (_, _) =>
                {
                    registry.Reset(command.Id);
                    Announce(Strings.Format("Shortcuts_ResetDone", name));
                    FillShortcuts();
                };
                row.Children.Add(reset);
            }

            AutomationProperties.SetName(row, Strings.Format("Shortcuts_Row_Automation", name, chordText));
            AutomationProperties.SetAutomationId(row, "Shortcut_" + command.Id);
            _shortcutList.Items.Add(row);
            if (command == selected)
            {
                _shortcutList.SelectedItem = row;
            }
        }

        if (_shortcutList.Items.Count == 0)
        {
            var none = new TextBlock { Text = Strings.Get("Shortcuts_NoMatch"), Margin = new Thickness(8) };
            AutomationProperties.SetAutomationId(none, "ShortcutNoMatch");
            _shortcutList.Items.Add(none);
        }

    }
}
