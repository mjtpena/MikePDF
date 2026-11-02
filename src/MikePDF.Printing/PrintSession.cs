using MikePDF.Broker;
using MikePDF.Core.Printing;
using MikePDF.Protocol;

namespace MikePDF.Printing;

/// <summary>
/// A print job's own engine: a document engine from the <see cref="EngineBudget"/> (so a print never starts a seventh process and never evicts the document being read), with a ring of
/// slots sized for bands, the document opened in it, and the engine <b>pinned</b> so the budget does not suspend it under the job (tabs-windows-session 3.8). It reads the same file the
/// tab has open, read-only, as every engine does. Dispose it when the job is over (or abandoned): the engine goes back to the budget.
/// </summary>
public sealed class PrintSession : IAsyncDisposable
{
    /// <summary>Bands of up to 16 MiB, three slots: one being drawn, one being rendered, one spare.</summary>
    public const int BandSlotBytes = RenderLimits.DefaultBandBytes;

    public const int BandSlotCount = 3;

    /// <summary>"Print as image": a whole page in one slot, up to the ring's 64 MiB limit; two slots.</summary>
    public const int ImageSlotBytes = SlotRing.MaxSlotSize;

    public const int ImageSlotCount = 2;

    private readonly DocumentEngine _engine;
    private readonly SupervisedDocument _document;
    private readonly IDisposable _pin;
    private int _disposed;

    private PrintSession(DocumentEngine engine, SupervisedDocument document, IDisposable pin, int slotBytes)
    {
        _engine = engine;
        _document = document;
        _pin = pin;
        Source = new EngineBandSource(document, slotBytes);
    }

    public IPrintSource Source { get; }

    public int PageCount => (int)_document.PageCount;

    /// <summary>The engine the job runs in, for tests that kill it and for the supervisor's events.</summary>
    public DocumentEngine Engine => _engine;

    /// <summary>
    /// Opens a print session. <paramref name="printingAllowedByPolicy"/> is read from the policy snapshot by the caller; when it is false nothing is started and the result is
    /// <c>PermissionDenied</c> (the pipeline checks again before it prints). <c>Timeout</c> when every engine of the budget stayed busy or pinned; a start failure is
    /// <c>EngineCrashed</c>.
    /// </summary>
    public static async Task<EngineResult<PrintSession>> OpenAsync(EngineBudget budget, string path, string? password, PrintOptions options, bool printingAllowedByPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(options);
        if (!printingAllowedByPolicy)
        {
            return new EngineResult<PrintSession>(Status.PermissionDenied, null, PrintStrings.DisabledPolicy);
        }

        var (count, size) = options.AsImage ? (ImageSlotCount, ImageSlotBytes) : (BandSlotCount, BandSlotBytes);
        DocumentEngine engine;
        try
        {
            engine = await budget.CreateDocumentEngineAsync(o => o with { SlotCount = count, SlotSize = size }, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            return new EngineResult<PrintSession>(Status.Timeout, null, e.Message);
        }
        catch (EngineStartException e)
        {
            return new EngineResult<PrintSession>(Status.EngineCrashed, null, e.Message);
        }

        var pin = engine.Pin();
        try
        {
            var opened = await engine.OpenDocumentAsync(path, password, cancellationToken).ConfigureAwait(false);
            if (!opened.IsOk || opened.Value is null)
            {
                pin.Dispose();
                await engine.DisposeAsync().ConfigureAwait(false);
                return new EngineResult<PrintSession>(opened.Status, null, opened.Detail);
            }

            return new EngineResult<PrintSession>(Status.Ok, new PrintSession(engine, opened.Value, pin, size));
        }
        catch
        {
            pin.Dispose();
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _document.DisposeAsync().ConfigureAwait(false);
        _pin.Dispose();
        await _engine.DisposeAsync().ConfigureAwait(false);
    }
}
