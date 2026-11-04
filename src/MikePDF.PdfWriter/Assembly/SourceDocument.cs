using System.Text;

namespace MikePDF.PdfWriter.Assembly;

/// <summary>Internal control flow: a refusal found deep in the work, turned into an <see cref="AssemblyResult"/> at the public boundary.</summary>
internal sealed class AssemblyAbort(AssemblyRefusal refusal) : Exception(refusal.ToString())
{
    public AssemblyRefusal Refusal { get; } = refusal;

    public static AssemblyAbort Limit(AssemblyLimit limit, string message, int source = -1, string? name = null) =>
        new(new AssemblyRefusal(AssemblyRefusalReason.LimitExceeded, message, source, name, Limit: limit));

    public static AssemblyAbort Structure(string message, int source = -1, string? name = null) =>
        new(new AssemblyRefusal(AssemblyRefusalReason.SourceInvalidStructure, message, source, name));

    /// <summary>The refusal for an error the parser reported while reading a source.</summary>
    public static AssemblyRefusal FromParser(PdfError error, int source, string? name)
    {
        var reason = error.Code switch
        {
            PdfErrorCode.UnsupportedEncryption => AssemblyRefusalReason.SourceEncrypted,
            PdfErrorCode.UnsupportedFilter or PdfErrorCode.UnsupportedFeature => AssemblyRefusalReason.SourceUnsupported,
            PdfErrorCode.LimitExceeded => AssemblyRefusalReason.LimitExceeded,
            _ => AssemblyRefusalReason.SourceNotReadable,
        };
        return new AssemblyRefusal(reason, error.Message, source, name, error);
    }
}

/// <summary>The inheritable page attributes in force at a page tree node (ISO 32000-2 7.7.3.4), shared by all the pages below the node that set them.</summary>
internal sealed class Inherited
{
    private Inherited(PdfObject? resources, PdfObject? mediaBox, PdfObject? cropBox, PdfObject? rotate)
    {
        Resources = resources;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Rotate = rotate;
    }

    public static Inherited None { get; } = new(null, null, null, null);

    public PdfObject? Resources { get; }

    public PdfObject? MediaBox { get; }

    public PdfObject? CropBox { get; }

    public PdfObject? Rotate { get; }

    /// <summary>The node state that introduced <see cref="Resources"/>. When that is a direct dictionary, the pages below share one output object instead of a copy each.</summary>
    public Inherited? ResourcesOwner { get; private set; }

    /// <summary>The attributes in force below <paramref name="node"/>: its own where it has them (a null value counts as absent), the inherited ones otherwise.</summary>
    public Inherited With(PdfDictionary node)
    {
        static PdfObject? Own(PdfDictionary n, string key) => n[key] is { } v and not PdfNull ? v : null;
        var resources = Own(node, "Resources");
        var mediaBox = Own(node, "MediaBox");
        var cropBox = Own(node, "CropBox");
        var rotate = Own(node, "Rotate");
        if (resources is null && mediaBox is null && cropBox is null && rotate is null)
            return this;
        var next = new Inherited(resources ?? Resources, mediaBox ?? MediaBox, cropBox ?? CropBox, rotate ?? Rotate);
        next.ResourcesOwner = resources is not null ? next : ResourcesOwner;
        return next;
    }
}

/// <summary>A leaf of the source's page tree: its object, its own dictionary and the attributes inherited from above.</summary>
internal sealed record PageSlot(PdfReference Ref, PdfDictionary Dictionary, Inherited Inherited);

/// <summary>A named destination: the page it names (an index into the source's pages) and the rest of its destination array.</summary>
internal sealed record NamedDestination(string Key, int Page, PdfObject[] Tail);

/// <summary>One range of a /PageLabels number tree.</summary>
internal sealed record LabelRange(int Start, string Style, byte[] Prefix, long FirstNumber);

/// <summary>
/// A source opened for assembly: the strict parser's view of it, its pages with their inherited attributes resolved, and the readers for the structures
/// the assembler carries (named destinations, page labels). Everything read from the file goes through <see cref="Res"/>, which turns a parser failure
/// into a refusal that names this source.
/// </summary>
internal sealed class SourceDocument
{
    private Dictionary<string, NamedDestination>? _names;
    private HashSet<string>? _conflicts;
    private List<LabelRange>? _labels;

    private SourceDocument(int index, string name, StrictDocument document, AssemblerLimits limits, PdfDictionary catalog)
    {
        Index = index;
        Name = name;
        Document = document;
        Limits = limits;
        Catalog = catalog;
    }

    public int Index { get; }

    public string Name { get; }

    public StrictDocument Document { get; }

    public PdfDocumentRevision Revision => Document.Latest;

    public AssemblerLimits Limits { get; }

    public PdfDictionary Catalog { get; }

    public IReadOnlyList<PageSlot> Pages { get; private set; } = [];

    /// <summary>Object number of a page to its index.</summary>
    public Dictionary<int, int> PageByObject { get; } = [];

    /// <summary>Objects that are never copied: the page tree's inner nodes and the catalog. A reference to one reads as null in the output.</summary>
    public HashSet<int> Blocked { get; } = [];

    public int HeaderMajor { get; private set; } = 1;

    public int HeaderMinor { get; private set; } = 7;

    // ---------------------------------------------------------------- opening

    /// <summary>Opens and walks the source. Throws <see cref="AssemblyAbort"/> with the reason when the source cannot be used.</summary>
    public static SourceDocument Open(int index, AssemblerSource source, ParserLimits parserLimits, AssemblerLimits limits)
    {
        var opened = StrictDocument.Open(source.Data, parserLimits);
        if (opened.TryGetError(out var error))
            throw new AssemblyAbort(AssemblyAbort.FromParser(error, index, source.Name));
        var document = opened.Value;

        PdfDictionary? catalog;
        try
        {
            catalog = document.Root is { } root ? document.Latest.ResolveInternal(root) as PdfDictionary : null;
        }
        catch (PdfParseException e)
        {
            throw new AssemblyAbort(AssemblyAbort.FromParser(e.Error, index, source.Name));
        }

        if (catalog is null)
            throw AssemblyAbort.Structure("the document has no catalog", index, source.Name);
        var result = new SourceDocument(index, source.Name, document, limits, catalog);
        result.ReadHeaderVersion(source.Data.Span);
        result.WalkPages();
        if (source.ExpectedPageCount is { } expected && expected != result.Pages.Count)
        {
            throw new AssemblyAbort(new AssemblyRefusal(
                AssemblyRefusalReason.SourcePageCountMismatch,
                $"the document has {result.Pages.Count} pages, {expected} were expected",
                index,
                source.Name));
        }

        return result;
    }

    private void ReadHeaderVersion(ReadOnlySpan<byte> data)
    {
        // The strict parser has required "%PDF-1.x" or "%PDF-2.x" at byte 0.
        if (data.Length >= 8 && data[5] is (byte)'1' or (byte)'2' && data[7] is >= (byte)'0' and <= (byte)'9')
        {
            HeaderMajor = data[5] - '0';
            HeaderMinor = data[7] - '0';
        }
    }

    // ---------------------------------------------------------------- the readers

    public PdfObject? Res(PdfObject? value)
    {
        try
        {
            return Revision.ResolveInternal(value) is var resolved && resolved is not PdfNull ? resolved : null;
        }
        catch (PdfParseException e)
        {
            throw new AssemblyAbort(AssemblyAbort.FromParser(e.Error, Index, Name));
        }
    }

    public PdfDictionary? Dict(PdfObject? value) => Res(value) as PdfDictionary;

    public PdfArray? Arr(PdfObject? value) => Res(value) as PdfArray;

    /// <summary>The object a reference names, parsed, or null when the entry is free or missing. A generation that does not match is a parse failure in strict mode.</summary>
    public PdfObject? Load(PdfReference reference)
    {
        try
        {
            if (!Revision.TryGetEntry(reference.Number, out var entry) || entry.Type == XrefEntryType.Free)
                return null;
            return Revision.LoadReferenced(reference);
        }
        catch (PdfParseException e)
        {
            throw new AssemblyAbort(AssemblyAbort.FromParser(e.Error, Index, Name));
        }
    }

    /// <summary>The object with this number as the newest revision has it, or null when the entry is free or missing.</summary>
    public PdfObject? LoadNumber(int number) =>
        Revision.TryGetEntry(number, out var entry) && entry.Type != XrefEntryType.Free ? Load(new PdfReference(number, entry.Generation)) : null;

    /// <summary>Reads an optional structure: if it cannot be followed, returns false (the caller warns and leaves it out). A limit or an encrypted file is still a refusal.</summary>
    public static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch (AssemblyAbort abort) when (abort.Refusal.Reason is AssemblyRefusalReason.SourceNotReadable or AssemblyRefusalReason.SourceInvalidStructure or AssemblyRefusalReason.SourceUnsupported)
        {
            value = default!;
            return false;
        }
    }

    // ---------------------------------------------------------------- the page tree

    private sealed class Frame(PdfReference node, PdfDictionary dictionary, PdfArray kids, Inherited inherited, int start)
    {
        public PdfReference Node { get; } = node;

        public PdfDictionary Dictionary { get; } = dictionary;

        public PdfArray Kids { get; } = kids;

        public Inherited Inherited { get; } = inherited;

        public int Start { get; } = start;

        public int Next { get; set; }
    }

    /// <summary>
    /// Flattens the page tree in page order. It refuses what PDFium and this parser could read differently: a node reachable twice (a cycle or a shared
    /// subtree), a /Count that is not the number of leaves below the node, a kid that is not a reference to a dictionary. The page numbers the user sees
    /// come from PDFium, so the two must agree.
    /// </summary>
    private void WalkPages()
    {
        if (Catalog["Pages"] is not PdfReference rootRef)
            throw AssemblyAbort.Structure("the catalog has no /Pages reference", Index, Name);
        Blocked.Add(Document.Root!.Number);
        var pages = new List<PageSlot>();
        var visited = new HashSet<int> { rootRef.Number };
        var maxDepth = Document.Limits.EffectiveMaxNestingDepth;
        var stack = new Stack<Frame>();
        if (Load(rootRef) is not PdfDictionary rootDictionary)
            throw AssemblyAbort.Structure("the page tree root is not a dictionary", Index, Name);
        Blocked.Add(rootRef.Number);
        stack.Push(MakeFrame(rootRef, rootDictionary, Inherited.None, 0));
        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.Next >= frame.Kids.Count)
            {
                stack.Pop();
                CheckCount(frame, pages.Count);
                continue;
            }

            if (frame.Kids[frame.Next++] is not PdfReference kid)
                throw AssemblyAbort.Structure("a /Kids entry is not a reference", Index, Name);
            if (stack.Count > maxDepth)
                throw AssemblyAbort.Limit(AssemblyLimit.NestingDepth, "the page tree is nested too deeply", Index, Name);
            if (!visited.Add(kid.Number))
                throw AssemblyAbort.Structure("a page tree node is reachable twice", Index, Name);
            if (Load(kid) is not PdfDictionary node)
                throw AssemblyAbort.Structure("a page tree node is not a dictionary", Index, Name);

            if (node["Kids"] is not null)
            {
                Blocked.Add(kid.Number);
                stack.Push(MakeFrame(kid, node, frame.Inherited, pages.Count));
                continue;
            }

            if (node.GetName("Type") == "Pages")
                throw AssemblyAbort.Structure("a /Pages node has no /Kids", Index, Name);
            if (pages.Count >= Limits.MaxSourcePages)
                throw AssemblyAbort.Limit(AssemblyLimit.SourcePages, $"the document has more than {Limits.MaxSourcePages} pages", Index, Name);
            pages.Add(new PageSlot(kid, node, frame.Inherited));
            PageByObject[kid.Number] = pages.Count - 1;
        }

        Pages = pages;
    }

    private Frame MakeFrame(PdfReference nodeRef, PdfDictionary node, Inherited inherited, int start)
    {
        if (Res(node["Kids"]) is not PdfArray kids)
            throw AssemblyAbort.Structure("a page tree node has no /Kids array", Index, Name);
        return new Frame(nodeRef, node, kids, inherited.With(node), start);
    }

    private void CheckCount(Frame frame, int leavesNow)
    {
        var leaves = leavesNow - frame.Start;
        if (Res(frame.Dictionary["Count"]) is not PdfInteger count || count.Value != leaves)
            throw AssemblyAbort.Structure($"a page tree node says /Count {(Res(frame.Dictionary["Count"]) as PdfInteger)?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"} but has {leaves} pages below it", Index, Name);
    }

    // ---------------------------------------------------------------- what the source has

    public bool IsTagged => Catalog["StructTreeRoot"] is not null and not PdfNull
        || (Dict(Catalog["MarkInfo"]) is { } mark && Res(mark["Marked"]) is PdfBoolean { Value: true });

    public bool HasOutline => Dict(Catalog["Outlines"]) is { } outlines && outlines["First"] is not null and not PdfNull;

    /// <summary>A catalog-level <c>/Names /EmbeddedFiles</c> tree. The output has no such tree.</summary>
    public bool HasEmbeddedFiles => Dict(Catalog["Names"]) is { } names && names["EmbeddedFiles"] is not null and not PdfNull;

    /// <summary>How many of document JavaScript (<c>/Names /JavaScript</c>), <c>/OpenAction</c> and the catalog's <c>/AA</c> the source has. The output has none of them.</summary>
    public int DocumentScriptKinds =>
        ((Dict(Catalog["Names"]) is { } names && names["JavaScript"] is not null and not PdfNull) ? 1 : 0)
        + (Catalog["OpenAction"] is not null and not PdfNull ? 1 : 0)
        + (Catalog["AA"] is not null and not PdfNull ? 1 : 0);

    public bool HasDocumentScripts => DocumentScriptKinds > 0;

    private int[]? _attachmentsOnPage;

    /// <summary>
    /// File-attachment annotations on each page, read once per source (a split asks again for every part). Each page's annotation array is bounded by
    /// <see cref="AssemblerLimits.MaxAnnotationsPerPage"/>; a page with more is counted up to the bound.
    /// </summary>
    public IReadOnlyList<int> AttachmentsOnPages
    {
        get
        {
            if (_attachmentsOnPage is not null)
                return _attachmentsOnPage;
            var counts = new int[Pages.Count];
            for (var i = 0; i < counts.Length; i++)
            {
                if (Arr(Pages[i].Dictionary["Annots"]) is not { } annots)
                    continue;
                for (var k = 0; k < Math.Min(annots.Count, Limits.MaxAnnotationsPerPage); k++)
                {
                    if (Dict(annots[k]) is { } annotation && annotation.GetName("Subtype") == "FileAttachment")
                        counts[i]++;
                }
            }

            _attachmentsOnPage = counts;
            return counts;
        }
    }

    public bool HasPageLabels => Dict(Catalog["PageLabels"]) is not null;

    public bool HasNamedDestinations =>
        (Dict(Catalog["Names"]) is { } names && Dict(names["Dests"]) is not null) || Dict(Catalog["Dests"]) is not null;

    public PdfDictionary? AcroForm => Dict(Catalog["AcroForm"]);

    public bool HasForm => AcroForm is { } form && Arr(form["Fields"]) is { Count: > 0 };

    public string? Title
    {
        get
        {
            if (Document.Trailer["Info"] is not PdfReference info || Dict(info) is not { } dictionary || Res(dictionary["Title"]) is not PdfString title)
                return null;
            var text = TextStrings.Decode(title.Value);
            return text.Length > 200 ? text[..200] : text;
        }
    }

    /// <summary>True when a signature field with a value is reachable from the form's /Fields. Bounded by <see cref="AssemblerLimits.MaxFieldNodes"/>.</summary>
    public bool HasSignatures
    {
        get
        {
            if (AcroForm is not { } form || Arr(form["Fields"]) is not { } fields)
                return false;
            var visited = new HashSet<int>();
            var stack = new Stack<(PdfObject Node, string? Type, int Depth)>();
            foreach (var root in fields)
                stack.Push((root, null, 0));
            var seen = 0;
            while (stack.Count > 0)
            {
                var (node, inheritedType, depth) = stack.Pop();
                if (node is PdfReference reference && !visited.Add(reference.Number))
                    continue;
                if (++seen > Limits.MaxFieldNodes)
                    throw AssemblyAbort.Limit(AssemblyLimit.FieldNodes, "the form has too many fields", Index, Name);
                if (depth > Limits.MaxFieldDepth)
                    throw AssemblyAbort.Limit(AssemblyLimit.FieldDepth, "the form's fields are nested too deeply", Index, Name);
                if (Dict(node) is not { } dictionary)
                    continue;
                var type = dictionary.GetName("FT") ?? inheritedType;
                if (type == "Sig" && dictionary["V"] is not null and not PdfNull && Res(dictionary["V"]) is not null)
                    return true;
                if (Arr(dictionary["Kids"]) is { } kids)
                {
                    foreach (var kid in kids)
                        stack.Push((kid, type, depth + 1));
                }
            }

            return false;
        }
    }

    // ---------------------------------------------------------------- named destinations

    /// <summary>
    /// Every named destination in the name tree and the legacy /Dests dictionary that leads to a page of this document. A name the tree defines twice
    /// with different targets is ambiguous (two readers could pick differently) and is in <see cref="ConflictingNames"/> instead.
    /// </summary>
    public IReadOnlyDictionary<string, NamedDestination> NamedDestinations
    {
        get
        {
            EnsureNames();
            return _names!;
        }
    }

    public IReadOnlySet<string> ConflictingNames
    {
        get
        {
            EnsureNames();
            return _conflicts!;
        }
    }

    private void EnsureNames()
    {
        if (_names is not null)
            return;
        var names = new Dictionary<string, NamedDestination>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        void Add(string key, PdfObject? value, bool fromLegacy)
        {
            if (++total > Limits.MaxNamedDestinations)
                throw AssemblyAbort.Limit(AssemblyLimit.NamedDestinations, "the document has too many named destinations", Index, Name);
            var target = DestinationTarget(value);
            if (target is not { } t)
                return;
            var entry = new NamedDestination(key, t.Page, t.Tail);
            if (conflicts.Contains(key))
                return;
            if (names.TryGetValue(key, out var existing))
            {
                if (fromLegacy)
                    return; // the name tree wins over the legacy dictionary, as in PDFium
                if (existing.Page != entry.Page || !TailsEqual(existing.Tail, entry.Tail))
                {
                    names.Remove(key);
                    conflicts.Add(key);
                }

                return;
            }

            names[key] = entry;
        }

        if (Dict(Catalog["Names"]) is { } namesDictionary && namesDictionary["Dests"] is { } tree)
        {
            var nodes = new Stack<(PdfObject Node, int Depth)>();
            var visited = new HashSet<int>();
            var count = 0;
            nodes.Push((tree, 0));
            while (nodes.Count > 0)
            {
                var (node, depth) = nodes.Pop();
                if (node is PdfReference reference && !visited.Add(reference.Number))
                    continue;
                if (++count > Limits.MaxTreeNodes)
                    throw AssemblyAbort.Limit(AssemblyLimit.TreeNodes, "a name tree has too many nodes", Index, Name);
                if (depth > Limits.MaxTreeDepth)
                    throw AssemblyAbort.Limit(AssemblyLimit.TreeDepth, "a name tree is nested too deeply", Index, Name);
                if (Dict(node) is not { } dictionary)
                    continue;
                if (Arr(dictionary["Names"]) is { } pairs)
                {
                    for (var i = 0; i + 1 < pairs.Count; i += 2)
                    {
                        if (Res(pairs[i]) is PdfString key)
                            Add(Encoding.Latin1.GetString(key.Value), pairs[i + 1], fromLegacy: false);
                    }
                }

                if (Arr(dictionary["Kids"]) is { } kids)
                {
                    for (var i = kids.Count - 1; i >= 0; i--)
                        nodes.Push((kids[i], depth + 1));
                }
            }
        }

        if (Dict(Catalog["Dests"]) is { } legacy)
        {
            foreach (var entry in legacy)
                Add(entry.Key.Value, entry.Value, fromLegacy: true);
        }

        _conflicts = conflicts;
        _names = names;
    }

    private static bool TailsEqual(PdfObject[] a, PdfObject[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => PdfObjects.DeepEquals(p.First, p.Second, 8));

    /// <summary>The page and the rest of an explicit destination (an array, or a dictionary holding one in /D), or null when it does not lead to a page of this document.</summary>
    public (int Page, PdfObject[] Tail)? DestinationTarget(PdfObject? value)
    {
        var resolved = Res(value);
        if (resolved is PdfDictionary wrapper)
            resolved = Res(wrapper["D"]);
        if (resolved is not PdfArray array || array.Count < 1)
            return null;
        var page = PageOf(array[0]);
        return page is null ? null : (page.Value, [.. array.Skip(1)]);
    }

    /// <summary>The index of the page a destination's first element names: a reference to a page, or (as PDFium reads it) a plain page number.</summary>
    public int? PageOf(PdfObject first)
    {
        switch (first)
        {
            case PdfReference reference when PageByObject.TryGetValue(reference.Number, out var index):
                return index;
            case PdfInteger number when number.Value >= 0 && number.Value < Pages.Count:
                return (int)number.Value;
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- page labels

    /// <summary>The ranges of /PageLabels, sorted. Null when the document has none. Throws <see cref="AssemblyAbort"/> for a structure that cannot be followed (the caller treats labels as optional).</summary>
    public IReadOnlyList<LabelRange>? Labels
    {
        get
        {
            if (_labels is not null)
                return _labels;
            if (Dict(Catalog["PageLabels"]) is not { } root)
                return null;
            var ranges = new List<LabelRange>();
            var seenStarts = new HashSet<int>();
            var nodes = new Stack<(PdfObject Node, int Depth)>();
            var visited = new HashSet<int>();
            var count = 0;
            nodes.Push((Catalog["PageLabels"]!, 0));
            _ = root;
            while (nodes.Count > 0)
            {
                var (node, depth) = nodes.Pop();
                if (node is PdfReference reference && !visited.Add(reference.Number))
                    continue;
                if (++count > Limits.MaxTreeNodes)
                    throw AssemblyAbort.Limit(AssemblyLimit.TreeNodes, "the page label tree has too many nodes", Index, Name);
                if (depth > Limits.MaxTreeDepth)
                    throw AssemblyAbort.Limit(AssemblyLimit.TreeDepth, "the page label tree is nested too deeply", Index, Name);
                if (Dict(node) is not { } dictionary)
                    continue;
                if (Arr(dictionary["Nums"]) is { } pairs)
                {
                    for (var i = 0; i + 1 < pairs.Count; i += 2)
                    {
                        if (Res(pairs[i]) is not PdfInteger { Value: >= 0 and <= int.MaxValue } key || Dict(pairs[i + 1]) is not { } label)
                            continue;
                        if (ranges.Count >= Limits.MaxPageLabelRanges)
                            throw AssemblyAbort.Limit(AssemblyLimit.PageLabelRanges, "the document has too many page label ranges", Index, Name);
                        if (!seenStarts.Add((int)key.Value))
                            throw AssemblyAbort.Structure("two page label ranges start at the same page", Index, Name);
                        var style = label.GetName("S") ?? "";
                        var prefix = Res(label["P"]) is PdfString p ? p.Value.ToArray() : [];
                        var first = Res(label["St"]) is PdfInteger { Value: >= 1 } st ? st.Value : 1;
                        ranges.Add(new LabelRange((int)key.Value, style, prefix, first));
                    }
                }

                if (Arr(dictionary["Kids"]) is { } kids)
                {
                    for (var i = kids.Count - 1; i >= 0; i--)
                        nodes.Push((kids[i], depth + 1));
                }
            }

            ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            _labels = ranges;
            return ranges;
        }
    }

    /// <summary>The label this page shows in the source: the last range that starts at or before it, or plain decimal numbering.</summary>
    public (string Style, byte[] Prefix, long Number) LabelOf(int page)
    {
        var ranges = _labels ?? [];
        LabelRange? range = null;
        foreach (var candidate in ranges)
        {
            if (candidate.Start > page)
                break;
            range = candidate;
        }

        return range is null ? ("D", [], page + 1L) : (range.Style, range.Prefix, range.FirstNumber + (page - range.Start));
    }
}

/// <summary>Text strings (7.9.2): UTF-16 with a byte order mark, UTF-8 with one, or PDFDocEncoding (read as Latin-1 here; only ASCII is relied on).</summary>
internal static class TextStrings
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A text string for <paramref name="text"/>: plain bytes when it is ASCII, UTF-16BE with a byte order mark otherwise.</summary>
    public static byte[] Encode(string text)
    {
        if (text.All(static c => c is >= ' ' and < (char)0x7F))
            return Encoding.ASCII.GetBytes(text);
        return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
    }

    /// <summary>The string with ASCII <paramref name="suffix"/> added, in the string's own encoding.</summary>
    public static byte[] Append(ReadOnlySpan<byte> bytes, string suffix)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return [.. bytes, .. Encoding.BigEndianUnicode.GetBytes(suffix)];
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return [.. bytes, .. Encoding.Unicode.GetBytes(suffix)];
        return [.. bytes, .. Encoding.ASCII.GetBytes(suffix)];
    }
}
