using System.Globalization;
using System.Text;

namespace MikePDF.Core.Commands;

/// <summary>Who owns a reserved chord.</summary>
public enum ReservedBy
{
    /// <summary>Windows takes it before the window sees it, or it opens a system menu or switches input.</summary>
    Windows,

    /// <summary>MikePDF itself uses it for something that is not a command: cancel and close, moving focus.</summary>
    App,
}

/// <summary>
/// The chords that cannot be bound (docs/features/command-palette-shortcuts.md 3.2): Alt+F4, Alt+Tab, Alt+Esc, Alt+Space, Ctrl+Esc, Ctrl+Alt+Delete,
/// Print Screen, F10 and Shift+F10 (menu activation), Tab and Shift+Tab (focus movement), Esc alone (cancel and close). Ctrl+Shift+0 is added from a measurement:
/// Windows takes it for the input-method hotkeys and the window never receives the 0 (the actual-size work found this on 2026-11-02, so actual size is Ctrl+Alt+0).
/// Ctrl+Shift+Esc (Task Manager) and Ctrl+Space (the input-method switch) are added for the same reason: the window would not see them either.
/// </summary>
public static class ReservedChords
{
    private const int Tab = 0x09;
    private const int Escape = 0x1B;
    private const int Space = 0x20;
    private const int PrintScreen = 0x2C;
    private const int Delete = 0x2E;
    private const int Zero = 0x30;
    private const int F4 = 0x73;
    private const int F10 = 0x79;

    /// <summary>Whether a chord is reserved, and by whom.</summary>
    public static bool IsReserved(Chord chord, out ReservedBy by)
    {
        var m = chord.Modifiers;
        var key = chord.Key;
        by = ReservedBy.Windows;
        if (key == PrintScreen)
        {
            return true;
        }

        if (key == F10 && m is ChordModifiers.None or ChordModifiers.Shift)
        {
            return true;
        }

        if (key == F4 && m == ChordModifiers.Alt)
        {
            return true;
        }

        if (m == ChordModifiers.Alt && key is Tab or Escape or Space)
        {
            return true;
        }

        if (m == ChordModifiers.Control && key is Escape or Space)
        {
            return true;
        }

        if (m == (ChordModifiers.Control | ChordModifiers.Shift) && key is Escape or Zero)
        {
            return true;
        }

        if (m == (ChordModifiers.Control | ChordModifiers.Alt) && key == Delete)
        {
            return true;
        }

        by = ReservedBy.App;
        return (key == Tab && m is ChordModifiers.None or ChordModifiers.Shift) || (key == Escape && m == ChordModifiers.None);
    }

    public static bool IsReserved(Chord chord) => IsReserved(chord, out _);
}

/// <summary>What a layout's keys produce, so that a chord can be shown in the user's own key names and so that zoom can be checked on each layout.</summary>
public sealed class KeyLayout
{
    private readonly Dictionary<int, string> _unshifted;

    public KeyLayout(string culture, IReadOnlyDictionary<int, string> unshifted)
    {
        Culture = culture;
        _unshifted = new Dictionary<int, string>(unshifted);
    }

    public string Culture { get; }

    /// <summary>The character the key produces without Shift, or null when this layout has no such key.</summary>
    public string? CharacterFor(int key) => _unshifted.TryGetValue(key, out var text) ? text : null;

    /// <summary>
    /// Whether a person can press the chord on this layout: the key is a numpad key, a function or navigation key, a letter or digit, or one of the
    /// OEM keys this layout has.
    /// </summary>
    public bool CanPress(Chord chord) => !(chord.Key is >= 0xBA and <= 0xDF) || _unshifted.ContainsKey(chord.Key);

    // The unshifted characters of the OEM keys that zoom, comma and period use, per layout. From the layout tables as recalled, not checked on a keyboard of each kind
    // (the checks in the tests are of this table, which is what CMD-22 asks for); the real display text in the app comes from MapVirtualKey for the layout in use.
    public static KeyLayout EnUs { get; } = new("en-US", new Dictionary<int, string> { [0xBB] = "=", [0xBD] = "-", [0xBC] = ",", [0xBE] = "." });

    public static KeyLayout EnGb { get; } = new("en-GB", new Dictionary<int, string> { [0xBB] = "=", [0xBD] = "-", [0xBC] = ",", [0xBE] = "." });

    public static KeyLayout DeDe { get; } = new("de-DE", new Dictionary<int, string> { [0xBB] = "+", [0xBD] = "-", [0xBC] = ",", [0xBE] = "." });

    // French AZERTY: "-" is the unshifted 6 key, so the key Windows calls OEM minus produces something else and zoom out relies on the numpad chord.
    public static KeyLayout FrFr { get; } = new("fr-FR", new Dictionary<int, string> { [0xBB] = "=", [0xBC] = ",", [0xBE] = ";" });

    // Japanese 106-key: the key at OEM plus produces ";" (plus is Shift+;).
    public static KeyLayout JaJp { get; } = new("ja-JP", new Dictionary<int, string> { [0xBB] = ";", [0xBD] = "-", [0xBC] = ",", [0xBE] = "." });

    public static IReadOnlyList<KeyLayout> Known { get; } = [EnUs, EnGb, DeDe, FrFr, JaJp];
}

/// <summary>Chord text for people: "Ctrl+Shift+P", "Ctrl+=", "Page Down", in the key names of the layout in use.</summary>
public static class ChordDisplay
{
    public static string Format(Chord chord, KeyLayout? layout = null, Func<int, string?>? characterFor = null)
    {
        var text = new StringBuilder();
        if (chord.HasControl)
        {
            text.Append("Ctrl+");
        }

        if (chord.HasAlt)
        {
            text.Append("Alt+");
        }

        if (chord.HasShift)
        {
            text.Append("Shift+");
        }

        return text.Append(KeyText(chord.Key, layout, characterFor)).ToString();
    }

    /// <summary>The effective chords of a command, joined for a label ("Ctrl+= or Ctrl+Num +"), or an empty string.</summary>
    public static string Format(IEnumerable<Chord> chords, KeyLayout? layout = null, Func<int, string?>? characterFor = null) =>
        string.Join(", ", chords.Select(c => Format(c, layout, characterFor)));

    private static string KeyText(int key, KeyLayout? layout, Func<int, string?>? characterFor)
    {
        if (key is >= 0xBA and <= 0xDF)
        {
            var produced = characterFor?.Invoke(key) ?? layout?.CharacterFor(key);
            if (!string.IsNullOrEmpty(produced))
            {
                return produced;
            }

            return ChordKeys.NameOf(key) switch
            {
                "Plus" => "+",
                "Minus" => "-",
                "Comma" => ",",
                "Period" => ".",
                var other => other,
            };
        }

        return ChordKeys.NameOf(key) switch
        {
            "PageUp" => "Page Up",
            "PageDown" => "Page Down",
            "Escape" => "Esc",
            "Delete" => "Del",
            "Add" => "Num +",
            "Subtract" => "Num -",
            "Multiply" => "Num *",
            "Divide" => "Num /",
            "Decimal" => "Num .",
            var name when name.StartsWith("NumberPad", StringComparison.Ordinal) => "Num " + name["NumberPad".Length..],
            var name => name,
        };
    }

    /// <summary>A culture's layout from the table, or null for a culture it does not list.</summary>
    public static KeyLayout? LayoutFor(string culture) =>
        KeyLayout.Known.FirstOrDefault(l => string.Equals(l.Culture, culture, StringComparison.OrdinalIgnoreCase))
        ?? KeyLayout.Known.FirstOrDefault(l => culture.Length >= 2 && l.Culture.StartsWith(culture[..2], StringComparison.OrdinalIgnoreCase));

    /// <summary>The current culture's layout row, for the unit tests and for the fallback when MapVirtualKey cannot answer.</summary>
    public static KeyLayout? CurrentLayout() => LayoutFor(CultureInfo.CurrentCulture.Name);
}
