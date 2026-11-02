using MikePDF.Perf.Measurement;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Metrics;

/// <summary>
/// What the harness measures. <see cref="Floor"/> is the smallest change, in the metric's own unit, that can count as a regression against a stored baseline
/// from another run or machine: 10% of a 1 ms median is 0.1 ms, which no runner can resolve, so a change must beat both the 10% rule and this floor.
/// <see cref="AbFloor"/> is the same for the same-runner A/B gate. There the paired rounds already absorb the speed of the machine, so the floor only has to
/// sit above the largest difference of medians that the A/A runs ever showed (docs/perf/README.md, "Where the numbers come from"); null means <see cref="Floor"/>.
/// </summary>
public sealed record MetricDefinition(string Id, string Unit, string Direction, string Description, string? BudgetId, double Floor, bool Gate = true, double? AbFloor = null)
{
    /// <summary>The floor the A/B gate uses.</summary>
    public double FloorForAb => AbFloor ?? Floor;

    public bool LowerIsBetter => string.Equals(Direction, "lower", StringComparison.Ordinal);
}

public static class MetricCatalog
{
    public const string TextOne = "text1";
    public const string Text20 = "text20";
    public const string Text500 = "text500";
    public const string Text2000 = "text2000";

    public static readonly MetricDefinition WarmOpen = new("open.warm.first_tile.text20", "ms", "lower",
        "Open a 20-page PDF the way the app does today (start the document engine and the pool engine, open, info, first 32 page sizes) to the first sharp 512 px tile.", "open.warm.20p", 5, AbFloor: 2);

    public static readonly MetricDefinition EngineWarmOpen20 = new("open.engine_warm.first_tile.text20", "ms", "lower",
        "Open a 20-page PDF in an engine that is already running, to the first sharp tile. What reusing the engine between documents would give.", null, 3, AbFloor: 0.5);

    public static readonly MetricDefinition ColdEngineOpen1 = new("open.cold_engine.first_tile.text1", "ms", "lower",
        "Engine start, open and first tile of a 1-page PDF. Only the engine part of the cold-open budget; the process start, window and drawing are measured by the app markers.", "open.cold.1p", 5, AbFloor: 2);

    public static readonly MetricDefinition LargeOpen = new("open.large.first_tile.text2000", "ms", "lower",
        "Open a 2,000-page PDF (app sequence) to the first sharp tile of page 1.", "open.large.2000p", 8, AbFloor: 2);

    public static readonly MetricDefinition EngineWarmOpen2000 = new("open.engine_warm.first_tile.text2000", "ms", "lower",
        "Open a 2,000-page PDF in a running engine to the first sharp tile.", null, 5, AbFloor: 0.7);

    public static readonly MetricDefinition SearchFirst = new("search.first_result.text500", "ms", "lower",
        "Start a search of a 500-page text PDF from page 1 to the first match (on page 5), through FindSession and DocumentReader.", "search.500p.first", 8, AbFloor: 0.75);

    public static readonly MetricDefinition SearchFull = new("search.full.text500", "ms", "lower",
        "The same search to the last page searched, every match found.", "search.500p.full", 25, AbFloor: 15);

    public static readonly MetricDefinition EnginePeakWorkingSet = new("memory.engine_peak_working_set.text500", "MB", "lower",
        "Peak working set of the document engine after rendering every 512 px tile of every page of a 500-page PDF at 100%.", "memory.engine.500p", 8, AbFloor: 1);

    public static readonly MetricDefinition EnginePrivateBytes = new("memory.engine_private_bytes.text500", "MB", "lower",
        "Private bytes (commit) of the same engine at the end of the same run.", null, 8, AbFloor: 0.5);

    public static readonly MetricDefinition CrashToTile = new("recovery.crash_to_first_tile", "ms", "lower",
        "Kill the engine process, then time until the document renders a tile again in the replacement engine.", "recovery.engine_crash", 25, AbFloor: 1.5);

    public static readonly MetricDefinition SupervisorRestore = new("recovery.supervisor_restore", "ms", "lower",
        "The supervisor's own figure: from the failure being noticed to the documents being reopened in the new engine.", null, 25, AbFloor: 1);

    public static readonly MetricDefinition TileLatency = new("render.tile_latency.text20", "ms", "lower",
        "One 512 px tile of a 20-page text PDF, one request in flight: IPC round trip plus PDFium.", null, 0.25, AbFloor: 0.04);

    public static readonly MetricDefinition TileThroughput = new("render.tile_throughput.text20", "tiles/s", "higher",
        "512 px tiles per second with 3 requests in flight, as the app's scheduler keeps them.", null, 40, AbFloor: 30);

    public static readonly MetricDefinition InstallSize = new("package.install_size", "MB", "lower",
        "Unpacked size of the MSIX (sum of its files), one architecture.", "install.size", 1);

    public static readonly MetricDefinition MsixFileSize = new("package.msix_file_size", "MB", "lower",
        "Size of the .msix file as downloaded.", null, 1);

    public static IReadOnlyList<MetricDefinition> All { get; } =
    [
        WarmOpen, EngineWarmOpen20, ColdEngineOpen1, LargeOpen, EngineWarmOpen2000, SearchFirst, SearchFull, EnginePeakWorkingSet, EnginePrivateBytes,
        CrashToTile, SupervisorRestore, TileLatency, TileThroughput, InstallSize, MsixFileSize,
    ];

    /// <summary>Finds a headless metric or one of the app's trace-marker metrics (<see cref="Trace.AppMetricCatalog"/>).</summary>
    public static MetricDefinition? Find(string id) =>
        All.Concat(Trace.AppMetricCatalog.All).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
}

public static class MetricResults
{
    public static MetricResult Measured(MetricDefinition definition, IReadOnlyList<double> samples, IReadOnlyDictionary<string, double>? details = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var summary = Stats.Summarize(samples);
        return new MetricResult
        {
            Id = definition.Id,
            Unit = definition.Unit,
            Direction = definition.Direction,
            Status = MetricStatus.Measured,
            N = summary.Count,
            Median = Round(summary.Median),
            P95 = Round(summary.P95),
            Min = Round(summary.Min),
            Max = Round(summary.Max),
            Mean = Round(summary.Mean),
            StdDev = Round(summary.StdDev),
            CoefficientOfVariation = Math.Round(summary.CoefficientOfVariation, 4),
            Samples = [.. samples.Select(Round)],
            Details = details?.ToDictionary(p => p.Key, p => Round(p.Value), StringComparer.Ordinal),
        };
    }

    public static MetricResult Skipped(MetricDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new MetricResult { Id = definition.Id, Unit = definition.Unit, Direction = definition.Direction, Status = MetricStatus.Skipped, Reason = reason };
    }

    public static MetricResult Failed(MetricDefinition definition, string reason)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new MetricResult { Id = definition.Id, Unit = definition.Unit, Direction = definition.Direction, Status = MetricStatus.Failed, Reason = reason };
    }

    public static double Round(double value) => Math.Round(value, 3);
}
