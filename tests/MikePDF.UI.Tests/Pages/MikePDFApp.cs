using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace MikePDF.UI.Tests.Pages;

/// <summary>
/// The only place that knows FlaUI (ADR-0009): a running MikePDF, started through its execution alias like a user would, and the controls a
/// journey needs. If FlaUI has to be replaced, only this folder changes.
/// </summary>
public sealed class MikePDFApp : IDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly UIA3Automation _automation;
    private readonly Application _application;

    private readonly bool _owner;

    private MikePDFApp(UIA3Automation automation, Application application, Window window, bool owner = true)
    {
        _automation = automation;
        _application = application;
        Window = window;
        _owner = owner;
    }

    public UIA3Automation Automation => _automation;

    /// <summary>Every top-level window of this MikePDF (a torn-out tab is another window of the same process).</summary>
    public IReadOnlyList<Window> AllWindows() => _application.GetAllTopLevelWindows(_automation);

    /// <summary>Another window of the same MikePDF, with the same page-object helpers. Disposing it does nothing; the app belongs to the first.</summary>
    public MikePDFApp ForWindow(Window window) => new(_automation, _application, window, owner: false);

    /// <summary>The tabs of the window, left to right.</summary>
    public IReadOnlyList<AutomationElement> TabItems() =>
        Window.FindFirstDescendant(cf => cf.ByAutomationId("DocumentTabs"))?.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).OrderBy(t => t.BoundingRectangle.Left).ToList() ?? [];

    public IReadOnlyList<string> TabNames() => [.. TabItems().Select(t => t.Name)];

    /// <summary>The name of the tab that is selected.</summary>
    public string SelectedTabName() => TabItems().FirstOrDefault(t => t.Patterns.SelectionItem.IsSupported && t.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault)?.Name ?? string.Empty;

    /// <summary>Starts MikePDF's alias with these files, the way Explorer does: a running instance takes them, and this process ends.</summary>
    public static void OpenInRunningApp(params string[] paths)
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "mikepdf.exe");
        var start = new ProcessStartInfo(alias) { UseShellExecute = false };
        foreach (var path in paths)
        {
            start.ArgumentList.Add(path);
        }

        Process.Start(start)?.Dispose();
    }

    public Window Window { get; }

    public int ProcessId => _application.ProcessId;

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Starts MikePDF (alias <c>mikepdf.exe</c>) with an optional document and waits for its window. Any running MikePDF is closed first.</summary>
    public static MikePDFApp Launch(string? document = null, string restore = "Never", bool fakeDirty = false, IReadOnlyDictionary<string, string>? environment = null, params string[] moreDocuments)
    {
        KillAll();
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "mikepdf.exe");
        if (!File.Exists(alias))
        {
            throw new FileNotFoundException("mikepdf.exe (the app execution alias) is not installed. Run build/run-ui-tests.ps1, which registers the package.", alias);
        }

        var start = new ProcessStartInfo(alias) { UseShellExecute = false };
        // A killed run must not make the next one ask to restore it: journeys start with the setting Never unless they are about the session (the app reads this only for that).
        start.Environment["MIKEPDF_TEST_RESTORE"] = restore;
        start.Environment["MIKEPDF_TEST_FAKE_DIRTY"] = fakeDirty ? "1" : "0";
        WaitUntilNoInstance(); // the previous instance's process, and with it its single-instance registration, must be gone: a launch that finds one is handed to it and ends
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        if (document is not null)
        {
            start.ArgumentList.Add(document);
        }

        foreach (var more in moreDocuments)
        {
            start.ArgumentList.Add(more);
        }

        // Right after the previous instance was killed the package can still be shutting down, and the alias refuses to start (access denied) for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Process.Start(start)?.Dispose();
                break;
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 10)
            {
                Thread.Sleep(1000);
            }
        }

        // If the launch was still handed to an instance that was going away, no MikePDF is left and no window comes: start it again (a few times) rather than fail on the race.
        Process? process = null;
        for (var launch = 1; process is null; launch++)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(launch < 3 ? 12 : 30);
            while (process is null && DateTime.UtcNow < deadline)
            {
                process = Process.GetProcessesByName("MikePDF").FirstOrDefault(p => p.MainWindowHandle != 0);
                if (process is null)
                {
                    Thread.Sleep(200);
                    if (launch < 3 && Process.GetProcessesByName("MikePDF").Length == 0 && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(8))
                    {
                        break; // nothing is running and nothing will show: launch again
                    }
                }
            }

            if (process is null)
            {
                if (launch >= 3)
                {
                    throw new TimeoutException("MikePDF did not show a window.");
                }

                WaitUntilNoInstance();
                Process.Start(start)?.Dispose();
            }
        }

        var automation = new UIA3Automation();
        var application = Application.Attach(process);
        var window = Retry.WhileNull(() => application.GetMainWindow(automation), Patience).Result ?? throw new TimeoutException("No main window.");
        return new MikePDFApp(automation, application, window);
    }

    /// <summary>
    /// Ends every MikePDF (a launch must not be redirected to an instance that is already running) and the engines those instances started: their
    /// descendants, found by parent process id. A MikePDF.Engine that belongs to anything else (another agent's integration test, the render tool,
    /// a person's own reader) is none of ours and is left alone.
    /// </summary>
    public static void KillAll()
    {
        foreach (var app in Process.GetProcessesByName("MikePDF"))
        {
            using (app)
            {
                foreach (var (id, exe) in ProcessTree.DescendantsOf(app.Id).Where(d => string.Equals(d.Exe, "MikePDF.Engine.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    Kill(id);
                }

                try
                {
                    app.Kill(entireProcessTree: true);
                    app.WaitForExit(5000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }
        }
    }

    private static void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Waits until no MikePDF process has existed for about a second: the previous instance has exited, so its single-instance registration is gone.</summary>
    public static void WaitUntilNoInstance()
    {
        var quietSince = DateTime.UtcNow;
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < limit)
        {
            if (Process.GetProcessesByName("MikePDF").Length > 0)
            {
                quietSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(1000))
            {
                return;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>The ids of the engines this MikePDF started (its child processes named MikePDF.Engine), now.</summary>
    public IReadOnlySet<int> EngineIds() =>
        ProcessTree.DescendantsOf(ProcessId).Where(d => string.Equals(d.Exe, "MikePDF.Engine.exe", StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToHashSet();

    /// <summary>Ends the engines this MikePDF started, and only those. Returns how many it ended.</summary>
    public int KillEngines()
    {
        var ids = EngineIds();
        foreach (var id in ids)
        {
            Kill(id);
        }

        return ids.Count;
    }

    // ---- elements -------------------------------------------------------------------------------

    public AutomationElement? Find(string automationId)
    {
        try
        {
            return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // the tree changed while it was searched (a pane opened, a node expanded): the caller looks again
        }
    }

    public AutomationElement Require(string automationId) =>
        Retry.WhileNull(() => Find(automationId), Patience, TimeSpan.FromMilliseconds(150)).Result ?? throw new InvalidOperationException("No element with automation id " + automationId);

    public bool Exists(string automationId) => Find(automationId) is not null;

    /// <summary>Waits until a value is what is expected, or throws saying what it was.</summary>
    public void WaitForEqual(string description, string expected, Func<string> actual, TimeSpan? timeout = null)
    {
        if (_application.HasExited)
        {
            throw new InvalidOperationException("MikePDF exited while waiting for " + description);
        }

        var last = string.Empty;
        var result = Retry.WhileFalse(() => (last = actual()) == expected, timeout ?? Patience, TimeSpan.FromMilliseconds(150));
        if (!result.Success)
        {
            throw new TimeoutException($"Timed out waiting for {description}: wanted [{expected}], last saw [{last}]");
        }
    }

    /// <summary>Waits until the condition holds (polling), or throws with a description.</summary>
    public void WaitFor(string description, Func<bool> condition, TimeSpan? timeout = null)
    {
        if (_application.HasExited)
        {
            throw new InvalidOperationException("MikePDF exited while waiting for " + description);
        }

        var result = Retry.WhileFalse(condition, timeout ?? Patience, TimeSpan.FromMilliseconds(150));
        if (!result.Success)
        {
            TryCapture("timeout-" + new string(description.Where(char.IsLetterOrDigit).Take(40).ToArray()));
            throw new TimeoutException("Timed out waiting for " + description);
        }
    }

    public PalettePage Palette => new(this);

    public SettingsPage Settings => new(this);

    /// <summary>The shortcut text a control announces (UIA AcceleratorKey), for example "Ctrl+2".</summary>
    public string AcceleratorKey(string automationId) => Require(automationId).Properties.AcceleratorKey.ValueOrDefault ?? string.Empty;

    public string HelpText(string automationId) => Require(automationId).Properties.HelpText.ValueOrDefault ?? string.Empty;

    public bool HasFocus(string automationId) => Find(automationId)?.Properties.HasKeyboardFocus.ValueOrDefault ?? false;

    /// <summary>Subscribes to the notifications screen readers hear (UIA Notification events) for the whole window; dispose to stop.</summary>
    public IDisposable ListenForNotifications(List<(string Text, string Activity)> into)
    {
        var listening = new Listening();
        Window.RegisterNotificationEvent(TreeScope.Subtree, (_, _, _, text, activity) =>
        {
            if (listening.Active)
            {
                lock (into)
                {
                    into.Add((text, activity));
                }
            }
        });
        return listening; // the registration itself ends with the app, which each journey closes
    }

    private sealed class Listening : IDisposable
    {
        public volatile bool Active = true;

        public void Dispose() => Active = false;
    }

    public string PageIndicatorName => Find("PageIndicator")?.Name ?? string.Empty;

    public string ZoomName => Find("ZoomButton")?.Name ?? string.Empty;

    private static readonly System.Text.RegularExpressions.Regex IndicatorPattern = new(@"Pages? (\d+)(?: and (\d+))? of (\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The pages the indicator names, 1-based and the total ("Page 3 of 12" is (3, 3, 12); "Pages 4 and 5 of 40" is (4, 5, 40)); (0, 0, 0) when it names none.</summary>
    public (int First, int Last, int Total) PagesInView()
    {
        var match = IndicatorPattern.Match(PageIndicatorName);
        if (!match.Success)
        {
            return (0, 0, 0);
        }

        var first = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : first;
        return (first, last, int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>1-based current page (the first of a spread) read from the indicator's accessible name ("Page 3 of 12. ..."), or 0.</summary>
    public int CurrentPage => PagesInView().First;

    public int PageCount => PagesInView().Total;

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

    /// <summary>Presses a control the way its pattern says: invoke a button, toggle a toggle, select a tab.</summary>
    public void Click(string automationId)
    {
        var element = Require(automationId);
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
        }
        else if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            element.Click();
        }
    }

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

    /// <summary>Presses the keys together (a chord): Shift+Enter, Ctrl+Shift+Right.</summary>
    public void Chord(params VirtualKeyShort[] keys)
    {
        Window.SetForeground();
        NativeKeyboard.Chord([.. keys.Select(k => (byte)(int)k)]);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>The count or progress under the find box.</summary>
    public string FindStatus => Find("FindStatus")?.Properties.Name.ValueOrDefault ?? string.Empty;

    /// <summary>Selects a phrase through the Text pattern, as a screen reader does, and returns what was selected.</summary>
    public string SelectPhrase(string phrase)
    {
        var range = PageText.DocumentRange.FindText(phrase, false, true) ?? throw new InvalidOperationException("Not on the pages in view: " + phrase);
        range.Select();
        return range.GetText(-1);
    }

    /// <summary>An item of a list or tree by its name; waits for it, because lists make their items as they scroll into view.</summary>
    /// <summary>Scrolls an item of a list or tree into view and clicks it.</summary>
    public static void ClickItem(AutomationElement item)
    {
        if (item.Patterns.ScrollItem.IsSupported)
        {
            item.Patterns.ScrollItem.Pattern.ScrollIntoView();
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(300));
        }

        item.Click();
    }

    public static AutomationElement WaitForItem(AutomationElement container, string name, ControlType type = ControlType.ListItem, TimeSpan? timeout = null) =>
        WaitForItemOrNull(container, name, type, timeout ?? Patience) ?? throw new TimeoutException("No item named " + name);

    public static AutomationElement? WaitForItemOrNull(AutomationElement container, string name, ControlType type = ControlType.ListItem, TimeSpan? timeout = null) =>
        Retry.WhileNull(() => container.FindFirstDescendant(cf => cf.ByControlType(type).And(cf.ByName(name))), timeout ?? TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), ignoreException: true).Result;

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
    /// <summary>Clicks the empty bottom margin of the centred page: focus returns to the pages by pointer, without the focus rectangle that keyboard use leaves, which a listing picture does not want. A click doesn't change how a control that already has focus got it, so focus moves away first.</summary>
    public void ClickPageMargin()
    {
        Require("PageIndicator").Focus();
        Wait.UntilInputIsProcessed();
        var frame = VisibleFrame();
        NativeMouse.Click(new System.Drawing.Point(frame.Left + (frame.Width / 2), frame.Bottom - 60));
        Thread.Sleep(300);
    }

    public string Capture(string label)
    {
        Window.SetForeground();
        // Park the pointer on the empty gutter beside the page so no hover tooltip or highlight lands in the picture.
        var frame = VisibleFrame();
        NativeMouse.MoveTo(new System.Drawing.Point(frame.Left + (frame.Width / 2), frame.Bottom - 60));
        Thread.Sleep(250);
        NativeMouse.MoveTo(new System.Drawing.Point(frame.Left + 20, frame.Bottom - 120));
        Thread.Sleep(1500);
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(400));
        var directory =Environment.GetEnvironmentVariable("MIKEPDF_SHOTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, label + ".png");
        // The visible frame, without the invisible resize border (other windows show through that).
        using var picture = FlaUI.Core.Capturing.Capture.Rectangle(VisibleFrame());
        picture.ToFile(path);
        return path;
    }

    // ---- window size, the text of the pages, the mouse and the clipboard -------------------------------------------------------

    /// <summary>Resizes and centres the window (outer size in pixels), for screenshots at a size the Store accepts.</summary>
    public void ResizeTo(int width, int height)
    {
        var handle = new IntPtr(Window.Properties.NativeWindowHandle.Value);
        SetWindowPos(handle, IntPtr.Zero, 40, 20, width, height, 0x0004 | 0x0040);
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(500));
    }

    private System.Drawing.Rectangle VisibleFrame()
    {
        var handle = new IntPtr(Window.Properties.NativeWindowHandle.Value);
        if (DwmGetWindowAttribute(handle, 9, out var frame, System.Runtime.InteropServices.Marshal.SizeOf<NativeRect>()) != 0)
        {
            return Window.BoundingRectangle;
        }

        return System.Drawing.Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // The page surface is named PresentationSurface while a presentation runs.
    private AutomationElement TextSurface => Find("PageSurface") ?? Require("PresentationSurface");

    private FlaUI.Core.Patterns.ITextPattern PageText => TextSurface.Patterns.Text.Pattern;

    public bool PageTextSupported => TextSurface.Patterns.Text.IsSupported;

    /// <summary>All the text the page surface offers to a screen reader: the text of the pages in view.</summary>
    public string AccessibleText => PageText.DocumentRange.GetText(-1);

    /// <summary>The text of the current selection, as a screen reader would read it.</summary>
    public string SelectedText => PageText.GetSelection() is { Length: > 0 } ranges ? ranges[0].GetText(-1) : string.Empty;

    /// <summary>Where a phrase is on the screen (pixels), found through the Text pattern; null when the pages in view do not have it.</summary>
    public System.Drawing.Rectangle? ScreenBoxOf(string phrase)
    {
        var range = PageText.DocumentRange.FindText(phrase, false, true);
        if (range is null)
        {
            return null;
        }

        var boxes = range.GetBoundingRectangles();
        return boxes.Length == 0 ? null : boxes.Aggregate(System.Drawing.Rectangle.Union);
    }

    public static System.Drawing.Point CenterOf(System.Drawing.Rectangle box) => new(box.Left + (box.Width / 2), box.Top + (box.Height / 2));

    public static System.Drawing.Point LeftOf(System.Drawing.Rectangle box) => new(box.Left + 1, box.Top + (box.Height / 2));

    public static System.Drawing.Point RightOf(System.Drawing.Rectangle box) => new(box.Right - 1, box.Top + (box.Height / 2));

    /// <summary>Just past the end of a box: on the next character, so the selection ends exactly at the end of the box.</summary>
    public static System.Drawing.Point JustPast(System.Drawing.Rectangle box) => new(box.Right + 1, box.Top + (box.Height / 2));

    public void ClickAt(System.Drawing.Point point)
    {
        Window.SetForeground();
        NativeMouse.Click(point);
        Wait.UntilInputIsProcessed();
    }

    public void DoubleClickAt(System.Drawing.Point point)
    {
        Window.SetForeground();
        NativeMouse.Click(point, 2);
        Wait.UntilInputIsProcessed();
    }

    public void TripleClickAt(System.Drawing.Point point)
    {
        Window.SetForeground();
        NativeMouse.Click(point, 3);
        Wait.UntilInputIsProcessed();
    }

    public void Drag(System.Drawing.Point from, System.Drawing.Point to)
    {
        Window.SetForeground();
        NativeMouse.Drag(from, to);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>The text on the clipboard, or null.</summary>
    public static string? ClipboardText() => NativeClipboard.GetText();

    public static void ClearClipboard() => NativeClipboard.Clear();

    private void TryCapture(string label)
    {
        try
        {
            Capture(label);
        }
        catch (Exception e)
        {
            // A picture is a courtesy to whoever reads the failure; when it cannot be taken, say why next to where it would have been.
            var directory = Environment.GetEnvironmentVariable("MIKEPDF_SHOTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, label + ".txt"), e.ToString() + Environment.NewLine + string.Join(", ", Process.GetProcesses().Where(p => p.ProcessName.StartsWith("MikePDF", StringComparison.Ordinal)).Select(p => p.ProcessName + ":" + p.Id)));
        }
    }

    /// <summary>Closes the window the way a user does and waits for the process to end, so the app saves its state.</summary>
    public void CloseGracefully()
    {
        Window.Close();
        Retry.WhileFalse(() => _application.HasExited, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
    }

    public void Dispose()
    {
        if (!_owner)
        {
            return;
        }

        _automation.Dispose();
        _application.Dispose();
        KillAll();
    }
}

/// <summary>
/// A test that needs the installed package and an interactive desktop. Skipped unless MIKEPDF_UI_TESTS=1 (set by build/run-ui-tests.ps1).
/// <c>ciQuarantine</c> names a reason the journey is skipped on the GitHub-hosted runner (build/run-ui-tests.ps1 -Ci sets MIKEPDF_UI_CI=1 and MIKEPDF_UI_ARCH),
/// for <c>ciArch</c> only when that is given. Every quarantine has a row in docs/milestones/production-roadmap.md; local runs are not affected.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute(string? ciQuarantine = null, string? ciArch = null, [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("MIKEPDF_UI_TESTS") != "1")
        {
            Skip = "UI tests need an interactive desktop and the installed package: run build/run-ui-tests.ps1";
        }
        else if (ciQuarantine is not null && Environment.GetEnvironmentVariable("MIKEPDF_UI_CI") == "1"
            && (ciArch is null || string.Equals(ciArch, Environment.GetEnvironmentVariable("MIKEPDF_UI_ARCH"), StringComparison.OrdinalIgnoreCase)))
        {
            Skip = "Quarantined on the GitHub-hosted runner: " + ciQuarantine;
        }
    }
}
