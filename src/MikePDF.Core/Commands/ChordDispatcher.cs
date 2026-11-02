namespace MikePDF.Core.Commands;

/// <summary>What the window knows about the key press and the focus, for one key-down.</summary>
public sealed record DispatchInput
{
    public CommandContext Context { get; init; } = CommandContext.Empty;

    /// <summary>A text input has focus: a TextBox, PasswordBox, NumberBox, editable ComboBox, the palette box.</summary>
    public bool TextInputFocused { get; init; }

    /// <summary>The page surface itself has focus.</summary>
    public bool SurfaceFocused { get; init; }

    /// <summary>An annotation or form text editor is active (scope Editing).</summary>
    public bool EditingActive { get; init; }

    /// <summary>The key is auto-repeating (it was already down).</summary>
    public bool IsRepeat { get; init; }

    /// <summary>Right Alt is acting as AltGr, which Windows reports as Ctrl+Alt.</summary>
    public bool AltGrActive { get; init; }

    /// <summary>An input method is composing text.</summary>
    public bool ImeComposing { get; init; }

    /// <summary>The command palette is open: only its own shortcut works until it closes.</summary>
    public bool PaletteOpen { get; init; }
}

public enum DispatchKind
{
    /// <summary>Not ours: leave the key for the focused control and the system.</summary>
    NotHandled,

    /// <summary>Run the command, and mark the key handled.</summary>
    Execute,

    /// <summary>The command exists but cannot run now: say why, and mark the key handled.</summary>
    Disabled,

    /// <summary>Mark the key handled and do nothing (a repeat of a command that runs once; a default key that was unbound).</summary>
    Swallow,
}

public sealed record DispatchResult(DispatchKind Kind, CommandDefinition? Command, CommandAvailability Availability)
{
    public static DispatchResult NotHandled { get; } = new(DispatchKind.NotHandled, null, CommandAvailability.Enabled);

    public static DispatchResult Swallow { get; } = new(DispatchKind.Swallow, null, CommandAvailability.Enabled);
}

/// <summary>
/// Turns a chord into the command to run (docs/features/command-palette-shortcuts.md 3.4). Pure: the window builds the chord and the <see cref="DispatchInput"/>
/// from the key event and the focus, calls <see cref="Dispatch"/>, and does what the result says.
/// </summary>
public sealed class ChordDispatcher
{
    public const string PaletteCommandId = "app.commandPalette";

    private readonly CommandRegistry _registry;

    public ChordDispatcher(CommandRegistry registry) => _registry = registry;

    public DispatchResult Dispatch(Chord chord, DispatchInput input)
    {
        // An input method that is composing owns every key; AltGr is Ctrl+Alt to Windows but a character to the person.
        if (input.ImeComposing || (input.AltGrActive && chord.HasControl && chord.HasAlt))
        {
            return DispatchResult.NotHandled;
        }

        var matches = _registry.CommandsFor(chord).Where(c => ScopeMatches(c, chord, input)).ToList();
        if (input.PaletteOpen)
        {
            matches = [.. matches.Where(c => c.Id == PaletteCommandId)];
        }

        // A text input keeps the chords that edit text, unless a command of the Editing scope claims the chord.
        if (input.TextInputFocused && IsTextEditingChord(chord) && !matches.Any(c => c.Scope == CommandScope.Editing))
        {
            return DispatchResult.NotHandled;
        }

        var command = matches.OrderBy(Specificity).FirstOrDefault();
        if (command is null)
        {
            return input.SurfaceFocused && !input.PaletteOpen && _registry.FreedSurfaceChords.Contains(chord) ? DispatchResult.Swallow : DispatchResult.NotHandled;
        }

        var availability = command.CanExecute(input.Context);
        if (!availability.IsEnabled)
        {
            // The reason is said once per press, not on every auto-repeat.
            return input.IsRepeat ? DispatchResult.Swallow : new DispatchResult(DispatchKind.Disabled, command, availability);
        }

        if (input.IsRepeat && !command.Repeatable)
        {
            return DispatchResult.Swallow;
        }

        return new DispatchResult(DispatchKind.Execute, command, availability);
    }

    /// <summary>Whether a chord is one that a text input uses for editing: typing, moving the caret, Ctrl+C, X, V, A, Z and Y.</summary>
    public static bool IsTextEditingChord(Chord chord)
    {
        if (chord.HasAlt)
        {
            return false;
        }

        if (!chord.HasControl)
        {
            return ChordKeys.IsTextEditing(chord.Key);
        }

        return chord.Key is 'C' or 'X' or 'V' or 'A' or 'Z' or 'Y' or (>= 0x21 and <= 0x28) or 0x2D or 0x2E or 0x08;
    }

    private static int Specificity(CommandDefinition command) => command.Scope == CommandScope.Global ? 1 : 0;

    private static bool ScopeMatches(CommandDefinition command, Chord chord, DispatchInput input) => command.Scope switch
    {
        CommandScope.Global => true,
        CommandScope.Reader => !input.TextInputFocused && (input.SurfaceFocused || chord.HasControl || chord.HasAlt || !ChordKeys.IsTextEditing(chord.Key)),
        CommandScope.Editing => input.EditingActive,
        CommandScope.Presentation => input.Context.Presentation,
        _ => false,
    };
}
