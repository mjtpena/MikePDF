# S11: PDFium page operations, import fidelity, incremental save of page changes, and the cost of a page assembler

- Status: **Complete for everything measurable on this machine.** Acrobat Reader and Edge were not run (R-15); other readers are **not measured**
- Date: 2026-11-02
- Author: Sonnet (spike). Reviewer: Opus
- Target documents: [page-organiser](../features/page-organiser.md) (route A or B, OQ-PO-1, OQ-PO-2, OQ-PO-3, OQ-PO-7, OQ-PO-8, OQ-PO-9), [save-pipeline](../features/save-pipeline.md) stage 2, ADR-0008
- Repo state: branch `m4/spikes-save`, base `50dfa75`. PDFium **158.0.8125.0** (`chromium/8125`, x64, no V8, no XFA), as pinned in `build/pdfium.lock`
- Code: `spikes/S11/` (throwaway; nothing under `src/` is changed). Raw output and all generated PDFs are in `spikes/S11/out/` (gitignored, re-created by the commands in section 12); the raw results that back this report are copied to `spikes/S11/results/` (`results.json` with every case and field, `report-tables.md`, `pdfjs-compare.txt`, `perf.jsonl`, `corpus.txt`/`corpus.jsonl`, `repo-fixtures.jsonl`, `pdfjs.jsonl`)

---

## 1. Questions and short answers

| # | Question | Short answer | Where |
|---|---|---|---|
| 1 | Do `FPDF_ImportPages`/`ImportPagesByIndex`, `FPDFPage_Delete`, `FPDF_MovePages` (present in this build) and `FPDFPage_SetRotation` keep outlines, internal links, named destinations, AcroForm fields, tags, page labels, annotations and optional content? | **Operations inside one document (rotate, move, delete, insert blank) are safe for everything except page labels**, which are positional and do not follow their pages, and deleted pages leave dead outline entries and orphan form fields behind. **Operations that build or extend a document by import (extract, split, merge, insert from file, duplicate) are not safe**: the outline, all named destinations, all page labels, the structure tree and `/OCProperties` are dropped; the destination of **every link that points forward or outside the import is stripped** (a link survives only if its target page was already imported earlier in the same call; 36 of 42 page links die in a merge of A and B); `/AcroForm` is not created; same-named form fields from different sources are merged and **the second source's values are silently replaced by the first's**; named destinations with clashing names silently retarget links to the wrong page. Annotations, popups/replies, page content, rotation and boxes survive. | 3 |
| 2 | Can PDFium save these changes incrementally, and are the results valid in PDFium, `MikePDF.PdfWriter`'s validator and pdf.js? | **Yes for classic-xref sources; no for xref-stream sources.** For 11 of 11 in-place cases on classic-xref fixtures and 35 of 35 classic corpus files the incremental output keeps the original bytes as an exact prefix and passes the strict validator, `qpdf --check` is clean except where the operation itself leaves an orphan widget (duplicate, insert from file), and pdf.js agrees with PDFium on pages, outline, links and labels. For the xref-stream fixture and for both xref-stream corpus files the appended xref is invalid in the way S4 found (`endobj` missing; the strict parser refuses it, `qpdf` warns); PDFium and pdf.js still open it by repairing. Deleted pages' bytes stay in the file. | 4 |
| 3 | If PDFium loses things, how much work is a page assembler in `MikePDF.PdfWriter`? | A **working prototype exists** (`spikes/S11/ops/Assembler.cs`, 734 lines, 465 of them code). It passes every check PDFium fails (outline, links between kept pages, named destinations with clash renaming, page labels, form fields with pruning and renaming, optional content) on 14 of 15 cases (the 15th shows a `/DR` font-name clash, section 5.2), and copies 243 of the 314 corpus files PDFium opens (it refuses 71 that the strict parser refuses). A production version is **estimated at 4 to 6 work packages** (estimate, not measured), listed in section 6. | 5, 6 |
| 4 | Cost of merging 5 PDFs of 100 pages and of splitting a 600-page PDF by bookmark? | PDFium: merge **255 ms**, split **421 ms** (60 parts). Assembler prototype: merge **138 ms**, split **329 ms**. Peak working set 88 to 110 MB over a 30 MB process baseline. Both are cheap enough that cost does not decide the route. Medians of 5 runs on a machine at 26 to 28% background CPU. | 7 |

**Recommendation (details in section 8): a hybrid.** PDFium for rotate, move, delete and insert-blank, saved incrementally by the existing stage 2 (plus a small writer-host fix-up for page labels and, if OQ-PO-3 says so, dead references). The assembler (`AssemblePages`, writer host message 1028) for extract, split, merge, insert from file and duplicate. **PDFium's import must not be used for anything that is saved**, including the undo of a delete (section 3.4).

---

## 2. Method

### 2.1 Environment

| Item | Value |
|---|---|
| Machine | Intel Core Ultra 9 275HX, 24 logical processors, 63.4 GB RAM, Windows 11 Home build 26300, x64. Not admin. Background CPU 26 to 28% (other agents) during the performance runs |
| .NET | SDK 10.0.300, `net10.0-windows10.0.26100.0`, Release |
| PDFium | prebuilt 158.0.8125.0 (see above), called by P/Invoke. Signatures of `FPDF_ImportPages`, `FPDF_ImportPagesByIndex`, `FPDF_MovePages`, `FPDFPage_Delete`, `FPDFPage_SetRotation`, `FPDFPage_New`, the bookmark, link, named-destination and page-label calls checked against `include/*.h` |
| qpdf | 12.4.2 (S4's pinned copy, `spikes/_shared/tools/qpdf`) |
| pdf.js | `pdfjs-dist` 6.4.299 under Node v24.16.0, reused from S4 (`spikes/S4/pdfjs/node_modules`, linked by a junction). Not Firefox itself |
| `MikePDF.PdfWriter` | the production project, referenced directly: `StrictDocument.Open`, `StructureValidator`, `PdfSerializer` |
| New dependencies | none. The harness reuses the S4 P/Invoke surface (`spikes/S4/common`) and `spikes/S4/check/Qpdf.cs` |

### 2.2 Fixtures (generated by `spikes/S11/fixtures/gen_fixtures.py`, classic xref, every page carries a unique text marker `PAGE-<doc>-<nn>` and a coloured rectangle)

| File | Content |
|---|---|
| `A.pdf` (16,399 B) | 10 pages. Outline of 3 levels with a named-destination action, a URI entry and a closed branch. Links: explicit `/Dest` to the next page, `GoTo` action, named `/Dest` string, link to the last page, URI. 12 named destinations in the `/Names` tree plus 2 legacy `/Dests`. Page labels (roman, then `A-` prefix). AcroForm: two text fields, a checkbox and a parent field with two kids. Tagged structure (Document > P per page, parent tree, `MarkInfo`). Optional content (one layer ON, one OFF, used on page 3). Annotations: a note with popup and reply, a highlight, a free text. Page 6 has `/Rotate 90`, page 7 a CropBox |
| `A.xs.pdf` (14,611 B) | `A.pdf` through `qpdf --object-streams=generate`: xref stream and object streams |
| `B.pdf` (5,708 B) | 5 pages with its own outline, links, named destinations and form fields whose names **clash** with A's (`name`, `agree`, `address.street`; also `dest-p1` to `dest-p5`) |
| `C.pdf` (2,844 B) | 6 pages: `/Rotate` 0, 90, 180, 270; MediaBox and CropBox with offsets and negative origins |
| `big-1..5.pdf` (2.05 MB each) | 100 pages each, 30 lines of text, a 96 KB incompressible image every 5 pages, an outline entry every 10 pages, a link per page |
| `big-600.pdf` (12.3 MB) | 600 pages, 60 top-level bookmarks (one per 10 pages) with 2 children each |
| PDFium corpus | `spikes/_shared/corpus/pdfium-resources`: 343 PDFs, 314 of which PDFium opens |
| Repo fixtures | `tests/MikePDF.Integration.Tests/Fixtures`: 5 PDFs (1 encrypted, which does not open without a password); they contain no outline, links, forms or labels, so they exercise nothing here (`results/repo-fixtures.jsonl`) |

### 2.3 What the harness does

`S11.Ops run` applies each operation with PDFium to an in-memory copy, saves it **incrementally** (`FPDF_INCREMENTAL`) and **in full** (`FPDF_NO_INCREMENTAL`) for in-place operations, and as a full copy for new documents, then reads the result back and compares it with what the operation should have produced:

1. **PDFium view** (`Profile.cs`): page order by the text marker, `/Rotate`, MediaBox, CropBox, page label, annotation counts by subtype, link targets (resolved to the marker of the page they land on), widgets (name, type, value), structure-tree children, a half-scale render hash, the outline (title and resolved target), named destinations, the popup and reply links of the note.
2. **File view**: `MikePDF.PdfWriter`'s strict parser reads the catalog (outline, `/Names`, `/Dests`, `/AcroForm` and its field count, `/StructTreeRoot`, `/MarkInfo`, `/PageLabels`, `/OCProperties`).
3. **Validators**: `qpdf --check`, `StructureValidator`, the "original is an exact prefix" test for incremental saves, and pdf.js (`pdfjs/inspect.mjs`: pages, outline targets, link targets, labels), compared with PDFium's view.
4. **Expectation**: computed from the source profiles and the intended page list. "Links to excluded pages" are expected to be removed or inert and must never land on another page.

The same operations are then run through the **assembler prototype** and evaluated by the same code.

Not done, and why: Acrobat Reader and Edge (R-15, no automation; every cell is **not measured**); encrypted sources (the strict parser and the prototype refuse them; PDFium's behaviour is out of scope here, OQ-AC-6); signatures through page operations (S8 territory); a 1 GiB-class source (no such fixture was generated).

---

## 3. Results: what PDFium keeps

Full tables are in section 3.5. Reading guide: **ok** = as expected; **dead entries n** = the outline entry still exists but goes nowhere; **shifted a/b** = the page label of a of b pages differs from the label that page had before (labels stay with positions); **LOST** = absent.

### 3.1 Operations inside one document

| Operation (API) | Outline | Links | Named dests | Labels | Forms | Annots | Tags | Opt. content | Render, boxes |
|---|---|---|---|---|---|---|---|---|---|
| Rotate 2 pages (`FPDFPage_SetRotation`) | ok | ok | ok | ok | ok | ok | ok | ok | ok |
| Move pages (`FPDF_MovePages`, one move and three successive moves) | ok | ok (33 of 33 land on the same page content) | ok | **shifted 7 to 8 of 10** | ok | ok | ok | ok | ok |
| Delete pages 3 and 10 (`FPDFPage_Delete`) | **dead entries 2** (they do not navigate anywhere else; PDFium resolves them to no page) | ok; 8 links to the deleted pages are inert | ok; 2 stale names unresolved | **shifted 6 of 8** | ok | ok | ok | ok | ok |
| Delete the 3 pages that carry form fields | dead entries 3 | ok | ok | shifted 6 of 7 | **orphan fields**: `/AcroForm /Fields` still lists all 4 fields although no widget is left | ok | ok | ok | ok |
| Insert a blank page (`FPDFPage_New`) | ok | ok | ok | shifted 7 of 11 | ok | ok | ok | ok | ok |
| Duplicate a page (`ImportPagesByIndex` with dest = src) | ok | **the copy loses its explicit-destination links** (2 missing; named-destination links survive) | ok | shifted 9 of 11 | the copy's widget is **not in `/AcroForm`** (`qpdf` warns) | ok | ok | ok | ok |
| Insert B's pages 1 and 2 into A (`ImportPagesByIndex` from another document) | B's outline is not imported (by design of the API) | **2 missing, 2 wrong**: B's named-destination links resolve through A's names and land on **A's pages** | A's names win; B's are dropped | shifted 9 of 12 | **B's `name` field shows A's value ("Bruno" is shown as "Alice")**; widget not in `/AcroForm` | ok | ok | ok | ok |
| "Undo" a delete by re-importing the page from the base file (page-organiser 3.6) | dead entry stays dead | **5 links stay dead** | the name stays dead | ok | ok | ok | ok | **the re-imported page loses its layer wiring: the hidden layer renders** | differs on the page |

Rotation, CropBox offsets and negative-origin MediaBoxes (C.pdf) survive every operation, including import (render hash equal, boxes equal).

### 3.2 Operations that build a new document (extract, split, merge)

Every row was run with `ImportPagesByIndex`; `FPDF_ImportPages` with a range string (`"4-6"`) gives an identical evaluation (`extract-4-6-range-string`).

| Operation | Outline | Links between kept pages | Named dests | Labels | Forms | Annots | Tags | Opt. content | Render |
|---|---|---|---|---|---|---|---|---|---|
| Extract A 4-6 | **LOST** | **all dead** (3 expected, 3 missing, 9 inert) | **LOST** | **LOST** | `/AcroForm` absent; one widget kept as a bare annotation | ok | **LOST** | ok (kept pages use no layer) | ok |
| Extract A 1-3 (includes the layer page) | LOST | all dead | LOST | LOST | `/AcroForm` absent | ok | LOST | **`/OCProperties` absent: the OFF layer is drawn** | differs on page 3 |
| Split A into chapters 1-3, 4-6, 7-10 | LOST in each | all dead | LOST | LOST | `/AcroForm` absent | ok | LOST | layer visible in chapter 1 | chapter 1 differs |
| Merge A then B (15 pages) | LOST | **36 of 42 page links dead** (the 6 backward links survive) | LOST (12 names) | LOST | `/AcroForm` absent; **B's `name` shows "Alice", B's `agree` shows "Yes" (source: "Off")**; the kid fields `address.street`/`.city` are not recognised as fields at all (parent field lost) | ok | LOST | layer visible | page 3 differs |
| Merge B then A, merge A with itself | same | 36 and 54 dead | LOST | LOST | same | ok | LOST | layer visible | page 3 differs |

What survives: page content, annotations (counts by subtype equal; the note's popup and reply links intact), rotation, MediaBox, CropBox, form widget appearance. pdf.js agrees with PDFium on every one of these results (pages, outline, link targets, labels), so the losses are in the file, not in PDFium's reading.

### 3.3 Why the links die (checked in the output)

`inspect` of `extract-4-6.full.pdf`: page A-04's links read `none | none | unresolved`. The explicit `/Dest [page ...]` and `GoTo /D [page ...]` of the imported link annotations are **removed** when the target page is not yet in the output, and the named-destination link keeps a name that no longer exists. This happens even when the target page is imported by the same call but later in the page list (A-04's link to A-05). **A destination survives only when its target page was imported earlier in the same call**: in the merge of A and B the links on A-07 to A-01, A-10 to A-01 and B-05 to B-01 survive, and the other 36 of 42 page links are dead. pdf.js reads the same results (`none | none | unresolved`). The rule explains the duplicate case too: the copy of page 2 loses its two explicit links although page 3 is in the document, because it was not copied in that call.

### 3.4 Consequences for the page organiser spec

1. **3.5 items 1 to 4 cannot be met by PDFium's import** (PO-13 fails on outline, links, named destinations, labels, forms, tags and layers). Route A is only viable for operations that do not import.
2. **3.6's "undo of Delete and Insert by re-importing the pages" is unsafe** (section 3.1 last row). Undo must come from the model: rebuild the engine's working document from the base plus the commands still in effect, or from the assembler.
3. **A document engine holding two documents (OQ-PO-2)** is only needed if imports are used for display. With the assembler it is not needed for save.
4. **Page labels** must be rewritten by something other than PDFium, because every operation that changes the order or number of pages (move, delete, insert, import) shifts them, and PDFium never touches `/PageLabels`. Rotate does not.
5. **Dead references after Delete** (OQ-PO-3): PDFium leaves them as inert entries (outline entries with a destination PDFium resolves to nothing, links to nothing, orphan `/AcroForm` fields). They never navigate to a wrong page in PDFium or pdf.js. Removing them needs a writer-host fix-up.

### 3.5 Full per-case tables (generated by `spikes/S11/report_tables.py`)

`results/report-tables.md` has all 64 rows (case, save mode, every feature, qpdf, validator, prefix). The in-place and new-document rows are summarised above; the assembler rows are in section 5.

---

## 4. Results: incremental save of page changes

### 4.1 Classic-xref sources

| Case (A.pdf, 16,399 B) | Appended | qpdf | PdfWriter validator | Exact prefix | pdf.js agrees with PDFium |
|---|---|---|---|---|---|
| rotate 2 pages | +4,492 B (27%) | ok | ok | yes | yes |
| delete 2 pages | +2,567 B (16%) | ok | ok | yes | yes |
| delete page 1 | +2,719 B (17%) | ok | ok | yes | yes |
| move 2 pages | +2,729 B (17%) | ok | ok | yes | yes |
| three moves | +2,920 B (18%) | ok | ok | yes | yes |
| insert a blank page | +1,838 B (11%) | ok | ok | yes | yes |
| duplicate a page | +4,129 B (25%) | **warning** (orphan widget) | ok | yes | yes |
| insert from file | +3,472 B (21%) | **warning** (orphan widget) | ok | yes | yes |
| delete the 3 form pages | +2,422 B (15%) | ok | ok | yes | yes |

The growth is a constant cost of rewriting the page tree nodes, not a fraction of the file. On `big-600.pdf` (12.32 MB, one flat `/Pages` node of 600 kids), medians of 5 runs:

| Operation | Appended | Time (open, operate, save) |
|---|---|---|
| no change | 0.01 MB (0.0%) | 43 ms total |
| rotate 1 page | 0.16 MB (1.3%) | 58 ms |
| delete 50 pages | 0.11 MB (0.9%) | 55 ms |
| move 10 pages | 0.05 MB (0.4%) | 50 ms |

(the no-op row differs from S4's +24% to +270% because those files were 1 to 20 KB; here it is bytes, not a ratio.)

### 4.2 Xref-stream sources: still broken, as S4 found

`xs-delete-move-rotate.inc.pdf` (A.xs.pdf, delete, move, rotate): **`StructureValidator` fails** (`ParseFailed: MissingEndObj ... 'endobj' expected after stream of object 103 0`), `qpdf` warns (`expected endobj`), the strict parser cannot read the catalog, so every catalog check on that file reads "absent". PDFium reopens it (it repairs) and so does pdf.js, with the same pages, outline and links as the full-save version. **The full-rewrite save of the same operations is valid.** So stage 3 (`NormalizeTail`) is required for page changes exactly as it is for forms; nothing new, but page operations must be in its test matrix.

### 4.3 Corpus (PDFium's test PDFs)

41 corpus files have at least two pages and open. Delete the last page, save incrementally:

- the original is an exact prefix in **41 of 41**;
- of the 10 files that are clean (strict parse accepts the source and `qpdf --check` exits 0), all 10 stay clean (qpdf exit 0, validator valid); all 10 use a classic xref table;
- the strict parser accepts the source of 37 of the 41; **35 are still accepted afterwards and the 2 that are not are exactly the 2 files that use an xref stream** (`feature_linearized_loading.pdf`, `page_labels.pdf`; validator invalid, qpdf warning). Together with `A.xs.pdf` that is 3 of 3 xref-stream sources failing, and 0 of 35 classic sources failing (`results/corpus.txt`);
- the remaining 31 files are not "clean" (the strict parser refuses the source of 4 of them, and qpdf already warns on the source of the others), so no baseline-free claim is made about them.

### 4.4 Deleted content stays in an incremental file

`grep` for the deleted page's text: present in `delete.inc.pdf`, absent in `delete.full.pdf` and in the assembler's output. A user who deletes a sensitive page and saves incrementally keeps its bytes in the file. This is normal for incremental updates, but the organiser should say so (or offer Save As with a full rewrite) because §8.5 users will assume delete means gone. **Redaction is separate (§8.6), but page deletion is the first place users will expect removal.**

---

## 5. Results: the assembler prototype

`spikes/S11/ops/Assembler.cs`, on `MikePDF.PdfWriter`'s public API only (`StrictDocument.Open`, `GetObject`, `PdfSerializer`, the object model). It takes strictly parsed sources and a page sequence and writes a new classic-xref file. No `src/` code was changed.

### 5.1 What it does

1. **Page tree**: flattens the source tree, materialises inherited `Resources`, `MediaBox`, `CropBox`, `Rotate`, writes a new tree (two levels above 100 pages).
2. **Object closure**: copies every object reachable from the kept pages, renumbers, keeps stream bytes verbatim. Any reference to a source *page* object is mapped to the new page if kept and to null otherwise (so annotations' `/P` and so on never pull in other pages).
3. **Links**: a link to a removed page is **removed**; a link to a kept page is retargeted (explicit destination, `GoTo` action, named destination).
4. **Named destinations**: reads name tree and legacy `/Dests`, carries the names whose page is kept into one flat name tree, **renames on a clash with a different page** (`dest-p1~2`) and rewrites the links that used the name.
5. **Outline**: rebuilds it from the kept entries; an entry that goes to a removed page is dropped and its surviving children move up; URI entries are kept under a kept parent; merging can wrap each source under an entry named after it.
6. **Page labels**: recomputes ranges so labels follow the pages; pages of an unlabelled source get decimal numbering by position.
7. **AcroForm**: finds the widgets on kept pages, prunes each field's `/Kids` to kept widgets and field nodes, rebuilds `/Fields`, and **renames a root field whose name is already used** (`name` to `name_2`), unions `/DR /Font`.
8. **Optional content**: copies `/OCProperties` (union of sources) so layer wiring survives.
9. Drops: structure tree and `MarkInfo` (tags), `StructParents`, article beads, `/Metadata`, other `/Names` trees, JavaScript, signatures' meaning (see 5.3).

### 5.2 Results on the same cases

All checks pass for 14 of the 15 assembler cases **except tags, which it drops on purpose** (OQ-PO-9); the 15th, merge B then A, renders one annotation differently (below). Rows: delete, reorder, extract 4-6, extract the form pages, extract 1-3 (layer page), extract the layer page alone, extract the note page alone, split into three chapters, insert B into A after page 3, merge A+B, merge B+A, extract all of C, extract from the xref-stream file.

| Check | PDFium import (section 3.2) | Assembler |
|---|---|---|
| Outline | LOST | ok in every case (outline kept for kept pages; merge adds one wrapper entry per file) |
| Links between kept pages | all dead | ok; links to removed pages removed |
| Named destinations | LOST | ok; clashes renamed, links follow |
| Page labels | LOST / shifted | ok (follow their pages) |
| Forms | no `/AcroForm`; values merged | `/AcroForm` rebuilt; B's `name` becomes `name_2` and keeps "Bruno"; pruned to kept pages |
| Optional content | lost, hidden layer drawn | ok |
| Annotations, popups, replies | ok | ok |
| Render, boxes | ok (not layers) | ok |
| `qpdf --check`, validator | ok | ok (all 15) |
| pdf.js agrees | yes | yes (the only differences are the renamed destination names, which the harness's pdf.js probe, a fixed list of names, did not ask for) |

**One real defect it found in itself:** `asm-merge-B-A` renders A's free-text annotation (page 4) differently: both sources define `/Helv` in `/AcroForm /DR /Font`, B's is Courier, and the union keeps the first. A production assembler must **rename or re-map clashing `/DR` resource names** (or regenerate appearances). PDFium's import has the same exposure but never reaches it because it drops `/AcroForm`.

### 5.3 What the prototype does not do (the work still to do)

| Gap | Note |
|---|---|
| Duplicating a page twice from one source | the map is 1:1 old page to new page; needs per-instance copies |
| Signature fields | a copied signature widget keeps `/V` (a now-invalid signature). Must clear the value or drop the field, and the dialog says signatures are not kept |
| Tags | dropped. Keeping them means rebuilding the parent tree and `/K` for the kept pages |
| Encrypted sources | refused (the parser does not decrypt) |
| `/DR` clashes | see above |
| Other action types (`GoToR`, `Launch`, `JavaScript`, `Named`) | copied unchanged; should be reviewed against the policy (script off by default, §5.3) |
| Output compression | streams are copied as they are and nothing is re-packed into object streams: output sizes were within 0.3% of PDFium's (10.26 MB vs 10.23 MB for the merge), but a source made of object streams stays large |
| Large files | whole sources are in memory (limit 1 GiB per `PdfWriter` rule R-38); streaming is not implemented |
| Limits, fuzzing, errors | the prototype throws on surprises; production needs the `PdfResult` discipline, `ParserLimits` accounting, SharpFuzz targets and the A.9 review |
| Incremental output | not attempted. All output is a new full file. Insert into an open document would then be a full rewrite (see 8, ADR-0008 decision 3) |

### 5.4 Corpus

Run over the 314 corpus files PDFium opens ("copy every page into a new file"):

- the strict parser **refused 71** (23%): `InvalidXrefEntry` 12, `InvalidTrailer` 12, `ObjectHeaderMismatch` 8, `InvalidXref` 6, `MissingStartXref` 6, `InvalidHeader` 6, `UnsupportedFilter` 6, `MissingEndObj` 5, others fewer. These are mostly deliberately damaged test files, but they show **R-39 in numbers**: anything that depends on the assembler cannot be used on a file the strict parser refuses. The same applies to `UnsupportedFilter` (the parser cannot decode a stream it needs, for example an object stream)
- it **threw 0** and produced output for **243**; the page count equals PDFium's in 236 of them (the 7 others differ; the cause was **not investigated**, but a page tree whose `/Count` disagrees with its `/Kids`, or a loop that PDFium resolves differently, is the likely kind of parser differential the spec worries about, SP-34)
- of the 243: outline kept in 7 of 7 files that have one, named destinations in 5 of 6, page labels in 2 of 2, every page link still resolving in 3 of 4, widgets recognised in 31 of 32; `qpdf --check` exits 0 for 152 (120 of these sources were clean), exits 2 (errors) for 5.

PDFium's import on the same files (section 4.3 and `results/corpus.txt`): the outline survived in **0 of 7**, named destinations in **0 of 8**, page labels in **0 of 2**, all page links in **1 of 4**; form widgets stay recognised in 38 of 40 files (PDFium repairs them on load; the catalog has no `/AcroForm`).

---

## 6. The assembler's cost, as a work estimate

Measured: the prototype is **734 lines (465 of code)**, written and debugged in one working session (not timed) against the public `PdfWriter` API, including the page tree, closure copy, links, names, outline, labels, forms and optional content. It is a prototype: no limits accounting, no fuzzing, no unit tests. Estimate (not measured) for a production version meeting the repo's rules (§12.1 coverage, mutation tests, fuzz targets, A.9 review) is **4 to 6 work packages** of about 600 changed lines:

| WP | Scope |
|---|---|
| 1 | Core: page-tree flatten, closure copy and renumber with limits and `PdfResult` errors, writer (object streams optional), page duplicates, encrypted/damaged refusal, `AssemblePages` (1028) protocol message and `WriterHost` glue, handle-based I/O (no paths) |
| 2 | Links, named destinations (both forms), clash rules, outline rebuild including the "dropped entry promotes children" rule and merge wrappers, page labels |
| 3 | AcroForm: prune, rename, `/DR` and `/DA` merge with resource renaming, signature fields, `/NeedAppearances`, widget `/Parent` chains, calculation order |
| 4 | Optional content, annotations edge cases (`/IRT`, `/Popup`, `/P`), JavaScript/action policy, tags decision (drop with notice, or rebuild) |
| 5 | Test matrix from this spike (fixtures A, B, C, the xs file), fidelity checks (fresh engine, qpdf, pdf.js), fuzz targets, differential check against PDFium for object resolution (SP-34), performance and memory tests |
| 6 (if tags or streaming are required) | Structure tree rebuild; streaming copy for 1 GiB-class sources |

It is smaller than the annotation work (ADR-0008 already accepted a writer in this assembly) and shares the parser, serializer, limits and validator.

---

## 7. Results: cost

Five runs per scenario, each in its own process; medians. Inputs generated as in 2.2. "Peak WS" is the process peak working set (includes the .NET runtime baseline of 30.6 MB and the input byte arrays held in memory); "growth" is peak minus the working set at start of the scenario.

| Scenario | Total | Peak WS | Growth | Private peak | Output |
|---|---|---|---|---|---|
| Merge 5 x 100 pages, **PDFium** | 255 ms | 110.5 MB | 80.0 MB | 93.8 MB | 10.23 MB |
| Merge 5 x 100 pages, **assembler** | 138 ms | 87.7 MB | 57.4 MB | 70.7 MB | 10.26 MB |
| Split 600 pages by 60 bookmarks, **PDFium** | 421 ms | 109.7 MB | 79.4 MB | 88.5 MB | 12.30 MB (60 files) |
| Split 600 pages by 60 bookmarks, **assembler** | 329 ms | 96.5 MB | 66.3 MB | 83.1 MB | 12.34 MB (60 files) |

One run's phases (not medians): PDFium merge: read 44 ms, open 4, import 29, save 185, write 4. Assembler merge: read 11, parse 26, assemble 90, write 4. PDFium split: read outline 3 ms, split and write 394. Assembler split: parse 25, read outline through PDFium 8, split and write 316.

Notes: the bookmark ranges come from PDFium's outline reading in both routes (as the broker's `GetOutline` would). The files are 2 MB per 100 pages of mostly incompressible images, so the byte-copy cost is representative of scans, not of text-only files. No timing was taken on reference hardware (R1 to R3), on ARM64, or under the AppContainer; the engine would add pipe and handle costs not measured here. Neither route comes near the 2 GB engine memory cap.

---

## 8. Recommendation per operation, with the evidence

| Operation | Route | Why | Save |
|---|---|---|---|
| Rotate | **PDFium** (`FPDFPage_SetRotation`) | nothing is lost (section 3.1) | stage 2 incremental (+ `NormalizeTail` for xref streams) |
| Reorder / move | **PDFium** (`FPDF_MovePages`) | links, outline, names, forms, tags, layers all follow the page objects (33 of 33 links, 100% of outline entries) | stage 2 incremental; **plus a page-label fix-up** (labels are positional) |
| Delete | **PDFium** (`FPDFPage_Delete`) | no wrong-page navigation anywhere; dead references stay inert | stage 2 incremental; plus label fix-up; plus a writer-host **clean-up pass if OQ-PO-3 says remove** (dead outline entries, dead links, orphan `/AcroForm` fields; the assembler's code does exactly this for full copies); and tell the user the deleted bytes remain in an incremental file |
| Insert blank page | **PDFium** (`FPDFPage_New`) | nothing lost | stage 2 incremental; plus label fix-up |
| Duplicate | **Assembler** (or a writer-host "clone page" built from its closure copy) | PDFium drops the copy's link destinations and leaves its widgets out of `/AcroForm` | full rewrite |
| Insert from file / drop pages from another tab | **Assembler** | PDFium retargets clashing named destinations to wrong pages and overwrites same-named field values | full rewrite |
| Extract | **Assembler** | PDFium loses the outline, forward links, names, labels, `/AcroForm`, tags and layer wiring (section 3.2) | new file |
| Split (ranges, every N, by bookmark) | **Assembler** | same | new files (the broker reads the bookmarks through `GetOutline`; ranges are computed by the broker; the assembler gets page lists) |
| Merge | **Assembler** | same, plus data loss in clashing fields | new file |

**Display versus file.** The engine still needs to show inserted and duplicated pages before the user saves. Two ways, for Opus to choose (OQ below):

1. **Assembler-backed working copy** (recommended): on each cross-document command the writer host assembles base plus pending commands into a temporary working file in the private journal directory (measured: 138 ms for 500 pages, 329 ms for 600 pages and 60 outputs, so about 0.1 to 0.3 s per command on a 600-page document) and the engine reopens it. What the user sees is what is saved, there is one implementation of page structure semantics, and undo is a re-assemble from the base. In-document operations (rotate, move, delete, blank) continue in PDFium on top of the working copy.
2. **PDFium import for display, assembler at save**: instant, but two implementations that can diverge; stage 5's fresh-engine comparison would catch divergence, at the price of failing saves.

**Go/No-go.**
- Route A alone (PDFium for everything): **No-go**. PO-13 fails on outline, links, names, labels, forms, tags and layers, and the same-name field overwrite is a silent data-corruption bug.
- Route A for rotate, move, delete and insert blank: **Go**, with the label fix-up and `NormalizeTail` for xref streams.
- Route B (assembler) for extract, split, merge, insert, duplicate: **Go**, subject to R-39 (23% of the corpus is refused by the strict parser; the product needs a policy for those files, see Open questions).

### 8.1 Proposed amendments (proposals only; no ADR or spec was edited)

**ADR-0008, decision 1** (replace the last bullet): "PDFium remains the engine for reading, rendering, text, form filling and field appearances, and for the page operations that stay inside one document and only reorder, delete, rotate or insert blank pages. `MikePDF.PdfWriter` owns every operation that creates a document or copies pages between documents (extract, split, merge, insert from file, duplicate) through `AssemblePages` (writer host message 1028). PDFium's `FPDF_ImportPages` and `FPDF_ImportPagesByIndex` are not used for anything that is saved."

**ADR-0008, decision 3, new step 3b**: "After `NormalizeTail`, when page commands were applied, the writer host runs `FixPages`: it rewrites `/PageLabels` so labels follow their pages and, when the product setting says so, removes outline entries, link annotations, named destinations and `/AcroForm` fields that refer to deleted pages. Page changes in a file whose newest cross-reference section is a stream require `NormalizeTail`, as forms do."

**ADR-0008, decision 4**: add "page operations that insert pages from another document, duplicate pages, or follow an assembler-backed working copy produce a full rewrite; the signed-document warning in save-pipeline 3.8 applies. This answers OQ-PO-7."

**Page-organiser spec 3.6**: replace "Undo. Rotate and Move are undone by their inverse operation. Delete and Insert are undone by re-importing the pages" with: "Undo of any structural command rebuilds the engine's working document from the base file (or the working copy) plus the commands still in effect. Re-importing deleted pages is not used: it removes the links that pointed to them and detaches their layer wiring (S11 3.1)."

**Page-organiser spec 11 (engine messages)**: with the recommendation, messages `InsertPages` (56) and `CreateDocument` (61) are not needed; `kMaxDocumentsDocumentKind` stays 1 (resolves OQ-PO-2). `InsertBlankPage`, `DeletePages`, `MovePages`, `RotatePages` stay.

**Page-organiser spec 3.5, add**: "(8) Page labels follow their pages in every operation (S11: PDFium shifts them). (9) A same-named form field from another source is renamed, never merged (S11: PDFium overwrites the value). (10) Named destinations from another source that clash with an existing name are renamed and the links that used them are updated (S11: PDFium retargets them to the wrong page)." This answers OQ-PO-8.

**Acceptance criteria to add**: PO-11 and PO-13 get fixtures A, B and C from this spike; a new PO-33 "after Delete the file still contains the deleted page's bytes unless the user saved with a full rewrite" (privacy notice); PO-34 "page labels follow pages through every operation".

---

## 9. Surprises

1. **PDFium strips the explicit destination from every imported link whose target page is not already in the output**, including forward links between pages imported by the same call. The links survive as annotations that do nothing; backward links survive.
2. **Same-named form fields are merged by value**: B's "Bruno" is displayed as "Alice", and B's unchecked box displayed checked. This is data corruption, not a loss.
3. **Named destinations from the second document do not fail, they lie**: a link meant for B's page 2 lands on A's page 2.
4. **Page labels are positional in PDF, so even a pure move shifts them**; PDFium does not touch `/PageLabels` in any operation.
5. **Deleting every page that holds a form field leaves all of the fields in `/AcroForm`.**
6. **Duplicating a page leaves its widget copy outside `/AcroForm`**; `qpdf` warns and other readers may ignore it. PDFium repairs on load, which hides the problem.
7. **An import of a page that uses a layer drops `/OCProperties`, so the OFF layer is drawn.** Visible, not just structural.
8. **"Undo by re-import" is worse than the original**: the page returns, but the links, the outline entry and the names that pointed at the old object stay dead.
9. **The assembler's own defect** (clashing `/DR` font names) is the kind of cross-document problem that would also be present in PDFium if it handled forms at all; it is the argument for owning this code.
10. **Incremental page changes on large files are cheap** (0.4% to 1.3% appended on 12 MB) but not free on tiny ones (11% to 43% of 1 to 16 KB).
11. The pdf.js named-destination listing returned nothing for a valid name tree (`getDestinations()` empty), while single lookups worked. The harness probes the known names instead.

## 10. UNVERIFIED and not measured

- Acrobat Reader and Edge, for every cell (R-15). In particular how Acrobat treats widgets that are in no `/AcroForm /Fields`.
- ARM64, and any hardware other than this machine. No reference-hardware timings.
- Encrypted sources through any operation. Signed documents through any operation.
- Files above a few MB for the correctness checks; a 1 GiB-class file for either route.
- Tagged structure through the assembler (dropped by design).
- `FPDF_ImportNPagesToOne` and `FPDF_CopyViewerPreferences` (not relevant here).
- Memory under the 2 GB job limit and inside the AppContainer.
- Whether the strict parser's refusal rate (23% of the PDFium test corpus) is representative of real-world files. The corpus is built from damaged and edge-case files; the Acrobat install's PDFs (S8's other corpus) were not re-run.

## 11. Open questions for Opus

1. **Display versus file** (section 8): assembler-backed working copy (recommended) or PDFium import for display plus assembler at save?
2. **OQ-PO-3**: leave dead outline entries, links and orphan fields in place (inert, what PDFium does) or remove them (needs the `FixPages` pass; the assembler already does it)?
3. **Files the strict parser refuses** (R-39): block extract/split/merge/insert for them, or fall back to PDFium's import with a warning naming what will be lost (outline, links, names, labels, forms, tags, layers)? The fallback is cheap to build and produces a worse file silently if the warning is skimmed.
4. **Tags** (OQ-PO-9): drop with a notice (what is measured) or budget the work package that rebuilds the parent tree.
5. **Incremental insert**: should the assembler later emit an incremental update (new objects appended, page tree replaced) so that insert into an unsigned or signed file keeps the original bytes? Not tried here.
6. **`/DR` clashes**: rename resources (cheap, changes `/DA` strings) or regenerate appearances?

## 12. How to reproduce

```powershell
python spikes/S11/fixtures/gen_fixtures.py spikes/S11/out/fixtures
dotnet build spikes/S11/ops/S11.Ops.csproj -c Release
$exe = 'spikes/S11/out/build/S11.Ops/bin/Release/net10.0-windows10.0.26100.0/S11.Ops.exe'
& $exe run spikes/S11/out/fixtures spikes/S11/out/results                      # every case, both save modes, plus the assembler cases
node spikes/S11/pdfjs/inspect.mjs spikes/S11/out/results/*.pdf > spikes/S11/out/results/pdfjs.jsonl
& $exe pdfjs spikes/S11/out/results                                              # pdf.js against PDFium
python spikes/S11/report_tables.py spikes/S11/out/results/results.json           # the tables
& $exe perf spikes/S11/out/fixtures spikes/S11/out/perf.jsonl                    # section 7
& $exe corpus spikes/_shared/corpus/pdfium-resources spikes/S11/out/corpus.jsonl # sections 4.3 and 5.4
```

`spikes/S11/pdfjs/node_modules` is a junction to `spikes/S4/pdfjs/node_modules` (run `npm install` in S4's folder first, or in S11's own).
