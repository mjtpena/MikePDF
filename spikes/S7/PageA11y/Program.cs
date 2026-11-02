using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PageA11y;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Write("Main start");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Log.Write("ComWrappers ok");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("UNHANDLED: " + e.ExceptionObject);
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            Log.Write("Application.Start callback");
            _ = new App(args);
            Log.Write("App constructed");
        });
        return 0;
    }
}

internal static class Log
{
    private static readonly object L = new();
    public static string? Path;
    public static void Write(string s)
    {
        try
        {
            lock (L)
            {
                var p = Path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "pagea11y.log");
                File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
