using System.Text.Json;
using MikePDF.Render.Corpus;
using MikePDF.Render.Runner;
using MikePDF.Render.Tests.Support;

namespace MikePDF.Render.Tests;

/// <summary>The command line as a user and CI meet it: arguments in, exit code, report files and messages out.</summary>
public sealed class CommandTests
{
    private static async Task<(int Code, string Out, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await RenderCommand.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<(int Code, string Out, string Error)> CorpusAsync(HttpClient? http, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CorpusCommand.RunAsync(args, output, error, http, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task HelpExplainsTheExitCodesAndExitsZero()
    {
        var (code, output, _) = await RunAsync("--help");
        Assert.Equal(RenderCommand.Passed, code);
        Assert.Contains("Exit code: 0 the gate passed, 1 it failed, 2 the run could not start", output, StringComparison.Ordinal);
        Assert.Contains("--tier", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "Nothing to render")]
    [InlineData(new[] { "--bogus", "x.pdf" }, "Unknown option --bogus")]
    [InlineData(new[] { "x.pdf", "--scale", "0" }, "--scale wants a number")]
    [InlineData(new[] { "x.pdf", "--pages", "0" }, "not a page number")]
    public async Task ABadCommandLineIsExitCodeTwoWithAMessageAndNothingIsStarted(string[] args, string message)
    {
        var (code, output, error) = await RunAsync(args);
        Assert.Equal(RenderCommand.CouldNotRun, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Empty(output);
    }

    [Fact]
    public async Task AFileThatIsNotThereIsExitCodeTwo()
    {
        var (code, _, error) = await RunAsync(Path.Combine(Path.GetTempPath(), "mikepdf-render-no-such-file.pdf"));
        Assert.Equal(RenderCommand.CouldNotRun, code);
        Assert.Contains("is not a file or a folder", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderOfPdfsIsRenderedAndTheReportFilesAreWritten()
    {
        Requires.Engine();
        var scratch = LiveEngine.NewScratchDirectory("cli");
        try
        {
            var (code, output, error) = await RunAsync(
                Path.GetDirectoryName(LiveEngine.Fixture("hello_world.pdf"))!, "--password", "5678", "--scale", "1,2", "--out", Path.Combine(scratch, "out"), "--work-dir", Path.Combine(scratch, "work"), "--png");

            Assert.Equal((RenderCommand.Passed, ""), (code, error));
            Assert.Contains("Gate: PASSED", output, StringComparison.Ordinal);
            Assert.Contains("12 pages: 12 Ok", output, StringComparison.Ordinal);
            Assert.Contains("SSIM: 12 of 12 compared pages at or above 0.98", output, StringComparison.Ordinal);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "out", "report.json")));
            Assert.True(json.RootElement.GetProperty("gate").GetProperty("passed").GetBoolean());
            Assert.Equal(5, json.RootElement.GetProperty("summary").GetProperty("files").GetInt32());
            Assert.Equal(1.0, json.RootElement.GetProperty("summary").GetProperty("ssimMean").GetDouble());
            Assert.Contains("**Gate: PASSED**", await File.ReadAllTextAsync(Path.Combine(scratch, "out", "report.md")), StringComparison.Ordinal);
            Assert.Equal(12, Directory.GetFiles(Path.Combine(scratch, "out", "png"), "*.png", SearchOption.AllDirectories).Length);
            Assert.False(Directory.Exists(Path.Combine(scratch, "out", "diff"))); // nothing failed, so no failure images
            Assert.Empty(Directory.GetDirectories(Path.Combine(scratch, "work"), "engine-*")); // the deployed engine copy was removed
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }

    [Fact]
    public async Task AFailedGateIsExitCodeOneAndNoGateTurnsThatOff()
    {
        Requires.Engine();
        var scratch = LiveEngine.NewScratchDirectory("cli-fail");
        try
        {
            var garbage = Path.Combine(scratch, "in", "garbage.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(garbage)!);
            await File.WriteAllBytesAsync(garbage, CorpusGenerators.Generate("garbage-with-header"));
            string[] common = [Path.GetDirectoryName(garbage)!, "--no-reference", "--out", Path.Combine(scratch, "out"), "--work-dir", Path.Combine(scratch, "work")];

            var (code, output, _) = await RunAsync(common);
            Assert.Equal(RenderCommand.GateFailed, code);
            Assert.Contains("FAIL UnexpectedOpenStatus garbage.pdf: Opening ended with Damaged; expected Ok.", output, StringComparison.Ordinal);
            Assert.Contains("Gate: FAILED", output, StringComparison.Ordinal);
            Assert.Contains("garbage.pdf", await File.ReadAllTextAsync(Path.Combine(scratch, "out", "report.md")), StringComparison.Ordinal);

            var (relaxed, _, _) = await RunAsync([.. common, "--no-gate"]);
            Assert.Equal(RenderCommand.Passed, relaxed);
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }

    [Fact]
    public async Task ASandboxThatCannotBeStartedIsExitCodeTwoNotAWeakerFallback()
    {
        Requires.Engine();
        var scratch = LiveEngine.NewScratchDirectory("cli-noengine");
        try
        {
            var (code, _, error) = await RunAsync(LiveEngine.Fixture("hello_world.pdf"), "--engine-dir", scratch, "--out", Path.Combine(scratch, "out"));
            Assert.Equal(RenderCommand.CouldNotRun, code);
            Assert.Contains("MikePDF.Engine.exe and pdfium.dll were not found", error, StringComparison.Ordinal);
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }

    // ---- a tier, start to finish ----------------------------------------------------------------

    private static string WriteMiniManifest(string directory)
    {
        var files = new[] { ("rotated-pages", "Ok", 5u), ("cropbox-offset", "Ok", 4u), ("truncated", "Damaged", 0u) }.Select(g =>
        {
            var bytes = CorpusGenerators.Generate(g.Item1);
            return new CorpusFile
            {
                Id = $"generated/{g.Item1}.pdf",
                Origin = CorpusOrigin.Generated,
                Source = "generated:" + g.Item1,
                Generator = g.Item1,
                Licence = "Apache-2.0",
                Redistributable = true,
                Sha256 = CorpusStore.Sha256Hex(bytes),
                Bytes = bytes.Length,
                Tags = ["test"],
                Tiers = ["full"],
                LicenceEvidence = "generated by the test",
                Expect = new CorpusExpect { Opens = g.Item2 == "Ok", Status = g.Item2, Pages = g.Item3 == 0 ? null : g.Item3 },
            };
        }).ToList();
        var path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(path, new CorpusManifest { Files = files }.ToJson());
        return path;
    }

    [Fact]
    public async Task ATierIsCheckedFetchedVerifiedAndRenderedFromItsManifest()
    {
        Requires.Engine();
        var scratch = LiveEngine.NewScratchDirectory("cli-tier");
        try
        {
            var manifest = WriteMiniManifest(scratch);
            var corpus = Path.Combine(scratch, "corpus");
            var common = new[] { "--manifest", manifest, "--corpus-dir", corpus, "--tier", "full" };

            Assert.Equal(RenderCommand.Passed, (await CorpusAsync(null, ["check", .. common])).Code);

            // Nothing fetched yet: verify says what is missing, and a render refuses to start on a corpus that does not match its manifest.
            var missing = await CorpusAsync(null, ["verify", .. common]);
            Assert.Equal(RenderCommand.GateFailed, missing.Code);
            Assert.Contains("generated/rotated-pages.pdf: missing", missing.Error, StringComparison.Ordinal);
            var refused = await RunAsync("--tier", "full", "--manifest", manifest, "--corpus-dir", corpus, "--out", Path.Combine(scratch, "out"));
            Assert.Equal(RenderCommand.CouldNotRun, refused.Code);
            Assert.Contains("does not match the manifest", refused.Error, StringComparison.Ordinal);

            var fetched = await CorpusAsync(null, ["fetch", .. common]);
            Assert.Equal(RenderCommand.Passed, fetched.Code);
            Assert.Contains("3 generated", fetched.Out, StringComparison.Ordinal);
            Assert.Equal(RenderCommand.Passed, (await CorpusAsync(null, ["verify", .. common])).Code);

            var run = await RunAsync("--tier", "full", "--manifest", manifest, "--corpus-dir", corpus, "--out", Path.Combine(scratch, "out"), "--work-dir", Path.Combine(scratch, "work"));
            Assert.Equal(RenderCommand.Passed, run.Code);
            Assert.Contains("3 files: 2 of 2 that should open did (100%); 1 are broken on purpose.", run.Out, StringComparison.Ordinal);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "out", "report.json")));
            var verdicts = json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("verdict").GetString());
            Assert.Equal("expected-failure", verdicts["generated/truncated.pdf"]);
            Assert.Equal("pass", verdicts["generated/rotated-pages.pdf"]);
            Assert.Equal("full", json.RootElement.GetProperty("settings").GetProperty("tier").GetString());

            // A file changed on disk is caught before anything is rendered.
            await File.AppendAllTextAsync(Path.Combine(corpus, "files", "generated", "rotated-pages.pdf"), "tampered");
            var tampered = await RunAsync("--tier", "full", "--manifest", manifest, "--corpus-dir", corpus, "--out", Path.Combine(scratch, "out2"));
            Assert.Equal(RenderCommand.CouldNotRun, tampered.Code);
            Assert.Contains("rotated-pages.pdf: expected sha256", tampered.Error, StringComparison.Ordinal);
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }

    [Fact]
    public async Task ABaselineWrittenByARunIsAcceptedByTheNextOne()
    {
        Requires.Engine();
        var scratch = LiveEngine.NewScratchDirectory("cli-baseline");
        try
        {
            var baseline = Path.Combine(scratch, "baseline.json");
            string[] common = [LiveEngine.Fixture("hello_world.pdf"), LiveEngine.Fixture("rectangles.pdf"), "--out", Path.Combine(scratch, "out"), "--work-dir", Path.Combine(scratch, "work")];
            Assert.Equal(RenderCommand.Passed, (await RunAsync([.. common, "--write-baseline", baseline])).Code);

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(baseline));
            Assert.Equal("mikepdf-render-baseline/1", json.RootElement.GetProperty("schema").GetString());
            Assert.Equal(1.0, json.RootElement.GetProperty("entries").GetProperty("hello_world.pdf#1@1").GetDouble());
            Assert.Equal(1.0, json.RootElement.GetProperty("entries").GetProperty("rectangles.pdf#1@1").GetDouble());

            var again = await RunAsync([.. common, "--baseline", baseline]);
            Assert.Equal(RenderCommand.Passed, again.Code);

            // A baseline that cannot be read is exit code 2, not a silent pass.
            await File.WriteAllTextAsync(baseline, "{ not json");
            Assert.Equal(RenderCommand.CouldNotRun, (await RunAsync([.. common, "--baseline", baseline])).Code);
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }

    [Fact]
    public async Task CorpusCheckRefusesAManifestWithAnUnverifiedLicenceAndTheCommandsRefuseBadUsage()
    {
        var scratch = LiveEngine.NewScratchDirectory("cli-check");
        try
        {
            var manifest = WriteMiniManifest(scratch);
            var text = (await File.ReadAllTextAsync(manifest)).Replace("\"Apache-2.0\"", "\"UNVERIFIED\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifest, text);

            var check = await CorpusAsync(null, "check", "--manifest", manifest);
            Assert.Equal(RenderCommand.GateFailed, check.Code);
            Assert.Contains("the licence is not verified", check.Error, StringComparison.Ordinal);
            // fetch will not run on a manifest that fails its check.
            Assert.Equal(RenderCommand.GateFailed, (await CorpusAsync(null, "fetch", "--manifest", manifest, "--tier", "full", "--corpus-dir", Path.Combine(scratch, "c"))).Code);

            Assert.Equal(RenderCommand.CouldNotRun, (await CorpusAsync(null)).Code);
            Assert.Equal(RenderCommand.CouldNotRun, (await CorpusAsync(null, "frobnicate")).Code);
            Assert.Equal(RenderCommand.CouldNotRun, (await CorpusAsync(null, "check", "--manifest", Path.Combine(scratch, "nope.json"))).Code);
            Assert.Equal(RenderCommand.CouldNotRun, (await CorpusAsync(null, "check", "--wat")).Code);
        }
        finally
        {
            LiveEngine.CleanUp(scratch);
        }
    }
}
