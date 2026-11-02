// S11: reads everything a page operation could damage out of a PDF, using PDFium (what a viewer sees) and MikePDF.PdfWriter's strict parser (what the file says).
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MikePDF.PdfWriter;
using S4;

namespace S11;

internal sealed record PageInfo(
    string? Key, int Rotation, string Media, string Crop, string Label,
    SortedDictionary<int, int> AnnotCounts, List<string> Links, List<(string Name, int Type, string Value)> Widgets,
    int StructChildren, string RenderHash, bool NoteHasPopup, bool ReplyHasParent);

internal sealed record OutlineEntry(int Depth, string Title, string Target);

internal sealed class Profile
{
    public bool Opened;
    public string OpenError = "";
    public int PageCount;
    public List<PageInfo> Pages = [];
    public List<OutlineEntry> Outline = [];
    public SortedDictionary<string, string> Named = new(StringComparer.Ordinal);
    public CatalogInfo Catalog = new();

    public string? KeyOf(int idx) => idx >= 0 && idx < Pages.Count ? Pages[idx].Key : null;

    private static readonly Regex KeyRe = new(@"PAGE-[A-Z0-9]+-\d+", RegexOptions.Compiled);

    public static unsafe Profile Read(byte[] bytes, bool light = false)
    {
        var pr = new Profile();
        using var d = PdfDoc.Open(bytes, true, out var err);
        if (d is null || d.Doc == IntPtr.Zero)
        {
            pr.OpenError = err;
            pr.Catalog = CatalogInfo.Read(bytes);
            return pr;
        }

        pr.Opened = true;
        pr.PageCount = P.FPDF_GetPageCount(d.Doc);
        var keys = new string?[pr.PageCount];
        for (var i = 0; i < pr.PageCount && !light; i++)
        {
            var page = d.LoadPage(i);
            var tp = P.FPDFText_LoadPage(page);
            keys[i] = KeyRe.Match(P2.PageText(tp)).Value is { Length: > 0 } m ? m : null;
            P.FPDFText_ClosePage(tp);
        }

        var pageLimit = light ? Math.Min(pr.PageCount, 200) : pr.PageCount;   // light mode (corpus sweep): first 200 pages only, no text, no render

        string Target(IntPtr dest, IntPtr action)
        {
            if (dest == IntPtr.Zero && action != IntPtr.Zero)
            {
                var type = P2.FPDFAction_GetType(action);
                if (type == 3) return "uri";
                if (type != 1) return "action:" + type;
                dest = P2.FPDFAction_GetDest(d.Doc, action);
            }

            if (dest == IntPtr.Zero) return "none";
            var idx = P2.FPDFDest_GetDestPageIndex(d.Doc, dest);
            if (idx < 0 || idx >= pr.PageCount) return "unresolved";
            return keys[idx] ?? $"page#{idx}";
        }

        for (var i = 0; i < pageLimit; i++)
        {
            var page = d.LoadPage(i);
            float l, b, r, t;
            P2.FPDFPage_GetMediaBox(page, &l, &b, &r, &t);
            var media = $"{l:0.##},{b:0.##},{r:0.##},{t:0.##}";
            var crop = P2.FPDFPage_GetCropBox(page, &l, &b, &r, &t) != 0 ? $"{l:0.##},{b:0.##},{r:0.##},{t:0.##}" : "-";
            var counts = new SortedDictionary<int, int>();
            var widgets = new List<(string, int, string)>();
            var n = P.FPDFPage_GetAnnotCount(page);
            var noteHasPopup = false;
            var replyHasParent = false;
            for (var a = 0; a < n; a++)
            {
                var annot = P.FPDFPage_GetAnnot(page, a);
                var st = P.FPDFAnnot_GetSubtype(annot);
                counts[st] = counts.GetValueOrDefault(st) + 1;
                if (st == P.ST_WIDGET)
                    widgets.Add((PdfDoc.FormFieldName(d.Form, annot), P.FPDFAnnot_GetFormFieldType(d.Form, annot), PdfDoc.FormFieldValue(d.Form, annot)));
                if (st == P.ST_TEXT)
                {
                    var popup = P.FPDFAnnot_GetLinkedAnnot(annot, "Popup");
                    if (popup != IntPtr.Zero) { noteHasPopup = true; P.FPDFPage_CloseAnnot(popup); }
                    var irt = P.FPDFAnnot_GetLinkedAnnot(annot, "IRT");
                    if (irt != IntPtr.Zero) { replyHasParent = true; P.FPDFPage_CloseAnnot(irt); }
                }

                P.FPDFPage_CloseAnnot(annot);
            }

            var links = new List<string>();
            int pos = 0;
            IntPtr link;
            while (P2.FPDFLink_Enumerate(page, &pos, &link) != 0)
            {
                var dest = P2.FPDFLink_GetDest(d.Doc, link);
                var action = dest == IntPtr.Zero ? P2.FPDFLink_GetAction(link) : IntPtr.Zero;
                links.Add(Target(dest, action));
            }

            var tree = P2.FPDF_StructTree_GetForPage(page);
            var sc = 0;
            if (tree != IntPtr.Zero) { sc = P2.FPDF_StructTree_CountChildren(tree); P2.FPDF_StructTree_Close(tree); }

            var hash = "";
            if (!light)
            {
                var (w, h, bgra) = PdfDoc.Render(page, 0.5, true);
                hash = Convert.ToHexString(SHA1.HashData(bgra))[..10];
            }
            pr.Pages.Add(new PageInfo(keys[i], P2.FPDFPage_GetRotation(page), media, crop, P2.PageLabel(d.Doc, i), counts, links, widgets, sc, hash, noteHasPopup, replyHasParent));
        }

        // Outline
        void Walk(IntPtr parent, int depth)
        {
            var bm = P2.FPDFBookmark_GetFirstChild(d.Doc, parent);
            while (bm != IntPtr.Zero && pr.Outline.Count < 10000)
            {
                var dest = P2.FPDFBookmark_GetDest(d.Doc, bm);
                var action = dest == IntPtr.Zero ? P2.FPDFBookmark_GetAction(bm) : IntPtr.Zero;
                pr.Outline.Add(new OutlineEntry(depth, P2.BookmarkTitle(bm), Target(dest, action)));
                if (depth < 16) Walk(bm, depth + 1);
                bm = P2.FPDFBookmark_GetNextSibling(d.Doc, bm);
            }
        }

        Walk(IntPtr.Zero, 0);

        // Named destinations
        var count = (int)P2.FPDF_CountNamedDests(d.Doc);
        for (var i = 0; i < count; i++)
        {
            int len = 0;
            P2.FPDF_GetNamedDest(d.Doc, i, null, &len);
            if (len <= 0) continue;
            var buf = new byte[len];
            IntPtr dest;
            fixed (byte* pb = buf) dest = P2.FPDF_GetNamedDest(d.Doc, i, pb, &len);
            var name = P2.Utf16(buf, len);
            pr.Named[name] = Target(dest, IntPtr.Zero);
        }

        pr.Catalog = CatalogInfo.Read(bytes);
        return pr;
    }
}

/// <summary>What the file itself says about the catalog, read with MikePDF.PdfWriter's strict parser (never the lenient one).</summary>
internal sealed class CatalogInfo
{
    public bool Parsed;
    public string ParseError = "";
    public bool HasOutlines, HasNamesDests, HasLegacyDests, HasAcroForm, HasStructTree, HasMarkInfo, HasPageLabels, HasOcProperties;
    public int AcroFormFields = -1;
    public int OcgCount;
    public int ObjectCount;

    public static CatalogInfo Read(byte[] bytes)
    {
        var info = new CatalogInfo();
        var opened = StrictDocument.Open(bytes);
        if (opened.TryGetError(out var e)) { info.ParseError = e.ToString(); return info; }
        var doc = opened.Value;
        info.Parsed = true;
        info.ObjectCount = doc.XrefEntryCount;
        if (doc.Root is not { } root || doc.GetObject(root.Number) is not { IsSuccess: true } rr || rr.Value is not PdfDictionary cat) return info;
        info.HasOutlines = cat.ContainsKey("Outlines");
        info.HasLegacyDests = cat.ContainsKey("Dests");
        info.HasStructTree = cat.ContainsKey("StructTreeRoot");
        info.HasMarkInfo = cat.ContainsKey("MarkInfo");
        info.HasPageLabels = cat.ContainsKey("PageLabels");
        if (Resolve(doc, cat["Names"]) is PdfDictionary names) info.HasNamesDests = names.ContainsKey("Dests");
        if (Resolve(doc, cat["AcroForm"]) is PdfDictionary af)
        {
            info.HasAcroForm = true;
            info.AcroFormFields = Resolve(doc, af["Fields"]) is PdfArray fa ? fa.Count : 0;
        }

        if (Resolve(doc, cat["OCProperties"]) is PdfDictionary oc)
        {
            info.HasOcProperties = true;
            info.OcgCount = Resolve(doc, oc["OCGs"]) is PdfArray og ? og.Count : 0;
        }

        return info;
    }

    private static PdfObject? Resolve(PdfDocument doc, PdfObject? o)
    {
        if (o is PdfReference r) { var res = doc.GetObject(r.Number); return res.IsSuccess ? res.Value : null; }
        return o;
    }

    public override string ToString() =>
        !Parsed ? "strict parse FAILED: " + ParseError
        : $"outlines={B(HasOutlines)} names/dests={B(HasNamesDests)} legacyDests={B(HasLegacyDests)} acroform={(HasAcroForm ? "yes(" + AcroFormFields + " fields)" : "no")} structTree={B(HasStructTree)} markInfo={B(HasMarkInfo)} pageLabels={B(HasPageLabels)} ocProps={(HasOcProperties ? "yes(" + OcgCount + " OCGs)" : "no")}";

    private static string B(bool v) => v ? "yes" : "no";
}
