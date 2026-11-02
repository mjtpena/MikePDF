using System.Diagnostics;
using MikePDF.Render.Corpus;
using MikePDF.Render.Imaging;
using MikePDF.Render.Reference;
using MikePDF.Render.Reporting;
using MikePDF.Render.Runner;
using MikePDF.Render.Tests.Support;

namespace MikePDF.Render.Tests;

/// <summary>
/// The tool against the real thing: the freshly built MikePDF.Engine.exe in its AppContainer, and the real reference renderer. These are the evidence for
/// the M1 gate sentence "renders ... through the sandboxed engine with visual diffs reported", and for "an engine crash or hang gets recorded, the engine
/// respawns, and the run continues".
/// </summary>
public sealed class LiveRunTests
{
    // Enough translucent layers that a page at 3x takes the engine a second or two (measured on the development machine: about 12 ms a layer at 4x), but not minutes. Kept small on purpose: these tests share the machine with timing-sensitive ones.
    private const int HeavyLayers = 250;

    private static readonly string[] FixtureNames = ["hello_world.pdf", "hello_world_2_pages.pdf", "rectangles.pdf", "js.pdf"];
    private static readonly string[] ExpectedCrashReasons = ["ProcessExited", "PipeClosed"];

    private static string Fixture(string name) => LiveEngine.Fixture(name);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(60), "Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task FixturesRenderThroughTheSandboxedEngineAndAgreeWithThePdfiumReference()
    {
        Requires.Engine();
        await using var h = Harness.Create("fixtures");
        var options = h.Options("--scale", "1,2");
        var items = FixtureNames.Select(n => Harness.Item(Fixture(n))).Append(Harness.Item(Fixture("encrypted.pdf"), password: "5678")).ToList();

        var files = await h.RunAsync(options, items, h.RealReference());

        Assert.Equal(5, files.Count);
        Assert.All(files, f => Assert.Equal("Ok", f.Open.Status));
        var pages = files.SelectMany(f => f.Pages).ToList();
        Assert.Equal(2 * (1 + 2 + 1 + 1 + 1), pages.Count); // every page at both scales
        Assert.All(pages, p =>
        {
            Assert.Equal("Ok", p.Status);
            Assert.Equal("Ok", p.ReferenceStatus);
            Assert.Equal(1.0, p.Ssim); // the same PDFium, in and out of the sandbox: identical pixels
            Assert.Equal(0.0, p.ChangedPercent);
            Assert.Null(p.DiffImage);
        });
        Assert.All(files, f => Assert.True(f.EngineEvents.Count == 0, f.Id + " saw engine events: " + string.Join("; ", f.EngineEvents.Select(e => $"{e.Kind} {e.Reason} {e.ExitCode} {e.Detail}"))));
        Assert.Equal(1, h.Host.SupervisorsStarted);

        var settings = RenderRun.SettingsFor(options, null);
        Assert.True(GateEvaluator.Evaluate(files, settings, null).Passed);

        // Rendering happened in another process: this one has not loaded PDFium.
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Contains("pdfium", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ThePixelsAreRightAndTheColoursAreInThePngInTheRightOrder()
    {
        Requires.Engine();
        await using var h = Harness.Create("pixels");
        var options = h.Options("--png", "--no-reference");
        var files = await h.RunAsync(options, [Harness.Item(Fixture("rectangles.pdf"))], null);

        var page = Assert.Single(files).Pages.Single();
        Assert.NotNull(page.Png);
        var image = Png.Load(Path.Combine(h.Out, page.Png!));
        Assert.Equal((200, 300), (image.Width, image.Height));

        // rectangles.pdf: black top left, blue top right, green bottom right, red bottom left (the integration tests' ground truth, as BGRA).
        byte[] At(int x, int y) => [.. image.Pixels.AsSpan((((y * image.Width) + x) * 4), 3)];
        Assert.Equal(new byte[] { 0, 0, 0 }, At(5, 5));
        Assert.Equal(new byte[] { 255, 0, 0 }, At(195, 5));
        Assert.Equal(new byte[] { 0, 255, 0 }, At(195, 295));
        Assert.Equal(new byte[] { 0, 0, 255 }, At(5, 295));
        Assert.Equal(new byte[] { 255, 255, 255 }, At(100, 5));
    }

    [Fact]
    public async Task APageOfSeveralTilesIsStitchedWithoutSeams()
    {
        Requires.Engine();
        await using var h = Harness.Create("tiles");
        var files = await h.RunAsync(h.Options("--scale", "6"), [Harness.Item(Fixture("rectangles.pdf"))], h.RealReference()); // 1200 x 1800: two tile columns, two tile rows

        var page = Assert.Single(files).Pages.Single();
        Assert.Equal((1200, 1800, 4), (page.Width, page.Height, page.Tiles));
        Assert.Equal("Ok", page.Status);
        Assert.Equal(1.0, page.Ssim); // one whole-page reference render: any seam or misplaced tile would show
    }

    [Fact]
    public async Task PagesScalesAndRotationAreHonouredAndReportedBeforeAnythingIsSkipped()
    {
        Requires.Engine();
        await using var h = Harness.Create("select");
        var options = h.Options("--scale", "1,2", "--rotation", "90", "--pages", "2,9");
        var files = await h.RunAsync(options, [Harness.Item(Fixture("hello_world_2_pages.pdf"), pages: "2,9")], h.RealReference());

        var file = Assert.Single(files);
        var rendered = file.Pages.Where(p => p.Status == "Ok").OrderBy(p => p.Scale).ToList();
        Assert.Equal([(2, 1.0, 200, 200), (2, 2.0, 400, 400)], rendered.Select(p => (p.Page, p.Scale, p.Width, p.Height)));
        Assert.All(rendered, p => Assert.Equal(1.0, p.Ssim));
        var outside = Assert.Single(file.Pages, p => p.Status == RenderStatus.OutOfRange);
        Assert.Equal(9, outside.Page);

        // A quarter turn swaps the sides of a page that is not square.
        var turned = await h.RunAsync(h.Options("--rotation", "90"), [Harness.Item(Fixture("rectangles.pdf"))], h.RealReference());
        var page = Assert.Single(turned).Pages.Single();
        Assert.Equal((300, 200), (page.Width, page.Height));
        Assert.Equal(1.0, page.Ssim);
    }

    [Fact]
    public async Task ABigDocumentIsOpenedAndOnlyTheRequestedPagesAreRendered()
    {
        Requires.Engine();
        await using var h = Harness.Create("bigdoc");
        var path = h.Write("pages-10000.pdf", CorpusGenerators.Generate("pages-10000"));
        var files = await h.RunAsync(h.Options(), [Harness.Item(path, pages: "1-3,5000,10000")], h.RealReference());

        var file = Assert.Single(files);
        Assert.Equal(10_000u, file.PageCount);
        Assert.Equal([1, 2, 3, 5000, 10000], file.Pages.Select(p => p.Page));
        Assert.All(file.Pages, p => Assert.Equal(("Ok", 1.0), (p.Status, p.Ssim)));
    }

    [Fact]
    public async Task APageBeyondThePixelBudgetIsRenderedSmallerAndStillMatchesTheReference()
    {
        Requires.Engine();
        await using var h = Harness.Create("huge");
        var path = h.Write("huge-mediabox.pdf", CorpusGenerators.Generate("huge-mediabox"));
        var files = await h.RunAsync(h.Options("--max-pixels", "4000000"), [Harness.Item(path)], h.RealReference());

        var page = Assert.Single(files).Pages.Single();
        Assert.Equal("Ok", page.Status);
        Assert.True(page.ScaleApplied < page.Scale);
        Assert.True((long)page.Width * page.Height <= 4_000_000);
        Assert.Equal(1.0, page.Ssim);
    }

    // ---- hostile files --------------------------------------------------------------------------

    [Fact]
    public async Task BrokenFilesAreReportedWithTheErrorModelAndTheRunGoesOnInTheSameEngine()
    {
        Requires.Engine();
        await using var h = Harness.Create("hostile");
        var garbage = h.Write("garbage.pdf", CorpusGenerators.Generate("garbage-with-header"));
        var truncated = h.Write("truncated.pdf", CorpusGenerators.Generate("truncated"));
        var empty = h.Write("empty.pdf", []);
        var options = h.Options();
        var damaged = new ExpectedOutcome { Status = "Damaged" };
        var supervisor = await h.Host.GetSupervisorAsync(default);
        var engine = supervisor.EngineProcessId;

        var files = await h.RunAsync(options, [Harness.Item(garbage, expected: damaged), Harness.Item(truncated, expected: damaged), Harness.Item(empty, expected: damaged), Harness.Item(Fixture("hello_world.pdf"))], h.RealReference());

        Assert.Equal(["Damaged", "Damaged", "Damaged", "Ok"], files.Select(f => f.Open.Status));
        Assert.Equal("Ok", files[3].Pages.Single().Status);
        Assert.Equal(1.0, files[3].Pages.Single().Ssim);
        Assert.All(files, f => Assert.True(f.EngineEvents.Count == 0, f.Id + " saw engine events: " + string.Join("; ", f.EngineEvents.Select(e => $"{e.Kind} {e.Reason} {e.ExitCode} {e.Detail}"))));
        Assert.Equal(engine, (await h.Host.GetSupervisorAsync(default)).EngineProcessId); // never restarted: a damaged file is an answer, not a crash
        Assert.True(GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null).Passed);

        // The same files, not expected to fail, fail the gate and say why.
        var unexpected = await h.RunAsync(options, [Harness.Item(garbage)], h.RealReference());
        var gate = GateEvaluator.Evaluate(unexpected, RenderRun.SettingsFor(options, null), null);
        Assert.False(gate.Passed);
        Assert.Contains(gate.Failures, f => f.Kind == "UnexpectedOpenStatus" && f.File == "garbage.pdf");
    }

    [Fact]
    public async Task AnEngineKilledUnderAPageIsRecordedTheDocumentComesBackAndTheRunGoesOn()
    {
        Requires.Engine();
        await using var h = Harness.Create("kill");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(HeavyLayers));
        var options = h.Options("--scale", "3", "--no-reference", "--retries", "1");
        var first = (await h.Host.GetSupervisorAsync(default)).EngineProcessId;

        var run = h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null);
        await WaitUntilAsync(() => h.Host.Context == "page 1@3", "the engine to be drawing the heavy page");
        await Task.Delay(150);
        Process.GetProcessById((int)first).Kill();
        var files = await run.WaitAsync(TimeSpan.FromMinutes(3));

        // The engine died under the page: that is on the record, with where and why ...
        var crash = Assert.Single(files[0].EngineEvents, e => e.Kind == "crashed");
        Assert.Equal("page 1@3", crash.During);
        Assert.Contains(crash.Reason, ExpectedCrashReasons);
        var restored = Assert.Single(files[0].EngineEvents, e => e.Kind == "restored");
        Assert.Equal(1, restored.DocumentsRestored);
        Assert.Equal(0, restored.DocumentsLost);
        // ... the page was drawn again by the new engine ...
        var page = Assert.Single(files[0].Pages);
        Assert.Equal(("Ok", 1), (page.Status, page.Retries));
        // ... and the run carried on with the next file in it.
        Assert.Equal("Ok", Assert.Single(files[1].Pages).Status);
        Assert.NotEqual(first, (await h.Host.GetSupervisorAsync(default)).EngineProcessId);

        // The gate still fails on it, unless the manifest says this file is known to do that.
        var settings = RenderRun.SettingsFor(options, null);
        var gate = GateEvaluator.Evaluate(files, settings, null);
        Assert.False(gate.Passed);
        Assert.Contains(gate.Failures, f => f.Kind == "EngineCrash" && f.File == "heavy.pdf");
        files[0] = Clone(files[0], new ExpectedOutcome { AllowsEngineCrash = true });
        Assert.True(GateEvaluator.Evaluate(files, settings, null).Passed);
    }

    [Fact]
    public async Task APageThatKeepsKillingTheEngineIsGivenUpOnAfterItsRetriesAndTheNextFileStillRenders()
    {
        Requires.Engine();
        await using var h = Harness.Create("poison");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(HeavyLayers));
        var options = h.Options("--scale", "3", "--no-reference", "--retries", "1");
        var supervisor = await h.Host.GetSupervisorAsync(default);

        using var killer = new CancellationTokenSource();
        var killing = Task.Run(async () =>
        {
            // Kill every engine that starts drawing the heavy page, until told to stop.
            var killed = new HashSet<uint>();
            while (!killer.IsCancellationRequested)
            {
                var pid = supervisor.EngineProcessId;
                if (killed.Count < 2 && h.Host.Context == "page 1@3" && pid != 0 && killed.Add(pid))
                {
                    await Task.Delay(150);
                    try
                    {
                        Process.GetProcessById((int)pid).Kill();
                    }
                    catch (ArgumentException)
                    {
                        // Already gone.
                    }
                }

                await Task.Delay(10);
            }
        });

        var files = await h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null).WaitAsync(TimeSpan.FromMinutes(3));
        await killer.CancelAsync();
        await killing;

        var page = Assert.Single(files[0].Pages);
        Assert.Equal(("EngineCrashed", 1), (page.Status, page.Retries)); // one attempt, one retry, both killed
        Assert.True(files[0].EngineEvents.Count(e => e.Kind == "crashed") >= 2);
        Assert.Equal("Ok", Assert.Single(files[1].Pages).Status);
        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.Contains(gate.Failures, f => f.Kind == "RenderFailed" && f.File == "heavy.pdf");
    }

    [Fact]
    public async Task ARenderPastItsDeadlineIsATimeoutNotAHangAndTheRunGoesOn()
    {
        Requires.Engine();
        await using var h = Harness.Create("deadline");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(HeavyLayers));
        var options = h.Options("--scale", "3", "--no-reference", "--tile-deadline-ms", "50");
        var engine = (await h.Host.GetSupervisorAsync(default)).EngineProcessId;

        var files = await h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null).WaitAsync(TimeSpan.FromMinutes(2));

        var page = Assert.Single(files[0].Pages);
        Assert.Equal("Timeout", page.Status);
        Assert.Empty(files[0].EngineEvents); // the engine honoured the deadline itself: it is alive and was not restarted
        Assert.Equal(engine, (await h.Host.GetSupervisorAsync(default)).EngineProcessId);
        Assert.Equal("Ok", Assert.Single(files[1].Pages).Status);
        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.Contains(gate.Failures, f => f.Kind == "Timeout" && f.File == "heavy.pdf");
    }

    [Fact]
    public async Task AFileThatOutlivesItsTimeLimitIsCutOffAndTheNextOneRenders()
    {
        Requires.Engine();
        await using var h = Harness.Create("filelimit");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(60_000));
        var options = h.Options("--scale", "3", "--no-reference", "--file-timeout-s", "2", "--tile-deadline-ms", "600000");

        var clock = Stopwatch.StartNew();
        var files = await h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null).WaitAsync(TimeSpan.FromMinutes(2));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"the cut-off took {clock.Elapsed}");
        Assert.Equal(RenderStatus.FileTimeout, Assert.Single(files[0].Pages).Status);
        Assert.Contains(files[0].Problems, p => p.Contains("did not finish within 2 s", StringComparison.Ordinal));
        Assert.Equal("Ok", Assert.Single(files[1].Pages).Status);
        Assert.Contains(GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null).Failures, f => f.Kind == "RenderFailed" && f.File == "heavy.pdf");
    }

    /// <summary>
    /// The flake: on a busy CI runner the first file's time limit also paid for starting the engine, so a limit of 1 s could run out before the file's first page was even listed
    /// ("Assert.Single() Failure: The collection was empty"). Starting an engine is now slower than the limit by construction, and the file must still get its pages.
    /// </summary>
    [Fact]
    public async Task TheFileTimeLimitDoesNotPayForStartingTheEngine()
    {
        Requires.Engine();
        await using var h = Harness.Create("filelimit-start");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(60_000));
        var options = h.Options("--scale", "3", "--no-reference", "--file-timeout-s", "1", "--tile-deadline-ms", "600000");
        h.Host.BeforeStart = ct => Task.Delay(TimeSpan.FromMilliseconds(2500), ct); // every engine start takes 2.5 s: well over the file limit

        var files = await h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal("Ok", files[0].Open.Status);
        Assert.Equal(RenderStatus.FileTimeout, Assert.Single(files[0].Pages).Status); // cut off while drawing, not before it began
        Assert.Equal("Ok", files[1].Open.Status);
        Assert.Equal("Ok", Assert.Single(files[1].Pages).Status); // and the file after the cut-off got its own engine and its own full second
        Assert.True(h.Host.SupervisorsStarted >= 2, "the engine that was cut off while busy is replaced, not waited for");
    }

    [Fact]
    public async Task StoppingTheRunKeepsWhatWasDoneAndReturnsPromptlyWithoutRenderingTheRest()
    {
        Requires.Engine();
        await using var h = Harness.Create("stop");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(60_000));
        var options = h.Options("--scale", "3", "--no-reference", "--tile-deadline-ms", "600000");
        using var stop = new CancellationTokenSource();

        var run = h.RunAsync(options, [Harness.Item(heavy), Harness.Item(Fixture("rectangles.pdf"))], null, stop.Token);
        await WaitUntilAsync(() => h.Host.Context == "page 1@3", "the engine to be drawing the heavy page");
        await Task.Delay(100);
        var clock = Stopwatch.StartNew();
        await stop.CancelAsync();
        var files = await run.WaitAsync(TimeSpan.FromMinutes(1));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"stopping took {clock.Elapsed}");
        var file = Assert.Single(files); // the second file was never started
        Assert.Equal("Ok", file.Open.Status);
        Assert.Contains(file.Problems, p => p.Contains("stopped", StringComparison.Ordinal));
        Assert.Equal(RenderStatus.Skipped, Assert.Single(file.Pages).Status);
    }

    private sealed class ThrowingReference : IReferenceRenderer
    {
        public Task<ReferenceResult> RenderAsync(string pdfPath, string? password, int rotation, IReadOnlyList<(int Page, float Scale)> jobs, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ABugInTheToolOnOneFileIsRecordedAsAFailureAndTheOtherFilesStillRender()
    {
        Requires.Engine();
        await using var h = Harness.Create("internal");
        var options = h.Options();
        var files = await h.RunAsync(options, [Harness.Item(Fixture("hello_world.pdf")), Harness.Item(Fixture("rectangles.pdf"))], new ThrowingReference());

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Contains(f.Problems, p => p == "The tool failed on this file: InvalidOperationException: boom"));
        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.False(gate.Passed);
        Assert.Equal(2, gate.Failures.Count(f => f.Kind == "FileProblem"));
    }

    [Fact]
    public async Task AFileOfPagesThatAllFailStopsAfterThreeAndSaysSo()
    {
        Requires.Engine();
        await using var h = Harness.Create("giveup");
        var heavy = h.Write("heavy.pdf", Harness.SlowPage(HeavyLayers));
        // One physical page, five jobs: five scales of the same heavy page, each of which times out.
        var options = h.Options("--scale", "3,3.1,3.2,3.3,3.4", "--no-reference", "--tile-deadline-ms", "50");
        var files = await h.RunAsync(options, [Harness.Item(heavy)], null).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(5, files[0].Pages.Count);
        Assert.Equal(["Timeout", "Timeout", "Timeout", RenderStatus.Skipped, RenderStatus.Skipped], files[0].Pages.Select(p => p.Status));
        Assert.Contains("3 pages in a row", files[0].Pages[3].Detail, StringComparison.Ordinal);
    }

    // ---- the reference and the comparison -------------------------------------------------------

    private sealed class FakeReference(Func<ReferenceResult, ReferenceResult> shape, string directory) : IReferenceRenderer
    {
        public Task<ReferenceResult> RenderAsync(string pdfPath, string? password, int rotation, IReadOnlyList<(int Page, float Scale)> jobs, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = new ReferenceResult(directory) { OpenStatus = "Ok", PageCount = 1 };
            return Task.FromResult(shape(result));
        }
    }

    private static void GiveBlankReference(ReferenceResult result, string directory, int width, int height)
    {
        var path = Path.Combine(directory, "job00000.bgra");
        using (var file = File.Create(path))
        {
            file.Write("MBGR"u8);
            file.Write(BitConverter.GetBytes(width));
            file.Write(BitConverter.GetBytes(height));
            file.Write(new byte[width * height * 4].Select(_ => (byte)255).ToArray());
        }

        result.Pages[0] = new ReferencePage("Ok", width, height, 1, "", path);
    }

    [Fact]
    public async Task APageThatDiffersFromTheReferenceFailsTheGateAndLeavesASideBySideDiffImage()
    {
        Requires.Engine();
        await using var h = Harness.Create("differs");
        var referenceDirectory = Path.Combine(h.Scratch, "fake-reference");
        Directory.CreateDirectory(referenceDirectory);
        // The reference "renders" a blank page where the engine draws the four coloured squares: the failure this tool caught in itself while it was written.
        var fake = new FakeReference(r =>
        {
            GiveBlankReference(r, referenceDirectory, 200, 300);
            return r;
        }, referenceDirectory);
        var options = h.Options();
        var files = await h.RunAsync(options, [Harness.Item(Fixture("rectangles.pdf"))], fake);

        var page = Assert.Single(files).Pages.Single();
        Assert.Equal("Ok", page.Status);
        Assert.True(page.Ssim < 0.98, $"SSIM was {page.Ssim}");
        Assert.True(page.ChangedPercent > 0);
        Assert.NotNull(page.DiffImage);
        var picture = Png.Load(Path.Combine(h.Out, page.DiffImage!));
        Assert.Equal((3 * 200) + (2 * 6), picture.Width);
        Assert.Equal(300, picture.Height);

        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.False(gate.Passed);
        Assert.Contains(gate.Failures, f => f.Kind == "SsimBelowThreshold" && f.File == "rectangles.pdf" && f.Page == "1@1");
    }

    [Fact]
    public async Task ADifferentSizedReferenceIsAMismatchWithScoreZero()
    {
        Requires.Engine();
        await using var h = Harness.Create("size");
        var referenceDirectory = Path.Combine(h.Scratch, "fake-reference");
        Directory.CreateDirectory(referenceDirectory);
        var fake = new FakeReference(r =>
        {
            GiveBlankReference(r, referenceDirectory, 201, 300);
            return r;
        }, referenceDirectory);

        var page = Assert.Single(await h.RunAsync(h.Options(), [Harness.Item(Fixture("rectangles.pdf"))], fake)).Pages.Single();
        Assert.Equal(0.0, page.Ssim);
        Assert.Contains("reference is 201 x 300", page.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceThatCrashesLeavesTheEngineResultsInPlaceAndFailsTheGateOnItsOwnAccount()
    {
        Requires.Engine();
        await using var h = Harness.Create("noref");
        var fake = new FakeReference(r =>
        {
            r.Outcome = "Crashed";
            r.Detail = "Exit code 0xC0000005.";
            return r;
        }, Path.Combine(h.Scratch, "fake-reference"));
        var options = h.Options();
        var files = await h.RunAsync(options, [Harness.Item(Fixture("hello_world.pdf")), Harness.Item(Fixture("rectangles.pdf"))], fake);

        Assert.All(files, f => Assert.Equal("Crashed", f.Reference));
        Assert.All(files.SelectMany(f => f.Pages), p => Assert.Equal(("Ok", "Missing", null), (p.Status, p.ReferenceStatus, p.Ssim)));
        var gate = GateEvaluator.Evaluate(files, RenderRun.SettingsFor(options, null), null);
        Assert.Equal(2, gate.Failures.Count(f => f.Kind == "ReferenceCrash"));
        Assert.Contains(gate.Failures, f => f.Kind == "NothingCompared"); // a run that compared nothing does not pass either
    }

    [Fact]
    public async Task TheRealReferenceRendererSurvivesBadInputAndATooShortTimeLimit()
    {
        Requires.Engine();
        await using var h = Harness.Create("refrun");
        var reference = h.RealReference();

        using (var missing = await reference.RenderAsync(Path.Combine(h.Scratch, "no-such.pdf"), null, 0, [(1, 1f)], TimeSpan.FromSeconds(30), default))
        {
            Assert.Equal("Failed", missing.Outcome);
            Assert.Empty(missing.Pages);
        }

        using (var tooSlow = await reference.RenderAsync(Fixture("rectangles.pdf"), null, 0, [(1, 1f)], TimeSpan.FromMilliseconds(1), default))
        {
            Assert.Equal("TimedOut", tooSlow.Outcome);
        }

        using var good = await reference.RenderAsync(Fixture("hello_world_2_pages.pdf"), null, 0, [(1, 1f), (2, 2f), (3, 1f)], TimeSpan.FromSeconds(60), default);
        Assert.Equal(("Ok", "Ok", 2), (good.Outcome, good.OpenStatus, good.PageCount));
        Assert.Equal(("Ok", 200, 200), (good.Pages[0].Status, good.Pages[0].Width, good.Pages[0].Height));
        Assert.Equal(("Ok", 400, 400), (good.Pages[1].Status, good.Pages[1].Width, good.Pages[1].Height));
        Assert.Equal("Skipped", good.Pages[2].Status);
        Assert.NotNull(good.Load(0));
        Assert.Null(good.Load(2));

        using var locked = await reference.RenderAsync(Fixture("encrypted.pdf"), null, 0, [(1, 1f)], TimeSpan.FromSeconds(60), default);
        Assert.Equal("PasswordRequired", locked.OpenStatus);
        using var wrong = await reference.RenderAsync(Fixture("encrypted.pdf"), "nope", 0, [(1, 1f)], TimeSpan.FromSeconds(60), default);
        Assert.Equal("PasswordIncorrect", wrong.OpenStatus);
    }

    [Fact]
    public async Task AFileTheEngineRefusesIsCheckedAgainstTheReferenceToo()
    {
        Requires.Engine();
        await using var h = Harness.Create("refopen");
        var garbage = h.Write("garbage.pdf", CorpusGenerators.Generate("garbage-with-header"));
        var file = Assert.Single(await h.RunAsync(h.Options(), [Harness.Item(garbage, expected: new ExpectedOutcome { Status = "Damaged" })], h.RealReference()));
        Assert.Equal("Damaged", file.Open.Status);
        Assert.Equal("Ok", file.Reference); // PDFium outside the sandbox refuses it as well: nothing is lost in the sandbox

        var good = Assert.Single(await h.RunAsync(h.Options(), [Harness.Item(Fixture("hello_world.pdf"))], h.RealReference()));
        Assert.Equal("Ok", good.Reference);
    }

    private static FileReport Clone(FileReport original, ExpectedOutcome expected)
    {
        var copy = new FileReport { Id = original.Id, Path = original.Path, Expected = expected, Open = original.Open, PageCount = original.PageCount, Reference = original.Reference };
        copy.Pages.AddRange(original.Pages);
        copy.EngineEvents.AddRange(original.EngineEvents);
        return copy;
    }
}
