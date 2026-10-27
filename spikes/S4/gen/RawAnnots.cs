// "Quire-authored" annotations: dictionaries and appearance streams written directly as an incremental update to
// the base fixture (no PDFium editing APIs). This is a stand-in for Quire.PdfWriter and proves that the gaps
// PDFium's public API leaves (Line, Polygon, PolyLine, /Popup + /IRT links, name objects, FreeText AP) can be
// closed by writing objects ourselves.
using System.Globalization;
using System.Text;

namespace S4;

public static class RawAnnots
{
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static byte[] Obj(string s) => Fx.Obj(s);

    public sealed record Result(PdfFile File, FS_RECTF Rect, string Subtype, string Contents, string Note);

    static string Hdr(string subtype, double[] rect, string contents, string extra = "")
        => $"/Type /Annot /Subtype /{subtype} /Rect [{string.Join(" ", rect.Select(F))}] /P 3 0 R {(extra.Contains("/F ") ? "" : "/F 4 ")}/Contents ({contents}) /T (Quire Tester) /M ({Fx.ModDate}) /NM (s4-{subtype.ToLowerInvariant()}-0001) {extra}";

    static byte[] Ap(double[] bbox, string content, string resources = "")
        => Fx.Stream($"/Type /XObject /Subtype /Form /BBox [{string.Join(" ", bbox.Select(F))}] /Matrix [1 0 0 1 0 0] /Resources << {resources} >>", content);

    static (PdfFile, int) Begin(PdfFile b) => (b, b.Size);

    static PdfFile Finish(PdfFile b, SortedDictionary<int, byte[]> objs, IEnumerable<int> annotNums)
    {
        objs[3] = Obj(Fx.PageBody(" /Annots [" + string.Join(" ", annotNums.Select(n => $"{n} 0 R")) + "]"));
        return Fx.Append(b, objs, objs.Keys.Max() + 1);
    }

    static string Arrow(string style, double ex, double ey, double dx, double dy, double len = 10, double ang = 25)
    {
        // (ex,ey) end point, (dx,dy) unit vector pointing OUT of the line at that end
        double a = ang * Math.PI / 180;
        (double, double) Rot(double x, double y, double t) => (x * Math.Cos(t) - y * Math.Sin(t), x * Math.Sin(t) + y * Math.Cos(t));
        var (r1x, r1y) = Rot(-dx, -dy, a); var (r2x, r2y) = Rot(-dx, -dy, -a);
        double p1x = ex + len * r1x, p1y = ey + len * r1y, p2x = ex + len * r2x, p2y = ey + len * r2y;
        return style switch
        {
            "OpenArrow" => $"{F(p1x)} {F(p1y)} m {F(ex)} {F(ey)} l {F(p2x)} {F(p2y)} l S\n",
            "ClosedArrow" => $"{F(p1x)} {F(p1y)} m {F(ex)} {F(ey)} l {F(p2x)} {F(p2y)} l h B\n",
            _ => ""
        };
    }

    public static Result Line(PdfFile b)
    {
        var (_, n) = Begin(b);
        double x1 = 100, y1 = 500, x2 = 330, y2 = 470, w = 2;
        double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)); double ux = (x2 - x1) / len, uy = (y2 - y1) / len;
        double pad = 14;
        double[] rect = { Math.Min(x1, x2) - pad, Math.Min(y1, y2) - pad, Math.Max(x1, x2) + pad, Math.Max(y1, y2) + pad };
        string content = $"0 0 1 RG 0.6 0.8 1 rg {F(w)} w {F(x1)} {F(y1)} m {F(x2)} {F(y2)} l S\n" +
                         Arrow("OpenArrow", x1, y1, -ux, -uy) + Arrow("ClosedArrow", x2, y2, ux, uy);
        var o = new SortedDictionary<int, byte[]>();
        o[n] = Obj($"<< {Hdr("Line", rect, "Line with arrow endings", $"/C [0 0 1] /IC [0.6 0.8 1] /L [{F(x1)} {F(y1)} {F(x2)} {F(y2)}] /LE [/OpenArrow /ClosedArrow] /BS << /W {F(w)} /S /S >> /AP << /N {n + 1} 0 R >>")} >>");
        o[n + 1] = Ap(rect, content);
        return new Result(Finish(b, o, new[] { n }), new FS_RECTF { left = (float)rect[0], bottom = (float)rect[1], right = (float)rect[2], top = (float)rect[3] }, "Line", "Line with arrow endings", "Quire-authored /L, /LE, /BS and /AP");
    }

    public static Result Polygon(PdfFile b)
    {
        var (_, n) = Begin(b);
        double[] v = { 360, 520, 420, 540, 480, 500, 460, 440, 380, 450 };
        double[] rect = { 360 - 3, 440 - 3, 480 + 3, 540 + 3 };
        var pts = new StringBuilder($"{F(v[0])} {F(v[1])} m ");
        for (int i = 2; i < v.Length; i += 2) pts.Append($"{F(v[i])} {F(v[i + 1])} l ");
        string content = $"0.8 0.2 0 RG 1 0.9 0.5 rg 2 w {pts}h B\n";
        var o = new SortedDictionary<int, byte[]>();
        o[n] = Obj($"<< {Hdr("Polygon", rect, "Polygon annotation", $"/C [0.8 0.2 0] /IC [1 0.9 0.5] /Vertices [{string.Join(" ", v.Select(F))}] /BS << /W 2 /S /S >> /AP << /N {n + 1} 0 R >>")} >>");
        o[n + 1] = Ap(rect, content);
        return new Result(Finish(b, o, new[] { n }), new FS_RECTF { left = (float)rect[0], bottom = (float)rect[1], right = (float)rect[2], top = (float)rect[3] }, "Polygon", "Polygon annotation", "Quire-authored /Vertices, /IC and /AP");
    }

    public static Result PolyLine(PdfFile b)
    {
        var (_, n) = Begin(b);
        double[] v = { 100, 400, 150, 440, 200, 400, 250, 440, 300, 400 };
        double[] rect = { 100 - 14, 400 - 14, 300 + 14, 440 + 14 };
        var pts = new StringBuilder($"{F(v[0])} {F(v[1])} m ");
        for (int i = 2; i < v.Length; i += 2) pts.Append($"{F(v[i])} {F(v[i + 1])} l ");
        // arrow at the last vertex, direction = last segment
        double dx = v[8] - v[6], dy = v[9] - v[7]; double l = Math.Sqrt(dx * dx + dy * dy); dx /= l; dy /= l;
        string content = $"0 0.5 0 RG 2 w {pts}S\n" + Arrow("OpenArrow", v[8], v[9], dx, dy);
        var o = new SortedDictionary<int, byte[]>();
        o[n] = Obj($"<< {Hdr("PolyLine", rect, "PolyLine annotation", $"/C [0 0.5 0] /Vertices [{string.Join(" ", v.Select(F))}] /LE [/None /OpenArrow] /BS << /W 2 /S /S >> /AP << /N {n + 1} 0 R >>")} >>");
        o[n + 1] = Ap(rect, content);
        return new Result(Finish(b, o, new[] { n }), new FS_RECTF { left = (float)rect[0], bottom = (float)rect[1], right = (float)rect[2], top = (float)rect[3] }, "PolyLine", "PolyLine annotation", "Quire-authored /Vertices, /LE and /AP");
    }

    /// <summary>Text (sticky note) with /Popup, an /IRT reply and a review-state annotation.</summary>
    public static Result TextPopupReply(PdfFile b)
    {
        var (_, n) = Begin(b);
        int text = n, popup = n + 1, reply = n + 2, state = n + 3, apn = n + 4;
        double x = 480, y = 560;
        double[] rect = { x, y, x + 20, y + 20 };
        string icon = "1 0.92 0.2 rg 0 G 0.8 w 0.4 0.4 19.2 19.2 re B 0 G 0.6 w 4 14 m 16 14 l S 4 10 m 16 10 l S 4 6 m 12 6 l S\n";
        var o = new SortedDictionary<int, byte[]>();
        o[text] = Obj($"<< {Hdr("Text", rect, "Parent comment: sticky note with popup", $"/Name /Comment /Open false /C [1 0.92 0.2] /CA 1 /Popup {popup} 0 R /AP << /N {apn} 0 R >> /CreationDate ({Fx.ModDate})")} >>");
        o[popup] = Obj($"<< /Type /Annot /Subtype /Popup /Rect [{F(x + 30)} {F(y - 120)} {F(x + 130)} {F(y)}] /Parent {text} 0 R /Open false /F 28 >>");
        o[reply] = Obj($"<< {Hdr("Text", rect, "Reply to the parent comment", $"/Name /Comment /IRT {text} 0 R /RT /R /C [1 0.92 0.2] /F 28 /CreationDate ({Fx.ModDate})").Replace("s4-text-0001", "s4-text-reply")} >>");
        o[state] = Obj($"<< {Hdr("Text", rect, "Accepted", $"/Name /Comment /IRT {text} 0 R /RT /R /StateModel /Review /State /Accepted /C [1 0.92 0.2] /F 28").Replace("s4-text-0001", "s4-text-state")} >>");
        o[apn] = Ap(new double[] { 0, 0, 20, 20 }, icon);
        // parent first so the /IRT targets precede their replies in /Annots
        return new Result(Finish(b, o, new[] { text, popup, reply, state }), new FS_RECTF { left = (float)rect[0], bottom = (float)rect[1], right = (float)rect[2], top = (float)rect[3] }, "Text", "Parent comment: sticky note with popup", "Quire-authored /Popup, /Parent, /IRT, /RT, /StateModel, /State");
    }

    public static Result StampName(PdfFile b)
    {
        var (_, n) = Begin(b);
        double[] rect = { 100, 300, 260, 350 };
        string content = "1 1 1 rg 0 0 160 50 re f 0.8 0 0 RG 0.8 0 0 rg 3 w 2 2 156 46 re S BT /F2 22 Tf 18 16 Td (APPROVED) Tj ET\n";
        var o = new SortedDictionary<int, byte[]>();
        o[n] = Obj($"<< {Hdr("Stamp", rect, "Standard Approved stamp", $"/Name /Approved /AP << /N {n + 1} 0 R >>")} >>");
        o[n + 1] = Ap(new double[] { 0, 0, 160, 50 }, content, "/Font << /F2 6 0 R >>");
        return new Result(Finish(b, o, new[] { n }), new FS_RECTF { left = 100, bottom = 300, right = 260, top = 350 }, "Stamp", "Standard Approved stamp", "Quire-authored /Name /Approved (name object) and /AP");
    }

    public static Result FreeText(PdfFile b)
    {
        var (_, n) = Begin(b);
        double[] rect = { 300, 300, 520, 350 };
        string content = "1 1 0.8 rg 0 G 1 w 0.5 0.5 219 49 re B BT /F1 14 Tf 0 g 6 30 Td (FreeText with a real AP) Tj 0 -16 Td (second line of text) Tj ET\n";
        var o = new SortedDictionary<int, byte[]>();
        o[n] = Obj($"<< {Hdr("FreeText", rect, "FreeText with a real AP", $"/DA (0 g /F1 14 Tf) /Q 0 /C [1 1 0.8] /BS << /W 1 >> /AP << /N {n + 1} 0 R >>")} >>");
        o[n + 1] = Ap(new double[] { 0, 0, 220, 50 }, content, "/Font << /F1 5 0 R >>");
        return new Result(Finish(b, o, new[] { n }), new FS_RECTF { left = 300, bottom = 300, right = 520, top = 350 }, "FreeText", "FreeText with a real AP", "Quire-authored /DA, /Q and /AP with font resource");
    }
}
