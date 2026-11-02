# Feature spec: shapes (rectangle, ellipse, line, arrow, polygon, polyline)

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (shapes), §8.2, §12.4
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md), geometry in `MikePDF.Markup`
- Related: [ink-pen](ink-pen.md) (freehand), [free-text](free-text.md)

Conventions are in [README.md](README.md). Shared rules are in annotation-core.

## 1. Why MikePDF writes these itself

S4: PDFium **cannot create** Line, Polygon or PolyLine (`FPDFAnnot_IsSupportedSubtype` is 0 and `FPDFPage_CreateAnnot` returns NULL, A10 to A12) and offers no setters for `/L`, `/LE` or `/Vertices`. For Square and Circle it writes no appearance unless a page was rendered first (A08, A09). The MikePDF-authored Q01 (line with arrow endings), Q02 (polygon) and Q03 (polyline) passed `qpdf --check`, reopened in a fresh PDFium and drew identically in PDFium and pdf.js. Acrobat and Edge were **not measured**.

## 2. User stories

1. As a reviewer, I box a paragraph with a rectangle, circle a figure with an ellipse, and point at a number with an arrow.
2. As a surveyor, I outline an irregular area with a polygon and measure nothing, but it prints cleanly.
3. As anyone, I change colour, width, dash style and fill, move and resize the shape, and edit a polygon's corners.
4. As a keyboard-only user, I create a shape, size it with numbers, and move its corners with the arrow keys.
5. As a recipient, my shapes look the same in Acrobat Reader and Edge.

## 3. Detailed behaviour

### 3.1 Kinds

| Kind | PDF subtype | Geometry keys | Notes |
|---|---|---|---|
| Rectangle | `/Square` | `/Rect` | Stroke inside the rect (inset by half the width) |
| Ellipse | `/Circle` | `/Rect` | The ellipse inscribed in the rect |
| Line | `/Line` | `/L [x1 y1 x2 y2]`, `/LE [start end]` | Two endpoints |
| Arrow | `/Line` | as Line with `/LE [/None /OpenArrow]` | A line preset; a document cannot tell it from a Line with that ending, and the UI treats it as one |
| Polygon | `/Polygon` | `/Vertices` | Closed; 3 to 1,000 vertices |
| PolyLine | `/PolyLine` | `/Vertices`, `/LE` | Open; 2 to 1,000 vertices |

### 3.2 Properties

| Property | Values | Default |
|---|---|---|
| Stroke colour | palette or custom | red |
| Fill colour | none or colour (not for Line/Arrow/PolyLine; for them the "interior" colour fills closed arrowheads and other filled ends) | none |
| Width | 0.25 to 36 pt | 2 pt |
| Dash | Solid; Dashed (dash 4x width, gap 2x width, minimum 2 pt); Dotted (dot of 1x width, gap 2x width, round caps, minimum 1 pt) | Solid |
| Opacity | 10% to 100% | 100% |
| Line endings (Line, PolyLine) | None, Open arrow, Closed arrow, Square, Circle, Diamond, for start and end separately | Arrow tool: end = Open arrow |

Arrow head geometry (in `MikePDF.Markup`, shared by overlay and appearance): length `clamp(6 x width, 8, 48) pt`, half-angle 25 degrees; open arrow = two strokes; closed arrow = filled triangle (stroke colour, or the interior colour if set); square, circle and diamond ends have a side of `clamp(4 x width, 6, 36) pt`. The line's visible end is shortened where the head sits so the stroke doesn't poke through the tip.

### 3.3 Creating

| Tool | Gesture |
|---|---|
| Rectangle, Ellipse, Line, Arrow | Press, drag, release. **Shift** constrains (square, circle, or a line to multiples of 45 degrees). **Alt** draws from the centre. A drag shorter than 4 pt cancels with no annotation (avoids accidental clicks). |
| Polygon, PolyLine | Click to add each vertex. **Enter** or **double-click** completes. **Esc** cancels. **Backspace** removes the last vertex. For a polygon, clicking within 8 DIP of the first vertex closes it. A polygon needs 3 vertices and a polyline 2, else completion is refused with a short message. A rubber-band segment follows the pointer. Maximum 1,000 vertices. |

Creation clamps points to the page's displayed box. After creation the tool returns to `Select` unless `markup.keepToolActive`.

**Keyboard creation:** `annot.newShape` (with the kind chosen from a menu or via the tool command) creates a default shape at the top-left of the visible area: rectangle and ellipse 144 x 72 pt, line and arrow 144 pt long, polygon an equilateral triangle of side 100 pt, polyline a two-segment path. The new shape is selected for editing with the keyboard (3.4).

### 3.4 Selecting and editing

- **Handles.** Rectangle and ellipse: eight. Line and arrow: two end handles; dragging the shape body moves it. Polygon and polyline: a handle at each vertex plus the eight handles of the bounding box (which scale all vertices). Double-click on a polygon or polyline edge inserts a vertex there; a selected vertex is removed with Delete (refused below the minimum, with the shape not deleted).
- **Hit-testing.** Filled shapes hit anywhere inside; unfilled shapes hit within the tolerance of their stroke; lines hit within tolerance. The polygon interior hits only when filled.
- **Keyboard editing.** With a shape focused: arrow keys move it by 1 pt (Shift 10 pt). **Enter** enters vertex mode on a line, arrow, polygon or polyline; **Tab / Shift+Tab** move between vertices (Escape leaves vertex mode); arrow keys move the focused vertex; **Insert** adds a vertex after the focused one (at the midpoint of the next segment); **Delete** removes the focused vertex. Rectangles and ellipses can be sized with the numeric fields in the properties pane (Left, Bottom, Width, Height in points).
- Moving, resizing and vertex edits are single undo steps per gesture; clamps and minimum sizes as annotation-core 3.4.
- Restyle applies to the selection. Changing a Line to an Arrow is a change of line ending, not of kind.
- Foreign shapes (made in other readers): move, delete, reply; no restyle or resize (annotation-core 3.2).

### 3.5 Saved geometry and appearance

All appearances follow annotation-core 3.9, plus:

- **Rectangle and ellipse:** `/Rect` is the outer extent including the stroke. The appearance draws the shape inset by half the width so the stroke stays inside the rect (S4 gap 6). `/BS << /W w /S /S or /D >>` and `/D [..]` for dashes. `/IC` fill colour when set. No `/RD`.
- **Line:** `/L`, `/LE` as name objects, `/IC` when an end is filled, `/LL` and `/LLE` not used. `/Rect` is the bounding box of the line and its end shapes, grown by half the width.
- **Polygon and polyline:** `/Vertices` flat array, `/IC`, `/BS`, polyline `/LE`. `/Rect` is the bounding box grown by half the width and, for polyline ends, by the end-shape size.
- Joins are miter with limit 10 for polygons and polylines, round caps for dotted lines, projecting-square or butt caps otherwise (butt). Fill uses the non-zero winding rule (`f`).
- **Opacity.** When opacity is below 100%, the appearance form has a transparency group (`/Group << /S /Transparency >>`) so stroke and fill are blended as one object and the overlap at the border doesn't darken. Plain alpha for each paint operation would show a double-blended band along the border. Support for the group in Acrobat and Edge is not measured.
- Dash arrays are written in `/D` and in the appearance's `d` operator.
- Cloudy borders (`/BE`) are not part of v1 (OPEN QUESTION OQ-SH-1).

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Rectangle / Ellipse / Line / Arrow / Polygon / PolyLine tool | none (OQ-CMD-3) | `tool.rectangle`, `tool.ellipse`, `tool.line`, `tool.arrow`, `tool.polygon`, `tool.polyline` |
| New shape at the current position | none | `annot.newShape` |
| Complete / cancel polygon | Enter, double-click / Esc | (tool) |
| Edit vertices | Enter on the focused shape | `annot.editVertices` |
| Add / remove vertex | Insert / Delete | (vertex mode) |

Pen: drag with the pen in a shape tool (pen mode inks instead). Touch: drag for box shapes; tap vertices for polygons; handles are at least 24 DIP and the polygon-close target 12 DIP. A long press opens the menu.

## 5. Accessibility

- UIA: localized control type per kind ("rectangle", "ellipse", "line", "arrow", "polygon", "polyline"); name "{kind} by {author}"; `HelpText` with page, size ("144 by 72 points") or vertex count, and colours as words ("red outline, no fill"). Vertices are exposed as child items in vertex mode with names "Vertex 3 of 5, 120, 340" (page coordinates).
- Properties pane controls have names and values; the numeric size fields are the keyboard alternative to dragging handles.
- Announcements: "Rectangle added on page 4", "Vertex 3 moved", "Vertex added", "Polygon needs at least 3 vertices".
- Selection handles and focus have 3:1 contrast against the page and the shape (the handle has a contrasting inner and outer ring, so it is visible on any colour).
- Dash style and fill are not conveyed by colour alone in the UI (the picker shows pattern previews with text names).

## 6. Error states

| Situation | Behaviour |
|---|---|
| Too few vertices on completion | Message and the shape stays in progress. |
| Vertex limit reached | Message; further clicks ignored until completion. |
| Delete below the minimum vertex count | Refused with a message. |
| Zero-size drag | Cancelled silently. |
| Permissions and limits | annotation-core 3.8. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** geometry in user space; arrow heads are computed in user space and rotate with the page; fixtures for `/Rotate` 0/90/180/270 and CropBox origin (50, 30).
- **Constrain modes on rotated pages:** Shift constrains in the displayed orientation (45-degree multiples relative to the screen), then converts to user space.
- **Very thin and very wide shapes, 0.25 pt hairlines:** minimum size and width clamps; hit tolerance covers them.
- **Huge shapes beyond the page:** clamped to the displayed box.
- **Self-intersecting polygons:** allowed; filled with non-zero winding; hit-testing uses the same rule.
- **Collinear and duplicate vertices:** duplicates (within 0.01 pt) are merged on completion; collinear vertices are kept.
- **Facing and presentation modes:** creation uses the page under the pointer; hidden in presentation mode.
- **Hostile input:** a loaded polygon with 5 million vertices or NaN coordinates: the engine reports at most 1,024 vertices (2,048 numbers) with a truncated flag; the shape is treated as foreign and shown from the first part; edits are disabled.
- **Foreign `/LE` values or `/LL` leaders, `/CL` callouts, `/RD` differences:** drawn by PDFium from their own appearance; not editable beyond move, delete and reply.

## 8. Undo and redo

Create, move, resize, vertex move/add/remove, restyle, delete: one command each (restyle of several properties changed together is one). In-progress polygon construction is not on the undo stack until completed; Backspace removes the last vertex during construction. Rules in annotation-core section 8.

## 9. Journal entries

`AddAnnotation` with `kind` one of `Rectangle`, `Ellipse`, `Line`, `Arrow`, `Polygon`, `PolyLine`; `payload`: `{ "points": [x,y,...], "lineEndings": ["None","OpenArrow"], "dash": "solid"|"dashed"|"dotted" }` (for rectangle and ellipse `points` is empty and `rect` carries the geometry), `style`: `{ "stroke": "#FF0000", "fill": null, "width": 2, "opacity": 1 }`. `UpdateAnnotation`, `MoveAnnotation`, `ResizeAnnotation` (with `points` and `previous`), `DeleteAnnotation`.

## 10. Save behaviour

One indirect annotation per shape with the keys in 3.5, `/F 4`, the common keys of annotation-core 3.5 and an `/AP /N` form. Appended by the writer host in stage 4. `qpdf --check` must pass on classic and xref-stream bases.

## 11. Engine and protocol additions

None beyond annotation-core. `GetAnnotation.vertices`, `lineEndings` and `interiorColour` serve loaded shapes.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Shape_Rectangle` / `_Ellipse` / `_Line` / `_Arrow` / `_Polygon` / `_PolyLine` | Rectangle / Ellipse / Line / Arrow / Polygon / Polyline |
| `Shape_Stroke` / `_Fill` / `_Width` / `_Dash` / `_Opacity` | Outline colour / Fill colour / Line width / Line style / Opacity |
| `Shape_Dash_Solid` / `_Dashed` / `_Dotted` | Solid / Dashed / Dotted |
| `Shape_End_Start` / `_End_End` | Start / End |
| `Shape_End_None` / `_OpenArrow` / `_ClosedArrow` / `_Square` / `_Circle` / `_Diamond` | None / Open arrow / Closed arrow / Square / Circle / Diamond |
| `Shape_Size` | {0} by {1} points |
| `Shape_Left` / `_Bottom` / `_WidthField` / `_HeightField` | Left / Bottom / Width / Height |
| `Shape_Vertex_Automation` | Vertex {0} of {1}, {2}, {3} |
| `Shape_Vertex_Moved_Announce` | Vertex {0} moved |
| `Shape_Vertex_Added_Announce` | Vertex added |
| `Shape_Vertex_Removed_Announce` | Vertex removed |
| `Shape_NeedPolygon` | A polygon needs at least 3 vertices. |
| `Shape_NeedPolyline` | A polyline needs at least 2 vertices. |
| `Shape_MaxVertices` | A shape can have up to {0} vertices. |
| `Shape_MinVertices` | A {0} needs at least {1} vertices. |
| `Shape_Help_Outline` | {0} outline, {1} |
| `Shape_Help_NoFill` | no fill |
| `Shape_Added_Announce` | {0} added on page {1} |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-SH-1 | Cloudy borders (`/BE`), common in contract and drawing review: out of v1? | User (product) |
| OQ-SH-2 | Bring-to-front and send-to-back (z-order commands): out of v1? | User (product) |
| OQ-SH-3 | Transparency group in the appearance for opacity < 100%: readers' behaviour is unmeasured. | Opus after the Acrobat pass |
| OQ-SH-4 | Default colours and widths. | Design |

## 14. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply.

| Id | Criterion | Layer |
|---|---|---|
| SH-01 | Arrow head and line-ending geometry: lengths, angles, shortening of the stroke at the tip, for widths 0.25, 2, 36 and all six end types, equal committed golden numbers. | Unit (`MikePDF.Markup.Tests`) |
| SH-02 | Dash patterns: dashed and dotted arrays and minimums equal specification for widths 0.25 to 36. | Unit |
| SH-03 | Creation gestures: Shift and Alt constraints (including on rotated pages), minimum drag, polygon close-by-click, Enter/double-click completion, Backspace, vertex limits. | Unit (tool state machine) |
| SH-04 | Bounding-rect computation includes width, arrow heads and end shapes for every kind; the appearance never draws outside `/Rect`. | Unit |
| SH-05 | Vertex editing: insert at edge midpoint, remove respecting minimums, move, scale from the bounding box; each one undo step. | Unit |
| SH-06 | Hit-testing: filled vs unfilled rule, tolerance for hairlines and dotted lines, non-zero winding interior for self-intersecting polygons. | Unit |
| SH-07 | Saved dictionaries: Square/Circle `/Rect` and inset; Line `/L` `/LE` (name objects) `/IC`; Polygon/PolyLine `/Vertices`; `/BS`, `/D`, `/CA`, `/F 4`; indirect object; no PDFium keys; transparency group present exactly when opacity < 100%. | Unit (PdfWriter) |
| SH-08 | `qpdf --check` passes and a fresh PDFium reopens every kind with properties intact (the Q01 to Q03 results are reproduced by the production code, not the spike). | Fidelity harness |
| SH-09 | Cold save (no prior render): every kind is visible in fresh PDFium and pdf.js (S4 A08, A09 failures do not recur; Line/Polygon/PolyLine exist). | Fidelity harness |
| SH-10 | Fidelity, PDFium and pdf.js: region SSIM >= 0.98 against the golden per kind and per rotation 0/90/180/270; arrow heads point the right way on rotated pages. | Fidelity harness |
| SH-11 | Fidelity, Acrobat Reader and Edge: every kind visible and correct, including dashed lines, closed arrows with fill, semi-transparent shapes; recorded as pass, fail or **not measured**. | Fidelity harness (manual-gated, R-15) |
| SH-12 | Reopen in MikePDF: the loaded model equals the saved one (points, endings, colours, width, dash, opacity). | Fidelity harness |
| SH-13 | Foreign shapes keep their appearance bytes after move, reply and delete operations. | Integration |
| SH-14 | Keyboard-only: `annot.newShape`, numeric resize, vertex mode with Tab, arrows, Insert, Delete, Esc, undo. | UI journey |
| SH-15 | UIA: names, help text, vertex items, announcements once each. | UI journey (a11y) |
| SH-16 | Hostile loaded shapes (5 M vertices, NaN) load bounded as foreign and non-editable. | Integration + fuzz |
| SH-17 | Rotated pages and CropBox: created shapes land at the pointer position (round trip within 0.5 pt) for all four rotations. | Integration |
| SH-18 | Permissions bit 6 clear disables shape tools. | Unit |
| SH-19 | Performance: dragging a 1,000-vertex polygon at 400% zoom redraws within a frame budget of 16 ms on the CI runner (recorded). | Integration (perf) |
