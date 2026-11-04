using System.Collections.Immutable;
using System.Security.Cryptography;

namespace MikePDF.PdfWriter.Assembly;

/// <summary>
/// One assembly: reads the planned pages from the strictly parsed sources and writes a new document. The steps, in order:
/// <list type="number">
/// <item>Check the plan and give every output page its object number. Copies of a source (the second time a page appears) get their own state.</item>
/// <item>Look at the annotations of the kept pages and decide what is cloned per copy, which form fields are kept, what they are called, which
/// resource names clash and which named destinations survive under which names. Nothing is written yet, so every rule that needs to see all the
/// sources (renaming) has.</item>
/// <item>Write the pages and, as they are met, every object they reach (renumbered, stream bytes verbatim).</item>
/// <item>Write what hangs off the catalog: named destinations, page labels, the outline, the form, optional content.</item>
/// <item>Write the page tree, the catalog, the information dictionary, the cross-reference table.</item>
/// </list>
/// Links, outline entries and named destinations are never retargeted by guessing: a destination is resolved in its own source and either leads to a
/// page that is in the output (it follows that page) or it is dropped.
/// </summary>
internal sealed partial class AssemblyJob
{
    private const int Fan = 64;
    private const int CatalogNumber = 1;
    private const int PageTreeRootNumber = 2;
    private const int InfoNumber = 3;
    private const int FirstPageNumber = 4;
    private const int MaxRewriteDepth = 200;

    private readonly IReadOnlyList<AssemblerSourceStatus> _statuses;
    private readonly SourceDocument?[] _documents;
    private readonly AssemblyPlan _plan;
    private readonly AssemblerOptions _options;
    private readonly AssemblerLimits _limits;
    private readonly CancellationToken _cancel;
    private readonly OutputWriter _writer;
    private readonly Dictionary<int, SourceState> _states = [];
    private readonly List<SourceState> _contributing = [];
    private readonly List<CopyState> _copies = [];
    private readonly List<OutputPage> _pages = [];
    private readonly Queue<WorkItem> _queue = new();
    private readonly List<AssemblyWarning> _warnings = [];
    private readonly Dictionary<Inherited, PdfReference> _hoistedResources = [];
    private int _next;
    private int[] _levelCounts = [];
    private int[] _levelBase = [];

    private int _linksRemoved;
    private int _outlineKept;
    private int _outlineRemoved;
    private int _namesKept;
    private int _namesDropped;
    private int _namesRenamed;
    private int _fieldsRenamed;
    private int _resourcesRenamed;
    private int _signaturesCleared;
    private int _labelRanges;

    private readonly record struct WorkItem(CopyState Copy, PdfReference Reference, int Number, bool Owned);

    public AssemblyJob(
        IReadOnlyList<AssemblerSourceStatus> statuses,
        SourceDocument?[] documents,
        AssemblyPlan plan,
        AssemblerOptions options,
        Stream output,
        CancellationToken cancel)
    {
        _statuses = statuses;
        _documents = documents;
        _plan = plan;
        _options = options;
        _limits = options.Limits;
        _cancel = cancel;
        _writer = new OutputWriter(output, _limits.MaxOutputBytes);
    }

    public int PageCount => _pages.Count;

    public int ObjectCount => _writer.ObjectsWritten;

    public long BytesWritten => _writer.Position;

    public IReadOnlyList<AssemblyWarning> Warnings => _warnings;

    public AssemblyStatistics Statistics => new()
    {
        LinksRemoved = _linksRemoved,
        OutlineEntriesKept = _outlineKept,
        OutlineEntriesRemoved = _outlineRemoved,
        NamedDestinationsKept = _namesKept,
        NamedDestinationsDropped = _namesDropped,
        NamedDestinationsRenamed = _namesRenamed,
        FieldsRenamed = _fieldsRenamed,
        FormResourcesRenamed = _resourcesRenamed,
        SignaturesCleared = _signaturesCleared,
        PageLabelRanges = _labelRanges,
    };

    public void Run()
    {
        PlanPages();
        Layout();
        CollectAnnotations();
        AssignFieldNames();
        MergeFormResources();
        BuildNamedDestinations();

        var major = Math.Max(1, _contributing.Max(static s => s.Document.HeaderMajor));
        var minor = _contributing.Where(s => s.Document.HeaderMajor == major).Max(static s => s.Document.HeaderMinor);
        if (major == 1)
            minor = Math.Max(minor, 7);
        _writer.WriteHeader(major, minor);

        for (var i = 0; i < _pages.Count; i++)
        {
            CheckCancelled();
            EmitPage(_pages[i], ParentOfPage(i));
            Drain();
        }

        BuildNameTree(out var names);
        BuildPageLabels(out var labels);
        BuildOutline(out var outlines);
        BuildForm(out var form);
        BuildOptionalContent(out var contentProperties);
        Drain();

        EmitPageTree();
        EmitInfo();
        EmitCatalog(names, labels, outlines, form, contentProperties);
        _writer.WriteTail(_next, CatalogNumber, InfoNumber, DocumentId());
        CollectWarnings();
    }

    private void CheckCancelled()
    {
        if (_cancel.IsCancellationRequested)
            throw new AssemblyAbort(new AssemblyRefusal(AssemblyRefusalReason.Cancelled, "the assembly was cancelled"));
    }

    // ---------------------------------------------------------------- the plan, the numbers

    private void PlanPages()
    {
        var entries = _plan.Pages;
        if (entries.Count == 0)
            throw new AssemblyAbort(new AssemblyRefusal(AssemblyRefusalReason.InvalidPlan, "the plan has no pages"));
        if (entries.Count > _limits.MaxOutputPages)
            throw AssemblyAbort.Limit(AssemblyLimit.OutputPages, $"the output would have more than {_limits.MaxOutputPages} pages");

        var occurrences = new Dictionary<SourcePage, int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Source < 0 || entry.Source >= _documents.Length)
                throw new AssemblyAbort(new AssemblyRefusal(AssemblyRefusalReason.InvalidPlan, $"page {i + 1} of the plan names source {entry.Source}, which does not exist"));
            var document = _documents[entry.Source];
            if (document is null)
            {
                var status = _statuses[entry.Source];
                var why = status.Refusal;
                throw new AssemblyAbort(new AssemblyRefusal(AssemblyRefusalReason.SourceRefused, why?.Message ?? "the source was refused", entry.Source, status.Name, why?.Error, why?.Limit));
            }

            if (entry.Page < 0 || entry.Page >= document.Pages.Count)
                throw new AssemblyAbort(new AssemblyRefusal(AssemblyRefusalReason.InvalidPlan, $"page {i + 1} of the plan names page {entry.Page} of {document.Name}, which has {document.Pages.Count}", entry.Source, document.Name));

            if (!_states.TryGetValue(entry.Source, out var state))
            {
                state = new SourceState(document);
                _states[entry.Source] = state;
                _contributing.Add(state);
                state.FirstOutputPage = i;
            }

            occurrences.TryGetValue(entry, out var occurrence);
            occurrences[entry] = occurrence + 1;
            var known = state.Copies.Count;
            var copy = state.GetCopy(occurrence);
            for (var c = known; c < state.Copies.Count; c++)
                _copies.Add(state.Copies[c]);
            var number = FirstPageNumber + i;
            copy.PageOut[entry.Page] = number;
            copy.PageList.Add(entry.Page);
            state.Included.Add(entry.Page);
            _pages.Add(new OutputPage(copy, entry.Page, number));
        }
    }

    /// <summary>Numbers: 1 catalog, 2 page tree root, 3 information, then one per page, then the inner page tree nodes, then everything else as it is met.</summary>
    private void Layout()
    {
        var counts = new List<int>();
        var n = _pages.Count;
        do
        {
            n = (n + Fan - 1) / Fan;
            counts.Add(n);
        }
        while (n > 1);
        _levelCounts = [.. counts];
        _levelBase = new int[counts.Count];
        var next = FirstPageNumber + _pages.Count;
        for (var level = 0; level < counts.Count - 1; level++)
        {
            _levelBase[level] = next;
            next += counts[level];
        }

        _levelBase[^1] = PageTreeRootNumber;
        _next = next;
        if (_next > _limits.MaxOutputObjects)
            throw AssemblyAbort.Limit(AssemblyLimit.OutputObjects, "the output would need more objects than the limit");
    }

    private int NodeNumber(int level, int index) => _levelBase[level] + (level == _levelCounts.Length - 1 ? 0 : index);

    private int ParentOfPage(int page) => NodeNumber(0, page / Fan);

    private int NewNumber()
    {
        if (_next >= _limits.MaxOutputObjects)
            throw AssemblyAbort.Limit(AssemblyLimit.OutputObjects, "the output would need more objects than the limit");
        return _next++;
    }

    private void EmitPageTree()
    {
        var total = _pages.Count;
        long span = Fan;
        for (var level = 0; level < _levelCounts.Length; level++)
        {
            for (var i = 0; i < _levelCounts[level]; i++)
            {
                var kidsFrom = i * Fan;
                var kidCount = level == 0 ? total : _levelCounts[level - 1];
                var kidsTo = Math.Min(kidCount, kidsFrom + Fan);
                var kids = new List<PdfObject>(kidsTo - kidsFrom);
                for (var k = kidsFrom; k < kidsTo; k++)
                    kids.Add(new PdfReference(level == 0 ? FirstPageNumber + k : NodeNumber(level - 1, k)));
                var first = (long)i * span;
                var leaves = (int)Math.Min(total, first + span) - (int)first;
                var entries = new List<KeyValuePair<PdfName, PdfObject>> { new(new PdfName("Type"), new PdfName("Pages")) };
                if (level < _levelCounts.Length - 1)
                    entries.Add(new(new PdfName("Parent"), new PdfReference(NodeNumber(level + 1, i / Fan))));
                entries.Add(new(new PdfName("Kids"), new PdfArray(kids)));
                entries.Add(new(new PdfName("Count"), new PdfInteger(leaves)));
                _writer.WriteObject(NodeNumber(level, i), new PdfDictionary(entries));
            }

            span *= Fan;
        }
    }

    private void EmitInfo()
    {
        var source = _contributing[0].Document;
        var entries = new List<KeyValuePair<PdfName, PdfObject>>();
        if (source.Document.Trailer["Info"] is PdfReference info && source.Dict(info) is { } dictionary)
        {
            foreach (var key in new[] { "Title", "Author", "Subject", "Keywords", "Creator", "CreationDate" })
            {
                if (source.Res(dictionary[key]) is PdfString text)
                    entries.Add(new(new PdfName(key), new PdfString(text.Value, text.Format)));
            }
        }

        entries.Add(new(new PdfName("Producer"), new PdfString(TextStrings.Encode(_options.Producer))));
        var when = _options.ModificationDate ?? DateTimeOffset.UtcNow;
        entries.Add(new(new PdfName("ModDate"), PdfString.FromAscii("D:" + when.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z")));
        _writer.WriteObject(InfoNumber, new PdfDictionary(entries));
    }

    private byte[] DocumentId() => _options.DocumentId is { } fixedId ? fixedId.ToArray() : RandomNumberGenerator.GetBytes(16);

    // ---------------------------------------------------------------- reading the kept pages' annotations

    /// <summary>
    /// Decides which objects are cloned per copy and, for the widgets on the kept pages, which form fields are kept: the widget, the chain of /Parent
    /// fields above it (up to the root), and the signature fields among them. A field that has no widget on a kept page is not carried.
    /// </summary>
    private void CollectAnnotations()
    {
        foreach (var copy in _copies)
        {
            var state = copy.Source;
            var document = state.Document;
            foreach (var pageIndex in copy.PageList)
            {
                CheckCancelled();
                var slot = document.Pages[pageIndex];
                if (document.Arr(slot.Dictionary["Annots"]) is not { } annots)
                    continue;
                if (annots.Count > _limits.MaxAnnotationsPerPage)
                    throw AssemblyAbort.Limit(AssemblyLimit.AnnotationsPerPage, "a page has more annotations than the limit", document.Index, document.Name);
                foreach (var item in annots)
                {
                    if (item is PdfReference reference)
                        state.Owned.Add(reference.Number);
                    if (document.Dict(item) is not { } annotation)
                        continue;
                    if (annotation["Popup"] is PdfReference popup)
                        state.Owned.Add(popup.Number);
                    if (annotation["IRT"] is PdfReference reply)
                        state.Owned.Add(reply.Number);
                    if (item is PdfReference widget && annotation.GetName("Subtype") == "Widget")
                        CollectWidget(copy, widget, annotation);
                }
            }
        }
    }

    private void CollectWidget(CopyState copy, PdfReference widget, PdfDictionary annotation)
    {
        var state = copy.Source;
        var document = state.Document;
        copy.Widgets.Add(widget.Number);
        var chain = new List<int>();
        var visited = new HashSet<int>();
        var current = widget;
        var dictionary = annotation;
        string? type = null;
        var typeAt = -1;
        while (true)
        {
            if (!visited.Add(current.Number))
                throw AssemblyAbort.Structure("a form field is its own ancestor", document.Index, document.Name);
            if (chain.Count >= _limits.MaxFieldDepth)
                throw AssemblyAbort.Limit(AssemblyLimit.FieldDepth, "a form field is nested too deeply", document.Index, document.Name);
            chain.Add(current.Number);
            if (type is null && document.Res(dictionary["FT"]) is PdfName fieldType)
            {
                type = fieldType.Value;
                typeAt = chain.Count - 1;
            }

            if (dictionary["Parent"] is not PdfReference parent || document.Dict(parent) is not { } parentDictionary)
                break;
            current = parent;
            dictionary = parentDictionary;
        }

        foreach (var number in chain)
        {
            copy.FieldNodes.Add(number);
            state.Owned.Add(number);
        }

        if (copy.FieldNodes.Count > _limits.MaxFieldNodes)
            throw AssemblyAbort.Limit(AssemblyLimit.FieldNodes, "the form has more fields than the limit", document.Index, document.Name);
        if (type is null)
            return; // a widget with no field type anywhere above it is only an annotation
        var root = chain[^1];
        if (copy.RootSet.Add(root))
            copy.Roots.Add(root);
        if (type == "Sig")
        {
            for (var i = 0; i <= typeAt; i++)
                copy.SignatureNodes.Add(chain[i]);
        }
    }

    // ---------------------------------------------------------------- form field names and defaults

    private void AssignFieldNames()
    {
        var everyName = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in _copies)
        {
            foreach (var root in copy.Roots)
            {
                if (RootName(copy, root) is { } name)
                    everyName.Add(name.Text);
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in _copies)
        {
            foreach (var root in copy.Roots)
            {
                if (RootName(copy, root) is not { } name || used.Add(name.Text))
                    continue;
                for (var n = 2; ; n++)
                {
                    var candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name.Text}_{n}");
                    if (everyName.Contains(candidate) || !used.Add(candidate))
                        continue;
                    copy.RootRename[root] = TextStrings.Append(name.Bytes, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"_{n}"));
                    _fieldsRenamed++;
                    break;
                }
            }
        }

        // The fields of a source that has its own default appearance (/DA) or quadding (/Q) in its form keep them when another source's become the output's.
        var (outputDa, outputQ) = OutputFormDefaults();
        foreach (var copy in _copies)
        {
            var document = copy.Source.Document;
            if (document.AcroForm is not { } form)
                continue;
            var da = document.Res(form["DA"]) as PdfString;
            var q = document.Res(form["Q"]) is PdfInteger quad ? quad.Value : 0;
            var needDa = da is not null && (outputDa is null || !da.Value.SequenceEqual(outputDa.Value));
            var needQ = q != outputQ;
            if (!needDa && !needQ)
                continue;
            foreach (var root in copy.Roots)
            {
                var rootDictionary = document.LoadNumber(root) as PdfDictionary;
                var da1 = needDa && rootDictionary is not null && !rootDictionary.ContainsKey("DA") ? da : null;
                var q1 = needQ && rootDictionary is not null && !rootDictionary.ContainsKey("Q") ? new PdfInteger(q) : null;
                if (da1 is not null || q1 is not null)
                    copy.RootDefaults[root] = (da1, q1);
            }
        }
    }

    private (PdfString? Da, long Q) OutputFormDefaults()
    {
        foreach (var state in _contributing)
        {
            var document = state.Document;
            if (document.AcroForm is { } form && document.Res(form["DA"]) is PdfString da)
                return (da, document.Res(form["Q"]) is PdfInteger q ? q.Value : 0);
        }

        return (null, 0);
    }

    private static (string Text, byte[] Bytes)? RootName(CopyState copy, int root)
    {
        var document = copy.Source.Document;
        if (document.LoadNumber(root) is not PdfDictionary dictionary || document.Res(dictionary["T"]) is not PdfString title)
            return null;
        var bytes = title.Value.ToArray();
        return (TextStrings.Decode(bytes), bytes);
    }

    // ---------------------------------------------------------------- form resources (/AcroForm /DR)

    private readonly Dictionary<string, Dictionary<string, (SourceState Owner, PdfObject Value)>> _formResources = new(StringComparer.Ordinal);

    private void MergeFormResources()
    {
        foreach (var state in _contributing)
        {
            var document = state.Document;
            if (document.AcroForm is not { } form)
                continue;
            if (!SourceDocument.TryRead(() => document.Dict(form["DR"]), out var resources))
            {
                AddWarning(AssemblyWarningCode.FormUnreadable, state.Index, 1, "the form's resources could not be read and were left out");
                continue;
            }

            if (resources is null)
                continue;
            state.FormResources = resources;
            foreach (var category in resources)
            {
                if (category.Key.Value == "ProcSet" || document.Dict(category.Value) is not { } entries)
                    continue;
                if (!_formResources.TryGetValue(category.Key.Value, out var merged))
                {
                    merged = new Dictionary<string, (SourceState, PdfObject)>(StringComparer.Ordinal);
                    _formResources[category.Key.Value] = merged;
                }

                foreach (var entry in entries)
                {
                    var name = entry.Key.Value;
                    if (!merged.TryGetValue(name, out var existing))
                    {
                        merged[name] = (state, entry.Value);
                        continue;
                    }

                    if (SameResource(existing, (state, entry.Value)))
                        continue;
                    var renamed = name;
                    for (var n = 2; merged.ContainsKey(renamed); n++)
                        renamed = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}_{n}");
                    merged[renamed] = (state, entry.Value);
                    state.HasResourceRenames = true;
                    _resourcesRenamed++;
                    if (category.Key.Value == "Font")
                        state.FontRenames[name] = renamed;
                }
            }
        }
    }

    /// <summary>Two resources count as the same only when both are plain dictionaries with no references that are equal: anything with a reference points into its own document, so the two differ.</summary>
    private static bool SameResource((SourceState Owner, PdfObject Value) a, (SourceState Owner, PdfObject Value) b)
    {
        var x = a.Owner.Document.Res(a.Value);
        var y = b.Owner.Document.Res(b.Value);
        return x is PdfDictionary dx && y is PdfDictionary dy
            && !PdfObjects.EnumerateReferences(dx).Any() && !PdfObjects.EnumerateReferences(dy).Any()
            && PdfObjects.DeepEquals(dx, dy);
    }

    // ---------------------------------------------------------------- pages

    private void EmitPage(OutputPage page, int parent)
    {
        var copy = page.Copy;
        var state = copy.Source;
        var document = state.Document;
        var slot = document.Pages[page.SourcePage];
        var dictionary = slot.Dictionary.Without("Parent").Without("B").Without("Annots").With("Type", new PdfName("Page"));
        PdfObject? inheritedResources = null; // already in output terms: it is added after the page's own entries are copied

        if (dictionary["Resources"] is null or PdfNull)
        {
            dictionary = dictionary.Without("Resources");
            if (slot.Inherited.Resources is { } resources)
                inheritedResources = HoistedResources(copy, slot.Inherited, resources);
        }

        if (dictionary["MediaBox"] is null or PdfNull)
        {
            dictionary = dictionary.Without("MediaBox");
            if (slot.Inherited.MediaBox is { } box)
            {
                dictionary = dictionary.With("MediaBox", box);
            }
            else
            {
                dictionary = dictionary.With("MediaBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792)]));
                state.MediaBoxDefaulted++;
            }
        }

        if (dictionary["CropBox"] is null or PdfNull)
        {
            dictionary = dictionary.Without("CropBox");
            if (slot.Inherited.CropBox is { } crop)
                dictionary = dictionary.With("CropBox", crop);
        }

        if (dictionary["Rotate"] is null or PdfNull)
        {
            dictionary = dictionary.Without("Rotate");
            if (slot.Inherited.Rotate is { } rotate)
                dictionary = dictionary.With("Rotate", rotate);
        }

        var rewritten = (PdfDictionary)Rewrite(copy, dictionary);
        if (inheritedResources is not null)
            rewritten = rewritten.With("Resources", inheritedResources);
        rewritten = rewritten.With("Parent", new PdfReference(parent));
        var annotations = PageAnnotations(copy, slot);
        if (annotations.Count > 0)
            rewritten = rewritten.With("Annots", new PdfArray(annotations));
        _writer.WriteObject(page.Number, rewritten);
    }

    /// <summary>
    /// The /Resources a page inherited, in output terms. A direct dictionary is written once as an object of its own and shared by the pages below the
    /// node that had it; a reference is mapped like any other.
    /// </summary>
    private PdfObject HoistedResources(CopyState copy, Inherited inherited, PdfObject resources)
    {
        if (resources is not PdfDictionary direct || inherited.ResourcesOwner is not { } owner)
            return Rewrite(copy, resources);
        if (_hoistedResources.TryGetValue(owner, out var existing))
            return existing;
        var number = NewNumber();
        _hoistedResources[owner] = new PdfReference(number);
        _writer.WriteObject(number, Rewrite(copy, direct));
        return _hoistedResources[owner];
    }

    private List<PdfObject> PageAnnotations(CopyState copy, PageSlot slot)
    {
        var document = copy.Source.Document;
        var result = new List<PdfObject>();
        if (document.Arr(slot.Dictionary["Annots"]) is not { } annots)
            return result;
        foreach (var item in annots)
        {
            if (document.Dict(item) is not { } annotation)
                continue;
            if (IsDeadLink(copy, annotation))
            {
                _linksRemoved++;
                continue;
            }

            if (item is PdfReference reference)
            {
                if (MapReference(copy, reference) is not PdfNull and var mapped)
                    result.Add(mapped);
            }
            else
            {
                result.Add(Rewrite(copy, TransformAnnotation(copy, annotation)));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- copying objects

    private PdfObject MapReference(CopyState copy, PdfReference reference)
    {
        var state = copy.Source;
        var document = state.Document;
        if (document.PageByObject.TryGetValue(reference.Number, out var pageIndex))
            return copy.OutputPage(pageIndex) is { } number ? new PdfReference(number) : PdfNull.Instance;
        if (document.Blocked.Contains(reference.Number))
            return PdfNull.Instance;
        if (!document.Revision.TryGetEntry(reference.Number, out var entry) || entry.Type == XrefEntryType.Free || entry.Generation != reference.Generation)
            return PdfNull.Instance;

        var owned = state.Owned.Contains(reference.Number);
        var map = owned ? copy.OwnedMap : state.Shared;
        if (!map.TryGetValue(reference.Number, out var mapped))
        {
            mapped = NewNumber();
            map[reference.Number] = mapped;
            _queue.Enqueue(new WorkItem(copy, reference, mapped, owned));
        }

        return new PdfReference(mapped);
    }

    /// <summary>Copies a direct object into the output: references become output references (queued for copying), page references follow their pages.</summary>
    private PdfObject Rewrite(CopyState copy, PdfObject value, int depth = 0)
    {
        if (depth > MaxRewriteDepth)
            throw AssemblyAbort.Limit(AssemblyLimit.NestingDepth, "an object is nested too deeply", copy.Source.Index, copy.Source.Document.Name);
        switch (value)
        {
            case PdfReference reference:
                return MapReference(copy, reference);
            case PdfArray array:
                var items = new PdfObject[array.Count];
                for (var i = 0; i < items.Length; i++)
                    items[i] = Rewrite(copy, array[i], depth + 1);
                return new PdfArray(items);
            case PdfDictionary dictionary:
                return RewriteDictionary(copy, dictionary, depth);
            case PdfStream stream:
                return new PdfStream(RewriteDictionary(copy, stream.Dictionary.Without("Length"), depth), stream.RawData);
            default:
                return value;
        }
    }

    private PdfDictionary RewriteDictionary(CopyState copy, PdfDictionary dictionary, int depth)
    {
        var entries = ImmutableArray.CreateBuilder<KeyValuePair<PdfName, PdfObject>>(dictionary.Count);
        foreach (var entry in dictionary)
        {
            // The structure tree is not carried, so its back-pointers go with it.
            if (entry.Key.Value is "StructParent" or "StructParents")
                continue;
            entries.Add(new KeyValuePair<PdfName, PdfObject>(entry.Key, Rewrite(copy, entry.Value, depth + 1)));
        }

        return PdfDictionary.FromTrusted(entries.ToImmutable());
    }

    private void Drain()
    {
        while (_queue.Count > 0)
        {
            CheckCancelled();
            var item = _queue.Dequeue();
            var state = item.Copy.Source;
            var loaded = state.Document.Load(item.Reference) ?? PdfNull.Instance;
            if (item.Owned && loaded is PdfDictionary dictionary)
                loaded = TransformOwned(item.Copy, item.Reference.Number, dictionary);
            else if (loaded is PdfStream stream && state.StreamsNeedingResources.Contains(item.Reference.Number) && state.FormResources is { } resources)
                loaded = new PdfStream(stream.Dictionary.With("Resources", resources), stream.RawData);

            var rewritten = Rewrite(item.Copy, loaded);
            if (item.Owned && rewritten is PdfDictionary { } result && result["P"] is PdfNull)
                rewritten = result.Without("P");
            _writer.WriteObject(item.Number, rewritten);
        }
    }

    private void AddWarning(AssemblyWarningCode code, int source, int count, string message) =>
        _warnings.Add(new AssemblyWarning(code, source, count, message));

    private void CollectWarnings()
    {
        foreach (var state in _contributing)
        {
            var index = state.Index;
            var info = _statuses[index].Info;
            if (info is { IsTagged: true })
                AddWarning(AssemblyWarningCode.TagsDropped, index, 1, "the structure tree (accessibility tags) is not carried to the new file");
            if (info is { HasSignatures: true })
                AddWarning(AssemblyWarningCode.SignaturesNotKept, index, state.SignaturesCleared, "digital signatures are not kept in the new file");
            if (state.MediaBoxDefaulted > 0)
                AddWarning(AssemblyWarningCode.MediaBoxDefaulted, index, state.MediaBoxDefaulted, "pages with no media box were given the Letter size");
        }

        var fields = _fieldsRenamed;
        if (fields > 0)
            AddWarning(AssemblyWarningCode.FieldsRenamed, -1, fields, "form fields with the same name were renamed");
        if (_namesRenamed > 0)
            AddWarning(AssemblyWarningCode.DestinationsRenamed, -1, _namesRenamed, "named destinations with the same name were renamed");
        if (_resourcesRenamed > 0)
            AddWarning(AssemblyWarningCode.FormResourcesRenamed, -1, _resourcesRenamed, "form resources with the same name were renamed");
    }
}
