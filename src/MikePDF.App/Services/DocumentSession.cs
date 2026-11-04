using System.Diagnostics;
using Microsoft.UI.Dispatching;
using MikePDF.Broker;
using MikePDF.Core.Errors;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Navigation;
using MikePDF.Core.Persistence;
using MikePDF.Core.Tiles;
using MikePDF.Core.Windowing;
using MikePDF.Protocol;

namespace MikePDF.App.Services;

internal enum OpenOutcomeKind
{
    Opened,
    PasswordRequired,
    PasswordIncorrect,
    Failed,
}

internal sealed record OpenOutcome(OpenOutcomeKind Kind, DocumentErrorKind Error = DocumentErrorKind.None, string? Detail = null);

internal enum EngineBanner
{
    /// <summary>The engine died; a new one is starting and the document will be reopened in it.</summary>
    Restarting,

    /// <summary>Back, with the view where it was.</summary>
    Restored,

    /// <summary>The engine keeps dying; the document cannot be shown until it is reopened.</summary>
    Failed,
}

/// <summary>
/// One open document: its sandboxed document engine, a document in the app's one shared pool engine (placeholders, thumbnails, text and search), the view
/// state, the tile cache and the scheduler (master prompt 7.1, ADR-0006). Use it on the UI thread. It raises events on the UI thread.
/// <para>
/// Both engines come from the app's <see cref="EngineBudget"/> (ADR-0004 amendment A3): the document engine counts against its cap, and the pool engine is
/// shared with every other session, so this session closes its document in the pool but never the pool.
/// </para>
/// <para>
/// A crash of an engine is not the end of the document. <see cref="EngineSupervisor"/> starts a new engine and reopens the document from the
/// broker's own file handle. The view state lives here, in the UI process, so the scroll position and zoom are exactly where they were; the
/// tiles that were in flight come back as <see cref="RenderStatus.EngineLost"/> and the scheduler asks for them again.
/// </para>
/// </summary>
internal sealed partial class DocumentSession : IAsyncDisposable
{
    private const int FirstSizeChunk = 32;
    private const int SizeChunk = 128;
    private static readonly TimeSpan ZoomSettle = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ScrollIdle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PrefetchDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TileDeadline = TimeSpan.FromSeconds(15);

    private readonly EngineHost _host;
    private readonly DocumentStore _store;
    private readonly PresenterProxy _presenter;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _zoomTimer;
    private readonly DispatcherQueueTimer _idleTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _prefetchTimer;
    private readonly CancellationTokenSource _cts = new();
    private DocumentEngine? _main;
    private EngineSupervisor? _pool; // the budget's shared pool engine: never disposed here
    private SupervisedDocument? _document;
    private SupervisedDocument? _poolDocument;
    private TileScheduler? _scheduler;
    private SizeD[] _sizes = [];
    private readonly List<EngineTileRenderer> _renderers = [];
    private bool _darkPages;
    private long _scrollTick;
    private double _velocity;
    private bool _zoomPending;
    private bool _framePending;
    private bool _disposed;
    private bool _sizesComplete;
    private string? _password;
    private readonly NavigationHistory _history = new();

    public DocumentSession(EngineHost host, DocumentStore store, ITilePresenter presenter, DispatcherQueue ui)
    {
        _host = host;
        _store = store;
        _presenter = new PresenterProxy(presenter);
        _ui = ui;
        Cache = new TileCache(TileCache.DefaultBudget(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
        _zoomTimer = ui.CreateTimer();
        _zoomTimer.IsRepeating = false;
        _zoomTimer.Interval = ZoomSettle;
        _zoomTimer.Tick += (_, _) =>
        {
            _zoomPending = false;
            Publish();
            RequestFrame();
            PrefetchVisible();
        };
        _idleTimer = ui.CreateTimer();
        _idleTimer.IsRepeating = false;
        _idleTimer.Interval = ScrollIdle;
        _idleTimer.Tick += (_, _) =>
        {
            _velocity = 0;
            if (!_zoomPending)
            {
                Publish();
            }

            PrefetchVisible();
        };
        _saveTimer = ui.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = SaveDelay;
        _saveTimer.Tick += (_, _) => SavePosition();
        _prefetchTimer = ui.CreateTimer();
        _prefetchTimer.IsRepeating = false;
        _prefetchTimer.Interval = PrefetchDelay;
        _prefetchTimer.Tick += (_, _) => PrefetchVisible();
        Controller.PrefetchAdjacent = TestSeams.Get(MikePDF.Core.TestSeamGate.NoPrefetch) != "1"; // (a journey measures the turn with and without it)
    }

    public ViewportController Controller { get; } = new();

    public TileCache Cache { get; }

    public string Path { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public bool IsOpen => _document is not null && !_disposed;

    /// <summary>Back and forward have somewhere to go (the toolbar buttons follow these). Changes are announced through <see cref="ViewChanged"/>.</summary>
    public bool CanGoBack => _history.CanGoBack;

    public bool CanGoForward => _history.CanGoForward;

    /// <summary>The permission bits that apply to this session: the file's /P, or all ones for an unprotected document or after the owner password.</summary>
    public uint Permissions { get; private set; } = uint.MaxValue;

    /// <summary>The owner has not forbidden copying text (bit 5 of /P). Screen readers are served either way (PDF 2.0 ignores the accessibility bit).</summary>
    public bool CanCopy => (Permissions & 0x10) != 0;

    /// <summary>The displayed size in points of every page whose size is known (pages not yet read carry the first page's size).</summary>
    public IReadOnlyList<SizeD> PageSizes => _sizes;

    /// <summary>Every page's size has been read, so "the pages differ in size" can be said truthfully.</summary>
    public bool PageSizesComplete => _sizesComplete;

    /// <summary>The newest frame handed to the presenter (diagnostics and tests).</summary>
    public FrameComposition? LastFrame { get; private set; }

    public TileSchedulerStats? Stats => _scheduler?.Stats;

    /// <summary>The page, zoom, rotation or document title changed. UI thread.</summary>
    public event Action? ViewChanged;

    public event Action<EngineBanner>? EngineStateChanged;

    /// <summary>The document engine was suspended by the budget, is resuming, runs again or failed (docs/features/tabs-windows-session.md 3.8). Raised on the UI thread.</summary>
    public event Action<DocumentEngineState>? EngineLifeChanged;

    /// <summary>A page could not be drawn completely (zero-based page index).</summary>
    public event Action<int>? PageIncomplete;

    /// <summary>The state of the document engine under the budget: running, suspended to make room for another document, resuming, or failed.</summary>
    public DocumentEngineState EngineState => _main?.State ?? DocumentEngineState.Running;

    /// <summary>The page, zoom, fit and rotation as a tab keeps them (session file, closed-tab history).</summary>
    public TabViewState CaptureView()
    {
        var (page, fraction) = Controller.TopPosition;
        var (zoom, fit, mode) = Controller.PersistedView;
        return new TabViewState(Math.Max(0, page), Math.Clamp(fraction, 0, 1), zoom, fit, Controller.ViewTurns, mode);
    }

    // ---- moving between windows ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows this document in another window's page surface (docs/features/tabs-windows-session.md 3.4). Nothing reloads: the engines, the tile cache, the selection, the find state and the view
    /// stay as they are; the tiles are GPU bitmaps on the app's one shared device, so they draw in any window. Call <see cref="DetachPresenter"/> on the old window first.
    /// </summary>
    public void AttachPresenter(ITilePresenter presenter)
    {
        _presenter.Attach(presenter);
        RequestFrame();
    }

    /// <summary>The window stopped showing this document (another tab is shown, or the tab is moving): frames are dropped, tiles keep arriving into the cache.</summary>
    public void DetachPresenter() => _presenter.Detach();

    /// <summary>Keeps the document engine from being suspended until the lease is disposed (a tab on screen, a save or print job). Null before the document is open.</summary>
    public IDisposable? PinEngine() => _main?.Pin();

    /// <summary>Resumes a suspended engine now, ahead of the first call that would (a tab about to be shown). <c>Timeout</c> when every slot stayed busy or pinned.</summary>
    public async Task<Status> ResumeEngineAsync()
    {
        if (_main is not { IsSuspended: true } engine || _disposed)
        {
            return Status.Ok;
        }

        return await engine.ResumeAsync(_cts.Token).ConfigureAwait(true);
    }

    // ---- opening -----------------------------------------------------------------------------

    /// <summary>
    /// Opens the document, or reports why not. Call again with a password after <see cref="OpenOutcomeKind.PasswordRequired"/> or
    /// <see cref="OpenOutcomeKind.PasswordIncorrect"/>: the engines stay up.
    /// </summary>
    public async Task<OpenOutcome> OpenAsync(string path, string? password, TabViewState? initialView = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(Path))
        {
            return new OpenOutcome(OpenOutcomeKind.Failed, DocumentErrorKind.FileNotFound);
        }

        var token = _cts.Token;
        HoldFile();
        try
        {
            _main ??= await StartDocumentEngineAsync(token).ConfigureAwait(true);
            var opened = await _main.OpenDocumentAsync(Path, password, token).ConfigureAwait(true);
            switch (opened.Status)
            {
                case Status.Ok when opened.Value is not null:
                    break;
                case Status.PasswordRequired:
                    return new OpenOutcome(OpenOutcomeKind.PasswordRequired, DocumentErrorKind.PasswordRequired);
                case Status.PasswordIncorrect:
                    return new OpenOutcome(OpenOutcomeKind.PasswordIncorrect, DocumentErrorKind.PasswordIncorrect);
                default:
                    var kind = DocumentErrorInfo.FromStatus(opened.Status);
                    return new OpenOutcome(OpenOutcomeKind.Failed, kind == DocumentErrorKind.None ? DocumentErrorKind.Unknown : kind, opened.Detail);
            }

            _document = opened.Value;
            _password = password;
            await StartPoolAsync(token).ConfigureAwait(true);

            var info = await _document.GetInfoAsync(token).ConfigureAwait(true);
            Title = info.Value is { Title.Length: > 0 } reply ? reply.Title : System.IO.Path.GetFileNameWithoutExtension(Path);
            Permissions = info.Value?.Permissions ?? uint.MaxValue;
            SignatureCount = (int)(info.Value?.SignatureCount ?? 0);
            await ReadDirectionAsync(token).ConfigureAwait(true);
            if (await LoadFirstSizesAsync(token).ConfigureAwait(true) is { } sizeError)
            {
                return new OpenOutcome(OpenOutcomeKind.Failed, sizeError);
            }
        }
        catch (OperationCanceledException)
        {
            return new OpenOutcome(OpenOutcomeKind.Failed, DocumentErrorKind.Unknown, "cancelled");
        }
        catch (EngineStartException e)
        {
            return new OpenOutcome(OpenOutcomeKind.Failed, DocumentErrorKind.SandboxUnavailable, e.Message);
        }
        catch (TimeoutException e)
        {
            // Every document engine of the budget is busy or pinned (not reachable while the app has one document at a time).
            return new OpenOutcome(OpenOutcomeKind.Failed, DocumentErrorKind.SandboxUnavailable, e.Message);
        }

        StartScheduler();
        StartReading();
        _store.AddRecent(Path);
        ApplySavedPosition(initialView);
        Publish();
        RequestFrame();
        ViewChanged?.Invoke();
        PrefetchVisible();
        _ = LoadRemainingSizesAsync(token);
        return new OpenOutcome(OpenOutcomeKind.Opened);
    }

    /// <summary>
    /// The broker's own handle on the document, opened for reading with the share flags every document is opened with (ADR-0005 A3: others may rename or replace the path, not write to the file). A save checks the
    /// path against this file's identity (docs/features/save-pipeline.md 3.6 item 3). Null when the file could not be held; the save then makes no identity check.
    /// </summary>
    public Microsoft.Win32.SafeHandles.SafeFileHandle? HeldFile { get; private set; }

    /// <summary>How many signature fields the document has (a flattened copy or a whole-file rewrite makes them invalid).</summary>
    public int SignatureCount { get; private set; }

    /// <summary>The document was opened with a password: MikePDF cannot write encrypted objects, so it cannot save edits into it.</summary>
    public bool OpenedWithPassword => _password is not null;

    private void HoldFile()
    {
        HeldFile?.Dispose();
        try
        {
            HeldFile = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HeldFile = null;
        }
    }

    // The binding side the file asks for (/ViewerPreferences /Direction, schema 5). The engine answers 0 left to right, 1 right to left, 2 absent; an engine that cannot answer leaves it automatic.
    private async Task ReadDirectionAsync(CancellationToken token)
    {
        var reply = await _document!.GetViewerPreferencesAsync(token).ConfigureAwait(true);
        if (reply.IsOk && reply.Value is { } preferences)
        {
            Controller.SetDocumentDirection(preferences.Direction switch
            {
                0 => ReadingDirection.LeftToRight,
                1 => ReadingDirection.RightToLeft,
                _ => ReadingDirection.Auto,
            });
        }
    }

    /// <summary>The document's own engine, from the budget: it counts against the cap on document engines.</summary>
    private async Task<DocumentEngine> StartDocumentEngineAsync(CancellationToken token)
    {
        var engine = await _host.Budget.CreateDocumentEngineAsync(token).ConfigureAwait(true);
        engine.EngineCrashed += (_, _) => _ui.TryEnqueue(() => EngineStateChanged?.Invoke(EngineBanner.Restarting));
        engine.Restored += (_, _) => _ui.TryEnqueue(() =>
        {
            EngineStateChanged?.Invoke(EngineBanner.Restored);
            Publish();
            RequestFrame();
        });
        engine.Failed += (_, _) => _ui.TryEnqueue(() => EngineStateChanged?.Invoke(EngineBanner.Failed));
        engine.StateChanged += state => _ui.TryEnqueue(() => EngineLifeChanged?.Invoke(state));
        return engine;
    }

    private async Task StartPoolAsync(CancellationToken token)
    {
        try
        {
            // The one pool engine of the whole app: this session only opens its document in it.
            _pool = await _host.Budget.GetPoolAsync(token).ConfigureAwait(true);
            var opened = await _pool.OpenDocumentAsync(Path, _password, token).ConfigureAwait(true);
            _poolDocument = opened.IsOk ? opened.Value : null;
        }
        catch (EngineStartException)
        {
            // Placeholders then come from the document's own engine: slower to appear, but still never blank.
            _pool = null;
            _poolDocument = null;
        }
    }

    private void StartScheduler()
    {
        var tiles = new EngineTileRenderer(_document!, TileDeadline) { DarkPages = _darkPages };
        _renderers.Add(tiles);
        ITileRenderer lowRes = tiles;
        if (_poolDocument is { } pool)
        {
            var placeholders = new EngineTileRenderer(pool, TileDeadline) { DarkPages = _darkPages };
            _renderers.Add(placeholders);
            lowRes = placeholders;
        }

        _scheduler = new TileScheduler(tiles, lowRes, _presenter, Cache);
        _scheduler.TileReady += _ => _ui.TryEnqueue(RequestFrame);
        _scheduler.PageStatus += (page, status) =>
        {
            if (status == RenderStatus.Partial)
            {
                _ui.TryEnqueue(() => PageIncomplete?.Invoke(page));
            }
        };
    }

    // ---- page sizes (lazy, ADR-0006 decision 4) -----------------------------------------------

    /// <summary>Reads the sizes of the first pages (enough for the first paint). Returns the error to show, or null.</summary>
    private async Task<DocumentErrorKind?> LoadFirstSizesAsync(CancellationToken token)
    {
        var count = (int)_document!.PageCount;
        if (count == 0)
        {
            return DocumentErrorKind.Damaged;
        }

        var first = Math.Min(count, FirstSizeChunk);
        var reply = await _document.GetPageSizesAsync(0, (uint)first, token).ConfigureAwait(true);
        if (!reply.IsOk || reply.Value is null)
        {
            var kind = DocumentErrorInfo.FromStatus(reply.Status);
            return kind == DocumentErrorKind.None ? DocumentErrorKind.Damaged : kind;
        }

        var fallback = new SizeD(reply.Value.Widths[0], reply.Value.Heights[0]);
        _sizes = new SizeD[count];
        Array.Fill(_sizes, fallback);
        for (var i = 0; i < first; i++)
        {
            _sizes[i] = new SizeD(reply.Value.Widths[i], reply.Value.Heights[i]);
        }

        Controller.SetPageSizes(_sizes);
        _sizesComplete = count <= first;
        return null;
    }

    private async Task LoadRemainingSizesAsync(CancellationToken token)
    {
        try
        {
            var count = _sizes.Length;
            var start = FirstSizeChunk;
            var attempts = 0;
            while (start < count && !token.IsCancellationRequested)
            {
                var n = Math.Min(SizeChunk, count - start);
                var reply = await _document!.GetPageSizesAsync((uint)start, (uint)n, token).ConfigureAwait(false);
                if (!reply.IsOk || reply.Value is null)
                {
                    // An engine restart: try the chunk again shortly. A damaged chunk keeps the assumed sizes; the page reports its own error when drawn.
                    if (reply.Status is Status.EngineCrashed or Status.Timeout && ++attempts < 20)
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        continue;
                    }

                    attempts = 0;
                    start += SizeChunk;
                    continue;
                }

                attempts = 0;
                var chunkStart = start;
                start += SizeChunk;

                var chunk = reply.Value;
                var last = chunkStart + n >= count;
                _ui.TryEnqueue(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var copy = _sizes.ToArray();
                    for (var i = 0; i < n; i++)
                    {
                        copy[chunkStart + i] = new SizeD(chunk.Widths[i], chunk.Heights[i]);
                    }

                    _sizes = copy;
                    _sizesComplete |= last;
                    Controller.SetPageSizes(_sizes);
                    Publish();
                    RequestFrame();
                    ViewChanged?.Invoke();
                });
                if (!last)
                {
                    await Task.Delay(20, token).ConfigureAwait(false); // leave the engine thread to the tiles
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    // A view handed over (a restored session, a reopened tab) wins over the position remembered for the file; its page is clamped when the file has fewer pages now.
    private void ApplySavedPosition(TabViewState? initial)
    {
        if (initial is not null)
        {
            Controller.Restore(Math.Clamp(initial.Page, 0, Math.Max(0, Controller.PageCount - 1)), initial.PageFraction, initial.Zoom, initial.Fit, initial.ViewTurns, initial.Mode);
        }
        else if (_store.GetPosition(Path) is { } saved && saved.Page < Controller.PageCount)
        {
            Controller.Restore(saved.Page, saved.PageFraction, saved.Zoom, saved.Fit, saved.ViewTurns, saved.Mode);
        }
        else if (_store.DefaultMode != ViewMode.Default)
        {
            Controller.SetMode(_store.DefaultMode); // a document seen for the first time: the layout the person chose as their default
        }
    }

    // ---- input from the page surface ---------------------------------------------------------

    public void SetViewport(double width, double height, double dpiScale) => Change(() => Controller.SetViewport(width, height, dpiScale));

    public void ScrollBy(double dx, double dy)
    {
        AppTrace.ScrollInput();
        Change(() => Controller.ScrollBy(dx, dy), trackVelocity: true);
    }

    public void ZoomBy(double factor, PointD? anchor = null) => Change(() => Controller.ZoomBy(factor, anchor));

    public void ZoomStep(int direction) => Change(() => Controller.ZoomStep(direction));

    public void ZoomTo(double zoom) => Change(() => Controller.ZoomTo(zoom));

    public void ActualSize() => Change(() => Controller.ActualSize());

    /// <summary>The zoom at which a page's inch is the monitor's inch, given the monitor's physical density (see <see cref="ZoomMath.ActualSizeZoom"/>).</summary>
    public void PhysicalSize(double rawMonitorDpi) => Change(() => Controller.PhysicalSize(rawMonitorDpi));

    public void FitWidth() => Change(() => Controller.FitWidth());

    public void FitPage() => Change(() => Controller.FitPage());

    public void Rotate(int quarterTurns) => Change(() => Controller.Rotate(quarterTurns));

    public void GoToPage(int zeroBasedPage) => Change(() => Controller.GoToPage(zeroBasedPage));

    /// <summary>Go to page as a jump: the place the view was at is remembered for Back (Go to page, a thumbnail).</summary>
    public void JumpToPage(int zeroBasedPage)
    {
        var before = HistoryMark.Take(this);
        GoToPage(zeroBasedPage);
        RecordJump(before, null);
    }

    /// <summary>Back: returns to where the view was before the last jump. False when there is nowhere to go.</summary>
    public bool GoBack() => GoTo(_history.Back(Here()));

    public bool GoForward() => GoTo(_history.Forward(Here()));

    private bool GoTo(ViewLocation? target)
    {
        if (target is not { } place || _disposed)
        {
            return false;
        }

        Change(() => Controller.GoToPage(place.Page, place.Fraction));
        return true;
    }

    private ViewLocation Here()
    {
        var (page, fraction) = Controller.TopPosition;
        return new ViewLocation(Math.Max(0, page), fraction);
    }

    /// <summary>Where the view was just before a jump: the place for Back, and the scroll offsets to tell whether the jump moved the view at all.</summary>
    private readonly record struct HistoryMark(ViewLocation Place, double ScrollX, double ScrollY)
    {
        public static HistoryMark Take(DocumentSession session) => new(session.Here(), session.Controller.ScrollX, session.Controller.ScrollY);
    }

    // A jump that left the view where it was (the destination was already in view) records nothing, so Back never "goes" nowhere.
    private void RecordJump(HistoryMark before, string? key)
    {
        if (_disposed || (Math.Abs(Controller.ScrollY - before.ScrollY) < 2 && Math.Abs(Controller.ScrollX - before.ScrollX) < 2))
        {
            return;
        }

        if (_history.Record(before.Place, key))
        {
            ViewChanged?.Invoke();
        }
    }

    // ---- dark pages ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>The engine draws dark pages (inverted, colour photographs left alone). Whoever shows the tiles draws them with <c>PageColourMode.Normal</c>, or they would be inverted twice.</summary>
    public bool DarkPages => _darkPages;

    /// <summary>
    /// Turns engine-drawn dark pages on or off. The tile cache holds the other colours, so it is emptied and the view asks for its tiles again; a tile that was in flight under the
    /// old setting is thrown away when it lands (see <see cref="EngineTileRenderer"/>).
    /// </summary>
    public void SetDarkPages(bool on)
    {
        if (on == _darkPages || _disposed)
        {
            return;
        }

        _darkPages = on;
        foreach (var renderer in _renderers)
        {
            renderer.DarkPages = on;
        }

        Cache.Clear();
        Cache.DisposeEvicted();
        Publish();
        RequestFrame();
    }

    // ---- view modes (docs/features/page-view-modes.md) ---------------------------------------------------------------------------------

    /// <summary>How pages are arranged and turned now, and the user's reading-direction choice.</summary>
    public ViewMode Mode => Controller.Mode;

    public bool Presenting => Controller.Presenting;

    public void SetMode(ViewMode mode) => Change(() => Controller.SetMode(mode));

    public void EnterPresentation() => Change(Controller.EnterPresentation);

    public void ExitPresentation() => Change(Controller.ExitPresentation);

    /// <summary>The next (above 0) or previous spread in the paged flow; the page in the continuous flow. False at either end.</summary>
    public bool Turn(int direction)
    {
        var turned = false;
        Change(() => turned = Controller.Turn(direction));
        return turned;
    }

    /// <summary>A page was turned in a presentation: milliseconds from the key press to the first frame drawn, and to the first frame with every tile sharp (no placeholder left in it). UI thread.</summary>
    public event Action<double, double>? TurnMeasured;

    private long _turnStarted;
    private double _turnFirstFrameMs = -1;

    // Called with every frame handed to the presenter: closes a turn that is being timed once the page on screen is sharp all over. (The GPU draw of that frame follows within one refresh.)
    private void MeasureTurn(FrameComposition frame)
    {
        if (_turnStarted == 0)
        {
            return;
        }

        var ms = (Stopwatch.GetTimestamp() - _turnStarted) * 1000.0 / Stopwatch.Frequency;
        if (_turnFirstFrameMs < 0)
        {
            _turnFirstFrameMs = ms;
        }

        if (frame.SharpArea > 0 && frame.FallbackArea + frame.BlankArea <= 0.002 * (frame.SharpArea + frame.FallbackArea + frame.BlankArea)) // sharp all over, give or take a seam
        {
            _turnStarted = 0;
            TurnMeasured?.Invoke(_turnFirstFrameMs, ms);
        }
        else if (ms > 10_000)
        {
            _turnStarted = 0; // never sharp (a page that cannot be drawn): not a measurement
        }
    }

    public bool WheelNotch(double dyPx, long tickMs)
    {
        var turned = false;
        Change(() => turned = Controller.WheelNotch(dyPx, tickMs), trackVelocity: true);
        return turned;
    }

    public void ArrowVertical(int direction, double stepPx) => Change(() => Controller.ArrowVertical(direction, stepPx), trackVelocity: true);

    public void ArrowHorizontal(int direction, double stepPx) => Change(() => Controller.ArrowHorizontal(direction, stepPx));

    public void PageDown() => Change(Controller.PageDown, trackVelocity: true);

    public void PageUp() => Change(Controller.PageUp, trackVelocity: true);

    public void NextPage() => Change(Controller.NextPage);

    public void PreviousPage() => Change(Controller.PreviousPage);

    public void FirstPage() => Change(Controller.FirstPage);

    public void LastPage() => Change(Controller.LastPage);

    public void ScrollScreen(int direction) => Change(() => Controller.ScrollScreen(direction), trackVelocity: true);

    /// <summary>Minimise or memory pressure: keep only what is on screen (master prompt 7.1 rule 5).</summary>
    public void TrimMemory()
    {
        Cache.TrimToVisible();
        Cache.DisposeEvicted();
    }

    private void Change(Action action, bool trackVelocity = false)
    {
        if (_disposed)
        {
            return;
        }

        var before = Controller.Layout;
        var scrollBefore = Controller.ScrollY;
        var started = Stopwatch.GetTimestamp();
        var spreadBefore = Controller.Presenting ? Controller.CurrentSpread : -1;
        action();
        if (spreadBefore >= 0 && Controller.Presenting && Controller.CurrentSpread != spreadBefore)
        {
            _turnStarted = started; // a page was turned in a presentation, by whatever key: timed until the page on screen is sharp all over
            _turnFirstFrameMs = -1;
        }

        var after = Controller.Layout;
        var layoutChanged = after.RenderScale != before.RenderScale || after.ViewTurns != before.ViewTurns || after.PageCount != before.PageCount;
        if (trackVelocity)
        {
            TrackVelocity(Controller.ScrollY - scrollBefore);
        }

        if (layoutChanged && _scheduler is not null)
        {
            // Scale what is cached on the GPU now; render at the new scale once the gesture has settled (master prompt 7.1 rule 6).
            _zoomPending = true;
            _zoomTimer.Stop();
            _zoomTimer.Start();
        }
        else if (!_zoomPending)
        {
            Publish();
        }

        RequestFrame();
        ViewChanged?.Invoke();
        _saveTimer.Stop();
        _saveTimer.Start();
        _prefetchTimer.Stop(); // the text of the pages in view is read once the view has stopped moving
        _prefetchTimer.Start();
    }

    private void TrackVelocity(double dy)
    {
        var now = Stopwatch.GetTimestamp();
        if (_scrollTick != 0)
        {
            var dt = (now - _scrollTick) / (double)Stopwatch.Frequency;
            if (dt > 0.001)
            {
                _velocity = (0.5 * _velocity) + (0.5 * (dy / dt));
            }
        }

        _scrollTick = now;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void Publish()
    {
        if (_scheduler is null || _disposed)
        {
            return;
        }

        _scheduler.Update(Controller.Snapshot(_velocity));
    }

    /// <summary>Asks for a repaint; coalesced to one per UI-thread turn.</summary>
    public void RequestFrame()
    {
        if (_framePending || _disposed || !_presenter.IsAttached)
        {
            return;
        }

        _framePending = true;
        _ui.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            _framePending = false;
            if (_disposed)
            {
                return;
            }

            if (!_presenter.IsAttached)
            {
                return; // the window moved on to another tab since this frame was asked for
            }

            var frame = FrameComposer.Compose(Controller.Snapshot(_velocity), Cache);
            LastFrame = frame;
            MeasureTurn(frame);
            _presenter.Present(frame);
            Cache.DisposeEvicted(); // after the new frame replaced the old one: nothing draws an evicted surface any more
        });
    }

    // ---- persistence ---------------------------------------------------------------------------

    public void SavePosition()
    {
        if (!IsOpen || Controller.PageCount == 0)
        {
            return;
        }

        var (page, fraction) = Controller.TopPosition;
        var (zoom, fit, mode) = Controller.PersistedView;
        _store.SavePosition(Path, page, fraction, zoom, fit, Controller.ViewTurns, mode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        SavePosition();
        _disposed = true;
        StopReading();
        _zoomTimer.Stop();
        _idleTimer.Stop();
        _saveTimer.Stop();
        _prefetchTimer.Stop();
        await _cts.CancelAsync().ConfigureAwait(true);
        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync().ConfigureAwait(true);
        }

        if (_document is not null)
        {
            await _document.DisposeAsync().ConfigureAwait(true);
        }

        if (_poolDocument is not null)
        {
            await _poolDocument.DisposeAsync().ConfigureAwait(true);
        }

        await DisposeServicesAsync().ConfigureAwait(true);

        if (_main is not null)
        {
            await _main.DisposeAsync().ConfigureAwait(true);
        }

        // (The pool engine is the budget's and is shared: the documents this session had open in it are closed above, the engine stays.)
        HeldFile?.Dispose();
        HeldFile = null;
        Cache.Clear();
        Cache.DisposeEvicted();
        _cts.Dispose();
    }
}

/// <summary>
/// The presenter a session draws through, which can change: the session survives a move to another window (a tab torn out, a tab merged), and the scheduler holds this proxy, not a window's
/// presenter. Uploads (<see cref="CreateSurface"/>) are independent of the window (the app's one shared GPU device), so they go on while no window shows the session.
/// </summary>
internal sealed class PresenterProxy(ITilePresenter first) : ITilePresenter
{
    private ITilePresenter _creator = first;
    private volatile ITilePresenter? _target = first;

    public bool IsAttached => _target is not null;

    public void Attach(ITilePresenter presenter)
    {
        _creator = presenter;
        _target = presenter;
    }

    public void Detach() => _target = null;

    public ITileSurface CreateSurface(IPixelBuffer pixels) => (_target ?? _creator).CreateSurface(pixels);

    public void Present(FrameComposition frame) => _target?.Present(frame);
}
