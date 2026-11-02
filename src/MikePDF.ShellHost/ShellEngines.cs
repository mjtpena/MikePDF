using MikePDF.Broker;
using MikePDF.Protocol;

namespace MikePDF.ShellHost;

/// <summary>The first page of a document an engine has opened.</summary>
internal sealed record ShellDocumentInfo(uint PageCount, float FirstWidth, float FirstHeight);

/// <summary>A page rendered by an engine. The engine is untrusted: the sizes were checked against the request by the broker, the pixels are whatever it wrote.</summary>
internal abstract class ShellRender : IDisposable
{
    public abstract int Width { get; }

    public abstract int Height { get; }

    public abstract int Stride { get; }

    /// <summary>Exactly <c>Stride * Height</c> bytes, valid until the render is disposed.</summary>
    public abstract ReadOnlySpan<byte> Pixels { get; }

    public abstract void Dispose();
}

/// <summary>A render that lives in a slot of the engine's ring until disposed.</summary>
internal sealed class TileRender(TileLease tile) : ShellRender
{
    public override int Width => tile.Width;

    public override int Height => tile.Height;

    public override int Stride => tile.Stride;

    public override ReadOnlySpan<byte> Pixels => tile.Pixels;

    public override void Dispose() => tile.Dispose();
}

/// <summary>A render held in an array (an engine double's, in tests).</summary>
internal sealed class ArrayRender(int width, int height, byte[] pixels) : ShellRender
{
    public override int Width => width;

    public override int Height => height;

    public override int Stride => width * 4;

    public override ReadOnlySpan<byte> Pixels => pixels;

    public override void Dispose()
    {
    }
}

/// <summary>The outcome of an engine call: a status and, for Ok, the value.</summary>
internal readonly record struct ShellResult<T>(Status Status, T? Value, string? Detail = null)
    where T : class
{
    public bool IsOk => Status == Status.Ok && Value is not null;
}

/// <summary>
/// One sandboxed engine for one file (ADR-0018 decision 5). The seam exists so that the queueing, the deadlines and the caches can be tested without a process
/// (fakes that hang, crash or answer slowly), while the engine tests run the real one.
/// </summary>
internal interface IShellEngine : IAsyncDisposable
{
    uint ProcessId { get; }

    /// <summary>False once the engine process is gone, however it went. A spare that died while it waited is not used.</summary>
    bool IsAlive { get; }

    Task<ShellResult<ShellDocumentInfo>> OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>Renders <paramref name="page"/> to fit <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, never larger than <paramref name="maxScale"/> times its natural size.</summary>
    Task<ShellResult<ShellRender>> RenderAsync(uint page, uint maxWidth, uint maxHeight, bool annotations, float maxScale, CancellationToken cancellationToken);

    /// <summary>Ends the process now, without asking it to leave. Safe at any time and more than once. A second call may return while the first is still working.</summary>
    ValueTask KillAsync();

    /// <summary>
    /// True once the process is really gone, false if it is still there after <paramref name="timeout"/>. <see cref="KillAsync"/> is a request and a second caller may return
    /// before the process has exited: the pool's cap counts processes, so it frees a slot only when this says true (SHELL-12).
    /// </summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout);
}

internal interface IShellEngineFactory
{
    Task<IShellEngine> StartAsync(CancellationToken cancellationToken);
}

/// <summary>The scale and size of a page fitted into a box. Pure, so the rule of spec 3.3 step 2 has a test of its own.</summary>
internal static class FitMath
{
    public const float DefaultMaxScale = 2f;

    /// <summary>
    /// The largest scale at which a page of <paramref name="pageWidth"/> x <paramref name="pageHeight"/> points fits the box, at most <paramref name="maxScale"/>, and the
    /// pixel size it gets there (rounded, at least 1, never above the box). Null when the page has no usable size.
    /// </summary>
    public static (float Scale, uint Width, uint Height)? Fit(float pageWidth, float pageHeight, uint maxWidth, uint maxHeight, float maxScale)
    {
        if (!(pageWidth > 0f && pageHeight > 0f && float.IsFinite(pageWidth) && float.IsFinite(pageHeight)) || maxWidth == 0 || maxHeight == 0)
        {
            return null;
        }

        var scale = MathF.Min(MathF.Min(maxWidth / pageWidth, maxHeight / pageHeight), maxScale);
        var width = (uint)Math.Clamp(MathF.Round(pageWidth * scale), 1f, maxWidth);
        var height = (uint)Math.Clamp(MathF.Round(pageHeight * scale), 1f, maxHeight);
        return (scale, width, height);
    }
}

/// <summary>The real engine: a sandboxed MikePDF.Engine.exe started with <c>--kind shell</c>, supervised by the broker with no restart (a crash on a file is that file's answer).</summary>
internal sealed class SandboxedShellEngine : IShellEngine
{
    private readonly EngineSupervisor _supervisor;
    private SupervisedDocument? _document;
    private ShellDocumentInfo? _first;
    private readonly ProcessExit _exit;
    private int _disposed;

    private SandboxedShellEngine(EngineSupervisor supervisor)
    {
        _supervisor = supervisor;
        _exit = ProcessExit.Watch(supervisor.EngineProcessId); // now, while the id still names this engine
    }

    public static async Task<SandboxedShellEngine> StartAsync(EngineSupervisorOptions options, CancellationToken cancellationToken) =>
        new(await EngineSupervisor.StartAsync(options, cancellationToken).ConfigureAwait(false));

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var exited = await _exit.WaitAsync(timeout).ConfigureAwait(false);
        if (exited)
        {
            _exit.Dispose();
        }

        return exited;
    }

    public uint ProcessId => _supervisor.EngineProcessId;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_supervisor.IsFailed && _supervisor.EngineProcessId != 0;

    public async Task<ShellResult<ShellDocumentInfo>> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var opened = await _supervisor.OpenDocumentAsync(path, null, cancellationToken).ConfigureAwait(false);
        if (!opened.IsOk || opened.Value is null)
        {
            return new(opened.Status, null, opened.Detail);
        }

        _document = opened.Value;
        var sizes = await _document.GetPageSizesAsync(0, 1, cancellationToken).ConfigureAwait(false);
        if (!sizes.IsOk || sizes.Value is null || sizes.Value.Widths.Length == 0 || sizes.Value.Heights.Length == 0)
        {
            return new(sizes.IsOk ? Status.Damaged : sizes.Status, null, sizes.Detail);
        }

        _first = new ShellDocumentInfo(_document.PageCount, sizes.Value.Widths[0], sizes.Value.Heights[0]);
        return new(Status.Ok, _first);
    }

    public async Task<ShellResult<ShellRender>> RenderAsync(uint page, uint maxWidth, uint maxHeight, bool annotations, float maxScale, CancellationToken cancellationToken)
    {
        if (_document is null || _first is null)
        {
            return new(Status.InvalidArgument, null, "no document");
        }

        var pageWidth = _first.FirstWidth;
        var pageHeight = _first.FirstHeight;
        if (page != 0)
        {
            var sizes = await _document.GetPageSizesAsync(page, 1, cancellationToken).ConfigureAwait(false);
            if (!sizes.IsOk || sizes.Value is null || sizes.Value.Widths.Length == 0 || sizes.Value.Heights.Length == 0)
            {
                return new(sizes.IsOk ? Status.InvalidArgument : sizes.Status, null, sizes.Detail);
            }

            pageWidth = sizes.Value.Widths[0];
            pageHeight = sizes.Value.Heights[0];
        }

        if (FitMath.Fit(pageWidth, pageHeight, maxWidth, maxHeight, maxScale) is not { } fit)
        {
            return new(Status.Damaged, null, "the page has no size");
        }

        // Annotations only. The spec also wants form fields drawn without highlight, but the engine answers Unsupported to the form-fields flag until forms arrive
        // (no form-fill environment yet): asking for it would fail every thumbnail. It is one more flag here when the engine takes it.
        var flags = annotations ? RenderTileArgs.Annotations : 0u;
        var rendered = await _document.RenderTileAsync(new RenderTileArgs(page, fit.Scale, 0, 0, 0, fit.Width, fit.Height, flags), 1, null, cancellationToken).ConfigureAwait(false);
        if (!rendered.IsOk || rendered.Value is null)
        {
            rendered.Value?.Dispose();
            return new(rendered.Status, null, rendered.Detail);
        }

        var tile = rendered.Value;
        // A page that did not finish is not shown: half a page is worse than the generic icon.
        if (tile.PartiallyRendered)
        {
            tile.Dispose();
            return new(Status.Timeout, null, "the page did not finish");
        }

        if (tile.Width != fit.Width || tile.Height != fit.Height || tile.Stride != 4 * tile.Width)
        {
            tile.Dispose();
            return new(Status.ProtocolError, null, "the engine returned another size than asked for");
        }

        // The slot of the ring is kept until the caller has copied the pixels out.
        return new(Status.Ok, new TileRender(tile));
    }

    public async ValueTask KillAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _supervisor.KillAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _supervisor.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Starts <see cref="SandboxedShellEngine"/>s with the options ShellHost chose (ADR-0018: 256 MB per engine, a small slot ring, no restart).</summary>
internal sealed class SandboxedShellEngineFactory(string enginePath, AppContainerProfile profile, long memoryLimitBytes = 256L << 20) : IShellEngineFactory
{
    public Task<IShellEngine> StartAsync(CancellationToken cancellationToken) => StartCoreAsync(cancellationToken);

    private async Task<IShellEngine> StartCoreAsync(CancellationToken cancellationToken) =>
        await SandboxedShellEngine.StartAsync(
            new EngineSupervisorOptions
            {
                EnginePath = enginePath,
                Profile = profile,
                Policy = new SandboxPolicy { ProcessMemoryLimitBytes = memoryLimitBytes },
                Kind = EngineKind.Shell,
                SlotCount = 2,
                SlotSize = 4 << 20, // 1024 x 1024 BGRA, the largest page the handler asks for
                StartupTimeout = TimeSpan.FromSeconds(10),
                DefaultRequestDeadline = TimeSpan.FromSeconds(5),
                // A shell engine has one file and one job. If it dies, that is the answer for the file: it is not restarted and the file is not opened again in a new one.
                MaxRestartsPerWindow = 0,
                MaxStartAttempts = 2,
            },
            cancellationToken).ConfigureAwait(false);
}
