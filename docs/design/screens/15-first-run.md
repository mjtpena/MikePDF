# 15 First run and the file-association prompt

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 8 (offline: nothing at first run needs a network), 9 (enterprise: IT sets the defaults and policies, so the user is never asked), and principle P2 (no interruptions)
- Spec: master prompt §2.1 ("file association for .pdf, and a prompt pointing the user to Windows Settings to set Quire as the default"), §14 (no telemetry by default)

## 1. Purpose

There is almost nothing to design, and that is the design. A reader that opens the file the user clicked, instantly, with nothing in the way, is the first-run experience. This file specifies the little that does exist: what happens on a first launch, and the one polite offer to become the default PDF app.

## 2. What MikePDF does not do on first run

| Not done | Why |
|---|---|
| No welcome screen, tour, carousel, "what's new" | The user came to read a file (P2). Competitors show a tour and sign-in prompts **[user report]** ([teardown A2](../competitive-teardown.md#41-against-adobe-acrobat-reader)) |
| No account, sign-in or licence step | There is no account (P9) |
| No telemetry question | Diagnostics are opt-in in Settings (Q9) |
| No permission prompts | The app needs no capability that prompts |
| No "rate this app" | Never |
| No modal of any kind before the document | README 9.2 |
| No Windows notification | |
| No automatic change to the Windows default | Windows doesn't allow it, and the user should choose |

## 3. Layout

### 3.1 First launch from Explorer with a file

```text
  (user double-clicks contract.pdf)
  -> window appears (budget 600 ms for an interactive empty window)
  -> tab "contract" shows the ring, page 1 appears (budget: first pixel at most 800 ms from Explorer)
  -> nothing else
```

### 3.2 First launch with no file (Start menu, pinned icon)

```text
+--------------------------------------------------------------------------------------------+
|[M] New tab  x | +                                                          [_]  [[]]  [X]  |
+--------------------------------------------------------------------------------------------+
|[=] ...                                                                                     |
+--------------------------------------------------------------------------------------------+
|     Open a PDF                                                                             |
|     Drop a PDF on this window, press Ctrl+O, or choose one below.                          |
|     [ Open a PDF ]                                                                         |
|                                                                                            |
|     Files you open will be listed here. The list stays on this device.                     |
|     No account. No tracking. Your files stay on this device.                               |
+--------------------------------------------------------------------------------------------+
```

This is the empty state of [02](02-empty-state.md) without recents.

### 3.3 The default-app prompt (later, on the empty state only)

```text
+--------------------------------------------------------------------------------------------+
|     Recent  ...                                                                            |
|                                                                                            |
|   +--------------------------------------------------------------------------------------+ |
|   | (i)  Open PDFs with MikePDF by default?                                              | |
|   |      Windows decides which app opens a file, so you choose it in Settings.           | |
|   |      [ Open Windows settings ]     [ Not now ]     [ Don't ask again ]               | |
|   +--------------------------------------------------------------------------------------+ |
+--------------------------------------------------------------------------------------------+
```

An `InfoBar` (Informational) below the recents grid, **inside the empty-state page**, never over a document.

### 3.4 After **Open Windows settings**: guidance for the Windows page

```text
   +----------------------------------------------------------------+
   | (i) In Windows Settings, find ".pdf" and choose MikePDF.       |
   |     Come back here when you're done.                    [ OK ] |   InfoBar with a Dismiss
   +----------------------------------------------------------------+
```

## 4. Behaviour

1. **First launch is the same as any launch.** There is no "first launch" flag with a different path. The only first-time state is "no recents yet" and "default-app prompt not yet shown".
2. **Cold start budget:** interactive empty window at most 600 ms (R1) and 350 ms (R3); first pixel for a document from Explorer at most 800 ms (§11). While the sandbox profile is created and the verified engine is put in place (as now **[repo: `MainWindow` constructor, `EngineHost.Create` on a background task]**), the window is already usable. If the engine isn't ready when the user opens a file, the tab shows its ring; no separate "Setting up..." screen exists.
3. **If the protected viewer can't start** (sandbox unavailable): the tab shows the page for `Error_SandboxUnavailable` ([14](14-errors-and-recovery.md)). Nothing is read without the sandbox. This is a first-run risk (R-41, R-43 in STATUS) and the message must be calm and specific.
4. **Default-app prompt rules:**
   - Shown **only** on the empty-state page, in a tab with no document, never over a document, never during a drop.
   - **Not shown** on the first time the empty state appears (the user may be about to open something). It appears the **second** time the empty state is seen on a different day, or the second launch **[design; Q10 asks whether to show it at all]**.
   - **Not shown** if MikePDF is already the default for `.pdf` (detected at launch, see 6), or if the user chose **Don't ask again**, or if an enterprise policy hides it.
   - **Not now** hides it for **30 days**, and it can be shown **at most 3 times in total**. After the third time it is only in Settings.
   - Shown at most once per session, and is part of the page (so it scrolls away with the recents).
5. **Open Windows settings** opens the Default apps page for `.pdf` (a settings URI; **[guess: the exact URI that targets `.pdf`, for example `ms-settings:defaultapps?registrytype=extension&target=.pdf`, must be verified on the supported Windows versions; fall back to `ms-settings:defaultapps`]**). MikePDF **cannot** set the default itself and says so ("Windows decides..."). The same button lives in Settings, Files and defaults, with the current status ("MikePDF is the default" / "Another app is the default").
6. **Detecting the current default:** the broker asks the shell for the effective handler of `.pdf` (**[guess: `IApplicationAssociationRegistration::QueryCurrentDefault`, or `AssocQueryString`; verify it works from a packaged app]**) and compares it to MikePDF's ProgId. If detection fails, treat it as "unknown": show the prompt per the rules above, and no status line in Settings.
7. **After the user returns** (window activation after the settings page opened): re-detect. If MikePDF is now the default, replace the prompt with "MikePDF is now your default PDF app." for 6 s, then remove it permanently. If not, show nothing and don't repeat (the rules above govern).
8. **Windows' own first-open chooser:** when a package registers a file association, Windows may show its own "Choose an app" prompt at the first double-click. That is Windows' UI, not MikePDF's; nothing to do except make sure our display name and icon in it are right (package manifest).
9. **Enterprise:** an administrator who sets the default through Intune or Group Policy sees no prompt: MikePDF detects it is the default. A policy to **hide the prompt** is proposed for the policy set (name not invented here; see [13](13-settings-and-policies.md) OPEN QUESTION 5).
10. **Opening a PDF from Explorer when MikePDF isn't the default** (right-click, Open with) behaves like any other open, with no prompt over the document.
11. **Privacy:** the prompt's state (shown count, snooze date) is stored locally with the settings; it never leaves the device.
12. **Offline:** nothing here needs a network.

## 5. States

| State | Behaviour |
|---|---|
| **Loading** | The page shows immediately. The prompt is added only after detection completes (a few ms), never causing a layout shift above it: it appears below the recents or the privacy line, in space reserved when it is expected |
| **Empty** | The empty state without recents (3.2) is the first-run page. The prompt is not shown on it the first time |
| **Error** | Detection fails: treated as "unknown" (rules in 4.6). The settings URI fails to open: the `InfoBar` says "Windows settings didn't open. Open Settings, then Apps, then Default apps, and choose MikePDF for .pdf." (three steps written plainly) |
| **Keyboard focus** | The prompt is reached in the empty page's Tab order **after** the recents. The first button is **Open Windows settings**. `Esc` on the prompt is **Not now**. Focus is never moved to it automatically |
| **High contrast** | An `InfoBar` in system colours with a 1 epx `WindowText` edge and an info glyph that is a shape plus the word "Information" in its accessible name |
| **225% text scale** | The prompt's text wraps, its buttons stack vertically, full width; no clipping |
| **Narrow window** | The prompt is full width of the page (minus 16 epx margins); buttons stack |
| **Touch** | Buttons are 44 epx high; the prompt is a normal element in the page, no swipe-to-dismiss (the **Don't ask again** button is the dismissal) |

## 6. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Prompt | InfoBar (Group) | "Information. Open PDFs with MikePDF by default?" | `DefaultAppPrompt` |
| Open settings | Button | "Open Windows settings" | `DefaultAppOpenSettings` |
| Not now | Button | "Not now" | `DefaultAppNotNow` |
| Don't ask again | Button | "Don't ask again" | `DefaultAppNever` |
| Settings row | Group | "Default PDF app. MikePDF is the default." / "Another app is the default." | `Setting_DefaultApp` |
| Privacy line | Text | "No account. No tracking. Your files stay on this device." | `EmptyPrivacyNote` |

Narrator, intended: the prompt does **not** announce itself when it appears (it is on a page the user hasn't focused, and it is not urgent). It is read when focus reaches it: "Information. Open PDFs with MikePDF by default? Windows decides which app opens a file, so you choose it in Settings. Open Windows settings, button." After returning with the default set: polite "MikePDF is now your default PDF app."

## 7. Keyboard path

| Task | Keys |
|---|---|
| Open the first file | `Ctrl+O` |
| Reach the prompt | `Tab` past the recents |
| Accept | `Enter` on **Open Windows settings** |
| Snooze | `Esc`, or **Not now** |
| Set later | `Ctrl+,`, Files and defaults |

## 8. Engine and protocol implications

None. The prompt is a UI and broker feature. The shell queries are not PDF parsing.

## 9. OPEN QUESTIONS

1. **Q10:** show the prompt at all, and when. This spec: the second time the empty state is seen, up to three times, snoozed 30 days, never over a document.
2. **Q9:** ask about opt-in diagnostics at first run? This spec: no. The risk is that nobody ever opts in. That is the privacy-first trade-off (§1.2 principle 4).
3. The settings **URI** that opens the Default apps page for `.pdf` needs verification.
4. Whether detecting the default handler works **from the package** (the packaged-app registry view) needs a test on a real install (STATUS lists the Store-signed and standard-user installs as unverified, R-41).
5. A policy to hide the prompt: name and default (13, open question 5).
