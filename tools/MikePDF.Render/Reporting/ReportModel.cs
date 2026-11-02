using System.Text.Json.Serialization;

namespace MikePDF.Render.Reporting;

/// <summary>Everything a run found, in the terms of the error model (§6.4): <c>status</c> strings are the protocol's <c>Status</c> names, plus the few the tool adds (see <see cref="RenderStatus"/>).</summary>
internal sealed record RunReport
{
    public const string SchemaId = "mikepdf-render-report/1";

    public string Schema { get; init; } = SchemaId;

    public required ToolInfo Tool { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public long DurationMs { get; init; }

    public required MachineInfo Machine { get; init; }

    public required RunSettings Settings { get; init; }

    public IReadOnlyList<FileReport> Files { get; init; } = [];

    public ReportSummary Summary { get; init; } = new();

    public GateResult Gate { get; init; } = new();
}

internal sealed record ToolInfo(string Name, string Version, uint ProtocolSchemaVersion);

internal sealed record MachineInfo(string Os, string Architecture, string Runtime, string? PdfiumVersion);

internal sealed record RunSettings
{
    public string? Tier { get; init; }

    public string? Manifest { get; init; }

    public IReadOnlyList<double> Scales { get; init; } = [];

    public string Pages { get; init; } = "all";

    public int Rotation { get; init; }

    public long TileDeadlineMs { get; init; }

    public long OpenDeadlineMs { get; init; }

    public long FileTimeoutMs { get; init; }

    public long MaxPixels { get; init; }

    public bool Reference { get; init; }

    public double SsimThreshold { get; init; }

    public double SsimPassTarget { get; init; }

    public string? Baseline { get; init; }
}

/// <summary>How the run saw one file.</summary>
internal sealed record FileReport
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public long SizeBytes { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public ExpectedOutcome? Expected { get; init; }

    public OpenReport Open { get; set; } = new("NotRun", null, 0);

    public uint PageCount { get; set; }

    public string? Reference { get; set; }

    public List<PageReport> Pages { get; } = [];

    public List<EngineEvent> EngineEvents { get; } = [];

    /// <summary>Things that make this file fail the gate, in words.</summary>
    public List<string> Problems { get; } = [];

    /// <summary><c>pass</c>, <c>fail</c>, or <c>expected-failure</c> (the file was expected not to open, and did not).</summary>
    public string Verdict { get; set; } = "pass";
}

internal sealed record ExpectedOutcome
{
    /// <summary>The status opening the file should end with. <c>Ok</c> unless the file is hostile on purpose.</summary>
    public string Status { get; init; } = "Ok";

    public uint? Pages { get; init; }

    /// <summary>A known crash of the engine on this file is tolerated (tracked as a risk, never silently).</summary>
    public bool AllowsEngineCrash { get; init; }

    /// <summary>Pages that are expected to come back <c>PartiallyRendered</c>.</summary>
    public bool AllowsPartialRender { get; init; }

    /// <summary>Pages that cannot be loaded and end <c>Damaged</c> are expected (the document is broken on purpose).</summary>
    public bool AllowsDamagedPages { get; init; }

    /// <summary><c>crash</c> or <c>hang</c> when the file is made to take the engine down: the run must see that, and the engine replaced.</summary>
    public string? EngineEvent { get; init; }

    /// <summary>Set when the file should open but is known not to (see the manifest): it counts against the open-success rate.</summary>
    public string? KnownIssue { get; init; }
}

internal sealed record OpenReport(string Status, string? Detail, long Ms);

internal sealed record PageReport
{
    /// <summary>1-based.</summary>
    public int Page { get; init; }

    /// <summary>The scale asked for.</summary>
    public double Scale { get; init; }

    /// <summary>The scale used: lower than asked when the page would exceed the pixel budget.</summary>
    public double ScaleApplied { get; set; }

    public string Status { get; set; } = RenderStatus.NotRun;

    public string? Detail { get; set; }

    public long Ms { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Tiles { get; set; }

    /// <summary>How many times the page was rendered again after the engine died under it.</summary>
    public int Retries { get; set; }

    public string? Png { get; set; }

    /// <summary>The reference render's status (<c>Ok</c>, <c>PartiallyRendered</c>, <c>Missing</c> or <c>Disabled</c>).</summary>
    public string ReferenceStatus { get; set; } = "Disabled";

    public double? Ssim { get; set; }

    public double? ChangedPercent { get; set; }

    public string? DiffImage { get; set; }

    [JsonIgnore]
    public string Key => PageKey.Make(Page, Scale);
}

internal static class PageKey
{
    public static string Make(int page, double scale) => FormattableString.Invariant($"{page}@{scale:0.####}");
}

internal sealed record EngineEvent
{
    public long AtMs { get; init; }

    /// <summary><c>crashed</c>, <c>restored</c> or <c>failed</c> (the supervisor gave up).</summary>
    public required string Kind { get; init; }

    public string? Reason { get; init; }

    public string? ExitCode { get; init; }

    public string? Detail { get; init; }

    public int RestartNumber { get; init; }

    public long? RestoreMs { get; init; }

    public int? DocumentsRestored { get; init; }

    public int? DocumentsLost { get; init; }

    /// <summary>What the run was doing when the event arrived: the page being rendered, or "open".</summary>
    public string? During { get; init; }
}

internal sealed record ReportSummary
{
    public int Files { get; init; }

    public int FilesExpectedToOpen { get; init; }

    public int FilesOpened { get; init; }

    public int FilesExpectedNotToOpen { get; init; }

    /// <summary>Real files that should open but are known not to (the manifest's <c>expect.knownIssue</c>). They are inside <see cref="FilesExpectedToOpen"/>.</summary>
    public int FilesKnownIssue { get; init; }

    public int UnexpectedOpenOutcomes { get; init; }

    public double OpenSuccessPercent { get; init; }

    public int PagesAttempted { get; init; }

    public int PagesRendered { get; init; }

    public int PagesPartial { get; init; }

    public int PagesFailed { get; init; }

    public int Timeouts { get; init; }

    public int EngineCrashes { get; init; }

    public int EngineHangs { get; init; }

    public int ReferenceMissing { get; init; }

    public int SsimCompared { get; init; }

    public int SsimAtOrAboveThreshold { get; init; }

    public double SsimPassPercent { get; init; }

    public double? SsimMin { get; init; }

    public double? SsimMean { get; init; }

    public double? SsimMedian { get; init; }

    /// <summary>The score 5% of the compared pages are at or below.</summary>
    public double? SsimP05 { get; init; }

    /// <summary>The score 1% of the compared pages are at or below.</summary>
    public double? SsimP01 { get; init; }

    public IReadOnlyList<SsimBucket> SsimDistribution { get; init; } = [];

    public double TotalRenderMs { get; init; }
}

/// <summary>How many compared pages scored in a band of SSIM.</summary>
internal sealed record SsimBucket(string Range, int Pages);

internal sealed record GateFailure(string Kind, string? File, string? Page, string Message);

internal sealed record GateResult
{
    public bool Passed { get; init; } = true;

    public IReadOnlyList<GateFailure> Failures { get; init; } = [];

    public IReadOnlyList<GateFailure> Warnings { get; init; } = [];
}

/// <summary>The statuses a page can end with: the protocol's names, plus these.</summary>
internal static class RenderStatus
{
    public const string NotRun = "NotRun";

    /// <summary>The page was not rendered because an earlier page of the same file left the document unusable.</summary>
    public const string Skipped = "Skipped";

    /// <summary>The file's time limit ran out.</summary>
    public const string FileTimeout = "FileTimeout";

    /// <summary>The page was not asked for at all (outside the document).</summary>
    public const string OutOfRange = "OutOfRange";
}
