using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MikePDF.App.Services;
using MikePDF.Broker;
using MikePDF.Core.Printing;
using MikePDF.Core.Settings;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.App.Views;

/// <summary>What the print sheet needs from the window and the open document.</summary>
/// <param name="DocumentName">The title or file name: the spool job's name, and what the progress strip says.</param>
/// <param name="PageCount">Pages in the document.</param>
/// <param name="CurrentPage">The page being read, 1-based.</param>
/// <param name="WindowHandle">The window the driver's own dialog is owned by.</param>
/// <param name="Settings">Where the last-used values are read from (they are written when a job starts, by the window).</param>
/// <param name="RequestedScope">The pages the command asked for ("Print current page"), or null.</param>
/// <param name="PrintingAllowed">The <c>PrintingEnabled</c> policy as read when the sheet opened.</param>
/// <param name="GetDefaults">The document's own wishes (<c>/ViewerPreferences</c>).</param>
/// <param name="OpenSession">A print session for preview and for the job.</param>
/// <param name="Resume">The choices to start from instead of the last used ones and the document's wishes (the sheet reopened after a job failed).</param>
internal sealed record PrintSheetContext(
    string DocumentName,
    int PageCount,
    int CurrentPage,
    nint WindowHandle,
    SettingsStore Settings,
    PrintScope? RequestedScope,
    bool PrintingAllowed,
    Func<CancellationToken, Task<PrintDefaults>> GetDefaults,
    Func<PrintOptions, CancellationToken, Task<EngineResult<PrintSession>>> OpenSession,
    PrintSheetState? Resume = null);

/// <summary>What the person asked for when they pressed Print: the printer, the options, the pages, and the session (and its mode) the sheet already has open.</summary>
internal sealed record PrintSheetChoice(string Printer, PrintOptions Options, PageSet Pages, PrintScope Scope, string RangeText, PageSubset Subset, DevMode? DevMode, PrintSession? Session, bool SessionForImage, bool LowQuality);

/// <summary>
/// The print sheet (docs/design/screens/11-print.md): printer, copies, pages, sides, orientation, scale, pages per sheet, paper, colour, what to include, and a preview of the first sheet drawn by
/// the same planner the job uses. The rules live in <see cref="PrintSheetModel"/>; this class shows them. The preview and the notices follow the options 250 ms after the last change and never block the
/// controls. The engine draws the preview from a session of its own; the printer driver is only asked for the paper and what it did with the settings (never to print).
/// </summary>
internal sealed partial class PrintSheetView : UserControl, IDisposable
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AnnounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly PrintSheetContext _context;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _announceTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refresh;
    private Task _refreshTask = Task.CompletedTask;
    private PrintSheetState _state;
    private PrintSheetEvaluation _eval;
    private Task<EngineResult<PrintSession>>? _sessionTask;
    private PrintSession? _session;
    private bool _sessionForImage;
    private PrinterProbe _probe = PrinterProbe.None;
    private DevMode? _devMode;
    private DevMode? _devModeAtDialog;
    private int _sheet;
    private int _updating;
    private bool _touched;
    private bool _printersKnown;
    private bool _printersEmpty;
    private bool _detached;
    private string _announcedError = string.Empty;

    public PrintSheetView(PrintSheetContext context)
    {
        _context = context;
        _ui = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        _refreshTimer = _ui.CreateTimer();
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Interval = RefreshDelay;
        _refreshTimer.Tick += (_, _) => StartRefresh();
        _announceTimer = _ui.CreateTimer();
        _announceTimer.IsRepeating = false;
        _announceTimer.Interval = AnnounceDelay;
        _announceTimer.Tick += (_, _) => ShowAnnouncedTexts();

        var saved = PrintPreferences.Load(context.Settings);
        _state = context.Resume ?? PrintSheetModel.Initial(saved, PrintDefaults.Plain, context.RequestedScope);
        _eval = PrintSheetModel.Evaluate(_state, context.PageCount, context.CurrentPage);
        SetTexts();
        FillStaticChoices();
        Load(_state);
        Loaded += OnLoaded;
    }

    /// <summary>Print can go ahead: there are pages, a printer, and policy allows it.</summary>
    public bool CanPrint => _context.PrintingAllowed && _eval.HasPages && !string.IsNullOrEmpty(_state.Printer);

    public event Action? CanPrintChanged;

    public void Dispose() => _lifetime.Dispose();

    /// <summary>The summary and the range error have changed in words (for a test that waits for them).</summary>
    public string SummaryTextNow => SummaryText.Text;

    /// <summary>Puts the keyboard on the printer drop-down (design 11-print section 5: initial focus).</summary>
    public void FocusFirst() => PrinterBox.Focus(FocusState.Programmatic);

    // ---- setting up ------------------------------------------------------------------------------------------------------------------

    private void SetTexts()
    {
        PrinterHeading.Text = Strings.Get("Print_Printer_Label");
        PagesHeading.Text = Strings.Get("Print_Pages_Heading");
        IncludeHeading.Text = Strings.Get("Print_Include_Heading");
        Label(PrinterBox, "Print_Printer_Label", header: true);
        PrinterBox.PlaceholderText = Strings.Get("Print_Printer_Looking");
        AutomationProperties.SetName(PrinterRing, Strings.Get("Print_Printer_Looking"));
        NoPrintersText.Text = Strings.Get("Print_Printer_None");
        OpenPrinterSettings.Content = Strings.Get("Print_Printer_OpenSettings");
        PreferencesButton.Content = Strings.Get("Print_Preferences");
        AutomationProperties.SetName(PreferencesButton, Strings.Get("Print_Preferences_Name"));
        CopiesBox.Header = Strings.Get("Print_Copies_Label");
        AutomationProperties.SetName(CopiesBox, Strings.Get("Print_Copies_Label"));
        CollateBox.Content = Strings.Get("Print_Collate_Label");
        AutomationProperties.SetName(CollateBox, Strings.Get("Print_Collate_Label"));
        PagesAll.Content = Strings.Format("Print_Range_All", _context.PageCount);
        AutomationProperties.SetName(PagesAll, Strings.Get("Print_Range_AllName"));
        PagesCurrent.Content = Strings.Format("Print_Range_Current", _context.CurrentPage);
        AutomationProperties.SetName(PagesCurrent, Strings.Get("Print_Range_CurrentName"));
        PagesRange.Content = Strings.Get("Print_Range_Pages");
        AutomationProperties.SetName(PagesRange, Strings.Get("Print_Range_PagesName"));
        RangeBox.PlaceholderText = Strings.Get("Print_Range_Hint");
        AutomationProperties.SetName(RangeBox, Strings.Get("Print_Range_Box_Name"));
        AutomationProperties.SetHelpText(RangeBox, Strings.Get("Print_Range_Hint"));
        Label(SubsetBox, "Print_Subset_Label", header: true);
        Label(SidesBox, "Print_Sides_Label", header: true);
        Label(OrientationBox, "Print_Orientation_Label", header: true);
        AutoRotateBox.Content = Strings.Get("Print_AutoRotate");
        AutomationProperties.SetName(AutoRotateBox, Strings.Get("Print_AutoRotate"));
        Label(ScaleBox, "Print_Scale_Label", header: true);
        AutomationProperties.SetName(ScalePercentBox, Strings.Get("Print_Scale_Percent_Name"));
        Label(PerSheetBox, "Print_PerSheet_Label", header: true);
        Label(SheetOrderBox, "Print_Order_Label", header: true);
        Label(PaperBox, "Print_Paper_Label", header: true);
        Label(ColourBox, "Print_Colour_Label", header: true);
        AnnotationsBox.Content = Strings.Get("Print_Annotations");
        AutomationProperties.SetName(AnnotationsBox, Strings.Get("Print_Annotations"));
        FormsBox.Content = Strings.Get("Print_FormFields");
        AutomationProperties.SetName(FormsBox, Strings.Get("Print_FormFields"));
        AsImageBox.Content = Strings.Get("Print_AsImage");
        AutomationProperties.SetName(AsImageBox, Strings.Get("Print_AsImage"));
        CutOffNotice.Message = Strings.Get("Print_CutOff_Notice");
        LowQualityNotice.Message = Strings.Get("Print_LowQuality_Notice");
        PreviewToggle.Content = Strings.Get("Print_Preview_Show");
        AutomationProperties.SetName(PreviewToggle, Strings.Get("Print_Preview_Show"));
        AutomationProperties.SetName(PrevButton, Strings.Get("Print_Preview_Prev"));
        AutomationProperties.SetName(NextButton, Strings.Get("Print_Preview_Next"));
        ToolTipService.SetToolTip(PrevButton, Strings.Get("Print_Preview_Prev"));
        ToolTipService.SetToolTip(NextButton, Strings.Get("Print_Preview_Next"));
        AutomationProperties.SetName(PagerText, Strings.Get("Print_Preview_Name"));
        AutomationProperties.SetName(SummaryText, Strings.Get("Print_Summary_Name"));
        AutomationProperties.SetName(PreviewPicture, Strings.Get("Print_Preview_Name"));
        AutomationProperties.SetName(PreviewStatus, Strings.Get("Print_Preview_Name"));
        PreviewStatus.Text = Strings.Get("Print_Preview_Loading");
        PreviewRing.IsActive = true;
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        PreferencesButton.IsEnabled = false;
    }

    private static void Label(ComboBox box, string key, bool header)
    {
        var text = Strings.Get(key);
        if (header)
        {
            box.Header = text;
        }

        AutomationProperties.SetName(box, text);
    }

    private static ComboBoxItem Item(string text, object tag) => new() { Content = text, Tag = tag };

    private void FillStaticChoices()
    {
        SubsetBox.Items.Add(Item(Strings.Get("Print_Subset_All"), PageSubset.All));
        SubsetBox.Items.Add(Item(Strings.Get("Print_Subset_Odd"), PageSubset.Odd));
        SubsetBox.Items.Add(Item(Strings.Get("Print_Subset_Even"), PageSubset.Even));
        SidesBox.Items.Add(Item(Strings.Get("Print_Sides_One"), PrintDuplex.None));
        SidesBox.Items.Add(Item(Strings.Get("Print_Sides_Long"), PrintDuplex.LongEdge));
        SidesBox.Items.Add(Item(Strings.Get("Print_Sides_Short"), PrintDuplex.ShortEdge));
        OrientationBox.Items.Add(Item(Strings.Get("Print_Orientation_Auto"), PaperOrientation.Auto));
        OrientationBox.Items.Add(Item(Strings.Get("Print_Orientation_Portrait"), PaperOrientation.Portrait));
        OrientationBox.Items.Add(Item(Strings.Get("Print_Orientation_Landscape"), PaperOrientation.Landscape));
        ScaleBox.Items.Add(Item(Strings.Get("Print_Scale_Shrink"), PrintScaling.Shrink));
        ScaleBox.Items.Add(Item(Strings.Get("Print_Scale_Fit"), PrintScaling.Fit));
        ScaleBox.Items.Add(Item(Strings.Get("Print_Scale_Actual"), PrintScaling.Actual));
        ScaleBox.Items.Add(Item(Strings.Get("Print_Scale_Custom"), PrintScaling.Custom));
        foreach (var n in PrintOptions.PagesPerSheetChoices)
        {
            PerSheetBox.Items.Add(Item(n == 1 ? Strings.Get("Print_PerSheet_1") : Strings.Format("Print_PerSheet_N", n), n));
        }

        SheetOrderBox.Items.Add(Item(Strings.Get("Print_Order_Horizontal"), SheetOrder.Horizontal));
        SheetOrderBox.Items.Add(Item(Strings.Get("Print_Order_Vertical"), SheetOrder.Vertical));
        ColourBox.Items.Add(Item(Strings.Get("Print_Colour_Colour"), false));
        ColourBox.Items.Add(Item(Strings.Get("Print_Colour_Grey"), true));
    }

    private static void Select(ComboBox box, object value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, value))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
    }

    private static T? Chosen<T>(ComboBox box)
        where T : struct => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private void Quietly(Action action)
    {
        _updating++;
        try
        {
            action();
        }
        finally
        {
            _updating--;
        }
    }

    // Puts a state into the controls without letting the controls' events change it back.
    private void Load(PrintSheetState state) => Quietly(() =>
    {
        var o = state.Options;
        CopiesBox.Value = o.Copies;
        CollateBox.IsChecked = o.Collate;
        (state.Scope switch { PrintScope.CurrentPage => PagesCurrent, PrintScope.Range => PagesRange, _ => PagesAll }).IsChecked = true;
        RangeBox.Text = state.RangeText;
        Select(SubsetBox, state.Subset);
        Select(SidesBox, o.Duplex);
        Select(OrientationBox, o.Orientation);
        AutoRotateBox.IsChecked = o.AutoRotate;
        Select(ScaleBox, o.Scaling);
        ScalePercentBox.Value = o.CustomPercent;
        Select(PerSheetBox, o.PagesPerSheet);
        Select(SheetOrderBox, o.SheetOrder);
        Select(ColourBox, o.Grayscale);
        AnnotationsBox.IsChecked = o.Annotations;
        FormsBox.IsChecked = o.FormFields;
        AsImageBox.IsChecked = o.AsImage;
        SelectPaper(o.PaperSizeId);
        Reflect(announceNow: true);
    });

    private void SelectPaper(int id)
    {
        var target = id != 0 ? id : _probe.DefaultPaperId;
        foreach (var item in PaperBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is int tag && tag == target)
            {
                PaperBox.SelectedItem = item;
                return;
            }
        }

        PaperBox.SelectedIndex = PaperBox.Items.Count > 0 ? 0 : -1;
    }

    // ---- start: printers, the document's wishes, the session ------------------------------------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _sessionForImage = _state.Options.AsImage;
        _sessionTask = _context.OpenSession(_state.Options, _lifetime.Token);
        _ = ObserveSessionAsync();
        var defaults = _context.GetDefaults(_lifetime.Token);
        var printers = Task.Run(() =>
        {
            try
            {
                return PrinterCatalog.Enumerate();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return [];
            }
        });
        try
        {
            var hints = await defaults;
            if (!_touched && _context.Resume is null && !_lifetime.IsCancellationRequested)
            {
                _state = PrintSheetModel.Initial(PrintPreferences.Load(_context.Settings), hints, _context.RequestedScope) with { Abilities = _state.Abilities };
                Load(_state);
            }

            var list = await printers;
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            ShowPrinters(list);
        }
        catch (OperationCanceledException)
        {
            // The sheet was closed before it finished starting.
        }
    }

    private async Task ObserveSessionAsync()
    {
        try
        {
            var result = await _sessionTask!;
            if (result.IsOk && result.Value is { } session)
            {
                if (_detached)
                {
                    return; // DetachAsync is waiting for this very task and takes the session
                }

                _session = session;
            }
        }
        catch (OperationCanceledException)
        {
            // closed while starting
        }
        catch (Exception e) when (e is EngineStartException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Print session: " + e.GetType().Name);
        }

        if (!_lifetime.IsCancellationRequested)
        {
            ScheduleRefresh();
        }
    }

    private void ShowPrinters(IReadOnlyList<PrinterInfo> printers)
    {
        _printersKnown = true;
        PrinterRing.IsActive = false;
        PrinterRing.Visibility = Visibility.Collapsed;
        _printersEmpty = printers.Count == 0;
        NoPrinters.Visibility = _printersEmpty ? Visibility.Visible : Visibility.Collapsed;
        PrinterBox.Visibility = _printersEmpty ? Visibility.Collapsed : Visibility.Visible;
        PreferencesButton.Visibility = _printersEmpty ? Visibility.Collapsed : Visibility.Visible;
        Quietly(() =>
        {
            PrinterBox.Items.Clear();
            foreach (var printer in printers)
            {
                PrinterBox.Items.Add(Item(printer.Name, printer.Name));
            }

            PrinterBox.IsEnabled = !_printersEmpty;
        });

        var chosen = PrintSheetModel.ChoosePrinter(_state.Printer, [.. printers.Select(p => p.Name)], printers.FirstOrDefault(p => p.IsDefault)?.Name);
        if (chosen is null)
        {
            _state = _state with { Printer = string.Empty };
            PreviewRing.IsActive = false;
            PreviewStatus.Text = Strings.Get("Print_Printer_None");
            Reflect(announceNow: true);
            return;
        }

        Quietly(() => Select(PrinterBox, chosen));
        _state = _state with { Printer = chosen };
        _ = ProbeAsync(chosen);
    }

    // Reads the printer's abilities off the UI thread (a network printer can take seconds) and applies them to the choices.
    private async Task ProbeAsync(string printer)
    {
        PreferencesButton.IsEnabled = false;
        var probe = await Task.Run(() => PrinterProbe.Read(printer));
        if (_lifetime.IsCancellationRequested || !string.Equals(_state.Printer, printer, StringComparison.Ordinal))
        {
            return; // another printer was chosen meanwhile
        }

        _probe = probe;
        _devMode = null; // the driver's own settings belong to the printer they were made for
        _state = PrintSheetModel.WithAbilities(_state, probe.Abilities);
        Quietly(() =>
        {
            PaperBox.Items.Clear();
            foreach (var paper in probe.Abilities.Papers)
            {
                PaperBox.Items.Add(Item(paper.Name, paper.Id));
            }

            PaperBox.IsEnabled = PaperBox.Items.Count > 0;
            SelectPaper(_state.Options.PaperSizeId);
            Select(SidesBox, _state.Options.Duplex);
            Select(ColourBox, _state.Options.Grayscale);
            if (PaperBox.SelectedItem is ComboBoxItem { Tag: int selectedPaper } && _state.Options.PaperSizeId != 0)
            {
                _state = _state with { Options = _state.Options with { PaperSizeId = selectedPaper } };
            }
        });
        PreferencesButton.IsEnabled = probe.Answered;
        DeviceNotice.IsOpen = !probe.Answered;
        DeviceNotice.Message = Strings.Get("Print_Offline_Notice");
        Reflect(announceNow: true);
        ScheduleRefresh();
    }

    // ---- the controls ------------------------------------------------------------------------------------------------------------------

    private void Touch()
    {
        if (_updating == 0)
        {
            _touched = true;
        }
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var stacked = width < 720;
        var hidden = width < 460;
        PreviewToggle.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        PreviewFrame.Visibility = !hidden || PreviewToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(OptionsScroll, stacked ? 2 : 1);
        Grid.SetColumnSpan(PreviewPane, stacked ? 2 : 1);
        if (stacked)
        {
            Grid.SetColumn(OptionsScroll, 0);
            Grid.SetRow(OptionsScroll, 1);
            Grid.SetRow(PreviewPane, 0);
            PreviewColumn.MinWidth = 0;
            OptionsColumn.Width = new GridLength(0);
            PreviewRow.Height = hidden ? GridLength.Auto : new GridLength(260);
            OptionsRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            Grid.SetColumn(OptionsScroll, 1);
            Grid.SetRow(OptionsScroll, 0);
            Grid.SetRow(PreviewPane, 0);
            PreviewColumn.MinWidth = 240;
            OptionsColumn.Width = new GridLength(390);
            PreviewRow.Height = new GridLength(1, GridUnitType.Star);
            OptionsRow.Height = new GridLength(0);
        }
    }

    private void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        var show = PreviewToggle.IsChecked == true;
        PreviewToggle.Content = Strings.Get(show ? "Print_Preview_Hide" : "Print_Preview_Show");
        AutomationProperties.SetName(PreviewToggle, Strings.Get(show ? "Print_Preview_Hide" : "Print_Preview_Show"));
        PreviewFrame.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || PrinterBox.SelectedItem is not ComboBoxItem { Tag: string name })
        {
            return;
        }

        Touch();
        _state = _state with { Printer = name };
        Reflect(announceNow: true);
        _ = ProbeAsync(name);
    }

    private void OnCopiesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating > 0)
        {
            return;
        }

        Touch();
        var copies = double.IsNaN(args.NewValue) ? 1 : (int)Math.Clamp(Math.Round(args.NewValue), PrintOptions.MinCopies, PrintOptions.MaxCopies);
        _state = _state with { Options = _state.Options with { Copies = copies } };
        Reflect(announceNow: true);
        ScheduleRefresh();
    }

    private void OnScalePercentChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating > 0)
        {
            return;
        }

        Touch();
        var percent = double.IsNaN(args.NewValue) ? 100 : Math.Clamp(Math.Round(args.NewValue), PrintOptions.MinCustomPercent, PrintOptions.MaxCustomPercent);
        _state = _state with { Options = _state.Options with { CustomPercent = percent } };
        Reflect(announceNow: true);
        ScheduleRefresh();
    }

    private void OnScopeChecked(object sender, RoutedEventArgs e)
    {
        if (_updating > 0)
        {
            return;
        }

        Touch();
        var scope = ReferenceEquals(sender, PagesCurrent) ? PrintScope.CurrentPage : ReferenceEquals(sender, PagesRange) ? PrintScope.Range : PrintScope.AllPages;
        _state = _state with { Scope = scope };
        _sheet = 0;
        Reflect(announceNow: true);
        ScheduleRefresh();
    }

    private void OnRangeFocused(object sender, RoutedEventArgs e)
    {
        // Typing in the range box selects the range option (design 11-print section 4.2); reaching it with the keyboard does too, so Enter then prints that range.
        if (_state.Scope != PrintScope.Range && _updating == 0 && RangeBox.Text.Length > 0)
        {
            PagesRange.IsChecked = true;
        }
    }

    private void OnRangeChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating > 0)
        {
            return;
        }

        Touch();
        _state = _state with { RangeText = RangeBox.Text, Scope = PrintScope.Range };
        Quietly(() => PagesRange.IsChecked = true);
        _sheet = 0;
        Reflect(announceNow: false);
        ScheduleRefresh();
    }

    // One handler for the combo boxes and check boxes that read straight into options.
    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updating > 0)
        {
            return;
        }

        Touch();
        var o = _state.Options;
        var wasPerSheet = o.PagesPerSheet;
        var subset = Chosen<PageSubset>(SubsetBox) ?? _state.Subset;
        o = o with
        {
            Collate = CollateBox.IsChecked == true,
            Duplex = _state.Abilities.Duplex ? Chosen<PrintDuplex>(SidesBox) ?? o.Duplex : PrintDuplex.None,
            Orientation = Chosen<PaperOrientation>(OrientationBox) ?? o.Orientation,
            AutoRotate = AutoRotateBox.IsChecked == true,
            Scaling = Chosen<PrintScaling>(ScaleBox) ?? o.Scaling,
            PagesPerSheet = Chosen<int>(PerSheetBox) ?? o.PagesPerSheet,
            SheetOrder = Chosen<SheetOrder>(SheetOrderBox) ?? o.SheetOrder,
            Grayscale = Chosen<bool>(ColourBox) ?? o.Grayscale,
            Annotations = AnnotationsBox.IsChecked == true,
            FormFields = FormsBox.IsChecked == true,
            AsImage = AsImageBox.IsChecked == true,
            PaperSizeId = PaperBox.SelectedItem is ComboBoxItem { Tag: int paper } ? paper : o.PaperSizeId,
        };
        _state = _state with { Options = o, Subset = subset };
        if (o.PagesPerSheet != wasPerSheet || ReferenceEquals(sender, SubsetBox))
        {
            _sheet = 0;
        }

        Reflect(announceNow: true);
        ScheduleRefresh();
    }

    // The Windows Settings page for printers, opened by the shell (an ms-settings address is a page of Settings, not a web address).
    private void OnOpenPrinterSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            using var settings = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:printers") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Printer settings: " + ex.GetType().Name);
        }
    }

    private async void OnPreferences(object sender, RoutedEventArgs e)
    {
        var printer = _state.Printer;
        if (printer.Length == 0)
        {
            return;
        }

        DevMode? result;
        try
        {
            var current = _devMode ?? await Task.Run(() => DevMode.GetDefault(printer));
            _devModeAtDialog = current;
            // The driver's own dialog: a Win32 modal over this window; it returns when it is closed. Nothing is saved as the printer's defaults.
            result = DevMode.ShowPreferences(_context.WindowHandle, printer, current);
        }
        catch (PrintDeviceException)
        {
            DeviceNotice.Message = Strings.Get("Print_Preferences_Failed");
            DeviceNotice.IsOpen = true;
            return;
        }

        if (result is null)
        {
            return; // cancelled
        }

        // What was changed there is read back into this sheet (design 11-print section 4.7); the rest (tray, quality) rides along in the DEVMODE to the job.
        var before = _devModeAtDialog!;
        _devMode = result;
        var o = _state.Options;
        if (_state.Abilities.Duplex && result.Has(DevMode.FieldDuplex) && result.Duplex != before.Duplex)
        {
            o = o with { Duplex = result.Duplex switch { DevMode.DuplexLongEdge => PrintDuplex.LongEdge, DevMode.DuplexShortEdge => PrintDuplex.ShortEdge, _ => PrintDuplex.None } };
        }

        if (result.Has(DevMode.FieldPaperSize) && result.PaperSize != before.PaperSize && _state.Abilities.Papers.Any(p => p.Id == result.PaperSize))
        {
            o = o with { PaperSizeId = result.PaperSize };
        }

        if (result.Has(DevMode.FieldColor) && result.ColorMode != before.ColorMode)
        {
            o = o with { Grayscale = result.ColorMode == DevMode.Monochrome };
        }

        if (result.Has(DevMode.FieldOrientation) && result.Orientation != before.Orientation)
        {
            o = o with { Orientation = result.Orientation == DevMode.Landscape ? PaperOrientation.Landscape : PaperOrientation.Portrait };
        }

        if (result.Has(DevMode.FieldCopies) && result.Copies != before.Copies)
        {
            o = o with { Copies = Math.Clamp((int)result.Copies, PrintOptions.MinCopies, PrintOptions.MaxCopies) };
        }

        _touched = true;
        _state = _state with { Options = o };
        Load(_state);
        ScheduleRefresh();
    }

    private void OnPrevSheet(object sender, RoutedEventArgs e) => MoveSheet(-1);

    private void OnNextSheet(object sender, RoutedEventArgs e) => MoveSheet(+1);

    private void MoveSheet(int delta)
    {
        var sheets = Sheets();
        var next = PrintSheetModel.ClampSheet(_sheet + delta, sheets);
        if (next == _sheet)
        {
            return;
        }

        _sheet = next;
        Reflect(announceNow: true);
        ScheduleRefresh(immediately: true);
    }

    private int Sheets() => !_eval.HasPages ? 0 : _eval.Sides;

    // ---- what the controls say about each other and about the choices ------------------------------------------------------------------------------

    private void Reflect(bool announceNow)
    {
        _eval = PrintSheetModel.Evaluate(_state, _context.PageCount, _context.CurrentPage);
        var o = _state.Options;
        var duplexKnown = _printersKnown && _probe.Answered;
        SidesBox.IsEnabled = _state.Abilities.Duplex;
        SidesNote.Visibility = duplexKnown && !_state.Abilities.Duplex ? Visibility.Visible : Visibility.Collapsed;
        SidesNote.Text = Strings.Get("Print_Sides_Unavailable");
        AutomationProperties.SetHelpText(SidesBox, duplexKnown && !_state.Abilities.Duplex ? Strings.Get("Print_Sides_Unavailable") : string.Empty);
        ColourBox.IsEnabled = _state.Abilities.Colour;
        ColourNote.Visibility = duplexKnown && !_state.Abilities.Colour ? Visibility.Visible : Visibility.Collapsed;
        ColourNote.Text = Strings.Get("Print_Colour_Unavailable");
        ScalePercentBox.Visibility = o.Scaling == PrintScaling.Custom && o.PagesPerSheet == 1 ? Visibility.Visible : Visibility.Collapsed;
        ScaleBox.IsEnabled = o.PagesPerSheet == 1;
        AutoRotateBox.IsEnabled = o.PagesPerSheet == 1;
        SheetOrderBox.IsEnabled = o.PagesPerSheet > 1;
        CollateBox.IsEnabled = o.Copies > 1;
        PaperBox.IsEnabled = PaperBox.Items.Count > 0;

        var sheets = Sheets();
        _sheet = PrintSheetModel.ClampSheet(_sheet, sheets);
        PrevButton.IsEnabled = _sheet > 0;
        NextButton.IsEnabled = _sheet < sheets - 1;
        PagerText.Text = sheets == 0 ? string.Empty : Strings.Format(o.PagesPerSheet > 1 ? "Print_Pager_Sheet" : "Print_Pager_Page", _sheet + 1, sheets);
        PagerText.Visibility = sheets > 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = sheets > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = sheets > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (announceNow)
        {
            _announceTimer.Stop();
            ShowAnnouncedTexts();
        }
        else
        {
            _announceTimer.Stop();
            _announceTimer.Start(); // typing in the range box: the summary and any error follow 500 ms after the last key
        }

        CanPrintChanged?.Invoke();
    }

    // The summary (polite) and the range error (assertive) are live regions: they change once, after the typing pauses.
    private void ShowAnnouncedTexts()
    {
        _announceTimer.Stop();
        var eval = _eval;
        if (eval.HasPages)
        {
            SummaryText.Text = Strings.Format(eval.SummaryKey!, eval.PageCount, eval.Sheets);
            CopiesSummaryText.Text = eval.CopiesKey is null ? string.Empty : Strings.Format(eval.CopiesKey, _state.Options.Copies);
            CopiesSummaryText.Visibility = eval.CopiesKey is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            SummaryText.Text = string.Empty;
            CopiesSummaryText.Visibility = Visibility.Collapsed;
        }

        var error = eval is { HasPages: false, ErrorIsLive: true, RangeErrorKey: { } key } ? Strings.Format(key, eval.RangeErrorLimit) : string.Empty;
        if (error != _announcedError)
        {
            _announcedError = error;
            RangeError.Text = error.Length == 0 ? string.Empty : Strings.Format("Print_Range_Invalid_Announce", error);
            RangeError.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(RangeBox, error.Length == 0 ? Strings.Get("Print_Range_Hint") : error);
        }
    }

    /// <summary>Print was pressed while the choices do not allow it: say why where the person is looking.</summary>
    public void ShowWhyNot()
    {
        ShowAnnouncedTexts();
        if (!_eval.HasPages && _state.Scope == PrintScope.Range)
        {
            RangeBox.Focus(FocusState.Programmatic);
        }
    }

    // ---- the preview and the notices -----------------------------------------------------------------------------------------------------------

    private void ScheduleRefresh(bool immediately = false)
    {
        _refreshTimer.Stop();
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (immediately)
        {
            StartRefresh();
        }
        else
        {
            _refreshTimer.Start();
        }
    }

    private void StartRefresh()
    {
        _refreshTimer.Stop();
        _refresh?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refresh = source;
        var previous = _refreshTask;
        _refreshTask = RefreshAsync(previous, source.Token);
    }

    private async Task RefreshAsync(Task previous, CancellationToken token)
    {
        try
        {
            await previous.ConfigureAwait(true); // one render at a time on the session
        }
        catch (Exception e) when (e is OperationCanceledException)
        {
            // the one before was cancelled: that is why this runs
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var state = _state;
        var eval = _eval;
        var sheet = _sheet;
        if (!eval.HasPages)
        {
            ShowPreviewMessage(Strings.Get("Print_Preview_None"), busy: false);
            ClearNotices();
            return;
        }

        if (state.Printer.Length == 0)
        {
            ShowPreviewMessage(Strings.Get(_printersEmpty ? "Print_Printer_None" : "Print_Printer_Looking"), busy: !_printersEmpty);
            return;
        }

        var session = _session;
        if (session is null)
        {
            if (_sessionTask is { IsCompleted: true })
            {
                ShowPreviewMessage(Strings.Get("Print_Preview_Failed"), busy: false);
            }
            else
            {
                ShowPreviewMessage(Strings.Get("Print_Preview_Loading"), busy: true);
            }

            return;
        }

        ShowPreviewMessage(Strings.Get("Print_Preview_Loading"), busy: true, keepImage: PreviewPicture.Source is not null);
        try
        {
            var options = PrintSheetModel.OptionsForJob(state);
            var request = new PrintJobRequest
            {
                PrinterName = state.Printer,
                DocumentName = _context.DocumentName,
                Pages = eval.Selection.Pages,
                Options = options,
                PrintingAllowedByPolicy = _context.PrintingAllowed,
                PrinterDevMode = _devMode,
            };
            var preflight = await PrintJob.PreflightAsync(request, session.Source, GdiPrintDeviceFactory.Instance, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            ApplyPreflight(preflight, state);
            if (preflight.EngineStatus != Status.Ok || preflight.Paper.WidthPx < 1)
            {
                ShowPreviewMessage(Strings.Get("Print_Preview_Failed"), busy: false);
                return;
            }

            var pages = PrintSheetModel.PagesOnSheet(eval.Selection.Pages, options.PagesPerSheet, sheet);
            if (pages.Count == 0)
            {
                ShowPreviewMessage(Strings.Get("Print_Preview_None"), busy: false);
                return;
            }


            var image = await PrintPreview.RenderAsync(session.Source, new PreviewRequest(options, pages, preflight.Paper, preflight.Device?.Physical ?? default), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            ShowPreview(image, state, pages);
        }
        catch (OperationCanceledException)
        {
            // a newer refresh is on its way
        }
        catch (Exception e) when (e is PrintDeviceException or InvalidOperationException or IOException)
        {
            System.Diagnostics.Debug.WriteLine("Print preview: " + e.GetType().Name);
            ShowPreviewMessage(Strings.Get("Print_Preview_Failed"), busy: false);
        }
    }

    private void ClearNotices()
    {
        CutOffNotice.IsOpen = false;
        LowQualityNotice.IsOpen = false;
    }

    private void ApplyPreflight(PrintPreflight preflight, PrintSheetState state)
    {
        CutOffNotice.IsOpen = preflight.CutOffPages.Count > 0;
        LowQualityNotice.IsOpen = preflight.Eligibility.LowQualityOnly;
        if (preflight.Device is { } report)
        {
            var notes = new List<string>();
            if (!report.DuplexApplied)
            {
                notes.Add(Strings.Get("Print_NotApplied_Duplex"));
            }

            if (!report.PaperApplied)
            {
                notes.Add(Strings.Get("Print_NotApplied_Paper"));
            }

            DeviceNotice.IsOpen = notes.Count > 0 || !_probe.Answered;
            DeviceNotice.Message = notes.Count > 0 ? string.Join(' ', notes) : Strings.Get("Print_Offline_Notice");
        }
        else if (preflight.EngineStatus == Status.Ok)
        {
            DeviceNotice.IsOpen = false;
        }
    }

    private void ShowPreviewMessage(string text, bool busy, bool keepImage = false)
    {
        PreviewStatus.Text = text;
        PreviewStatus.Visibility = busy && keepImage ? Visibility.Collapsed : Visibility.Visible;
        PreviewRing.IsActive = busy;
        if (!keepImage)
        {
            PreviewPicture.Source = null;
            PreviewPaperEdge.BorderThickness = new Thickness(0);
            AutomationProperties.SetName(PreviewPicture, text);
        }
    }

    private void ShowPreview(PreviewImage image, PrintSheetState state, IReadOnlyList<int> pages)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Bgra, 0, image.Bgra.Length);
        }

        bitmap.Invalidate();
        PreviewPicture.Source = bitmap;
        PreviewPaperEdge.BorderThickness = new Thickness(1);
        PreviewStatus.Visibility = image.AnyFailed ? Visibility.Visible : Visibility.Collapsed;
        PreviewStatus.Text = image.AnyFailed ? Strings.Get("Print_Preview_Failed") : string.Empty;
        PreviewRing.IsActive = false;
        AutomationProperties.SetName(PreviewPicture, Describe(image, state, pages));
    }

    // "Preview of page 3. Portrait, A4, scaled to fit." (design 11-print section 6): a screen-reader user learns the scale and the orientation without seeing the picture.
    private string Describe(PreviewImage image, PrintSheetState state, IReadOnlyList<int> pages)
    {
        var o = state.Options;
        var orientation = Strings.Get(image.Landscape ? "Print_Orientation_Landscape" : "Print_Orientation_Portrait");
        var paper = PaperBox.SelectedItem is ComboBoxItem { Content: string name } && name.Length > 0 ? name : Strings.Get("Print_Preview_Paper_Unknown");
        if (o.PagesPerSheet > 1)
        {
            return Strings.Format("Print_Preview_DescriptionSheet", _sheet + 1, pages.Count, orientation + ", " + paper, Strings.Get("Print_Preview_Scale_Cells"));
        }

        var scale = o.Scaling switch
        {
            PrintScaling.Fit => Strings.Get("Print_Preview_Scale_Fit"),
            PrintScaling.Actual => Strings.Get("Print_Preview_Scale_Actual"),
            PrintScaling.Custom => Strings.Format("Print_Preview_Scale_Custom", o.CustomPercent.ToString("0", CultureInfo.CurrentCulture)),
            _ => Strings.Get("Print_Preview_Scale_Shrink"),
        };
        return Strings.Format("Print_Preview_Description", pages[0], orientation, paper, scale);
    }

    // ---- leaving -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The sheet is closing. With <paramref name="accepted"/> the choices are returned, with the session the sheet opened (the caller owns it from here); otherwise nothing is returned and the
    /// session is disposed. Either way the preview stops.
    /// </summary>
    public async Task<PrintSheetChoice?> DetachAsync(bool accepted)
    {
        _detached = true;
        _refreshTimer.Stop();
        _announceTimer.Stop();
        _refresh?.Cancel();
        var wanted = accepted && CanPrint;
        try
        {
            await _refreshTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            // the render was told to stop; the session is not shared with anything else
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Print preview ended with " + e.GetType().Name);
        }

        // A session that is still starting is waited for when the job needs it, and stopped when it does not.
        if (!wanted)
        {
            await _lifetime.CancelAsync().ConfigureAwait(true);
        }

        var session = _session;
        _session = null;
        if (_sessionTask is { } pending && session is null)
        {
            try
            {
                var result = await pending.ConfigureAwait(true);
                session = result.Value;
            }
            catch (OperationCanceledException)
            {
                // never started
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(true);
        if (!wanted)
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(true);
            }

            return null;
        }

        return new PrintSheetChoice(
            _state.Printer,
            PrintSheetModel.OptionsForJob(_state),
            _eval.Selection.Pages,
            _state.Scope,
            _state.RangeText,
            _state.Subset,
            _devMode,
            session,
            _sessionForImage,
            LowQuality: LowQualityNotice.IsOpen);
    }
}
