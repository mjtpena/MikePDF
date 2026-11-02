using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Core.Colour;
using MikePDF.Core.Commands;
using MikePDF.Core.Settings;
using MikePDF.Core.Windowing;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MikePDF.App;

/// <summary>
/// The command host: every button, menu item, shortcut and palette row is a view of one registry (docs/features/command-palette-shortcuts.md). The window turns
/// key presses into chords for the dispatcher, keeps the controls' names, tooltips, accelerator text and enabled state in step with the registry, and holds the
/// one place where each command's action is written.
/// </summary>
public sealed partial class MainWindow
{
    private const double ArrowStepDip = 48;
    private const int ImeProcessKey = 229; // VK_PROCESSKEY: an input method is composing, so the key is not a chord

    private readonly AppServices _services;
    private readonly Dictionary<string, Func<object?, Task>> _actions = new(StringComparer.Ordinal);
    private readonly List<CommandBinding> _bindings = [];
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _policyTimer;
    private int _modalDepth;
    private bool _hasRecent;
    private bool _canUndoImport;

    [Flags]
    private enum Parts
    {
        Click = 1,
        Label = 2,
        Tooltip = 4,
        Enabled = 8,
        Accelerator = 16,
        All = Click | Label | Tooltip | Enabled | Accelerator,
        Display = Label | Tooltip | Enabled | Accelerator,
    }

    private sealed class CommandBinding(DependencyObject target, CommandDefinition command, Parts parts)
    {
        public DependencyObject Target { get; } = target;

        public CommandDefinition Command { get; } = command;

        public Parts Parts { get; } = parts;

        public string Last { get; set; } = string.Empty;
    }

    // ---- startup --------------------------------------------------------------------------------------------------------------------------

    private void InitializeCommands()
    {
        RegisterActions();
        BindCommands();
        _services.Commands.BindingsChanged += RefreshCommands;
        _services.Policy.Changed += RefreshCommands;
        _services.Settings.SettingsChanged += OnSettingsChanged;
        _services.Settings.SaveFailed += () => DispatcherQueue.TryEnqueue(() => ShowBannerText(InfoBarSeverity.Warning, Strings.Get("Settings_SaveFailed"), string.Empty, retry: true));
        _hasRecent = _store.Recent.Count > 0;
        _canUndoImport = SettingsTransfer.CanUndo(_services.Settings);
        Palette.Closed += OnPaletteClosed;
        SettingsPage.Attach(_services, this);
        SettingsPage.CloseRequested += CloseSettings;
        ApplySettings(null);

        // Policy is read again every two seconds (a read is a size query per known name), which keeps a change inside the five seconds the spec allows.
        _policyTimer = DispatcherQueue.CreateTimer();
        _policyTimer.Interval = TimeSpan.FromSeconds(2);
        _policyTimer.IsRepeating = true;
        _policyTimer.Tick += (_, _) => _services.Policy.Refresh();
        _policyTimer.Start();
    }

    private void BindCommands()
    {
        Bind(OpenButton, "file.open");
        Bind(EmptyOpenButton, "file.open", Parts.Click | Parts.Accelerator);
        Bind(BackButton, "nav.back");
        Bind(ForwardButton, "nav.forward");
        Bind(SidebarButton, "pane.toggleLeft", Parts.Display); // a toggle: its own Checked and Unchecked events carry the state; the command sets IsChecked
        Bind(FindButton, "find.open");
        Bind(ZoomOutButton, "view.zoomOut");
        Bind(ZoomInButton, "view.zoomIn");
        Bind(ZoomButton, "view.zoomTo", Parts.Enabled);
        Bind(FitWidthButton, "view.fitWidth");
        Bind(FitPageButton, "view.fitPage");
        Bind(RotateLeftButton, "view.rotateLeft");
        Bind(RotateRightButton, "view.rotateRight");
        Bind(PropertiesButton, "file.properties");
        Bind(FullScreenButton, "view.readingMode");
        Bind(PageIndicator, "nav.goToPage", Parts.Click | Parts.Enabled | Parts.Accelerator);
        Bind(ZoomActualSizeItem, "view.zoomPhysical", Parts.All);
        Bind(ZoomFitWidthItem, "view.fitWidth", Parts.All);
        Bind(ZoomFitPageItem, "view.fitPage", Parts.All);
        foreach (var item in ZoomMenu.Items.OfType<MenuFlyoutItem>().Where(i => i.Tag is string))
        {
            var zoom = double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
            Bind(item, "view.zoomTo", Parts.Click, parameter: zoom);
        }

        Bind(PageColourNormal, "view.pageColorsNormal", Parts.Click);
        Bind(PageColourInverted, "view.pageColorsDark", Parts.Click);
        Bind(PageColourSepia, "view.pageColorsSepia", Parts.Click);
        Bind(ThumbnailsTab, "pane.thumbnails", Parts.Tooltip | Parts.Accelerator);
        Bind(BookmarksTab, "pane.outline", Parts.Tooltip | Parts.Accelerator);
        Bind(FindPreviousButton, "find.previous", Parts.Click | Parts.Accelerator);
        Bind(FindNextButton, "find.next", Parts.Click | Parts.Accelerator);
        Bind(FindCloseButton, "find.close", Parts.Click | Parts.Accelerator);
        Bind(NewTabButton, "file.newTab", Parts.Click | Parts.Tooltip | Parts.Accelerator);
        Bind(LocateButton, "file.locate", Parts.Click | Parts.Accelerator);
        Bind(MissingCloseButton, "file.close", Parts.Click | Parts.Accelerator);
        RefreshCommands();
    }

    // ---- the binder -------------------------------------------------------------------------------------------------------------------------

    private void Bind(DependencyObject target, string commandId, Parts parts = Parts.All, object? parameter = null)
    {
        var command = _services.Commands.Find(commandId) ?? throw new InvalidOperationException("No command " + commandId);
        var effective = target is AppBarButton or AppBarToggleButton or MenuFlyoutItem ? parts : parts & ~Parts.Label; // only toolbar buttons and menu items are named from the command; others keep their own wording

        if (effective.HasFlag(Parts.Click))
        {
            switch (target)
            {
                case MenuFlyoutItem item:
                    item.Click += (_, _) => _ = RunCommandAsync(commandId, parameter);
                    break;
                case ButtonBase button:
                    button.Click += (_, _) => _ = RunCommandAsync(commandId, parameter);
                    break;
                default:
                    break;
            }
        }

        _bindings.Add(new CommandBinding(target, command, effective));
    }

    private static string LabelOf(CommandDefinition command)
    {
        var key = command.NameKey[..^"_Name".Length] + "_Label";
        var label = Strings.Get(key);
        return label == key ? Strings.Get(command.NameKey) : label;
    }

    /// <summary>Brings every bound control in step with the registry: name, tooltip, accelerator text, enabled state and the reason it is not.</summary>
    private void RefreshCommands()
    {
        var context = BuildContext();
        foreach (var binding in _bindings)
        {
            var command = binding.Command;
            var availability = command.CanExecute(context);
            var chords = _services.Commands.EffectiveChords(command.Id);
            var canonical = string.Join(", ", chords);
            var shown = ChordDisplay.Format(chords, null, KeyNames.CharacterFor);
            var state = string.Join('\u0001', availability.IsEnabled, availability.Reason, canonical, shown);
            if (state == binding.Last)
            {
                continue;
            }

            binding.Last = state;
            var name = Strings.Get(command.NameKey);
            var target = binding.Target;
            if (binding.Parts.HasFlag(Parts.Label))
            {
                switch (target)
                {
                    case AppBarButton bar:
                        bar.Label = LabelOf(command);
                        break;
                    case AppBarToggleButton toggle:
                        toggle.Label = LabelOf(command);
                        break;
                    case MenuFlyoutItem item:
                        item.Text = LabelOf(command);
                        break;
                    default:
                        break;
                }
            }

            if (binding.Parts.HasFlag(Parts.Tooltip) && target is UIElement element)
            {
                ToolTipService.SetToolTip(element, shown.Length == 0 ? name : name + " (" + shown + ")");
            }

            if (binding.Parts.HasFlag(Parts.Accelerator))
            {
                AutomationProperties.SetAcceleratorKey(target, canonical);
                if (target is MenuFlyoutItem menuItem)
                {
                    menuItem.KeyboardAcceleratorTextOverride = shown;
                }
            }

            if (binding.Parts.HasFlag(Parts.Enabled) && target is Control control)
            {
                control.IsEnabled = availability.IsEnabled;
                AutomationProperties.SetHelpText(control, availability.IsEnabled || availability.Reason is null ? string.Empty : Strings.Get(Reasons.ResourceKey(availability.Reason)));
            }
        }
    }

    /// <summary>What the commands need to know, as a snapshot. Cheap: it reads state the window already holds.</summary>
    private CommandContext BuildContext()
    {
        var session = _session;
        var open = session is { IsOpen: true };
        var policy = _services.Policy.Current;
        return new CommandContext
        {
            HasDocument = open,
            PageCount = open ? session!.Controller.PageCount : 0,
            CurrentPage = open ? Math.Max(0, session!.Controller.CurrentPage) : 0,
            HasPath = open && !string.IsNullOrEmpty(session!.Path),
            CanGoBack = open && session!.CanGoBack,
            CanGoForward = open && session!.CanGoForward,
            CanCopy = !open || session!.CanCopy,
            HasSelection = open && session!.Selection is { HasSelection: true },
            FindOpen = FindOpen,
            FullScreen = _fullScreen,
            SidebarOpen = Sidebar.IsPaneOpen,
            Busy = Busy.IsActive,
            SettingsOpen = _active?.Model.Kind == TabKind.Settings,
            TabCount = Model.Tabs.Count,
            ActiveTabIndex = Math.Max(0, Model.ActiveIndex),
            CanReopenClosed = _app.Model.Closed.Count > 0,
            TabMissing = _active?.Missing == true,
            ModalOpen = _modalDepth > 0,
            HasRecent = _hasRecent,
            HasUsageHistory = !_services.History.IsEmpty,
            HasCustomShortcuts = _services.Commands.HasCustomShortcuts,
            CanUndoImport = _canUndoImport,
            PageColour = Pages.PageColour,
            ImportExportAllowed = policy.ImportExportAllowed,
            PrintingAllowed = policy.PrintingAllowed,
        };
    }

    // ---- running a command ------------------------------------------------------------------------------------------------------------------

    /// <summary>Runs a command if it can run now; otherwise says why. Exceptions never escape: the window stays up and a banner says which command failed.</summary>
    internal async Task RunCommandAsync(string commandId, object? parameter = null)
    {
        var command = _services.Commands.Find(commandId);
        if (command is null || !_actions.TryGetValue(commandId, out var action))
        {
            return;
        }

        var availability = command.CanExecute(BuildContext());
        if (!availability.IsEnabled)
        {
            AnnounceUnavailable(command, availability);
            return;
        }

        if (!_running.Add(commandId))
        {
            return; // already running (a dialog is open); a second press does not start another
        }

        try
        {
            _services.History.Record(commandId);
            await action(parameter);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Only the kind of exception is kept: nothing from the document, no paths.
            System.Diagnostics.Debug.WriteLine("Command " + commandId + " failed: " + e.GetType().Name);
            ShowBannerText(InfoBarSeverity.Error, Strings.Format("Command_Failed", Strings.Get(command.NameKey)), string.Empty);
            _bannerTimer.Interval = TimeSpan.FromSeconds(6);
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
        finally
        {
            _running.Remove(commandId);
            RefreshCommands();
        }
    }

    private void AnnounceUnavailable(CommandDefinition command, CommandAvailability availability)
    {
        if (availability.Reason == Reasons.OwnerNoCopy)
        {
            OnCopyBlocked(); // the banner the owner's restriction has always had
            return;
        }

        var reason = Strings.Get(Reasons.ResourceKey(availability.Reason ?? Reasons.NoDocument));
        ShowBannerText(InfoBarSeverity.Informational, Strings.Format("Banner_Disabled_Title", Strings.Get(command.NameKey)), reason);
        _bannerTimer.Interval = TimeSpan.FromSeconds(4);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void ShowBannerText(InfoBarSeverity severity, string title, string message, bool retry = false)
    {
        Banner.Severity = severity;
        Banner.Title = title;
        Banner.Message = message;
        Banner.IsClosable = true;
        BannerAction.Visibility = Visibility.Collapsed;
        if (retry)
        {
            BannerAction.Content = Strings.Get("Settings_Retry");
            BannerAction.Visibility = Visibility.Visible;
            _retrySettings = true;
        }

        Banner.IsOpen = true;
    }

    private bool _retrySettings;

    // ---- keys -------------------------------------------------------------------------------------------------------------------------------

    private static bool IsKeyDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsModifier(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    // Esc is not a command: it is "cancel and close" for whatever is on top, and no chord can take it (reserved). Everything else goes through the dispatcher.
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _fullScreen && !FindOpen && !Palette.IsOpen && SettingsPage.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            SetFullScreen(false);
            return;
        }

        if (IsModifier(e.Key) || (int)e.Key == ImeProcessKey || IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            return; // a modifier alone is not a chord; an input method is composing; the Windows key is never part of one
        }

        var modifiers = (IsKeyDown(VirtualKey.Control) ? ChordModifiers.Control : 0) | (IsKeyDown(VirtualKey.Menu) ? ChordModifiers.Alt : 0) | (IsKeyDown(VirtualKey.Shift) ? ChordModifiers.Shift : 0);
        var focused = Root.XamlRoot is null ? null : FocusManager.GetFocusedElement(Root.XamlRoot);
        var input = new DispatchInput
        {
            Context = BuildContext(),
            TextInputFocused = focused is TextBox or PasswordBox or RichEditBox or ComboBox { IsEditable: true },
            SurfaceFocused = ReferenceEquals(focused, Pages),
            IsRepeat = e.KeyStatus.WasKeyDown,
            AltGrActive = IsKeyDown(VirtualKey.RightMenu) && IsKeyDown(VirtualKey.LeftControl) && KeyNames.LayoutHasAltGr(),
            PaletteOpen = Palette.IsOpen,
        };
        var result = _services.Dispatcher.Dispatch(new Chord(modifiers, (int)e.Key), input);
        switch (result.Kind)
        {
            case DispatchKind.Execute:
                e.Handled = true;
                _ = RunCommandAsync(result.Command!.Id);
                break;
            case DispatchKind.Disabled:
                e.Handled = true;
                AnnounceUnavailable(result.Command!, result.Availability);
                break;
            case DispatchKind.Swallow:
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    // ---- the actions ------------------------------------------------------------------------------------------------------------------------

    private void RegisterActions()
    {
        void Sync(string id, Action action) => _actions[id] = _ =>
        {
            action();
            return Task.CompletedTask;
        };
        void Async(string id, Func<Task> action) => _actions[id] = _ => action();

        Async("file.open", PickAndOpenAsync);
        Sync("file.newTab", NewTab);
        Sync("file.newWindow", () => _app.CreateWindow(like: this));
        Async("file.close", CloseActiveTabAsync);
        Async("file.closeOthers", CloseOthersAsync);
        Async("file.closeToRight", CloseToTheRightAsync);
        Async("file.reopenClosed", () => _app.ReopenClosedAsync(this));
        Sync("file.closeWindow", Close);
        Sync("file.copyPath", CopyPath);
        Async("file.showInFolder", ShowInFolderAsync);
        Async("file.locate", LocateAsync);
        Sync("tab.next", () => GoToTab(Model.Next(+1)));
        Sync("tab.previous", () => GoToTab(Model.Next(-1)));
        Sync("tab.goto1", () => GoToTab(Model.ByNumber(1)));
        Sync("tab.goto2", () => GoToTab(Model.ByNumber(2)));
        Sync("tab.goto3", () => GoToTab(Model.ByNumber(3)));
        Sync("tab.goto4", () => GoToTab(Model.ByNumber(4)));
        Sync("tab.goto5", () => GoToTab(Model.ByNumber(5)));
        Sync("tab.goto6", () => GoToTab(Model.ByNumber(6)));
        Sync("tab.goto7", () => GoToTab(Model.ByNumber(7)));
        Sync("tab.goto8", () => GoToTab(Model.ByNumber(8)));
        Sync("tab.last", () => GoToTab(Model.Last));
        Sync("tab.moveLeft", () => MoveActiveTab(-1));
        Sync("tab.moveRight", () => MoveActiveTab(+1));
        Sync("tab.moveToNewWindow", MoveActiveToNewWindow);
        Async("file.reload", ReloadAsync);
        Async("file.properties", ShowPropertiesAsync);
        Sync("nav.back", GoBack);
        Sync("nav.forward", GoForward);
        Async("nav.goToPage", GoToPageAsync);
        Sync("nav.nextPage", () => _session?.NextPage());
        Sync("nav.previousPage", () => _session?.PreviousPage());
        Sync("nav.firstPage", () => _session?.FirstPage());
        Sync("nav.lastPage", () => _session?.LastPage());
        Sync("nav.scrollScreenDown", () => _session?.PageDown());
        Sync("nav.scrollScreenUp", () => _session?.PageUp());
        Sync("nav.scrollDown", () => _session?.ScrollBy(0, ArrowStepDip * (Root.XamlRoot?.RasterizationScale ?? 1.0)));
        Sync("nav.scrollUp", () => _session?.ScrollBy(0, -ArrowStepDip * (Root.XamlRoot?.RasterizationScale ?? 1.0)));
        Sync("view.zoomIn", () => _session?.ZoomStep(+1));
        Sync("view.zoomOut", () => _session?.ZoomStep(-1));
        Sync("view.zoomActual", () => _session?.ActualSize());
        Sync("view.zoomPhysical", () => _session?.PhysicalSize(RawMonitorDpi()));
        Sync("view.fitWidth", () => _session?.FitWidth());
        Sync("view.fitPage", () => _session?.FitPage());
        _actions["view.zoomTo"] = async parameter =>
        {
            if (parameter is double zoom)
            {
                _session?.ZoomTo(zoom);
            }
            else
            {
                await ZoomToAsync();
            }
        };
        Sync("view.rotateRight", () => _session?.Rotate(+1));
        Sync("view.rotateLeft", () => _session?.Rotate(-1));
        Sync("view.readingMode", () => SetFullScreen(!_fullScreen));
        Sync("view.pageColors", () => SetPageColour((PageColourMode)(((int)Pages.PageColour + 1) % Enum.GetValues<PageColourMode>().Length)));
        Sync("view.pageColorsNormal", () => SetPageColour(PageColourMode.Normal));
        Sync("view.pageColorsDark", () => SetPageColour(PageColourMode.Inverted));
        Sync("view.pageColorsSepia", () => SetPageColour(PageColourMode.Sepia));
        Sync("pane.toggleLeft", ToggleSidebar);
        Sync("pane.thumbnails", () => ShowSidebarTab(outline: false));
        Sync("pane.outline", () => ShowSidebarTab(outline: true));
        Sync("find.open", OpenFind);
        Sync("find.next", () => FindStep(forward: true));
        Sync("find.previous", () => FindStep(forward: false));
        Sync("find.close", CloseFind);
        Sync("edit.selectAll", Pages.SelectAll);
        Async("edit.copy", Pages.CopyAsync);
        Sync("app.commandPalette", TogglePalette);
        Sync("app.settings", () => ShowSettings(null));
        Sync("app.shortcuts", () => ShowSettings("shortcuts"));
        Sync("app.changeShortcut", () => ShowSettings("shortcuts"));
        Sync("app.policies", () => ShowSettings("policies"));
        Async("app.about", ShowAboutAsync);
        RegisterSettingsActions(Sync, Async);
    }

    // Reload opens the same file again in the same tab, where it was.
    private async Task ReloadAsync()
    {
        if (_active is { Session: { IsOpen: true } session } entry)
        {
            Banner.IsOpen = false;
            var view = session.CaptureView();
            entry.SetSession(null);
            entry.ShownLease?.Dispose();
            entry.ShownLease = null;
            DeactivateEntryView();
            await session.DisposeAsync();
            await LoadEntryAsync(entry, view, removeOnFailure: false);
        }
    }

    private async Task ZoomToAsync()
    {
        if (_session is not { IsOpen: true })
        {
            return;
        }

        var box = new NumberBox
        {
            Minimum = 10,
            Maximum = 6400,
            Value = Math.Round(_session.Controller.Zoom * 100),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 160,
        };
        AutomationProperties.SetAutomationId(box, "ZoomToBox");
        AutomationProperties.SetName(box, Strings.Get("ZoomTo_Box_Name"));
        var dialog = NewDialog();
        dialog.Title = Strings.Get("ZoomTo_Title");
        dialog.Content = box;
        dialog.PrimaryButtonText = Strings.Get("ZoomTo_Go");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "ZoomToDialog");
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !double.IsNaN(box.Value))
        {
            _session?.ZoomTo(Math.Clamp(box.Value, 10, 6400) / 100.0);
        }

        Pages.FocusPages();
    }

    private void SetPageColour(PageColourMode mode)
    {
        _services.Settings.Set("view.pageColour", mode.ToString());
        Pages.PageColour = mode; // also when the setting did not change (the pages and the menu are then already right)
        ApplyPageColourMenu();
    }

    private void ToggleSidebar()
    {
        if (!SidebarButton.IsEnabled)
        {
            return;
        }

        var open = SidebarButton.IsChecked != true;
        SidebarButton.IsChecked = open;
        if (open)
        {
            FocusSidebar();
        }
        else
        {
            Pages.FocusPages();
        }
    }

    private void ShowSidebarTab(bool outline)
    {
        if (!SidebarButton.IsEnabled)
        {
            return;
        }

        SidebarButton.IsChecked = true;
        (outline ? BookmarksTab : ThumbnailsTab).IsChecked = true;
        FocusSidebar();
    }

    private void FindStep(bool forward)
    {
        if (FindOpen && _session?.Find is not null)
        {
            _ = MoveToMatchAsync(forward);
        }
        else
        {
            OpenFind();
        }
    }

    // ---- dialogs ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>A dialog on this window that follows the app's theme (a dialog does not inherit the theme chosen in Settings by itself).</summary>
    private ContentDialog NewDialog() => new() { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _modalDepth++;
        _app.ModalChanged(+1);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await dialog.ShowAsync();
                }
                catch (Exception e) when (attempt < 10 && e is InvalidOperationException or COMException)
                {
                    await Task.Delay(100); // a dialog that just closed can still be going away
                }
            }
        }
        finally
        {
            _modalDepth--;
            _app.ModalChanged(-1);
        }
    }
}

/// <summary>The names of keys on the layout in use, and whether that layout has an AltGr key.</summary>
internal static partial class KeyNames
{
    private const uint MapVkToChar = 2;
    private static nint _layout;
    private static bool _hasAltGr;

    /// <summary>What the key produces unshifted on the current layout, or null.</summary>
    public static string? CharacterFor(int key)
    {
        var value = MapVirtualKeyW((uint)key, MapVkToChar);
        if (value == 0 || (value & 0x80000000) != 0)
        {
            return null; // no character, or a dead key
        }

        var character = (char)(value & 0xFFFF);
        return char.IsControl(character) ? null : char.ToUpperInvariant(character).ToString();
    }

    /// <summary>Whether the layout in use makes characters with Ctrl+Alt (AltGr). Cached per layout.</summary>
    public static bool LayoutHasAltGr()
    {
        var layout = GetKeyboardLayout(0);
        if (layout == _layout)
        {
            return _hasAltGr;
        }

        var found = false;
        for (var c = 0x20; c < 0x250 && !found; c++)
        {
            var scan = VkKeyScanExW((ushort)c, layout);
            found = scan != -1 && ((scan >> 8) & 6) == 6; // the shift state has both Ctrl and Alt
        }

        found |= VkKeyScanExW(0x20AC, layout) is var euro && euro != -1 && ((euro >> 8) & 6) == 6;
        _layout = layout;
        _hasAltGr = found;
        return found;
    }

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint thread);

    [LibraryImport("user32.dll")]
    private static partial short VkKeyScanExW(ushort character, nint layout);
}
