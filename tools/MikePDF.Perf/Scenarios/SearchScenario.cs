using MikePDF.Broker;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Protocol;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// Ctrl+F on a 500-page text PDF, through the same classes the app uses: <see cref="FindSession"/> over <see cref="DocumentReader"/> over the
/// broker's document in the pool engine. The clock starts when the search starts (the document is open already, as it is when the person presses
/// Ctrl+F) and stops at the first match, then at the last page. The document is reopened for every iteration so that nothing the engine cached for
/// an earlier search helps the next one. Every run is checked against what the generator planted: the first match on page 5 and 11 matches in all.
/// </summary>
internal static class SearchScenario
{
    public static async Task<IReadOnlyList<MetricResult>> RunAsync(PerfContext context, CancellationToken cancellationToken)
    {
        try
        {
            var spec = PerfDocuments.Text500;
            var path = context.Document(spec).Path;
            await using var pool = await context.Engine.StartPoolEngineAsync(cancellationToken);
            var first = new List<double>();
            var full = new List<double>();
            for (var i = 0; i < context.Profile.Warmup + context.Profile.HeavyIterations; i++)
            {
                var (toFirst, toFull) = await context.Resilient(() => OnceAsync(pool, spec, path), MetricCatalog.SearchFull.Id);
                if (i >= context.Profile.Warmup)
                {
                    first.Add(toFirst);
                    full.Add(toFull);
                }
            }

            context.Log($"{MetricCatalog.SearchFirst.Id}: median {Stats.Median(first):F1} ms; full {Stats.Median(full):F1} ms over {full.Count}");
            var perPage = new Dictionary<string, double>(StringComparer.Ordinal) { ["ms_per_page"] = Stats.Median(full) / spec.Pages };
            return
            [
                MetricResults.Measured(MetricCatalog.SearchFirst, first),
                MetricResults.Measured(MetricCatalog.SearchFull, full, perPage),
            ];
        }
        catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
        {
            return
            [
                MetricResults.Failed(MetricCatalog.SearchFirst, e.Message),
                MetricResults.Failed(MetricCatalog.SearchFull, e.Message),
            ];
        }
    }

    private static async Task<(double ToFirst, double ToFull)> OnceAsync(EngineSupervisor pool, DocumentSpec spec, string path)
    {
        var opened = Checks.Require(await pool.OpenDocumentAsync(path), "OpenDocument");
        await using var document = opened;
        var reader = new DocumentReader(new DocumentReadingEngine(document), (int)document.PageCount);
        using var session = new FindSession(reader, (int)document.PageCount, new SearchOptions(PerfDocuments.Needle), startPage: 0);

        var start = Clock.Now();
        var run = session.StartAsync();
        var match = await session.NextAsync();
        var firstAt = Clock.Now();
        await run;
        var fullAt = Clock.Now();

        var progress = session.Progress;
        if (match is null || match.Value.Page != spec.FirstNeedlePage)
        {
            throw new PerfException($"The first match was on page {match?.Page + 1}, expected {spec.FirstNeedlePage + 1}.");
        }

        if (!progress.IsComplete || progress.PagesFailed != 0 || progress.PagesSearched != spec.Pages || progress.MatchCount != spec.ExpectedNeedleMatches)
        {
            throw new PerfException($"The search found {progress.MatchCount} matches on {progress.PagesSearched} pages ({progress.PagesFailed} failed, complete {progress.IsComplete}); expected {spec.ExpectedNeedleMatches} on {spec.Pages}.");
        }

        return (Clock.Milliseconds(start, firstAt), Clock.Milliseconds(start, fullAt));
    }
}

/// <summary>The reading services over a broker document: the same adapter the app uses (MikePDF.App.Services.SupervisedReadingEngine), which is internal to the app.</summary>
internal sealed class DocumentReadingEngine(SupervisedDocument document) : IReadingEngine
{
    public Task<ReadReply<GetOutlineReply>> GetOutlineAsync(IReadOnlyList<uint> path, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(document.GetOutlineAsync(path, first, count, cancellationToken));

    public Task<ReadReply<GetPageLinksReply>> GetPageLinksAsync(uint page, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(document.GetPageLinksAsync(page, first, count, cancellationToken));

    public Task<ReadReply<GetLinkUriReply>> GetLinkUriAsync(uint page, uint index, CancellationToken cancellationToken) =>
        CallAsync(document.GetLinkUriAsync(page, index, cancellationToken));

    public Task<ReadReply<GetPageBoxesReply>> GetPageBoxesAsync(uint page, CancellationToken cancellationToken) =>
        CallAsync(document.GetPageBoxesAsync(page, cancellationToken));

    public Task<ReadReply<GetPageTextReply>> GetPageTextAsync(uint page, uint first, uint count, CancellationToken cancellationToken) =>
        CallAsync(document.GetPageTextAsync(page, first, count, cancellationToken));

    public Task<ReadReply<SearchPageReply>> SearchPageAsync(uint page, string query, uint flags, CancellationToken cancellationToken) =>
        CallAsync(document.SearchPageAsync(page, query, flags, cancellationToken));

    private static async Task<ReadReply<T>> CallAsync<T>(Task<EngineResult<T>> call)
        where T : class
    {
        var result = await call;
        return new ReadReply<T>(result.Status, result.Value, result.Detail);
    }
}
