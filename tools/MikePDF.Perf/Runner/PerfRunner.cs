using MikePDF.Perf.Documents;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Scenarios;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Runner;

public sealed record PerfRunOptions
{
    public PerfProfile Profile { get; init; } = PerfProfile.Ci;

    /// <summary>Names the baseline file this run belongs to ("ci-x64", "ci-arm64", "dev-...").</summary>
    public string RunnerId { get; init; } = "local";

    /// <summary>"R1", "R2" or "R3" if, and only if, this really is that reference machine. Otherwise null, and every budget verdict is marked as not from reference hardware.</summary>
    public string? ReferenceTier { get; init; }

    public string? EngineDirectory { get; init; }

    public string? PackagePath { get; init; }

    /// <summary>Where the generated PDFs go; null for build/out/perf-documents (or the temp directory outside a checkout).</summary>
    public string? DocumentsDirectory { get; init; }

    public string? BudgetsPath { get; init; }

    /// <summary>Only these metric ids (their scenarios run, the rest are left out of the result); null for all.</summary>
    public IReadOnlySet<string>? Only { get; init; }

    /// <summary>The AppContainer profile name; distinct callers use distinct names.</summary>
    public string AppContainerName { get; init; } = "MikePDF.Perf";

    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>Runs the headless suite: generates the documents, deploys the real engine, runs each scenario, and writes down what it ran on.</summary>
public static class PerfRunner
{
    private sealed record Scenario(IReadOnlyList<MetricDefinition> Metrics, Func<PerfContext, CancellationToken, Task<IReadOnlyList<MetricResult>>> Run);

    private static readonly IReadOnlyList<Scenario> Scenarios =
    [
        .. OpenScenarios.Metrics.Select(m => new Scenario([m], async (c, _) => [await OpenScenarios.RunAsync(c, m)])),
        new Scenario([MetricCatalog.SearchFirst, MetricCatalog.SearchFull], SearchScenario.RunAsync),
        new Scenario([MetricCatalog.EnginePeakWorkingSet, MetricCatalog.EnginePrivateBytes], MemoryScenario.RunAsync),
        new Scenario([MetricCatalog.CrashToTile, MetricCatalog.SupervisorRestore], RecoveryScenario.RunAsync),
        new Scenario([MetricCatalog.TileLatency, MetricCatalog.TileThroughput], RenderScenario.RunAsync),
        new Scenario([MetricCatalog.InstallSize, MetricCatalog.MsixFileSize], (c, _) => Task.FromResult(PackageScenario.Run(c))),
    ];

    public static async Task<PerfResults> RunAsync(PerfRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var only = options.Only;
        if (only is not null)
        {
            var unknown = only.Where(id => MetricCatalog.All.All(m => !string.Equals(m.Id, id, StringComparison.Ordinal))).ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException("Unknown metric id: " + string.Join(", ", unknown), nameof(options));
            }
        }

        var window = TimeSpan.FromMilliseconds(500);
        var (background, waited) = await SystemLoad.WaitForQuietAsync(() => SystemLoad.MeasureAsync(window, cancellationToken), TimeSpan.FromSeconds(options.Profile.QuietWaitSeconds), BusyWarningPercent, TimeSpan.FromMilliseconds(250), cancellationToken);
        using var engine = EngineWorkspace.Create(options.EngineDirectory, options.AppContainerName);
        var documentsDirectory = options.DocumentsDirectory
            ?? (engine.RepositoryRoot.Length > 0 ? Path.Combine(engine.RepositoryRoot, "build", "out", "perf-documents") : Path.Combine(Path.GetTempPath(), "MikePDF.Perf.Documents"));
        var documents = PerfDocuments.Materialize(documentsDirectory, PerfDocuments.All);
        var context = new PerfContext
        {
            Engine = engine,
            Documents = documents.ToDictionary(d => d.Spec.Id, StringComparer.Ordinal),
            Profile = options.Profile,
            Log = options.Log,
            PackagePath = options.PackagePath,
        };

        var metrics = new List<MetricResult>();
        foreach (var scenario in Scenarios)
        {
            if (only is not null && !scenario.Metrics.Any(m => only.Contains(m.Id)))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var produced = await scenario.Run(context, cancellationToken);
            metrics.AddRange(only is null ? produced : produced.Where(m => only.Contains(m.Id)));
        }

        var budgetsPath = options.BudgetsPath ?? BudgetFile.DefaultPath(engine.RepositoryRoot);
        IReadOnlyList<MetricResult> finalMetrics = metrics;
        if (budgetsPath is not null && File.Exists(budgetsPath))
        {
            finalMetrics = BudgetFile.Load(budgetsPath).Apply(metrics, options.ReferenceTier);
        }

        var machine = MachineProbe.Read();
        return new PerfResults
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Runner = new RunnerInfo { Id = options.RunnerId, ReferenceTier = options.ReferenceTier },
            Machine = machine,
            Engine = engine.Info,
            BackgroundCpuBusyPercent = background,
            UnexpectedEngineCrashes = context.UnexpectedEngineCrashes,
            Profile = new ProfileInfo(options.Profile.Name, options.Profile.Warmup, options.Profile.Iterations, options.Profile.HeavyIterations),
            Documents = [.. documents.Select(d => new DocumentInfo(d.Spec.Id, d.Spec.Pages, d.Bytes, d.Sha256))],
            Metrics = finalMetrics,
            Notes = Notes(options, machine, finalMetrics, background, waited, context.UnexpectedEngineCrashes),
        };
    }

    /// <summary>Above this share of background CPU use the timings are flagged as inflated.</summary>
    public const double BusyWarningPercent = 15;

    private static List<string> Notes(PerfRunOptions options, MachineInfo machine, IReadOnlyList<MetricResult> metrics, double? background, TimeSpan waited, int crashes)
    {
        var notes = new List<string>
        {
            "Headless: the broker and the real sandboxed engine, no window. The app's process start, XAML, GPU upload and compositor are not in these numbers; they are measured by the app's trace markers (tools/MikePDF.Perf/measure-app.ps1), which have not been run end to end.",
        };
        if (options.ReferenceTier is null)
        {
            notes.Add("Not reference hardware (R1, R2 and R3 are not declared for this run): every comparison with a §11 target is indicative only and proves nothing about the budget.");
        }

        if (options.Profile.QuietWaitSeconds > 0)
        {
            notes.Add($"Waited {waited.TotalSeconds:0.#} s (at most {options.Profile.QuietWaitSeconds} s) for the machine to be under {BusyWarningPercent:0}% busy before measuring; it read {background:0}% when the run began.");
        }

        if (background is > BusyWarningPercent)
        {
            notes.Add($"The machine was {background:0}% busy with other work before the run began (all programs, all logical processors): every timing here is inflated by that. Rerun on an idle machine before trusting a number.");
        }

        if (crashes > 0)
        {
            notes.Add($"The sandboxed engine died on its own {crashes} time(s) while being measured (not counting the deliberate kills of the recovery scenario). Each affected iteration was thrown away and repeated. An engine that exits by itself is a defect to be found; see docs/perf/README.md, 'Known engine crash'.");
        }

        if (machine.CiHosted)
        {
            notes.Add("Ran on a CI-hosted runner: a shared virtual machine. Useful for catching regressions against its own baseline, never for judging a budget.");
        }

        foreach (var skipped in metrics.Where(m => m.Status == MetricStatus.Skipped))
        {
            notes.Add($"Skipped {skipped.Id}: {skipped.Reason}");
        }

        return notes;
    }
}
