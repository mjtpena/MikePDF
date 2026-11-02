# Feature spec: annotation foundation (shared by every M4 markup spec)

- Status: Draft for Opus review. **This file is not in the list of requested features.** It exists because the nine annotation specs would otherwise repeat, and risk contradicting, the same model, display strategy and rules. Each annotation spec says "see annotation-core" and adds only what is specific to its type
- Milestone: M4
- Master prompt: §8.2, §5.2 rule 6, §6.3, §7.1 rules 7 and 8, §7.3, §10.3, §12.4; ADR-0008 (ownership of annotation dictionaries and appearance streams), S4 gap list (`docs/spikes/S4.md` section 5.2)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [save-pipeline](save-pipeline.md) (journal and save), [command-palette-shortcuts](command-palette-shortcuts.md), [settings-policies](settings-policies.md)
- Used by: [text-markup](text-markup.md), [sticky-notes](sticky-notes.md), [free-text](free-text.md), [ink-pen](ink-pen.md), [shapes](shapes.md), [stamps](stamps.md), [annotation-panel](annotation-panel.md), [forms](forms.md) (shares focus and tab order), [print](print.md) (snapshot)

Conventions are in [README.md](README.md).

## 1. What the evidence says

These facts from S4, S5 and ADR-0007/0008 constrain everything below.

- PDFium writes **no `/AP`** for most annotation types on a cold save; pdf.js then draws nothing for Text, FreeText and Stamp. It cannot create Line, Polygon or PolyLine at all, writes `/IRT` and `/Name` as strings, never links `/Popup`, stores annotations as direct dictionaries, and leaves a private `/PDFIUM_HasGeneratedAP` key. Its incremental save is invalid for xref-stream sources (7 of 9 in S4).
- So **`MikePDF.PdfWriter` owns every annotation dictionary and appearance stream** MikePDF creates or restyles (ADR-0008 decision 1), running only in `MikePDF.WriterHost`. PDFium stays the engine for reading, rendering, text, forms and page operations.
- The S4 Quire-authored cases Q01 to Q06 (Line, Polygon, PolyLine, Text with Popup and reply and review state, Stamp with a name object, FreeText with its own resources) pass `qpdf --check`, reopen in a fresh PDFium and draw in PDFium and pdf.js. **Acrobat Reader and Edge were not measured.** Every fidelity criterion in the annotation specs is therefore a target until the manual pass (R-15) runs.
- WinUI 3 has no ink control; the stroke model must be MikePDF's own (ADR-0007).

## 2. User stories (shared)

1. As a reviewer, everything I add looks the same in MikePDF, Acrobat Reader, Edge, Chrome and Firefox.
2. As a reviewer, I can select, move, resize, restyle and delete what I added, and undo and redo any of it.
3. As a keyboard-only or Narrator user, I can find, read, edit and delete annotations without a mouse.
4. As a reviewer opening a file a colleague annotated in another reader, I can see, reply to and delete their comments, and I'm told what I can't change.
5. As anyone, a crash never loses my annotations ([save-pipeline](save-pipeline.md)).

## 3. Detailed behaviour

### 3.1 Model and identity

An annotation in the edit model has:

| Property | Notes |
|---|---|
| `Id` | For annotations MikePDF creates: `MikePDF:` followed by a 32-character lower-case hex GUID, written to `/NM`. For others: the file's `/NM` if it has one, else a session-only id built from page identity and original index. |
| `Kind` | `Highlight`, `Underline`, `StrikeOut`, `Squiggly`, `Note` (PDF Text), `FreeText`, `Ink`, `Rectangle` (Square), `Ellipse` (Circle), `Line`, `Arrow` (a Line with an arrow ending), `Polygon`, `PolyLine`, `Stamp`. Others read from files are `Unsupported`. |
| `Page` | A **page identity** (a GUID assigned to each page when the document is opened and to each inserted page), never a page index. Indices change when pages move; identities don't. The index is resolved at save time ([save-pipeline](save-pipeline.md)). |
| `Rect` | In PDF user space of the page (points, origin bottom-left, honouring CropBox offsets and `/Rotate`), normalised so left < right and bottom < top. |
| `Style` | Colour, interior colour, opacity (`/CA`), border width and style, font, and so on, per kind. |
| `Contents` | The comment text, up to 10,000 characters in the UI. |
| `Author`, `Created`, `Modified`, `Subject` | See 3.5. |
| `Parent` | Optional id of the annotation this is a reply or state change to ([sticky-notes](sticky-notes.md)). |
| `Payload` | Kind-specific: quads, vertices, strokes, text lines, stamp artwork. |
| `Origin` | `Created` (new in this session) or `Loaded` (read from the file), with the original page index and `/Annots` index when loaded. |

The model is plain C# (`MikePDF.Core.Editing`), has no XAML or engine dependency, and is the single source of truth for save, print snapshot, undo and journal.

### 3.2 Who may change what

Every annotation is in exactly one class:

| Class | Meaning | Allowed |
|---|---|---|
| **Owned** | `/NM` starts with `MikePDF:` | Everything in this table. |
| **Foreign supported** | A kind MikePDF can draw and understand, made by another app (or by an older MikePDF without our prefix) | Select, **delete**, **reply**, set review state, edit comment text (`/Contents`). Move for kinds that are not text-anchored (Note, Rectangle, Ellipse, Line, Arrow, Polygon, PolyLine, Stamp, FreeText, Ink) by changing `/Rect` and the geometry keys, **keeping the existing appearance stream** (viewers map the stream's `/BBox` to the new `/Rect`). Text markup is not movable (anchored to text). **Not** allowed: restyle or resize that would require redrawing the appearance. |
| **Unsupported** | Link, Widget (handled by [forms](forms.md)), Popup (a helper), FileAttachment, Sound, Caret, Redact, 3D and others | Listed read-only in the panel when they are markup; **not** deletable in v1. They render through PDFium. |

Why foreign annotations aren't restyled: regenerating the appearance replaces the author's own look with ours. OPEN QUESTION OQ-AC-3 asks whether to offer "Redraw in MikePDF style" explicitly.

**Hostile "owned" claims.** A hostile file can put `MikePDF:` in `/NM` of anything. The effect is only that MikePDF treats it as owned and regenerates its appearance from its (validated) dictionary values, never from its old appearance stream. That is safe because the generator reads values through the same limits as any other, and an old `/AP` is never parsed by the generator.

### 3.3 Display: what the screen shows

This is the main open design question of M4 and it affects print, save and tabs. Nothing in the other specs depends on which option wins as long as **what the screen shows is what a fresh PDFium renders from the saved file** (criterion AC-12).

The problem: the engine renders tiles from its in-memory PDFium document. Annotations MikePDF creates live in the broker's edit model and are not in that document. PDFium cannot create three of our kinds, its generated appearances differ from ours, and its API for the rest is Experimental (R-12; S4 recommends dropping the creating calls from production use).

| Option | How | For | Against |
|---|---|---|---|
| 1. Overlay only | The UI draws every pending annotation from the model on a layer above the tiles, from geometry shared with the appearance generator | No dependence on PDFium annotation APIs; matches ADR-0006 (annotations on a separate layer); instant feedback | An original annotation that the user deletes or moves is still drawn in the tile underneath. Needs option 4's hiding |
| 2. Mirror in PDFium | The engine applies each edit through PDFium's annotation APIs | Tiles show it with no overlay | Can't create Line/Polygon/PolyLine; PDFium's appearance differs from what we save; Experimental APIs; edits would end up in a PDFium save |
| 3. Snapshot reload | After each edit the writer host builds the update and the engine reloads base + update | Screen equals saved file by construction | A reload per edit; slow for large files and for ink |
| 4. **Hybrid (recommended hypothesis)** | Overlay (option 1) for **pending owned** annotations; for deleted, moved or restyled **loaded** annotations, the engine sets the annotation's **Hidden flag in memory** so its tile no longer draws it; after a successful save the session **rebases** (reopens engines on the saved file, clears pending edits and overlay) | Cheap, keeps one geometry source, no PDFium creation APIs | The hidden-flag mirror must never reach a saved file (4a below); the overlay and the saved appearance are two renderings of one geometry and can drift |

**4a. Engine invariant.** The engine records every flag it changed for display (`SetAnnotationHidden`) and **restores them before any save** (`SaveDocument`, [save-pipeline](save-pipeline.md)). A saved file never contains a hidden-flag change that the user didn't make. Test AC-14.

**Recommendation:** option 4, **conditional on spike S9** (annotation display strategy), which must measure: overlay frame cost with 5,000 annotations on a page and 50,000 in a document; the hidden-flag behaviour in tiles; rebase time on a 1,000-page file; drift between the overlay and a fresh PDFium render for each kind. If S9 shows option 3 is affordable for the common sizes, the overlay shrinks to the live gesture only. OPEN QUESTION OQ-AC-1.

**Shared geometry.** A new pure project `MikePDF.Markup` (no I/O, no UI, no PDF syntax) computes everything both renderings need: quad highlight shapes, underline and squiggle paths, rectangle and ellipse outlines, arrow heads, polygon paths, ink outlines from strokes, note icon paths, text layout from shared font metrics. The overlay draws these with Win2D. `MikePDF.PdfWriter` turns the same output into content stream operators. A change to a shape changes both. Unit tests live in `MikePDF.Markup.Tests`.

**Overlay behaviour.** Drawn on its own Win2D layer above the tiles and below selection handles (§7.1 rule 7). It uses `PageProjection` so rotated pages, CropBox offsets, zoom and facing layouts are handled in one place. Selection, hover and handles are vector overlays, not baked into tiles (§7.1 rule 8). Blend modes (Multiply for highlights) are applied by the overlay as the file will apply them.

### 3.4 Tools, creation, selection

- **Tool state.** One active tool: `Select` (default) or a creation tool. After creating one annotation, the tool returns to `Select`, unless the setting `markup.keepToolActive` is on or the tool is pen or highlighter (they stay active). Esc returns to `Select`.
- **Creation** gestures are defined per type. All creation clamps to the page's displayed box.
- **Hit-testing** runs in the UI against the model (owned and loaded) using the shared geometry; tolerance 6 DIP for mouse and 12 DIP for touch and pen (proposed). The topmost annotation wins; order is `/Annots` order, later on top. Filled shapes hit on their interior, unfilled on their stroke. Text markup hits on its quads. A click on empty page deselects.
- **Selection** shows handles: eight for box kinds, vertex handles for line/polygon/polyline, none for text markup (selected state is an outline). **Multi-select** with Ctrl+click and rubber-band drag in the Select tool; Delete, move and status commands apply to all; restyle applies to those that accept it.
- **Moving and resizing** is a single undo step per gesture. Moves clamp so the annotation stays at least 8 x 8 pt inside the page's displayed box. Minimum size 4 x 4 pt. Shift constrains axis or aspect; Alt resizes from the centre.
- **Keyboard nudge:** arrow keys move 1 pt, Shift+arrows 10 pt; repeated nudges of one annotation within 750 ms are one undo step.
- **Context menu / menu key:** Edit, Properties, Reply (markup with comments), Delete, Copy comment text. Copying annotations to the clipboard and pasting them is out of scope for v1 (OPEN QUESTION OQ-AC-5).

### 3.5 Common properties

- **Author** (`/T`): `markup.authorName` if set, else policy `AnnotationAuthorName`, else the Windows account display name (`GetUserNameEx` with `NameDisplay`), else `Environment.UserName`. §8.2 asks for the display name by default. Privacy: the author name is written into every file the user shares. OPEN QUESTION OQ-AC-4: show a one-time notice on the first annotation ("Your name, {name}, will be saved with your comments. Change this in Settings.")?
- **Dates:** `/CreationDate` and `/M` as PDF dates in local time with offset, `D:YYYYMMDDHHmmSS+HH'mm'`. `/M` changes on every edit MikePDF makes to that annotation, not on views.
- **`/F` flags:** owned annotations get Print (4). Notes also get NoZoom (8) and NoRotate (16). Nothing else is set; Hidden, NoView and Locked are never set by MikePDF. Foreign flags are preserved.
- **`/Subj`:** the tool's name ("Highlight", "Sticky Note", "Text Box", "Drawing", and so on), as other readers show it in comment lists.
- **`/NM`, `/P`:** always written; `/P` refers to the page object.
- **Comment text:** NFC-normalised; control characters other than tab and line breaks stripped; written as a PDF text string (PDFDocEncoding when possible, else UTF-16BE with BOM).
- **Colour:** an accessible named palette (12 colours with names for screen readers) plus a custom colour. Stored as `/C` (RGB, 0 to 1), with opacity in `/CA`. Colour is never the only carrier of meaning in the UI.
- **Defaults per tool** are kept in `markup.toolDefaults` and updated by "last used".

### 3.6 The edit model and where it lives

- The **edit model is in the broker (UI process)**. It records commands (§5.2 rule 6) and the journal; it is the truth for save. The engine holds a **display mirror** only (3.3). Consequences: an engine crash or a tab suspension loses nothing; replay after reopening re-applies the hidden flags; the engine cannot corrupt edits.
- The UI process still never parses PDF bytes: it holds model data it created or that the engine reported through validated messages.
- **Page identity.** The model keeps an ordered list of page identities that tracks page operations ([page-organiser](page-organiser.md)); annotation edits are keyed by identity. If a page is deleted its annotations are dropped from the save but stay in the undo stack.
- **Reading existing annotations.** `ListAnnotations`, `GetAnnotation` and `GetInkStroke` (section 11) give the model its `Loaded` annotations lazily for the pages near the viewport, and for the panel in the background. They are validated like every engine reply.
- **Appearance generation** happens only in `MikePDF.PdfWriter` inside the writer host, from model values. It never reads or reuses an existing appearance stream.

### 3.7 Keyboard navigation and focus

§8.2: "Tab cycles annotations on the page; Enter edits; Delete removes."

- With the page surface focused, **Tab** enters annotation navigation: focus moves to the first interactive item on the current page (annotations and form widgets in the order set by [forms](forms.md) section 3.5; annotations alone follow `/Annots` order). **Shift+Tab** moves back. Each item gets a visible focus rectangle (a 2 px high-contrast-safe outline, plus handles when selected).
- After the last item on a page, Tab moves to the first item of the next page that has items. After the last item in the document, Tab **leaves the surface** to the next control in the window's focus order. This is the no-keyboard-trap rule (WCAG 2.1.2): the surface never swallows Tab at its end.
- **Esc** drops item focus back to the page level (the surface keeps focus).
- **Enter** edits the focused item (opens text editing for notes and free text; opens the properties pane for others). **Alt+Enter** opens properties. **Delete** deletes. **F2** renames (edit comment). **Arrow keys** nudge. **Space** selects or toggles selection.
- Focus on an item scrolls it into view with a margin and raises the UIA focus event.

### 3.8 Permissions, signed and encrypted documents

| Condition | Behaviour |
|---|---|
| `permissions` bit 6 (add or modify annotations, value 32) clear | All markup tools are disabled with the owner reason. Existing annotations can still be read. Forms have their own bit (see [forms](forms.md)). |
| Signed document (`signatureCount > 0`) | Annotating is allowed. On the first edit a non-blocking notice says: "This document is signed. Your changes are saved as an addition, so the signature stays intact, but some readers will show that the document changed after signing." The saved file keeps the original bytes as an exact prefix (ADR-0008, S4). **MikePDF cannot yet tell whether the signature's DocMDP level forbids annotations** (validation is M6). OPEN QUESTION OQ-AC-2 asks whether to block annotations on a certified document in M4. |
| Encrypted document (opened with a password) | The writer host reports `UnsupportedEncryption` for appends (it cannot encrypt new strings and streams). In M4 the markup tools stay enabled for **review**, but **Save** offers only Save As... without password protection, with a clear explanation, or keeps the changes in the journal. OPEN QUESTION OQ-AC-6: confirm this; **not measured**: what PDFium does to encryption in a full save. |
| Document larger than 1 GiB | `MikePDF.PdfWriter` refuses (R-38). Markup tools show a notice at the first edit: "This file is too large to add comments to." |
| `Damaged` or repaired document | Edits allowed only if the writer host's strict parse accepts the file (R-39). Otherwise tools are disabled with "This file's structure can't be edited safely. You can still read it." |

### 3.9 Appearance rules shared by every kind (from S4's gap list)

1. Every owned annotation gets an `/AP /N` form XObject with `/Type /XObject`, `/Subtype /Form`, `/BBox`, `/Matrix` (identity unless noted), and its own `/Resources` (fonts, ExtGState), written at creation and on every restyle, independent of whether a page was ever rendered.
2. Annotations are **indirect objects** (not direct dictionaries inside `/Annots`), with `/P` set. Page `/Annots` is replaced by an array of references.
3. `/BBox` equals the annotation's `/Rect` size and origin such that moving the annotation changes only `/Rect`.
4. Opacity: `/CA` on the annotation **and** the same value as `/ca` and `/CA` in an ExtGState used by the appearance, so readers that ignore `/CA` still draw it.
5. Blend modes: highlights use `/BM /Multiply` in an ExtGState.
6. Fonts in appearances are the standard 14 with explicit `/Encoding /WinAnsiEncoding`, declared in the appearance's own resources (never inherited from the page; S4 F4).
7. Standard name values (`/Name` for Text and Stamp, `/LE` entries) are **name objects**, not strings (S4 F3).
8. No private PDFium keys are ever written.
9. Output must pass `MikePDF.PdfWriter`'s structural validator and `qpdf --check` in the fidelity harness.

## 4. Keyboard, pen and touch

| Action | Behaviour |
|---|---|
| Undo / redo | Ctrl+Z / Ctrl+Y and Ctrl+Shift+Z (`edit.undo`, `edit.redo`) |
| Delete | Delete (`edit.delete`) |
| Navigate annotations | Tab / Shift+Tab ([3.7](#37-keyboard-navigation-and-focus)) |
| Edit / properties | Enter / Alt+Enter |
| Tools | Commands `tool.*` (no default shortcuts, OQ-CMD-3) and `markup.*` selection commands |

Pen and touch: pen and touch can create every kind except where a spec says otherwise (ink is pen-first). Touch targets for handles are 24 DIP minimum; a long press opens the context menu. Palm rejection applies while inking ([ink-pen](ink-pen.md)); outside inking, pen and touch behave as pointers. A single finger on the page scrolls unless a creation tool is active **and** pen mode is off.

## 5. Accessibility (shared)

- Each annotation is a UIA element, a child of the page element in the surface's automation peer (`PageSurfaceAutomationPeer` gets child peers). `AutomationId` is `annot:{Id}`. Control type `Custom` with a `LocalizedControlType` from the kind ("highlight", "note", "text box", "drawing", "rectangle", "ellipse", "line", "arrow", "polygon", "polyline", "stamp"). `IsKeyboardFocusable` true. Name: "{kind} by {author}, {excerpt}", with the excerpt the first 80 characters of the comment or the underlying text for markup. `HelpText`: date, page and review state. Patterns: `Invoke` (edit), `Value` (comment text; read-only when permissions forbid), `SelectionItem` (selected state).
- Child peers must not disturb the Text pattern: `RangeFromChild` for a text markup returns the range of the text it covers, so Narrator reads "highlight: {the highlighted text}".
- **Announcements** (polite live region, throttled to one per 500 ms): "Highlight added on page 5", "Note deleted", "Moved 3 points right", "Undo: highlight removed".
- Selection handles, outlines and focus rectangles meet 3:1 contrast against the page and against each other in light, dark and high contrast; high-contrast themes use system colours.
- Drawing with a pen is a path-based gesture; its keyboard alternative is the shape and free-text tools plus `Contents` text on any annotation (WCAG 2.5.1 essential exception for freehand drawing, documented in [ink-pen](ink-pen.md)).
- Every property control has a name; the colour palette announces colour names.
- Text scaling to 225%: properties UI and the annotation panel reflow; the page content is scaled by zoom, not by text scaling.

## 6. Error states (shared)

| Situation | Behaviour |
|---|---|
| Journal cannot be written | Persistent chip "Changes can't be backed up" ([save-pipeline](save-pipeline.md)); editing continues. |
| Engine restarts | Overlay keeps drawing from the model; the hidden-flag mirror is re-applied when the engine returns. |
| Writer host crash during save | Save fails with "Your changes are safe. Try saving again."; the journal is intact. |
| `ListAnnotations` fails or times out for a page | The page shows its annotations from the tile only; the panel shows "Couldn't read comments on page {n}" and a retry. |
| Annotation cannot be edited (class, permission, size) | Its commands are disabled with the reason; nothing silent. |
| Out-of-bounds creation | Clamped; no error. |

## 7. Edge cases (shared)

- **Rotated pages and CropBox offsets:** the model is in user space. Creating on a rotated page converts through `PageProjection`; text-anchored kinds use the char boxes the engine reports in user space; every kind has a fixture for `/Rotate` 0, 90, 180, 270 and a non-zero CropBox origin. Appearance streams are authored in unrotated user space; readers rotate the page, and `NoRotate` kinds (notes) keep their icon upright by flag.
- **Mixed page sizes, landscape pages, facing views:** creation and hit-testing go through the projection of the page under the pointer.
- **Huge documents:** annotation data is loaded for the pages near the viewport (the page and two on each side) and cached with the same eviction as page text. The panel's scan runs on the pool engine at low priority. No whole-document annotation load at open.
- **Hostile input** (a file with 2 million annotations; `NaN` or infinite rects; quad lists with 10 million numbers; reply loops; `/Parent` cycles; a 50 MB `/Contents`; AP streams that are zip bombs): engine counting stops at 100,000 per page; coordinates outside +-1,000,000 are dropped; arrays are capped at the schema limits; contents are truncated at 16 KiB with a flag; reply chains are cut at depth 32 and cycles are detected; the generator never decodes an existing AP.
- **Many annotations on one page** (10,000): hit-testing uses a spatial index in the model, not a linear scan per pointer move; overlay culls to the viewport.
- **Duplicate `/NM` values** in a loaded file: the later one gets a session-only id; saves keep the file's values.
- **Annotations outside the page box or with zero area:** listed, not drawn by the overlay, deletable.
- **Concurrent edits to one file in two tabs:** impossible (one tab per file, [tabs-windows-session](tabs-windows-session.md)).

## 8. Undo and redo (shared semantics)

- One linear undo stack per document across annotation, form and page edits (§7.3: unlimited within a session). Redo clears on any new command.
- Commands: `AddAnnotation`, `DeleteAnnotation`, `UpdateAnnotation` (a set of property changes), `MoveAnnotation`, `ResizeAnnotation`, `AddReply`, `SetReviewState`, and a `CompositeCommand` that groups several (a multi-page highlight is one command; multi-select delete is one).
- **Granularity:** a drag is one command (mouse down to up); keyboard nudges coalesce within 750 ms; a text edit session is one command (the text box has its own character-level undo while open); a restyle of a selection is one command; a pen stroke is one command and strokes batch as described in [ink-pen](ink-pen.md).
- Delete then undo restores the same `Id`, so replies stay attached.
- **Undo after save:** the stack survives a save. The save marks a clean point. Undoing past it makes the document dirty again and, because the saved file now contains the annotation, the inverse is expressed as a new change relative to the saved base (undoing an add after a save becomes a delete on the next save). The journal records it as an ordinary command.
- Undo and redo update the overlay, the hidden-flag mirror, the panel and the dirty state together.

## 9. Journal entries (annotation payloads)

Record envelope and rules are in [save-pipeline](save-pipeline.md) section 3.4. Annotation commands add these payloads. `page` is a page identity. Style values are the same names as the model.

| `cmd` | Payload |
|---|---|
| `AddAnnotation` | `id`, `kind`, `page`, `rect`, `style`, `contents`, `author`, `created`, `payload` (kind-specific, see each spec), `parent` (optional) |
| `UpdateAnnotation` | `id`, `changes` (map of property to new value) and `previous` (the old values, so replay can build the inverse without the file) |
| `MoveAnnotation` / `ResizeAnnotation` | `id`, `rect` (and geometry when the kind has it), `previous` |
| `DeleteAnnotation` | `id`, and for loaded annotations `page` and original index (the full body is not stored) |
| `AddReply` / `SetReviewState` | `id`, `parent`, `contents` or `state`, `author`, `created` |

No page content is journaled. Annotation text and image stamp bytes are user-added content (§7.4) and are documented in the privacy notes ([save-pipeline](save-pipeline.md) section 3.4).

## 10. Save behaviour

Annotation edits are applied in save stage 4 by the writer host as an incremental append: new annotation objects, replaced objects for changed or deleted ones, the page's `/Annots` array and page object, and the xref. The original bytes stay an exact prefix. Details, validation and failure handling are in [save-pipeline](save-pipeline.md).

## 11. Engine and protocol additions (schema v5, read side)

IDs follow the existing ranges (16 to 25 document and reading, 32 and 33 rendering). All take `documentId` (required) and use windows so that a reply always fits one 64 KiB frame, as ADR-0005 amendment A1 does. PDFium subtype numbers are the `FPDF_ANNOT_SUBTYPE_*` values (UNKNOWN 0 ... REDACT 28); they must be confirmed against the pinned `fpdf_annot.h` (S1 rule). The calls used (`FPDFPage_GetAnnot`, `FPDFAnnot_*` getters) are Experimental (R-12); the wrapper in `MikePDF.Engine.Pdfium` is their only consumer and has a header-diff check.

**`ListAnnotations` (40).** Request: `page` (u32, max 9999999), `first` (u32, max 99999), `count` (u32, 1 to 64).

| Reply key | Name | Type | Limits |
|---|---|---|---|
| 0 | `total` | u32 | max 100000; counts every annotation on the page, engine stops at 100000 |
| 1 | `subtypes` | array u16 | maxCount 64, max 28 |
| 2 | `flags` | array u32 | maxCount 64, `sameLengthAs` `subtypes` (PDF `/F`) |
| 3 | `lefts` | array f32 | maxCount 64, -1000000 to 1000000, `sameLengthAs` |
| 4 | `bottoms` | array f32 | as above |
| 5 | `rights` | array f32 | as above |
| 6 | `tops` | array f32 | as above |
| 7 | `colours` | array u32 | maxCount 64, `sameLengthAs` (0xAARRGGBB; 0 = none) |
| 8 | `nms` | array string | maxCount 64, maxBytes 64, `sameLengthAs` (empty when absent) |
| 9 | `authors` | array string | maxCount 64, maxBytes 128 (cut at a UTF-8 boundary), `sameLengthAs` |
| 10 | `modified` | array string | maxCount 64, maxBytes 32, `sameLengthAs` (raw `/M`; parsed by the broker) |
| 11 | `snippets` | array string | maxCount 64, maxBytes 192, `sameLengthAs` (start of `/Contents`) |
| 12 | `parents` | array i32 | maxCount 64, -1 to 99999, `sameLengthAs` (index of the annotation this replies to, -1 for none) |

Estimated worst-case frame: about 31 KiB at 64 annotations.

**`GetAnnotation` (41).** Request: `page`, `index` (u32, max 99999). Reply:

| Name | Type | Limits |
|---|---|---|
| `subtype`, `flags` | u16, u32 | as above |
| `left`, `bottom`, `right`, `top` | f32 | -1000000 to 1000000 |
| `colour`, `interiorColour` | u32 | 0xAARRGGBB or 0 |
| `opacity` | f32 | 0 to 1 |
| `borderWidth` | f32 | 0 to 1000 |
| `borderStyle` | u16 | max 4 (solid, dashed, beveled, inset, underline) |
| `contents` | string | maxBytes 16384; `contentsTruncated` bool |
| `author`, `subject` | string | maxBytes 256 |
| `nm` | string | maxBytes 64 |
| `created`, `modified` | string | maxBytes 32 |
| `parentIndex` | i32 | -1 to 99999 |
| `replyType` | u16 | max 2 (none, reply, group) |
| `stateModel`, `state` | string | maxBytes 16 |
| `popupOpen` | bool | |
| `name` | string | maxBytes 64 (Text icon name or Stamp name) |
| `quadPoints` | array f32 | maxCount 512, -1000000 to 1000000 |
| `vertices` | array f32 | maxCount 2048 (x,y pairs; a Line's four numbers), `verticesTruncated` bool |
| `lineEndings` | array u16 | maxCount 2, max 10 |
| `strokeCount`, `pointCount` | u32 | max 100000, max 10000000 |
| `defaultAppearance` | string | maxBytes 256 (FreeText `/DA`) |
| `alignment` | u16 | max 2 |
| `hasAppearance` | bool | |

Estimated worst-case frame: about 32 KiB.

**`GetInkStroke` (42).** Request: `page`, `index`, `stroke` (u32, max 99999), `first` (u32, max 9999999), `count` (u32, 1 to 1024). Reply: `pointCount` (u32, max 10000000), `xs`, `ys` (f32 arrays, maxCount 1024, -1000000 to 1000000), `hasWidths` (bool) and `widths` (f32 array, maxCount 1024, `sameLengthAs` `xs`, 0 to 1000; zeros when absent). `widths` come from MikePDF's private ink data if the annotation has it ([ink-pen](ink-pen.md) OQ-INK-3).

**`SetAnnotationHidden` (43).** Request: `page`, `index`, `hidden` (bool). Reply: empty. Display mirror only (3.3, invariant 4a).

**`CountAnnotations` (44).** Request: `firstPage` (u32, max 9999999), `count` (u32, 1 to 5000). Reply: `counts` (array u16, maxCount 5000; user-visible annotations only, excluding Link, Popup and Widget; saturates at 65535). A full reply is about 15 KiB.

`ReplyChecks` additions: every parallel array equals the requested window length; `parents` and `parentIndex` refer to an index below `total`; `contents` is valid UTF-8; no float is NaN or infinite; rect edges satisfy left <= right and bottom <= top, else the entry is marked invalid and skipped rather than failing the session.

The writer-host side (`AppendAnnotations`) is defined in [save-pipeline](save-pipeline.md) section 11.

## 12. Localisation strings (shared)

| Key | en-US |
|---|---|
| `Annot_Kind_Highlight` / `_Underline` / `_StrikeOut` / `_Squiggly` | highlight / underline / strikethrough / squiggly underline |
| `Annot_Kind_Note` / `_FreeText` / `_Ink` / `_Rectangle` / `_Ellipse` / `_Line` / `_Arrow` / `_Polygon` / `_PolyLine` / `_Stamp` | note / text box / drawing / rectangle / ellipse / line / arrow / polygon / polyline / stamp |
| `Annot_Automation_Name` | {0} by {1}, {2} |
| `Annot_Help` | {0}, page {1}, {2} |
| `Annot_Added_Announce` | {0} added on page {1} |
| `Annot_Deleted_Announce` | {0} deleted |
| `Annot_Moved_Announce` | Moved {0} points {1} |
| `Annot_Undo_Announce` | Undo: {0} |
| `Annot_Redo_Announce` | Redo: {0} |
| `Annot_Signed_Notice` | This document is signed. Your changes are saved as an addition, so the signature stays intact, but some readers will show that the document changed after signing. |
| `Annot_Author_Notice` | Your name, {0}, will be saved with your comments. You can change this in Settings. |
| `Annot_Disabled_Owner` | The document's owner doesn't allow adding comments. |
| `Annot_Disabled_TooLarge` | This file is too large to add comments to. |
| `Annot_Disabled_Structure` | This file's structure can't be edited safely. You can still read it. |
| `Annot_Disabled_Foreign` | Made in another app. MikePDF can move, reply to and delete it, but can't restyle it. |
| `Annot_ReadFailed` | Couldn't read comments on page {0}. |
| `Tool_Select` | Select |
| `Prop_Colour` / `_Opacity` / `_Width` / `_Font` / `_Size` | Colour / Opacity / Width / Font / Size |
| `Colour_Yellow` / `_Green` / `_Cyan` / `_Pink` / `_Orange` / `_Purple` / `_Red` / `_Blue` / `_Black` / `_White` / `_Grey` / `_Brown` | colour names for the palette |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-AC-1 | Display strategy (3.3): confirm option 4 after spike S9, or choose another. | Opus |
| OQ-AC-2 | Block annotating a certified (DocMDP) document in M4, or allow with a notice (current text). | Opus, User |
| OQ-AC-3 | Offer "redraw in MikePDF style" for foreign annotations. | User (product) |
| OQ-AC-4 | One-time author-name notice. | User (product), Opus (privacy) |
| OQ-AC-5 | Copy and paste of annotations between pages and documents: out of scope for v1? | User (product) |
| OQ-AC-6 | Encrypted documents: annotations allowed but Save As without password only (3.8)? Needs a measurement of PDFium's encryption behaviour on a full save. | Opus; Sonnet measurement |
| OQ-AC-7 | New projects `MikePDF.Markup` and tests: needs ADR? (No new third-party dependency; internal project only.) | Opus |
| OQ-AC-8 | Marker for MikePDF-authored annotations: `/NM` prefix (assumed) versus a private dictionary key. | Opus |

## 14. Acceptance criteria

These apply to every kind. Each annotation spec repeats the ones that need type-specific fixtures.

| Id | Criterion | Layer |
|---|---|---|
| AC-01 | Model: ids are `MikePDF:` + 32 hex for owned annotations, unique across 100,000 creations; loaded annotations without `/NM` get session ids that do not collide. | Unit (`MikePDF.Core.Tests`) |
| AC-02 | Classification: owned, foreign supported and unsupported are assigned as in 3.2, and the allowed-operation matrix is enforced by commands (disabled with the right reason key). | Unit |
| AC-03 | Coordinates: every conversion between page space and user space for `/Rotate` 0, 90, 180, 270 and a CropBox origin of (50, 30) round-trips within 0.01 pt. | Unit |
| AC-04 | Hit-testing: topmost wins; filled vs unfilled rule; tolerance for mouse and touch; 10,000 annotations hit-test in < 1 ms using the index. | Unit |
| AC-05 | Move and resize clamp rules: minimum 4 x 4 pt, stays 8 x 8 pt inside the page, Shift and Alt modifiers. | Unit |
| AC-06 | Undo and redo: for every command class, apply then undo restores the model exactly (structural equality), redo reapplies, new commands clear redo, composite commands are atomic. | Unit |
| AC-07 | Undo after save: undoing an add that was saved yields a delete change relative to the saved base; the journal records it as a normal command. | Unit + Integration |
| AC-08 | Coalescing: nudges within 750 ms are one command; a drag is one; a text edit session is one. | Unit |
| AC-09 | Property rules: author resolution order, PDF date formatting with offsets (including negative and half-hour zones), flags per kind, NFC normalisation and control-character stripping of comments. | Unit |
| AC-10 | Permissions: bit 6 clear disables every markup tool; signed, encrypted, too-large and structure-refused documents behave as 3.8 (notice shown once per document, tool state). | Unit + Integration |
| AC-11 | Protocol: golden vectors and malformed frames (every limit exceeded by one, unequal array lengths, NaN, bad subtype) for messages 40 to 44 in both codecs; `ReplyChecks` rejects invalid replies and skips invalid entries. | Unit (Protocol + Engine C++) |
| AC-12 | **Screen equals file:** for every kind and each of `/Rotate` 0/90/180/270, the overlay-or-tile render of an annotated page and a fresh PDFium render of the saved file agree: SSIM >= 0.98 over the annotation's bounding box (target; threshold to be confirmed on the first run). | Fidelity harness |
| AC-13 | Overlay and appearance share geometry: for each kind, the path points produced by `MikePDF.Markup` equal those parsed back from the generated appearance content stream within 0.01 pt. | Unit (`MikePDF.Markup.Tests` + `MikePDF.PdfWriter.Tests`) |
| AC-14 | Display mirror invariant: after hiding, moving or deleting loaded annotations and then saving, the saved file contains no changed `/F` values except for annotations the user edited. | Integration |
| AC-15 | Rebase: after a save, engines reopen on the saved file, pending edits are cleared, the overlay shows no gap or double drawing (pixel test across the swap), and tiles equal a fresh render. | Integration |
| AC-16 | Lazy loading: opening a 5,000-page document performs no `ListAnnotations` call before the first paint and requests only the near pages; the panel scan is low priority. | Integration |
| AC-17 | Hostile files: the annotation fuzz corpus (huge counts, NaN rects, loops, giant contents, zip-bomb AP) completes without crash, hang or memory above the caps; the loaded model has bounded size. | Integration + fuzz |
| AC-18 | Keyboard navigation: Tab cycles items in the specified order across pages, Shift+Tab reverses, the last Tab leaves the surface, Esc returns to page level, Enter edits, Delete deletes, no trap (UIA focus events). | UI journey |
| AC-19 | UIA tree: each annotation element has the specified `AutomationId`, localized control type, name, help text and patterns; `RangeFromChild` for text markup returns the covered text. Raw `IUIAutomation`. | UI journey (a11y) |
| AC-20 | Announcements: add, delete, move and undo raise exactly one polite message each within 500 ms throttle. | UI journey (a11y) |
| AC-21 | Appearance rules 1 to 9 in 3.9 hold for every kind in the saved output (`MikePDF.PdfWriter.Tests` structural assertions plus `qpdf --check`). | Unit + Fidelity |
| AC-22 | Contrast: selection outline, handles and focus rectangle reach 3:1 in light, dark and high contrast (computed from theme resources). | Unit |
| AC-23 | Signed-document notice appears once on the first edit of a signed fixture and the saved file keeps the original bytes as an exact prefix. | Integration |
| AC-24 | Foreign annotation edit set: delete, reply, state change and non-text-anchored move work on a file annotated by another app and leave the foreign appearance stream byte-identical after a move. | Integration + Fidelity |
| AC-25 | Journey 3 (§12.5): highlight text, add a note, save, reopen: annotations intact. | UI journey |
