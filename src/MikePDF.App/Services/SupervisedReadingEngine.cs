using MikePDF.Broker;
using MikePDF.Core.Reading;
using MikePDF.Protocol;

namespace MikePDF.App.Services;

/// <summary>
/// The engine's reading services (schema 3) for <see cref="MikePDF.Core.Reading.DocumentReader"/>, over a broker document. The document is
/// opened on first use (outline, text and search are not needed to show the first page), and a restart of the engine is the broker's concern:
/// the same call works against the reopened document.
/// </summary>
internal sealed class SupervisedReadingEngine(Func<Task<SupervisedDocument?>> document) : IReadingEngine
{
    public Task<ReadReply<GetDocumentInfoReply>> GetDocumentInfoAsync(CancellationToken cancellationToken) =>
        CallAsync(d => d.GetInfoAsync(cancellationToken));

    public Task<ReadReply<GetDocumentDetailsReply>> GetDocumentDetailsAsync(CancellationToken cancellationToken) =>
        CallAsync(d => d.GetDocumentDetailsAsync(cancellationToken));

    public Task<ReadReply<GetOutlineReply>> GetOutlineAsync(IReadOnlyList<uint> path, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(d => d.GetOutlineAsync(path, first, count, cancellationToken));

    public Task<ReadReply<GetPageLinksReply>> GetPageLinksAsync(uint page, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(d => d.GetPageLinksAsync(page, first, count, cancellationToken));

    public Task<ReadReply<GetLinkUriReply>> GetLinkUriAsync(uint page, uint index, CancellationToken cancellationToken) =>
        CallAsync(d => d.GetLinkUriAsync(page, index, cancellationToken));

    public Task<ReadReply<GetPageBoxesReply>> GetPageBoxesAsync(uint page, CancellationToken cancellationToken) =>
        CallAsync(d => d.GetPageBoxesAsync(page, cancellationToken));

    public Task<ReadReply<GetPageTextReply>> GetPageTextAsync(uint page, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(d => d.GetPageTextAsync(page, first, count, cancellationToken));

    public Task<ReadReply<SearchPageReply>> SearchPageAsync(uint page, string query, uint flags, CancellationToken cancellationToken) =>
        CallAsync(d => d.SearchPageAsync(page, query, flags, cancellationToken));

    private async Task<ReadReply<T>> CallAsync<T>(Func<SupervisedDocument, Task<EngineResult<T>>> call)
        where T : class
    {
        var opened = await document().ConfigureAwait(false);
        if (opened is null)
        {
            return ReadReply.Fail<T>(Status.EngineCrashed, "No engine is available.");
        }

        var result = await call(opened).ConfigureAwait(false);
        return new ReadReply<T>(result.Status, result.Value, result.Detail);
    }
}
