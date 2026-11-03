using System.Text;

namespace MikePDF.PdfWriter.Assembly;

// Destinations, actions, annotations and fields (transformed as they are copied), and everything that hangs off the catalog.
internal sealed partial class AssemblyJob
{
    private readonly Dictionary<string, int> _nameTargets = new(StringComparer.Ordinal);
    private readonly List<(string Key, SourceState State, NamedDestination Destination)> _outputNames = [];

    private static PdfName Name(string value) => new(value);

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(new PdfName(key), value);

    // ---------------------------------------------------------------- destinations

    /// <summary>Where a destination (an array, a name, a dictionary holding an array) leads, in the source it came from.</summary>
    private static ResolvedDestination Resolve(SourceState state, PdfObject? value)
    {
        if (value is null or PdfNull)
            return new ResolvedDestination(DestinationKind.Absent, -1, [], null);
        var document = state.Document;
        var resolved = document.Res(value);
        if (resolved is null)
            return Dead();
        string? key = resolved switch
        {
            PdfString text => Encoding.Latin1.GetString(text.Value),
            PdfName name => name.Value,
            _ => null,
        };
        if (key is not null)
        {
            if (state.NamesUnreadable || document.ConflictingNames.Contains(key) || !document.NamedDestinations.TryGetValue(key, out var named))
                return Dead();
            return new ResolvedDestination(DestinationKind.Page, named.Page, named.Tail, key);
        }

        return document.DestinationTarget(resolved) is { } target
            ? new ResolvedDestination(DestinationKind.Page, target.Page, target.Tail, null)
            : Dead();

        static ResolvedDestination Dead() => new(DestinationKind.Dead, -1, [], null);
    }

    /// <summary>
    /// The destination to write for a link that survives, still in source terms: the output page is reached through the source page reference, which
    /// <see cref="MapReference"/> maps. A name is kept (under its output name) while it still leads where the link leads; otherwise the destination is explicit.
    /// </summary>
    private PdfObject DestinationValue(CopyState copy, ResolvedDestination destination)
    {
        var state = copy.Source;
        if (destination.Name is { } key && state.NameRename.TryGetValue(key, out var final)
            && _nameTargets.TryGetValue(final, out var target) && copy.OutputPage(destination.Page) == target)
        {
            return new PdfString(Encoding.Latin1.GetBytes(final));
        }

        return new PdfArray([state.Document.Pages[destination.Page].Ref, .. destination.Tail]);
    }

    private static bool LeadsOutside(SourceState state, ResolvedDestination destination) =>
        destination.Kind != DestinationKind.Page || !state.Included.Contains(destination.Page);

    /// <summary>A link whose destination is dead or is a page that is not in the output (decisions.md: dropped, not left inert).</summary>
    private static bool IsDeadLink(CopyState copy, PdfDictionary annotation)
    {
        if (annotation.GetName("Subtype") != "Link")
            return false;
        var state = copy.Source;
        if (annotation["Dest"] is { } dest and not PdfNull)
            return LeadsOutside(state, Resolve(state, dest));
        if (state.Document.Dict(annotation["A"]) is { } action && action.GetName("S") == "GoTo")
            return LeadsOutside(state, Resolve(state, action["D"]));
        return false;
    }

    /// <summary>The action with its GoTo destinations retargeted, or null when it leads to a page that is not in the output. A /Next chain loses its dead links only.</summary>
    private PdfDictionary? RewriteAction(CopyState copy, PdfObject value, int depth)
    {
        if (depth > _limits.MaxActionDepth)
            throw AssemblyAbort.Limit(AssemblyLimit.ActionDepth, "actions are chained too deeply", copy.Source.Index, copy.Source.Document.Name);
        var state = copy.Source;
        var document = state.Document;
        if (document.Dict(value) is not { } action)
        {
            _linksRemoved++;
            return null;
        }

        if (action.GetName("S") == "GoTo")
        {
            var destination = Resolve(state, action["D"]);
            if (LeadsOutside(state, destination))
            {
                _linksRemoved++;
                return null;
            }

            action = action.With("D", DestinationValue(copy, destination));
        }

        if (action["Next"] is { } next and not PdfNull)
        {
            switch (document.Res(next))
            {
                case PdfDictionary:
                    action = RewriteAction(copy, next, depth + 1) is { } kept ? action.With("Next", kept) : action.Without("Next");
                    break;
                case PdfArray list:
                    var rewritten = new List<PdfObject>();
                    foreach (var item in list)
                    {
                        if (RewriteAction(copy, item, depth + 1) is { } keptItem)
                            rewritten.Add(keptItem);
                    }

                    action = rewritten.Count > 0 ? action.With("Next", new PdfArray(rewritten)) : action.Without("Next");
                    break;
                default:
                    action = action.Without("Next");
                    break;
            }
        }

        return action;
    }

    // ---------------------------------------------------------------- annotations and fields

    private PdfDictionary TransformAnnotation(CopyState copy, PdfDictionary annotation)
    {
        var state = copy.Source;
        var document = state.Document;
        if (annotation["Dest"] is { } dest and not PdfNull)
        {
            var resolved = Resolve(state, dest);
            annotation = LeadsOutside(state, resolved) ? annotation.Without("Dest") : annotation.With("Dest", DestinationValue(copy, resolved));
        }

        if (annotation["A"] is { } action and not PdfNull)
            annotation = RewriteAction(copy, action, 0) is { } kept ? annotation.With("A", kept) : annotation.Without("A");

        if (annotation["AA"] is { } additional and not PdfNull)
            annotation = RewriteAdditionalActions(copy, annotation, additional);

        annotation = RenameFonts(state, annotation);
        if (state.HasResourceRenames && document.Dict(annotation["AP"]) is { } appearance)
            MarkStreamsNeedingResources(state, appearance);
        return annotation;
    }

    private PdfDictionary RewriteAdditionalActions(CopyState copy, PdfDictionary owner, PdfObject additional)
    {
        if (copy.Source.Document.Dict(additional) is not { } triggers)
            return owner.Without("AA");
        var kept = new List<KeyValuePair<PdfName, PdfObject>>();
        foreach (var trigger in triggers)
        {
            if (RewriteAction(copy, trigger.Value, 0) is { } rewritten)
                kept.Add(new KeyValuePair<PdfName, PdfObject>(trigger.Key, rewritten));
        }

        return kept.Count > 0 ? owner.With("AA", new PdfDictionary(kept)) : owner.Without("AA");
    }

    private static PdfDictionary RenameFonts(SourceState state, PdfDictionary dictionary)
    {
        if (state.FontRenames.Count == 0 || state.Document.Res(dictionary["DA"]) is not PdfString da)
            return dictionary;
        var bytes = da.Value;
        var output = new List<byte>(bytes.Length + 8);
        var changed = false;
        for (var i = 0; i < bytes.Length;)
        {
            if (bytes[i] == (byte)'/')
            {
                var end = i + 1;
                while (end < bytes.Length && PdfBytes.IsRegular(bytes[end]))
                    end++;
                if (state.FontRenames.TryGetValue(Encoding.Latin1.GetString(bytes[(i + 1)..end]), out var renamed))
                {
                    output.Add((byte)'/');
                    output.AddRange(Encoding.Latin1.GetBytes(renamed));
                    changed = true;
                    i = end;
                    continue;
                }
            }

            output.Add(bytes[i]);
            i++;
        }

        return changed ? dictionary.With("DA", new PdfString([.. output], da.Format)) : dictionary;
    }

    private static void MarkStreamsNeedingResources(SourceState state, PdfDictionary appearance)
    {
        var document = state.Document;
        void Check(PdfObject? value)
        {
            if (value is PdfReference reference && document.Load(reference) is PdfStream stream && !stream.Dictionary.ContainsKey("Resources"))
                state.StreamsNeedingResources.Add(reference.Number);
        }

        foreach (var key in new[] { "N", "R", "D" })
        {
            if (appearance[key] is not { } value)
                continue;
            if (value is PdfReference reference && document.Load(reference) is PdfDictionary states)
            {
                foreach (var entry in states)
                    Check(entry.Value);
            }
            else
            {
                Check(value);
                if (value is PdfDictionary direct)
                {
                    foreach (var entry in direct)
                        Check(entry.Value);
                }
            }
        }
    }

    /// <summary>What happens to an object this copy owns when it is copied: an annotation's actions and default appearance, a field's kids, name and signature.</summary>
    private PdfDictionary TransformOwned(CopyState copy, int number, PdfDictionary dictionary)
    {
        if (dictionary.ContainsKey("Subtype"))
            dictionary = TransformAnnotation(copy, dictionary);
        if (copy.FieldNodes.Contains(number))
            dictionary = TransformField(copy, number, dictionary);
        return dictionary;
    }

    private PdfDictionary TransformField(CopyState copy, int number, PdfDictionary field)
    {
        var state = copy.Source;
        if (state.Document.Arr(field["Kids"]) is { } kids)
        {
            var kept = new List<PdfObject>();
            foreach (var kid in kids)
            {
                if (kid is PdfReference reference && copy.FieldNodes.Contains(reference.Number))
                    kept.Add(kid);
            }

            field = field.With("Kids", new PdfArray(kept));
        }

        if (copy.RootRename.TryGetValue(number, out var renamed))
            field = field.With("T", new PdfString(renamed));
        if (copy.RootDefaults.TryGetValue(number, out var defaults))
        {
            if (defaults.Da is not null)
                field = field.With("DA", defaults.Da);
            if (defaults.Q is not null)
                field = field.With("Q", defaults.Q);
        }

        if (copy.SignatureNodes.Contains(number) && field["V"] is not null and not PdfNull)
        {
            field = field.Without("V");
            state.SignaturesCleared++;
            _signaturesCleared++;
        }

        return RenameFonts(state, field);
    }

    // ---------------------------------------------------------------- named destinations

    private void BuildNamedDestinations()
    {
        foreach (var state in _contributing)
        {
            var document = state.Document;
            if (!document.HasNamedDestinations)
                continue;
            if (!SourceDocument.TryRead(() => document.NamedDestinations, out var names))
            {
                state.NamesUnreadable = true;
                AddWarning(AssemblyWarningCode.DestinationsUnreadable, state.Index, 1, "the named destinations could not be read and were left out");
                continue;
            }

            _namesDropped += document.ConflictingNames.Count;
            foreach (var pair in names.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                var destination = pair.Value;
                if (!state.Included.Contains(destination.Page))
                {
                    _namesDropped++;
                    continue;
                }

                var final = pair.Key;
                if (_nameTargets.ContainsKey(final))
                {
                    var suffix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"~{state.Index + 1}");
                    final = Encoding.Latin1.GetString(TextStrings.Append(Encoding.Latin1.GetBytes(pair.Key), suffix));
                    while (_nameTargets.ContainsKey(final))
                        final += "~";
                    _namesRenamed++;
                }

                state.NameRename[pair.Key] = final;
                _nameTargets[final] = state.Copies[0].PageOut[destination.Page];
                _outputNames.Add((final, state, destination));
                _namesKept++;
            }
        }
    }

    private void BuildNameTree(out PdfObject? names)
    {
        names = null;
        if (_outputNames.Count == 0)
            return;
        var pairs = new List<PdfObject>(_outputNames.Count * 2);
        foreach (var (key, state, destination) in _outputNames.OrderBy(static n => n.Key, StringComparer.Ordinal))
        {
            var copy = state.Copies[0];
            var page = new PdfReference(copy.PageOut[destination.Page]);
            pairs.Add(new PdfString(Encoding.Latin1.GetBytes(key)));
            pairs.Add(new PdfArray([page, .. destination.Tail.Select(t => Rewrite(copy, t))]));
        }

        var number = NewNumber();
        _writer.WriteObject(number, new PdfDictionary([Entry("Names", new PdfArray(pairs))]));
        names = new PdfDictionary([Entry("Dests", new PdfReference(number))]);
    }

    // ---------------------------------------------------------------- page labels

    private void BuildPageLabels(out PdfObject? labels)
    {
        labels = null;
        var any = false;
        foreach (var state in _contributing)
        {
            var document = state.Document;
            if (!document.HasPageLabels)
                continue;
            if (!SourceDocument.TryRead(() => document.Labels, out var ranges))
                AddWarning(AssemblyWarningCode.PageLabelsUnreadable, state.Index, 1, "the page labels could not be read and were left out");
            else if (ranges is { Count: > 0 })
                any = true;
        }

        if (!any)
            return;

        // A page keeps the label it had in its source (a source without labels counts its pages 1, 2, 3), so labels follow their pages.
        var nums = new List<PdfObject>();
        string? style = null;
        byte[] prefix = [];
        long expected = 0;
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var (pageStyle, pagePrefix, number) = page.Copy.Source.Document.LabelOf(page.SourcePage);
            var continues = style is not null && style == pageStyle && prefix.AsSpan().SequenceEqual(pagePrefix) && (pageStyle.Length == 0 || number == expected);
            if (!continues)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>();
                if (pageStyle.Length > 0)
                    entries.Add(Entry("S", Name(pageStyle)));
                if (pagePrefix.Length > 0)
                    entries.Add(Entry("P", new PdfString(pagePrefix)));
                if (number != 1 && pageStyle.Length > 0)
                    entries.Add(Entry("St", new PdfInteger(number)));
                nums.Add(new PdfInteger(i));
                nums.Add(new PdfDictionary(entries));
                _labelRanges++;
            }

            style = pageStyle;
            prefix = pagePrefix;
            expected = number + 1;
        }

        labels = new PdfDictionary([Entry("Nums", new PdfArray(nums))]);
    }

    // ---------------------------------------------------------------- outline

    private void BuildOutline(out PdfObject? outlines)
    {
        outlines = null;
        var tops = new List<OutlineNode>();
        foreach (var state in _contributing)
        {
            var document = state.Document;
            var kept = new List<OutlineNode>();
            if (document.HasOutline && !SourceDocument.TryRead(() => ReadOutline(state), out kept!))
            {
                kept = [];
                AddWarning(AssemblyWarningCode.OutlineUnreadable, state.Index, 1, "the outline could not be read and was left out");
            }

            if (_options.WrapSourceOutlines)
            {
                var first = _pages[state.FirstOutputPage];
                tops.Add(new OutlineNode
                {
                    Title = new PdfString(TextStrings.Encode(state.Document.Name)),
                    Page = first.SourcePage,
                    Tail = [Name("Fit")],
                    Kids = kept,
                    Source = state,
                });
            }
            else
            {
                tops.AddRange(kept);
            }
        }

        if (tops.Count == 0)
            return;
        var root = NewNumber();
        var (firstRef, lastRef, visible) = EmitOutlineLevel(tops, root);
        _writer.WriteObject(root, new PdfDictionary([Entry("Type", Name("Outlines")), Entry("First", firstRef), Entry("Last", lastRef), Entry("Count", new PdfInteger(visible))]));
        outlines = new PdfReference(root);
    }

    /// <summary>
    /// The entries of a source's outline that go to a page that is in the output. An entry that goes to a page that is not (or nowhere) is dropped and
    /// its surviving children move up to its place; an entry with no destination of its own (a URI, say, or a heading) stays while its parent does and,
    /// for a heading, while it still has something under it.
    /// </summary>
    private List<OutlineNode> ReadOutline(SourceState state)
    {
        var document = state.Document;
        if (document.Dict(document.Catalog["Outlines"]) is not { } root)
            return [];
        var visited = new HashSet<int>();
        return Level(root["First"], 0, true);

        List<OutlineNode> Level(PdfObject? first, int depth, bool parentKept)
        {
            if (depth >= _limits.MaxOutlineDepth)
                throw AssemblyAbort.Limit(AssemblyLimit.OutlineDepth, "the outline is nested too deeply", document.Index, document.Name);
            var result = new List<OutlineNode>();
            var current = first;
            while (current is PdfReference reference)
            {
                CheckCancelled();
                if (!visited.Add(reference.Number))
                    throw AssemblyAbort.Structure("an outline entry is reachable twice", document.Index, document.Name);
                if (visited.Count > _limits.MaxOutlineItems)
                    throw AssemblyAbort.Limit(AssemblyLimit.OutlineItems, "the outline has too many entries", document.Index, document.Name);
                if (document.Dict(reference) is not { } item)
                    break;

                var destination = new ResolvedDestination(DestinationKind.Absent, -1, [], null);
                PdfObject? action = null;
                if (item["Dest"] is { } dest and not PdfNull)
                {
                    destination = Resolve(state, dest);
                }
                else if (document.Dict(item["A"]) is { } a)
                {
                    if (a.GetName("S") == "GoTo")
                        destination = Resolve(state, a["D"]);
                    else
                        action = a;
                }

                var toIncludedPage = destination.Kind == DestinationKind.Page && state.Included.Contains(destination.Page);
                var keptSelf = toIncludedPage || (destination.Kind == DestinationKind.Absent && parentKept);
                var kids = Level(item["First"], depth + 1, keptSelf);
                if (keptSelf && destination.Kind == DestinationKind.Absent && action is null && kids.Count == 0)
                    keptSelf = false;
                if (keptSelf)
                {
                    result.Add(new OutlineNode
                    {
                        Title = document.Res(item["Title"]) is PdfString title ? new PdfString(title.Value, title.Format) : new PdfString([]),
                        Page = toIncludedPage ? destination.Page : -1,
                        Tail = toIncludedPage ? destination.Tail : [],
                        Action = action,
                        Open = document.Res(item["Count"]) is not PdfInteger { Value: < 0 },
                        Color = item["C"] is { } c and not PdfNull ? c : null,
                        Flags = item["F"] is { } f and not PdfNull ? f : null,
                        Kids = kids,
                        Source = state,
                    });
                }
                else
                {
                    _outlineRemoved++;
                    result.AddRange(kids);
                }

                current = item["Next"];
            }

            return result;
        }
    }

    private (PdfReference First, PdfReference Last, long Visible) EmitOutlineLevel(List<OutlineNode> nodes, int parent)
    {
        var numbers = nodes.Select(_ => NewNumber()).ToList();
        long visible = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            CheckCancelled();
            var node = nodes[i];
            var copy = node.Source!.Copies[0];
            var entries = new List<KeyValuePair<PdfName, PdfObject>>
            {
                Entry("Title", node.Title),
                Entry("Parent", new PdfReference(parent)),
            };
            if (i > 0)
                entries.Add(Entry("Prev", new PdfReference(numbers[i - 1])));
            if (i < nodes.Count - 1)
                entries.Add(Entry("Next", new PdfReference(numbers[i + 1])));
            long below = 0;
            if (node.Kids.Count > 0)
            {
                var (first, last, childVisible) = EmitOutlineLevel(node.Kids, numbers[i]);
                below = childVisible;
                entries.Add(Entry("First", first));
                entries.Add(Entry("Last", last));
                entries.Add(Entry("Count", new PdfInteger(node.Open ? below : -below)));
            }

            if (node.Page >= 0 && copy.OutputPage(node.Page) is { } page)
                entries.Add(Entry("Dest", new PdfArray([new PdfReference(page), .. node.Tail.Select(t => Rewrite(copy, t))])));
            else if (node.Action is not null)
                entries.Add(Entry("A", Rewrite(copy, node.Action)));
            if (node.Color is not null)
                entries.Add(Entry("C", Rewrite(copy, node.Color)));
            if (node.Flags is not null)
                entries.Add(Entry("F", Rewrite(copy, node.Flags)));
            _writer.WriteObject(numbers[i], new PdfDictionary(entries));
            _outlineKept++;
            visible += 1 + (node.Open ? below : 0);
        }

        return (new PdfReference(numbers[0]), new PdfReference(numbers[^1]), visible);
    }

    // ---------------------------------------------------------------- the form

    private static PdfReference ReferenceOf(SourceDocument document, int number) =>
        document.Revision.TryGetEntry(number, out var entry) ? new PdfReference(number, entry.Generation) : new PdfReference(number);

    private void BuildForm(out PdfObject? form)
    {
        form = null;
        var fields = new List<PdfObject>();
        var calculation = new List<PdfObject>();
        var needAppearances = false;
        foreach (var copy in _copies)
        {
            var document = copy.Source.Document;
            foreach (var root in copy.Roots)
            {
                if (MapReference(copy, ReferenceOf(document, root)) is PdfReference mapped)
                    fields.Add(mapped);
            }

            if (copy.Index != 0 || document.AcroForm is not { } source)
                continue;
            if (copy.Roots.Count > 0 && document.Res(source["NeedAppearances"]) is PdfBoolean { Value: true })
                needAppearances = true;
            if (document.Arr(source["CO"]) is { } order)
            {
                foreach (var item in order)
                {
                    if (item is PdfReference reference && copy.FieldNodes.Contains(reference.Number) && MapReference(copy, reference) is PdfReference mapped)
                        calculation.Add(mapped);
                }
            }
        }

        PdfDictionary? resources = null;
        if (_formResources.Count > 0)
        {
            var categories = new List<KeyValuePair<PdfName, PdfObject>>();
            foreach (var category in _formResources)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>();
                foreach (var resource in category.Value)
                    entries.Add(Entry(resource.Key, Rewrite(resource.Value.Owner.Copies[0], resource.Value.Value)));
                categories.Add(Entry(category.Key, new PdfDictionary(entries)));
            }

            resources = new PdfDictionary(categories);
        }

        if (fields.Count == 0 && resources is null)
            return;
        var dictionary = new List<KeyValuePair<PdfName, PdfObject>> { Entry("Fields", new PdfArray(fields)) };
        var (da, q) = OutputFormDefaults();
        if (da is not null)
            dictionary.Add(Entry("DA", new PdfString(da.Value, da.Format)));
        if (q != 0)
            dictionary.Add(Entry("Q", new PdfInteger(q)));
        if (resources is not null)
            dictionary.Add(Entry("DR", resources));
        if (needAppearances)
            dictionary.Add(Entry("NeedAppearances", PdfBoolean.True));
        if (calculation.Count > 0)
            dictionary.Add(Entry("CO", new PdfArray(calculation)));
        form = new PdfDictionary(dictionary);
    }

    // ---------------------------------------------------------------- optional content

    /// <summary>
    /// The groups of every source and the union of their default states. A source whose base state is OFF is written as an explicit list of what is off,
    /// so the output can use the ON base state for everyone.
    /// </summary>
    private void BuildOptionalContent(out PdfObject? properties)
    {
        properties = null;
        var groups = new List<PdfObject>();
        var off = new List<PdfObject>();
        var order = new List<PdfObject>();
        var radio = new List<PdfObject>();
        foreach (var state in _contributing)
        {
            var document = state.Document;
            var copy = state.Copies[0];
            if (document.Dict(document.Catalog["OCProperties"]) is not { } source || document.Arr(source["OCGs"]) is not { } listed)
                continue;
            if (listed.Count > _limits.MaxOptionalContentGroups)
                throw AssemblyAbort.Limit(AssemblyLimit.OptionalContentGroups, "the document has more optional content groups than the limit", document.Index, document.Name);
            var all = listed.OfType<PdfReference>().ToList();
            groups.AddRange(all.Select(g => MapReference(copy, g)).Where(static g => g is PdfReference));
            var defaults = document.Dict(source["D"]);
            var on = defaults is not null && document.Arr(defaults["ON"]) is { } onList ? onList.OfType<PdfReference>().Select(static r => r.Number).ToHashSet() : [];
            IEnumerable<PdfReference> turnedOff = defaults is not null && document.Arr(defaults["OFF"]) is { } offList ? offList.OfType<PdfReference>() : [];
            if (defaults?.GetName("BaseState") == "OFF")
                turnedOff = all.Where(g => !on.Contains(g.Number));
            off.AddRange(turnedOff.Select(g => MapReference(copy, g)).Where(static g => g is PdfReference));
            if (defaults is not null && document.Arr(defaults["Order"]) is { } orderList)
                order.AddRange(orderList.Select(item => Rewrite(copy, item)));
            if (defaults is not null && document.Arr(defaults["RBGroups"]) is { } radioList)
                radio.AddRange(radioList.Select(item => Rewrite(copy, item)));
        }

        if (groups.Count == 0)
            return;
        var entries = new List<KeyValuePair<PdfName, PdfObject>>();
        if (off.Count > 0)
            entries.Add(Entry("OFF", new PdfArray(off)));
        if (order.Count > 0)
            entries.Add(Entry("Order", new PdfArray(order)));
        if (radio.Count > 0)
            entries.Add(Entry("RBGroups", new PdfArray(radio)));
        properties = new PdfDictionary([Entry("OCGs", new PdfArray(groups)), Entry("D", new PdfDictionary(entries))]);
    }

    // ---------------------------------------------------------------- the catalog

    private void EmitCatalog(PdfObject? names, PdfObject? labels, PdfObject? outlines, PdfObject? form, PdfObject? contentProperties)
    {
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Type", Name("Catalog")),
            Entry("Pages", new PdfReference(PageTreeRootNumber)),
        };
        var first = _contributing[0];
        var document = first.Document;
        var copy = first.Copies[0];
        if (document.Res(document.Catalog["Lang"]) is PdfString lang)
            entries.Add(Entry("Lang", new PdfString(lang.Value, lang.Format)));
        if (document.Res(document.Catalog["PageLayout"]) is PdfName layout)
            entries.Add(Entry("PageLayout", layout));
        if (document.Res(document.Catalog["PageMode"]) is PdfName mode
            && (mode.Value is "UseNone" or "UseThumbs" or "FullScreen" || (mode.Value == "UseOutlines" && outlines is not null)))
        {
            entries.Add(Entry("PageMode", mode));
        }

        if (document.Dict(document.Catalog["ViewerPreferences"]) is { } preferences)
            entries.Add(Entry("ViewerPreferences", Rewrite(copy, preferences)));
        if (labels is not null)
            entries.Add(Entry("PageLabels", labels));
        if (names is not null)
            entries.Add(Entry("Names", names));
        if (outlines is not null)
            entries.Add(Entry("Outlines", outlines));
        if (form is not null)
            entries.Add(Entry("AcroForm", form));
        if (contentProperties is not null)
            entries.Add(Entry("OCProperties", contentProperties));
        Drain();
        _writer.WriteObject(CatalogNumber, new PdfDictionary(entries));
    }
}
