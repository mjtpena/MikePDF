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
using MikePDF.Broker;
using MikePDF.Core.Errors;
using MikePDF.Core.Persistence;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

using WinRT.Interop;

namespace MikePDF.App;

/// <summary>An entry of the recent files list.</summary>
public sealed record RecentItem(string Name, string Folder, string Path, string AccessibleName);

/// <summary>
/// The window: title bar, command bar, the empty state with recent files, and the page surface. It opens one document at a time; opening
/// another (from the picker, a drop, the command line, or a second MikePDF launch) replaces the open one. Tabs and more windows are later work.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DocumentStore _store;
    private readonly Task<EngineHost> _host;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _openChain = Task.CompletedTask;
    private readonly DispatcherQueueTimer _bannerTimer;
    private DocumentSession? _session;
    private bool _closed;

    internal MainWindow(AppServices services)
    {
        _services = services;
        _store = services.Documents;
        // Creating the sandbox profile and putting the verified engine in place takes a moment; do it while the window comes up.
        _host = Task.Run(EngineHost.Create);
        InitializeComponent();
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
        AppWindow.Changed += OnAppWindowChanged;
        InitializeReaderGaps();
        InitializeCommands();
        RefreshRecent();
        UpdateChrome();
        Title = Strings.Get("AppName");
    }

    /// <summary>The open document, for tests and diagnostics.</summary>
    internal DocumentSession? Session => _session;

    // ---- window ------------------------------------------------------------------------------

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
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
        // Minimised: keep only what is on screen (master prompt 7.1 rule 5).
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            _session?.TrimMemory();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _policyTimer?.Stop();
        _services.Settings.Flush(); // a change in the last half second is written now, not lost
        await CloseDocumentAsync();
    }

    /// <summary>Brings the window forward (a second launch redirected here).</summary>
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

    // ---- opening -----------------------------------------------------------------------------

    /// <summary>Opens a document, replacing the current one. Shows its own errors.</summary>
    internal Task OpenPathAsync(string path)
    {
        AppTrace.OpenRequested();
        // One open at a time, in the order asked (a drop during an open waits for it).
        var next = _openChain.ContinueWith(_ => OpenCoreAsync(path), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
        _openChain = next;
        return next;
    }

    private async Task OpenCoreAsync(string path)
    {
        await _loaded.Task; // dialogs need the window's XamlRoot
        try
        {
            if (_closed)
            {
                return;
            }

            Busy.IsActive = true;
            Banner.IsOpen = false;
            EngineHost host;
            try
            {
                host = await _host;
            }
            catch (Exception e) when (e is EngineStartException or InvalidOperationException)
            {
                await ShowErrorAsync(DocumentErrorKind.SandboxUnavailable, path, e.Message);
                return;
            }

            await CloseDocumentAsync();
            var session = new DocumentSession(host, _store, Pages.Presenter, DispatcherQueue);
            session.ViewChanged += UpdateChrome;
            session.EngineStateChanged += OnEngineStateChanged;
            session.PageIncomplete += OnPageIncomplete;
            session.OverlaysChanged += OnOverlaysChanged;
            Pages.Session = session;
            _session = session;

            var attempts = new PasswordAttempts();
            string? password = null;
            while (true)
            {
                var outcome = await session.OpenAsync(path, password);
                if (outcome.Kind == OpenOutcomeKind.Opened)
                {
                    ShowDocument();
                    return;
                }

                if (outcome.Kind is OpenOutcomeKind.PasswordRequired or OpenOutcomeKind.PasswordIncorrect)
                {
                    if (attempts.Exhausted)
                    {
                        await CloseDocumentAsync();
                        await ShowErrorAsync(DocumentErrorKind.PasswordIncorrect, path, null, tooMany: true);
                        return;
                    }

                    var entered = await PromptPasswordAsync(path, wrong: outcome.Kind == OpenOutcomeKind.PasswordIncorrect, attempts.Remaining);
                    if (entered is null)
                    {
                        await CloseDocumentAsync(); // cancelled
                        return;
                    }

                    attempts.Record();
                    password = entered;
                    continue;
                }

                await CloseDocumentAsync();
                if (outcome.Error == DocumentErrorKind.FileNotFound)
                {
                    _store.RemoveRecent(path);
                    RefreshRecent();
                }

                await ShowErrorAsync(outcome.Error, path, outcome.Detail);
                return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever went wrong, the person gets a plain message and the window stays usable.
            await CloseDocumentAsync();
            await ShowErrorAsync(DocumentErrorKind.Unknown, path, e.Message);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private async Task CloseDocumentAsync()
    {
        var session = _session;
        _session = null;
        DetachSidebar();
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = string.Empty;
        Pages.Session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        if (!_closed)
        {
            ShowEmptyState();
        }
    }

    private void ShowDocument()
    {
        CloseSettings();
        EmptyState.Visibility = Visibility.Collapsed;
        Pages.Visibility = Visibility.Visible;
        RefreshRecent();
        UpdateChrome();
        if (_session is { } opened)
        {
            AttachSidebar(opened);
        }

        Pages.FocusPages();
    }

    private void ShowEmptyState()
    {
        Pages.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        RefreshRecent();
        UpdateChrome();
    }

    /// <summary>Several files (a drop, a multi-file launch): the first is shown; the others are left for tabs (not built yet).</summary>
    internal async Task HandleActivationAsync(IReadOnlyList<string> paths)
    {
        BringToFront();
        if (paths.Count > 0)
        {
            await OpenPathAsync(paths[0]);
        }
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
            TitleText.Text = Strings.Get("AppName");
            Title = Strings.Get("AppName");
            AutomationProperties.SetName(PageIndicator, Strings.Get("PageIndicator_None"));
            return;
        }

        var controller = session!.Controller;
        var page = Math.Max(0, controller.CurrentPage) + 1;
        var total = controller.PageCount;
        PageIndicator.Content = Strings.Format("PageIndicator_Text", page, total);
        AutomationProperties.SetName(PageIndicator, Strings.Format("PageIndicator_Automation", page, total));
        var percent = Math.Round(controller.Zoom * 100).ToString("0", CultureInfo.CurrentCulture);
        ((AppBarButton)ZoomButton).Label = percent + "%";
        AutomationProperties.SetName(ZoomButton, Strings.Format("Zoom_Automation", percent));
        AutomationProperties.SetItemStatus(Pages, controller.ViewTurns == 0 ? Strings.Get("Surface_NotRotated") : Strings.Format("Surface_Rotated", controller.ViewTurns * 90));
        var title = Strings.Format("Window_Title", session.Title);
        TitleText.Text = title;
        Title = title;
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
        var result = await dialog.ShowAsync();
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
        await dialog.ShowAsync();
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(box.Value) && _session is { IsOpen: true } current)
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
            var file = items.OfType<StorageFile>().FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase)) ?? items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null)
            {
                await OpenPathAsync(file.Path);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
