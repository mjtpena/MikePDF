using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MikePDF.App.Services;
using MikePDF.Core.Commands;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MikePDF.App.Views;

/// <summary>One row of the palette as the list shows it.</summary>
public sealed record PaletteRow
{
    internal PaletteRow(PaletteItem item, int index)
    {
        Item = item;
        Title = item.Title;
        Shortcut = item.Shortcut;
        // A disabled row says why in words, so dimming is never the only cue; the others show where they are from.
        Subtitle = item.Enabled ? (item.Detail.Length > 0 ? item.Detail : item.Category) : item.DisabledReason ?? string.Empty;
        Opacity = item.Enabled ? 1.0 : 0.65;
        Spoken = item.Spoken(Strings.Get);
        AutomationId = "PaletteItem_" + (item.Kind == PaletteItemKind.Command ? item.Key : item.Kind + "_" + index.ToString(CultureInfo.InvariantCulture));
    }

    internal PaletteItem Item { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Shortcut { get; }

    public double Opacity { get; }

    public string Spoken { get; }

    public string AutomationId { get; }
}

/// <summary>
/// The command palette: a search box with focus, and a list of results that the selection moves through while focus stays in the box. The ranking is
/// <see cref="PaletteEngine"/> (MikePDF.Core); this class shows it, handles the keys, and tells a screen reader what is selected and how many results there are.
/// </summary>
public sealed partial class CommandPaletteView : UserControl
{
    private static readonly TimeSpan CountDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherQueueTimer _countTimer;
    private PaletteEngine? _engine;
    private Action<PaletteItem>? _execute;
    private Control? _restoreTo;
    private bool _navigating;
    private bool _unavailableAnnounced;

    public CommandPaletteView()
    {
        InitializeComponent();
        AutomationProperties.SetName(Panel, Strings.Get("Palette_Name"));
        AutomationProperties.SetName(Box, Strings.Get("Palette_Box_Name"));
        AutomationProperties.SetName(List, Strings.Get("Palette_List_Name"));
        Box.PlaceholderText = Strings.Get("Palette_Box_Placeholder");
        _countTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _countTimer.Interval = CountDelay;
        _countTimer.IsRepeating = false;
        _countTimer.Tick += (_, _) =>
        {
            _countTimer.Stop();
            ShowCount();
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Raised after the palette closed; the argument says whether focus went back to where it was.</summary>
    internal event Action<bool>? Closed;

    /// <summary>The text of the box (for tests and diagnostics).</summary>
    internal string Query => Box.Text;

    internal void Open(PaletteEngine engine, Action<PaletteItem> execute)
    {
        _engine = engine;
        _execute = execute;
        _restoreTo = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        _unavailableAnnounced = false;
        Visibility = Visibility.Visible;
        Box.Text = string.Empty;
        Refresh();
        Box.Focus(FocusState.Programmatic);
        ShowCount();
    }

    internal void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _countTimer.Stop();
        Visibility = Visibility.Collapsed;
        var target = _restoreTo;
        _restoreTo = null;
        _engine = null;
        _execute = null;
        List.ItemsSource = null;
        var restored = target is { IsLoaded: true } && target.Focus(FocusState.Programmatic);
        Closed?.Invoke(restored);
    }

    // ---- results -----------------------------------------------------------------------------------------------------------------------

    private void Refresh()
    {
        if (_engine is null)
        {
            return;
        }

        var text = Box.Text;
        var results = text.Length == 0 ? _engine.Suggestions() : _engine.Search(text);
        List.ItemsSource = results.Select((r, i) => new PaletteRow(r.Item, i)).ToList();
        List.SelectedIndex = results.Count > 0 ? 0 : -1;
        _unavailableAnnounced = false;
    }

    private int Count => (List.ItemsSource as IReadOnlyCollection<PaletteRow>)?.Count ?? 0;

    private void ShowCount()
    {
        var count = Count;
        Status.Text = count switch
        {
            0 => Strings.Get("Palette_NoResults"),
            1 => Strings.Get("Palette_Count_One"),
            _ => Strings.Format("Palette_Count_Other", count),
        };
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Refresh();
        _countTimer.Stop();
        _countTimer.Start(); // the count is announced after a pause in typing, not on every key
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not PaletteRow row)
        {
            return;
        }

        List.ScrollIntoView(row);
        if (_navigating)
        {
            // Focus stays in the box while the selection moves, so a screen reader is told what is selected (one announcement per move, the latest wins).
            Announce(row.Spoken, "PaletteSelection");
        }
    }

    private void Announce(string text, string activity)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(Box) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Box);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, activity);
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------------------------

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardState.IsDown(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Down:
                Move(+1, wrap: true);
                break;
            case VirtualKey.Up:
                Move(-1, wrap: true);
                break;
            case VirtualKey.PageDown:
                Move(+8, wrap: false);
                break;
            case VirtualKey.PageUp:
                Move(-8, wrap: false);
                break;
            case VirtualKey.Home when ctrl:
                SelectIndex(0);
                break;
            case VirtualKey.End when ctrl:
                SelectIndex(Count - 1);
                break;
            case VirtualKey.Enter:
                RunSelected();
                break;
            case VirtualKey.Tab:
                break; // focus stays in the box: the list is reached with Up and Down
            case VirtualKey.Escape:
                if (Box.Text.Length > 0)
                {
                    Box.Text = string.Empty; // the first Esc clears the query, the next closes
                }
                else
                {
                    Close();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Move(int delta, bool wrap)
    {
        var count = Count;
        if (count == 0)
        {
            return;
        }

        var at = List.SelectedIndex < 0 ? (delta > 0 ? -1 : 0) : List.SelectedIndex;
        var next = wrap ? (((at + delta) % count) + count) % count : Math.Clamp(at + delta, 0, count - 1);
        SelectIndex(next);
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }

        _navigating = true;
        try
        {
            if (List.SelectedIndex == index)
            {
                Announce(((PaletteRow)List.SelectedItem).Spoken, "PaletteSelection");
            }
            else
            {
                List.SelectedIndex = index;
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteRow row)
        {
            Run(row);
        }
    }

    private void RunSelected()
    {
        if (List.SelectedItem is PaletteRow row)
        {
            Run(row);
        }
    }

    private void Run(PaletteRow row)
    {
        if (!row.Item.Enabled)
        {
            // Enter on an unavailable command does nothing but say why, once.
            if (!_unavailableAnnounced)
            {
                _unavailableAnnounced = true;
                Announce(Strings.Format("Palette_Unavailable_Announce", row.Item.Title, row.Item.DisabledReason ?? string.Empty), "PaletteUnavailable");
            }

            return;
        }

        var execute = _execute;
        Close(); // closed first, so a command that opens a dialog starts from a clean focus
        execute?.Invoke(row.Item);
    }
}
