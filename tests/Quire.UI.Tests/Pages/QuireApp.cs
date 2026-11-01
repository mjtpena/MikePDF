using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Quire.UI.Tests.Pages;

/// <summary>
/// The only place that knows FlaUI (ADR-0009): a running Quire, started through its execution alias like a user would, and the controls a
/// journey needs. If FlaUI has to be replaced, only this folder changes.
/// </summary>
public sealed class QuireApp : IDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly UIA3Automation _automation;
    private readonly Application _application;

    private QuireApp(UIA3Automation automation, Application application, Window window)
    {
        _automation = automation;
        _application = application;
        Window = window;
    }

    public Window Window { get; }

    public int ProcessId => _application.ProcessId;

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Starts Quire (alias <c>quire.exe</c>) with an optional document and waits for its window. Any running Quire is closed first.</summary>
    public static QuireApp Launch(string? document = null)
    {
        KillAll();
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "quire.exe");
        if (!File.Exists(alias))
        {
            throw new FileNotFoundException("quire.exe (the app execution alias) is not installed. Run build/run-ui-tests.ps1, which registers the package.", alias);
        }

        var start = new ProcessStartInfo(alias) { UseShellExecute = false };
        if (document is not null)
        {
            start.ArgumentList.Add(document);
        }

        Process.Start(start)?.Dispose();
        var process = Retry.WhileNull(() => Process.GetProcessesByName("Quire").FirstOrDefault(p => p.MainWindowHandle != 0), Patience, TimeSpan.FromMilliseconds(200)).Result
            ?? throw new TimeoutException("Quire did not show a window.");
        var automation = new UIA3Automation();
        var application = Application.Attach(process);
        var window = Retry.WhileNull(() => application.GetMainWindow(automation), Patience).Result ?? throw new TimeoutException("No main window.");
        return new QuireApp(automation, application, window);
    }

    public static void KillAll()
    {
        foreach (var name in new[] { "Quire", "Quire.Engine" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    // ---- elements -------------------------------------------------------------------------------

    public AutomationElement? Find(string automationId) => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    public AutomationElement Require(string automationId) =>
        Retry.WhileNull(() => Find(automationId), Patience, TimeSpan.FromMilliseconds(150)).Result ?? throw new InvalidOperationException("No element with automation id " + automationId);

    public bool Exists(string automationId) => Find(automationId) is not null;

    /// <summary>Waits until the condition holds (polling), or throws with a description.</summary>
    public void WaitFor(string description, Func<bool> condition, TimeSpan? timeout = null)
    {
        if (_application.HasExited)
        {
            throw new InvalidOperationException("Quire exited while waiting for " + description);
        }

        var result = Retry.WhileFalse(condition, timeout ?? Patience, TimeSpan.FromMilliseconds(150));
        if (!result.Success)
        {
            throw new TimeoutException("Timed out waiting for " + description);
        }
    }

    public string PageIndicatorName => Find("PageIndicator")?.Name ?? string.Empty;

    public string ZoomName => Find("ZoomButton")?.Name ?? string.Empty;

    /// <summary>1-based current page read from the indicator's accessible name ("Page 3 of 12. ..."), or 0.</summary>
    public int CurrentPage
    {
        get
        {
            var name = PageIndicatorName;
            var match = System.Text.RegularExpressions.Regex.Match(name, @"Page (\d+) of (\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    public int PageCount
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(PageIndicatorName, @"Page (\d+) of (\d+)");
            return match.Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    public int ZoomPercent
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(ZoomName, @"Zoom (\d+) percent");
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    public void WaitForDocument(int pages) => WaitFor($"a document of {pages} pages", () => PageCount == pages);

    // ---- actions --------------------------------------------------------------------------------

    public void Click(string automationId) => Require(automationId).AsButton().Invoke();

    public void FocusPages()
    {
        Require("PageSurface").Focus();
        Wait.UntilInputIsProcessed();
    }

    public void Press(params VirtualKeyShort[] keys)
    {
        Window.SetForeground();
        Keyboard.Type(keys);
        Wait.UntilInputIsProcessed();
    }

    public void PressWithControl(VirtualKeyShort key)
    {
        Window.SetForeground();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, key);
        Wait.UntilInputIsProcessed();
    }

    public static void HoldControlShift(VirtualKeyShort key)
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, key);
        Wait.UntilInputIsProcessed();
    }

    public void Type(string text)
    {
        Window.SetForeground();
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>A screenshot of the window (visible frame) saved as a PNG; returns the path.</summary>
    public string Capture(string label)
    {
        Window.SetForeground();
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(400));
        var directory = Environment.GetEnvironmentVariable("QUIRE_SHOTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, label + ".png");
        FlaUI.Core.Capturing.Capture.Element(Window).ToFile(path);
        return path;
    }

    /// <summary>Closes the window the way a user does and waits for the process to end, so the app saves its state.</summary>
    public void CloseGracefully()
    {
        Window.Close();
        Retry.WhileFalse(() => _application.HasExited, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
    }

    public void Dispose()
    {
        _automation.Dispose();
        _application.Dispose();
        KillAll();
    }
}

/// <summary>A test that needs the installed package and an interactive desktop. Skipped unless QUIRE_UI_TESTS=1 (set by build/run-ui-tests.ps1).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("QUIRE_UI_TESTS") != "1")
        {
            Skip = "UI tests need an interactive desktop and the installed package: run build/run-ui-tests.ps1";
        }
    }
}
