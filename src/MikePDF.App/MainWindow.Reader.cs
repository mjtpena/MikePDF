using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Core.Colour;
using MikePDF.Core.Geometry;
using MikePDF.Core.Reading;
using WinRT.Interop;

namespace MikePDF.App;

/// <summary>
/// The reader gaps of M2 (docs/features/m2-reader-gaps.md): Back and forward, actual size, page colours, the properties dialog, the copy
/// restriction's banner, and full screen.
/// </summary>
public sealed partial class MainWindow
{
    private const int MonitorDefaultToNearest = 2;
    private const int MonitorRawDpi = 2;

    private bool _fullScreen;
    private bool _sidebarWasOpen;
    private bool _propertiesOpen;

    private void InitializeReaderGaps()
    {
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);
        Root.PreviewKeyDown += OnRootPreviewKeyDown;
        Pages.CopyBlocked += OnCopyBlocked;
    }

    // ---- back and forward ----------------------------------------------------------------------------------------------------------

    private void GoBack()
    {
        if (_session?.GoBack() == true)
        {
            Pages.FocusPages();
        }
    }

    private void GoForward()
    {
        if (_session?.GoForward() == true)
        {
            Pages.FocusPages();
        }
    }

    // The mouse's back and forward buttons work over the whole window, whatever the pointer is over or has already handled.
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(Root).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.XButton1Pressed)
        {
            e.Handled = true;
            GoBack();
        }
        else if (kind == PointerUpdateKind.XButton2Pressed)
        {
            e.Handled = true;
            GoForward();
        }
    }

    // ---- actual size ----------------------------------------------------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>The physical pixels per inch of the monitor the window is on, as Windows knows them (the monitor's own size), or 0 when it does not.</summary>
    private double RawMonitorDpi()
    {
        var monitor = MonitorFromWindow(WindowNative.GetWindowHandle(this), MonitorDefaultToNearest);
        return monitor != 0 && GetDpiForMonitor(monitor, MonitorRawDpi, out var x, out _) == 0 ? x : 0;
    }


    // ---- page colour -----------------------------------------------------------------------------------------------------------------

    private void ApplyPageColourMenu()
    {
        var mode = Pages.PageColour;
        PageColourNormal.IsChecked = mode == PageColourMode.Normal;
        PageColourInverted.IsChecked = mode == PageColourMode.Inverted;
        PageColourSepia.IsChecked = mode == PageColourMode.Sepia;
    }

    // ---- copying -----------------------------------------------------------------------------------------------------------------------

    private void OnCopyBlocked()
    {
        ShowBanner(InfoBarSeverity.Informational, "Banner_CopyBlocked_Title", "Banner_CopyBlocked_Message", closable: true);
        _bannerTimer.Interval = TimeSpan.FromSeconds(6);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    // ---- full screen -----------------------------------------------------------------------------------------------------------------

    private void SetFullScreen(bool on, bool quiet = false)
    {
        if (on == _fullScreen)
        {
            return;
        }

        _fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        ApplyFullScreenChrome(on);
        if (on && quiet)
        {
            Banner.IsOpen = false;
        }
        else if (on)
        {
            ShowBanner(InfoBarSeverity.Informational, "Banner_FullScreen_Title", "Banner_FullScreen_Message", closable: true);
            _bannerTimer.Interval = TimeSpan.FromSeconds(4);
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
        else
        {
            Banner.IsOpen = false;
        }

        Pages.FocusPages();
    }

    // The chrome goes and comes back; the sidebar is remembered. (The presenter changes size, so the viewport follows by itself.)
    private void ApplyFullScreenChrome(bool on)
    {
        var visibility = on ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Visibility = visibility;
        Commands.Visibility = visibility;
        if (on)
        {
            _sidebarWasOpen = Sidebar.IsPaneOpen;
            if (_sidebarWasOpen)
            {
                SidebarButton.IsChecked = false;
            }
        }
        else if (_sidebarWasOpen && SidebarButton.IsEnabled)
        {
            SidebarButton.IsChecked = true;
        }
    }

    // Left some other way (Win+Shift+Enter, the system): put the window back as it was.
    private void SyncFullScreen(AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange && _fullScreen && AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            _fullScreen = false;
            ApplyFullScreenChrome(false);
            if (_presenting)
            {
                _presentationFullScreenBefore = false; // the system took the full screen away: the presentation ends with it, and the window stays as it is now
                ExitPresentation();
            }
        }
    }

    // ---- document properties ---------------------------------------------------------------------------------------------------------

    private async Task ShowPropertiesAsync()
    {
        if (_propertiesOpen || _session is not { IsOpen: true } session)
        {
            return;
        }

        _propertiesOpen = true;
        var released = false; // set once this dialog has closed and the flag has been given back: a later call may own it by then
        using var cancel = new CancellationTokenSource();
        try
        {
            var loading = new StackPanel { Spacing = 12, MinWidth = 420 };
            var progress = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(progress, Strings.Get("Properties_Loading"));
            var message = new TextBlock { Text = Strings.Get("Properties_Loading"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
            loading.Children.Add(progress);
            loading.Children.Add(message);

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme,
                Title = Strings.Get("Properties_Title"),
                Content = loading,
                CloseButtonText = Strings.Get("Dialog_Close"),
                DefaultButton = ContentDialogButton.Close,
            };
            AutomationProperties.SetAutomationId(dialog, "PropertiesDialog");
            // Free again the moment the dialog has closed (not when ShowAsync's continuation gets round to it): the framework hands the keyboard
            // back to the pages first, and a person (or a screen reader) who asks again right away must not be told no.
            dialog.Closed += (_, _) =>
            {
                released = true;
                _propertiesOpen = false;
            };
            var fill = FillPropertiesAsync(dialog, session, cancel.Token);
            await ShowWhenTheLastDialogIsGoneAsync(dialog);
            await cancel.CancelAsync();
            // Closing the dialog does not wait for the engine: a read still in flight is abandoned (it has been told to stop) and is not awaited,
            // so the next press of Ctrl+D is never held up by it. Its outcome is only ever shown in a dialog that is no longer there.
            _ = fill.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        finally
        {
            if (!released)
            {
                _propertiesOpen = false; // the dialog never opened
            }
        }

        Pages.FocusPages();
    }

    // A ContentDialog that has just been closed can still be going away when the next one is asked for, and the framework refuses a second one
    // ("only one at a time") for a moment. Ask again shortly, a few times, rather than losing the person's request.
    private static async Task ShowWhenTheLastDialogIsGoneAsync(ContentDialog dialog)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dialog.ShowAsync();
                return;
            }
            catch (Exception e) when (attempt < 10 && e is InvalidOperationException or COMException)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task FillPropertiesAsync(ContentDialog dialog, DocumentSession session, CancellationToken token)
    {
        ReadReply<DocumentProperties> reply;
        try
        {
            reply = await session.GetPropertiesAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!reply.IsOk)
        {
            var failed = new TextBlock { Text = Strings.Get("Properties_Failed"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(failed, "PropertiesFailed");
            dialog.Content = failed;
            return;
        }

        dialog.Content = BuildPropertiesContent(session, reply.Value!);
    }

    private static ScrollViewer BuildPropertiesContent(DocumentSession session, DocumentProperties p)
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 440, MaxWidth = 560 };
        var unknown = Strings.Get("Properties_Unknown");

        void Heading(string key)
        {
            var heading = new TextBlock { Text = Strings.Get(key), Margin = new Thickness(0, 12, 0, 4), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            panel.Children.Add(heading);
        }

        void Row(string id, string labelKey, string value)
        {
            var label = Strings.Get(labelKey);
            var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            AutomationProperties.SetAccessibilityView(left, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var right = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(right, 1);
            AutomationProperties.SetAutomationId(right, id);
            AutomationProperties.SetName(right, Strings.Format("Properties_RowName", label, value));
            grid.Children.Add(left);
            grid.Children.Add(right);
            panel.Children.Add(grid);
        }

        string Text(string value) => value.Length == 0 ? unknown : value;
        string YesNo(bool value) => Strings.Get(value ? "Properties_Yes" : "Properties_No");

        Heading("Properties_Section_Description");
        Row("Prop_Title", "Properties_Title_Label", Text(p.Title));
        Row("Prop_Author", "Properties_Author", Text(p.Author));
        Row("Prop_Subject", "Properties_Subject", Text(p.Subject));
        Row("Prop_Keywords", "Properties_Keywords", Text(p.Keywords));

        Heading("Properties_Section_Document");
        Row("Prop_FileName", "Properties_FileName", System.IO.Path.GetFileName(session.Path));
        Row("Prop_FileSize", "Properties_FileSize", FileSizeText(session.Path, unknown));
        Row("Prop_Pages", "Properties_Pages", p.PageCount.ToString("N0", CultureInfo.CurrentCulture));
        Row("Prop_PageSize", "Properties_PageSize", PageSizeText(session, unknown));
        Row("Prop_Version", "Properties_Version", Text(p.PdfVersion));
        Row("Prop_Tagged", "Properties_Tagged", YesNo(p.IsTagged));
        Row("Prop_Created", "Properties_Created", DateText(p.Created, p.CreatedText, p.DetailsAvailable, unknown));
        Row("Prop_Modified", "Properties_Modified", DateText(p.Modified, p.ModifiedText, p.DetailsAvailable, unknown));
        Row("Prop_Creator", "Properties_Creator", Text(p.Creator));
        Row("Prop_Producer", "Properties_Producer", Text(p.Producer));
        Row("Prop_Scripts", "Properties_Scripts", YesNo(p.HasJavaScript));
        Row("Prop_Signatures", "Properties_Signatures", p.SignatureCount.ToString("N0", CultureInfo.CurrentCulture));
        Row("Prop_Form", "Properties_Form", p.DetailsAvailable ? Strings.Get("Properties_Form_" + p.FormType) : unknown);

        Heading("Properties_Section_Security");
        Row("Prop_Encryption", "Properties_Encryption", p.DetailsAvailable ? EncryptionText(p.Permissions.SecurityRevision) : unknown);
        if (p.OpenedWithOwnerPassword)
        {
            Row("Prop_OwnerPassword", "Properties_OwnerPassword", Strings.Get("Properties_OwnerOpened"));
        }

        foreach (var kind in p.Permissions.Defined)
        {
            Row("Prop_Perm_" + kind, "Properties_Perm_" + kind, Strings.Get(p.Permissions.Allows(kind) ? "Properties_Allowed" : "Properties_NotAllowed"));
        }

        Heading("Properties_Section_Fonts");
        if (!p.DetailsAvailable)
        {
            Row("Prop_Fonts", "Properties_Fonts", unknown);
        }
        else
        {
            var fonts = p.Fonts.Count == 0
                ? Strings.Get("Properties_NoFonts")
                : string.Join('\n', p.Fonts.Select(f => Strings.Format(f.IsEmbedded ? (f.IsSubset ? "Properties_Font_EmbeddedSubset" : "Properties_Font_Embedded") : "Properties_Font_NotEmbedded", f.Name)));
            Row("Prop_Fonts", p.FontsComplete ? "Properties_Fonts" : "Properties_FontsPartial", fonts);
            if (!p.FontsComplete)
            {
                Row("Prop_FontsNote", "Properties_Note", Strings.Format("Properties_FontsPartialNote", p.FontPagesScanned));
            }
        }

        if (!p.DetailsAvailable)
        {
            var missing = new TextBlock { Text = Strings.Get("Properties_DetailsMissing"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            AutomationProperties.SetAutomationId(missing, "PropertiesDetailsMissing");
            AutomationProperties.SetLiveSetting(missing, AutomationLiveSetting.Polite);
            panel.Children.Add(missing);
        }

        return new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static string DateText(DateTimeOffset? date, string raw, bool available, string unknown)
    {
        if (date is { } value)
        {
            return value.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        }

        return available && raw.Length > 0 ? raw : unknown;
    }

    private static string FileSizeText(string path, string unknown)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return Strings.Format("Properties_FileSizeValue", HumanSize(bytes), bytes.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return unknown;
        }
    }

    private static string HumanSize(long bytes)
    {
        var culture = CultureInfo.CurrentCulture;
        return bytes switch
        {
            < 1024 => Strings.Format("Properties_Unit_B", bytes.ToString("N0", culture)),
            < 1024 * 1024 => Strings.Format("Properties_Unit_KB", (bytes / 1024.0).ToString("N1", culture)),
            < 1024L * 1024 * 1024 => Strings.Format("Properties_Unit_MB", (bytes / (1024.0 * 1024)).ToString("N1", culture)),
            _ => Strings.Format("Properties_Unit_GB", (bytes / (1024.0 * 1024 * 1024)).ToString("N2", culture)),
        };
    }

    private static string PageSizeText(DocumentSession session, string unknown)
    {
        var sizes = session.PageSizes;
        if (sizes.Count == 0)
        {
            return unknown;
        }

        // The size as the file has it, before the view rotation: the sizes the session keeps are the pages' own displayed sizes.
        var first = sizes[0];
        var text = SizeText(first);
        var differ = session.PageSizesComplete && sizes.Any(s => Math.Abs(s.Width - first.Width) > 0.5 || Math.Abs(s.Height - first.Height) > 0.5);
        return differ ? Strings.Format("Properties_PageSizeVaries", text) : text;
    }

    private static string SizeText(SizeD size)
    {
        var culture = CultureInfo.CurrentCulture;
        var metric = RegionInfo.CurrentRegion.IsMetric;
        var points = size.Width.ToString("0.#", culture);
        var pointsHeight = size.Height.ToString("0.#", culture);
        var width = metric ? (size.Width * 25.4 / 72).ToString("0", culture) : (size.Width / 72).ToString("0.##", culture);
        var height = metric ? (size.Height * 25.4 / 72).ToString("0", culture) : (size.Height / 72).ToString("0.##", culture);
        var name = PaperSizes.Name(size.Width, size.Height);
        var key = (metric ? "Properties_PageSizeMm" : "Properties_PageSizeIn") + (name is null ? string.Empty : "Named");
        return name is null ? Strings.Format(key, points, pointsHeight, width, height) : Strings.Format(key, points, pointsHeight, width, height, name);
    }

    private static string EncryptionText(int revision) => revision switch
    {
        0 => Strings.Get("Properties_Enc_None"),
        2 => Strings.Get("Properties_Enc_R2"),
        3 => Strings.Get("Properties_Enc_R3"),
        4 => Strings.Get("Properties_Enc_R4"),
        5 or 6 => Strings.Format("Properties_Enc_Aes256", revision),
        _ => Strings.Format("Properties_Enc_Other", revision),
    };
}
