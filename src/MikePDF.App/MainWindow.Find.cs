using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
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

        var regex = FindRegexToggle.IsChecked == true;
        var matchCase = FindMatchCaseToggle.IsChecked == true;
        if (regex && RegexQuery.TryCompile(FindBox.Text, matchCase).Problem is var problem and not RegexProblem.None)
        {
            // Never started: a pattern that cannot be searched for is said so, in the live region, and nothing is highlighted.
            session.CancelFind();
            AutomationProperties.SetLiveSetting(FindStatus, AutomationLiveSetting.Polite);
            FindStatus.Text = Strings.Get(problem switch
            {
                RegexProblem.TooComplex => "Find_RegexTooComplex",
                RegexProblem.Unsupported => "Find_RegexUnsupported",
                _ => "Find_RegexInvalid",
            });
            return;
        }

        session.StartFind(new SearchOptions(FindBox.Text, MatchCase: matchCase, WholeWord: !regex && FindWholeWordToggle.IsChecked == true, UseRegex: regex));
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
        var incomplete = progress is { IsComplete: true, PagesFailed: > 0 } && !progress.QueryRefused; // some pages could not be searched: say so, don't claim "no matches"
        string text;
        if (progress.QueryRefused)
        {
            text = Strings.Get("Find_Refused");
        }
        else if (progress.MatchCount == 0)
        {
            text = progress.IsComplete ? Strings.Get(incomplete ? "Find_NoneFailed" : "Find_None") : Strings.Format("Find_SearchingPercent", (int)Math.Round(progress.Fraction * 100));
        }
        else if (ordinal > 0)
        {
            text = Strings.Format(progress.IsComplete ? (incomplete ? "Find_CountFailed" : "Find_Count") : "Find_CountMore", ordinal, progress.MatchCount);
        }
        else
        {
            text = Strings.Format(progress.IsComplete ? (incomplete ? "Find_MatchesFailed" : "Find_Matches") : "Find_MatchesMore", progress.MatchCount);
        }

        AutomationProperties.SetLiveSetting(FindStatus, progress.IsComplete ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        FindStatus.Text = text;
    }

    private void OnOverlaysChanged() => UpdateFindStatus();

    // ---- events -------------------------------------------------------------------------------------------------------------------------

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

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        if (FindBar is not null && FindWholeWordToggle is not null)
        {
            StartFind();
        }
    }

    // A pattern writes \b where it wants word edges, so whole word is set aside while patterns are on.
    private void OnFindRegexChanged(object sender, RoutedEventArgs e)
    {
        if (FindBar is null || FindWholeWordToggle is null)
        {
            return;
        }

        FindWholeWordToggle.IsEnabled = FindRegexToggle.IsChecked != true;
        StartFind();
    }

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
    public static bool IsShiftDown() => IsDown(VirtualKey.Shift);

    public static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
