using System.Diagnostics;
using Microsoft.UI.Dispatching;
using MikePDF.Broker;
using MikePDF.Core.Errors;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Persistence;
using MikePDF.Core.Tiles;
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
/// One open document: its sandboxed engines (the document's own and the pool engine for placeholders), the view state, the tile cache and the
/// scheduler (master prompt 7.1, ADR-0006). Use it on the UI thread. It raises events on the UI thread.
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
    private readonly ITilePresenter _presenter;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _zoomTimer;
    private readonly DispatcherQueueTimer _idleTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _prefetchTimer;
    private readonly CancellationTokenSource _cts = new();
    private EngineSupervisor? _main;
    private EngineSupervisor? _pool;
    private SupervisedDocument? _document;
    private SupervisedDocument? _poolDocument;
    private TileScheduler? _scheduler;
    private SizeD[] _sizes = [];
    private long _scrollTick;
    private double _velocity;
    private bool _zoomPending;
    private bool _framePending;
    private bool _disposed;
    private string? _password;

    public DocumentSession(EngineHost host, DocumentStore store, ITilePresenter presenter, DispatcherQueue ui)
    {
        _host = host;
        _store = store;
        _presenter = presenter;
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
    }

    public ViewportController Controller { get; } = new();

    public TileCache Cache { get; }

    public string Path { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public bool IsOpen => _document is not null && !_disposed;

    /// <summary>The newest frame handed to the presenter (diagnostics and tests).</summary>
    public FrameComposition? LastFrame { get; private set; }

    public TileSchedulerStats? Stats => _scheduler?.Stats;

    /// <summary>The page, zoom, rotation or document title changed. UI thread.</summary>
    public event Action? ViewChanged;

    public event Action<EngineBanner>? EngineStateChanged;

    /// <summary>A page could not be drawn completely (zero-based page index).</summary>
    public event Action<int>? PageIncomplete;

    // ---- opening -----------------------------------------------------------------------------

    /// <summary>
    /// Opens the document, or reports why not. Call again with a password after <see cref="OpenOutcomeKind.PasswordRequired"/> or
    /// <see cref="OpenOutcomeKind.PasswordIncorrect"/>: the engines stay up.
    /// </summary>
    public async Task<OpenOutcome> OpenAsync(string path, string? password)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(Path))
        {
            return new OpenOutcome(OpenOutcomeKind.Failed, DocumentErrorKind.FileNotFound);
        }

        var token = _cts.Token;
        try
        {
            _main ??= await StartEngineAsync(_host.DocumentOptions(), token).ConfigureAwait(true);
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

        StartScheduler();
        StartReading();
        _store.AddRecent(Path);
        ApplySavedPosition();
        Publish();
        RequestFrame();
        ViewChanged?.Invoke();
        PrefetchVisible();
        _ = LoadRemainingSizesAsync(token);
        return new OpenOutcome(OpenOutcomeKind.Opened);
    }

    private async Task<EngineSupervisor> StartEngineAsync(EngineSupervisorOptions options, CancellationToken token)
    {
        var supervisor = await EngineSupervisor.StartAsync(options, token).ConfigureAwait(true);
        if (options.Kind == EngineKind.Document)
        {
            supervisor.EngineCrashed += (_, _) => _ui.TryEnqueue(() => EngineStateChanged?.Invoke(EngineBanner.Restarting));
            supervisor.Restored += (_, _) => _ui.TryEnqueue(() =>
            {
                EngineStateChanged?.Invoke(EngineBanner.Restored);
                Publish();
                RequestFrame();
            });
            supervisor.Failed += (_, _) => _ui.TryEnqueue(() => EngineStateChanged?.Invoke(EngineBanner.Failed));
        }

        return supervisor;
    }

    private async Task StartPoolAsync(CancellationToken token)
    {
        try
        {
            _pool = await StartEngineAsync(_host.PoolOptions(), token).ConfigureAwait(true);
            var opened = await _pool.OpenDocumentAsync(Path, _password, token).ConfigureAwait(true);
            _poolDocument = opened.IsOk ? opened.Value : null;
        }
        catch (EngineStartException)
        {
            // Placeholders then come from the document's own engine: slower to appear, but still never blank.
            _poolDocument = null;
        }
    }

    private void StartScheduler()
    {
        var tiles = new EngineTileRenderer(_document!, TileDeadline);
        ITileRenderer lowRes = _poolDocument is { } pool ? new EngineTileRenderer(pool, TileDeadline) : tiles;
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

    private void ApplySavedPosition()
    {
        if (_store.GetPosition(Path) is { } saved && saved.Page < Controller.PageCount)
        {
            Controller.Restore(saved.Page, saved.PageFraction, saved.Zoom, saved.Fit, saved.ViewTurns);
        }
    }

    // ---- input from the page surface ---------------------------------------------------------

    public void SetViewport(double width, double height, double dpiScale) => Change(() => Controller.SetViewport(width, height, dpiScale));

    public void ScrollBy(double dx, double dy) => Change(() => Controller.ScrollBy(dx, dy), trackVelocity: true);

    public void ZoomBy(double factor, PointD? anchor = null) => Change(() => Controller.ZoomBy(factor, anchor));

    public void ZoomStep(int direction) => Change(() => Controller.ZoomStep(direction));

    public void ZoomTo(double zoom) => Change(() => Controller.ZoomTo(zoom));

    public void ActualSize() => Change(() => Controller.ActualSize());

    public void FitWidth() => Change(() => Controller.FitWidth());

    public void FitPage() => Change(() => Controller.FitPage());

    public void Rotate(int quarterTurns) => Change(() => Controller.Rotate(quarterTurns));

    public void GoToPage(int zeroBasedPage) => Change(() => Controller.GoToPage(zeroBasedPage));

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
        action();
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
        if (_framePending || _disposed)
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

            var frame = FrameComposer.Compose(Controller.Snapshot(_velocity), Cache);
            LastFrame = frame;
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
        _store.SavePosition(Path, page, fraction, Controller.Zoom, Controller.Fit, Controller.ViewTurns);
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

        if (_pool is not null)
        {
            await _pool.DisposeAsync().ConfigureAwait(true);
        }

        Cache.Clear();
        Cache.DisposeEvicted();
        _cts.Dispose();
    }
}
