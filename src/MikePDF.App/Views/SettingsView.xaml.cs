using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core.Commands;
using MikePDF.Core.Settings;
using Windows.System;

namespace MikePDF.App.Views;

/// <summary>What the Settings page needs from the window: dialogs, commands and a polite announcement.</summary>
internal interface ISettingsHost
{
    ElementTheme Theme { get; }

    Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog);

    /// <summary>Runs a command through the registry (the buttons on the page are views of commands, like every other button).</summary>
    Task RunCommandAsync(string commandId, object? parameter = null);

    CommandAvailability Availability(string commandId);

    Task<bool> ChangeShortcutAsync(CommandDefinition command);

    string? CharacterFor(int key);
}

/// <summary>
/// The Settings page: the sections, with one control for each setting that exists, generated from the registry; a locked control is disabled, says why, and has a
/// focusable note beside it so a keyboard user can reach the reason. Changes apply at once; there is no Save button.
/// </summary>
internal sealed partial class SettingsView : UserControl
{
    internal static readonly (string Id, string TitleKey)[] Sections =
    [
        ("general", "Settings_Section_General"),
        ("reading", "Settings_Section_Reading"),
        ("privacy", "Settings_Section_Privacy"),
        ("shortcuts", "Settings_Section_Shortcuts"),
        ("policies", "Settings_Section_Policies"),
        ("about", "Settings_Section_About"),
    ];

    private AppServices? _services;
    private ISettingsHost? _host;
    private string _section = "general";
    private bool _building;
    private bool _selfChange;

    public SettingsView()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Settings_Title");
        AutomationProperties.SetName(Nav, Strings.Get("Settings_Nav_Name"));
        AutomationProperties.SetName(BackButton, Strings.Get("Settings_Back_Name"));
        ToolTipService.SetToolTip(BackButton, Strings.Get("Settings_Back"));
        AutomationProperties.SetName(SectionPanel, Strings.Get("Settings_Title"));
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised when the person leaves Settings (the back button or Esc).</summary>
    internal event Action? CloseRequested;

    internal string CurrentSection => _section;

    internal void Attach(AppServices services, ISettingsHost host)
    {
        _services = services;
        _host = host;
        Nav.MenuItems.Clear();
        foreach (var (id, key) in Sections)
        {
            var item = new NavigationViewItem { Content = Strings.Get(key), Tag = id };
            AutomationProperties.SetAutomationId(item, "SettingsSection_" + id);
            Nav.MenuItems.Add(item);
        }

        services.Settings.SettingsChanged += _ => Dispatcher_Refresh();
        services.Policy.Changed += Dispatcher_Refresh;
        services.Commands.BindingsChanged += () => { if (_section == "shortcuts") { Dispatcher_Refresh(); } };
    }

    internal void Show(string? section)
    {
        var id = Sections.Any(s => s.Id == section) ? section! : _section;
        _building = true;
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == id);
        _building = false;
        Build(id);
    }

    internal void FocusFirst()
    {
        if (Nav.SelectedItem is NavigationViewItem item)
        {
            item.Focus(FocusState.Programmatic);
        }
    }

    internal void Announce(string message)
    {
        Announcement.Text = string.Empty;
        Announcement.Text = message;
    }

    // ---- navigation ----------------------------------------------------------------------------------------------------------------------

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_building && args.SelectedItem is NavigationViewItem { Tag: string id })
        {
            Build(id);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    // The person's own change is already on screen, and rebuilding the page would take focus away: only a change from elsewhere (policy, an import) is redrawn.
    private bool Commit(string key, object value)
    {
        _selfChange = true;
        try
        {
            return _services!.Settings.Set(key, value) == SetResult.Applied;
        }
        finally
        {
            _selfChange = false;
        }
    }

    private void Dispatcher_Refresh()
    {
        if (Visibility != Visibility.Visible || _services is null || _selfChange)
        {
            return;
        }

        // A change by policy or an import while the page is open: show it in place. (The person's own change is already on screen; rebuilding would move focus.)
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_building && Visibility == Visibility.Visible && !HasFocusInside())
            {
                Build(_section);
            }
        });
    }

    private bool HasFocusInside() => XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox or ComboBox { IsDropDownOpen: true };

    // ---- building a section --------------------------------------------------------------------------------------------------------------

    private void Build(string id)
    {
        if (_services is null || _host is null)
        {
            return;
        }

        _section = id;
        SectionPanel.Children.Clear();
        _building = true;
        try
        {
            switch (id)
            {
                case "general":
                    AddHeading("Settings_Section_General");
                    AddSettings(SettingSection.General);
                    break;
                case "reading":
                    AddHeading("Settings_Section_Reading");
                    AddSettings(SettingSection.Reading);
                    break;
                case "privacy":
                    AddHeading("Settings_Section_Privacy");
                    AddSettings(SettingSection.Privacy);
                    AddDataActions();
                    break;
                case "shortcuts":
                    AddHeading("Settings_Section_Shortcuts");
                    BuildShortcuts();
                    break;
                case "policies":
                    AddHeading("Settings_Section_Policies");
                    BuildPolicies();
                    break;
                default:
                    AddHeading("Settings_Section_About");
                    SectionPanel.Children.Add(AboutContent.Build(_services));
                    break;
            }
        }
        finally
        {
            _building = false;
        }

        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void AddHeading(string key)
    {
        var heading = new TextBlock { Text = Strings.Get(key), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        AutomationProperties.SetAutomationId(heading, "SectionHeading");
        SectionPanel.Children.Add(heading);
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        return block;
    }

    // ---- settings ------------------------------------------------------------------------------------------------------------------------

    private void AddSettings(SettingSection section)
    {
        foreach (var definition in SettingsRegistry.All.Where(d => d.Section == section))
        {
            SectionPanel.Children.Add(SettingRow(definition));
        }
    }

    private StackPanel SettingRow(SettingDefinition definition)
    {
        var effective = _services!.Settings.Effective(definition.Key);
        var name = Strings.Get(definition.NameKey);
        var description = Strings.Get(definition.DescriptionKey);
        var row = new StackPanel { Spacing = 4 };
        var label = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        row.Children.Add(label);
        row.Children.Add(Caption(description));

        var control = MakeControl(definition, effective);
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetAutomationId(control, "Setting_" + definition.Key);
        row.Children.Add(control);

        if (effective.Locked)
        {
            control.IsEnabled = false;
            AutomationProperties.SetHelpText(control, Strings.Get("Settings_Locked"));
            var note = new ContentControl { IsTabStop = true, UseSystemFocusVisuals = true, Margin = new Thickness(0, 2, 0, 0) };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
            line.Children.Add(new TextBlock { Text = Strings.Get("Settings_Locked"), TextWrapping = TextWrapping.Wrap });
            note.Content = line;
            AutomationProperties.SetName(note, Strings.Format("Settings_Row_Locked", name, ValueText(definition, effective.Value)));
            AutomationProperties.SetHelpText(note, Strings.Format("Settings_Locked_Description", name));
            AutomationProperties.SetAutomationId(note, "SettingLocked_" + definition.Key);
            row.Children.Add(note);
        }

        return row;
    }

    private static string ValueText(SettingDefinition definition, object value) => definition.Kind switch
    {
        SettingKind.Enum => Strings.Get(definition.ValueKey((string)value)),
        SettingKind.Bool => Strings.Get((bool)value ? "Settings_On" : "Settings_Off"),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
    };

    private Control MakeControl(SettingDefinition definition, EffectiveSetting effective)
    {
        switch (definition.Kind)
        {
            case SettingKind.Bool:
                var toggle = new ToggleSwitch { IsOn = (bool)effective.Value, OnContent = Strings.Get("Settings_On"), OffContent = Strings.Get("Settings_Off") };
                toggle.Toggled += (_, _) =>
                {
                    if (!_building)
                    {
                        Commit(definition.Key, toggle.IsOn);
                    }
                };
                return toggle;
            case SettingKind.Number:
                var box = new NumberBox
                {
                    Minimum = definition.Min,
                    Maximum = definition.Max,
                    Value = (int)effective.Value,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    SmallChange = 1,
                    LargeChange = 5,
                    MinWidth = 140,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                box.ValueChanged += (_, args) =>
                {
                    if (!_building && !double.IsNaN(args.NewValue))
                    {
                        Commit(definition.Key, (int)Math.Round(args.NewValue));
                    }
                };
                return box;
            case SettingKind.Enum:
                var combo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var value in Offered(definition, (string)effective.Value))
                {
                    combo.Items.Add(new ComboBoxItem { Content = Strings.Get(definition.ValueKey(value)), Tag = value });
                }

                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (string)effective.Value);
                combo.SelectionChanged += (_, _) =>
                {
                    if (!_building && combo.SelectedItem is ComboBoxItem { Tag: string value })
                    {
                        Commit(definition.Key, value);
                        if (definition.Key == "general.theme")
                        {
                            Announce(Strings.Format("Settings_Theme_Announce", Strings.Get(definition.ValueKey(value))));
                        }
                    }
                };
                return combo;
            default:
                var text = new TextBox { Text = (string)effective.Value, MaxLength = definition.MaxLength, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
                text.LostFocus += (_, _) => Commit(definition.Key, text.Text);
                return text;
        }
    }

    // This build can show an external link's address and can block it. "Ask first, then open" opens nothing here, so it is offered only when a policy has chosen it.
    private static IEnumerable<string> Offered(SettingDefinition definition, string current) =>
        definition.Key == "links.behaviour" && current != "Confirm" ? definition.Values!.Where(v => v != "Confirm") : definition.Values!;

    // ---- data actions (the buttons are views of commands) ----------------------------------------------------------------------------------

    private Button CommandButton(string commandId, string textKey, string automationId)
    {
        var availability = _host!.Availability(commandId);
        var button = new Button { Content = Strings.Get(textKey), IsEnabled = availability.IsEnabled };
        AutomationProperties.SetAutomationId(button, automationId);
        if (!availability.IsEnabled && availability.Reason is { } reason)
        {
            AutomationProperties.SetHelpText(button, Strings.Get(Reasons.ResourceKey(reason)));
        }

        button.Click += async (_, _) => await _host.RunCommandAsync(commandId);
        return button;
    }

    private void AddDataActions()
    {
        var host = _host!;
        var note = Caption(Strings.Get("Settings_DataFolder"));
        SectionPanel.Children.Add(note);
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(CommandButton("app.clearHistory", "Settings_ClearHistory", "SettingsClearHistory"));
        actions.Children.Add(CommandButton("app.resetSettings", "Settings_Reset", "SettingsReset"));
        SectionPanel.Children.Add(actions);

        if (host.Availability("app.exportSettings") is { IsEnabled: false, Reason: Reasons.Policy })
        {
            SectionPanel.Children.Add(Caption(Strings.Get("Settings_Import_Blocked"))); // hidden, with the reason, when the organisation turned it off
            return;
        }

        var transfer = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        transfer.Children.Add(CommandButton("app.exportSettings", "Settings_Export", "SettingsExport"));
        transfer.Children.Add(CommandButton("app.importSettings", "Settings_Import", "SettingsImport"));
        transfer.Children.Add(CommandButton("app.undoImportSettings", "Settings_Import_Undo", "SettingsUndoImport"));
        SectionPanel.Children.Add(transfer);
        SectionPanel.Children.Add(Caption(Strings.Get("Settings_Export_AuthorNote")));
    }

    // ---- policies --------------------------------------------------------------------------------------------------------------------------

    private void BuildPolicies()
    {
        var snapshot = _services!.Policy.Current;
        var report = PolicyReport.Build(snapshot, Strings.Get);
        if (!report.AnyPresent)
        {
            var none = new TextBlock { Text = Strings.Get("Settings_NoPolicies"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(none, "PoliciesNone");
            SectionPanel.Children.Add(none);
        }
        else
        {
            SectionPanel.Children.Add(Caption(Strings.Get("Settings_Policies_Header")));
            if (snapshot.Entries.Any(e => e.State == PolicyState.ReadFailed))
            {
                SectionPanel.Children.Add(Caption(Strings.Get("Settings_Policies_ReadFailed")));
            }

            SectionPanel.Children.Add(AboutContent.PolicyList(report));
            var copy = new Button { Content = Strings.Get("Settings_Policies_Copy") };
            AutomationProperties.SetAutomationId(copy, "PoliciesCopy");
            copy.Click += (_, _) =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(report.ToPlainText([Strings.Get("AppName")], Strings.Get));
                try
                {
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    Announce(Strings.Get("About_Copied"));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Another program holds the clipboard.
                }
            };
            SectionPanel.Children.Add(copy);
        }

        SectionPanel.Children.Add(Caption(Strings.Get("Settings_Policies_Registry")));
    }
}
