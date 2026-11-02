using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Runner;
using MikePDF.Perf.Setup;
using MikePDF.Perf.Trace;

namespace MikePDF.Perf.Tests;

/// <summary>docs/perf/budgets.json against master prompt §11 and against what the harness measures.</summary>
public sealed class BudgetsFileTests
{
    private static readonly string[] AllowedMeasuredBy = ["headless", "headless-part+app-etw", "app-etw", "package", "manual"];

    private static BudgetFile Load()
    {
        var root = EngineWorkspace.FindRepositoryRoot() ?? throw new DirectoryNotFoundException("MikePDF.slnx not found above " + AppContext.BaseDirectory);
        return BudgetFile.Load(BudgetFile.DefaultPath(root)!);
    }

    [Fact]
    public void EveryRowOfTheSection11TableIsThereWithItsNumbers()
    {
        var budgets = Load();
        Assert.Equal(1, budgets.Schema);
        Assert.Equal(0.10, budgets.RegressionThreshold);
        Assert.Equal(["R1", "R2", "R3"], budgets.Tiers.Keys.Order());

        // (budget id, R1, R3) exactly as the table in docs/MASTER-PROMPT.md section 11 gives them; R2 and R3 are R1's unless the table says otherwise.
        (string Id, double? R1, double? R3)[] table =
        [
            ("startup.cold", 600, 350),
            ("open.cold.1p", 800, 800),
            ("open.warm.20p", 250, 250),
            ("open.large.2000p", 1000, 1000),
            ("scroll.fps", 60, null),
            ("scroll.frame_max", 50, 50),
            ("scroll.blank", 0, 0),
            ("zoom.response", 300, 300),
            ("search.500p.first", 300, 300),
            ("search.500p.full", 3000, 3000),
            ("ink.latency", 25, 25),
            ("memory.ui.20p", 200, 200),
            ("memory.engine.500p", 600, 600),
            ("install.size", 60, 60),
            ("recovery.engine_crash", 2000, 2000),
        ];
        Assert.Equal(table.Select(t => t.Id).Order(), budgets.Metrics.Select(m => m.BudgetId).Order());
        foreach (var (id, r1, r3) in table)
        {
            var entry = budgets.Find(id)!;
            Assert.Equal(r1, entry.Targets["R1"]);
            Assert.Equal(r1, entry.Targets["R2"]); // §11: budgets are measured on R1 unless stated
            Assert.Equal(r3, entry.Targets["R3"]);
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Notes), id);
        }
    }

    [Fact]
    public void EveryRowSaysHowItIsMeasuredAndTheRowsThatAreNotMeasuredSayWhy()
    {
        foreach (var entry in Load().Metrics)
        {
            Assert.Contains(entry.MeasuredBy, AllowedMeasuredBy);
            if (entry.MeasuredBy == "manual")
            {
                Assert.Empty(entry.HarnessMetrics);
                Assert.Matches("(?i)not measured|cannot be measured", entry.Notes);
            }
            else
            {
                Assert.NotEmpty(entry.HarnessMetrics);
            }
        }

        // The three rows nothing here can measure.
        Assert.Equal(["ink.latency", "scroll.blank", "zoom.response"], Load().Metrics.Where(m => m.MeasuredBy == "manual").Select(m => m.BudgetId).Order());
    }

    [Fact]
    public void EveryMetricNamedInTheFileExistsAndEveryMetricWithABudgetIsNamedByIt()
    {
        var budgets = Load();
        var known = MetricCatalog.All.Concat(AppMetricCatalog.All).ToDictionary(m => m.Id, StringComparer.Ordinal);

        foreach (var entry in budgets.Metrics)
        {
            foreach (var id in entry.HarnessMetrics)
            {
                var definition = Assert.Contains(id, known);
                Assert.Equal(entry.BudgetId, definition.BudgetId);
                Assert.Equal(entry.Unit, definition.Unit);
                Assert.Equal(entry.Direction, definition.Direction);
            }

            foreach (var covered in entry.Coverage.Keys)
            {
                Assert.Contains(covered, entry.HarnessMetrics);
                Assert.Equal("lower-bound", entry.Coverage[covered]);
            }
        }

        foreach (var definition in known.Values.Where(d => d.BudgetId is not null))
        {
            var entry = budgets.Find(definition.BudgetId!);
            Assert.NotNull(entry);
            Assert.Contains(definition.Id, entry.HarnessMetrics);
        }
    }

    [Fact]
    public void EveryHeadlessMetricIsEitherHeldAgainstABudgetOrDeclaredSupporting()
    {
        // The supporting metrics have no §11 row of their own; adding a metric forces a decision here.
        string[] supporting =
        [
            "open.engine_warm.first_tile.text20", "open.engine_warm.first_tile.text2000", "memory.engine_private_bytes.text500",
            "recovery.supervisor_restore", "render.tile_latency.text20", "render.tile_throughput.text20", "package.msix_file_size", "app.scroll_frame_interval_p95",
        ];
        var unbudgeted = MetricCatalog.All.Concat(AppMetricCatalog.All).Where(m => m.BudgetId is null).Select(m => m.Id).Order();
        Assert.Equal(supporting.Order(), unbudgeted);
    }

    [Fact]
    public void ABudgetCheckIsAttachedToMeasuredMetricsAndSaysWhetherItCameFromReferenceHardware()
    {
        var budgets = Load();
        var metrics = new[]
        {
            Make.Metric(MetricCatalog.EnginePeakWorkingSet, 300), // budget 600: within
            Make.Metric(MetricCatalog.CrashToTile, 2500), // budget 2000: over
            Make.Metric(MetricCatalog.WarmOpen, 40), // 250, but a lower bound
            Make.Metric(MetricCatalog.TileLatency, 1), // no budget
            MetricResults.Skipped(MetricCatalog.InstallSize, "no package"),
        };

        var here = budgets.Apply(metrics, referenceTier: null);
        Assert.Equal(new BudgetCheck("memory.engine.500p", "R1", 600, true, false), here[0].Budget);
        Assert.Equal(new BudgetCheck("recovery.engine_crash", "R1", 2000, false, false), here[1].Budget);
        Assert.True(here[2].Budget!.IsLowerBound);
        Assert.Null(here[3].Budget);
        Assert.Null(here[4].Budget); // a skipped metric is held against nothing

        var onR3 = budgets.Apply([Make.Metric(AppMetricCatalog.ColdStart, 400)], "R3");
        Assert.Equal(new BudgetCheck("startup.cold", "R3", 350, false, true, "full"), onR3[0].Budget);
        var onR1 = budgets.Apply([Make.Metric(AppMetricCatalog.ColdStart, 400)], "R1");
        Assert.True(onR1[0].Budget!.Within); // 400 <= 600 on R1
    }

    [Fact]
    public void AHigherIsBetterBudgetIsMetFromAbove()
    {
        var budgets = Load();
        Assert.True(budgets.Apply([Make.Metric(AppMetricCatalog.ScrollFps, 60)], "R1")[0].Budget!.Within);
        Assert.False(budgets.Apply([Make.Metric(AppMetricCatalog.ScrollFps, 45)], "R1")[0].Budget!.Within);
        // R3 has no numeric frame-rate target (it is the display's refresh rate), so R1's number is used for it, as a floor and not a claim about R3.
        var entry = budgets.Find("scroll.fps")!;
        Assert.Null(entry.Targets["R3"]);
        Assert.Equal(60, BudgetFile.TargetFor(entry, "R3"));
    }
}
