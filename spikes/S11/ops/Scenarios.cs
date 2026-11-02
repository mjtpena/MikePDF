// S11: the page operations under test, applied with PDFium, each saved as a full copy and (where the document is edited in place) incrementally.
using System.Runtime.InteropServices;
using S4;

namespace S11;

internal sealed record Case(string Name, string Mode, string Description, byte[] Output, byte[]? PrefixBase, string[] ExpectKeys, string[] Sources, Dictionary<string, int>? Rot = null, string Notes = "");

internal static unsafe class Scenarios
{
    public const uint Inc = 1, Full = 2;

    public static string K(string doc, int i) => $"PAGE-{doc}-{i:00}";

    public static string[] Keys(string doc, params int[] oneBased) => oneBased.Select(i => K(doc, i)).ToArray();

    public static byte[]? SaveRaw(IntPtr doc, uint flags, out string error)
    {
        var ms = new MemoryStream();
        WriteBlock wb = (self, data, size) =>
        {
            var tmp = new byte[size];
            Marshal.Copy(data, tmp, 0, (int)size);
            ms.Write(tmp, 0, tmp.Length);
            return 1;
        };
        var fw = Marshal.AllocHGlobal(16);
        new Span<byte>((void*)fw, 16).Clear();
        *(int*)fw = 1;
        *(IntPtr*)((byte*)fw + 8) = Marshal.GetFunctionPointerForDelegate(wb);
        try
        {
            var ok = P.FPDF_SaveAsCopy(doc, fw, flags);
            GC.KeepAlive(wb);
            error = ok != 0 ? "" : "FPDF_SaveAsCopy returned 0";
            return ok != 0 ? ms.ToArray() : null;
        }
        finally { Marshal.FreeHGlobal(fw); }
    }

    private delegate int WriteBlock(IntPtr self, IntPtr data, uint size);

    /// <summary>Applies <paramref name="op"/> to a fresh in-memory copy of <paramref name="src"/> and saves it in both modes.</summary>
    public static IEnumerable<Case> InPlace(string name, string desc, byte[] src, Action<PdfDoc> op, string[] expect, string[] sources, Dictionary<string, int>? rot = null, params byte[][] extraSources)
    {
        foreach (var (mode, flags) in new[] { ("inc", Inc), ("full", Full) })
        {
            using var d = PdfDoc.Open((byte[])src.Clone(), true, out var err);
            if (d is null) { yield return new Case(name, mode, desc, [], null, expect, sources, rot, "open failed: " + err); continue; }
            op(d);
            var bytes = d.Save(flags, out var serr);
            yield return new Case(name, mode, desc, bytes ?? [], mode == "inc" ? src : null, expect, sources, rot, bytes is null ? "save failed: " + serr : "");
        }
    }

    public static IEnumerable<Case> NewDoc(string name, string desc, Func<IntPtr, bool> build, string[] expect, string[] sources, Dictionary<string, int>? rot = null)
    {
        foreach (var (mode, flags) in new[] { ("full", Full), ("inc", Inc) })
        {
            var dest = P2.FPDF_CreateNewDocument();
            var ok = build(dest);
            var bytes = SaveRaw(dest, flags, out var serr);
            P.FPDF_CloseDocument(dest);
            yield return new Case(name, mode, desc, bytes ?? [], null, expect, sources, rot, (ok ? "" : "import returned FALSE; ") + (bytes is null ? "save failed: " + serr : ""));
        }
    }

    public static bool Import(IntPtr dest, byte[] src, int[] idx, int at = -1)
    {
        using var s = PdfDoc.Open(src, false, out _);
        if (at < 0) at = P.FPDF_GetPageCount(dest);
        return P2.ImportByIndex(dest, s!.Doc, idx, at);
    }

    public static string[] SimulateMove(string[] keys, int[] moved, int dest)
    {
        var list = keys.ToList();
        var mv = moved.Select(i => keys[i]).ToList();
        foreach (var k in mv) list.Remove(k);
        list.InsertRange(dest, mv);
        return [.. list];
    }

    /// <summary>The same operations through the assembler prototype (MikePDF.PdfWriter strict parser, full rewrite into a new file).</summary>
    public static IEnumerable<Case> AssemblerCases(string fixtures)
    {
        var a = File.ReadAllBytes(Path.Combine(fixtures, "A.pdf"));
        var axs = File.ReadAllBytes(Path.Combine(fixtures, "A.xs.pdf"));
        var b = File.ReadAllBytes(Path.Combine(fixtures, "B.pdf"));
        var c = File.ReadAllBytes(Path.Combine(fixtures, "C.pdf"));
        var allA = Keys("A", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        AsmSource Src(string name, byte[] bytes, int[] pages) => new() { Name = name, Doc = MikePDF.PdfWriter.StrictDocument.Open(bytes).Value, Pages = pages };
        Case Make(string name, string desc, List<AsmSource> srcs, string[] expect, string[] sources, bool wrap = false, Dictionary<string, int>? rot = null)
        {
            var asm = new Assembler(srcs);
            byte[] bytes;
            try { bytes = asm.Build(wrap); }
            catch (Exception e) { return new Case(name, "asm", desc, [], null, expect, sources, rot, "assembler threw: " + e.Message); }
            return new Case(name, "asm", desc, bytes, null, expect, sources, rot, string.Join("; ", asm.Notes));
        }

        yield return Make("asm-delete", "Assembler: pages 1-10 of A without pages 3 and 10", [Src("A", a, [0, 1, 3, 4, 5, 6, 7, 8])], allA.Where(k => k is not ("PAGE-A-03" or "PAGE-A-10")).ToArray(), ["A"]);
        yield return Make("asm-reorder", "Assembler: A with pages 1-2 moved to position 7", [Src("A", a, [2, 3, 4, 5, 6, 7, 0, 1, 8, 9])], SimulateMove(allA, [0, 1], 6), ["A"]);
        yield return Make("asm-extract-4-6", "Assembler: pages 4-6 of A", [Src("A", a, [3, 4, 5])], Keys("A", 4, 5, 6), ["A"]);
        yield return Make("asm-extract-forms", "Assembler: pages 2, 5, 8 of A (forms)", [Src("A", a, [1, 4, 7])], Keys("A", 2, 5, 8), ["A"]);
        yield return Make("asm-extract-1-3", "Assembler: pages 1-3 of A", [Src("A", a, [0, 1, 2])], Keys("A", 1, 2, 3), ["A"]);
        yield return Make("asm-extract-3-ocg", "Assembler: page 3 of A (optional content)", [Src("A", a, [2])], Keys("A", 3), ["A"]);
        yield return Make("asm-extract-1-note", "Assembler: page 1 of A (note, popup, reply)", [Src("A", a, [0])], Keys("A", 1), ["A"]);
        yield return Make("asm-split-chapter-1", "Assembler: Chapter 1 = pages 1-3", [Src("A", a, [0, 1, 2])], Keys("A", 1, 2, 3), ["A"]);
        yield return Make("asm-split-chapter-2", "Assembler: Chapter 2 = pages 4-6", [Src("A", a, [3, 4, 5])], Keys("A", 4, 5, 6), ["A"]);
        yield return Make("asm-split-chapter-3", "Assembler: Chapter 3 = pages 7-10", [Src("A", a, [6, 7, 8, 9])], Keys("A", 7, 8, 9, 10), ["A"]);
        {
            var sa = Src("A", a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
            var sb = Src("B", b, [0, 1]);
            var seq = new List<(AsmSource, int)>();
            for (var i = 0; i < 3; i++) seq.Add((sa, i));
            seq.Add((sb, 0)); seq.Add((sb, 1));
            for (var i = 3; i < 10; i++) seq.Add((sa, i));
            var asm = new Assembler([sa, sb]) { Sequence = seq };
            var bytes = asm.Build(false);
            yield return new Case("asm-insert-from-file", "asm", "Assembler: A with pages 1-2 of B inserted after page 3", bytes, null, [.. allA.Take(3), .. Keys("B", 1, 2), .. allA.Skip(3)], ["A", "B"], null, string.Join("; ", asm.Notes));
        }
        yield return Make("asm-merge-A-B", "Assembler: merge A then B (outline wrapped per file)", [Src("A", a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]), Src("B", b, [0, 1, 2, 3, 4])], [.. allA, .. Keys("B", 1, 2, 3, 4, 5)], ["A", "B"], wrap: true);
        yield return Make("asm-merge-B-A", "Assembler: merge B then A", [Src("B", b, [0, 1, 2, 3, 4]), Src("A", a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9])], [.. Keys("B", 1, 2, 3, 4, 5), .. allA], ["B", "A"], wrap: true);
        yield return Make("asm-extract-C-all", "Assembler: all of C (rotation, MediaBox and CropBox offsets)", [Src("C", c, [0, 1, 2, 3, 4, 5])], Keys("C", 1, 2, 3, 4, 5, 6), ["C"]);
        yield return Make("asm-xs-extract-4-6", "Assembler: pages 4-6 of A.xs.pdf (xref streams and object streams in the source)", [Src("Axs", axs, [3, 4, 5])], Keys("A", 4, 5, 6), ["A"]);
    }

    public static IEnumerable<Case> All(string fixtures, string outDir)
    {
        var a = File.ReadAllBytes(Path.Combine(fixtures, "A.pdf"));
        var axs = File.ReadAllBytes(Path.Combine(fixtures, "A.xs.pdf"));
        var b = File.ReadAllBytes(Path.Combine(fixtures, "B.pdf"));
        var c = File.ReadAllBytes(Path.Combine(fixtures, "C.pdf"));
        var allA = Keys("A", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        var allB = Keys("B", 1, 2, 3, 4, 5);
        var allC = Keys("C", 1, 2, 3, 4, 5, 6);

        // ---- in place (the working document), saved incrementally and in full
        foreach (var x in InPlace("rotate", "Rotate pages 2 and 5 by 90 degrees (FPDFPage_SetRotation)", a, d =>
        {
            foreach (var i in new[] { 1, 4 }) { var p = d.LoadPage(i); P2.FPDFPage_SetRotation(p, (P2.FPDFPage_GetRotation(p) + 1) % 4); }
        }, allA, ["A"], new() { ["PAGE-A-02"] = 1, ["PAGE-A-05"] = 1 })) yield return x;

        foreach (var x in InPlace("delete", "Delete pages 3 and 10 (FPDFPage_Delete)", a, d =>
        {
            P2.FPDFPage_Delete(d.Doc, 9);
            P2.FPDFPage_Delete(d.Doc, 2);
        }, allA.Where(k => k is not ("PAGE-A-03" or "PAGE-A-10")).ToArray(), ["A"])) yield return x;

        foreach (var x in InPlace("delete-first", "Delete page 1 (labels and the first outline entries are affected)", a, d => P2.FPDFPage_Delete(d.Doc, 0),
            allA.Skip(1).ToArray(), ["A"])) yield return x;

        foreach (var x in InPlace("undo-delete-by-reimport", "Delete page 3, then 'undo' by re-importing it from the base file at position 3 (route A's undo, page-organiser 3.6)", a, d =>
        {
            P2.FPDFPage_Delete(d.Doc, 2);
            using var s = PdfDoc.Open(a, false, out _);
            P2.ImportByIndex(d.Doc, s!.Doc, [2], 2);
        }, allA, ["A"])) yield return x;

        foreach (var x in InPlace("delete-form-pages", "Delete pages 2, 5 and 8 (the three pages that carry form fields): do the fields leave /AcroForm?", a, d =>
        {
            P2.FPDFPage_Delete(d.Doc, 7);
            P2.FPDFPage_Delete(d.Doc, 4);
            P2.FPDFPage_Delete(d.Doc, 1);
        }, allA.Where(k => k is not ("PAGE-A-02" or "PAGE-A-05" or "PAGE-A-08")).ToArray(), ["A"])) yield return x;

        foreach (var x in InPlace("insert-blank", "Insert a blank Letter page after page 3 (FPDFPage_New)", a, d =>
        {
            var pg = P2.FPDFPage_New(d.Doc, 3, 612, 792);
            P.FPDF_ClosePage(pg);
        }, [.. allA.Take(3), "?", .. allA.Skip(3)], ["A"])) yield return x;

        var moveExpect = SimulateMove(allA, [0, 1], 6);
        foreach (var x in InPlace("move", "Move pages 1-2 to position 7 (FPDF_MovePages)", a, d =>
        {
            var idx = new[] { 0, 1 };
            P2.Move(d.Doc, idx, 6);
        }, moveExpect, ["A"])) yield return x;

        var reorder = SimulateMove(SimulateMove(SimulateMove(allA, [9], 0), [4, 5], 8), [0], 5);
        foreach (var x in InPlace("reorder-3-moves", "Three successive moves (the page organiser's drag)", a, d =>
        {
            var m1 = new[] { 9 }; var m2 = new[] { 4, 5 }; var m3 = new[] { 0 };
            P2.Move(d.Doc, m1, 0);
            P2.Move(d.Doc, m2, 8);
            P2.Move(d.Doc, m3, 5);
        }, reorder, ["A"])) yield return x;

        foreach (var x in InPlace("duplicate", "Duplicate page 2 after itself (ImportPagesByIndex with dest == src)", a, d =>
        {
            var idx = new[] { 1 };
            P2.ImportByIndex(d.Doc, d.Doc, idx, 2);
        }, [.. allA.Take(2), K("A", 2), .. allA.Skip(2)], ["A"])) yield return x;

        foreach (var x in InPlace("insert-from-file", "Insert pages 1-2 of B into A after page 3 (ImportPagesByIndex into the open document)", a, d =>
        {
            using var s = PdfDoc.Open(b, false, out _);
            var idx = new[] { 0, 1 };
            P2.ImportByIndex(d.Doc, s!.Doc, idx, 3);
        }, [.. allA.Take(3), .. allB.Take(2), .. allA.Skip(3)], ["A", "B"])) yield return x;

        // ---- xref-stream + object-stream source (7 of 9 such files failed incremental save in S4)
        foreach (var x in InPlace("xs-delete-move-rotate", "A.xs.pdf (xref stream + object streams): delete page 3, move pages 1-2 to 7, rotate page 5", axs, d =>
        {
            P2.FPDFPage_Delete(d.Doc, 2);
            var idx = new[] { 0, 1 };
            P2.Move(d.Doc, idx, 6);
            var pg = d.LoadPage(3); // after the above the page at index 3 is A-06... rotate whatever is there
            P2.FPDFPage_SetRotation(pg, (P2.FPDFPage_GetRotation(pg) + 1) % 4);
        }, SimulateMove(allA.Where(k => k != "PAGE-A-03").ToArray(), [0, 1], 6), ["A"], new() { [SimulateMove(allA.Where(k => k != "PAGE-A-03").ToArray(), [0, 1], 6)[3]] = 1 })) yield return x;

        // ---- new documents (extract, merge, split)
        foreach (var x in NewDoc("extract-4-6", "Extract pages 4-6 of A into a new document (CreateNewDocument + ImportPagesByIndex)", d => Import(d, a, [3, 4, 5]), Keys("A", 4, 5, 6), ["A"])) yield return x;
        foreach (var x in NewDoc("extract-forms", "Extract pages 2, 5 and 8 of A (all three carry form fields)", d => Import(d, a, [1, 4, 7]), Keys("A", 2, 5, 8), ["A"])) yield return x;
        foreach (var x in NewDoc("extract-1-3", "Extract pages 1-3 of A (links to pages 4+ and 10 point outside)", d => Import(d, a, [0, 1, 2]), Keys("A", 1, 2, 3), ["A"])) yield return x;
        foreach (var x in NewDoc("extract-3-ocg", "Extract page 3 of A alone (optional content: one layer ON, one OFF)", d => Import(d, a, [2]), Keys("A", 3), ["A"])) yield return x;
        foreach (var x in NewDoc("extract-1-note", "Extract page 1 of A alone (note + popup + reply, links)", d => Import(d, a, [0]), Keys("A", 1), ["A"])) yield return x;
        foreach (var x in NewDoc("split-chapter-1", "Split A by bookmark: Chapter 1 = pages 1-3", d => Import(d, a, [0, 1, 2]), Keys("A", 1, 2, 3), ["A"])) yield return x;
        foreach (var x in NewDoc("split-chapter-2", "Split A by bookmark: Chapter 2 = pages 4-6", d => Import(d, a, [3, 4, 5]), Keys("A", 4, 5, 6), ["A"])) yield return x;
        foreach (var x in NewDoc("split-chapter-3", "Split A by bookmark: Chapter 3 = pages 7-10", d => Import(d, a, [6, 7, 8, 9]), Keys("A", 7, 8, 9, 10), ["A"])) yield return x;
        foreach (var x in NewDoc("merge-A-B", "Merge A (10 pages) then B (5 pages) into a new document", d => Import(d, a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) && Import(d, b, [0, 1, 2, 3, 4]), [.. allA, .. allB], ["A", "B"])) yield return x;
        foreach (var x in NewDoc("merge-B-A", "Merge B then A (name clashes resolved the other way round)", d => Import(d, b, [0, 1, 2, 3, 4]) && Import(d, a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]), [.. allB, .. allA], ["B", "A"])) yield return x;
        foreach (var x in NewDoc("merge-A-A", "Merge A with itself (every name clashes with itself)", d => Import(d, a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) && Import(d, a, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]), [.. allA, .. allA], ["A"])) yield return x;
        foreach (var x in NewDoc("extract-C-all", "Extract all of C (/Rotate 0/90/180/270, MediaBox and CropBox offsets)", d => Import(d, c, [0, 1, 2, 3, 4, 5]), allC, ["C"])) yield return x;
        foreach (var x in InPlace("C-move-delete", "C: delete page 2, move pages 5-6 to the front", c, d =>
        {
            P2.FPDFPage_Delete(d.Doc, 1);
            var idx = new[] { 3, 4 };
            P2.Move(d.Doc, idx, 0);
        }, SimulateMove(allC.Where(k => k != "PAGE-C-02").ToArray(), [3, 4], 0), ["C"])) yield return x;
    }
}
