using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core.Commands;
using Windows.System;

namespace MikePDF.App.Views;

/// <summary>
/// "Change shortcut for {command}": press the keys, or type them (for assistive technology that takes keys, and for a touch keyboard). It shows the chord live,
/// says in words when it is reserved or already used (and then offers Reassign), and applies to the first or the second of the command's two shortcuts.
/// </summary>
internal static class ShortcutDialog
{
    /// <summary>Shows the dialog. Returns true if the shortcuts changed.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, ElementTheme theme, CommandRegistry registry, CommandDefinition command, Func<int, string?> characterFor, Action<string> announce, Func<ContentDialog, Task<ContentDialogResult>> show)
    {
        var name = Strings.Get(command.NameKey);
        Chord? pending = null;
        var changed = false;
        string Show(Chord chord) => ChordDisplay.Format(chord, null, characterFor);
        IReadOnlyList<Chord> Current() => registry.EffectiveChords(command.Id);

        var prompt = new TextBlock { Text = Strings.Get("Shortcuts_Capture_Prompt"), TextWrapping = TextWrapping.Wrap };
        var slots = new RadioButtons { MaxColumns = 1 };
        AutomationProperties.SetAutomationId(slots, "ShortcutSlot");
        AutomationProperties.SetName(slots, Strings.Format("Shortcuts_Change_Title", name));
        void FillSlots()
        {
            var selected = Math.Max(0, slots.SelectedIndex);
            slots.Items.Clear();
            for (var i = 0; i < CommandRegistry.MaxChordsPerCommand; i++)
            {
                var text = i < Current().Count ? Show(Current()[i]) : Strings.Get("Shortcuts_None");
                slots.Items.Add(new RadioButton { Content = text, Tag = i });
            }

            slots.SelectedIndex = selected;
        }

        FillSlots();

        var live = new TextBlock { Text = Strings.Get("Shortcuts_Capture_None"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(live, "ShortcutLive");
        AutomationProperties.SetLiveSetting(live, AutomationLiveSetting.Polite);
        var capture = new ContentControl
        {
            Content = live,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            MinHeight = 56,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(capture, "ShortcutCaptureBox");
        AutomationProperties.SetName(capture, Strings.Get("Shortcuts_Capture_Box"));

        var typed = new TextBox { PlaceholderText = Strings.Get("Shortcuts_Capture_Typed"), MaxLength = ChordParser.MaxLength };
        AutomationProperties.SetAutomationId(typed, "ShortcutTyped");
        AutomationProperties.SetName(typed, Strings.Get("Shortcuts_Capture_TypedName"));

        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        AutomationProperties.SetAutomationId(problem, "ShortcutProblem");
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        foreach (var child in new UIElement[] { prompt, slots, capture, typed, problem })
        {
            panel.Children.Add(child);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = Strings.Format("Shortcuts_Change_Title", name),
            Content = panel,
            PrimaryButtonText = Strings.Get("Shortcuts_Assign"),
            SecondaryButtonText = Strings.Get("Shortcuts_Clear"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        AutomationProperties.SetAutomationId(dialog, "ShortcutDialog");
        AutomationProperties.SetName(dialog, Strings.Get("ShortcutDialog_Name"));

        bool conflicting = false;
        void Evaluate()
        {
            conflicting = false;
            if (pending is not { } chord)
            {
                dialog.IsPrimaryButtonEnabled = false;
                problem.Text = string.Empty;
                return;
            }

            live.Text = Strings.Format("Shortcuts_Capture_Live", Show(chord));
            var text = string.Empty;
            var bindable = registry.CheckBindable(command.Id, chord);
            if (bindable is { Kind: BindingProblemKind.ReservedChord })
            {
                text = Strings.Format("Shortcuts_Reserved", Show(chord));
            }
            else if (bindable is not null)
            {
                text = Strings.Format("Shortcuts_PlainKey", Show(chord));
            }
            else if (registry.FindConflicts(command.Id, chord) is { Count: > 0 } clashes && !Current().Contains(chord))
            {
                conflicting = true;
                text = Strings.Format("Shortcuts_Conflict", Show(chord), string.Join(", ", clashes.Select(c => Strings.Get(c.Command.NameKey))));
            }

            problem.Text = text;
            dialog.IsPrimaryButtonEnabled = bindable is null;
            dialog.PrimaryButtonText = Strings.Get(conflicting ? "Shortcuts_Reassign" : "Shortcuts_Assign");
        }

        capture.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Escape or VirtualKey.Tab or VirtualKey.Enter)
            {
                return; // the dialog's own: cancel, move on, accept
            }

            e.Handled = true;
            var ctrl = InputKeyboardState.IsDown(VirtualKey.Control);
            var alt = InputKeyboardState.IsDown(VirtualKey.Menu);
            var shift = InputKeyboardState.IsDown(VirtualKey.Shift);
            if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
                or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            {
                live.Text = (ctrl ? "Ctrl+" : string.Empty) + (alt ? "Alt+" : string.Empty) + (shift ? "Shift+" : string.Empty);
                return;
            }

            if (InputKeyboardState.IsDown(VirtualKey.LeftWindows) || InputKeyboardState.IsDown(VirtualKey.RightWindows) || !ChordKeys.IsKnown((int)e.Key))
            {
                return;
            }

            pending = new Chord((ctrl ? ChordModifiers.Control : 0) | (alt ? ChordModifiers.Alt : 0) | (shift ? ChordModifiers.Shift : 0), (int)e.Key);
            typed.Text = string.Empty;
            Evaluate();
        };
        typed.TextChanged += (_, _) =>
        {
            if (typed.Text.Length == 0)
            {
                return;
            }

            if (ChordParser.TryParse(typed.Text, out var chord, out _))
            {
                pending = chord;
                Evaluate();
            }
            else
            {
                pending = null;
                dialog.IsPrimaryButtonEnabled = false;
                live.Text = Strings.Get("Shortcuts_Capture_None");
                problem.Text = Strings.Get("Shortcuts_Invalid");
            }
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (pending is not { } chord)
            {
                args.Cancel = true;
                return;
            }

            var chords = Current().ToList();
            var slot = Math.Max(0, slots.SelectedIndex);
            if (slot < chords.Count)
            {
                chords[slot] = chord;
            }
            else
            {
                chords.Add(chord);
            }

            var outcome = registry.SetChords(command.Id, [.. chords.Distinct()], reassign: conflicting);
            if (!outcome.Applied)
            {
                args.Cancel = true;
                problem.Text = Strings.Get("Shortcuts_Max");
                Evaluate();
                return;
            }

            changed = true;
            announce(Strings.Format("Shortcuts_Saved", name, Show(chord)));
        };
        dialog.SecondaryButtonClick += (_, _) =>
        {
            var chords = Current().ToList();
            var slot = Math.Max(0, slots.SelectedIndex);
            if (slot < chords.Count)
            {
                chords.RemoveAt(slot);
            }

            if (registry.SetChords(command.Id, chords, reassign: false).Applied)
            {
                changed = true;
                announce(Strings.Format("Shortcuts_Removed", name));
            }
        };
        dialog.Opened += (_, _) => capture.Focus(FocusState.Programmatic);

        await show(dialog);
        return changed;
    }
}
