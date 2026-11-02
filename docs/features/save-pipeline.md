# Feature spec: the save pipeline, the edit journal and crash recovery

- Status: Draft for Opus review. **A security-review gate (§13.3) applies**: save pipeline, writer host, IPC changes. Depends on a spike (S10) for file replacement, section 3.6
- Milestone: M4 (no write feature ships before this: §0.2 rule 5)
- Master prompt: §7.4, §7.5, §0.2 rule 5 ("never write user data destructively"), §12.4, §12.5 journeys 3 to 5 and 8, §18 scenarios 1, 3, 5; ADR-0008; ADR-0004 amendments A1 and A2; risks R-31, R-37 to R-40
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: `MikePDF.PdfWriter` (strict parser, `IncrementalUpdate`, `StructureValidator`), `MikePDF.WriterHost`, `MikePDF.Broker` (`WriterHostClient`), engine `SaveDocument`
- Used by: every M4 spec. Section numbers 3.4 (journal) and 3.7 (undo stack) are referenced by other specs

Conventions are in [README.md](README.md).

## 1. The decisions this spec builds on (ADR-0008, accepted)

1. `MikePDF.PdfWriter` owns annotation dictionaries and appearance streams, and runs only in the sandboxed `MikePDF.WriterHost`. The broker never parses PDF bytes.
2. Save stages: (1) no changes, do nothing; (2) form or page changes: PDFium `FPDF_SaveAsCopy(FPDF_INCREMENTAL)` into temporary file A; (3) the writer host normalises PDFium's appended tail and strips `/PDFIUM_HasGeneratedAP`; (4) annotation changes: the writer host appends its own incremental update; (5) a fresh engine reopens the result and checks it against the command model, and the host's structural validator walks the xref chain; (6) the broker replaces the original atomically.
3. Full rewrite (`FPDF_NO_INCREMENTAL`) only for Save As "optimised", flatten and redaction; never for a signed document without an explicit warning.
4. The writer's parser is strict and fails closed; a lenient parse never upgrades a verdict.
5. No write feature ships without the journal; the journal entry for a save is cleared only after the replace succeeds.

Known costs from the spikes: PDFium's incremental save is never "just the new part" (S4 surprise 4); it is invalid for xref-stream sources (7 of 9), so stage 3 is mandatory; `MikePDF.PdfWriter` refuses files over 1 GiB (32-bit offsets, R-38) and strict mode refuses some files PDFium opens (R-39).

## 2. User stories

1. As an accountant, I press Ctrl+S after highlighting and commenting, the file saves in a moment, and a colleague opening it in Acrobat Reader sees every comment (§18 scenario 1).
2. As anyone, I never lose the original file because a save failed halfway.
3. As a user whose app crashed with unsaved work, I'm offered my changes back when I reopen the file (journey 8).
4. As someone sending a finished form, I save a flattened copy that can't be edited, and my working file stays open and editable.
5. As a user of a signed document, I'm warned before I do anything that would break the signature.
6. As a user whose file is in OneDrive or on a share, saving works or fails with a clear message and a Save As option.

## 3. Detailed behaviour

### 3.1 Overview

```text
 edit model (broker)                          journal (private dir)
        |  Save                                     ^ appended per command, flushed
        v
 0 preflight --> 1 nothing to do? --> stop
        |
        v
 2 engine: PDFium incremental save of form/page changes --> A   (skipped if none)
        |
 3 writer host: NormalizeTail(original, A) --> B                (skipped if 2 skipped)
        |
 4 writer host: AppendAnnotations(B or original, edit blob) --> C   (skipped if no annotation edits)
        |
 5 validate C: structure (writer host) + fresh engine vs model + prefix check
        |
 6 replace original with C (atomic) --> rebase engines, clear journal, mark clean
```

Every file in the diagram is a temporary file in the same directory as the target, created by the broker and handed to a sandboxed process as a handle (processes never get paths). The original is untouched until stage 6.

### 3.2 Commands

| Command | Default | Behaviour |
|---|---|---|
| **Save** (`file.save`) | Ctrl+S | Stages 0 to 6 on the open file. Disabled with nothing to save (not dirty). If the file can't be replaced (read-only, no permission, locked), falls to Save As. |
| **Save As** (`file.saveAs`) | Ctrl+Shift+S | File picker. Runs stages 0 to 5 into a temporary file in the target folder, then moves it to the chosen name (replacing an existing file the picker already confirmed). The **tab switches to the new file**; the original is left as it was; the journal is re-keyed to the new path. Option "Rewrite the whole file" (off by default): smaller output, no edit history, **invalidates signatures** (second pass in 3.3). |
| **Save a flattened copy** (`file.saveFlattenedCopy`) | none | See 3.3. The tab and the working file are unchanged. |
| **Revert** (`file.revert`) | none | Confirm, then reopen from disk and discard all edits and the journal. |
| Close / exit with changes | | Save, Don't save, Cancel ([tabs-windows-session](tabs-windows-session.md)). "Don't save" discards the journal. |

**Dirty state.** A document is dirty when its edit model holds commands after the clean point (the last save, or the open state). Dirty shows as a dot on the tab, in the window title, and in the command state. Undoing back to the clean point makes it clean again.

**While saving**, edits are blocked (the document is read-only; scrolling and zooming continue). A save over 300 ms shows a progress strip with the stage ("Saving...", "Checking the saved file...") and Cancel, until stage 6 starts; after that there is no cancel. Typical saves take a second or two; times are **not measured**.

**Policy** `SaveMode=1` (AlwaysSaveAsCopy): Save runs Save As with the suggested name `{name} (edited).pdf`, and never writes the original ([settings-policies](settings-policies.md)).

### 3.3 The stages in detail

**Stage 0, preflight.** Refuse, with a reason and Save As where it helps, when: the document is encrypted (3.8); larger than 1 GiB; `MikePDF.PdfWriter`'s strict parser refuses the structure (the writer host reports `Damaged`/`Unsupported`); permissions forbid the edits made; free space is below the original size plus 20% (full rewrite: twice the size); the directory can't hold a temporary file. If the document is signed, show the signed-document notice (annotation-core 3.8), and for operations that break signatures (flatten, full rewrite, page operations) require explicit confirmation. Take an **immutable snapshot** of the edit model (its sequence number is the new clean point). Write a `SaveBegin` journal record.

**Stage 1.** No commands since the clean point: do nothing (an incremental save of an unmodified document is not a no-op: +24% to +270% in S4). Page and form commands only, annotation commands only, or both, select the stages below.

**Stage 2** (form or page changes). The broker checks that the engine document matches the model's form and page state (comparing `GetFormFields` values and page count with the model), replaying commands into a fresh engine if not. It then sends `SaveDocument(mode=incremental, outputHandle)` to the engine. The engine first **restores every annotation flag it changed for display** (annotation-core 4a), then saves. Page edits that import pages ([page-organiser](page-organiser.md)) run before this in the same engine.

**Stage 3** (only if stage 2 ran). `NormalizeTail(original, A) -> B` in the writer host: parse the original strictly; verify that A starts with the original's exact bytes; rewrite PDFium's appended cross-reference section in the same form as the original's newest section (a stream after a stream, a table after a table; its own entry for a stream); strip `/PDFIUM_HasGeneratedAP`; restore author-made check box and radio state streams per [forms](forms.md) 10.2; set the trailer `/ID` second element. A repair that patches only the original bytes is forbidden (S4). Duplicate keys or any ambiguity fail the stage (R-31).

**Stage 4** (annotation changes). `AppendAnnotations(base, editBlob) -> C` in the writer host: for each edit, resolve the page by its **ordinal in the base** (the broker translates page identities to ordinals using the current page order); for edits to loaded annotations, **verify the expectation** sent with the edit (subtype and rectangle within 0.01 pt); a mismatch fails the whole stage rather than editing the wrong object. Generate dictionaries and appearance streams from model values; add new objects, replace changed ones (`IncrementalUpdate.Replace`), write the page object and its `/Annots` array, the xref section and trailer (`/Prev`, `/Size`, `/Root`, `/Info`, `/ID` first element kept, second new). The original (or B) bytes stay an exact prefix.

**Stage 5, validation.** All of:
1. Writer host `ValidateStructure(C)` walks the whole xref chain with the strict parser: no issues.
2. **Prefix check:** the first `len(base)` bytes of C equal the base (compared by the broker as a hash over the range; it reads bytes but does not parse them).
3. A **fresh engine** (a new process, not the working one) opens C and checks against the model: page count; per page, the count of non-link, non-popup, non-widget annotations equals (loaded minus deleted plus added); for owned annotations, kind, rectangle (0.01 pt), colour, contents and author; for loaded foreign ones, subtype and rectangle unchanged unless moved; form values equal the model's; no `/PDFIUM_HasGeneratedAP` anywhere.
4. Any failure aborts the save; the original is untouched; the user sees "MikePDF couldn't save safely. Your changes are still here." with Save As (to a different location), Try again, and Details (a short, non-sensitive reason code).

**Stage 6, replace** (3.6). Then **rebase**: reopen the working engines on the new file, clear the pending model, move the clean point, delete the journal (after the replace has succeeded, never before), update recents and the position store, and swap the display without a gap (annotation-core AC-15).

**Save a flattened copy.** Run stages 0 to 4 into C (so owned annotations are in the file), then: a fresh engine opens C; `SaveDocument(mode=flattened, flags)` flattens each page (`FPDFPage_Flatten`; options: form fields, comments and markup) and saves a full copy D; validate D (page count equal; no widget annotations if fields were flattened; no flattened-type annotations left; text extractable; renders within SSIM 0.98 of C's render without field highlight); write D to the chosen path via a temporary file and move. The signed-document warning applies. The working document, its tab, its journal and the original file are untouched.

**Full rewrite** (Save As option): stages 0 to 4 into C, then `SaveDocument(mode=full)` on a fresh engine over C gives D, validated as above. Used only on request.

### 3.4 The edit journal

**Location.** `LocalState\private\journals\<docKey>.jsonl`, with `<docKey>.blobs\` beside it for content-addressed binary payloads (image stamps, large ink strokes). §7.4 names `LocalState\journals`; **that is overridden by ADR-0004 amendment A2** because the sandboxed engine holds the package's own capability and can read and write package data outside `LocalState\private`. The journal holds user content (comment text, form values) and must not be readable or alterable by the engine. This is OPEN QUESTION OQ-SP-7 only for Opus to confirm the deviation from §7.4's path.

**Key.** `docKey` is the first 32 hex characters of SHA-256 over the lower-cased, normalised full path (UTF-16 little-endian). A moved or renamed file loses its journal association (the journal is still listed under recovery with its recorded path).

**Format.** UTF-8 JSON Lines, one record per line, LF-terminated.

```json
{"v":1,"type":"header","docPath":"C:\\x\\a.pdf","fileId":"...","size":123456,"mtime":"...","fingerprint":"<sha256 of first 64 KiB + last 64 KiB + size>","app":"1.0.0.0","pages":["<page identity>", "..."]}
{"v":1,"seq":1,"t":"2026-11-02T10:30:00.123Z","cmd":"AddAnnotation", ... payload ..., "crc":"1a2b3c4d"}
{"v":1,"seq":2,"t":"...","cmd":"Undo","target":1,"crc":"..."}
{"v":1,"seq":3,"t":"...","cmd":"SaveBegin","outputSha256":"...","outputLength":123999,"crc":"..."}
```

`crc` is CRC-32C of the record text without the `crc` field. Control records: `Undo` (target seq), `Redo`, `SaveBegin`, `SaveCommitted`, `Discard`. Command records are defined by each feature spec (`AddAnnotation`, `UpdateAnnotation`, `MoveAnnotation`, `ResizeAnnotation`, `DeleteAnnotation`, `AddReply`, `SetReviewState`, `AddInkStroke`, `EraseInk`, `SetFieldValue`, `ResetField`, `ClearForm`, `InsertPages`, `InsertBlankPage`, `DeletePages`, `MovePages`, `RotatePages`).

**Writing.** Each command is appended and flushed (`FILE_FLAG_WRITE_THROUGH` plus `FlushFileBuffers`) **before the command is acknowledged** (§7.4). Ink strokes use a queued flush within 50 ms (ink-pen OQ-INK-7). The file is opened exclusively by the broker. Appends are single `WriteFile` calls so a crash leaves at most one torn line at the end.

**Reading.** Reading stops at the first record that fails JSON parsing, CRC, or sequence continuity, and reports how many records were valid and that the rest was ignored. A torn last line is normal and silent. A bad record in the middle is reported ("Some of your changes couldn't be recovered") and recovery offers the valid prefix.

**Limits (proposed).** 64 MiB per document journal, 512 MiB across all journals. Warning chip at 90%. At the limit, commands still apply but the chip says "Changes can't be backed up right now. Save to keep them safe." (degraded, not blocked). OPEN QUESTION OQ-SP-1: strict (refuse the command, which is what "flushed before acknowledged" literally requires) versus degraded.

**Privacy.** The journal stores only what the user added: comment and note text, typed form values (not password fields), ink strokes, text box text, stamp names and image bytes, page identities, and the paths of files from which pages were inserted. It stores **no page content** and no text of the document except a capped excerpt of marked text (200 characters) for the recovery summary. This list goes into `docs/user/privacy.md`. Files are in the private directory, removed on uninstall, deleted on successful save and on Discard, and cleared by Settings > Clear recovery data. OPEN QUESTION OQ-SP-2: DPAPI encryption of journal contents.

**Retention.** No automatic deletion: silently deleting a user's unsaved work disagrees with §0.2 rule 5. OPEN QUESTION OQ-SP-6: show journals untouched for 90 days in recovery with a suggestion to delete.

### 3.5 Crash recovery

A journal that exists when a document opens means unsaved changes were never saved or discarded (a normal close either saves or discards, and both remove the journal).

1. **When:** after opening a document (also a lazy tab on first activation), and on the empty window, which lists "Unsaved changes found for N documents" with an Open button each.
2. **Prompt:** "MikePDF closed before {name} was saved. Restore the unsaved changes?" with a summary ("8 comments, 3 form fields, 2 page changes. Last change 10:31") from the journal. Buttons: **Restore**, **Discard**, **Not now** (keep for later).
3. **Idempotency.** Before restoring, compare the journal header and the file. If the file's hash equals a `SaveBegin.outputSha256` record, the save completed but the journal wasn't cleared (crash between replace and cleanup): clear the journal silently and say nothing. If the file matches the header fingerprint, replay. If it differs from both ("The file changed after you made these edits"), warn and let the user choose Restore anyway (entries that no longer fit are skipped and counted), Discard, or Not now.
4. **Replay** applies records in order, reconstructing the model and the undo and redo stacks (an `Undo` record pops, a `Redo` pushes). Entries that can't be applied (a page identity that no longer exists, a form field not found, a missing source file for inserted pages) are skipped and counted; a summary ("11 of 12 changes restored") is shown. The document is then **dirty**; nothing is saved by recovery.
5. **Engine crash (not UI crash)** needs no prompt: the supervisor restarts the engine and the model is replayed into it (annotation-core 3.6).
6. **Hard kill of the whole app** during editing, during a save at any stage, and between the replace and the cleanup, are all tested (section 14).

### 3.6 Handles and the replace step (needs spike S10)

ADR-0005 decision 6 has the broker open the document read-only **deny-write**, and the engine maps it. §7.5 says to replace with `ReplaceFileW`. These collide: `ReplaceFileW` cannot replace a file that anyone holds open without `FILE_SHARE_DELETE`, and a file with a mapped view may refuse deletion. Whether the existing handle flags (`FileShare.Read`) already block the save has **not been tested**.

Proposal, to be proven by **spike S10** before implementation:

1. Open documents with `FILE_SHARE_READ | FILE_SHARE_DELETE` (others can't write; they can rename or replace the path). This also stops MikePDF blocking Explorer renames, OneDrive updates and other programs' atomic saves.
2. At stage 6: re-check the original's fingerprint through a verification handle (also share-delete), then replace with `ReplaceFileW` (or a handle-based rename with replace semantics) **while the engines still map the old file**, if S10 shows that works; otherwise close the engine documents first (unmap and close), replace, then reopen (the rebase already does this).
3. If another program changed the original since the model's baseline, ask: Overwrite / Save As / Cancel.
4. On `ERROR_UNABLE_TO_MOVE_REPLACEMENT*`, sharing violation or access denied: retry up to 5 times at 200 ms; then fail with Save As offered. Fall back to `MoveFileExW` with replace-existing only for file systems that return `ERROR_NOT_SUPPORTED` for `ReplaceFileW` (some WebDAV-backed paths); on NTFS and SMB it is atomic.
5. Preserve the security descriptor, attributes and alternate data streams, notably the Mark-of-the-Web (`Zone.Identifier`). Whether `ReplaceFileW` keeps them is part of S10; if not, copy them explicitly before the replace.
6. The temporary file is `.{name}.mikepdf-{8 random hex}.tmp` in the target's directory, hidden, created exclusive. Temporary files are removed on failure, and a stale one is removed at startup **only if** a journal `SaveBegin` record names it (MikePDF never deletes files it did not record). If the directory can't hold the file (read-only, no permission), Save becomes Save As. §7.5 step 1's "or the app's temp folder" applies only to Save As to a different volume, where the final move is a copy and therefore not atomic; it is not used for Save.

S10 must report, on x64 and ARM64: sharing combinations versus `ReplaceFileW` and handle-based rename, with and without mapped views and open engine handles; ACL and ADS preservation; behaviour on a OneDrive Files On-Demand folder (does the temporary file sync, does the replace create a conflict copy), a SharePoint-synced folder, an SMB share and a mapped drive.

### 3.7 The undo stack and the clean point

- One linear stack per document for annotation, form and page commands. It lives in `MikePDF.Core.Editing` in the broker, so it survives engine restarts and tab suspension.
- A save moves the clean point; the stack is kept. After a save, undoing a command that was saved yields an inverse change relative to the saved file (annotation-core section 8), recorded in a new journal opened against the saved file's fingerprint.
- The undo stack is **not** persisted across crashes except through the journal's `Undo` and `Redo` records, which restore the stack of the current unsaved session. After a successful save the journal is gone and so is the persisted stack; the in-memory stack remains until the tab closes.
- Memory: unbounded within a session (§7.3). The size of ink and image payloads counts toward an in-memory budget (proposed 256 MB of command payload); beyond it the oldest stroke data is spilled to the blob folder, never dropped.

### 3.8 Special documents

| Condition | Behaviour |
|---|---|
| **Signed** | Incremental append only; notice on first edit; confirmation before full rewrite, flatten or page operations ("This will make the signature invalid" or "will be reported as changed after signing"). MikePDF does not read DocMDP in M4 (OQ-AC-2). |
| **Encrypted** | `PdfWriter` can't write encrypted objects. **Save is blocked** with "This document is password protected. MikePDF can't save changes into it yet. Save a copy without the password, or discard your changes." Save As to a new file is offered, with the words "The copy won't be password protected." The original is never replaced by an unprotected version. What PDFium does to encryption in a full save is **not measured** (OQ-AC-6). |
| **Over 1 GiB** | Edits refused up front (R-38). |
| **Read-only attribute, no write permission, directory not writable** | Save becomes Save As, with the reason. |
| **File open elsewhere or locked** | Retry rule in 3.6; then error with Save As. |
| **Cloud placeholder** | Hydrated when opened. Saving to a synced folder works as a normal replace; the sync client may briefly see the temporary file. Manual check on OneDrive and SharePoint (§12.7). |
| **Network paths** | As cloud. Lost connection mid-save: the temporary file may remain and is cleaned on the next start via the `SaveBegin` record. |
| **Damaged or repaired by PDFium** | Edits allowed only if the strict parser accepts the file (R-39); else tools are disabled with the structure message. |
| **Another process changed the file while the tab was open** | A banner ("{name} changed on disk. Reload or keep your version.") appears when a change is noticed; saving over it asks first (3.6 item 3). |

### 3.9 Timeouts and limits

Each stage has a deadline: engine save and validation `60 s + 30 s per 100 MB`; writer host stages the same; the replace 10 s. A missed deadline kills the process, fails the save and leaves the original untouched (watchdog rules of ADR-0004). Edit blob up to 256 MiB; per-annotation limits are in the feature specs. At most one save runs per document; a second request waits or is ignored.

## 4. Keyboard, pen and touch

Ctrl+S, Ctrl+Shift+S, `file.saveFlattenedCopy` (palette), `file.revert` (palette). The dialogs (recovery, close, signed-document, encrypted) are standard content dialogs: default button is the safe one (Save, Restore), Esc is Cancel or Not now. Touch and pen use the same dialogs.

## 5. Accessibility

- Save progress is a `ProgressBar` with name "Saving {name}" and a polite live region announcing the stage once each and "Saved" or the failure at the end. Cancel is a button.
- Dirty state is exposed in the tab item and window title names ("unsaved changes") and in command state, not by a dot alone.
- Recovery and close dialogs have clear titles, the summary as text, a default button, and focus placed on it. The recovery summary is read in full by Narrator.
- Failure messages state what happened, that the changes are safe, and what to do next, in plain words, without codes in the primary text (codes in Details).
- "Changes can't be backed up right now" is a persistent status with a role of status, not a transient toast.
- Dialogs honour high contrast and 225% text scaling.

## 6. Error states

| Situation | Message and effect |
|---|---|
| Any stage fails or times out | "MikePDF couldn't save safely. Your changes are still here." Original untouched; temporary files removed; Save As offered. |
| Validation mismatch | Same message; reason code in Details; the fresh engine's finding is logged without document data. |
| Disk full | "There isn't enough space to save. Free some space or save to another drive." |
| Replace fails after retries | "{name} is in use by another program. Save a copy instead." |
| File changed externally | Overwrite / Save As / Cancel. |
| Writer host crash | Restarted by the broker (watchdog); the save fails once; retry works. |
| Journal write fails | Chip; editing continues (OQ-SP-1). |
| Recovery can't read the journal | "Your saved changes for {name} couldn't be read." with Discard; nothing else touched. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** all coordinates are in user space; the edit blob carries user-space values; no conversion in the writer.
- **Page operations plus annotations in one save:** ordinals are resolved after page operations; annotations on deleted pages are dropped; annotations keyed to moved pages follow them ([page-organiser](page-organiser.md)).
- **Saving while the engine is restarting:** the save waits for the supervisor, then replays.
- **Saving twice quickly:** the second waits; nothing is written if there are no new commands.
- **Very large annotation counts:** the edit blob and `/Annots` arrays are built streaming; the page object is rewritten once per page, not once per annotation.
- **A page with 100,000 existing annotations:** `/Annots` is rewritten as an array of references to existing and new objects without parsing each annotation.
- **Unicode paths, long paths (over 260), trailing dots:** the temporary name is built from the target's directory with the `\\?\` form when needed.
- **Files in `WindowsApps` or other protected locations:** no write permission, so Save As.
- **Hostile input:** a file whose xref has loops, duplicate keys, hybrid sections or a 4 GB claimed length fails strict parsing in the writer host and the save is refused with the structure message (R-31); a file crafted so PDFium and the writer disagree about an object is the parser-differential case, tracked as a High-severity bug if found.
- **Antivirus or backup software locking the temporary file:** the retry rule covers it; the temp name is not reused.
- **Clock changes and time zones:** `/M` and journal times use the system clock; out-of-order times are not an error.
- **Power loss:** an interrupted stage 6 leaves either the old file or the new file (NTFS atomic replace); the stale temporary file is cleaned via the journal record.

## 8. Undo and redo

See 3.7 and annotation-core section 8. The save pipeline itself is not a command; a save adds no entry to the undo stack.

## 9. Journal entries

Defined in 3.4. This spec owns the control records and the envelope; feature specs own their command payloads.

## 10. Save behaviour

This is the save behaviour. Output requirements for every saved file: original bytes are an exact prefix (incremental saves); a valid new xref section in the same form as the previous (table after table, stream after stream, including the stream's own entry); trailer with `/Prev`, correct `/Size`, `/Root`, `/Info`, first `/ID` element kept; `%%EOF`; no private PDFium keys; every annotation an indirect object with `/P`; `qpdf --check` passes; the Arlington-model checker reports no new errors compared with the source file (a baseline comparison, because real files already have errors).

## 11. Engine and protocol additions

### 11.1 Engine: `SaveDocument` (schema v6, id 62)

| Request field | Type | Limits |
|---|---|---|
| `mode` | u16 | max 2: 0 incremental, 1 full rewrite, 2 flattened full rewrite |
| `flags` | u32 | max 3: bit 0 flatten form fields, bit 1 flatten comments and markup (only with mode 2); other bits must be 0 |
| `outputHandle` | u64 | min 1; write handle duplicated into the engine by the broker |

Reply: `bytesWritten` (u64, max 2^40), `pageCount` (u32, max 10000000). Statuses: `Ok`, `PermissionDenied`, `InvalidArgument`, `Timeout`, `EngineCrashed`, `Unsupported`. Invariant: the engine restores all display-mirror flag changes before saving (annotation-core 4a).

### 11.2 Writer host (ids 1025 and up, same framing; `ValidateStructure` 1024 exists)

**`NormalizeTail` (1026).**

| Request field | Type | Limits |
|---|---|---|
| `originalHandle`, `pdfiumHandle`, `outputHandle` | u64 | min 1 |
| `flags` | u32 | max 3: bit 0 restore author check box and radio state streams, bit 1 strip PDFium private keys |

Reply: `ok` (bool), `bytesWritten` (u64), `truncated` (bool), `issueCodes` (array u32, maxCount 256), `issueMessages` (array string, maxCount 256, maxBytes 192, `sameLengthAs`), the same shape as `ValidateStructureReply`.

**`AppendAnnotations` (1025).**

| Request field | Type | Limits |
|---|---|---|
| `baseHandle` | u64 | min 1 |
| `editsHandle` | u64 | min 1; handle to the edit blob (a read-only file or section) |
| `editsLength` | u64 | max 268435456 (256 MiB) |
| `outputHandle` | u64 | min 1 |
| `pageCount` | u32 | max 10000000; must equal the base's page count, else `InvalidArgument` |

Reply: `ok`, `annotationsWritten` (u32, max 100000000), `objectsWritten` (u32), `bytesWritten` (u64), `truncated`, `issueCodes`, `issueMessages` as above. Statuses: `InvalidArgument` (blob bad, page mismatch, expectation mismatch), `Unsupported` (encrypted, hybrid, ambiguous structure), `Damaged`, `Timeout`, `EngineCrashed`.

**`ImportStampImage` (1027):** [stamps](stamps.md). **`AssemblePages` (1028):** reserved for [page-organiser](page-organiser.md) if the assembler option is chosen.

### 11.3 The edit blob

The broker writes the edits for stage 4 into a file or section and passes its handle. It is **not** a frame, so the 64 KiB limit and the no-nesting rule of the control protocol do not apply to it. A second schema file, `src/MikePDF.Protocol/schema/mikepdf.edits.json`, defines it (MessagePack: a header map `{version, pageCount, assetCount}`, an array of edit maps, an array of asset blobs keyed by SHA-256) with explicit limits on every field (contents 16 KiB, points per stroke 1,000,000, strokes per annotation 100,000, edits 1,000,000, total 256 MiB), and a generator emits the C# reader and writer. The reader is a parser of broker-produced data inside the sandbox; it is still fuzzed (SharpFuzz) as defence in depth. Each edit on a loaded annotation carries its expectation (subtype, rectangle) for the stage 4 check. OPEN QUESTION OQ-SP-3: ADR-0005 states "messages have no nesting"; this is a deliberate second format for bulk data and needs Opus's decision (alternative: many small windowed messages, which is slow and complicated for ink).

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Save_Progress_Name` | Saving {0} |
| `Save_Stage_Saving` | Saving... |
| `Save_Stage_Checking` | Checking the saved file... |
| `Save_Done_Announce` | Saved {0} |
| `Save_Failed_Title` | MikePDF couldn't save safely |
| `Save_Failed_Body` | Your changes are still here. Nothing in your file was changed. |
| `Save_Failed_Details` | Details |
| `Save_NoSpace` | There isn't enough space to save. Free some space or save to another drive. |
| `Save_InUse` | {0} is in use by another program. Save a copy instead. |
| `Save_ChangedOnDisk` | {0} was changed by another program since you opened it. |
| `Save_Overwrite` / `_SaveAs` / `_Cancel` | Overwrite / Save a copy / Cancel |
| `Save_ReadOnly` | {0} can't be changed here. Save a copy instead. |
| `Save_Encrypted` | This document is password protected. MikePDF can't save changes into it yet. Save a copy without the password, or discard your changes. |
| `Save_Encrypted_Copy` | The copy won't be password protected. |
| `Save_TooLarge` | This file is too large to save changes into. |
| `Save_Structure` | This file's structure can't be edited safely. You can still read it. |
| `Save_Signed_Break_Title` | This will make the signature invalid |
| `Save_Signed_Break_Body` | Saving this way rewrites the whole file. Readers will report that the signature is no longer valid. |
| `Save_Signed_Pages_Body` | Changing pages in a signed document will make readers report that it was changed after signing. |
| `Save_AsDialog_Title` | Save a copy |
| `Save_RewriteWhole` | Rewrite the whole file (smaller, removes edit history, invalidates signatures) |
| `Save_FlattenedTitle` | Save a flattened copy |
| `Save_Flatten_Hint` | The copy can't be edited. Your open document stays as it is. |
| `Save_Revert_Confirm` | Discard all changes and reload {0} from disk? |
| `Save_BackupFailed` | Changes can't be backed up right now. Save to keep them safe. |
| `Save_Dirty_Suffix` | , unsaved changes |
| `Recover_Title` | Restore unsaved changes? |
| `Recover_Body` | MikePDF closed before {0} was saved. |
| `Recover_Summary` | {0}. Last change {1}. |
| `Recover_Restore` / `_Discard` / `_NotNow` | Restore / Discard / Not now |
| `Recover_Partial` | {0} of {1} changes were restored. |
| `Recover_FileChanged` | {0} changed after you made these edits. Some changes may not fit. |
| `Recover_Unreadable` | Your saved changes for {0} couldn't be read. |
| `Recover_List_One` / `_Other` | Unsaved changes found for 1 document / Unsaved changes found for {0} documents |

Telemetry: none. If opt-in diagnostics are ever added, only counts of save stage failures by reason code, never names.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-SP-1 | Journal write failure: refuse the edit (strict) or continue with a warning (degraded, proposed). | Opus |
| OQ-SP-2 | DPAPI-protect journal contents. | Opus (security), User |
| OQ-SP-3 | Second schema for the edit blob versus windowed messages (11.3). | Opus |
| OQ-SP-4 | Replace mechanism after spike S10; share-delete open flags; ADR-0005 decision 6 and §7.5 step 3 may need an amendment. | Opus |
| OQ-SP-5 | Block all edits during a save (assumed), or allow annotation edits to queue. | Design |
| OQ-SP-6 | Retention and cleanup suggestions for old journals. | User |
| OQ-SP-7 | Confirm journals live in `LocalState\private\journals`, overriding §7.4's path. | Opus |
| OQ-SP-8 | Encrypted documents: block Save (assumed) until PDFium's behaviour is measured and encrypted writing exists. | Opus, User |
| OQ-SP-9 | An Arlington-model checker as a test tool: licence and availability for CI (the model is published by the PDF Association; confirm the licence of the checker). | Opus |
| OQ-SP-10 | M4 gate wording: "fidelity harness green for all P0 operations across PDFium, pdf.js, Acrobat Reader and Edge". Acrobat and Edge cells need the user (R-15); the gate cannot pass without that pass. | User |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| SP-01 | Stage planner: for every combination of edit kinds (none, forms, pages, annotations, and each mix) the planner selects the specified stages; "none" does nothing and no engine save is called. | Unit (`MikePDF.Core.Tests`) |
| SP-02 | Dirty tracking: dirty after any command, clean after save, clean again after undo to the clean point, dirty after undo past it; save snapshot sequence becomes the clean point even if edits arrived afterwards (they were blocked; test the guard). | Unit |
| SP-03 | Journal format: envelope, CRC-32C, sequence continuity; torn last line is silent; a corrupt middle record yields the valid prefix and a report; unknown `cmd` is skipped and counted. | Unit |
| SP-04 | Journal durability: every command is flushed before acknowledgement (fault-injecting file layer asserts the order `Write`, `Flush`, `Ack`); a failed flush triggers the OQ-SP-1 behaviour. | Unit |
| SP-05 | `docKey` is stable per path, case-insensitive on Windows, and differs between paths; header fingerprint detects a changed file. | Unit |
| SP-06 | Replay reconstructs the model, undo and redo stacks for scripted sequences including undo, redo and new command after undo. | Unit |
| SP-07 | Idempotency table: file equals `SaveBegin` output (journal cleared silently), equals header fingerprint (replay), equals neither (warn), missing file (error); tested at each combination. | Unit |
| SP-08 | Journal limits and privacy: size caps, 90% chip, password fields absent, excerpts capped at 200 characters, no page content; a byte scan of a journal from a fixture session finds none of the document's body text. | Unit + Integration |
| SP-09 | Page-identity to ordinal translation after moves, deletes and inserts; annotations on deleted pages dropped; expectation mismatch aborts stage 4. | Unit + Integration |
| SP-10 | Edit blob codec: round trip; every limit exceeded by one is rejected; truncated, oversized and duplicated-asset blobs rejected; SharpFuzz target exists and runs in the fuzz workflow. | Unit + fuzz |
| SP-11 | Protocol: golden vectors and malformed frames for `SaveDocument`, `NormalizeTail`, `AppendAnnotations` in the codecs (C# both sides, C++ for `SaveDocument`). | Unit (Protocol + Engine C++) |
| SP-12 | Stage 2 + 3 on xref-stream and classic fixtures: output has the original as exact prefix, a valid appended xref in the right form, no private keys; 9 of 9 xref-stream sources validate (S4 had 7 of 9 invalid). | Integration + Fidelity |
| SP-13 | Stage 4 on classic and xref-stream bases and a base with a prior incremental update: valid xref, `/Prev` chain, `/Size`, trailer `/ID` first element kept and second new, `%%EOF`, original prefix. `qpdf --check` and the strict validator pass. | Unit (PdfWriter) + Fidelity |
| SP-14 | No-op: save with no commands writes nothing and does not touch the file's timestamp. | Integration |
| SP-15 | Validation catches faults: injected writer-host faults (drop an annotation, corrupt an xref offset, wrong `/Size`, leave a PDFium key) each fail stage 5, leave the original byte-identical, and delete temporary files. | Integration |
| SP-16 | Atomicity under kill: killing the app, the engine and the writer host at each stage boundary (0 to 6) and during stage 6 leaves either the old file or the new file, never a mix (byte comparison), with no temporary file left after the next start. | Integration (fault injection) |
| SP-17 | Replace semantics (needs S10): with engines holding the file, the chosen sharing flags and replace method succeed; the security descriptor, attributes and `Zone.Identifier` stream are identical before and after (compare SDDL and stream bytes). | Integration |
| SP-18 | Read-only file, read-only directory, no-permission directory, `WindowsApps` path: Save becomes Save As with the right message; nothing written. | Integration |
| SP-19 | Disk full (small VHD): save fails with `Save_NoSpace`, original intact, temporary files removed. | Integration (needs mounted VHD; admin) |
| SP-20 | Sharing violation and transient lock: the 5-retry rule succeeds when the lock clears within 600 ms and fails cleanly after. | Integration |
| SP-21 | External change: modifying the file while open and dirty triggers the banner and the Overwrite / Save As prompt on save. | Integration |
| SP-22 | Signed documents: notice once; incremental save keeps all original bytes (hash of the prefix) and the signature dictionary; flatten, full rewrite and page operations require the confirmation dialog. | Integration |
| SP-23 | Encrypted fixtures (RC4, AES-128, AES-256): Save is blocked with the specified message; Save As offers an unprotected copy with the notice; the original is never replaced. | Integration |
| SP-24 | Over 1 GiB fixture (synthetic sparse file): edits refused with the message and no partial files. | Integration |
| SP-25 | Save As: writes to the new path, switches the tab, leaves the original byte-identical, re-keys the journal; Save As to the same path equals Save. | Integration + UI journey |
| SP-26 | Full rewrite option and flattened copy: output validates; flattened output has no widgets or flattened-type annotations, renders within SSIM 0.98, text extractable; the open document and its file are unchanged. | Integration + Fidelity |
| SP-27 | Rebase: after save the engines reopen on the new file, pending model is empty, no frame is blank or double-drawn across the swap (AC-15), journal deleted only after replace success (assert order). | Integration |
| SP-28 | Crash recovery journey 8 (§12.5): kill the UI process with unsaved ink, relaunch, restore prompt, ink restored point for point. | UI journey |
| SP-29 | Recovery variants: kill between replace and cleanup (silent clear); file changed (warning); journal tail torn (valid prefix); journal for a document not in the session (listed in the empty window). | Integration + UI journey |
| SP-30 | Journey 3 (§12.5): highlight, note, save, reopen: intact. Journey 4: form fill, save, fidelity harness values intact. Journey 5: reorder, rotate, delete, undo all, redo all, save. | UI journey |
| SP-31 | Timeouts: a stalled engine or writer host at each stage is killed at the deadline, the save fails, the original is intact, the next save works. | Integration |
| SP-32 | Concurrency: a second save request while one runs does not start a second pipeline; edits are blocked during the save and re-enabled after. | Unit + UI journey |
| SP-33 | Fidelity of the whole pipeline: for the S4 annotation matrix (A01 to A14 as Q-series production output) and form matrix (B01 to B12), the saved files reopen in a fresh PDFium and pdf.js with the model matching, on classic and xref-stream bases; Acrobat and Edge cells recorded as pass, fail or **not measured** (R-15). | Fidelity harness |
| SP-34 | Parser differential: for the fuzz corpus, the writer's strict parse and PDFium agree on object resolution for every object the update touches, or the writer refuses (no silent disagreement). | Integration + fuzz |
| SP-35 | Accessibility: progress, failure and recovery dialogs expose names, default buttons and polite announcements once each; dirty state is in tab names. | UI journey (a11y) |
| SP-36 | OneDrive Files On-Demand and SharePoint-synced folders: save, no conflict copy, no leftover temporary file (manual checklist, §12.7); marked **not run** until done. | Manual record |
| SP-37 | Performance recorded (no budget claimed): stage timings for a 20-page, a 600-page and a 5,000-page fixture with 100 annotations; peak memory of each process. | Integration (perf) |
