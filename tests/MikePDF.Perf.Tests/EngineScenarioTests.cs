using System.IO.Compression;
using MikePDF.Broker;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Scenarios;
using MikePDF.Protocol;

namespace MikePDF.Perf.Tests;

/// <summary>Each scenario against the real sandboxed engine with the quick profile: the numbers mean nothing here, what is checked is that every scenario runs, checks its own correctness and produces what it promises.</summary>
public sealed class EngineScenarioTests(HarnessFixture fixture)
{
    private PerfContext Context => fixture.Context(PerfProfile.Quick);

    private PerfContext WithPackage(string path) => new() { Engine = fixture.Engine, Documents = fixture.Documents, Profile = PerfProfile.Quick, PackagePath = path };

    [Fact]
    public async Task EveryGeneratedDocumentOpensInTheSandboxedEngineWithItsPageCount()
    {
        await using var engine = await fixture.Engine.StartDocumentEngineAsync();
        foreach (var spec in PerfDocuments.All)
        {
            var opened = await engine.OpenDocumentAsync(fixture.Documents[spec.Id].Path);
            Assert.Equal(Status.Ok, opened.Status);
            await using var document = opened.Value!;
            Assert.Equal((uint)spec.Pages, document.PageCount);
            var sizes = await document.GetPageSizesAsync(0, 1);
            Assert.Equal((612f, 792f), (sizes.Value!.Widths[0], sizes.Value.Heights[0]));
        }
    }

    [Fact]
    public async Task TheFirstTileOfAGeneratedPageIsFiveHundredTwelvePixelsOfRealInk()
    {
        await using var engine = await fixture.Engine.StartDocumentEngineAsync();
        var opened = await engine.OpenDocumentAsync(fixture.Documents["text20"].Path);
        await using var document = opened.Value!;

        var first = await document.RenderTileAsync(AppTiles.First(0, 612, 792), 1);
        Assert.Equal(Status.Ok, first.Status);
        using var tile = first.Value!;
        Assert.Equal((512, 512), (tile.Width, tile.Height));
        Assert.True(AppTiles.HasInk(tile));

        // The page at 100% is 816 x 1056 px: 2 columns by 3 rows, the right column 304 px wide and the bottom row 32 px tall. TileX and TileY are pixel offsets.
        var all = AppTiles.All(0, 612, 792).ToList();
        Assert.Equal(6, all.Count);
        Assert.Equal([(0, 0, 512u, 512u), (512, 0, 304u, 512u), (0, 512, 512u, 512u), (512, 512, 304u, 512u), (0, 1024, 512u, 32u), (512, 1024, 304u, 32u)], all.Select(a => (a.TileX, a.TileY, a.TileWidth, a.TileHeight)));
        foreach (var args in all)
        {
            var result = await document.RenderTileAsync(args, 1);
            Assert.Equal(Status.Ok, result.Status);
            using var rendered = result.Value!;
            Assert.Equal(((int)args.TileWidth, (int)args.TileHeight), (rendered.Width, rendered.Height));
            // Text fills the page down to y = 82 pt and the page number sits at 40 pt: the first two rows have ink, the 32 px strip at the bottom is margin.
            Assert.Equal(args.TileY < 1024, AppTiles.HasInk(rendered));
        }
    }

    [Fact]
    public async Task ABlankTileIsNotMistakenForInk()
    {
        await using var engine = await fixture.Engine.StartDocumentEngineAsync();
        var opened = await engine.OpenDocumentAsync(fixture.Documents["text20"].Path);
        await using var document = opened.Value!;
        // The bottom-right corner of the page is margin: white.
        var corner = await document.RenderTileAsync(new RenderTileArgs(0, AppTiles.Scale, 0, 512, 1024, 304, 32), 1);
        using var tile = corner.Value!;
        Assert.False(AppTiles.HasInk(tile));
    }

    [Fact]
    public async Task TheSearchFindsEveryPlantedNeedleAndNothingElseOnTheRightPages()
    {
        var spec = PerfDocuments.Text500;
        await using var pool = await fixture.Engine.StartPoolEngineAsync();
        var opened = await pool.OpenDocumentAsync(fixture.Documents[spec.Id].Path);
        await using var document = opened.Value!;
        var reader = new DocumentReader(new DocumentReadingEngine(document), (int)document.PageCount);
        using var session = new FindSession(reader, (int)document.PageCount, new SearchOptions(PerfDocuments.Needle), 0);

        await session.StartAsync();

        Assert.True(session.Progress.IsComplete);
        Assert.Equal(spec.ExpectedNeedleMatches, session.Progress.MatchCount);
        Assert.Equal(spec.Pages, session.Progress.PagesSearched);
        Assert.Equal(0, session.Progress.PagesFailed);
        foreach (var page in Enumerable.Range(0, spec.Pages))
        {
            Assert.Equal(spec.Needles.GetValueOrDefault(page), session.MatchesOnPage(page).Count);
        }

        // A phrase that is not in the document is found nowhere, so "found everything" above was not a search that matches anything.
        using var nothing = new FindSession(reader, (int)document.PageCount, new SearchOptions("zzzzzzzzzz"), 0);
        await nothing.StartAsync();
        Assert.Equal(0, nothing.Progress.MatchCount);
        Assert.True(nothing.Progress.IsComplete);
    }

    [Theory]
    [InlineData("open.warm.first_tile.text20")]
    [InlineData("open.cold_engine.first_tile.text1")]
    [InlineData("open.large.first_tile.text2000")]
    [InlineData("open.engine_warm.first_tile.text20")]
    [InlineData("open.engine_warm.first_tile.text2000")]
    public async Task EachOpenMetricIsMeasuredOverTheProfilesIterationsAndTheAppSequenceTimingsAreInOrder(string id)
    {
        var definition = MetricCatalog.Find(id)!;
        var result = await OpenScenarios.RunAsync(Context, definition);
        Assert.Equal(MetricStatus.Measured, result.Status);
        Assert.Equal(PerfProfile.Quick.Iterations, result.N);
        Assert.Equal(PerfProfile.Quick.Iterations, result.Samples.Count);
        Assert.InRange(result.Median!.Value, 0.5, 20_000);
        Assert.True(result.Min <= result.Median && result.Median <= result.Max && result.P95 <= result.Max);

        if (definition.Id.StartsWith("open.engine_warm", StringComparison.Ordinal))
        {
            Assert.Null(result.Details);
            return;
        }

        var stages = result.Details!;
        string[] order = ["t_engine_started_ms", "t_document_opened_ms", "t_pool_engine_started_ms", "t_pool_document_opened_ms", "t_info_ms", "t_first_sizes_ms"];
        for (var i = 1; i < order.Length; i++)
        {
            Assert.True(stages[order[i - 1]] <= stages[order[i]], $"{order[i - 1]} {stages[order[i - 1]]} should not be later than {order[i]} {stages[order[i]]}");
        }

        Assert.True(stages["t_first_sizes_ms"] < result.Median, "the first tile comes after the sizes");
        Assert.True(stages["t_first_pixel_ms"] <= result.Median + 0.001, "the first pixel is the earlier of tile and placeholder");
        Assert.True(stages["t_first_pixel_ms"] > stages["t_first_sizes_ms"]);
    }

    [Fact]
    public async Task TheSearchMetricsReachTheFirstMatchBeforeTheLastPageAndCheckTheirOwnCorrectness()
    {
        var results = await SearchScenario.RunAsync(Context, CancellationToken.None);
        var first = results.Single(r => r.Id == MetricCatalog.SearchFirst.Id);
        var full = results.Single(r => r.Id == MetricCatalog.SearchFull.Id);
        Assert.Equal(MetricStatus.Measured, first.Status);
        Assert.Equal(MetricStatus.Measured, full.Status);
        Assert.Equal(PerfProfile.Quick.HeavyIterations, full.N);
        Assert.True(first.Median < full.Median, "the first result (page 5) comes long before the last page (page 500)");
        Assert.InRange(full.Details!["ms_per_page"], 0.01, 100);
    }

    [Fact]
    public async Task TheMemoryScenarioRendersEveryTileOfEveryPageAndReadsTheEngineProcess()
    {
        var results = await MemoryScenario.RunAsync(Context, CancellationToken.None);
        var peak = results.Single(r => r.Id == MetricCatalog.EnginePeakWorkingSet.Id);
        var privateBytes = results.Single(r => r.Id == MetricCatalog.EnginePrivateBytes.Id);
        Assert.Equal(MetricStatus.Measured, peak.Status);
        Assert.Equal(MetricStatus.Measured, privateBytes.Status);
        Assert.Equal(3000, peak.Details!["tiles"]); // 500 pages x 6 tiles
        Assert.InRange(peak.Median!.Value, 1, 4096);
        Assert.InRange(privateBytes.Median!.Value, 1, 4096);
        Assert.True(peak.Details["working_set_end_mb"] <= peak.Median + 0.001, "the peak is at least what is resident at the end");
        Assert.True(peak.Details["scroll_seconds"] > 0);
    }

    [Fact]
    public async Task TheEngineIsKilledFromOutsideAndTheDocumentComesBackInAReplacementEngine()
    {
        var results = await RecoveryScenario.RunAsync(Context, CancellationToken.None);
        var wall = results.Single(r => r.Id == MetricCatalog.CrashToTile.Id);
        var supervisor = results.Single(r => r.Id == MetricCatalog.SupervisorRestore.Id);
        Assert.Equal(MetricStatus.Measured, wall.Status);
        Assert.Equal(MetricStatus.Measured, supervisor.Status);
        Assert.Equal(PerfProfile.Quick.Iterations, wall.N);
        Assert.InRange(wall.Median!.Value, 1, 30_000);
        Assert.True(supervisor.Median <= wall.Median + 50, "the supervisor's restore is part of the time to the first tile");
    }

    [Fact]
    public async Task TheRenderMetricsCoverOneHundredTwentyTilesAPass()
    {
        var results = await RenderScenario.RunAsync(Context, CancellationToken.None);
        var latency = results.Single(r => r.Id == MetricCatalog.TileLatency.Id);
        var throughput = results.Single(r => r.Id == MetricCatalog.TileThroughput.Id);
        Assert.Equal(MetricStatus.Measured, latency.Status);
        Assert.Equal(120, latency.Details!["tiles_per_pass"]);
        Assert.True(latency.Details["tile_p95_ms"] >= latency.Median);
        Assert.True(throughput.Median > 0);
        Assert.False(throughput.LowerIsBetter);
        Assert.Equal("tiles/s", throughput.Unit);
    }

    [Fact]
    public async Task APackageIsMeasuredByItsUnpackedAndItsFileSizeAndNoPackageIsSkippedWithAReason()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "MikePDF_1.0.0.0_x64.msix");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                await Add(archive, "MikePDF.exe", 3 * 1024 * 1024);
                await Add(archive, "Engine/pdfium.dll", 5 * 1024 * 1024);
                await Add(archive, "AppxManifest.xml", 1024);
            }

            var (fileBytes, unpackedBytes, entries) = PackageScenario.Measure(path);
            Assert.Equal(new FileInfo(path).Length, fileBytes);
            Assert.Equal((8L * 1024 * 1024) + 1024, unpackedBytes);
            Assert.Equal(3, entries);

            var context = WithPackage(path);
            var measured = PackageScenario.Run(context);
            var size = measured.Single(m => m.Id == MetricCatalog.InstallSize.Id);
            Assert.Equal(MetricStatus.Measured, size.Status);
            Assert.Equal(8.001, size.Median!.Value, 3);
            Assert.True(measured.Single(m => m.Id == MetricCatalog.MsixFileSize.Id).Median < size.Median); // the zip is smaller than what is in it (these entries are zeros)

            var none = PackageScenario.Run(WithPackage(Path.Combine(directory, "missing.msix")));
            Assert.All(none, m => Assert.Equal(MetricStatus.Skipped, m.Status));
            Assert.All(none, m => Assert.Contains("package.ps1", m.Reason, StringComparison.Ordinal));

            File.WriteAllText(path, "not a zip");
            Assert.All(PackageScenario.Run(context), m => Assert.Equal(MetricStatus.Failed, m.Status));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static async Task Add(ZipArchive archive, string name, int length)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(new byte[length]);
        }
    }
}
