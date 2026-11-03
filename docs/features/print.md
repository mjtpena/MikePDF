# Feature spec: printing and the §8.10 test matrix

- Status: Built and reviewed in parts: the pipeline (W2-print-core) and the print sheet (W2-print-sheet, [section 17](#17-the-print-sheet-w2-print-sheet)). ADR-0011 is accepted. Not verified: physical, PCL, PostScript, duplex and ARM64 printers, XPS (sections 14 and 17)
- Milestone: M3 (full reader); one step ([section 3.9](#39-printing-unsaved-edits)) completes in M4
- Master prompt: §2.1 (print with the Windows print dialog, page ranges, scaling, duplex), §8.10, §12.5 journey 11, §18 scenario 2, Appendix C (ADR-0011)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: engine messages `RenderBand` and `GetViewerPreferences` (schema v5), [settings-policies](settings-policies.md), [command-palette-shortcuts](command-palette-shortcuts.md)
- Related: [save-pipeline](save-pipeline.md) (printing a document with unsaved edits)

Conventions are in [README.md](README.md).

## 1. Constraints from the architecture

- PDFium runs in a sandboxed engine with no access to printers, no `HDC` and no UI. **The engine can only produce bitmaps.** So every print path is "render bands to bitmaps in the engine, hand them to the broker, and the broker sends them to the printer". A vector print path (`FPDF_RenderPage` to a printer DC) is not available without weakening the sandbox. This matters for text sharpness and file size and is the reason section 3.1 asks for a measured decision.
- Master §8.10 says "render at printer DPI through the engine, banded to cap memory", which agrees.
- The UI process never parses PDF bytes. Printing does not change that: the broker receives pixels only.

## 2. User stories

1. As a contract reviewer, I print pages 3 to 7 double-sided and they come out the right way round (§18 scenario 2).
2. As an accountant, I print a 600-page report without the app or the machine running out of memory.
3. As an office worker, I print a form with my filled-in values and without the blue field highlight.
4. As a reviewer, I choose whether my comments and highlights are printed.
5. As a user with an awkward printer, I turn on "print as image" and it prints.
6. As someone with a document that forbids printing, I am told why the command is off.
7. As a user, I can cancel a big job and the printer stops being fed.

## 3. Detailed behaviour

### 3.1 Decision needed first: ADR-0011

§8.10 leaves "WinUI print integration or Windows print APIs" to an ADR. Nothing in `docs/adr/` covers it. **This spec does not choose.** It states what any choice must deliver, so the ADR can be judged against it, and it keeps everything that does not depend on the choice separate.

| Candidate | What it is | Known concerns (all to be tested, none measured) |
|---|---|---|
| A. XAML print pipeline | `PrintManager` shown for the window through `IPrintManagerInterop`, `PrintDocument` with `UIElement` pages | The modern print flyout with preview and custom options. Pages are XAML elements, so each page is an image element; memory per page at 600 dpi is large unless the bitmap is band-sized. Needs COM interop that works under Native AOT (S5 found classic `[ComImport]` fails there; source-generated COM is needed). |
| B. Win32 print dialog and GDI | `PrintDlgExW` for settings, then `CreateDC`/`StartDoc`/`StartPage` and `StretchDIBits` per band | Classic dialog, no preview; no UWP dependency; direct control of banding and DPI; per-page `ResetDC` for orientation or paper changes. Drivers differ in how they handle many band blits. |
| C. Print through an intermediate XPS or PDF document | Build a temporary document and hand it to the system | Extra encode step; needs an XPS or PDF writer for bitmaps; risks double rasterisation. |

**Criteria for the ADR** (each must be answered with a measurement or a link, not an opinion): preview support; how page ranges, copies, collate and duplex reach the driver; whether printer DPI can be requested; peak memory at 600 dpi for a 1,000-page file; ARM64 behaviour with class drivers; Native AOT compatibility; dialog accessibility with Narrator; effort. **Spike S12** (one WP): print one page and a 50-page file through each of A and B to Microsoft Print to PDF and record the criteria. Until S12 reports, section 3.2 onwards is the behaviour contract and the implementation WPs that touch the dialog wait.

### 3.2 Options (the same set under any candidate)

| Option | Values | Notes |
|---|---|---|
| Printer, copies, collate, duplex, colour or grey, paper size, orientation | from the system dialog | Duplex and copies: the document's `/ViewerPreferences` values (`/NumCopies`, `/Duplex`) are used as **defaults** for the dialog, never forced. |
| Pages | All, Current page, Pages (range text), Selected pages in the sidebar | See 3.3. |
| Subset | All pages in range, Odd only, Even only | Applied after the range. |
| Scaling | Fit to printable area, Shrink oversized pages only, Actual size (100%), Custom (10% to 400%) | Default: Shrink oversized only. If the document says `/PrintScaling /None`, the default is Actual size. |
| Auto-rotate and centre | on / off (default on) | When on, a landscape page on a portrait sheet is rotated 90 degrees (and the reverse), then centred. |
| Annotations | Document only / Document and markup (default) | Maps to the annotation flag; PDF annotations without the Print flag never print. |
| Form fields | Print field contents (default) / Print form fields without contents? | OPEN QUESTION OQ-PRINT-4: is a second form option needed? Default behaviour: field values print, the editing highlight never prints. |
| Print as image | off (default) / on | See 3.6. |
| Resolution | Printer's reported resolution, capped (3.6) | Not shown to the user except in the image fallback. |

Settings persist the last-used values for scaling, annotations, forms and auto-rotate across runs. A policy can disable printing ([settings-policies](settings-policies.md): `PrintingEnabled`).

### 3.3 Page ranges

Grammar (ASCII, whitespace ignored): `list = item { "," item }`, `item = n | n "-" n | n "-" | "-" n`. Pages are 1-based physical pages. `5-` means page 5 to the last; `-5` means 1 to 5. A range with start greater than end is an error (no silent reversal). Out-of-range numbers are an error that names the limit ("The document has 40 pages."). Duplicates are removed, order is ascending (Windows prints a range in document order; arbitrary order such as `5,1` is not supported and is rejected with an explanation). At most 4,096 items and 256 characters. Page labels (`/PageLabels`) are not used in v1 (OPEN QUESTION OQ-PRINT-5: Acrobat uses labels in the print dialog).

### 3.4 Geometry

- The printed area of a page is its **displayed box**: CropBox clipped to MediaBox, then `/Rotate` applied (the same `PageGeometry` used by the viewer). User view rotation is **not** applied.
- The printable area of the paper comes from the device (`HORZRES`/`VERTRES` and physical offsets, or the equivalent in the chosen path). The scaled page is centred in it. Parts that would fall outside the printable area are clipped, and with Actual size and a page larger than the paper the job shows a notice before starting ("Some pages are larger than the paper and will be cut off.").
- Scale arithmetic lives in `MikePDF.Core.Printing.PrintLayout` (pure functions): inputs are page points, paper printable size in device pixels and DPI, mode, auto-rotate; outputs are the page's device-pixel rectangle and rotation. It is unit-tested for the four rotations, non-zero CropBox origins, pages of 1 pt and 14,400 pt, and DPI 72 to 1200.

### 3.5 Banded rendering

- For each printed page the broker plans bands: `bandHeight = floor(bandBytes / (4 * pageWidthPx))` rows, with `bandBytes` default **16 MiB** (proposed, and below the slot ring's 64 MiB limit). Bands tile the page exactly: no gap, no overlap, last band short. A 600 dpi A4 page (4,960 x 7,016 px) is about 6 bands.
- The broker requests `RenderBand` for band *k+1* while sending band *k* to the printer, so at most 2 bands are in broker memory and 1 in the engine. Peak memory for a job is independent of the page count.
- `RenderBand` renders with PDFium's printing flag (no form-field highlight, no screen-only annotations), no LCD text, and the annotation and form flags from the options. One engine renders; a large job can use the shared pool engine so the document engine stays responsive. A job runs while the user continues reading.
- Cancellation: the cancel button calls `CancelBefore` and ends the spool job (`AbortDoc` or the equivalent in the chosen path). Within 1 second no engine render is running for the job and the printer queue holds no partial job from us.

### 3.6 Print as image (the fallback)

Because every path is already raster, the fallback differs only in **how** the bitmap is sent: instead of bands at the printer's native resolution, each page is rendered as **one bitmap** at `min(printer DPI, 300)` (proposed) and sent as a single image. This avoids drivers that mishandle many small blits, and it makes output size predictable. OPEN QUESTION OQ-PRINT-1: confirm this reading of "print as image fallback for problem printers" (§8.10). If the intent was a vector path as the normal path and raster as the fallback, ADR-0011 has to say how a vector path is possible under the sandbox.

### 3.7 Permissions and encryption

From `GetDocumentInfo.permissions` (PDF bits, bit 3 = value 4, bit 12 = value 2048):

| Bit 3 (print) | Bit 12 (high quality) | Behaviour |
|---|---|---|
| clear | any | Print command disabled. Tooltip and palette reason: "The document's owner doesn't allow printing." |
| set | set | Normal. |
| set | clear | Allowed at **low quality**: resolution capped at 150 dpi, a notice in the dialog. |

An owner password cannot be entered in v1 (we open with a user password only). OPEN QUESTION OQ-PRINT-6: is capping to 150 dpi the right reading of "low quality"? It is the common reader behaviour; the spec does not define a number.

### 3.8 Progress, errors and completion

Printing is non-blocking. A progress strip (design spec) shows "Printing page {n} of {total}" with Cancel. It updates once per page, not per band. On completion a polite announcement; on failure a banner with the reason and Retry. No toast notification in v1.

### 3.9 Printing unsaved edits

Until M4 there are no edits, and printing renders the file directly. From M4, annotations MikePDF has created but not saved live in the broker's edit model, and the engine's page may not contain them ([annotation-core](annotation-core.md) section 3.3). To make print match what a saved file would show in other readers, **printing a modified document first builds a snapshot**: it runs the save pipeline stages 1 to 4 ([save-pipeline](save-pipeline.md)) into a temporary file in `LocalState\private\print`, opens that in an engine, prints from it, and deletes it. The original file and the edit journal are untouched. OPEN QUESTION OQ-PRINT-3: this adds a save-sized delay for large documents; the alternative (drawing pending annotations over bands in the broker) is a second renderer and is rejected here because it diverges from saved-file output.

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Print | Ctrl+P | `file.print` |
| Print current page | none | `file.printCurrentPage` |
| Cancel print job | Esc while the progress strip has focus | `print.cancel` |

Pen and touch: no special behaviour. The dialog is the system's, or standard controls in the options pane.

## 5. Accessibility

- The system print dialog is accessible by the OS. If candidate A is chosen, the flyout is system UI; if B, the classic dialog. Neither is ours to fix; ADR-0011 must record a Narrator check.
- Our options pane (annotations, forms, image fallback, scaling, range) uses standard controls with names and the same tab order as the visual order. The range box has an accessible description with the grammar ("For example 1-3, 5, 8-") and shows errors in a live region and with `AutomationProperties.HelpText`.
- Progress: a UIA `ProgressBar` named "Printing {document}" with value and range, plus a polite live region announcing "Printing page {n} of {total}" per page and "Printing finished" or the failure message at the end. Cancel is a normal button with Esc as accelerator.
- Disabled Print has a visible and programmatic reason (palette, tooltip, `HelpText`).
- Text scaling to 225% and high contrast apply to the options pane.

## 6. Error states

| Situation | Behaviour |
|---|---|
| No printers installed | Dialog says so (system); Print to PDF is normally present. |
| Printer offline, out of paper, spooler stopped | Spooler or driver error surfaced as a banner with Retry; the job is aborted by us if it did not start. We do not poll the printer. |
| Invalid range | Inline error; Print stays disabled. |
| Engine crash mid-job | The supervisor restarts the engine; the job resumes at the page it was on (the broker keeps the page cursor). After 3 restarts the job fails with a message. |
| `RenderBand` times out | Per-band deadline of 30 seconds (proposed); the page is retried once at the same size, then the job fails. |
| Disk full for the snapshot | Message with Save As alternative; nothing printed. |
| Page partially rendered (`PartiallyRendered`) | Prints what rendered and adds the page to a "pages printed with missing content: 4, 9" notice at the end. |
| Printing disabled by policy or permission | Command disabled with reason. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** section 3.4; fixtures include `/Rotate 90/180/270` and a CropBox whose origin is not (0,0).
- **Mixed page sizes and orientations:** each page is placed independently; auto-rotate decides per page. Per-page paper size changes (a poster page in a letter document) are driver-specific and **not** supported in v1.
- **Huge pages:** a 14,400 pt page on A4 is scaled by about 1.5%; the band planner uses the scaled size, not the source size, so memory stays bounded.
- **Poster/tiling:** not in v1.
- **Booklet, N-up:** not in §8.10, not in v1.
- **Encrypted documents:** the user password was already given; permissions as 3.7.
- **Huge documents:** 5,000 pages of 300 dpi scans. The engine cost is the JBIG2/JPX decode; banding keeps memory flat. The band deadline scales with page cost by retry only, not by guess.
- **Transparency and blend modes at print:** rendered by PDFium, flattened in the bitmap.
- **ARM64:** physical printers may have no ARM64 driver. Inbox class drivers (Microsoft PCL6 and PS class drivers) are the supported baseline for the matrix on ARM64. Documented risk.
- **Hostile input:** page sizes, `/NumCopies`, `/PrintPageRange` come from the file and are validated by the reply checks; `/NumCopies` is a default only and clamped to 1 to 99; ranges from the document are ignored if invalid.
- **File name in the job:** the spool job name is the document title or file name. Windows shows it in the queue. That is expected by users and is noted in the privacy policy.

## 8. Undo and redo

Not applicable. Printing does not change the document.

## 9. Journal entries

None. A snapshot file is not a journal. If the app is killed during a snapshot, the next start deletes every file in `LocalState\private\print` older than the current process (the directory is only for this purpose).

## 10. Save behaviour

Printing never saves, never replaces the original and never clears the journal. The snapshot of section 3.9 is built with the save pipeline's stages 1 to 4 only (no validation replace, no `ReplaceFileW`).

## 11. Engine and protocol additions (schema v5, implemented in `m3/engine`: the schema has the limits below, the engine and the broker check them)

All engine messages take `documentId` (required) unless noted. Frames are at most 64 KiB; nothing here approaches that.

**`GetViewerPreferences` (id 28; 26 became `GetDocumentDetails` in schema 4).** Request: no fields. Reply:

| Key | Name | Type | Limits |
|---|---|---|---|
| 0 | `direction` | u16 | max 2 (0 left-to-right, 1 right-to-left, 2 absent) |
| 1 | `printScaling` | u16 | max 2 (0 absent, 1 none, 2 app default) |
| 2 | `numCopies` | u16 | max 99 (0 = absent) |
| 3 | `duplex` | u16 | max 3 (0 absent, 1 simplex, 2 flip short edge, 3 flip long edge) |
| 4 | `rangeFirsts` | array of u32 | maxCount 64, max 9999999 |
| 5 | `rangeLasts` | array of u32 | maxCount 64, max 9999999, `sameLengthAs` `rangeFirsts` |

PDFium has `FPDF_VIEWERREF_GetPrintScaling`, `GetNumCopies`, `GetPrintPageRange`, `GetDuplex` and `GetName` (for named values such as `Direction`) in the pinned `fpdfview.h` (checked 2026-11-02). Their return conventions are for the engine work package to confirm.

**`RenderBand` (id 34).** The broker's `RenderTile` is limited to 1,024 px; print bands are wider.

| Key | Request field | Type | Limits |
|---|---|---|---|
| 0 | `page` | u32 | max 9999999 |
| 1 | `dpi` | u32 | 72 to 1200 |
| 2 | `rotation` | u16 | max 3 |
| 3 | `pageWidthPx` | u32 | 1 to 65536 |
| 4 | `pageHeightPx` | u32 | 1 to 65536 |
| 5 | `bandTop` | u32 | max 65535 |
| 6 | `bandHeight` | u32 | 1 to 16384; and `pageWidthPx x bandHeight x 4` must not exceed the slot size (checked by the engine and the broker) |
| 7 | `flags` | u32 | max 7: bit 0 annotations, bit 1 form fields, bit 2 printing mode. Bit 3 is reserved and the schema refuses it (it said max 15 and left the engine to refuse bit 3; PRINT-05 wants the decoder to) |
| 8 | `slotIndex` | u32 | max 1023 |
| 9 | `generation` | u64 | |

Reply: `slotIndex`, `width` (= `pageWidthPx`), `height` (= `bandHeight` or the short last band), `stride` (= `4 x width`), `generation`, with the same bounds as `RenderTileReply` extended to 65536 for width. `ReplyChecks` verifies that width, height and stride equal the request, that the band lies inside the slot, and that the generation matches.

Slot size: print jobs use a dedicated slot ring whose slots are at least 16 MiB; `AttachSlotRing` already allows up to 64 MiB. A print job asks `EngineHost` for a `PrintOptions` ring of 3 slots.

Why a new message and not a larger `RenderTile`: the tile limits (1,024 px, `scale`) protect the viewing path from absurd requests; a separate message with its own limits and its own fuzz target keeps that guarantee.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Print_Title` | Print |
| `Print_Range_All` | All pages |
| `Print_Range_Current` | Current page |
| `Print_Range_Pages` | Pages |
| `Print_Range_Hint` | For example 1-3, 5, 8- |
| `Print_Range_Error_Syntax` | Enter page numbers like 1-3, 5, 8- |
| `Print_Range_Error_Limit` | The document has {0} pages. |
| `Print_Range_Error_Order` | Pages must be in order, from lowest to highest. |
| `Print_Subset_All` / `_Odd` / `_Even` | All pages in range / Odd pages only / Even pages only |
| `Print_Scale_Fit` / `_Shrink` / `_Actual` / `_Custom` | Fit to printable area / Shrink large pages only / Actual size / Custom scale |
| `Print_AutoRotate` | Rotate and centre each page |
| `Print_Annotations` | Print comments and markup |
| `Print_FormFields` | Print form field contents |
| `Print_AsImage` | Print as image (for printers that have trouble) |
| `Print_CutOff_Notice` | Some pages are larger than the paper and will be cut off. |
| `Print_LowQuality_Notice` | The document's owner allows low-quality printing only. |
| `Print_Disabled_Owner` | The document's owner doesn't allow printing. |
| `Print_Disabled_Policy` | Printing is turned off by your organisation. |
| `Print_Progress_Name` | Printing {0} |
| `Print_Progress_Page` | Printing page {0} of {1} |
| `Print_Done_Announce` | Printing finished |
| `Print_Failed` | Printing stopped: {0} |
| `Print_Cancel` | Cancel printing |
| `Print_Missing_Content` | Some content couldn't be printed on pages {0}. |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-PRINT-1 | Meaning of "print as image fallback" (3.6). | Opus |
| OQ-PRINT-2 | ADR-0011 (candidate A, B or C) after spike S12. | Opus |
| OQ-PRINT-3 | Snapshot before printing a modified document (3.9) versus another approach. | Opus |
| OQ-PRINT-4 | A second form option ("print form fields without contents")? | User (product) |
| OQ-PRINT-5 | Use `/PageLabels` in the range box? | User (product) |
| OQ-PRINT-6 | Meaning of low-quality printing (150 dpi cap). | Opus |
| OQ-PRINT-7 | Microsoft XPS Document Writer may not be installed on Windows 11 24H2 and later (**unverified here**). If absent, what stands in for "an XPS printer" in the matrix? | User (machine), Opus |
| OQ-PRINT-8 | Who installs printers for the matrix: adding a printer or driver changes system settings, which the road-to-production rules forbid agents to do. The user or IT runs a one-off setup script on the test machine. | User |
| OQ-PRINT-9 | Physical PCL and PostScript printers: which models, and who prints? Hardware is required (R-17 style blocker). | User |

## 14. The §8.10 test matrix

Cells marked **A** are automated with a printer that needs no hardware; **S** are semi-automated (printer installed once by the user; the test prints to a file and checks it); **M** are manual with a recorded checklist in `docs/test-reports/`. "Not run" is a legitimate status until the printer exists (§19.3).

| Case | Microsoft Print to PDF | XPS writer (OQ-PRINT-7) | PCL class driver to FILE | PostScript class driver to FILE | Physical PCL | Physical PostScript |
|---|---|---|---|---|---|---|
| Whole 12-page document, page count and sizes | A | A (structure) | S | S | M | M |
| Range `3-7`, `2,4`, `5-`, current page | A | A | S | S | M | M |
| Odd only / even only | A | A | S | S | M | M |
| Duplex long edge and short edge (job ticket / DEVMODE only; PDF output has no duplex) | n/a | n/a | S (DEVMODE in file) | S (`%%BeginFeature` / setpagedevice) | M | M |
| Scaling: fit, shrink, actual, custom 50% | A | A | S | S | M | M |
| Auto-rotate, landscape pages in portrait job | A | A | S | S | M | M |
| `/Rotate` 90/180/270 and CropBox offset fixtures | A | A | S | S | M | M |
| Annotations on / off (M4 fixtures) | A | A | S | S | M | M |
| Form fields on / off; no highlight tint | A | A | S | S | M | M |
| Print as image | A | A | S | S | M | M |
| 1,000-page document: peak broker and engine memory flat | A | A | S | S | M | M |
| Cancel mid-job: spool empty, engine idle < 1 s | A | A | S | S | M | M |
| Printer offline / spooler stopped error path | A (stop spooler: needs admin, so M) | M | M | M | M | M |
| Visual match: output rasterised at 150 dpi vs source at 150 dpi, SSIM >= 0.98 (threshold **untested**) | A | M (no XPS rasteriser in the toolchain) | M | M | M | M |

How the semi-automated cells check output without hardware: install the inbox Microsoft PCL6 and PS class drivers on a **FILE:** port (a one-off, user-run setup), print, then inspect the file: PostScript page count (`%%Pages`, `showpage` count), media size and orientation features; PCL page and media commands. Pixel comparison of PCL/PS output needs a rasteriser (Ghostscript is AGPL, which `risks.md` R-26 allows only as an external test tool by ADR): OPEN QUESTION OQ-PRINT-10 asks whether to adopt it for the test machine only.

On ARM64 the matrix runs with the class drivers only.

## 15. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| PRINT-01 | Range grammar: accepts `1`, `1-3`, `5-`, `-5`, `1-3,5,8-`, spaces; rejects empty, `a`, `3-1`, `0`, `-`, `1,,2`, > 256 chars, > 4,096 items, numbers above the page count; each error carries the right resource key. | Unit (`MikePDF.Core.Tests`) |
| PRINT-02 | Expansion: ranges become ascending de-duplicated lists; odd/even filtering applies after the range; `5-` on a 10-page document gives 5..10. | Unit |
| PRINT-03 | `PrintLayout`: fit, shrink-only, actual and custom scales produce the expected rectangles for page sizes 1 pt to 14,400 pt, DPI 72 to 1200, the four rotations, with and without auto-rotate, and a non-zero-origin CropBox. Property test: the placed page never exceeds the printable area in fit and shrink modes. | Unit |
| PRINT-04 | Band planner: bands tile each page with no gap or overlap, none exceeds the byte cap, the sum of heights equals the page height, for 400 random page/DPI combinations. | Unit |
| PRINT-05 | `RenderBand` golden vectors and malformed frames (every limit in section 11 exceeded by one, reserved flag bit set, size product above the slot) round-trip in C# and C++ and are rejected with `InvalidArgument`. | Unit (Protocol + Engine C++) |
| PRINT-06 | Concatenating the bands of a page matches one full-page render at the same DPI for the print fixture set at 150 and 600 dpi: at most 0.3% of the pixels differ, none of them within 1 pixel of a band boundary, and solid content is identical. (Amended 2026-11-03 by Opus: PDFium puts the band offset into a float matrix, so anti-aliased edges on a half pixel round differently.) | Integration |
| PRINT-07 | `ReplyChecks` rejects a band reply whose width, height, stride, slot or generation differs from the request. | Unit |
| PRINT-08 | `GetViewerPreferences` returns the values in a fixture with `/Direction`, `/PrintScaling`, `/NumCopies`, `/Duplex`, `/PrintPageRange`, and `absent` values for a plain file; hostile values (negative, huge, wrong types) give clamped or absent values. | Integration + Unit |
| PRINT-09 | Permission matrix: bit 3 clear disables Print with the owner reason; bit 3 set and bit 12 clear caps `dpi` at 150; both set is normal. | Unit + Integration |
| PRINT-10 | Printing 1,000 pages to Microsoft Print to PDF keeps engine peak private bytes and broker peak private bytes within 5% of a 10-page job's peaks (flat memory). | Integration (perf) |
| PRINT-11 | Cancel mid-job: within 1 s no render is in flight for the job, the spool job is gone (queue query), and the next print works. | Integration |
| PRINT-12 | Engine killed mid-job: the job finishes with all pages and no duplicates; after 3 kills it fails with the message. | Integration |
| PRINT-13 | Journey 11 (§12.5): print a fixture to Microsoft Print to PDF; the output has the expected page count and page sizes, and each page matches the source render at 150 dpi with SSIM >= 0.98 (threshold unproven; recorded as the target). | UI journey |
| PRINT-14 | Annotation flag: a fixture with a Print-flag annotation and a no-print annotation prints the first only when annotations are on, neither when off. | Integration |
| PRINT-15 | Forms flag: a filled form prints values and no field highlight tint (pixel test of a field background region). | Integration |
| PRINT-16 | Print as image sends one bitmap per page at <= 300 dpi; output page count and sizes are unchanged. | Integration |
| PRINT-17 | Rotated and cropped fixtures print with the right orientation and content placement (compare to the viewer render under the same transform). | Integration |
| PRINT-18 | Mixed portrait and landscape: auto-rotate on rotates exactly the pages whose orientation differs from the paper; off rotates none. | Unit + Integration |
| PRINT-19 | Printing a modified document (M4) prints annotation content that exists only in the edit model, via the snapshot, and leaves the original file bytes and the journal unchanged. | Integration (after M4) |
| PRINT-20 | Snapshot directory is empty after a job, and stale files from a killed run are removed at next start. | Integration |
| PRINT-21 | Accessibility: the options pane and progress strip have names, roles and tab order; progress and completion strings are raised once per page and once at the end (UIA event count). | UI journey (a11y) |
| PRINT-22 | Disabled Print exposes its reason through UIA `HelpText` and the palette. | UI journey |
| PRINT-23 | Test matrix cells marked A pass. Cells S and M are recorded in `docs/test-reports/` as pass, fail or **not run**, never omitted. | Integration + manual record |
| PRINT-24 | A hostile document (page size 14,400 pt, `/NumCopies 2147483647`, `/PrintPageRange` junk) cannot cause allocation beyond the band cap or more than 99 copies. | Integration |
| PRINT-25 | Fuzz target for `RenderBand` request decoding exists for both codecs and runs in the fuzz workflow. | Unit (fuzz harness build) |

## 16. Implementation status (W2-print-core, `m3/print-core`)

The pipeline behind the print sheet is built and tested; the sheet itself (design 11-print, `file.print`, the progress strip, the settings keys `print.*`, the strings of section 12) is the next work package. Code: `src/MikePDF.Core/Printing` (pure: ranges, options, geometry, band planning, N-up, copies, eligibility) and `src/MikePDF.Printing` (printers, `DevMode`, the GDI device, `PrintSession` on the engine budget, `PrintJob`). Tests: `tests/MikePDF.Core.Tests/Print*Tests.cs` and `tests/MikePDF.Integration.Tests/Printing/`.

| Id | Status | Where |
|---|---|---|
| PRINT-01, -02 | Done | `PrintRangeTests` |
| PRINT-03, -04 | Done | `PrintLayoutTests` (property tests over 2,000 and 400 random cases) |
| PRINT-05, -07, -25 | Not touched here: the schema, the codecs and the fuzz targets are wave 2's engine and fuzz packages | `tests/MikePDF.Protocol.Tests`, `EngineClientValidationTests`, `tests/fuzz` |
| PRINT-06 | Amended criterion (see 274): `PrintContentTests` assert at most 0.3% differing pixels and none at a boundary. Measured: about 0.1%, on text, hairline and rectangle edges | `PrintContentTests` |
| PRINT-08 | Engine reply as before; the defaults taken from it are clamped and junk is ignored | `PrintPolicyTests`, `PrintContentTests.HostilePrintPreferences...` |
| PRINT-09 | Done, in the pipeline as well as in `PrintEligibility` | `PrintPolicyTests`, `PrintJobTests`, `PrintSessionTests` |
| PRINT-10 | **Reduced test passes; the full 1,000 page run (2026-11-04) fails the 5% bound**: broker +34%, engine 3.2 times (17.3) | `PrintRecoveryTests`, `PrintThousandPageTests` |
| PRINT-11, -12 | Done, with a scripted engine and with the real one (killed twice, and three times) and the real PDF queue | `PrintJobTests`, `PrintRecoveryTests`, `PrintToPdfTests` |
| PRINT-13 | Done at both layers. Pipeline: SSIM of each printed page against the source render, box filtered by 4 (0.99 at readable sizes; shrunk text 0.98, see the test). UI journey 11 (the sheet, Print To PDF, the file read back through the engine): range 3-5 at Fit, 0.9998 on every page; 4 pages per sheet, 0.9995 to 1.0000 in every cell of both sheets; custom 50%, 1.0000; a pseudo-RTL window, 0.9999 | `PrintToPdfTests`, `PrintJourneyTests` (`J11_*`, `RTL_*`) |
| PRINT-14 | Done | `PrintContentTests` |
| PRINT-15 | Done. No highlight ever prints, and the engine draws widgets when `RenderBand` carries flag bit 1 (PR #19): a filled field prints its appearance | `PrintContentTests` |
| PRINT-16, -17, -18 | Done | `PrintContentTests`, `PrintJobTests`, `PrintToPdfTests` |
| PRINT-19, -20 | M4 (the snapshot of a modified document) | not built |
| PRINT-21, -22 | Done by the sheet (section 17): names, roles, tab order, announcements, and the reason for a disabled Print in help text, palette, banner and a hover flyout | `PrintJourneyTests`, `PrintSheetSourceTests`, `PrintSheetTests` |
| PRINT-23 | The Print To PDF cells marked A pass on this machine; no XPS queue exists here (OQ-PRINT-7) | `PrintToPdfTests` |
| PRINT-24 | Done (pixel cap, protocol limits, 99 copies, junk `/PrintPageRange`) | `PrintJobTests`, `PrintContentTests` |

Not in v1, by this spec: booklet. N-up is offered by the sheet (section 17): 2, 4, 6, 9 and 16 pages per sheet, as decisions.md says.

## 17. The print sheet (W2-print-sheet, `m3/print-sheet`)

### 17.1 What was built

- **The sheet** (`Ctrl+P`, the **Print** button of the command bar, the palette, and **Print current page**): `src/MikePDF.App/Views/PrintSheetView.xaml` in a `ContentDialog`, driven by `MainWindow.Print.cs`. Printer, **Printer preferences...** (the driver's own dialog, `DocumentProperties` with `DM_PROMPT`; what is changed there is read back into the sheet and the whole DEVMODE rides on to the job), copies and collate, pages (all, current, a typed range with inline errors from `PageRangeParser`, odd or even), sides (only where the printer has them), orientation and auto-rotate, scale (shrink large pages, fit, actual size, custom 10 to 400%), pages per sheet with their order, paper from the driver's list, colour or black and white, comments and form contents, print as image. A summary says what will happen ("Printing 5 pages on 3 sheets, both sides").
- **The preview** (`PrintPreview`): the first sheet of the selection (a pager steps through the others), drawn by the planner the job uses at screen size from engine bands, with the paper's margins from the device, 250 ms after the last change and never blocking the controls. Notices say what the printer did with the settings (`DeviceOpenReport`): a cleared duplex or paper, pages that would be cut off, low-quality-only permission, a printer that did not answer.
- **Last-used values** in the `print.*` settings (`SettingsRegistry`, with names and descriptions; the printer and the paper are device state and are not exported). Copies and pages are not remembered. A document's `/ViewerPreferences` (`GetViewerPreferences`, clamped as the pipeline does: copies 1 to 99, junk ranges ignored) win over the remembered scale, copies, sides and pages for what they say.
- **The job and its strip**: an `InfoBar` over the document area with a `ProgressBar` named "Printing {document}", the page count (one UIA notification per sheet and one at the end), **Cancel** (and `Esc` while the strip has focus; command `print.cancel`), the plain-language end state (sent, stopped after page n, or why not, with **Try again** and **Choose another printer**) and, for a failure, the printer's or engine's own words on a second line. The document stays readable; one job at a time per window; closing the window mid-job asks first. `PrintFailure` sorts a Win32 error or engine status into offline, access denied, spooler, engine failed (three crashes), page too slow, output unwritable, or the person closing the system's own save box.
- **Disabled Print** (PRINT-22): `file.print` is off for no document, policy (`PrintingEnabled`), then the owner's permission (bit 3), then a running job, each with its own reason string. The reason is the UIA `HelpText`, the palette row, the banner `Ctrl+P` shows, and a hover flyout (WinUI shows no tooltip for a disabled control, so the command bar looks at the cursor every 150 ms while the window is active and shows the tooltip's words in a small flyout).
- **Test seam** `MIKEPDF_TEST_PRINT_OUTPUT` (a folder; the job for Microsoft Print to PDF is written there as `print-<n>.pdf`, so no Save dialog appears), read only through `TestSeamGate`.

### 17.2 Criteria to tests

| Id | Test |
|---|---|
| PRINT-10 | `PrintThousandPageTests` (opt-in: `MIKEPDF_PRINT_1000=1`), the full criterion: **run on 2026-11-04 and it fails the 5% bound** (17.3); `PrintRecoveryTests` is the reduced one that always runs and passes |
| PRINT-11 | `PrintJourneyTests.PRINT11_CancelStopsTheJobAtOnceLeavesNoFileOrSpoolJobAndTheNextPrintWorks` (through the UI: 94 to 390 ms from pressing Cancel to the strip saying it stopped, see 17.3), `PrintToPdfTests` and `PrintJobTests` (the pipeline) |
| PRINT-13 | `PrintJourneyTests.J11_*` and `RTL_*` (section 16, PRINT-13) |
| PRINT-21 | `PrintJourneyTests.PRINT21_*` (names, control types, the tab order of 18 stops, the progress bar, exactly five notifications for a four sheet job), `TheWholeJobWorksFromTheKeyboardAlone` (`Ctrl+P`, `Tab` to the range, `3-7`, `Enter`), `ANarrowWindow...` (stacked and hidden preview), `PrintSheetSourceTests` (system colours only, every automation id, document order, live regions) |
| PRINT-22 | `PrintJourneyTests.PRINT22_*` (owner, with and without the owner password; policy through a throwaway HKCU key, off and back on), `PrintSheetTests` (the rule, every reason, the palette row) |
| Also touched | the range grammar inline (`ARangeThatIsNotValid...`), settings persistence and the settings file holding no paths (`TheLastUsedOptions...`, `PrintSheetTests`), the driver's own preferences dialog (`ThePrintersOwnPreferences...`), the preview (`PrintSheetPipelineTests`, `PrintPreviewEngineTests`: bands, margins, N-up order, grey scale, flags, annotations and form fields with the real engine), failure kinds (`PrintSheetTests`), `TestSeamGateTests` |

### 17.3 Measurements

Taken on the development machine (Windows 11, Microsoft Print to PDF, no physical printer). The pipeline numbers are from `build/out/print-evidence.txt`, the UI numbers from the journeys' own evidence file `build/out/print-ui-evidence.txt`; neither file is committed.

| What | Result | Run |
|---|---|---|
| PRINT-10, 10 pages | broker (test host) peak 163.8 MiB, engine 4.8 MiB, spooler side 150.8 MiB, 17 s, output 27.5 MiB | `PrintThousandPageTests`, 2026-11-04 |
| PRINT-10, **1,000 pages** | broker peak **220.2 MiB (+34%)**, engine peak **15.3 MiB (3.2 times)**, spooler side 2,261 MiB, 1,428 s (1.43 s per page), output 2,230 MiB. **The test fails its 5% bound for both processes** | same run, 24 min |
| PRINT-10, reduced (always runs) | broker 52.9 MiB at 10 pages and 57.4 MiB at 60 (passes with its allowance); engine 4.8 and 5.1 MiB | `PrintRecoveryTests` |
| Cancel, pipeline | 26 ms from the cancel to the end of the job, 2 sheets printed | `PrintToPdfTests` |
| Cancel, through the sheet | 94 to 390 ms over seven runs from pressing Cancel to the strip saying it stopped (the last four: "Stopped after page 2."), all under the 1 s of PRINT-11 | `PRINT11_*`, 2026-11-03, previous work package's runs |
| PRINT-13, SSIM against the source render | pipeline 0.98 to 0.996; through the sheet: range 3-5 at Fit 0.9998 on every page, 4 pages per sheet 0.9995 to 1.0000, custom 50% 1.0000, pseudo-RTL 0.9999 | `PrintToPdfTests`, `J11_*`, `RTL_*` |
| PRINT-21, notifications | five for a four sheet job: pages 1 to 4 and "Sent to Microsoft Print to PDF." | `PRINT21_*`, 2026-11-03 |

**The PRINT-10 result is not explained yet.** The growth is real in the full run and the criterion says "within 5% of a 10-page job's peaks". Two readings, neither tested: (1) part of it is the document itself (the engine holds a 1,000 page file, about 10 KB a page; the test host holds the fixture and a longer-running .NET heap that is not collected until it must be), in which case the criterion should say "no growth from the bands or the pages already printed" and be measured after the document is loaded; (2) part of it is something the job keeps for each page (a retained bitmap, a handle, a pending item), in which case it is a leak of about 56 KB a page in the broker. The reduced test (10 to 60 pages, +8% at the broker with its allowance) cannot tell the two apart. ADR-0011 point 4 says page-count independence "stands for MikePDF's own processes"; this run does not support that as worded. Left for the reviewer (decisions list in the completion note); the criterion and the test were not changed.

### 17.4 Not done, not verified

- **XPS (OQ-PRINT-7): not run.** This machine has no XPS Document Writer: its printer drivers are Microsoft Print To PDF, the Virtual Print Class Driver, the IPP and Universal Print class drivers and OneNote's. Adding the driver or a queue changes the machine's printer configuration and needs elevation (the session is at medium integrity), which the rules forbid.
- Physical, PCL and PostScript printers, duplex reaching a driver, collate by a driver, and ARM64 printing are unchanged from section 16: unverified. The sheet reads what a driver says (`DeviceCapabilities`, the merged DEVMODE) and shows what it did not apply, but no duplex printer was available to try it.
- High contrast and 225% text scale were not run: both are system settings the rules do not allow an agent to change. What can be checked without them is: the sheet and the strip use theme resources only (`PrintSheetSourceTests`), the layout stacks at 640 px and hides the preview behind a toggle at 480 px (`ANarrowWindow...`), and the options scroll inside a fixed-height dialog with the buttons pinned. The Narrator pass is manual.
- The **Open printer settings** button starts `ms-settings:printers` through the shell; not verified on other Windows versions, and not run here (it would open the Settings app).
- **Selected pages** (design 11-print) is not offered: the thumbnail list is single-select until the page organiser (M4).
- Printing a modified document (PRINT-19, -20) is M4's.
- A job that is running when the window closes is stopped (after asking); a print that should continue after the app exits is not supported.

### 17.5 Where the design and the spec disagreed (the spec was followed)

See the table in [design 11-print, section 10](../design/screens/11-print.md): low-resolution permission (a notice and a 150 dpi cap, Print as image stays the person's choice), the cadence of announcements (once per page), the default scale (shrink large pages only, where the design said fit), and the error actions (the union of the spec's Retry and the design's Try again and Choose another printer).
