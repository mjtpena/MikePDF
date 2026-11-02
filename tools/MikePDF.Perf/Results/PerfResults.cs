using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikePDF.Perf.Results;

public enum MetricStatus
{
    /// <summary>Measured: the statistics are present.</summary>
    Measured,

    /// <summary>Not run, with the reason in <see cref="MetricResult.Reason"/> (for example: no built package). Never counted as a pass or a fail.</summary>
    Skipped,

    /// <summary>Tried and could not be measured (the engine would not start, a correctness check failed). A failure of the run.</summary>
    Failed,
}

/// <summary>One metric's outcome. The statistics are over <see cref="Samples"/>; <see cref="Details"/> holds named medians of the parts a sample is made of (stage timings, memory counters).</summary>
public sealed record MetricResult
{
    public required string Id { get; init; }

    public required string Unit { get; init; }

    /// <summary>"lower" when a smaller number is better (times, sizes), "higher" otherwise (throughput).</summary>
    public required string Direction { get; init; }

    public required MetricStatus Status { get; init; }

    /// <summary>Why a metric was skipped or failed.</summary>
    public string? Reason { get; init; }

    /// <summary>"headless" (this harness, through the broker) or "app-etw" (the app's trace markers).</summary>
    public string Source { get; init; } = "headless";

    public int N { get; init; }

    public double? Median { get; init; }

    public double? P95 { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Mean { get; init; }

    public double? StdDev { get; init; }

    /// <summary>Standard deviation over the mean: how noisy this measurement was on this machine.</summary>
    public double? CoefficientOfVariation { get; init; }

    public IReadOnlyList<double> Samples { get; init; } = [];

    public IReadOnlyDictionary<string, double>? Details { get; init; }

    /// <summary>The §11 target this metric is held against, when it has one.</summary>
    public BudgetCheck? Budget { get; init; }

    public bool LowerIsBetter => string.Equals(Direction, "lower", StringComparison.Ordinal);
}

/// <summary>A measured value held against a §11 target. <see cref="ReferenceHardware"/> is false on any machine that is not R1/R2/R3: then "within" means "within on this machine", which proves nothing about the budget.</summary>
public sealed record BudgetCheck(string BudgetId, string Tier, double Target, bool Within, bool ReferenceHardware, string Coverage = "full")
{
    /// <summary>True when the metric is only part of what the budget covers: then a value under the target is not a pass.</summary>
    public bool IsLowerBound => string.Equals(Coverage, "lower-bound", StringComparison.Ordinal);
}

public sealed record MachineInfo
{
    public required string Cpu { get; init; }

    public required int LogicalCores { get; init; }

    public required double RamGb { get; init; }

    public required IReadOnlyList<string> Gpus { get; init; }

    public required string Os { get; init; }

    public required string Architecture { get; init; }

    public required string DotNet { get; init; }

    public string? PowerPlan { get; init; }

    /// <summary>True on a CI-hosted runner, which is shared and is never reference hardware.</summary>
    public bool CiHosted { get; init; }

    public string? CiImage { get; init; }
}

public sealed record RunnerInfo
{
    /// <summary>The baseline this run is compared against, such as "ci-x64", "ci-arm64" or "dev-core-ultra9".</summary>
    public required string Id { get; init; }

    /// <summary>"R1", "R2" or "R3" only when the run was on that reference machine (set explicitly by whoever ran it); null otherwise.</summary>
    public string? ReferenceTier { get; init; }
}

public sealed record EngineInfo(string ExeSha256, string PdfiumSha256, string Directory);

public sealed record DocumentInfo(string Id, int Pages, long Bytes, string Sha256);

public sealed record ProfileInfo(string Name, int Warmup, int Iterations, int HeavyIterations);

public sealed record PerfResults
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    public string Tool { get; init; } = "MikePDF.Perf";

    public required DateTimeOffset CreatedUtc { get; init; }

    public required RunnerInfo Runner { get; init; }

    public required MachineInfo Machine { get; init; }

    public EngineInfo? Engine { get; init; }

    public required ProfileInfo Profile { get; init; }

    public IReadOnlyList<DocumentInfo> Documents { get; init; } = [];

    public required IReadOnlyList<MetricResult> Metrics { get; init; }

    /// <summary>Plain statements the reader of this file needs (what was and was not measured).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public MetricResult? Find(string id) => Metrics.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
}

public static class ResultsJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON was empty.");

    public static PerfResults Load(string path) => Deserialize<PerfResults>(File.ReadAllText(path));

    public static void Save(string path, PerfResults results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(results) + Environment.NewLine);
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
