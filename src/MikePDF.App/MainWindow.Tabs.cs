using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Broker;
using MikePDF.Core.Errors;
using MikePDF.Core.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace MikePDF.App;

/// <summary>What the person answered when asked about unsaved edits.</summary>
internal enum CloseAnswer
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>
/// The tabs of a window (docs/features/tabs-windows-session.md): the strip in the title bar, opening a document into a tab, showing the selected tab in the page surface, closing, reordering,
/// moving a tab to another window (the session goes along and nothing reloads), and the window's part of the session file. The pure rules (where a file opens, the order, the titles) are in
/// <c>MikePDF.Core/Windowing</c>; this file is what binds them to controls.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, TabEntry> _entries = [];
    private TabEntry? _active;
    private TabEntry? _beforeSettings;
    private int _programmatic; // while the code changes the strip itself, its own change events are not the person's doing
    private SessionWindow? _restoreView;

    // ---- the strip -------------------------------------------------------------------------------------------------------------------

    private void InitializeTabs()
    {
        AutomationProperties.SetName(Tabs, Strings.Get("Tabs_Strip_Name"));
        AutomationProperties.SetName(NewTabButton, Strings.Get("Cmd_file_newTab_Name"));
        Tabs.SizeChanged += (_, _) => UpdateCaptionRegions();
        TitleDragRegion.SizeChanged += (_, _) => UpdateCaptionRegions();
        Tabs.SelectionChanged += OnTabSelectionChanged;
        Tabs.TabCloseRequested += OnTabCloseRequested;
        Tabs.TabItemsChanged += OnTabItemsChanged;
        Tabs.TabTearOutWindowRequested += OnTearOutWindowRequested;
        Tabs.TabTearOutRequested += OnTearOutRequested;
        Tabs.ExternalTornOutTabsDropping += (_, args) =>
        {
            args.AllowDrop = true;
        };
        Tabs.ExternalTornOutTabsDropped += OnExternalTabsDropped;
        LocateButton.Content = Strings.Get("Missing_Locate");
        MissingCloseButton.Content = Strings.Get("Missing_Close");
        foreach (var tab in Model.Tabs.ToList())
        {
            var entry = NewEntry(tab);
            _programmatic++;
            Tabs.TabItems.Add(entry.Item);
            _programmatic--;
        }

        if (Model.Active is { } active && _entries.TryGetValue(active.Id, out var first))
        {
            ActivateEntry(first, moveFocus: false);
        }

        RefreshTabs();
    }

    private TabEntry NewEntry(TabModel model)
    {
        var entry = new TabEntry(model) { Owner = this };
        entry.Item = CreateItem(entry);
        _entries[model.Id] = entry;
        return entry;
    }

    private TabViewItem CreateItem(TabEntry entry)
    {
        var item = new TabViewItem { Tag = entry, IsClosable = true };
        var menu = new MenuFlyout();
        foreach (var id in new[] { "file.close", "file.closeOthers", "file.closeToRight", "tab.moveToNewWindow", "file.copyPath", "file.showInFolder", "file.reload" })
        {
            var command = id;
            var line = new MenuFlyoutItem { Text = Strings.Get(CommandNameKey(command)) };
            AutomationProperties.SetAutomationId(line, "TabMenu_" + command);
            line.Click += (_, _) => _ = RunCommandAsync(command);
            menu.Items.Add(line);
        }

        // A right-click on a tab that is not shown selects it first: the commands act on the shown tab.
        menu.Opening += (_, _) =>
        {
            ActivateEntry(entry, moveFocus: false);
            var context = BuildContext();
            foreach (var line in menu.Items.OfType<MenuFlyoutItem>())
            {
                var id = ((string)AutomationProperties.GetAutomationId(line))["TabMenu_".Length..];
                line.IsEnabled = _services.Commands.Find(id)?.CanExecute(context).IsEnabled == true;
            }
        };
        item.ContextFlyout = menu;
        return item;
    }

    private static string CommandNameKey(string id) => "Cmd_" + id.Replace('.', '_') + "_Name";

    private bool TabsHaveFocus => Root.XamlRoot is not null && FocusManager.GetFocusedElement(Root.XamlRoot) is TabViewItem;

    /// <summary>The titles, tooltips, automation names and glyphs of every tab, from the model: the document's title or file name, the folder when two tabs share a title.</summary>
    private void RefreshTabs()
    {
        var titles = TabTitles.For(Model.Tabs, Strings.Get("Tab_Empty_Title"), Strings.Get("Tab_Settings_Title"), (title, folder) => Strings.Format("Tab_WithFolder", title, folder));
        for (var i = 0; i < Model.Tabs.Count; i++)
        {
            var tab = Model.Tabs[i];
            if (!_entries.TryGetValue(tab.Id, out var entry))
            {
                continue;
            }

            var name = titles[i];
            if (tab.IsDirty)
            {
                name += Strings.Get("Tab_Modified_Suffix");
            }

            if (entry.Missing)
            {
                name += Strings.Get("Tab_Missing_Suffix");
            }
            else if (entry.Opening || entry.Session?.EngineState == DocumentEngineState.Resuming)
            {
                name += Strings.Get("Tab_Loading_Suffix");
            }

            var item = entry.Item;
            item.Header = titles[i];
            ToolTipService.SetToolTip(item, tab.Path ?? titles[i]);
            AutomationProperties.SetName(item, name);
            AutomationProperties.SetHelpText(item, tab.Path ?? string.Empty);
            AutomationProperties.SetAutomationId(item, "Tab_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            item.IconSource = new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = GlyphFor(entry) };
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateCaptionRegions);
        // One tab: it is the window's drag handle, not something to tear out. Two or more: they reorder, tear out and merge.
        var many = Model.Tabs.Count > 1;
        Tabs.CanDragTabs = many;
        Tabs.CanTearOutTabs = many;
        Tabs.CanReorderTabs = many;
        RefreshCommands();
    }

    private static string GlyphFor(TabEntry entry)
    {
        if (entry.Missing || entry.PlaceholderKey is not null)
        {
            return ""; // warning
        }

        if (entry.Opening || entry.Session?.EngineState == DocumentEngineState.Resuming)
        {
            return ""; // sync: opening
        }

        return entry.Model.Kind == TabKind.Settings ? "" : "";
    }

    private void AnnounceTab(string message)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(TabAnnouncement)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(TabAnnouncement);
        peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other, Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent, message, "TabAnnouncement");
    }

    private string TitleOf(TabModel tab)
    {
        var index = Model.IndexOf(tab.Id);
        var titles = TabTitles.For(Model.Tabs, Strings.Get("Tab_Empty_Title"), Strings.Get("Tab_Settings_Title"), (title, folder) => Strings.Format("Tab_WithFolder", title, folder));
        return index >= 0 ? titles[index] : TabTitles.BaseTitle(tab, Strings.Get("Tab_Empty_Title"), Strings.Get("Tab_Settings_Title"));
    }

    /// <summary>The tabs this window has, for the palette's "Switch to tab".</summary>
    internal IEnumerable<(string Id, string Title)> TabListing() => Model.Tabs.Select(t => (t.Id.ToString("N"), TitleOf(t)));

    // ---- events of the strip ----------------------------------------------------------------------------------------------------------

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_programmatic > 0 || _closed)
        {
            return;
        }

        if (Tabs.SelectedItem is TabViewItem { Tag: TabEntry entry } && !ReferenceEquals(entry, _active))
        {
            ActivateEntry(entry, moveFocus: !TabsHaveFocus);
        }
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is TabEntry entry)
        {
            await CloseEntryAsync(entry);
        }
    }

    // The person dragged a tab to a new place in the strip: the model follows the strip.
    private void OnTabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        if (_programmatic > 0 || _closed)
        {
            return;
        }

        var order = Tabs.TabItems.OfType<TabViewItem>().Select(i => (i.Tag as TabEntry)?.Model.Id ?? Guid.Empty).ToList();
        if (order.Count == Model.Tabs.Count && order.All(id => id != Guid.Empty))
        {
            Model.Reorder(order);
            RefreshTabs();
            _app.RequestSessionWrite();
        }
    }

    // ---- tear out and merge ---------------------------------------------------------------------------------------------------------------

    private void OnTearOutWindowRequested(TabView sender, TabViewTabTearOutWindowRequestedEventArgs args)
    {
        var window = _app.CreateWindow(like: this, activate: false);
        _app.PendingTearOut = window;
        args.NewWindowId = window.AppWindow.Id;
    }

    private void OnTearOutRequested(TabView sender, TabViewTabTearOutRequestedEventArgs args)
    {
        // The id the framework hands back here is not always the one given above (it was 0 when measured): the window made a moment ago is the one.
        var target = _app.WindowOf(args.NewWindowId) ?? _app.PendingTearOut;
        _app.PendingTearOut = null;
        if (target is null)
        {
            return;
        }

        foreach (var tab in args.Tabs.OfType<TabViewItem>().ToList())
        {
            if (tab.Tag is TabEntry entry)
            {
                _app.MoveTab(this, entry, target, int.MaxValue);
            }
        }

        target.Activate();
        _app.WatchTornOutDrop(target); // the tab is in its own window now and the window follows the pointer; let go over another strip and it joins that window
    }

    private void OnExternalTabsDropped(TabView sender, TabViewExternalTornOutTabsDroppedEventArgs args)
    {
        var index = Math.Clamp(args.DropIndex, 0, Model.Tabs.Count);
        foreach (var tab in args.Tabs.OfType<TabViewItem>().ToList())
        {
            if (tab.Tag is TabEntry { Owner: { } from } entry && !ReferenceEquals(from, this))
            {
                _app.MoveTab(from, entry, this, index++);
            }
        }
    }

    // The only tab of a window is not torn out: dragging it moves the window (docs/features/tabs-windows-session.md 3.4). The tab (but not its close button) is made part of the
    // title bar for as long as it is alone, so Windows does the dragging; with two or more tabs only the empty part after them is.
    private bool _captionOverridden;

    private void UpdateCaptionRegions()
    {
        if (_closed || Root.XamlRoot is not { } root || !Tabs.IsLoaded)
        {
            return;
        }

        if (Model.Tabs.Count != 1)
        {
            if (_captionOverridden)
            {
                // Back to the ordinary title bar: only the empty part after the tabs drags (the tabs themselves must stay ordinary controls, or dragging one out does not work).
                _captionOverridden = false;
                try
                {
                    Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id).ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption);
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
                {
                    // The window is closing.
                }

                SetTitleBar(TitleDragRegion);
            }

            return;
        }

        _captionOverridden = true;
        var scale = root.RasterizationScale;
        RectInt32 Pixels(FrameworkElement element, double trimRight)
        {
            var box = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, Math.Max(0, element.ActualWidth - trimRight), element.ActualHeight));
            return new RectInt32((int)(box.X * scale), (int)(box.Y * scale), (int)(box.Width * scale), (int)(box.Height * scale));
        }

        var caption = new List<RectInt32>();
        var through = new List<RectInt32>();
        if (TitleDragRegion.ActualWidth > 0)
        {
            caption.Add(Pixels(TitleDragRegion, 0));
        }

        if (NewTabButton.IsLoaded)
        {
            through.Add(Pixels(NewTabButton, 0));
        }

        foreach (var entry in _entries.Values.Where(e => e.Item.IsLoaded && e.Item.ActualWidth > 0))
        {
            if (Model.Tabs.Count == 1 && entry.Item.ActualWidth > 56)
            {
                caption.Add(Pixels(entry.Item, 44));
                var close = Pixels(entry.Item, 0);
                through.Add(new RectInt32(close.X + close.Width - (int)(44 * scale), close.Y, (int)(44 * scale), close.Height));
            }
            else
            {
                through.Add(Pixels(entry.Item, 0));
            }
        }

        try
        {
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, [.. caption]);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // The window is closing: nothing to drag.
        }
    }

    /// <summary>The place in this window's strip under a point on the screen (physical pixels), or null when the point is not over the strip.</summary>
    internal int? StripIndexAt(int screenX, int screenY)
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var origin = AppWindow.Position;
        var size = AppWindow.Size;
        if (screenX < origin.X || screenX > origin.X + size.Width || screenY < origin.Y || screenY > origin.Y + (int)(64 * scale))
        {
            return null;
        }

        var x = (screenX - origin.X) / scale; // in device-independent pixels from the window's left edge
        var index = 0;
        foreach (var tab in Model.Tabs)
        {
            if (_entries.TryGetValue(tab.Id, out var entry) && entry.Item.IsLoaded)
            {
                var box = entry.Item.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, entry.Item.ActualWidth, entry.Item.ActualHeight));
                if (x > box.X + (box.Width / 2))
                {
                    index++;
                }
            }
        }

        return index;
    }

    /// <summary>Takes a tab out of this window (it is going to another one): its session is detached from this window's surface and goes on running.</summary>
    internal void TakeEntry(TabEntry entry)
    {
        var wasActive = ReferenceEquals(_active, entry);
        var tab = entry.Model;
        if (wasActive)
        {
            DeactivateEntry(entry);
            _active = null;
        }

        Model.Remove(tab.Id);
        _programmatic++;
        Tabs.TabItems.Remove(entry.Item);
        _programmatic--;
        _entries.Remove(tab.Id);
        entry.Owner = null;
        if (Model.Tabs.Count > 0 && wasActive && Model.Active is { } next && _entries.TryGetValue(next.Id, out var neighbour))
        {
            ActivateEntry(neighbour, moveFocus: false);
        }

        RefreshTabs();
    }

    /// <summary>Takes a tab that another window gave up. Its session is attached to this window's surface: no reload, no new engine.</summary>
    internal void AcceptEntry(TabEntry entry, int index, bool activate)
    {
        entry.Owner = this;
        _entries[entry.Model.Id] = entry;
        var at = Math.Clamp(index, 0, Model.Tabs.Count);
        Model.Insert(at, entry.Model, activate);
        _programmatic++;
        Tabs.TabItems.Insert(Math.Min(at, Tabs.TabItems.Count), entry.Item);
        _programmatic--;
        if (activate)
        {
            ActivateEntry(entry, moveFocus: true);
        }

        RefreshTabs();
        AnnounceTab(Strings.Format("Tab_MovedWindow_Announce", TitleOf(entry.Model)));
    }

    private void RemoveItemQuietly(TabViewItem item)
    {
        _programmatic++;
        Tabs.TabItems.Remove(item);
        _programmatic--;
    }

    /// <summary>A window made for a torn-out tab starts with an empty tab; when the real tab arrives that one goes.</summary>
    internal void RemovePlaceholderEmptyTab()
    {
        foreach (var tab in Model.Tabs.Where(t => t.Kind == TabKind.Empty).ToList())
        {
            if (Model.Tabs.Count > 1 && _entries.TryGetValue(tab.Id, out var entry))
            {
                Model.Remove(tab.Id);
                RemoveItemQuietly(entry.Item);
                _entries.Remove(tab.Id);
                entry.Owner = null;
            }
        }

        RefreshTabs();
    }

    internal TabEntry? TabEntryOf(Guid id) => _entries.GetValueOrDefault(id);

    internal bool IsOnlyAnEmptyTab() => Model.Tabs.Count == 1 && Model.Tabs[0].Kind == TabKind.Empty;

    /// <summary>Sizes this window like another and puts it a little down and to the right of it.</summary>
    internal void SizeAndPlaceLike(MainWindow other)
    {
        var size = other.AppWindow.Size;
        var at = other.AppWindow.Position;
        var offset = (int)(32 * Math.Max(1.0, GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0));
        AppWindow.MoveAndResize(new RectInt32(at.X + offset, at.Y + offset, size.Width, size.Height));
    }

    // ---- showing a tab ----------------------------------------------------------------------------------------------------------------

    /// <summary>Brings the window forward and shows the tab (a file that is already open was asked for).</summary>
    internal void ShowTab(Guid id)
    {
        BringToFront();
        if (_entries.TryGetValue(id, out var entry))
        {
            ActivateEntry(entry, moveFocus: true);
        }
    }

    private void DeactivateEntry(TabEntry entry)
    {
        entry.ShownLease?.Dispose();
        entry.ShownLease = null;
        if (entry.Session is { } session)
        {
            session.CancelFind();
            session.DetachPresenter();
        }

        DetachSidebar();
        _findTimer?.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = string.Empty;
        Pages.Session = null;
        _session = null;
        Busy.IsActive = false;
    }

    private void SelectInStrip(TabEntry entry)
    {
        _programmatic++;
        Tabs.SelectedItem = entry.Item;
        _programmatic--;
    }

    private void ActivateEntry(TabEntry entry, bool moveFocus)
    {
        if (_closed)
        {
            return;
        }

        if (ReferenceEquals(_active, entry))
        {
            SelectInStrip(entry);
            return;
        }

        var previous = _active;
        if (previous is not null)
        {
            DeactivateEntry(previous);
            if (previous.Model.Kind == TabKind.Settings && previous.Model.Id != entry.Model.Id)
            {
                SettingsPage.Visibility = Visibility.Collapsed;
            }
        }

        _active = entry;
        Model.Activate(entry.Model.Id);
        SelectInStrip(entry);
        Banner.IsOpen = false;
        BannerAction.Visibility = Visibility.Collapsed;
        switch (entry.Model.Kind)
        {
            case TabKind.Settings:
                Pages.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Collapsed;
                MissingPanel.Visibility = Visibility.Collapsed;
                SettingsPage.Visibility = Visibility.Visible;
                if (moveFocus)
                {
                    DispatcherQueue.TryEnqueue(SettingsPage.FocusFirst);
                }

                break;
            case TabKind.Empty:
                ShowEmptyState();
                if (moveFocus)
                {
                    EmptyOpenButton.Focus(FocusState.Programmatic);
                }

                break;
            default:
                ShowDocumentTab(entry, moveFocus);
                break;
        }

        UpdateChrome();
        RefreshTabs();
        _app.RequestSessionWrite();
    }

    private void ShowDocumentTab(TabEntry entry, bool moveFocus)
    {
        if (entry.Session is { } session)
        {
            _session = session;
            session.AttachPresenter(Pages.Presenter);
            Pages.Session = session;
            AcquireShownLease();
            if (session.IsOpen)
            {
                ShowDocument(moveFocus);

                if (session.EngineState is DocumentEngineState.Suspended or DocumentEngineState.Resuming)
                {
                    _ = ResumeAsync(entry);
                }

                if (entry.Banner is EngineBanner.Restarting or EngineBanner.Failed)
                {
                    OnEngineStateChanged(entry.Banner.Value);
                }
            }
            else
            {
                ShowEmptyState(); // still opening: the ring shows over the empty state, as it always did
                Busy.IsActive = entry.Opening;
            }

            return;
        }

        if (entry.Missing || entry.PlaceholderKey is not null)
        {
            ShowMissing(entry);
            return;
        }

        if (entry.Model.IsSuspended && !entry.Opening)
        {
            ShowEmptyState();
            _ = OpenLazyAsync(entry); // a restored or batch-opened tab opens when it is first shown
        }
        else
        {
            ShowEmptyState();
            Busy.IsActive = entry.Opening;
        }
    }

    private void AcquireShownLease()
    {
        if (_active is { Model.Kind: TabKind.Document } entry && entry.ShownLease is null && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            entry.ShownLease = entry.Leases.Show();
            entry.PinIfNeeded();
            if (entry.Session is { IsOpen: true } session && session.EngineState != DocumentEngineState.Running)
            {
                _ = ResumeAsync(entry);
            }
        }
    }

    private void ReleaseShownLease()
    {
        _active?.ShownLease?.Dispose();
        if (_active is not null)
        {
            _active.ShownLease = null;
        }
    }

    private async Task ResumeAsync(TabEntry entry)
    {
        if (entry.Session is not { } session)
        {
            return;
        }

        var status = await session.ResumeEngineAsync();
        if (status != MikePDF.Protocol.Status.Ok && ReferenceEquals(entry, _active))
        {
            ShowNotice(Strings.Get("Resume_Failed"));
        }
    }

    private void ShowMissing(TabEntry entry)
    {
        Pages.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;
        MissingName.Text = System.IO.Path.GetFileName(entry.Model.Path ?? string.Empty);
        MissingText.Text = Strings.Get(entry.PlaceholderKey ?? "Missing_Text");
        LocateButton.Visibility = entry.Missing ? Visibility.Visible : Visibility.Collapsed;
        UpdateChrome();
        DispatcherQueue.TryEnqueue(() => (entry.Missing ? LocateButton : (Control)MissingCloseButton).Focus(FocusState.Programmatic));
    }

    // ---- the session's news for the tab that is shown ---------------------------------------------------------------------------------------

    internal void OnEntryViewChanged(TabEntry entry)
    {
        if (ReferenceEquals(entry, _active))
        {
            UpdateChrome();
        }
    }

    internal void OnEntryBanner(TabEntry entry, EngineBanner state)
    {
        if (ReferenceEquals(entry, _active))
        {
            OnEngineStateChanged(state);
        }

        RefreshTabs();
    }

    internal void OnEntryPageIncomplete(TabEntry entry, int page)
    {
        if (ReferenceEquals(entry, _active))
        {
            OnPageIncomplete(page);
        }
    }

    internal void OnEntryOverlays(TabEntry entry)
    {
        if (ReferenceEquals(entry, _active))
        {
            OnOverlaysChanged();
        }
    }

    internal void OnEntryEngineLife(TabEntry entry, DocumentEngineState state)
    {
        RefreshTabs();
        if (!ReferenceEquals(entry, _active))
        {
            return;
        }

        Busy.IsActive = entry.Opening || state == DocumentEngineState.Resuming;
        if (state == DocumentEngineState.Resuming)
        {
            AnnounceTab(Strings.Format("Suspended_Resuming", TitleOf(entry.Model)));
        }
    }

    // ---- opening a document into a tab ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a document in this window (decided by <see cref="AppHost.OpenAsync"/>): in the active empty tab, or in a new tab at <paramref name="index"/>. <paramref name="lazy"/> makes the tab
    /// without opening the document; it opens when the tab is first shown. A new tab whose document cannot be opened is removed again, after the error.
    /// </summary>
    internal async Task OpenDocumentAsync(string path, DocumentIdentity identity, int index, bool reuseEmpty, bool lazy, TabViewState? view)
    {
        await _loaded.Task; // dialogs need the window's XamlRoot
        if (_closed)
        {
            return;
        }

        TabEntry entry;
        var fresh = true;
        if (reuseEmpty && Model.Active is { Kind: TabKind.Empty } empty && _entries.TryGetValue(empty.Id, out var existing))
        {
            entry = existing;
            fresh = false;
            entry.Model.Kind = TabKind.Document;
        }
        else
        {
            if (Model.IsFull)
            {
                return;
            }

            entry = NewEntry(new TabModel(TabKind.Document));
            Model.Insert(index, entry.Model, activate: false);
            _programmatic++;
            Tabs.TabItems.Insert(Math.Min(Math.Max(index, 0), Tabs.TabItems.Count), entry.Item);
            _programmatic--;
        }

        entry.Model.Path = path;
        entry.Model.Identity = identity;
        entry.Model.View = view ?? TabViewState.Start;
        entry.Model.IsSuspended = lazy;
        if (lazy)
        {
            RefreshTabs();
            _app.RequestSessionWrite();
            return;
        }

        if (!ReferenceEquals(entry, _active) || entry.Model.Id != Model.ActiveId)
        {
            ActivateEntry(entry, moveFocus: false);
        }

        await LoadEntryAsync(entry, view, removeOnFailure: fresh);
    }

    private Task OpenLazyAsync(TabEntry entry)
    {
        if (_app.Model.OpenDocumentCount >= AppModel.MaxOpenDocuments)
        {
            entry.PlaceholderKey = "Missing_TooMany";
            ShowMissing(entry);
            RefreshTabs();
            return Task.CompletedTask;
        }

        return LoadEntryAsync(entry, entry.Model.View, removeOnFailure: false);
    }

    /// <summary>Opens the entry's document in a new session (the password asked for as often as the rules allow). Shows its own errors.</summary>
    private async Task LoadEntryAsync(TabEntry entry, TabViewState? view, bool removeOnFailure)
    {
        var path = entry.Model.Path!;
        var restored = entry.Model.IsSuspended;
        entry.Model.IsSuspended = false;
        entry.Opening = true;
        entry.Missing = false;
        entry.PlaceholderKey = null;
        RefreshTabs();
        if (ReferenceEquals(entry, _active))
        {
            Busy.IsActive = true;
            Banner.IsOpen = false;
        }

        try
        {
            EngineHost host;
            try
            {
                host = await _app.Host;
            }
            catch (Exception e) when (e is EngineStartException or InvalidOperationException)
            {
                await FailEntryAsync(entry, DocumentErrorKind.SandboxUnavailable, path, e.Message, removeOnFailure, restored);
                return;
            }

            if (entry.Owner is null)
            {
                return; // the window was closed while the host started
            }

            var session = new DocumentSession(host, _store, Pages.Presenter, DispatcherQueue);
            entry.SetSession(session);
            if (ReferenceEquals(entry, _active))
            {
                _session = session;
                Pages.Session = session;
            }
            else
            {
                session.DetachPresenter();
            }

            var attempts = new PasswordAttempts();
            string? password = null;
            while (true)
            {
                var outcome = await session.OpenAsync(path, password, view);
                if (outcome.Kind == OpenOutcomeKind.Opened)
                {
                    entry.Model.DocumentTitle = session.Title;
                    entry.Opening = false;
                    if (!restored && _services.Settings.Value<bool>("privacy.addToWindowsRecent"))
                    {
                        JumpListService.AddToRecent(path);
                    }

                    entry.PinIfNeeded();
                    if (FakeDirty)
                    {
                        entry.Model.IsDirty = true;
                    }

                    if (ReferenceEquals(entry, _active))
                    {
                        _session = session;
                        ShowDocument(!TabsHaveFocus);
                        AcquireShownLease();
                        ApplyRestoredSidebar();
                    }

                    RefreshTabs();
                    if (!restored || !string.Equals(path, entry.Model.Path, StringComparison.Ordinal))
                    {
                        _app.RequestSessionWrite();
                    }

                    return;
                }

                if (outcome.Kind is OpenOutcomeKind.PasswordRequired or OpenOutcomeKind.PasswordIncorrect)
                {
                    if (attempts.Exhausted)
                    {
                        await FailEntryAsync(entry, DocumentErrorKind.PasswordIncorrect, path, null, removeOnFailure, restored, tooMany: true);
                        return;
                    }

                    var entered = await PromptPasswordAsync(path, wrong: outcome.Kind == OpenOutcomeKind.PasswordIncorrect, attempts.Remaining);
                    if (entered is null)
                    {
                        await FailEntryAsync(entry, DocumentErrorKind.None, path, null, removeOnFailure, restored); // cancelled: no message
                        return;
                    }

                    attempts.Record();
                    password = entered;
                    continue;
                }

                if (outcome.Error == DocumentErrorKind.FileNotFound)
                {
                    _store.RemoveRecent(path);
                    RefreshRecent();
                }

                await FailEntryAsync(entry, outcome.Error, path, outcome.Detail, removeOnFailure, restored);
                return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever went wrong, the person gets a plain message and the window stays usable.
            await FailEntryAsync(entry, DocumentErrorKind.Unknown, path, e.Message, removeOnFailure, restored);
        }
        finally
        {
            entry.Opening = false;
            if (ReferenceEquals(entry, _active))
            {
                Busy.IsActive = false;
            }

            RefreshTabs();
        }
    }

    private static bool FakeDirty => Environment.GetEnvironmentVariable("MIKEPDF_TEST_FAKE_DIRTY") == "1";

    private async Task FailEntryAsync(TabEntry entry, DocumentErrorKind kind, string path, string? detail, bool removeOnFailure, bool restored, bool tooMany = false)
    {
        var session = entry.Session;
        entry.SetSession(null);
        if (ReferenceEquals(entry, _active))
        {
            Pages.Session = null;
            _session = null;
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }

        // A restored tab keeps its place: it shows "can't be found" with Locate file and Close tab. Nothing is silently dropped.
        if (restored && kind == DocumentErrorKind.FileNotFound && !entry.Model.IsDirty)
        {
            entry.Missing = true;
            if (ReferenceEquals(entry, _active))
            {
                ShowMissing(entry);
            }

            return;
        }

        if (removeOnFailure && entry.Owner is not null)
        {
            await CloseEntryAsync(entry, ask: false, remember: false);
        }
        else
        {
            // The tab that was empty goes back to being empty.
            entry.Model.Kind = TabKind.Empty;
            entry.Model.Path = null;
            entry.Model.Identity = default;
            entry.Model.DocumentTitle = null;
            if (ReferenceEquals(entry, _active))
            {
                ShowEmptyState();
            }
        }

        if (kind != DocumentErrorKind.None)
        {
            await ShowErrorAsync(kind, path, detail, tooMany);
        }
    }

    // ---- closing -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Closes a tab. Asks first when it has unsaved edits. The tab to its right (else to its left) is shown; the last tab is replaced by an empty one, so the window stays. The document is
    /// remembered for Ctrl+Shift+T. False when the person cancelled.
    /// </summary>
    internal async Task<bool> CloseEntryAsync(TabEntry entry, bool ask = true, bool remember = true)
    {
        if (entry.Owner is null)
        {
            return false;
        }

        if (ask && ClosePlan.Dirty([entry.Model]) is { Count: > 0 } dirty && await ConfirmCloseAsync(dirty) == CloseAnswer.Cancel)
        {
            return false;
        }

        var tab = entry.Model;
        var title = TitleOf(tab);
        var wasActive = ReferenceEquals(entry, _active);
        var focusWasInStrip = TabsHaveFocus;
        if (remember && tab.HasDocument)
        {
            tab.View = entry.Session is { IsOpen: true } open ? open.CaptureView() : tab.View;
            _app.Model.Closed.Push(new ClosedTab(Model.Id, tab.Path!, tab.View));
        }

        Model.Remove(tab.Id);
        if (tab.Kind == TabKind.Settings && wasActive && _beforeSettings is { } before && before.Model.Id != tab.Id && Model.IndexOf(before.Model.Id) >= 0)
        {
            Model.Activate(before.Model.Id); // closing Settings goes back to where it was opened from
        }

        _programmatic++;
        Tabs.TabItems.Remove(entry.Item);
        _programmatic--;
        _entries.Remove(tab.Id);
        TabEntry? added = null;
        if (Model.Tabs.Count == 0)
        {
            var empty = Model.EnsureNotEmpty()!;
            added = NewEntry(empty);
            _programmatic++;
            Tabs.TabItems.Add(added.Item);
            _programmatic--;
        }

        var session = entry.Session;
        if (wasActive)
        {
            if (!_entries.TryGetValue(Model.ActiveId, out var next))
            {
                next = added!;
            }

            ActivateEntry(next, moveFocus: !focusWasInStrip);
            if (focusWasInStrip)
            {
                next.Item.Focus(FocusState.Programmatic);
            }
        }

        entry.ShownLease?.Dispose();
        entry.ShownLease = null;
        entry.SetSession(null);
        entry.Owner = null;
        RefreshTabs();
        _app.RequestSessionWrite();
        var now = Model.Active is { } shown ? TitleOf(shown) : string.Empty;
        AnnounceTab(added is not null ? Strings.Format("Tab_ClosedLast_Announce", title) : Strings.Format("Tab_Closed_Announce", title, now));
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        return true;
    }

    private Task<bool> CloseTabsAsync(IEnumerable<TabModel> tabs) => CloseTabsCoreAsync([.. tabs]);

    private async Task<bool> CloseTabsCoreAsync(List<TabModel> tabs)
    {
        if (ClosePlan.Dirty(tabs) is { Count: > 0 } dirty && await ConfirmCloseAsync(dirty) == CloseAnswer.Cancel)
        {
            return false;
        }

        foreach (var tab in tabs)
        {
            if (_entries.TryGetValue(tab.Id, out var entry))
            {
                await CloseEntryAsync(entry, ask: false);
            }
        }

        return true;
    }

    /// <summary>Save, Don't save, Cancel: one dialog for one tab, or one for several, listing the documents (docs/features/tabs-windows-session.md 3.5).</summary>
    private async Task<CloseAnswer> ConfirmCloseAsync(IReadOnlyList<TabModel> dirty)
    {
        await _loaded.Task;
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Close_Dirty_Title");
        var panel = new StackPanel { Spacing = 8 };
        if (dirty.Count == 1)
        {
            panel.Children.Add(new TextBlock { Text = Strings.Format("Close_Dirty_One", TitleOf(dirty[0])), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = Strings.Get("Close_Dirty_Many"), TextWrapping = TextWrapping.Wrap });
            foreach (var tab in dirty)
            {
                var line = new TextBlock { Text = TitleOf(tab), TextTrimming = TextTrimming.CharacterEllipsis };
                AutomationProperties.SetAutomationId(line, "CloseDirtyItem");
                panel.Children.Add(line);
            }
        }

        dialog.Content = panel;
        dialog.PrimaryButtonText = Strings.Get("Close_Dirty_Save");
        dialog.IsPrimaryButtonEnabled = AppHost.SaveHandler is not null; // saving is the save pipeline's (M4); until it exists, edits can only be kept (Cancel) or dropped
        dialog.SecondaryButtonText = Strings.Get("Close_Dirty_Discard");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "CloseDirtyDialog");
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary && AppHost.SaveHandler is { } save)
        {
            return await save(dirty) ? CloseAnswer.Save : CloseAnswer.Cancel;
        }

        return result == ContentDialogResult.Secondary ? CloseAnswer.DontSave : CloseAnswer.Cancel;
    }

    // ---- what the commands do ----------------------------------------------------------------------------------------------------------------------

    private void NewTab()
    {
        if (Model.IsFull)
        {
            _app.CreateWindow(like: this);
            return;
        }

        var entry = NewEntry(new TabModel(TabKind.Empty));
        var index = Model.ActiveIndex + 1;
        Model.Insert(index, entry.Model, activate: false);
        _programmatic++;
        Tabs.TabItems.Insert(Math.Min(index, Tabs.TabItems.Count), entry.Item);
        _programmatic--;
        ActivateEntry(entry, moveFocus: true);
        RefreshTabs();
    }

    private Task CloseActiveTabAsync()
    {
        if (_active is null || (Model.Tabs.Count == 1 && Model.Tabs[0].Kind == TabKind.Empty))
        {
            return Task.CompletedTask; // the last empty tab stays: Ctrl+W twice cannot quit the app
        }

        return CloseEntryAsync(_active);
    }

    private Task CloseOthersAsync() => _active is null ? Task.CompletedTask : CloseTabsAsync(Model.Others(_active.Model.Id));

    private Task CloseToTheRightAsync() => _active is null ? Task.CompletedTask : CloseTabsAsync(Model.ToTheRightOf(_active.Model.Id));

    private void GoToTab(TabModel? tab)
    {
        if (tab is not null && _entries.TryGetValue(tab.Id, out var entry))
        {
            ActivateEntry(entry, moveFocus: !TabsHaveFocus);
        }
    }

    private void MoveActiveTab(int delta)
    {
        if (_active is null)
        {
            return;
        }

        var from = Model.ActiveIndex;
        var to = from + delta;
        if (to < 0 || to >= Model.Tabs.Count || !Model.Move(_active.Model.Id, to))
        {
            return;
        }

        _programmatic++;
        Tabs.TabItems.RemoveAt(from);
        Tabs.TabItems.Insert(to, _active.Item);
        _programmatic--;
        SelectInStrip(_active);
        RefreshTabs();
        _app.RequestSessionWrite();
        AnnounceTab(Strings.Format("Tab_Moved_Announce", TitleOf(_active.Model), to + 1, Model.Tabs.Count));
    }

    private void MoveActiveToNewWindow()
    {
        if (_active is not null && Model.Tabs.Count > 1)
        {
            var entry = _active;
            var title = TitleOf(entry.Model);
            _app.MoveTab(this, entry, null, 0);
            AnnounceTab(Strings.Format("Tab_MovedWindow_Announce", title));
        }
    }

    private void CopyPath()
    {
        if (_session is { } session && !string.IsNullOrEmpty(session.Path))
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
            package.SetText(session.Path);
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program holds the clipboard; the person can try again.
            }
        }
    }

    private async Task ShowInFolderAsync()
    {
        if (_session is not { } session || string.IsNullOrEmpty(session.Path))
        {
            return;
        }

        try
        {
            var options = new Windows.System.FolderLauncherOptions();
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(session.Path);
            options.ItemsToSelect.Add(file);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(session.Path));
            await Windows.System.Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ShowNotice(Strings.Get("ShowInFolder_Failed"));
        }
    }

    // A restored tab whose file went: the person points at where it is now (the same document, a new place).
    private async Task LocateAsync()
    {
        if (_active is not { Missing: true } entry)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.List, SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null || ActivationPathValidator.Validate(file.Path) is not { IsOk: true } check)
        {
            return;
        }

        entry.Model.Path = check.FullPath;
        entry.Model.Identity = DocumentIdentity.Of(check.FullPath);
        entry.Model.IsSuspended = true;
        entry.Missing = false;
        MissingPanel.Visibility = Visibility.Collapsed;
        await OpenLazyAsync(entry);
    }

    // ---- Settings is a tab ------------------------------------------------------------------------------------------------------------------------------

    private void ShowSettingsTab(string? section)
    {
        Palette.Close();
        var tab = Model.FirstOfKind(TabKind.Settings);
        TabEntry entry;
        if (tab is not null && _entries.TryGetValue(tab.Id, out var existing))
        {
            entry = existing;
        }
        else
        {
            if (Model.IsFull)
            {
                return;
            }

            _beforeSettings = _active;
            entry = NewEntry(new TabModel(TabKind.Settings));
            var index = Model.Tabs.Count;
            Model.Insert(index, entry.Model, activate: false);
            _programmatic++;
            Tabs.TabItems.Add(entry.Item);
            _programmatic--;
        }

        if (!ReferenceEquals(_active, entry) && _active is { Model.Kind: not TabKind.Settings })
        {
            _beforeSettings = _active;
        }

        ActivateEntry(entry, moveFocus: true);
        SettingsPage.Show(section);
        RefreshCommands();
    }

    private void CloseSettingsTab()
    {
        if (Model.FirstOfKind(TabKind.Settings) is { } tab && _entries.TryGetValue(tab.Id, out var entry))
        {
            _ = CloseEntryAsync(entry, ask: false, remember: false);
        }
    }

    /// <summary>"Restore your last session?" after MikePDF closed unexpectedly (startup.restore = Ask).</summary>
    internal async Task<bool> AskRestoreAsync(int documents)
    {
        await _loaded.Task;
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Restore_Title");
        dialog.Content = new TextBlock { Text = Strings.Format(documents == 1 ? "Restore_Body_One" : "Restore_Body_Other", documents), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Restore_Yes");
        dialog.CloseButtonText = Strings.Get("Restore_No");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "RestoreDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    // The shown tab's session is going away (Reload): the page surface and the panes let go of it first.
    private void DeactivateEntryView()
    {
        DetachSidebar();
        _findTimer?.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = string.Empty;
        Pages.Session = null;
        _session = null;
    }

    // ---- the session file ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>This window as the session file keeps it.</summary>
    internal SessionWindow Capture()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var (bounds, maximized) = NativeWindowing.CaptureBounds(AppWindow, hwnd);
        var tabs = new List<SessionTab>();
        foreach (var tab in Model.Tabs.Where(t => t.Kind != TabKind.Empty))
        {
            if (_entries.TryGetValue(tab.Id, out var entry) && entry.Session is { IsOpen: true } open)
            {
                tab.View = open.CaptureView();
            }

            var fileId = tab.Identity.Key.StartsWith("id:", StringComparison.Ordinal) ? tab.Identity.Key[3..] : null;
            tabs.Add(new SessionTab(tab.Kind, tab.Path, fileId, tab.DocumentTitle, tab.View, tab.IsSuspended));
        }

        var active = Model.Active is { Kind: not TabKind.Empty } current ? Model.Tabs.Where(t => t.Kind != TabKind.Empty).ToList().IndexOf(current) : 0;
        return new SessionWindow(bounds, maximized, Math.Max(0, active), Sidebar.IsPaneOpen, OutlineShown ? "outline" : "thumbnails", tabs);
    }

    /// <summary>Makes this window what the session says: its place and size, its tabs (none opened yet), and shows the active one, which opens its document.</summary>
    internal void RestoreFrom(SessionWindow saved, IReadOnlyList<MonitorInfo> monitors)
    {
        var place = WindowPlacement.Resolve(saved.Bounds, monitors);
        AppWindow.MoveAndResize(new RectInt32(place.X, place.Y, place.Width, place.Height));
        if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        foreach (var old in _entries.Values.ToList())
        {
            Model.Remove(old.Model.Id);
            RemoveItemQuietly(old.Item);
            _entries.Remove(old.Model.Id);
        }

        _active = null;
        foreach (var stored in saved.Tabs)
        {
            var identity = stored.FileId is { Length: > 0 } id ? new DocumentIdentity("id:" + id) : stored.Path is { Length: > 0 } path ? DocumentIdentity.Of(path) : default;
            var model = new TabModel(stored.Kind, stored.Path, identity) { DocumentTitle = stored.Title, View = stored.View, IsSuspended = stored.Kind == TabKind.Document };
            if (!Model.Insert(Model.Tabs.Count, model, activate: false))
            {
                break;
            }

            var entry = NewEntry(model);
            _programmatic++;
            Tabs.TabItems.Add(entry.Item);
            _programmatic--;
        }

        if (Model.Tabs.Count == 0)
        {
            var empty = Model.EnsureNotEmpty()!;
            var entry = NewEntry(empty);
            _programmatic++;
            Tabs.TabItems.Add(entry.Item);
            _programmatic--;
        }

        _restoreView = saved;
        var activeModel = Model.Tabs[Math.Clamp(saved.ActiveTab, 0, Model.Tabs.Count - 1)];
        Model.Activate(activeModel.Id);
        RefreshTabs();
        ActivateEntry(_entries[activeModel.Id], moveFocus: false);
    }

    // The sidebar of a restored window opens once its first document has: the toggle is enabled by then.
    private void ApplyRestoredSidebar()
    {
        if (_restoreView is not { } saved)
        {
            return;
        }

        _restoreView = null;
        if (saved.SidebarOpen && SidebarButton.IsEnabled)
        {
            (saved.SidebarPane == "outline" ? BookmarksTab : ThumbnailsTab).IsChecked = true;
            SidebarButton.IsChecked = true;
        }
    }
}
