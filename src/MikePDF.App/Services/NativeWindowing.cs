using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using MikePDF.Core.Windowing;
using Windows.Graphics;

namespace MikePDF.App.Services;

/// <summary>The few Win32 calls the window model needs: the restored rectangle of a window, the monitors, starting a window move, and the end-of-session message.</summary>
internal static class NativeWindowing
{
    private const int MonitorDpiEffective = 0;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacementInfo
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
        public Rect Device;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacementInfo placement);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>Whether the left mouse button is down now.</summary>
    public static bool LeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    public static uint DpiOf(nint hwnd) => GetDpiForWindow(hwnd);

    /// <summary>Where the pointer is on the screen, in physical pixels.</summary>
    public static (int X, int Y)? CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            var areas = DisplayArea.FindAll();
            for (var i = 0; i < areas.Count; i++) // (not foreach: the projected list cannot be enumerated)
            {
                var area = areas[i];
                var work = area.WorkArea;
                var dpi = 96u;
                var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
                if (monitor != 0 && GetDpiForMonitor(monitor, MonitorDpiEffective, out var x, out _) == 0 && x > 0)
                {
                    dpi = x;
                }

                list.Add(new MonitorInfo(work.X, work.Y, work.X + work.Width, work.Y + work.Height, (int)dpi, area.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or InvalidCastException)
        {
            // No answer from the display stack: the stored rectangle is used as it is.
        }

        return list;
    }

    /// <summary>The restored-state rectangle of a window (the one it returns to when un-maximised) and whether it is maximised now.</summary>
    public static (StoredBounds Bounds, bool Maximized) CaptureBounds(AppWindow window, nint hwnd)
    {
        var maximized = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var rect = new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
        if (maximized)
        {
            var placement = new WindowPlacementInfo { Length = (uint)Marshal.SizeOf<WindowPlacementInfo>() };
            if (GetWindowPlacement(hwnd, ref placement))
            {
                var n = placement.NormalPosition;
                rect = new RectInt32(n.Left, n.Top, n.Right - n.Left, n.Bottom - n.Top);
            }
        }

        var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dpi = (int)Math.Clamp(GetDpiForWindow(hwnd), 24u, 1000u);
        return (new StoredBounds(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height), dpi, display), maximized);
    }

    private static readonly List<SubclassProc> Procedures = [];

    /// <summary>Calls <paramref name="onEndSession"/> when Windows asks the window whether it may end the session (shutdown, sign-out). The answer is always "yes": there is no dialog.</summary>
    public static void WatchEndSession(nint hwnd, Action onEndSession)
    {
        SubclassProc procedure = (window, message, wParam, lParam, id, data) =>
        {
            if (message is WmQueryEndSession or WmEndSession)
            {
                try
                {
                    onEndSession();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Writing the session must never stop a shutdown.
                }

                if (message == WmQueryEndSession)
                {
                    return 1; // TRUE: this window does not stand in the way
                }
            }

            return DefSubclassProc(window, message, wParam, lParam);
        };
        Procedures.Add(procedure); // the native side holds only a thunk: keep the delegate alive for the life of the process
        SetWindowSubclass(hwnd, procedure, 1, 0);
    }
}
