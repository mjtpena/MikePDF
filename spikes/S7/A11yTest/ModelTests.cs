using System.IO;
using S7.Core.Engine;
using S7.Core.Geometry;
using S7.Core.Text;

namespace A11yTest;

/// <summary>In-process tests of the text model, the range semantics and the geometry. Same code the WinUI provider calls.</summary>
public static class ModelTests
{
    private const string Suite = "model";

    public static void Run()
    {
        foreach (var ex in Expect.All)
        {
            using var e = PdfiumEngine.Open(Path.Combine(Paths.Fixtures, ex.File));
            var data = e.ExtractPage(0);
            var m = TextModelBuilder.Build(data);
            Console.WriteLine($"-- {ex.File}: order source = {m.OrderSource}");

            Results.Equal(Suite, ex.File, "-", "document text", ex.FullText, m.GetText(0, m.Length));
            Results.SequenceEqual(Suite, ex.File, "-", "walk by word (Move +1)", ex.Words, Walk(m, TextUnitKind.Word).Select(t => t.Trim()).ToList());
            Results.SequenceEqual(Suite, ex.File, "-", "walk by line (Move +1)", ex.Lines, Walk(m, TextUnitKind.Line).Select(t => t.Trim()).ToList());
            Results.SequenceEqual(Suite, ex.File, "-", "walk by paragraph (Move +1)", ex.Paragraphs, Walk(m, TextUnitKind.Paragraph).Select(t => t.Trim()).ToList());
            var chars = Walk(m, TextUnitKind.Character);
            Results.Add(Suite, ex.File, "-", "walk by character covers whole text", string.Concat(chars) == m.GetText(0, m.Length) && chars.Count == m.Length, $"{chars.Count} chars");
            Results.SequenceEqual(Suite, ex.File, "-", "walk by word backwards (Move -1) mirrors forward", ex.Words.Reverse().ToList(), WalkBack(m, TextUnitKind.Word).Select(t => t.Trim()).ToList());

            GeometryChecks(e, data, m, ex.File);
        }
        SemanticChecks();
    }

    private static List<string> Walk(TextModel m, TextUnitKind u)
    {
        var l = new List<string>();
        var r = new TextRange(m, 0, 0);
        r.ExpandToEnclosingUnit(u);
        while (true)
        {
            l.Add(r.GetText());
            if (r.Move(u, 1) == 0) break;
            if (l.Count > 100000) throw new InvalidOperationException("runaway walk");
        }
        return l;
    }

    private static List<string> WalkBack(TextModel m, TextUnitKind u)
    {
        var l = new List<string>();
        var r = new TextRange(m, m.Length, m.Length);
        r.ExpandToEnclosingUnit(u);
        while (true)
        {
            l.Add(r.GetText());
            if (r.Move(u, -1) == 0) break;
        }
        return l;
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static void GeometryChecks(PdfiumEngine e, PageTextData data, TextModel m, string file)
    {
        for (int v = 0; v < 4; v++)
        {
            var geo = new PageGeometry(data.Box, v);
            var (w, h) = e.DeviceSizeFor(0, 1.0, v);
            double maxErr = 0; int n = 0;
            foreach (var c in data.Chars)
            {
                if (c.Generated || c.Text.Length == 0) continue;
                foreach (var (ux, uy) in new[] { (c.Loose.Left, c.Loose.Bottom), (c.Loose.Right, c.Loose.Top) })
                {
                    var (px, py) = geo.UserToPage(ux, uy);
                    var (dx, dy) = e.PdfiumPageToDevice(0, w, h, v, ux, uy);
                    maxErr = Math.Max(maxErr, Math.Max(Math.Abs(px - dx), Math.Abs(py - dy)));
                    n++;
                }
            }
            Results.Add(Suite, file, $"view {v * 90}", "PageGeometry.UserToPage vs FPDF_PageToDevice", maxErr <= 1.0, $"{n} points, max |error| {maxErr:F3} px (PDFium returns ints)");

            // Inverse: DeviceToPage then back
            double invErr = 0;
            foreach (var (px, py) in new[] { (10.0, 10.0), (w / 2.0, h / 3.0), (w - 5.0, h - 7.0) })
            {
                var (ux, uy) = geo.PageToUser(px, py);
                var (bx, by) = geo.UserToPage(ux, uy);
                invErr = Math.Max(invErr, Math.Max(Math.Abs(bx - px), Math.Abs(by - py)));
                var (qx, qy) = e.PdfiumDeviceToPage(0, w, h, v, (int)px, (int)py);
                if (Math.Max(Math.Abs(qx - ux), Math.Abs(qy - uy)) > 1.01) invErr = 99;
            }
            Results.Add(Suite, file, $"view {v * 90}", "PageToUser round trip and FPDF_DeviceToPage agree", invErr < 0.001, $"max error {invErr:F4}");

            // TextModel's own base->view rotation must agree with PageGeometry built for that view rotation.
            double rectErr = 0; int rn = 0, modified = 0;
            foreach (var a in m.Atoms)
            {
                if (a.Source < 0 || a.Rect is not { } br) continue;
                var c = data.Chars[a.Source];
                var baseRect = new PageGeometry(data.Box, 0).UserRectToPage(c.Loose);
                if (Math.Abs(baseRect.X - br.X) > 0.01 || Math.Abs(baseRect.Width - br.Width) > 0.01) { modified++; continue; }
                var viaModel = m.ToView(br, v);
                var viaGeo = geo.UserRectToPage(c.Loose);
                rectErr = Math.Max(rectErr, new[] { Math.Abs(viaModel.X - viaGeo.X), Math.Abs(viaModel.Y - viaGeo.Y), Math.Abs(viaModel.Width - viaGeo.Width), Math.Abs(viaModel.Height - viaGeo.Height) }.Max());
                rn++;
            }
            Results.Add(Suite, file, $"view {v * 90}", "TextModel.ToView vs PageGeometry(view) per glyph", rectErr < 0.01, $"{rn} glyphs compared, {modified} ligature/hyphen-adjusted glyphs skipped, max error {rectErr:F4} pt");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static void SemanticChecks()
    {
        string fx = "d-ligatures-hyphenation.pdf";
        using var e = PdfiumEngine.Open(Path.Combine(Paths.Fixtures, fx));
        var data = e.ExtractPage(0);
        var m = TextModelBuilder.Build(data);
        void T(string name, bool ok, string detail = "") => Results.Add(Suite, fx, "-", "semantics: " + name, ok, detail);

        // Hyphenated word spans two lines, and the hyphen is not in the text.
        int ex = m.GetText(0, m.Length).IndexOf("exam", StringComparison.Ordinal);
        var r = new TextRange(m, ex, ex);
        r.ExpandToEnclosingUnit(TextUnitKind.Word);
        T("hyphen-joined word 'example' is one Word unit", r.GetText() == "example ", $"got '{r.GetText()}'");
        T("... and its bounding rects span two lines", m.LineRects(r.Start, r.End).Count() == 2, $"{m.LineRects(r.Start, r.End).Count()} rects");
        var r2 = new TextRange(m, ex + 4, ex + 4);   // 'p' of "ple"
        r2.ExpandToEnclosingUnit(TextUnitKind.Line);
        T("Line unit at 'ple' is the second visual line", r2.GetText().StartsWith("ple of line"), $"got '{r2.GetText()}'");

        // Ligature expansion: 'fi' in "official" are two separate characters sharing one glyph box split in two.
        int of = m.GetText(0, m.Length).IndexOf("official", StringComparison.Ordinal);
        var fi = m.Atoms[of + 2]; var ii = m.Atoms[of + 3];
        T("ligature 'fi' is two characters 'f','i'", fi.Text == "f" && ii.Text == "i" && fi.Source != ii.Source);
        T("... whose rectangles tile the glyph without overlap", fi.Rect is { } a && ii.Rect is { } b && Math.Abs(a.Right - b.X) < 0.01 && Math.Abs(a.Width - b.Width) < 0.01,
            fi.Rect is { } a2 && ii.Rect is { } b2 ? $"f=[{a2.X:F2},{a2.Right:F2}] i=[{b2.X:F2},{b2.Right:F2}]" : "no rect");

        // Move / MoveEndpointByUnit
        var w = new TextRange(m, 0, 0);
        w.ExpandToEnclosingUnit(TextUnitKind.Word);
        T("ExpandToEnclosingUnit(Word) at start", w.GetText() == "The ", $"got '{w.GetText()}'");
        T("Move(Word,-1) at first word moves 0", w.Move(TextUnitKind.Word, -1) == 0 && w.GetText() == "The ");
        int total = Expect.All.First(x => x.File == fx).Words.Length;
        var big = w.Clone();
        int moved = big.Move(TextUnitKind.Word, 100000);
        T("Move(Word,+huge) stops on the last word and returns count-1", moved == total - 1 && big.GetText().TrimEnd().EndsWith("again."), $"moved {moved}, expected {total - 1}, text '{big.GetText()}'");
        T("... a further Move returns 0", big.Move(TextUnitKind.Word, 1) == 0);
        var d = new TextRange(m, 0, 0);
        int mv = d.MoveEndpointByUnit(RangeEndpoint.End, TextUnitKind.Word, 2);
        T("MoveEndpointByUnit(End,Word,+2) from a degenerate range", mv == 2 && d.GetText() == "The official ", $"moved {mv}, text '{d.GetText()}'");
        int mv2 = d.MoveEndpointByUnit(RangeEndpoint.Start, TextUnitKind.Word, 5);
        T("MoveEndpointByUnit(Start,...) past End collapses the range onto the new start", mv2 > 0 && d.IsDegenerate && d.Start == d.End, $"moved {mv2}, [{d.Start},{d.End})");
        var mid = new TextRange(m, 6, 6);   // inside "official"
        int back = mid.MoveEndpointByUnit(RangeEndpoint.Start, TextUnitKind.Word, -1);
        T("MoveEndpointByUnit(Start,Word,-1) from mid-word goes to the word start and counts 1", back == -1 && mid.Start == 4, $"moved {back}, start {mid.Start}");
        var c = new TextRange(m, 0, 5);
        var c2 = c.Clone();
        T("Compare / CompareEndpoints", c.Compare(c2) && c.CompareEndpoints(RangeEndpoint.Start, c2, RangeEndpoint.End) < 0 && c.CompareEndpoints(RangeEndpoint.End, c2, RangeEndpoint.End) == 0);
        var para = new TextRange(m, 10, 10);
        para.ExpandToEnclosingUnit(TextUnitKind.Paragraph);
        T("ExpandToEnclosingUnit(Paragraph) returns the whole first paragraph", para.GetText().StartsWith("The official offline") && para.GetText().TrimEnd().EndsWith("in the data."));
        var doc = new TextRange(m, 10, 12);
        doc.ExpandToEnclosingUnit(TextUnitKind.Document);
        T("ExpandToEnclosingUnit(Document) is the whole text", doc.Start == 0 && doc.End == m.Length);
        var page = new TextRange(m, 10, 12);
        page.ExpandToEnclosingUnit(TextUnitKind.Page);
        T("ExpandToEnclosingUnit(Page) is the whole (single-page) text", page.Start == 0 && page.End == m.Length);
        var endpos = new TextRange(m, m.Length, m.Length);
        endpos.ExpandToEnclosingUnit(TextUnitKind.Word);
        T("ExpandToEnclosingUnit(Word) at end-of-document yields the last word", endpos.GetText() == "again.", $"got '{endpos.GetText()}'");
        var fmt = new TextRange(m, 0, 0);
        fmt.ExpandToEnclosingUnit(TextUnitKind.Format);
        T("Format unit (font-size run) is the whole single-font page", fmt.End == m.Length, $"[{fmt.Start},{fmt.End})");

        // Hit testing at all four rotations returns the glyph under a glyph's own centre.
        for (int v = 0; v < 4; v++)
        {
            int ok = 0, tot = 0;
            for (int i = 0; i < m.Length; i++)
            {
                if (m.Atoms[i].Rect is not { } rr) continue;
                var vr = m.ToView(rr, v);
                int hit = m.HitTest(vr.X + vr.Width / 2, vr.Y + vr.Height / 2, v);
                tot++; if (hit == i) ok++;
            }
            Results.Add(Suite, fx, $"view {v * 90}", "semantics: HitTest at each glyph centre returns that glyph", ok == tot, $"{ok}/{tot}");
        }
    }
}
