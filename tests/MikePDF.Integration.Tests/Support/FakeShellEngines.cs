using MikePDF.Protocol;
using MikePDF.ShellHost;

namespace MikePDF.Integration.Tests.Support;

/// <summary>An engine double with no process: what it does on open and on render is a delegate, so a test can make it hang, crash, answer slowly or draw a known page.</summary>
internal sealed class FakeShellEngine : IShellEngine
{
    private readonly FakeShellEngineFactory _factory;
    private readonly CancellationTokenSource _killed = new();
    private int _ended;

    public FakeShellEngine(FakeShellEngineFactory factory, int serial)
    {
        _factory = factory;
        Serial = serial;
    }

    public int Serial { get; }

    public uint ProcessId => (uint)(100_000 + Serial);

    public bool IsAlive => Volatile.Read(ref _ended) == 0;

    public bool Killed => _killed.IsCancellationRequested;

    public int Opens { get; private set; }

    public int Renders { get; private set; }

    /// <summary>Cancelled when the engine is killed: a double that hangs waits on it, so that "killed" is visible as "the hung call ended".</summary>
    public CancellationToken KilledToken => _killed.Token;

    public async Task<ShellResult<ShellDocumentInfo>> OpenAsync(string path, CancellationToken cancellationToken)
    {
        Opens++;
        _factory.StagedPaths.Add(path);
        return await _factory.OnOpen(this, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShellResult<ShellRender>> RenderAsync(uint page, uint maxWidth, uint maxHeight, bool annotations, float maxScale, CancellationToken cancellationToken)
    {
        Renders++;
        return await _factory.OnRender(this, page, maxWidth, maxHeight, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask KillAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _killed.Cancel();
            _factory.NoteEnded(this, killed: true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => KillAsync();
}

/// <summary>Hands out <see cref="FakeShellEngine"/>s and counts them: started, alive, the most alive at once, killed.</summary>
internal sealed class FakeShellEngineFactory : IShellEngineFactory
{
    private int _serial;
    private int _alive;
    private int _peak;
    private int _killed;

    public TimeSpan StartDelay { get; set; } = TimeSpan.Zero;

    public bool FailToStart { get; set; }

    public int Started => Volatile.Read(ref _serial);

    public int Alive => Volatile.Read(ref _alive);

    public int Peak => Volatile.Read(ref _peak);

    public int Kills => Volatile.Read(ref _killed);

    public System.Collections.Concurrent.ConcurrentBag<FakeShellEngine> Engines { get; } = [];

    public System.Collections.Concurrent.ConcurrentBag<string> StagedPaths { get; } = [];

    /// <summary>The order in which engines were handed out, by serial: a queue that serves the newest first shows here.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<int> StartOrder { get; } = new();

    public Func<FakeShellEngine, string, CancellationToken, Task<ShellResult<ShellDocumentInfo>>> OnOpen { get; set; } =
        (_, _, _) => Task.FromResult(new ShellResult<ShellDocumentInfo>(Status.Ok, new ShellDocumentInfo(3, 612, 792)));

    /// <summary>The default draws a solid page of the size that fits the box, at the shape of a US Letter page.</summary>
    public Func<FakeShellEngine, uint, uint, uint, CancellationToken, Task<ShellResult<ShellRender>>> OnRender { get; set; } = (_, _, maxWidth, maxHeight, _) =>
    {
        var fit = FitMath.Fit(612, 792, maxWidth, maxHeight, FitMath.DefaultMaxScale)!.Value;
        var pixels = new byte[(int)(fit.Width * fit.Height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x40;
            pixels[i + 2] = 0x80;
        }

        return Task.FromResult(new ShellResult<ShellRender>(Status.Ok, new ArrayRender((int)fit.Width, (int)fit.Height, pixels)));
    };

    public async Task<IShellEngine> StartAsync(CancellationToken cancellationToken)
    {
        if (StartDelay > TimeSpan.Zero)
        {
            await Task.Delay(StartDelay, cancellationToken);
        }

        if (FailToStart)
        {
            throw new InvalidOperationException("no engine");
        }

        var serial = Interlocked.Increment(ref _serial);
        var engine = new FakeShellEngine(this, serial);
        Engines.Add(engine);
        StartOrder.Enqueue(serial);
        var alive = Interlocked.Increment(ref _alive);
        int peak;
        while ((peak = Volatile.Read(ref _peak)) < alive && Interlocked.CompareExchange(ref _peak, alive, peak) != peak)
        {
        }

        return engine;
    }

    internal void NoteEnded(FakeShellEngine engine, bool killed)
    {
        Interlocked.Decrement(ref _alive);
        if (killed)
        {
            Interlocked.Increment(ref _killed);
        }
    }
}

/// <summary>A clock a test moves by hand (the negative cache's ten minutes).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
