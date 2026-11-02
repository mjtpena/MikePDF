using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MikePDF.App.Services;
using MikePDF.Core.Windowing;
using Windows.ApplicationModel.Activation;

namespace MikePDF.App;

public partial class App : Application
{
    private static DispatcherQueue? _queue;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        var host = AppHost.Create(AppServices.Create());

        // What this launch was asked to open waits in the activation relay until the first window is ready and the session is restored (docs/features/tabs-windows-session.md 3.7).
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var request = RequestFrom(activation);
        if (request.Launch.IsEmpty && request.ItemsWithoutPath == 0)
        {
            request = new ActivationRequest(LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1).Where(a => !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))), 0);
        }

        if (!request.Launch.IsEmpty || request.ItemsWithoutPath > 0)
        {
            host.Post(request);
        }

        _ = StartAsync(host);
    }

    private static async Task StartAsync(AppHost host)
    {
        var loaded = host.LoadSession();
        var action = SessionPolicy.Decide(host.CurrentRestoreMode(), loaded.Session);
        var first = host.CreateWindow();
        try
        {
            if (action == RestoreAction.Restore)
            {
                host.Restore(loaded.Session!, first);
            }
            else if (action == RestoreAction.AskThenRestore && await first.AskRestoreAsync(loaded.Session!.DocumentCount))
            {
                host.Restore(loaded.Session!, first);
            }
            else if (loaded.Problem is not (SessionProblem.None or SessionProblem.Missing))
            {
                first.ShowNotice(Strings.Get("Restore_Failed")); // a damaged file counts as empty; this is said once, and the next write replaces it
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Start-up failed: " + e.GetType().Name); // the window stays usable; only the kind is kept: nothing from the session
        }
        finally
        {
            host.ActivationsReady();
            // After first paint and the restore: the Jump List is not part of start-up.
            _queue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = JumpListService.BuildAsync());
        }
    }

    /// <summary>A second launch handed over its arguments. Runs on a background thread.</summary>
    internal static void OnRedirectedActivation(AppActivationArguments arguments)
    {
        var request = RequestFrom(arguments);
        _queue?.TryEnqueue(() => AppHost.Current?.Post(request));
    }

    private static ActivationRequest RequestFrom(AppActivationArguments arguments)
    {
        var paths = new List<string>();
        var withoutPath = 0;
        var launch = LaunchRequest.Empty;
        try
        {
            switch (arguments.Kind)
            {
                case ExtendedActivationKind.File when arguments.Data is IFileActivatedEventArgs file:
                    foreach (var item in file.Files)
                    {
                        // An item with no path (a cloud-only or virtual item) cannot be opened in place; it is said so and nothing is opened (OQ-FA-2).
                        if (item.Path is { Length: > 0 } path)
                        {
                            paths.Add(path);
                        }
                        else
                        {
                            withoutPath++;
                        }
                    }

                    launch = new LaunchRequest(paths.Take(ActivationLimits.Maximum).ToList(), false, false);
                    break;
                case ExtendedActivationKind.Launch when arguments.Data is ILaunchActivatedEventArgs launchArguments:
                    // Depending on how it was started, the argument string may begin with the program's own name.
                    launch = LaunchArguments.Parse(CommandLine.Split(launchArguments.Arguments).Where(a => !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
                    break;
                default:
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An activation we do not understand, or one whose sender has gone away (the files come from the launching process), opens nothing.
        }

        return new ActivationRequest(launch, withoutPath);
    }
}

/// <summary>Splits a command line the way Windows does for ordinary programs (quotes group; backslashes only matter before a quote).</summary>
internal static class CommandLine
{
    public static IEnumerable<string> Split(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var started = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    yield return current.ToString();
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            yield return current.ToString();
        }
    }
}
