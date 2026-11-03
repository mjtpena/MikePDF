namespace MikePDF.PdfWriter.Assembly;

/// <summary>
/// The page assembler (page-organiser route B, spike S11, decisions.md "After the M4 spikes"): builds a new document from pages of one or more
/// strictly parsed sources. It produces the saved file for extract, split, merge, insert from file and duplicate; rotate, move, delete and insert
/// blank page are PDFium's, not this.
///
/// Open the sources once with <see cref="Open"/> (it reports every source that cannot be used, with the reason, and the warnings that must be shown
/// before the save), then call <see cref="AssemblySourceSet.Assemble"/> with a plan for each output (split makes one per part from the same set).
/// Inputs are plain data (the bytes of each file, which the writer host maps from the handle it was given) and an output stream or buffer. Not
/// thread-safe.
/// </summary>
public static class PageAssembler
{
    /// <summary>Opens every source. Never throws for bad input: a source that cannot be used is reported in <see cref="AssemblySourceSet.Sources"/>.</summary>
    public static AssemblySourceSet Open(IReadOnlyList<AssemblerSource> sources, AssemblerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return AssemblySourceSet.Create(sources, options ?? new AssemblerOptions());
    }

    /// <summary>Opens the sources and assembles one output in a single call.</summary>
    public static AssemblyResult Assemble(IReadOnlyList<AssemblerSource> sources, AssemblyPlan plan, AssemblerOptions? options = null, CancellationToken cancellationToken = default) =>
        Open(sources, options).Assemble(plan, cancellationToken);
}

/// <summary>Sources opened for assembly. Holds the parsed documents, so assembling several outputs from the same sources (a split) parses them once.</summary>
public sealed class AssemblySourceSet
{
    private readonly SourceDocument?[] _documents;
    private readonly AssemblerOptions _options;

    private AssemblySourceSet(SourceDocument?[] documents, IReadOnlyList<AssemblerSourceStatus> statuses, IReadOnlyList<AssemblyWarning> warnings, AssemblyRefusal? refusal, AssemblerOptions options)
    {
        _documents = documents;
        Sources = statuses;
        Warnings = warnings;
        Refusal = refusal;
        _options = options;
    }

    /// <summary>One entry per source, in the order given: its facts, or why it was refused.</summary>
    public IReadOnlyList<AssemblerSourceStatus> Sources { get; }

    /// <summary>What the user must hear before saving, for every usable source: tags that will be dropped, signatures that will not be kept.</summary>
    public IReadOnlyList<AssemblyWarning> Warnings { get; }

    /// <summary>Set when the list as a whole is refused (too many sources, too many bytes). Every <see cref="Assemble"/> then returns it.</summary>
    public AssemblyRefusal? Refusal { get; }

    internal static AssemblySourceSet Create(IReadOnlyList<AssemblerSource> sources, AssemblerOptions options)
    {
        var limits = options.Limits;
        var statuses = new List<AssemblerSourceStatus>(Math.Min(sources.Count, limits.MaxSources));
        if (sources.Count > limits.MaxSources)
        {
            var refusal = new AssemblyRefusal(AssemblyRefusalReason.LimitExceeded, $"more than {limits.MaxSources} sources", Limit: AssemblyLimit.Sources);
            return new AssemblySourceSet([], [], [], refusal, options);
        }

        long total = 0;
        foreach (var source in sources)
            total += source.Data.Length;
        if (total > limits.MaxTotalSourceBytes)
        {
            var refusal = new AssemblyRefusal(AssemblyRefusalReason.LimitExceeded, "the sources are larger together than the limit", Limit: AssemblyLimit.TotalSourceBytes);
            return new AssemblySourceSet([], [], [], refusal, options);
        }

        var parserLimits = options.ParserLimits ?? ParserLimits.Default;
        var documents = new SourceDocument?[sources.Count];
        var warnings = new List<AssemblyWarning>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            try
            {
                var document = SourceDocument.Open(i, source, parserLimits, limits);
                var info = Describe(document);
                documents[i] = document;
                statuses.Add(new AssemblerSourceStatus(i, source.Name, info, null));
                if (info.IsTagged)
                    warnings.Add(new AssemblyWarning(AssemblyWarningCode.TagsDropped, i, 1, "the structure tree (accessibility tags) is not carried to the new file"));
                if (info.HasSignatures)
                    warnings.Add(new AssemblyWarning(AssemblyWarningCode.SignaturesNotKept, i, 0, "digital signatures are not kept in the new file"));
            }
            catch (AssemblyAbort abort)
            {
                statuses.Add(new AssemblerSourceStatus(i, source.Name, null, abort.Refusal with { SourceIndex = i, SourceName = source.Name }));
            }
        }

        return new AssemblySourceSet(documents, statuses, warnings, null, options);
    }

    private static AssemblerSourceInfo Describe(SourceDocument document)
    {
        bool Read(Func<bool> read) => SourceDocument.TryRead(read, out var value) && value;
        return new AssemblerSourceInfo(
            document.Pages.Count,
            Read(() => document.IsTagged),
            Read(() => document.HasSignatures),
            Read(() => document.HasOutline),
            Read(() => document.HasForm),
            Read(() => document.HasPageLabels),
            Read(() => document.HasNamedDestinations),
            SourceDocument.TryRead(() => document.Title, out var title) ? title : null);
    }

    /// <summary>Assembles the planned pages into one new document held in memory (<see cref="AssemblyOutput.Bytes"/>). The output is reopened and checked unless <see cref="AssemblerOptions.VerifyOutput"/> is off.</summary>
    public AssemblyResult Assemble(AssemblyPlan plan, CancellationToken cancellationToken = default) => Run(plan, null, cancellationToken);

    /// <summary>
    /// Assembles straight into <paramref name="output"/> (the writer host wraps the handle the broker gave it), so a large result is never held in
    /// memory. <see cref="AssemblyOutput.Bytes"/> is then empty and the output is not reopened; the broker's fresh-engine check does that. On a refusal
    /// the stream holds a partial file, which the caller discards.
    /// </summary>
    public AssemblyResult AssembleTo(AssemblyPlan plan, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        return Run(plan, output, cancellationToken);
    }

    private AssemblyResult Run(AssemblyPlan plan, Stream? destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Refusal is not null)
            return AssemblyResult.Refuse(Refusal);

        var memory = destination is null ? new MemoryStream() : null;
        var job = new AssemblyJob(Sources, _documents, plan, _options, destination ?? memory!, cancellationToken);
        try
        {
            job.Run();
        }
        catch (AssemblyAbort abort)
        {
            return AssemblyResult.Refuse(abort.Refusal);
        }
        catch (PdfParseException error)
        {
            return AssemblyResult.Refuse(AssemblyAbort.FromParser(error.Error, -1, null));
        }

        var bytes = memory is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(memory.GetBuffer(), 0, checked((int)memory.Length));
        if (memory is not null && _options.VerifyOutput && Verify(bytes, job.PageCount) is { } invalid)
            return AssemblyResult.Refuse(invalid);
        return AssemblyResult.Ok(new AssemblyOutput(bytes, job.PageCount, job.ObjectCount, job.BytesWritten, job.Warnings, job.Statistics));
    }

    /// <summary>The output must reopen strictly with the planned number of pages. A failure here is a bug in the assembler, and no file is returned.</summary>
    private AssemblyRefusal? Verify(ReadOnlyMemory<byte> bytes, int pageCount)
    {
        var parserLimits = _options.ParserLimits ?? ParserLimits.Default;
        var limits = parserLimits with { MaxFileSize = Math.Max(parserLimits.MaxFileSize, _options.Limits.MaxOutputBytes) };
        var reopened = StrictDocument.Open(bytes, limits);
        if (reopened.TryGetError(out var error))
            return new AssemblyRefusal(AssemblyRefusalReason.OutputInvalid, "the output does not parse strictly: " + error.Message, Error: error);
        var pages = Editing.PageTree.Enumerate(reopened.Value);
        if (pages.TryGetError(out var treeError))
            return new AssemblyRefusal(AssemblyRefusalReason.OutputInvalid, "the output's page tree is not readable: " + treeError.Message, Error: treeError);
        return pages.Value.Count == pageCount
            ? null
            : new AssemblyRefusal(AssemblyRefusalReason.OutputInvalid, $"the output has {pages.Value.Count} pages, {pageCount} were planned");
    }
}
