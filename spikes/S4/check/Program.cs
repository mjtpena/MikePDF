using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S4;

public sealed class Prop
{
    public string Name { get; set; } = ""; public string Expected { get; set; } = ""; public string Actual { get; set; } = ""; public bool Ok { get; set; }
}

public sealed class AnnotInfo
{
    public string Subtype { get; set; } = ""; public bool DirectDict { get; set; }
    public double[]? Rect { get; set; }
    public bool HasAp { get; set; }
    public string ApKind { get; set; } = "";       // stream | states | none
    public string ApDetail { get; set; } = "";
    public string Contents { get; set; } = "";
    public Dictionary<string, string> Keys { get; set; } = new();
    public string[] KeyNames { get; set; } = Array.Empty<string>();
}

public static unsafe class Program
{
    static string Out = "";
    const double Scale = 1.5;

    public static int Main(string[] args)
    {
        Out = args.Length > 0 ? args[0] : Path.Combine(Gen.FindRoot(), "spikes", "S4", "out");
        var root = Gen.FindRoot();
        Qpdf.Exe = Path.Combine(root, "spikes", "_shared", "tools", "qpdf", "qpdf-12.4.2-msvc64", "bin", "qpdf.exe");
        PdfDoc.Init();
        Directory.CreateDirectory(Path.Combine(Out, "render", "pdfium"));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Out, "manifest.json"))).RootElement;
        var inputs = new Dictionary<string, byte[]>();
        foreach (var p in manifest.GetProperty("inputs").EnumerateObject()) inputs[p.Name] = File.ReadAllBytes(Path.Combine(Out, p.Value.GetProperty("file").GetString()!));
        var inputRender = new Dictionary<string, (int w, int h, byte[] px)>();
        foreach (var (k, b) in inputs)
        {
            using var d = PdfDoc.Open(b, true, out _);
            var pg = d.LoadPage(0);
            inputRender[k] = PdfDoc.Render(pg, Scale, true, k == "base" ? IntPtr.Zero : d.Form);
            PdfDoc.WritePng(Path.Combine(Out, "render", "pdfium", $"input-{k}.png"), inputRender[k].w, inputRender[k].h, inputRender[k].px);
        }
        var inputQ = inputs.ToDictionary(kv => kv.Key, kv => new Qpdf(Path.Combine(Out, "inputs", kv.Key + ".pdf")));

        Dictionary<string, JsonElement> pdfjs = new();
        var pjPath = Path.Combine(Out, "pdfjs-results.json");
        string pdfjsVersion = "not run";
        if (File.Exists(pjPath))
        {
            var pj = JsonDocument.Parse(File.ReadAllText(pjPath)).RootElement;
            pdfjsVersion = pj.GetProperty("pdfjsVersion").GetString()!;
            foreach (var p in pj.GetProperty("results").EnumerateObject()) pdfjs[p.Name] = p.Value;
        }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var c in manifest.GetProperty("cases").EnumerateArray())
        {
            string id = c.GetProperty("id").GetString()!, group = c.GetProperty("group").GetString()!, input = c.GetProperty("input").GetString()!;
            bool creatable = c.TryGetProperty("creatable", out var cr) && cr.GetBoolean();
            var caseRow = new Dictionary<string, object?>
            {
                ["id"] = id, ["group"] = group, ["title"] = c.GetProperty("title").GetString(), ["input"] = input, ["mode"] = c.TryGetProperty("mode", out var mm) ? mm.GetString() : null,
                ["creatable"] = creatable,
                ["log"] = c.TryGetProperty("log", out var lg) ? lg.EnumerateArray().Select(x => x.GetString()).ToArray() : null,
                ["note"] = c.GetProperty("note").GetString()
            };
            var perMode = new Dictionary<string, object?>();
            if (c.TryGetProperty("outputs", out var outs))
                foreach (var o in outs.EnumerateObject())
                    perMode[o.Name] = CheckOne(c, id, group, input, o.Name, o.Value.GetString()!, inputs, inputRender, inputQ, pdfjs);
            caseRow["modes"] = perMode;
            if (c.TryGetProperty("fullError", out var fe)) caseRow["fullError"] = fe.GetString();
            rows.Add(caseRow);
            Console.WriteLine($"checked {id}");
        }
        var result = new Dictionary<string, object?>
        {
            ["qpdf"] = Qpdf.Version(), ["pdfjs"] = pdfjsVersion, ["scale"] = Scale, ["cases"] = rows
        };
        File.WriteAllText(Path.Combine(Out, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Matrix.Write(Out, result);
        MatrixCompact.Write(Out);
        Checklist.Write(Out);
        Probe.Run(root, Out);
        DefaultsProbe.Run(Out);
        return 0;
    }

    // ------------------------------------------------------------------------------------------
    static Dictionary<string, object?> CheckOne(JsonElement c, string id, string group, string input, string mode, string rel,
        Dictionary<string, byte[]> inputs, Dictionary<string, (int w, int h, byte[] px)> inputRender, Dictionary<string, Qpdf> inputQ, Dictionary<string, JsonElement> pdfjs)
    {
        var r = new Dictionary<string, object?>();
        string path = Path.Combine(Out, rel);
        var bytes = File.ReadAllBytes(path);
        r["bytes"] = bytes.Length;
        r["file"] = rel;

        // ---- qpdf --check
        var (code, text) = Qpdf.Run("--check", path);
        var diag = text.Split('\n').Select(s => s.Trim()).Where(s => s.StartsWith("WARNING") || s.StartsWith("ERROR") || s.Contains("error")).Where(s => !s.Contains("errors that qpdf cannot detect")).ToList();
        r["qpdfExit"] = code;
        r["qpdfVerdict"] = code == 0 ? "pass" : code == 3 ? "pass-with-warnings" : "FAIL";
        r["qpdfDiag"] = diag;

        // ---- incremental prefix
        var inb = inputs[input];
        bool prefix = bytes.Length >= inb.Length && bytes.AsSpan(0, inb.Length).SequenceEqual(inb);
        r["inputIsExactPrefix"] = prefix;
        if (prefix) { var app = Encoding.Latin1.GetString(bytes, inb.Length, bytes.Length - inb.Length); r["incObjects"] = Regex.Matches(app, @"(?m)^(\d+) 0 obj").Select(m => int.Parse(m.Groups[1].Value)).ToList(); r["incAppendedBytes"] = bytes.Length - inb.Length; }
        r["inputBytes"] = inb.Length;

        var q = new Qpdf(path);
        var qin = inputQ[input];
        var exp = c.TryGetProperty("exp", out var ex) ? ex : default;

        // ---- AcroForm
        var cat = q.Resolve(q.Root.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        var acro = q.Get(cat, "/AcroForm");
        r["needAppearances"] = acro.ValueKind == JsonValueKind.Object && q.Get(acro, "/NeedAppearances").ValueKind == JsonValueKind.True ? "true" : (acro.ValueKind == JsonValueKind.Object ? (q.Get(acro, "/NeedAppearances").ValueKind == JsonValueKind.Undefined ? "absent" : "false") : "no AcroForm");

        // ---- annotations on page 1 (file-level)
        var annots = ReadAnnots(q);
        var inAnnots = ReadAnnots(qin);
        r["annots"] = annots;
        r["inputAnnotCount"] = inAnnots.Count;

        if (group == "annot" || group == "annot-quire")
            AnnotChecks(c, exp, q, annots, bytes, r);
        else if (group == "form" || group == "sig" && exp.ValueKind == JsonValueKind.Object && exp.TryGetProperty("fieldName", out _))
            FormChecks(c, exp, q, qin, bytes, inb, r);

        if (group == "sig") SigChecks(c, input, inputs, bytes, mode, r);

        // ---- PDFium render, no appearance regeneration by us (FPDF_ANNOT; widgets through FFLDraw in a fresh session)
        string key = $"{id}.{mode}";
        using (var d = PdfDoc.Open(bytes, true, out var err))
        {
            if (d == null) r["pdfiumOpenError"] = err;
            else
            {
                var pg = d.LoadPage(0);
                var img = PdfDoc.Render(pg, Scale, true, group is "annot" or "annot-quire" ? IntPtr.Zero : d.Form);
                PdfDoc.WritePng(Path.Combine(Out, "render", "pdfium", key + ".png"), img.w, img.h, img.bgra);
                var b = inputRender[input];
                double[]? rect = null;
                if (exp.ValueKind == JsonValueKind.Object && exp.TryGetProperty("rect", out var rr)) rect = rr.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var (tot, inr) = Diff(b, img, rect);
                r["pdfiumChangedPx"] = tot; r["pdfiumChangedInRect"] = inr;
            }
        }
        if (pdfjs.TryGetValue(key, out var pj))
        {
            if (pj.TryGetProperty("error", out var pe)) r["pdfjsError"] = pe.GetString();
            else
            {
                r["pdfjsChangedPx"] = pj.GetProperty("changedPixels").GetInt32();
                r["pdfjsChangedInRect"] = pj.TryGetProperty("changedInRect", out var cir) && cir.ValueKind == JsonValueKind.Number ? cir.GetInt32() : null;
            }
        }
        return r;
    }

    static (int total, int? inRect) Diff((int w, int h, byte[] px) a, (int w, int h, byte[] px) b, double[]? rect)
    {
        if (a.w != b.w || a.h != b.h) return (-1, null);
        int total = 0, inr = 0;
        double pad = 6;
        double rx0 = 0, rx1 = 0, ry0 = 0, ry1 = 0;
        if (rect != null) { rx0 = (rect[0] - pad) * Scale; rx1 = (rect[2] + pad) * Scale; ry0 = (792 - rect[3] - pad) * Scale; ry1 = (792 - rect[1] + pad) * Scale; }
        for (int y = 0; y < a.h; y++) for (int x = 0; x < a.w; x++)
        {
            int i = (y * a.w + x) * 4;
            int d = Math.Abs(a.px[i] - b.px[i]) + Math.Abs(a.px[i + 1] - b.px[i + 1]) + Math.Abs(a.px[i + 2] - b.px[i + 2]);
            if (d > 24) { total++; if (rect != null && x >= rx0 && x <= rx1 && y >= ry0 && y <= ry1) inr++; }
        }
        return (total, rect != null ? inr : null);
    }

    // ------------------------------------------------------------------------------------------
    internal static List<AnnotInfo> ReadAnnots(Qpdf q)
    {
        var res = new List<AnnotInfo>();
        var page = q.Resolve(q.Page1());
        var arr = q.Resolve(q.Get(page, "/Annots"));
        if (arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var el in arr.EnumerateArray())
        {
            bool direct = !Qpdf.IsRef(el, out _);
            var d = q.Resolve(el);
            if (d.ValueKind != JsonValueKind.Object) continue;
            var a = new AnnotInfo { DirectDict = direct };
            a.Subtype = Qpdf.Text(q.Get(d, "/Subtype")).TrimStart('/');
            var rect = q.Get(d, "/Rect");
            if (rect.ValueKind == JsonValueKind.Array) a.Rect = rect.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            a.Contents = Qpdf.Text(q.Get(d, "/Contents"));
            a.KeyNames = d.EnumerateObject().Select(p => p.Name).ToArray();
            var ap = q.Get(d, "/AP");
            if (ap.ValueKind == JsonValueKind.Object)
            {
                var n = q.Get(ap, "/N");
                if (n.ValueKind != JsonValueKind.Undefined)
                {
                    a.HasAp = true;
                    if (q.IsStream(n))
                    {
                        a.ApKind = "stream";
                        var sd = q.Resolve(n);
                        var bbox = q.Get(sd, "/BBox");
                        a.ApDetail = $"obj {Qpdf.ObjNum(n)}; BBox {(bbox.ValueKind == JsonValueKind.Array ? "[" + string.Join(" ", bbox.EnumerateArray().Select(x => x.GetRawText())) + "]" : "none")}; Matrix {(q.Get(sd, "/Matrix").ValueKind == JsonValueKind.Undefined ? "none" : "yes")}; Resources {(q.Get(sd, "/Resources").ValueKind == JsonValueKind.Undefined ? "none" : "yes")}";
                    }
                    else
                    {
                        a.ApKind = "states";
                        var nd = q.Resolve(n);
                        a.ApDetail = nd.ValueKind == JsonValueKind.Object ? "states: " + string.Join(",", nd.EnumerateObject().Select(p => p.Name)) : "";
                    }
                }
            }
            if (!a.HasAp) a.ApKind = "none";
            foreach (var k in new[] { "/Popup", "/IRT", "/Parent", "/Name", "/RT", "/StateModel", "/State", "/PDFIUM_HasGeneratedAP", "/LE", "/Vertices", "/L", "/QuadPoints", "/InkList", "/BS", "/Border", "/IC", "/C", "/DA", "/F", "/NM" })
            {
                var v = q.Get(d, k);
                if (v.ValueKind == JsonValueKind.Undefined) continue;
                string kind = Qpdf.IsRef(v, out _) ? "ref" : v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString()!.StartsWith("/") ? "name" : "string",
                    JsonValueKind.Array => "array", JsonValueKind.Object => "dict", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "bool", _ => "other"
                };
                a.Keys[k] = kind + (kind is "name" or "number" or "bool" or "string" ? ":" + Short(Qpdf.Text(v)) : kind == "array" ? $"[{v.GetArrayLength()}]" : "");
            }
            res.Add(a);
        }
        return res;
    }
    static string Short(string s) => s.Length > 40 ? s.Substring(0, 40) + "..." : s;

    // ------------------------------------------------------------------------------------------
    static int SubtypeConst(string s) => s switch
    {
        "Text" => P.ST_TEXT, "FreeText" => P.ST_FREETEXT, "Line" => P.ST_LINE, "Square" => P.ST_SQUARE, "Circle" => P.ST_CIRCLE, "Polygon" => P.ST_POLYGON,
        "PolyLine" => P.ST_POLYLINE, "Highlight" => P.ST_HIGHLIGHT, "Underline" => P.ST_UNDERLINE, "Squiggly" => P.ST_SQUIGGLY, "StrikeOut" => P.ST_STRIKEOUT,
        "Stamp" => P.ST_STAMP, "Ink" => P.ST_INK, "Popup" => P.ST_POPUP, _ => -1
    };

    static void AnnotChecks(JsonElement c, JsonElement exp, Qpdf q, List<AnnotInfo> annots, byte[] bytes, Dictionary<string, object?> r)
    {
        string subtype = exp.GetProperty("subtype").GetString()!;
        string contents = exp.TryGetProperty("contents", out var ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString()! : "";
        var main = annots.FirstOrDefault(a => a.Subtype == subtype && (contents == "" || a.Contents == contents)) ?? annots.FirstOrDefault(a => a.Subtype == subtype);
        r["mainAnnotFound"] = main != null;
        if (main != null)
        {
            r["apPresent"] = main.HasAp; r["apKind"] = main.ApKind; r["apDetail"] = main.ApDetail; r["directDict"] = main.DirectDict;
            r["mainKeys"] = main.Keys;
        }
        // all other annots: popup/reply linkage
        r["subtypeCounts"] = annots.GroupBy(a => a.Subtype).ToDictionary(g => g.Key, g => g.Count());
        r["anyAnnotMissingAp"] = annots.Where(a => a.Subtype != "Popup").Any(a => !a.HasAp);

        // ---- fresh PDFium session: reopen and compare properties
        var props = new List<Prop>();
        using var d = PdfDoc.Open(bytes, true, out var err);
        if (d == null) { r["reopenError"] = err; return; }
        var pg = d.LoadPage(0);
        int n = P.FPDFPage_GetAnnotCount(pg);
        IntPtr hit = IntPtr.Zero; int want = SubtypeConst(subtype);
        var found = new List<IntPtr>();
        for (int i = 0; i < n; i++)
        {
            var a = P.FPDFPage_GetAnnot(pg, i);
            if (P.FPDFAnnot_GetSubtype(a) == want && (hit == IntPtr.Zero)) { var cts = PdfDoc.GetAnnotString(a, "Contents"); if (contents == "" || cts == contents) { hit = a; continue; } }
            if (a != hit) P.FPDFPage_CloseAnnot(a);
        }
        void Add(string name, object? e, object? a, bool? ok = null) => props.Add(new Prop { Name = name, Expected = e?.ToString() ?? "", Actual = a?.ToString() ?? "", Ok = ok ?? Equals(e?.ToString(), a?.ToString()) });
        Add("annotation found by subtype+contents", "yes", hit != IntPtr.Zero ? "yes" : "no", hit != IntPtr.Zero);
        if (hit != IntPtr.Zero)
        {
            Add("subtype", subtype, subtype);
            var rc = PdfDoc.GetRect(hit);
            if (exp.TryGetProperty("rect", out var er))
            {
                var e = er.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                bool ok = Math.Abs(rc.left - e[0]) < 0.05 && Math.Abs(rc.bottom - e[1]) < 0.05 && Math.Abs(rc.right - e[2]) < 0.05 && Math.Abs(rc.top - e[3]) < 0.05;
                if (!ok && subtype == "Ink") { ok = rc.left <= e[0] + 0.05 && rc.bottom <= e[1] + 0.05 && rc.right >= e[2] - 0.05 && rc.top >= e[3] - 0.05; r["inkRectGrownByPdfium"] = true; }
                Add("rect", $"[{e[0]:0.##} {e[1]:0.##} {e[2]:0.##} {e[3]:0.##}]", $"[{rc.left:0.##} {rc.bottom:0.##} {rc.right:0.##} {rc.top:0.##}]", ok);
            }
            if (contents != "") Add("contents", contents, PdfDoc.GetAnnotString(hit, "Contents"));
            // colour from the file (FPDFAnnot_GetColor fails once an AP exists, so read /C via qpdf)
            if (exp.TryGetProperty("color", out var ec) && ec.ValueKind == JsonValueKind.Array)
            {
                var e = ec.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var raw = main != null ? ColorFromFile(q, subtype, contents) : null;
                Add("colour /C", $"{e[0]},{e[1]},{e[2]}", raw == null ? "absent" : $"{raw[0]},{raw[1]},{raw[2]}", raw != null && Math.Abs(raw[0] - e[0]) <= 1 && Math.Abs(raw[1] - e[1]) <= 1 && Math.Abs(raw[2] - e[2]) <= 1);
            }
            if (exp.TryGetProperty("quadCount", out var eq)) Add("QuadPoints count", eq.GetInt32(), (int)P.FPDFAnnot_CountAttachmentPoints(hit));
            if (exp.TryGetProperty("inkStrokes", out var ei)) Add("InkList strokes", ei.GetInt32(), (int)P.FPDFAnnot_GetInkListCount(hit));
            if (exp.TryGetProperty("textUnder", out var tu))
            {
                var tp = P.FPDFText_LoadPage(pg);
                var buf = new ushort[256];
                int len;
                fixed (ushort* bp = buf) len = P.FPDFText_GetBoundedText(tp, rc.left + 0.5, rc.top - 0.5, rc.right - 0.5, rc.bottom + 0.5, bp, 256);
                var s = new string(buf.Take(Math.Max(0, len - 1)).Select(x => (char)x).ToArray());
                Add("text under annotation rect", tu.GetString(), s.Trim());
                P.FPDFText_ClosePage(tp);
            }
            if (subtype is "Line")
            {
                FS_POINTF a, b2; bool ok = P.FPDFAnnot_GetLine(hit, &a, &b2) != 0;
                Add("Line /L endpoints readable", "yes", ok ? $"({a.x:0},{a.y:0})-({b2.x:0},{b2.y:0})" : "no", ok);
            }
            if (subtype is "Polygon" or "PolyLine")
            {
                uint cnt = P.FPDFAnnot_GetVertices(hit, null, 0);
                Add("/Vertices count", 5, (int)cnt);
            }
            if (subtype == "Text" && exp.TryGetProperty("expectPopupLink", out _) || subtype == "Text" && c.GetProperty("id").GetString() == "Q04")
            {
                var link = P.FPDFAnnot_GetLinkedAnnot(hit, "Popup");
                Add("/Popup link resolves", "yes", link != IntPtr.Zero ? "yes" : "no", link != IntPtr.Zero);
                if (link != IntPtr.Zero) P.FPDFPage_CloseAnnot(link);
                // find any Text annot with /IRT
                bool irt = false;
                for (int i = 0; i < n; i++)
                {
                    var a = P.FPDFPage_GetAnnot(pg, i);
                    var l = P.FPDFAnnot_GetLinkedAnnot(a, "IRT");
                    if (l != IntPtr.Zero) { irt = true; P.FPDFPage_CloseAnnot(l); }
                    P.FPDFPage_CloseAnnot(a);
                }
                Add("/IRT reply resolves to parent", "yes", irt ? "yes" : "no", irt);
            }
            if (exp.TryGetProperty("stampName", out var sn))
            {
                int vt = P.FPDFAnnot_GetValueType(hit, "Name");
                Add("/Name is a name object", "name (4)", vt == 4 ? "name (4)" : vt == 3 ? "string (3)" : $"type {vt}", vt == 4);
            }
            int fl = P.FPDFAnnot_GetFlags(hit);
            Add("flags (Print = 4)", 4, fl & 4);
            P.FPDFPage_CloseAnnot(hit);
        }
        r["props"] = props;
        r["reopenOk"] = props.All(p => p.Ok);
        r["reopenFailed"] = props.Where(p => !p.Ok).Select(p => $"{p.Name}: expected {p.Expected}, got {p.Actual}").ToList();
    }

    static int[]? ColorFromFile(Qpdf q, string subtype, string contents)
    {
        var page = q.Resolve(q.Page1());
        var arr = q.Resolve(q.Get(page, "/Annots"));
        foreach (var el in arr.EnumerateArray())
        {
            var d = q.Resolve(el);
            if (Qpdf.Text(q.Get(d, "/Subtype")).TrimStart('/') != subtype) continue;
            if (contents != "" && Qpdf.Text(q.Get(d, "/Contents")) != contents) continue;
            var cc = q.Get(d, "/C");
            if (cc.ValueKind != JsonValueKind.Array) return null;
            var v = cc.EnumerateArray().Select(x => (int)Math.Round(x.GetDouble() * 255)).ToArray();
            return v.Length == 3 ? v : null;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    static void FormChecks(JsonElement c, JsonElement exp, Qpdf q, Qpdf qin, byte[] bytes, byte[] inputBytes, Dictionary<string, object?> r)
    {
        if (exp.ValueKind != JsonValueKind.Object || !exp.TryGetProperty("fieldName", out var fn)) return;
        string name = fn.GetString()!, ftype = exp.GetProperty("fieldType").GetString()!;
        var fields = q.Root.GetProperty("acroform").GetProperty("fields").EnumerateArray().Where(f => f.GetProperty("fullname").GetString() == name).ToList();
        var inFields = qin.Root.GetProperty("acroform").GetProperty("fields").EnumerateArray().Where(f => f.GetProperty("fullname").GetString() == name).ToList();
        var props = new List<Prop>();
        void Add(string n, object? e, object? a, bool? ok = null) => props.Add(new Prop { Name = n, Expected = e?.ToString() ?? "", Actual = a?.ToString() ?? "", Ok = ok ?? Equals(e?.ToString(), a?.ToString()) });
        r["fieldCount"] = fields.Count;
        string Norm(string s) => s.Replace("\r\n", "\r").Replace("\n", "\r");

        string expValue = exp.TryGetProperty("value", out var ev) ? ev.GetString()! : "";
        // ---- /V and appearance in the file
        string fileValue = fields.Count > 0 ? Qpdf.Text(fields[0].GetProperty("value")) : "(missing)";
        r["fileValue"] = fileValue;
        r["fileState"] = fields.Select(f => Qpdf.Text(f.GetProperty("annotation").GetProperty("appearancestate"))).ToArray();

        // AP of each widget: decoded stream text (state-selected for buttons)
        var apInfo = new List<string>();
        bool apContainsValue = false, apChanged = false;
        foreach (var f in fields.Select((f, i) => (f, i)))
        {
            string wobj = f.f.GetProperty("annotation").GetProperty("object").GetString()!;
            var wd = q.Resolve(JsonDocument.Parse($"\"{wobj}\"").RootElement);
            var ap = q.Get(wd, "/AP"); var nn = q.Get(ap, "/N");
            int streamObj = -1;
            if (q.IsStream(nn)) streamObj = Qpdf.ObjNum(nn);
            else
            {
                var states = q.Resolve(nn);
                var asv = Qpdf.Text(q.Get(wd, "/AS"));
                if (states.ValueKind == JsonValueKind.Object && asv != "" && states.TryGetProperty(asv, out var sel)) streamObj = Qpdf.ObjNum(sel);
            }
            if (streamObj < 0) { apInfo.Add($"widget {wobj}: no /AP /N stream for current state"); continue; }
            var content = q.StreamText(streamObj);
            // compare to the input's widget AP
            string inContent = "";
            if (f.i < inFields.Count)
            {
                string iw = inFields[f.i].GetProperty("annotation").GetProperty("object").GetString()!;
                var iwd = qin.Resolve(JsonDocument.Parse($"\"{iw}\"").RootElement);
                var inn = qin.Get(qin.Get(iwd, "/AP"), "/N");
                int isobj = -1;
                if (qin.IsStream(inn)) isobj = Qpdf.ObjNum(inn);
                else { var st = qin.Resolve(inn); var ia = Qpdf.Text(qin.Get(iwd, "/AS")); if (st.ValueKind == JsonValueKind.Object && ia != "" && st.TryGetProperty(ia, out var s2)) isobj = Qpdf.ObjNum(s2); }
                if (isobj >= 0) inContent = qin.StreamText(isobj);
            }
            if (content != inContent) apChanged = true;
            if (inContent != "") r["inputHadAp"] = true;
            var tj = Regex.Matches(content, @"\(((?:\\.|[^\\)])*)\)\s*Tj").Select(m => Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1")).ToList();
            string joined = string.Concat(tj);
            string wantStr = ftype is "list_multi" ? "BetaDelta" : expValue;
            if (ftype == "list_multi") apContainsValue = joined.Contains("Beta") && joined.Contains("Delta");
            else if (expValue != "" && ftype is "text" or "multiline" or "comb" or "combo" or "combo_editable") apContainsValue = Regex.Replace(joined, @"\s", "") == Regex.Replace(Norm(expValue), @"\s", "") || Regex.Replace(joined, @"\s", "").Contains(Regex.Replace(Norm(expValue), @"\s", ""));
            apInfo.Add($"widget {wobj}: AP stream obj {streamObj}, {content.Length} bytes decoded, Tj text '{joined}', changed-vs-input={content != inContent}");
        }
        r["apInfo"] = apInfo; r["apChangedVsInput"] = apChanged; r["apContainsValue"] = apContainsValue;

        switch (ftype)
        {
            case "text": case "multiline": case "comb": case "combo": case "combo_editable":
                Add("/V in file", Norm(expValue), Norm(fileValue), Norm(fileValue) == Norm(expValue));
                break;
            case "checkbox":
                Add("/V in file", "/Yes", fileValue, fileValue == "/Yes");
                Add("/AS in file", "/Yes", ((string[])r["fileState"]!).FirstOrDefault(), ((string[])r["fileState"]!).FirstOrDefault() == "/Yes");
                break;
            case "radio":
                Add("/V in file", "/ChoiceB", fileValue, fileValue == "/ChoiceB");
                var st = (string[])r["fileState"]!;
                Add("/AS kid1,kid2", "/Off,/ChoiceB", string.Join(",", st), st.Length == 2 && st[0] == "/Off" && st[1] == "/ChoiceB");
                break;
            case "list_multi":
                {
                    string raw = fields.Count > 0 ? fields[0].GetProperty("value").GetRawText() : "";
                    bool ok = raw.Contains("u:Beta") && raw.Contains("u:Delta");
                    Add("/V in file", "[(Beta) (Delta)]", Regex.Replace(raw, @"\s+", ""), ok);
                    break;
                }
            case "pushbutton":
                Add("no /V", "none", fileValue, fileValue is "" or "(missing)" or "null");
                break;
        }
        if (ftype == "pushbutton") Add("AP unchanged (no value)", "yes", apChanged ? "no" : "yes", !apChanged);
        else if (ftype is "text" or "multiline" or "comb" or "combo" or "combo_editable") Add("widget AP shows the value", "yes", apContainsValue ? "yes" : "no", apContainsValue);
        else if (ftype == "list_multi") Add("widget AP regenerated for selection", "yes", apChanged ? "yes" : "no", apChanged);

        // ---- fresh PDFium session
        using var d = PdfDoc.Open(bytes, true, out var err);
        if (d == null) { r["reopenError"] = err; return; }
        var pg = d.LoadPage(0);
        int idx = 0;
        IntPtr a = IntPtr.Zero;
        int n = P.FPDFPage_GetAnnotCount(pg);
        var widgets = new List<IntPtr>();
        for (int i = 0; i < n; i++)
        {
            var w = P.FPDFPage_GetAnnot(pg, i);
            if (P.FPDFAnnot_GetSubtype(w) == P.ST_WIDGET && PdfDoc.FormFieldName(d.Form, w) == name) widgets.Add(w); else P.FPDFPage_CloseAnnot(w);
        }
        if (widgets.Count == 0) Add("widget found after reopen", "yes", "no", false);
        else
        {
            a = widgets[ftype == "radio" ? 1 : 0];
            string pv = PdfDoc.FormFieldValue(d.Form, a);
            switch (ftype)
            {
                case "text": case "multiline": case "comb": case "combo": case "combo_editable":
                    Add("PDFium value after reopen", Norm(expValue), Norm(pv)); break;
                case "checkbox":
                    Add("PDFium IsChecked after reopen", "1", P.FPDFAnnot_IsChecked(d.Form, a)); break;
                case "radio":
                    Add("PDFium IsChecked(kid2) / IsChecked(kid1)", "1/0", $"{P.FPDFAnnot_IsChecked(d.Form, widgets[1])}/{P.FPDFAnnot_IsChecked(d.Form, widgets[0])}"); break;
                case "list_multi":
                    Add("PDFium selected options after reopen", "Beta,Delta", string.Join(",", Enumerable.Range(0, 5).Where(i => P.FPDFAnnot_IsOptionSelected(d.Form, a, i) != 0).Select(i => Fx2.Opts[i])));
                    break;
                case "pushbutton":
                    Add("PDFium field type after reopen", "pushbutton", P.FPDFAnnot_GetFormFieldType(d.Form, a) == P.FT_PUSH ? "pushbutton" : "other", P.FPDFAnnot_GetFormFieldType(d.Form, a) == P.FT_PUSH); break;
            }
            foreach (var w in widgets) P.FPDFPage_CloseAnnot(w);
        }
        r["props"] = props;
        r["reopenOk"] = props.All(p => p.Ok);
        r["reopenFailed"] = props.Where(p => !p.Ok).Select(p => $"{p.Name}: expected {p.Expected}, got {p.Actual}").ToList();
    }

    // ------------------------------------------------------------------------------------------
    static void SigChecks(JsonElement c, string input, Dictionary<string, byte[]> inputs, byte[] bytes, string mode, Dictionary<string, object?> r)
    {
        var notes = new List<string>();
        var inb = inputs[input];
        (int[] br, byte[] contents)? Read(byte[] b)
        {
            using var d = PdfDoc.Open(b, false, out _);
            if (d == null) return null;
            int cnt = P.FPDF_GetSignatureCount(d.Doc);
            if (cnt < 1) return (Array.Empty<int>(), Array.Empty<byte>());
            var sig = P.FPDF_GetSignatureObject(d.Doc, 0);
            uint nbr = P.FPDFSignatureObj_GetByteRange(sig, null, 0);
            var br = new int[nbr];
            fixed (int* bp = br) P.FPDFSignatureObj_GetByteRange(sig, bp, nbr);
            uint nc = P.FPDFSignatureObj_GetContents(sig, null, 0);
            var cb = new byte[nc];
            fixed (byte* cp = cb) P.FPDFSignatureObj_GetContents(sig, cp, nc);
            return (br, cb);
        }
        int CountSigs(byte[] b) { using var d = PdfDoc.Open(b, false, out _); return d == null ? -1 : P.FPDF_GetSignatureCount(d.Doc); }
        var before = Read(inb); var after = Read(bytes);
        r["sigCountBefore"] = CountSigs(inb); r["sigCountAfter"] = CountSigs(bytes);
        if (before != null && after != null)
        {
            r["byteRangeBefore"] = string.Join(" ", before.Value.br); r["byteRangeAfter"] = string.Join(" ", after.Value.br);
            r["byteRangeUnchanged"] = before.Value.br.SequenceEqual(after.Value.br);
            r["contentsUnchanged"] = before.Value.contents.SequenceEqual(after.Value.contents);
            // Cryptographic check with the ORIGINAL ByteRange against the NEW file bytes
            var (ok1, m1) = VerifyCms(bytes, before.Value.br, before.Value.contents);
            r["cmsOriginalRangeOnNewBytes"] = ok1 + ": " + m1;
            r["cmsVerifiesOriginalRange"] = ok1;
            // And with the ByteRange as now written in the new file (what a viewer would use)
            if (after.Value.br.Length == 4)
            {
                var (ok2, m2) = VerifyCms(bytes, after.Value.br, after.Value.contents);
                r["cmsVerifiesWrittenRange"] = ok2; r["cmsWrittenRangeMsg"] = m2;
                r["byteRangeEndsAtFileEnd"] = (long)after.Value.br[2] + after.Value.br[3] == bytes.Length;
            }
        }
        r["signedRevisionBytes"] = before is { } b0 && b0.br.Length == 4 ? (long)b0.br[2] + b0.br[3] : -1;
    }

    static (bool ok, string msg) VerifyCms(byte[] file, int[] br, byte[] cmsDer)
    {
        try
        {
            if (br.Length != 4) return (false, "no ByteRange");
            if ((long)br[2] + br[3] > file.Length || br[1] < 0 || br[2] < br[1]) return (false, "ByteRange exceeds file length or is malformed");
            // trim zero padding to the DER length
            int derLen = DerLength(cmsDer);
            var der = derLen > 0 && derLen <= cmsDer.Length ? cmsDer.AsSpan(0, derLen).ToArray() : cmsDer;
            var data = file.Take(br[1]).Concat(file.Skip(br[2]).Take(br[3])).ToArray();
            var cms = new SignedCms(new ContentInfo(data), true);
            cms.Decode(der);
            cms.CheckSignature(true);
            return (true, "CheckSignature ok (signature-only, chain not evaluated)");
        }
        catch (Exception e) { return (false, "CheckSignature failed: " + e.Message.Split('\n')[0]); }
    }

    static int DerLength(byte[] b)
    {
        if (b.Length < 2 || b[0] != 0x30) return -1;
        if ((b[1] & 0x80) == 0) return 2 + b[1];
        int nb = b[1] & 0x7F; int len = 0;
        for (int i = 0; i < nb; i++) len = (len << 8) | b[2 + i];
        return 2 + nb + len;
    }
}

static class Gen
{
    public static string FindRoot() => Program2.FindRoot();
}
static class Program2
{
    public static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "spikes"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repo root not found");
    }
}
static class Fx2 { public static readonly string[] Opts = { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }; }
