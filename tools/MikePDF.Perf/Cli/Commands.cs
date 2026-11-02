using System.Globalization;
using MikePDF.Perf.Comparison;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;
using MikePDF.Perf.Scenarios;
using MikePDF.Perf.Setup;
using MikePDF.Perf.Trace;

namespace MikePDF.Perf.Cli;

/// <summary>The command line. Exit codes: 0 success, 1 a regression (gate, compare) , 2 bad usage, 3 a metric could not be measured (run).</summary>
public static class Commands
{
    public const string Usage = """
        MikePDF.Perf: performance harness (docs/perf/README.md)

          run            Measure the headless suite and write perf-results.json and perf-results.md.
          gate           run, then compare with a baseline; re-measure metrics that look regressed; exit 1 on a regression.
          ab             The CI gate: measure a built base and a built head on this machine, interleaved, and compare them with a confidence interval.
          compare        Compare two result files.
          report         Render a result file as Markdown (to refresh the .md beside a baseline).
          generate       Write the generated test PDFs.
          trace-metrics  Compute the app-level figures (cold start, first pixel, scroll frames) from event CSVs.
          info           Print what is known about this machine.

        run and gate options:
          --profile quick|ci|full   (default ci)       --out <dir>            (default perf-results)
          --runner-id <id>          (default local)    --reference-tier R1|R2|R3   only on that real machine
          --engine-dir <dir>        built engine       --package <file.msix>  for the install-size metrics
          --docs-dir <dir>          generated PDFs     --budgets <file>       (default docs/perf/budgets.json)
          --only <id,id,...>        just these metrics
          --appcontainer <name>     AppContainer profile name (default MikePDF.Perf); give runs that overlap different names
        gate options:
          --baseline <file> (a missing file means no baseline yet) or --baseline-dir <dir> (picks <runner-id>.*.json with the same CPU)
          --threshold 0.10   --retries 2
        ab options:
          --base-exe <MikePDF.Perf.exe> --base-engine <dir>   the base, built (tools/MikePDF.Perf/build-bundle.ps1)
          --head-exe <MikePDF.Perf.exe> --head-engine <dir>   the head, built
          --rounds 8   --confirm-rounds 4   --threshold 0.10   --margin 0.03   --confidence 0.95
          --profile ci   --runner-id <id>   --out <dir>   --budgets <file>   --reference-tier R1|R2|R3
          --baseline-dir <dir>   stored baselines, shown as a trend (never gates)   --base-label <text> --head-label <text>
          --only <id,id,...>   just these metrics   --work-dir <dir>   generated PDFs   --appcontainer-prefix <name>
          --plant-head 0.18      TEST ONLY: slow every time in the head by this fraction, to prove the gate catches it
        report options:   --results <file> [--out <file.md>]
        compare options:  --baseline <file> --current <file> [--threshold 0.10] [--out <dir>]
        generate options: --out <dir> [--only text1,text20,text500,text2000]
        trace-metrics options:
          --run "<events.csv>[;processStart=<ISO time>][;uiWorkingSetMb=<n>][;uiPrivateMb=<n>]"  (repeat for each launch)
          --out <dir> [--runner-id <id>] [--reference-tier R1|R2|R3] [--budgets <file>]
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (args.Count == 0 || args[0] is "help" or "--help" or "-h" or "/?")
            {
                await output.WriteLineAsync(Usage);
                return args.Count == 0 ? 2 : 0;
            }

            var parsed = Arguments.Parse(args);
            return parsed.Command switch
            {
                "run" => await RunSuiteAsync(parsed, output, gate: false, cancellationToken),
                "gate" => await RunSuiteAsync(parsed, output, gate: true, cancellationToken),
                "ab" => await AbCommand.RunAsync(parsed, output, cancellationToken),
                "compare" => await CompareAsync(parsed, output),
                "report" => await ReportAsync(parsed, output),
                "generate" => await GenerateAsync(parsed, output),
                "trace-metrics" => await TraceMetricsAsync(parsed, output),
                "info" => await InfoAsync(parsed, output),
                _ => throw new UsageException($"Unknown command '{parsed.Command}'."),
            };
        }
        catch (UsageException e)
        {
            await error.WriteLineAsync("error: " + e.Message);
            await error.WriteLineAsync("Run with --help for usage.");
            return 2;
        }
        catch (Exception e) when (e is MikePDF.Broker.EngineStartException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // The sandbox or the engine could not be brought up on this machine: nothing was measured.
            await error.WriteLineAsync("error: the sandboxed engine could not be started: " + e.Message);
            return 3;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or IOException)
        {
            await error.WriteLineAsync("error: " + e.Message);
            return 2;
        }
    }

    private static PerfRunOptions OptionsFrom(Arguments args, Action<string> log)
    {
        var profileName = args.Get("profile") ?? "ci";
        var profile = PerfProfile.Find(profileName) ?? throw new UsageException($"Unknown profile '{profileName}' (quick, ci or full).");
        var tier = args.Get("reference-tier");
        if (tier is not null && tier is not ("R1" or "R2" or "R3"))
        {
            throw new UsageException("--reference-tier must be R1, R2 or R3.");
        }

        var only = args.Get("only");
        return new PerfRunOptions
        {
            Profile = profile,
            RunnerId = args.Get("runner-id") ?? "local",
            ReferenceTier = tier,
            EngineDirectory = args.Get("engine-dir"),
            PackagePath = args.Get("package"),
            DocumentsDirectory = args.Get("docs-dir"),
            BudgetsPath = args.Get("budgets"),
            AppContainerName = args.Get("appcontainer") ?? "MikePDF.Perf",
            Only = only is null ? null : only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
            Log = log,
        };
    }

    private static async Task<int> RunSuiteAsync(Arguments args, TextWriter output, bool gate, CancellationToken cancellationToken)
    {
        void Log(string line) => output.WriteLine(line);
        var options = OptionsFrom(args, Log);
        var outDir = args.Get("out") ?? "perf-results";
        var baselinePath = gate ? args.Get("baseline") : null;
        var baselineDirectory = gate ? args.Get("baseline-dir") : null;
        if (gate && (baselinePath is null) == (baselineDirectory is null))
        {
            throw new UsageException("gate needs exactly one of --baseline <file> and --baseline-dir <directory>.");
        }

        var threshold = args.GetDouble("threshold", BaselineComparer.DefaultThreshold);
        var retries = args.GetInt("retries", GateRunner.DefaultRetries);
        args.RejectUnused();

        PerfResults results;
        ComparisonReport? report = null;
        string? baselineNote = null;
        if (gate)
        {
            PerfResults? baseline;
            if (baselineDirectory is not null)
            {
                var choice = BaselineSelector.Choose(baselineDirectory, options.RunnerId, MachineProbe.Read().Cpu);
                baseline = choice.Baseline;
                baselineNote = choice.Message;
            }
            else
            {
                baseline = baselinePath is not null && File.Exists(baselinePath) ? ResultsJson.Load(baselinePath) : null;
                baselineNote = baseline is null ? $"No baseline at {baselinePath}: this run becomes the candidate baseline and nothing is compared." : $"Baseline {baselinePath}.";
            }

            await output.WriteLineAsync(baselineNote);
            var outcome = await GateRunner.RunAsync(baseline, only => PerfRunner.RunAsync(only is null ? options : options with { Only = only }, cancellationToken), threshold, retries, Log);
            results = outcome.Results;
            report = outcome.Report;
        }
        else
        {
            results = await PerfRunner.RunAsync(options, cancellationToken);
        }

        Directory.CreateDirectory(outDir);
        ResultsJson.Save(Path.Combine(outDir, "perf-results.json"), results);
        await File.WriteAllTextAsync(Path.Combine(outDir, "perf-results.md"), MarkdownReport.Render(results, report, baselineNote), cancellationToken);
        if (report is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(outDir, "perf-comparison.json"), ResultsJson.Serialize(report), cancellationToken);
        }

        await output.WriteLineAsync(MarkdownReport.Render(results, report, baselineNote));
        if (report is { Failed: true })
        {
            foreach (var item in report.Regressions)
            {
                await output.WriteLineAsync($"REGRESSION {item.Id}: {item.Note}");
            }

            return 1;
        }

        var failed = results.Metrics.Where(m => m.Status == MetricStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            foreach (var metric in failed)
            {
                await output.WriteLineAsync($"FAILED {metric.Id}: {metric.Reason}");
            }

            return 3;
        }

        return 0;
    }

    private static async Task<int> CompareAsync(Arguments args, TextWriter output)
    {
        var baseline = ResultsJson.Load(args.Require("baseline"));
        var current = ResultsJson.Load(args.Require("current"));
        var threshold = args.GetDouble("threshold", BaselineComparer.DefaultThreshold);
        var outDir = args.Get("out");
        args.RejectUnused();

        var report = BaselineComparer.Compare(baseline, current, threshold);
        var markdown = MarkdownReport.Render(current, report);
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "perf-comparison.md"), markdown);
            await File.WriteAllTextAsync(Path.Combine(outDir, "perf-comparison.json"), ResultsJson.Serialize(report));
        }

        await output.WriteLineAsync(markdown);
        foreach (var item in report.Regressions)
        {
            await output.WriteLineAsync($"REGRESSION {item.Id}: {item.Note}");
        }

        return report.Failed ? 1 : 0;
    }

    private static async Task<int> ReportAsync(Arguments args, TextWriter output)
    {
        var results = ResultsJson.Load(args.Require("results"));
        var outFile = args.Get("out");
        args.RejectUnused();
        var markdown = MarkdownReport.Render(results);
        if (outFile is null)
        {
            await output.WriteLineAsync(markdown);
        }
        else
        {
            await File.WriteAllTextAsync(outFile, markdown);
        }

        return 0;
    }

    private static async Task<int> GenerateAsync(Arguments args, TextWriter output)
    {
        var outDir = args.Require("out");
        var only = args.Get("only");
        args.RejectUnused();
        var specs = only is null
            ? PerfDocuments.All
            : [.. only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(id => PerfDocuments.Find(id) ?? throw new UsageException($"Unknown document '{id}'."))];
        foreach (var document in PerfDocuments.Materialize(outDir, specs))
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{document.Spec.Id}\t{document.Spec.Pages} pages\t{document.Bytes} bytes\tsha256 {document.Sha256}\t{document.Path}"));
        }

        return 0;
    }

    private static async Task<int> TraceMetricsAsync(Arguments args, TextWriter output)
    {
        var specs = args.GetAll("run");
        if (specs.Count == 0)
        {
            throw new UsageException("At least one --run is needed.");
        }

        var outDir = args.Require("out");
        var runnerId = args.Get("runner-id") ?? "local-app";
        var tier = args.Get("reference-tier");
        var budgetsPath = args.Get("budgets") ?? BudgetFile.DefaultPath(EngineWorkspace.FindRepositoryRoot() ?? string.Empty);
        args.RejectUnused();

        var runs = specs.Select(ParseRun).ToList();
        var budgets = budgetsPath is not null && File.Exists(budgetsPath) ? BudgetFile.Load(budgetsPath) : null;
        var results = AppTraceReport.Build(runs, runnerId, tier, budgets);
        Directory.CreateDirectory(outDir);
        ResultsJson.Save(Path.Combine(outDir, "app-results.json"), results);
        var markdown = MarkdownReport.Render(results);
        await File.WriteAllTextAsync(Path.Combine(outDir, "app-results.md"), markdown);
        await output.WriteLineAsync(markdown);
        return 0;
    }

    private static AppRun ParseRun(string spec)
    {
        var parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DateTimeOffset? start = null;
        double? workingSet = null;
        double? privateMb = null;
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                throw new UsageException($"'{part}' is not name=value.");
            }

            switch (pair[0])
            {
                case "processStart":
                    start = DateTimeOffset.Parse(pair[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    break;
                case "uiWorkingSetMb":
                    workingSet = double.Parse(pair[1], CultureInfo.InvariantCulture);
                    break;
                case "uiPrivateMb":
                    privateMb = double.Parse(pair[1], CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new UsageException($"Unknown run setting '{pair[0]}'.");
            }
        }

        return new AppRun(TraceAnalysis.ParseCsv(File.ReadAllText(parts[0])), start, workingSet, privateMb);
    }

    private static async Task<int> InfoAsync(Arguments args, TextWriter output)
    {
        args.RejectUnused();
        await output.WriteLineAsync(ResultsJson.Serialize(MachineProbe.Read()));
        return 0;
    }
}
