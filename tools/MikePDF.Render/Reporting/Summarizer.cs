namespace MikePDF.Render.Reporting;

/// <summary>Totals for the report header and the gate.</summary>
internal static class Summarizer
{
    /// <summary>The lower bounds of the SSIM distribution in the report: a bucket is <c>[lower, next lower)</c>, the last one reaches 1.</summary>
    private static readonly double[] BucketLowerBounds = [0, 0.90, 0.95, 0.98, 0.99, 0.999];

    public static ReportSummary Summarize(IReadOnlyList<FileReport> files, RunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        var shouldOpen = files.Where(ShouldOpen).ToList();
        var opened = shouldOpen.Count(f => f.Open.Status == "Ok");
        var pages = files.SelectMany(f => f.Pages).Where(p => p.Status != RenderStatus.OutOfRange).ToList();
        var scored = pages.Where(p => p.Ssim is not null).Select(p => p.Ssim!.Value).Order().ToList();
        var events = files.SelectMany(f => f.EngineEvents).Where(e => e.Kind == "crashed").ToList();
        var atOrAbove = scored.Count(s => s >= settings.SsimThreshold);
        return new ReportSummary
        {
            Files = files.Count,
            FilesExpectedToOpen = shouldOpen.Count,
            FilesOpened = opened,
            FilesExpectedNotToOpen = files.Count - shouldOpen.Count,
            FilesKnownIssue = files.Count(f => f.Expected?.KnownIssue is not null),
            UnexpectedOpenOutcomes = files.Count(f => f.Open.Status != ExpectedStatus(f)),
            OpenSuccessPercent = shouldOpen.Count == 0 ? 100 : Math.Round(100.0 * opened / shouldOpen.Count, 3),
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
            SsimMin = scored.Count == 0 ? null : Math.Round(scored[0], 6),
            SsimMean = scored.Count == 0 ? null : Math.Round(scored.Average(), 6),
            SsimMedian = Percentile(scored, 50),
            SsimP05 = Percentile(scored, 5),
            SsimP01 = Percentile(scored, 1),
            SsimDistribution = Distribution(scored),
            TotalRenderMs = pages.Sum(p => p.Ms),
        };
    }

    public static string ExpectedStatus(FileReport file) => file.Expected?.Status ?? "Ok";

    /// <summary>A file counts toward the open-success rate when it is expected to open, or is a real file that should but is known not to.</summary>
    public static bool ShouldOpen(FileReport file) => ExpectedStatus(file) == "Ok" || file.Expected?.KnownIssue is not null;

    /// <summary>The value at the given percentile of an ascending list (the lowest value that at least that share of the list is at or below), rounded to six places.</summary>
    private static double? Percentile(List<double> ascending, double percent)
    {
        if (ascending.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percent / 100.0 * ascending.Count);
        return Math.Round(ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)], 6);
    }

    private static List<SsimBucket> Distribution(List<double> scores)
    {
        var buckets = new List<SsimBucket>();
        for (var i = 0; i < BucketLowerBounds.Length; i++)
        {
            var lower = BucketLowerBounds[i];
            var last = i + 1 == BucketLowerBounds.Length;
            var upper = last ? double.PositiveInfinity : BucketLowerBounds[i + 1];
            var label = last ? FormattableString.Invariant($"{lower:0.###} and above") : FormattableString.Invariant($"{lower:0.###} to under {upper:0.###}");
            buckets.Add(new SsimBucket(label, scores.Count(s => s >= lower && s < upper)));
        }

        return buckets;
    }
}
