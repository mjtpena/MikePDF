# 08 Markup toolbar, annotation editing, sticky notes with replies, ink and pen mode

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 1 (highlight three passages, add comments), 2 (sticky notes with replies), 4 (pen user: ink and highlighter, finger scrolls, undo and redo, no lag), 6 (add a comment with a screen reader)
- Spec: master prompt §2.1 (markup), §8.2 (annotations), §8.3 (ink and pen), §11 (ink latency at most 25 ms). ADR-0007 (ink stack, proposed), ADR-0008 (the writer owns annotation dictionaries and appearance streams)
- Milestone: M4. Nothing here exists in the current app **[repo: STATUS "No annotations, forms, printing or save"]**.

## 1. Purpose

Mark a document up faster than any other reader, with a mouse, a keyboard, touch or a pen, and read it back as a conversation. Four connected pieces:

1. The **selection toolbar**: select text, mark it in one click.
2. The **markup command bar**: the full tool set, in the same 40 epx row.
3. **Notes with replies**: a note popup and a thread in the right pane.
4. **Ink and pen mode**: the pen inks without a mode switch; the finger scrolls.

### Principles specific to this screen

| # | Rule |
|---|---|
| M1 | **Text markup needs no mode.** Select, then act. Markup mode is for drawing tools and for people who like a visible toolbar |
| M2 | **One use, then Select**, unless the tool is pinned (double-click the tool, or hold `Shift` when choosing it). A pinned tool shows a small pin glyph. This prevents "I marked a page by accident" |
| M3 | **Tool settings are remembered** per tool, across documents and sessions (Edge resets them per document **[user report]**) |
| M4 | **Everything is undoable**, one step per user gesture (not per stroke point, and not per page when a selection spans pages) |
| M5 | **The saved file looks like the screen in other viewers** (§0.3): MikePDF writes every appearance stream |
| M6 | The pen never needs a UI action to start inking |

## 2. Layout

### 2.1 The markup command bar (Markup mode on, 1280 epx wide)

```text
+--------------------------------------------------------------------------------------------+
|[M] Contract v3.pdf x | +                                                    [_]  [[]]  [X]  |
+--------------------------------------------------------------------------------------------+
|[=]|[<] 12/40 [>]|[-] 125% [+]|[sel]| [HL v][U][S] |[note][T]| [pen v][hlp][era] |[shape v][stamp v]|[undo][redo]| [Done] |
+--------------------------------------------------------------------------------------------+
   Left groups stay. Centre groups swapped in place (cross-fade). Right groups (Print, Commands, Details) go to overflow
   in Markup mode so that Done stays visible. Row height and the page well do not change.
```

| Group | Buttons | Notes |
|---|---|---|
| Select | **Select** (`markup.select`) | Default tool. Toggle |
| Text markup | **Highlight** (split: colour swatches and styles), **Underline**, **Strikethrough** | Each applies the last colour used for that style. **Squiggly** is in the Highlight split menu. All act on the current selection, or arm the tool so that the next text selection is marked on mouse-up |
| Notes | **Note** (`markup.note`), **Text box** (`markup.freeText`) | |
| Draw | **Pen** (split: colour, size, style), **Highlighter pen**, **Eraser** | Pen and highlighter have remembered colour and size |
| Shapes and stamps | **Shapes** (split: rectangle, ellipse, line, arrow, polygon, polyline), **Stamp** (split: built-in stamps, **Image stamp...**) | |
| Edit | **Undo**, **Redo** | |
| Done | **Done** (`markup.toggle`) | Leaves Markup mode. `Esc` does the same when no tool is active |

Width priority at M (800 to 1199 epx): Select, Highlight, Note, Pen stay visible; Underline, Strikethrough, Text box, Highlighter pen, Eraser, Shapes, Stamp go into a **More tools** menu in that order. At S and XS the whole group is one **Markup tools** menu.

### 2.2 The selection toolbar (floats over selected text, no mode)

```text
                       +-------------------------------------------------------------+
                       | (Y) (G) (B) (P) (O) | [U] [S] | [note] | [copy] | [...]      |
                       +-------------------------------------------------------------+
                                 ^ highlight colours: last-used first      ...: squiggly, remove markup, more colours
   ...the company's [contingent liabilities are disclosed in note 12] and the
                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ selection
```

Above the selection's first line, 40 epx high, `OverlayCornerRadius`, shown after the mouse is released or the selection handles settle (touch). The six swatches show colour **and** carry the colour name in the tooltip and accessible name. A swatch that is the current default has a tick glyph, so it's not colour alone.

### 2.3 A note: popup and thread

```text
   page                                                       right pane: Comment tab
   +---------------------------------+                        +----------------------------+
   | ...contingent liabilities  [N2] |                        | Comments                   |
   |                      +--------------------------+        | [Dana R] Dana  2 h ago     |
   |                      | (DR) Dana    2 hours ago |        |  Check against note 12.    |
   |                      | Check against note 12.   |        |                            |
   |                      | -------------------------|        |   [MP] Mike   1 h ago      |
   |                      |   (MP) Mike  1 hour ago  |        |   Agreed, see schedule 4.  |
   |                      |   Agreed, see schedule 4.|        |                            |
   |                      | [ Reply...               ]|        | [ Reply...              ]  |
   |                      | Status [None v]  [Delete] |        | Status [None v]   [Delete] |
   |                      +--------------------------+        +----------------------------+
   +---------------------------------+
```

Note popup: 320 epx wide, anchored to the note icon, `Flyout`-style, light-dismiss. N2 is the note icon with a 2-reply badge.

### 2.4 First time a user adds a comment (Q11)

```text
                      +--------------------------------------------+
                      | Commenting as Mike Pena         [ Change ]  |  <- one line, above the text box, only until confirmed once
                      | [ Add a comment                          ]  |
                      +--------------------------------------------+
```

### 2.5 Ink with the pen (Surface, 1280 x 800 epx), no UI action needed

```text
+--------------------------------------------------------------------------------------------+
|[=]|[<] 12/30 [>]|...|[pen: black 2.0 v] (shown in the command bar when the pen is in range) | 
+--------------------------------------------------------------------------------------------+
|      +----------------------------------------------+                                      |
|      |  Lecture 4                                    |   pen tip: strokes appear immediately |
|      |  ~~~~~~~~ (ink, pressure-sensitive width)     |   finger: scrolls and pinches        |
|      |      ____/\____  ink layer above the tiles     |   palm: ignored while pen in range   |
|      +----------------------------------------------+                                      |
+--------------------------------------------------------------------------------------------+
```

When the pen comes in range, the command bar's centre shows a compact **pen chip** (colour swatch, size, a menu) instead of the full Markup mode, so no chrome is added. Hovering shows the tip ring.

### 2.6 Pen options popover (the Pen button's drop-down, or the chip)

```text
   +------------------------------------------+
   | Pen    Highlighter    Eraser             |   segmented, 3 items
   | Colour  (K) (R) (B) (G) (+ more...)      |   black, red, blue, green + custom
   | Size    [----o-------]  2.0 pt           |   slider + value
   | Opacity [-----------o]  100%             |   pen only
   | [x] Pen writes ink by default            |   -> Settings
   | Barrel button: Select    Eraser end: Erase|   links to Settings, Markup and pen
   +------------------------------------------+
```

### 2.7 Format tab (right pane): properties of the selected annotation

```text
 +--------------------------------+
 | Comment | Format               |
 | Highlight                      |
 | Colour  (Y) (G) (B) (P) (O)... |
 | Opacity [---------o--]  100%   |
 | Author  Mike Pena              |
 | Modified  2 Nov 2026, 14:05    |
 | Position (for shapes, stamps): |
 |   X [  72.0 ] Y [ 300.5 ] pt   |
 |   W [ 120.0 ] H [  40.0 ] pt   |
 | Line   [--o----] 1.5 pt  Style v|
 | [ Delete annotation ]          |
 +--------------------------------+
```

The Format tab also gives a **pointer-free way** to set position and size (WCAG 2.5.7).

## 3. Behaviour

### 3.1 Text markup (highlight, underline, strikethrough, squiggly)

1. **With a selection:** click a swatch or a tool, or press `Ctrl+H` (highlight, last colour), `Ctrl+U` (underline), `Ctrl+Shift+X` (strikethrough). The selection is marked, the selection stays selected for 600 ms and then clears, and the undo bar is **not** shown (undo is `Ctrl+Z`; the live region says "Highlighted, yellow, page 41").
2. **With the tool armed (Markup mode, or pinned):** each text selection is marked on mouse-up with no extra click.
3. **Multi-line and multi-page selections.** One annotation per page. They are one undo step ("Highlight on pages 4 to 5"). The annotation's quad points follow the text's lines.
4. **Highlight is multiply-blended**, so text beneath stays readable in every viewer (appearance stream with a multiply blend mode; **fidelity-test in Acrobat Reader, Edge, Chrome and Firefox**, S4).
5. **Re-marking** text that already has a highlight of the **same colour** with `Ctrl+H` removes that highlight (toggle) **[design; discoverability risk: see OPEN QUESTIONS]**. A different colour adds a second highlight (most recent on top). The overflow menu of the selection toolbar has **Remove markup**.
6. **Find, then mark.** When Find has a current hit, `Ctrl+H` marks the hit ([05](05-find.md)).
7. **Extending or trimming an existing highlight** is not supported in v1.0; delete and re-mark.
8. Colour and style defaults per style are remembered (M3).

### 3.2 Notes (sticky notes with replies)

1. **Add:** select text and press `Ctrl+M`, click **Note** in the selection toolbar, or use the Note tool and click a point. A note on a selection is anchored at the selection's start and the selected text is highlighted (like Edge's note on a selection **[src: [Edge PDF docs](https://learn.microsoft.com/deployedge/microsoft-edge-pdf)]**). A note at a point is a plain note icon. The popup opens with the text box focused.
2. **Author.** The first time, one line above the text box shows "Commenting as {Windows display name}" with **Change** (Q11). After the user types in a note, that is taken as confirmed, and the line is gone. The name can be changed in Settings, Markup.
3. **Popup.** Header: initials circle (24 epx; colour from a hash of the author name, never the only cue, the name is written), author, time. Body: text box (grows to 8 lines, then scrolls). Replies below, indented 16 epx. A **Reply** box at the bottom. **Status** drop-down: None, Accepted, Rejected, Cancelled, Completed (§8.2). **Delete** (the whole thread, with the undo bar).
4. **Reply:** type in Reply, `Ctrl+Enter` or **Reply** posts. Replies are annotations of their own, linked to the parent (`/IRT`, `/RT /R`). They show the replier's name and time. A reply's status change is a state-model annotation (ADR-0008 owns the dictionaries).
5. **Close** the popup with `Esc`, a click outside, or **Done**. A note whose text is empty **and** that has no replies is discarded on close (no empty icons). The text is saved with each pause in typing (700 ms) into the edit journal, not into the file.
6. **Editing someone else's text.** A comment written by another author can be edited, but the first edit asks "This comment was written by {name}. Edit it anyway?" **[design; OPEN QUESTION]**. Replying is always allowed.
7. **Right pane.** The **Comment** tab shows the selected annotation's thread with the same controls and more room. With nothing selected it says "Select a comment to see its thread." The Annotations pane ([06](06-side-panes.md)) is the list.
8. **Note icons** are 24 epx glyphs on the page (hit area 36 epx), drawn by the UI layer and also in the saved appearance stream. They don't scale with zoom below 75% or above 200% (clamped), so they stay clickable.
9. **Replies on a document with a signature**: handled by the save pipeline's pre-save warning (§7.5 step 5), not at typing time.

### 3.3 Free text (text box)

- Click for an auto-width box, or drag for a fixed box. Type immediately. The box is an editable surface drawn by MikePDF (not a PDFium widget). Controls in the Format tab and a floating mini bar: font (Helvetica, Times, Courier: the standard fonts the appearance stream can reference without embedding), size 8 to 72 pt, colour, alignment, border, fill.
- **Characters outside the font's encoding** can't be drawn by a standard font. The editor shows a one-line warning "Some characters can't be saved with this font" and keeps them in the box; to save them, the writer would need to embed a font subset. **OPEN QUESTION** for ADR-0008 (does the writer embed a subset of a system font for non-Latin text?). Until it does, v1.0 may restrict the text box to characters the font supports and say so.
- `Enter` inserts a new line. `Esc` or a click outside commits. `Ctrl+Enter` also commits. An empty box is discarded.

### 3.4 Shapes and stamps

- **Shapes:** drag to draw (rectangle, ellipse, line, arrow). `Shift` constrains (square, circle, 15-degree angles). Polygon and polyline: click points, double-click or `Enter` to finish, `Esc` to cancel. Style (line colour, width, fill, opacity, arrow ends) in the Format tab and the shape's mini bar. PDFium can't create Line, Polygon or PolyLine **[repo: ADR-0008]**, so `PdfWriter` writes them.
- **Stamps:** a built-in set of text stamps (Approved, Not approved, Draft, Final, Confidential, For comment, Reviewed, Void **[design: wording is a placeholder]**) and **Image stamp...** from a PNG or JPEG file (the file is read by the broker, and the image bytes go to the writer as the annotation's content; the image is size-capped to 8 MP and downsampled above it **[design]**). Place with a click or by dragging a box. Aspect is locked for images (`Shift` unlocks).
- **Fill and Sign** (a handwritten-style signature image) is P1 and **not** part of this screen.

### 3.5 Selecting and editing annotations

1. **Select** with a click (the Select tool, or any tool's pointer when the click lands on an annotation, with `Alt` held to pick through to the page). A selected annotation shows a focus outline (two rings), eight resize handles for boxes (shapes, free text, stamps), and endpoints for lines. Text markup shows its outline only.
2. **Mini bar** above the selection: colour, opacity, **Add comment**, **Delete**, **More**.
3. **Move** by dragging the body (or arrow keys: 1 pt, `Shift` for 10 pt). **Resize** by the handles, or the Format tab. A drag that would put the annotation off the page clamps to the page's edge.
4. **Tab and Shift+Tab** (page focused) cycle annotations on the page in reading order, with form fields interleaved by position ([09](09-forms.md)). `Enter` edits (opens a note, enters a text box for typing, selects a shape for the Format tab). `Delete` removes. `Esc` deselects.
5. **Copy, cut, paste** work for annotations (`Ctrl+C`, `Ctrl+X`, `Ctrl+V`) within and across documents. Paste places at the pointer's position or, from the keyboard, offset 12 pt from the original.
6. **Lock** (`annot.lock`): stops accidental edits (the `Locked` flag). Locked annotations show a lock glyph in the list and can't be moved or deleted until unlocked.
7. **Order:** Bring to front, Send to back (menu).
8. **Read-only documents and permissions.** If the owner's permissions forbid annotating (§6.4 `PermissionDenied`), Markup and all marking commands are disabled with the reason "The document's owner doesn't allow adding comments." The document can still be read.

### 3.6 Ink and pen

**Pen-first (principle M6).**

1. If the device has a pen digitizer and **Pen writes ink by default** is on (Q5; default on), then when the pen **touches the page**, it inks. No tool needs to be selected; Markup mode doesn't need to be on. The first time this happens in a session on a device, a non-blocking `InfoBar` (the pen is a user action, so it is allowed to explain) says "The pen now writes ink. Use your finger to scroll. You can change this in Settings." with **Settings** and **Dismiss**, once per device.
2. The pen uses the **last pen** (colour, size). Pressing **Pen**, **Highlighter pen** or **Eraser** in the markup bar or the chip selects which one.
3. **Pen mode** (`markup.penMode`) is a toggle for the same behaviour on devices with no digitizer detected, or to turn it off temporarily. Off: the pen acts like a mouse (selects text, clicks links, drags annotations).
4. **A mouse or a finger never inks** unless the Pen tool is explicitly armed in Markup mode (then the mouse draws with fixed width). On touch-only devices, the armed Pen lets a finger draw, and a two-finger drag scrolls.
5. **Palm rejection.** While the pen is in range (hovering or touching), **touch contacts are ignored** entirely (no ink, no scroll, no zoom). The rule stays for 500 ms after the pen leaves range, because the palm often lifts last. Contacts with a large area (more than about 20 mm across **[guess]**) are rejected even with no pen in range.
6. **Tap versus stroke.** A pen contact shorter than 150 ms and moving less than 2 epx is a **tap**: it activates links and fields and selects annotations. It doesn't make a dot. (Dots are made with a deliberate press of at least 150 ms.)
7. **Capture.** The pen's intermediate points (`GetIntermediatePoints`) are used, with pressure and tilt; predicted points (`PointerPredictor`) reduce latency, are drawn faintly and are replaced when the real points arrive **[ADR-0007 working assumption]**. Live ink draws on a dedicated ink layer above the tiles. **Pen-to-pixel latency target: at most 25 ms** on Surface Pro-class hardware (§11). It's not measured yet (S5 needs a Surface, a pen and a 240 fps camera).
8. **Smoothing:** the stroke is smoothed in `MikePDF.Ink` (a light spline), width follows pressure (`width = size x (0.35 + 0.65 x pressure)`, a **[design]** curve to be tuned on the device), and the **same** stroke model draws on screen and produces the saved `/InkList` and a variable-width appearance stream (ADR-0007, ADR-0008). A mouse draws constant width.
9. **Highlighter pen:** constant width (default 16 epx), multiply blend, flat nib, 50% default opacity, a second stroke over an earlier one doesn't darken the overlap more than once (**[design: the saved appearance uses one path per stroke group, so overlap darkening in other viewers is a fidelity question]**).
10. **Eraser.** The Eraser tool or the pen's eraser end removes **whole strokes** the tip passes over (a 16 epx ring shows the hit radius). One drag is one undo step. **Partial erasing** (cutting a stroke) isn't in v1.0.
11. **Barrel button** (default: held for the **Select** tool; configurable), **eraser end** (default: Eraser; configurable) ([Settings, Markup and pen](13-settings-and-policies.md)).
12. **Batching into annotations** (§8.3 "long sessions are batched"): consecutive strokes on the **same page**, same tool and style, with each pen-up within **5 s** of the previous one, are appended to the same ink annotation (a `/InkList` with several paths). A different page, tool or style, or a pause over 5 s, starts a new annotation. The Annotations list shows one row ("Ink, 7 strokes"). The **eraser can still remove single strokes** inside an annotation. **[design; the 5 s window is a guess to tune]**
13. **Undo and redo are per stroke** (`Ctrl+Z` removes the last stroke, even within a batched annotation) and per eraser drag. The Undo and Redo buttons in the command bar work the same way (and are 48 x 48 in Touch density, so a finger can reach them while the pen is in the other hand). The journal records each stroke as it is committed, so a UI crash loses at most the stroke in progress (journey 8).
14. **Finger scrolling and pinch** always work. A finger drag is never ink while the Pen is not armed.
15. **Zoom while inking:** the ink layer scales on the GPU with the page (no re-rasterising of live ink during a gesture) and strokes keep their size in page units.
16. **Selecting ink** after drawing: use the Select tool (or the barrel button) to click an ink annotation; its bounding box has handles. The ink annotation can be moved and resized as a whole, and recoloured.

### 3.7 Undo, journal, save

- Every gesture above is an edit command (§5.2 rule 6) appended to the journal before it is acknowledged (§7.4). The journal holds the text the user typed and stroke geometry, **not** the document.
- **Save** (`Ctrl+S`) writes an incremental update (default, §7.5). Annotations are written by `PdfWriter` with appearance streams. Close with unsaved edits asks first ([README 9.3](../README.md#93-undo-save-and-dirty-state)).
- **Fidelity:** each annotation type is round-trip tested in PDFium, pdf.js, Acrobat Reader and Edge (§12.4). Types with known gaps (S4) must not be offered until their writer path exists. If **Squiggly** or **Highlighter pen** appearances fail that matrix, hide them (don't ship a tool that looks different elsewhere).

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Entering Markup mode must be instant: the markup bar is already built, and the ink layer is created at the first pen contact in range (not at the first stroke). While the document's annotations load, existing ones appear as they arrive; adding new ones isn't blocked. The first stroke never waits on the engine, because strokes are committed to the UI-side model and sent to the engine afterwards |
| **Empty** | No annotations: Annotations pane says "No comments yet. Select text and press Ctrl+H to highlight it, or Ctrl+M to add a note." Right pane Comment tab: "Select a comment to see its thread." Selection toolbar with an empty selection never appears |
| **Error** | If an edit command fails in the engine, the change is undone in the UI and an `InfoBar` says "Couldn't add the highlight." with **Try again** and **Details**. If the engine crashes, edits are replayed from the journal ([14](14-errors-and-recovery.md)); a stroke in progress is kept in the UI and committed when the engine returns. A note's text is never lost by a failed post: the text stays in the box |
| **Keyboard focus** | Tab order within the markup bar: Select, text markup group (arrows within groups), notes, draw, shapes, edit, Done. Tab from the page cycles annotations. A selected annotation has the two-ring focus visual and its handles. After a highlight, focus stays on the page. After closing a note popup, focus returns to the note icon. The popup's focus order: text box, replies, Reply box, Status, Delete |
| **High contrast** | Active tool uses `Highlight` backplate and `HighlightText`. Swatches get a 1 epx `WindowText` outline and a text name in the tooltip. Handles and outlines on the page use `Highlight` (G-HC6). The annotation's own colours are document content and stay as they are; in the optional page high-contrast mode they stay too, with a 1 epx `WindowText` outline added to text-markup annotations so they remain visible |
| **225% text scale** | The markup bar is icon-only; tooltips scale. The note popup widens to 360 epx and grows in height; its status drop-down and buttons wrap onto a second row. The Format tab scrolls. The pen options popover grows and scrolls |
| **Narrow window** | See section 2.1: tools collapse into **More tools**, then into one **Markup tools** menu. The right pane overlays; the note popup stays a popup but is full width minus 16 epx. The selection toolbar wraps its overflow into **...**. At XS the selection toolbar shows only the three most recent colours plus **...** |
| **Touch** | Selection handles 44 epx; selection toolbar buttons 44 x 44; annotation resize handles 44 x 44 hit with 12 px visible; note icon hit 44 epx. A **long-press** on text starts a selection; a **tap on a note icon** opens it. Palm rejection and the pen rules in 3.6 apply. Hold-and-drag to draw a marquee for multiple annotations isn't supported by touch: use the Annotations pane's multi-select |

## 5. Accessibility

### 5.1 UIA names

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Markup toggle | Button (toggle) | "Markup" | `MarkupButton` |
| Select tool | Button (toggle) | "Select" | `ToolSelect` |
| Highlight | SplitButton | "Highlight, {colour}" | `ToolHighlight` |
| Underline, Strikethrough | Button (toggle) | "Underline, {colour}", "Strikethrough, {colour}" | `ToolUnderline`, `ToolStrike` |
| Note, Text box | Button (toggle) | "Note", "Text box" | `ToolNote`, `ToolFreeText` |
| Pen | SplitButton | "Pen, {colour}, {size}" | `ToolPen` |
| Highlighter pen, Eraser | Button (toggle) | "Highlighter pen", "Eraser" | `ToolHighlighterPen`, `ToolEraser` |
| Shapes, Stamp | SplitButton | "Shapes", "Stamp" | `ToolShapes`, `ToolStamp` |
| Undo, Redo | Button | "Undo {last action}", "Redo {last action}" | `UndoButton`, `RedoButton` |
| Done | Button | "Done marking up" | `MarkupDoneButton` |
| Selection toolbar | ToolBar | "Text markup" | `SelectionToolbar` |
| Swatch | Button | "Highlight yellow" etc. | `Swatch_{colour}` |
| Annotation on the page | Custom, with the localized control type "highlight", "note", "ink", etc. | "{Type}, {colour}, page {n}, by {author}: {excerpt}" | `Annot_{id}` |
| Note popup | Group | "Note by {author}" | `NotePopup` |
| Note text | Edit | "Comment" | `NoteText` |
| Reply box | Edit | "Reply" | `ReplyBox` |
| Status | ComboBox | "Status" | `NoteStatus` |
| Comment thread (right pane) | List | "Comments on this note" | `CommentThread` |
| Format fields | Edit, Slider, ComboBox | "X position in points", "Opacity", "Line width", ... | `Format_*` |
| Pen chip | Button | "Pen, {colour}, {size}. Opens pen options." | `PenChip` |

### 5.2 Narrator, intended (not yet tested)

| Action | Announcement |
|---|---|
| Highlight | "Highlighted, yellow, page 41" (polite) |
| Undo | "Undid highlight" |
| Open a note | "Note by Dana, 2 hours ago. Check against note 12. 1 reply. Comment, edit." |
| Post a reply | "Reply added" |
| Change status | "Status: Completed" |
| Delete | "Deleted note and 2 replies. Undo available" (polite) |
| Pen first use | "The pen now writes ink. Use your finger to scroll." (the InfoBar's text, polite) |
| Ink saved | Strokes are not announced individually. On pen-up after a batch ends (the 5 s window closes), polite "Ink added, page 12" |
| Enter Markup mode | "Markup mode on. Select a tool." Exit: "Markup mode off." |

### 5.3 Keyboard completeness for markup (scenario 6)

A screen-reader user can mark up without a pointer: move the caret and select with `F7` caret selection, then `Shift+Arrows`; press `Ctrl+H`; press `Ctrl+M`, type, `Ctrl+Enter` to post, `Esc`. The Annotations pane provides navigation and deletion. Free-hand ink and shape drawing are **pointer-based** by nature. The Format tab's position and size fields give a keyboard way to place and size shapes and text boxes. Ink has no keyboard equivalent, and that is stated in Help ("Drawing with the pen needs a pen, mouse or touch").

## 6. Keyboard path

| Task | Keys |
|---|---|
| Enter or leave Markup mode | `Ctrl+Shift+M` (leave: `Esc` with no active tool, or **Done**) |
| Highlight selection | `Ctrl+H` |
| Underline / strikethrough | `Ctrl+U` / `Ctrl+Shift+X` |
| Add a note on the selection | `Ctrl+M` |
| Post a reply | `Ctrl+Enter` in the Reply box |
| Next / previous annotation on the page | `Tab` / `Shift+Tab` (page focused) |
| Edit the focused annotation | `Enter` (or `F2`) |
| Delete | `Delete` |
| Nudge | `Arrow` (1 pt), `Shift+Arrow` (10 pt) |
| Open the Format tab for the selection | `Alt+Enter` |
| Next / previous comment in the document | `Alt+Down` / `Alt+Up` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` or `Ctrl+Shift+Z` |
| Choose the Pen / Highlighter / Eraser | Markup mode and `Tab` to the tool; or the palette. Single-key shortcuts (`P`, `H`, `E`, `N`, `T`, `S`) exist **only** when "Single-key tool shortcuts" is on (default off) |
| Cancel the tool in progress | `Esc` |

## 7. Engine and protocol implications

| # | Need | Notes |
|---|---|---|
| E5 | `ListAnnotations` with geometry, quads, ink paths, popup text, `/IRT`, review state, author, dates | Required for the pane, the thread and UI-side hit testing |
| E6 | `HitTestAnnotation`, `GetAnnotationBounds` | Avoid a round trip for every pointer move: the UI keeps a cache of the visible page's annotation bounds |
| E7 | `ApplyEditCommand` types: `AddTextMarkup`, `AddNote`, `AddReply`, `SetReviewState`, `AddFreeText`, `AppendInkStroke`, `RemoveInkStroke`, `AddShape`, `AddStamp`, `UpdateAnnotation`, `DeleteAnnotation`, `MoveAnnotation`, `ResizeAnnotation`, `SetLocked` | Each is a journalled, serialisable command (§5.2 rule 6). The **writer host** (ADR-0008) generates appearance streams. The UI process doesn't parse PDF to do any of this |
| E7 | Free-text characters outside a standard font's encoding | **Needs an ADR**: embed a font subset, or limit the characters |
| | **Image stamps:** the broker reads an image the user picked; decoding the image to pixels is **untrusted-input parsing**, so it must happen in the engine or writer host (sandboxed), not in the UI process. The UI process may only show a thumbnail produced by the engine | |
| | **Ink latency:** live ink draws in the UI process with no engine round trip. Committing a stroke to the engine is asynchronous and can lag behind the pen | |
| | **Ink input events:** pointer events and pen data are UI-process only; stroke geometry sent to the engine is bounds-checked (point counts, coordinate ranges) like every UI-to-engine message | |

## 8. OPEN QUESTIONS

1. **Q4:** selection toolbar on by default? This spec says yes. Test with the 5-user study.
2. **Q5:** pen writes ink by default on devices with a digitizer. Yes in this spec. It could surprise someone who uses a pen as a pointer on a convertible. The one-time `InfoBar` and the Settings switch are the safety valve.
3. **Q11:** the author name default. This spec asks once, inline.
4. Does `Ctrl+H` on an existing same-colour highlight (toggle off) surprise users? Alternative: always add, with **Remove markup** in the toolbar and the context menu. Test.
5. Editing another author's comment: confirm first, as specified? An alternative is to allow it silently, as most readers do **[unverified]**.
6. **Non-Latin text in free text** (section 3.3): embed a subset or restrict. ADR needed.
7. Barrel button and eraser-end defaults (Select and Erase) are guesses from common practice, not from measurement. They need the Surface Pen test.
8. 5 s batching window and the pressure curve are guesses. Tune on a device.
9. **Partial eraser** (cut strokes): not in v1.0. If the usability test shows demand, M4.x.
10. **Pen on a document the user can't annotate** (owner restriction): the pen taps still select and click, no ink is drawn, and a brief `InfoBar` explains "The document's owner doesn't allow adding comments."
