using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using S7.Core.Engine;
using S7.Core.Geometry;
using S7.Core.Text;
using SWRect = System.Windows.Rect;
using SWPoint = System.Windows.Point;

namespace A11yTest;

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>
/// S7.4. UIA CLIENT test in a separate process from the app: launches PageA11y, finds the page element, takes the
/// TextPattern and exercises it through the real UIA core (cross-process COM), the same path Narrator/NVDA use.
/// </summary>
public static class UiaTests
{
    private const string Suite = "uia";

    private static string AppExe => Path.Combine(Paths.Root, "out", "bin", "PageA11y", "x64", "Release", "net10.0-windows10.0.26100.0", "win-x64", "PageA11y.exe");

    public static double Zoom = 1.0;

    public static void Run(string? onlyFixture, int? onlyRot)
    {
        foreach (var ex in Expect.All)
        {
            if (onlyFixture != null && !ex.File.StartsWith(onlyFixture)) continue;
            for (int rot = 0; rot < 4; rot++)
            {
                if (onlyRot != null && onlyRot != rot) continue;
                try { RunOne(ex, rot); }
                catch (Exception e) { Results.Add(Suite, ex.File, $"view {rot * 90}", "test harness", false, e.GetType().Name + ": " + e.Message); }
            }
        }
    }

    private static void RunOne(Expect ex, int rot)
    {
        string rotName = $"view {rot * 90}";
        string logPath = Path.Combine(Paths.Out, $"uia-app-{ex.File[..1]}-rot{rot * 90}.log");
        Directory.CreateDirectory(Paths.Out);
        File.Delete(logPath);
        var psi = new ProcessStartInfo(AppExe, $"--fixture \"{Path.Combine(Paths.Fixtures, ex.File)}\" --rot {rot} --zoom {Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)} --log \"{logPath}\"") { UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        try
        {
            var sw = Stopwatch.StartNew();
            var page = WaitForPage(proc);
            var tp = (TextPattern)page.GetCurrentPattern(TextPattern.Pattern);
            WaitForText(tp);
            Results.Add(Suite, ex.File, rotName, "launch -> page element with Text pattern", true, $"{sw.ElapsedMilliseconds} ms");

            ElementChecks(ex, rotName, page, tp);
            string full = tp.DocumentRange.GetText(-1);
            Results.Equal(Suite, ex.File, rotName, "DocumentRange.GetText == expected reading-order text", ex.FullText, full);

            var rect = page.Current.BoundingRectangle;
            double scale = Native.GetDpiForWindow(proc.MainWindowHandle) / 96.0 * Zoom;   // DIP per point = zoom, device px per DIP = dpi/96

            if (rot == 0)
            {
                var t0 = Stopwatch.StartNew();
                var words = WalkUia(tp, TextUnit.Word);
                Results.SequenceEqual(Suite, ex.File, rotName, "walk by WORD (ExpandToEnclosingUnit + Move)", ex.Words, words.Select(w => w.Trim()).ToList());
                var lines = WalkUia(tp, TextUnit.Line);
                Results.SequenceEqual(Suite, ex.File, rotName, "walk by LINE", ex.Lines, lines.Select(w => w.Trim()).ToList());
                var paras = WalkUia(tp, TextUnit.Paragraph);
                Results.SequenceEqual(Suite, ex.File, rotName, "walk by PARAGRAPH", ex.Paragraphs, paras.Select(w => w.Trim()).ToList());
                var chars = WalkUia(tp, TextUnit.Character);
                Results.Add(Suite, ex.File, rotName, "walk by CHARACTER", string.Concat(chars) == ex.FullText, $"{chars.Count} chars, {t0.ElapsedMilliseconds} ms for all four walks ({(words.Count + lines.Count + paras.Count + chars.Count)} Move calls)");
                var back = WalkUia(tp, TextUnit.Word, backwards: true);
                Results.SequenceEqual(Suite, ex.File, rotName, "walk by WORD backwards (Move -1)", ex.Words.Reverse().ToList(), back.Select(w => w.Trim()).ToList());
            }
            else
            {
                var lines = WalkUia(tp, TextUnit.Line);
                Results.SequenceEqual(Suite, ex.File, rotName, "walk by LINE", ex.Lines, lines.Select(w => w.Trim()).ToList());
            }

            RectChecks(ex, rot, rotName, tp, rect, scale);
            SelectionAndEvents(ex, rotName, page, tp);
            PointChecks(ex, rotName, tp);
            if (rot == 0 && ex.File.StartsWith("c")) LiveRotate(ex, proc, page, tp, scale);
            if (rot == 0 && ex.File.StartsWith("a")) TextChangedViaCombo(ex, proc, page, tp);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static AutomationElement WaitForPage(Process proc)
    {
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PageSurface"));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) throw new InvalidOperationException("app exited with code " + proc.ExitCode);
            var el = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, cond);
            if (el != null) return el;
            Thread.Sleep(150);
        }
        throw new TimeoutException("PageSurface element not found");
    }

    private static void WaitForText(TextPattern tp)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try { if (tp.DocumentRange.GetText(5).Length > 0) return; } catch { }
            Thread.Sleep(150);
        }
        throw new TimeoutException("document text never became available");
    }

    private static void ElementChecks(Expect ex, string rot, AutomationElement page, TextPattern tp)
    {
        if (rot != "view 0") return;
        var c = page.Current;
        Results.Add(Suite, ex.File, rot, "element: ControlType Document, ClassName, Name", c.ControlType == ControlType.Document && c.ClassName == "QuirePageSurface" && c.Name == "PDF page 1",
            $"type={c.ControlType.ProgrammaticName} class={c.ClassName} name='{c.Name}'");
        bool avail = (bool)page.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty);
        Results.Add(Suite, ex.File, rot, "element: IsTextPatternAvailable", avail, "");
        Results.Add(Suite, ex.File, rot, "element: IsKeyboardFocusable + content/control element", c.IsKeyboardFocusable && c.IsContentElement && c.IsControlElement, $"focusable={c.IsKeyboardFocusable} content={c.IsContentElement} control={c.IsControlElement}");
        Results.Add(Suite, ex.File, rot, "TextPattern.SupportedTextSelection == Single", tp.SupportedTextSelection == SupportedTextSelection.Single, tp.SupportedTextSelection.ToString());
        try { page.SetFocus(); Thread.Sleep(200); Results.Add(Suite, ex.File, rot, "SetFocus() gives the page element keyboard focus", page.Current.HasKeyboardFocus, $"HasKeyboardFocus={page.Current.HasKeyboardFocus}"); }
        catch (Exception e) { Results.Add(Suite, ex.File, rot, "SetFocus()", false, e.Message); }
        var vis = tp.GetVisibleRanges();
        Results.Add(Suite, ex.File, rot, "GetVisibleRanges returns the document", vis.Length == 1 && vis[0].GetText(-1) == ex.FullText);
        var sel = tp.GetSelection();
        Results.Add(Suite, ex.File, rot, "GetSelection before any selection is one degenerate range", sel.Length == 1 && sel[0].GetText(-1) == "", $"{sel.Length} range(s), text '{(sel.Length > 0 ? sel[0].GetText(-1) : "")}'");
        try
        {
            var v = tp.DocumentRange.GetAttributeValue(TextPattern.FontSizeAttribute);
            Results.Add(Suite, ex.File, rot, "GetAttributeValue(FontSize) does not fault (not supported value expected)", true, $"returned {(v == AutomationElement.NotSupported ? "NotSupported" : v?.ToString() ?? "null")}");
        }
        catch (Exception e) { Results.Add(Suite, ex.File, rot, "GetAttributeValue(FontSize) does not fault", false, e.GetType().Name + " " + e.Message); }
        try
        {
            string needle = ex.Words[1];
            var f = tp.DocumentRange.FindText(needle, false, false);
            Results.Add(Suite, ex.File, rot, "FindText finds a word", f != null && f.GetText(-1) == needle, $"'{needle}' -> '{f?.GetText(-1)}'");
        }
        catch (Exception e) { Results.Add(Suite, ex.File, rot, "FindText", false, e.GetType().Name + " " + e.Message); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static List<string> WalkUia(TextPattern tp, TextUnit unit, bool backwards = false)
    {
        var doc = tp.DocumentRange;
        var r = doc.Clone();
        if (backwards) r.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.End);
        else r.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.Start);   // collapse at start
        r.ExpandToEnclosingUnit(unit);
        var l = new List<string>();
        int step = backwards ? -1 : 1;
        while (true)
        {
            l.Add(r.GetText(-1));
            if (r.Move(unit, step) == 0) break;
            if (l.Count > 50000) throw new InvalidOperationException("runaway walk");
        }
        return l;
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static void RectChecks(Expect ex, int rot, string rotName, TextPattern tp, SWRect elem, double scale)
    {
        using var eng = PdfiumEngine.Open(Path.Combine(Paths.Fixtures, ex.File));
        var data = eng.ExtractPage(0);
        var model = TextModelBuilder.Build(data);
        var geo = new PageGeometry(data.Box, rot);

        // 1. Whole-document rectangles: one per visual line, all inside the page element.
        var docRects = tp.DocumentRange.GetBoundingRectangles();
        int lineCount = model.Boundaries(TextUnitKind.Line).Length - 1;
        Results.Add(Suite, ex.File, rotName, "DocumentRange rectangles: one per line", docRects.Length == lineCount, $"{docRects.Length} rects, {lineCount} lines");
        var tol = Inflate(elem, 1.5);
        bool allInside = docRects.All(r => tol.Contains(r));
        Results.Add(Suite, ex.File, rotName, "DocumentRange rectangles fall inside the page element", allInside, $"element=({elem.X:F0},{elem.Y:F0},{elem.Width:F0},{elem.Height:F0}) scale={scale:F2}");

        // 2. Per-character alignment against an INDEPENDENT calculation: PDFium loose char box -> PageGeometry (validated
        //    against FPDF_PageToDevice) -> zoom 1 -> DPI scale -> element origin.
        var r0 = tp.DocumentRange.Clone();
        r0.MoveEndpointByRange(TextPatternRangeEndpoint.End, tp.DocumentRange, TextPatternRangeEndpoint.Start);
        r0.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
        int checkedN = 0, synthetic = 0, adjusted = 0, bad = 0, emptyBad = 0, insideBad = 0;
        double maxErr = 0; string firstBad = "";
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < model.Length; i++)
        {
            var rects = r0.GetBoundingRectangles();
            var a = model.Atoms[i];
            if (a.Source < 0)
            {
                synthetic++;
                if (rects.Length != 0) { emptyBad++; if (firstBad == "") firstBad = $"atom {i} synthetic but {rects.Length} rects"; }
            }
            else
            {
                var c = data.Chars[a.Source];
                var exp = geo.UserRectToPage(c.Loose);
                var expScreen = new SWRect(elem.X + exp.X * scale, elem.Y + exp.Y * scale, exp.Width * scale, exp.Height * scale);
                if (rects.Length != 1) { bad++; if (firstBad == "") firstBad = $"atom {i} '{a.Text}' got {rects.Length} rects"; }
                else
                {
                    var got = rects[0];
                    var be = new PageGeometry(data.Box, 0).UserRectToPage(c.Loose);
                    var ar = a.Rect!.Value;
                    bool split = Math.Abs(ar.X - be.X) > 0.01 || Math.Abs(ar.Y - be.Y) > 0.01 || Math.Abs(ar.Width - be.Width) > 0.01 || Math.Abs(ar.Height - be.Height) > 0.01;
                    if (!split)
                    {
                        double err = new[] { Math.Abs(got.X - expScreen.X), Math.Abs(got.Y - expScreen.Y), Math.Abs(got.Width - expScreen.Width), Math.Abs(got.Height - expScreen.Height) }.Max();
                        maxErr = Math.Max(maxErr, err); checkedN++;
                        if (err > 1.5) { bad++; if (firstBad == "") firstBad = $"atom {i} '{a.Text}' err {err:F2}px got ({got.X:F1},{got.Y:F1},{got.Width:F1},{got.Height:F1}) exp ({expScreen.X:F1},{expScreen.Y:F1},{expScreen.Width:F1},{expScreen.Height:F1})"; }
                    }
                    else
                    {
                        adjusted++;   // ligature piece or hyphen-extended glyph: must still overlap the glyph's own box
                        if (!got.IntersectsWith(Inflate(expScreen, 1.5))) { bad++; if (firstBad == "") firstBad = $"adjusted atom {i} '{a.Text}' does not overlap its glyph"; }
                    }
                    if (!tol.Contains(got)) insideBad++;
                }
            }
            if (i < model.Length - 1 && r0.Move(TextUnit.Character, 1) != 1) { bad++; firstBad = $"Move(Character) stopped at {i}"; break; }
        }
        Results.Add(Suite, ex.File, rotName, "per-character rectangles match PDFium+PageGeometry+DPI (<= 1.5 px)", bad == 0 && emptyBad == 0,
            $"{checkedN} exact-compared (max err {maxErr:F2} px), {adjusted} ligature/hyphen-adjusted overlap-checked, {synthetic} separators returned no rect, {bad + emptyBad} bad; {sw.ElapsedMilliseconds} ms{(firstBad != "" ? "; first: " + firstBad : "")}");
        Results.Add(Suite, ex.File, rotName, "every per-character rectangle lies inside the page element", insideBad == 0, $"{insideBad} outside");
    }

    private static AutomationElement? FindById(Process proc, string id)
        => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)));

    /// <summary>Drive the app's own Rotate button through UIA (InvokePattern) and re-verify rectangles against the new orientation.</summary>
    private static void LiveRotate(Expect ex, Process proc, AutomationElement page, TextPattern tp, double scale)
    {
        var btn = FindById(proc, "RotateButton");
        if (btn == null) { Results.Add(Suite, ex.File, "view 0 -> button", "Rotate button found via UIA", false); return; }
        ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(700);
        var elem = page.Current.BoundingRectangle;
        RectChecks(ex, 1, "view 0 -> Rotate button (UIA Invoke) -> view 90", tp, elem, scale);
        Results.Add(Suite, ex.File, "view 90 (live)", "document text unchanged by rotation", tp.DocumentRange.GetText(-1) == ex.FullText);
    }

    /// <summary>Switch fixture through the app's ComboBox and expect TextChanged plus the new document text.</summary>
    private static void TextChangedViaCombo(Expect ex, Process proc, AutomationElement page, TextPattern tp)
    {
        var other = Expect.All[1];
        using var got = new ManualResetEventSlim(false);
        AutomationEventHandler h = (s, e) => got.Set();
        Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, page, TreeScope.Element, h);
        try
        {
            var combo = FindById(proc, "FixtureCombo");
            if (combo == null) { Results.Add(Suite, ex.File, "view 0", "fixture combo found via UIA", false); return; }
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Thread.Sleep(500);
            var item = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id),
                new PropertyCondition(AutomationElement.NameProperty, other.File)));
            if (item == null) { Results.Add(Suite, ex.File, "view 0", "combo item found via UIA", false, other.File); return; }
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            bool evt = got.Wait(4000);
            Thread.Sleep(400);
            string text = tp.DocumentRange.GetText(-1);
            Results.Add(Suite, ex.File, "view 0", "TextChanged event reaches a UIA client when the document changes", evt, evt ? "received" : "no event within 4 s");
            Results.Equal(Suite, ex.File, "view 0", "after switching fixture, the SAME TextPattern object reads the new text", other.FullText, text);
        }
        finally { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, page, h); }
    }

    private static SWRect Inflate(SWRect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    // ---------------------------------------------------------------------------------------------------------------
    private static void SelectionAndEvents(Expect ex, string rotName, AutomationElement page, TextPattern tp)
    {
        // Event handler registered from the client before the selection changes.
        using var got = new ManualResetEventSlim(false);
        int events = 0;
        AutomationEventHandler h = (s, e) => { Interlocked.Increment(ref events); got.Set(); };
        Automation.AddAutomationEventHandler(TextPattern.TextSelectionChangedEvent, page, TreeScope.Element, h);
        try
        {
            Thread.Sleep(300);
            // Select the 3rd word.
            var r = tp.DocumentRange.Clone();
            r.MoveEndpointByRange(TextPatternRangeEndpoint.End, tp.DocumentRange, TextPatternRangeEndpoint.Start);
            r.ExpandToEnclosingUnit(TextUnit.Word);
            r.Move(TextUnit.Word, 2);
            string want = r.GetText(-1);
            var sw = Stopwatch.StartNew();
            r.Select();
            bool evt = got.Wait(4000);
            long ms = sw.ElapsedMilliseconds;
            var sel = tp.GetSelection();
            string selText = sel.Length > 0 ? sel[0].GetText(-1) : "<none>";
            Results.Add(Suite, ex.File, rotName, "Select() then GetSelection returns the selected word", sel.Length == 1 && selText == want, $"selected '{want.Trim()}', GetSelection '{selText.Trim()}'");
            Results.Add(Suite, ex.File, rotName, "TextSelectionChanged event reaches a UIA client", evt, evt ? $"after {ms} ms, {events} event(s)" : "no event within 4 s");

            // Selecting a whole line, then a paragraph: selection text and rects.
            var line = tp.DocumentRange.Clone();
            line.MoveEndpointByRange(TextPatternRangeEndpoint.End, tp.DocumentRange, TextPatternRangeEndpoint.Start);
            line.ExpandToEnclosingUnit(TextUnit.Line);
            line.Select();
            var sel2 = tp.GetSelection();
            Results.Add(Suite, ex.File, rotName, "Select() a line, GetSelection text equals the line", sel2.Length == 1 && sel2[0].GetText(-1) == line.GetText(-1), $"'{(sel2.Length > 0 ? sel2[0].GetText(-1).Trim() : "")}'");
            var selRects = sel2.Length > 0 ? sel2[0].GetBoundingRectangles() : Array.Empty<SWRect>();
            Results.Add(Suite, ex.File, rotName, "selected range has a bounding rectangle", selRects.Length >= 1 && selRects.All(x => x.Width > 0 && x.Height > 0), $"{selRects.Length} rect(s)");
        }
        finally
        {
            Automation.RemoveAutomationEventHandler(TextPattern.TextSelectionChangedEvent, page, h);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    private static void PointChecks(Expect ex, string rotName, TextPattern tp)
    {
        // RangeFromPoint at the centre of several words must return that word.
        var doc = tp.DocumentRange;
        var r = doc.Clone();
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.Start);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        int total = ex.Words.Length, ok = 0, tried = 0; string firstBad = "";
        for (int i = 0; i < total; i++)
        {
            if (i % 3 == 0)
            {
                var rects = r.GetBoundingRectangles();
                if (rects.Length > 0)
                {
                    var first = rects[0];
                    // a point inside the first glyph of the word
                    var pt = new SWPoint(first.X + Math.Min(first.Width, 4) / 2, first.Y + first.Height / 2);
                    var hit = tp.RangeFromPoint(pt);
                    hit.ExpandToEnclosingUnit(TextUnit.Word);
                    string got = hit.GetText(-1).Trim(), want = r.GetText(-1).Trim();
                    tried++;
                    if (got == want) ok++; else if (firstBad == "") firstBad = $"word #{i} want '{want}' got '{got}' at ({pt.X:F1},{pt.Y:F1})";
                }
            }
            if (r.Move(TextUnit.Word, 1) == 0) break;
        }
        Results.Add(Suite, ex.File, rotName, "RangeFromPoint(rect of word) + ExpandToEnclosingUnit(Word) returns that word", tried > 0 && ok == tried, $"{ok}/{tried}{(firstBad != "" ? "; first: " + firstBad : "")}");
    }
}
