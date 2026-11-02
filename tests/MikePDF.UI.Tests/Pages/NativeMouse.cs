using System.Drawing;
using System.Runtime.InteropServices;

namespace MikePDF.UI.Tests.Pages;

/// <summary>
/// Mouse input as hardware makes it: absolute moves over the whole virtual desktop (SendInput), in small steps. Moving the cursor with
/// SetCursorPos, which is what a quick drag in a test tool does, moves the pointer without WinUI ever seeing a pointer-move, so a drag made that
/// way selects nothing. MikePDF's selection is tested with the real thing.
/// </summary>
internal static partial class NativeMouse
{
    private const uint Move = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint XDown = 0x0080;
    private const uint XUp = 0x0100;
    private const uint Absolute = 0x8000;
    private const uint VirtualDesk = 0x4000;
    private const int VirtualLeft = 76;
    private const int VirtualTop = 77;
    private const int VirtualWidth = 78;
    private const int VirtualHeight = 79;

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    public static void MoveTo(Point point)
    {
        var left = GetSystemMetrics(VirtualLeft);
        var top = GetSystemMetrics(VirtualTop);
        var width = GetSystemMetrics(VirtualWidth);
        var height = GetSystemMetrics(VirtualHeight);
        var x = (uint)Math.Round((point.X - left) * 65535.0 / width);
        var y = (uint)Math.Round((point.Y - top) * 65535.0 / height);
        mouse_event(Move | Absolute | VirtualDesk, x, y, 0, UIntPtr.Zero);
    }

    /// <summary>Clicks <paramref name="count"/> times in a row at one point (a double click is two clicks within the system's double-click time).</summary>
    public static void Click(Point point, int count = 1)
    {
        MoveTo(point);
        Thread.Sleep(150);
        for (var i = 0; i < count; i++)
        {
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
        }
    }

    /// <summary>The mouse's back button (X1) or forward button (X2), pressed and released with the pointer at <paramref name="point"/>.</summary>
    public static void SideButton(Point point, bool forward)
    {
        MoveTo(point);
        Thread.Sleep(150);
        var button = forward ? 2u : 1u;
        mouse_event(XDown, 0, 0, button, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(XUp, 0, 0, button, UIntPtr.Zero);
        Thread.Sleep(60);
    }

    /// <summary>Presses at <paramref name="from"/>, moves to <paramref name="to"/> in steps of a few pixels, and lets go.</summary>
    public static void Drag(Point from, Point to)
    {
        MoveTo(from);
        Thread.Sleep(120);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y)) / 6.0));
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(new Point(from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps)));
            Thread.Sleep(12);
        }

        Thread.Sleep(80);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }
}
