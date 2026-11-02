// S11 spike: a PROTOTYPE page assembler on MikePDF.PdfWriter's strict parser and serializer. It merges, extracts and reorders pages from strictly parsed sources
// into a new classic-xref file, and carries over what PDFium's import drops: outlines, named destinations, page labels, links between kept pages,
// AcroForm fields (with a rename rule for clashes) and optional content. It drops tags (documented loss) and everything not listed in the report.
// It exists to measure how much work route B really is (lines, hard cases, time and memory); it is throwaway and not production quality.
using System.Text;
using MikePDF.PdfWriter;

namespace S11;

internal sealed class AsmSource
{
    public required string Name { get; init; }
    public required StrictDocument Doc { get; init; }
    public required int[] Pages { get; init; }                      // zero-based page indices to take, in output order
    // filled by the assembler
    internal List<(int Num, PdfDictionary Dict)> AllPages = [];
    internal HashSet<int> PageObjNums = [];
    internal Dictionary<int, int> IncludedPageNew = [];             // old page object number -> new number
    internal Dictionary<int, int> Map = [];                          // old object number -> new object number
    internal HashSet<int> KeptWidgets = [];
    internal HashSet<int> FieldNodes = [];
    internal List<int> FieldRoots = [];
    internal Dictionary<int, byte[]> RootRename = [];
    internal Dictionary<string, NamedDest> Named = [];              // key = hex of the name bytes
    internal Dictionary<string, byte[]> NameMap = [];               // old name (hex) -> new name bytes
}

internal sealed record NamedDest(byte[] Name, int PageObj, PdfObject[] Tail);

internal sealed class OItem
{
    public PdfString Title = new([]);
    public int PageObj = -1;            // target page (object number) when the item goes to a page
    public PdfObject[] DestTail = [];
    public PdfObject? OtherAction;      // URI etc.
    public long Count;
    public PdfObject? Color, Flags;
    public AsmSource? Owner;
    public bool HadPageDest;
    public List<OItem> Kids = [];
}

internal sealed class Assembler
{
    private readonly List<AsmSource> _sources;
    private readonly Dictionary<int, PdfObject> _out = [];
    private readonly Queue<(AsmSource S, int Old, int New)> _queue = new();
    private int _next = 1;
    public int Issues;
    public List<string> Notes = [];

    /// <summary>Output page order as (source, zero-based page index). Defaults to each source's Pages in turn; set it to interleave sources (insert in the middle).</summary>
    public List<(AsmSource S, int Idx)> Sequence { get; set; }

    public Assembler(List<AsmSource> sources)
    {
        _sources = sources;
        Sequence = sources.SelectMany(src => src.Pages.Select(i => (src, i))).ToList();
    }

    private int NewNum() => _next++;

    // ---------------------------------------------------------------- entry point

    public byte[] Build(bool wrapOutlines)
    {
        var catalogNum = NewNum();
        var pagesNum = NewNum();
        var infoNum = NewNum();

        foreach (var s in _sources) Prepare(s);

        // 1. Pages: allocate numbers first so references between kept pages can be mapped, then copy.
        var outPages = new List<int>();
        foreach (var (s, idx) in Sequence)
            {
                var (oldNum, _) = s.AllPages[idx];
                var n = NewNum();
                s.IncludedPageNew[oldNum] = n;     // duplicates (same page twice) keep the last; a later pass could clone, the spike does not need it
                outPages.Add(n);
            }

        // 2. Pre-pass over kept pages: widgets, fields, so the field tree can be pruned when it is copied.
        foreach (var s in _sources) CollectWidgets(s);
        AssignFieldNames();

        // 3. Page objects
        var pageDicts = new Dictionary<int, PdfDictionary>();
        foreach (var (s, idx) in Sequence)
            {
                var (oldNum, dict) = s.AllPages[idx];
                var newNum = s.IncludedPageNew[oldNum];
                dict = dict.Without("Parent").Without("StructParents").Without("B");
                if (dict["Annots"] is { } annots) dict = dict.With("Annots", FilterAnnots(s, annots));
                _out[newNum] = Rewrite(s, dict);
                pageDicts[newNum] = (PdfDictionary)_out[newNum];
            }

        // 3b. Named destinations first: the rename map must exist before link annotations are copied.
        var names = BuildNamedDests();

        // 4. Drain the copy queue (closure of everything the pages reference).
        Drain();

        // 5. Catalog parts
        var catalog = new List<KeyValuePair<PdfName, PdfObject>> { Kv("Type", new PdfName("Catalog")), Kv("Pages", new PdfReference(pagesNum)) };
        var labels = BuildPageLabels(outPages);
        if (labels is not null) catalog.Add(Kv("PageLabels", labels));
        if (names is not null) catalog.Add(Kv("Names", new PdfDictionary([Kv("Dests", names)])));
        var outline = BuildOutline(wrapOutlines);
        if (outline is not null)
        {
            catalog.Add(Kv("Outlines", outline));
            catalog.Add(Kv("PageMode", new PdfName("UseOutlines")));
        }

        var acro = BuildAcroForm();
        if (acro is not null) catalog.Add(Kv("AcroForm", acro));
        var oc = BuildOcProperties();
        if (oc is not null) catalog.Add(Kv("OCProperties", oc));
        Drain();

        // 6. Page tree: two levels above 100 pages
        const int fan = 100;
        PdfObject pagesRoot;
        if (outPages.Count <= fan)
        {
            foreach (var n in outPages) _out[n] = pageDicts[n].With("Parent", new PdfReference(pagesNum));
            pagesRoot = new PdfDictionary([Kv("Type", new PdfName("Pages")), Kv("Kids", new PdfArray(outPages.Select(n => (PdfObject)new PdfReference(n)))), Kv("Count", new PdfInteger(outPages.Count))]);
        }
        else
        {
            var kids = new List<PdfObject>();
            for (var i = 0; i < outPages.Count; i += fan)
            {
                var group = outPages.Skip(i).Take(fan).ToList();
                var nodeNum = NewNum();
                foreach (var n in group) _out[n] = pageDicts[n].With("Parent", new PdfReference(nodeNum));
                _out[nodeNum] = new PdfDictionary([Kv("Type", new PdfName("Pages")), Kv("Parent", new PdfReference(pagesNum)), Kv("Kids", new PdfArray(group.Select(n => (PdfObject)new PdfReference(n)))), Kv("Count", new PdfInteger(group.Count))]);
                kids.Add(new PdfReference(nodeNum));
            }

            pagesRoot = new PdfDictionary([Kv("Type", new PdfName("Pages")), Kv("Kids", new PdfArray(kids)), Kv("Count", new PdfInteger(outPages.Count))]);
        }

        _out[pagesNum] = pagesRoot;
        _out[infoNum] = new PdfDictionary([Kv("Producer", PdfString.FromAscii("MikePDF S11 assembler prototype")), Kv("Title", PdfString.FromAscii(string.Join(" + ", _sources.Select(s => s.Name))))]);
        _out[catalogNum] = new PdfDictionary(catalog);
        return Write(catalogNum, infoNum);
    }

    private static KeyValuePair<PdfName, PdfObject> Kv(string k, PdfObject v) => new(new PdfName(k), v);

    // ---------------------------------------------------------------- reading the sources

    private static PdfObject? Res(StrictDocument d, PdfObject? o)
    {
        for (var i = 0; i < 16 && o is PdfReference r; i++)
        {
            var g = d.GetObject(r.Number);
            o = g.IsSuccess ? g.Value : null;
        }

        return o is PdfNull ? null : o;
    }

    private static PdfDictionary? Dict(StrictDocument d, PdfObject? o) => Res(d, o) switch { PdfDictionary x => x, PdfStream st => st.Dictionary, _ => null };

    private void Prepare(AsmSource s)
    {
        var d = s.Doc;
        var root = Dict(d, d.Root) ?? throw new InvalidDataException("no catalog");
        var visited = new HashSet<int>();
        void Walk(PdfObject? nodeRef, PdfDictionary inherited, int depth)
        {
            if (depth > 48 || nodeRef is null) return;
            if (nodeRef is PdfReference r && !visited.Add(r.Number)) return;
            var node = Dict(d, nodeRef);
            if (node is null) return;
            var inh = inherited;
            foreach (var k in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
                if (node[k] is { } v) inh = inh.With(k, v);
            if (node["Kids"] is { } kidsObj && Res(d, kidsObj) is PdfArray kids)
            {
                foreach (var kid in kids) Walk(kid, inh, depth + 1);
            }
            else if (nodeRef is PdfReference lr)
            {
                var leaf = node;
                foreach (var kv in inherited)
                    if (!leaf.ContainsKey(kv.Key.Value)) leaf = leaf.With(kv.Key.Value, kv.Value);
                // inherited keys from ancestors above this leaf are merged into `inherited` by the callers
                foreach (var kv in inh)
                    if (!leaf.ContainsKey(kv.Key.Value)) leaf = leaf.With(kv.Key.Value, kv.Value);
                s.AllPages.Add((lr.Number, leaf));
                s.PageObjNums.Add(lr.Number);
            }
        }

        Walk(root["Pages"], PdfDictionary.Empty, 0);
        ReadNamedDests(s, root);
    }

    private static void ReadNamedDests(AsmSource s, PdfDictionary root)
    {
        var d = s.Doc;
        void Add(byte[] name, PdfObject? val)
        {
            var arr = Res(d, val) switch { PdfArray a => a, PdfDictionary dd => Res(d, dd["D"]) as PdfArray, _ => null };
            if (arr is null || arr.Count < 1 || arr[0] is not PdfReference pr) return;
            s.Named[Convert.ToHexString(name)] = new NamedDest(name, pr.Number, arr.Skip(1).ToArray());
        }

        void Tree(PdfObject? node, int depth)
        {
            if (depth > 16) return;
            var nd = Dict(d, node);
            if (nd is null) return;
            if (Res(d, nd["Names"]) is PdfArray pairs)
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                    if (Res(d, pairs[i]) is PdfString key) Add(key.Value.ToArray(), pairs[i + 1]);
            if (Res(d, nd["Kids"]) is PdfArray kids)
                foreach (var k in kids) Tree(k, depth + 1);
        }

        if (Dict(d, root["Names"]) is { } names) Tree(names["Dests"], 0);
        if (Dict(d, root["Dests"]) is { } legacy)
            foreach (var kv in legacy) Add(Encoding.Latin1.GetBytes(kv.Key.Value), kv.Value);
    }

    // ---------------------------------------------------------------- links, annotations

    private enum Target { None, Included, Excluded }

    private Target LinkTarget(AsmSource s, PdfDictionary annot)
    {
        var dest = annot["Dest"];
        var action = Dict(s.Doc, annot["A"]);
        if (dest is null && action is not null && action.GetName("S") == "GoTo") dest = action["D"];
        if (dest is null) return Target.None;
        return DestPage(s, dest) is { } pg ? (s.IncludedPageNew.ContainsKey(pg) ? Target.Included : Target.Excluded) : Target.None;
    }

    private int? DestPage(AsmSource s, PdfObject dest)
    {
        var r = Res(s.Doc, dest);
        if (r is PdfArray a && a.Count > 0 && a[0] is PdfReference pr) return pr.Number;
        if (r is PdfString str && s.Named.TryGetValue(Convert.ToHexString(str.Value), out var nd)) return nd.PageObj;
        if (r is PdfName nm && s.Named.TryGetValue(Convert.ToHexString(Encoding.Latin1.GetBytes(nm.Value)), out var nd2)) return nd2.PageObj;
        if (r is PdfDictionary dd) return dd["D"] is { } inner ? DestPage(s, inner) : null;
        return null;
    }

    /// <summary>Links to pages that are not in the output are removed (never retargeted); every other annotation is kept.</summary>
    private PdfObject FilterAnnots(AsmSource s, PdfObject annots)
    {
        if (Res(s.Doc, annots) is not PdfArray arr) return annots;
        var keep = new List<PdfObject>();
        foreach (var a in arr)
        {
            var dict = Dict(s.Doc, a);
            if (dict is not null && dict.GetName("Subtype") == "Link" && LinkTarget(s, dict) == Target.Excluded) continue;
            keep.Add(a);
        }

        return new PdfArray(keep);
    }

    private PdfDictionary RewriteLink(AsmSource s, PdfDictionary d)
    {
        var dest = d["Dest"];
        if (dest is not null && Res(s.Doc, dest) is PdfString or PdfName) d = d.With("Dest", MapNamedRef(s, Res(s.Doc, dest)!));
        if (Dict(s.Doc, d["A"]) is { } act && act.GetName("S") == "GoTo" && act["D"] is { } ad && Res(s.Doc, ad) is PdfString or PdfName)
            d = d.With("A", act.With("D", MapNamedRef(s, Res(s.Doc, ad)!)));
        return d;
    }

    private static PdfObject MapNamedRef(AsmSource s, PdfObject name)
    {
        var bytes = name is PdfString ps ? ps.Value.ToArray() : Encoding.Latin1.GetBytes(((PdfName)name).Value);
        return s.NameMap.TryGetValue(Convert.ToHexString(bytes), out var nb) ? new PdfString(nb) : name;
    }

    // ---------------------------------------------------------------- forms

    private void CollectWidgets(AsmSource s)
    {
        foreach (var idx in Sequence.Where(x => x.S == s).Select(x => x.Idx))
        {
            var (_, page) = s.AllPages[idx];
            if (Res(s.Doc, page["Annots"]) is not PdfArray arr) continue;
            foreach (var a in arr)
            {
                if (a is not PdfReference ar || Dict(s.Doc, a) is not { } ad || ad.GetName("Subtype") != "Widget") continue;
                s.KeptWidgets.Add(ar.Number);
                var cur = ar;
                var cd = ad;
                for (var depth = 0; depth < 32; depth++)
                {
                    s.FieldNodes.Add(cur.Number);
                    if (cd["Parent"] is PdfReference pr && Dict(s.Doc, pr) is { } pd) { cur = pr; cd = pd; continue; }
                    if (!s.FieldRoots.Contains(cur.Number)) s.FieldRoots.Add(cur.Number);
                    break;
                }
            }
        }
    }

    private void AssignFieldNames()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _sources)
            foreach (var root in s.FieldRoots)
            {
                var t = Dict(s.Doc, new PdfReference(root))?["T"] as PdfString;
                var name = t is null ? "" : Encoding.Latin1.GetString(t.Value);
                if (name.Length == 0) continue;
                var candidate = name;
                for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}_{n}";
                if (candidate != name)
                {
                    s.RootRename[root] = Encoding.Latin1.GetBytes(candidate);
                    Notes.Add($"field '{name}' from {s.Name} renamed '{candidate}'");
                }
            }
    }

    private PdfDictionary? BuildAcroForm()
    {
        var roots = new List<PdfObject>();
        var dr = new List<KeyValuePair<PdfName, PdfObject>>();
        var fonts = new Dictionary<string, PdfObject>();
        string? da = null;
        foreach (var s in _sources)
        {
            foreach (var r in s.FieldRoots) roots.Add(new PdfReference(Map(s, r)));
            var af = Dict(s.Doc, Dict(s.Doc, s.Doc.Root)?["AcroForm"]);
            if (af is null || s.FieldRoots.Count == 0) continue;
            if (da is null && Res(s.Doc, af["DA"]) is PdfString ds) da = Encoding.Latin1.GetString(ds.Value);
            if (Dict(s.Doc, af["DR"]) is { } d && Dict(s.Doc, d["Font"]) is { } f)
                foreach (var kv in f) fonts.TryAdd(kv.Key.Value, Rewrite(s, kv.Value));
        }

        if (roots.Count == 0) return null;
        var entries = new List<KeyValuePair<PdfName, PdfObject>> { Kv("Fields", new PdfArray(roots)) };
        if (da is not null) entries.Add(Kv("DA", PdfString.FromAscii(da)));
        if (fonts.Count > 0) entries.Add(Kv("DR", new PdfDictionary([Kv("Font", new PdfDictionary(fonts.Select(kv => Kv(kv.Key, kv.Value))))])));
        return new PdfDictionary(entries);
    }

    // ---------------------------------------------------------------- optional content

    private PdfDictionary? BuildOcProperties()
    {
        var ocgs = new List<PdfObject>();
        var on = new List<PdfObject>();
        var off = new List<PdfObject>();
        var order = new List<PdfObject>();
        var any = false;
        foreach (var s in _sources)
        {
            if (Dict(s.Doc, Dict(s.Doc, s.Doc.Root)?["OCProperties"]) is not { } oc) continue;
            any = true;
            if (Res(s.Doc, oc["OCGs"]) is PdfArray og) ocgs.AddRange(og.Select(x => Rewrite(s, x)));
            if (Dict(s.Doc, oc["D"]) is { } d)
            {
                if (Res(s.Doc, d["ON"]) is PdfArray a1) on.AddRange(a1.Select(x => Rewrite(s, x)));
                if (Res(s.Doc, d["OFF"]) is PdfArray a2) off.AddRange(a2.Select(x => Rewrite(s, x)));
                if (Res(s.Doc, d["Order"]) is PdfArray a3) order.AddRange(a3.Select(x => Rewrite(s, x)));
            }
        }

        if (!any) return null;
        var dd = new List<KeyValuePair<PdfName, PdfObject>>();
        if (on.Count > 0) dd.Add(Kv("ON", new PdfArray(on)));
        if (off.Count > 0) dd.Add(Kv("OFF", new PdfArray(off)));
        if (order.Count > 0) dd.Add(Kv("Order", new PdfArray(order)));
        return new PdfDictionary([Kv("OCGs", new PdfArray(ocgs)), Kv("D", new PdfDictionary(dd))]);
    }

    // ---------------------------------------------------------------- named destinations

    private PdfObject? BuildNamedDests()
    {
        var used = new Dictionary<string, int>(); // hex name -> page new number
        var entries = new List<(byte[] Name, PdfObject Dest)>();
        foreach (var s in _sources)
        {
            foreach (var (hex, nd) in s.Named)
            {
                if (!s.IncludedPageNew.TryGetValue(nd.PageObj, out var np)) continue;     // target page not in the output: the name is not carried
                var name = nd.Name;
                var key = Convert.ToHexString(name);
                if (used.TryGetValue(key, out var existing) && existing != np)
                {
                    name = [.. name, .. "~"u8, .. Encoding.ASCII.GetBytes((_sources.IndexOf(s) + 1).ToString())];   // clash with a different page: rename, and links are remapped
                    key = Convert.ToHexString(name);
                    s.NameMap[hex] = name;
                }

                used[key] = np;
                entries.Add((name, new PdfArray([new PdfReference(np), .. nd.Tail.Select(t => Rewrite(s, t))])));
            }
        }

        if (entries.Count == 0) return null;
        entries.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
        var flat = new List<PdfObject>();
        foreach (var (name, dest) in entries) { flat.Add(new PdfString(name)); flat.Add(dest); }
        var num = NewNum();
        _out[num] = new PdfDictionary([Kv("Names", new PdfArray(flat))]);
        return new PdfReference(num);
    }

    // ---------------------------------------------------------------- page labels

    private PdfObject? BuildPageLabels(List<int> outPages)
    {
        var any = _sources.Any(s => Dict(s.Doc, Dict(s.Doc, s.Doc.Root)?["PageLabels"]) is not null);
        if (!any) return null;
        var runs = new List<(int OutIndex, string Style, byte[] Prefix, long Start)>();
        var outIndex = 0;
        (string Style, string Prefix, long Next) last = default;
        var rangeCache = new Dictionary<AsmSource, List<(int Start, string Style, byte[] Prefix, long St)>>();
        foreach (var (s, idx) in Sequence)
        {
            if (!rangeCache.TryGetValue(s, out var ranges)) rangeCache[s] = ranges = ReadLabelRanges(s);
            {
                string style; byte[] prefix; long number;
                if (ranges.Count == 0) { style = "D"; prefix = []; number = outIndex + 1; }
                else
                {
                    var r = ranges.Last(x => x.Start <= idx);
                    style = r.Style; prefix = r.Prefix; number = r.St + (idx - r.Start);
                }

                var pk = Encoding.Latin1.GetString(prefix);
                if (runs.Count == 0 || last.Style != style || last.Prefix != pk || last.Next != number)
                    runs.Add((outIndex, style, prefix, number));
                last = (style, pk, number + 1);
                outIndex++;
            }
        }

        var nums = new List<PdfObject>();
        foreach (var (i, style, prefix, start) in runs)
        {
            var d = new List<KeyValuePair<PdfName, PdfObject>>();
            if (style.Length > 0) d.Add(Kv("S", new PdfName(style)));
            if (prefix.Length > 0) d.Add(Kv("P", new PdfString(prefix)));
            if (start != 1) d.Add(Kv("St", new PdfInteger(start)));
            nums.Add(new PdfInteger(i));
            nums.Add(new PdfDictionary(d));
        }

        return new PdfDictionary([Kv("Nums", new PdfArray(nums))]);
    }

    private static List<(int Start, string Style, byte[] Prefix, long St)> ReadLabelRanges(AsmSource s)
    {
        var list = new List<(int, string, byte[], long)>();
        void Tree(PdfObject? node, int depth)
        {
            var nd = Dict(s.Doc, node);
            if (nd is null || depth > 16) return;
            if (Res(s.Doc, nd["Nums"]) is PdfArray a)
                for (var i = 0; i + 1 < a.Count; i += 2)
                    if (Res(s.Doc, a[i]) is PdfInteger k && Dict(s.Doc, a[i + 1]) is { } d)
                        list.Add(((int)k.Value, d.GetName("S") ?? "", (Res(s.Doc, d["P"]) as PdfString)?.Value.ToArray() ?? [], (Res(s.Doc, d["St"]) as PdfInteger)?.Value ?? 1));
            if (Res(s.Doc, nd["Kids"]) is PdfArray kids)
                foreach (var kid in kids) Tree(kid, depth + 1);
        }

        Tree(Dict(s.Doc, s.Doc.Root)?["PageLabels"], 0);
        list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return list;
    }

    // ---------------------------------------------------------------- outlines

    private PdfObject? BuildOutline(bool wrap)
    {
        var tops = new List<OItem>();
        foreach (var s in _sources)
        {
            var kept = ReadOutline(s);
            if (kept.Count == 0) continue;
            if (wrap)
            {
                var firstIdx = Sequence.FirstOrDefault(x => x.S == s);
                var first = firstIdx.S is null ? -1 : s.AllPages[firstIdx.Idx].Num;
                tops.Add(new OItem { Title = PdfString.FromAscii(s.Name), PageObj = first, Kids = kept, Count = 1, Owner = s });
                // the wrapper's page belongs to this source
                tops[^1].DestTail = [new PdfName("Fit")];
            }
            else tops.AddRange(kept);
        }

        if (tops.Count == 0) return null;
        // allocate objects
        var rootNum = NewNum();
        long total = 0;
        PdfReference Emit(List<OItem> items, int parentNum, AsmSource? owner, out long visible, out PdfReference last)
        {
            var nums = items.Select(_ => NewNum()).ToList();
            visible = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var src = it.Owner ?? owner ?? _sources[0];
                var entries = new List<KeyValuePair<PdfName, PdfObject>> { Kv("Title", it.Title), Kv("Parent", new PdfReference(parentNum)) };
                if (i > 0) entries.Add(Kv("Prev", new PdfReference(nums[i - 1])));
                if (i < items.Count - 1) entries.Add(Kv("Next", new PdfReference(nums[i + 1])));
                long childVisible = 0;
                if (it.Kids.Count > 0)
                {
                    var f = Emit(it.Kids, nums[i], src, out childVisible, out var l);
                    entries.Add(Kv("First", f));
                    entries.Add(Kv("Last", l));
                    entries.Add(Kv("Count", new PdfInteger(it.Count < 0 ? -childVisible : childVisible)));
                }

                if (it.PageObj >= 0 && src.IncludedPageNew.TryGetValue(it.PageObj, out var np))
                    entries.Add(Kv("Dest", new PdfArray([new PdfReference(np), .. it.DestTail])));
                else if (it.OtherAction is not null) entries.Add(Kv("A", it.OtherAction));
                if (it.Color is not null) entries.Add(Kv("C", it.Color));
                if (it.Flags is not null) entries.Add(Kv("F", it.Flags));
                _out[nums[i]] = new PdfDictionary(entries);
                visible += 1 + (it.Count < 0 ? 0 : childVisible);
            }

            last = new PdfReference(nums[^1]);
            return new PdfReference(nums[0]);
        }

        var firstRef = Emit(tops, rootNum, null, out total, out var lastRef);
        _out[rootNum] = new PdfDictionary([Kv("Type", new PdfName("Outlines")), Kv("First", firstRef), Kv("Last", lastRef), Kv("Count", new PdfInteger(total))]);
        return new PdfReference(rootNum);
    }

    /// <summary>Reads the outline and keeps entries that go to an included page; an entry that goes to a removed page is dropped and its surviving children move up.</summary>
    private List<OItem> ReadOutline(AsmSource s)
    {
        var root = Dict(s.Doc, Dict(s.Doc, s.Doc.Root)?["Outlines"]);
        if (root is null) return [];
        var guard = 0;
        List<OItem> Level(PdfObject? firstRef, int depth, bool parentKept)
        {
            var result = new List<OItem>();
            var cur = firstRef;
            var seen = new HashSet<int>();
            while (cur is PdfReference cr && seen.Add(cr.Number) && guard++ < 100000 && depth < 16)
            {
                var d = Dict(s.Doc, cur);
                if (d is null) break;
                var it = new OItem { Owner = s, Title = Res(s.Doc, d["Title"]) as PdfString ?? new PdfString([]), Count = (Res(s.Doc, d["Count"]) as PdfInteger)?.Value ?? 0, Color = d["C"], Flags = d["F"] };
                PdfObject? dest = d["Dest"];
                var act = Dict(s.Doc, d["A"]);
                if (dest is null && act is not null)
                {
                    if (act.GetName("S") == "GoTo") dest = act["D"];
                    else it.OtherAction = Rewrite(s, act);
                }

                if (dest is not null)
                {
                    var rd = Res(s.Doc, dest);
                    var pg = DestPage(s, dest);
                    if (pg is not null)
                    {
                        it.PageObj = pg.Value;
                        it.HadPageDest = true;
                        it.DestTail = rd is PdfArray a ? a.Skip(1).Select(x => Rewrite(s, x)).ToArray() : (rd is PdfString or PdfName && s.Named.TryGetValue(Convert.ToHexString(rd is PdfString ps ? ps.Value : Encoding.Latin1.GetBytes(((PdfName)rd).Value)), out var nd) ? nd.Tail.Select(x => Rewrite(s, x)).ToArray() : []);
                    }
                }

                var goesToIncluded = it.PageObj >= 0 && s.IncludedPageNew.ContainsKey(it.PageObj);
                var keptSelf = goesToIncluded || (!it.HadPageDest && parentKept);
                var kids = Level(d["First"], depth + 1, keptSelf);
                it.Kids = kids;
                if (keptSelf) result.Add(it);
                else result.AddRange(kids);             // dropped entry (goes to a removed page, or a page-less entry under a dropped parent): its surviving children move up
                cur = d["Next"];
            }

            return result;
        }

        return Level(root["First"], 0, true);
    }

    // ---------------------------------------------------------------- copying objects

    private int Map(AsmSource s, int oldNum)
    {
        if (!s.Map.TryGetValue(oldNum, out var n))
        {
            n = NewNum();
            s.Map[oldNum] = n;
            _queue.Enqueue((s, oldNum, n));
        }

        return n;
    }

    private PdfObject MapRef(AsmSource s, PdfReference r)
    {
        if (s.PageObjNums.Contains(r.Number))
            return s.IncludedPageNew.TryGetValue(r.Number, out var pn) ? new PdfReference(pn) : PdfNull.Instance;
        return new PdfReference(Map(s, r.Number));
    }

    private PdfObject Rewrite(AsmSource s, PdfObject o) => o switch
    {
        PdfReference r => MapRef(s, r),
        PdfArray a => new PdfArray(a.Select(x => Rewrite(s, x))),
        PdfDictionary d => new PdfDictionary(d.Select(kv => new KeyValuePair<PdfName, PdfObject>(kv.Key, Rewrite(s, kv.Value)))),
        PdfStream st => new PdfStream((PdfDictionary)Rewrite(s, st.Dictionary), st.RawData),
        _ => o,
    };

    private void Drain()
    {
        while (_queue.Count > 0)
        {
            var (s, oldNum, newNum) = _queue.Dequeue();
            var g = s.Doc.GetObject(oldNum);
            if (!g.IsSuccess) { Issues++; _out[newNum] = PdfNull.Instance; continue; }
            var obj = g.Value;
            var dict = obj as PdfDictionary ?? (obj as PdfStream)?.Dictionary;
            if (dict is not null)
            {
                if (dict.GetName("Subtype") == "Link") dict = RewriteLink(s, dict);
                if (s.FieldNodes.Contains(oldNum))
                {
                    if (dict["Kids"] is { } kidsObj && Res(s.Doc, kidsObj) is PdfArray kids)
                        dict = dict.With("Kids", new PdfArray(kids.Where(k => k is PdfReference kr && (s.KeptWidgets.Contains(kr.Number) || s.FieldNodes.Contains(kr.Number)))));
                    if (s.RootRename.TryGetValue(oldNum, out var newT)) dict = dict.With("T", new PdfString(newT));
                }

                if (dict.GetName("Subtype") == "Widget") dict = dict.Without("StructParent");
                dict = dict.Without("StructParent");
                obj = obj is PdfStream stm ? new PdfStream(dict, stm.RawData) : dict;
            }

            _out[newNum] = Rewrite(s, obj);
        }
    }

    // ---------------------------------------------------------------- writing

    private byte[] Write(int catalogNum, int infoNum)
    {
        using var ms = new MemoryStream();
        void W(string text) => ms.Write(Encoding.Latin1.GetBytes(text));
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[_next];
        foreach (var n in _out.Keys.OrderBy(x => x))
        {
            offsets[n] = ms.Position;
            W($"{n} 0 obj\n");
            var obj = _out[n];
            if (obj is PdfStream st)
            {
                var dict = st.Dictionary.With("Length", new PdfInteger(st.RawData.Length));
                ms.Write(PdfSerializer.Serialize(dict).Value);
                W("\nstream\n");
                ms.Write(st.RawData.Span);
                W("\nendstream");
            }
            else ms.Write(PdfSerializer.Serialize(obj).Value);
            W("\nendobj\n");
        }

        var xref = ms.Position;
        W($"xref\n0 {_next}\n0000000000 65535 f \n");
        for (var n = 1; n < _next; n++)
            W(_out.ContainsKey(n) ? $"{offsets[n]:D10} 00000 n \n" : "0000000000 65535 f \n");
        var id = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        W($"trailer\n<< /Size {_next} /Root {catalogNum} 0 R /Info {infoNum} 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- command line

    public static int Cli(string[] args)
    {
        // assemble <out.pdf> [--wrap] <file.pdf>[:1-3,7] ...
        var sources = new List<AsmSource>();
        var wrap = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--wrap") { wrap = true; continue; }
            var spec = args[i];
            var colon = spec.LastIndexOf(':');
            string? range = null;
            if (colon > 2) { range = spec[(colon + 1)..]; spec = spec[..colon]; }
            var opened = StrictDocument.Open(File.ReadAllBytes(spec));
            if (opened.TryGetError(out var e)) { Console.Error.WriteLine($"{spec}: strict parse failed: {e}"); return 2; }
            var doc = opened.Value;
            var total = CountPages(doc);
            var pages = range is null ? Enumerable.Range(0, total).ToArray() : ParseRange(range, total);
            sources.Add(new AsmSource { Name = Path.GetFileNameWithoutExtension(spec), Doc = doc, Pages = pages });
        }

        var asm = new Assembler(sources);
        var bytes = asm.Build(wrap);
        File.WriteAllBytes(args[0], bytes);
        Console.WriteLine($"wrote {args[0]} ({bytes.Length} bytes); {asm._out.Count} objects; notes: {string.Join("; ", asm.Notes)}");
        return 0;
    }

    public static int CountPages(StrictDocument d)
    {
        var a = new Assembler([new AsmSource { Name = "x", Doc = d, Pages = [] }]);
        var s = a._sources[0];
        a.Prepare(s);
        return s.AllPages.Count;
    }

    public static int[] ParseRange(string range, int total)
    {
        var list = new List<int>();
        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-');
            var lo = int.Parse(ab[0]);
            var hi = ab.Length > 1 ? (ab[1].Length == 0 ? total : int.Parse(ab[1])) : lo;
            for (var p = lo; p <= hi; p++) list.Add(p - 1);
        }

        return [.. list];
    }
}
