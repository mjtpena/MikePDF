using MikePDF.Broker;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// Tile render speed on the 20-page document: all 120 tiles (6 per page at 100%), one pass per iteration.
/// Latency: one request in flight, so a sample is the full round trip of one tile (pipe, PDFium, shared-memory slot) and the iteration's
/// value is the median of its 120 tiles. Throughput: three requests in flight, tiles per second over the pass.
/// </summary>
internal static class RenderScenario
{
    private const int InFlight = 3;

    public static async Task<IReadOnlyList<MetricResult>> RunAsync(PerfContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Document(PerfDocuments.Text20).Path;
            await using var engine = await context.Engine.StartDocumentEngineAsync(cancellationToken);
            var opened = Checks.Require(await engine.OpenDocumentAsync(path, cancellationToken: cancellationToken), "OpenDocument");
            await using var document = opened;
            var sizes = await AppTiles.ReadAllSizesAsync(document);
            var tiles = sizes.Select((s, page) => AppTiles.All((uint)page, s.Width, s.Height)).SelectMany(t => t).ToList();

            var medians = new List<double>();
            var pooled = new List<double>();
            var throughput = new List<double>();
            for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
            {
                var latencies = new List<double>(tiles.Count);
                foreach (var args in tiles)
                {
                    var start = Clock.Now();
                    using var lease = Checks.Require(await document.RenderTileAsync(args, 1, cancellationToken: cancellationToken), "RenderTile");
                    latencies.Add(Clock.MillisecondsSince(start));
                }

                var passStart = Clock.Now();
                await Parallel.ForEachAsync(tiles, new ParallelOptions { MaxDegreeOfParallelism = InFlight, CancellationToken = cancellationToken }, async (args, token) =>
                {
                    using var lease = Checks.Require(await document.RenderTileAsync(args, 1, cancellationToken: token), "RenderTile");
                });
                var seconds = Clock.MillisecondsSince(passStart) / 1000.0;

                if (i >= context.Profile.Warmup)
                {
                    medians.Add(Stats.Median(latencies));
                    pooled.AddRange(latencies);
                    throughput.Add(tiles.Count / seconds);
                }
            }

            var summary = Stats.Summarize(pooled);
            context.Log($"{MetricCatalog.TileLatency.Id}: median {Stats.Median(medians):F2} ms, p95 {summary.P95:F2} ms; {MetricCatalog.TileThroughput.Id}: median {Stats.Median(throughput):F0}/s");
            return
            [
                MetricResults.Measured(MetricCatalog.TileLatency, medians, new Dictionary<string, double>(StringComparer.Ordinal) { ["tile_p95_ms"] = summary.P95, ["tile_max_ms"] = summary.Max, ["tiles_per_pass"] = tiles.Count }),
                MetricResults.Measured(MetricCatalog.TileThroughput, throughput),
            ];
        }
        catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
        {
            return
            [
                MetricResults.Failed(MetricCatalog.TileLatency, e.Message),
                MetricResults.Failed(MetricCatalog.TileThroughput, e.Message),
            ];
        }
    }
}
