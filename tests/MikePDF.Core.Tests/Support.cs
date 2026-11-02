using MikePDF.Core.Tiles;

namespace MikePDF.Core.Tests;

internal sealed class FakeSurface(int width = 512, int height = 512) : ITileSurface
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

internal sealed class FakePixels(int width, int height) : IPixelBuffer
{
    private readonly byte[] _bytes = new byte[width * height * 4];

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride => Width * 4;

    public bool Disposed { get; private set; }

    public ReadOnlySpan<byte> Pixels => _bytes;

    public void Dispose() => Disposed = true;
}

internal sealed class FakePresenter : ITilePresenter
{
    public List<FakeSurface> Created { get; } = [];

    public ITileSurface CreateSurface(IPixelBuffer pixels)
    {
        var surface = new FakeSurface(pixels.Width, pixels.Height);
        lock (Created)
        {
            Created.Add(surface);
        }

        return surface;
    }

    public void Present(FrameComposition frame)
    {
    }
}

internal sealed class FakeRenderer : ITileRenderer
{
    private int _running;
    private int _maxRunning;
    private readonly List<RenderRequest> _requests = [];

    public Func<RenderRequest, CancellationToken, Task<RenderResult>>? Behaviour { get; set; }

    public int MaxRunning => Volatile.Read(ref _maxRunning);

    public int Running => Volatile.Read(ref _running);

    public List<RenderRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
        {
        }

        try
        {
            if (Behaviour is not null)
            {
                return await Behaviour(request, cancellationToken).ConfigureAwait(false);
            }

            await Task.Yield();
            return new RenderResult(RenderStatus.Ok, new FakePixels(request.Width, request.Height));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    /// <summary>A renderer that never answers until cancelled.</summary>
    public static Func<RenderRequest, CancellationToken, Task<RenderResult>> Hang => async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return new RenderResult(RenderStatus.Cancelled);
    };
}
