using MikePDF.Perf.Measurement;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Comparison;

/// <summary>The decision rule of the same-runner A/B gate. See <see cref="AbComparer"/> and docs/perf/README.md.</summary>
public sealed record AbSettings
{
    public const double DefaultThreshold = 0.10;
    public const double DefaultMargin = 0.03;
    public const double DefaultConfidence = 0.95;

    /// <summary>§11: a regression of more than 10% fails the build. The estimate (the median of the per-round changes) must be over this.</summary>
    public double Threshold { get; init; } = DefaultThreshold;

    /// <summary>
    /// The lower end of the confidence interval of the change must also be over this: nearly every round, not just the median one, must show a slowdown.
    /// Chosen from the A/A runs (docs/perf/README.md, "Where the numbers come from").
    /// </summary>
    public double Margin { get; init; } = DefaultMargin;

    /// <summary>The confidence of the interval (95%; the sign-test interval gets as close to it as the number of rounds allows).</summary>
    public double Confidence { get; init; } = DefaultConfidence;

    /// <summary>Fewer paired rounds than this cannot fail the build: the interval would be too wide to mean anything.</summary>
    public int MinimumPairs { get; init; } = PairedStats.MinimumFor95;
}

/// <summary>One round of the A/B run: the base and the head measured back to back on the same machine.</summary>
public sealed record AbRound(PerfResults Base, PerfResults Head);

/// <summary>
/// One metric, head against base. <see cref="Ratio"/> is head over base as measured (so for a throughput, below 1 is worse); <see cref="Change"/> is the
/// same thing as a fraction of the base, signed so that positive always means worse, which is what the threshold is held against.
/// </summary>
public sealed record AbMetric(
    string Id,
    string Unit,
    string Direction,
    Verdict Verdict,
    double? Base,
    double? Head,
    double? Ratio,
    double? RatioLower,
    double? RatioUpper,
    double? Change,
    double? ChangeLower,
    double? ChangeUpper,
    double? Confidence,
    int Pairs,
    IReadOnlyList<double> Ratios,
    string Note);

public sealed record AbReport(AbSettings Settings, IReadOnlyList<AbMetric> Items, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<AbMetric> Regressions => [.. Items.Where(i => i.Verdict is Verdict.Regressed or Verdict.Failed)];

    /// <summary>True when the gate fails.</summary>
    public bool Failed => Regressions.Count > 0;
}

/// <summary>
/// Holds the head against the base using rounds in which both were measured one after the other on the same runner. For every round a metric gives a
/// change <c>(head - base) / base</c> (positive is worse); the metric's change is the median of those, with the sign-test confidence interval
/// (<see cref="PairedStats"/>). It <b>regresses</b> when all three hold: the median change is over <see cref="AbSettings.Threshold"/>, the lower end of
/// the interval is over <see cref="AbSettings.Margin"/>, and the difference of the medians is over the metric's absolute floor. Over the threshold
/// without the evidence is <see cref="Verdict.Unconfirmed"/>: reported, never failing.
/// </summary>
public static class AbComparer
{
    public static AbReport Compare(IReadOnlyList<AbRound> rounds, AbSettings? settings = null, Func<string, MetricDefinition?>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        settings ??= new AbSettings();
        definitions ??= MetricCatalog.Find;
        var warnings = new List<string>();
        if (rounds.Count == 0)
        {
            return new AbReport(settings, [], ["No rounds were measured."]);
        }

        var ids = rounds.SelectMany(r => r.Head.Metrics).Select(m => m.Id).Distinct(StringComparer.Ordinal).ToList();
        var items = ids.Select(id => CompareOne(id, rounds, settings, definitions(id))).ToList();
        return new AbReport(settings, items, warnings);
    }

    private static AbMetric CompareOne(string id, IReadOnlyList<AbRound> rounds, AbSettings settings, MetricDefinition? definition)
    {
        var heads = rounds.Select(r => r.Head.Find(id)).Where(m => m is not null).Select(m => m!).ToList();
        var first = heads[0];
        AbMetric Plain(Verdict verdict, string note, int pairs = 0) =>
            new(id, first.Unit, first.Direction, verdict, null, null, null, null, null, null, null, null, null, pairs, [], note);

        if (heads.Any(m => m.Status == MetricStatus.Failed))
        {
            return Plain(Verdict.Failed, heads.First(m => m.Status == MetricStatus.Failed).Reason ?? "could not be measured");
        }

        if (heads.All(m => m.Status == MetricStatus.Skipped))
        {
            return Plain(Verdict.Skipped, first.Reason ?? "skipped");
        }

        var lowerIsBetter = first.LowerIsBetter;
        var pairs = new List<(double Base, double Head)>();
        foreach (var round in rounds)
        {
            if (round.Base.Find(id) is { Status: MetricStatus.Measured, Median: { } b } && b > 0 && round.Head.Find(id) is { Status: MetricStatus.Measured, Median: { } h })
            {
                pairs.Add((b, h));
            }
        }

        if (pairs.Count == 0)
        {
            return Plain(Verdict.NoBaseline, "not measured on the base, so nothing to compare with");
        }

        var changes = pairs.Select(p => lowerIsBetter ? (p.Head - p.Base) / p.Base : (p.Base - p.Head) / p.Base).ToList();
        var interval = PairedStats.MedianInterval(changes, settings.Confidence);
        var baseMedian = Stats.Median(pairs.Select(p => p.Base).ToList());
        var headMedian = Stats.Median(pairs.Select(p => p.Head).ToList());
        var ratios = pairs.Select(p => Math.Round(p.Head / p.Base, 4)).ToList();

        // Change is worse-positive; Ratio is head over base. For a "higher is better" metric the two run in opposite directions.
        var ratio = lowerIsBetter ? 1 + interval.Estimate : 1 - interval.Estimate;
        var ratioLower = lowerIsBetter ? 1 + interval.Lower : 1 - interval.Upper;
        var ratioUpper = lowerIsBetter ? 1 + interval.Upper : 1 - interval.Lower;

        var floor = definition?.FloorForAb ?? 0;
        var worseBy = lowerIsBetter ? headMedian - baseMedian : baseMedian - headMedian;
        var verdict = Verdict.Ok;
        var note = string.Empty;
        if (pairs.Count < settings.MinimumPairs)
        {
            verdict = Verdict.Unconfirmed;
            note = $"only {pairs.Count} paired round(s); at least {settings.MinimumPairs} are needed for a {Fmt.Percent(settings.Confidence, 0)} interval";
        }
        else if (interval.Estimate > settings.Threshold)
        {
            if (interval.Lower > settings.Margin && worseBy > floor)
            {
                verdict = definition?.Gate == false ? Verdict.Advisory : Verdict.Regressed;
                note = $"{Fmt.Percent(interval.Estimate, 1)} worse (limit {Fmt.Percent(settings.Threshold, 0)}); the {Fmt.Percent(interval.Confidence, 1)} interval is {Fmt.Percent(interval.Lower, 1)} to {Fmt.Percent(interval.Upper, 1)}, all over the {Fmt.Percent(settings.Margin, 0)} margin"
                    + (definition?.Gate == false ? "; advisory metric, not gating" : string.Empty);
            }
            else if (worseBy <= floor)
            {
                note = $"{Fmt.Percent(interval.Estimate, 1)} worse but only {worseBy:0.###} {first.Unit}, under the {floor:0.###} {first.Unit} floor";
            }
            else
            {
                verdict = Verdict.Unconfirmed;
                note = $"{Fmt.Percent(interval.Estimate, 1)} worse, but the {Fmt.Percent(interval.Confidence, 1)} interval ({Fmt.Percent(interval.Lower, 1)} to {Fmt.Percent(interval.Upper, 1)}) reaches down to the {Fmt.Percent(settings.Margin, 0)} margin: not enough evidence that it is real";
            }
        }
        else if (interval.Estimate < -settings.Threshold && interval.Upper < 0 && -worseBy > floor)
        {
            verdict = Verdict.Improved;
            note = $"{Fmt.Percent(-interval.Estimate, 1)} better";
        }

        return new AbMetric(id, first.Unit, first.Direction, verdict, Math.Round(baseMedian, 3), Math.Round(headMedian, 3), Math.Round(ratio, 4), Math.Round(ratioLower, 4), Math.Round(ratioUpper, 4),
            Math.Round(interval.Estimate, 4), Math.Round(interval.Lower, 4), Math.Round(interval.Upper, 4), Math.Round(interval.Confidence, 4), pairs.Count, ratios, note);
    }
}
