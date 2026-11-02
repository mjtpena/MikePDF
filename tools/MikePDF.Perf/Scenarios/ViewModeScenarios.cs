using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Tiles;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// The two measurements of the page view modes that can be made without a window (PV-12, PV-17). Not registered in <c>PerfRunner</c>: they are run by the tests of this harness and by
/// anyone who calls <see cref="RunAsync"/>, and add nothing to the gate.
/// </summary>
internal static class ViewModeScenarios
{
    private const int FirstSizeChunk = 32; // DocumentSession.FirstSizeChunk
    private const int SizeChunk = 128;     // DocumentSession.SizeChunk
    private const int InFlight = 3;
    private const int Width = 1920;
    private const int Height = 1080;

    public static async Task<IReadOnlyList<MetricResult>> RunAsync(PerfContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<MetricResult>();
        foreach (var (definition, measure) in new (MetricDefinition, Func<Task<MetricResult>>)[]
        {
            (ViewModeMetrics.LargeFacingOpen, () => OpenAsync(context)),
            (ViewModeMetrics.PageTurnTiles, () => TurnAsync(context, cancellationToken)),
        })
        {
            try
            {
                results.Add(await measure());
            }
            catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
            {
                results.Add(MetricResults.Failed(definition, e.Message));
            }
        }

        return results;
    }

    /// <summary>The 10,000-page document, written next to the harness's own (a file with the right content is left alone).</summary>
    private static string TenThousand(PerfContext context)
    {
        var directory = Path.GetDirectoryName(context.Document(PerfDocuments.Text20).Path)!;
        return PerfDocuments.Materialize(directory, [ViewModeMetrics.Text10000])[0].Path;
    }

    // ---- PV-12 ---------------------------------------------------------------------------------------------------------------------------

    private static async Task<MetricResult> OpenAsync(PerfContext context)
    {
        var path = TenThousand(context);
        var samples = new List<double>();
        var stages = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
        {
            var (total, parts) = await context.Resilient(() => OneOpenAsync(context, path), ViewModeMetrics.LargeFacingOpen.Id);
            if (i < context.Profile.Warmup)
            {
                continue;
            }

            samples.Add(total);
            foreach (var (name, value) in parts)
            {
                if (!stages.TryGetValue(name, out var list))
                {
                    stages[name] = list = [];
                }

                list.Add(value);
            }
        }

        context.Log($"{ViewModeMetrics.LargeFacingOpen.Id}: median {Stats.Median(samples):F1} ms over {samples.Count}");
        return MetricResults.Measured(ViewModeMetrics.LargeFacingOpen, samples, stages.ToDictionary(p => p.Key, p => Stats.Median(p.Value), StringComparer.Ordinal));
    }

    private static async Task<(double Total, IReadOnlyDictionary<string, double> Stages)> OneOpenAsync(PerfContext context, string path)
    {
        var start = Clock.Now();
        await using var main = await context.Engine.StartDocumentEngineAsync();
        var opened = Checks.Require(await main.OpenDocumentAsync(path), "OpenDocument");
        await using var document = opened;
        await using var pool = await context.Engine.StartPoolEngineAsync();
        var poolOpened = Checks.Require(await pool.OpenDocumentAsync(path), "OpenDocument (pool)");
        await using var poolDocument = poolOpened;
        _ = Checks.Require(await document.GetInfoAsync(), "GetDocumentInfo");
        var count = (int)document.PageCount;
        if (count != ViewModeMetrics.Text10000.Pages)
        {
            throw new PerfException($"The document has {count} pages, not {ViewModeMetrics.Text10000.Pages}.");
        }

        var sizes = Checks.Require(await document.GetPageSizesAsync(0, FirstSizeChunk), "GetPageSizes");
        var sizesDone = Clock.Now();

        // The layout the window builds from the first sizes: every page assumed to be the first one's size, then the sizes that are known.
        var controller = new ViewportController();
        controller.SetViewport(Width, Height, 1.0);
        controller.SetMode(new ViewMode(PageArrangement.FacingWithCover, PageFlow.Continuous));
        var known = new SizeD[count];
        Array.Fill(known, new SizeD(sizes.Widths[0], sizes.Heights[0]));
        for (var p = 0; p < FirstSizeChunk; p++)
        {
            known[p] = new SizeD(sizes.Widths[p], sizes.Heights[p]);
        }

        controller.SetPageSizes(known);
        var layoutDone = Clock.Now();

        var tile = Checks.Require(await document.RenderTileAsync(AppTiles.First(0, sizes.Widths[0], sizes.Heights[0]), 1), "RenderTile");
        var tileAt = Clock.Now();
        using (tile)
        {
            if (!AppTiles.HasInk(tile))
            {
                throw new PerfException("A tile of a page of text came back blank.");
            }
        }

        // What the later size chunks cost the UI thread: the app lays the whole document out again for each of the 78 it reads.
        var chunkCosts = new List<double>();
        var rebuilt = known.ToArray();
        for (var first = FirstSizeChunk; first < count; first += SizeChunk)
        {
            for (var p = first; p < Math.Min(first + SizeChunk, count); p++)
            {
                rebuilt[p] = new SizeD(sizes.Widths[0], sizes.Heights[0] + 0.5); // a size that differs from the assumed one, as a document of mixed pages has
            }

            var at = Clock.Now();
            controller.SetPageSizes(rebuilt.ToArray());
            chunkCosts.Add(Clock.MillisecondsSince(at));
        }

        return (
            Clock.Milliseconds(start, tileAt),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["t_first_sizes_ms"] = Clock.Milliseconds(start, sizesDone),
                ["t_layout_ms"] = Clock.Milliseconds(sizesDone, layoutDone),
                ["t_first_tile_ms"] = Clock.Milliseconds(layoutDone, tileAt),
                ["t_relayout_per_chunk_ms"] = chunkCosts.Count == 0 ? 0 : Stats.Median(chunkCosts),
                ["t_relayout_all_chunks_ms"] = chunkCosts.Sum(),
                ["pages"] = count,
            });
    }

    // ---- PV-17 ---------------------------------------------------------------------------------------------------------------------------

    private static async Task<MetricResult> TurnAsync(PerfContext context, CancellationToken cancellationToken)
    {
        var path = context.Document(PerfDocuments.Text20).Path;
        await using var engine = await context.Engine.StartDocumentEngineAsync(cancellationToken);
        var opened = Checks.Require(await engine.OpenDocumentAsync(path, cancellationToken: cancellationToken), "OpenDocument");
        await using var document = opened;
        var sizes = await AppTiles.ReadAllSizesAsync(document);

        // The tiles a presentation asks for on a 1920 x 1080 screen: fit page, no margin, as the controller lays it out.
        var controller = new ViewportController();
        controller.SetViewport(Width, Height, 1.0);
        controller.SetPageSizes([.. sizes.Select(s => new SizeD(s.Width, s.Height))]);
        controller.EnterPresentation();
        var layout = controller.Layout;
        var scale = (float)layout.RenderScale;
        IReadOnlyList<RenderTileArgs> TilesOf(int page)
        {
            var (w, h) = layout.RenderSize(page);
            var list = new List<RenderTileArgs>();
            for (var ty = 0; ty < TileGrid.Rows(h); ty++)
            {
                for (var tx = 0; tx < TileGrid.Columns(w); tx++)
                {
                    var rect = TileGrid.Rect(tx, ty, w, h);
                    list.Add(new RenderTileArgs((uint)page, scale, 0, rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height));
                }
            }

            return list;
        }

        var samples = new List<double>();
        var tiles = 0;
        for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
        {
            var page = (i * 7) % sizes.Length; // a different page each time, so nothing is the engine's second look at one
            var wanted = TilesOf(page);
            tiles = wanted.Count;
            var elapsed = await context.Resilient(async () =>
            {
                var start = Clock.Now();
                using var gate = new SemaphoreSlim(InFlight);
                var tasks = wanted.Select(async args =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        using var lease = Checks.Require(await document.RenderTileAsync(args, 1), "RenderTile"); // given back at once, as the scheduler does after the upload
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);
                return Clock.MillisecondsSince(start);
            }, ViewModeMetrics.PageTurnTiles.Id);

            if (i >= context.Profile.Warmup)
            {
                samples.Add(elapsed);
            }
        }

        context.Log($"{ViewModeMetrics.PageTurnTiles.Id}: median {Stats.Median(samples):F1} ms over {samples.Count} pages of {tiles} tiles");
        return MetricResults.Measured(ViewModeMetrics.PageTurnTiles, samples, new Dictionary<string, double>(StringComparer.Ordinal) { ["tiles_per_page"] = tiles, ["scale"] = scale });
    }
}
