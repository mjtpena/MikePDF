# Feature spec: sticky notes with replies

- Status: Draft for Opus review
- Milestone: M4
- Master prompt: §2.1 (sticky notes with replies), §8.2 (replies and review state: Accepted, Rejected, Cancelled, Completed), §18 scenario 2 (sticky notes with replies in a contract) and 6 (add a comment by keyboard)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md)
- Related: [text-markup](text-markup.md) (a markup can be a thread root), [annotation-panel](annotation-panel.md) (lists threads), [free-text](free-text.md)

Conventions are in [README.md](README.md). Shared rules are in annotation-core.

## 1. User stories

1. As a contract reviewer, I click on a clause, type a comment, and a note icon appears there (§18 scenario 2).
2. As the other party, I open the same file, reply to the note, and mark it Accepted. The first reviewer sees the thread when they reopen the file.
3. As someone who gets a file with comments from Acrobat Reader, I read the threads, reply, and change their state.
4. As a screen reader user, I add a comment, read a thread and reply entirely from the keyboard (§18 scenario 6).
5. As a reviewer, I move a note, change its icon or colour, and delete a whole thread with undo.

## 2. Detailed behaviour

### 2.1 The note

A note is a PDF `/Text` annotation.

| Property | Value |
|---|---|
| Icon (`/Name`, a name object) | `Comment` (default), `Note`, `Key`, `Help`, `NewParagraph`, `Paragraph`, `Insert`. These are the ISO 32000 standard icon names. |
| Size | The `/Rect` is 20 x 20 pt. |
| Colour | Default yellow (1, 0.92, 0); the icon fill follows the annotation colour, the outline is dark grey. |
| Flags (`/F`) | 28: Print, NoZoom, NoRotate. The icon stays the same size on screen at any zoom and upright on a rotated page in readers that honour the flags. |
| `/Open` | Always false when saved. Whether a note's panel is open in the UI is view state and never saved. |
| `/Contents` | The root comment text, up to 10,000 characters. |
| `/Popup` | A linked Popup annotation, written for every note so that readers that show popups can open the comment (see 2.4). |

Icon artwork for the seven names is vector geometry in `MikePDF.Markup` (so the overlay and the appearance stream share it). The artwork is MikePDF's own; it does not copy another reader's icons.

### 2.2 Creating

1. `tool.note` active: a click or tap places the note with its icon's top-left corner at the pointer (in the displayed orientation, converted to user space through `PageProjection`), then opens the comment editor. Esc in the editor with empty text removes the note (an empty note is never kept).
2. With a note selected from the keyboard (annotation navigation) no creation happens; to create from the keyboard use `markup.addNoteToSelection`/`annot.newNote` ("Add note at the current position"): with the page surface focused it places a note at the top-left of the visible part of the current page, or at the end of the current text selection if there is one, and opens the editor. This is the keyboard-only route (§18 scenario 6).
3. After creation the tool returns to `Select` unless `markup.keepToolActive`.

The rotated-page placement rule: the icon appears upright at the clicked point in the displayed page. How readers position a NoRotate icon relative to `/Rect` on a rotated page is not obvious from the standard, so there is a fixture for each `/Rotate` value that compares MikePDF, PDFium, pdf.js and (manually) Acrobat (OQ-SN-3).

### 2.3 Replies and review state

**Structure** (ISO 32000, markup annotations): a reply is another `/Text` annotation with `/IRT` pointing (as an **indirect reference**) to the annotation it answers, and `/RT /R`. A review-state change is a `/Text` annotation with `/IRT` to its target, `/StateModel /Review` and `/State` one of `None`, `Accepted`, `Rejected`, `Cancelled`, `Completed`. All of `/IRT`, `/Popup` and `/Parent` are references, never strings or direct dictionaries (S4 F2).

- A thread has a root (a note or any text markup, shape or free text with a comment) and replies of unlimited depth, **cut at depth 32** when read.
- **Reply:** `annot.reply` on a selected thread member opens a reply box. Ctrl+Enter sends. A reply gets its own author, dates and `/NM`.
- **Review state:** a drop-down on each thread member sets its state. Each change **adds a state annotation** (history is kept, as other readers do). The current state of a member is the newest state annotation by `/M` with that member as `/IRT`. `None` is a state too.
- **Appearance of replies and state annotations.** They are not meant to show as extra icons. OPEN QUESTION OQ-SN-2: the exact flags and appearance. The proposal is `/Rect` equal to the parent's rect, `/F 28`, and an appearance that is an empty form XObject (draws nothing). Alternatives are the Hidden flag (also hides it from printing and from some readers' comment lists) or a full icon (stacked icons). The choice must be checked against Acrobat's behaviour; until then reply drawing is "not measured".
- **Editing and deleting.** A comment can be edited if it is owned, or if it is foreign and its author equals the current author name; otherwise edit is disabled with "Comments by other people can't be edited." Anyone can reply to or set the state of any member. **Delete:** a member with replies asks "Delete this comment and its {n} replies?"; deleting a root deletes the whole thread; both are one undo step. A deleted reply leaves its own replies attached to the grandparent.
- **Orphans:** a loaded reply whose `/IRT` target is missing, or a cycle in `/IRT`, becomes a root, flagged "reply to a missing comment" in the panel.
- **Limits:** 1,000 replies per thread, 100,000 annotations per page, contents up to 10,000 characters in the UI. Above the thread limit the reply button is disabled with a reason.

### 2.4 Popup annotations

For every note (and every markup with a comment) MikePDF writes a `/Popup` annotation: `/Subtype /Popup`, `/Parent` (the note), `/Open false`, `/Rect` 180 x 100 pt placed to the right of the icon and moved so that it stays inside the page's displayed box. The note has `/Popup` referring to it. Popups are never shown by MikePDF's own UI (the comment pane is the UI); they exist for other readers. Popups of loaded files are ignored by the UI and preserved on save.

### 2.5 The comment UI

Visuals are in the design spec. Behaviour:

- Clicking or tapping a note, or pressing Enter on it, opens its thread in the right pane (the "Comments" pane of §10.1) and scrolls the thread into view. A small hover card shows author, date and the first 200 characters of the comment.
- The thread shows each member's author, local date and time, text, state, and Reply, Edit, Delete, State actions.
- The editor is a multi-line text box (IME, spell check and text scaling work because it is a real XAML text box). Enter inserts a newline; **Ctrl+Enter** commits; **Esc** commits non-empty text and closes (an empty new comment is discarded). Text longer than the limit is refused with a count.
- Typing in the editor is not journaled per keystroke; the journal gets one entry when the edit is committed. If the app crashes mid-edit, the uncommitted text in the box is lost. OPEN QUESTION OQ-SN-4: periodically journal a draft (every 5 s)? It would protect long comments at the cost of more journal traffic.

### 2.6 Moving and styling

Drag a note to move it (one undo step; clamps inside the page). Arrow keys nudge. The icon and colour change in the properties pane; the appearance is regenerated. Notes can't be resized.

## 3. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Note tool | none | `tool.note` |
| New note at the current position | none | `annot.newNote` |
| Add note to the text selection | none | `markup.addNoteToSelection` |
| Reply | none (Enter in an open thread focuses the reply box) | `annot.reply` |
| Edit comment | F2 | `annot.edit` |
| Send / commit | Ctrl+Enter | (editor) |
| Next / previous annotation | Tab / Shift+Tab | `annot.next`, `annot.previous` |

Pen: tap with the pen to place a note; the pen in pen mode inks instead. Touch: tap to place, tap an icon to open, long-press for the menu. Hover with pen or mouse shows the hover card.

## 4. Accessibility

- A note is a UIA element with localized control type "note"; name "note by {author}, {first 80 characters}"; `HelpText` "{date}, page {n}, {state}, {k} replies". Invoke opens the thread.
- The thread pane is a UIA `Tree` or nested `List` (depth announced as level), with each member a `ListItem` named "{author}, {date}: {text}" followed by "{state}" when set. Reply and State are buttons with names that include the author ("Reply to Alex").
- The editor announces its purpose ("New comment" or "Reply to Alex") and the character count remaining in a polite region when within 200 of the limit.
- Announcements: "Note added on page 3", "Reply added", "Marked Accepted", "Thread deleted, 3 comments", "Undo: note removed".
- State is shown by text ("Accepted") and an icon, never by colour alone. Contrast 4.5:1 for text and 3:1 for icons in all themes.
- Narrator journey (§18 scenario 6): navigate to a page, `annot.newNote`, type, Ctrl+Enter, Tab to the note, Enter, read the thread, reply, all with Narrator speaking each step.

## 5. Error states

| Situation | Behaviour |
|---|---|
| Comment too long | The text box refuses extra characters and says how many are allowed. |
| Thread limit reached | Reply disabled with reason. |
| Permission bit 6 clear | All note tools disabled; existing threads are readable. |
| `ListAnnotations` fails for a page | Thread pane shows "Couldn't read comments on page {n}" and Retry. |
| Save fails | Comments stay in the journal and the model ([save-pipeline](save-pipeline.md)). |
| Note placement outside the page | Clamped inside. |

## 6. Edge cases

- **Rotated pages / CropBox:** section 2.2; fixtures for the four rotations and CropBox origin (50, 30); popup placement uses the displayed box.
- **Many notes close together:** icons overlap; Tab order is by thread then position; the panel is the reliable list. Hit-testing prefers the topmost.
- **Notes under text selection:** the hit area is the icon only; text selection by drag still works.
- **RTL and mixed-direction comments, emoji, combining marks:** stored as UTF-16BE text strings; length counted in Unicode code points; the editor and thread pane use XAML text rendering; the appearance contains no text.
- **Control characters and NUL in `/Contents` of loaded files:** stripped for display; saved as loaded if untouched.
- **Encrypted documents and signed documents:** annotation-core 3.8.
- **Huge threads in loaded files** (10,000 replies, deep chains): the reader caps at 1,000 replies per thread and depth 32; the rest are preserved in the file and shown as "{n} more not shown".
- **Same note read from several programs with different date formats or time zones:** dates are parsed leniently (`D:` forms with or without offset); unparsable dates display as "date unknown" and sort last.
- **Hostile input:** `/IRT` and `/Popup` loops, `/Parent` pointing at a non-popup, a Popup claiming 10 million points of size, strings with 50 MB: all bounded by the protocol limits and the 32-depth rule.

## 7. Undo and redo

Create note (with its first comment), edit comment, move, change icon or colour, add reply, set state, delete (member or thread): each one command; delete of a thread is a composite. Undo of "add reply" removes the reply annotation; undo of "set state" removes the state annotation (so history stays honest). Commit of an edit session is one command. Rules in annotation-core section 8.

## 8. Journal entries

`AddAnnotation` (`kind: "Note"`, `payload: { "icon": "Comment" }`, `contents`), `UpdateAnnotation` (`contents`, `icon`, `colour`), `MoveAnnotation`, `AddReply` (`id`, `parent`, `contents`, `author`, `created`), `SetReviewState` (`id`, `parent`, `state`, `author`, `created`), `DeleteAnnotation` (with `thread: true` and the list of ids removed so replay and undo agree). Comment text is document content added by the user and is described in the privacy notes.

## 9. Save behaviour

Per note: a `/Text` annotation (indirect, `/NM`, `/P`, `/Subtype /Text`, `/Name` as a name object, `/Rect`, `/C`, `/CA`, `/F 28`, `/Contents`, `/T`, `/M`, `/CreationDate`, `/Subj`, `/Open false`, `/Popup`, `/AP`) and its Popup (`/Parent`). Per reply: a `/Text` annotation with `/IRT` (reference), `/RT /R` and its own `/Popup` only if OQ-SN-2 requires one. Per state change: `/StateModel /Review`, `/State`, `/IRT`. Everything is written in one incremental append by the writer host. All references resolve in the same append. `/Annots` includes the popups.

## 10. Engine and protocol additions

None beyond annotation-core: threads are built from `ListAnnotations.parents`, `GetAnnotation.parentIndex`, `replyType`, `stateModel` and `state`.

## 11. Localisation strings

| Key | en-US |
|---|---|
| `Note_Tool` | Note |
| `Note_New` | New note |
| `Note_Icon_Comment` / `_Note` / `_Key` / `_Help` / `_NewParagraph` / `_Paragraph` / `_Insert` | Comment / Note / Key / Help / New paragraph / Paragraph / Insert |
| `Note_Editor_New` | New comment |
| `Note_Editor_Reply` | Reply to {0} |
| `Note_Editor_Remaining` | {0} characters left |
| `Note_Reply` | Reply |
| `Note_Edit` | Edit |
| `Note_Delete` | Delete |
| `Note_Delete_Thread_Confirm_One` | Delete this comment and its reply? |
| `Note_Delete_Thread_Confirm_Other` | Delete this comment and its {0} replies? |
| `Note_State_None` / `_Accepted` / `_Rejected` / `_Cancelled` / `_Completed` | None / Accepted / Rejected / Cancelled / Completed |
| `Note_State_Label` | Review status |
| `Note_Cannot_Edit_Others` | Comments by other people can't be edited. |
| `Note_Orphan` | Reply to a missing comment |
| `Note_More_Hidden_One` / `_Other` | 1 more reply isn't shown / {0} more replies aren't shown |
| `Note_Thread_Limit` | This thread has reached its limit of {0} replies. |
| `Note_Date_Unknown` | Date unknown |
| `Note_Automation_Name` | note by {0}, {1} |
| `Note_Help` | {0}, page {1}, {2}, {3} replies |
| `Note_Added_Announce` | Note added on page {0} |
| `Note_Reply_Announce` | Reply added |
| `Note_State_Announce` | Marked {0} |
| `Note_ThreadDeleted_Announce` | Thread deleted, {0} comments |

Telemetry: none.

## 12. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-SN-1 | Default icon and artwork for the seven names. | Design |
| OQ-SN-2 | Flags and appearance of reply and state annotations (2.3). | Opus after the Acrobat pass |
| OQ-SN-3 | Position of a NoRotate icon on rotated pages across readers (2.2). | Opus after fixtures run |
| OQ-SN-4 | Journal drafts of an unfinished comment. | Opus |
| OQ-SN-5 | Should state changes by the same author replace instead of add (less file growth)? | User |

## 13. Acceptance criteria

Shared AC-01 to AC-25 ([annotation-core](annotation-core.md)) apply with these fixtures.

| Id | Criterion | Layer |
|---|---|---|
| SN-01 | Thread builder: from `parents` arrays it builds trees; handles orphans, cycles, depth > 32 (cut), > 1,000 replies (cut), duplicate ids. | Unit (`MikePDF.Core.Tests`) |
| SN-02 | Current-state resolution: newest state annotation by `/M` wins; ties break by `/Annots` order; `None` resets. | Unit |
| SN-03 | Editability matrix: owned editable; foreign editable only when the author matches; reply and state always allowed; delete confirm text depends on reply count. | Unit |
| SN-04 | Creation geometry: icon top-left at the click for `/Rotate` 0, 90, 180, 270 and CropBox (50, 30); the saved `/Rect` is 20 x 20 and inside the page. | Unit |
| SN-05 | Popup rect: 180 x 100, to the right of the icon, shifted inside the page box when near an edge, for all four rotations. | Unit |
| SN-06 | Comment text normalisation and encoding: NFC, control stripping, PDFDocEncoding vs UTF-16BE choice, emoji and combining marks round-trip; 10,000-code-point limit counts code points. | Unit |
| SN-07 | Saved structure: `/Name` is a name object; `/IRT`, `/Popup`, `/Parent` are indirect references that resolve; `/RT /R`; `/StateModel /Review` and `/State` names valid; `/F 28`; indirect object; no PDFium private keys; `qpdf --check` passes on classic and xref-stream bases. | Unit (PdfWriter) + Fidelity |
| SN-08 | Reopen in fresh PDFium: `FPDFAnnot_GetLinkedAnnot` resolves `/Popup` and `/IRT` (the S4 failures F2 do not recur); thread equals the model. | Fidelity harness |
| SN-09 | Icon appearance: each of the seven icons is visible in PDFium and pdf.js without a rendered-first save (the S4 cold-save failure); SSIM >= 0.98 vs golden at the icon rect. | Fidelity harness |
| SN-10 | Acrobat Reader and Edge: icons visible at the right place on all four rotations; Acrobat shows the thread (author, text, replies, state); Edge/PDFium readers show the icon. Recorded per cell as pass, fail or **not measured**. | Fidelity harness (manual-gated, R-15) |
| SN-11 | Reply and state annotations do not produce visible stray icons in PDFium, pdf.js (and Acrobat when measured), under the chosen OQ-SN-2 option. | Fidelity harness |
| SN-12 | Foreign threads from fixtures made by Acrobat or PDFium load with correct structure; a reply added by MikePDF keeps the foreign appearance streams byte-identical. | Integration |
| SN-13 | Delete thread with 3 replies: one confirm, one undo step; undo restores all four with the same ids and links. | Unit + Integration |
| SN-14 | Editor commit rules: Enter newline, Ctrl+Enter commit, Esc commits non-empty and discards empty; an empty new note leaves no annotation or journal entry. | UI journey |
| SN-15 | Crash with committed notes and replies restores them all from the journal; an uncommitted draft is lost (or restored, per OQ-SN-4). | Integration |
| SN-16 | Keyboard-only (§18 scenario 6 steps): create note, type, commit, navigate to it, open, read, reply, set state, delete, undo. | UI journey |
| SN-17 | UIA: note name, help text, patterns; thread pane tree levels and item names; announcements match section 11 strings, once each. | UI journey (a11y) |
| SN-18 | Narrator script run (recorded by hand per milestone) for the thread reading order. | Manual record (§12.7) |
| SN-19 | Move and nudge clamp inside the page; icon never smaller than 20 x 20 pt in the saved file. | Unit |
| SN-20 | Hostile threads (loops, 10,000 replies, 50 MB string, popup with huge rect) load bounded and without crash. | Integration + fuzz |
| SN-21 | Permission bit 6 clear disables note tools; reading threads still works. | Unit |
| SN-22 | Facing and presentation modes: notes draw and hit-test on the right page of a spread; hidden in presentation mode. | Integration |
| SN-23 | Pseudo-RTL and 225% text scale: the thread pane and editor reflow with no clipped buttons. | UI journey (visual) |
