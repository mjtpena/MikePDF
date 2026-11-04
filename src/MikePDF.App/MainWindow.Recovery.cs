using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MikePDF.App.Services;
using MikePDF.Core.Editing.Recovery;
using MikePDF.Core.Windowing;

namespace MikePDF.App;

/// <summary>What a person is asked at the start of the app, or when a document opens, because MikePDF closed before its edits were saved.</summary>
public sealed record RecoverItem(string Name, string Summary, string Path, string AccessibleName);

/// <summary>
/// Crash recovery in the window (docs/features/save-pipeline.md 3.5, design 14 sections 2.7 and 3.8): the dialog at launch that lists every document with unsaved changes, the same question for one document
/// when it is opened, and the list in the empty window of the journals that were kept for later. Finding the journals, finishing a save that was interrupted, and rebuilding the edits are
/// <c>MikePDF.Core.Editing.Recovery</c>; this file asks, and applies the answer.
/// </summary>
public sealed partial class MainWindow
{
    private enum RecoveryAnswer
    {
        Restore,
        Discard,
        NotNow,
    }

    /// <summary>
    /// At launch: looks at every journal (which also finishes a save that was cut short) and, if some document has unsaved changes, asks what to do with them. Returns the documents to open once the session is
    /// restored; their tabs apply the journals. Nothing is saved by this.
    /// </summary>
    internal async Task<IReadOnlyList<string>> PromptStartupRecoveryAsync()
    {
        await _loaded.Task;
        if (_services.Recovery is not { } recovery)
        {
            return [];
        }

        IReadOnlyList<RecoveryCandidate> found;
        try
        {
            found = await Task.Run(recovery.Scan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Recovery scan failed: " + e.GetType().Name);
            return [];
        }

        var rows = RecoveryPrompt.Rows(found);
        if (rows.Count == 0)
        {
            return [];
        }

        var (answer, chosen) = await ShowRecoveryDialogAsync(rows, launch: true);
        var open = new List<string>();
        switch (answer)
        {
            case RecoveryAnswer.Restore:
                foreach (var row in rows)
                {
                    if (chosen.Contains(row) && row.Candidate.DocumentPath is { } path)
                    {
                        _app.QueueRestore(row.Candidate);
                        open.Add(path);
                    }
                    else if (row.CanRestore)
                    {
                        _app.Defer(row); // left unchecked: kept, and listed in the empty window
                    }
                }

                break;
            case RecoveryAnswer.Discard when await ConfirmDiscardRecoveryAsync():
                foreach (var row in rows)
                {
                    Discard(recovery, row);
                }

                break;
            default:
                foreach (var row in rows.Where(static r => r.CanRestore))
                {
                    _app.Defer(row);
                }

                break;
        }

        RefreshRecoverPanel();
        return open;
    }

    /// <summary>Opens the documents the person chose to restore (after the session came back): a tab that is already there is shown, and loading it applies its journal.</summary>
    internal async Task OpenRecoveredAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            await _app.OpenAsync(path, OpenSource.User, this);
        }
    }

    private static void Discard(RecoveryService recovery, RecoveryRow row)
    {
        try
        {
            recovery.Discard(row.Candidate);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Discarding a journal failed: " + e.GetType().Name); // left for the next start
        }
    }

    /// <summary>A document is opening and a journal was found for it that nobody has settled this run: the same question, for this document.</summary>
    private async Task<RecoveryCandidate?> OfferRecoveryAsync(string path)
    {
        if (_services.Recovery is not { } recovery)
        {
            return null;
        }

        RecoveryCandidate? found;
        try
        {
            found = await Task.Run(() => recovery.Find(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (found is null)
        {
            return null;
        }

        var row = RecoveryPrompt.Row(found);
        var (answer, _) = await ShowRecoveryDialogAsync([row], launch: false);
        switch (answer)
        {
            case RecoveryAnswer.Restore when row.CanRestore:
                return found;
            case RecoveryAnswer.Discard when await ConfirmDiscardRecoveryAsync():
                Discard(recovery, row);
                return null;
            default:
                _app.Defer(row);
                RefreshRecoverPanel();
                return null;
        }
    }

    // One dialog for one document or several. Restore is the default and Esc is Not now, so a keystroke never throws anything away (save-pipeline 4).
    private async Task<(RecoveryAnswer Answer, IReadOnlyList<RecoveryRow> Chosen)> ShowRecoveryDialogAsync(IReadOnlyList<RecoveryRow> rows, bool launch)
    {
        var dialog = NewDialog();
        var panel = new StackPanel { Spacing = 8 };
        var boxes = new List<(CheckBox Box, RecoveryRow Row)>();
        string Name(RecoveryRow row) => row.Name.Length > 0 ? row.Name : Strings.Get("Recover_Unnamed");
        string Summary(RecoveryRow row) => RecoveryPrompt.Summary(row.Candidate.Summary, Strings.Get);

        if (launch || rows.Count > 1)
        {
            dialog.Title = Strings.Get("Recover_Multi_Title");
            panel.Children.Add(new TextBlock { Text = Strings.Get("Recover_Multi_Body"), TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var line = new StackPanel { Spacing = 2 };
                var text = row.State == RecoveryRowState.Unreadable ? Strings.Format("Recover_Unreadable", Name(row)) : $"{Name(row)}   {Summary(row)}";
                var box = new CheckBox { Content = text, IsChecked = row.DefaultChecked, IsEnabled = row.CanRestore };
                AutomationProperties.SetAutomationId(box, "RecoverItem_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                AutomationProperties.SetName(box, row.State == RecoveryRowState.Unreadable ? text : $"{Name(row)}: {Summary(row)}");
                line.Children.Add(box);
                if (Caption(row) is { } caption)
                {
                    line.Children.Add(new TextBlock { Text = caption, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32, 0, 0, 0) });
                }

                panel.Children.Add(line);
                boxes.Add((box, row));
            }

            panel.Children.Add(new TextBlock { Text = Strings.Get("Recover_Multi_Footer"), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var row = rows[0];
            dialog.Title = Strings.Get("Recover_Title");
            if (row.State == RecoveryRowState.Unreadable)
            {
                panel.Children.Add(new TextBlock { Text = Strings.Format("Recover_Unreadable", Name(row)), TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = Strings.Format("Recover_Body", Name(row)), TextWrapping = TextWrapping.Wrap });
                var summary = new TextBlock { Text = Summary(row), TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(summary, "RecoverSummary");
                panel.Children.Add(summary);
                if (Caption(row) is { } caption)
                {
                    panel.Children.Add(new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap });
                }
            }

            boxes.Add((new CheckBox { IsChecked = row.CanRestore }, row));
        }

        dialog.Content = panel;
        dialog.PrimaryButtonText = Strings.Get("Recover_Restore");
        dialog.IsPrimaryButtonEnabled = rows.Any(static r => r.CanRestore);
        dialog.SecondaryButtonText = Strings.Get("Recover_Discard");
        dialog.CloseButtonText = Strings.Get("Recover_NotNow");
        dialog.DefaultButton = dialog.IsPrimaryButtonEnabled ? ContentDialogButton.Primary : ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "RecoverDialog");
        var result = await ShowDialogAsync(dialog);
        var chosen = boxes.Where(static b => b.Box.IsChecked == true && b.Row.CanRestore).Select(static b => b.Row).ToList();
        return result switch
        {
            ContentDialogResult.Primary when chosen.Count > 0 => (RecoveryAnswer.Restore, chosen),
            ContentDialogResult.Secondary => (RecoveryAnswer.Discard, rows),
            _ => (RecoveryAnswer.NotNow, []),
        };
    }

    // What to say under a row that is not simply ready: the file went, the file changed, or part of the journal was damaged.
    private static string? Caption(RecoveryRow row)
    {
        var name = row.Name.Length > 0 ? row.Name : Strings.Get("Recover_Unnamed");
        return row.State switch
        {
            RecoveryRowState.FileMissing => Strings.Get("Recover_Moved"),
            RecoveryRowState.FileChanged => Strings.Format("Recover_FileChanged", name),
            _ => row.Partial ? Strings.Get("Recover_Damaged") : null,
        };
    }

    private async Task<bool> ConfirmDiscardRecoveryAsync()
    {
        var dialog = NewDialog();
        dialog.Title = Strings.Get("Recover_Discard");
        dialog.Content = new TextBlock { Text = Strings.Get("Recover_DiscardConfirm"), TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = Strings.Get("Recover_Discard");
        dialog.CloseButtonText = Strings.Get("Dialog_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "RecoverDiscardDialog");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    // ---- the empty window ----------------------------------------------------------------------------------------------------------------------

    /// <summary>"Unsaved changes found for N documents": the journals kept for later whose documents are not open, each with a way to open it.</summary>
    private void RefreshRecoverPanel()
    {
        var items = _app.Deferred
            .Where(static r => r.Candidate.DocumentPath is not null)
            .Where(r => _app.Model.Find(DocumentIdentity.Of(r.Candidate.DocumentPath!)) is null)
            .Select(r => new RecoverItem(
                r.Name,
                RecoveryPrompt.Summary(r.Candidate.Summary, Strings.Get),
                r.Candidate.DocumentPath!,
                Strings.Format("Recover_Item_Automation", r.Name, RecoveryPrompt.Summary(r.Candidate.Summary, Strings.Get))))
            .ToList();
        RecoverList.ItemsSource = items;
        RecoverHeading.Text = items.Count == 1 ? Strings.Get("Recover_List_One") : Strings.Format("Recover_List_Other", items.Count);
        RecoverPanel.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRecoverClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecoverItem item)
        {
            _app.Undefer(item.Path); // asking again is the point: opening the document asks about its journal
            RefreshRecoverPanel();
            await OpenPathAsync(item.Path);
        }
    }
}
