using System.Runtime.InteropServices;

namespace S4;

public static unsafe class Cases
{
    // ------------------------------------------------------------------ helpers
    static string Dump(FS_RECTF r) => $"[{r.left:0.##} {r.bottom:0.##} {r.right:0.##} {r.top:0.##}]";

    /// <summary>Quad points over real text found by FPDFText_FindStart; also returns the union (PDF space, bottom/top).</summary>
    static List<FS_QUADPOINTSF> Quads(Ctx c, string phrase, out FS_RECTF union)
    {
        var res = new List<FS_QUADPOINTSF>();
        union = default;
        var tp = P.FPDFText_LoadPage(c.Page);
        var sch = P.FPDFText_FindStart(tp, phrase, 0, 0);
        if (P.FPDFText_FindNext(sch) == 0) { c.L("FPDFText_FindNext(" + phrase + ")", 0); P.FPDFText_FindClose(sch); P.FPDFText_ClosePage(tp); return res; }
        int idx = P.FPDFText_GetSchResultIndex(sch), cnt = P.FPDFText_GetSchCount(sch);
        int n = P.FPDFText_CountRects(tp, idx, cnt);
        c.L($"text search '{phrase}'", $"start={idx} count={cnt} rects={n}");
        float ul = float.MaxValue, ub = float.MaxValue, ur = float.MinValue, ut = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            double l, t, r, b;
            P.FPDFText_GetRect(tp, i, &l, &t, &r, &b);
            // Z-order (TL, TR, BL, BR): what Acrobat and PDFium's own reader (RectFromQuadPoints uses pts 3 and 2) expect
            res.Add(new FS_QUADPOINTSF { x1 = (float)l, y1 = (float)t, x2 = (float)r, y2 = (float)t, x3 = (float)l, y3 = (float)b, x4 = (float)r, y4 = (float)b });
            ul = Math.Min(ul, (float)l); ub = Math.Min(ub, (float)b); ur = Math.Max(ur, (float)r); ut = Math.Max(ut, (float)t);
        }
        union = new FS_RECTF { left = ul, bottom = ub, right = ur, top = ut };
        P.FPDFText_FindClose(sch); P.FPDFText_ClosePage(tp);
        return res;
    }

    static void SetStr(Ctx c, IntPtr an, string key, string val) => c.L($"SetStringValue({key})", P.FPDFAnnot_SetStringValue(an, key, val));

    static void Common(Ctx c, IntPtr an, string contents, string nm)
    {
        SetStr(c, an, "Contents", contents);
        SetStr(c, an, "T", "Quire Tester");
        SetStr(c, an, "NM", nm);
        SetStr(c, an, "M", Fx.ModDate);
        c.L("SetFlags(Print)", P.FPDFAnnot_SetFlags(an, 4));
    }

    static void ExpRect(Ctx c, FS_RECTF r) => c.Exp["rect"] = new[] { r.left, r.bottom, r.right, r.top };

    static void SetRect(Ctx c, IntPtr an, FS_RECTF r) { c.L("SetRect " + Dump(r), P.FPDFAnnot_SetRect(an, &r)); ExpRect(c, r); }

    // FS_RECTF has {left, top, right, bottom} order in memory; our "bottom/top" naming is by field name, so set by name.
    static FS_RECTF R(float l, float b, float r, float t) => new FS_RECTF { left = l, bottom = b, right = r, top = t };

    static IntPtr Create(Ctx c, int subtype, string name)
    {
        int sup = P.FPDFAnnot_IsSupportedSubtype(subtype);
        var an = P.FPDFPage_CreateAnnot(c.Page, subtype);
        c.L($"FPDFAnnot_IsSupportedSubtype({name})", sup);
        c.L($"FPDFPage_CreateAnnot({name})", an == IntPtr.Zero ? "NULL" : "ok");
        c.Exp["subtype"] = name;
        return an;
    }

    // ------------------------------------------------------------------ text markup
    static Func<Ctx, bool> Markup(int subtype, string name, string phrase, uint r, uint g, uint b, uint a)
        => c =>
        {
            var quads = Quads(c, phrase, out var union);
            if (quads.Count == 0) return false;
            var an = Create(c, subtype, name);
            if (an == IntPtr.Zero) return false;
            c.L("SetColor(Color)", P.FPDFAnnot_SetColor(an, 0, r, g, b, a));
            foreach (var q0 in quads) { var q = q0; c.L("AppendAttachmentPoints", P.FPDFAnnot_AppendAttachmentPoints(an, &q)); }
            SetRect(c, an, union);
            Common(c, an, name + " over '" + phrase + "'", "s4-" + name.ToLower() + "-0001");
            c.Exp["color"] = new[] { r, g, b, a };
            c.Exp["contents"] = name + " over '" + phrase + "'";
            c.Exp["quadCount"] = quads.Count;
            c.Exp["textUnder"] = phrase;
            P.FPDFPage_CloseAnnot(an);
            return true;
        };

    // ------------------------------------------------------------------ Text note, Popup, reply
    static bool TextNote(Ctx c)
    {
        var an = Create(c, P.ST_TEXT, "Text");
        if (an == IntPtr.Zero) return false;
        var rect = R(480, 560, 500, 580);
        c.L("SetColor(Color)", P.FPDFAnnot_SetColor(an, 0, 255, 235, 59, 255));
        SetRect(c, an, rect);
        Common(c, an, "Parent comment: sticky note with popup", "s4-text-0001");
        c.Exp["contents"] = "Parent comment: sticky note with popup";
        c.Exp["color"] = new uint[] { 255, 235, 59, 255 };
        // popup: PDFium can create the Popup annotation but exposes no API to link it (/Popup in parent, /Parent in popup)
        var pop = P.FPDFPage_CreateAnnot(c.Page, P.ST_POPUP);
        c.L("FPDFPage_CreateAnnot(Popup)", pop == IntPtr.Zero ? "NULL" : "ok");
        if (pop != IntPtr.Zero)
        {
            var pr = R(510, 440, 610, 560);
            c.L("Popup SetRect", P.FPDFAnnot_SetRect(pop, &pr));
            c.L("Popup SetStringValue(Contents)", P.FPDFAnnot_SetStringValue(pop, "Contents", "popup text"));
            P.FPDFPage_CloseAnnot(pop);
        }
        // reply: a second Text annotation. /IRT needs an indirect reference; the only writer is SetStringValue (strings)
        var rep = P.FPDFPage_CreateAnnot(c.Page, P.ST_TEXT);
        if (rep != IntPtr.Zero)
        {
            var rr = R(480, 560, 500, 580);
            c.L("Reply SetRect", P.FPDFAnnot_SetRect(rep, &rr));
            c.L("Reply SetStringValue(Contents)", P.FPDFAnnot_SetStringValue(rep, "Contents", "Reply to the parent comment"));
            c.L("Reply SetStringValue(IRT) (string, wrong type)", P.FPDFAnnot_SetStringValue(rep, "IRT", "s4-text-0001"));
            P.FPDFPage_CloseAnnot(rep);
        }
        c.Exp["expectPopupLink"] = true;
        c.Exp["expectIrt"] = true;
        P.FPDFPage_CloseAnnot(an);
        return true;
    }

    // ------------------------------------------------------------------ FreeText
    static Func<Ctx, bool> FreeText(bool withAp) => c =>
    {
        var an = Create(c, P.ST_FREETEXT, "FreeText");
        if (an == IntPtr.Zero) return false;
        var rect = R(300, 300, 520, 350);
        SetRect(c, an, rect);
        SetStr(c, an, "DA", "0 g /Helv 14 Tf");
        Common(c, an, "FreeText box", "s4-freetext-0001");
        c.Exp["contents"] = "FreeText box";
        if (withAp)
        {
            // FPDFAnnot_SetAP takes the content stream text. No API to attach resources (fonts).
            string ap = "1 1 0.8 rg 0 G 1 w 300.5 300.5 219 49 re B BT /Helv 14 Tf 0 g 306 330 Td (FreeText via SetAP) Tj ET";
            c.L("SetAP(normal, stream text)", P.FPDFAnnot_SetAP(an, 0, ap));
            c.Exp["apViaSetAP"] = true;
        }
        P.FPDFPage_CloseAnnot(an);
        return true;
    };

    // ------------------------------------------------------------------ Ink
    static FS_POINTF[] Wave(double x0, double y0, double w, double amp, int n)
    {
        var pts = new FS_POINTF[n];
        for (int i = 0; i < n; i++) { double t = i / (double)(n - 1); pts[i] = new FS_POINTF { x = (float)(x0 + w * t), y = (float)(y0 + amp * Math.Sin(t * Math.PI * 2)) }; }
        return pts;
    }

    static bool InkApi(Ctx c)
    {
        var an = Create(c, P.ST_INK, "Ink");
        if (an == IntPtr.Zero) return false;
        var rect = R(90, 230, 330, 290);
        SetRect(c, an, rect);
        c.L("SetColor(Color red)", P.FPDFAnnot_SetColor(an, 0, 220, 0, 0, 255));
        c.L("SetBorder(width 3)", P.FPDFAnnot_SetBorder(an, 0, 0, 3));
        var strokes = new[] { Wave(100, 260, 220, 20, 30), Wave(100, 245, 220, 10, 20), Wave(100, 275, 220, 8, 12) };
        foreach (var s in strokes) fixed (FS_POINTF* p = s) c.L($"AddInkStroke({s.Length} pts)", P.FPDFAnnot_AddInkStroke(an, p, (nuint)s.Length));
        Common(c, an, "Ink via AddInkStroke (single width)", "s4-ink-0001");
        c.Exp["contents"] = "Ink via AddInkStroke (single width)";
        c.Exp["color"] = new uint[] { 220, 0, 0, 255 };
        c.Exp["inkStrokes"] = strokes.Length;
        P.FPDFPage_CloseAnnot(an);
        return true;
    }

    static bool InkPressure(Ctx c)
    {
        var an = Create(c, P.ST_INK, "Ink");
        if (an == IntPtr.Zero) return false;
        var rect = R(90, 150, 330, 215);
        SetRect(c, an, rect);
        c.L("SetColor(Color blue)", P.FPDFAnnot_SetColor(an, 0, 0, 0, 200, 255));
        var stroke = Wave(100, 185, 220, 22, 40);
        fixed (FS_POINTF* p = stroke) c.L("AddInkStroke(40 pts)", P.FPDFAnnot_AddInkStroke(an, p, 40));
        // Pressure-varying width has no representation in /InkList + /BS (one width per annotation). The only way through the
        // PDFium API is to build the appearance ourselves from path objects (one per segment, width ramps 0.5 -> 7 -> 0.5).
        int made = 0;
        for (int i = 0; i < stroke.Length - 1; i++)
        {
            double t = i / (double)(stroke.Length - 2);
            float w = (float)(0.5 + 6.5 * Math.Sin(t * Math.PI));
            var path = P.FPDFPageObj_CreateNewPath(stroke[i].x, stroke[i].y);
            P.FPDFPath_LineTo(path, stroke[i + 1].x, stroke[i + 1].y);
            P.FPDFPageObj_SetStrokeColor(path, 0, 0, 200, 255);
            P.FPDFPageObj_SetStrokeWidth(path, w);
            P.FPDFPageObj_SetLineCap(path, 1);
            P.FPDFPath_SetDrawMode(path, 0, 1);
            if (P.FPDFAnnot_AppendObject(an, path) != 0) made++;
        }
        c.L("AppendObject(path) segments appended", made);
        Common(c, an, "Ink pressure-varying width", "s4-ink-0002");
        c.Exp["contents"] = "Ink pressure-varying width";
        c.Exp["color"] = new uint[] { 0, 0, 200, 255 };
        c.Exp["inkStrokes"] = 1;
        c.Exp["appendedObjects"] = made;
        P.FPDFPage_CloseAnnot(an);
        return true;
    }

    // ------------------------------------------------------------------ Square / Circle
    static Func<Ctx, bool> Shape(int subtype, string name, FS_RECTF rect, uint[] line, uint[] fill) => c =>
    {
        var an = Create(c, subtype, name);
        if (an == IntPtr.Zero) return false;
        c.L("SetColor(Color)", P.FPDFAnnot_SetColor(an, 0, line[0], line[1], line[2], line[3]));
        c.L("SetColor(InteriorColor)", P.FPDFAnnot_SetColor(an, 1, fill[0], fill[1], fill[2], fill[3]));
        c.L("SetBorder(width 2)", P.FPDFAnnot_SetBorder(an, 0, 0, 2));
        SetRect(c, an, rect);
        Common(c, an, name + " annotation", "s4-" + name.ToLower() + "-0001");
        c.Exp["contents"] = name + " annotation";
        c.Exp["color"] = line; c.Exp["interior"] = fill;
        P.FPDFPage_CloseAnnot(an);
        return true;
    };

    // ------------------------------------------------------------------ Line / Polygon / PolyLine: not creatable
    static Func<Ctx, bool> NotCreatable(int subtype, string name) => c =>
    {
        var an = Create(c, subtype, name);
        if (an != IntPtr.Zero) { P.FPDFPage_CloseAnnot(an); return true; }
        return false;
    };

    // ------------------------------------------------------------------ Stamps
    static Func<Ctx, bool> StampName(bool withObjects) => c =>
    {
        var an = Create(c, P.ST_STAMP, "Stamp");
        if (an == IntPtr.Zero) return false;
        var rect = R(100, 300, 260, 350);
        SetRect(c, an, rect);
        // /Name must be a name object (/Approved). The only setter writes a string.
        SetStr(c, an, "Name", "Approved");
        c.L("GetValueType(Name) (4 = name, 3 = string)", P.FPDFAnnot_GetValueType(an, "Name"));
        Common(c, an, "Standard Approved stamp", "s4-stamp-0001");
        c.Exp["contents"] = "Standard Approved stamp";
        c.Exp["stampName"] = "Approved";
        if (withObjects)
        {
            var box = P.FPDFPageObj_CreateNewRect(102, 302, 156, 46);
            P.FPDFPageObj_SetStrokeColor(box, 200, 0, 0, 255);
            P.FPDFPageObj_SetStrokeWidth(box, 3);
            P.FPDFPageObj_SetFillColor(box, 255, 255, 255, 255);
            P.FPDFPath_SetDrawMode(box, 1, 1);
            c.L("AppendObject(rect)", P.FPDFAnnot_AppendObject(an, box));
            var txt = P.FPDFPageObj_NewTextObj(c.D.Doc, "Helvetica-Bold", 22);
            c.L("NewTextObj(Helvetica-Bold)", txt == IntPtr.Zero ? "NULL" : "ok");
            if (txt != IntPtr.Zero)
            {
                c.L("SetText", P.FPDFText_SetText(txt, "APPROVED"));
                P.FPDFPageObj_SetFillColor(txt, 200, 0, 0, 255);
                P.FPDFPageObj_Transform(txt, 1, 0, 0, 1, 118, 316);
                c.L("AppendObject(text)", P.FPDFAnnot_AppendObject(an, txt));
            }
        }
        P.FPDFPage_CloseAnnot(an);
        return true;
    };

    static bool StampImage(Ctx c)
    {
        var an = Create(c, P.ST_STAMP, "Stamp");
        if (an == IntPtr.Zero) return false;
        var rect = R(300, 380, 400, 480);
        SetRect(c, an, rect);
        Common(c, an, "Stamp with image", "s4-stamp-0002");
        c.Exp["contents"] = "Stamp with image";
        // 64x64 BGRA bitmap: diagonal gradient with transparent corners
        var bmp = P.FPDFBitmap_Create(64, 64, 1);
        P.FPDFBitmap_FillRect(bmp, 0, 0, 64, 64, 0x00000000);
        var buf = (byte*)P.FPDFBitmap_GetBuffer(bmp); int stride = P.FPDFBitmap_GetStride(bmp);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            byte* px = buf + y * stride + x * 4;
            double dx = x - 31.5, dy = y - 31.5;
            bool inside = dx * dx + dy * dy < 30 * 30;
            px[0] = (byte)(255 - x * 4); px[1] = (byte)(y * 4); px[2] = (byte)(x * 4); px[3] = (byte)(inside ? 255 : 0);
        }
        var img = P.FPDFPageObj_NewImageObj(c.D.Doc);
        c.L("FPDFImageObj_SetBitmap", P.FPDFImageObj_SetBitmap(null, 0, img, bmp));
        c.L("FPDFImageObj_SetMatrix (100x100 at 300,380)", P.FPDFImageObj_SetMatrix(img, 100, 0, 0, 100, 300, 380));
        c.L("AppendObject(image)", P.FPDFAnnot_AppendObject(an, img));
        P.FPDFBitmap_Destroy(bmp);
        P.FPDFPage_CloseAnnot(an);
        return true;
    }

    // ------------------------------------------------------------------ Forms
    static IntPtr Widget(Ctx c, string name, int nth = 0)
    {
        int n = P.FPDFPage_GetAnnotCount(c.Page);
        for (int i = 0; i < n; i++)
        {
            var a = P.FPDFPage_GetAnnot(c.Page, i);
            if (P.FPDFAnnot_GetSubtype(a) == P.ST_WIDGET && PdfDoc.FormFieldName(c.Form, a) == name && nth-- == 0) return a;
            P.FPDFPage_CloseAnnot(a);
        }
        return IntPtr.Zero;
    }

    static void Click(Ctx c, IntPtr annot)
    {
        var r = PdfDoc.GetRect(annot);
        double x = (r.left + r.right) / 2, y = (r.top + r.bottom) / 2;
        c.L($"click at ({x:0.#},{y:0.#}) down", P.FORM_OnLButtonDown(c.Form, c.Page, 0, x, y));
        c.L("click up", P.FORM_OnLButtonUp(c.Form, c.Page, 0, x, y));
    }

    static string Report(Ctx c, IntPtr a)
    {
        var v = PdfDoc.FormFieldValue(c.Form, a);
        c.L("PDFium value after op (FPDFAnnot_GetFormFieldValue)", "'" + v.Replace("\r", "\\r").Replace("\n", "\\n") + "'");
        return v;
    }

    static Func<Ctx, bool> FormText(string name, string text, string type) => c =>
    {
        var a = Widget(c, name);
        if (a == IntPtr.Zero) { c.L("widget not found", name); return false; }
        c.Exp["fieldName"] = name; c.Exp["fieldType"] = type; c.Exp["value"] = text;
        c.L("FORM_SetFocusedAnnot", P.FORM_SetFocusedAnnot(c.Form, a));
        c.L("FORM_SelectAllText", P.FORM_SelectAllText(c.Form, c.Page));
        P.FORM_ReplaceSelection(c.Form, c.Page, text);
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        Report(c, a);
        P.FPDFPage_CloseAnnot(a);
        return true;
    };

    static bool FormCheckbox(Ctx c) => FormCheckboxNamed(c, "chk");

    static bool FormCheckboxNamed(Ctx c, string name)
    {
        var a = Widget(c, name);
        if (a == IntPtr.Zero) return false;
        c.Exp["fieldName"] = name; c.Exp["fieldType"] = "checkbox"; c.Exp["value"] = "Yes"; c.Exp["checked"] = true;
        Click(c, a);
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        c.L("FPDFAnnot_IsChecked", P.FPDFAnnot_IsChecked(c.Form, a));
        Report(c, a);
        P.FPDFPage_CloseAnnot(a);
        return true;
    }

    static bool FormRadio(Ctx c)
    {
        var a = Widget(c, "radioGroup", 1);
        if (a == IntPtr.Zero) return false;
        c.Exp["fieldName"] = "radioGroup"; c.Exp["fieldType"] = "radio"; c.Exp["value"] = "ChoiceB"; c.Exp["checkedIndex"] = 1;
        Click(c, a);
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        c.L("FPDFAnnot_IsChecked(kid 1)", P.FPDFAnnot_IsChecked(c.Form, a));
        Report(c, a);
        P.FPDFPage_CloseAnnot(a);
        return true;
    }

    static bool FormCombo(Ctx c, string name, bool edit)
    {
        var a = Widget(c, name);
        if (a == IntPtr.Zero) return false;
        c.Exp["fieldName"] = name; c.Exp["fieldType"] = edit ? "combo_editable" : "combo";
        c.L("FORM_SetFocusedAnnot", P.FORM_SetFocusedAnnot(c.Form, a));
        if (!edit)
        {
            c.L("FORM_SetIndexSelected(1,true)", P.FORM_SetIndexSelected(c.Form, c.Page, 1, 1));
            c.Exp["value"] = "Beta";
        }
        else
        {
            c.L("FORM_SelectAllText", P.FORM_SelectAllText(c.Form, c.Page));
            P.FORM_ReplaceSelection(c.Form, c.Page, "custom entry");
            c.Exp["value"] = "custom entry";
        }
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        Report(c, a);
        P.FPDFPage_CloseAnnot(a);
        return true;
    }

    static bool FormList(Ctx c)
    {
        var a = Widget(c, "list_multi");
        if (a == IntPtr.Zero) return false;
        c.Exp["fieldName"] = "list_multi"; c.Exp["fieldType"] = "list_multi";
        c.Exp["values"] = new[] { "Beta", "Delta" };
        c.L("FORM_SetFocusedAnnot", P.FORM_SetFocusedAnnot(c.Form, a));
        c.L("FORM_SetIndexSelected(1,true)", P.FORM_SetIndexSelected(c.Form, c.Page, 1, 1));
        c.L("FORM_SetIndexSelected(3,true)", P.FORM_SetIndexSelected(c.Form, c.Page, 3, 1));
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        for (int i = 0; i < 5; i++) c.L($"IsOptionSelected({i})", P.FPDFAnnot_IsOptionSelected(c.Form, a, i));
        P.FPDFPage_CloseAnnot(a);
        return true;
    }

    static bool FormPush(Ctx c)
    {
        var a = Widget(c, "push");
        if (a == IntPtr.Zero) return false;
        c.Exp["fieldName"] = "push"; c.Exp["fieldType"] = "pushbutton"; c.Exp["value"] = "";
        Click(c, a);
        c.L("FORM_ForceToKillFocus", P.FORM_ForceToKillFocus(c.Form));
        P.FPDFPage_CloseAnnot(a);
        return true;
    }

    // ------------------------------------------------------------------ signed-fixture cases
    static bool SignedNoop(Ctx c) { c.Exp["noop"] = true; return true; }

    public static List<Case> All(PdfFile baseF)
    {
        var l = new List<Case>();
        void A(string id, string title, string input, Func<Ctx, bool> f, string group = "annot", string note = "")
            => l.Add(new Case { Id = id, Group = group, Title = title, Input = input, Apply = f, Note = note });
        void Q(string id, string title, Func<PdfFile, RawAnnots.Result> fn, string note = "")
            => l.Add(new Case { Id = id, Group = "annot-quire", Title = title, Input = "base", RawFn = fn, Note = note });

        // annotations via PDFium
        A("A01", "Highlight", "base", Markup(P.ST_HIGHLIGHT, "Highlight", Fx.PhraseHL, 255, 235, 0, 255));
        A("A02", "Underline", "base", Markup(P.ST_UNDERLINE, "Underline", Fx.PhraseUL, 0, 160, 0, 255));
        A("A03", "StrikeOut", "base", Markup(P.ST_STRIKEOUT, "StrikeOut", Fx.PhraseSO, 220, 0, 0, 255));
        A("A04", "Squiggly", "base", Markup(P.ST_SQUIGGLY, "Squiggly", Fx.PhraseSQ, 200, 0, 200, 255));
        A("A05", "Text note + Popup + reply (PDFium API)", "base", TextNote);
        A("A06a", "FreeText (no AP)", "base", FreeText(false));
        A("A06b", "FreeText (FPDFAnnot_SetAP stream)", "base", FreeText(true));
        A("A07a", "Ink via AddInkStroke (single width)", "base", InkApi);
        A("A07b", "Ink pressure-varying width (AppendObject paths)", "base", InkPressure);
        A("A08", "Square", "base", Shape(P.ST_SQUARE, "Square", R(400, 230, 480, 290), new uint[] { 0, 0, 255, 255 }, new uint[] { 255, 255, 0, 255 }));
        A("A09", "Circle", "base", Shape(P.ST_CIRCLE, "Circle", R(495, 230, 575, 290), new uint[] { 0, 120, 0, 255 }, new uint[] { 150, 255, 150, 255 }));
        A("A10", "Line with arrow endings (PDFium API)", "base", NotCreatable(P.ST_LINE, "Line"));
        A("A11", "Polygon (PDFium API)", "base", NotCreatable(P.ST_POLYGON, "Polygon"));
        A("A12", "PolyLine (PDFium API)", "base", NotCreatable(P.ST_POLYLINE, "PolyLine"));
        A("A13a", "Stamp standard name, no appearance", "base", StampName(false));
        A("A13b", "Stamp standard name + drawn appearance objects", "base", StampName(true));
        A("A14", "Stamp with image", "base", StampImage);

        // annotations authored by Quire (raw incremental update)
        Q("Q01", "Line with arrow endings (Quire-authored)", RawAnnots.Line);
        Q("Q02", "Polygon (Quire-authored)", RawAnnots.Polygon);
        Q("Q03", "PolyLine (Quire-authored)", RawAnnots.PolyLine);
        Q("Q04", "Text + Popup + /IRT reply + review state (Quire-authored)", RawAnnots.TextPopupReply);
        Q("Q05", "Stamp /Name /Approved (Quire-authored)", RawAnnots.StampName);
        Q("Q06", "FreeText with AP (Quire-authored)", RawAnnots.FreeText);

        // forms
        A("B01", "Text field, single line", "forms", FormText("txt_single", "Hello Quire", "text"), "form");
        A("B02", "Text field, multiline", "forms", FormText("txt_multi", "Line one\rLine two\rLine three", "multiline"), "form");
        A("B03", "Text field, comb (MaxLen 8)", "forms", FormText("txt_comb", "12345678", "comb"), "form");
        A("B04", "Checkbox", "forms", FormCheckbox, "form");
        A("B05", "Radio group (select second button)", "forms", FormRadio, "form");
        A("B06", "Combo box, not editable", "forms", c => FormCombo(c, "combo_ro", false), "form");
        A("B07", "Combo box, editable", "forms", c => FormCombo(c, "combo_edit", true), "form");
        A("B08", "List box, multi-select", "forms", FormList, "form");
        A("B09", "Push button (click, no value)", "forms", FormPush, "form");

        // forms that arrive WITHOUT any /AP (typical of generated forms): PDFium test corpus multiple_form_types.pdf
        A("B10", "AP-less form: text field", "corpus-forms", FormText("Text Box", "Hello Quire", "text"), "form");
        A("B11", "AP-less form: editable combo", "corpus-forms", FormText("Combo_Editable", "custom entry", "combo_editable"), "form");
        A("B12", "AP-less form: checkbox (no /AP states at all)", "corpus-forms", c => FormCheckboxNamed(c, "Checkbox"), "form");

        // signature preservation
        A("G01", "Signed base + Highlight", "signed-base", Markup(P.ST_HIGHLIGHT, "Highlight", Fx.PhraseHL, 255, 235, 0, 255), "sig");
        A("G02", "Signed forms + text field fill", "signed-forms", FormText("txt_single", "Hello Quire", "text"), "sig");
        A("G03", "Signed base, no edit (open and save)", "signed-base", SignedNoop, "sig");
        A("G04", "Signed forms + checkbox", "signed-forms", FormCheckbox, "sig");
        return l;
    }
}
