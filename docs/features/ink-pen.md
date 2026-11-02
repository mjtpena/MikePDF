# Feature spec: ink and pen (pressure, highlighter, eraser, palm rejection, latency)

- Status: Draft for Opus review. **ADR-0007 is Proposed and S5 is incomplete**: no latency, pressure or palm-rejection number exists yet (needs a Surface Pro, a pen and a 240 fps camera, R-17)
- Milestone: M4 (the ink latency budget is an M4 exit-gate item, §17)
- Master prompt: §8.3, §11 (pen-to-pixel <= 25 ms), §2.1, §12.5 journey 8, §18 scenario 4, §12.7 (pen and touch on a Surface)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md), ADR-0007, `docs/spikes/S5.md`, a new project `MikePDF.Ink`
- Related: [shapes](shapes.md), [annotation-panel](annotation-panel.md)

Conventions are in [README.md](README.md). Shared rules are in annotation-core.

## 1. What is known and what is not

Known (S5 inventory, desk research and a headless probe on a PC without a pen):

- WinUI 3 on Windows App SDK 2.5.1 has **no ink control**. Win2D 1.4.0 has no `DrawInk`. The pipeline is MikePDF's own: pointer events, smoothing, drawing, stroke model, PDF conversion (ADR-0007, `MikePDF.Ink`).
- `PointerPointProperties` provides pressure (0 to 1), tilt, twist, barrel button, eraser/inverted, hover (`IsInRange`), `ContactRect` and `TouchConfidence`. `GetIntermediatePoints` gives coalesced points. `PointerPredictor` exists but has not run on a device.
- DirectComposition delegated ink objects can be created, but **how to attach a trail to WinUI's visual tree is unverified**. Classic `[ComImport]` fails under Native AOT; source-generated COM is required.
- S4: `/BS /W` is a single width, so **variable-width ink must live in the appearance stream**. A Q-series appearance with width-varying paths drew in PDFium and pdf.js (A07b).

Not known: pen-to-pixel latency of any candidate, how many pressure levels a Surface pen gives, whether `TouchConfidence` is enough for palm rejection, whether `PointerPredictor` helps. Every number below that depends on those is a **provisional constant** in `MikePDF.Ink` with a test, and the S5 device run sets the final values. This spec does not claim the 25 ms budget is met.

## 2. User stories

1. As a student on a Surface, I annotate a 30-slide lecture PDF with a pressure-sensitive pen and a highlighter, scroll with my finger, and undo and redo strokes without lag (§18 scenario 4).
2. As an artist, I press harder and the line gets thicker, and the saved file shows the same variation in Acrobat Reader and Edge.
3. As a note-taker, I rest my palm on the screen and it never makes a mark or scrolls.
4. As a reviewer, I flip the pen over (or hold the barrel button) and erase part of a stroke.
5. As a user whose app crashed, I get my last strokes back (journey 8).
6. As a user without a pen, I can still draw with a mouse.

## 3. Detailed behaviour

### 3.1 Tools

| Tool | Behaviour | Defaults |
|---|---|---|
| **Pen** | Variable width by pressure | colour black; base width 2 pt (range 0.5 to 24); pressure curve Linear; minimum width ratio 0.35 |
| **Highlighter** | Constant width, translucent, Multiply blend, flat-ended | colour yellow; width 14 pt (range 6 to 40); opacity 100% with Multiply |
| **Eraser** | Stroke eraser (removes a whole stroke the eraser touches) or Point eraser (removes the part under the eraser and splits the stroke) | mode Stroke; size small 3 / medium 6 / large 12 DIP radius |

Widths are in **PDF points on the page**, not screen pixels: a stroke drawn at 400% zoom is 4x finer on the page than one drawn at 100%. This makes saved width independent of zoom. A mouse draws constant width (pressure treated as 1.0).

### 3.2 Pen mode

`pen.mode` ("Pen mode"): when on, **the pen always inks** with the current pen or highlighter tool (no need to select a tool), and **touch always scrolls and pinch-zooms**, never inks. With pen mode off, a pen or mouse inks when an ink tool is selected; touch scrolls (touch inking is off unless the user turns on `pen.touchInks`, OQ-INK-5). The toolbar shows the mode, and a pen entering range while a non-ink tool is active does not change the tool (it behaves as a pointer).

### 3.3 Input pipeline

1. The page surface receives pointer events. For each move it reads all intermediate points (`GetIntermediatePoints`), keeping device timestamp, DIP position, pressure, and flags. Device type, eraser/inverted state and barrel state are read at pen-down and on each state change.
2. Points are converted to page space through `PageProjection` once, at the capture edge, so the stroke is stored in user space (rotated pages and CropBox offsets are handled by the projection).
3. **Smoothing** (`MikePDF.Ink`, pure and allocation-free in the steady state): remove points closer than 0.1 DIP; adaptive low-pass on position (speed-dependent cutoff, the "1 euro" filter family; starting constants cutoff 1.0 Hz, beta 0.007, to be tuned on the device); light smoothing of pressure (time constant 30 ms). The first and last points are never moved. Prediction: if `PointerPredictor` helps on the device, predicted points are drawn on a **separate transient layer** and discarded when real points arrive; they never enter the stroke.
4. The live stroke is drawn on a dedicated **ink layer** above the tiles (§7.1 rule 7): only the newest segments are drawn each frame into a retained surface (dirty-rectangle update), not the whole stroke. Pen-up commits the stroke to the model (and the journal, 3.9).
5. Stroke state is kept entirely in the UI process. An engine crash or restart does not interrupt or lose a stroke.

### 3.4 Palm rejection

Provisional rules (ADR-0007; to be tuned in S5):

- While the pen is **in range** (hover) or in contact, touch contacts are ignored for inking and for scrolling; they are routed to nothing.
- A touch contact is also ignored if its `TouchConfidence` is false or its `ContactRect` is larger than 12 mm in either dimension (about 45 DIP at 96 dpi, scaled by the display's reported physical size).
- After the pen leaves range, touch stays ignored for **500 ms** (a palm often lands as the pen lifts).
- In pen mode, accepted touch contacts scroll and zoom. A palm that is accepted by mistake must not produce an ink mark in any case.
- Mouse input is unaffected.

### 3.5 Barrel button and eraser end

`pen.barrelAction` and `pen.eraserEndAction` map the physical controls to a **temporary tool** held while the control is pressed: `None`, `Eraser`, `Select`, `Highlighter`, `Pen`, `Undo` (on press). Default for the eraser end: `Eraser`. The default for the barrel button is OPEN QUESTION OQ-INK-4 (Windows treats the barrel as right-click; `Select` is the proposal). A pen-down with the control held starts the temporary tool; releasing returns to the previous tool.

### 3.6 Eraser

- **Stroke eraser:** every stroke whose outline the eraser's circle touches during the gesture is removed. Strokes about to be erased are highlighted while the eraser hovers over them.
- **Point eraser:** the part of any stroke inside the eraser circle is removed; the stroke is split into pieces (each a new stroke keeping its colour, width samples and pressure).
- One eraser gesture, pen-down to pen-up, is **one** undo step regardless of how many strokes it touches.
- The eraser works on strokes of **owned** ink annotations (current session or reloaded, if stroke data is available, 3.8). It does nothing on foreign ink, which can only be deleted whole by selecting it.
- An annotation whose last stroke is erased is deleted.

### 3.7 Batching strokes into annotations (§8.3)

"Long sessions are batched into reasonable annotation objects, not one annotation per stroke."

A stroke joins the **open batch** (an owned Ink annotation) when all hold: same page, same tool kind, same colour, same base width and opacity, the previous stroke ended less than **10 seconds** earlier, and the batch has fewer than **200 strokes** and fewer than **20,000 points** (all provisional). Otherwise a new annotation is started. A batch **closes** on: a page change, a tool or style change, 10 s idle, any selection change, a save, or an undo past its first stroke.

Batches are an object-level concept only. **Undo is per stroke** (and per erase gesture), not per batch. A closed batch can still be edited by the eraser and can be selected as a whole (move, delete, colour, opacity, width scale). Resizing an ink annotation is not offered (OQ-INK-8 covers lasso and partial selection).

### 3.8 Conversion to a PDF `/Ink` annotation

For each batch:

- **`/InkList`:** one array of x,y numbers per stroke: the smoothed centreline, simplified by Douglas-Peucker with tolerance 0.1 pt. Readers that regenerate appearances use this with a constant width.
- **`/AP /N`:** per stroke, a **filled outline** of the variable-width stroke (left and right offsets by half the width at each point, round caps), expressed as a Bezier path fitted to the outline with maximum deviation 0.1 pt, filled with the non-zero winding rule. A single dot is a filled circle. This carries the pressure variation; `/BS /W` carries the base width for readers that ignore the appearance.
- **`/C`** colour, **`/CA`** opacity; for the highlighter an ExtGState with `/BM /Multiply` and the same opacity.
- **`/Rect`** is the union of the outlines. (PDFium's `AddInkStroke` grew the rectangle by the border width, S4 F6; MikePDF computes its own and reads back whatever it finds.)
- **Pressure data for re-editing.** `/InkList` has no pressure. To erase part of a stroke or restyle width in a later session, the per-point widths are needed. OPEN QUESTION OQ-INK-3: store them in a private dictionary entry (second-class name with a MikePDF prefix) inside the annotation, ignored by other readers, or accept that reloaded strokes have constant width. The proposal is the private entry, capped at 20,000 samples; `GetInkStroke.widths` carries it to the model.
- Size guard: an annotation's appearance is capped at 2 MB; the batch limits above keep it far below that for normal use. Typical size after fitting is expected to be a few tens of bytes per input point; this is **not measured**.

### 3.9 Journal and crash recovery

Each committed stroke is journaled at pen-up as `AddInkStroke` ([save-pipeline](save-pipeline.md) section 3.4). §7.4 says an entry is flushed before the command is acknowledged. A synchronous flush at pen-up would put an SSD flush (about 1 to 10 ms, more on a busy disk) on the path of the next stroke's first sample. OPEN QUESTION OQ-INK-7 proposes: the stroke is committed to the model and visible immediately; the journal write is queued to a single ordered writer and flushed within **50 ms**; at most the last stroke is at risk on a hard crash. This is a deliberate relaxation of §7.4 for ink only and needs Opus's decision.

### 3.10 Highlighter blending on screen

The saved file draws highlighter strokes with Multiply. For the screen to match (AC-12), the ink layer must multiply with the page tiles beneath it, which are drawn by a different Win2D surface. Whether that is possible with a composition blend over the tile surface, or requires drawing highlighter strokes in the same drawing session as the tiles, is not verified. OPEN QUESTION OQ-INK-6; the fallback is normal alpha blending at a lower opacity on screen, with a documented difference. Spike S9 (display strategy) covers it.

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Pen / Highlighter / Eraser tools | none (OQ-CMD-3) | `tool.pen`, `tool.highlighter`, `tool.eraser` |
| Toggle pen mode | none | `pen.toggleMode` |
| Undo / redo | Ctrl+Z / Ctrl+Y | `edit.undo`, `edit.redo` |
| Delete selected ink | Delete | `edit.delete` |
| Tool size up / down | none | `pen.widthUp`, `pen.widthDown` |

**Ink is a freehand path-based input and has no keyboard equivalent** (an essential exception to WCAG 2.5.1). Keyboard-only users get the other tools (shapes, free text, notes, highlighting) and can set `Contents` text on any drawing. A keyboard user can still select, move, recolour and delete existing ink. This limitation is stated in the accessibility statement.

Pen: pressure, barrel button, eraser end, hover cursor (a dot the size of the stroke at the current zoom). Touch: scroll and pinch in pen mode; no inking by default. Mouse: constant-width ink.

## 5. Accessibility

- UIA for an ink annotation: localized control type "drawing", name "drawing by {author}, {n} strokes", `HelpText` with date and page, `Value` pattern for the alternative text (`/Contents`), which the properties pane offers ("Describe this drawing"). The panel lists it with the same text.
- **No per-stroke announcements** (they would flood a screen reader that is also reading the pen's hover). One polite announcement when a batch opens ("Drawing started on page 4") and one when inking pauses for 3 seconds ("5 strokes added"). Erasing announces once per gesture ("Erased 2 strokes"). Undo and redo announce as in annotation-core.
- Tool state (pen mode, tool, colour, width) is exposed as named controls with states, not only as colours.
- Pen hover cursor and selection outlines have 3:1 contrast and use system colours in high contrast. User ink colours are the user's own.
- Reduced motion: no animated ink effects exist.
- Text scaling to 225% affects the toolbar only.

## 6. Error states

| Situation | Behaviour |
|---|---|
| Stroke too long (> 20,000 points) | Split into consecutive strokes, joined visually; one undo step per original gesture. |
| Pointer capture lost (window deactivated, alt-tab, tab switch) mid-stroke | The stroke is committed as it stands. |
| Journal write queue behind by > 500 ms | Persistent chip "Changes can't be backed up right now" ([save-pipeline](save-pipeline.md)). |
| Permission bit 6 clear | Ink tools disabled with the owner reason. |
| Stroke begins outside any page (gap or margin) | Ignored (no annotation). |
| No pen hardware and pen mode on | Pen mode is available; mouse inks; touch scrolls. |

## 7. Edge cases

- **Rotated pages, CropBox offsets, facing and mixed page sizes:** strokes are stored in user space of the page under the pen-down point; a stroke that leaves that page's displayed box is **clipped** to it (the part outside is dropped; re-entering does not resume).
- **High DPI and mixed-DPI monitors:** coordinates are DIPs at capture; per-monitor DPI changes mid-stroke do not distort the stroke.
- **Different pen digitisers:** pressure may be quantised (S5 did not measure how coarsely); smoothing must not produce stepped widths: width changes are interpolated and limited to a maximum slope.
- **Zero-pressure samples at stroke start and end:** clamped to the minimum width ratio, so lines never taper to nothing unless the user chooses a taper (no taper in v1).
- **Very fast strokes and low sample rates:** gaps larger than 3 DIP between consecutive samples are bridged by the curve fit, not by straight lines through long jumps.
- **Zooming or scrolling while a stroke is active:** the stroke stays in page space.
- **Encrypted or signed documents; documents above 1 GiB:** annotation-core 3.8.
- **Huge pages:** the ink layer is clipped to the viewport.
- **Loaded foreign ink** (from Acrobat, Edge): drawn by PDFium; can be selected and deleted; cannot be erased partially.
- **Hostile input:** a loaded `/InkList` with a million points, NaN coordinates, 100,000 strokes: the engine caps the point and stroke counts (schema limits), the model keeps the first part, and the panel says it is truncated. Width samples outside 0 to 1000 are clamped.
- **Thousands of strokes on one page:** hit-testing uses the stroke bounding boxes through a spatial index first.

## 8. Undo and redo

Stroke (one command, journaled), erase gesture (one command listing removed strokes and the pieces added), whole-annotation move/delete/colour/opacity/width scale (one command each). Undo while a batch is open removes the last stroke; undoing a batch's last remaining stroke removes the annotation. After an undo the next stroke starts a new batch. Rules in annotation-core section 8.

## 9. Journal entries

| `cmd` | Payload |
|---|---|
| `AddInkStroke` | `annotation` (batch id, owned `MikePDF:` id), `page`, `tool` (`pen` or `highlighter`), `style` (`colour`, `opacity`, `width`, `curve`, `minRatio`), `stroke` (`id`, `n`, `data`: base64 of little-endian packed samples, each x f32, y f32, pressure u8 = 9 bytes), `newBatch` bool |
| `EraseInk` | `annotation`, `removed` (stroke ids), `added` (strokes, same encoding, for the pieces of a split) |
| `UpdateAnnotation` / `MoveAnnotation` / `DeleteAnnotation` | as annotation-core, `kind: "Ink"` |

A 20,000-point batch is about 240 KB of base64; the journal size cap ([save-pipeline](save-pipeline.md)) bounds the total. Ink strokes are user-added content and are stated in the privacy notes.

## 10. Save behaviour

The writer host receives the batch (strokes with pressure) in the edit blob and builds the `/Ink` annotation with the `/InkList`, the filled-outline appearance and, per OQ-INK-3, the private width samples. Highlighter appearances use `/BM /Multiply`. Everything else as annotation-core 3.9. The annotation is an indirect object; page `/Annots` is replaced in the same append.

## 11. Engine and protocol additions

None beyond annotation-core (`GetAnnotation.strokeCount/pointCount`, `GetInkStroke`). The writer-host edit blob carries stroke data ([save-pipeline](save-pipeline.md) section 11); the sizes there are not subject to the 64 KiB frame limit.

New internal pieces: project `MikePDF.Ink` (capture adapter interface, smoothing, pressure curves, outline and Bezier fitting, eraser geometry, batching) with **no WinUI or UWP dependency**, so it is unit-testable on CI and the same code produces both the screen rendering and the PDF (ADR-0007). `Windows.UI.Input.Inking.InkStrokeBuilder` is not used (S5 open question 4: it would tie the core to a UWP projection).

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Ink_Pen` / `_Highlighter` / `_Eraser` | Pen / Highlighter / Eraser |
| `Ink_PenMode` | Pen mode |
| `Ink_PenMode_On_Announce` | Pen mode on. The pen draws and touch scrolls. |
| `Ink_PenMode_Off_Announce` | Pen mode off |
| `Ink_Eraser_Stroke` / `_Point` | Erase whole strokes / Erase part of a stroke |
| `Ink_Size_Small` / `_Medium` / `_Large` | Small / Medium / Large |
| `Ink_Width` | Width {0} points |
| `Ink_Pressure_Curve` | Pressure response |
| `Ink_Curve_Linear` / `_Soft` / `_Firm` | Linear / Soft / Firm |
| `Ink_Barrel` | Pen button |
| `Ink_EraserEnd` | Pen eraser end |
| `Ink_Action_None` / `_Eraser` / `_Select` / `_Highlighter` / `_Pen` / `_Undo` | None / Eraser / Select / Highlighter / Pen / Undo |
| `Ink_TouchInks` | Draw with touch |
| `Ink_AltText` | Describe this drawing |
| `Ink_Automation_Name` | drawing by {0}, {1} strokes |
| `Ink_Started_Announce` | Drawing started on page {0} |
| `Ink_Strokes_Announce_One` / `_Other` | 1 stroke added / {0} strokes added |
| `Ink_Erased_Announce_One` / `_Other` | Erased 1 stroke / Erased {0} strokes |
| `Ink_NoKeyboard_Note` | Drawing needs a pen, finger or mouse. Use shapes or text boxes to mark up with the keyboard. |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-INK-1 | The S5 device run (latency, pressure levels, palm rejection) gates ADR-0007 and the M4 exit. Who has the Surface Pro, pen and 240 fps camera? | User (R-17) |
| OQ-INK-2 | Try delegated ink on the device (candidate c), or drop it? | Opus |
| OQ-INK-3 | Private width samples inside the annotation versus constant width on reload (3.8). | Opus |
| OQ-INK-4 | Default for the barrel button. | User (product) |
| OQ-INK-5 | Should touch be able to ink when pen mode is off (setting `pen.touchInks`, default off)? | User (product) |
| OQ-INK-6 | Highlighter Multiply on screen over the tile surface (3.10). | Opus (spike S9) |
| OQ-INK-7 | Relax "flush before acknowledge" for ink strokes to a 50 ms queued flush (3.9). | Opus |
| OQ-INK-8 | Lasso selection, partial selection of strokes, shape recognition and handwriting-to-text: out of scope for v1? | User (product) |

## 14. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply. Criteria marked **device** need the S5 hardware and are **not measured** until it exists.

| Id | Criterion | Layer |
|---|---|---|
| INK-01 | Smoothing: endpoints preserved; output within 0.5 DIP of a noise-free reference for 100 synthetic traces with injected 0.3 DIP jitter; no NaN for duplicate, out-of-order and zero-pressure inputs; steady-state processing allocates 0 bytes per sample (BenchmarkDotNet allocation diagnoser). | Unit + benchmark (`MikePDF.Ink.Tests`) |
| INK-02 | Pressure curves: Linear, Soft, Firm are monotonic, map 0 to the minimum ratio and 1 to 1.0, and width slope is bounded. | Unit |
| INK-03 | Outline geometry: for 200 traces the outline polygon contains every centreline point at its width, has no self-overlap artefacts that change area by more than 1% at sharp turns, and a one-point stroke gives a filled circle of the base width. | Unit |
| INK-04 | Bezier fitting deviates at most 0.1 pt from the outline on the trace set; Douglas-Peucker keeps the centreline within 0.1 pt. | Unit |
| INK-05 | Batching table: each rule (page, tool, colour, width, opacity, 10 s gap, 200 strokes, 20,000 points, selection change, save, undo) opens or keeps a batch as specified, with an injectable clock. | Unit |
| INK-06 | Eraser geometry: stroke mode removes exactly the touched strokes; point mode splits strokes into the right pieces keeping width samples; an erase gesture is one command; undo restores exact strokes. | Unit |
| INK-07 | Palm rejection state machine with synthetic pointer sequences: touch ignored while pen in range; ignored for 500 ms after; ignored with `TouchConfidence` false or oversized contact; accepted touch scrolls in pen mode; no ink from touch. | Unit |
| INK-08 | Barrel and eraser-end mapping: temporary tool is active only while held and restores the previous tool, for each action value. | Unit |
| INK-09 | Pen mode: pen inks without tool selection; touch scrolls; mouse inks only when a tool is selected. | UI journey (synthetic pen/touch injection via `CreateSyntheticPointerDevice` and `InjectSyntheticPointerInput`; API use to be verified in the first WP) |
| INK-10 | Saved structure: `/InkList` present with simplified points; `/AP` present without a prior render (S4 A07a/A07b failures do not recur); `/BS /W`, `/C`, `/CA`; highlighter has `/BM /Multiply`; private width samples per OQ-INK-3; `/Rect` encloses the outlines; indirect object; `qpdf --check` on classic and xref-stream bases. | Unit (PdfWriter) + Fidelity |
| INK-11 | Fidelity, fresh PDFium and pdf.js: width variation is visible (stroke-width profile of the rendered line along its length correlates >= 0.9 with the pressure profile), SSIM >= 0.97 against the golden over the annotation box (threshold to be confirmed). | Fidelity harness |
| INK-12 | Fidelity, Acrobat Reader and Edge: ink visible at the right place with width variation; highlighter multiplies over text; recorded as pass, fail or **not measured**. | Fidelity harness (manual-gated, R-15) |
| INK-13 | Reopen in MikePDF: strokes reload with points (and widths per OQ-INK-3); an erase on a reloaded stroke works; foreign ink cannot be partly erased. | Fidelity harness + Integration |
| INK-14 | Highlighter on screen equals saved render (AC-12) or the documented fallback difference is within the stated bound. | Fidelity harness (depends on OQ-INK-6) |
| INK-15 | Highlighter keeps text legible: black text pixels under a highlight remain below a luminance threshold in the PDFium render. | Fidelity harness |
| INK-16 | Rotated pages 0/90/180/270 and CropBox (50, 30): a recorded stroke appears at the same page position in PDFium render as in the overlay (within 0.5 pt). | Integration |
| INK-17 | A stroke crossing the page edge is clipped; a stroke starting in a margin creates nothing. | Unit |
| INK-18 | Capture robustness: pointer capture lost, window deactivation and tab switch mid-stroke commit the stroke; no partial stroke remains in the overlay. | Integration |
| INK-19 | Journal: one `AddInkStroke` per stroke, journal flushed within 50 ms of pen-up (clock-injected test and a measured integration test), strokes after a simulated crash are restored identically (point-for-point). | Integration |
| INK-20 | Journey 8 (§12.5): kill the UI process with unsaved ink, relaunch, restore prompt, ink restored with the same stroke and point counts. | UI journey |
| INK-21 | Latency proxy: from synthetic pointer event arrival to the draw call that renders the new segment, p95 <= 4 ms on the CI runner (an internal budget derived from the 25 ms target, proposed), recorded per commit. | Integration (perf) |
| INK-22 | **Device:** pen-to-pixel latency p95 <= 25 ms on a Surface Pro, 30 or more strokes, measured with a 240 fps camera (S5 method). Status stays **not measured** until the hardware run. | Manual record (hardware) |
| INK-23 | **Device:** 5 minutes of inking and resting a palm produce no stray strokes; pressure is monotonic over the pen's range. | Manual record (hardware) |
| INK-24 | Sustained synthetic 240 Hz inking for 5 minutes: no UI frame over 50 ms, memory growth bounded, scroll by synthetic touch remains responsive. | Integration (perf) |
| INK-25 | 30-slide scenario (§18 scenario 4) scripted with synthetic strokes: file size stays under a stated bound per slide (set from the first run; recorded), the saved file opens in PDFium and pdf.js with all strokes. | Integration + Fidelity |
| INK-26 | Accessibility: UIA name, alt text value, batch-start and pause announcements fire once (no per-stroke events), keyboard users can select, move, recolour, delete ink. | UI journey (a11y) |
| INK-27 | Hostile loaded ink (1 M points, NaN, 100,000 strokes, widths out of range) loads bounded, no crash. | Integration + fuzz |
| INK-28 | Fuzz/seed: stroke decoding from journal base64 rejects truncated and oversized data without crashing. | Unit |
