using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core;
using MikePDF.Core.Layout;
using MikePDF.Core.Windowing;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MikePDF.App;

/// <summary>
/// The page view modes of docs/features/page-view-modes.md: single page, two pages, two pages with a cover, continuous or one at a time, the reading direction, and presentation
/// (the whole screen, one page, the room's dark round it). The modes are commands of the registry; this is where their actions are, and where the View menu is kept in step.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan HintTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HintTimeAtEntry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypedPageTime = TimeSpan.FromMilliseconds(1500);

    private enum Blank
    {
        None,
        Black,
        White,
    }

    private bool _presenting;
    private bool _presentationFullScreenBefore;
    private Blank _blank;
    private string _typedPage = string.Empty;
    private string _presentationDisplay = string.Empty;
    private string _surfaceName = string.Empty;
    private string _surfaceId = string.Empty;
    private bool _pointerOverHint;
    private bool _awake;
    private bool _suppressTurnAnnouncement;
    private DocumentSession? _announcedSession;
    private int _announcedFirst = -1;
    private int _announcedLast = -1;
    private DispatcherQueueTimer? _hintTimer;
    private DispatcherQueueTimer? _typedPageTimer;

    private void InitializeViews()
    {
        PresentationExit.Content = Strings.Get("Present_Exit");
        AutomationProperties.SetName(PresentationExit, Strings.Get("Present_Exit_Name"));
        AutomationProperties.SetName(PresentationHint, Strings.Get("Present_Hint_Name"));
        PresentationEscText.Text = Strings.Get("Present_Hint");
        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.IsRepeating = false;
        _hintTimer.Tick += (_, _) => OnHintTick();
        _typedPageTimer = DispatcherQueue.CreateTimer();
        _typedPageTimer.IsRepeating = false;
        _typedPageTimer.Interval = TypedPageTime;
        _typedPageTimer.Tick += (_, _) =>
        {
            _typedPageTimer.Stop();
            PresentationGoTo.Visibility = Visibility.Collapsed;
        };
        Pages.PointerMoved += (_, _) =>
        {
            if (_presenting)
            {
                ShowPresentationHint(showEscape: false);
            }
        };
        NativeWindowing.WatchDisplayChange(WindowNative.GetWindowHandle(this), OnDisplayChange);
    }

    // ---- the actions of the commands -------------------------------------------------------------------------------------------------------

    private void RegisterViewActions(Action<string, Action> sync)
    {
        sync("view.layoutSingle", () => ChangeMode(m => m.With(PageArrangement.OnePage)));
        sync("view.layoutTwo", () => ChangeMode(m => m.With(PageArrangement.Facing)));
        sync("view.layoutTwoCover", () => ChangeMode(m => m.With(PageArrangement.FacingWithCover)));
        sync("view.continuous", () => ChangeMode(m => m.With(m.IsPaged ? PageFlow.Continuous : PageFlow.Paged)));
        sync("view.coverPage", () => ChangeMode(m => m.Arrangement switch
        {
            PageArrangement.Facing => m.With(PageArrangement.FacingWithCover),
            PageArrangement.FacingWithCover => m.With(PageArrangement.Facing),
            _ => m,
        }));
        sync("view.directionAuto", () => ChangeMode(m => m.With(ReadingDirection.Auto)));
        sync("view.directionLtr", () => ChangeMode(m => m.With(ReadingDirection.LeftToRight)));
        sync("view.directionRtl", () => ChangeMode(m => m.With(ReadingDirection.RightToLeft)));
        sync("view.presentation", TogglePresentation);
        sync("present.next", () => PresentationTurn(+1));
        sync("present.previous", () => PresentationTurn(-1));
        sync("present.black", () => ToggleBlank(Blank.Black));
        sync("present.white", () => ToggleBlank(Blank.White));
    }

    private void ChangeMode(Func<ViewMode, ViewMode> change)
    {
        if (_session is not { IsOpen: true } session || _presenting)
        {
            return;
        }

        var before = session.Mode;
        var after = change(before);
        if (after == before)
        {
            SyncViewMenu();
            return;
        }

        _suppressTurnAnnouncement = true;
        try
        {
            session.SetMode(after);
        }
        finally
        {
            _suppressTurnAnnouncement = false;
        }

        _announcedSession = session;
        (_announcedFirst, _announcedLast) = session.Controller.CurrentPages;
        SyncViewMenu();
        AnnounceView(Strings.Format("View_Announce_Mode", Describe(after), PagesText(session)));
        _app.RequestSessionWrite();
        Pages.FocusPages();
    }

    // "Two pages with cover, continuous" (docs/features/page-view-modes.md 5).
    private static string Describe(ViewMode mode) => Strings.Format(
        "View_Layout_Description",
        Strings.Get(mode.Arrangement switch
        {
            PageArrangement.Facing => "View_Facing",
            PageArrangement.FacingWithCover => "View_FacingCover",
            _ => "View_Single",
        }),
        Strings.Get(mode.IsPaged ? "View_Flow_Paged" : "View_Flow_Continuous"));

    private static string PagesText(DocumentSession session)
    {
        var (first, last) = session.Controller.CurrentPages;
        var total = session.Controller.PageCount;
        return last > first ? Strings.Format("View_Announce_Pages_Two", first + 1, last + 1, total) : Strings.Format("View_Announce_Pages_One", first + 1, total);
    }

    /// <summary>Puts the View menu's checks, its button's help text and the page surface's description in step with the shown document's mode.</summary>
    private void SyncViewMenu()
    {
        var session = _session is { IsOpen: true } open ? open : null;
        var mode = session?.Mode ?? ViewMode.Default;
        LayoutSingleItem.IsChecked = mode.Arrangement == PageArrangement.OnePage;
        LayoutTwoItem.IsChecked = mode.Arrangement == PageArrangement.Facing;
        LayoutTwoCoverItem.IsChecked = mode.Arrangement == PageArrangement.FacingWithCover;
        LayoutContinuousItem.IsChecked = !mode.IsPaged;
        LayoutCoverPageItem.IsChecked = mode.Arrangement == PageArrangement.FacingWithCover;
        DirectionAutoItem.IsChecked = mode.Direction == ReadingDirection.Auto;
        DirectionLtrItem.IsChecked = mode.Direction == ReadingDirection.LeftToRight;
        DirectionRtlItem.IsChecked = mode.Direction == ReadingDirection.RightToLeft;
        ViewButton.IsEnabled = session is not null && !_presenting;
        AutomationProperties.SetHelpText(ViewButton, session is null ? string.Empty : Strings.Format("View_Surface_Status", Describe(mode)));
    }

    // ---- announcements ----------------------------------------------------------------------------------------------------------------------

    /// <summary>A polite message for a screen reader, as a UI Automation notification (one each, never a live region that would say it twice).</summary>
    private void AnnounceView(string message)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(TabAnnouncement) ?? FrameworkElementAutomationPeer.CreatePeerForElement(TabAnnouncement);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, message, "ViewAnnouncement");
    }

    // A page turned in the paged flow, or in a presentation: "Pages 4 and 5 of 40" / "Page 13 of 30". Not when the mode itself changed (that announced its own news).
    private void AnnounceTurnIfChanged(DocumentSession session)
    {
        var (first, last) = session.Controller.CurrentPages;
        if (!ReferenceEquals(_announcedSession, session))
        {
            _announcedSession = session;
            (_announcedFirst, _announcedLast) = (first, last);
            return;
        }

        if (first == _announcedFirst && last == _announcedLast)
        {
            return;
        }

        (_announcedFirst, _announcedLast) = (first, last);
        if (_suppressTurnAnnouncement)
        {
            return;
        }

        if (_presenting)
        {
            AnnounceView(Strings.Format("Present_Turn_Announce", first + 1, session.Controller.PageCount));
        }
        else if (session.Mode.IsPaged)
        {
            AnnounceView(PagesText(session));
        }
    }

    /// <summary>Called by <see cref="UpdateChrome"/>: the view menu, the announcements and the presentation's own page number follow the view.</summary>
    private void SyncViewChrome(DocumentSession session)
    {
        SyncViewMenu();
        AnnounceTurnIfChanged(session);
        if (_presenting)
        {
            var (first, _) = session.Controller.CurrentPages;
            var total = session.Controller.PageCount;
            PresentationPageText.Text = Strings.Format("Present_Page", first + 1, total);
            AutomationProperties.SetName(Pages, Strings.Format("Present_Surface_Name", first + 1, total));
        }
    }

    // ---- turning pages in a presentation ------------------------------------------------------------------------------------------------------

    private void PresentationTurn(int direction)
    {
        if (_presenting && _session is { IsOpen: true } session)
        {
            session.Turn(direction);
        }
    }

    private void ToggleBlank(Blank which) => SetBlank(_blank == which ? Blank.None : which);

    private void SetBlank(Blank state)
    {
        if (!_presenting || state == _blank)
        {
            return;
        }

        _blank = state;
        BlankScreen.Background = new SolidColorBrush(state == Blank.White ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        BlankScreen.Visibility = state == Blank.None ? Visibility.Collapsed : Visibility.Visible;
        AnnounceView(Strings.Get(state switch
        {
            Blank.Black => "Present_Black_Announce",
            Blank.White => "Present_White_Announce",
            _ => "Present_Restored_Announce",
        }));
    }

    // Keys that only a presentation reads: the digits of a page number, a key that brings the page back from a blank screen. Everything else is a command of the registry.
    private bool HandlePresentationKey(KeyRoutedEventArgs e)
    {
        ShowPresentationHint(showEscape: false);
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return false;
        }

        if (!IsKeyDown(VirtualKey.Shift) && DigitOf(e.Key) is { } digit)
        {
            _typedPage = (_typedPage + digit).TrimStart('0');
            if (_typedPage.Length > 7)
            {
                _typedPage = _typedPage[..7];
            }

            ShowTypedPage();
            e.Handled = true;
            return true;
        }

        if (_typedPage.Length > 0 && e.Key == VirtualKey.Enter)
        {
            var page = PresentationRules.PageFromTyped(_typedPage, _session?.Controller.PageCount ?? 0);
            _typedPage = string.Empty;
            if (page is { } index && _session is { IsOpen: true } session)
            {
                if (_blank != Blank.None)
                {
                    SetBlank(Blank.None);
                }

                session.GoToPage(index);
            }

            PresentationGoTo.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return true;
        }

        if (_typedPage.Length > 0 && e.Key == VirtualKey.Back)
        {
            _typedPage = _typedPage[..^1];
            ShowTypedPage();
            e.Handled = true;
            return true;
        }

        if (_blank != Blank.None && IsPresentationNavigationKey(e.Key))
        {
            SetBlank(Blank.None);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private static int? DigitOf(VirtualKey key) => key switch
    {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => key - VirtualKey.Number0,
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => key - VirtualKey.NumberPad0,
        _ => null,
    };

    private static bool IsPresentationNavigationKey(VirtualKey key) =>
        key is VirtualKey.Right or VirtualKey.Left or VirtualKey.Down or VirtualKey.Up or VirtualKey.PageDown or VirtualKey.PageUp or VirtualKey.Space or VirtualKey.Enter
            or VirtualKey.Home or VirtualKey.End or VirtualKey.Back or VirtualKey.N or VirtualKey.P;

    private void ShowTypedPage()
    {
        _typedPageTimer?.Stop();
        if (_typedPage.Length == 0)
        {
            PresentationGoTo.Visibility = Visibility.Collapsed;
            return;
        }

        PresentationGoToText.Text = Strings.Format("Present_GoTo", _typedPage);
        PresentationGoTo.Visibility = Visibility.Visible;
    }

    // Esc first drops a page number being typed, and only then ends the presentation.
    private bool EscapeInPresentation()
    {
        if (!_presenting)
        {
            return false;
        }

        if (_typedPage.Length > 0)
        {
            _typedPage = string.Empty;
            PresentationGoTo.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExitPresentation();
        }

        return true;
    }

    // ---- entering and leaving ---------------------------------------------------------------------------------------------------------------------

    private void TogglePresentation()
    {
        if (_presenting)
        {
            ExitPresentation();
        }
        else
        {
            EnterPresentation();
        }
    }

    private void EnterPresentation()
    {
        if (_presenting || _session is not { IsOpen: true } session)
        {
            return;
        }

        if (FindOpen)
        {
            CloseFind();
        }

        _presentationFullScreenBefore = _fullScreen;
        _presenting = true;
        _suppressTurnAnnouncement = true;
        try
        {
            SetFullScreen(true, quiet: true);
            session.EnterPresentation();
        }
        finally
        {
            _suppressTurnAnnouncement = false;
        }

        Pages.Presentation = true;
        _measuredSession = session;
        session.TurnMeasured += OnTurnMeasured;
        _surfaceName = AutomationProperties.GetName(Pages);
        _surfaceId = AutomationProperties.GetAutomationId(Pages);
        AutomationProperties.SetAutomationId(Pages, "PresentationSurface");
        _presentationDisplay = NativeWindowing.DisplayOf(AppWindow);
        KeepAwake(true);
        _announcedSession = session;
        (_announcedFirst, _announcedLast) = session.Controller.CurrentPages;
        AnnounceView(Strings.Format("Present_Enter_Announce", _announcedFirst + 1, session.Controller.PageCount));
        SyncViewChrome(session);
        ShowPresentationHint(showEscape: true);
        RefreshCommands();
        Pages.FocusPages();
    }

    private void ExitPresentation()
    {
        if (!_presenting)
        {
            return;
        }

        _presenting = false;
        if (_measuredSession is { } measured)
        {
            measured.TurnMeasured -= OnTurnMeasured;
            _measuredSession = null;
        }

        _typedPage = string.Empty;
        _pointerOverHint = false;
        _hintTimer?.Stop();
        _typedPageTimer?.Stop();
        PresentationHint.Visibility = Visibility.Collapsed;
        PresentationGoTo.Visibility = Visibility.Collapsed;
        BlankScreen.Visibility = Visibility.Collapsed;
        _blank = Blank.None;
        _suppressTurnAnnouncement = true;
        try
        {
            _session?.ExitPresentation();
            Pages.Presentation = false;
            if (!_presentationFullScreenBefore)
            {
                SetFullScreen(false, quiet: true);
            }
        }
        finally
        {
            _suppressTurnAnnouncement = false;
        }

        AutomationProperties.SetAutomationId(Pages, string.IsNullOrEmpty(_surfaceId) ? "PageSurface" : _surfaceId);
        AutomationProperties.SetName(Pages, _surfaceName);
        KeepAwake(false);
        if (_session is { IsOpen: true } session)
        {
            _announcedSession = session;
            (_announcedFirst, _announcedLast) = session.Controller.CurrentPages;
            AnnounceView(Strings.Format("Present_Exit_Announce", _announcedFirst + 1, session.Controller.PageCount));
            SyncViewChrome(session);
        }

        RefreshCommands();
        Pages.FocusPages();
    }

    // ---- the page-number bar ------------------------------------------------------------------------------------------------------------------

    private void ShowPresentationHint(bool showEscape)
    {
        if (!_presenting || _session is not { IsOpen: true } session)
        {
            return;
        }

        var (first, _) = session.Controller.CurrentPages;
        PresentationPageText.Text = Strings.Format("Present_Page", first + 1, session.Controller.PageCount);
        PresentationEscText.Visibility = showEscape ? Visibility.Visible : Visibility.Collapsed;
        PresentationHint.Visibility = Visibility.Visible;
        PresentationHint.UpdateLayout(); // so Tab can reach Exit in the very key press that showed the bar
        _hintTimer!.Stop();
        _hintTimer.Interval = showEscape ? HintTimeAtEntry : HintTime;
        _hintTimer.Start();
    }

    // The bar stays while Exit has the keyboard or the pointer is on the bar; otherwise it goes after its time.
    private void OnHintTick()
    {
        _hintTimer?.Stop();
        if (!_presenting)
        {
            return;
        }

        if (_pointerOverHint || PresentationExit.FocusState != FocusState.Unfocused)
        {
            _hintTimer?.Start();
            return;
        }

        PresentationHint.Visibility = Visibility.Collapsed;
    }

    private void OnPresentationHintEntered(object sender, PointerRoutedEventArgs e) => _pointerOverHint = true;

    private void OnPresentationHintExited(object sender, PointerRoutedEventArgs e) => _pointerOverHint = false;

    // ---- the displays and the power -----------------------------------------------------------------------------------------------------------

    // The display the talk is on is gone: leave presentation, and the document stays where it is.
    private void OnDisplayChange()
    {
        if (_presenting && PresentationRules.ShouldEndOnDisplayChange(_presentationDisplay, [.. NativeWindowing.Monitors().Select(m => m.Name)]))
        {
            ExitPresentation();
        }
    }

    private DocumentSession? _measuredSession;

    // The turn times, for the journey that measures them (PV-17): one line per page turned, only when the test seam names a file.
    private void OnTurnMeasured(double firstFrameMs, double sharpMs) => AppendPresentationDump(
        "turn " + firstFrameMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + sharpMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendPresentationDump(string line)
    {
        if (TestSeams.Get(TestSeamGate.PresentationDump) is { Length: > 0 } name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.PrivateDirectory, name), line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A journey's own file; if it cannot be written the journey says so.
            }
        }
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    // The display and the system stay awake while presenting (SetThreadExecutionState with ES_CONTINUOUS), and are let go on exit. Windows ends the request with the thread, so a crash never leaves it on.
    private void KeepAwake(bool on)
    {
        if (on == _awake)
        {
            return;
        }

        _awake = on;
        _ = SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous);
        AppendPresentationDump(on ? "awake" : "asleep");
    }
}
