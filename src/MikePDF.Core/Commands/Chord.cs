using System.Globalization;
using System.Text;

namespace MikePDF.Core.Commands;

/// <summary>The modifier keys of a chord. The Windows key is never part of a chord (docs/features/command-palette-shortcuts.md 3.2).</summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// A keyboard shortcut: zero or more of Ctrl, Alt and Shift plus one key, the key held as a Windows virtual-key code (which is also the value of WinUI's
/// <c>VirtualKey</c>). Chords are bound by virtual key, so Plus and Minus mean "the keys that are + and - on this layout, unshifted, where there are such
/// keys" (OQ-CMD-2, assumed). The canonical text is "Ctrl+Alt+Shift+Key" in that order.
/// </summary>
public readonly record struct Chord(ChordModifiers Modifiers, int Key)
{
    public bool HasControl => (Modifiers & ChordModifiers.Control) != 0;

    public bool HasAlt => (Modifiers & ChordModifiers.Alt) != 0;

    public bool HasShift => (Modifiers & ChordModifiers.Shift) != 0;

    /// <summary>The canonical text, for example <c>Ctrl+Shift+P</c>. The same text round-trips through <see cref="ChordParser"/>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        if (HasControl)
        {
            text.Append("Ctrl+");
        }

        if (HasAlt)
        {
            text.Append("Alt+");
        }

        if (HasShift)
        {
            text.Append("Shift+");
        }

        return text.Append(ChordKeys.NameOf(Key)).ToString();
    }

    /// <summary>The chord for canonical or loose text, or null when the text is not a chord. Use <see cref="ChordParser.TryParse"/> to learn why.</summary>
    public static Chord? FromText(string text) => ChordParser.TryParse(text, out var chord, out _) ? chord : null;
}

/// <summary>The key names a chord can use, and the virtual-key codes behind them.</summary>
public static class ChordKeys
{
    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> ByKey = [];

    static ChordKeys()
    {
        for (var c = 'A'; c <= 'Z'; c++)
        {
            Add(c.ToString(), c);
        }

        for (var d = 0; d <= 9; d++)
        {
            Add(d.ToString(CultureInfo.InvariantCulture), 0x30 + d);
            Add("Number" + d.ToString(CultureInfo.InvariantCulture), 0x30 + d, canonical: false);
            Add("NumberPad" + d.ToString(CultureInfo.InvariantCulture), 0x60 + d);
            Add("NumPad" + d.ToString(CultureInfo.InvariantCulture), 0x60 + d, canonical: false);
        }

        for (var f = 1; f <= 24; f++)
        {
            Add("F" + f.ToString(CultureInfo.InvariantCulture), 0x70 + f - 1);
        }

        Add("Backspace", 0x08);
        Add("Tab", 0x09);
        Add("Enter", 0x0D);
        Add("Return", 0x0D, canonical: false);
        Add("Pause", 0x13);
        Add("Escape", 0x1B);
        Add("Esc", 0x1B, canonical: false);
        Add("Space", 0x20);
        Add("PageUp", 0x21);
        Add("PgUp", 0x21, canonical: false);
        Add("PageDown", 0x22);
        Add("PgDn", 0x22, canonical: false);
        Add("End", 0x23);
        Add("Home", 0x24);
        Add("Left", 0x25);
        Add("Up", 0x26);
        Add("Right", 0x27);
        Add("Down", 0x28);
        Add("PrintScreen", 0x2C);
        Add("Insert", 0x2D);
        Add("Delete", 0x2E);
        Add("Del", 0x2E, canonical: false);
        Add("Multiply", 0x6A);
        Add("Add", 0x6B);
        Add("Subtract", 0x6D);
        Add("Decimal", 0x6E);
        Add("Divide", 0x6F);
        Add("Semicolon", 0xBA);
        Add("Plus", 0xBB);
        Add("Equals", 0xBB, canonical: false);
        Add("Comma", 0xBC);
        Add("Minus", 0xBD);
        Add("Period", 0xBE);
        Add("Slash", 0xBF);
        Add("Backtick", 0xC0);
        Add("LeftBracket", 0xDB);
        Add("Backslash", 0xDC);
        Add("RightBracket", 0xDD);
        Add("Quote", 0xDE);
    }

    private static void Add(string name, int key, bool canonical = true)
    {
        ByName[name] = key;
        if (canonical)
        {
            ByKey[key] = name;
        }
    }

    /// <summary>The virtual-key code for a key name (any case), or null.</summary>
    public static int? KeyOf(string name) => ByName.TryGetValue(name, out var key) ? key : null;

    /// <summary>The canonical name of a key; a code with no name gets "Key" and its number, which does not parse back and so cannot be bound.</summary>
    public static string NameOf(int key) => ByKey.TryGetValue(key, out var name) ? name : "Key" + key.ToString(CultureInfo.InvariantCulture);

    public static bool IsKnown(int key) => ByKey.ContainsKey(key);

    /// <summary>Every canonical key name, in code order. For tests and for the typed-chord help.</summary>
    public static IEnumerable<string> CanonicalNames => ByKey.OrderBy(p => p.Key).Select(p => p.Value);

    /// <summary>Keys that produce a printable character or edit text on their own: letters, digits, punctuation, Space and Enter.</summary>
    public static bool IsPrintable(int key) => key is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xDF) or 0x20 or 0x0D;

    /// <summary>Keys that move or change text in an input: the printable ones, the arrows, Home, End, Page Up and Down, Delete, Backspace and Insert.</summary>
    public static bool IsTextEditing(int key) => IsPrintable(key) || key is (>= 0x21 and <= 0x28) or 0x2D or 0x2E or 0x08;
}

/// <summary>Why chord text could not be read, and where in the text the problem is.</summary>
public sealed record ChordParseError(ChordParseProblem Problem, int Position, string Detail);

public enum ChordParseProblem
{
    Empty,
    UnknownKey,
    TwoKeys,
    NoKey,
    WindowsKey,
    DuplicateModifier,
}

/// <summary>
/// Reads chord text such as "ctrl+shift+p" or "Shift+Ctrl+P": any case, any modifier order, the aliases "Control", "Menu", "Esc", "=" and so on.
/// "Ctrl++" is Ctrl and the Plus key. Invalid text is an error with a position, never an exception.
/// </summary>
public static class ChordParser
{
    public const int MaxLength = 64;

    private static readonly Dictionary<string, ChordModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ChordModifiers.Control,
        ["Control"] = ChordModifiers.Control,
        ["Alt"] = ChordModifiers.Alt,
        ["Menu"] = ChordModifiers.Alt,
        ["Shift"] = ChordModifiers.Shift,
    };

    private static readonly HashSet<string> WindowsKeyNames = new(StringComparer.OrdinalIgnoreCase) { "Win", "Windows", "Super", "Meta", "Cmd", "Command", "LWin", "RWin" };

    public static bool TryParse(string? text, out Chord chord, out ChordParseError? error)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = new ChordParseError(ChordParseProblem.Empty, 0, "empty");
            return false;
        }

        if (text.Length > MaxLength)
        {
            error = new ChordParseError(ChordParseProblem.UnknownKey, MaxLength, "too long");
            return false;
        }

        var tokens = Tokenise(text);
        var modifiers = ChordModifiers.None;
        int? key = null;
        foreach (var (token, position) in tokens)
        {
            if (token.Length == 0)
            {
                error = new ChordParseError(position >= text.Length ? ChordParseProblem.NoKey : ChordParseProblem.UnknownKey, position, "empty part");
                return false;
            }

            if (WindowsKeyNames.Contains(token))
            {
                error = new ChordParseError(ChordParseProblem.WindowsKey, position, token);
                return false;
            }

            if (ModifierNames.TryGetValue(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    error = new ChordParseError(ChordParseProblem.DuplicateModifier, position, token);
                    return false;
                }

                if (key is not null)
                {
                    // A modifier after the key ("P+Ctrl") is a second key as far as the reader is concerned.
                    error = new ChordParseError(ChordParseProblem.TwoKeys, position, token);
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            var code = ChordKeys.KeyOf(token) ?? Alias(token);
            if (code is null)
            {
                error = new ChordParseError(ChordParseProblem.UnknownKey, position, token);
                return false;
            }

            if (key is not null)
            {
                error = new ChordParseError(ChordParseProblem.TwoKeys, position, token);
                return false;
            }

            key = code;
        }

        if (key is null)
        {
            error = new ChordParseError(ChordParseProblem.NoKey, text.Length, "no key");
            return false;
        }

        chord = new Chord(modifiers, key.Value);
        error = null;
        return true;
    }

    /// <summary>The chord, or an <see cref="FormatException"/> for text that is not one. For defaults written in code.</summary>
    public static Chord Parse(string text) =>
        TryParse(text, out var chord, out var error) ? chord : throw new FormatException($"'{text}' is not a chord: {error!.Problem} at {error.Position}");

    // Single characters that people type for the OEM keys.
    private static int? Alias(string token) => token switch
    {
        "=" => 0xBB,
        "+" => 0xBB,
        "-" => 0xBD,
        "," => 0xBC,
        "." => 0xBE,
        "/" => 0xBF,
        ";" => 0xBA,
        "'" => 0xDE,
        "[" => 0xDB,
        "]" => 0xDD,
        "\\" => 0xDC,
        "`" => 0xC0,
        _ => null,
    };

    // Splits on '+', except that a '+' with nothing before it (at the start, or right after another '+') is the key: "Ctrl++" is Ctrl and Plus.
    private static List<(string Token, int Position)> Tokenise(string text)
    {
        var tokens = new List<(string, int)>();
        var start = 0;
        var lastWasKeyPlus = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '+')
            {
                lastWasKeyPlus = false;
                continue;
            }

            if (i == start)
            {
                tokens.Add(("+", i));
                start = i + 1;
                lastWasKeyPlus = true;
                continue;
            }

            tokens.Add((text[start..i].Trim(), start));
            start = i + 1;
            lastWasKeyPlus = false;
        }

        if (start < text.Length)
        {
            tokens.Add((text[start..].Trim(), start));
        }
        else if (!lastWasKeyPlus && start > 0)
        {
            tokens.Add((string.Empty, start)); // the text ends with a separator: "Ctrl+"
        }

        return tokens;
    }
}
