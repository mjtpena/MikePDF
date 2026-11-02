# Feature spec: page organiser (insert, delete, reorder, rotate, extract, merge, split)

- Status: Draft for Opus review. **Depends on spike S11** (does PDFium keep outlines, links and forms through page operations and import?) before the implementation route is fixed
- Milestone: M4
- Master prompt: §2.1 (Page operations; merge; split by page range, every N pages or by bookmark), §8.5, §5.4 (page messages), §12.5 journeys 5 and 6, §18 scenario 5
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [save-pipeline](save-pipeline.md) (stage 2 saves page changes; journal), [annotation-core](annotation-core.md) (page identity), engine page messages (schema v6), the outline reader (`GetOutline`)
- Related: [tabs-windows-session](tabs-windows-session.md) (drag between tabs), [command-palette-shortcuts](command-palette-shortcuts.md)

Conventions are in [README.md](README.md).

## 1. Constraints and unknowns

1. **The engine can hold one document at a time** in the document kind (`kMaxDocumentsDocumentKind = 1`) and up to 32 in the pool kind. PDFium's `FPDF_ImportPages` needs the source and the destination in the same process. Inserting pages from another file into the open document therefore needs the document engine to hold **two** documents (the working one and a transient source), or an assembly engine.
2. **PDFium's page import is known to be weaker than it looks** (not verified here): outlines, named destinations, link destinations that point outside the imported range, AcroForm fields, page labels and structure trees are not guaranteed to survive `FPDF_ImportPages`, and PDFium has no API to write an outline at all. §8.5 says "bookmarks and links pointing to moved pages stay correct; test it". Moves within one document keep references to page objects; extract, merge and split build new documents and are where loss would show.
3. **PDFium's incremental save of page-tree changes** (delete, move, import) has not been tested in S4 (it tested annotations and fields). It may force a full rewrite, which would discard history and invalidate signatures.
4. **Two routes**, to be chosen after **spike S11**: (A) PDFium operations in the engine (`InsertPages`, `DeletePages`, `MovePages`, `RotatePages`, `CreateDocument`, then `SaveDocument`); (B) a **page assembler in `MikePDF.PdfWriter`** (writer host message `AssemblePages`, id 1028) that copies the transitive object closure of the chosen pages from strictly parsed sources, renumbers objects, builds a new `/Pages` tree, retargets outlines, destinations and link actions, merges form fields and drops what can't be kept. B is more work (an estimated three to five work packages, not measured), but it can make the guarantees in section 3.5 hold. The acceptance criteria below are written as **requirements any route must meet**; if A fails them, B is needed.

## 2. User stories

1. As someone assembling paperwork, I merge a cover letter, a scan and three receipts, drag pages into order, rotate one, delete a blank page, and export one PDF (§18 scenario 5).
2. As a reader of a long report, I extract chapters 3 to 5 to a new file.
3. As an admin with a combined scan, I split it every 2 pages, or by its bookmarks, into separate files named after the bookmarks.
4. As a keyboard-only user, I move, rotate, delete and insert pages without dragging.
5. As anyone, I undo and redo every page operation and nothing is written until I save.
6. As someone with a signed PDF, I'm warned before changing its pages.

## 3. Detailed behaviour

### 3.1 The organiser view

`pages.organise` replaces the reading view with a **grid of page thumbnails** (the thumbnail sidebar stays available in reading view for simple reorder; the grid is the full tool). Esc or the same command returns to reading, at the page that was selected.

- Thumbnails come from the pool engine at three sizes (small 96, medium 160, large 256 px wide) with a size control. The grid is virtualised (10,000 pages fine).
- Each cell shows the thumbnail (the displayed page: CropBox, `/Rotate` applied), the page number, and badges for rotation changed, newly inserted or moved.
- **Selection:** click; Ctrl+click toggles; Shift+click extends; rubber-band drag; Ctrl+A; keyboard: arrows move focus, Shift+arrows extend, Space toggles, Home and End jump.
- The status line shows "3 of 40 pages selected".

### 3.2 Operations

| Operation | Command | Behaviour |
|---|---|---|
| Rotate | `pages.rotateRight`, `pages.rotateLeft` | Selected pages: page `/Rotate` becomes (current + 90 or - 90) mod 360. This changes the file (unlike view rotation). |
| Delete | `pages.delete` (Delete key) | Removes selected pages. Cannot delete every page ("A document needs at least one page."). No confirmation (undoable); the status line says what happened. |
| Reorder | drag, or `pages.moveTo` | Drag the selection to a new position with an insertion marker; or choose a position number in a small dialog; or `pages.moveBack` / `pages.moveForward` (Ctrl+Shift+Left / Right in the grid) move the selection by one page. Moving keeps selected pages in their relative order. |
| Duplicate | `pages.duplicate` | Inserts a copy of each selected page right after itself. |
| Insert blank | `pages.insertBlank` | One or more blank pages before or after the selection; size taken from the first selected page's displayed size (rotation handled); if there is no selection, the size of the last page; for a new empty case, A4 or Letter by region. |
| Insert from file | `pages.insertFromFile` | File picker (PDF), then a dialog: pages to take (all, or the range grammar of [print](print.md) 3.3) and position (before or after the selection, start, end). Password-protected sources prompt for the password (3 attempts). |
| Insert by drag | drop on the grid | Dropping one or more PDF files from Explorer inserts all their pages at the drop marker after a confirmation naming the page count and position. Dropping pages from another MikePDF tab **copies** them (the source tab is unchanged); whether Shift moves them is OPEN QUESTION OQ-PO-4. The source must have no unsaved page changes, or the dialog says "Unsaved changes in {name} won't be included." |
| Extract | `pages.extract` | Selected pages go to a new PDF (save picker). Option "Remove these pages from this document afterwards" (off by default; if on, it is a separate undoable delete). Permission: copy (bit 5). |

All structural operations except extract are commands on the document's undo stack and dirty the document. Nothing touches the file until Save.

### 3.3 Merge

`file.merge`: a dialog with a list of PDFs (add with the picker or by dropping, reorder by drag or Move up / Move down, remove). Each file has an optional page range (grammar of print 3.3, default all). Output: "Create a new PDF" (save picker) or "Add to the open document at the end". Up to 200 files and 100,000 output pages (proposed limits). Progress with Cancel; cancelling deletes the partial output. The result can be opened in a new tab. Rules for sources: unreadable or damaged files are listed with the reason and skipped only if the user chooses to continue; password-protected files prompt; a source with digital signatures loses its signatures in the merged output (the dialog says so, once); form fields are carried over per section 3.5; a source's outline is merged as a top-level entry named after the file followed by its entries (Acrobat does this; route B only, route A drops outlines unless S11 shows otherwise).

### 3.4 Split

`file.split` on the open document (or a chosen file). Three modes:

| Mode | Input | Output |
|---|---|---|
| **By page ranges** | Comma-separated parts, each becoming one file: `1-3, 4-7, 8-` makes three files. A part may combine pieces with `+`: `1-3+10` is one file of pages 1 to 3 and 10. Grammar and errors as print 3.3, except that a range with start after end is an error and parts may overlap. | One file per part, in the order given. |
| **Every N pages** | N from 1 to the page count. | `ceil(pages / N)` files; the last may be shorter. |
| **By bookmark** | A level (1 to the document's depth; default 1) and "Include pages before the first bookmark as a front-matter file" (default on). | One file per bookmark at the chosen level. |

**By bookmark rules** (the outline is read with `GetOutline`, depth at most 16, at most 10,000 entries considered):

- Only entries at the chosen level are used; their children are ignored.
- An entry needs a resolvable page destination (a page or page-and-position destination). Entries with a URI, an unresolved named destination or any other action are **skipped and listed** in the summary.
- Starting pages are sorted ascending; entries on the same page produce one part named after the first. The part for an entry runs from its page to one before the next entry's page; the last runs to the last page.
- Pages before the first entry go to a front-matter file if the option is on, otherwise they are not exported and the summary says "Pages 1 to 3 weren't included." Nothing is dropped silently.
- If no entries are usable the dialog says so and offers the other modes.

**Output names.** Template `{name}` + `_` + part label, where the label is the page range (`1-3`), the part number, or the bookmark title. Names are sanitised: characters `< > : " / \ | ? *` and controls replaced with `_`; Unicode normalised to NFC; bidirectional controls removed; trailing dots and spaces trimmed; reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`, `LPT1` to `LPT9`, with or without extension) get `_` appended; length limited to 120 characters per name; empty becomes `part {n}`; collisions get ` (2)`, ` (3)`. The user chooses the output folder; existing files are **never** overwritten without a per-file confirmation (Replace, Keep both, Skip, applying to all).

Progress shows "Writing file 3 of 12" with Cancel. Cancel keeps files already completed and deletes the one in progress; a summary lists what was written, skipped and failed, with "Show in folder". The source document is never changed by split.

### 3.5 What must stay correct (requirements for any route)

1. **Reorder, rotate, duplicate, insert, delete inside the document:** every bookmark and every internal link, GoTo action and named destination that points at a page that still exists still lands on **that same page content** afterwards. A page that moved is still the target.
2. **Delete:** a bookmark, link or destination whose page was deleted must not navigate to a different page. It becomes inert ("destination not available"). OPEN QUESTION OQ-PO-3: also remove those bookmarks and links from the file (needs the writer host's structure editing), or leave them inert in the file.
3. **Extract, split, merge:** for pages that are included, bookmarks pointing into the included pages are kept (with a rebuilt outline for the output), internal links between included pages work, links to pages not included are removed or inert (never retargeted to a wrong page), named destinations are kept if their page is, and page labels follow their pages. If route A cannot do this, route B is required.
4. **Form fields on kept pages** are kept with their values; fields on removed pages are removed from `/AcroForm` (no orphan fields). Fields with the same name from different merged sources are renamed or merged per a rule written in the implementation WP (OPEN QUESTION OQ-PO-8: Acrobat merges same-named fields; the safe option is to rename).
5. **Annotations** on kept pages travel with them; replies stay attached; popups too.
6. **Structure tree and tags:** v1 does not promise tagged-PDF preservation through merge and split; the output may be untagged. The product tells the user ("The new file may lose accessibility tags."). OPEN QUESTION OQ-PO-9.
7. **Digital signatures** are not preserved in extract, split or merge (the output is a new document); the open document's own page operations follow the signed-document rule in 3.8.

### 3.6 Engine route A messages and the working-document mirror

The working document's page order in the engine mirrors the model's list of page identities. Each structural command updates both and the broker checks the page count in the reply. After an engine restart the page commands are replayed from the model.

**Undo.** Rotate and Move are undone by their inverse operation. Delete and Insert are undone by re-importing the pages: deleted pages come from the **base file** (kept open as the second document in the document engine), inserted pages from a **journal blob** (3.7). The engine's document limit for the document kind therefore moves from 1 to 2. Alternatively an undo of Delete or Insert reloads the engine from base plus replay; the choice is made in S11 with measurements.

### 3.7 Journal blobs for inserted pages

Inserting pages from another file copies those pages into a PDF blob in the journal's blob folder (created by the assembly engine with `CreateDocument`, `InsertPages` and `SaveDocument`), capped at 100 MB per insert and 500 MB per document. This makes undo, redo and crash recovery independent of the source file still existing. Above the cap, the insert records the source path and fingerprint instead, and the dialog says "If you move or change {name}, undoing this later may not be possible." OPEN QUESTION OQ-PO-6.

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Organise pages | none | `pages.organise` |
| Rotate right / left | none (in the grid: R and Shift+R are OPEN QUESTION OQ-CMD-3) | `pages.rotateRight`, `pages.rotateLeft` |
| Delete | Delete | `pages.delete` |
| Move selection back / forward | Ctrl+Shift+Left / Ctrl+Shift+Right in the grid | `pages.moveBack`, `pages.moveForward` |
| Move to position | none | `pages.moveTo` |
| Duplicate / insert blank / insert from file / extract | none | `pages.duplicate`, `pages.insertBlank`, `pages.insertFromFile`, `pages.extract` |
| Merge / split | none | `file.merge`, `file.split` |
| Undo / redo | Ctrl+Z / Ctrl+Y | `edit.undo`, `edit.redo` |

Touch: tap to select, long-press then drag to reorder; two-finger pinch changes thumbnail size; a selection toolbar appears with Rotate, Delete, Duplicate, Extract. Pen: as touch. Drag has a keyboard equivalent in every case (`pages.moveTo`, `pages.moveBack/Forward`).

## 5. Accessibility

- The grid is a UIA `List` with `ListItem`s exposing `GridItem` row and column and `SelectionItem`; the container supports `Selection` (multiple) and `Grid`. Item name: "Page {n} of {total}, {rotated 90 degrees,} {new,} {selected}".
- Announcements (polite): "Moved 3 pages to position 8", "Deleted 2 pages, 38 pages left", "Rotated 4 pages right", "Inserted 6 pages at position 12", "Extracted 3 pages to {file}", "Merged 3 files, 41 pages", "Split into 5 files". Undo and redo announce what changed.
- Drag-and-drop is never the only way: every drag has a command and a dialog.
- The merge and split dialogs are standard forms with labelled controls; the file list supports keyboard reorder (Alt+Up / Alt+Down) and removal (Delete); progress is a `ProgressBar` with a live region; the final summary is text.
- Thumbnail grid respects high contrast (selection uses system colours), 225% text scaling (labels wrap) and reduced motion (no animated reflow).
- Page thumbnails have no alternative text beyond number and state; the page text is available in reading view.

## 6. Error states

| Situation | Behaviour |
|---|---|
| Delete every page | Refused with message. |
| Source file can't be read, damaged, empty, wrong password (3 tries) | Listed with reason; in the insert dialog nothing is inserted; in merge the user may continue without it. |
| Source or output path invalid (device path, directory) | Refused using the activation validator rules. |
| Disk full during merge, split or extract | Fails the current output, removes it, keeps completed ones, explains. |
| Engine crash mid-operation | The operation is retried once on a new engine; on a second failure it fails with no partial output; in the working document the model is replayed. |
| Permission denied (bit 11 or bit 5 clear, or encrypted without rights) | Command disabled with the owner reason; sources checked individually. |
| Operation would exceed page limits | Refused with the limit named. |
| Cancel | Partial output deleted; working document unchanged. |
| Route A cannot meet 3.5 (found by tests) | Release blocker for the route, not a user-facing error. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** thumbnails and blank-page sizes use displayed boxes; extracted and imported pages keep their `/Rotate`, CropBox and MediaBox exactly; rotation applies to `/Rotate`, never to the MediaBox. Fixtures with `/Rotate` 0/90/180/270 and CropBox origin (50, 30) go through every operation and are compared by render.
- **Mixed page sizes and orientations:** preserved; blank pages take the neighbour's size.
- **Encrypted documents and permissions:** table below.
- **Huge documents:** deleting 4,000 of 5,000 pages is one command; the grid never builds 5,000 thumbnails at once; merging two 1 GB files runs in the assembly engine within its 2 GB memory limit or fails with a clear message; temp space checked first (twice the total input size).
- **Hostile input:** a source with a page tree loop, `/Count` lying, 10 million pages, or an object closure of hundreds of MB: the engine's own limits and the 100,000-page output cap apply; route B's strict parser refuses ambiguous structures (R-31, R-39).
- **Same file as source and destination:** allowed (duplicate); the source is the base or the blob.
- **Source modified between choosing and running:** fingerprint checked at run; mismatch asks to continue.
- **Page identities after merge into the open document:** new identities for new pages; existing annotations keep theirs.
- **Unicode and very long bookmark titles in file names:** section 3.4 sanitising.
- **Duplicate bookmark titles:** collision suffixes.
- **Output into a OneDrive or network folder:** same temp-and-move rules as save ([save-pipeline](save-pipeline.md) 3.6) in the output folder.

Permissions and encryption:

| Operation | Needs (PDF permission bit) | Notes |
|---|---|---|
| Rotate, delete, reorder, insert blank, insert from file, duplicate | 11 (assemble, value 1024) | Also bit 4 (modify contents, value 8) is **not** required for these under the standard |
| Extract, split, merge (as sources) | 5 (copy, value 16) | Each source is checked separately; the output is not password protected and the dialog says so |
| Encrypted open document | Page edits are possible in the model, but Save is blocked until OQ-AC-6 is resolved | Same rule as annotations |

## 8. Undo and redo

Commands: `RotatePages`, `DeletePages`, `MovePages`, `InsertPages`, `InsertBlankPage`, `DuplicatePages` (an insert from self). Each user gesture is one command (a multi-page drag is one). Extract, merge and split do not change the open document and are not commands, except "remove after extract", which is a `DeletePages`. Undo and redo update the grid, the page-identity list, the engine mirror, annotations on affected pages (they follow identities; annotations on deleted pages come back with the page), the panel and the dirty state together. Rules in annotation-core section 8; journey 5 requires "undo all, redo all".

## 9. Journal entries

| `cmd` | Payload |
|---|---|
| `RotatePages` | `pages` (identities), `delta` (+1, +2 or +3 quarter turns) |
| `DeletePages` | `pages` (identities, with their positions before deletion) |
| `MovePages` | `pages` (identities in order), `toIndex` (position among the remaining pages), `from` positions |
| `InsertBlankPage` | `at`, `widthPt`, `heightPt`, `count`, `newPages` (identities) |
| `InsertPages` | `at`, `newPages` (identities), `source`: `{ "blob": "<sha256>" }` or `{ "path": "...", "fingerprint": "...", "ranges": [[first,last]] }` |

The journal header records the page identity list at the baseline so replay is deterministic. Blobs follow 3.7. Inserted file paths and page content copies are user-added data and appear in the privacy notes.

## 10. Save behaviour

Page commands are saved in stage 2 ([save-pipeline](save-pipeline.md)) by PDFium's incremental save if S11 shows it is valid for page-tree changes, otherwise by a full rewrite, which **requires a warning when the document is signed and discards edit history** (ADR-0008 decision 3 allows full rewrite only for Save As optimised, flatten and redaction; page operations would be a fourth case needing Opus's approval, OQ-PO-7). Annotation edits on moved or deleted pages are resolved by page identity after the page order is final. Extract, merge and split write new files through the engine (route A) or the writer host (route B) using temporary files in the output folder, validated by a fresh engine (page count, outline, link and form checks per 3.5) before the move into place, with the original never touched.

## 11. Engine and protocol additions (schema v6; route A)

All engine messages carry `documentId`. Page ranges are two parallel arrays of first and last zero-based indices.

| Message | Id | Request | Reply |
|---|---|---|---|
| `InsertPages` | 56 | `sourceDocumentId` (u32, min 1; another document open in this engine), `firsts`, `lasts` (arrays u32, maxCount 1024, max 9999999, `sameLengthAs`), `destIndex` (u32, max 9999999) | `inserted` (u32, max 10000000), `newPageCount` (u32, max 10000000) |
| `InsertBlankPage` | 57 | `index` (u32, max 9999999), `widthPt`, `heightPt` (f32, exclusive min 0, max 14400), `count` (u32, 1 to 1000) | `newPageCount` |
| `DeletePages` | 58 | `firsts`, `lasts` (as above; must leave at least one page) | `newPageCount` |
| `MovePages` | 59 | `pageIndices` (array u32, maxCount 4096, max 9999999, distinct), `destIndex` (u32) | `newPageCount` (unchanged, a check) |
| `RotatePages` | 60 | `firsts`, `lasts` (as above), `quarterTurns` (u16, 1 to 3) | `newPageCount` |
| `CreateDocument` | 61 | none; pool or assembly kind only | `documentId` (u32, min 1) |
| `SaveDocument` | 62 | defined in [save-pipeline](save-pipeline.md) 11.1 and shipped earlier, in schema v5 | |

Extract is composed: `CreateDocument`, `InsertPages` from the source, `SaveDocument(full)`; the master prompt's `ExtractPages` is not a separate message. The document kind's limit rises from 1 to 2 open documents (`kMaxDocumentsDocumentKind`); the assembly work for merge and split runs in a pool-kind engine (up to 32 documents, 2 GB limit). Validation: ranges must satisfy first <= last < page count and the arrays must have equal length; `destIndex` at most the page count; the engine refuses an operation that leaves zero pages. Frames are small (at most about 10 KB for 1,024 ranges; 21 KB for 4,096 indices). `ReplyChecks` verifies the reply's page counts against the broker's expectation.

Route B adds the writer-host message `AssemblePages` (1028), specified when S11 chooses it.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Pages_Organise` | Organise pages |
| `Pages_Selected_One` / `_Other` | 1 page selected / {0} pages selected |
| `Pages_Item_Automation` | Page {0} of {1}{2} |
| `Pages_Item_Rotated` | , rotated {0} degrees |
| `Pages_Item_New` | , new |
| `Pages_Item_Selected` | , selected |
| `Pages_Size_Small` / `_Medium` / `_Large` | Small / Medium / Large thumbnails |
| `Pages_Delete_Last` | A document needs at least one page. |
| `Pages_MoveTo_Title` | Move pages |
| `Pages_MoveTo_Prompt` | Move to position (1 to {0}) |
| `Pages_Insert_Title` | Insert pages |
| `Pages_Insert_Which` | Pages to insert |
| `Pages_Insert_Where` | Insert |
| `Pages_Insert_Before` / `_After` / `_AtStart` / `_AtEnd` | Before the selection / After the selection / At the start / At the end |
| `Pages_Insert_Drop_Confirm` | Insert {0} pages from {1} at position {2}? |
| `Pages_Insert_Unsaved` | Unsaved changes in {0} won't be included. |
| `Pages_Extract_Title` | Extract pages |
| `Pages_Extract_Remove` | Remove these pages from this document afterwards |
| `Pages_Extract_NoProtection` | The new file won't be password protected. |
| `Merge_Title` | Merge PDFs |
| `Merge_Add` / `_Remove` / `_MoveUp` / `_MoveDown` | Add files / Remove / Move up / Move down |
| `Merge_Range` | Pages (all if empty) |
| `Merge_Output_New` / `_AddToOpen` | Create a new PDF / Add to the open document |
| `Merge_Signature_Note` | Digital signatures in the files you merge won't be kept. |
| `Merge_Tags_Note` | The new file may lose accessibility tags. |
| `Merge_Failed_File` | {0} couldn't be used: {1} |
| `Merge_Limit` | You can merge up to {0} files and {1} pages at once. |
| `Split_Title` | Split a PDF |
| `Split_Mode_Ranges` / `_EveryN` / `_Bookmarks` | By page ranges / Every N pages / By bookmarks |
| `Split_Ranges_Hint` | For example 1-3, 4-7, 8- (use + to join pieces: 1-3+10) |
| `Split_Level` | Bookmark level |
| `Split_Front` | Include pages before the first bookmark |
| `Split_NoBookmarks` | This document has no bookmarks MikePDF can use. Try another way to split it. |
| `Split_Skipped_One` / `_Other` | 1 bookmark was skipped because it doesn't go to a page. / {0} bookmarks were skipped because they don't go to a page. |
| `Split_NotIncluded` | Pages {0} weren't included. |
| `Split_Folder` | Output folder |
| `Split_Progress` | Writing file {0} of {1} |
| `Split_Replace` / `_KeepBoth` / `_Skip` | Replace / Keep both / Skip |
| `Split_Summary` | {0} files written, {1} skipped, {2} failed. |
| `Split_ShowInFolder` | Show in folder |
| `Pages_Moved_Announce_One` / `_Other` | Moved 1 page to position {1} / Moved {0} pages to position {1} |
| `Pages_Deleted_Announce` | Deleted {0} pages, {1} pages left |
| `Pages_Rotated_Announce` | Rotated {0} pages {1} |
| `Pages_Inserted_Announce` | Inserted {0} pages at position {1} |
| `Pages_Extracted_Announce` | Extracted {0} pages to {1} |
| `Merge_Done_Announce` | Merged {0} files, {1} pages |
| `Split_Done_Announce` | Split into {0} files |
| `Pages_Disabled_Owner` | The document's owner doesn't allow changing pages. |
| `Pages_Disabled_Copy` | The document's owner doesn't allow copying pages. |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-PO-1 | Route A or B after spike S11. | Opus |
| OQ-PO-2 | Document engine limit 1 to 2, or an assembly engine for every cross-document operation. | Opus |
| OQ-PO-3 | Bookmarks and links to deleted pages: inert in the file, or removed. | User (product), Opus |
| OQ-PO-4 | Dragging pages between tabs: copy only, or Shift to move. | User (product) |
| OQ-PO-5 | Is the `+` joiner in split ranges wanted. | User (product) |
| OQ-PO-6 | Journal blob for inserted pages (proposed) versus path reference. | Opus |
| OQ-PO-7 | Page operations on an incremental-only save: if PDFium needs a full rewrite, amend ADR-0008 decision 3. | Opus |
| OQ-PO-8 | Same-name form fields from different merged sources: rename (safe) or merge. | Opus |
| OQ-PO-9 | Tagged PDF through merge and split: not promised in v1. | User (product) |
| OQ-PO-10 | Page labels (`/PageLabels`) shown in the grid and used in ranges. | User (product) |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| PO-01 | Selection model: click, Ctrl, Shift, Ctrl+A, rubber-band, keyboard extend, over a 100-page model; selection survives scrolling and thumbnail resize. | Unit (`MikePDF.Core.Tests`) |
| PO-02 | Page identity bookkeeping: for random sequences of insert, delete, move, rotate, duplicate on a 50-page model, the identity list matches an independent reference implementation and counts never go below 1. | Unit (property test) |
| PO-03 | `MovePages` semantics: moving a selection keeps relative order, handles destination inside the selection, at start and at end; `toIndex` meaning is among the remaining pages. | Unit |
| PO-04 | Range grammars reuse the print parser; split parts with `+`, overlaps, open ends and errors behave as specified. | Unit |
| PO-05 | Split by every N: part counts and boundaries for N = 1, page count, page count + 1 (error), and a non-divisor. | Unit |
| PO-06 | Bookmark mapping on synthetic outlines: levels, same-page entries, backwards entries, entries without page destinations (skipped and listed), no usable entries, front matter on and off, nothing dropped silently. | Unit |
| PO-07 | File naming: every rule in 3.4 (forbidden characters, reserved names, trailing dots, length, NFC, bidi controls, collisions, empty). Fuzz-style test with 10,000 random titles never yields an invalid Windows file name. | Unit |
| PO-08 | Protocol golden vectors and malformed frames for messages 56 to 61 (limits exceeded by one, first > last, unequal arrays, zero-page result) in both codecs; engine refuses to delete all pages. | Unit (Protocol + Engine C++) |
| PO-09 | Engine operations on fixtures: insert, blank page, delete, move, rotate, duplicate produce the expected page count, order (by unique page text via `GetPageText`) and `/Rotate`; render of each page equals the expected source page render (SSIM >= 0.99, same engine). | Integration |
| PO-10 | Rotated and CropBox fixtures (`/Rotate` 0/90/180/270, CropBox origin (50, 30)) keep boxes and rotation exactly through insert, move, extract and merge; blank pages take the neighbour's displayed size. | Integration |
| PO-11 | **Link and bookmark integrity (§8.5):** a 10-page fixture whose pages carry unique text, with bookmarks, GoTo links and named destinations to every page. After reordering, rotating and duplicating, activating every bookmark, link and named destination lands on the page whose text matches the original target. | Integration |
| PO-12 | After deleting a page, every bookmark, link and destination that targeted it is inert (no navigation, no wrong-page navigation, no crash); others unchanged. | Integration |
| PO-13 | Extract, split and merge preserve, for included pages: bookmarks into those pages (rebuilt outline), internal links between included pages, named destinations, page labels, annotations with replies, form fields with values; links to excluded pages are inert or removed. Fixture-driven. If route A fails this, the criterion fails and route B is required. | Integration + Fidelity |
| PO-14 | Forms through merge and split: no orphan fields in `/AcroForm`, same-name rule per OQ-PO-8, values preserved. | Integration |
| PO-15 | Outputs open and render in fresh PDFium and pdf.js; `qpdf --check` passes; page count and page sizes equal the plan; Acrobat Reader and Edge cells recorded as pass, fail or **not measured** (R-15). | Fidelity harness |
| PO-16 | Page-operation save: for each operation on classic and xref-stream sources, the saved file validates, reopens with the right order and rotation, and bytes before the update are unchanged if incremental; if a full rewrite is used it is flagged and the signed-document warning appears. | Integration + Fidelity |
| PO-17 | Undo all and redo all (journey 5): reorder, rotate, delete, then undo every step restores the original order, rotation and pages (render compare) and redo reproduces the final state; annotations follow their pages and come back with deleted pages. | UI journey + Integration |
| PO-18 | Journal replay after a crash restores page order, rotation, deletions, blank pages and inserted pages (from blobs even if the source file is deleted). | Integration |
| PO-19 | Merge three PDFs then split by bookmark (journey 6): outputs have the expected page counts and names, bookmark-derived titles, and content equals the planned page ranges. | UI journey |
| PO-20 | Scenario 5 (§18): merge cover letter, scan and three receipts; reorder, rotate a page, delete a blank page; export one PDF; the output is valid in PDFium and pdf.js (Edge **not measured** until the manual pass). | UI journey + Fidelity |
| PO-21 | Permissions: bit 11 clear disables rotate, delete, reorder, insert; bit 5 clear disables extract, split and merge with that source; encrypted sources prompt for passwords (3 attempts); outputs are unprotected and the notice shows. | Unit + Integration |
| PO-22 | Signed document: page commands trigger the signed-document confirmation; merge warns that signatures are not kept. | Integration |
| PO-23 | Cancel in merge and split deletes the partial output, keeps completed outputs, leaves no temporary file, and the working document is unchanged. | Integration |
| PO-24 | Overwrite policy: existing output files are never replaced without confirmation; Keep both produces ` (2)` names. | Integration |
| PO-25 | Engine crash during merge or split retries once and then fails cleanly with no partial files. | Integration (fault injection) |
| PO-26 | Huge inputs: deleting 4,000 of 5,000 pages is one command and completes; the grid never creates more than the visible thumbnails plus a margin; merge of 100,000 total pages is accepted and 100,001 refused; memory stays within the engine limit. | Integration (perf) |
| PO-27 | Hostile sources (page-tree loop, lying `/Count`, 10 M pages, giant closure, damaged) never crash or hang; results are errors or bounded outputs. | Integration + fuzz |
| PO-28 | Drag and drop: dropping PDFs inserts at the marker after confirmation; dropping a device path or a directory is refused; cross-tab drag copies and warns about unsaved changes. | UI journey |
| PO-29 | Keyboard-only: organise, select, move with `pages.moveBack/Forward` and `pages.moveTo`, rotate, delete, insert blank, extract, undo, with no mouse. | UI journey |
| PO-30 | UIA: list and grid patterns, item names and states, every announcement in section 12 raised once. Raw `IUIAutomation`. | UI journey (a11y) |
| PO-31 | High contrast, 225% text, pseudo-RTL: grid, merge and split dialogs remain usable (screenshot compare). | UI journey (visual) |
| PO-32 | Thumbnails: first visible thumbnails appear within 250 ms after opening the organiser on a 40-page file (recorded; budget proposed). | Integration (perf) |
