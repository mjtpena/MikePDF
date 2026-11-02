using System.Runtime.InteropServices;

namespace MikePDF.UI.Tests.Pages;

/// <summary>Key chords with virtual-key codes, the way the keyboard driver reports them to a program (what the Windows Forms SendKeys does): modifiers down, the key, modifiers up.</summary>
internal static partial class NativeKeyboard
{
    public const byte Shift = 0x10;
    public const byte Control = 0x11;
    public const byte Enter = 0x0D;
    public const byte Left = 0x25;
    public const byte Up = 0x26;
    public const byte Right = 0x27;
    public const byte Down = 0x28;

    private const uint KeyUp = 0x0002;
    private const uint Extended = 0x0001;

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static void Chord(params byte[] keys)
    {
        foreach (var key in keys[..^1])
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
        }

        var last = keys[^1];
        // The keys of the navigation block are "extended": without the flag Shift turns them into the number pad's keys.
        var flags = last is Left or Up or Right or Down or 0x21 or 0x22 or 0x23 or 0x24 or 0x2D or 0x2E ? Extended : 0u;
        keybd_event(last, 0, flags, UIntPtr.Zero);
        keybd_event(last, 0, flags | KeyUp, UIntPtr.Zero);
        foreach (var key in keys[..^1].Reverse())
        {
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
    }
}
