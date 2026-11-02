# Feature spec: file association, the default-app prompt and the Jump List

- Status: Draft for Opus review
- Milestone: M3 (full reader)
- Master prompt: §2.1 (open via file association, Jump List and Recent; a prompt pointing the user to Windows Settings to set the default), §7.6 (single instance, Jump List), §13.1 (launch actions and links), §15.3
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [tabs-windows-session](tabs-windows-session.md) (where activations land), [settings-policies](settings-policies.md)

Conventions are in [README.md](README.md).

## 1. Where the code is today

- `Package.appxmanifest` declares a `.pdf` file type association (`uap:FileTypeAssociation Name="pdf"`, `OpenIsSafe="false"`) and an execution alias `mikepdf.exe`. MikePDF never changes the default itself (store notes).
- `Program.Main` redirects a second launch to the running instance; `App.PathsFrom` extracts paths from a file activation (`IFileActivatedEventArgs`) or a launch command line, keeping only paths for which `File.Exists` is true. `MainWindow.HandleActivationAsync` opens the first path.
- `DocumentStore` keeps a recent list (20 entries). There is **no Jump List code** and no default-app logic.
- ADR-0015 decision 6: no thumbnail or preview handlers in the Store preview. Those are in [shell-extensions](shell-extensions.md).

## 2. User stories

1. As a user, I double-click a PDF in Explorer and it opens in MikePDF, in the window I was using.
2. As a user who has just installed MikePDF, I can make it my default PDF app in two clicks, and MikePDF never changes that behind my back.
3. As a user who says "no thanks", I am not asked again for a long time, or never if I say so.
4. As a user, I right-click the taskbar icon and see my recent and pinned PDFs and tasks to open a file or a new window.
5. As a privacy-minded user, "Clear recent files" in MikePDF also clears them from the Jump List.
6. As an IT admin, I can turn the prompt off by policy.

## 3. Detailed behaviour

### 3.1 Registration

The manifest keeps the file type association for `.pdf` and the `mikepdf.exe` alias. Windows shows MikePDF under "Open with". Changes in this spec:

- **Verbs.** Today only the open verb exists. A `Print` shell verb (right-click, Print in Explorer) needs `SupportedVerbs` on the association; the exact manifest elements for packaged apps are **UNVERIFIED**. OPEN QUESTION OQ-FA-3: whether to add it in M3. If added, it activates the app with a print request that goes through the normal print dialog ([print](print.md)), never prints silently.
- **`OpenIsSafe="false"`** stays. It is an existing decision and means Windows treats the file type as one that needs the usual download warnings.
- **No other file types** are registered in v1 (not `.fdf`, not `.xfdf`).
- The protocol `mikepdf://` (§15.3, "internal use only") is **not registered** in M3. OPEN QUESTION OQ-FA-5: nothing in the plan needs it, and any registered protocol is something a web page can launch. If a use appears (for example shell handlers), it needs its own input validation spec.

### 3.2 The default-app prompt

Windows does not allow an app to set itself as default. The only action is to send the user to Settings. MikePDF does exactly that and nothing else.

**Detecting the state.** A small service `IDefaultAppProbe.IsDefaultForPdf()` answers yes, no or unknown. The candidate implementation asks the shell for the current handler of `.pdf` and compares the handler's application identity with this package's. The exact call (`AssocQueryStringW` with an application-ID string, or `IApplicationAssociationRegistration`) and what it returns for a packaged handler are **UNVERIFIED**; the first task of this feature is a ten-line probe run against a real install with Edge, Acrobat and MikePDF as default in turn. "Unknown" is treated as "do not prompt".

**When to prompt.** All of these must hold:

1. The probe says "no" (not "unknown").
2. At least **3** documents have opened successfully (a counter in `LocalState\private\prompts.json`, so that the first launch is never interrupted).
3. The user has not chosen "Don't ask again".
4. At least **30 days** since a "Remind me later" (both numbers proposed).
5. Policy `DefaultAppPromptEnabled` is not 0.
6. The window is not in presentation mode or full screen, and no dialog is open.

**How.** A non-modal in-app notice (an InfoBar at the top of the content area; look and wording in the design spec). It never steals focus and never blocks reading. Buttons: **Open Settings**, **Remind me later**, **Don't ask again**, and a close "X" that means "Remind me later".

**Open Settings** launches the fixed URI `ms-settings:defaultapps` (a constant in code, not built from input). Whether a more specific URI can open MikePDF's own entry is **UNVERIFIED** and is tried in the probe task; if it works for packaged apps it replaces the constant. When the window is next activated, the probe runs again; if MikePDF is now the default, a polite notice says so.

**Also available any time:** Settings > General shows "MikePDF is / isn't your default PDF app" with an Open Settings button, and the palette has "Make MikePDF the default PDF app" (`app.openDefaultAppsSettings`). Both are always visible regardless of the prompt state.

Nothing is sent anywhere; detection reads local shell state.

### 3.3 Activation and path validation

Every path that arrives from outside is untrusted: it comes from Explorer, a shortcut, another app, a script, or a Jump List entry that may have been tampered with. `ActivationPathValidator` (in `MikePDF.Core`, pure, no I/O except the final disk checks) runs before any handle is opened.

| Rule | Detail |
|---|---|
| Length | 1 to 32,767 characters, no NUL, no control characters. |
| Normalise | `GetFullPath`; `\\?\C:\...` long-path prefix accepted and normalised; `\\?\UNC\server\share` accepted. |
| Reject device namespace | `\\.\...` (devices, pipes, mailslots), `\\?\GLOBALROOT`, `\\?\Volume{...}` raw volume access, and reserved device names (`CON`, `NUL`, `COM1` and so on, which `GetFullPath` turns into device paths). |
| Reject alternate data streams | A `:` anywhere after the drive designator. |
| Must be a file | After opening for attribute query only: regular disk file (`GetFileType` = disk), not a directory. A symbolic link is followed and the target must pass the same checks. |
| Network paths | UNC and mapped drives are allowed: the user chose to open the file. (The NTLM concern in §13.1 is about links inside documents, which stay blocked.) |
| Extension | **Not** checked on open: the engine decides if it is a PDF by content. The association only gates how Windows offers MikePDF. |
| Count | At most 25 files per activation without confirmation ([tabs-windows-session](tabs-windows-session.md)). |

Items that arrive without a file system path (virtual or cloud-only items from some providers, where `IStorageItem.Path` is empty): OPEN QUESTION OQ-FA-2. Today they are silently dropped. Proposed: show "This location can't be opened directly. Copy the file to your PC and open it there." and open nothing. A temporary copy would break the "save in place" promise.

**Command line.** Recognised: file paths, and `--new-window`. Everything starting with `-` or `/` that is not recognised is ignored (never treated as a path). No `--page`, `--dest` or `#nameddest=` arguments: they would let a link or shortcut steer the app; the m2 spec's named destinations are reachable from inside the app. Quoting follows the existing `CommandLine.Split`.

**Cloud placeholders.** A OneDrive "online-only" file (recall-on-data-access attribute) is hydrated by the first read; the opening state shows "Downloading {name}" if it takes more than 500 ms, and Cancel is available (§7.5 rule 6). Hydration cannot be forced earlier than the first read without triggering it for thumbnails; opening is the user's intent so it is allowed.

### 3.4 Jump List

Built with the packaged-app Jump List API (`Windows.UI.StartScreen.JumpList`; member names to be confirmed in the first task, **UNVERIFIED**).

| Section | Content | Who manages it |
|---|---|---|
| Tasks | "Open a PDF..." (launch argument `--open-dialog`) and "New window" (`--new-window`) | MikePDF, set at startup |
| Recent | Windows' system Recent group for this app | Windows, from files opened through the shell and from MikePDF registering opens |
| Pinned | Items the user pinned in the Jump List | Windows (nothing for MikePDF to implement) |

- **Registering opens.** When MikePDF opens a document from its own picker, drop or recent list, it registers it with the shell's recent documents under this app's identity (`SHAddToRecentDocs` with the application ID; exact call **UNVERIFIED** for packaged apps). Files opened by double-click are already registered by Windows.
- **Setting `privacy.addToWindowsRecent`** (default on). Off means MikePDF does not register opens with the shell. The user's Windows setting "Show recently opened items in Start, Jump Lists and File Explorer" overrides everything.
- **Clear recent files** in MikePDF clears its own list and asks the shell to clear this app's destinations. The call to use (`IApplicationDestinations::RemoveAllDestinations`) is **UNVERIFIED** in a packaged app. If it cannot be made to work, OPEN QUESTION OQ-FA-4 decides between clearing the whole shell recent list (too broad) and documenting that Jump List entries are cleared by Windows' own setting.
- **A Jump List entry for a file that no longer exists:** launching it opens the app with an invalid path, which gets the normal "file not found" message with a "Remove from recent" button.
- **Timing.** The Jump List is built **after first paint** and after the session restore, on a background continuation, so it does not count against the 600 ms cold-start budget (§11). A failure is logged and ignored.
- The list is refreshed on start and when the recent list changes (debounced to once per 5 seconds).

### 3.5 Where an activation lands

Handled by [tabs-windows-session](tabs-windows-session.md) section 3.2 and 3.7: the same file activates its existing tab; a new file opens in a new tab in the window chosen by `windows.openFromShell`; `--new-window` forces a new window.

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Open file | Ctrl+O | `file.open` |
| Open Default apps settings | none | `app.openDefaultAppsSettings` |
| Clear recent files | none | `file.clearRecent` |

The prompt's buttons are reachable by Tab and have access keys. Pen and touch use ordinary buttons with 40 x 40 DIP targets.

## 5. Accessibility

- The prompt is an `InfoBar` (UIA `Group` with a name; it exposes its title, message and buttons). It is added in tab order after the command bar, not before, and does not take focus when it appears.
- On appearing it raises a **polite** live region: "Make MikePDF your default PDF app? Open Settings, Remind me later, Don't ask again." It is announced once, not repeatedly.
- The Settings status line is plain text with a button; its state is not conveyed by colour alone.
- Jump List entries are OS UI; their text comes from our task names, which are localised.
- Opening state "Downloading {name}" is a progress element with a name and a Cancel button.

## 6. Error states

| Situation | Behaviour |
|---|---|
| Probe returns unknown | No prompt; the Settings status line says "MikePDF couldn't check" and still offers the button. |
| `ms-settings:` launch fails | Message: "Open Windows Settings, choose Apps, then Default apps, and search for .pdf." |
| Jump List API fails | Logged; no user-visible error. |
| Activation path invalid | Notice with the specific reason (not found, is a folder, not a file, location not supported); nothing opens. |
| File locked by another process | The existing open error with Retry. |
| Hydration cancelled or fails | "Couldn't download {name}. Check your connection and try again." |

## 7. Edge cases

- **Rotated pages, CropBox, encryption:** not applicable at this layer.
- **Multi-selection** of 200 PDFs in Explorer: handled by the 25-file confirmation.
- **Rapid double-click** (two activations for one file within a second): the second finds the existing tab.
- **Path with Unicode, spaces, trailing dots or spaces, or very long path:** normalised by the validator; unit tests include each.
- **Activation while a modal dialog is open or while restoring a session:** queued ([tabs-windows-session](tabs-windows-session.md) section 3.7).
- **Hostile activation:** device paths, pipes, ADS names, 40,000-character paths, directories, junctions to a pipe, trailing-space names. None may block the UI thread (opening `\\.\pipe\x` can block forever in `CreateFile`; the validator never opens it).
- **Standard user versus administrator:** the prompt and Jump List need no elevation.
- **Policy:** `DefaultAppPromptEnabled=0` hides the prompt but not the Settings status line.
- **Store updates:** a package update keeps the user's default choice (Windows behaviour). The prompt counters live in `LocalState\private` and survive updates and are removed on uninstall.

## 8. Undo and redo

Not applicable.

## 9. Journal entries

None.

## 10. Save behaviour

None. `prompts.json` is small state written atomically in `LocalState\private`.

## 11. Engine and protocol additions

None. Path validation happens before any engine is involved.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `DefaultApp_Title` | Make MikePDF your default PDF app? |
| `DefaultApp_Body` | Windows asks you to choose it yourself. We'll take you to Settings. |
| `DefaultApp_Open` | Open Settings |
| `DefaultApp_Later` | Remind me later |
| `DefaultApp_Never` | Don't ask again |
| `DefaultApp_Now` | MikePDF is now your default PDF app. |
| `DefaultApp_Status_Yes` | MikePDF is your default app for PDF files. |
| `DefaultApp_Status_No` | MikePDF isn't your default app for PDF files. |
| `DefaultApp_Status_Unknown` | MikePDF couldn't check which app opens PDF files. |
| `DefaultApp_LaunchFailed` | Open Windows Settings, choose Apps, then Default apps, and search for .pdf. |
| `JumpList_OpenFile` | Open a PDF... |
| `JumpList_NewWindow` | New window |
| `Activation_NotFound` | {0} couldn't be found. |
| `Activation_IsFolder` | {0} is a folder, not a PDF file. |
| `Activation_NotAllowed` | This location can't be opened. |
| `Activation_NoPath` | This location can't be opened directly. Copy the file to your PC and open it there. |
| `Activation_RemoveRecent` | Remove from recent files |
| `Hydrate_Progress` | Downloading {0} |
| `Hydrate_Failed` | Couldn't download {0}. Check your connection and try again. |
| `Cmd_OpenDefaultApps` | Make MikePDF the default PDF app |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-FA-1 | Which API reliably reports the default `.pdf` handler for a packaged app (probe task). | Sonnet probe, Opus review |
| OQ-FA-2 | Items with no file system path: refuse with a message (proposed) or support through a temporary copy. | User (product) |
| OQ-FA-3 | Add a `Print` verb in M3? | User (product) |
| OQ-FA-4 | How to clear Jump List destinations for this app only, in a packaged app. | Sonnet probe |
| OQ-FA-5 | Register `mikepdf://` at all? §15.3 says "internal use only". | Opus (security) |
| OQ-FA-6 | Prompt thresholds (3 opens, 30 days). | User (product) |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| FA-01 | `ActivationPathValidator` accepts normal drive paths, UNC paths, `\\?\C:\...` long paths and Unicode names; it normalises case-insensitively. | Unit (`MikePDF.Core.Tests`) |
| FA-02 | It rejects: empty, NUL or control characters, > 32,767 characters, `\\.\pipe\x`, `\\.\PhysicalDrive0`, `\\?\GLOBALROOT\...`, `\\?\Volume{...}`, `CON`, `NUL`, `COM1.pdf`, `C:\a.pdf:stream`, and a directory. | Unit + Integration |
| FA-03 | A path to a named pipe or a junction pointing at one never blocks: the validator returns within 200 ms. | Integration |
| FA-04 | Command line parsing: paths with spaces and quotes work; `--new-window` is recognised; unknown `-x` and `/x` arguments are ignored and never opened as files; `--page 3` opens no file. | Unit |
| FA-05 | A file activation of three files opens three tabs, in order, in one window, with the first active. | Integration |
| FA-06 | A file activation for an already-open file activates the existing tab and window. | Integration |
| FA-07 | An activation with no `Path` shows `Activation_NoPath` and opens nothing. | Integration |
| FA-08 | Prompt rules: for each of the six conditions in 3.2, flipping only that condition suppresses the prompt (table-driven test with a fake probe, clock and counters). | Unit |
| FA-09 | "Remind me later" suppresses for 30 days (fake clock); "Don't ask again" suppresses permanently; both persist across restarts in `prompts.json`. | Unit + Integration |
| FA-10 | The prompt is non-modal: opening a document and scrolling work while it is shown; focus is not moved when it appears. | UI journey |
| FA-11 | Open Settings launches exactly the constant URI and nothing derived from user or document input. | Unit |
| FA-12 | The probe reports "no" when another app is default and "unknown" when it cannot decide, on a real packaged install. "Yes" is verified in the manual checklist after the user sets the default (cannot be automated: Windows protects the setting). | Integration + manual record |
| FA-13 | Jump List contains the two tasks with the right arguments after startup (`JumpList.LoadCurrentAsync`), and building it happens after first paint (trace point ordering). | Integration (packaged) |
| FA-14 | Opening a document from the picker registers it with shell recents when `privacy.addToWindowsRecent` is on, and does not when off. | Integration |
| FA-15 | "Clear recent files" empties the in-app list and the app's Jump List destinations (or, if OQ-FA-4 is unresolved, this criterion is marked **not met** in the milestone report). | Integration |
| FA-16 | Opening a Jump List entry for a missing file shows the not-found message with "Remove from recent files", which removes it. | UI journey |
| FA-17 | Activation on a OneDrive online-only placeholder opens the file and shows `Hydrate_Progress` when it takes over 500 ms; cancel stops the download and the tab closes without error. | Integration (needs a placeholder fixture; manual-gated on OneDrive) |
| FA-18 | Accessibility: the prompt has a name, its buttons have names and access keys, it raises one polite live-region message when shown, and takes no focus. | UI journey (a11y) |
| FA-19 | With policy `DefaultAppPromptEnabled=0` the prompt never shows; the Settings status line still does. | Unit |
| FA-20 | Uninstall removes `prompts.json` (package data removed) and the association; install then shows MikePDF under "Open with" (manifest check at build). | Integration (package smoke test) |
| FA-21 | Manifest check in CI: only the `.pdf` association, the alias, no protocol, no verbs unless OQ-FA-3 is resolved. | Unit (build check on `Package.appxmanifest`) |
