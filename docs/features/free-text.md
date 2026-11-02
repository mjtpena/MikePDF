# Feature spec: free text (typewriter) annotations

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (free text, typewriter), §8.2, §12.4
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md), shared font metrics in `MikePDF.Markup`
- Related: [stamps](stamps.md), [forms](forms.md) (text typed into form fields is a different feature)

Conventions are in [README.md](README.md). Shared rules are in annotation-core.

## 1. Why this one is risky

S4 case A06a: a PDFium-made FreeText saved without an appearance is **invisible in pdf.js**; A06b: an appearance made with PDFium's `SetAP` had no `/Resources` and drew only because the fixture page happened to define `/Helv` (F4). Q06, written by MikePDF's own generator with its own resources, drew correctly in PDFium and pdf.js. Acrobat and Edge were not measured. Text is also where a screen rendering and a saved rendering are most likely to differ (fonts, wrapping, kerning), so layout is shared code and the character set is limited until fonts can be embedded (section 2.4).

## 2. User stories

1. As a form-filler with a flat scanned form, I click anywhere and type text onto the page.
2. As a reviewer, I add a boxed remark with a border and a pale fill beside a paragraph.
3. As anyone, I change the font, size and colour of text I added, and move or resize its box.
4. As a keyboard-only user, I add, edit and delete a text box without a mouse.
5. As a recipient, I see the text exactly where it was placed, in Acrobat Reader, Edge, Chrome and Firefox.

## 3. Detailed behaviour

### 3.1 Two kinds

| | Typewriter | Text box |
|---|---|---|
| PDF | `/FreeText` with `/IT /FreeTextTypeWriter` | `/FreeText` |
| Border and fill | none | optional border (colour, width 0 to 12 pt), optional fill colour |
| Width | grows with the text up to the page's right edge, then wraps | fixed by the user's drag; wraps to the width |
| Create by | click | drag a rectangle, or click (creates a default 180 x 40 pt box) |

A callout (a line from the box to a point, `/CL`) is **not** in v1 (OPEN QUESTION OQ-FT-6).

### 3.2 Properties

| Property | Values | Default |
|---|---|---|
| Font family | Sans (Helvetica), Serif (Times), Mono (Courier) | Sans |
| Bold, italic | on / off (maps to the standard 14 variants) | off |
| Size | 6 to 144 pt in steps of 1 (typed or chosen) | 12 |
| Text colour | palette or custom | black |
| Alignment | left, centre, right | left |
| Border colour and width | text boxes only | border off |
| Fill colour | text boxes only; none or a colour | none |
| Opacity | 10% to 100% | 100% |

These are the 12 base fonts that every PDF reader has: Helvetica, Times-Roman, Courier with Bold, Oblique/Italic and BoldOblique/BoldItalic. Their metrics (Adobe's published AFM widths for characters 32 to 255 of WinAnsi) are shipped as data in `MikePDF.Markup.Fonts`.

### 3.3 Layout (shared by editor, overlay and appearance)

One pure function lays out text: input text, font, size, inner width, alignment; output a list of lines with their x offsets and baselines. Rules:

- Inner width is the box width minus twice the padding (2 pt) minus twice the border width.
- Lines break at spaces by greedy fit using AFM advance widths (no kerning, no hyphenation). A word longer than the line breaks at character level. Explicit line breaks (`\n`, `\r\n`) are kept. A tab is stored as 4 spaces.
- Line height is 1.2 x size. The first baseline is placed so the font's ascender sits at the top padding (constant defined next to the AFM data).
- A typewriter's width is the widest line (up to the page edge).
- Box height is at least the text height plus padding; it never shrinks below the text, and the user can only make it taller.
- Text that would pass the bottom of the page (when the box is moved or the text is long) is **not clipped in the file**; the editor warns "Some text is outside the page."

This function is unit-tested with golden outputs and is the only text layout in the product, so the overlay and the saved appearance agree (annotation-core AC-13).

### 3.4 Characters

Only characters that exist in **WinAnsiEncoding** (Windows-1252: Latin letters with accents, common punctuation, the euro sign) can be drawn, because the standard 14 fonts are not embedded. Typing or pasting any other character (Cyrillic, Greek, CJK, Arabic, Hebrew, emoji) is refused with a notice: "This character can't be saved in a text box yet." Pasted text has the unsupported characters removed, and the notice says how many. OPEN QUESTION OQ-FT-1 is the biggest one in this spec:

| Option | Effect |
|---|---|
| A. WinAnsi only (this spec) | Simple, safe, and wrong for much of the world |
| B. Embed a subset of a system TrueType font | Correct for any script the font covers; needs a TrueType subsetter (about 1,500 lines of security-critical code in `MikePDF.PdfWriter`, or a vetted dependency under ADR), a `/ToUnicode` map, and bidi/shaping for Arabic and Indic scripts, which a plain glyph-per-codepoint layout can't do |
| C. Let PDFium create the text objects | Conflicts with ADR-0008 (the writer owns appearances); font embedding in PDFium embeds whole fonts |

Recommendation: ship A in M4 so the pipeline is proven, plan B as a separate work package with its own spec, and say plainly in the product that other scripts are not yet supported for text boxes. `/Contents` (the plain-text value) could hold Unicode, but a mismatch between the saved text and what is drawn is worse than refusing, so it is not used for that.

### 3.5 Creating and editing

- `tool.freeText` (and `tool.typewriter`, same tool with a mode): click or drag, then an in-place editor opens. The editor is a real XAML text box (so that the IME, spell check, caret browsing, text scaling and Narrator work), placed over the annotation and scaled with the zoom, using Arial, Times New Roman or Courier New, which are metrically compatible with the three base families. Its wrapping uses the text box's own layout, so wrapping while typing can differ by a character from the committed layout (OPEN QUESTION OQ-FT-3: write a custom editing control that uses the shared layout, at the cost of IME and accessibility work).
- **Commit:** Esc or clicking outside commits non-empty text; **Ctrl+Enter** commits; Enter inserts a line break. Empty text on commit removes the annotation and leaves no journal entry. After commit the overlay shows the shared layout.
- **Edit again:** double-click, Enter on the focused annotation, or F2.
- **Move, resize, restyle:** as annotation-core 3.4. Resize changes the wrap width; font size is not scaled by resizing. Text boxes resize with eight handles; typewriter text only moves (its width follows the text).
- **Limits:** 10,000 characters; 200 lines.
- Pressing Delete while editing deletes characters; Delete on the selected annotation (not editing) deletes the annotation.

### 3.6 Rotated pages

Text must appear upright in the displayed page. The appearance form's `/Matrix` rotates the content by the negative of the page's `/Rotate`, and `/Rect` is the unrotated-space box that encloses the rotated form (the standard mapping of `/BBox` through `/Matrix` onto `/Rect`). This avoids relying on a non-standard `/Rotate` key. How each reader treats it is not measured; fixtures for the four rotations are in the fidelity set (OPEN QUESTION OQ-FT-4).

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Text box tool / typewriter tool | none | `tool.freeText`, `tool.typewriter` |
| Edit text | Enter or F2 on the selected annotation | `annot.edit` |
| Commit | Ctrl+Enter, Esc | (editor) |
| Font size up / down (while selected or editing) | Ctrl+] / Ctrl+[ | `text.sizeUp`, `text.sizeDown` (proposed) |
| Bold / italic (while editing) | Ctrl+B / Ctrl+I | `text.bold`, `text.italic` |
| Create at the current position | none | `annot.newFreeText` |

`annot.newFreeText` is the keyboard way to create: a default-size box at the top-left of the visible page area, opened in the editor. Pen: tap or drag with the pen to create; handwriting recognition into text is not in scope. Touch: tap creates a default box; the on-screen keyboard opens.

## 5. Accessibility

- UIA: localized control type "text box", name "text box by {author}, {first 80 characters}", `Value` pattern with the full text (writable unless read-only), `Invoke` to edit.
- The in-place editor is a standard text input named "Text box contents", with the IME and screen-reader text navigation of a real control. Its font size follows the zoom and the user's text scaling does not affect the page text.
- Formatting controls have names and states (Bold: on). The notice for refused characters is announced politely.
- Announcements: "Text box added on page 3", "Text box deleted", "Text outside the page".
- Contrast of border and handles: annotation-core. User-chosen text and fill colours are the user's; the properties pane warns when text/fill contrast is below 3:1 ("Low contrast") without blocking.

## 6. Error states

| Situation | Behaviour |
|---|---|
| Unsupported character typed or pasted | Refused with the notice; the rest of a paste is kept. |
| Text too long | Refused beyond 10,000 characters or 200 lines. |
| Text extends past the page bottom | Warning chip "Some text is outside the page."; saved as is. |
| Permissions or size limits | annotation-core 3.8. |
| Save fails | Journal keeps the text. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** 3.6; origin (50, 30) fixture; the box clamps to the displayed box, not the MediaBox.
- **Facing pages:** the editor is placed through `PageProjection` over the right page.
- **Zoom levels from 10% to 6400%:** the editor's font size scales; at very low zoom the editor is still usable (min 10 px effective).
- **Huge font in a tiny box:** the box grows to fit.
- **Loaded foreign FreeText** (different fonts, rich text `/RC`, callouts, rotation): drawn by PDFium from its own appearance; MikePDF allows delete, reply and moving; it does not edit the text (annotation-core 3.2).
- **Loaded owned FreeText from an earlier MikePDF session:** the text, font and size are rebuilt from `/Contents`, `/DA`, `/DS` and `/Q` when the appearance matches what MikePDF writes; if they disagree, it's treated as foreign.
- **Encrypted or signed documents:** annotation-core 3.8.
- **Hostile input:** a loaded `/DA` with a 1 MB string, a font size of 1e9 or NaN, a `/Contents` of 50 MB: bounded by the protocol limits (256-byte `/DA`, 16 KiB contents) and treated as foreign when out of range.
- **Right-to-left and mixed text:** refused in M4 as non-WinAnsi (OQ-FT-1).
- **Kerning and ligatures:** none in the saved appearance; the editor may show kerning; documented difference.

## 8. Undo and redo

Create (with its first text), commit of an edit session, move, resize, restyle (one command per property change, grouped when changed together), delete. Character-level undo is the text box's own while editing and is discarded on commit. Rules in annotation-core section 8.

## 9. Journal entries

`AddAnnotation` `kind: "FreeText"`, `payload: { "typewriter": false, "text": "...", "family": "Sans", "bold": false, "italic": false, "size": 12, "textColour": "#000000", "align": "left", "borderWidth": 0, "borderColour": null, "fillColour": null }`. `UpdateAnnotation` for any changed property including `text`, with `previous` values. The text is user content ([save-pipeline](save-pipeline.md) privacy notes).

## 10. Save behaviour

One indirect `/FreeText` annotation: `/Rect`, `/Contents` (the text with `\r` line breaks), `/DA` (for example `/Helv 12 Tf 0 0 0 rg`), `/DS` (a CSS-style string kept in sync), `/Q`, `/IT /FreeTextTypeWriter` when applicable, `/BS << /W n >>`, `/C` (border colour) and `/IC` (fill) when set, `/CA`, `/F 4`, the common keys, and `/AP << /N >>` whose form XObject has its own `/Resources << /Font << /Helv ... >> >>` with the font dictionaries `/Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding` (never inherited from the page; S4 F4) and ExtGState for opacity. The appearance draws the fill, the border, then each line with `BT ... Tj ET`. Whether to add the fonts to `/AcroForm /DR` for Acrobat's benefit when it edits the annotation is OPEN QUESTION OQ-FT-2; the default is not to touch `/AcroForm` (creating an empty one can turn a plain document into a "form").

## 11. Engine and protocol additions

None. Reading existing FreeText uses `GetAnnotation` (`defaultAppearance`, `alignment`, `contents`, `colour`, `interiorColour`, `borderWidth`).

## 12. Localisation strings

| Key | en-US |
|---|---|
| `FreeText_Tool` | Text box |
| `Typewriter_Tool` | Typewriter |
| `FreeText_Editor_Name` | Text box contents |
| `FreeText_Font_Sans` / `_Serif` / `_Mono` | Sans serif / Serif / Monospace |
| `FreeText_Bold` / `_Italic` | Bold / Italic |
| `FreeText_Size` | Size {0} points |
| `FreeText_Align_Left` / `_Center` / `_Right` | Align left / Align centre / Align right |
| `FreeText_Border` / `_Fill` | Border / Fill |
| `FreeText_BadChar` | This character can't be saved in a text box yet. |
| `FreeText_BadChars_Pasted_One` / `_Other` | 1 character was removed because it can't be saved in a text box yet. / {0} characters were removed because they can't be saved in a text box yet. |
| `FreeText_TooLong` | A text box can hold up to {0} characters. |
| `FreeText_OutsidePage` | Some text is outside the page. |
| `FreeText_LowContrast` | Low contrast between the text and the fill |
| `FreeText_Added_Announce` | Text box added on page {0} |
| `FreeText_Deleted_Announce` | Text box deleted |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-FT-1 | Characters outside WinAnsi (3.4): ship WinAnsi only in M4 and plan font embedding separately? | User (product), Opus |
| OQ-FT-2 | Add the base fonts to `/AcroForm /DR` for Acrobat. | Opus after the Acrobat pass |
| OQ-FT-3 | Custom editing control versus XAML `TextBox` while editing. | Opus |
| OQ-FT-4 | Reader behaviour for the rotated-matrix appearance on rotated pages. | Opus after fixtures |
| OQ-FT-5 | Maximum lengths (10,000 characters, 200 lines). | Design |
| OQ-FT-6 | Callout support. | User (product) |

## 14. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply.

| Id | Criterion | Layer |
|---|---|---|
| FT-01 | AFM data: widths for all 12 fonts and characters 32 to 255 equal the published values (checksum test against a committed table); the table covers every WinAnsi code. | Unit (`MikePDF.Markup.Tests`) |
| FT-02 | Layout golden tests: 40 cases covering wrapping at spaces, long words, explicit breaks, tabs, alignment left, centre and right, bold and italic fonts, size 6 and 144, zero-width and one-character-wide boxes; outputs equal the committed golden lines. | Unit |
| FT-03 | WinAnsi filter: every code point is classified; typing and pasting unsupported characters are refused or removed with the right resource key and count. | Unit |
| FT-04 | Box growth: height never below the text; typewriter width follows the widest line up to the page edge, then wraps. | Unit |
| FT-05 | Rotation: for `/Rotate` 0, 90, 180, 270, the form `/Matrix` and `/Rect` make the text upright in the displayed orientation (rasterise the appearance with the page transform and compare the glyph orientation by template matching). | Unit + Integration |
| FT-06 | Saved dictionary and appearance: `/DA` valid, appearance has its own `/Resources` with the font dictionaries and ExtGState, no reference to the page's resources, indirect object, `/IT` only for typewriter, `qpdf --check` passes on classic and xref-stream bases. | Unit (PdfWriter) + Fidelity |
| FT-07 | The cold-save failures do not recur: the appearance is present and the text visible in fresh PDFium and pdf.js with no page rendered before saving (S4 A06a, A06b). | Fidelity harness |
| FT-08 | Fidelity, PDFium and pdf.js: text position equals the editor layout within 0.5 pt per line; region SSIM >= 0.95 against the golden (glyph rasteriser differences allowed; threshold to be confirmed on the first run). | Fidelity harness |
| FT-09 | Fidelity, Acrobat Reader and Edge: text visible, at the right place, upright on all four rotations; recorded per cell as pass, fail or **not measured**. | Fidelity harness (manual-gated, R-15) |
| FT-10 | Reopen in MikePDF: an owned FreeText round-trips (text, family, bold, italic, size, colours, alignment, border, fill); a modified appearance is treated as foreign. | Fidelity harness |
| FT-11 | Editor commit rules: Esc and click-outside commit non-empty text; Ctrl+Enter commits; Enter inserts a line break; empty text leaves nothing behind. | UI journey |
| FT-12 | Undo and redo for create, edit session, move, resize, restyle and delete restore exact model state. | Unit |
| FT-13 | Journal replay after a crash reproduces the text box with all properties. | Integration |
| FT-14 | Keyboard-only: `annot.newFreeText`, type, Ctrl+Enter, Tab to it, F2, change size with the shortcuts, Delete, undo. | UI journey |
| FT-15 | UIA: name, `Value` pattern content, editor accessible name and IME composition works in the editor (manual-gated for IME). | UI journey (a11y) |
| FT-16 | Hostile loaded FreeText (huge `/DA`, NaN size, 50 MB contents) loads bounded as foreign. | Integration |
| FT-17 | Low-contrast warning appears below 3:1 text/fill contrast and not above. | Unit |
| FT-18 | Text past the page bottom shows the warning and is saved unclipped. | Unit |
| FT-19 | Facing mode and 400% zoom: the editor sits over the text and typing keeps up (no frame over 50 ms while typing a 200-character string). | Integration (perf) |
