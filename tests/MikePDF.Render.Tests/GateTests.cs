using MikePDF.Render.Reporting;

namespace MikePDF.Render.Tests;

public sealed class GateTests
{
    private static readonly RunSettings Settings = new() { Reference = true, SsimThreshold = 0.98, SsimPassTarget = 98, Scales = [1.0] };

    private static FileReport File(string id = "a.pdf", string open = "Ok", uint pages = 1, ExpectedOutcome? expected = null, params PageReport[] rendered)
    {
        var file = new FileReport { Id = id, Path = id, Open = new OpenReport(open, null, 5), PageCount = pages, Expected = expected };
        file.Pages.AddRange(rendered);
        return file;
    }

    private static PageReport Page(int page = 1, string status = "Ok", double? ssim = 1.0, string? detail = null) =>
        new() { Page = page, Scale = 1, ScaleApplied = 1, Status = status, Ssim = ssim, Detail = detail, ReferenceStatus = ssim is null ? "Missing" : "Ok" };

    private static EngineEvent Crash(string reason = "ProcessExited", string? during = "page 1@1") =>
        new() { Kind = "crashed", Reason = reason, ExitCode = "0x1", Detail = "gone", During = during };

    private static GateResult Evaluate(IReadOnlyList<FileReport> files, SsimBaseline? baseline = null) => GateEvaluator.Evaluate(files, Settings, baseline);

    private static void AssertFails(GateResult result, string kind)
    {
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Kind == kind);
    }

    [Fact]
    public void AFileThatOpensAndRendersCleanlyPasses()
    {
        var result = Evaluate([File(rendered: Page())]);
        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void AnEngineCrashFailsTheGateEvenWhenTheRunRecoveredFromIt()
    {
        var file = File(rendered: Page());
        file.EngineEvents.Add(Crash());
        file.EngineEvents.Add(new EngineEvent { Kind = "restored", RestartNumber = 1 });
        var result = Evaluate([file]);
        AssertFails(result, "EngineCrash");
        Assert.Equal("a.pdf", result.Failures.Single().File);
    }

    [Fact]
    public void AHangTheWatchdogKilledIsNamedAsAHang()
    {
        var file = File(rendered: Page());
        file.EngineEvents.Add(Crash("Hung"));
        AssertFails(Evaluate([file]), "EngineHang");
    }

    [Fact]
    public void AMemoryLimitKillCountsAsACrash()
    {
        var file = File(rendered: Page());
        file.EngineEvents.Add(Crash("MemoryLimit"));
        AssertFails(Evaluate([file]), "EngineCrash");
    }

    [Fact]
    public void ACrashTheManifestToleratesDoesNotFailTheGate()
    {
        var file = File(expected: new ExpectedOutcome { AllowsEngineCrash = true }, rendered: Page());
        file.EngineEvents.Add(Crash());
        Assert.True(Evaluate([file]).Passed);
    }

    [Fact]
    public void ASupervisorThatGaveUpAlwaysFailsEvenIfCrashesAreTolerated()
    {
        var file = File(expected: new ExpectedOutcome { AllowsEngineCrash = true }, rendered: Page());
        file.EngineEvents.Add(new EngineEvent { Kind = "failed", Reason = "ProcessExited", Detail = "crash loop" });
        AssertFails(Evaluate([file]), "EngineFailed");
    }

    [Fact]
    public void AFileExpectedNotToOpenPassesWhenItDoesNotAndFailsWhenItDoes()
    {
        var hostile = new ExpectedOutcome { Status = "Damaged" };
        Assert.True(Evaluate([File(open: "Damaged", pages: 0, expected: hostile)]).Passed);
        AssertFails(Evaluate([File(open: "Ok", expected: hostile, rendered: Page())]), "UnexpectedOpenStatus");
        AssertFails(Evaluate([File(open: "Damaged", pages: 0)]), "UnexpectedOpenStatus"); // expected Ok by default
        AssertFails(Evaluate([File(open: "PasswordRequired", pages: 0, expected: hostile)]), "UnexpectedOpenStatus");
    }

    [Fact]
    public void AProblemRecordedAgainstAFileFailsTheGate()
    {
        var file = File(rendered: Page());
        file.Problems.Add("The file did not finish within 300 s and was cut off.");
        var result = Evaluate([file]);
        AssertFails(result, "FileProblem");
        Assert.Contains("cut off", result.Failures.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentPageCountThanExpectedFails()
    {
        AssertFails(Evaluate([File(pages: 3, expected: new ExpectedOutcome { Pages = 2 }, rendered: Page())]), "PageCountMismatch");
        Assert.True(Evaluate([File(pages: 2, expected: new ExpectedOutcome { Pages = 2 }, rendered: Page())]).Passed);
    }

    [Theory]
    [InlineData("Timeout", "Timeout")]
    [InlineData("EngineCrashed", "RenderFailed")]
    [InlineData("Damaged", "RenderFailed")]
    [InlineData("InvalidArgument", "RenderFailed")]
    [InlineData("Skipped", "RenderFailed")]
    [InlineData("FileTimeout", "RenderFailed")]
    [InlineData("PartiallyRendered", "PartiallyRendered")]
    public void APageThatDoesNotRenderOkFails(string status, string kind) =>
        AssertFails(Evaluate([File(rendered: Page(status: status, ssim: null))]), kind);

    [Fact]
    public void APartialRenderThatTheManifestAllowsPasses() =>
        Assert.True(Evaluate([File(expected: new ExpectedOutcome { AllowsPartialRender = true }, rendered: Page(status: "PartiallyRendered", ssim: 1.0))]).Passed);

    [Fact]
    public void ASsimBelowTheThresholdFailsUnlessTheBaselineAlreadyKnewIt()
    {
        var files = new[] { File(rendered: Page(ssim: 0.90)) };
        AssertFails(Evaluate(files), "SsimBelowThreshold");

        var known = new SsimBaseline { Entries = { ["a.pdf#1@1"] = 0.90 } };
        var result = Evaluate(files, known);
        Assert.Contains(result.Warnings, w => w.Kind == "SsimKnownLow");
        Assert.DoesNotContain(result.Failures, f => f.Kind == "SsimBelowThreshold");
        // ... but the pass-rate rule still counts it: one page of one is 0%.
        AssertFails(result, "SsimPassRateBelowTarget");
    }

    [Fact]
    public void AKnownLowPageThatGotWorseIsARegression()
    {
        var known = new SsimBaseline { Entries = { ["a.pdf#1@1"] = 0.95 } };
        AssertFails(Evaluate([File(rendered: Page(ssim: 0.90))], known), "SsimRegression");
    }

    [Fact]
    public void APageStillAboveTheBarButClearlyBelowItsBaselineIsARegression()
    {
        var baseline = new SsimBaseline { Entries = { ["a.pdf#1@1"] = 1.0 } };
        AssertFails(Evaluate([File(rendered: Page(ssim: 0.99))], baseline), "SsimRegression");
        Assert.True(Evaluate([File(rendered: Page(ssim: 1.0 - (SsimBaseline.Tolerance / 2)))], baseline).Passed);
    }

    [Fact]
    public void APageNotInTheBaselineIsHeldToTheThreshold()
    {
        var baseline = new SsimBaseline { Entries = { ["other.pdf#1@1"] = 1.0 } };
        Assert.True(Evaluate([File(rendered: Page(ssim: 0.99))], baseline).Passed);
        AssertFails(Evaluate([File(rendered: Page(ssim: 0.97))], baseline), "SsimBelowThreshold");
    }

    [Fact]
    public void LessThan98PercentOfPagesAtTheBarFailsEvenWhenEachIsKnown()
    {
        var pages = Enumerable.Range(1, 100).Select(i => Page(i, ssim: i <= 3 ? 0.90 : 1.0)).ToArray();
        var baseline = new SsimBaseline();
        foreach (var p in pages.Where(p => p.Ssim < 0.98))
        {
            baseline.Entries[$"a.pdf#{p.Page}@1"] = 0.90;
        }

        var result = Evaluate([File(pages: 100, rendered: pages)], baseline);
        Assert.All(result.Failures, f => Assert.Equal("SsimPassRateBelowTarget", f.Kind));
        AssertFails(result, "SsimPassRateBelowTarget"); // 97 of 100

        var twoLow = pages.Select(p => p.Page <= 2 ? p : Page(p.Page, ssim: 1.0)).ToArray();
        Assert.True(Evaluate([File(pages: 100, rendered: twoLow)], baseline).Passed); // 98 of 100 is the target
    }

    [Fact]
    public void FewerThan995PercentOfOpenableFilesOpeningFails()
    {
        var files = Enumerable.Range(0, 100).Select(i => File($"f{i}.pdf", open: i == 0 ? "Damaged" : "Ok", rendered: i == 0 ? [] : [Page()])).ToList();
        var result = Evaluate(files);
        AssertFails(result, "OpenSuccessBelowTarget");
        AssertFails(result, "UnexpectedOpenStatus");
    }

    [Fact]
    public void ReferenceOnWithNothingComparedFails()
    {
        var result = Evaluate([File(rendered: Page(ssim: null))]);
        AssertFails(result, "NothingCompared");
        Assert.True(GateEvaluator.Evaluate([File(rendered: Page(ssim: null))], Settings with { Reference = false }, null).Passed);
    }

    [Theory]
    [InlineData("Crashed", "ReferenceCrash")]
    [InlineData("TimedOut", "ReferenceHang")]
    public void AReferenceRendererThatCrashesOrHangsFailsTheGateLikeTheEngineWould(string outcome, string kind)
    {
        var file = File(rendered: [Page(1, ssim: 1.0), Page(2, ssim: null)]);
        file.Reference = outcome;
        AssertFails(Evaluate([file]), kind);

        // ... unless the manifest says this file is known to bring PDFium down.
        var known = File(expected: new ExpectedOutcome { AllowsEngineCrash = true }, rendered: [Page(1, ssim: 1.0)]);
        known.Reference = outcome;
        Assert.True(Evaluate([known]).Passed);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("OpenedByReference")]
    [InlineData("OpenFailed:Damaged")]
    public void AReferenceThatCouldNotRunOrDisagreesAboutOpeningIsAWarning(string outcome)
    {
        var file = File(rendered: [Page(1, ssim: 1.0), Page(2, ssim: null)]);
        file.Reference = outcome;
        var result = Evaluate([file]);
        Assert.True(result.Passed);
        Assert.Contains(result.Warnings, w => w.Kind == "ReferenceDiffers");
    }

    [Fact]
    public void ASummaryCountsWhatTheGateSaw()
    {
        var crashed = File("c.pdf", rendered: Page());
        crashed.EngineEvents.Add(Crash());
        crashed.EngineEvents.Add(Crash("Hung"));
        var files = new List<FileReport>
        {
            File("a.pdf", rendered: [Page(1, ssim: 1.0), Page(2, ssim: 0.5), Page(3, "Timeout", null)]),
            File("b.pdf", open: "Damaged", pages: 0, expected: new ExpectedOutcome { Status = "Damaged" }),
            crashed,
        };
        var summary = Summarizer.Summarize(files, Settings);
        Assert.Equal(3, summary.Files);
        Assert.Equal(2, summary.FilesExpectedToOpen);
        Assert.Equal(2, summary.FilesOpened);
        Assert.Equal(1, summary.FilesExpectedNotToOpen);
        Assert.Equal(0, summary.UnexpectedOpenOutcomes);
        Assert.Equal(4, summary.PagesAttempted);
        Assert.Equal(3, summary.PagesRendered);
        Assert.Equal(1, summary.PagesFailed);
        Assert.Equal(1, summary.Timeouts);
        Assert.Equal((1, 1), (summary.EngineCrashes, summary.EngineHangs));
        Assert.Equal((3, 2), (summary.SsimCompared, summary.SsimAtOrAboveThreshold));
        Assert.Equal(0.5, summary.SsimMin);
        Assert.Equal(66.667, summary.SsimPassPercent);
    }
}
