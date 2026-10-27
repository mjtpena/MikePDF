using S8.Core;

namespace S8.Validate;

/// <summary>
/// S8.2(e): compares the document as signed (view at the signed revision end) with the document as a reader now
/// sees it (final view) and classifies every semantic difference as allowed / allowed-with-warning / disallowed.
///
/// Design rules (conservative on purpose, an unknown difference is Disallowed, never ignored):
///   * Walk from /Root. Every key the walk does not have an explicit rule for must be deep-equal.
///   * Pages are compared through the page tree only; a reference TO a page elsewhere compares by object number.
///   * Allowed (DocMDP P &gt;= 2 or no DocMDP): new signature fields + their signature dictionaries, form fill
///     (/V /AP /AS /I) on non-read-only fields, /SigFlags, DSS, /Extensions.
///   * Allowed with warning (P = 3 or no DocMDP): annotation create/modify/delete, XMP/Info changes.
///   * Never allowed: page content / resources / page tree / page boxes / AcroForm structure / any other catalog entry,
///     a changed signed signature dictionary, a broken xref chain, changed /Encrypt.
/// </summary>
public sealed class ModificationAnalyzer
{
    private readonly PdfView _o, _n;
    private readonly int? _p;
    public List<Change> Changes { get; } = new();
    private Dictionary<int, PdfDict> _oldFields = new(), _newFields = new();
    private readonly Dictionary<(int, int), bool> _memo = new();
    private readonly HashSet<(int, int)> _inProgress = new();
    private int _assumptions; // times a cycle was cut by assuming equality

    public ModificationAnalyzer(PdfView oldView, PdfView newView, int? docMdp)
    {
        _o = oldView; _n = newView; _p = docMdp;
    }

    private void Add(ChangeClass cls, string desc, int level = 0)
    {
        if (cls != ChangeClass.Disallowed && _p != null && level > 0 && _p < level)
        { cls = ChangeClass.Disallowed; desc += $" [forbidden by DocMDP P={_p}]"; }
        Changes.Add(new Change(cls, desc));
    }

    private void Dup(PdfDict od, PdfDict nd, string what)
    {
        if (nd.Duplicates.Count > od.Duplicates.Count)
            Add(ChangeClass.Disallowed, $"{what}: duplicate dictionary key(s) {string.Join(", ", nd.Duplicates.Select(k => "/" + k))} introduced after signing (first-wins and last-wins readers would disagree)");
    }

    public List<Change> Analyze()
    {
        // 1. the new cross-reference chain must contain every xref section of the signed revision
        var oldOffs = _o.Sections.Select(s => s.Offset).ToHashSet();
        var newOffs = _n.Sections.Select(s => s.Offset).ToHashSet();
        if (!oldOffs.IsSubsetOf(newOffs))
            Add(ChangeClass.Disallowed, "revision chain broken: the final cross-reference chain does not pass through the signed revision (file rewritten after signing)");

        foreach (var sec in _n.Sections.Where(x => !oldOffs.Contains(x.Offset)))
            if (sec.Trailer.Duplicates.Count > 0) Add(ChangeClass.Disallowed, $"trailer of the update at offset {sec.Offset} has duplicate key(s) {string.Join(", ", sec.Trailer.Duplicates)}");

        // 2. trailer
        if (_o.IsEncrypted != _n.IsEncrypted || (_o.IsEncrypted && !Equal(_o.Trailer["Encrypt"], _n.Trailer["Encrypt"])))
            Add(ChangeClass.Disallowed, "/Encrypt changed");
        var oid = _o.ResolveArray(_o.Trailer["ID"]); var nid = _n.ResolveArray(_n.Trailer["ID"]);
        if ((oid == null) != (nid == null) || (oid != null && nid != null && !Equal(oid.Count > 0 ? oid[0] : null, nid.Count > 0 ? nid[0] : null)))
            Add(ChangeClass.AllowedWithWarning, "document /ID[0] changed", 3);

        var oldInfo = _o.Trailer["Info"]; var newInfo = _n.Trailer["Info"];
        if (!(oldInfo == null && newInfo == null) && !Equal(oldInfo, newInfo))
            Add(ChangeClass.AllowedWithWarning, "document /Info dictionary changed", 3);

        // 3. catalog
        var oc = _o.ResolveDict(_o.Trailer["Root"]);
        var nc = _n.ResolveDict(_n.Trailer["Root"]);
        if (oc == null || nc == null) { Add(ChangeClass.Disallowed, "catalog missing in signed or final revision"); return Changes; }
        if (_o.RootRef?.Num != _n.RootRef?.Num)
            Add(ChangeClass.AllowedWithWarning, $"trailer /Root now points to a different object ({_o.RootRef?.Num} -> {_n.RootRef?.Num}); contents compared structurally", 3);
        CatalogRule(oc, nc);
        return Changes;
    }

    // ---------------------------------------------------------------- catalog

    private void CatalogRule(PdfDict oc, PdfDict nc)
    {
        Dup(oc, nc, "catalog");
        // AcroForm first: it fills the field maps that the annotation rule consults.
        AcroFormRule(oc["AcroForm"], nc["AcroForm"]);
        var keys = oc.Keys.Concat(nc.Keys).Distinct().ToList();
        foreach (var k in keys)
        {
            if (k == "AcroForm") continue;
            if (k == "Pages") { PagesRule(oc["Pages"], nc["Pages"]); continue; }
            var a = oc[k]; var b = nc[k];
            if (Equal(a, b)) continue;
            switch (k)
            {
                case "DSS": Add(ChangeClass.Allowed, "Document Security Store (/DSS) added or extended (validation data)", 1); break;
                case "Extensions": Add(ChangeClass.Allowed, "/Extensions changed", 1); break;
                case "Version": Add(ChangeClass.Allowed, "catalog /Version changed", 1); break;
                case "Metadata": Add(ChangeClass.Allowed, "XMP /Metadata stream replaced (signers such as pyHanko/Acrobat rewrite it on every save; not page content)", 2); break;
                default: Add(ChangeClass.Disallowed, $"catalog entry /{k} changed"); break;
            }
        }
    }

    // ---------------------------------------------------------------- page tree

    private static bool IsPageNode(PdfDict d) => d["Type"].NameOf() is "Page" or "Pages" || (!d.Has("Type") && d.Has("Kids") && d.Has("Count") && !d.Has("T"));

    private void PagesRule(PdfObj? oldNode, PdfObj? newNode, int depth = 0)
    {
        if (depth > 40) { Add(ChangeClass.Disallowed, "page tree too deep"); return; }
        var od = _o.ResolveDict(oldNode); var nd = _n.ResolveDict(newNode);
        if (od == null || nd == null) { Add(ChangeClass.Disallowed, "page tree node missing"); return; }
        Dup(od, nd, "page-tree node");
        bool oPages = od.Has("Kids"), nPages = nd.Has("Kids");
        if (oPages != nPages) { Add(ChangeClass.Disallowed, "page tree node changed kind (page <-> pages)"); return; }
        if (!oPages) { PageRule(od, nd, (oldNode as PdfRef)?.Num ?? -1); return; }

        foreach (var k in od.Keys.Concat(nd.Keys).Distinct())
        {
            if (k is "Kids" or "Parent") continue;
            if (!Equal(od[k], nd[k])) Add(ChangeClass.Disallowed, $"page-tree node entry /{k} changed (e.g. inherited /Resources, /MediaBox, /Count)");
        }
        var ok = _o.ResolveArray(od["Kids"]); var nk = _n.ResolveArray(nd["Kids"]);
        if (ok == null || nk == null || ok.Count != nk.Count)
        {
            Add(ChangeClass.Disallowed, $"page tree /Kids changed (pages added, removed or reordered: {ok?.Count} -> {nk?.Count})");
            return;
        }
        for (int i = 0; i < ok.Count; i++) PagesRule(ok[i], nk[i], depth + 1);
    }

    private void PageRule(PdfDict op, PdfDict np, int pageObj)
    {
        Dup(op, np, $"page object {pageObj}");
        foreach (var k in op.Keys.Concat(np.Keys).Distinct())
        {
            if (k == "Annots") continue;
            if (k == "Parent")
            {
                if (!(op[k] is PdfRef a && np[k] is PdfRef b && a.Num == b.Num)) Add(ChangeClass.Disallowed, $"page object {pageObj}: /Parent changed");
                continue;
            }
            if (!Equal(op[k], np[k])) Add(ChangeClass.Disallowed, $"page object {pageObj}: /{k} changed (page content, resources, boxes or other page data)");
        }
        AnnotsRule(op["Annots"], np["Annots"], pageObj);
    }

    // ---------------------------------------------------------------- annotations

    private void AnnotsRule(PdfObj? oldA, PdfObj? newA, int pageObj)
    {
        var oa = _o.ResolveArray(oldA)?.Items ?? new List<PdfObj>();
        var na = _n.ResolveArray(newA)?.Items ?? new List<PdfObj>();
        var oldRefs = oa.OfType<PdfRef>().GroupBy(r => r.Num).ToDictionary(g => g.Key, g => g.First());
        var newRefs = na.OfType<PdfRef>().GroupBy(r => r.Num).ToDictionary(g => g.Key, g => g.First());
        if (oa.Count != oldRefs.Count || na.Count != na.OfType<PdfRef>().Count())
        {
            // direct annotation dictionaries: compare as a whole
            if (!Equal(new PdfArray(oa), new PdfArray(na))) Add(ChangeClass.AllowedWithWarning, $"page object {pageObj}: direct (non-indirect) annotation dictionaries changed", 3);
        }
        foreach (var (num, nref) in newRefs)
        {
            var nd = _n.ResolveDict(nref);
            if (nd == null) { Add(ChangeClass.Disallowed, $"page object {pageObj}: annotation {num} unreadable"); continue; }
            string sub = nd["Subtype"].NameOf() ?? "?";
            if (!oldRefs.TryGetValue(num, out var oref))
            {
                // new annotation
                if (sub == "Widget")
                {
                    if (!_newFields.ContainsKey(num)) Add(ChangeClass.Disallowed, $"page object {pageObj}: new widget annotation {num} is not part of /AcroForm /Fields");
                    // else: accounted for by FieldsRule
                }
                else
                {
                    string extra = (nd.Has("A") || nd.Has("AA")) ? " (carries an action)" : "";
                    Add(ChangeClass.AllowedWithWarning, $"annotation added: /{sub}, object {num}{extra}", 3);
                }
                continue;
            }
            var od = _o.ResolveDict(oref);
            if (od == null) continue;
            if (_oldFields.ContainsKey(num) || _newFields.ContainsKey(num)) continue; // widget: handled by the field rule
            if (!Equal(oref, nref))
            {
                if (od["Subtype"].NameOf() != nd["Subtype"].NameOf()) Add(ChangeClass.Disallowed, $"annotation {num}: /Subtype changed");
                else Add(ChangeClass.AllowedWithWarning, $"annotation modified: /{sub}, object {num}", 3);
            }
        }
        foreach (var (num, oref) in oldRefs)
        {
            if (newRefs.ContainsKey(num)) continue;
            var od = _o.ResolveDict(oref);
            string sub = od?["Subtype"].NameOf() ?? "?";
            if (sub == "Widget" || _oldFields.ContainsKey(num)) Add(ChangeClass.Disallowed, $"widget annotation {num} removed from page {pageObj}");
            else Add(ChangeClass.AllowedWithWarning, $"annotation removed: /{sub}, object {num}", 3);
        }
    }

    // ---------------------------------------------------------------- AcroForm

    private void AcroFormRule(PdfObj? oldAf, PdfObj? newAf)
    {
        var od = _o.ResolveDict(oldAf) ?? new PdfDict();
        var nd = _n.ResolveDict(newAf) ?? new PdfDict();
        _oldFields = Flatten(_o, od); _newFields = Flatten(_n, nd);
        Dup(od, nd, "AcroForm");

        foreach (var k in od.Keys.Concat(nd.Keys).Distinct())
        {
            if (k == "Fields") continue;
            var a = od[k]; var b = nd[k];
            if (Equal(a, b)) continue;
            switch (k)
            {
                case "SigFlags": Add(ChangeClass.Allowed, "/AcroForm /SigFlags changed", 2); break;
                case "NeedAppearances": Add(ChangeClass.AllowedWithWarning, "/AcroForm /NeedAppearances changed (viewer will regenerate field appearances)", 2); break;
                case "DR":
                    if (a == null || Superset(a, b)) Add(ChangeClass.AllowedWithWarning, "/AcroForm /DR (default resources) extended", 2);
                    else Add(ChangeClass.Disallowed, "/AcroForm /DR existing resources modified or removed");
                    break;
                default: Add(ChangeClass.Disallowed, $"/AcroForm /{k} changed"); break;
            }
        }

        foreach (var (num, nf) in _newFields)
        {
            string name = FullName(_n, nf);
            if (!_oldFields.TryGetValue(num, out var of))
            {
                string? ft = FieldType(_n, nf);
                if (ft == "Sig")
                {
                    bool hasValue = _n.Resolve(InheritedValue(_n, nf, "V")) is PdfDict vd && vd.Has("ByteRange") && vd.Has("Contents");
                    if (!hasValue && IsVisible(nf))
                        Add(ChangeClass.AllowedWithWarning, $"empty VISIBLE signature field added: '{name}' (object {num}); its appearance can cover page content", 2);
                    else Add(ChangeClass.Allowed, $"signature field added: '{name}' (object {num})", 2);
                }
                else if (ft == null && nf.Has("Kids")) { /* intermediate node without FT: judged by its kids */ }
                else Add(ChangeClass.Disallowed, $"new non-signature form field added: '{name}' (object {num}, /FT /{ft})");
                continue;
            }
            if (!Equal(of, nf)) FieldRule(num, of, nf, name);
        }
        foreach (var (num, of) in _oldFields)
            if (!_newFields.ContainsKey(num)) Add(ChangeClass.Disallowed, $"form field removed: '{FullName(_o, of)}' (object {num})");
    }

    private bool IsVisible(PdfDict w)
    {
        var rect = _n.ResolveArray(w["Rect"]);
        long flags = w["F"].IntOf() ?? 0;
        if ((flags & (2 | 32)) != 0) return false; // Hidden / NoView
        if (rect == null || rect.Count != 4) return false;
        double V(int i) => rect[i] is PdfInt a ? a.Value : rect[i] is PdfReal r ? r.Value : 0;
        return Math.Abs(V(2) - V(0)) > 0.5 && Math.Abs(V(3) - V(1)) > 0.5;
    }

    private static readonly string[] FillKeys = { "V", "AP", "AS", "I" };

    private void FieldRule(int num, PdfDict of, PdfDict nf, string name)
    {
        string? ft = FieldType(_o, of);
        Dup(of, nf, $"form field '{name}'");
        var diff = new List<string>();
        foreach (var k in of.Keys.Concat(nf.Keys).Distinct())
        {
            if (k == "Kids") continue;
            if (k == "Parent") { if (!(of[k] is PdfRef a && nf[k] is PdfRef b && a.Num == b.Num)) diff.Add(k); continue; }
            if (!Equal(of[k], nf[k])) diff.Add(k);
        }
        if (diff.Count == 0) return;
        if (ft == "Sig")
        {
            var oldV = InheritedValue(_o, of, "V");
            if (oldV != null) { Add(ChangeClass.Disallowed, $"already-signed signature field '{name}' (object {num}) modified: {string.Join(", ", diff.Select(d => "/" + d))}"); return; }
            var bad = diff.Where(d => d is not ("V" or "AP")).ToList();
            if (bad.Count > 0) Add(ChangeClass.Disallowed, $"empty signature field '{name}' changed beyond signing: {string.Join(", ", bad.Select(d => "/" + d))}");
            else Add(ChangeClass.Allowed, $"signature field '{name}' (object {num}) signed", 2);
            return;
        }
        var other = diff.Where(d => !FillKeys.Contains(d)).ToList();
        if (other.Count > 0) { Add(ChangeClass.Disallowed, $"form field '{name}' (object {num}) attributes changed: {string.Join(", ", other.Select(d => "/" + d))}"); return; }
        long ff = InheritedValue(_o, of, "Ff").IntOf() ?? 0;
        if ((ff & 1) != 0) { Add(ChangeClass.Disallowed, $"read-only form field '{name}' (object {num}) changed"); return; }
        Add(ChangeClass.AllowedWithWarning, $"form field filled/changed: '{name}' (object {num}, /FT /{ft})", 2);
    }

    private Dictionary<int, PdfDict> Flatten(PdfView v, PdfDict acro)
    {
        var res = new Dictionary<int, PdfDict>();
        void Walk(PdfObj? o, int depth)
        {
            if (depth > 32) return;
            if (o is PdfArray a) { foreach (var x in a.Items) Walk(x, depth + 1); return; }
            if (o is PdfRef r && !res.ContainsKey(r.Num))
            {
                var d = v.ResolveDict(r);
                if (d == null) return;
                res[r.Num] = d;
                Walk(v.Resolve(d["Kids"]), depth + 1);
            }
        }
        Walk(v.Resolve(acro["Fields"]), 0);
        return res;
    }

    private static string? FieldType(PdfView v, PdfDict d)
    {
        for (int i = 0; i < 16 && d != null; i++)
        {
            if (d["FT"].NameOf() is string s) return s;
            d = v.ResolveDict(d["Parent"])!;
        }
        return null;
    }

    private static PdfObj? InheritedValue(PdfView v, PdfDict d, string key)
    {
        for (int i = 0; i < 16 && d != null; i++)
        {
            var x = v.Resolve(d[key]);
            if (x != null) return x;
            d = v.ResolveDict(d["Parent"])!;
        }
        return null;
    }

    private static string FullName(PdfView v, PdfDict d)
    {
        var parts = new List<string>();
        for (int i = 0; i < 16 && d != null; i++)
        {
            if (v.Resolve(d["T"]) is PdfString s) parts.Insert(0, s.ToString());
            d = v.ResolveDict(d["Parent"])!;
        }
        return string.Join(".", parts);
    }

    // ---------------------------------------------------------------- deep comparison

    private bool Superset(PdfObj? a, PdfObj? b)
    {
        var ra = a is PdfRef x ? _o.Resolve(x) : a; var rb = b is PdfRef y ? _n.Resolve(y) : b;
        if (ra is PdfDict da && rb is PdfDict db)
        {
            foreach (var k in da.Keys)
            {
                if (!db.Has(k)) return false;
                if (!Superset(da[k], db[k])) return false;
            }
            return true;
        }
        return Equal(a, b);
    }

    /// <summary>Deep structural equality of an old-view object and a new-view object.</summary>
    public bool Equal(PdfObj? a, PdfObj? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        (int, int)? key = null;
        if (a is PdfRef ra)
        {
            var ta = _o.Resolve(ra);
            if (ta is PdfDict pd && IsPageNode(pd))
            {
                // references to pages compare by identity; page contents are judged by the page-tree rule
                return b is PdfRef rb0 && rb0.Num == ra.Num && _n.ResolveDict(rb0) is PdfDict nd0 && IsPageNode(nd0);
            }
            if (b is PdfRef rb)
            {
                key = (ra.Num, rb.Num);
                if (_memo.TryGetValue(key.Value, out var cached)) return cached;
                if (!_inProgress.Add(key.Value)) { _assumptions++; return true; } // cycle: assume equal; a difference elsewhere is still found by the outer comparison
            }
            a = ta;
        }
        if (b is PdfRef rb2) b = _n.Resolve(rb2);
        int before = _assumptions;
        bool res = Compare(a, b);
        if (key != null)
        {
            _inProgress.Remove(key.Value);
            // a 'true' that relied on a cycle assumption is only provisional: do not cache it
            if (!res || _assumptions == before) _memo[key.Value] = res;
        }
        return res;
    }

    private bool Compare(PdfObj? a, PdfObj? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is PdfNull) return b is PdfNull;
        if (a is PdfBool ab) return b is PdfBool bb && ab.Value == bb.Value;
        if (a is PdfInt or PdfReal) return (b is PdfInt or PdfReal) && NumVal(a) == NumVal(b);
        if (a is PdfName an) return b is PdfName bn && an.Value == bn.Value;
        if (a is PdfString asr) return b is PdfString bs && asr.Value.AsSpan().SequenceEqual(bs.Value);
        if (a is PdfArray aa)
        {
            if (b is not PdfArray ba || aa.Count != ba.Count) return false;
            for (int i = 0; i < aa.Count; i++) if (!Equal(aa[i], ba[i])) return false;
            return true;
        }
        if (a is PdfDict ad) return b is PdfDict bd && DictEqual(ad, bd);
        if (a is PdfStream ast) return b is PdfStream bst && DictEqual(ast.Dict, bst.Dict) && ast.Raw.SequenceEqual(bst.Raw);
        return false;
    }

    private static double NumVal(PdfObj o) => o switch { PdfInt i => i.Value, PdfReal r => r.Value, _ => double.NaN };

    private bool DictEqual(PdfDict x, PdfDict y)
    {
        if (y.Duplicates.Count > x.Duplicates.Count) return false; // duplicate keys introduced later: first-wins and last-wins readers would disagree
        if (x.Map.Count != y.Map.Count) return false;
        foreach (var kv in x.Map)
        {
            if (!y.Map.TryGetValue(kv.Key, out var yv)) return false;
            if (!Equal(kv.Value, yv)) return false;
        }
        return true;
    }
}
