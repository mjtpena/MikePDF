using MikePDF.Broker;
using MikePDF.Core.Tiles;
using MikePDF.Protocol;

namespace MikePDF.App.Services;

/// <summary>Pixels in a slot of the shared-memory ring. Disposing gives the slot back to the engine.</summary>
internal sealed class LeasePixels(TileLease lease) : IPixelBuffer
{
    public int Width => lease.Width;

    public int Height => lease.Height;

    public int Stride => lease.Stride;

    public ReadOnlySpan<byte> Pixels => lease.Pixels;

    public void Dispose() => lease.Dispose();
}

/// <summary>
/// Renders through a broker document (and so through the sandboxed engine). Failures are reported as results, never thrown: the scheduler
/// retries what is worth retrying. An engine crash ends the call with <see cref="RenderStatus.EngineLost"/>; the supervisor is already starting
/// a new engine and reopening the document, so the next request works.
/// </summary>
internal sealed class EngineTileRenderer(SupervisedDocument document, TimeSpan deadline) : ITileRenderer
{
    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        // The form layer: PDFium draws a field's widget only through its form-fill environment, so without bit 1 a filled form shows empty fields. Tiles and printed bands draw them the same way.
        var args = new RenderTileArgs((uint)request.Key.Page, request.Scale, (ushort)request.Turns, request.X, request.Y, (uint)request.Width, (uint)request.Height,
            RenderTileArgs.Annotations | RenderTileArgs.FormFields);
        EngineResult<TileLease> result;
        try
        {
            result = await document.RenderTileAsync(args, request.Generation, deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RenderResult(RenderStatus.Cancelled);
        }

        return result.Status switch
        {
            Status.Ok when result.Value is { } lease => new RenderResult(RenderStatus.Ok, new LeasePixels(lease)),
            Status.PartiallyRendered when result.Value is { } lease => new RenderResult(RenderStatus.Partial, new LeasePixels(lease)),
            Status.Cancelled => new RenderResult(RenderStatus.Cancelled),
            Status.Timeout => new RenderResult(RenderStatus.Timeout, detail: result.Detail),
            Status.EngineCrashed => new RenderResult(RenderStatus.EngineLost, detail: result.Detail),
            _ => new RenderResult(RenderStatus.Failed, detail: result.Detail),
        };
    }
}
