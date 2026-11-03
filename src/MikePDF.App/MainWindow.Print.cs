using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.App.Views;
using MikePDF.Core;
using MikePDF.Core.Printing;
using MikePDF.Printing;
using MikePDF.Protocol;
using WinRT.Interop;

namespace MikePDF.App;

/// <summary>
/// Printing from the window (docs/features/print.md, design 11-print): the print sheet, the job it starts, and the strip that shows the job. The job runs on the broker with GDI (ADR-0011) on a session
/// of its own, so the document stays readable while it prints. One job at a time per window.
/// </summary>
public sealed partial class MainWindow
{
    private const string PdfQueue = "Microsoft Print to PDF";
    private static readonly TimeSpan DoneStripTime = TimeSpan.FromSeconds(4);

    private PrintRun? _print;
    private DispatcherQueueTimer? _printStripTimer;
    private static int _testJobs;

    /// <summary>One print job started from this window: what it is printing, how to stop it, and what it did.</summary>
    private sealed class PrintRun(string printer, string documentName, CancellationTokenSource cancel)
    {
        public string Printer { get; } = printer;

        public string DocumentName { get; } = documentName;

        public CancellationTokenSource Cancel { get; } = cancel;

        public bool Running { get; set; } = true;

        public int SheetsSeen { get; set; }

        public int PagesPerSheet { get; set; } = 1;
    }

    /// <summary>A print job is running in this window (the Print command waits, Cancel printing can act, closing the window asks first).</summary>
    private bool PrintJobRunning => _print is { Running: true };

    private void RegisterPrintActions(Action<string, Action> sync, Action<string, Func<Task>> async)
    {
        async("file.print", () => ShowPrintSheetAsync(null));
        async("file.printCurrentPage", () => ShowPrintSheetAsync(PrintScope.CurrentPage));
        sync("print.cancel", CancelPrinting);
    }

    // ---- the sheet -----------------------------------------------------------------------------------------------------------------------------

    private async Task ShowPrintSheetAsync(PrintScope? scope, PrintSheetState? resume = null)
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        var name = session.Title.Length > 0 ? session.Title : System.IO.Path.GetFileNameWithoutExtension(session.Path);
        var current = Math.Max(0, session.Controller.CurrentPage) + 1;
        var context = new PrintSheetContext(
            name,
            session.Controller.PageCount,
            Math.Min(current, Math.Max(1, session.Controller.PageCount)),
            WindowNative.GetWindowHandle(this),
            _services.Settings,
            scope,
            _services.Policy.Current.PrintingAllowed,
            session.GetPrintDefaultsAsync,
            (options, token) => session.OpenPrintSessionAsync(options, _services.Policy.Current.PrintingAllowed, token),
            resume);

        var view = new PrintSheetView(context);
        var bounds = Root.XamlRoot?.Size ?? new Windows.Foundation.Size(1100, 820);
        view.Width = Math.Min(900, Math.Max(320, bounds.Width - 96));
        view.Height = Math.Clamp(bounds.Height - 240, 300, 580);

        var dialog = NewDialog();
        dialog.Title = Strings.Get("Print_Title");
        dialog.Content = view;
        dialog.PrimaryButtonText = Strings.Get("Print_Button_Print");
        dialog.CloseButtonText = Strings.Get("Print_Button_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.IsPrimaryButtonEnabled = view.CanPrint;
        dialog.FlowDirection = Root.FlowDirection; // a dialog is a popup and does not inherit the window's direction (the pseudo-RTL journeys mirror the sheet)
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        dialog.Resources["ContentDialogMinWidth"] = 320.0;
        AutomationProperties.SetAutomationId(dialog, "PrintDialog");
        view.CanPrintChanged += () => dialog.IsPrimaryButtonEnabled = view.CanPrint;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!view.CanPrint)
            {
                args.Cancel = true;
                view.ShowWhyNot();
            }
        };
        dialog.Opened += (_, _) => view.FocusFirst();

        var result = await ShowDialogAsync(dialog);
        var choice = await view.DetachAsync(result == ContentDialogResult.Primary);
        view.Dispose();
        Pages.FocusPages();
        if (choice is not null)
        {
            await StartPrintAsync(choice, name, session);
        }
    }

    // ---- the job ---------------------------------------------------------------------------------------------------------------------------------

    private async Task StartPrintAsync(PrintSheetChoice choice, string documentName, DocumentSession document)
    {
        PrintPreferences.Save(_services.Settings, choice.Printer, choice.Options);

        var cancel = new CancellationTokenSource();
        var run = new PrintRun(choice.Printer, documentName, cancel) { PagesPerSheet = choice.Options.PagesPerSheet };
        _print = run;
        RefreshCommands();
        ShowPrintStrip(run);

        PrintSession? printSession = choice.Session;
        PrintResult? result = null;
        try
        {
            if (printSession is not null && choice.SessionForImage != choice.Options.AsImage)
            {
                await printSession.DisposeAsync(); // print as image needs a ring of whole pages; the one the sheet opened has bands
                printSession = null;
            }

            if (printSession is null)
            {
                var opened = await document.OpenPrintSessionAsync(choice.Options, _services.Policy.Current.PrintingAllowed, cancel.Token);
                printSession = opened.Value;
                if (!opened.IsOk || printSession is null)
                {
                    FinishPrintStrip(run, null, PrintFailureKind.EngineFailed, opened.Status == Status.PermissionDenied, choice, documentName, document);
                    return;
                }
            }

            var request = new PrintJobRequest
            {
                PrinterName = choice.Printer,
                DocumentName = documentName,
                Pages = choice.Pages,
                Options = choice.Options,
                PrintingAllowedByPolicy = _services.Policy.Current.PrintingAllowed,
                OutputPath = TestOutputPath(choice.Printer),
                PrinterDevMode = choice.DevMode,
            };
            var progress = new Progress<PrintProgress>(value => OnPrintProgress(run, value));
            result = await PrintJob.RunAsync(request, printSession.Source, GdiPrintDeviceFactory.Instance, progress, cancel.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Print failed: " + e.GetType().Name);
            FinishPrintStrip(run, null, PrintFailureKind.Unknown, disabled: false, choice, documentName, document);
            return;
        }
        finally
        {
            if (printSession is not null)
            {
                await printSession.DisposeAsync();
            }

            run.Running = false;
            cancel.Dispose();
            RefreshCommands();
        }

        FinishPrintStrip(run, result, PrintFailureKind.Unknown, disabled: false, choice, documentName, document);
    }

    // The folder a journey names in the test seam: a job for Microsoft Print to PDF is written there, so the queue does not ask for a file name with a dialog of its own.
    private static string? TestOutputPath(string printer)
    {
        var folder = TestSeams.Get(TestSeamGate.PrintOutput);
        if (folder is null || !string.Equals(printer, PdfQueue, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, "print-" + Interlocked.Increment(ref _testJobs).ToString(CultureInfo.InvariantCulture) + ".pdf");
    }

    private void CancelPrinting()
    {
        if (_print is { Running: true } run)
        {
            run.Cancel.Cancel(); // the job aborts the spool job; the strip shows where it stopped when the job reports back
        }
    }

    // ---- the strip ---------------------------------------------------------------------------------------------------------------------------------

    private void ShowPrintStrip(PrintRun run)
    {
        _printStripTimer?.Stop();
        PrintStrip.Severity = InfoBarSeverity.Informational;
        PrintStrip.Title = Strings.Format("Print_Progress_Name", run.DocumentName);
        PrintStrip.IsClosable = false;
        PrintBar.IsIndeterminate = true;
        PrintBar.Value = 0;
        PrintBar.Maximum = 1;
        PrintBar.Visibility = Visibility.Visible;
        AutomationProperties.SetName(PrintBar, Strings.Format("Print_Progress_Name", run.DocumentName));
        PrintStatus.Text = Strings.Get("Print_Progress_Preparing"); // the live line says something from the first moment, so the element is in the accessibility tree before the first page is counted
        PrintDetail.Visibility = Visibility.Collapsed;
        PrintActions.Children.Clear();
        PrintActions.Visibility = Visibility.Collapsed;
        PrintCancelButton.Visibility = Visibility.Visible;
        PrintStrip.IsOpen = true;
    }

    private void OnPrintProgress(PrintRun run, PrintProgress value)
    {
        if (!ReferenceEquals(run, _print) || !run.Running)
        {
            return;
        }

        if (value.Stage == PrintStage.Finishing)
        {
            // The spooler is converting the job; there is no page to count. Not a live region: the last page was announced, and the end is.
            PrintBar.IsIndeterminate = true;
            PrintDetail.Text = Strings.Format("Print_Progress_Finishing", run.Printer);
            PrintDetail.Visibility = Visibility.Visible;
            return;
        }

        run.SheetsSeen = value.Sheet;
        PrintBar.IsIndeterminate = false;
        PrintBar.Maximum = Math.Max(1, value.TotalSheets);
        PrintBar.Value = Math.Max(0, value.Sheet - 1);
        PrintDetail.Visibility = Visibility.Collapsed;
        var sheetWise = run.PagesPerSheet > 1;
        PrintStatus.Text = Strings.Format(sheetWise ? "Print_Progress_Sheet" : "Print_Progress_Page", value.Sheet, value.TotalSheets);
        AnnouncePrint(PrintStatus.Text);
    }

    private void FinishPrintStrip(PrintRun run, PrintResult? result, PrintFailureKind kind, bool disabled, PrintSheetChoice? choice = null, string? documentName = null, DocumentSession? document = null)
    {
        PrintBar.Visibility = Visibility.Collapsed;
        PrintDetail.Visibility = Visibility.Collapsed;
        PrintCancelButton.Visibility = Visibility.Collapsed;
        PrintStrip.IsClosable = true;
        PrintActions.Children.Clear();
        PrintActions.Visibility = Visibility.Collapsed;
        var linger = DoneStripTime;

        if (result is { Outcome: PrintOutcome.Completed })
        {
            PrintStrip.Severity = result.PagesWithMissingContent.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            PrintStrip.Title = Strings.Format("Print_Progress_Name", run.DocumentName);
            PrintStatus.Text = result.PagesWithMissingContent.Count > 0
                ? Strings.Format("Print_Missing_Content_Image", PageList(result.PagesWithMissingContent))
                : Strings.Format("Print_Done", run.Printer);
            if (result.PagesWithMissingContent.Count > 0)
            {
                linger = TimeSpan.FromSeconds(15);
            }
        }
        else if (result is { Outcome: PrintOutcome.Cancelled })
        {
            PrintStrip.Severity = InfoBarSeverity.Warning;
            PrintStrip.Title = Strings.Format("Print_Progress_Name", run.DocumentName);
            var done = Math.Max(0, result.SheetsPrinted);
            PrintStatus.Text = done == 0 ? Strings.Get("Print_Stopped_Early") : Strings.Format(run.PagesPerSheet > 1 ? "Print_StoppedSheet" : "Print_Stopped", done);
        }
        else
        {
            // A failure, or a refusal: the sentence says what to do, and the two things to do are one click away.
            var failure = disabled || result is { Outcome: PrintOutcome.Disabled }
                ? Strings.Get("Print_Error_Disabled")
                : Strings.Format(PrintFailure.ResourceKey(result is null ? kind : PrintFailure.Classify(result.Win32Error, result.EngineStatus)), run.Printer);
            var declined = result is not null && PrintFailure.Classify(result.Win32Error, result.EngineStatus) == PrintFailureKind.UserDeclined;
            PrintStrip.Severity = declined ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
            PrintStrip.Title = declined ? Strings.Format("Print_Progress_Name", run.DocumentName) : Strings.Format("Print_Failed_Title", run.DocumentName);
            PrintStatus.Text = failure;
            if (!declined && result?.Detail is { Length: > 0 } detail)
            {
                PrintDetail.Text = Strings.Format("Print_Error_Details", detail); // what the printer or the engine said, for whoever is asked to help
                PrintDetail.Visibility = Visibility.Visible;
            }

            if (!declined && choice is not null && documentName is not null && document is not null)
            {
                linger = TimeSpan.FromMinutes(5);
                AddStripButton("Print_TryAgain", "PrintTryAgain", () => _ = StartPrintAsync(choice with { Session = null }, documentName, document));
                AddStripButton("Print_ChooseOther", "PrintChooseOther", () => _ = ChooseAnotherPrinterAsync(choice));
            }
            else if (!declined)
            {
                linger = TimeSpan.FromMinutes(5);
            }
        }

        AnnouncePrint(PrintStatus.Text); // once at the end: sent, stopped, or why not

        _printStripTimer ??= DispatcherQueue.CreateTimer();
        _printStripTimer.IsRepeating = false;
        _printStripTimer.Interval = linger;
        _printStripTimer.Stop();
        _printStripTimer.Tick -= OnPrintStripTimer;
        _printStripTimer.Tick += OnPrintStripTimer;
        _printStripTimer.Start();
    }

    // A UIA notification from an element that is always there (the way tab moves are announced): once for each page counted and once at the end.
    private void AnnouncePrint(string message)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(PrintAnnouncement)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(PrintAnnouncement);
        peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other, Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent, message, "PrintAnnouncement");
    }

    private void OnPrintStripTimer(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!PrintJobRunning)
        {
            PrintStrip.IsOpen = false;
        }
    }

    private void AddStripButton(string textKey, string automationId, Action click)
    {
        var button = new Button { Content = Strings.Get(textKey) };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) =>
        {
            PrintStrip.IsOpen = false;
            click();
        };
        PrintActions.Children.Add(button);
        PrintActions.Visibility = Visibility.Visible;
    }

    private async Task ChooseAnotherPrinterAsync(PrintSheetChoice choice)
    {
        // The options are already the last used; the pages the person chose come back too, and the printer that failed is not chosen again.
        var state = new PrintSheetState { Printer = string.Empty, Scope = choice.Scope, RangeText = choice.RangeText, Subset = choice.Subset, Options = choice.Options };
        if (_services.Commands.Find("file.print")?.CanExecute(BuildContext()) is { IsEnabled: true } && _running.Add("file.print"))
        {
            try
            {
                await ShowPrintSheetAsync(null, state);
            }
            finally
            {
                _running.Remove("file.print");
                RefreshCommands();
            }
        }
    }

    private static string PageList(IReadOnlyList<int> pages)
    {
        const int Shown = 12;
        var text = string.Join(", ", pages.Take(Shown).Select(p => p.ToString(CultureInfo.CurrentCulture)));
        return pages.Count > Shown ? text + "..." : text;
    }

    private void OnPrintStripKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && PrintJobRunning)
        {
            e.Handled = true;
            _ = RunCommandAsync("print.cancel");
        }
    }

    // ---- closing the window while a job runs -----------------------------------------------------------------------------------------------------

    /// <summary>The window is closing with a job running: ask, and stop the job if the person says so. True when the window may close now.</summary>
    private async Task<bool> ConfirmClosingWhilePrintingAsync()
    {
        if (!PrintJobRunning)
        {
            return true;
        }

        var dialog = NewDialog();
        dialog.Title = Strings.Get("Print_Close_Title");
        dialog.Content = new TextBlock { Text = Strings.Get("Print_Close_Body"), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Print_Close_Stop");
        dialog.CloseButtonText = Strings.Get("Print_Close_Keep");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "PrintCloseDialog");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        if (_print is { Running: true } run)
        {
            run.Cancel.Cancel();
            for (var wait = 0; wait < 100 && run.Running; wait++)
            {
                await Task.Delay(50); // the job winds down in well under a second (PRINT-11)
            }
        }

        return true;
    }
}
