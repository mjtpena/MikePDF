# 10 Page organiser, merge and split

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenario 5 (paperwork assembly: merge a cover letter, a signed scan and three receipts; reorder and rotate; delete a blank page; export one PDF). Also the page operations in §2.1 and §8.5.
- Milestone M4. Nothing here exists yet.
- Page operations **modify the file** (unlike *Rotate view*). That difference must be visible in the wording ([README 12](../README.md#12-what-this-design-changes-in-the-current-app)).

## 1. Purpose

Rearrange the pages of a document, or assemble one from several, with direct manipulation, with undo, and with an equal path for people who can't drag.

One model for everything: **the organiser is a view of one document**. Merging creates a new unsaved document whose pages come from several files. Splitting and extracting create new files from a selection or a rule.

## 2. Layout

### 2.1 Organiser view (the page well is replaced by a grid; same tab, same undo stack)

```text
+--------------------------------------------------------------------------------------------+
|[M] Paperwork (merged).pdf  .  | +                                                          |
+--------------------------------------------------------------------------------------------+
|[=]|[ins v][del][rot L][rot R][dup][extract...]  [Move to...]  size [--o---]   |[undo][redo]| [Done]
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|   +--------+  +--------+  +--------+  +--------+  +--------+  +--------+                   |
|   | cover  |  | scan   |  | receipt|  | receipt|  | (blank)|  | receipt|                   |
|   | letter |  | (signed)  |   1    |  |   2    |  |        |  |   3    |                   |
|   +--------+  +--------+  +--------+  +--------+  +--------+  +--------+                   |
|   [=]1        [=]2        [=]3        [=]4        [=]5        [=]6      <- number, then source colour strip
|   letter.pdf  scan.pdf    receipts    receipts    receipts    receipts      (Caption: file . page)
|                                                                                            |
|   +========+  <- selected: 2 epx accent border + check glyph in the corner                  |
|   # rotated #  rotated pages show the rotation badge "90" in the corner                    |
|                                                                                            |
|   | drag insertion bar: 3 epx accent bar between two cells, with the count badge "3"       |
+--------------------------------------------------------------------------------------------+
|  6 pages, 1 selected                                              [Undo bar: Deleted 1 page. Undo] |
+--------------------------------------------------------------------------------------------+
```

The command bar **centre** is swapped in place for the organiser's commands; Panes, Page and Zoom groups on the left are hidden and replaced by **Select all**; the Details toggle stays on the right ([01](01-main-window.md) section 3.2). **Done** returns to the reading view at the page that was selected.

| Button | Command | Notes |
|---|---|---|
| Insert | `pages.insertBlank`, `pages.insertFromFile` (split menu) | **Insert blank page**, **Insert pages from file...** |
| Delete | `pages.delete` | |
| Rotate left, Rotate right | `pages.rotateLeft`, `pages.rotateRight` | Changes the page in the file |
| Duplicate | `pages.duplicate` | Copies after each selected page |
| Extract... | `pages.extract` | Saves the selection as a new file |
| Move to... | `pages.moveTo` | Dialog: "Move 3 pages before page [ ]" |
| Size | slider, 96 to 320 epx | Remembered |
| Undo, Redo | | |

### 2.2 Insert pages from a file (a position control, no wizard)

```text
   [Insert v]
    +-------------------------------+
    | Insert blank page             |
    | Insert pages from file...     |   -> file picker (multi-select) -> pages appear after the selection (or at the end),
    +-------------------------------+      selected, so they can be dragged or rotated straight away
```

### 2.3 Merge (from the empty state or File menu): pick files, get a grid

```text
 Merge PDFs...  ->  [ system file picker, multi-select ]  ->  new tab "Merged document" (unsaved) in the organiser view
                    (sorted by name; the user reorders in the grid)
```

Each page's caption shows its source (`receipts.pdf . 2`). A source with a password asks inline ([14](14-errors-and-recovery.md)). A source that can't be read is **left out** and named in an `InfoBar`: "receipt-3.pdf couldn't be added. It looks damaged." The others are still merged.

### 2.4 Split dialog (`file.split`)

```text
        +--------------------------------------------------------------+
        | Split "Report.pdf"                                           |
        |                                                              |
        | Split by                                                      |
        |  (o) Page ranges      [ 1-3, 4-10, 11-          ]            |
        |  ( ) Every            [ 5 ] pages                            |
        |  ( ) Top-level bookmarks  (12 bookmarks)                     |
        |                                                              |
        | Save in   C:\Users\mike\Documents\Report      [ Browse... ]  |
        | Name      Report - {n}                                       |
        |                                                              |
        | This makes 3 files:                                          |
        |   Report - 1.pdf   pages 1-3                                 |
        |   Report - 2.pdf   pages 4-10                                |
        |   Report - 3.pdf   pages 11-40                               |
        | (!) 1 file already exists: [ Keep both ] [ Replace ]         |
        |                                      [ Split ]   [ Cancel ]  |
        +--------------------------------------------------------------+
```

### 2.5 Narrow (XS): the grid is two columns, the bar scrolls

```text
+----------------------------------+
|[M] Paperwork.pdf . x +    [_][X] |
|[ins v][del][rot R][...]   [Done] |
|  +------+  +------+              |
|  | p1   |  | p2   |              |
|  +------+  +------+              |
|  +======+  +------+              |
|  # p3   #  | p4   |              |
+----------------------------------+
```

## 3. Behaviour

### 3.1 Entering and leaving

- **Organise pages** (`pages.organise`, `Ctrl+Shift+O`; or **Organise pages** in the thumbnails pane header; or the View menu) switches the page well to the grid with the current page selected and scrolled into view. The tab keeps its identity, history, undo stack and journal.
- **Done**, `Esc` (with nothing selected and no menu open), or `Ctrl+Shift+O` again leaves. The reading view then shows the current page.
- A password-protected document that doesn't permit assembly (owner restriction: "assemble") disables the organiser with the reason "The document's owner doesn't allow changing pages."

### 3.2 Selecting

Click selects one; `Ctrl+click` toggles; `Shift+click` selects a range; drag in the empty area draws a **rubber band**; `Ctrl+A` selects all; arrows move the focus; `Space` toggles selection of the focused cell; `Shift+Arrow` extends. A selected cell shows a border **and** a check glyph.

### 3.3 Moving pages

- **Drag** a selection: the cells under the pointer shift apart, a 3 epx insertion bar shows where it will land, the dragged cells show a stack with a count badge. The grid **auto-scrolls** near its edges (16 epx per 16 ms ramping up to 64). `Esc` cancels the drag. The settle animation is `MotionSlow` (instant under reduced motion).
- **Keyboard:** `Alt+Shift+Left` and `Alt+Shift+Right` move the selection one position earlier or later (one undo step per press, coalesced if pressed repeatedly within 400 ms); `Alt+Shift+Home` and `Alt+Shift+End` move it to the start or end.
- **Move to...** (`pages.moveTo`): a small dialog "Move {n} pages before page [ ]". It's the pointer-free path for people who can't drag (WCAG 2.5.7).
- **Copy** with `Ctrl` held while dropping (a "+" badge shows). `Ctrl+C`, `Ctrl+V` paste copies of pages.
- **From Explorer:** dropping PDF files on the grid inserts their pages at the drop position. Non-PDF files show "That isn't a PDF."
- **Between tabs:** dragging pages over another tab's header for 600 ms activates that tab (switching it to the organiser view if needed); dropping inserts **copies** there. The source keeps its pages. `Shift` while dropping **moves** them (the source's pages are deleted as one undoable step in the source tab). The source and target remain independently undoable.

### 3.4 Operations

| Operation | Behaviour | Undo |
|---|---|---|
| Delete | Removes selected pages. Cannot delete all pages ("A document needs at least one page."). Shows the undo bar "Deleted 3 pages. Undo". Bookmarks and links that pointed to deleted pages are **removed or retargeted** by the engine: bookmarks to deleted pages are dropped; links to deleted pages are removed **[design; the test required by §8.5 decides which]** | One step |
| Rotate left / right | Adds or subtracts 90 degrees on each selected page's `/Rotate`. Cell shows the rotation | One step |
| Insert blank | A blank page with the **size of the neighbour** (the page before the insertion point, else after). Default is the same size as the first page | One step |
| Insert from file | Opens a multi-select file picker. The broker opens each selected file (read-only), and the engine imports their pages (E11) | One step per file |
| Duplicate | A copy after each selected page | One step |
| Extract | **Extract pages...** opens a save dialog (`{name} (pages 3-5).pdf`), writes a new file with the selected pages, and shows an `InfoBar` "Saved 3 pages. [Open] [Show in folder]". The source is unchanged | n/a (new file) |
| Move | As in 3.3 | One step |
| Rotate view | Not here. It is in the View menu, and its tooltip says "The file is not changed." **[repo]** | n/a |

All operations are edit commands in the journal (§7.4) and use the document's shared undo stack. Operations on many pages are one command.

### 3.5 Merge

1. `file.merge` (empty state, File menu, palette, organiser **Insert**): file picker with multi-select.
2. A new **unsaved** tab opens in the organiser view with all pages of all files, in picker order (sorted by name), with source captions.
3. The user reorders, rotates, deletes, then saves. **Save** asks for a name (the Save As dialog; suggested name "Merged document.pdf" in the first source's folder). The tab is marked modified from the start. Closing it asks to save ([README 9.3](../README.md#93-undo-save-and-dirty-state)).
4. **What isn't carried over (to verify):** bookmarks, form fields and document-level JavaScript of the sources. PDFium's page import copies pages and their annotations, but the document's outline and form-field hierarchy are separate **[unverified: check `fpdf_ppo.h` behaviour in the pinned build]**. If any source has bookmarks or form fields, show once: "Bookmarks and form fields from the merged files aren't kept." with **OK** (an `InfoBar`, not a dialog).
5. **Passwords and permissions:** an encrypted source asks for its password; pages from a source that restricts assembly aren't added ("{name} doesn't allow its pages to be copied").
6. **Size warning:** over 2 GB in total or over 5,000 pages, show a progress bar and allow cancel. The memory cap (2 GB per engine, §5.2) applies. Many large sources are imported one at a time.

### 3.6 Split

1. `file.split` opens the dialog (2.4) on the current document, from the organiser's **...** menu, the File menu or the palette.
2. **Page ranges:** `1-3, 4-10, 11-` ("11-" means to the end). Ranges may not overlap or leave pages out unless the user lists them: pages not covered **are not exported**, and the preview says so ("Pages 41 to 45 aren't in any file."). Invalid text is flagged in the box (`(!)` badge and the text "Use ranges like 1-3, 4-10.").
3. **Every N pages:** the last file takes the remainder.
4. **By top-level bookmarks:** each file runs from a top-level bookmark's page to the page before the next one. Pages before the first bookmark go in their own file named "{name} - start". Titles become file names (invalid characters removed, 80 characters max). Disabled with "This document has no bookmarks" when none.
5. The preview lists the first five files and "and {n} more". Existing files are listed (3.6 wireframe). **Keep both** adds " (2)" to names; **Replace** is the only choice that overwrites and isn't the default. An output location the broker can't write to shows the reason inline.
6. **Split** runs with a determinate progress bar and **Cancel**. Files already written stay (they are valid PDFs); the dialog says so. On completion an `InfoBar` "3 files saved in Report. [Show in folder]". **Split can't be undone with `Ctrl+Z`** (it makes new files and changes none); the dialog says "This makes new files. Your original isn't changed."
7. Splitting never modifies the source. Split of a signed document: the pieces are new documents with no signatures.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | The grid shows placeholders (correct aspect ratio, page-shaped, static under reduced motion) while thumbnails arrive from the pool engine; cells are usable immediately. A merge shows "Adding pages from 3 files..." with a determinate bar |
| **Empty** | A merged document with no usable file: "Nothing to merge. None of the files could be read." with **Add files...**. A document whose pages were all deleted can't exist (blocked). The grid has no empty state otherwise |
| **Error** | An import failure for one file: `InfoBar` naming the file and the reason; the rest continue. An engine crash during an operation: the journal replays, and the undo bar says "MikePDF restarted. Your page changes were kept." If the replay fails: "Some page changes couldn't be restored." with the list and **Reopen the original**. A failed extract or split: the dialog or `InfoBar` shows the reason and **Try again**; no partial file is left half-written (temp then rename, §7.5) |
| **Keyboard focus** | The grid is one tab stop; arrows move focus between cells (two-ring focus on the cell); `Space` selects; `Enter` opens that page in the reading view. `Tab` from the grid goes to the status bar then out. In the dialog, focus starts on the first radio |
| **High contrast** | Selected: 3 epx `Highlight` border, check glyph in `HighlightText` on `Highlight`. Focus ring as usual. Insertion bar: `Highlight`. Source colour strips are replaced by a text caption (the file name), because colour alone isn't enough. Rotation badge is text |
| **225% text scale** | Cell captions wrap to three lines under the thumbnail, the cells grow, the grid reflows to fewer columns. The command bar is icon-only (unchanged), so it holds. The Split dialog scrolls and its preview list wraps; its buttons stay pinned. The undo bar wraps and grows |
| **Narrow window** | See 2.5: 2 columns at XS, the command bar scrolls horizontally into **...** by priority (Delete, Rotate, Insert first). The Split dialog is a full-width sheet |
| **Touch** | A long-press on a cell enters **select mode** with checkboxes on every cell; tap toggles; a drag handle (24 x 24) in each cell's corner starts a reorder drag (so a finger scrolling the grid isn't mistaken for a drag); cells are at least 96 epx wide at the smallest slider position; the insertion bar is 6 epx during touch drags; **Move to...** is offered for fine placement |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Organiser region | Group | "Organise pages" | `OrganiserView` |
| Grid | List (grid pattern) | "Pages" | `OrganiserGrid` |
| Cell | ListItem | "Page {n} of {total}, from {source file, page k}, rotated {90} degrees, selected" (omit what doesn't apply) | `OrgPage_{n}` |
| Insert, Delete, Rotate, Duplicate, Extract, Move to | Button | "Insert", "Delete pages", "Rotate pages left", "Rotate pages right", "Duplicate pages", "Extract pages", "Move pages" | `OrgInsert`, `OrgDelete`, `OrgRotateLeft`, `OrgRotateRight`, `OrgDuplicate`, `OrgExtract`, `OrgMoveTo` |
| Size | Slider | "Thumbnail size" | `OrgSize` |
| Status | Text (live, polite) | "6 pages, 1 selected" | `OrgStatus` |
| Undo bar | Group (live, polite) | "Deleted 1 page. Undo" with the **Undo** button | `UndoBar` |
| Split dialog | Dialog | "Split Report" | `SplitDialog` |
| Mode radios | RadioButton | "Page ranges", "Every N pages", "Top-level bookmarks" | `SplitModeRanges`, `SplitModeEvery`, `SplitModeBookmarks` |
| Ranges box | Edit | "Page ranges, for example 1-3, 4-10" | `SplitRanges` |
| Preview | List | "Files that will be made" | `SplitPreview` |

Narrator, intended:

| Action | Announcement |
|---|---|
| Enter organiser | "Organise pages. 6 pages. Page 3 selected." |
| Move through cells | "Page 4 of 6, from receipts, page 2" |
| Select | "Page 4 selected. 2 pages selected." (polite, coalesced) |
| Delete | "Deleted 1 page. 5 pages. Undo available." |
| Move | "Moved to position 2." (polite) |
| Rotate | "Page 3 rotated 90 degrees." |
| Merge done | "Merged 5 files, 9 pages." |
| Split done | "3 files saved." |

The grid exposes the **Grid** and **SelectionItem** patterns so Narrator can announce row and column. **The drag has an equivalent without dragging** (Alt+Shift+arrows and Move to...).

## 6. Keyboard path (scenario 5, no mouse)

| Task | Keys |
|---|---|
| Merge files | `Ctrl+Shift+P`, "Merge", `Enter`, select files in the picker, `Enter` |
| Enter / leave the organiser | `Ctrl+Shift+O` |
| Select a page | Arrows, `Space` |
| Select several | `Shift+Arrows`, `Ctrl+Arrows` then `Space` |
| Move earlier / later | `Alt+Shift+Left` / `Alt+Shift+Right` |
| Rotate | `Ctrl+[` / `Ctrl+]` |
| Delete | `Delete` |
| Save | `Ctrl+S` (Save As dialog for a merged document) |
| Split | `Ctrl+Shift+P`, "Split", `Enter`; `Tab` through the dialog |
| Undo | `Ctrl+Z` |

**Count for scenario 5** (merge five files, rotate one page, delete one blank page, save): Merge (1), pick files (2: select all, Open), rotate: click page, `Ctrl+]` (2), delete the blank: click page, `Delete` (2), save: `Ctrl+S`, name, Enter (3). **About 10 actions.** See [ux-metrics](../ux-metrics.md#2-task-scripts-click-counts-and-timings).

## 7. Engine and protocol implications

| # | Need |
|---|---|
| E7 | Commands: `InsertPages` (from a source document or a blank page), `DeletePages`, `MovePages`, `RotatePages`, `ExtractPages`, `DuplicatePages` (can be insert from self) |
| E11 | **A new blank document, and importing pages from another document.** PDFium imports between two documents loaded in the same process (`fpdf_ppo`, verify). So the destination's engine must also load each source (read-only, from a broker handle) for the import. Concurrent open documents per engine process are allowed by the single-thread actor rule (§5.2), but memory is capped (2 GB per engine): import sources one at a time and close them |
| | **Engine behaviour on deleted pages:** bookmarks, links, named destinations, form fields and annotations on removed pages must stay consistent. §8.5 requires a test |
| | **Merge of forms and outlines** isn't covered by the page import: confirm and decide what v1.0 supports |
| | **Split** needs repeated `ExtractPages` to broker-provided handles (one per output file), with a progress callback |
| | The thumbnails of source pages come from the same pool engine; cells for merged pages need thumbnails from several sources |
| | **Cross-tab drag** keeps everything in the broker: pages are referenced by (document id, page indices), never moved as bytes through the UI |

## 8. OPEN QUESTIONS

1. **Merging keeps bookmarks and forms?** Likely not in v1.0 with PDFium's import (verify). The `InfoBar` in 3.5 says so. Is that acceptable for scenario 5 (a cover letter, a signed scan, three receipts: probably no forms or bookmarks)?
2. **Signed pages in a merge:** signatures don't survive a merge. Say so when a source has signatures.
3. Should **Delete** ask for confirmation? This spec says no, because it is undoable and shows the undo bar (P6).
4. **Blank page size:** neighbour's size or the first page's? This spec: neighbour's.
5. Does the organiser need a **page-range select** ("select pages 3-7")? Add a small text box in the status bar if testing shows it.
6. **Drag to an Explorer folder** to export pages: not in v1.0.
