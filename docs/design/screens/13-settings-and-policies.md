# 13 Settings, enterprise policies and About

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 9 (IT deploys MikePDF with JavaScript disabled, AI local-only and telemetry off; policies are enforced and **visible in the About dialog**), 8 (offline: nothing in Settings needs a network)
- Spec: master prompt §7.7 (settings and policies), §14 (privacy), §10.3 (accessibility), §10.2 (remappable shortcuts)
- Milestone: M3. The About dialog showing policies is in the Wave 2 list of the roadmap.

## 1. Purpose

One place for every preference, honest about what is stored and where, with enterprise policies shown plainly: which settings are locked, by whom, and why.

## 2. Layout

### 2.1 Settings opens as a tab (`app.settings`, `Ctrl+,`)

```text
+--------------------------------------------------------------------------------------------+
|[M] Annual report.pdf | [gear] Settings  x | +                                              |
+--------------------------------------------------------------------------------------------+
| [search settings        ]|                                                                  |
|                          |  Appearance                                                      |
|  Appearance              |  App theme            [ Use system setting v ]                   |
|  Reading                 |  Page colours         [ Normal v ]    (o) Normal ( ) Dark ( ) Sepia|
|  Markup and pen          |  Touch-friendly spacing [ Automatic v ]                          |
|  Keyboard shortcuts      |  Compact spacing      [ off ]                                    |
|  Files and defaults      |                                                                  |
|  Privacy and data        |  Text size follows Windows. Change it in Settings, Accessibility.|
|  Enterprise policies (3) |  [Open Windows text size settings]                               |
|  Accessibility           |                                                                  |
|  About                   |                                                                  |
+--------------------------+------------------------------------------------------------------+
```

`NavigationView`, left pane, `PaneDisplayMode=Auto` (collapses to icons below 640 epx, to a top menu below 520 epx). The search box filters settings by name and description and jumps to the setting (the palette also finds settings, [07](07-command-palette.md)).

### 2.2 Sections and the settings in each

| Section | Settings (control) | Default |
|---|---|---|
| **Appearance** | App theme: Use system setting, Light, Dark (ComboBox). Page colours: Normal, Dark (invert), Sepia, High contrast pages (RadioButtons; the last needs E13). Touch-friendly spacing: Automatic, Always, Never. Compact spacing (ToggleSwitch) | System; Normal; Automatic; off |
| **Reading** | Default layout: scrolling (continuous, paged) and pages (single, two, two with cover). Use two pages automatically on wide windows (off). Remember position per document (on). Open the left pane on opening (Off, Thumbnails, Bookmarks, Last used). Reading progress line in reading mode (off). Show the selection toolbar on text selection (on). Smooth scrolling (on; follows Windows animation setting). | Continuous, single; off; on; Off; off; on; on |
| **Markup and pen** | Author name (text box; default: Windows display name; Q11). Pen writes ink by default (on if a pen digitizer is present, else off). Pen barrel button: Select / Erase / Highlighter / Nothing. Pen eraser end: Erase / Nothing. Palm rejection (on). Default colours per tool (swatches). Single-key tool shortcuts (off). | as stated |
| **Keyboard shortcuts** | Table of every command with its shortcut, a search box, **Change**, **Reset**, **Reset all**; **Export...** and **Import...** (JSON, §10.2 "remappable and exportable"). Shows conflicts. | Defaults in [commands.md](../commands.md) |
| **Files and defaults** | **Make MikePDF the default for .pdf** (opens Windows Default apps; shows whether MikePDF is currently the default). Open files from other programs in: the most recent window / a new window (Q13). Reopen the last session on startup (on). Show previews of recent files (on). **Clear recent files and previews**. External links: show the address (fixed, not a setting; written out here as a statement of behaviour). | as stated |
| **Privacy and data** | **Send diagnostics (opt-in):** off, with a plain explanation of what would be sent and what never would ([privacy.md](../../user/privacy.md)). **Clear usage history** (palette ranking). **Clear all MikePDF data**: recents, previews, journals, settings, with a confirmation. Where the data is stored (the folder path, with **Open folder**). | off |
| **Enterprise policies** | Read-only list of policies in effect ([section 2.4](#24-enterprise-policies)). The section **appears only when at least one policy is set** (or always, with "No policies are applied", **OPEN QUESTION**). | |
| **Accessibility** | Links to the Windows text size, high contrast and animation settings; **Announce "not tagged"** (on); **Page high contrast mode**; **Caret selection on open** (off); reduced motion follows Windows. | |
| **About** | Name, version, build, engine version and PDFium revision (`chromium/8125` today **[repo: STATUS]**), architecture, **Policies in effect (n)**, licences and notices, **Copy details**, privacy statement, "Open source: Apache-2.0" with the repository link (**opens the link in the system browser only after the user clicks; the link text shows its address**). | |

### 2.3 Locked setting

```text
 JavaScript in documents
 [ Off ]  (lock) Managed by your organization
          Your IT administrator turned this off. You can't change it.   [Details]
```

The control is disabled (but focusable, so the reason is reachable), has a lock glyph, and the explanation line is part of its accessible description.

### 2.4 Enterprise policies

```text
 Enterprise policies
 These settings come from your organization. They override your choices.   [ Copy as text ]

 Setting                       Value               Source
 JavaScript in documents       Off                 Local machine policy
 AI features                   Local only          Local machine policy
 Diagnostics                   Off                 Local machine policy
 Update channel                Stable              Local machine policy
 Engine memory limit           1 GB                Local machine policy
 Default save                  Incremental         Local machine policy
 External link behaviour       Show address        Local machine policy
 Allowed AI endpoints          (none)              Local machine policy

 Registry key: HKLM\Software\Policies\MikePDF        (Q3: the spec says "Quire")
```

### 2.5 About dialog (also reachable from the palette, `app.about`)

```text
+----------------------------------------------------+
| [icon]  MikePDF                                    |
|  Version 1.0.0 (build 1234), x64                   |
|  Engine: PDFium chromium/8125                      |
|                                                    |
|  Policies in effect: 3   [ Show ]                  |
|  No telemetry. No account.                         |
|                                                    |
|  [ Copy details ]   [ Licences ]   [ Privacy ]     |
|                                          [ Close ] |
+----------------------------------------------------+
```

**Show** opens the Enterprise policies page in Settings. **Copy details** puts the version, build, architecture, engine, OS version and the effective policies on the clipboard as text, for support requests. It contains no document names, no paths and no user name.

## 3. Behaviour

1. **Where it lives.** Settings is a **singleton tab** (`Ctrl+,`): opening it again activates it. It is part of the window's session (restored). Changes **apply immediately** (ToggleSwitches and drop-downs), with no Save or Apply button, and there's no "restart required" for anything in v1.0. If a setting does need a restart, it's labelled and offers **Restart** (none known).
2. **Storage.** Settings are stored in the packaged app's local settings, and mirrored to a JSON export for backup (§7.7). **Export settings...** and **Import settings...** in the About section handle that file. An imported file is **validated** (known keys, ranges, lengths) and unknown keys are ignored; policies override imported values.
3. **Policies** (§7.7). A policy in the registry **wins** over the user's value and over an imported file. The policy list (2.4) shows each in effect with its value and source. Policies **read at launch and on change** (the app watches the key). The minimum set: JavaScript enablement, external link behaviour, AI features (off, local-only, allowed endpoints), telemetry, update channel, default save behaviour, engine memory limit (§7.7). **The Settings UI doesn't invent policy names**: they're listed in the admin documentation, which the About page links to.
4. **A locked control** is disabled with a lock glyph, a reason line, and `HelpText` ("Managed by your organization. The value can't be changed here."). A locked value that equals the default is still shown locked (the admin chose it).
5. **Invalid policy values** (a typo in the registry, a value out of range): the app **ignores** the bad value, uses the default, and lists it in the policies view as "Not applied: the value isn't valid" **[design]**. It never fails to start because of policy.
6. **Keyboard shortcuts page.** The table lists commands in groups (File, Navigate, View, Find, Markup, Forms, Pages, Window). Selecting a row and pressing `Enter` or **Change** opens the "Press the new keys" dialog (same as in the palette). The dialog:
   - Records the next chord pressed; shows it.
   - Warns about a **conflict** and offers **Replace** (the other command loses the shortcut and is listed) or **Cancel**.
   - Refuses chords that the system reserves (`Alt+Tab`, `Ctrl+Alt+Del`, `Win+...`, `Alt+F4`) with a message.
   - Allows a **single plain key** (a letter or a digit) **only** when "Single-key tool shortcuts" is on (G-KB4).
   - Offers a second shortcut for a command (up to two per command).
   **Export** writes a JSON of changed bindings only.
7. **Search settings** matches the setting's name and description and highlights the match. No result: "No settings match '{text}'."
8. **Reset.** Each section has **Reset section to defaults**. A single **Reset all settings** is in Privacy and data with confirmation. Reset never removes policies, recents, or documents.
9. **No online anything.** Settings never contacts a server. There is no "check for updates" button in v1.0 beyond what App Installer or the Store provide (ADR-0015); the About section states "Updates come through the Microsoft Store." when installed from the Store.
10. **Diagnostics (opt-in)** has an explanation: what is sent (crash dumps with document content excluded, anonymous counters, feature usage counts), what never is (file names, paths, text, AI prompts), where it goes (**OPEN: ADR-0014 not written**). The toggle's description repeats this in full, so it isn't hidden behind a link. If a policy forces it off, the control is locked.
11. **AI settings** don't appear in v1.0. When they arrive (M7): provider choice, local-only default, with the policy-locked states already designed above.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | The page appears at once from local data. The default-app status ("MikePDF is the default PDF app" or not) and the stored-data size load asynchronously: "Checking..." in secondary text and then the value (polite). Policy values are read at startup, so locks are correct from the first paint |
| **Empty** | Enterprise policies: when none apply, the section shows "No policies are applied." (and the navigation item doesn't show a count). The keyboard table with a search that finds nothing: "No commands match." Recents: "Nothing to clear." on the clear button's description |
| **Error** | A setting that can't be saved (disk full, store locked): an `InfoBar` in the page "That setting couldn't be saved. It will be used until you close MikePDF." with **Try again**. A corrupt settings file is renamed and defaults are used; the Privacy section shows a one-time "Your settings file couldn't be read, so defaults are in use. The old file was kept as settings.json.bad." An invalid import: "That file isn't a MikePDF settings file." with the first reason |
| **Keyboard focus** | The page's landmarks: navigation (arrows move; `Enter` opens a section), search box, then the content in reading order. Locked controls are focusable (see 2.3). `Ctrl+F` in Settings focuses the settings search box. Standard focus visuals. After closing the Shortcut dialog focus returns to its row |
| **High contrast** | System colours throughout; locks and glyphs are `WindowText`; toggles use the system's high-contrast switch visuals; the keyboard table uses text for conflicts ("Conflict: Ctrl+K is used by ..."), not colour |
| **225% text scale** | All rows wrap; the navigation pane collapses to icons at 640 epx effective width, which is reached sooner; descriptions grow; ToggleSwitch labels wrap to their own line; tables (keyboard, policies) reflow into stacked rows (label over value over source). Nothing is clipped |
| **Narrow window** | `NavigationView` goes to a top menu below 520 epx (`PaneDisplayMode=Top`) or a hamburger **only here**, where it is the standard pattern, since Settings is a page. The keyboard table becomes a list with the shortcut under each name. About stays a dialog |
| **Touch** | Rows 48 epx high; toggles 44 epx hit; the shortcut recording dialog accepts a touch keyboard only through its **Type it** button (a text box where the user types "Ctrl+Shift+K"), because a chord can't be pressed on a glass keyboard **[design]** |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Settings tab | TabItem | "Settings" | `SettingsTab` |
| Navigation | Tree or List (NavigationView) | "Settings sections" | `SettingsNav` |
| Section | Group | Section name | `Settings_{section}` |
| Setting | The control with a label and description | "{Setting name}. {description}" | `Setting_{key}` |
| Locked setting | Same control, disabled | "{name}. Locked. Managed by your organization." | `Setting_{key}` |
| Search | Edit | "Search settings" | `SettingsSearch` |
| Keyboard table | DataGrid (List with columns) | "Keyboard shortcuts" | `ShortcutTable` |
| Shortcut row | Item | "{command}, {shortcut}" | `Shortcut_{commandId}` |
| Change dialog | Dialog | "Change shortcut for {command}" | `ShortcutDialog` |
| Policies list | List | "Enterprise policies" | `PolicyList` |
| Policy row | Item | "{setting}: {value}. Source: {source}" | `Policy_{key}` |
| About dialog | Dialog | "About MikePDF" | `AboutDialog` |

Narrator, intended: "Settings, tab. Appearance. App theme, combo box, Use system setting." Locked: "JavaScript in documents, off. Locked. Managed by your organization." Policy list: "Enterprise policies, list, 8 items. JavaScript in documents: Off. Source: local machine policy." Changing a toggle: "On" (the standard). Reset: "Section reset to defaults."

## 6. Keyboard path

| Task | Keys |
|---|---|
| Open Settings | `Ctrl+,` |
| Move between sections | Arrow keys in the navigation, `Enter` |
| Search | `Ctrl+F` |
| Change a shortcut | Table, select the row, `Enter`, press the chord, `Enter` |
| See policies | Navigate to **Enterprise policies**; or About, **Show** |
| Close Settings | `Ctrl+W` |

## 7. Engine and protocol implications

- **Policy to engine:** **JavaScript** and **engine memory limit** reach the engine host through the broker at launch (the separate engine flavour with V8 for JS, §5.3; the Job Object limit, §5.2 rule 5). The engine does not read the registry, because the sandbox gives it no access.
- **External link behaviour**, **AI** and **telemetry** are UI-process and broker concerns only.
- **Default save behaviour** (incremental or full) is passed to the save pipeline ([ADR-0008](../../adr/0008-save-pipeline.md)).
- The About dialog's PDFium revision comes from the engine's `Hello` reply (§5.4).

## 8. OPEN QUESTIONS

1. **Q3:** the registry key. §7.7 says `HKLM\Software\Policies\Quire`. After ADR-0016 it should be `MikePDF`; the admin documentation and ADMX templates (P2) use the final name. Confirm, and decide whether to also read the old name for early adopters (this spec: no, there are none).
2. **Q13:** the default for "open files from other programs in": a tab in the most recent window (§7.6).
3. The **Enterprise policies** section: always visible, or only when a policy applies? This spec says only when one applies, with About always showing "Policies in effect: n".
4. **Page colours: High contrast pages** needs an engine render option (README E13).
5. **ADMX/ADML templates** are P2 (§2.3). The Settings UI should match the policy names, so write the names down before M3 ends.
6. **Clear all MikePDF data** is destructive. A confirmation dialog states what is removed; the journal is included, so unsaved recovery data is lost: the dialog says so.
