namespace MikePDF.PdfWriter.Assembly;

/// <summary>
/// One source document for the page assembler: the bytes of a PDF (the writer host maps the file the broker handed it and passes the view, so no copy is
/// made) and the name the user knows it by (used for the outline entry of a merge and in refusals).
/// </summary>
/// <param name="Name">A display name, normally the file name without its folder. Never used as a path.</param>
/// <param name="Data">The whole file. Not copied and not changed.</param>
public sealed record AssemblerSource(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// The page count the caller believes the source has (the engine's count when the user chose the pages). When it is set and the assembler's count
    /// differs, the assembly is refused: the page numbers the user picked would name different pages.
    /// </summary>
    public int? ExpectedPageCount { get; init; }
}

/// <summary>One page of a source: the index of the source in the list the assembler was given, and the zero-based page number in that source.</summary>
public readonly record struct SourcePage(int Source, int Page);

/// <summary>
/// The output of one assembly: the pages, in order. The same source page may appear more than once (duplicate); each appearance becomes its own page with
/// its own annotations and form fields. Extract, split, merge, insert from file and duplicate are all just different plans (see the helpers).
/// </summary>
public sealed record AssemblyPlan(IReadOnlyList<SourcePage> Pages)
{
    /// <summary>Pages <paramref name="first"/> to <paramref name="last"/> (zero-based, both included) of one source.</summary>
    public static AssemblyPlan Range(int source, int first, int last)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(last, first);
        return new AssemblyPlan([.. Enumerable.Range(first, last - first + 1).Select(p => new SourcePage(source, p))]);
    }

    /// <summary>The listed pages of one source, in that order.</summary>
    public static AssemblyPlan Pick(int source, IEnumerable<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return new AssemblyPlan([.. pages.Select(p => new SourcePage(source, p))]);
    }

    /// <summary>Every page of each source, one source after the other: a merge.</summary>
    public static AssemblyPlan Merge(params (int Source, int PageCount)[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new AssemblyPlan([.. sources.SelectMany(s => Enumerable.Range(0, s.PageCount).Select(p => new SourcePage(s.Source, p)))]);
    }

    /// <summary>
    /// Insert from file: the whole of <paramref name="baseSource"/> with the listed pages of <paramref name="insertSource"/> inserted before page
    /// <paramref name="at"/> of the base (0 puts them first, the base's page count puts them last).
    /// </summary>
    public static AssemblyPlan Insert(int baseSource, int basePageCount, int at, int insertSource, IEnumerable<int> insertPages)
    {
        ArgumentNullException.ThrowIfNull(insertPages);
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(at, basePageCount);
        var pages = new List<SourcePage>();
        pages.AddRange(Enumerable.Range(0, at).Select(p => new SourcePage(baseSource, p)));
        pages.AddRange(insertPages.Select(p => new SourcePage(insertSource, p)));
        pages.AddRange(Enumerable.Range(at, basePageCount - at).Select(p => new SourcePage(baseSource, p)));
        return new AssemblyPlan(pages);
    }

    /// <summary>Duplicate: the whole source with each listed page followed by a copy of itself (the page organiser's "right after itself").</summary>
    public static AssemblyPlan Duplicate(int source, int pageCount, IEnumerable<int> pagesToDuplicate)
    {
        ArgumentNullException.ThrowIfNull(pagesToDuplicate);
        var twice = new HashSet<int>(pagesToDuplicate);
        var pages = new List<SourcePage>();
        for (var p = 0; p < pageCount; p++)
        {
            pages.Add(new SourcePage(source, p));
            if (twice.Contains(p))
                pages.Add(new SourcePage(source, p));
        }

        return new AssemblyPlan(pages);
    }
}

/// <summary>
/// Every bound of the assembler. Like <see cref="ParserLimits"/>, a value read from an untrusted file, or sized by the caller, is checked against one of
/// these before it allocates or loops, and exceeding one is a refusal with <see cref="AssemblyRefusalReason.LimitExceeded"/>, never a truncated result.
/// The defaults are the page organiser's proposed limits (page-organiser 3.3) and a margin over what real documents need.
/// </summary>
public sealed record AssemblerLimits
{
    /// <summary>Most sources in one assembly (merge: 200 files).</summary>
    public int MaxSources { get; init; } = 200;

    /// <summary>Most pages in the output, and so most entries in a plan (merge: 100,000 pages).</summary>
    public int MaxOutputPages { get; init; } = 100_000;

    /// <summary>Most pages a source may have. Larger than the output bound so a few pages can still be taken from a very long document.</summary>
    public int MaxSourcePages { get; init; } = 250_000;

    /// <summary>Most bytes in all the sources together. The sources are mapped, not copied, so this is larger than the engine's memory limit.</summary>
    public long MaxTotalSourceBytes { get; init; } = 4L << 30;

    /// <summary>Most objects in the output. Below the parser's own object-number ceiling so the output always reopens.</summary>
    public int MaxOutputObjects { get; init; } = 4_000_000;

    /// <summary>Most bytes in the output. The result is one file; offsets in its cross-reference table are ten digits.</summary>
    public long MaxOutputBytes { get; init; } = 1L << 30;

    /// <summary>Most outline entries read from one source.</summary>
    public int MaxOutlineItems { get; init; } = 100_000;

    /// <summary>Deepest outline nesting read from one source.</summary>
    public int MaxOutlineDepth { get; init; } = 32;

    /// <summary>Most named destinations read from one source (name tree and legacy dictionary together).</summary>
    public int MaxNamedDestinations { get; init; } = 100_000;

    /// <summary>Most nodes visited in one name tree or number tree.</summary>
    public int MaxTreeNodes { get; init; } = 100_000;

    /// <summary>Deepest name tree or number tree.</summary>
    public int MaxTreeDepth { get; init; } = 16;

    /// <summary>Most page label ranges read from one source.</summary>
    public int MaxPageLabelRanges { get; init; } = 100_000;

    /// <summary>Most form field nodes (fields and widgets) in one source's form, and most followed up from the widgets of the pages kept.</summary>
    public int MaxFieldNodes { get; init; } = 200_000;

    /// <summary>Longest /Parent chain from a widget to its root field.</summary>
    public int MaxFieldDepth { get; init; } = 32;

    /// <summary>Deepest chain of actions (an action's /Next, an action inside an action).</summary>
    public int MaxActionDepth { get; init; } = 16;

    /// <summary>Most optional content groups read from one source.</summary>
    public int MaxOptionalContentGroups { get; init; } = 100_000;

    /// <summary>Most annotations kept on one page.</summary>
    public int MaxAnnotationsPerPage { get; init; } = 100_000;

    public static AssemblerLimits Default { get; } = new();
}

public sealed record AssemblerOptions
{
    /// <summary>Limits for reading each source. Null: <see cref="ParserLimits.Default"/>.</summary>
    public ParserLimits? ParserLimits { get; init; }

    public AssemblerLimits Limits { get; init; } = AssemblerLimits.Default;

    /// <summary>
    /// Merge: add one top-level outline entry per source, named after it (<see cref="AssemblerSource.Name"/>), holding that source's own entries
    /// (page-organiser 3.3; Acrobat does the same). Off for every other operation.
    /// </summary>
    public bool WrapSourceOutlines { get; init; }

    /// <summary>After writing, reopen the output with the strict parser and check its page count. Costs one parse of the page tree.</summary>
    public bool VerifyOutput { get; init; } = true;

    /// <summary>The Producer written to the new information dictionary.</summary>
    public string Producer { get; init; } = "MikePDF";

    /// <summary>The /ModDate written. Null: now. Tests set it so output is reproducible.</summary>
    public DateTimeOffset? ModificationDate { get; init; }

    /// <summary>The 16 bytes used for both halves of the trailer /ID. Null: random. Tests set it so output is reproducible.</summary>
    public ReadOnlyMemory<byte>? DocumentId { get; init; }
}

/// <summary>Why an assembly (or one source in it) was refused. The page organiser maps each to a message.</summary>
public enum AssemblyRefusalReason
{
    /// <summary>The source is password protected. The parser does not decrypt.</summary>
    SourceEncrypted,

    /// <summary>The strict parser refused the file (R-39): a damaged or ambiguous cross-reference table, a duplicate key, a header not at byte 0 and so on. In-place page operations through PDFium still work on such a file; this one cannot be a source.</summary>
    SourceNotReadable,

    /// <summary>The file uses something the writer does not support (a stream filter it cannot decode, for example).</summary>
    SourceUnsupported,

    /// <summary>The file parses, but its page tree or an object the pages need is not one the assembler can follow without guessing: a cycle, a node reachable twice, a lying /Count, a page that is not a dictionary.</summary>
    SourceInvalidStructure,

    /// <summary>The source has a different number of pages than the caller expected (<see cref="AssemblerSource.ExpectedPageCount"/>).</summary>
    SourcePageCountMismatch,

    /// <summary>A bound in <see cref="AssemblerLimits"/> or <see cref="ParserLimits"/> was reached. <see cref="AssemblyRefusal.Limit"/> or the error's limit says which.</summary>
    LimitExceeded,

    /// <summary>The plan is empty or names a source or page that does not exist.</summary>
    InvalidPlan,

    /// <summary>The plan names a page of a source that was itself refused.</summary>
    SourceRefused,

    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>The output did not reopen strictly or has the wrong page count. This is a bug in the assembler, reported instead of a bad file.</summary>
    OutputInvalid,
}

/// <summary>Which <see cref="AssemblerLimits"/> value was exceeded.</summary>
public enum AssemblyLimit
{
    Sources,
    OutputPages,
    SourcePages,
    TotalSourceBytes,
    OutputObjects,
    OutputBytes,
    OutlineItems,
    OutlineDepth,
    NamedDestinations,
    TreeNodes,
    TreeDepth,
    PageLabelRanges,
    FieldNodes,
    FieldDepth,
    ActionDepth,
    OptionalContentGroups,
    AnnotationsPerPage,
    NestingDepth,
}

/// <param name="Reason">What kind of refusal.</param>
/// <param name="Message">English text for logs and tests; the UI uses <paramref name="Reason"/>.</param>
/// <param name="SourceIndex">The source concerned, or -1.</param>
/// <param name="SourceName">That source's name, or null.</param>
/// <param name="Error">The parser's error when the refusal comes from it.</param>
/// <param name="Limit">The assembler bound when <paramref name="Reason"/> is <see cref="AssemblyRefusalReason.LimitExceeded"/> and the bound is the assembler's own.</param>
public sealed record AssemblyRefusal(AssemblyRefusalReason Reason, string Message, int SourceIndex = -1, string? SourceName = null, PdfError? Error = null, AssemblyLimit? Limit = null)
{
    public override string ToString() => SourceName is null ? $"{Reason}: {Message}" : $"{Reason} ({SourceName}): {Message}";
}

/// <summary>What the user should be told. Counts of removed links and outline entries are in <see cref="AssemblyStatistics"/>, not here.</summary>
public enum AssemblyWarningCode
{
    /// <summary>A source is tagged (has a structure tree) and the output has no tags. Decided for v1.0: tags are dropped, the UI says so before the save (OQ-PO-9).</summary>
    TagsDropped,

    /// <summary>
    /// A source has embedded files (a catalog-level <c>/Names /EmbeddedFiles</c> tree, which is not carried) or file-attachment annotations on pages that are not in the
    /// output. <see cref="AssemblyWarning.Count"/> is the number of attachment annotations left out, plus one for the embedded-files tree. File attachments on kept pages stay.
    /// </summary>
    AttachmentsDropped,

    /// <summary>
    /// A source has document-level scripts or an action that runs when the document opens, none of which is carried: <c>/Names /JavaScript</c>, <c>/OpenAction</c> or the
    /// catalog's <c>/AA</c>. <see cref="AssemblyWarning.Count"/> is how many of the three it has. Actions on links, buttons and fields are kept.
    /// </summary>
    ScriptsDropped,

    /// <summary>A source has digital signatures; the output is a new document, so they are gone. The signature fields stay, empty, if their page is in.</summary>
    SignaturesNotKept,

    /// <summary>Form fields were renamed because another field had the same name (OQ-PO-8). <see cref="AssemblyWarning.Count"/> is how many.</summary>
    FieldsRenamed,

    /// <summary>Named destinations were renamed because another source had the same name; the links that used them were updated.</summary>
    DestinationsRenamed,

    /// <summary>Form resource names (fonts and the like in /AcroForm /DR) were renamed because another source used the same name for something else.</summary>
    FormResourcesRenamed,

    /// <summary>A page had no /MediaBox anywhere in its tree; the Letter size was used, as readers do.</summary>
    MediaBoxDefaulted,

    /// <summary>A source's outline could not be followed (a cycle, a broken link) and was left out.</summary>
    OutlineUnreadable,

    /// <summary>A source's named destinations could not be read and were left out; links that used them were removed.</summary>
    DestinationsUnreadable,

    /// <summary>A source's page labels could not be read and were left out.</summary>
    PageLabelsUnreadable,

    /// <summary>A source's form could not be read and its form resources were left out.</summary>
    FormUnreadable,
}

public sealed record AssemblyWarning(AssemblyWarningCode Code, int SourceIndex, int Count, string Message);

/// <summary>What the assembler did to links and structure. For the summary and for tests; none of it is an error.</summary>
public sealed record AssemblyStatistics
{
    /// <summary>Link annotations (and other annotations' actions) dropped because they led to a page that is not in the output, or nowhere.</summary>
    public int LinksRemoved { get; init; }

    public int OutlineEntriesKept { get; init; }

    public int OutlineEntriesRemoved { get; init; }

    public int NamedDestinationsKept { get; init; }

    public int NamedDestinationsDropped { get; init; }

    public int NamedDestinationsRenamed { get; init; }

    public int FieldsRenamed { get; init; }

    public int FormResourcesRenamed { get; init; }

    public int SignaturesCleared { get; init; }

    public int PageLabelRanges { get; init; }
}

/// <summary>The assembled document.</summary>
/// <param name="Bytes">The whole file. Empty when the output went to a stream.</param>
/// <param name="PageCount">Pages in the output (the plan's length).</param>
/// <param name="ObjectCount">Indirect objects written.</param>
/// <param name="BytesWritten">Size of the file.</param>
/// <param name="Warnings">What the user should hear about, for the sources that contributed pages.</param>
/// <param name="Statistics">What was done to links and structure.</param>
public sealed record AssemblyOutput(ReadOnlyMemory<byte> Bytes, int PageCount, int ObjectCount, long BytesWritten, IReadOnlyList<AssemblyWarning> Warnings, AssemblyStatistics Statistics);

/// <summary>Either an <see cref="AssemblyOutput"/> or an <see cref="AssemblyRefusal"/>. Assembly never throws for bad input.</summary>
public sealed class AssemblyResult
{
    private readonly AssemblyOutput? _output;
    private readonly AssemblyRefusal? _refusal;

    private AssemblyResult(AssemblyOutput? output, AssemblyRefusal? refusal)
    {
        _output = output;
        _refusal = refusal;
    }

    public bool IsSuccess => _output is not null;

    public AssemblyOutput Output => _output ?? throw new InvalidOperationException("The assembly was refused: " + _refusal);

    public AssemblyRefusal Refusal => _refusal ?? throw new InvalidOperationException("The assembly succeeded.");

    internal static AssemblyResult Ok(AssemblyOutput output) => new(output, null);

    internal static AssemblyResult Refuse(AssemblyRefusal refusal) => new(null, refusal);
}

/// <summary>What can be said about a source before any page is chosen: for the merge dialog's list, and for the warnings that must come before the save.</summary>
public sealed record AssemblerSourceInfo(
    int PageCount,
    bool IsTagged,
    bool HasSignatures,
    bool HasOutline,
    bool HasForm,
    bool HasPageLabels,
    bool HasNamedDestinations,
    string? Title,
    bool HasEmbeddedFiles = false,
    int DocumentScriptKinds = 0)
{
    /// <summary>The source has document JavaScript, an open action or document additional actions (<see cref="DocumentScriptKinds"/> of the three).</summary>
    public bool HasDocumentScripts => DocumentScriptKinds > 0;
}

/// <summary>One source after opening: its info, or why it was refused. Every source is reported, so a dialog can list all the files that cannot be used at once.</summary>
public sealed record AssemblerSourceStatus(int Index, string Name, AssemblerSourceInfo? Info, AssemblyRefusal? Refusal)
{
    public bool IsUsable => Info is not null;
}
