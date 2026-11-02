using System.Globalization;
using MikePDF.Perf.Comparison;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;
using MikePDF.Perf.Scenarios;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Cli;

/// <summary>
/// <c>ab</c>: measure a built base and a built head on this machine, round after round, and decide with <see cref="AbComparer"/>. The CI gate
/// (.github/workflows/perf.yml). Exit codes: 0 passed, 1 a regression (or, on declared reference hardware, a metric over its section 11 target),
/// 2 bad usage, 3 a metric could not be measured.
/// </summary>
internal static class AbCommand
{
    public static async Task<int> RunAsync(Arguments args, TextWriter output, CancellationToken cancellationToken)
    {
        var profileName = args.Get("profile") ?? "ci";
        _ = PerfProfile.Find(profileName) ?? throw new UsageException($"Unknown profile '{profileName}' (quick, ci or full).");
        var runnerId = args.Get("runner-id") ?? "local";
        var tier = args.Get("reference-tier");
        if (tier is not null && tier is not ("R1" or "R2" or "R3"))
        {
            throw new UsageException("--reference-tier must be R1, R2 or R3.");
        }

        var rounds = args.GetInt("rounds", AbRunner.DefaultRounds);
        var confirmRounds = args.GetInt("confirm-rounds", AbRunner.DefaultConfirmRounds);
        var settings = new AbSettings
        {
            Threshold = args.GetDouble("threshold", AbSettings.DefaultThreshold),
            Margin = args.GetDouble("margin", AbSettings.DefaultMargin),
            Confidence = args.GetDouble("confidence", AbSettings.DefaultConfidence),
        };
        var planted = args.GetDouble("plant-head", 0);
        var outDir = args.Get("out") ?? "perf-results";
        var budgetsPath = args.Get("budgets");
        var baselineDirectory = args.Get("baseline-dir");
        var onlyText = args.Get("only");
        IReadOnlySet<string>? onlyAlways = onlyText is null ? null : onlyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var baseLabel = args.Get("base-label") ?? "base";
        var headLabel = args.Get("head-label") ?? "head";
        var work = args.Get("work-dir") ?? Path.Combine(Path.GetTempPath(), "MikePDF.Perf.ab");
        var container = args.Get("appcontainer-prefix") ?? "MikePDF.Perf";
        var baseSide = new ProcessSide("base", ExistingFile(args.Require("base-exe")), ExistingDirectory(args.Require("base-engine")), Path.Combine(work, "base"), runnerId, profileName, container);
        var headSide = new ProcessSide("head", ExistingFile(args.Require("head-exe")), ExistingDirectory(args.Require("head-engine")), Path.Combine(work, "head"), runnerId, profileName, container, planted);
        args.RejectUnused();
        if (rounds < 1 || confirmRounds < 0 || planted < 0 || planted > 2)
        {
            throw new UsageException("--rounds must be at least 1, --confirm-rounds 0 or more, and --plant-head a fraction between 0 and 2.");
        }

        void Log(string line) => output.WriteLine(line);
        var counters = new Dictionary<AbSide, int>();
        async Task<PerfResults> Measure(AbSide side, IReadOnlySet<string>? only)
        {
            counters[side] = counters.GetValueOrDefault(side) + 1;
            var spec = side == AbSide.Base ? baseSide : headSide;
            var directory = Path.Combine(outDir, "rounds", $"{spec.Name}-{counters[side]:00}");
            Log($"{spec.Name} run {counters[side]}...");
            return await spec.RunAsync(directory, only ?? onlyAlways, Log, cancellationToken);
        }

        var outcome = await AbRunner.RunAsync(Measure, settings, rounds, confirmRounds, Log);
        var head = outcome.Head with { Runner = new RunnerInfo { Id = runnerId, ReferenceTier = tier } };
        var resolvedBudgets = budgetsPath ?? BudgetFile.DefaultPath(EngineWorkspace.FindRepositoryRoot() ?? string.Empty);
        if (resolvedBudgets is not null && File.Exists(resolvedBudgets))
        {
            head = head with { Metrics = BudgetFile.Load(resolvedBudgets).Apply(head.Metrics, tier) };
        }

        ComparisonReport? trend = null;
        string? trendNote = null;
        if (baselineDirectory is not null)
        {
            var choice = BaselineSelector.Choose(baselineDirectory, runnerId, head.Machine.Cpu);
            trendNote = choice.Message;
            trend = choice.Baseline is null ? null : BaselineComparer.Compare(choice.Baseline, head);
        }

        var meta = new AbMeta(baseLabel, headLabel, runnerId, profileName, rounds, outcome.Rounds.Count, outcome.Confirmed, planted, tier, trendNote);
        var markdown = AbMarkdown.Render(outcome.Report, meta, head, trend);
        Directory.CreateDirectory(outDir);
        ResultsJson.Save(Path.Combine(outDir, "perf-results.json"), head); // the head over all rounds: also the candidate for docs/perf/baselines (trend record)
        ResultsJson.Save(Path.Combine(outDir, "perf-results-base.json"), outcome.Base);
        await File.WriteAllTextAsync(Path.Combine(outDir, "perf-ab.json"), ResultsJson.Serialize(new { meta, outcome.Report }), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outDir, "perf-results.md"), markdown, cancellationToken);
        await output.WriteLineAsync(markdown);

        foreach (var item in outcome.Report.Regressions)
        {
            await output.WriteLineAsync($"{(item.Verdict == Verdict.Failed ? "FAILED" : "REGRESSION")} {item.Id}: {item.Note}");
        }

        var overTarget = tier is null ? [] : head.Metrics.Where(m => m.Budget is { Within: false }).ToList();
        foreach (var metric in overTarget)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"OVER TARGET {metric.Id}: {metric.Median} {metric.Unit} against {metric.Budget!.Target} on declared reference hardware {tier}"));
        }

        if (outcome.Report.Items.Any(i => i.Verdict == Verdict.Regressed) || overTarget.Count > 0)
        {
            return 1;
        }

        return outcome.Report.Failed ? 3 : 0;
    }

    private static string ExistingFile(string path) => File.Exists(path) ? Path.GetFullPath(path) : throw new UsageException($"{path} does not exist.");

    private static string ExistingDirectory(string path) => Directory.Exists(path) ? Path.GetFullPath(path) : throw new UsageException($"{path} does not exist.");
}
