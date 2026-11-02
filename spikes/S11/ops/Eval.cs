// S11: compares an output PDF's profile with what the page operation should have produced, feature by feature.
using System.Text;
using MikePDF.PdfWriter;
using S4;

namespace S11;

internal sealed class EvalResult
{
    public SortedDictionary<string, string> Fields = new(StringComparer.Ordinal);
    public bool AllOk => Fields.Values.All(v => v.StartsWith("ok", StringComparison.Ordinal) || v.StartsWith("n/a", StringComparison.Ordinal) || v.StartsWith("info", StringComparison.Ordinal));
}

internal static class Eval
{
    public static EvalResult Evaluate(byte[] outBytes, byte[]? baseBytesForPrefix, Dictionary<string, Profile> sources, string[] expectKeys, Dictionary<string, int>? rotDelta = null, bool compareRender = true)
    {
        var r = new EvalResult();
        var o = Profile.Read(outBytes);
        if (!o.Opened) { r.Fields["open"] = "FAIL: " + o.OpenError; return r; }
        r.Fields["open"] = "ok";
        rotDelta ??= [];

        // source page lookup: key -> (profile, page); first source wins on duplicate keys
        var src = new Dictionary<string, (Profile S, PageInfo P)>();
        foreach (var s in sources.Values)
            foreach (var p in s.Pages)
                if (p.Key is not null) src.TryAdd(p.Key, (s, p));
        var keep = expectKeys.ToHashSet();
        var dupKeys = expectKeys.Length != keep.Count;

        // pages and order
        var gotKeys = o.Pages.Select(p => p.Key ?? "?").ToArray();
        r.Fields["pages"] = o.PageCount == expectKeys.Length ? "ok" : $"WRONG count {o.PageCount}, expected {expectKeys.Length}";
        r.Fields["order"] = gotKeys.SequenceEqual(expectKeys) ? "ok" : "WRONG: " + string.Join(",", gotKeys.Select(Short));

        // outline
        var expOutline = new List<(string, string)>();
        foreach (var s in sources.Values)
            foreach (var e in s.Outline)
                if (keep.Contains(e.Target)) expOutline.Add((e.Title, e.Target));
        var actOutline = o.Outline.Where(e => e.Target != "uri").Select(e => (e.Title, e.Target)).ToList();
        var missing = Multiset(expOutline).Sum(kv => Math.Max(0, kv.Value - Multiset(actOutline).GetValueOrDefault(kv.Key)));
        var wrappers = sources.Keys.ToHashSet();   // a merge may add one top-level entry per source file, named after it (by design)
        var wrong = o.Outline.Count(e => keep.Contains(e.Target) && !expOutline.Contains((e.Title, e.Target)) && !wrappers.Contains(e.Title));
        var dangling = o.Outline.Count(e => e.Target.StartsWith("unresolved", StringComparison.Ordinal) || e.Target == "none");
        var srcOutlineTotal = sources.Values.Sum(s => s.Outline.Count);
        r.Fields["outline"] = srcOutlineTotal == 0 ? "n/a (no source outline)"
            : missing == 0 && wrong == 0 && dangling == 0 ? $"ok ({o.Outline.Count} entries, expected {expOutline.Count}+uri)"
            : $"{(o.Outline.Count == 0 ? "LOST" : "BROKEN")}: expected {expOutline.Count} page entries, got {o.Outline.Count} (missing {missing}, retargeted {wrong}, dangling {dangling})";

        // links
        int lExp = 0, lMissing = 0, lWrong = 0, lInert = 0, lExcluded = 0, lDropped = 0;
        for (var i = 0; i < o.Pages.Count; i++)
        {
            var k = o.Pages[i].Key;
            if (k is null || !src.TryGetValue(k, out var sp)) continue;
            var expected = sp.P.Links.Where(t => t == "uri" || keep.Contains(t)).ToList();
            var excluded = sp.P.Links.Count(t => t != "uri" && !keep.Contains(t) && t != "none");
            lExcluded += excluded;
            lExp += expected.Count;
            var actual = o.Pages[i].Links.ToList();
            foreach (var t in expected) { if (!actual.Remove(t)) lMissing++; }
            foreach (var t in actual)
            {
                if (t.StartsWith("unresolved", StringComparison.Ordinal) || t == "none") lInert++;
                else lWrong++;
            }

            lDropped += Math.Max(0, sp.P.Links.Count - o.Pages[i].Links.Count);
        }

        var srcLinkTotal = src.Values.Sum(v => v.P.Links.Count);
        r.Fields["links"] = srcLinkTotal == 0 ? "n/a"
            : lMissing == 0 && lWrong == 0 ? $"ok ({lExp} kept; {lExcluded} to excluded pages: {lInert} left inert, rest removed)" + (dupKeys ? " [duplicate pages: approximate]" : "")
            : $"BROKEN: {lExp} expected, missing {lMissing}, retargeted/extra {lWrong}, inert {lInert} (of {lExcluded} to excluded pages)";

        // named destinations
        int nExp = 0, nMissing = 0, nWrong = 0, nStaleOk = 0;
        var firstDefs = new Dictionary<string, string>(); // name -> key (first source wins)
        var clashes = 0;
        foreach (var s in sources.Values)
            foreach (var (name, key) in s.Named)
            {
                if (firstDefs.TryGetValue(name, out var prev)) { if (prev != key) clashes++; }
                else firstDefs[name] = key;
            }

        foreach (var (name, key) in firstDefs)
        {
            o.Named.TryGetValue(name, out var got);
            if (keep.Contains(key))
            {
                nExp++;
                if (got is null) nMissing++;
                else if (got != key) nWrong++;
            }
            else if (got is not null && !got.StartsWith("unresolved", StringComparison.Ordinal))
            {
                nWrong++;
            }
            else nStaleOk++;
        }

        r.Fields["named"] = firstDefs.Count == 0 ? "n/a"
            : nMissing == 0 && nWrong == 0 ? $"ok ({nExp} kept, {nStaleOk} stale removed/inert){(clashes > 0 ? $" ; {clashes} name clash(es) between sources: first wins" : "")}"
            : $"BROKEN: expected {nExp}, missing {nMissing}, wrong page {nWrong}{(clashes > 0 ? $" ; {clashes} clash(es) between sources" : "")} (catalog: names/dests={(o.Catalog.HasNamesDests ? "y" : "n")} legacy={(o.Catalog.HasLegacyDests ? "y" : "n")})";

        // labels
        int lbMis = 0;
        var anySrcLabel = src.Values.Any(v => v.P.Label.Length > 0);
        for (var i = 0; i < o.Pages.Count; i++)
        {
            var k = o.Pages[i].Key;
            if (k is not null && src.TryGetValue(k, out var sp) && sp.P.Label != o.Pages[i].Label) lbMis++;
        }

        r.Fields["labels"] = !anySrcLabel ? "n/a" : lbMis == 0 ? "ok" : $"WRONG on {lbMis}/{o.Pages.Count} pages (labels do not follow their pages; catalog /PageLabels={(o.Catalog.HasPageLabels ? "yes" : "no")}; first: '{o.Pages[0].Label}' expected '{(src.TryGetValue(o.Pages[0].Key ?? "", out var f0) ? f0.P.Label : "?")}')";

        // forms
        var expW = new List<(string, int, string)>();
        foreach (var k in expectKeys.Distinct())
            if (src.TryGetValue(k, out var sp)) expW.AddRange(sp.P.Widgets);
        var actW = o.Pages.SelectMany(p => p.Widgets).ToList();
        var orphans = actW.Count(w => w.Type < 0 || w.Name.Length == 0);
        static string Root(string n) { var dot = n.IndexOf('.'); var first = dot < 0 ? n : n[..dot]; var us = first.LastIndexOf('_'); return (us > 0 && int.TryParse(first[(us + 1)..], out _) ? first[..us] : first) + (dot < 0 ? "" : n[dot..]); }
        var matched = expW.Count(e => actW.Any(a => (a.Name == e.Item1 || Root(a.Name) == e.Item1) && a.Type == e.Item2 && a.Value == e.Item3));   // a renamed root field (name_2) counts as the same field
        var srcHadFields = sources.Values.Any(s => s.Catalog.AcroFormFields > 0);
        r.Fields["forms"] = expW.Count == 0 && srcHadFields && o.Catalog.AcroFormFields > 0 ? $"BROKEN: no widget is left on the kept pages but /AcroForm /Fields still lists {o.Catalog.AcroFormFields} top-level field(s): orphan fields"
            : expW.Count == 0 ? "n/a"
            : matched == expW.Count && orphans == 0 && o.Catalog.HasAcroForm ? $"ok ({actW.Count} widgets, AcroForm {o.Catalog.AcroFormFields} top-level fields)"
            : $"BROKEN: expected {expW.Count} widgets, {matched} identical in name/type/value, {orphans} not recognised as fields by PDFium (orphan widgets), AcroForm in catalog: {(o.Catalog.HasAcroForm ? $"yes({o.Catalog.AcroFormFields})" : "NO")}; names: {string.Join("|", actW.Select(w => w.Name + "/" + w.Type))}";

        // annotations (all subtypes, per page)
        int aMis = 0;
        for (var i = 0; i < o.Pages.Count; i++)
        {
            var k = o.Pages[i].Key;
            if (k is null || !src.TryGetValue(k, out var sp)) continue;
            if (!sp.P.AnnotCounts.Where(kv => kv.Key != 2).SequenceEqual(o.Pages[i].AnnotCounts.Where(kv => kv.Key != 2))) aMis++;   // links (subtype 2) are checked separately
        }

        var note = src.TryGetValue("PAGE-A-01", out var n1) && keep.Contains("PAGE-A-01");
        var oi = o.Pages.FindIndex(p => p.Key == "PAGE-A-01");
        var popupOk = !note || oi < 0 || (o.Pages[oi].NoteHasPopup == n1.P.NoteHasPopup && o.Pages[oi].ReplyHasParent == n1.P.ReplyHasParent);
        r.Fields["annots"] = aMis == 0 && popupOk ? "ok" : $"WRONG: {aMis} page(s) with different annotation counts by subtype; popup/reply links {(popupOk ? "ok" : "BROKEN")}";

        // structure tree
        var stSrc = expectKeys.Count(k => src.TryGetValue(k, out var sp) && sp.P.StructChildren > 0);
        var stAct = o.Pages.Count(p => p.Key is not null && src.TryGetValue(p.Key, out var sp) && sp.P.StructChildren > 0 && p.StructChildren > 0);
        r.Fields["structure"] = stSrc == 0 ? "n/a" : stAct == stSrc && o.Catalog.HasStructTree ? "ok" : $"LOST: {stSrc} pages had tags, now {stAct}; StructTreeRoot={(o.Catalog.HasStructTree ? "yes" : "no")}, MarkInfo={(o.Catalog.HasMarkInfo ? "yes" : "no")}";

        // optional content
        var srcHasOc = sources.Values.Any(s => s.Catalog.HasOcProperties);
        var ocPage = o.Pages.FindIndex(p => p.Key == "PAGE-A-03");
        string ocRender = "";
        if (ocPage >= 0 && src.TryGetValue("PAGE-A-03", out var s3) && s3.P.RenderHash != o.Pages[ocPage].RenderHash) ocRender = "; page A-03 renders DIFFERENTLY (hidden layer visible or layer lost)";
        r.Fields["optionalContent"] = !srcHasOc ? "n/a" : !keep.Contains("PAGE-A-03") ? $"ok (the kept pages use no layers; /OCProperties {(o.Catalog.HasOcProperties ? "kept" : "dropped")})" : o.Catalog.HasOcProperties && ocRender.Length == 0 ? "ok" : $"BROKEN: /OCProperties {(o.Catalog.HasOcProperties ? "present" : "ABSENT")}{ocRender}";

        // render
        var diff = new List<string>();
        if (compareRender)
            for (var i = 0; i < o.Pages.Count; i++)
            {
                var k = o.Pages[i].Key;
                if (k is null || !src.TryGetValue(k, out var sp) || rotDelta.ContainsKey(k)) continue;
                if (sp.P.RenderHash != o.Pages[i].RenderHash) diff.Add(Short(k));
            }

        r.Fields["render"] = diff.Count == 0 ? "ok" : $"DIFFERENT on {diff.Count}: {string.Join(",", diff.Take(6))}";

        // geometry
        int gMis = 0;
        for (var i = 0; i < o.Pages.Count; i++)
        {
            var k = o.Pages[i].Key;
            if (k is null || !src.TryGetValue(k, out var sp)) continue;
            var expectRot = (sp.P.Rotation + rotDelta.GetValueOrDefault(k)) % 4; // PDFium reports quarter turns; rotDelta is in quarter turns
            if (o.Pages[i].Rotation != expectRot) gMis++;
            else if (sp.P.Media != o.Pages[i].Media || sp.P.Crop != o.Pages[i].Crop) gMis++;
        }

        r.Fields["geometry"] = gMis == 0 ? "ok" : $"WRONG on {gMis} page(s) (rotation, MediaBox or CropBox)";

        // external validators
        var qpdfFile = Path.Combine(Path.GetTempPath(), "s11-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        File.WriteAllBytes(qpdfFile, outBytes);
        var (code, qout) = Qpdf.Run("--check", qpdfFile);
        File.Delete(qpdfFile);
        var firstWarn = qout.Split('\n').FirstOrDefault(l => l.Contains("WARNING") || l.Contains("ERROR")) ?? "";
        r.Fields["qpdf"] = code == 0 ? "ok" : $"{(code == 3 ? "WARN" : "FAIL")} (exit {code}): {firstWarn.Trim()[..Math.Min(150, firstWarn.Trim().Length)]}";

        var report = StructureValidator.Validate(outBytes);
        var errs = report.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        r.Fields["pdfwriterValidator"] = report.IsValid ? $"ok ({report.Issues.Count} warnings)" : $"FAIL: {errs[0].Code}: {errs[0].Message[..Math.Min(140, errs[0].Message.Length)]}{(errs.Count > 1 ? $" (+{errs.Count - 1} more)" : "")}";

        if (baseBytesForPrefix is not null)
        {
            var prefixOk = outBytes.Length >= baseBytesForPrefix.Length && outBytes.AsSpan(0, baseBytesForPrefix.Length).SequenceEqual(baseBytesForPrefix);
            r.Fields["prefix"] = prefixOk ? $"ok (+{outBytes.Length - baseBytesForPrefix.Length} bytes appended, {100.0 * (outBytes.Length - baseBytesForPrefix.Length) / baseBytesForPrefix.Length:0}% of the original)" : "BROKEN: original bytes are not an exact prefix";
        }

        return r;
    }

    private static Dictionary<(string, string), int> Multiset(IEnumerable<(string, string)> items)
    {
        var d = new Dictionary<(string, string), int>();
        foreach (var i in items) d[i] = d.GetValueOrDefault(i) + 1;
        return d;
    }

    private static string Short(string k) => k.StartsWith("PAGE-", StringComparison.Ordinal) ? k[5..] : k;
}
