namespace MikePDF.Ink;

/// <summary>A clock the batch policy reads, injected so tests control time. Milliseconds from any fixed origin.</summary>
public interface IInkClock
{
    /// <summary>The current time in milliseconds.</summary>
    long NowMs { get; }
}

/// <summary>What the batch policy decided for a new stroke.</summary>
public enum BatchDecision
{
    /// <summary>The stroke starts a new annotation (the caller allocates the batch id).</summary>
    StartNew,

    /// <summary>The stroke joins the open annotation.</summary>
    Continue,
}

/// <summary>Why a batch is closed by the caller.</summary>
public enum BatchCloseReason
{
    /// <summary>The user moved to another page.</summary>
    PageChange,

    /// <summary>The tool or its style changed.</summary>
    StyleChange,

    /// <summary>The selection changed.</summary>
    SelectionChange,

    /// <summary>The document was saved.</summary>
    Save,

    /// <summary>An undo removed the first stroke of the batch.</summary>
    UndoPastFirst,

    /// <summary>An undo happened; the next stroke starts a new batch (ink-pen.md section 8).</summary>
    AfterUndo,

    /// <summary>The idle timer fired.</summary>
    Idle,
}

/// <summary>What must match for two strokes to share an annotation: page (0-based index), tool, colour (0xRRGGBB), base width and opacity.</summary>
public readonly record struct BatchKey(int Page, InkTool Tool, uint ColourRgb, float BaseWidth, float Opacity);

/// <summary>
/// Decides which strokes share an Ink annotation (ink-pen.md 3.7, INK-05). A stroke continues the open batch when the key is equal,
/// the previous stroke was added less than the idle time ago, and adding it keeps the batch within the stroke and point limits.
/// The caller closes the batch for events it sees (page change, style change, selection change, save, undo) with <see cref="CloseBatch"/>.
/// </summary>
public sealed class BatchPolicy
{
    private readonly IInkClock _clock;
    private readonly int _maxStrokes;
    private readonly int _maxPoints;
    private readonly long _idleMs;
    private BatchKey _key;
    private long _lastStrokeMs;

    /// <summary>Creates a policy. Defaults are the provisional limits of ink-pen.md 3.7 (200 strokes, 20,000 points, 10 s).</summary>
    public BatchPolicy(IInkClock clock, int maxStrokes = StrokeLimits.MaxStrokesPerBatch, int maxPoints = StrokeLimits.MaxPointsPerBatch, long idleMs = InkConstants.BatchIdleMs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStrokes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(idleMs);
        _clock = clock;
        _maxStrokes = maxStrokes;
        _maxPoints = maxPoints;
        _idleMs = idleMs;
    }

    /// <summary>True while a batch is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Strokes in the open batch (0 when closed).</summary>
    public int StrokeCount { get; private set; }

    /// <summary>Points in the open batch (0 when closed).</summary>
    public int PointCount { get; private set; }

    /// <summary>Why the last batch was closed, or null if none was closed by the caller yet.</summary>
    public BatchCloseReason? LastCloseReason { get; private set; }

    /// <summary>
    /// Registers a committed stroke of <paramref name="points"/> points. Returns <see cref="BatchDecision.Continue"/> when it joins the
    /// open batch, <see cref="BatchDecision.StartNew"/> when it opens a new one (the previous batch, if any, is then finished).
    /// A batch never exceeds the stroke or point limits; a single stroke above the point limit starts a batch of its own.
    /// </summary>
    public BatchDecision AddStroke(BatchKey key, int points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(points);
        var now = _clock.NowMs;
        var gap = now - _lastStrokeMs;
        var canContinue = IsOpen
            && key.Equals(_key)
            && gap >= 0
            && gap < _idleMs
            && StrokeCount + 1 <= _maxStrokes
            && (long)PointCount + points <= _maxPoints;
        _lastStrokeMs = now;
        if (canContinue)
        {
            StrokeCount++;
            PointCount += points;
            return BatchDecision.Continue;
        }

        IsOpen = true;
        _key = key;
        StrokeCount = 1;
        PointCount = points;
        return BatchDecision.StartNew;
    }

    /// <summary>Closes the open batch; the next stroke starts a new one. Harmless when no batch is open.</summary>
    public void CloseBatch(BatchCloseReason reason)
    {
        IsOpen = false;
        StrokeCount = 0;
        PointCount = 0;
        LastCloseReason = reason;
    }
}
