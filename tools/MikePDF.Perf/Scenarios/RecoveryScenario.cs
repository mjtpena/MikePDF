using MikePDF.Broker;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Protocol;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// Engine crash to restored document (§5.2 rule 3, budget 2 s). Each iteration has its own supervisor (so the crash-loop guard never trips), opens
/// a 20-page document and renders a tile, then kills the engine process from outside and asks for the tile again until it arrives: that wait is the
/// number, from the kill to a tile in the replacement engine. The supervisor's own figure (failure noticed to documents reopened) is reported too.
/// </summary>
internal static class RecoveryScenario
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<MetricResult>> RunAsync(PerfContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Document(PerfDocuments.Text20).Path;
            var toTile = new List<double>();
            var supervisorFigure = new List<double>();
            for (var i = 0; i < context.Profile.Warmup + context.Profile.Iterations; i++)
            {
                var (wall, restore) = await context.Resilient(() => OnceAsync(context, path, cancellationToken), MetricCatalog.CrashToTile.Id);
                if (i >= context.Profile.Warmup)
                {
                    toTile.Add(wall);
                    supervisorFigure.Add(restore);
                }
            }

            context.Log($"{MetricCatalog.CrashToTile.Id}: median {Stats.Median(toTile):F1} ms over {toTile.Count}");
            return
            [
                MetricResults.Measured(MetricCatalog.CrashToTile, toTile),
                MetricResults.Measured(MetricCatalog.SupervisorRestore, supervisorFigure),
            ];
        }
        catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
        {
            return
            [
                MetricResults.Failed(MetricCatalog.CrashToTile, e.Message),
                MetricResults.Failed(MetricCatalog.SupervisorRestore, e.Message),
            ];
        }
    }

    private static async Task<(double ToTile, double Supervisor)> OnceAsync(PerfContext context, string path, CancellationToken cancellationToken)
    {
        await using var engine = await context.Engine.StartDocumentEngineAsync(cancellationToken);
        var restored = new TaskCompletionSource<EngineRestoredEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Restored += (_, e) => restored.TrySetResult(e);

        var opened = Checks.Require(await engine.OpenDocumentAsync(path, cancellationToken: cancellationToken), "OpenDocument");
        await using var document = opened;
        var sizes = Checks.Require(await document.GetPageSizesAsync(0, 1, cancellationToken), "GetPageSizes");
        var request = AppTiles.First(0, sizes.Widths[0], sizes.Heights[0]);
        using (Checks.Require(await document.RenderTileAsync(request, 1, cancellationToken: cancellationToken), "RenderTile before the crash"))
        {
        }

        var firstEngine = engine.EngineProcessId;
        using var process = EngineProcess.Open(engine);

        var start = Clock.Now();
        process.Kill();
        TileLease? tile = null;
        var deadline = Clock.Now() + (long)(Limit.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        while (tile is null)
        {
            // A request that reaches the dying engine ends with EngineCrashed, as the app's scheduler sees it, and is simply asked again.
            var result = await document.RenderTileAsync(request, 2, cancellationToken: cancellationToken);
            if (result.Status == Status.Ok && result.Value is { } lease)
            {
                tile = lease;
            }
            else if (Clock.Now() > deadline)
            {
                throw new PerfException($"No tile {Limit.TotalSeconds:0} s after the engine was killed (last status {result.Status}).");
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }
        }

        var wall = Clock.MillisecondsSince(start);
        using (tile)
        {
            if (!AppTiles.HasInk(tile))
            {
                throw new PerfException("The restored document rendered a blank tile.");
            }
        }

        if (engine.EngineProcessId == firstEngine || engine.RestartCount != 1)
        {
            throw new PerfException($"The engine was not replaced exactly once (restarts: {engine.RestartCount}).");
        }

        var supervisor = (await restored.Task.WaitAsync(Limit, cancellationToken)).Elapsed.TotalMilliseconds;
        return (wall, supervisor);
    }
}
