using System.Globalization;

namespace MikePDF.Render.Reporting;

/// <summary>
/// The pass/fail decision of a run (the M1 gate, §17, with §0.3's numbers). A run fails on any of:
/// <list type="bullet">
/// <item>a crash of an engine process or a hang (the watchdog killed it, or a request ran past its deadline) that the manifest does not tolerate for that file;</item>
/// <item>a file that opens differently from its expectation, or has another page count;</item>
/// <item>a page that does not render (any status but Ok, and PartiallyRendered unless allowed);</item>
/// <item>a page whose SSIM against the PDFium reference is below the threshold and not already known to be, or that fell more than <see cref="SsimBaseline.Tolerance"/> below its baseline;</item>
/// <item>fewer than <see cref="RunSettings.SsimPassTarget"/> percent of compared pages at or above the threshold, or fewer than 99.5% of the files that should open opening.</item>
/// </list>
/// </summary>
internal static class GateEvaluator
{
    public const double OpenSuccessTarget = 99.5;

    public static GateResult Evaluate(IReadOnlyList<FileReport> files, RunSettings settings, SsimBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        var failures = new List<GateFailure>();
        var warnings = new List<GateFailure>();

        foreach (var file in files)
        {
            CheckOpen(file, failures);
            CheckEngineEvents(file, failures);
            foreach (var page in file.Pages)
            {
                CheckPage(file, page, settings, baseline, failures, warnings);
            }

            foreach (var problem in file.Problems)
            {
                failures.Add(new GateFailure("FileProblem", file.Id, null, problem));
            }

            CheckReference(file, failures, warnings);
        }

        var summary = Summarizer.Summarize(files, settings);
        if (summary.FilesExpectedToOpen > 0 && summary.OpenSuccessPercent < OpenSuccessTarget)
        {
            failures.Add(new GateFailure("OpenSuccessBelowTarget", null, null, string.Create(CultureInfo.InvariantCulture, $"{summary.OpenSuccessPercent:0.###}% of the files that should open opened; the target is {OpenSuccessTarget}%.")));
        }

        if (baseline?.OpenSuccessPercent is { } recordedOpen && summary.FilesExpectedToOpen > 0 && summary.OpenSuccessPercent < recordedOpen)
        {
            failures.Add(new GateFailure("OpenSuccessRegression", null, null, string.Create(CultureInfo.InvariantCulture, $"{summary.OpenSuccessPercent:0.###}% of the files that should open opened; the baseline has {recordedOpen:0.###}%.")));
        }

        if (summary.SsimCompared > 0 && summary.SsimPassPercent < settings.SsimPassTarget)
        {
            failures.Add(new GateFailure("SsimPassRateBelowTarget", null, null, string.Create(CultureInfo.InvariantCulture, $"{summary.SsimPassPercent:0.###}% of {summary.SsimCompared} compared pages reach SSIM {settings.SsimThreshold}; the target is {settings.SsimPassTarget}%.")));
        }

        if (settings.Reference && summary.SsimCompared == 0 && summary.PagesRendered > 0)
        {
            failures.Add(new GateFailure("NothingCompared", null, null, "The reference renderer is on, and no page could be compared."));
        }

        return new GateResult { Passed = failures.Count == 0, Failures = failures, Warnings = warnings };
    }

    /// <summary>
    /// The reference renderer is PDFium without the sandbox, built by this repository. If it crashes or hangs on a file, that is a crash or a hang in a
    /// MikePDF-owned process, and fails the gate like the engine's would. Anything else it cannot do (it would not start, it opens what the engine refuses)
    /// is a warning: the affected pages are simply not compared, and a run that compares nothing fails on its own.
    /// </summary>
    private static void CheckReference(FileReport file, List<GateFailure> failures, List<GateFailure> warnings)
    {
        switch (file.Reference)
        {
            case null or "Ok" or "Disabled":
                break;
            case "Crashed" or "TimedOut" when file.Expected?.AllowsEngineCrash != true:
                failures.Add(new GateFailure(file.Reference == "Crashed" ? "ReferenceCrash" : "ReferenceHang", file.Id, null, $"The reference renderer (PDFium outside the sandbox) {(file.Reference == "Crashed" ? "crashed" : "did not finish and was killed")} on this file."));
                break;
            default:
                warnings.Add(new GateFailure("ReferenceDiffers", file.Id, null, $"Reference renderer: {file.Reference}. Pages it could not render are not compared."));
                break;
        }
    }

    private static void CheckOpen(FileReport file, List<GateFailure> failures)
    {
        var expected = Summarizer.ExpectedStatus(file);
        if (file.Open.Status != expected)
        {
            failures.Add(new GateFailure("UnexpectedOpenStatus", file.Id, null, $"Opening ended with {file.Open.Status}; expected {expected}.{Detail(file.Open.Detail)}"));
        }
        else if (file.Expected?.Pages is { } pages && file.Open.Status == "Ok" && file.PageCount != pages)
        {
            failures.Add(new GateFailure("PageCountMismatch", file.Id, null, $"The document has {file.PageCount} pages; expected {pages}."));
        }
    }

    private static void CheckEngineEvents(FileReport file, List<GateFailure> failures)
    {
        var tolerated = file.Expected?.AllowsEngineCrash == true;
        foreach (var engineEvent in file.EngineEvents)
        {
            var where = engineEvent.During is null ? "" : $" during {engineEvent.During}";
            switch (engineEvent.Kind)
            {
                case "crashed" when !tolerated && engineEvent.Reason == "Hung":
                    failures.Add(new GateFailure("EngineHang", file.Id, engineEvent.During, $"The engine hung and was killed{where}: {engineEvent.Detail}"));
                    break;
                case "crashed" when !tolerated:
                    failures.Add(new GateFailure("EngineCrash", file.Id, engineEvent.During, $"The engine process ended ({engineEvent.Reason}, exit code {engineEvent.ExitCode}){where}: {engineEvent.Detail}"));
                    break;
                case "failed":
                    failures.Add(new GateFailure("EngineFailed", file.Id, engineEvent.During, $"The supervisor gave up restarting the engine{where}: {engineEvent.Detail}"));
                    break;
                default:
                    break;
            }
        }

        CheckExpectedEngineEvent(file, failures);
    }

    /// <summary>
    /// A file whose manifest entry says it takes the engine down (<c>expect.engineEvent</c>) is there to exercise recovery. If the run did not see the crash or the
    /// hang, the file no longer tests anything and the gate says so; if the engine was not replaced afterwards, recovery is what failed.
    /// </summary>
    private static void CheckExpectedEngineEvent(FileReport file, List<GateFailure> failures)
    {
        if (file.Expected?.EngineEvent is not { } expected)
        {
            return;
        }

        var crashedAt = file.EngineEvents.FindIndex(e => e.Kind == "crashed" && (expected == "hang" ? e.Reason == "Hung" : e.Reason != "Hung"));
        if (crashedAt < 0)
        {
            var seen = !file.EngineEvents.Any(e => e.Kind == "crashed") ? "no engine crash or hang" : "a different kind of engine failure (" + string.Join(", ", file.EngineEvents.Where(e => e.Kind == "crashed").Select(e => e.Reason)) + ")";
            failures.Add(new GateFailure("ExpectedEngineEventMissing", file.Id, null, $"The manifest says this file makes the engine {(expected == "hang" ? "hang" : "crash")}; the run saw {seen}. The file no longer exercises recovery."));
        }
        else if (!file.EngineEvents.Skip(crashedAt + 1).Any(e => e.Kind == "restored"))
        {
            failures.Add(new GateFailure("EngineNotRestored", file.Id, null, "The engine went down as expected, but the run did not see it replaced."));
        }
    }

    private static void CheckPage(FileReport file, PageReport page, RunSettings settings, SsimBaseline? baseline, List<GateFailure> failures, List<GateFailure> warnings)
    {
        var pageName = page.Key;
        switch (page.Status)
        {
            case "Ok":
                break;
            case "PartiallyRendered":
                if (file.Expected?.AllowsPartialRender != true)
                {
                    failures.Add(new GateFailure("PartiallyRendered", file.Id, pageName, $"PDFium reported a failure part-way through the page.{Detail(page.Detail)}"));
                }

                break;
            case "Timeout" when file.Expected?.AllowsEngineCrash == true:
                warnings.Add(new GateFailure("Timeout", file.Id, pageName, $"The page did not render within its deadline (a tolerated engine failure).{Detail(page.Detail)}"));
                return;
            case "Timeout":
                failures.Add(new GateFailure("Timeout", file.Id, pageName, $"The page did not render within its deadline.{Detail(page.Detail)}"));
                break;
            case RenderStatus.OutOfRange:
                warnings.Add(new GateFailure("PageOutOfRange", file.Id, pageName, "The page is outside the document and was not rendered."));
                return;
            default:
                if (file.Expected?.AllowsEngineCrash == true && page.Status == "EngineCrashed")
                {
                    warnings.Add(new GateFailure("RenderFailed", file.Id, pageName, $"Rendering ended with {page.Status} (a tolerated engine crash).{Detail(page.Detail)}"));
                }
                else
                {
                    failures.Add(new GateFailure("RenderFailed", file.Id, pageName, $"Rendering ended with {page.Status}.{Detail(page.Detail)}"));
                }

                return;
        }

        if (page.Ssim is not { } ssim)
        {
            return;
        }

        var key = SsimBaseline.Key(file.Id, pageName);
        double? known = baseline is not null && baseline.Entries.TryGetValue(key, out var recorded) ? recorded : null;
        var score = string.Create(CultureInfo.InvariantCulture, $"{ssim:0.######}");
        if (known is { } before && ssim < before - SsimBaseline.Tolerance)
        {
            failures.Add(new GateFailure("SsimRegression", file.Id, pageName, string.Create(CultureInfo.InvariantCulture, $"SSIM {score} is below its baseline {before:0.######} by more than {SsimBaseline.Tolerance}.")));
        }
        else if (ssim < settings.SsimThreshold)
        {
            if (known is { } already && already < settings.SsimThreshold)
            {
                warnings.Add(new GateFailure("SsimKnownLow", file.Id, pageName, $"SSIM {score} is below {settings.SsimThreshold.ToString(CultureInfo.InvariantCulture)}, as it was in the baseline."));
            }
            else
            {
                failures.Add(new GateFailure("SsimBelowThreshold", file.Id, pageName, $"SSIM {score} is below {settings.SsimThreshold.ToString(CultureInfo.InvariantCulture)}."));
            }
        }
    }

    private static string Detail(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : " " + detail;
}
