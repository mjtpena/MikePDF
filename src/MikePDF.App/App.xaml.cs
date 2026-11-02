using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MikePDF.App.Services;
using MikePDF.Core.Persistence;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace MikePDF.App;

public partial class App : Application
{
    private static DispatcherQueue? _queue;
    private static MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        _window = new MainWindow(new DocumentStore(AppPaths.DocumentStorePath));
        _window.Activate();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var paths = PathsFrom(activation);
        if (paths.Count == 0)
        {
            paths = [.. Environment.GetCommandLineArgs().Skip(1).Where(File.Exists)];
        }

        if (paths.Count > 0)
        {
            _ = _window.OpenPathAsync(paths[0]);
        }
    }

    /// <summary>A second launch handed over its arguments. Runs on a background thread.</summary>
    internal static void OnRedirectedActivation(AppActivationArguments arguments)
    {
        var paths = PathsFrom(arguments);
        _queue?.TryEnqueue(() => _ = _window?.HandleActivationAsync(paths));
    }

    private static List<string> PathsFrom(AppActivationArguments arguments)
    {
        var paths = new List<string>();
        try
        {
            switch (arguments.Kind)
            {
                case ExtendedActivationKind.File when arguments.Data is IFileActivatedEventArgs file:
                    paths.AddRange(file.Files.OfType<IStorageFile>().Select(f => f.Path));
                    break;
                case ExtendedActivationKind.Launch when arguments.Data is ILaunchActivatedEventArgs launch:
                    // Depending on how it was started, the argument string may begin with the program's own name.
                    paths.AddRange(CommandLine.Split(launch.Arguments).Where(a => File.Exists(a) && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
                    break;
                default:
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An activation we do not understand, or one whose sender has gone away (the files come from the launching process), opens nothing.
        }

        return paths;
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
