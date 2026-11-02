using MikePDF.Core.Geometry;
using MikePDF.Printing;
using MikePDF.Protocol;
using MikePDF.Broker;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>Wraps a source and writes down every band it is asked for, for tests of what the pipeline asks the real engine.</summary>
internal sealed class RecordingSource(IPrintSource inner) : IPrintSource
{
    private readonly object _gate = new();

    public List<RenderBandArgs> Requests { get; } = [];

    public int SlotBytes => inner.SlotBytes;

    public int EngineGeneration => inner.EngineGeneration;

    public Task<(Status Status, PrintDocumentInfo? Info)> GetInfoAsync(CancellationToken cancellationToken) => inner.GetInfoAsync(cancellationToken);

    public Task<(Status Status, IReadOnlyList<SizeD> Sizes)> GetPageSizesAsync(int first, int count, CancellationToken cancellationToken) => inner.GetPageSizesAsync(first, count, cancellationToken);

    public Task<GetViewerPreferencesReply?> GetViewerPreferencesAsync(CancellationToken cancellationToken) => inner.GetViewerPreferencesAsync(cancellationToken);

    public Task<BandResult> RenderBandAsync(RenderBandArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(args);
        }

        return inner.RenderBandAsync(args, generation, deadline, cancellationToken);
    }
}
