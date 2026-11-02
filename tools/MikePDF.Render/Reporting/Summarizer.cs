namespace MikePDF.Render.Reporting;

/// <summary>Totals for the report header and the gate.</summary>
internal static class Summarizer
{
    public static ReportSummary Summarize(IReadOnlyList<FileReport> files, RunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        var expectedToOpen = files.Where(f => ExpectedStatus(f) == "Ok").ToList();
        var opened = expectedToOpen.Count(f => f.Open.Status == "Ok");
        var pages = files.SelectMany(f => f.Pages).Where(p => p.Status != RenderStatus.OutOfRange).ToList();
        var scored = pages.Where(p => p.Ssim is not null).Select(p => p.Ssim!.Value).ToList();
        var events = files.SelectMany(f => f.EngineEvents).Where(e => e.Kind == "crashed").ToList();
        var atOrAbove = scored.Count(s => s >= settings.SsimThreshold);
        return new ReportSummary
        {
            Files = files.Count,
            FilesExpectedToOpen = expectedToOpen.Count,
            FilesOpened = opened,
            FilesExpectedNotToOpen = files.Count - expectedToOpen.Count,
            UnexpectedOpenOutcomes = files.Count(f => f.Open.Status != ExpectedStatus(f)),
            OpenSuccessPercent = expectedToOpen.Count == 0 ? 100 : Math.Round(100.0 * opened / expectedToOpen.Count, 3),
            PagesAttempted = pages.Count,
            PagesRendered = pages.Count(p => p.Status == "Ok"),
            PagesPartial = pages.Count(p => p.Status == "PartiallyRendered"),
            PagesFailed = pages.Count(p => p.Status is not ("Ok" or "PartiallyRendered")),
            Timeouts = pages.Count(p => p.Status == "Timeout"),
            EngineCrashes = events.Count(e => e.Reason != "Hung"),
            EngineHangs = events.Count(e => e.Reason == "Hung"),
            ReferenceMissing = pages.Count(p => p.Status is ("Ok" or "PartiallyRendered") && p.ReferenceStatus == "Missing"),
            SsimCompared = scored.Count,
            SsimAtOrAboveThreshold = atOrAbove,
            SsimPassPercent = scored.Count == 0 ? 100 : Math.Round(100.0 * atOrAbove / scored.Count, 3),
            SsimMin = scored.Count == 0 ? null : Math.Round(scored.Min(), 6),
            SsimMean = scored.Count == 0 ? null : Math.Round(scored.Average(), 6),
            TotalRenderMs = pages.Sum(p => p.Ms),
        };
    }

    public static string ExpectedStatus(FileReport file) => file.Expected?.Status ?? "Ok";
}
