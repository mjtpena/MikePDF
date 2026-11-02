# Feature spec: printing and the §8.10 test matrix

- Status: Draft for Opus review. **Blocked on ADR-0011**, which is not written yet (section 3.1)
- Milestone: M3 (full reader); one step ([section 3.9](#39-printing-unsaved-edits)) completes in M4
- Master prompt: §2.1 (print with the Windows print dialog, page ranges, scaling, duplex), §8.10, §12.5 journey 11, §18 scenario 2, Appendix C (ADR-0011)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: engine messages `RenderBand` and `GetViewerPreferences` (schema v4), [settings-policies](settings-policies.md), [command-palette-shortcuts](command-palette-shortcuts.md)
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

**Criteria for the ADR** (each must be answered with a measurement or a link, not an opinion): preview support; how page ranges, copies, collate and duplex reach the driver; whether printer DPI can be requested; peak memory at 600 dpi for a 1,000-page file; ARM64 behaviour with class drivers; Native AOT compatibility; dialog accessibility with Narrator; effort. **Spike P-1** (one WP): print one page and a 50-page file through each of A and B to Microsoft Print to PDF and record the criteria. Until P-1 reports, section 3.2 onwards is the behaviour contract and the implementation WPs that touch the dialog wait.

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

## 11. Engine and protocol additions (schema v4)

All engine messages take `documentId` (required) unless noted. Frames are at most 64 KiB; nothing here approaches that.

**`GetViewerPreferences` (id 26).** Request: no fields. Reply:

| Key | Name | Type | Limits |
|---|---|---|---|
| 0 | `direction` | u16 | max 2 (0 left-to-right, 1 right-to-left, 2 absent) |
| 1 | `printScaling` | u16 | max 2 (0 absent, 1 none, 2 app default) |
| 2 | `numCopies` | u16 | max 99 (0 = absent) |
| 3 | `duplex` | u16 | max 3 (0 absent, 1 simplex, 2 flip short edge, 3 flip long edge) |
| 4 | `rangeFirsts` | array of u32 | maxCount 64, max 9999999 |
| 5 | `rangeLasts` | array of u32 | maxCount 64, max 9999999, `sameLengthAs` `rangeFirsts` |

PDFium has `FPDF_VIEWERREF_*` functions in `fpdf_doc.h` for these (print scaling, copies, page ranges, duplex, named values such as `Direction`); the names are to be confirmed against the pinned headers (S1 rule: do not guess APIs).

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
| 7 | `flags` | u32 | max 15: bit 0 annotations, bit 1 form fields, bit 2 printing mode, bit 3 reserved (must be 0) |
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
| OQ-PRINT-2 | ADR-0011 (candidate A, B or C) after spike P-1. | Opus |
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
| PRINT-06 | Concatenating the bands of a page equals one full-page render at the same DPI, pixel for pixel, for the print fixture set at 150 and 600 dpi. | Integration |
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
