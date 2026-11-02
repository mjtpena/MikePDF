# 06 Side panes: thumbnails, bookmarks (outline), annotations, attachments

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 1 (annotations list after marking up a report), 2 (notes with replies), 5 (thumbnails for reordering), 6 (outline as the heading map), 10 (virtualised thumbnails of a 5,000-page file)
- Replaces: the two-toggle sidebar with `ThumbnailList` and `OutlineTree` in [MainWindow.xaml](../../../src/MikePDF.App/MainWindow.xaml) **[repo]**. The tree and list controls, their AutomationIds and the loading and error strings stay.

## 1. Purpose

Four panes in the left pane's `SelectorBar`, each answering a different question: where am I (thumbnails), what is the structure (bookmarks), what has been marked (annotations), what is attached (attachments). **Search results** is a fifth pane that appears after a search ([05](05-find.md)). **Signatures** (P1) and **Layers** (P2) are reserved slots; they appear only for documents that have them and only when shipped.

## 2. Layout

### 2.1 The left pane, common frame

```text
 +-----------------------------------+
 | [thm] [out] [ann] [att] [srch]    |  SelectorBar: icons only, 40 x 40, selected item has a 3 epx accent bar
 | --------------------------------- |
 |  Thumbnails                  [..] |  header: Subtitle + overflow (pane-specific settings)
 | --------------------------------- |
 |                                   |
 |          pane content             |
 |                                   |
 +-----------------------------------+
   296 epx wide, resizable 200 to 480
```

### 2.2 Thumbnails

```text
 +-----------------------------------+
 | [thm][out][ann][att]              |
 |  Thumbnails                   [..] |   [..]: Small / Medium / Large, Select all
 |                                   |
 |        +------------+             |
 |        |   page 1   |             |   thumbnail width 128 epx (Medium), 96 (Small), 192 (Large)
 |        +------------+             |
 |             1                     |   label (Caption). The page label if present ("iv")
 |        +============+             |
 |        #   page 2   # <- current   |   2 epx accent border and a filled backplate for the current page
 |        +============+             |
 |             2     (*)             |   (*) small dot: this page has annotations
 |        +------------+             |
 |        |   page 3   |             |
 +-----------------------------------+
```

### 2.3 Bookmarks (outline)

```text
 +-----------------------------------+
 | [thm][OUT][ann][att]              |
 |  Bookmarks                    [..] |  [..]: Expand all, Collapse all, Follow reading position (toggle)
 |  [ Filter bookmarks          x ]  |  appears when the tree has more than 30 items
 |  v 1  Introduction                |
 |      1.1  Purpose                 |
 |      1.2  Scope                   |
 |  v 2  Findings                    |
 |    > 2.1  Revenue                 |
 |   [ 2.2  Contingent liabilities ] |  the entry for the current page range has the accent bar
 |  > 3  Notes to the accounts       |
 +-----------------------------------+
```

### 2.4 Annotations

```text
 +-----------------------------------+
 | [thm][out][ANN][att]              |
 |  Annotations (24)           [..]  |  [..]: Sort by, Filter, Export summary (P1)
 |  [ Search comments          x ]   |
 |  [Filter v]  Mine only [ ]  Sort: Page v
 |  ---------------------------------|
 |  Page 41                          |
 |  [hl-yellow] "...contingent       |   type icon (shape-coded) tinted with the annotation colour
 |     liabilities are disclosed..." |
 |     Dana  2 hours ago             |   Caption
 |  [note] Check against note 12     |
 |     Mike  Yesterday  2 replies  [Open] [Completed]  chips
 |  Page 44                          |
 |  [ink] Ink                        |
 |     Mike  Mon                     |
 +-----------------------------------+
```

### 2.5 Attachments

```text
 +-----------------------------------+
 | [thm][out][ann][ATT]              |
 |  Attachments (2)           [Save all]
 |  [xlsx] schedule-4.xlsx           |
 |         18 KB   Revenue schedule  |
 |         [Save as...]              |
 |  [exe!] setup.exe    <- warning glyph |
 |         1.2 MB  "This file type can run programs. MikePDF never runs files."|
 |         [Save as...]              |
 +-----------------------------------+
```

## 3. Behaviour

### 3.1 Common

1. **Opening.** `F4` toggles the left pane. `Alt+Shift+T`, `B`, `A`, `E` open it on a specific pane. The pane shown is remembered per document, with a global default in Settings.
2. **Follow the reading position.** Thumbnails scroll to keep the current page in view (only when the pane is not being scrolled by the user in the last 3 s); bookmarks highlight the entry whose page range contains the current page (toggle, default on).
3. **Selecting an entry** in any pane moves the page well to that destination, sets the current page, and gives the destination a brief 2 epx arrival outline (1 s, instant under reduced motion). **Keyboard focus stays in the pane** (arrow keys continue to walk); `Enter` moves focus to the page, as does `F6`.
4. **Virtualisation.** All lists virtualise. Only visible items plus a buffer of two screens are realised (ADR-0006: thumbnails are cached within +/-8 screens, 128 px wide, from the pool engine).

### 3.2 Thumbnails (§2.1, §8.5)

- **Select:** click selects and navigates; `Ctrl+click` toggles; `Shift+click` ranges; `Ctrl+A` selects all pages when the list has focus. The **current page** (the page being read) and the **selected pages** (for operations) are different things: the current page has the filled backplate, selected pages have the accent border and a check-glyph in the corner (so it's not colour only).
- **Operations on the selection:** right-click, Menu key or `Shift+F10`: Rotate left (`Ctrl+[`), Rotate right (`Ctrl+]`), Delete (`Delete`), Extract pages..., Insert blank page before or after, Insert pages from file..., Copy pages, Print selected pages. These are the same commands as the page organiser ([10](10-page-organiser.md)) and are undoable. A multi-page delete shows the undo bar.
- **Reorder** by drag, with an insertion line. Keyboard: `Alt+Shift+Left/Right` moves the selection earlier or later by one ([10](10-page-organiser.md)). The thumbnails pane supports the same drag semantics as the organiser; for a bigger target and file-level operations the **Organise pages** button in the pane header opens the organiser view.
- **Size:** Small (96 epx), Medium (128), Large (192). `Ctrl+wheel` over the pane changes size.
- **Annotation dot:** a 6 epx dot on thumbnails whose page has annotations (decoration, plus the count in the accessible name).
- **Rotated or cropped pages** show as they are shown in the page well (with `/Rotate`, `/CropBox`).

### 3.3 Bookmarks (outline, §2.1)

- `TreeView`: `Right` expands, `Left` collapses, `Up` and `Down` move, `Home` and `End`, type-ahead on the first letters. **Lazy expansion** from the engine as today (`Expanding`, `Outline_More` for capped lists **[repo]**).
- **Invoke:** goes to the destination. A bookmark that points to a **named destination** is resolved by the engine (`ResolveDestination`, §5.4). A bookmark that leads outside the document shows the existing banner ("This bookmark leads outside the document") **[repo]**. Nothing is opened.
- **Filter:** a text box appears when the tree has more than 30 entries (no shortcut; `Shift+Tab` from the tree reaches it, and typing inside the tree is type-ahead, not filtering). Matching entries are shown with their parents; others hide. `Esc` clears.
- **Bookmarks for a document with none:** "This document has no bookmarks." (existing string `Outline_Empty`) followed by "Use the thumbnails to move around, or press Ctrl+G to go to a page." **[design]**
- **Structure tags.** For a tagged PDF without bookmarks, the pane could fall back to the structure tree's headings. This is a P1 idea and depends on Q15 (headings).

### 3.4 Annotations (§2.1 "annotation list panel with filter, sort, jump-to")

- **Content:** every annotation except form widgets and links: highlight, underline, strikethrough, squiggly, note (with its reply count and review state), free text, ink (one row per annotation, not per stroke), shapes, stamps, redaction marks (P1). Rows are grouped by page.
- **Row:** type icon (a shape that differs per type, tinted with the annotation's colour), a two-line excerpt (for markup: the marked text; for notes: the note's text; for ink and shapes: the type name), then author, relative time and chips: reply count, review state (Accepted, Rejected, Cancelled, Completed; None shows nothing).
- **Filter** (flyout): by type, by author, by colour, by review state, by "Mine". **Search comments** matches excerpt, note text and author. **Sort:** Page (default), Date (newest first), Author, Type.
- **Select a row:** go to the annotation, select it on the page, and open its thread in the right pane when that pane is open (it does not open it on its own). **Double-click** or `Enter` opens the right pane on the thread and moves focus to its reply box.
- **Delete:** `Delete` on a row deletes the annotation (undoable, with the undo bar). Replies delete with their parent: the undo bar says "Deleted a note and 2 replies".
- **Review status** can be changed from the row's menu (Set status).
- **Counts:** "Annotations (24)" counts what the filter hides too: "Annotations (24, 6 shown)" when filtered.
- **Large documents** (hundreds of annotations on thousands of pages): `ListAnnotations` is requested **by page range around the viewport first**, then in the background; the list grows. Sorting by page needs nothing more. Sorting by date or filtering by author needs the full list: show "Reading annotations, 62%" with a determinate bar and enable those options only when complete (or, they apply to what is loaded and the heading says "so far").
- **Export summary** (comments list as text or PDF): P1, not in v1.0.

### 3.5 Attachments (§2.3 P2)

- The pane appears only for documents that **have** attachments (file attachment annotations and the embedded files name tree). A **PDF portfolio** (collection) is P2: show a banner "This document is a portfolio. Its files are listed here" and list the entries; don't render the portfolio's own UI.
- **Each row:** file icon from the extension (shell icon cache, never executed), name, size, description, modified time (if given).
- **Actions:** **Save as...** and **Save all...** only. **There is no Open**. The reason is in §13.1 ("show attachments as files to save only, with Mark-of-the-Web applied"). The broker writes the file to the user-chosen location with the Mark of the Web (`Zone.Identifier`) applied.
- **Executable types** (the list in the broker, e.g. `.exe .msi .bat .cmd .ps1 .js .vbs .scr .lnk .jar` **[design: the list is illustrative; the broker owns the real one]**) show a warning glyph and the sentence "This file type can run programs. MikePDF never runs files." Saving is allowed, with a confirmation dialog showing the file name and type.
- **Embedded PDF attachments** also have no **Open**. To read one, save it and open it, so that an attachment can't silently become a document inside the same trust context.

### 3.6 Search results

See [05-find.md](05-find.md).

## 4. States

| State | Thumbnails | Bookmarks | Annotations | Attachments |
|---|---|---|---|---|
| **Loading** | Page-shaped placeholders at the right aspect ratio (needs page sizes) with the page label; static under reduced motion. The `Busy` ring is for the document, not the pane | "Loading bookmarks..." (`Outline_Loading` **[repo]**), live region polite | "Reading annotations..." with a small ring; the first page range appears as soon as it arrives | "Reading attachments..." |
| **Empty** | Not possible (a document has at least one page) | "This document has no bookmarks." **[repo]** plus a hint | "No comments yet. Select text and press Ctrl+H to highlight it, or Ctrl+M to add a note." When the filter hides everything: "No annotations match. Clear the filter" with a **Clear filter** button | The pane is not offered |
| **Error** | A thumbnail that failed shows a page glyph with a warning badge and a tooltip "This thumbnail couldn't be drawn"; clicking the page still navigates. It retries when scrolled back into view | "The bookmarks could not be read." (`Outline_Failed` **[repo]**) with a **Retry** button | "The annotations couldn't be read." with **Retry**. Annotations already loaded stay | "The attachments couldn't be read." with **Retry** |
| **Keyboard focus** | One tab stop for the list; arrows move, `Space` toggles selection, `Enter` goes to the page. Focus ring on the item (two-ring) | One tab stop; tree arrows | One tab stop for the list; `Tab` moves to the filter button, search box and sort control (they sit above the list in order) | One tab stop; `Tab` inside a row moves between **Save as** and the next row |
| **High contrast** | Current page: 2 epx `Highlight` border and `HighlightText` label; selected: 2 epx border plus the check glyph; 1 epx `WindowText` thumbnail edge | Selected node uses `Highlight`/`HighlightText`; current-position bar is a 3 epx `Highlight` rule | Type icons are `WindowText` shapes; the colour is shown as a text name ("Yellow") in the row's second line | Warning glyph is `WindowText` plus the text |
| **225% text scale** | Labels wrap; thumbnails don't scale (document content) but the row height grows; selector icons don't scale | Node text wraps up to three lines (G-TS5) and the row grows; the indentation per level stays 16 epx | The excerpt allows three lines, then ellipsis; chips wrap onto their own line; the filter row wraps into two rows | File name and description wrap; button text stays on one line |
| **Narrow window** | The pane is an overlay. At XS it covers the page well up to 100% of the width minus 48 epx (the dim scrim shows the page edge) and closes after navigating, unless pinned with the pin button in the header | same | same | same |
| **Touch** | Row hit height 48 epx; the **Select** mode: a long-press on a thumbnail enters multi-select with checkboxes (so there is no need for Ctrl or Shift); drag-to-reorder starts with a long-press and shows the insertion line | Tap expands via the chevron (32 x 32 hit), tap on the text navigates | Rows 56 epx; swipe is not used; the row menu opens by tap on its `...` button | same |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Pane group | Pane | "Panes" | `SidebarPane` **[repo]** |
| Selector | Tab | "Pane" | `PaneSelector` (replaces `SidebarTabs` **[repo]**) |
| Selector items | TabItem | "Thumbnails", "Bookmarks", "Annotations", "Attachments", "Search results" | `ThumbnailsTab`, `BookmarksTab` **[repo]**, `AnnotationsTab`, `AttachmentsTab`, `SearchTab` |
| Thumbnail list | List | "Page thumbnails" **[repo]** | `ThumbnailList` **[repo]** |
| Thumbnail | ListItem | "Page {n}" **[repo]**; with a label "Page iv, 4 of 40"; "has 3 annotations"; "selected" | `Thumb_{n}` **[repo: `AutomationId` is on the item]** |
| Outline tree | Tree | "Bookmarks" **[repo]** | `OutlineTree` **[repo]** |
| Outline node | TreeItem | The title (or "(untitled)" **[repo]**), with "expanded" or "collapsed", the level and "page {n}" | `Outline_{path}` |
| Outline message | Text (live, polite) | The loading, empty or error message | `OutlineMessage` **[repo]** |
| Annotation list | List | "Annotations" | `AnnotationList` |
| Annotation row | ListItem | "{Type}, page {n}, by {author}, {time}: {excerpt}. {k} replies. Status: {state}" | `Annot_{id}` |
| Filter button | Button with a flyout | "Filter annotations" | `AnnotFilterButton` |
| Attachment list | List | "Attachments" | `AttachmentList` |
| Attachment row | ListItem | "{name}, {size}, {description}. Warning: this file type can run programs" (when applicable) | `Attach_{n}` |
| Save as | Button | "Save {name} as" | `AttachSave_{n}` |

Narrator, intended: switching pane: "Annotations, 24 items" (polite). Moving through the annotation list: "Highlight, page 41, by Dana, 2 hours ago: contingent liabilities are disclosed. 0 replies. List item 3 of 24." Moving through thumbnails: "Page 12, 12 of 612" and when the page has annotations "Page 12, has 3 annotations". Bookmarks: "Findings, expanded, level 1, page 8, tree item".

**Heading map.** The bookmarks tree is the only structure map the UI provides for a screen-reader user in v1.0, until the heading question (Q15) is settled. For tagged PDFs, the pane may later be fed from the structure tree.

## 6. Keyboard path

| Task | Keys |
|---|---|
| Show or hide the pane | `F4` |
| Open Thumbnails, Bookmarks, Annotations, Attachments | `Alt+Shift+T`, `B`, `A`, `E` |
| Walk a list | `Up`, `Down`, `Home`, `End`; `PageUp`/`PageDown` by a screen |
| Go to the page | `Enter` on an item (moves focus to the page); the page well follows the selection as you arrow if **Follow selection** is on (default off for lists, so arrow keys don't scroll the page unexpectedly) |
| Rotate or delete selected pages (thumbnails) | `Ctrl+[`, `Ctrl+]`, `Delete` |
| Move selected pages | `Alt+Shift+Left/Right` |
| Filter annotations | `Tab` to the filter button, `Enter` |
| Delete an annotation from the list | `Delete` |
| Return to the page | `F6` |

## 7. Engine and protocol implications

- **E5** `ListAnnotations` with geometry, authorship, dates, reply links, review state. Needs paging by page range.
- **E9** `GetAttachments`, `SaveAttachment` (broker handle; Mark of the Web). **The engine must never execute or open an attachment.** Attachment names are untrusted strings: sanitise path separators and reserved names in the broker before showing a default save name.
- **E17 Layers:** PDFium has no public API to list optional content groups **[unverified]**. Do not promise Layers before an ADR.
- **E16** `GetStructTree` for a headings fallback.
- Thumbnails come from the pool engine (ADR-0004, ADR-0006): no new messages.

## 8. OPEN QUESTIONS

1. Should the bookmarks filter have a dedicated shortcut? None assigned. Type-ahead in the tree covers short lists.
2. Should **Follow selection** be on for thumbnails (arrow keys scroll the page)? This spec: on for thumbnails (a page strip is a navigator), off for annotations and bookmarks.
3. Is deleting an annotation from a list row with one key too easy? It is undoable and shows the undo bar. Keep, but test.
4. Should an attachment ever be openable? §13.1 says no for v1.0. Keep.
5. Q15 (headings for screen readers) decides whether the Bookmarks pane gets a structure-tree fallback.
