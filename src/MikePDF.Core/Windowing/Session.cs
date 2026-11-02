using System.Globalization;
using System.Text.Json;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Windowing;

/// <summary>A window's restored-state rectangle in physical pixels, the display it was on and that display's DPI (only to validate the rectangle later).</summary>
public sealed record StoredBounds(int X, int Y, int Width, int Height, int Dpi, string Display);

/// <summary>One tab in <c>session.json</c>. It never holds a password, document text or edits.</summary>
public sealed record SessionTab(TabKind Kind, string? Path, string? FileId, string? Title, TabViewState View, bool Suspended);

public sealed record SessionWindow(StoredBounds Bounds, bool Maximized, int ActiveTab, bool SidebarOpen, string SidebarPane, IReadOnlyList<SessionTab> Tabs);

/// <summary>What is written to <c>LocalState\private\session.json</c> (docs/features/tabs-windows-session.md 3.6), schema version 1.</summary>
public sealed record SessionFile(bool CleanExit, DateTimeOffset Saved, int ActiveWindow, IReadOnlyList<SessionWindow> Windows)
{
    public const int Version = 1;

    public int DocumentCount => Windows.Sum(w => w.Tabs.Count(t => t.Kind == TabKind.Document));
}

public enum SessionProblem
{
    None,
    Missing,
    TooLarge,
    NotJson,
    WrongVersion,
    TooManyWindows,
    TooManyTabs,
    OutOfRange,
    BadType,
    Unreadable,
}

public sealed record SessionReadResult(SessionFile? Session, SessionProblem Problem)
{
    public bool IsOk => Session is not null;
}

/// <summary>
/// Reads and writes the session file. The file is user data, so it is parsed defensively: at most 1 MiB, 64 windows and 1,000 tabs, every number range-checked, unknown keys ignored.
/// Anything wrong rejects the whole file (it is treated as empty); nothing here throws for bad input.
/// </summary>
public static class SessionCodec
{
    public const int MaxBytes = 1 << 20;
    public const int MaxWindows = 64;
    public const int MaxTabs = 1000;
    private const int MaxText = 32767;

    public static byte[] Write(SessionFile session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", SessionFile.Version);
            w.WriteBoolean("cleanExit", session.CleanExit);
            w.WriteString("saved", session.Saved.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("activeWindow", session.ActiveWindow);
            w.WriteStartArray("windows");
            foreach (var window in session.Windows)
            {
                w.WriteStartObject();
                w.WriteStartObject("bounds");
                w.WriteNumber("x", window.Bounds.X);
                w.WriteNumber("y", window.Bounds.Y);
                w.WriteNumber("width", window.Bounds.Width);
                w.WriteNumber("height", window.Bounds.Height);
                w.WriteNumber("dpi", window.Bounds.Dpi);
                w.WriteString("display", window.Bounds.Display);
                w.WriteEndObject();
                w.WriteBoolean("maximized", window.Maximized);
                w.WriteNumber("activeTab", window.ActiveTab);
                w.WriteBoolean("sidebarOpen", window.SidebarOpen);
                w.WriteString("sidebarPane", window.SidebarPane);
                w.WriteStartArray("tabs");
                foreach (var tab in window.Tabs)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", tab.Kind.ToString());
                    if (tab.Path is not null)
                    {
                        w.WriteString("path", tab.Path);
                    }

                    if (tab.FileId is not null)
                    {
                        w.WriteString("fileId", tab.FileId);
                    }

                    if (tab.Title is not null)
                    {
                        w.WriteString("title", tab.Title);
                    }

                    w.WriteNumber("page", tab.View.Page);
                    w.WriteNumber("pageFraction", tab.View.PageFraction);
                    w.WriteNumber("zoom", tab.View.Zoom);
                    w.WriteString("fit", tab.View.Fit.ToString());
                    w.WriteNumber("viewTurns", tab.View.ViewTurns);
                    w.WriteBoolean("suspended", tab.Suspended);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static SessionReadResult Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            return Fail(SessionProblem.TooLarge);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail(SessionProblem.BadType);
            }

            if (!Int(root, "version", out var version, 0, int.MaxValue) || version != SessionFile.Version)
            {
                return Fail(SessionProblem.WrongVersion);
            }

            if (!Bool(root, "cleanExit", out var clean) || !Text(root, "saved", out var savedText, 64) || !DateTimeOffset.TryParse(savedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved)
                || !Int(root, "activeWindow", out var activeWindow, 0, MaxWindows) || !root.TryGetProperty("windows", out var windowsElement) || windowsElement.ValueKind != JsonValueKind.Array)
            {
                return Fail(SessionProblem.BadType);
            }

            if (windowsElement.GetArrayLength() > MaxWindows)
            {
                return Fail(SessionProblem.TooManyWindows);
            }

            var windows = new List<SessionWindow>();
            var tabCount = 0;
            foreach (var windowElement in windowsElement.EnumerateArray())
            {
                var parsed = ReadWindow(windowElement, ref tabCount, out var problem);
                if (parsed is null)
                {
                    return Fail(problem);
                }

                windows.Add(parsed);
            }

            return new SessionReadResult(new SessionFile(clean, saved, Math.Min(activeWindow, Math.Max(0, windows.Count - 1)), windows), SessionProblem.None);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Fail(SessionProblem.NotJson);
        }
    }

    private static SessionReadResult Fail(SessionProblem problem) => new(null, problem);

    private static SessionWindow? ReadWindow(JsonElement e, ref int tabCount, out SessionProblem problem)
    {
        problem = SessionProblem.BadType;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("bounds", out var b) || b.ValueKind != JsonValueKind.Object || !e.TryGetProperty("tabs", out var tabsElement) || tabsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (!Int(b, "x", out var x, -100_000, 100_000) || !Int(b, "y", out var y, -100_000, 100_000) || !Int(b, "width", out var width, 1, 100_000) || !Int(b, "height", out var height, 1, 100_000)
            || !Int(b, "dpi", out var dpi, 24, 1000) || !Text(b, "display", out var display, 256)
            || !Bool(e, "maximized", out var maximized) || !Int(e, "activeTab", out var activeTab, 0, MaxTabs) || !Bool(e, "sidebarOpen", out var sidebarOpen) || !Text(e, "sidebarPane", out var pane, 32))
        {
            problem = SessionProblem.OutOfRange;
            return null;
        }

        tabCount += tabsElement.GetArrayLength();
        if (tabCount > MaxTabs)
        {
            problem = SessionProblem.TooManyTabs;
            return null;
        }

        var tabs = new List<SessionTab>();
        foreach (var t in tabsElement.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object || !Text(t, "kind", out var kindText, 16) || !Enum.TryParse<TabKind>(kindText, out var kind) || !Enum.IsDefined(kind)
                || !Int(t, "page", out var page, 0, 10_000_000) || !Dbl(t, "pageFraction", out var fraction, 0, 1) || !Dbl(t, "zoom", out var zoom, 0.1, 64)
                || !Text(t, "fit", out var fitText, 16) || !Enum.TryParse<FitMode>(fitText, out var fit) || !Enum.IsDefined(fit) || !Int(t, "viewTurns", out var turns, 0, 3) || !Bool(t, "suspended", out var suspended))
            {
                problem = SessionProblem.OutOfRange;
                return null;
            }

            string? path = null;
            string? fileId = null;
            string? title = null;
            if ((HasProperty(t, "path") && !Text(t, "path", out path, MaxText)) || (HasProperty(t, "fileId") && !Text(t, "fileId", out fileId, 128)) || (HasProperty(t, "title") && !Text(t, "title", out title, 1024)))
            {
                problem = SessionProblem.OutOfRange;
                return null;
            }

            if (kind == TabKind.Document && string.IsNullOrEmpty(path))
            {
                problem = SessionProblem.BadType;
                return null;
            }

            tabs.Add(new SessionTab(kind, path, fileId, title, new TabViewState(page, fraction, zoom, fit, turns), suspended));
        }

        problem = SessionProblem.None;
        return new SessionWindow(new StoredBounds(x, y, width, height, dpi, display), maximized, tabs.Count == 0 ? 0 : Math.Min(activeTab, tabs.Count - 1), sidebarOpen, pane, tabs);
    }

    private static bool HasProperty(JsonElement e, string name) => e.TryGetProperty(name, out _);

    private static bool Bool(JsonElement e, string name, out bool value)
    {
        value = false;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = p.GetBoolean();
        return true;
    }

    private static bool Int(JsonElement e, string name, out int value, int min, int max)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value) && value >= min && value <= max;
    }

    private static bool Dbl(JsonElement e, string name, out double value, double min, double max)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value) && !double.IsNaN(value) && value >= min && value <= max;
    }

    private static bool Text(JsonElement e, string name, out string value, int maxLength)
    {
        value = string.Empty;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var s = p.GetString() ?? string.Empty;
        if (s.Length > maxLength || s.Any(c => c < ' ' && c != '\t'))
        {
            return false;
        }

        value = s;
        return true;
    }
}

/// <summary>Reads and writes <c>session.json</c> as one file, atomically: the new bytes go to a temp file that is then moved over the old one, so a crash mid-write leaves the old file whole.</summary>
public sealed class SessionStore
{
    private readonly string _path;

    public SessionStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public string Path => _path;

    /// <summary>Test seam: runs after the temp file is written and before it replaces the old file (the moment a crash would be worst).</summary>
    internal Action? BeforeReplace { get; set; }

    public SessionReadResult Load()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                return new SessionReadResult(null, SessionProblem.Missing);
            }

            if (info.Length > SessionCodec.MaxBytes)
            {
                return new SessionReadResult(null, SessionProblem.TooLarge);
            }

            return SessionCodec.Read(File.ReadAllBytes(_path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SessionReadResult(null, SessionProblem.Unreadable);
        }
    }

    /// <summary>Writes the session. False when it could not be written: reading is never interrupted by that.</summary>
    public bool Save(SessionFile session)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, SessionCodec.Write(session));
            BeforeReplace?.Invoke();
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(_path);
            File.Delete(_path + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be removed is no worse than one that was never written.
        }
    }
}

/// <summary>The setting <c>startup.restore</c>.</summary>
public enum RestoreMode
{
    /// <summary>Restore after any exit, silently.</summary>
    Always,

    /// <summary>Ask after an unclean exit (a crash or a kill); after a normal close restore when more than one document was open, else start with an empty window.</summary>
    Ask,

    /// <summary>Never restore, and never write the session file.</summary>
    Never,
}

public enum RestoreAction
{
    StartFresh,
    Restore,
    AskThenRestore,
}

public static class SessionPolicy
{
    public static RestoreMode Parse(string? text) => Enum.TryParse<RestoreMode>(text, out var mode) && Enum.IsDefined(mode) ? mode : RestoreMode.Ask;

    public static RestoreAction Decide(RestoreMode mode, SessionFile? session)
    {
        if (mode == RestoreMode.Never || session is null || session.DocumentCount == 0)
        {
            return RestoreAction.StartFresh;
        }

        return mode switch
        {
            RestoreMode.Always => RestoreAction.Restore,
            _ => !session.CleanExit ? RestoreAction.AskThenRestore : session.DocumentCount > 1 ? RestoreAction.Restore : RestoreAction.StartFresh,
        };
    }
}

/// <summary>Writes the session file when the setting allows it. With <see cref="RestoreMode.Never"/> nothing is written, and a file left from before is removed.</summary>
public sealed class SessionRecorder
{
    private readonly SessionStore _store;
    private readonly Func<RestoreMode> _mode;

    public SessionRecorder(SessionStore store, Func<RestoreMode> mode)
    {
        _store = store;
        _mode = mode;
    }

    /// <summary>True when the session was written.</summary>
    public bool Record(SessionFile session)
    {
        if (_mode() == RestoreMode.Never)
        {
            _store.Delete();
            return false;
        }

        return _store.Save(session);
    }
}

/// <summary>A monitor's work area (physical pixels, in virtual-desktop coordinates) and its DPI.</summary>
public sealed record MonitorInfo(int Left, int Top, int Right, int Bottom, int Dpi, string Name)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Puts a stored window rectangle on a monitor that exists (docs/features/tabs-windows-session.md 3.6).</summary>
public static class WindowPlacement
{
    private const int MinWidth = 480;
    private const int MinHeight = 320;

    /// <summary>
    /// The monitor the rectangle overlaps most (else the nearest), the rectangle with its size kept in logical terms if that monitor's DPI differs from the stored one, clamped to the
    /// monitor's work area and moved fully inside it. With no monitors known the rectangle is returned as it was.
    /// </summary>
    public static PixelRect Resolve(StoredBounds stored, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(monitors);
        var rect = new PixelRect(stored.X, stored.Y, stored.Width, stored.Height);
        if (monitors.Count == 0)
        {
            return rect;
        }

        var target = monitors.OrderByDescending(m => Overlap(rect, m)).ThenBy(m => Distance(rect, m)).First();
        var width = rect.Width;
        var height = rect.Height;
        if (stored.Dpi > 0 && target.Dpi > 0 && stored.Dpi != target.Dpi)
        {
            width = (int)Math.Round(width * (double)target.Dpi / stored.Dpi);
            height = (int)Math.Round(height * (double)target.Dpi / stored.Dpi);
        }

        width = Math.Min(Math.Max(width, Math.Min(MinWidth, target.Width)), target.Width);
        height = Math.Min(Math.Max(height, Math.Min(MinHeight, target.Height)), target.Height);
        var x = Math.Clamp(rect.X, target.Left, target.Right - width);
        var y = Math.Clamp(rect.Y, target.Top, target.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    private static long Overlap(PixelRect r, MonitorInfo m)
    {
        var w = Math.Min(r.X + r.Width, m.Right) - Math.Max(r.X, m.Left);
        var h = Math.Min(r.Y + r.Height, m.Bottom) - Math.Max(r.Y, m.Top);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }

    private static long Distance(PixelRect r, MonitorInfo m)
    {
        var cx = r.X + (r.Width / 2);
        var cy = r.Y + (r.Height / 2);
        var dx = cx < m.Left ? m.Left - cx : cx > m.Right ? cx - m.Right : 0;
        var dy = cy < m.Top ? m.Top - cy : cy > m.Bottom ? cy - m.Bottom : 0;
        return ((long)dx * dx) + ((long)dy * dy);
    }
}
