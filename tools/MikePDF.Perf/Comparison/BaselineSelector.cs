using System.Text.RegularExpressions;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

/// <summary>
/// Picks the baseline for a run from a directory. Hosted runners are drawn from a pool of different machines (the first two CI runs got an AMD EPYC 7763
/// and then an EPYC 9V45, and the same code ran twice as fast on the second), so a baseline is only comparable with a run on the same CPU. Baselines
/// are kept as <c>&lt;runner&gt;.&lt;cpu&gt;.json</c> (or plain <c>&lt;runner&gt;.json</c>); the one whose CPU is the current CPU is chosen.
/// </summary>
public static partial class BaselineSelector
{
    public sealed record Choice(PerfResults? Baseline, string? Path, string Message);

    public static Choice Choose(string directory, string runnerId, string currentCpu)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(runnerId);
        ArgumentNullException.ThrowIfNull(currentCpu);
        var candidates = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json")
                .Where(f => System.IO.Path.GetFileName(f).StartsWith(runnerId + ".", StringComparison.Ordinal))
                .Select(f => (Path: f, Results: ResultsJson.Load(f)))
                .Where(c => string.Equals(c.Results.Runner.Id, runnerId, StringComparison.Ordinal))
                .OrderByDescending(c => c.Results.CreatedUtc)
                .ToList()
            : [];

        var same = candidates.FirstOrDefault(c => string.Equals(c.Results.Machine.Cpu, currentCpu, StringComparison.Ordinal));
        if (same.Results is not null)
        {
            return new Choice(same.Results, same.Path, $"Baseline {System.IO.Path.GetFileName(same.Path)} (same CPU, {currentCpu}).");
        }

        var suggestion = SuggestedFileName(runnerId, currentCpu);
        if (candidates.Count > 0)
        {
            var other = candidates[0];
            return new Choice(other.Results, other.Path,
                $"No baseline for {currentCpu}; comparing with {System.IO.Path.GetFileName(other.Path)} ({other.Results.Machine.Cpu}) for information only. Commit this run's perf-results.json as {suggestion} to gate on this CPU.");
        }

        return new Choice(null, null, $"No baseline for runner '{runnerId}' in {directory}. Commit this run's perf-results.json as {suggestion} to start gating.");
    }

    /// <summary>The file name a baseline for this runner and CPU should be committed under, such as <c>ci-x64.amd-epyc-9v45-96-core-processor.json</c>.</summary>
    public static string SuggestedFileName(string runnerId, string cpu)
    {
        var slug = NotAlphanumeric().Replace(cpu.ToLowerInvariant(), "-").Trim('-');
        return $"{runnerId}.{(slug.Length == 0 ? "unknown-cpu" : slug)}.json";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAlphanumeric();
}
