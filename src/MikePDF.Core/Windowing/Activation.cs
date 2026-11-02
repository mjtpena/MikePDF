namespace MikePDF.Core.Windowing;

public enum PathProblem
{
    None,
    Empty,
    TooLong,
    ControlCharacter,
    NotFullyQualified,
    DeviceNamespace,
    ReservedName,
    AlternateStream,
    NotFound,
    IsFolder,
    NotAllowed,
}

public sealed record PathCheck(PathProblem Problem, string FullPath)
{
    public bool IsOk => Problem == PathProblem.None;
}

/// <summary>
/// The rules every path that arrives from outside must pass before any handle is opened (docs/features/file-association-jumplist.md 3.3). The path comes from Explorer, a shortcut, another
/// program, a script or a Jump List entry that may have been tampered with. Everything except the last two disk questions is pure string work: a device path such as <c>\\.\pipe\x</c> is
/// refused before anything touches it, because opening one can block for ever.
/// </summary>
public static class ActivationPathValidator
{
    public const int MaxLength = 32767;

    private static readonly string[] ReservedBase = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"];

    /// <summary>Checks a path. <paramref name="checkDisk"/> false stops after the string rules (for tests and for the palette's recent list).</summary>
    public static PathCheck Validate(string? raw, bool checkDisk = true)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PathCheck(PathProblem.Empty, string.Empty);
        }

        if (raw.Length > MaxLength)
        {
            return new PathCheck(PathProblem.TooLong, string.Empty);
        }

        foreach (var c in raw)
        {
            if (c < ' ' || c == '\u007f')
            {
                return new PathCheck(PathProblem.ControlCharacter, string.Empty);
            }
        }

        // The device namespace in every spelling, before GetFullPath turns a reserved name into one.
        var text = raw.Replace('/', '\\');
        if (IsDeviceNamespace(text))
        {
            return new PathCheck(PathProblem.DeviceNamespace, string.Empty);
        }

        if (!IsFullyQualified(text))
        {
            return new PathCheck(PathProblem.NotFullyQualified, string.Empty);
        }

        if (HasReservedComponent(text))
        {
            return new PathCheck(PathProblem.ReservedName, string.Empty);
        }

        // ":" is only allowed as the drive designator (and the "\\?\C:" form).
        var afterDrive = text;
        if (afterDrive.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            afterDrive = afterDrive[4..];
        }

        if (afterDrive.Length >= 2 && afterDrive[1] == ':')
        {
            afterDrive = afterDrive[2..];
        }

        if (afterDrive.Contains(':', StringComparison.Ordinal))
        {
            return new PathCheck(PathProblem.AlternateStream, string.Empty);
        }

        string full;
        try
        {
            full = DocumentIdentity.StripExtendedPrefix(System.IO.Path.GetFullPath(text));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathCheck(PathProblem.NotAllowed, string.Empty);
        }

        // GetFullPath may have produced a device path from something that was not one (a reserved name it knows).
        if (IsDeviceNamespace(full) || HasReservedComponent(full))
        {
            return new PathCheck(PathProblem.DeviceNamespace, string.Empty);
        }

        return checkDisk ? CheckDisk(full) : new PathCheck(PathProblem.None, full);
    }

    private static PathCheck CheckDisk(string full)
    {
        try
        {
            var info = new FileInfo(full);
            var attributes = info.Attributes;
            if ((int)attributes == -1)
            {
                return new PathCheck(PathProblem.NotFound, full);
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return new PathCheck(PathProblem.IsFolder, full);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A symbolic link or a junction: the target has to pass the same rules (a link to a pipe must not be opened).
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    return new PathCheck(PathProblem.NotFound, full);
                }

                var inner = Validate(target.FullName, checkDisk: false);
                if (!inner.IsOk)
                {
                    return new PathCheck(PathProblem.NotAllowed, full);
                }

                if (target.Attributes.HasFlag(FileAttributes.Directory))
                {
                    return new PathCheck(PathProblem.IsFolder, full);
                }

                if (!target.Exists)
                {
                    return new PathCheck(PathProblem.NotFound, full);
                }
            }

            return new PathCheck(PathProblem.None, full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new PathCheck(PathProblem.NotAllowed, full);
        }
    }

    private static bool IsDeviceNamespace(string text) =>
        text.StartsWith(@"\\.\", StringComparison.Ordinal)
        || text.StartsWith(@"\??\", StringComparison.Ordinal)
        || (text.StartsWith(@"\\?\", StringComparison.Ordinal) && !IsExtendedDrive(text) && !text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase));

    // \\?\C:\...: a long path, which is accepted. Anything else after \\?\ (GLOBALROOT, Volume{...}, a device name) is not.
    private static bool IsExtendedDrive(string text) => text.Length > 6 && text[5] == ':' && char.IsAsciiLetter(text[4]) && text[6] == '\\';

    private static bool IsFullyQualified(string text) =>
        (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\')
        || (text.StartsWith(@"\\?\", StringComparison.Ordinal) && (IsExtendedDrive(text) || text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)))
        || (text.StartsWith(@"\\", StringComparison.Ordinal) && text.Length > 2 && text[2] != '\\' && text[2] != '?' && text[2] != '.');

    private static bool HasReservedComponent(string text)
    {
        foreach (var component in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            // Windows treats "NUL.txt" and "nul .pdf" as the device too: the name before the first dot, without trailing spaces.
            var stem = component.Contains('.', StringComparison.Ordinal) ? component[..component.IndexOf('.', StringComparison.Ordinal)] : component;
            stem = stem.TrimEnd(' ').ToUpperInvariant();
            if (Array.IndexOf(ReservedBase, stem) >= 0)
            {
                return true;
            }

            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3'))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a launch asked for: files, and the two switches the app knows. Everything else on the command line is ignored (docs/features/file-association-jumplist.md 3.3).</summary>
public sealed record LaunchRequest(IReadOnlyList<string> Paths, bool NewWindow, bool OpenDialog)
{
    public static LaunchRequest Empty { get; } = new([], false, false);

    public bool IsEmpty => Paths.Count == 0 && !NewWindow && !OpenDialog;
}

public static class LaunchArguments
{
    /// <summary>Recognises <c>--new-window</c> and <c>--open-dialog</c> (the Jump List tasks), and takes every other word that is a fully qualified path as a file. A word that starts with <c>-</c> or <c>/</c> and is not known is ignored and never opened as a file; so is a word that is not a fully qualified path (the value of an option we do not know, such as the 3 in <c>--page 3</c>).</summary>
    public static LaunchRequest Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var paths = new List<string>();
        var newWindow = false;
        var openDialog = false;
        foreach (var argument in arguments)
        {
            if (string.IsNullOrEmpty(argument))
            {
                continue;
            }

            if (argument[0] is '-' or '/' && !IsPathLike(argument))
            {
                newWindow |= string.Equals(argument, "--new-window", StringComparison.OrdinalIgnoreCase);
                openDialog |= string.Equals(argument, "--open-dialog", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (paths.Count < ActivationLimits.Maximum && (argument.Length >= 3 && argument[1] == ':' || argument.StartsWith(@"\\", StringComparison.Ordinal)))
            {
                paths.Add(argument);
            }
        }

        return new LaunchRequest(paths, newWindow, openDialog);
    }

    // "/x" is an option; "//server/share/a.pdf" and "/\\?\..." are not (they start with two path separators).
    private static bool IsPathLike(string argument) => argument.Length > 1 && argument[0] == '/' && argument[1] is '/' or '\\';
}

/// <summary>
/// Runs activations (a double-click in Explorer, a second launch, a Jump List entry) one at a time, in the order they arrived, and only once the first window is ready: an activation that arrives
/// while a session is being restored, or while a modal dialog is open, waits (docs/features/tabs-windows-session.md 3.7). Use it from one thread (the UI thread).
/// </summary>
public sealed class ActivationRelay<T>
{
    private readonly Func<T, Task> _handler;
    private readonly Queue<T> _pending = new();
    private bool _ready;
    private bool _held;
    private bool _running;

    public ActivationRelay(Func<T, Task> handler)
    {
        _handler = handler;
    }

    public int Pending => _pending.Count;

    /// <summary>Queues an activation. It runs at once if the queue is ready, idle and not held.</summary>
    public void Post(T item)
    {
        _pending.Enqueue(item);
        Pump();
    }

    /// <summary>The first window is ready (the session has been restored): the queued activations run now, in order.</summary>
    public void SetReady()
    {
        _ready = true;
        Pump();
    }

    /// <summary>A modal dialog is open (held) or has closed (not held).</summary>
    public void SetHeld(bool held)
    {
        _held = held;
        Pump();
    }

    private async void Pump()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            while (_ready && !_held && _pending.TryDequeue(out var item))
            {
                try
                {
                    await _handler(item);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // One bad activation never stops the ones behind it.
                    System.Diagnostics.Debug.WriteLine("Activation failed: " + e.GetType().Name);
                }
            }
        }
        finally
        {
            _running = false;
        }
    }
}

/// <summary>
/// What keeps a tab's document engine from being suspended (docs/features/tabs-windows-session.md 3.8): the tab is on screen, a save or print job is running, a search is running. The leases are
/// counted here, with no engine in sight, so the rule is a plain unit test; the window pins the engine while <see cref="IsPinned"/> is true.
/// </summary>
public sealed class TabEngineLeases
{
    private readonly object _gate = new();
    private int _visible;
    private int _jobs;

    /// <summary>Raised after <see cref="IsPinned"/> changed. May be raised on any thread.</summary>
    public event Action<bool>? PinnedChanged;

    public bool IsPinned
    {
        get
        {
            lock (_gate)
            {
                return _visible > 0 || _jobs > 0;
            }
        }
    }

    /// <summary>The tab is the one shown in a window. Dispose when it stops being.</summary>
    public IDisposable Show() => Acquire(visible: true);

    /// <summary>A save, a print job or a search is running for the tab. Dispose when it ends.</summary>
    public IDisposable BeginJob() => Acquire(visible: false);

    private Lease Acquire(bool visible)
    {
        bool changed;
        lock (_gate)
        {
            var before = _visible > 0 || _jobs > 0;
            if (visible)
            {
                _visible++;
            }
            else
            {
                _jobs++;
            }

            changed = !before;
        }

        if (changed)
        {
            PinnedChanged?.Invoke(true);
        }

        return new Lease(this, visible);
    }

    private void Release(bool visible)
    {
        bool changed;
        lock (_gate)
        {
            if (visible)
            {
                _visible--;
            }
            else
            {
                _jobs--;
            }

            changed = _visible == 0 && _jobs == 0;
        }

        if (changed)
        {
            PinnedChanged?.Invoke(false);
        }
    }

    private sealed class Lease(TabEngineLeases owner, bool visible) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(visible);
            }
        }
    }
}
