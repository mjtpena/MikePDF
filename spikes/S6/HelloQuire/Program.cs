using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace HelloQuire;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Timing.Mark("Main");
        Log.Write("Main args=" + string.Join(' ', args));
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Timing.Mark("ComWrappers.init.done");

        // S6.5: app instancing / redirection. Only when asked (--single), so the startup bench can run instances in parallel.
        if (!args.Contains("--launch-file") && (args.Contains("--single") || args.Any(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))) && DecideRedirection()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool DecideRedirection()
    {
        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        Log.Write("activation kind=" + activation.Kind);
        var key = AppInstance.FindOrRegisterForKey("QuireMain");
        if (key.IsCurrent)
        {
            Log.Write("I am the key instance (QuireMain)");
            key.Activated += (s, e) => Log.Write("REDIRECTED ACTIVATION RECEIVED kind=" + e.Kind + " data=" + DescribeData(e));
            return false;
        }
        Log.Write("Another instance owns QuireMain; redirecting");
        // Redirect on a worker thread and wait (STA-safe pattern from the Windows App SDK docs).
        var done = new ManualResetEvent(false);
        Exception? err = null;
        Task.Run(async () =>
        {
            try { await key.RedirectActivationToAsync(activation); }
            catch (Exception ex) { err = ex; }
            finally { done.Set(); }
        });
        while (!done.WaitOne(20)) { }
        Log.Write(err == null ? "Redirect completed" : "Redirect FAILED: " + err);
        return true;
    }

    private static string DescribeData(AppActivationArguments e)
    {
        try
        {
            if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l) return "Launch args='" + l.Arguments + "'";
            if (e.Kind == ExtendedActivationKind.File && e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs f) return "File count=" + f.Files.Count + " first=" + (f.Files.Count > 0 ? f.Files[0].Path : "");
            return e.Data?.GetType().FullName ?? "null";
        }
        catch (Exception ex) { return "ex " + ex.Message; }
    }
}
