# Feature spec: the annotation list panel (filter, sort, jump-to)

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 ("annotation list panel with filter, sort, and jump-to"), §10.1 (left pane has an annotations tab; the right pane holds comments), §10.3, §11 (huge documents), §18 scenarios 1 and 6
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md) (`ListAnnotations`, `CountAnnotations`, the edit model), [sticky-notes](sticky-notes.md) (thread pane), the sidebar in `MainWindow.Sidebar.cs`
- Related: [text-markup](text-markup.md), [forms](forms.md) (widgets are not listed here)

Conventions are in [README.md](README.md).

## 1. User stories

1. As an accountant with 600 pages and 80 comments, I see all my highlights and notes in one list and jump to any of them (§18 scenario 1).
2. As a reviewer, I filter to one colleague's comments, or to comments not yet Accepted, and step through them.
3. As a reviewer, I sort by date to see what changed last.
4. As a Narrator user, I move through the list by keyboard, hear what each item is, and jump to it (§18 scenario 6).
5. As someone with a 5,000-page file, opening the panel never freezes the reader.

## 2. Detailed behaviour

### 2.1 Where it lives

The panel is a tab of the **left pane** ("Annotations", next to thumbnails and outline, §10.1). Selecting an item shows its thread in the **right pane** (Comments). `annot.panel` opens the left pane on the Annotations tab and focuses its list.

### 2.2 What is listed

Every annotation of these kinds, from the file and from pending edits: highlight, underline, strikethrough, squiggly, note, text box, drawing, rectangle, ellipse, line, arrow, polygon, polyline, stamp. Replies are shown nested under their root, not as separate rows, when "Show replies as threads" is on (default). With it off, every reply is a row. **Never listed:** links, form widgets, popups. **Listed read-only only when "Show other types" is on** (default off): file attachments, sound, caret, redact and unknown subtypes. Pending (unsaved) items are marked "Not saved".

### 2.3 Row content

Kind icon and name, page number, author, date (relative such as "Yesterday", with the absolute date and time as tooltip and in the accessible name), a one-line excerpt (for markup the marked text; for notes and text boxes the comment or text; for drawings, stamps and shapes the alternative text or nothing), reply count, and review state when set. Excerpts are at most 192 bytes as the engine reports them; longer text is shown elided.

All text from the file is **sanitised for display**: control characters and Unicode bidirectional override characters (U+202A to U+202E, U+2066 to U+2069) are removed, so a hostile author name or comment can't reorder the row (spoofing).

### 2.4 Filter

A filter bar above the list. Filters combine with AND; within one filter, values combine with OR.

| Filter | Values |
|---|---|
| Type | any subset of the kinds in 2.2 |
| Author | any subset of the authors found so far (the list grows as the scan proceeds) |
| Review state | None, Accepted, Rejected, Cancelled, Completed |
| Date | Any, Today, Last 7 days, Last 30 days, Custom range (created or modified, chosen) |
| Pages | a range in the print grammar ([print](print.md) section 3.3) |
| Text | substring in author, kind name and excerpt (case, diacritic and width insensitive) |
| Quick toggles | "Only mine" (author equals the current author), "Unsaved only" |

The text filter searches **only the excerpt (first 192 bytes), author and kind**, not the full comment, because full text would need a `GetAnnotation` call per item. OPEN QUESTION OQ-PNL-3: add an engine-side `SearchAnnotations` for full-text search, or accept this limit in v1. "Clear filters" resets all. A summary shows "12 of 48 shown".

### 2.5 Sort

Page order (default: page ascending, then position in the page's annotation order, with page headers as group headings), Date created (newest or oldest first), Date modified, Author (A to Z, then date), Type (then page). Sorting is stable. Threads sort by their root.

### 2.6 Jump to

- **Click, tap, Enter or double-click** on an item: scroll the document so the annotation is visible (centred horizontally, in the upper third vertically, at the current zoom; page top for an annotation outside its page), select it (the overlay shows its handles), open its thread in the right pane, and add a history entry (back/forward from the m2 work).
- **Arrow keys** move the list selection **without** scrolling the page. A toggle "Follow selection" (default off) makes arrow movement also jump, for users who step through comments one by one.
- **Focus after a jump:** stays in the list for keyboard users (so they can continue down the list). Pressing Tab moves to the annotation on the page.
- The reverse also holds: selecting an annotation on the page selects its row and scrolls the row into view.

### 2.7 Actions

On the selected row(s): Go to, Reply, Edit (comment), Set review state, Copy comment text, Delete. Multi-select with Shift and Ctrl clicks and Ctrl+A; Delete and Set review state apply to all selected (one undo step); Reply and Edit need one item. Actions use the underlying commands, so permissions, class rules and undo are annotation-core's. A deleted row disappears at once.

### 2.8 Loading and big documents

- The panel loads in the background on the **pool engine** (never the document's own engine), starting when it is first shown.
- Step 1: `CountAnnotations` in windows of 5,000 pages tells which pages have user-visible annotations. Step 2: `ListAnnotations` (64 per request) for those pages, nearest to the viewport first, then in page order. Visible rows appear as soon as they arrive.
- A progress strip shows "Reading comments: 1,200 of 5,000 pages" with Cancel. Cancelling keeps what was read and shows "Not all pages have been read" with Continue. Closing the document cancels.
- Memory cap: 100,000 items; beyond that the panel says "Showing the first 100,000 comments in page order."
- The list is **virtualised**; scrolling a list of 100,000 rows must keep up.
- The panel merges two sources: items loaded from the file (minus those hidden by edits) and pending items from the edit model. After a save, the **rebase** replaces loaded items by id without clearing the list or losing the selection or scroll position ([annotation-core](annotation-core.md) AC-15).
- Scanning does not delay rendering: it yields to document-engine requests and pauses while the user is scrolling fast.

### 2.9 Empty and error states

"No comments in this document." (with "Add a comment" pointing at the tools); "Nothing matches these filters." with Clear filters; "Couldn't read comments on page {n}" rows with Retry for pages that failed; "Comments can't be read in this document" if the engine refuses.

## 3. Keyboard, pen and touch

| Action | Behaviour |
|---|---|
| Open the panel and focus the list | `annot.panel` (no default shortcut) |
| Move | Up / Down, Home / End, Page Up / Page Down |
| Jump | Enter (also double-click, tap) |
| Select all | Ctrl+A |
| Delete / Edit / Reply | Delete / F2 / `annot.reply` |
| Filters | Tab into the filter bar; each filter is a button with a flyout that has check boxes; Esc closes it |
| Clear filters | `annot.clearFilters` (no default) |

Touch: tap jumps; long press opens the context menu; filters are buttons at 40 DIP. Pen: as touch.

## 4. Accessibility

- The list is a UIA `List` with `ListItem`s (or a `Tree` when threads are nested, with level information); page headers are `Group` headings. Multi-select uses the `Selection` pattern with `CanSelectMultiple`.
- Item name: "{kind}, page {n}, {author}, {absolute date}: {excerpt}, {k} replies, {state}, not saved". `AutomationId` `annotrow:{id}`.
- Live regions (polite, debounced 300 ms): the count after filter or sort changes ("12 of 48 comments shown"); scan progress at 25% steps; "Reading comments finished".
- Filter flyouts are standard menus with check items and names. Active filters are listed as text in the summary, not only by chip colour.
- Jump announces the destination: "Page 14, highlight by Alex."
- The progress strip is a `ProgressBar` with name and value.
- High contrast, 225% text scale, RTL mirroring: rows reflow; the page number column keeps a minimum width.

## 5. Error states

Listed in 2.9. In addition: if an annotation's page was deleted by a pending page operation, its row is hidden (it returns on undo). If the engine restarts during a scan, the scan resumes from the last completed page.

## 6. Edge cases

- **Rotated pages and CropBox offsets:** jump uses the annotation rectangle through `PageProjection`; fixtures for four rotations and CropBox origin (50, 30).
- **Facing and presentation modes:** jump shows the spread; in presentation mode the panel is unavailable.
- **Encrypted documents, permissions:** reading is always allowed; actions follow the permission bits.
- **Annotations with the Hidden or NoView flags:** listed with a "hidden" tag, not drawn.
- **Annotations with a missing or invalid rectangle:** listed; jump goes to the page top.
- **Duplicate authors with different spellings or case:** shown as separate filter values; matching is exact after trimming.
- **Dates in odd formats or the future:** unparsable shows "Date unknown" and sorts last.
- **Thousands of annotations on one page** (the engine caps at 100,000): windows of 64; the list is built progressively.
- **Page labels:** rows show physical page numbers (the same limitation as print, OQ-PRINT-5).
- **Hostile input:** annotation counts above 100,000 per page, strings of 50 MB, authors with bidi controls, reply loops: capped by the protocol, sanitised for display, loops cut.

## 7. Undo and redo

The panel has no state that is undone. Its actions run commands that are on the undo stack. Filter and sort state is per tab, kept in memory, and not restored after a restart.

## 8. Journal entries

None of its own.

## 9. Save behaviour

None of its own. The "Not saved" marker clears after a save.

## 10. Engine and protocol additions

None. It uses `CountAnnotations` (44) and `ListAnnotations` (40) from [annotation-core](annotation-core.md) section 11, and `GetAnnotation` for full text on demand when an item is opened. A full-text search message is OQ-PNL-3.

## 11. Localisation strings

| Key | en-US |
|---|---|
| `Panel_Title` | Annotations |
| `Panel_Empty` | No comments in this document. |
| `Panel_NoMatch` | Nothing matches these filters. |
| `Panel_ClearFilters` | Clear filters |
| `Panel_Filter_Type` / `_Author` / `_State` / `_Date` / `_Pages` / `_Text` | Type / Author / Review status / Date / Pages / Search comments |
| `Panel_OnlyMine` | Only mine |
| `Panel_UnsavedOnly` | Not saved only |
| `Panel_ShowReplies` | Show replies as threads |
| `Panel_ShowOther` | Show other types |
| `Panel_Follow` | Follow selection |
| `Panel_Sort_Page` / `_Created` / `_Modified` / `_Author` / `_Type` | Page order / Date created / Date modified / Author / Type |
| `Panel_Sort_Newest` / `_Oldest` | Newest first / Oldest first |
| `Panel_Count` | {0} of {1} shown |
| `Panel_Scan` | Reading comments: {0} of {1} pages |
| `Panel_Scan_Done` | Reading comments finished |
| `Panel_Scan_Partial` | Not all pages have been read. |
| `Panel_Scan_Continue` | Continue |
| `Panel_Capped` | Showing the first {0} comments in page order. |
| `Panel_PageFailed` | Couldn't read comments on page {0}. |
| `Panel_Retry` | Retry |
| `Panel_NotSaved` | Not saved |
| `Panel_Hidden` | Hidden |
| `Panel_Row_Automation` | {0}, page {1}, {2}, {3}: {4}, {5} replies, {6} |
| `Panel_Jump_Announce` | Page {0}, {1} by {2}. |
| `Panel_Date_Unknown` | Date unknown |

Telemetry: none.

## 12. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-PNL-1 | Default sort: page order (assumed) or newest first. | Design |
| OQ-PNL-2 | Should "Follow selection" be on by default for screen reader users? | Design, accessibility |
| OQ-PNL-3 | Full-text search of comments needs an engine message (`SearchAnnotations`); v1 or later. | User (product), Opus |
| OQ-PNL-4 | Export or print a comment summary (CSV or text). Not in §2.1; a common request. | User (product) |

## 13. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply where relevant.

| Id | Criterion | Layer |
|---|---|---|
| PNL-01 | Filter logic: each filter alone and in combination (AND across filters, OR within) over a 300-item fixture returns the expected ids; text filter is case, diacritic and width insensitive and limited to excerpt, author, kind. | Unit (`MikePDF.Core.Tests`) |
| PNL-02 | Sort: all five orders are stable and correct, including ties and unparsable dates; threads sort by root. | Unit |
| PNL-03 | Thread nesting: replies appear under roots; orphans become roots; "Show replies as threads" off flattens. | Unit |
| PNL-04 | Merge model: loaded items minus hidden/deleted plus pending items; undo of a delete restores the row; a page deleted by a pending page operation hides its rows. | Unit |
| PNL-05 | Rebase: after a simulated save, rows are replaced by id with selection and scroll offset preserved and no flicker (item-change events limited to changed ids). | Unit + Integration |
| PNL-06 | Loader: scans a 5,000-page fixture with 800 annotated pages issuing `CountAnnotations` windows then `ListAnnotations` only for annotated pages, nearest first; cancel keeps results; resume continues; engine restart resumes. | Integration |
| PNL-07 | The scan uses the pool engine and does not add latency to document-engine tile requests (tile request latency with the scan running is within 10% of without). | Integration (perf) |
| PNL-08 | Virtualised list with 100,000 rows: initial display < 200 ms after data arrival; scrolling 10 seconds with no frame over 50 ms; memory under a stated cap (proposed 100 MB, recorded). | Integration (perf) |
| PNL-09 | Cap: with more than 100,000 items the panel shows the first 100,000 and the notice. | Unit |
| PNL-10 | Jump: for 4 rotations and CropBox origin (50, 30), the annotation ends up inside the viewport after a jump, selected, with a history entry. | Integration |
| PNL-11 | Arrow keys move selection without scrolling when "Follow selection" is off; with it on they jump. | Unit + UI journey |
| PNL-12 | Selecting on the page selects and reveals the row. | UI journey |
| PNL-13 | Multi-select actions: delete and set state apply to all selected in one undo step; permissions and class rules respected (foreign unsupported are not deletable). | Unit + Integration |
| PNL-14 | Sanitising: bidi override and control characters are removed from author and excerpt in rows and accessible names. | Unit |
| PNL-15 | Hostile data: 100,000-per-page counts, 50 MB strings, reply loops, NaN rects load bounded without crash. | Integration + fuzz |
| PNL-16 | UIA structure: list/tree roles, group headings, names, selection pattern with multi-select; raw `IUIAutomation`. | UI journey (a11y) |
| PNL-17 | Live regions: count after filter (debounced), scan progress at 25% steps, completion, each raised once. | UI journey (a11y) |
| PNL-18 | Keyboard-only: open with `annot.panel`, filter by type, jump with Enter, reply, set state, delete, undo, no mouse. | UI journey |
| PNL-19 | Empty and error states render with the right text and Retry works. | UI journey |
| PNL-20 | Pseudo-RTL and 225% text scale: no clipped rows or overlapping columns (screenshot compare). | UI journey (visual) |
| PNL-21 | Journey 3 (§12.5) includes the panel: after reopen, the highlight and the note are listed and jump works. | UI journey |
