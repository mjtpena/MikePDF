# Feature spec: tabs, multiple windows, tear-out and session restore

- Status: Draft for Opus review
- Milestone: M3 (full reader)
- Master prompt: §2.1 (Tabs, with tear-out to new window, and session restore), §5.2 rules 3 and 5, §7.6, §10.1, §18 scenarios 1 and 7
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [command-palette-shortcuts](command-palette-shortcuts.md) (every command here is a registry command), [settings-policies](settings-policies.md) (settings and policy keys), the design spec in `docs/design/` (all visuals)
- Used by: [save-pipeline](save-pipeline.md) (dirty prompts on close), [page-organiser](page-organiser.md) (drag pages between tabs)

Conventions (test layers, IDs, "proposed" numbers, OPEN QUESTION tags) are in [README.md](README.md).

## 1. Where the code is today

Facts read from `src/MikePDF.App` on 2026-11-02, because they shape the work:

- `MainWindow` shows one document. `HandleActivationAsync` opens only `paths[0]` and replaces the open document. A second launch of `mikepdf.exe` is redirected to the running instance by `AppInstance` (`Program.cs`, key `MikePDF.Main`).
- `DocumentSession` takes its `ITilePresenter` and `DispatcherQueue` in its constructor, so it cannot move between windows yet.
- Each `DocumentSession` starts **two** engine processes: the document engine and a pool engine (`StartPoolAsync`). ADR-0004 says one shared pool engine and a cap of 6 engines. **Nothing in the code enforces a cap, and the pool engine is per document, not shared.** Ten tabs would be 20 sandboxed processes, and each document engine may use 2 GB (§5.2 rule 5). Tabs cannot ship without fixing this (section 3.8).
- `DocumentStore` (`LocalState\private\documents.json`) holds recent files and per-document positions. There is no session file.

## 2. User stories

1. As a reader with several PDFs open, I switch between them from a tab strip, and each tab keeps its own page, zoom and undo history.
2. As a multi-monitor user, I drag a tab out of the window and it becomes a new window with the document still open, without a reload or flicker, and I can drag it back.
3. As someone who double-clicks PDFs in Explorer, each file opens as a tab in the window I was last using (or a new window, if I chose that).
4. As a user who closed MikePDF with eight tabs open, I get them back at the same pages next time, without waiting for eight documents to load.
5. As an IT admin, I can see that MikePDF will not start 40 processes because a user opened 20 files.
6. As a keyboard-only or Narrator user, I can switch, close, reorder and move tabs without the mouse.
7. As a user whose engine crashed in one tab, the other tabs and windows keep working (§18 scenario 7).

## 3. Detailed behaviour

### 3.1 Model

`Window` contains an ordered list of `Tab`. A tab has a stable `TabId` (GUID, never reused), a normalised document identity, a title, and either a live `DocumentSession` or a `SuspendedTabState` (section 3.8). The model is plain C# in `MikePDF.Core` (`Windowing/`) so the rules below are unit-testable without XAML. The WinUI layer binds to it.

Document identity is the **file ID** (volume serial number plus 128-bit file ID from `GetFileInformationByHandleEx(FileIdInfo)`), with the normalised full path as fallback when the file ID is unavailable (some network shares). Two paths that name the same file (`C:\a\x.pdf`, `C:\A\X.PDF`, an 8.3 name, a mapped drive versus UNC, a symbolic link) have the same identity.

### 3.2 Opening

| Source | Behaviour |
|---|---|
| Ctrl+O, drop, Recent list, activation, command line | Resolve identity. If a tab for it exists in **any** window, activate that tab and bring its window forward. Otherwise open a new tab. |
| New tab position | User-initiated (Ctrl+O, drop, Recent): immediately right of the active tab. Activation from outside (Explorer, alias): at the end of the strip. |
| Active tab is an empty tab (no document) | Reuse it instead of adding a tab. |
| Several files in one activation | Open all, activate the first. **More than 10** in one activation asks "Open all N files?" first (decisions.md Q12; the first draft said 25), and at most 100 are read from one activation; both limits exist to stop a hostile or accidental launch from starting hundreds of documents. |
| Same file, second view | Not supported in v1. One live view per file, because two views would give two edit histories for one file (see [save-pipeline](save-pipeline.md)). Activating the existing tab is the answer. |
| Window for activation from outside | Setting `windows.openFromShell`: `LastActiveWindow` (default), `NewWindow`, `ActiveWindowOfThisDesktop`. OPEN QUESTION OQ-TAB-3 covers virtual desktops. |

Opening validates the path first (see [file-association-jumplist](file-association-jumplist.md) section 3.3: device paths, directories, pipes and non-disk files are refused before any handle is opened).

### 3.3 Tab strip

- Title: the document title from `GetDocumentInfo` if non-empty, else the file name without extension. Two tabs with the same title in one window show the parent folder name after the title. Tooltip: full path.
- A tab shows: a modified dot when there are unsaved edits (M4), a lock glyph while it is waiting for a password, and a warning glyph when its engine is restarting or has failed.
- Close button on each tab (visible on hover, on the selected tab and on keyboard focus). Middle-click closes. A "+" button adds an empty tab, which shows the empty state with recent files.
- Context menu: Close, Close other tabs, Close tabs to the right, Move to new window, Copy full path, Show in File Explorer, Reload from disk (confirm if modified).
- Overflow: the strip scrolls; the selected tab is always scrolled into view. Maximum **50 tabs per window** (proposed); the 51st opens a new window instead.
- Tabs live in the title bar (§7.6). Drag regions, caption buttons and snap layouts (Win+Z, maximise-button hover) must keep working with tabs present. The design spec fixes the visuals.

### 3.4 Windows and tear-out

- `Ctrl+N` opens a new window with an empty tab. "Move to new window" (context menu, command) moves the selected tab.
- **Tear-out:** dragging a tab beyond the strip's vertical bounds by more than the system drag threshold creates a window under the pointer, sized like the source window, and moves the tab into it. Dragging the **only** tab of a window moves the window instead. Dropping a tab on another MikePDF window's strip inserts it at the drop position. Dropping on the taskbar or empty desktop creates a window there.
- **Nothing reloads on a move.** The `DocumentSession` is detached from the source window's presenter and attached to the target's; its engines, tile cache, selection, find state, undo stack and edit journal stay alive. The tile cache is invalidated only if the target window's DPI differs from the source's (tiles are DPI-specific); the old tiles stay on screen, scaled, until new ones arrive (§7.1 rule 3: never blank).
- This requires `DocumentSession` to expose `DetachPresenter()` and `AttachPresenter(ITilePresenter, DispatcherQueue)`. All windows share **one UI thread** (OPEN QUESTION OQ-TAB-2 asks whether to allow a thread per window; one thread is the assumption here because a session's timers and events are bound to one `DispatcherQueue`).
- WinUI's `TabView` has tear-out support (`CanTearOutTabs` and related events). The exact event names and their behaviour under Windows App SDK 2.5.1 are **UNVERIFIED**; the first WP of this feature is spike S13 (UI API verification), which confirms them, or falls back to a custom drag implementation. Do not write production code against remembered names.

### 3.5 Closing

| Action | Behaviour |
|---|---|
| Close a clean tab | Closes immediately. The engines are shut down and the position is saved (existing behaviour). |
| Close a tab with unsaved edits | Dialog: Save, Don't save, Cancel. "Don't save" discards the edit journal for that document. |
| Close a window | One dialog if any tab is dirty, listing the dirty documents: Save all, Don't save, Cancel. |
| Close the last window | The app exits. The session is written first. |
| System shutdown or sign-out (`WM_QUERYENDSESSION`) | No dialog. The journal is already on disk (§7.4); the session is written; the next launch offers restore. |
| Ctrl+Shift+T | Reopens the most recently closed tab (path, page, zoom, fit, view rotation), in the window it was closed from if that still exists. Stack of 10 per app run; not persisted across runs. Edits are not restored (they were saved or discarded when closing). |

### 3.6 Session restore

The session file is `LocalState\private\session.json` (ADR-0004 A2: nothing the engine could tamper with goes anywhere else). Schema version 1, written atomically (temp file, then move) like `DocumentStore`.

It stores, per window: bounds (restored-state rectangle), maximised or not, the display it was on (device name and its DPI, only to validate bounds), the ordered tabs, the active tab, the sidebar open state and pane. Per tab: path, file ID if known, page, page fraction, zoom, fit mode, view rotation, view mode ([page-view-modes](page-view-modes.md)), and whether it was suspended. It never stores passwords, document text, or edits.

- **When it is written:** 2 seconds after any change (debounced), every 30 seconds while the app is open, and on every clean close. A write failure never interrupts reading.
- **Restore setting** `startup.restore`: `Always`, `Ask`, `Never`. **The default is OPEN QUESTION OQ-TAB-1.** Recommendation: `Ask` only after an unclean exit (crash or kill), `Always` after a normal close with more than one tab open, otherwise start with an empty window. Edge and Chrome differ on this; Acrobat Reader restores nothing by default.
- **Restore is lazy.** Windows and tab strips are created at once; only the **active tab of each window** opens its document immediately. Other tabs are created as `SuspendedTabState` (title from the file name or the stored title) and open when first selected. This is how startup stays inside the §11 budget with many tabs.
- **Bounds:** a stored rectangle that no longer intersects any monitor's work area is moved onto the nearest one and clamped to its size. A different DPI keeps the logical size, not the pixel size. Maximised stays maximised, on the nearest monitor.
- **Missing, moved or unreadable files:** the tab is kept and shows a placeholder: the file name, "This file couldn't be found", and buttons Locate file, Close tab. Nothing is silently dropped.
- **Password-protected documents:** the tab asks for the password when it is first selected. A password is never written to disk.
- **Crash restore** (§7.4): after an unclean exit the session restores tabs as above, then each document with a journal offers its own restore prompt ([save-pipeline](save-pipeline.md)).
- **Corrupt session file:** treated as empty. One non-blocking notice, "MikePDF couldn't restore your last session", shown once.
- **Privacy:** the session holds file paths, which is the same data class as the recent list. Settings has "Clear history" (recent files, positions, session, closed-tab stack) and `startup.restore = Never` prevents the file being written at all.

### 3.7 Single-instance routing

`Program.Main` keeps `AppInstance.FindOrRegisterForKey`. The activation handler becomes a queue: activations that arrive during startup or session restore wait until the first window is ready, then run in arrival order, so they do not race with the restore. A modal dialog in the target window delays the activation, which then runs when the dialog closes; the window is flashed in the taskbar meanwhile.

### 3.8 Engine budget and tab suspension

This section is required for tabs to be safe. The numbers are **proposals, not measurements**; the W1-perf harness must measure idle and peak memory per tab before they are fixed (§19.3).

- **Shared pool engine.** One pool engine per **window group** (all windows, in v1) serves thumbnails, text and search for every document, using the pool kind's 32-document limit (`kMaxDocumentsPoolKind`). Document engines remain one per live tab (`kMaxDocumentsDocumentKind = 1`).
- **Live-tab cap** `engines.maxLiveTabs`, default **6** (policy-controllable, [settings-policies](settings-policies.md)). When a seventh tab is selected, the **least recently active** unsuspended tab that is not the active tab of any visible window is suspended.
- **Suspension** closes the tab's document engine and pool document, frees its tile cache (the existing `TrimMemory` path plus eviction of everything), keeps the last frame as a static bitmap for the switch animation, and keeps view state and the edit model. Edits survive because the edit model and journal live in the broker, not the engine ([annotation-core](annotation-core.md) section 3.6).
- **Resume** reopens the document from the file (a new engine, the same path as engine-crash recovery), reapplies the edit model to the engine's display mirror, and restores the view. The existing banner is not shown for a resume. Target: sharp first tile within the §11 warm-open budget (250 ms for a typical 20-page file) once the engine is up. Not measured.
- A tab with an active password keeps the password in an in-memory protected buffer (`CryptProtectMemory`, zeroed on close) so it can resume without asking. This touches risk R-37; see OQ-TAB-4.
- A tab with a modified document is **never** suspended while a save is running.
- Background tabs with a running search or print job are not suspended until the job ends.
- Minimised windows keep only visible tiles (existing behaviour) and their background tabs are suspended after `engines.suspendAfterMinutes` (default 10).

## 4. Keyboard, pen and touch

| Action | Default shortcut | Command id |
|---|---|---|
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab | `tabs.next`, `tabs.previous` |
| Go to tab 1 to 8 | Ctrl+1 to Ctrl+8 | `tabs.goto1` to `tabs.goto8` |
| Go to last tab | Ctrl+9 | `tabs.gotoLast` |
| Close tab / reopen closed tab | Ctrl+W (also Ctrl+F4) / Ctrl+Shift+T | `tabs.close`, `tabs.reopen` |
| New tab / new window | Ctrl+T / Ctrl+N | `tabs.new`, `window.new` |
| Move tab left / right | Ctrl+Shift+PageUp / Ctrl+Shift+PageDown | `tabs.moveLeft`, `tabs.moveRight` |
| Move tab to a new window | none by default; palette and context menu | `tabs.moveToNewWindow` |

Note a conflict with §10.2: **Ctrl+1 and Ctrl+2 are fit page and fit width** in the master shortcut table. Ctrl+1 to Ctrl+8 as "go to tab" therefore collides. OPEN QUESTION OQ-TAB-5: keep §10.2 and use Alt+1 to Alt+9 for tabs (recommended), or follow browsers and move the zoom shortcuts.

- Mouse: drag to reorder, tear out and merge; middle-click closes; Ctrl+click is not special.
- Touch: touch-drag a tab to reorder; long-press then drag to tear out. A swipe on the strip scrolls it. Touch targets are at least 40 x 40 DIP (design spec).
- Pen: behaves as mouse for the strip. Palm rejection ([ink-pen](ink-pen.md)) does not apply to the strip.
- The keyboard order of the title bar is: tab strip (one tab stop; arrow keys move between tabs, Enter or Space selects, Delete closes), "+" button, window caption buttons.

## 5. Accessibility

- The strip is a UIA `Tab` with `TabItem` children (WinUI `TabView` provides this; verify in the spike). Each item has `SelectionItemPattern`. Name: "{title}", with ", unsaved changes" appended when dirty, and ", needs password" when locked. HelpText: the full path.
- Narrator on switching tabs: the selected tab item's name, then "tab {n} of {count}" (provided by the control). After the switch, the page surface raises the existing page-changed announcement ("Page {n} of {count}"). No extra live region is added, to avoid doubling.
- Closing: a polite live-region message "{title} closed. Now showing {other}" or "No documents open".
- Tear-out and drag have keyboard equivalents: `tabs.moveLeft`, `tabs.moveRight`, `tabs.moveToNewWindow`. Moving announces "{title} moved to position {n} of {count}" or "{title} moved to a new window".
- Focus after closing a tab goes to the neighbouring tab, not to the document surface, if focus was in the strip.
- All tab-strip controls honour high contrast and 225% text scaling; labels must not truncate the close button's target.
- The restore placeholder is a normal page of buttons with automation names.

## 6. Error states

| Situation | UI |
|---|---|
| Window creation fails | Non-blocking notice; the tab stays in the source window. |
| Engine start fails for one tab | That tab shows the existing "viewer keeps stopping" banner with "Open again". Other tabs are unaffected. |
| `session.json` unreadable | Section 3.6 corrupt-file rule. |
| Too many tabs | Section 3.3: the 51st opens a window; if that also fails, a notice. |
| Activation path invalid | Refused before any handle opens; a notice names the problem (no such file, not a file, path not allowed). |
| Resume from suspension fails | The tab shows the file-error page for the cause (`PasswordRequired`, `Damaged`, file missing) with the normal recovery actions. |

## 7. Edge cases

- **Rotated pages and CropBox offsets:** not affected, but the stored position (page, fraction) is in layout terms and survives them. View rotation is stored per tab.
- **Encrypted documents:** see 3.6 and 3.8. Permissions are re-read on resume.
- **Huge documents:** a 5,000-page tab suspended and resumed must not re-fetch all page sizes before painting (existing lazy layout). The stored page fraction must be clamped when the file changed on disk and now has fewer pages.
- **File changed on disk while suspended or while restoring:** the position is clamped; a tab whose file ID changed but path matches is treated as the same file.
- **Hostile input:** activation arguments are untrusted strings: bounded length, no device paths, no alternate data stream names, at most 100 files read, and a question above 10. `session.json` is user-writable only by the user and SYSTEM (protected DACL) but is still parsed defensively: size cap 1 MiB, at most 64 windows and 1,000 tabs, every numeric field range-checked, unknown keys ignored.
- **Two monitors with different DPI:** tear-out onto the other monitor re-rasterises tiles once, at the new scale.
- **Display removed while the app is running:** windows on it move to the primary display (Windows does this; the model must re-clamp stored bounds on `WM_DISPLAYCHANGE`).
- **Sleep and resume, lock screen:** engines survive. If the watchdog killed an engine during sleep, the existing crash path restores it.
- **RTL UI:** the strip mirrors; "right" in "close tabs to the right" follows visual direction. Test in the pseudo-RTL build.
- **Tab dragged while its document is mid-password prompt or mid-engine-restart:** the move is allowed; the dialog and banner belong to the session and move with it.

## 8. Undo and redo

Tab and window operations are **not** undoable commands. The recovery path is Reopen closed tab (Ctrl+Shift+T). Each document's undo stack belongs to its `DocumentSession` and moves with it across windows and through suspension unchanged ([save-pipeline](save-pipeline.md) section 3.7).

## 9. Journal entries

None for tab operations. The session file is state, not a journal. Per-document edit journals ([save-pipeline](save-pipeline.md)) are keyed by document identity and are unaffected by which window or tab shows the document.

## 10. Save behaviour

Closing a dirty tab or window uses the save pipeline's prompts. `session.json` is written atomically and is separate from the edit journal. Session restore never saves, discards or replays edits by itself.

## 11. Engine and protocol additions

**No protocol messages.** Broker-side work only:

- `EngineBudget` in `MikePDF.Broker`: counts live engines, enforces `engines.maxLiveTabs` and the process cap, and tells the app which tab to suspend. **Implemented in `m3/engine`** (ADR-0004 amendment A3), with one difference from this text: the budget suspends the engine itself rather than telling the app which tab to suspend. The broker keeps the deny-write file handle, the password and the `SupervisedDocument`, so the tab keeps its document object and the next call resumes it. The tabs keep the tile cache, the last frame and the view state, as 3.8 says. `DocumentEngine.Pin()` is how a visible tab, a save or a print job (TAB-24) stays alive; `DocumentEngine.StateChanged` (`Suspended`, `Resuming`, `Running`, `Failed`) is what the tab strip binds to for `Suspended_Resuming`; `DocumentEngine.SuspendAsync()` and `ResumeAsync()` are for suspending a tab at once on `engines.suspendAfterMinutes` and for resuming one ahead of a tab switch. The cap is `EngineBudgetOptions.MaxDocumentEngines` (default 6); reading it from `engines.maxLiveTabs` and the policy is the tabs work package's job. TAB-22 says the number of processes is at most `2 + maxLiveTabs`; the budget keeps it at most `1 + maxLiveTabs` (one shared pool), sampled in `EngineBudgetTests`.
- Engine kinds are unchanged. The pool kind already allows 32 documents; `DocumentSession` no longer starts its own pool engine (done in `m3/engine`). **The 32 is now the limit on open tabs** unless a tab's document in the pool is closed when the tab is suspended; the tabs work package decides.
- A memory limit per engine stays at the Job Object default of 2 GB. With the live-tab cap of 6, the worst case is about 12 GB of engine memory on a 16 GB machine. **That is too high**, and the default cap or the per-engine limit must be lowered once the perf harness gives real numbers. Flagged in OQ-TAB-6.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Tabs_Strip_Name` | Open documents |
| `Tab_Modified_Suffix` | , unsaved changes |
| `Tab_Locked_Suffix` | , needs password |
| `Tab_New` | New tab |
| `Tab_Close` | Close tab |
| `Tab_Menu_CloseOthers` | Close other tabs |
| `Tab_Menu_CloseRight` | Close tabs to the right |
| `Tab_Menu_MoveToWindow` | Move to new window |
| `Tab_Menu_CopyPath` | Copy full path |
| `Tab_Menu_ShowInExplorer` | Show in File Explorer |
| `Tab_Menu_Reload` | Reload from disk |
| `Tab_Moved_Announce` | {0} moved to position {1} of {2} |
| `Tab_MovedWindow_Announce` | {0} moved to a new window |
| `Tab_Closed_Announce` | {0} closed. Now showing {1} |
| `Tab_ClosedLast_Announce` | {0} closed. No documents open |
| `Close_Dirty_Title` | Save changes? |
| `Close_Dirty_One` | {0} has changes that aren't saved. |
| `Close_Dirty_Many` | These documents have changes that aren't saved: |
| `Close_Dirty_Save` | Save |
| `Close_Dirty_Discard` | Don't save |
| `Open_Many_Title` | Open many files? |
| `Open_Many_Body_One` | Open 1 file? |
| `Open_Many_Body_Other` | Open all {0} files? |
| `Restore_Title` | Restore your last session? |
| `Restore_Body_One` | You had 1 document open. |
| `Restore_Body_Other` | You had {0} documents open. |
| `Restore_Yes` | Restore |
| `Restore_No` | Start fresh |
| `Restore_Failed` | MikePDF couldn't restore your last session. |
| `Restore_Missing_Title` | This file couldn't be found |
| `Restore_Missing_Locate` | Locate file |
| `Suspended_Resuming` | Opening {0} |

Plural keys follow the README's `_One` / `_Other` convention (OQ-GLOBAL-1).

Telemetry: none. The product has no telemetry (§14); if opt-in diagnostics are ever added, counts of tabs and windows only, never paths.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-TAB-1 | Default for `startup.restore` (section 3.6). | User (product) |
| OQ-TAB-2 | One UI thread for all windows (assumed) or one per window. One per window isolates a hung window but breaks the session-to-dispatcher binding. | Opus |
| OQ-TAB-3 | Which window receives an outside activation when windows are on different virtual desktops. | User (product) |
| OQ-TAB-4 | Keep a document password in a protected in-memory buffer so a suspended tab can resume silently (touches R-37), or re-ask the password on resume. | Opus (security) |
| OQ-TAB-5 | Ctrl+1/Ctrl+2 conflict between §10.2 zoom shortcuts and "go to tab" (section 4). | User (product) |
| OQ-TAB-6 | Live-tab cap and per-engine memory limit: needs W1-perf measurements. | Opus, after W1-perf |
| OQ-TAB-7 | Does `TabView` tear-out in Windows App SDK 2.5.1 support our needs, or is a custom drag needed (spike)? | Sonnet spike, Opus review |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| TAB-01 | Document identity: all path spellings of one file (case, 8.3, symlink, UNC vs mapped) resolve to one identity; two files with the same name in different folders do not. | Unit (`MikePDF.Core.Tests`) |
| TAB-02 | Opening a file that has a tab in any window activates that tab and that window; no second session is created. | Integration |
| TAB-03 | Opening from an empty tab reuses the empty tab; otherwise a new tab appears right of the active one for user actions and at the end for outside activations. | Unit (tab model) |
| TAB-04 | An activation with 30 files opens none until confirmed, then opens all; with 10 files it opens all without a prompt, with 11 it asks (decisions.md Q12). | Unit + UI journey |
| TAB-05 | A 51st tab in a window opens in a new window. | Unit |
| TAB-06 | Tab titles: document title, else file name; duplicates in one window gain the folder name; the tooltip is the full path. | Unit |
| TAB-07 | Moving a tab to another window keeps the same engine process IDs, the same undo stack object and the same edit journal file; no `OpenDocument` is sent to a new engine. | Integration |
| TAB-08 | Moving a tab to a window on a monitor with different DPI re-renders tiles at the new scale and shows no blank frame (zero frames with missing coverage in the frame-composer log). | Integration + UI journey |
| TAB-09 | Dragging the only tab of a window moves the window, and does not create a second window. | UI journey |
| TAB-10 | Closing a dirty tab shows Save / Don't save / Cancel; Cancel leaves the tab; Don't save deletes that document's journal. | Integration (needs the M4 save pipeline; until then a fake dirty flag) |
| TAB-11 | Closing a window with three dirty tabs shows one dialog listing three documents. | UI journey |
| TAB-12 | `WM_QUERYENDSESSION` shows no dialog and leaves `session.json` complete and every journal flushed. | Integration |
| TAB-13 | Ctrl+Shift+T reopens the last 10 closed tabs in reverse order with their page, zoom, fit and view rotation; the 11th oldest is gone. | Unit |
| TAB-14 | `session.json` round-trips: write, read, identical model. Version field present. Written atomically: killing the writer mid-write leaves the old file intact. | Unit + Integration |
| TAB-15 | Restoring a session with eight tabs opens exactly one document engine per window at first paint; the other seven are suspended placeholders. | Integration |
| TAB-16 | Window bounds on a monitor that no longer exists are moved onto a visible monitor and are fully inside its work area. Maximised state is kept. | Unit (pure function over a monitor list) |
| TAB-17 | A restored tab whose file is missing shows the placeholder with Locate file and Close tab, and stays in the strip. | UI journey |
| TAB-18 | A restored password-protected document asks for the password on first selection; `session.json` contains no password (grep of the bytes for the test password). | Integration |
| TAB-19 | Corrupt, truncated, oversized (> 1 MiB), too-many-windows and out-of-range `session.json` files are rejected without a crash; the app starts with an empty window and shows the one-time notice. | Unit + fuzz-style corpus |
| TAB-20 | With `startup.restore = Never`, no `session.json` is written. | Unit |
| TAB-21 | Activations received during session restore run after it, in arrival order. | Unit (activation queue with a fake clock) |
| TAB-22 | Live-tab cap: opening N+1 tabs suspends the least recently active background tab; the number of engine processes never exceeds `2 + maxLiveTabs` (one shared pool plus document engines; value to be confirmed after OQ-TAB-6). | Integration |
| TAB-23 | A suspended tab with unsaved edits resumes with identical edits, undo stack depth and view position. | Integration (after M4 edits exist) |
| TAB-24 | A tab is never suspended during a save or print job. | Unit |
| TAB-25 | Killing one tab's engine leaves other tabs, in the same and other windows, rendering; only that tab shows the banner (§18 scenario 7). | UI journey |
| TAB-26 | Keyboard-only: open three files, switch with the tab shortcuts, reorder with the move shortcuts, move one to a new window, close it with Ctrl+W, reopen it with Ctrl+Shift+T, with no mouse input. | UI journey |
| TAB-27 | UIA: the strip is a `Tab`; each item is a `TabItem` with `SelectionItem`, name including the dirty and locked suffixes, and help text with the path. Checked with raw `IUIAutomation`. | UI journey (a11y) |
| TAB-28 | Narrator-facing text: moving and closing raise the live-region strings in section 12 exactly once each (counted from UIA events). | UI journey (a11y) |
| TAB-29 | A hostile activation (device path `\\.\pipe\x`, directory, 40,000-character path, alternate data stream name) opens nothing and keeps the app responsive. | Unit + Integration |
| TAB-30 | High contrast and 225% text scale: tab titles, close buttons and the "+" button remain reachable and unclipped (screenshot compare against golden at both settings). | UI journey (visual) |
| TAB-31 | Ten tabs open for five minutes with background suspension on: UI-process private bytes stay within the budget set in OQ-TAB-6 (value not yet set; this criterion is "not measured" until it is). | Integration (perf) |


## 15. What was built (work package W2-tabs, branch `m3/tabs`)

Written 2026-11-03 by the implementer. The test map is in the work package's completion note; this section records the decisions and measurements that differ from the text above.

| Part | Where |
|---|---|
| Identity, tab and window model, open rules, closed tabs, engine leases, activation relay, path validator, launch arguments | `MikePDF.Core/Windowing/` (`DocumentIdentity`, `TabModels`, `WindowModels`, `Activation`) |
| `session.json` codec, store, recorder, restore policy, window placement | `MikePDF.Core/Windowing/Session.cs` |
| What the app shares between windows (engine host and budget, model, activation, session, tab moves) | `MikePDF.App/Services/AppHost.cs`, `TabEntry.cs`, `NativeWindowing.cs` |
| The window: strip in the title bar, opening into tabs, closing, moving, Settings as a tab, session capture and restore | `MikePDF.App/MainWindow.Tabs.cs` (and the other `MainWindow*` partials) |
| A session that can change window | `DocumentSession.AttachPresenter` / `DetachPresenter` (a presenter proxy), `PinEngine`, `ResumeEngineAsync`, `EngineLifeChanged` |

Decisions taken where the spec was silent or in conflict (all can be overridden):

- **Command ids** follow `docs/design/commands.md` (`tab.next`, `file.newTab`, `file.close`, `file.reopenClosed` ...), as the commands work package did, not the `tabs.*` names of section 4.
- **OQ-TAB-5:** go to tab 1 to 8 is **Alt+1 to Alt+8**, and Alt+9 goes to the last tab. Ctrl+1 and Ctrl+2 stay fit page and fit width (master prompt 10.2).
- **OQ-TAB-1:** `startup.restore` is `Ask` by default, meaning: after an unclean exit ask first; after a normal close restore when more than one document was open, else start empty. `Always` and `Never` are as named; `Never` also stops the session file being written and removes an old one.
- **OQ-TAB-3:** `windows.openFromShell` offers `LastActiveWindow` (default) and `NewWindow`. `ActiveWindowOfThisDesktop` is not offered: the virtual desktop API was not verified.
- **Confirm before many files:** more than **10** files in one activation or drop asks first (decisions.md Q12), not 25. At most 100 files are read from one activation.
- **Several files at once:** the first opens at once; the others become tabs that open when first shown (like a restored session), so ten files do not start ten engines.
- **The open-document limit is 16, not 32.** Each open document holds two documents in the shared pool engine (one for placeholders, one for text, thumbnails and search), and the pool allows 32. A seventeenth open is refused with a message ("MikePDF keeps up to 16 documents open at once"). A restored placeholder holds nothing and does not count; a window holds at most 50 tabs.
- **The cap on document engines** is read from `engines.maxLiveTabs` (policy `MaxLiveTabs`) at start and applied when it changes (`EngineBudget.SetMaxDocumentEngines`, a small addition to the broker). The tab on screen in each non-minimised window holds a pin; a minimised window releases it.
- **Settings is a singleton tab** per window (design 13). Closing it goes back to the tab it was opened from.
- **A window with one tab:** that tab is part of the title bar (not draggable out); dragging it moves the window. With two or more tabs, dragging out creates a window (TabView tear-out), and letting go over another window's strip merges the tab into it.
- **Closing the last tab** leaves an empty tab (design 3.1.4); Ctrl+W on a lone empty tab does nothing.
- **Dirty prompts** exist and are tested with a fake flag (`TabModel.IsDirty`; the journeys set it with the test seam `MIKEPDF_TEST_FAKE_DIRTY`). The Save button is disabled until the save pipeline sets `AppHost.SaveHandler`.

Measured facts about WinUI's `TabView` in Windows App SDK 2.5.1 (answers to OQ-TAB-7 / spike S13):

- `CanTearOutTabs`, `TabTearOutWindowRequested`, `TabTearOutRequested`, `ExternalTornOutTabsDropping` and `ExternalTornOutTabsDropped` exist (checked in the metadata) and tear-out works: dragging a tab down out of the strip raises the first event, then the second with the tab in hand, and the new window follows the pointer. **`TabTearOutRequested.NewWindowId` was 0, not the id given in the first event**, so the window made in the first event is remembered and used.
- **`ExternalTornOutTabsDropping` and `Dropped` were never raised** when a torn-out tab was let go over another window's strip, so the merge is done by the app (the left button is watched until it comes up; if the pointer is over another strip the tab joins it).
- Taking part of the title bar away from the tabs (`InputNonClientPointerSource` caption regions that cover them) stops a tab being dragged out, so the caption override is used only while the window has a single tab.
- `DisplayArea.FindAll()` cannot be enumerated with `foreach` (an `InvalidCastException`); an index loop works.

Not done, or not verified: see the completion note.

## 16. Test seams

The journeys steer the app through five environment variables. **Every one is read through `TestSeamGate` (`MikePDF.Core`) and `TestSeams` (the app), and none is read when the app runs from a Store-signed package** (`Package.Current.SignatureKind == PackageSignatureKind.Store`; if the kind cannot be read the app counts as shipped and ignores them). `TestSeamGateTests` checks the gate with an injectable signature kind, that no other code reads a `MIKEPDF_TEST_` variable, and that this list is the whole list.

| Variable | What it does |
|---|---|
| `MIKEPDF_TEST_POLICY_KEY` | Names a throwaway key under `HKCU\Software\MikePDF.Tests`, read as more user-level policy (which can only tighten). |
| `MIKEPDF_TEST_RESTORE` | Overrides the `startup.restore` setting (`Always`, `Ask`, `Never`). The harness passes `Never` unless a journey is about the session. |
| `MIKEPDF_TEST_FAKE_DIRTY` | `1` marks every opened document as having unsaved edits, so the close prompts can be driven before the save pipeline exists. |
| `MIKEPDF_TEST_JUMPLIST_DUMP` | A file name in the private folder; the app writes the Jump List items it saved there. |
| `MIKEPDF_TEST_PRESENT_DUMP` | A file name in the private folder; the app appends `awake` when presentation mode asks Windows to keep the display awake and `asleep` when it lets go (docs/features/page-view-modes.md, PV-14). |
