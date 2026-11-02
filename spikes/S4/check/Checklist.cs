using System.Text;
using System.Text.Json;

namespace S4;

/// <summary>Writes out/manual-acrobat-checklist.md: a hand-operated pass for Acrobat Reader and Edge. Nothing here automates Acrobat (risk R-15).</summary>
public static class Checklist
{
    static readonly Dictionary<string, string> Look = new()
    {
        ["A01"] = "Yellow highlight over \"quick brown fox\" (line A); text still readable through it. Hover or click: author \"Quire Tester\", comment text shown.",
        ["A02"] = "Green underline under \"liquor jugs\" (line B).",
        ["A03"] = "Red strike-through across \"strike this out\" (line C).",
        ["A04"] = "Magenta squiggly line under \"squiggly words\" (line D).",
        ["A05"] = "Yellow note icon near (480,560). Does the icon show? Does its comment open? Is there a linked popup? Replies are NOT linked (no /IRT): there should be no thread.",
        ["A06a"] = "FreeText box with the text \"FreeText box\" at (300,300)-(520,350). Without /AP in the cold file, does Acrobat draw anything?",
        ["A06b"] = "Pale yellow box with \"FreeText via SetAP\". Font comes from the page's /Helv because the AP stream has no /Resources.",
        ["A07a"] = "Three red wavy strokes, uniform 3 pt width.",
        ["A07b"] = "One blue wavy stroke that thickens in the middle and tapers at both ends.",
        ["A08"] = "Square, blue 2 pt border, yellow interior.",
        ["A09"] = "Circle/ellipse, green border, light-green interior.",
        ["A13a"] = "Stamp annotation with /Name written as a STRING and no appearance. Acrobat may draw its own built-in Approved stamp, or nothing. Record which.",
        ["A13b"] = "Red \"APPROVED\" box. /Name is a string, not a name object. Check the stamp's properties/comment panel.",
        ["A14"] = "Round colour-gradient image stamp at (300,380) 100x100 pt, transparent corners.",
        ["Q01"] = "Blue line with an open arrowhead at the left end and a closed arrowhead at the right end.",
        ["Q02"] = "Orange-bordered, pale-yellow pentagon.",
        ["Q03"] = "Green zig-zag polyline with an open arrowhead at the right end.",
        ["Q04"] = "Yellow note icon. Open the comment: parent text, a popup, one reply (\"Reply to the parent comment\") and an Accepted review state, all threaded under the parent.",
        ["Q05"] = "Red \"APPROVED\" stamp. /Name is a real name object.",
        ["Q06"] = "Pale-yellow FreeText box, two lines of text. Click into it: is the text editable as FreeText?",
        ["B01"] = "Text field shows \"Hello Quire\". Click in: value is editable.",
        ["B02"] = "Multiline text field shows three lines: Line one / Line two / Line three.",
        ["B03"] = "Comb field shows 1 2 3 4 5 6 7 8 in separate cells.",
        ["B04"] = "Checkbox is checked.",
        ["B05"] = "Radio group: second button (B) selected, first (A) clear.",
        ["B06"] = "Dropdown shows \"Beta\".",
        ["B07"] = "Editable dropdown shows \"custom entry\".",
        ["B08"] = "List box shows Beta and Delta highlighted; Alpha, Gamma, Epsilon not.",
        ["B09"] = "Push button \"Submit\" unchanged.",
        ["G01"] = "Signature panel: is the signature reported valid / \"changes made after signing\" / invalid? Highlight visible. (Self-signed cert: an untrusted-identity warning is expected and fine.)",
        ["G02"] = "Signature panel status after a form-field fill (incremental) and for the full copy. Field shows \"Hello Quire\".",
        ["G03"] = "Open-and-save with no edit. Signature panel status: valid / modified / invalid.",
        ["G04"] = "Checkbox checked; signature panel status.",
    };

    public static void Write(string outDir)
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "manifest.json"))).RootElement;
        var sb = new StringBuilder();
        sb.AppendLine("# S4 manual viewer pass: Adobe Acrobat Reader and Microsoft Edge");
        sb.AppendLine();
        sb.AppendLine("This is a hand-operated checklist. **Do not script Acrobat** (risk R-15: the EULA position on automated testing is an open question). Open each file by hand: Acrobat via File > Open, Edge by dragging the file onto a tab. Every file is under `spikes/S4/out/`. Record what you see in the last two columns using: **ok** (matches), **partial** (visible but wrong), **missing** (not drawn), or free text.");
        sb.AppendLine();
        sb.AppendLine("Things to note on every file, as well as the item-specific check:");
        sb.AppendLine("- Any banner, dialog or prompt on open (\"document has been repaired\", \"damaged\", \"cannot be displayed\", \"save changes?\"). For Acrobat, also whether Save is offered immediately after opening without editing (a sign Acrobat repaired the file in memory).");
        sb.AppendLine("- Whether the annotation or field is selectable and editable by a user, not only drawn.");
        sb.AppendLine("- For the signature cases: Acrobat's Signature panel wording, verbatim.");
        sb.AppendLine("- Record the Acrobat Reader build (Help > About) and the Edge version here: Acrobat ____ / Edge ____. Date: ____");
        sb.AppendLine();
        sb.AppendLine("Mode key: `inc` = incremental save, page not rendered before the save. `incR` = incremental, page rendered by PDFium before the save (so PDFium generated appearances). `full` / `fullR` = full rewrite. Annotation cases A01..A14 have all four modes. Priority 1 files are the ones that decide the open questions; priority 2 are the full-rewrite twins.");
        sb.AppendLine();

        void Table(string title, IEnumerable<(string id, string title, string mode, string file, string look, int prio)> rows)
        {
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Pri | File | What to look for | Acrobat Reader | Edge |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in rows) sb.AppendLine($"| {r.prio} | `{r.file}` ({r.id} {r.title}, {r.mode}) | {r.look} | | |");
            sb.AppendLine();
        }

        var all = new List<(string id, string title, string mode, string file, string look, int prio)>();
        foreach (var c in manifest.GetProperty("cases").EnumerateArray())
        {
            string id = c.GetProperty("id").GetString()!, t = c.GetProperty("title").GetString()!, g = c.GetProperty("group").GetString()!;
            if (!c.TryGetProperty("outputs", out var outs)) continue;
            foreach (var o in outs.EnumerateObject())
            {
                string mode = o.Name; string file = o.Value.GetString()!;
                int prio = mode is "full" or "fullR" ? 2 : 1;
                if (g == "annot" && mode == "fullR") prio = 2;
                if (g == "annot" && (id is "A02" or "A03" or "A04" or "A09") && mode == "incR") prio = 2;   // redundant with A01/A08 for this question
                if (g == "sig") prio = 1;
                all.Add((id, t, mode, file, Look.GetValueOrDefault(id, ""), prio));
            }
        }
        Table("Annotations created through PDFium", all.Where(r => r.id.StartsWith("A")).OrderBy(r => r.prio).ThenBy(r => r.id).ThenBy(r => r.mode));
        Table("Annotations authored by Quire (raw incremental update)", all.Where(r => r.id.StartsWith("Q")).OrderBy(r => r.prio).ThenBy(r => r.id));
        Table("AcroForm fields", all.Where(r => r.id.StartsWith("B")).OrderBy(r => r.prio).ThenBy(r => r.id));
        Table("Signature preservation (fixtures `inputs/signed-base.pdf` and `inputs/signed-forms.pdf` first: record the signature panel for the untouched fixtures)", all.Where(r => r.id.StartsWith("G")).OrderBy(r => r.id).ThenBy(r => r.mode));

        sb.AppendLine("## Foreign-file incremental saves with xref streams (structural validity)");
        sb.AppendLine();
        sb.AppendLine("PDFium's incremental output for files that use cross-reference streams lacks `/Type /XRef` and the closing `endobj` (see the S4 report). The `.repaired.pdf` twin has only those two defects patched. Record whether each viewer opens the PDFium output without a repair prompt.");
        sb.AppendLine();
        sb.AppendLine("| Pri | File | What to look for | Acrobat Reader | Edge |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var f in new[] { "annotation_stamp_with_ap.edit.inc.pdf", "annotation_stamp_with_ap.edit.repaired.pdf", "bug_1029.edit.inc.pdf", "bug_1029.edit.repaired.pdf", "embedded_attachments.edit.inc.pdf" })
            if (File.Exists(Path.Combine(outDir, "probe", f))) sb.AppendLine($"| 1 | `probe/{f}` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |");
        sb.AppendLine();
        File.WriteAllText(Path.Combine(outDir, "manual-acrobat-checklist.md"), sb.ToString());
    }
}
