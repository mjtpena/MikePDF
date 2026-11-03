using System.Globalization;
using System.Text;
using MikePDF.Perf.Comparison;
using MikePDF.Perf.Measurement;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Cli;

/// <summary>
/// <c>calibrate</c>: reads the <c>perf-ab.json</c> files of many A/B runs (the nightly A/A artifacts, or the files kept in docs/perf/calibration) and says
/// how the decision rule behaved: per runner and metric, the largest median change and the largest lower end of the interval that any run showed, and how
/// many runs ended in each verdict. For A/A runs every <c>regressed</c> is a false positive; for planted runs every one that is not is a miss.
/// </summary>
internal static class CalibrateCommand
{
    public static async Task<int> RunAsync(Arguments args, TextWriter output)
    {
        var directory = args.Require("dir");
        args.RejectUnused();
        if (!Directory.Exists(directory))
        {
            throw new UsageException($"{directory} does not exist.");
        }

        var files = Directory.EnumerateFiles(directory, "perf-ab*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            throw new UsageException($"No perf-ab*.json under {directory}.");
        }

        await output.WriteLineAsync(Render([.. files.Select(f => ResultsJson.Deserialize<AbFile>(File.ReadAllText(f)))]));
        return 0;
    }

    internal static string Render(IReadOnlyList<AbFile> runs)
    {
        var text = new StringBuilder();
        foreach (var group in runs.GroupBy(r => r.Meta.RunnerId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var planted = group.Select(r => r.Meta.PlantedSlowdown).Distinct().Order().ToList();
            var settings = group.First().Report.Settings;
            text.AppendLine(CultureInfo.InvariantCulture, $"## `{group.Key}`: {group.Count()} run(s){(planted.Any(p => p > 0) ? ", planted slowdown " + string.Join(", ", planted.Select(p => Fmt.Percent(p, 0))) : ", no slowdown planted")}").AppendLine();
            var failedRuns = group.Count(r => r.Report.Items.Any(i => i.Verdict == Verdict.Regressed));
            text.AppendLine(CultureInfo.InvariantCulture, $"Rule: change over {Fmt.Percent(settings.Threshold, 0)}, lower end of the {Fmt.Percent(settings.Confidence, 0)} interval over {Fmt.Percent(settings.Margin, 0)}, and the metric's floor. Runs with at least one regressed metric: **{failedRuns} of {group.Count()}**.").AppendLine();
            text.AppendLine("| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |").AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            var metrics = group.SelectMany(r => r.Report.Items).Where(i => i.Change is not null).GroupBy(i => i.Id, StringComparer.Ordinal);
            foreach (var metric in metrics)
            {
                var changes = metric.Select(i => i.Change!.Value).ToList();
                var lowers = metric.Select(i => i.ChangeLower!.Value).ToList();
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{metric.Key}` | {metric.Count()} | {Fmt.Percent(changes.Min())} | {Fmt.Percent(Stats.Median(changes))} | {Fmt.Percent(changes.Max())} | {Fmt.Percent(lowers.Max())} | {metric.Count(i => i.Verdict == Verdict.Regressed)} | {metric.Count(i => i.Verdict == Verdict.Unconfirmed)} | {metric.Count(i => i.Verdict is Verdict.Ok or Verdict.Improved)} |");
            }

            text.AppendLine();
        }

        return text.ToString();
    }
}
