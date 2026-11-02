using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Reading;
using MikePDF.Protocol;

namespace MikePDF.Printing;

/// <summary>One band of pixels from the engine, valid until it is disposed (it is a slot of the shared ring).</summary>
public interface IPrintBand : IDisposable
{
    int Width { get; }

    int Height { get; }

    int Stride { get; }

    /// <summary>PDFium failed part-way through the page: what was drawn is what there is.</summary>
    bool PartiallyRendered { get; }

    /// <summary>Exactly <c>Stride * Height</c> bytes of BGRA.</summary>
    ReadOnlySpan<byte> Pixels { get; }
}

/// <summary>A band, or the reason there is none.</summary>
public readonly record struct BandResult(Status Status, IPrintBand? Band, string? Detail = null);

/// <summary>What a job needs to know about the document.</summary>
public sealed record PrintDocumentInfo(int PageCount, PdfPermissions Permissions);

/// <summary>
/// Where the job gets its pages: the document's pixels in bands and the displayed size of each page. <see cref="EngineBandSource"/> is the engine; tests use a fake. Every call may fail
/// with an engine status, and the job decides what to do with it (retry after a restart, retry once after a timeout, fail).
/// </summary>
public interface IPrintSource
{
    /// <summary>The size of one slot of the ring bands arrive in: no band is larger than this.</summary>
    int SlotBytes { get; }

    /// <summary>
    /// How many times the engine behind this source has been replaced. The job reads it before a call, so that two calls that were in flight when one engine died are one crash
    /// (PRINT-12), however late the second one reports it.
    /// </summary>
    int EngineGeneration { get; }

    Task<(Status Status, PrintDocumentInfo? Info)> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>The displayed sizes in points of <paramref name="count"/> pages from the 0-based page <paramref name="first"/>.</summary>
    Task<(Status Status, IReadOnlyList<SizeD> Sizes)> GetPageSizesAsync(int first, int count, CancellationToken cancellationToken);

    /// <summary>The document's own print wishes (<c>/ViewerPreferences</c>), or null when they cannot be read. Only ever defaults.</summary>
    Task<GetViewerPreferencesReply?> GetViewerPreferencesAsync(CancellationToken cancellationToken);

    Task<BandResult> RenderBandAsync(RenderBandArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken);
}

/// <summary>The broker's <see cref="SupervisedDocument"/> as a print source.</summary>
public sealed class EngineBandSource(SupervisedDocument document, int slotBytes) : IPrintSource
{
    private const int PageSizeWindow = 5000; // the schema's limit for GetPageSizes

    public int SlotBytes { get; } = slotBytes;

    public int EngineGeneration => document.EngineRestartCount;

    public async Task<(Status Status, PrintDocumentInfo? Info)> GetInfoAsync(CancellationToken cancellationToken)
    {
        var info = await document.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.IsOk || info.Value is null)
        {
            return (info.Status, null);
        }

        var details = await document.GetDocumentDetailsAsync(cancellationToken).ConfigureAwait(false);
        // PDFium reports every bit set for an unprotected document. When the details cannot be read, a document with any bit clear is treated as protected (revision 3), so a
        // restricted file is never printed because one query failed.
        var revision = details.IsOk && details.Value is not null ? (int)details.Value.SecurityRevision : info.Value.Permissions == uint.MaxValue ? 0 : 3;
        var permissions = revision == 0 ? PdfPermissions.Unrestricted : new PdfPermissions(info.Value.Permissions, revision);
        return (Status.Ok, new PrintDocumentInfo((int)info.Value.PageCount, permissions));
    }

    public async Task<(Status Status, IReadOnlyList<SizeD> Sizes)> GetPageSizesAsync(int first, int count, CancellationToken cancellationToken)
    {
        var sizes = new List<SizeD>(count);
        while (sizes.Count < count)
        {
            var window = Math.Min(PageSizeWindow, count - sizes.Count);
            var reply = await document.GetPageSizesAsync((uint)(first + sizes.Count), (uint)window, cancellationToken).ConfigureAwait(false);
            if (!reply.IsOk || reply.Value is not { } value || value.Widths.Length != window || value.Heights.Length != window)
            {
                return (reply.IsOk ? Status.ProtocolError : reply.Status, []);
            }

            for (var i = 0; i < window; i++)
            {
                sizes.Add(new SizeD(value.Widths[i], value.Heights[i]));
            }
        }

        return (Status.Ok, sizes);
    }

    public async Task<GetViewerPreferencesReply?> GetViewerPreferencesAsync(CancellationToken cancellationToken)
    {
        var reply = await document.GetViewerPreferencesAsync(cancellationToken).ConfigureAwait(false);
        return reply.IsOk ? reply.Value : null;
    }

    public async Task<BandResult> RenderBandAsync(RenderBandArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var result = await document.RenderBandAsync(args, generation, deadline, cancellationToken).ConfigureAwait(false);
        return result.Status is Status.Ok or Status.PartiallyRendered && result.Value is { } lease
            ? new BandResult(result.Status, new LeaseBand(lease))
            : new BandResult(result.Status, null, result.Detail);
    }

    private sealed class LeaseBand(TileLease lease) : IPrintBand
    {
        public int Width => lease.Width;

        public int Height => lease.Height;

        public int Stride => lease.Stride;

        public bool PartiallyRendered => lease.PartiallyRendered;

        public ReadOnlySpan<byte> Pixels => lease.Pixels;

        public void Dispose() => lease.Dispose();
    }
}
