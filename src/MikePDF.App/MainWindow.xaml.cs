using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core.Errors;
using MikePDF.Core.Persistence;
using MikePDF.Core.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

using WinRT.Interop;

namespace MikePDF.App;

/// <summary>An entry of the recent files list.</summary>
public sealed record RecentItem(string Name, string Folder, string Path, string AccessibleName);

/// <summary>
/// One window: tabs in the title bar, the command bar, the empty state with recent files, and the page surface, which shows the document of the selected tab. A window holds any number of
/// tabs (docs/features/tabs-windows-session.md); each tab is a <see cref="DocumentSession"/> that can move to another window without reloading. The rules for where a file opens, and what
/// the windows share (engines, the session file, activation), are in <see cref="AppHost"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppHost _app;
    private readonly DocumentStore _store;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherQueueTimer _bannerTimer;
    private DocumentSession? _session; // the session of the tab that is shown (null for an empty, Settings or placeholder tab)
    private bool _closed;
    private bool _closeConfirmed;

    internal MainWindow(AppHost app, WindowModel model)
    {
        _app = app;
        Model = model;
        _services = app.Services;
        _store = app.Services.Documents;
        InitializeComponent();
        Pages.FlowDirection = FlowDirection.LeftToRight; // a document is not mirrored with the window: its pages lie as its binding says
        if (TestSeams.Get(MikePDF.Core.TestSeamGate.PseudoRtl) == "1")
        {
            Root.FlowDirection = FlowDirection.RightToLeft; // a pseudo-RTL build for the journeys (PV-23)
        }

        Pages.LinkActivated += OnLinkActivated;
        Pages.Copied += OnCopied;
        SystemBackdrop = new MicaBackdrop();
        ConfigureTitleBar();
        _bannerTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _bannerTimer.IsRepeating = false;
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.IsOpen = false;
        };
        Closed += OnClosed;
        Activated += OnActivated;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        InitializeReaderGaps();
        InitializeCommands();
        InitializeViews();
        InitializeTabs();
        RefreshRecent();
        UpdateChrome();
        Title = Strings.Get("AppName");
        NativeWindowing.WatchEndSession(WindowNative.GetWindowHandle(this), _app.OnEndSession);
    }

    /// <summary>The tabs of this window (plain data).</summary>
    internal WindowModel Model { get; }

    /// <summary>The open document, for tests and diagnostics.</summary>
    internal DocumentSession? Session => _session;

    // ---- window ------------------------------------------------------------------------------------------------------------------

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RightInsetColumn.Width = new GridLength(bar.RightInset / Math.Max(1.0, Root.XamlRoot?.RasterizationScale ?? 1.0));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        var scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(820 * scale)));
        Root.Loaded += (_, _) => _loaded.TrySetResult();
        Root.Loaded += (_, _) => AppTrace.WindowLoaded(DispatcherQueue);
        Root.Loaded += (_, _) => RightInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / Math.Max(1.0, (Root.XamlRoot?.RasterizationScale ?? 1.0)));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        SyncFullScreen(args);
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            // Minimised: keep only what is on screen (master prompt 7.1 rule 5), and let the engine of the tab go: nothing is shown any more.
            _session?.TrimMemory();
            ReleaseShownLease();
        }
        else if (args.DidPresenterChange)
        {
            AcquireShownLease();
        }

        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            _app.RequestSessionWrite();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _app.OnWindowActivated(this);
        }
    }

    // Closing the window asks first when a tab has unsaved edits (one dialog for all of them); otherwise it just closes.
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (PrintJobRunning)
        {
            // A job started here stops with the window: ask first, stop it if the person says so, then close the way a window without a job closes (unsaved edits are asked about next).
            args.Cancel = true;
            if (await ConfirmClosingWhilePrintingAsync())
            {
                Close();
            }

            return;
        }

        var dirty = ClosePlan.Dirty(Model.Tabs);
        if (dirty.Count == 0)
        {
            _app.OnWindowClosing(this);
            return;
        }

        args.Cancel = true;
        if (await ConfirmCloseAsync(dirty) != CloseAnswer.Cancel)
        {
            _closeConfirmed = true;
            _app.OnWindowClosing(this);
            Close();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _policyTimer?.Stop();
        KeepAwake(false);
        _services.Settings.Flush(); // a change in the last half second is written now, not lost
        var entries = _entries.Values.ToList();
        _app.OnWindowClosed(this);
        foreach (var entry in entries)
        {
            entry.Owner = null;
            entry.ShownLease?.Dispose();
            entry.ShownLease = null;
            if (entry.Session is { } session)
            {
                entry.SetSession(null);
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>Brings the window forward (a second launch redirected here, an existing tab was asked for).</summary>
    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>A short message in the banner (a refused file, a path that cannot be opened).</summary>
    internal void ShowNotice(string message)
    {
        ShowBannerText(InfoBarSeverity.Informational, message, string.Empty);
        _bannerTimer.Interval = TimeSpan.FromSeconds(6);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    // ---- opening -----------------------------------------------------------------------------------------------------------------

    /// <summary>Opens a document the way a person asked for it: where the rules say, replacing nothing. Shows its own errors.</summary>
    internal Task OpenPathAsync(string path) => _app.OpenAsync(path, OpenSource.User, this);

    /// <summary>More than ten files at once: ask first (decisions.md Q12).</summary>
    internal async Task<bool> ConfirmOpenManyAsync(int count)
    {
        await _loaded.Task;
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Open_Many_Title");
        dialog.Content = new TextBlock { Text = Strings.Format(count == 1 ? "Open_Many_Body_One" : "Open_Many_Body_Other", count), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Open_Many_Open");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(dialog, "OpenManyDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private void ShowDocument(bool focus = true)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        Pages.Visibility = Visibility.Visible;
        RefreshRecent();
        UpdateChrome();
        if (_session is { } opened)
        {
            AttachSidebar(opened);
        }

        if (focus)
        {
            Pages.FocusPages();
        }
    }

    private void ShowEmptyState()
    {
        Pages.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        RefreshRecent();
        UpdateChrome();
    }

    private void RefreshRecent()
    {
        var items = _store.Recent
            .Select(r => new RecentItem(
                System.IO.Path.GetFileName(r.Path),
                System.IO.Path.GetDirectoryName(r.Path) ?? string.Empty,
                r.Path,
                Strings.Format("Recent_Automation", System.IO.Path.GetFileName(r.Path), System.IO.Path.GetDirectoryName(r.Path) ?? string.Empty)))
            .ToList();
        RecentList.ItemsSource = items;
        _hasRecent = items.Count > 0;
        RecentList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoRecentText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- chrome ------------------------------------------------------------------------------

    private void UpdateChrome()
    {
        var session = _session;
        var open = session is { IsOpen: true };
        RefreshCommands();
        if (!open)
        {
            PageIndicator.Content = Strings.Get("PageIndicator_None");
            ((AppBarButton)ZoomButton).Label = Strings.Get("Zoom_None");
            Title = Strings.Get("AppName");
            AutomationProperties.SetName(PageIndicator, Strings.Get("PageIndicator_None"));
            SyncViewMenu();
            return;
        }

        var controller = session!.Controller;
        var page = Math.Max(0, controller.CurrentPage) + 1;
        var total = controller.PageCount;
        var (firstPage, lastPage) = controller.CurrentPages;
        if (lastPage > firstPage)
        {
            // A spread: both of its pages ("4-5 / 40"; Narrator hears "Pages 4 and 5 of 40").
            PageIndicator.Content = Strings.Format("PageIndicator_Spread", firstPage + 1, lastPage + 1, total);
            AutomationProperties.SetName(PageIndicator, Strings.Format("PageIndicator_SpreadAutomation", firstPage + 1, lastPage + 1, total));
        }
        else
        {
            PageIndicator.Content = Strings.Format("PageIndicator_Text", page, total);
            AutomationProperties.SetName(PageIndicator, Strings.Format("PageIndicator_Automation", page, total));
        }
        var percent = Math.Round(controller.Zoom * 100).ToString("0", CultureInfo.CurrentCulture);
        ((AppBarButton)ZoomButton).Label = percent + "%";
        AutomationProperties.SetName(ZoomButton, Strings.Format("Zoom_Automation", percent));
        AutomationProperties.SetItemStatus(Pages, controller.ViewTurns == 0 ? Strings.Get("Surface_NotRotated") : Strings.Format("Surface_Rotated", controller.ViewTurns * 90));
        var title = Strings.Format("Window_Title", session.Title);
        Title = title;
        SyncViewChrome(session);
    }

    // ---- banner ------------------------------------------------------------------------------

    private void OnEngineStateChanged(EngineBanner state)
    {
        _bannerTimer.Stop();
        BannerAction.Visibility = Visibility.Collapsed;
        switch (state)
        {
            case EngineBanner.Restarting:
                ShowBanner(InfoBarSeverity.Warning, "Banner_EngineRestarting_Title", "Banner_EngineRestarting_Message", closable: false);
                break;
            case EngineBanner.Restored:
                ShowBanner(InfoBarSeverity.Success, "Banner_EngineRestored_Title", "Banner_EngineRestored_Message", closable: true);
                _bannerTimer.Interval = TimeSpan.FromSeconds(4);
                _bannerTimer.Start();
                break;
            default:
                ShowBanner(InfoBarSeverity.Error, "Banner_EngineFailed_Title", "Banner_EngineFailed_Message", closable: true);
                BannerAction.Content = Strings.Get("Banner_Reopen");
                BannerAction.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnPageIncomplete(int page)
    {
        ShowBanner(InfoBarSeverity.Informational, "Banner_PartialPage_Title", null, closable: true, Strings.Format("Banner_PartialPage_Message", page + 1));
        _bannerTimer.Interval = TimeSpan.FromSeconds(6);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void ShowBanner(InfoBarSeverity severity, string titleKey, string? messageKey, bool closable, string? messageText = null)
    {
        _retrySettings = false;
        Banner.Severity = severity;
        Banner.Title = Strings.Get(titleKey);
        Banner.Message = messageText ?? (messageKey is null ? string.Empty : Strings.Get(messageKey));
        Banner.IsClosable = closable;
        Banner.IsOpen = true;
    }

    // The banner's one button: "Open again" after the viewer failed, or "Try again" after settings could not be saved.
    private async void OnBannerActionClick(object sender, RoutedEventArgs e)
    {
        Banner.IsOpen = false;
        if (_retrySettings)
        {
            _retrySettings = false;
            _services.Settings.Flush();
            return;
        }

        await RunCommandAsync("file.reload");
    }

    // ---- dialogs -----------------------------------------------------------------------------

    private async Task<string?> PromptPasswordAsync(string path, bool wrong, int remaining)
    {
        var box = new PasswordBox { MinWidth = 280 };
        AutomationProperties.SetAutomationId(box, "PasswordBox");
        AutomationProperties.SetName(box, Strings.Get("Password_Box_Name"));
        var message = new TextBlock
        {
            Text = wrong ? Strings.Format("Password_Wrong", remaining) : Strings.Format("Password_Prompt", System.IO.Path.GetFileName(path)),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(message, "PasswordMessage");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(message);
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = Strings.Get("Password_Title"),
            Content = panel,
            PrimaryButtonText = Strings.Get("Password_Open"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "PasswordDialog");
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary ? box.Password : null;
    }

    private async Task ShowErrorAsync(DocumentErrorKind kind, string path, string? detail, bool tooMany = false)
    {
        var info = DocumentErrorInfo.For(kind);
        var name = System.IO.Path.GetFileName(path);
        var text = Strings.Format(tooMany ? "Error_PasswordTooMany" : info.ResourceKey, name);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = Strings.Get("Error_Title"),
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Strings.Get("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ErrorDialog");
        _ = detail; // technical detail is not shown to people; it stays out of the UI (no logging yet: no telemetry)
        await ShowDialogAsync(dialog);
    }

    private async Task GoToPageAsync()
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        var total = session.Controller.PageCount;
        var box = new NumberBox
        {
            Minimum = 1,
            Maximum = total,
            Value = Math.Max(0, session.Controller.CurrentPage) + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 160,
        };
        AutomationProperties.SetAutomationId(box, "GoToPageBox");
        AutomationProperties.SetName(box, Strings.Format("GoTo_Box_Name", total));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = Strings.Get("GoTo_Title"),
            Content = box,
            PrimaryButtonText = Strings.Get("GoTo_Go"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "GoToPageDialog");
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !double.IsNaN(box.Value) && _session is { IsOpen: true } current)
        {
            current.JumpToPage((int)Math.Clamp(box.Value, 1, total) - 1);
        }

        Pages.FocusPages();
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenPathAsync(file.Path);
        }
    }

    // ---- recent files --------------------------------------------------------------------------------------------------------

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item)
        {
            await OpenPathAsync(item.Path);
        }
    }

    // ---- drag and drop -----------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Strings.Get("Drop_Caption");
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().Where(f => !string.IsNullOrEmpty(f.Path)).ToList();
            var pdfs = files.Where(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToList();
            if (pdfs.Count == 0 && files.Count > 0)
            {
                pdfs = [files[0].Path]; // not named .pdf: the engine decides by content
            }

            if (pdfs.Count == 1)
            {
                await OpenPathAsync(pdfs[0]);
            }
            else if (pdfs.Count > 1 && (!ActivationLimits.NeedsConfirmation(pdfs.Count) || await ConfirmOpenManyAsync(pdfs.Count)))
            {
                await _app.OpenManyAsync(pdfs.Take(ActivationLimits.Maximum).ToList(), OpenSource.User, this);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
