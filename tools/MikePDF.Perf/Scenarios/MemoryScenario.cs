using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// The engine's memory after a full scroll of a 500-page PDF: every 512 px tile of every page is rendered at 100% (3,000 tiles) with three requests
/// in flight, which is the most the app's scheduler would ever have asked for, and the engine process is read while it does and when it stops.
/// A fresh engine for every iteration. The primary figure is the peak working set (the largest the engine's resident memory got);
/// private bytes at the end are reported alongside. 1 MB here is 1,048,576 bytes.
/// </summary>
internal static class MemoryScenario
{
    private const double Megabyte = 1024.0 * 1024.0;
    private const int InFlight = 3;

    public static async Task<IReadOnlyList<MetricResult>> RunAsync(PerfContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Document(PerfDocuments.Text500).Path;
            var peak = new List<double>();
            var privateBytes = new List<double>();
            var details = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            for (var i = 0; i < context.Profile.Warmup + context.Profile.HeavyIterations; i++)
            {
                var sample = await OnceAsync(context, path, cancellationToken);
                if (i < context.Profile.Warmup)
                {
                    continue; // the first full scroll in a process pays for one-off allocations the second does not
                }

                peak.Add(sample.PeakWorkingSetMb);
                privateBytes.Add(sample.PrivateMb);
                Add(details, "working_set_end_mb", sample.WorkingSetEndMb);
                Add(details, "sampled_max_private_mb", sample.SampledMaxPrivateMb);
                Add(details, "scroll_seconds", sample.Seconds);
                Add(details, "tiles", sample.Tiles);
            }

            context.Log($"{MetricCatalog.EnginePeakWorkingSet.Id}: median {Stats.Median(peak):F1} MB peak working set over {peak.Count}");
            var medians = details.ToDictionary(p => p.Key, p => Stats.Median(p.Value), StringComparer.Ordinal);
            return
            [
                MetricResults.Measured(MetricCatalog.EnginePeakWorkingSet, peak, medians),
                MetricResults.Measured(MetricCatalog.EnginePrivateBytes, privateBytes),
            ];
        }
        catch (Exception e) when (e is PerfException or EngineStartException or OperationCanceledException or TimeoutException)
        {
            return
            [
                MetricResults.Failed(MetricCatalog.EnginePeakWorkingSet, e.Message),
                MetricResults.Failed(MetricCatalog.EnginePrivateBytes, e.Message),
            ];
        }
    }

    private sealed record Sample(double PeakWorkingSetMb, double WorkingSetEndMb, double PrivateMb, double SampledMaxPrivateMb, double Seconds, double Tiles);

    private static void Add(Dictionary<string, List<double>> into, string key, double value)
    {
        if (!into.TryGetValue(key, out var list))
        {
            into[key] = list = [];
        }

        list.Add(value);
    }

    private static async Task<Sample> OnceAsync(PerfContext context, string path, CancellationToken cancellationToken)
    {
        await using var engine = await context.Engine.StartDocumentEngineAsync(cancellationToken);
        var opened = Checks.Require(await engine.OpenDocumentAsync(path, cancellationToken: cancellationToken), "OpenDocument");
        await using var document = opened;
        var sizes = await AppTiles.ReadAllSizesAsync(document);
        var tiles = sizes.Select((s, page) => AppTiles.All((uint)page, s.Width, s.Height)).SelectMany(t => t).ToList();

        using var process = EngineProcess.Open(engine);
        var sampledMaxPrivate = 0.0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                sampledMaxPrivate = Math.Max(sampledMaxPrivate, process.Read().PrivateBytes / Megabyte);
                await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None);

        var start = Clock.Now();
        await Parallel.ForEachAsync(tiles, new ParallelOptions { MaxDegreeOfParallelism = InFlight, CancellationToken = cancellationToken }, async (args, token) =>
        {
            using var lease = Checks.Require(await document.RenderTileAsync(args, 1, cancellationToken: token), "RenderTile");
        });
        var seconds = Clock.MillisecondsSince(start) / 1000.0;
        await stop.CancelAsync();
        await sampler;

        var end = process.Read();
        return new Sample(end.PeakWorkingSet / Megabyte, end.WorkingSet / Megabyte, end.PrivateBytes / Megabyte, sampledMaxPrivate, seconds, tiles.Count);
    }
}

/// <summary>Reads an engine process's memory counters from outside, by process id: the broker never exposes them.</summary>
internal sealed class EngineProcess : IDisposable
{
    private readonly Process _process;

    private EngineProcess(Process process) => _process = process;

    public static EngineProcess Open(EngineSupervisor supervisor)
    {
        var id = supervisor.EngineProcessId;
        if (id == 0)
        {
            throw new PerfException("The engine has no process.");
        }

        try
        {
            return new EngineProcess(Process.GetProcessById((int)id));
        }
        catch (ArgumentException e)
        {
            throw new PerfException("The engine process is gone.", e);
        }
    }

    public int Id => _process.Id;

    public (long WorkingSet, long PeakWorkingSet, long PrivateBytes) Read()
    {
        _process.Refresh();
        return (_process.WorkingSet64, _process.PeakWorkingSet64, _process.PrivateMemorySize64);
    }

    public void Kill()
    {
        try
        {
            _process.Kill();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PerfException("The engine process could not be killed from outside: " + e.Message, e);
        }
    }

    public void Dispose() => _process.Dispose();
}
