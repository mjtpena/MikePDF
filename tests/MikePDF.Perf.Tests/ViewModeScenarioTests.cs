using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Scenarios;

namespace MikePDF.Perf.Tests;

/// <summary>The two view-mode metrics (PV-12, PV-17) against the real engine with the quick profile: they run, check their own correctness, and are not gated.</summary>
public sealed class ViewModeScenarioTests(HarnessFixture fixture)
{
    [Fact]
    public async Task TheViewModeScenariosRunAndProduceBothMetrics()
    {
        var results = await ViewModeScenarios.RunAsync(fixture.Context(PerfProfile.Quick));
        Assert.Equal(ViewModeMetrics.All.Select(m => m.Id), results.Select(r => r.Id));
        Assert.All(results, r => Assert.True(r.Status == MetricStatus.Measured, r.Id + ": " + r.Reason));

        var open = results.Single(r => r.Id == ViewModeMetrics.LargeFacingOpen.Id);
        Assert.Equal(10_000, open.Details!["pages"]);
        Assert.True(open.Details["t_layout_ms"] >= 0);
        Assert.True(open.Details["t_relayout_all_chunks_ms"] >= open.Details["t_relayout_per_chunk_ms"]);
        Assert.True(open.Median > 0);

        var turn = results.Single(r => r.Id == ViewModeMetrics.PageTurnTiles.Id);
        Assert.InRange(turn.Details!["tiles_per_page"], 1, 12); // a page at fit-page scale on a 1080p screen is a handful of tiles
        Assert.True(turn.Median > 0);
    }

    /// <summary>Not a test: set MIKEPDF_PERF_VIEWMODES_OUT to a file name and the scenarios are run with the CI profile (median of 15) and written there, for a report. Skipped otherwise.</summary>
    [Fact]
    public async Task MeasureTheViewModesWithTheCiProfileWhenAsked()
    {
        var output = Environment.GetEnvironmentVariable("MIKEPDF_PERF_VIEWMODES_OUT");
        Assert.SkipUnless(!string.IsNullOrEmpty(output), "Set MIKEPDF_PERF_VIEWMODES_OUT to measure.");
        var results = await ViewModeScenarios.RunAsync(fixture.Context(PerfProfile.Ci));
        var lines = results.Select(r => $"{r.Id}: median {r.Median} ms, p95 {r.P95}, min {r.Min}, max {r.Max}, n {r.N}; " + string.Join(", ", (r.Details ?? new Dictionary<string, double>()).Select(d => $"{d.Key}={d.Value}")));
        await File.WriteAllLinesAsync(output!, lines);
    }

    [Fact]
    public void NeitherMetricIsAGateNorTiedToABudgetSoTheGateAndTheBudgetFileAreUntouched()
    {
        Assert.All(ViewModeMetrics.All, m =>
        {
            Assert.False(m.Gate);
            Assert.Null(m.BudgetId);
        });
        Assert.DoesNotContain(MetricCatalog.All, m => ViewModeMetrics.All.Any(v => v.Id == m.Id));
    }
}
