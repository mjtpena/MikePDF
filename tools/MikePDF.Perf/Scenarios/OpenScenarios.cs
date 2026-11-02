using MikePDF.Broker;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// Opening a document to its first tile. Two sequences are timed:
/// <list type="bullet">
/// <item><b>Session</b> is what the app does today for every document (DocumentSession.OpenAsync): start the document engine, open the file in it,
/// start the pool engine and open the file there, ask for the document info and the first 32 page sizes, then request the first sharp tile from the
/// document engine and the low-res placeholder from the pool engine. Both engines are new processes each time.</item>
/// <item><b>Engine warm</b> opens in an engine that is already running (and already rendered something): the open, the info, the sizes and the
/// first tile. This is what reusing one engine across documents would cost, and the gap between the two is the price of the engine starts.</item>
/// </list>
/// The reported value is the time to the first sharp tile, the stricter of the two ways to read "first rendered pixel"; the earlier of tile and
/// placeholder is in the details. Process start of the app, window creation and drawing are not here: the app's own markers measure those.
/// </summary>
internal static class OpenScenarios
{
    private const int FirstSizeChunk = 32; // DocumentSession.FirstSizeChunk

    /// <summary>The five open metrics, each its own run so that a retry can repeat just the one that regressed.</summary>
    public static IReadOnlyList<MetricDefinition> Metrics { get; } =
        [MetricCatalog.WarmOpen, MetricCatalog.ColdEngineOpen1, MetricCatalog.LargeOpen, MetricCatalog.EngineWarmOpen20, MetricCatalog.EngineWarmOpen2000];

    public static Task<MetricResult> RunAsync(PerfContext context, MetricDefinition definition) => definition.Id switch
    {
        _ when definition == MetricCatalog.WarmOpen => Guard(definition, () => SessionAsync(context, definition, PerfDocuments.Text20)),
        _ when definition == MetricCatalog.ColdEngineOpen1 => Guard(definition, () => SessionAsync(context, definition, PerfDocuments.Text1)),
        _ when definition == MetricCatalog.LargeOpen => Guard(definition, () => SessionAsync(context, definition, PerfDocuments.Text2000)),
        _ when definition == MetricCatalog.EngineWarmOpen20 => Guard(definition, () => EngineWarmAsync(context, definition, PerfDocuments.Text20)),
        _ when definition == MetricCatalog.EngineWarmOpen2000 => Guard(definition, () => EngineWarmAsync(context, definition, PerfDocuments.Text2000)),
        _ => throw new ArgumentException("Not an open metric: " + definition.Id, nameof(definition)),
    };

    private static async Task<MetricResult> Guard(MetricDefinition definition, Func<Task<MetricResult>> measure)
    {
        try
        {
            return await measure();
        }
        catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
        {
            return MetricResults.Failed(definition, e.Message);
        }
    }

    private static async Task<MetricResult> SessionAsync(PerfContext context, MetricDefinition definition, DocumentSpec spec)
    {
        var document = context.Document(spec);
        var stages = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var samples = new List<double>();
        for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
        {
            var timing = await context.Resilient(() => OneSessionAsync(context, document.Path), definition.Id);
            if (i < context.Profile.Warmup)
            {
                continue;
            }

            samples.Add(timing.FirstTile);
            foreach (var (name, value) in timing.Stages)
            {
                if (!stages.TryGetValue(name, out var list))
                {
                    stages[name] = list = [];
                }

                list.Add(value);
            }
        }

        context.Log($"{definition.Id}: median {Stats.Median(samples):F1} ms over {samples.Count}");
        return MetricResults.Measured(definition, samples, stages.ToDictionary(p => p.Key, p => Stats.Median(p.Value), StringComparer.Ordinal));
    }

    private sealed record SessionTiming(double FirstTile, IReadOnlyDictionary<string, double> Stages);

    private static async Task<SessionTiming> OneSessionAsync(PerfContext context, string path)
    {
        var start = Clock.Now();
        await using var main = await context.Engine.StartDocumentEngineAsync();
        var engineStarted = Clock.Now();
        var opened = Checks.Require(await main.OpenDocumentAsync(path), "OpenDocument");
        await using var document = opened;
        var documentOpened = Clock.Now();

        await using var pool = await context.Engine.StartPoolEngineAsync();
        var poolStarted = Clock.Now();
        var poolOpened = Checks.Require(await pool.OpenDocumentAsync(path), "OpenDocument (pool)");
        await using var poolDocument = poolOpened;
        var poolReady = Clock.Now();

        _ = Checks.Require(await document.GetInfoAsync(), "GetDocumentInfo");
        var infoDone = Clock.Now();
        var first = (uint)Math.Min(document.PageCount, FirstSizeChunk);
        var sizes = Checks.Require(await document.GetPageSizesAsync(0, first), "GetPageSizes");
        var sizesDone = Clock.Now();

        var tileRequest = AppTiles.First(0, sizes.Widths[0], sizes.Heights[0]);
        var (placeholderScale, placeholderWidth, placeholderHeight) = AppTiles.Placeholder(sizes.Widths[0], sizes.Heights[0]);
        var placeholderRequest = new RenderTileArgs(0, placeholderScale, 0, 0, 0, placeholderWidth, placeholderHeight);

        async Task<(long At, TileLease Lease)> Render(SupervisedDocument target, RenderTileArgs args, string what)
        {
            var lease = Checks.Require(await target.RenderTileAsync(args, 1), what);
            return (Clock.Now(), lease);
        }

        var tileTask = Render(document, tileRequest, "RenderTile");
        var placeholderTask = Render(poolDocument, placeholderRequest, "RenderTile (placeholder)");
        var tile = await tileTask;
        var placeholder = await placeholderTask;
        var firstPixel = Math.Min(tile.At, placeholder.At);
        using (tile.Lease)
        using (placeholder.Lease)
        {
            // Checked after the clock stopped: a blank page would make every number here meaningless.
            if (!AppTiles.HasInk(tile.Lease) || !AppTiles.HasInk(placeholder.Lease))
            {
                throw new PerfException("A tile of a page of text came back blank.");
            }
        }

        return new SessionTiming(
            Clock.Milliseconds(start, tile.At),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["t_engine_started_ms"] = Clock.Milliseconds(start, engineStarted),
                ["t_document_opened_ms"] = Clock.Milliseconds(start, documentOpened),
                ["t_pool_engine_started_ms"] = Clock.Milliseconds(start, poolStarted),
                ["t_pool_document_opened_ms"] = Clock.Milliseconds(start, poolReady),
                ["t_info_ms"] = Clock.Milliseconds(start, infoDone),
                ["t_first_sizes_ms"] = Clock.Milliseconds(start, sizesDone),
                ["t_first_placeholder_ms"] = Clock.Milliseconds(start, placeholder.At),
                ["t_first_pixel_ms"] = Clock.Milliseconds(start, firstPixel),
            });
    }

    private static async Task<MetricResult> EngineWarmAsync(PerfContext context, MetricDefinition definition, DocumentSpec spec)
    {
        var path = context.Document(spec).Path;
        await using var engine = await context.Engine.StartDocumentEngineAsync();
        var samples = new List<double>();
        for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
        {
            var elapsed = await context.Resilient(async () =>
            {
                var start = Clock.Now();
                var opened = Checks.Require(await engine.OpenDocumentAsync(path), "OpenDocument");
                await using var document = opened;
                _ = Checks.Require(await document.GetInfoAsync(), "GetDocumentInfo");
                var sizes = Checks.Require(await document.GetPageSizesAsync(0, (uint)Math.Min(document.PageCount, FirstSizeChunk)), "GetPageSizes");
                var lease = Checks.Require(await document.RenderTileAsync(AppTiles.First(0, sizes.Widths[0], sizes.Heights[0]), 1), "RenderTile");
                var took = Clock.MillisecondsSince(start);
                using (lease)
                {
                    if (!AppTiles.HasInk(lease))
                    {
                        throw new PerfException("A tile of a page of text came back blank.");
                    }
                }

                return took;
            }, definition.Id);

            if (i >= context.Profile.Warmup)
            {
                samples.Add(elapsed);
            }
        }

        context.Log($"{definition.Id}: median {Stats.Median(samples):F1} ms over {samples.Count}");
        return MetricResults.Measured(definition, samples);
    }
}
