using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

public enum AbSide
{
    Base,
    Head,
}

/// <param name="Report">The decision, from every round.</param>
/// <param name="Base">The base's results over all rounds: each metric is the median of its per-round medians.</param>
/// <param name="Head">The same for the head.</param>
/// <param name="Rounds">Every pair of runs, in the order they were measured.</param>
/// <param name="Confirmed">The metrics that looked regressed after the planned rounds and were measured again.</param>
public sealed record AbOutcome(AbReport Report, PerfResults Base, PerfResults Head, IReadOnlyList<AbRound> Rounds, IReadOnlyList<string> Confirmed);

/// <summary>
/// The same-runner A/B run. Both sides are measured on this machine in the same job, one after the other, round after round, and the order alternates
/// (base first, head first, base first ...): whatever the machine does slowly or quickly in any minute lands on both sides, and a trend or a "second run
/// is always warmer" effect cancels across rounds. After the planned rounds a metric that looks regressed is measured again, on its own, for a few more
/// rounds, and the decision is taken over all of them (a fluke is diluted, a real slowdown stays).
/// </summary>
public static class AbRunner
{
    public const int DefaultRounds = 8;
    public const int DefaultConfirmRounds = 4;

    /// <param name="measure">Runs the suite, or just the given metrics, on one side and returns its results.</param>
    /// <param name="settings">The decision rule.</param>
    /// <param name="rounds">How many rounds are planned.</param>
    /// <param name="confirmRounds">How many more rounds a metric that looks regressed gets.</param>
    /// <param name="log">Progress messages.</param>
    /// <param name="definitions">Finds a metric's definition (its floor and whether it gates); the catalogue by default.</param>
    public static async Task<AbOutcome> RunAsync(
        Func<AbSide, IReadOnlySet<string>?, Task<PerfResults>> measure,
        AbSettings? settings = null,
        int rounds = DefaultRounds,
        int confirmRounds = DefaultConfirmRounds,
        Action<string>? log = null,
        Func<string, MikePDF.Perf.Metrics.MetricDefinition?>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(confirmRounds);
        settings ??= new AbSettings();
        log ??= _ => { };

        var pairs = new List<AbRound>();
        for (var i = 0; i < rounds; i++)
        {
            pairs.Add(await RoundAsync(measure, i, null, log));
        }

        var report = AbComparer.Compare(pairs, settings, definitions);
        // The suspects: over the threshold, and either failing or only short of the evidence. More rounds settle both: they narrow the interval of a real slowdown and dilute a fluke.
        var suspects = report.Items
            .Where(i => i.Verdict == Verdict.Regressed || (i.Verdict == Verdict.Unconfirmed && i.Pairs >= settings.MinimumPairs && i.Change > settings.Threshold))
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (suspects.Count > 0 && confirmRounds > 0)
        {
            log($"Looks regressed, or nearly, after {rounds} rounds: {string.Join(", ", suspects.Order(StringComparer.Ordinal))}. Measuring them in {confirmRounds} more round(s).");
            for (var i = 0; i < confirmRounds; i++)
            {
                pairs.Add(await RoundAsync(measure, rounds + i, suspects, log));
            }

            report = AbComparer.Compare(pairs, settings, definitions);
        }

        return new AbOutcome(report, Merge(pairs.Select(p => p.Base).ToList()), Merge(pairs.Select(p => p.Head).ToList()), pairs, [.. suspects.Order(StringComparer.Ordinal)]);
    }

    private static async Task<AbRound> RoundAsync(Func<AbSide, IReadOnlySet<string>?, Task<PerfResults>> measure, int index, HashSet<string>? only, Action<string> log)
    {
        var baseFirst = index % 2 == 0;
        log($"Round {index + 1}: {(baseFirst ? "base then head" : "head then base")}{(only is null ? string.Empty : $" ({only.Count} metric(s))")}");
        PerfResults first;
        PerfResults second;
        if (baseFirst)
        {
            first = await measure(AbSide.Base, only);
            second = await measure(AbSide.Head, only);
            return new AbRound(first, second);
        }

        first = await measure(AbSide.Head, only);
        second = await measure(AbSide.Base, only);
        return new AbRound(second, first);
    }

    /// <summary>One side over all rounds: the first run's description of the machine and notes, the busiest start of any run, and every metric merged (median of the runs' medians, samples pooled).</summary>
    internal static PerfResults Merge(IReadOnlyList<PerfResults> runs)
    {
        var first = runs[0];
        var ids = runs.SelectMany(r => r.Metrics).Select(m => m.Id).Distinct(StringComparer.Ordinal);
        var metrics = new List<MetricResult>();
        foreach (var id in ids)
        {
            var each = runs.Select(r => r.Find(id)).ToList();
            var head = each.First(m => m is not null)!;
            metrics.Add(each.Count(m => m is not null) == 1 ? head : GateRunner.Merge(head, each));
        }

        var busy = runs.Where(r => r.BackgroundCpuBusyPercent is not null).Select(r => r.BackgroundCpuBusyPercent!.Value).ToList();
        return first with
        {
            Metrics = metrics,
            UnexpectedEngineCrashes = runs.Sum(r => r.UnexpectedEngineCrashes),
            BackgroundCpuBusyPercent = busy.Count == 0 ? null : busy.Max(),
        };
    }
}
