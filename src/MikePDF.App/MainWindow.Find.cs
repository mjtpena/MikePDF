using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Core.Reading;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Windows.System;

namespace MikePDF.App;

/// <summary>Find in the document (Ctrl+F): a bar over the top right of the pages, a count that a screen reader announces, next and previous, match case.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan FindDelay = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _findTimer;

    private bool FindOpen => FindBar.Visibility == Visibility.Visible;

    private void OpenFind()
    {
        if (_session is not { IsOpen: true })
        {
            return;
        }

        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0)
        {
            StartFind();
        }
    }

    private void CloseFind()
    {
        _findTimer?.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = string.Empty;
        _session?.CancelFind();
        Pages.FocusPages();
    }

    private void StartFind()
    {
        _findTimer?.Stop();
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FindBox.Text))
        {
            session.CancelFind();
            FindStatus.Text = string.Empty;
            return;
        }

        session.StartFind(new SearchOptions(FindBox.Text, MatchCase: FindMatchCaseToggle.IsChecked == true));
        UpdateFindStatus();
        _ = MoveToMatchAsync(forward: true);
    }

    private async Task MoveToMatchAsync(bool forward)
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        await session.MoveToMatchAsync(forward);
        UpdateFindStatus();
    }

    /// <summary>The count under the box: "3 of 17", "No matches", or how far the search has got. Announced when the search ends or the match changes, not while it works.</summary>
    private void UpdateFindStatus()
    {
        if (!FindOpen)
        {
            return;
        }

        if (_session?.Find is not { } find)
        {
            FindStatus.Text = string.Empty;
            return;
        }

        var progress = find.Progress;
        var ordinal = find.CurrentOrdinal;
        string text;
        if (progress.QueryRefused)
        {
            text = Strings.Get("Find_Refused");
        }
        else if (progress.MatchCount == 0)
        {
            text = progress.IsComplete ? Strings.Get("Find_None") : Strings.Format("Find_SearchingPercent", (int)Math.Round(progress.Fraction * 100));
        }
        else if (ordinal > 0)
        {
            text = Strings.Format(progress.IsComplete ? "Find_Count" : "Find_CountMore", ordinal, progress.MatchCount);
        }
        else
        {
            text = Strings.Format(progress.IsComplete ? "Find_Matches" : "Find_MatchesMore", progress.MatchCount);
        }

        AutomationProperties.SetLiveSetting(FindStatus, progress.IsComplete ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        FindStatus.Text = text;
    }

    private void OnOverlaysChanged() => UpdateFindStatus();

    // ---- events -------------------------------------------------------------------------------------------------------------------------

    private void OnFindClick(object sender, RoutedEventArgs e) => OpenFind();

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenFind();
    }

    private void OnFindNextAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (FindOpen && _session?.Find is not null)
        {
            _ = MoveToMatchAsync(forward: true);
        }
        else
        {
            OpenFind();
        }
    }

    private void OnFindPreviousAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (FindOpen && _session?.Find is not null)
        {
            _ = MoveToMatchAsync(forward: false);
        }
        else
        {
            OpenFind();
        }
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _findTimer ??= DispatcherQueue.CreateTimer();
        _findTimer.Interval = FindDelay;
        _findTimer.IsRepeating = false;
        _findTimer.Tick -= OnFindTimer;
        _findTimer.Tick += OnFindTimer;
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void OnFindTimer(DispatcherQueueTimer sender, object args) => StartFind();

    private void OnFindOptionChanged(object sender, RoutedEventArgs e) => StartFind();

    private void OnFindNextClick(object sender, RoutedEventArgs e) => _ = MoveToMatchAsync(forward: true);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => _ = MoveToMatchAsync(forward: false);

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindBarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseFind();
                e.Handled = true;
                break;
            case VirtualKey.Enter when e.OriginalSource is TextBox:
                if (_findTimer?.IsRunning == true || _session?.Find is null)
                {
                    StartFind(); // the box changed a moment ago: search now
                }
                else
                {
                    _ = MoveToMatchAsync(forward: !InputKeyboardState.IsShiftDown());
                }

                e.Handled = true;
                break;
            default:
                break;
        }
    }
}

internal static class InputKeyboardState
{
    public static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
