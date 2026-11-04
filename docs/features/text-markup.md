# Feature spec: text markup annotations (highlight, underline, strikethrough, squiggly)

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (Markup and forms), §8.2, §7.2 (text selection), §12.4 (fidelity), §18 scenario 1
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md) (model, display, journal payloads, shared criteria), text selection in `MikePDF.Core.Text` (`TextSelection`, `TextModel`), [save-pipeline](save-pipeline.md)
- Related: [sticky-notes](sticky-notes.md) (a text markup can carry a comment and replies)

Conventions are in [README.md](README.md). Shared rules (ownership, undo, journal envelope, hostile input, announcements) are in annotation-core and not repeated.

## 1. User stories

1. As an accountant, I select three passages in a 600-page report, press Ctrl+H, and each is highlighted (§18 scenario 1).
2. As a reviewer, I underline or strike out text and the marks look the same in Acrobat Reader and Edge.
3. As a reviewer, I attach a comment to a highlight so my colleague sees it in their reader.
4. As a student, I turn on the highlight tool and drag over several passages without reselecting the tool each time.
5. As a Narrator user, I hear "highlight: {the highlighted text}" and can delete or recolour it from the keyboard.

## 2. Detailed behaviour

### 2.1 Kinds and defaults

| Kind | PDF subtype | Default colour | Other defaults | Look |
|---|---|---|---|---|
| Highlight | `/Highlight` | Yellow (1, 0.92, 0) | opacity 100% | A filled quadrilateral per line, drawn with the Multiply blend so text stays readable |
| Underline | `/Underline` | Red | thickness 1 pt | A straight line along the bottom edge of each line's quad |
| Strikethrough | `/StrikeOut` | Red | thickness 1 pt | A line through the vertical middle of each quad |
| Squiggly | `/Squiggly` | Red | thickness 1 pt | A zigzag along the bottom edge (wavelength 3 pt, amplitude 1 pt at thickness 1, scaled with thickness) |

Opacity is 10% to 100% in steps of 5. Thickness (not for highlight) is 0.5 to 4 pt. These ranges and the wave constants live in `MikePDF.Markup`.

### 2.2 Creating

1. **From a selection.** With text selected, any of: Ctrl+H (highlight, `markup.highlightSelection`), the floating mini toolbar that appears near the selection (design spec) with the four kinds, a colour and "Add comment", the context menu, the command bar, or the palette.
2. **With a tool active.** `tool.highlight` (and the other three) make a drag over text create the mark when the mouse, finger or pen is released. The tool returns to `Select` unless `markup.keepToolActive` is on. With a tool active, a click that selects no text creates nothing.
3. **Rectangular (column) selection** (Alt+drag, §7.2) creates one quad per row.
4. **A selection across several pages** creates one annotation per page, as **one** undo step.
5. A selection that covers no characters (only margins, or a page with no text) shows "There's no text to mark here." and creates nothing. Scanned pages have no text until OCR (P1).

New marks use the tool's last-used colour, opacity and thickness (`markup.toolDefaults`).

### 2.3 Turning a selection into quads

The selection is a list of character ranges per page in the numbering of `GetPageText`. The engine turns a range into quads, because only it knows each character's angle and matrix. The new message `GetTextQuads` (section 10) returns, for a range, one quad per text line, in PDF user space, in the order **top-left, top-right, bottom-left, bottom-right** (the order that Acrobat Reader and Edge expect, S4 gap 2).

Rules the engine applies and tests assert:

- Characters are grouped into lines by baseline and by direction of the text. Text at any angle, and vertical writing, produces rotated quads, not bounding boxes.
- Trailing and leading whitespace of each line is trimmed. Spaces between words stay in. A character PDFium made up (flag bit 0 of `GetPageText`, a space or line break it inserted) is never the first or last character of a quad.
- A hyphen at the end of a line (flag bit 1) is included.
- A ligature glyph that stands for several characters is included whole if any of its characters is in the range.
- Quad height uses the characters' loose boxes (ascent and descent of the font), so it follows the font, as other readers do. OPEN QUESTION OQ-TM-1: PDFium's loose boxes can differ from Acrobat's, producing taller or shorter highlights. The fidelity fixtures include six fonts; the target is a visually equivalent result, and the threshold is set after the first Acrobat pass.
- Adjacent quads of one line that touch or overlap within 0.5 pt are merged.
- At most 4,096 quads per range; beyond that the reply says `truncated` and the UI marks only the first 4,096 and tells the user.

`/QuadPoints` holds the quads. `/Rect` is the union of all quads (for underline, strike-out and squiggly expanded by half the thickness). The appearance's `/BBox` equals `/Rect`.

### 2.4 Appearance stream

Per annotation-core 3.9, plus:

- **Highlight:** `/ExtGState << /GS << /Type /ExtGState /BM /Multiply /CA {o} /ca {o} >> >>`; content: `/GS gs {r g b} rg` then, per quad, a closed path through its four corners in drawing order (TL, TR, BR, BL) and `f`. Rounded corners are not used in v1.
- **Underline:** `/GS gs`, stroking colour, `{w} w`, per quad `m` to `l` along the bottom edge, moved up by half the thickness, `S`. Butt caps.
- **StrikeOut:** the same along the vertical midline of the quad.
- **Squiggly:** a polyline of alternating points along the bottom edge, miter joins, round caps.
- No fonts are used.

### 2.5 Selecting, recolouring and deleting

- A **click** on a mark (no drag) with the Select tool selects the annotation. A **drag** that starts on a mark still starts a text selection, so highlighting over an existing highlight works. Double-click still selects a word.
- A selected mark shows an outline around its quads (no resize handles; marks are anchored to text and cannot be moved or resized).
- Recolour, opacity and thickness are changed in the properties pane, the mini toolbar, or the palette. The appearance is regenerated.
- Changing a mark from one kind to another is not offered in v1 (delete and recreate).
- Delete removes it. Foreign marks (made by another reader) can be deleted, replied to and commented, but not recoloured ([annotation-core](annotation-core.md) 3.2).

### 2.6 Comments on a mark

"Add comment" (mini toolbar, context menu, `markup.addCommentToSelection`) gives the mark `/Contents` and a `/Popup`, and opens the comment editor attached to it. Replies and review state follow [sticky-notes](sticky-notes.md) section 2.3, with the mark as the root of the thread. A mark without a comment has no popup.

## 3. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Highlight selection | Ctrl+H (configurable, §10.2) | `markup.highlightSelection` |
| Underline / strikethrough / squiggly selection | none | `markup.underlineSelection`, `markup.strikeoutSelection`, `markup.squigglySelection` |
| Add comment to selection | none | `markup.addCommentToSelection` |
| Highlight / underline / strikethrough / squiggly tool | none | `tool.highlight`, `tool.underline`, `tool.strikeout`, `tool.squiggly` |

Keyboard text selection (Shift+arrows on the page surface, from M2) followed by Ctrl+H is the keyboard-only way to create a mark. Pen: with a pen, dragging over text with a markup tool active creates the mark; with the pen in pen mode ([ink-pen](ink-pen.md)) the pen inks instead. Touch: touch-select handles (from M2) then the mini toolbar; touch targets 40 DIP.

## 4. Accessibility

See annotation-core section 5. Specific:

- Control type name: "highlight", "underline", "strikethrough" or "squiggly underline". Name: "highlight by {author}, {the marked text up to 80 characters}".
- Creating raises "{Kind} added on page {n}: {text up to 80 characters}".
- Colours have names in the picker; opacity and thickness are sliders with value text ("Thickness 1 point").
- The mini toolbar is a toolbar with arrow-key navigation and Esc to dismiss; it never takes focus from the selection unless the user tabs into it (the shortcut Alt+F10 style entry is OPEN QUESTION OQ-TM-4 for the design spec).
- The highlight's colour is not the only cue that text is marked: the panel lists it by kind and text.

## 5. Error states

| Situation | Behaviour |
|---|---|
| `GetTextQuads` times out or fails | The selection stays; "Couldn't mark that text. Try again." |
| No text in the selection | "There's no text to mark here." |
| Range truncated (> 4,096 quads) | Mark created for the first 4,096 quads and a notice. |
| Permissions or document limits | As annotation-core 3.8. |
| Engine restarts between selection and creation | The selection is rebuilt from the model ranges, the request repeats once; on a second failure the user is told. |

## 6. Edge cases

- **Rotated pages:** quads come back in user space and are drawn through `PageProjection`; fixtures for `/Rotate` 0, 90, 180, 270.
- **CropBox offset:** quads are in user space, so a non-zero CropBox origin needs no special handling; the fixture has origin (50, 30).
- **Rotated and vertical text:** fixtures at 30 and 90 degrees and a vertical CJK line.
- **Right-to-left and mixed-direction text:** quads follow visual order per line; the selection order follows reading order from M2. A line with mixed directions may produce several quads (one per run).
- **Type 3 fonts and fonts with bad metrics:** the loose box may be empty; the engine falls back to the character box; a character with an empty box is skipped.
- **Text under images or clipped text:** marked where it is; no attempt to detect visibility.
- **Invisible OCR text (render mode 3, P1):** markable; the mark sits over the image.
- **Encrypted documents, copy forbidden (bit 5):** OPEN QUESTION OQ-TM-2: text selection for marking is allowed even when copying is forbidden; Copy stays disabled. The m2 spec must agree.
- **Huge pages and documents:** a page with 200,000 characters (the engine limit) uses windowed quad requests; only the pages in the selection are touched.
- **Overlapping marks:** allowed; Multiply makes overlaps darker, as in other readers.
- **Hostile input:** a loaded annotation with 10 million `QuadPoints` numbers is cut at 512 numbers by the protocol (64 quads) and the mark is shown from those, with a note in the panel; quads that are NaN, infinite or degenerate are dropped.

## 7. Undo and redo

Create, recolour, change opacity or thickness, delete, add or remove a comment: each is one command. A multi-page mark is one composite command. Rules in annotation-core section 8.

## 8. Journal entries

`AddAnnotation` with `kind` in `Highlight`, `Underline`, `StrikeOut`, `Squiggly`, `rect`, `style` (`colour`, `opacity`, `thickness`), and `payload`: `{ "quads": [[x0,y0,x1,y1,x2,y2,x3,y3], ...], "text": "<first 200 characters of marked text>" }`. The text excerpt is document content copied into the journal for the accessible name and recovery panel; it is capped and mentioned in the privacy notes. `UpdateAnnotation` for style changes. `DeleteAnnotation`.

## 9. Save behaviour

Writer host stage 4 ([save-pipeline](save-pipeline.md)): one indirect annotation object per mark with `/Subtype`, `/Rect`, `/QuadPoints`, `/C`, `/CA`, `/F 4`, `/T`, `/M`, `/CreationDate`, `/NM`, `/P`, `/Subj`, `/Contents` if any, `/Popup` if a comment, and `/AP << /N ref >>`. The text excerpt is **not** saved to the PDF. Highlight appearances use `/BM /Multiply`.

## 10. Engine and protocol additions (schema v5; id in the reading range)

**`GetTextQuads` (27).**

| Request field | Type | Limits |
|---|---|---|
| `page` | u32 | max 9999999 |
| `start` | u32 | max 199999 (character numbering of `GetPageText`) |
| `count` | u32 | 1 to 200000 |
| `firstQuad` | u32 | max 4095 |

| Reply field | Type | Limits |
|---|---|---|
| `totalQuads` | u32 | max 4096 |
| `truncated` | bool | more than 4,096 quads |
| `xs` | array f32 | maxCount 256 (64 quads x 4 corners), -1000000 to 1000000 |
| `ys` | array f32 | maxCount 256, same range, `sameLengthAs` `xs` |

Corners are in the order TL, TR, BL, BR; the array length must be a multiple of 4 (checked by `ReplyChecks`, which also verifies that `firstQuad + length/4 <= totalQuads` and that `start + count` is inside the page's character total). Worst-case frame about 3 KiB. Uses `FPDFText_GetLooseCharBox`, `FPDFText_GetCharAngle` and `FPDFText_GetMatrix`, which exist in the pinned `fpdf_text.h` (checked 2026-11-02); how well they describe rotated text is for the engine work package to test.

## 11. Localisation strings

| Key | en-US |
|---|---|
| `Markup_Highlight` | Highlight |
| `Markup_Underline` | Underline |
| `Markup_Strikeout` | Strikethrough |
| `Markup_Squiggly` | Squiggly underline |
| `Markup_AddComment` | Add comment |
| `Markup_Toolbar_Name` | Text markup |
| `Markup_Thickness` | Thickness {0} points |
| `Markup_Opacity` | Opacity {0} percent |
| `Markup_NoText` | There's no text to mark here. |
| `Markup_Failed` | Couldn't mark that text. Try again. |
| `Markup_Truncated` | Only the first part of a very long selection was marked. |
| `Markup_Added_Announce` | {0} added on page {1}: {2} |

Telemetry: none.

## 12. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-TM-1 | Loose-box height versus other readers' highlight height. | Opus after the Acrobat pass |
| OQ-TM-2 | Selection for marking when copying is forbidden. | User, Opus |
| OQ-TM-3 | Default highlight opacity and colour set. | Design |
| OQ-TM-4 | How a keyboard user reaches the floating mini toolbar. | Design |

## 13. Acceptance criteria

Shared criteria AC-01 to AC-25 in [annotation-core](annotation-core.md) also apply with these fixtures.

| Id | Criterion | Layer |
|---|---|---|
| TM-01 | `GetTextQuads` golden vectors and malformed frames (limits exceeded by one, array length not multiple of 4, unequal arrays, NaN) round-trip in C# and C++ and are rejected. | Unit (Protocol + Engine C++) |
| TM-02 | Quad construction on a horizontal single line: one quad, corners in TL, TR, BL, BR order, height equals the line's loose box, trimmed of trailing spaces. | Integration (engine) |
| TM-03 | Multi-line range gives one quad per line; a hyphenated line end is included; made-up line breaks are not the first or last character of a quad. | Integration |
| TM-04 | Rotated text at 30 and 90 degrees and vertical CJK give rotated quads whose corner order is TL, TR, BL, BR in the text's own frame. | Integration |
| TM-05 | `/Rotate` 0, 90, 180, 270 and CropBox origin (50, 30): the quads of a known word land on the word (compared with the character boxes) within 0.5 pt. | Integration |
| TM-06 | Column selection (Alt+drag) produces one quad per row and column slice. | Unit + Integration |
| TM-07 | Selection spanning three pages creates three annotations as one undo step; undo removes all three, redo restores them with the same ids. | Unit |
| TM-08 | Empty selection and text-less pages create nothing and show `Markup_NoText`. | Unit + UI journey |
| TM-09 | Appearance geometry: for each kind the generated stream's path equals the `MikePDF.Markup` geometry; highlight uses Multiply and the opacity in `/ca`/`/CA`; underline sits on the quad's bottom edge inset by half the thickness; squiggly wavelength and amplitude as specified. | Unit |
| TM-10 | Saved dictionary: `/QuadPoints` order, `/Rect` equals the union (expanded by half thickness for line kinds), `/AP /N` present, name values are name objects, `/F 4`, indirect object, `/Popup` only when a comment exists, no PDFium private key. | Unit (PdfWriter) + Fidelity (`qpdf --check`) |
| TM-11 | Fidelity, fresh PDFium and pdf.js: a highlighted word region has the marker colour blended with the page (hue within tolerance) and black text pixels remain dark (Multiply), SSIM >= 0.98 against the golden over the mark's bounding box. | Fidelity harness |
| TM-12 | Fidelity, Acrobat Reader and Edge: every mark kind is visible, in the right place, with Multiply behaviour, recorded in the matrix as pass, fail or **not measured**; no "invisible" cell. | Fidelity harness (manual-gated, R-15) |
| TM-13 | Reopen in MikePDF: the loaded model equals the model before save (kind, quads, colour, opacity, author, dates, contents). | Fidelity harness |
| TM-14 | Foreign marks made by PDFium/pdf.js/Acrobat fixtures load as foreign: deletable, replyable, not recolourable; the appearance bytes are unchanged after reply. | Integration |
| TM-15 | Click selects a mark; drag starting on a mark starts text selection; double-click selects a word. | UI journey |
| TM-16 | Recolour, opacity and thickness edits regenerate the appearance and are single undo steps. | Unit + Integration |
| TM-17 | Comment on a mark creates `/Contents` and a linked `/Popup`; removing the comment removes the popup in the next save. | Unit + Fidelity |
| TM-18 | Journey 3: highlight text, add a note, save, reopen: marks intact in MikePDF; marks visible in the PDFium and pdf.js renders. | UI journey + Fidelity |
| TM-19 | Keyboard-only: select text with Shift+arrows, Ctrl+H, Tab to the mark, Delete, Ctrl+Z, no mouse. | UI journey |
| TM-20 | UIA: element name and `RangeFromChild` return the marked text; announcement string matches `Markup_Added_Announce`. | UI journey (a11y) |
| TM-21 | Hostile loaded mark (10 million quad numbers, NaN quads) loads bounded and does not crash. | Integration |
| TM-22 | Permissions bit 6 clear disables the commands, palette reason shown. | Unit |
| TM-23 | Facing pages: marking text on the right page of a spread draws and saves correctly. | Integration |
| TM-24 | Performance: highlighting a 40-word selection on a 600-page document returns quads in < 50 ms from the engine and shows the overlay in the next frame (proposed; recorded, not asserted until measured). | Integration (perf) |

## Implementation notes: the engine side (W3-engine, schema 7, ADR-0005 A5)

`GetTextQuads` (27) is built: character numbering as `GetPageText`; quads from line grouping by baseline and direction, whitespace trimmed at both ends of a line, a line-end hyphen kept, rotated text in the text's own frame (from `FPDFText_GetMatrix`, since `FPDFText_GetCharAngle` runs the other way), output in the order top-left, top-right, bottom-left, bottom-right; 64 quads per reply from `firstQuad`, at most 4096 per range (`truncated`).
