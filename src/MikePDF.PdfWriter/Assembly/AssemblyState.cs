namespace MikePDF.PdfWriter.Assembly;

/// <summary>
/// One copy of a source inside one assembly: the k-th time its pages appear in the plan. Objects that must be unique per page (annotations, popups,
/// replies, form fields) are cloned per copy; everything else (content, fonts, images) is shared by all copies of the source.
/// </summary>
internal sealed class CopyState(SourceState source, int index)
{
    public SourceState Source { get; } = source;

    public int Index { get; } = index;

    /// <summary>Source page index to output page object number, for the pages of this copy.</summary>
    public Dictionary<int, int> PageOut { get; } = [];

    public List<int> PageList { get; } = [];

    /// <summary>Source object number to output object number, for the objects owned by this copy.</summary>
    public Dictionary<int, int> OwnedMap { get; } = [];

    public HashSet<int> Widgets { get; } = [];

    public HashSet<int> FieldNodes { get; } = [];

    /// <summary>Root fields that have a field type somewhere up their chain, in the order their widgets were met. These go in the output /AcroForm /Fields.</summary>
    public List<int> Roots { get; } = [];

    public HashSet<int> RootSet { get; } = [];

    /// <summary>Field nodes up to the one that defines a signature field type: their /V is cleared.</summary>
    public HashSet<int> SignatureNodes { get; } = [];

    public Dictionary<int, byte[]> RootRename { get; } = [];

    /// <summary>For a root field: the inheritable /DA and /Q to write on it so it keeps its own source's defaults.</summary>
    public Dictionary<int, (PdfObject? Da, PdfObject? Q)> RootDefaults { get; } = [];

    /// <summary>The output page for a source page: this copy's, or else the first copy's (a link to a page that was copied keeps going to the original).</summary>
    public int? OutputPage(int sourcePage)
    {
        if (PageOut.TryGetValue(sourcePage, out var own))
            return own;
        return Source.Copies.Count > 0 && Source.Copies[0].PageOut.TryGetValue(sourcePage, out var first) ? first : null;
    }
}

/// <summary>What the assembler tracks about one source within one assembly.</summary>
internal sealed class SourceState(SourceDocument document)
{
    public SourceDocument Document { get; } = document;

    public List<CopyState> Copies { get; } = [];

    /// <summary>Pages of the source that are in the output.</summary>
    public HashSet<int> Included { get; } = [];

    /// <summary>Index in the output of the first page from this source, or -1.</summary>
    public int FirstOutputPage { get; set; } = -1;

    /// <summary>Source objects that are cloned per copy: annotations, their popups and replies, and the field nodes above their widgets.</summary>
    public HashSet<int> Owned { get; } = [];

    /// <summary>Source object number to output object number, for objects every copy shares.</summary>
    public Dictionary<int, int> Shared { get; } = [];

    /// <summary>Named destination key (the source's) to the key it has in the output.</summary>
    public Dictionary<string, string> NameRename { get; } = [];

    public bool NamesUnreadable { get; set; }

    /// <summary>Form font resource names renamed because of a clash, used to rewrite /DA strings.</summary>
    public Dictionary<string, string> FontRenames { get; } = [];

    public bool HasResourceRenames { get; set; }

    public PdfDictionary? FormResources { get; set; }

    /// <summary>Appearance streams without their own /Resources, which must get the form's resources so renamed names do not break them.</summary>
    public HashSet<int> StreamsNeedingResources { get; } = [];

    public int SignaturesCleared { get; set; }

    public int MediaBoxDefaulted { get; set; }

    public int Index => Document.Index;

    public CopyState GetCopy(int index)
    {
        while (Copies.Count <= index)
            Copies.Add(new CopyState(this, Copies.Count));
        return Copies[index];
    }
}

/// <summary>A page of the output.</summary>
internal sealed record OutputPage(CopyState Copy, int SourcePage, int Number);

/// <summary>What a destination (an array, a name, or an action's /D) leads to in its source.</summary>
internal enum DestinationKind
{
    /// <summary>There is no destination.</summary>
    Absent,

    /// <summary>There is one, and it leads nowhere: an undefined name, an ambiguous name, a first element that is not a page. It is dead in the source and stays dead (it is removed, never left to resolve to something else in the output).</summary>
    Dead,

    Page,
}

internal readonly record struct ResolvedDestination(DestinationKind Kind, int Page, PdfObject[] Tail, string? Name);

/// <summary>Outline entry read from a source, in source terms.</summary>
internal sealed class OutlineNode
{
    public PdfString Title { get; init; } = new([]);

    public int Page { get; init; } = -1;

    public PdfObject[] Tail { get; init; } = [];

    /// <summary>A non-GoTo action (a URI, say), kept as the entry's /A.</summary>
    public PdfObject? Action { get; init; }

    public bool Open { get; init; } = true;

    public PdfObject? Color { get; init; }

    public PdfObject? Flags { get; init; }

    public List<OutlineNode> Kids { get; set; } = [];

    /// <summary>The source this entry came from, for rewriting its action and tail.</summary>
    public SourceState? Source { get; init; }
}
