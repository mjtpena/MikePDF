using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

/// <summary>Builds result files for the comparison tests.</summary>
internal static class Make
{
    public static PerfResults Results(string runnerId, params MetricResult[] metrics) => new()
    {
        CreatedUtc = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero),
        Runner = new RunnerInfo { Id = runnerId },
        Machine = new MikePDF.Perf.Results.MachineInfo { Cpu = "Test CPU", LogicalCores = 4, RamGb = 8, Gpus = [], Os = "Windows", Architecture = "x64", DotNet = ".NET 10" },
        Profile = new ProfileInfo("ci", 3, 15, 5),
        Metrics = metrics,
    };

    /// <summary>A measured metric whose samples are all <paramref name="median"/>.</summary>
    public static MetricResult Metric(MetricDefinition definition, double median) => MetricResults.Measured(definition, [median, median, median]);

    public static MetricResult Metric(string id, string unit, string direction, double median) =>
        MetricResults.Measured(new MetricDefinition(id, unit, direction, id, null, 0), [median, median, median]);
}
