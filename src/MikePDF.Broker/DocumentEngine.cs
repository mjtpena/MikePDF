using MikePDF.Protocol;

namespace MikePDF.Broker;

/// <summary>
/// One document engine under an <see cref="EngineBudget"/>: a <see cref="EngineSupervisor"/> (crash recovery, the watchdog, the documents opened in it) that the budget may
/// <b>suspend</b> to make room for a more recently used document and that comes back by itself when a call needs it. A <see cref="SupervisedDocument"/> opened here keeps
/// working across a suspension: its file handle, password and id stay with the broker, and the call that finds the engine suspended waits for it to resume.
/// </summary>
public sealed class DocumentEngine : IAsyncDisposable
{
    private readonly EngineBudget _budget;
    private EngineSupervisor? _supervisor;
    private int _state = (int)DocumentEngineState.Running;
    private int _disposed;

    internal DocumentEngine(EngineBudget budget) => _budget = budget;

    /// <summary>The crash, the restart and the end of the supervisor's patience, as <see cref="EngineSupervisor"/> reports them. A suspension and a resume are not among them.</summary>
    public event EventHandler<EngineCrashedEventArgs>? EngineCrashed;

    public event EventHandler<EngineRestoredEventArgs>? Restored;

    public event EventHandler<EngineCrashedEventArgs>? Failed;

    /// <summary>The engine was suspended, is resuming, runs again or failed. Raised on the thread that caused it; handlers must not block.</summary>
    public event Action<DocumentEngineState>? StateChanged;

    /// <summary>The supervisor under this engine, for the documents opened through it and for tests.</summary>
    internal EngineSupervisor Supervisor => _supervisor ?? throw new InvalidOperationException("The engine has not been started.");

    /// <summary>True while this engine holds one of the budget's slots (guarded by the budget's lock).</summary>
    internal bool HoldsSlot { get; set; }

    public DocumentEngineState State => (DocumentEngineState)Volatile.Read(ref _state);

    public bool IsSuspended => State is DocumentEngineState.Suspended or DocumentEngineState.Resuming;

    /// <summary>The budget's clock at the engine's latest use; the engine with the smallest value is the next to be suspended.</summary>
    public long LastUsed => _supervisor?.LastUsed ?? 0;

    /// <summary>The engine process, or 0 while it is suspended.</summary>
    public uint EngineProcessId => _supervisor?.EngineProcessId ?? 0;

    public int RestartCount => _supervisor?.RestartCount ?? 0;

    internal void Attach(EngineSupervisor supervisor)
    {
        _supervisor = supervisor;
        supervisor.EngineCrashed += (s, e) => EngineCrashed?.Invoke(this, e);
        supervisor.Restored += (s, e) => Restored?.Invoke(this, e);
        supervisor.Failed += (s, e) =>
        {
            SetState(DocumentEngineState.Failed);
            _budget.ReleaseSlot(this); // an engine that gave up holds no process, so no slot
            Failed?.Invoke(this, e);
        };
        supervisor.LifeChanged = SetState;
    }

    /// <summary>
    /// Opens a document in this engine. The file is opened by the broker, read-only and deny-write, and that handle is kept for as long as the document is open, suspended or not.
    /// A suspended engine is resumed first. <c>Timeout</c> when every slot of the budget stayed busy or pinned.
    /// </summary>
    public Task<EngineResult<SupervisedDocument>> OpenDocumentAsync(string path, string? password = null, CancellationToken cancellationToken = default) =>
        Supervisor.OpenDocumentAsync(path, password, cancellationToken);

    /// <summary>
    /// Keeps this engine from being suspended until the lease is disposed: a visible tab, a save or a print job (docs/features/tabs-windows-session.md 3.8). Pinning a suspended
    /// engine does not resume it. An engine can be pinned more than once.
    /// </summary>
    public IDisposable Pin() => Supervisor.Pin();

    /// <summary>
    /// Suspends the engine now, for example when its tab goes to the background. False when it cannot be: it has a call in flight or a pin, or it is already suspended, restarting or
    /// gone. When this returns true its process has exited and its slot is free.
    /// </summary>
    public async Task<bool> SuspendAsync()
    {
        // The slot goes back to the budget after the process has exited and before a resume that was asked for meanwhile may claim one again.
        return Volatile.Read(ref _disposed) == 0 && await Supervisor.TrySuspendAsync(() => _budget.ReleaseSlot(this)).ConfigureAwait(false);
    }

    /// <summary>Resumes a suspended engine now, ahead of the first call that would (a tab about to be shown). Success is <c>Ok</c>; <c>Timeout</c> when no slot became free.</summary>
    public Task<Status> ResumeAsync(CancellationToken cancellationToken = default) => Supervisor.EnsureRunningAsync(cancellationToken);

    /// <summary>Called by the budget when it needs this engine's slot. True when the engine was idle and has been shut down.</summary>
    internal Task<bool> TrySuspendForBudgetAsync() => SuspendAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _budget.Forget(this);
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }

        _budget.ReleaseSlot(this);
    }

    private void SetState(DocumentEngineState state)
    {
        Volatile.Write(ref _state, (int)state);
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A subscriber's failure is the subscriber's problem.
        }
    }
}
