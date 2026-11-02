# 14 Errors, damaged files, engine recovery and crash restore

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 7 (hostile file: an error for that document, other tabs usable, nothing lost), 10 (a big file under memory pressure), and the crash journeys 7 and 8 in §12.5
- Spec: master prompt §6.4 (error model), §5.2 rule 3 (engine crashes are survivable), §7.4 (journal, restore), §7.5 (save failures), §11 (engine crash to restored document at most 2 s)
- Replaces nothing: it designs around the strings and banners that exist today (`Error_*`, `Banner_Engine*`, `Password_*` in [Resources.resw](../../../src/MikePDF.App/Strings/en-US/Resources.resw) **[repo]**)

## 1. Purpose

When something goes wrong, tell the user the truth in one sentence, keep everything that still works usable, lose nothing, and show the next step. An error belongs to **one document in one tab**. It never takes down the window, and never blocks another tab.

### Rules

| # | Rule |
|---|---|
| R1 | **Scope:** the error shows where it happened (in that tab), not in a window-level dialog. The exceptions are the password prompt and the crash-restore question at launch |
| R2 | **Words:** what happened, then what you can do. No error codes in the main text. No blame. No "oops" |
| R3 | **Next step:** every error offers at least one action (Try again, Locate, Save as, Show in folder) or says plainly that there is none |
| R4 | **Details on request:** a **Copy details** button puts a technical report on the clipboard: error kind, engine version, OS version, time, and a request id. **Never** document content, file path, file name or text |
| R5 | **Work is never lost silently:** any error that could lose edits says whether the edits are safe and where |
| R6 | **Retry limits:** automatic retries are bounded (3 engine restarts in 60 s), then the app stops and tells the user |

## 2. Layout

### 2.1 In-tab error page (a document that couldn't be opened)

```text
+--------------------------------------------------------------------------------------------+
|[M] Annual report.pdf  x |[!] broken.pdf  x | +                                             |
+--------------------------------------------------------------------------------------------+
|[=]|  (document commands disabled)                                                          |
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|                       [ warning glyph, 48 epx ]                                            |
|                                                                                            |
|                       This file could not be opened                    <- Title            |
|                       broken.pdf looks damaged, and MikePDF couldn't    <- Body             |
|                       read it.                                                            |
|                                                                                            |
|                       [ Try to repair ]   [ Show in folder ]   [ Copy details ]            |
|                                                                                            |
+--------------------------------------------------------------------------------------------+
   Other tabs keep working. The tab shows the warning glyph.
```

### 2.2 The error kinds and what each page offers

| Kind (§6.4) | Body text (existing strings where they exist **[repo]**) | Actions |
|---|---|---|
| `PasswordRequired` | **Dialog, not a page** (2.3): "{0} is protected. Enter its password to open it." | Open, Cancel |
| `PasswordIncorrect` | In the same dialog: "That password isn't right. You can try {0} more times." Then, after the limit: page: "{0} was not opened because the password was wrong too many times." | Try again (re-asks), Close tab |
| `UnsupportedEncryption` | "{0} uses a kind of protection that MikePDF doesn't support yet, so it can't be opened here." | **Open with another app...** (the system "Open with" chooser, started by the user's click), Show in folder, Close tab |
| `Damaged` | "{0} looks damaged, and MikePDF couldn't read it." | **Try to repair**, Show in folder, Copy details |
| `PartiallyRendered` | **Not a page.** A warning chip on the affected page (2.5) | Details |
| `Timeout` | "MikePDF took too long to draw part of a page. Try again." (page-level overlay, 2.5) | Try again |
| `EngineCrashed` | "MikePDF's viewer stopped unexpectedly. Try opening {0} again." (page, only if recovery failed) | Open again, Copy details |
| `PermissionDenied` (owner) | Not an error: an informational banner (2.6) and disabled commands | Details |
| File not found | "{0} can't be found. It may have been moved or deleted." | **Locate...**, Remove from recent, Close tab |
| File in use or no access | "{0} is being used by another program. Close it there and try again." / "MikePDF couldn't read {0}. It may be open in another program, or you may not have permission to read it." | Try again, Show in folder |
| Sandbox unavailable | "MikePDF couldn't start its protected viewer, so it did not open {0}. Nothing was read." | Try again, Copy details |
| Unknown | "Something went wrong while opening {0}." | Try again, Copy details |

`{0}` is the file name. Messages never contain a path.

### 2.3 Password dialog (kept; small changes)

```text
        +----------------------------------------------+
        | Password needed                              |
        | contract.pdf is protected. Enter its         |
        | password to open it.                         |
        | [ ******************        ] [eye]            |   PasswordBox, reveal button (peek)
        | That password isn't right. You can try 2 more times.   <- after a wrong attempt
        |                         [ Open ]   [ Cancel ]|
        +----------------------------------------------+
```

Focus starts in the password box. `Enter` opens. The reveal button shows the text only while pressed (`PasswordRevealMode=Peek`). The password is held in memory only until the open completes and is never written to the journal, the recents or the diagnostics.

### 2.4 Engine recovery banners (existing strings), in sequence

```text
 1.  (!) Restarting the viewer
         The part of MikePDF that draws pages stopped. It is starting again, and your place in the document is kept.
                                                                                     [x]
 2.  (v) Back to normal                         <- success, auto-dismisses after 6 s
         The viewer restarted. You are where you were.
 3.  (x) The viewer keeps stopping               <- after 3 failures in 60 s
         MikePDF could not keep the viewer running for this file. You can try opening it again.
                                                                [ Open again ]   [ Copy details ]
```

Banner 1 appears **only if the restart takes more than 400 ms**; faster restarts show only banner 2 (budget: at most 2 s from crash to restored, §11). The page well keeps showing the last drawn tiles while the engine restarts, so there is no blank. Edits are replayed from the journal in the background.

### 2.5 Page-level problems

```text
  +----------------------------+          +----------------------------+
  |                            |          |        (spinner)           |
  |        page content        |          |   Drawing is taking long   |
  |               [!] <- chip  |          |   [ Try again ]            |
  +----------------------------+          +----------------------------+
   PartiallyRendered: a 24 epx warning chip           Timeout: overlay on the page (on the
   at the page's top-right corner (UI layer,          low-res fallback), Retry; the engine also
   not in the page) ; click for details.              retries once at lower quality by itself.
   Details flyout: "Some content on page 12 could not be drawn."
```

### 2.6 Owner restrictions (not an error)

```text
 | (i) The owner of this document limits what you can do.                                    [Details] [x] |
 |     Copying and changing pages are turned off.                                                      |
```

### 2.7 Unsaved changes found after a crash (a dialog at launch)

```text
        +--------------------------------------------------------------+
        | Restore your unsaved changes?                                |
        | MikePDF closed unexpectedly. These documents had changes     |
        | that weren't saved:                                          |
        |   [x] Contract v3.pdf       12 comments, 3 highlights        |
        |   [x] Permit application    41 filled fields                 |
        |   [ ] Lecture 4.pdf         2 ink strokes (file moved)       |
        |                                                              |
        | MikePDF kept only what you added, not the documents.         |
        |       [ Restore ]    [ Not now ]    [ Discard ]              |
        +--------------------------------------------------------------+
```

- **Restore** opens each checked document in a tab with its edits re-applied, marked modified. Nothing is saved to disk until the user saves.
- **Not now** keeps the journals and asks again next launch.
- **Discard** deletes the journals after a confirmation line ("This can't be undone").
- A document that has **moved or changed** on disk: shown unchecked with the reason; restoring it needs **Locate...**. A document that changed since the journal was written is opened but the dialog warns that "The file changed after you worked on it. Your changes may not line up." and defaults to unchecked **[design]**.
- This dialog is the **only** dialog that may appear at launch besides a password prompt for a file the user asked to open (README 9.2).

### 2.8 Save failed

```text
 | (x) Couldn't save "Contract v3". Your original file is unchanged.            [ Save as... ]  [ Details ] |
 |     The file is locked by another program.                                                          |
```

Reasons are specific: locked by another program, no permission, disk full, the folder is read-only, a cloud sync conflict, a failed validation (the output didn't match what was expected, §7.5 step 2; "MikePDF wouldn't replace your file with a copy it couldn't verify"). **Save as...** is always offered. The user's edits remain in the tab, marked modified, and in the journal.

### 2.9 The file changed on disk

```text
 | (i) "Annual report" changed on disk.                        [ Reload ]  [ Keep what I have ]  [x] |
```

Shown when the open file is modified by another program (checked on window activation, not by polling). If the document has no unsaved edits, **Reload** keeps the page and zoom. If it has edits, **Reload** asks first ("Reloading drops your unsaved changes").

## 3. Behaviour

1. **Opening a damaged file.** The engine returns `Damaged`. The tab shows 2.1 with **Try to repair**. Repair asks the engine to open the file through its recovery path (PDFium rebuilds a missing cross-reference table **[unverified: confirm which damage classes it recovers]**). If it works, the document opens **read-only as "Repaired copy (not saved)"** with an `InfoBar` "MikePDF repaired this file in memory. Your original is unchanged." and **Save repaired copy...**. If it can't, the page says "MikePDF couldn't repair this file." and offers **Show in folder**. No repair ever changes the original.
2. **Hostile files (scenario 7).** A file that crashes or hangs the engine takes down only that document's engine (one engine per document or pooled with isolation, ADR-0004). The tab shows the Damaged or EngineCrashed page after the retry limit; **every other tab keeps its engine and state**. The window stays responsive throughout (the broker watchdog kills a hung engine after 5 s, §5.2 rule 4).
3. **Recovery.** On engine death the broker: keeps the last tiles on screen, starts a new engine, reloads the document from the broker's handle, replays the journal, restores scroll and zoom, and shows the banners above. Target: at most 2 s.
4. **Retry limit** (R6): 3 restarts within 60 s for the same document stops the automatic loop and shows banner 3.
5. **Memory pressure** (scenario 10): when an engine nears its cap (2 GB by default), the broker trims caches first. If the engine is killed by the Job Object, treat as a crash and recover; if it repeats, show "MikePDF ran out of memory drawing this file. Try zooming out, or close other documents." as the reason in banner 3 **[design]**.
6. **Cloud-only files** (OneDrive placeholders, §7.5 rule 6): opening hydrates them with a visible progress bar and a Cancel; failure to hydrate (offline) shows "{0} is stored online and can't be reached right now." with **Try again**. Offline use of local files is unaffected (scenario 8).
7. **Errors inside panes** (thumbnails, bookmarks, annotations): shown in the pane with Retry (see [06](06-side-panes.md)), never as a document error.
8. **Journal restore** (2.7) happens once per launch, before session restore, so the restored tabs include the recovered edits. If both a session and a journal exist for the same file, the journal's edits are applied to the restored tab.
9. **Reporting.** **Copy details** is the only reporting route in v1.0. There's no automatic crash upload. If the user opted in to diagnostics, minidumps with document content excluded may be sent (§14); the error pages don't mention it.
10. **Never silent data loss.** Closing a window with unsaved edits asks first. A crash keeps the journal. A save never replaces the original with an unverified file.
11. **Logging.** Local diagnostic logs hold error kinds and timings, never paths, names, text or passwords. They can be cleared in Settings, Privacy.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Opening shows the tab ring and the page well's `Busy` ring after 300 ms. A repair attempt shows "Trying to repair {name}..." with an indeterminate bar and **Cancel**. Hydrating a cloud file shows a determinate bar. The restore dialog opens immediately; its list is built from the journal index (fast) |
| **Empty** | The restore dialog never opens with an empty list. An error page always has at least one action. "No details are available" can't happen: **Copy details** always has at least the kind and the version |
| **Error** | This whole file is about the Error state. Errors inside error handling: if **Copy details** fails (clipboard busy), show "Couldn't copy. Try again." If **Try to repair** crashes the engine, the page says "MikePDF couldn't repair this file" and doesn't retry |
| **Keyboard focus** | On an error page, focus goes to the **first action button** and the heading is announced. Tab order: actions in visual order. In the restore dialog: the document list (arrow keys and `Space` toggle), then **Restore** (default), **Not now**, **Discard**. Banners take focus only when the user presses `F6` to reach them; they don't steal focus (except the save-failed banner, which is reachable with one `F6`) |
| **High contrast** | Glyphs (warning, error, success, info) are `WindowText` shapes with the severity written in the title. Banners use `Window` and a 1 epx `WindowText` edge. The page chip uses a `WindowText` outline and glyph |
| **225% text scale** | Error pages reflow: heading and text wrap (max width 480 epx or the window), buttons stack. The restore dialog scrolls; its list rows wrap; buttons pinned. Banners grow in height; their actions wrap below the text |
| **Narrow window** | Error pages use the full width with 16 epx margins; actions stack at XS. Banners are full width minus 16 epx. The restore dialog is a full-width sheet |
| **Touch** | Buttons are 44 epx high; the chip's hit area is 44 x 44 around its 24 epx visible glyph; the restore list rows are 56 epx high with 44 epx checkboxes |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Error page | Group | "Could not open {name}" | `ErrorPage` |
| Heading | Text (heading) | "This file could not be opened" **[repo]** | `ErrorTitle` |
| Message | Text (live, assertive on appearance) | The message | `ErrorMessage` |
| Actions | Button | "Try to repair", "Show in folder", "Copy details", "Locate", "Open again", "Open with another app" | `ErrorRepair`, `ErrorShowInFolder`, `ErrorCopyDetails`, `ErrorLocate`, `ErrorReopen`, `ErrorOpenWith` |
| Banner | InfoBar | Severity and title | `Banner` **[repo]** |
| Banner action | Button | e.g. "Open again" | `BannerAction` **[repo]** |
| Page chip | Button | "Part of page 12 could not be drawn. Details." | `PageChip_{n}` |
| Timeout overlay | Group | "Drawing page 12 is taking too long" | `PageTimeout_{n}` |
| Password dialog | Dialog | "Password needed" **[repo]** | `PasswordDialog` |
| Password box | Edit (password) | "Password" **[repo]** | `PasswordBox` |
| Restore dialog | Dialog | "Restore your unsaved changes?" | `RestoreDialog` |
| Restore row | CheckBox | "{name}: {summary}" | `RestoreItem_{n}` |

Narrator, intended:

| Event | Announcement |
|---|---|
| A document fails to open | Assertive: "This file could not be opened. broken.pdf looks damaged, and MikePDF couldn't read it. Try to repair, button." (Focus moves to the first action.) |
| Engine restarts | Assertive: "Restarting the viewer. Your place is kept." Then polite: "Back to normal." |
| Engine keeps failing | Assertive: "The viewer keeps stopping. Open again, button." |
| Save fails | Assertive: "Couldn't save Contract v3. Your original file is unchanged." |
| Wrong password | Assertive: "That password isn't right. You can try 2 more times." |
| Restore dialog | "Restore your unsaved changes? Contract v3, 12 comments, 3 highlights, check box, checked." |
| Page chip appears | Polite once per page: "Part of page 12 could not be drawn." |

Assertive announcements are limited to events that stop the user's current action ([README G-SR2](../README.md#66-screen-reader-g-sr)).

## 6. Keyboard path

| Task | Keys |
|---|---|
| Act on an error page | Focus is on the first action: `Enter` |
| Open the document again | `Ctrl+R` (reload) on an error page, or `Enter` on **Open again** |
| Reach a banner | `F6` (it sits between the command bar and the page well in the order) |
| Dismiss a banner | `Esc` while it has focus |
| Copy details | `Tab` to the button, `Enter` |
| Restore dialog | Arrows and `Space` to choose, `Enter` for **Restore** |
| Close the failed tab | `Ctrl+W` |

## 7. Engine and protocol implications

| # | Need |
|---|---|
| E14 | **Repair:** the engine must tell the broker that an open succeeded through recovery (`Damaged` with a `recovered` flag, or a separate open mode), and `SaveFull` must be allowed on such a document to write the repaired copy |
| | **Typed errors** per §6.4 already exist; add **sub-reasons** so the UI can say "out of memory" vs "crashed" (broker-side: Job Object kill versus exception exit code) |
| | **Request ids** in every message (§5.4) become the "request id" in **Copy details** |
| | **Journal index:** a small per-document summary (counts of annotation types, fields filled, ink strokes) is written with the journal so the restore dialog can list them without replaying. It contains no document text (§7.4) |
| | **Hung engine kill:** the existing watchdog (5 s). No UI change |
| | **Open-with:** `Launcher` with the `LauncherOptions.DisplayApplicationPicker` is a broker action initiated by a user click; the file path stays with the broker |

## 8. OPEN QUESTIONS

1. **`UnsupportedEncryption`:** §6.4 says "offer to open in default browser". This spec offers the system **Open with** chooser instead, so the user picks the program; MikePDF doesn't choose a browser for them. Confirm.
2. **What damage can PDFium recover?** The repair promise depends on it. Run it on the "nasty" corpus (broken xref, truncated files) before shipping **Try to repair**; if it recovers almost nothing, drop the button and keep Show in folder.
3. Should the **restore dialog** pre-check all documents? This spec: yes, except those that moved or changed.
4. **Retry limit** (3 in 60 s) and the 400 ms threshold for showing the first banner are guesses.
5. Out-of-memory wording (3.5) assumes the broker can distinguish a Job Object kill. Verify.
6. Is **Copy details** enough, or does v1.0 need a "Send a report" action? §14 says no telemetry by default; reporting stays manual.
