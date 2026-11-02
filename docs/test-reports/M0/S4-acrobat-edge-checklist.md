# S4 manual viewer pass: Adobe Acrobat Reader and Microsoft Edge

This is a hand-operated checklist. **Do not script Acrobat** (risk R-15: the EULA position on automated testing is an open question). Open each file by hand: Acrobat via File > Open, Edge by dragging the file onto a tab. Every file is under `spikes/S4/out/`. Record what you see in the last two columns using: **ok** (matches), **partial** (visible but wrong), **missing** (not drawn), or free text.

Things to note on every file, as well as the item-specific check:
- Any banner, dialog or prompt on open ("document has been repaired", "damaged", "cannot be displayed", "save changes?"). For Acrobat, also whether Save is offered immediately after opening without editing (a sign Acrobat repaired the file in memory).
- Whether the annotation or field is selectable and editable by a user, not only drawn.
- For the signature cases: Acrobat's Signature panel wording, verbatim.
- Record the Acrobat Reader build (Help > About) and the Edge version here: Acrobat ____ / Edge ____. Date: ____

Mode key: `inc` = incremental save, page not rendered before the save. `incR` = incremental, page rendered by PDFium before the save (so PDFium generated appearances). `full` / `fullR` = full rewrite. Annotation cases A01..A14 have all four modes. Priority 1 files are the ones that decide the open questions; priority 2 are the full-rewrite twins.

## Annotations created through PDFium

| Pri | File | What to look for | Acrobat Reader | Edge |
|---|---|---|---|---|
| 1 | `files/A01.inc.pdf` (A01 Highlight, inc) | Yellow highlight over "quick brown fox" (line A); text still readable through it. Hover or click: author "Quire Tester", comment text shown. | | |
| 1 | `files/A01.incR.pdf` (A01 Highlight, incR) | Yellow highlight over "quick brown fox" (line A); text still readable through it. Hover or click: author "Quire Tester", comment text shown. | | |
| 1 | `files/A02.inc.pdf` (A02 Underline, inc) | Green underline under "liquor jugs" (line B). | | |
| 1 | `files/A03.inc.pdf` (A03 StrikeOut, inc) | Red strike-through across "strike this out" (line C). | | |
| 1 | `files/A04.inc.pdf` (A04 Squiggly, inc) | Magenta squiggly line under "squiggly words" (line D). | | |
| 1 | `files/A05.inc.pdf` (A05 Text note + Popup + reply (PDFium API), inc) | Yellow note icon near (480,560). Does the icon show? Does its comment open? Is there a linked popup? Replies are NOT linked (no /IRT): there should be no thread. | | |
| 1 | `files/A05.incR.pdf` (A05 Text note + Popup + reply (PDFium API), incR) | Yellow note icon near (480,560). Does the icon show? Does its comment open? Is there a linked popup? Replies are NOT linked (no /IRT): there should be no thread. | | |
| 1 | `files/A06a.inc.pdf` (A06a FreeText (no AP), inc) | FreeText box with the text "FreeText box" at (300,300)-(520,350). Without /AP in the cold file, does Acrobat draw anything? | | |
| 1 | `files/A06a.incR.pdf` (A06a FreeText (no AP), incR) | FreeText box with the text "FreeText box" at (300,300)-(520,350). Without /AP in the cold file, does Acrobat draw anything? | | |
| 1 | `files/A06b.inc.pdf` (A06b FreeText (FPDFAnnot_SetAP stream), inc) | Pale yellow box with "FreeText via SetAP". Font comes from the page's /Helv because the AP stream has no /Resources. | | |
| 1 | `files/A06b.incR.pdf` (A06b FreeText (FPDFAnnot_SetAP stream), incR) | Pale yellow box with "FreeText via SetAP". Font comes from the page's /Helv because the AP stream has no /Resources. | | |
| 1 | `files/A07a.inc.pdf` (A07a Ink via AddInkStroke (single width), inc) | Three red wavy strokes, uniform 3 pt width. | | |
| 1 | `files/A07a.incR.pdf` (A07a Ink via AddInkStroke (single width), incR) | Three red wavy strokes, uniform 3 pt width. | | |
| 1 | `files/A07b.inc.pdf` (A07b Ink pressure-varying width (AppendObject paths), inc) | One blue wavy stroke that thickens in the middle and tapers at both ends. | | |
| 1 | `files/A07b.incR.pdf` (A07b Ink pressure-varying width (AppendObject paths), incR) | One blue wavy stroke that thickens in the middle and tapers at both ends. | | |
| 1 | `files/A08.inc.pdf` (A08 Square, inc) | Square, blue 2 pt border, yellow interior. | | |
| 1 | `files/A08.incR.pdf` (A08 Square, incR) | Square, blue 2 pt border, yellow interior. | | |
| 1 | `files/A09.inc.pdf` (A09 Circle, inc) | Circle/ellipse, green border, light-green interior. | | |
| 1 | `files/A13a.inc.pdf` (A13a Stamp standard name, no appearance, inc) | Stamp annotation with /Name written as a STRING and no appearance. Acrobat may draw its own built-in Approved stamp, or nothing. Record which. | | |
| 1 | `files/A13a.incR.pdf` (A13a Stamp standard name, no appearance, incR) | Stamp annotation with /Name written as a STRING and no appearance. Acrobat may draw its own built-in Approved stamp, or nothing. Record which. | | |
| 1 | `files/A13b.inc.pdf` (A13b Stamp standard name + drawn appearance objects, inc) | Red "APPROVED" box. /Name is a string, not a name object. Check the stamp's properties/comment panel. | | |
| 1 | `files/A13b.incR.pdf` (A13b Stamp standard name + drawn appearance objects, incR) | Red "APPROVED" box. /Name is a string, not a name object. Check the stamp's properties/comment panel. | | |
| 1 | `files/A14.inc.pdf` (A14 Stamp with image, inc) | Round colour-gradient image stamp at (300,380) 100x100 pt, transparent corners. | | |
| 1 | `files/A14.incR.pdf` (A14 Stamp with image, incR) | Round colour-gradient image stamp at (300,380) 100x100 pt, transparent corners. | | |
| 2 | `files/A01.full.pdf` (A01 Highlight, full) | Yellow highlight over "quick brown fox" (line A); text still readable through it. Hover or click: author "Quire Tester", comment text shown. | | |
| 2 | `files/A01.fullR.pdf` (A01 Highlight, fullR) | Yellow highlight over "quick brown fox" (line A); text still readable through it. Hover or click: author "Quire Tester", comment text shown. | | |
| 2 | `files/A02.full.pdf` (A02 Underline, full) | Green underline under "liquor jugs" (line B). | | |
| 2 | `files/A02.fullR.pdf` (A02 Underline, fullR) | Green underline under "liquor jugs" (line B). | | |
| 2 | `files/A02.incR.pdf` (A02 Underline, incR) | Green underline under "liquor jugs" (line B). | | |
| 2 | `files/A03.full.pdf` (A03 StrikeOut, full) | Red strike-through across "strike this out" (line C). | | |
| 2 | `files/A03.fullR.pdf` (A03 StrikeOut, fullR) | Red strike-through across "strike this out" (line C). | | |
| 2 | `files/A03.incR.pdf` (A03 StrikeOut, incR) | Red strike-through across "strike this out" (line C). | | |
| 2 | `files/A04.full.pdf` (A04 Squiggly, full) | Magenta squiggly line under "squiggly words" (line D). | | |
| 2 | `files/A04.fullR.pdf` (A04 Squiggly, fullR) | Magenta squiggly line under "squiggly words" (line D). | | |
| 2 | `files/A04.incR.pdf` (A04 Squiggly, incR) | Magenta squiggly line under "squiggly words" (line D). | | |
| 2 | `files/A05.full.pdf` (A05 Text note + Popup + reply (PDFium API), full) | Yellow note icon near (480,560). Does the icon show? Does its comment open? Is there a linked popup? Replies are NOT linked (no /IRT): there should be no thread. | | |
| 2 | `files/A05.fullR.pdf` (A05 Text note + Popup + reply (PDFium API), fullR) | Yellow note icon near (480,560). Does the icon show? Does its comment open? Is there a linked popup? Replies are NOT linked (no /IRT): there should be no thread. | | |
| 2 | `files/A06a.full.pdf` (A06a FreeText (no AP), full) | FreeText box with the text "FreeText box" at (300,300)-(520,350). Without /AP in the cold file, does Acrobat draw anything? | | |
| 2 | `files/A06a.fullR.pdf` (A06a FreeText (no AP), fullR) | FreeText box with the text "FreeText box" at (300,300)-(520,350). Without /AP in the cold file, does Acrobat draw anything? | | |
| 2 | `files/A06b.full.pdf` (A06b FreeText (FPDFAnnot_SetAP stream), full) | Pale yellow box with "FreeText via SetAP". Font comes from the page's /Helv because the AP stream has no /Resources. | | |
| 2 | `files/A06b.fullR.pdf` (A06b FreeText (FPDFAnnot_SetAP stream), fullR) | Pale yellow box with "FreeText via SetAP". Font comes from the page's /Helv because the AP stream has no /Resources. | | |
| 2 | `files/A07a.full.pdf` (A07a Ink via AddInkStroke (single width), full) | Three red wavy strokes, uniform 3 pt width. | | |
| 2 | `files/A07a.fullR.pdf` (A07a Ink via AddInkStroke (single width), fullR) | Three red wavy strokes, uniform 3 pt width. | | |
| 2 | `files/A07b.full.pdf` (A07b Ink pressure-varying width (AppendObject paths), full) | One blue wavy stroke that thickens in the middle and tapers at both ends. | | |
| 2 | `files/A07b.fullR.pdf` (A07b Ink pressure-varying width (AppendObject paths), fullR) | One blue wavy stroke that thickens in the middle and tapers at both ends. | | |
| 2 | `files/A08.full.pdf` (A08 Square, full) | Square, blue 2 pt border, yellow interior. | | |
| 2 | `files/A08.fullR.pdf` (A08 Square, fullR) | Square, blue 2 pt border, yellow interior. | | |
| 2 | `files/A09.full.pdf` (A09 Circle, full) | Circle/ellipse, green border, light-green interior. | | |
| 2 | `files/A09.fullR.pdf` (A09 Circle, fullR) | Circle/ellipse, green border, light-green interior. | | |
| 2 | `files/A09.incR.pdf` (A09 Circle, incR) | Circle/ellipse, green border, light-green interior. | | |
| 2 | `files/A13a.full.pdf` (A13a Stamp standard name, no appearance, full) | Stamp annotation with /Name written as a STRING and no appearance. Acrobat may draw its own built-in Approved stamp, or nothing. Record which. | | |
| 2 | `files/A13a.fullR.pdf` (A13a Stamp standard name, no appearance, fullR) | Stamp annotation with /Name written as a STRING and no appearance. Acrobat may draw its own built-in Approved stamp, or nothing. Record which. | | |
| 2 | `files/A13b.full.pdf` (A13b Stamp standard name + drawn appearance objects, full) | Red "APPROVED" box. /Name is a string, not a name object. Check the stamp's properties/comment panel. | | |
| 2 | `files/A13b.fullR.pdf` (A13b Stamp standard name + drawn appearance objects, fullR) | Red "APPROVED" box. /Name is a string, not a name object. Check the stamp's properties/comment panel. | | |
| 2 | `files/A14.full.pdf` (A14 Stamp with image, full) | Round colour-gradient image stamp at (300,380) 100x100 pt, transparent corners. | | |
| 2 | `files/A14.fullR.pdf` (A14 Stamp with image, fullR) | Round colour-gradient image stamp at (300,380) 100x100 pt, transparent corners. | | |

## Annotations authored by Quire (raw incremental update)

| Pri | File | What to look for | Acrobat Reader | Edge |
|---|---|---|---|---|
| 1 | `files/Q01.inc.pdf` (Q01 Line with arrow endings (Quire-authored), inc) | Blue line with an open arrowhead at the left end and a closed arrowhead at the right end. | | |
| 1 | `files/Q02.inc.pdf` (Q02 Polygon (Quire-authored), inc) | Orange-bordered, pale-yellow pentagon. | | |
| 1 | `files/Q03.inc.pdf` (Q03 PolyLine (Quire-authored), inc) | Green zig-zag polyline with an open arrowhead at the right end. | | |
| 1 | `files/Q04.inc.pdf` (Q04 Text + Popup + /IRT reply + review state (Quire-authored), inc) | Yellow note icon. Open the comment: parent text, a popup, one reply ("Reply to the parent comment") and an Accepted review state, all threaded under the parent. | | |
| 1 | `files/Q05.inc.pdf` (Q05 Stamp /Name /Approved (Quire-authored), inc) | Red "APPROVED" stamp. /Name is a real name object. | | |
| 1 | `files/Q06.inc.pdf` (Q06 FreeText with AP (Quire-authored), inc) | Pale-yellow FreeText box, two lines of text. Click into it: is the text editable as FreeText? | | |
| 2 | `files/Q01.full.pdf` (Q01 Line with arrow endings (Quire-authored), full) | Blue line with an open arrowhead at the left end and a closed arrowhead at the right end. | | |
| 2 | `files/Q02.full.pdf` (Q02 Polygon (Quire-authored), full) | Orange-bordered, pale-yellow pentagon. | | |
| 2 | `files/Q03.full.pdf` (Q03 PolyLine (Quire-authored), full) | Green zig-zag polyline with an open arrowhead at the right end. | | |
| 2 | `files/Q04.full.pdf` (Q04 Text + Popup + /IRT reply + review state (Quire-authored), full) | Yellow note icon. Open the comment: parent text, a popup, one reply ("Reply to the parent comment") and an Accepted review state, all threaded under the parent. | | |
| 2 | `files/Q05.full.pdf` (Q05 Stamp /Name /Approved (Quire-authored), full) | Red "APPROVED" stamp. /Name is a real name object. | | |
| 2 | `files/Q06.full.pdf` (Q06 FreeText with AP (Quire-authored), full) | Pale-yellow FreeText box, two lines of text. Click into it: is the text editable as FreeText? | | |

## AcroForm fields

| Pri | File | What to look for | Acrobat Reader | Edge |
|---|---|---|---|---|
| 1 | `files/B01.inc.pdf` (B01 Text field, single line, inc) | Text field shows "Hello Quire". Click in: value is editable. | | |
| 1 | `files/B02.inc.pdf` (B02 Text field, multiline, inc) | Multiline text field shows three lines: Line one / Line two / Line three. | | |
| 1 | `files/B03.inc.pdf` (B03 Text field, comb (MaxLen 8), inc) | Comb field shows 1 2 3 4 5 6 7 8 in separate cells. | | |
| 1 | `files/B04.inc.pdf` (B04 Checkbox, inc) | Checkbox is checked. | | |
| 1 | `files/B05.inc.pdf` (B05 Radio group (select second button), inc) | Radio group: second button (B) selected, first (A) clear. | | |
| 1 | `files/B06.inc.pdf` (B06 Combo box, not editable, inc) | Dropdown shows "Beta". | | |
| 1 | `files/B07.inc.pdf` (B07 Combo box, editable, inc) | Editable dropdown shows "custom entry". | | |
| 1 | `files/B08.inc.pdf` (B08 List box, multi-select, inc) | List box shows Beta and Delta highlighted; Alpha, Gamma, Epsilon not. | | |
| 1 | `files/B09.inc.pdf` (B09 Push button (click, no value), inc) | Push button "Submit" unchanged. | | |
| 1 | `files/B10.inc.pdf` (B10 AP-less form: text field, inc) |  | | |
| 1 | `files/B11.inc.pdf` (B11 AP-less form: editable combo, inc) |  | | |
| 1 | `files/B12.inc.pdf` (B12 AP-less form: checkbox (no /AP states at all), inc) |  | | |
| 2 | `files/B01.full.pdf` (B01 Text field, single line, full) | Text field shows "Hello Quire". Click in: value is editable. | | |
| 2 | `files/B02.full.pdf` (B02 Text field, multiline, full) | Multiline text field shows three lines: Line one / Line two / Line three. | | |
| 2 | `files/B03.full.pdf` (B03 Text field, comb (MaxLen 8), full) | Comb field shows 1 2 3 4 5 6 7 8 in separate cells. | | |
| 2 | `files/B04.full.pdf` (B04 Checkbox, full) | Checkbox is checked. | | |
| 2 | `files/B05.full.pdf` (B05 Radio group (select second button), full) | Radio group: second button (B) selected, first (A) clear. | | |
| 2 | `files/B06.full.pdf` (B06 Combo box, not editable, full) | Dropdown shows "Beta". | | |
| 2 | `files/B07.full.pdf` (B07 Combo box, editable, full) | Editable dropdown shows "custom entry". | | |
| 2 | `files/B08.full.pdf` (B08 List box, multi-select, full) | List box shows Beta and Delta highlighted; Alpha, Gamma, Epsilon not. | | |
| 2 | `files/B09.full.pdf` (B09 Push button (click, no value), full) | Push button "Submit" unchanged. | | |
| 2 | `files/B10.full.pdf` (B10 AP-less form: text field, full) |  | | |
| 2 | `files/B11.full.pdf` (B11 AP-less form: editable combo, full) |  | | |
| 2 | `files/B12.full.pdf` (B12 AP-less form: checkbox (no /AP states at all), full) |  | | |

## Signature preservation (fixtures `inputs/signed-base.pdf` and `inputs/signed-forms.pdf` first: record the signature panel for the untouched fixtures)

| Pri | File | What to look for | Acrobat Reader | Edge |
|---|---|---|---|---|
| 1 | `files/G01.full.pdf` (G01 Signed base + Highlight, full) | Signature panel: is the signature reported valid / "changes made after signing" / invalid? Highlight visible. (Self-signed cert: an untrusted-identity warning is expected and fine.) | | |
| 1 | `files/G01.inc.pdf` (G01 Signed base + Highlight, inc) | Signature panel: is the signature reported valid / "changes made after signing" / invalid? Highlight visible. (Self-signed cert: an untrusted-identity warning is expected and fine.) | | |
| 1 | `files/G02.full.pdf` (G02 Signed forms + text field fill, full) | Signature panel status after a form-field fill (incremental) and for the full copy. Field shows "Hello Quire". | | |
| 1 | `files/G02.inc.pdf` (G02 Signed forms + text field fill, inc) | Signature panel status after a form-field fill (incremental) and for the full copy. Field shows "Hello Quire". | | |
| 1 | `files/G03.full.pdf` (G03 Signed base, no edit (open and save), full) | Open-and-save with no edit. Signature panel status: valid / modified / invalid. | | |
| 1 | `files/G03.inc.pdf` (G03 Signed base, no edit (open and save), inc) | Open-and-save with no edit. Signature panel status: valid / modified / invalid. | | |
| 1 | `files/G04.full.pdf` (G04 Signed forms + checkbox, full) | Checkbox checked; signature panel status. | | |
| 1 | `files/G04.inc.pdf` (G04 Signed forms + checkbox, inc) | Checkbox checked; signature panel status. | | |

## Foreign-file incremental saves with xref streams (structural validity)

PDFium's incremental output for files that use cross-reference streams lacks `/Type /XRef` and the closing `endobj` (see the S4 report). The `.repaired.pdf` twin has only those two defects patched. Record whether each viewer opens the PDFium output without a repair prompt.

| Pri | File | What to look for | Acrobat Reader | Edge |
|---|---|---|---|---|
| 1 | `probe/annotation_stamp_with_ap.edit.inc.pdf` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |
| 1 | `probe/annotation_stamp_with_ap.edit.repaired.pdf` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |
| 1 | `probe/bug_1029.edit.inc.pdf` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |
| 1 | `probe/bug_1029.edit.repaired.pdf` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |
| 1 | `probe/embedded_attachments.edit.inc.pdf` | Opens cleanly? Prompt about repair? A red Square at lower left (20,20)-(80,60). | | |

