using MikePDF.Render.Cli;
using MikePDF.Render.Corpus;
using MikePDF.Render.Engine;
using MikePDF.Render.Reporting;
using MikePDF.Render.Runner;
using MikePDF.Render.Tests.Support;

namespace MikePDF.Render.Tests;

/// <summary>What the core tier added to the tool: files made to take the engine down, open-rate bars, known issues, nested tiers, and the report's distribution.</summary>
public sealed class CorpusTierTests
{
    private static readonly RunSettings Settings = new() { Reference = true, SsimThreshold = 0.98, SsimPassTarget = 98, Scales = [1.0] };

    private static string ManifestPath => Path.Combine(Locations.RepositoryRoot() ?? throw new DirectoryNotFoundException("MikePDF.slnx not found above the tests"), "tests", "corpus", "manifest.json");

    private static FileReport File(string id, string open = "Ok", ExpectedOutcome? expected = null, IEnumerable<string>? tags = null, params PageReport[] pages)
    {
        var file = new FileReport { Id = id, Path = id, Open = new OpenReport(open, null, 1), PageCount = 1, Expected = expected, Tags = [.. tags ?? []] };
        file.Pages.AddRange(pages);
        return file;
    }

    private static PageReport Page(string status = "Ok", double? ssim = 1.0, string scale = "1") =>
        new() { Page = 1, Scale = double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture), ScaleApplied = 1, Status = status, Ssim = ssim, ReferenceStatus = ssim is null ? "Disabled" : "Ok" };

    private static EngineEvent Down(string reason) => new() { Kind = "crashed", Reason = reason, ExitCode = "0xDEAD", Detail = "gone" };

    private static readonly EngineEvent Restored = new() { Kind = "restored", RestartNumber = 1 };

    private static GateResult Evaluate(SsimBaseline? baseline, params FileReport[] files) => GateEvaluator.Evaluate(files, Settings, baseline);

    private static void AssertFails(GateResult result, string kind)
    {
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Kind == kind);
    }

    // ---- files made to take the engine down ------------------------------------------------------

    [Fact]
    public void AHangTheManifestExpectsPassesWhenTheEngineWasKilledAsHungAndReplaced()
    {
        var file = File("hang.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "hang" }, pages: Page("Timeout", null));
        file.EngineEvents.AddRange([Down("Hung"), Restored]);
        var result = Evaluate(null, file);
        Assert.True(result.Passed, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.Contains(result.Warnings, w => w.Kind == "Timeout");
    }

    [Fact]
    public void ACrashTheManifestExpectsPassesWhenTheEngineDiedAndWasReplaced()
    {
        var file = File("crash.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "crash" }, pages: Page("EngineCrashed", null));
        file.EngineEvents.AddRange([Down("MemoryLimit"), Restored]);
        Assert.True(Evaluate(null, file).Passed);
    }

    [Fact]
    public void AFileThatShouldTakeTheEngineDownButDidNotFailsBecauseItNoLongerTestsRecovery()
    {
        var quiet = File("crash.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "crash" }, pages: Page());
        AssertFails(Evaluate(null, quiet), "ExpectedEngineEventMissing");
    }

    [Fact]
    public void ACrashWhereAHangWasExpectedAndAHangWhereACrashWasExpectedAreNotTheSameThing()
    {
        var crashedInstead = File("hang.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "hang" }, pages: Page("EngineCrashed", null));
        crashedInstead.EngineEvents.AddRange([Down("MemoryLimit"), Restored]);
        AssertFails(Evaluate(null, crashedInstead), "ExpectedEngineEventMissing");

        var hungInstead = File("crash.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "crash" }, pages: Page("Timeout", null));
        hungInstead.EngineEvents.AddRange([Down("Hung"), Restored]);
        AssertFails(Evaluate(null, hungInstead), "ExpectedEngineEventMissing");
    }

    [Fact]
    public void AnExpectedCrashWithNoReplacementAfterItFailsAsRecoveryThatDidNotHappen()
    {
        var file = File("crash.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "crash" }, pages: Page("EngineCrashed", null));
        file.EngineEvents.Add(Down("MemoryLimit"));
        AssertFails(Evaluate(null, file), "EngineNotRestored");

        // A replacement that came before the crash does not count.
        var before = File("crash.pdf", expected: new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = "crash" }, pages: Page("EngineCrashed", null));
        before.EngineEvents.AddRange([Restored, Down("MemoryLimit")]);
        AssertFails(Evaluate(null, before), "EngineNotRestored");
    }

    [Fact]
    public void ADamagedPageIsExpectedOnlyWhereTheManifestSaysSo()
    {
        var broken = File("broken-pages.pdf", expected: new ExpectedOutcome { AllowsDamagedPages = true }, pages: [Page(), Page("Damaged", null, "2")]);
        var result = Evaluate(null, broken);
        Assert.True(result.Passed, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.Contains(result.Warnings, w => w.Kind == "PageDamaged");
        AssertFails(Evaluate(null, File("other.pdf", pages: Page("Damaged", null))), "RenderFailed");
        // Only Damaged is excused: a page that did not render for another reason still fails.
        AssertFails(Evaluate(null, File("broken-pages.pdf", expected: new ExpectedOutcome { AllowsDamagedPages = true }, pages: Page("InternalError", null))), "RenderFailed");
    }

    [Fact]
    public void ATimeoutOnAFileThatIsNotAllowedToFailIsStillAFailure()
    {
        AssertFails(Evaluate(null, File("slow.pdf", pages: Page("Timeout", null))), "Timeout");
    }

    // ---- the open rate ---------------------------------------------------------------------------

    [Fact]
    public void ARealFileThatIsKnownNotToOpenCountsAgainstTheOpenRateWhileAFileBrokenOnPurposeDoesNot()
    {
        var files = Enumerable.Range(0, 198).Select(i => File($"ok{i}.pdf", pages: Page())).ToList();
        files.Add(File("known.pdf", "Damaged", new ExpectedOutcome { Status = "Damaged", KnownIssue = "PDFium cannot parse the cross-reference stream" }));
        files.Add(File("broken-on-purpose.pdf", "Damaged", new ExpectedOutcome { Status = "Damaged" }));
        var summary = Summarizer.Summarize(files, Settings);

        Assert.Equal(199, summary.FilesExpectedToOpen); // 198 plus the known issue; the file broken on purpose is not one that should open
        Assert.Equal(198, summary.FilesOpened);
        Assert.Equal(1, summary.FilesKnownIssue);
        Assert.Equal(1, summary.FilesExpectedNotToOpen);
        Assert.Equal(99.497, summary.OpenSuccessPercent);
        // 99.497% is under the 99.5% of 0.3 and over the 99% of M2: the gate enforces the first, and the report shows both.
        var result = GateEvaluator.Evaluate(files, Settings, null);
        AssertFails(result, "OpenSuccessBelowTarget");
        Assert.DoesNotContain(result.Failures, f => f.Kind == "UnexpectedOpenStatus");
    }

    [Fact]
    public void ADropInTheOpenRateBelowTheBaselineFailsEvenWhenTheTargetIsStillMet()
    {
        var files = Enumerable.Range(0, 1000).Select(i => File($"ok{i}.pdf", i == 0 ? "Damaged" : "Ok", i == 0 ? new ExpectedOutcome { Status = "Damaged", KnownIssue = "known" } : null, pages: i == 0 ? [] : [Page()])).ToList();
        var baseline = new SsimBaseline { OpenSuccessPercent = 100 };
        Assert.True(Summarizer.Summarize(files, Settings).OpenSuccessPercent >= GateEvaluator.OpenSuccessTarget);
        AssertFails(GateEvaluator.Evaluate(files, Settings, baseline), "OpenSuccessRegression");
        Assert.DoesNotContain(GateEvaluator.Evaluate(files, Settings, new SsimBaseline { OpenSuccessPercent = 99.9 }).Failures, f => f.Kind == "OpenSuccessRegression");
    }

    [Fact]
    public void ABaselineRecordsTheOpenRateItWasWrittenAt()
    {
        var files = new List<FileReport> { File("a.pdf", pages: Page()), File("b.pdf", "Damaged", new ExpectedOutcome { Status = "Damaged", KnownIssue = "x" }) };
        var baseline = SsimBaseline.FromReport(files, Settings, "x64", null);
        Assert.Equal(50, baseline.OpenSuccessPercent);
    }

    // ---- the report ------------------------------------------------------------------------------

    [Fact]
    public void TheSsimDistributionSumsToEveryComparedPageAndThePercentilesAreOnTheScores()
    {
        double[] scores = [1.0, 1.0, 1.0, 0.9995, 0.995, 0.985, 0.97, 0.93, 0.5, 0.999];
        var file = File("a.pdf", pages: [.. scores.Select(s => Page(ssim: s))]);
        var summary = Summarizer.Summarize([file], Settings);

        Assert.Equal(scores.Length, summary.SsimDistribution.Sum(b => b.Pages));
        Assert.Equal([1, 1, 1, 1, 1, 5], summary.SsimDistribution.Select(b => b.Pages)); // [0,.9) .5; [.9,.95) .93; [.95,.98) .97; [.98,.99) .985; [.99,.999) .995; the rest
        Assert.Equal(0.5, summary.SsimMin);
        Assert.Equal(0.5, summary.SsimP01);
        Assert.Equal(0.5, summary.SsimP05); // with ten pages the 5th percentile is the lowest score
        Assert.Equal(0.995, summary.SsimMedian);
    }

    [Fact]
    public void TheMarkdownReportStatesBothOpenBarsTheDistributionAndEachTag()
    {
        var files = new[] { File("a.pdf", tags: ["scanned"], pages: Page()), File("b.pdf", tags: ["scanned", "cjk"], pages: Page(ssim: 0.97)) };
        var report = new RunReport
        {
            Tool = RenderRun.ToolInfo(),
            StartedUtc = DateTimeOffset.UnixEpoch,
            Machine = new MachineInfo("os", "x64", "net", null),
            Settings = Settings,
            Files = files,
            Summary = Summarizer.Summarize(files, Settings),
            Gate = GateEvaluator.Evaluate(files, Settings, null),
        };
        var text = MarkdownReport.Write(report);

        Assert.Contains("§0.3: files that should open", text, StringComparison.Ordinal);
        Assert.Contains("M2 exit gate", text, StringComparison.Ordinal);
        Assert.Contains("## SSIM distribution", text, StringComparison.Ordinal);
        Assert.Contains("| scanned | 2 | 2 | 2 |", text, StringComparison.Ordinal);
        Assert.Contains("| cjk | 1 | 1 | 1 |", text, StringComparison.Ordinal);
    }

    // ---- tiers and the manifest check ------------------------------------------------------------

    private static CorpusFile Entry(string id, params string[] tiers) => new()
    {
        Id = id,
        Origin = CorpusOrigin.Fetch,
        Source = "https://example.test/" + id,
        Licence = "BSD-3-Clause",
        Redistributable = true,
        Sha256 = new string('a', 64),
        Tiers = [.. tiers],
        LicenceEvidence = "https://example.test/LICENSE",
    };

    [Fact]
    public void TiersNestSoCoreRunsTheSmokeFilesToo()
    {
        var manifest = new CorpusManifest { Files = [Entry("s.pdf", "smoke"), Entry("c.pdf", "core"), Entry("f.pdf", "full")] };
        Assert.Equal(["s.pdf"], CorpusStore.InTier(manifest, "smoke").Select(f => f.Id));
        Assert.Equal(["s.pdf", "c.pdf"], CorpusStore.InTier(manifest, "core").Select(f => f.Id));
        Assert.Equal(["s.pdf", "c.pdf", "f.pdf"], CorpusStore.InTier(manifest, "full").Select(f => f.Id));
        Assert.Empty(CorpusStore.InTier(manifest, "nightly"));
    }

    [Fact]
    public void AFileOutsideTheSmokeTierNeedsItsLicenceEvidence()
    {
        var withoutEvidence = Entry("c.pdf", "core") with { LicenceEvidence = null };
        Assert.Contains(ManifestValidator.Validate(new CorpusManifest { Files = [withoutEvidence] }), p => p.Contains("licenceEvidence", StringComparison.Ordinal));
        Assert.DoesNotContain(ManifestValidator.Validate(new CorpusManifest { Files = [Entry("c.pdf", "core")] }), p => p.Contains("licenceEvidence", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEngineEventAndKnownIssueEntriesAreChecked()
    {
        var odd = Entry("a.pdf", "core") with { Expect = new CorpusExpect { EngineEvent = "explode" } };
        var closed = Entry("b.pdf", "core") with { Expect = new CorpusExpect { EngineEvent = "hang", Opens = false, Status = "Damaged" } };
        var issue = Entry("c.pdf", "core") with { Expect = new CorpusExpect { KnownIssue = "why" } };
        var deadline = Entry("d.pdf", "core") with { Render = new CorpusRender { TileDeadlineMs = 0 } };
        var problems = ManifestValidator.Validate(new CorpusManifest { Files = [odd, closed, issue, deadline] });
        string[] expected = ["'crash' or 'hang'", "must open", "knownIssue is for a file that should open and does not", "tileDeadlineMs must be positive"];
        foreach (var part in expected)
        {
            Assert.Contains(problems, p => p.Contains(part, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheCheckedInSmokeTierHasAFileThatCrashesAndAFileThatHangsTheEngineAndTheCoreTierKeepsThem()
    {
        var manifest = CorpusManifest.Load(ManifestPath);
        var smoke = manifest.Files.Where(f => f.Tiers.Contains("smoke")).ToList();
        var crash = Assert.Single(smoke, f => f.Expect.EngineEvent == "crash");
        var hang = Assert.Single(smoke, f => f.Expect.EngineEvent == "hang");
        Assert.All([crash, hang], f =>
        {
            Assert.Equal(CorpusOrigin.Generated, f.Origin); // made by us, so there is no third party's file to licence
            Assert.Equal("Apache-2.0", f.Licence);
            Assert.False(f.Render?.Reference ?? true); // PDFium outside the sandbox would spin or die the same way
        });
        Assert.InRange(hang.Render?.TileDeadlineMs ?? int.MaxValue, 1, 2000);
        Assert.Contains(CorpusStore.InTier(manifest, "core"), f => f.Id == crash.Id);
    }

    [Fact]
    public void TheNightlyWorkflowRendersTheCoreTierOnBothArchitecturesWithACacheAndUploadsTheReport()
    {
        var text = System.IO.File.ReadAllText(Path.Combine(Locations.RepositoryRoot()!, ".github", "workflows", "nightly-corpus.yml"));
        foreach (var needed in new[] { "schedule:", "workflow_dispatch:", "windows-2025-vs2026", "windows-11-vs2026-arm", "actions/cache/restore@", "actions/cache/save@", "corpus fetch --tier", "--tier $env:TIER", "actions/upload-artifact@", "baseline-candidate.json" })
        {
            Assert.Contains(needed, text, StringComparison.Ordinal);
        }

        // Every third-party action is pinned to a commit, as ci.yml does.
        foreach (var line in text.Split('\n').Where(l => l.TrimStart().StartsWith("- uses:", StringComparison.Ordinal) || l.TrimStart().StartsWith("uses:", StringComparison.Ordinal)))
        {
            Assert.Matches("@[0-9a-f]{40}", line);
        }
    }

    // ---- against the real engine -----------------------------------------------------------------

    [Fact]
    public async Task TheCrashFileAndTheHangFileTakeTheRealEngineDownAndTheNextFileStillRenders()
    {
        Requires.Engine();
        var manifest = CorpusManifest.Load(ManifestPath);
        await using var h = Harness.Create("recovery", TimeSpan.FromSeconds(2)); // the tool's own default grace
        var items = new List<WorkItem>();
        foreach (var entry in manifest.Files.Where(f => f.Expect.EngineEvent is not null).OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            var path = h.Write(Path.GetFileName(entry.Id), CorpusGenerators.Generate(entry.Generator!));
            items.Add(new WorkItem(
                entry.Id,
                path,
                null,
                new ExpectedOutcome { AllowsEngineCrash = true, EngineEvent = entry.Expect.EngineEvent },
                PageSpec.Parse(entry.Render!.Pages!),
                entry.Tags,
                TimeSpan.FromMilliseconds(entry.Render.TileDeadlineMs ?? 30_000),
                entry.Render.Reference != false));
        }

        items.Add(Harness.Item(LiveEngine.Fixture("rectangles.pdf")));
        var options = h.Options("--scale", "1", "--retries", "1", "--no-reference");
        var files = await h.RunAsync(options, items, null).WaitAsync(TimeSpan.FromMinutes(3));

        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.True(gate.Passed, string.Join("; ", gate.Failures.Select(f => $"{f.Kind} {f.File}: {f.Message}")));
        Assert.Contains(files.Single(f => f.Id.Contains("crash", StringComparison.Ordinal)).EngineEvents, e => e.Kind == "crashed" && e.Reason == "MemoryLimit");
        Assert.Contains(files.Single(f => f.Id.Contains("hang", StringComparison.Ordinal)).EngineEvents, e => e.Kind == "crashed" && e.Reason == "Hung");
        Assert.Equal("Ok", Assert.Single(files[^1].Pages).Status); // the file after them rendered in the replacement engine
    }
}
