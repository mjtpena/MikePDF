using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MikePDF.App.Services;

namespace MikePDF.App;

/// <summary>
/// Our own entry point, so that single-instance redirection runs before any XAML does (S6): a second launch (a double-click on a PDF, the
/// <c>mikepdf.exe</c> alias with a file name) hands its arguments to the running window and exits. The window then replaces its document.
/// </summary>
public static class Program
{
    public const string InstanceKey = "MikePDF.Main";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
#if MIKEPDF_SELFTEST
        if (SelfTest.Requested(args))
        {
            return SelfTest.Run(args);
        }
#endif
        if (!AppPaths.IsPackaged)
        {
            Console.Error.WriteLine("MikePDF runs as an installed package. Build one with build/package.ps1, or register the build output with Add-AppxPackage -Register.");
            return 3;
        }

        var key = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!key.IsCurrent)
        {
            Redirect(key);
            return 0;
        }

        key.Activated += (_, e) => App.OnRedirectedActivation(e);
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Hands this launch's arguments to the running instance. On a worker thread, because the STA thread must not block on it (S6).</summary>
    private static void Redirect(AppInstance key)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await key.RedirectActivationToAsync(activation);
            }
            finally
            {
                done.Set();
            }
        });
        while (!done.WaitOne(20))
        {
            // Pump nothing: this process has no window.
        }
    }
}
