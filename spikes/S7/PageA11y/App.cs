using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PageA11y;

public sealed class App : Application
{
    private readonly string[] _args;
    private MainWindow? _window;
    private IntPtr _hwnd;
    public IntPtr WindowHandle => _hwnd;
    public void SetWindowHandle(IntPtr h) => _hwnd = h;

    public App(string[] args)
    {
        _args = args;
        Log.Write("App ctor");
        // NOTE: `new XamlControlsResources()` in a code-only App (no App.xaml) kills the process with a stowed exception
        // (combase 0x8000FFFF) on WinAppSDK 2.5.1, so controls use the built-in base styles. See report, Surprises.
        Log.Write("resources ok");
        UnhandledException += (_, e) => { Log.Write("XAML UNHANDLED: " + e.Exception); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write("OnLaunched");
        var o = Options.Parse(_args);
        _window = new MainWindow(o);
        Log.Write("MainWindow ctor done");
        _window.Activate();
        Log.Write("Activated");
    }
}

public sealed class Options
{
    public string? Fixture;
    public int Rotation;
    public double Zoom = 1.0;
    public OverlayMode Overlay = OverlayMode.None;
    public string? ShotsDir;      // take screenshots of every fixture x rotation, then exit
    public string? FixturesDir;
    public string? LogPath;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--fixture": o.Fixture = a[++i]; break;
                case "--rot": o.Rotation = int.Parse(a[++i]); break;
                case "--zoom": o.Zoom = double.Parse(a[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--overlay": o.Overlay = Enum.Parse<OverlayMode>(a[++i], true); break;
                case "--shots": o.ShotsDir = a[++i]; break;
                case "--fixtures-dir": o.FixturesDir = a[++i]; break;
                case "--log": o.LogPath = a[++i]; break;
            }
        }
        if (o.LogPath != null) Log.Path = o.LogPath;
        return o;
    }
}

[Flags]
public enum OverlayMode { None = 0, Lines = 1, Words = 2, Chars = 4, Selection = 8, All = 15 }
