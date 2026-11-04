using Microsoft.UI.Dispatching;
using MikePDF.Core;
using MikePDF.Core.Editing.Recovery;
using MikePDF.Core.Persistence;
using MikePDF.Core.Windowing;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MikePDF.App.Services;

/// <summary>What an outside activation (a double-click in Explorer, a second launch, a Jump List entry) asked for.</summary>
internal sealed record ActivationRequest(LaunchRequest Launch, int ItemsWithoutPath);

/// <summary>
/// The app's windows and what they share: one engine host and budget, the one model of windows and tabs (docs/features/tabs-windows-session.md 3.1), the activation relay, the session
/// file and the Jump List. All windows live on one UI thread (OQ-TAB-2); a document's session is bound to that thread's dispatcher, which is what lets a tab move between windows.
/// </summary>
internal sealed class AppHost
{
    private static readonly TimeSpan SessionDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionPeriod = TimeSpan.FromSeconds(30);

    private readonly List<MainWindow> _windows = [];
    private readonly ActivationRelay<ActivationRequest> _activations;
    private readonly SessionStore _sessionStore;
    private readonly SessionRecorder _recorder;
    private readonly DispatcherQueueTimer _debounce;
    private readonly DispatcherQueueTimer _periodic;
    private readonly DispatcherQueue _queue;
    private Task _openChain = Task.CompletedTask;
    private bool _sessionPending;
    private bool _endingSession;

    private AppHost(AppServices services)
    {
        Services = services;
        _queue = DispatcherQueue.GetForCurrentThread();
        // Creating the sandbox profile and putting the verified engine in place takes a moment; it runs while the first window comes up.
        var cap = services.Settings.Value<int>("engines.maxLiveTabs");
        var memoryLimit = services.Policy.Current.EngineMemoryLimitMb;
        Host = Task.Run(() => EngineHost.Create(cap, memoryLimit));
        Saves = new SaveService(Host);
        SaveHandler = SaveForCloseAsync;
        services.PdfiumVersion = PdfiumVersionAsync;
        _activations = new ActivationRelay<ActivationRequest>(HandleActivationAsync);
        _sessionStore = new SessionStore(AppPaths.SessionPath);
        _recorder = new SessionRecorder(_sessionStore, CurrentRestoreMode);
        _debounce = _queue.CreateTimer();
        _debounce.IsRepeating = false;
        _debounce.Interval = SessionDebounce;
        _debounce.Tick += (_, _) => WriteSession(clean: false);
        _periodic = _queue.CreateTimer();
        _periodic.IsRepeating = true;
        _periodic.Interval = SessionPeriod;
        _periodic.Tick += (_, _) => WriteSession(clean: false);
        services.Settings.SettingsChanged += OnSettingsChanged;
        services.Policy.Changed += ApplyEngineCap;
    }

    public static AppHost? Current { get; private set; }

    /// <summary>Saves the documents of the tabs about to close (the Save button of the close prompt). True when every one was saved; false when one was not, and then nothing closes.</summary>
    public static Func<IReadOnlyList<TabModel>, Task<bool>>? SaveHandler { get; set; }

    /// <summary>The writer host and the pipeline over it, shared by every window.</summary>
    public SaveService Saves { get; }

    /// <summary>The system is ending the session (shutdown, sign-out): windows that close now keep their journals, because nobody was asked and nothing was decided.</summary>
    public bool EndingSession => _endingSession;

    // ---- recovery bookkeeping (docs/features/save-pipeline.md 3.5) ------------------------------------------------------------------------

    private readonly Dictionary<string, RecoveryCandidate> _restores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecoveryRow> _deferred = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Documents whose journal the person chose to keep for later ("Not now") this run: opening them does not ask again; the empty window lists them.</summary>
    public IReadOnlyCollection<RecoveryRow> Deferred => _deferred.Values;

    public bool IsDeferred(string path) => _deferred.ContainsKey(path);

    public void Defer(RecoveryRow row)
    {
        if (row.Candidate.DocumentPath is { } path)
        {
            _deferred[path] = row;
        }
    }

    public void Undefer(string path) => _deferred.Remove(path);

    /// <summary>The person chose Restore for this document: the tab that opens it applies the journal.</summary>
    public void QueueRestore(RecoveryCandidate candidate)
    {
        if (candidate.DocumentPath is { } path)
        {
            _restores[path] = candidate;
            _deferred.Remove(path);
        }
    }

    /// <summary>The journal to apply to the document being opened, if the person chose to restore it. Taken once.</summary>
    public RecoveryCandidate? TakeRestore(string path) => _restores.Remove(path, out var candidate) ? candidate : null;

    private async Task<bool> SaveForCloseAsync(IReadOnlyList<TabModel> tabs)
    {
        foreach (var tab in tabs)
        {
            if (WindowOfTab(tab.Id) is not { } window || window.TabEntryOf(tab.Id) is not { } entry || !await window.SaveForCloseAsync(entry))
            {
                return false;
            }
        }

        return true;
    }

    public AppServices Services { get; }

    public Task<EngineHost> Host { get; }

    // About shows the engine's PDFium version without an open document: the pool engine says it in its Hello. Null when no engine can start.
    private async Task<string?> PdfiumVersionAsync()
    {
        try
        {
            return await (await Host.ConfigureAwait(true)).Budget.GetPdfiumVersionAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public AppModel Model { get; } = new();

    public IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>The window made for a tab that is being torn out, until the tab arrives in it.</summary>
    public MainWindow? PendingTearOut { get; set; }

    public static AppHost Create(AppServices services)
    {
        Current = new AppHost(services);
        Current._periodic.Start();
        return Current;
    }

    /// <summary>The setting <c>startup.restore</c>. A journey can name another value in <c>MIKEPDF_TEST_RESTORE</c> (the harness sets Never so that a killed run does not ask the next one).</summary>
    public RestoreMode CurrentRestoreMode()
    {
        var test = TestSeams.Get(TestSeamGate.Restore);
        return SessionPolicy.Parse(test is { Length: < 16 } ? test : Services.Settings.Value<string>("startup.restore"));
    }

    // ---- windows -------------------------------------------------------------------------------------------------------------------

    /// <summary>A new window with one empty tab. <paramref name="like"/> gives its size and place (a torn-out tab's window is sized like the one it came from).</summary>
    public MainWindow CreateWindow(MainWindow? like = null, bool activate = true)
    {
        var model = Model.NewWindow();
        var window = new MainWindow(this, model);
        _windows.Add(window);
        if (like is not null)
        {
            window.SizeAndPlaceLike(like);
        }

        if (activate)
        {
            window.Activate();
        }

        RequestSessionWrite();
        return window;
    }

    public MainWindow? WindowOf(Guid modelId) => _windows.FirstOrDefault(w => w.Model.Id == modelId);

    public MainWindow? WindowOf(Microsoft.UI.WindowId id) => _windows.FirstOrDefault(w => w.AppWindow.Id.Value == id.Value);

    public MainWindow? WindowOfTab(Guid tabId) => _windows.FirstOrDefault(w => w.Model.IndexOf(tabId) >= 0);

    /// <summary>The window used last; null before the first window exists.</summary>
    public MainWindow? LastActiveWindow => Model.LastActive is { } model ? WindowOf(model.Id) : null;

    internal void OnWindowActivated(MainWindow window)
    {
        Model.Touch(window.Model);
        RequestSessionWrite();
    }

    /// <summary>A window is going away. If it is the last one, the session is written now with the clean-exit mark, before anything is torn down.</summary>
    internal void OnWindowClosing(MainWindow window)
    {
        if (_windows.Count == 1 && !_endingSession)
        {
            WriteSession(clean: true);
            _debounce.Stop();
            _periodic.Stop();
        }
    }

    internal void OnWindowClosed(MainWindow window)
    {
        _windows.Remove(window);
        Model.Remove(window.Model);
        if (_windows.Count > 0)
        {
            RequestSessionWrite();
        }
    }

    // ---- opening ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a document where the rules say (docs/features/tabs-windows-session.md 3.2). One open at a time, in the order asked (a drop during an open waits for it). A tab that already shows the
    /// file is just shown. Shows its own errors.
    /// </summary>
    public Task OpenAsync(string path, OpenSource source, MainWindow? preferred, bool newWindow = false, bool lazy = false, TabViewState? view = null)
    {
        AppTrace.OpenRequested();
        var next = _openChain.ContinueWith(_ => OpenCoreAsync(path, source, preferred, newWindow, lazy, view), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
        _openChain = next;
        return next;
    }

    private async Task OpenCoreAsync(string path, OpenSource source, MainWindow? preferred, bool newWindow, bool lazy, TabViewState? view)
    {
        try
        {
            // The rules of what may be opened at all run first and never touch the path (a pipe would block). A path from the person's own recent list that has gone is reported by the open itself.
            var check = ActivationPathValidator.Validate(path, checkDisk: false);
            if (!check.IsOk)
            {
                (preferred ?? LastActiveWindow)?.ShowNotice(NoticeFor(check.Problem, path));
                return;
            }

            var identity = DocumentIdentity.Of(check.FullPath);
            var decision = Model.Plan(identity, source, preferred?.Model ?? Model.LastActive, newWindow);
            switch (decision.Outcome)
            {
                case OpenAction.Refused:
                    (preferred ?? LastActiveWindow)?.ShowNotice(Strings.Format("Open_TooMany", AppModel.MaxOpenDocuments));
                    return;
                case OpenAction.ActivateExisting:
                    if (WindowOf(decision.Window!.Id) is { } shown)
                    {
                        shown.ShowTab(decision.Tab!.Id);
                    }

                    return;
                case OpenAction.NewWindow:
                    var created = CreateWindow(like: preferred ?? LastActiveWindow);
                    await created.OpenDocumentAsync(check.FullPath, identity, 0, reuseEmpty: true, lazy, view);
                    return;
                default:
                    if (WindowOf(decision.Window!.Id) is { } target)
                    {
                        await target.OpenDocumentAsync(check.FullPath, identity, decision.Index, decision.Outcome == OpenAction.ReuseEmpty, lazy, view);
                    }

                    return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Open failed: " + e.GetType().Name);
        }
    }

    internal static string NoticeFor(PathProblem problem, string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0 || name.Length > 80)
        {
            name = Strings.Get("Activation_ThisPath");
        }

        return problem switch
        {
            PathProblem.NotFound => Strings.Format("Activation_NotFound", name),
            PathProblem.IsFolder => Strings.Format("Activation_IsFolder", name),
            _ => Strings.Get("Activation_NotAllowed"),
        };
    }

    /// <summary>Reopens the tab closed last (Ctrl+Shift+T): in the window it was closed from if that still exists.</summary>
    public Task ReopenClosedAsync(MainWindow from)
    {
        if (Model.Closed.Pop() is not { } closed)
        {
            return Task.CompletedTask;
        }

        var window = WindowOf(closed.WindowId) ?? from;
        return OpenAsync(closed.Path, OpenSource.User, window, view: closed.View);
    }

    // ---- activations -----------------------------------------------------------------------------------------------------------------

    /// <summary>Queues an outside activation. It runs when the first window is ready (and the session restored) and no modal dialog is open, in the order it arrived.</summary>
    public void Post(ActivationRequest request) => _activations.Post(request);

    public void ActivationsReady() => _activations.SetReady();

    private int _modal;

    /// <summary>A modal dialog opened (+1) or closed (-1) in some window: activations wait while one is open and run when the last closes.</summary>
    public void ModalChanged(int delta)
    {
        _modal = Math.Max(0, _modal + delta);
        _activations.SetHeld(_modal > 0);
    }

    private async Task HandleActivationAsync(ActivationRequest request)
    {
        var settings = Services.Settings;
        var openInNewWindow = request.Launch.NewWindow || settings.Value<string>("windows.openFromShell") == "NewWindow";
        var window = LastActiveWindow ?? CreateWindow();
        window.BringToFront();
        if (request.ItemsWithoutPath > 0)
        {
            window.ShowNotice(Strings.Get("Activation_NoPath"));
        }

        var valid = new List<string>();
        foreach (var raw in request.Launch.Paths.Take(ActivationLimits.Maximum))
        {
            var check = ActivationPathValidator.Validate(raw);
            if (check.IsOk)
            {
                if (!valid.Contains(check.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    valid.Add(check.FullPath);
                }
            }
            else
            {
                window.ShowNotice(NoticeFor(check.Problem, raw));
            }
        }

        if (request.Launch.OpenDialog && valid.Count == 0)
        {
            await window.RunCommandAsync("file.open");
            return;
        }

        if (valid.Count == 0)
        {
            if (request.Launch.NewWindow)
            {
                CreateWindow(like: window);
            }

            return;
        }

        if (ActivationLimits.NeedsConfirmation(valid.Count) && !await window.ConfirmOpenManyAsync(valid.Count))
        {
            return;
        }

        await OpenManyAsync(valid, OpenSource.Outside, window, openInNewWindow);
    }

    /// <summary>
    /// Opens several files (an activation, a drop): all of them, the first one shown. The first opens at once; the others become tabs that open when they are first selected, like a restored
    /// session, so that ten files do not start ten engines and the first is not kept waiting for the rest.
    /// </summary>
    public async Task OpenManyAsync(IReadOnlyList<string> paths, OpenSource source, MainWindow? window, bool newWindow = false)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            await OpenAsync(paths[i], source, window, newWindow: newWindow && i == 0, lazy: i > 0);
            window = LastActiveWindow ?? window; // the first file may have opened a new window: the others follow it
        }

        if (paths.Count > 1 && window is not null && Model.Find(DocumentIdentity.Of(paths[0])) is { } first && WindowOf(first.Window.Id) is { } home)
        {
            home.ShowTab(first.Tab.Id); // the first file is the one shown
        }
    }

    // ---- moving tabs between windows ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A tab was torn out into <paramref name="window"/> and the person is still dragging it. When the mouse button comes up over another window's tab strip, the tab joins that window at the
    /// place under the pointer and the window made for it closes (the framework's own "dropped on another strip" events were not raised when this was measured, so the drop is found here).
    /// </summary>
    public void WatchTornOutDrop(MainWindow window)
    {
        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        timer.IsRepeating = true;
        var ticks = 0;
        timer.Tick += (_, _) =>
        {
            if (NativeWindowing.LeftButtonDown() && ++ticks < 1500)
            {
                return;
            }

            timer.Stop();
            if (!_windows.Contains(window) || window.Model.Tabs.Count != 1 || window.Model.Tabs[0].Kind == TabKind.Empty)
            {
                return;
            }

            if (FindMergeTarget(window, null) is { } merge && window.TabEntryOf(window.Model.Tabs[0].Id) is { } entry)
            {
                MoveTab(window, entry, merge.Window, merge.Index);
            }
        };
        timer.Start();
    }

    /// <summary>Another window whose tab strip the pointer is over, with the place in it, for a tab that was just let go (not the window it came from, nor the one made for it).</summary>
    public (MainWindow Window, int Index)? FindMergeTarget(MainWindow from, MainWindow? made)
    {
        if (NativeWindowing.CursorPosition() is not { } cursor)
        {
            return null;
        }

        foreach (var window in _windows)
        {
            if (!ReferenceEquals(window, from) && !ReferenceEquals(window, made) && window.StripIndexAt(cursor.X, cursor.Y) is { } index)
            {
                return (window, index);
            }
        }

        return null;
    }

    /// <summary>Moves a tab (with its live session: nothing reloads) to another window, or to a new one. A window the tab leaves with no tab left closes.</summary>
    public void MoveTab(MainWindow from, TabEntry entry, MainWindow? to, int index)
    {
        var target = to ?? CreateWindow(like: from, activate: false);
        if (ReferenceEquals(target, from))
        {
            return;
        }

        from.TakeEntry(entry);
        target.AcceptEntry(entry, index, activate: true);
        target.RemovePlaceholderEmptyTab();
        target.Activate();
        if (from.Model.Tabs.Count == 0)
        {
            from.Close();
        }

        RequestSessionWrite();
    }

    // ---- the session -------------------------------------------------------------------------------------------------------------------

    public void RequestSessionWrite()
    {
        if (_endingSession || CurrentRestoreMode() == RestoreMode.Never)
        {
            return;
        }

        _sessionPending = true;
        _debounce.Stop();
        _debounce.Start();
    }

    public SessionFile Snapshot(bool clean)
    {
        var windows = new List<SessionWindow>();
        foreach (var model in Model.Windows)
        {
            if (WindowOf(model.Id) is { } window)
            {
                windows.Add(window.Capture());
            }
        }

        return new SessionFile(clean, DateTimeOffset.UtcNow, 0, windows);
    }

    /// <summary>Writes the session file now (never while the setting is Never). A write that fails is not reported: reading goes on.</summary>
    public void WriteSession(bool clean)
    {
        _sessionPending = false;
        if (_windows.Count == 0)
        {
            return;
        }

        _recorder.Record(Snapshot(clean));
    }

    /// <summary>Windows is ending the session (shutdown, sign-out): no dialog, the session is written, and the next launch offers to restore it.</summary>
    internal void OnEndSession()
    {
        _endingSession = false;
        WriteSession(clean: false);
        _endingSession = true; // nothing after this overwrites it with a "clean" mark
        _debounce.Stop();
    }

    public SessionReadResult LoadSession() => _sessionStore.Load();

    /// <summary>Builds the windows of a session. <paramref name="reuse"/> is a window that already exists and is empty (the one the app opened first): it becomes the first window of the session.</summary>
    public void Restore(SessionFile session, MainWindow? reuse)
    {
        var monitors = NativeWindowing.Monitors();
        var created = new List<MainWindow>();
        for (var i = 0; i < session.Windows.Count; i++)
        {
            var saved = session.Windows[i];
            var window = i == 0 && reuse is not null ? reuse : CreateWindow(activate: false);
            window.RestoreFrom(saved, monitors);
            created.Add(window);
        }

        // The first window of the file was the one used last: it is activated last, so it ends on top and first in the model.
        for (var i = created.Count - 1; i >= 0; i--)
        {
            created[i].Activate();
        }
    }

    // ---- settings ----------------------------------------------------------------------------------------------------------------------

    private void OnSettingsChanged(IReadOnlyList<string> keys)
    {
        if (keys.Contains("engines.maxLiveTabs"))
        {
            ApplyEngineCap();
        }

        if (keys.Contains("startup.restore"))
        {
            if (CurrentRestoreMode() == RestoreMode.Never)
            {
                _sessionStore.Delete();
            }
            else
            {
                RequestSessionWrite();
            }
        }
    }

    private void ApplyEngineCap()
    {
        var cap = Services.Settings.Value<int>("engines.maxLiveTabs");
        if (Host.IsCompletedSuccessfully)
        {
            Host.Result.Budget.SetMaxDocumentEngines(Math.Max(1, cap));
        }
    }

    /// <summary>Clear history also forgets the session and the closed tabs (docs/features/tabs-windows-session.md 3.6).</summary>
    public void ClearHistory()
    {
        Model.Closed.Clear();
        _sessionStore.Delete();
        RequestSessionWrite();
    }

    /// <summary>How many tabs are open in all windows, for diagnostics and tests.</summary>
    public int TabCount => _windows.Sum(w => w.Model.Tabs.Count);

    internal bool SessionWritePending => _sessionPending;

    internal DocumentStore Documents => Services.Documents;
}
