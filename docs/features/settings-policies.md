# Feature spec: settings and enterprise policies

- Status: Draft for Opus review
- Milestone: M3 (full reader). Later specs add their own keys to the registry defined here
- Master prompt: §7.7, §2.1 (settings are implied by many P0 items), §10.4, §13.1 (links), §14, §18 scenario 9 (enterprise), ADR-0004 amendment A2 (where data may be stored)
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: nothing. **Almost every other spec depends on this one** for its settings and policy keys
- Related: [command-palette-shortcuts](command-palette-shortcuts.md) (key bindings are stored with settings and exported with them)

Conventions are in [README.md](README.md).

## 1. Where the code is today

There are no settings. `DocumentStore` writes `LocalState\private\documents.json`. Behaviours that a user or an admin will want to control are constants in code: the 20-entry recent list, the external-link behaviour (show the address and offer to copy, open nothing), the 2 GB engine memory limit. There is no policy reader and no About dialog.

## 2. User stories

1. As a user, I change the theme or the default zoom once and every window follows immediately.
2. As a user, I back up my settings and shortcuts to a file and restore them on another PC.
3. As an IT admin deploying through Intune, I set JavaScript off, AI to local-only and telemetry off, and users cannot change those.
4. As a user on a managed PC, I can see which settings my organisation controls and why a control is disabled.
5. As an admin, I open About on a test machine and see exactly which policies the app received and what they resolved to (§18 scenario 9).
6. As a privacy-minded user, I can clear my history and my recovery data from Settings.

## 3. Detailed behaviour

### 3.1 Storage

- File: `LocalState\private\settings.json`. **Not** `ApplicationData.LocalSettings`. ADR-0004 amendment A2 says "anything the broker must keep from the engine goes in `LocalState\private`; nothing else may be written to package data", because the sandboxed engine holds the package's own capability and can read and write the rest of the data folder. §7.7's wording, "stored in packaged app local settings", conflicts with that rule. This spec follows A2 and marks OPEN QUESTION OQ-SET-3 for Opus to confirm that A2 overrides §7.7.
- Format: `{ "schemaVersion": 1, "values": { "<key>": <value>, ... }, "keybindings": [...] }`, UTF-8, written atomically (temp file and move, like `DocumentStore`), debounced 500 ms after the last change.
- Load: a missing file means defaults. A file that fails to parse is renamed `settings.corrupt-<timestamp>.json` (keep the last 3), defaults are used, and one non-blocking notice is shown. Per key: wrong type, unknown enum value or out-of-range value falls back to the default for that key only and is logged; one bad key never discards the others. Unknown keys are **kept** on rewrite (so a downgrade does not erase newer settings), up to 256 keys and 1 MiB total.
- Settings are defined in code as a registry of `SettingDefinition` records: key, type, default, range or allowed values, whether it is exported, whether a restart is needed, and the policy that can lock it (if any). Everything else (UI, export, import, docs, tests) is generated from the registry.

### 3.2 Resolution order and live change

`Effective(key)` returns the value, its source (`Default`, `User`, `Policy`) and `Locked`. **Policy beats user beats default.** Reading is done at point of use, not cached for a session, so a change of either layer takes effect: user changes immediately in all windows (a `SettingsChanged` event with the key list), policy within 5 seconds of a registry change (3.4).

### 3.3 The setting registry (v1)

Keys added by other specs are listed here so that nothing collides. A key that says "owner: X" is defined by that spec.

| Key | Type | Default | Range / values | Owner |
|---|---|---|---|---|
| `general.theme` | enum | System | System, Light, Dark | this |
| `general.language` | enum | System | System, en-US, en-AU, en-GB | this |
| `startup.restore` | enum | per OQ-TAB-1 | Always, Ask, Never | tabs |
| `windows.openFromShell` | enum | LastActiveWindow | LastActiveWindow, NewWindow, ActiveWindowOfThisDesktop | tabs |
| `engines.maxLiveTabs` | int | 6 | 1 to 32 (policy `MaxLiveTabs`) | tabs |
| `engines.suspendAfterMinutes` | int | 10 | 1 to 240 | tabs |
| `privacy.addToWindowsRecent` | bool | true | | file association |
| `recent.maxItems` | int | 20 | 0 to 50 (0 turns the list off) | this |
| `view.defaultArrangement` | enum | Single | Single, Facing, FacingWithCover | page modes |
| `view.defaultFlow` | enum | Continuous | Continuous, Paged | page modes |
| `view.defaultFit` | enum | None | None, Width, Page | this |
| `view.defaultZoom` | double | 1.0 | 0.1 to 64 | this |
| `view.restorePosition` | bool | true | | this |
| `view.smoothScrolling` | bool | true | follows the OS "Animation effects" setting when false is not chosen | this |
| `links.behaviour` | enum | OQ-SET-1 | Block, ShowAddress, Confirm | this (policy `ExternalLinks`) |
| `save.mode` | enum | InPlace | InPlace, AlwaysSaveAsCopy | save pipeline (policy `SaveMode`) |
| `save.warnSignedChanges` | bool | true | | save pipeline |
| `markup.authorName` | string | "" (use the Windows account display name) | up to 128 characters | annotation core |
| `markup.keepToolActive` | bool | false | | text markup |
| `markup.toolDefaults` | object | built-in | colour, opacity, width, font per tool, validated by each annotation spec | annotation specs |
| `pen.mode` | bool | false | | ink |
| `pen.palmRejection` | bool | true | | ink |
| `pen.barrelAction` | enum | OQ-INK-4 | | ink |
| `pen.eraserEndAction` | enum | Eraser | | ink |
| `pen.pressureCurve` | enum | Linear | Linear, Soft, Firm | ink |
| `forms.highlightFields` | bool | true | | forms |
| `print.scaling`, `print.annotations`, `print.formFields`, `print.autoRotate`, `print.asImage` | various | see print | last used values | print |
| `shell.thumbnails`, `shell.preview` | bool | true | | shell |
| `stamps.library` | list | empty | up to 50 entries, 20 MiB total | stamps |
| `defaultApp.promptState` | object | | not exported | file association (kept in `prompts.json`, listed for completeness) |

Not settings: recent files, last positions, session, closed-tab stack, prompts counters, edit journals. These are history and recovery data, kept in their own files, not exported with settings.

### 3.4 Enterprise policies

**Location.** `HKLM\Software\Policies\MikePDF` (the project was Quire when §7.7 was written; the key name follows the product name, ADR-0016). Read by the broker process only; the engines never read policy. Values are `REG_DWORD`, `REG_SZ` or `REG_MULTI_SZ`.

**Why HKLM only.** The threat model's boundary B7 says policy is read from HKLM so that a standard user cannot weaken it. `HKCU\Software\Policies` is where user-scope Intune and Group Policy settings land. OPEN QUESTION OQ-SET-2: whether to read HKCU as well. If yes, HKCU may only make behaviour **stricter** than HKLM, never looser, and only for the keys flagged "user-scope allowed" below.

**Packaged-app note.** MSIX registry virtualisation affects writes under HKLM\Software and the app's own HKCU hive; reads of real HKLM values work. An integration test in the installed package proves this for the keys in the table, because an assumption here would silently disable every policy.

**Reading.** A `PolicyProvider` reads all keys on start, then watches the key with `RegNotifyChangeKeyValue` and re-reads on change (debounced to 1 second, so effective within 5 seconds). Missing key or value means "not configured". A value of the wrong type or outside its range is **invalid**: it is ignored, listed as invalid in About, and the setting behaves as not configured, **except** for restrictive policies whose purpose is to block (rows marked "fail closed"), where an invalid value gives the most restrictive behaviour.

**The policies.**

| Value name | Type | Allowed values | Absent means | Invalid means | Effect |
|---|---|---|---|---|---|
| `JavaScriptEnabled` | DWORD | 0, 1 | 0 | 0 (fail closed) | Reserved for the V8 engine flavour (§5.3). The v1 builds do not contain V8, so 1 has **no effect**; About says "JavaScript: not available in this build". Document-level script banners (forms) still show. |
| `ExternalLinks` | DWORD | 0 block, 1 show address only, 2 confirm then open | OQ-SET-1 | 0 (fail closed) | Behaviour when a user activates an external link. Locks `links.behaviour`. File and UNC links are always blocked and are not policy-controllable in v1. |
| `AllowedLinkSchemes` | MULTI_SZ | each entry `[a-z][a-z0-9+.-]{0,31}`, at most 16 entries; `file` is ignored | `https`, `http`, `mailto` | default list | Which schemes may be offered or opened at all. |
| `AiMode` | DWORD | 0 off, 1 local only, 2 local and cloud | not configured | 0 (fail closed) | **Reserved until M7.** Read and shown in About now so scenario 9 can be verified before AI ships. |
| `AiAllowedEndpoints` | MULTI_SZ | absolute `https` URLs, at most 16, 1024 characters each | none | none | Reserved until M7. |
| `TelemetryEnabled` | DWORD | 0, 1 | 0 | 0 (fail closed) | The v1 build has no telemetry code. About shows "Telemetry: off (this build has none)" whatever the value. If opt-in diagnostics are added (ADR-0014), 0 forces them off. |
| `SaveMode` | DWORD | 0 in place, 1 always save as a copy | 0 | 0 | 1 makes Save open Save As with the suggested name `name (edited).pdf`; the original is never overwritten. Locks `save.mode`. |
| `EngineMemoryLimitMB` | DWORD | 256 to 8192 | 2048 | 2048 | Job Object memory limit per engine (§5.2 rule 5). Read when an engine starts. |
| `MaxLiveTabs` | DWORD | 1 to 32 | 6 | 6 | See [tabs-windows-session](tabs-windows-session.md). |
| `PrintingEnabled` | DWORD | 0, 1 | 1 | 1 | 0 disables Print everywhere ([print](print.md)). |
| `ShellThumbnailsEnabled`, `ShellPreviewEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | Read by the shell handler itself ([shell-extensions](shell-extensions.md)). |
| `DefaultAppPromptEnabled` | DWORD | 0, 1 | 1 | 0 | 0 hides the default-app prompt. |
| `SettingsImportExportEnabled` | DWORD | 0, 1 | 1 | 0 (fail closed) | 0 hides settings and shortcut import and export (prevents copying settings out of a managed machine). |
| `AnnotationAuthorName` | SZ | 1 to 128 characters | not configured | not configured | Fixes the author written into new annotations and locks `markup.authorName`. For organisations that want a directory name instead of the display name. |
| `AllowWeakerSandbox` | DWORD | 0 | 0 | 0 | **Not implemented, deliberately.** ADR-0004 decision 5 says an enterprise policy "may allow" the low-integrity fallback. S3 showed that fallback fails the bar. There is no code path for it; the value is read only so About can report "ignored". OPEN QUESTION OQ-SET-4 asks Opus to remove it from the contract or keep it reserved. |
| `UpdateChannel` | DWORD | 0 | n/a | n/a | **No effect.** Distribution is through the Microsoft Store (ADR-0015); updates are Windows' business. §7.7 lists an update channel as controllable. OQ-SET-4 asks to drop it from the admin documentation. |

Every policy has a row in `docs/admin/policies.md` (an M5 deliverable), generated from the registry. ADMX/ADML templates are P2 (§2.3); the registry layout above is the contract they will use.

### 3.5 What the user sees when a setting is locked

A locked control is disabled, shows a lock glyph and the text "Managed by your organisation", and exposes the same text as UIA `HelpText`. The palette command for a locked setting is disabled with the same reason. A setting changed by policy while the app is running updates in place.

### 3.6 About

A dialog with product name, version (`AppPaths.Version`), engine and PDFium versions (from `Hello`), licence link, privacy link, and a **Managed by your organisation** section. The section appears only when at least one policy value exists. For each value present it lists: friendly name, the value as received, the effective result in words, and the source ("Registry policy, machine"). Invalid values are listed with "Ignored: not a valid value". Reserved or ineffective policies say so ("Reserved for a future version", "Not available in this build"). A button copies this report as plain text (for support). No paths or user data are in it.

The text is produced by `PolicyReport` in `MikePDF.Core`, which the dialog only displays, so the output is unit-testable.

### 3.7 Export, import, reset, clear

- **Export** settings and shortcuts to a file chosen with the file picker. Name `MikePDF-settings.json`. Contents: `format: "mikepdf-settings"`, `schemaVersion`, the exportable user values, the key bindings. **Not** exported: policy-locked values, history, recovery data, prompts. The author name is exported and the dialog says so.
- **Import**: size limit 256 KiB; JSON depth limit 8; no comments; keys checked against the registry. Unknown keys are reported and skipped. Type or range errors are reported and skipped (never silently clamped). Locked keys are reported as "can't be changed". A summary is shown (for example "23 settings will change, 2 can't be imported because your organisation controls them, 1 isn't recognised") and nothing changes until the user confirms. Apply is all-or-nothing: the previous file is first copied to `settings.prev.json`, which "Undo import" restores until the next import. Policy `SettingsImportExportEnabled=0` hides both.
- **Reset all settings**: confirm, then writes defaults (locked values are unaffected). Does not touch history or recovery data.
- **Clear history**: recent files, positions, session, closed tabs, prompt counters. Confirm.
- **Clear recovery data**: deletes edit journals ([save-pipeline](save-pipeline.md)). The dialog first lists how many documents have unsaved changes recorded and requires an explicit confirmation, because this deletes user work.

### 3.8 Language

`general.language` sets `ApplicationLanguages.PrimaryLanguageOverride` (restart not needed if the strings can be reloaded; otherwise "takes effect next time MikePDF starts" is shown). English variants en-US, en-AU and en-GB ship at launch (§2.1): they differ in spelling (colour, grey, organise, programme), date and number formats, and paper size defaults (Letter versus A4). OPEN QUESTION OQ-SET-6: whether en-AU and en-GB share one resource set derived from a neutral `en` plus overrides, or three full copies. This affects the localisation build, not behaviour. Default paper size for new blank pages follows the region (A4 for en-AU and en-GB, Letter for en-US).

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Open settings | Ctrl+, | `app.settings` |
| About | none | `app.about` |
| Export / import settings | none | `settings.export`, `settings.import` |
| Reset all settings | none | `settings.reset` |
| Clear history | none | `settings.clearHistory` |

Pen and touch: standard WinUI controls with 40 x 40 DIP targets. Toggles and sliders work with a finger. A settings search box is OPEN QUESTION OQ-SET-5 (the command palette could search settings by name, which would remove the need).

## 5. Accessibility

- Settings is one page with grouped sections. Each section is a UIA `Group` with a heading (`AutomationProperties.HeadingLevel`). Every control has a name from its label and a description from its helper text.
- Order of tab stops equals visual order. Locked controls remain focusable (a disabled control that cannot be focused would hide the reason from a keyboard user); they announce "disabled, managed by your organisation".
- Changes that apply instantly do not announce themselves, except theme and language, which announce "Theme changed to Dark" politely.
- Import and export results are in a live region, plus a dialog for the confirmation summary. The summary is text, not a table of icons.
- About's policy section is a list with each item readable as one line "Name: value. Effect." Copy button announces "Copied".
- Text scaling to 225% and high contrast apply to Settings and About. No setting is conveyed by colour only.

## 6. Error states

| Situation | Behaviour |
|---|---|
| `settings.json` unreadable or corrupt | Section 3.1: renamed, defaults, one notice. |
| Write fails (disk full, access denied) | The setting still applies in memory; a banner says "MikePDF couldn't save your settings." with Retry. |
| Policy read fails (registry error) | Treated as "not configured" for non-fail-closed keys; for fail-closed keys the most restrictive behaviour applies; About reports "Policy couldn't be read". |
| Import file invalid | Dialog explains (not a MikePDF settings file, too large, version too new) and changes nothing. |
| Import with a version newer than supported | Reports and applies only the known keys after confirmation. |
| Language resources missing | Falls back to en-US and logs. |

## 7. Edge cases

- **Rotated pages, CropBox, encryption, permissions:** not applicable. (Some settings, such as defaults for view and print, apply to documents in general.)
- **Policy changes while a dialog is open:** the dialog's controls update after it closes or re-render if safe.
- **Two registry values for one policy, wrong case, extra whitespace in strings:** names are matched case-insensitively (as the registry does); string values are trimmed; values over their length cap are invalid.
- **Locale-dependent parsing:** `view.defaultZoom` and other numbers are stored invariantly in JSON and displayed per the user's regional settings.
- **Huge or hostile settings file:** size cap 1 MiB, depth cap 8, key cap 256; a 100 MB file is rejected before parsing.
- **Hostile policy values:** `MULTI_SZ` with 100,000 entries, strings of 10 MB, binary values: capped by entry count and byte length before use.
- **Standard user versus admin:** reading HKLM needs no elevation.
- **Settings keys added in a later version** appear in an old build's file; old builds preserve them.
- **Concurrency:** one process owns the file. If a second instance somehow runs (development builds), the last writer wins and the file is never half-written.

## 8. Undo and redo

Settings changes are not document edits and are not on the undo stack. "Undo import" exists for the one destructive bulk action. Reset all settings has a confirmation instead of undo.

## 9. Journal entries

None. (`clear recovery data` deletes journals, which are specified in [save-pipeline](save-pipeline.md).)

## 10. Save behaviour

`settings.json` is written atomically and never as part of a document save. `save.mode` / `SaveMode` is read by the save pipeline.

## 11. Engine and protocol additions

None. The one engine-visible policy, `EngineMemoryLimitMB`, is applied by the broker when it creates the Job Object, and `maxEngines` by the broker; neither is a message.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `Settings_Title` | Settings |
| `Settings_Section_General` / `_Reading` / `_Markup` / `_Pen` / `_Forms` / `_Printing` / `_Privacy` / `_Shortcuts` / `_Advanced` | General / Reading / Markup / Pen and touch / Forms / Printing / Privacy and security / Shortcuts / Advanced |
| `Settings_Theme` | Theme |
| `Settings_Language` | Language |
| `Settings_Locked` | Managed by your organisation |
| `Settings_Locked_Description` | {0} is controlled by your organisation and can't be changed here. |
| `Settings_Theme_Announce` | Theme changed to {0} |
| `Settings_SaveFailed` | MikePDF couldn't save your settings. |
| `Settings_Corrupt_Notice` | Your settings file was damaged, so MikePDF started with the defaults. A copy was kept. |
| `Settings_Export` / `_Import` / `_Reset` | Export settings / Import settings / Reset all settings |
| `Settings_Export_AuthorNote` | The file includes your annotation author name. |
| `Settings_Import_Summary` | {0} will change, {1} can't be imported because your organisation controls them, {2} aren't recognised. |
| `Settings_Import_Confirm` | Import |
| `Settings_Import_Invalid` | That isn't a MikePDF settings file, or it's too large. |
| `Settings_Import_Undo` | Undo import |
| `Settings_Reset_Confirm` | Reset every setting to its default? Your history and unsaved-change backups aren't affected. |
| `Settings_ClearHistory` | Clear history |
| `Settings_ClearRecovery` | Clear recovery data |
| `Settings_ClearRecovery_Warn_One` | 1 document has unsaved changes backed up. Deleting recovery data discards them. |
| `Settings_ClearRecovery_Warn_Other` | {0} documents have unsaved changes backed up. Deleting recovery data discards them. |
| `About_Title` | About MikePDF |
| `About_Version` | Version {0} |
| `About_Engine` | PDF engine {0} |
| `About_Managed_Header` | Managed by your organisation |
| `About_Policy_Line` | {0}: {1}. {2} |
| `About_Policy_Invalid` | Ignored: not a valid value |
| `About_Policy_Reserved` | Reserved for a future version |
| `About_Policy_NotInBuild` | Not available in this build |
| `About_JavaScript_Off` | JavaScript: off |
| `About_Telemetry_Off` | Telemetry: off (this build has none) |
| `About_Ai_LocalOnly` | AI: local only |
| `About_Copy` | Copy details |
| `About_Copied` | Copied |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-SET-1 | Default for external links in v1.0: show the address only (today's Store-preview behaviour) or confirm and open (§13.1)? | User (product), Opus (security) |
| OQ-SET-2 | Read `HKCU\Software\Policies\MikePDF` as well, stricter-only? | Opus (security) |
| OQ-SET-3 | Confirm ADR-0004 A2 overrides §7.7's "local settings" wording. | Opus |
| OQ-SET-4 | Drop `UpdateChannel` and `AllowWeakerSandbox` from the contract, or keep them reserved? | Opus |
| OQ-SET-5 | A search box in Settings, or rely on the command palette. | Design |
| OQ-SET-6 | Structure of en-US, en-AU, en-GB resources. | Opus / localisation |
| OQ-SET-7 | Should the policy contract be frozen now (it will be quoted in the ADMX in P2)? | Opus |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| SET-01 | Registry: every key has a unique name, type, default, range and an owner; defaults satisfy their own ranges (property test over the registry). | Unit (`MikePDF.Core.Tests`) |
| SET-02 | Resolution: for each setting with a policy, the 3x3 matrix of (policy absent, valid, invalid) x (user unset, set) yields the specified value, source and lock flag. | Unit |
| SET-03 | Fail-closed policies (`JavaScriptEnabled`, `ExternalLinks`, `AiMode`, `TelemetryEnabled`, `ShellThumbnailsEnabled`, `ShellPreviewEnabled`, `SettingsImportExportEnabled`) give the restrictive result for wrong-typed, out-of-range and unreadable values. | Unit |
| SET-04 | `settings.json` round-trips; written atomically (killing the writer between temp write and move leaves the old file readable); unknown keys survive a rewrite; caps (1 MiB, 256 keys, depth 8) hold. | Unit + Integration |
| SET-05 | Corrupt, truncated, empty, BOM-prefixed and huge files give defaults, keep a `.corrupt` copy (max 3), and show the notice once. A bad value in one key does not reset other keys. | Unit |
| SET-06 | `SettingsChanged` fires once per applied change with the right key list; a second window sees it; no event for a no-op write. | Unit |
| SET-07 | Policy reader against a fake registry: reads DWORD, SZ and MULTI_SZ, applies caps (entry count, length), trims strings, matches names case-insensitively. | Unit |
| SET-08 | Real registry: writing `HKLM\Software\Policies\MikePDF\JavaScriptEnabled` etc. is seen by the **installed package** (proves registry virtualisation does not hide it) and a change is effective in <= 5 s without restart. Needs elevation on the runner and cleans up. | Integration (packaged, admin) |
| SET-09 | `EngineMemoryLimitMB` is applied to a newly started engine's Job Object (query `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`), and out-of-range falls back to 2048. | Integration |
| SET-10 | `PolicyReport` text for the scenario-9 set (JavaScript 0, `AiMode` 1, telemetry 0) names all three with effect and source, and flags a deliberately invalid fourth value. | Unit |
| SET-11 | Scenario 9 end to end: with those three policies set in HKLM, About shows the three lines, JavaScript-dependent UI is off, the AI entry says local only, and the telemetry line says off. | UI journey |
| SET-12 | Locked control: disabled, shows the lock text, `HelpText` is "Managed by your organisation", remains keyboard focusable, palette command disabled with the same reason (UIA check). | UI journey (a11y) |
| SET-13 | Setting a policy while the app runs updates the control and the effective behaviour in place within 5 s. | Integration |
| SET-14 | Export contains only exportable user values and key bindings, excludes locked values, history and prompts; the author-name note is shown. | Unit |
| SET-15 | Import preview counts (changed, locked, unknown, invalid) are exact for a crafted file; nothing changes before confirmation; confirmation applies all; cancel applies none. | Unit + UI journey |
| SET-16 | Import rejects: > 256 KiB, depth > 8, wrong `format`, newer `schemaVersion` (applies known keys after confirm), wrong types, out-of-range values (reported, skipped, not clamped). | Unit |
| SET-17 | "Undo import" restores `settings.prev.json` exactly; it is gone after the next import. | Unit |
| SET-18 | `SettingsImportExportEnabled=0` hides export, import and the palette commands. | Unit + UI journey |
| SET-19 | Reset all settings restores defaults for unlocked keys, leaves locked keys and history unchanged. | Unit |
| SET-20 | Clear history removes recent files, positions, session, closed-tab stack and prompt counters, and nothing else; clear recovery data lists the document count and deletes journals only after confirmation. | Integration |
| SET-21 | `ExternalLinks` policy values 0, 1, 2 produce block, show-address and confirm behaviours on an external link activation; `file:` and UNC links are blocked for all three. | Integration |
| SET-22 | `SaveMode=1` makes Save open Save As with the suggested name and never write to the original path. | Integration (after M4) |
| SET-23 | `PrintingEnabled=0` disables Print in menu, shortcut and palette. | UI journey |
| SET-24 | `AnnotationAuthorName` overrides the author in new annotations and locks the setting. | Integration (after M4) |
| SET-25 | `docs/admin/policies.md` lists exactly the policy names in the registry (a lint test fails if they differ). | Unit (docs lint) |
| SET-26 | Theme and language: changing either applies without restart (or shows the restart note for language if resources cannot reload), and announces the change once. | UI journey |
| SET-27 | Privacy test: `settings.json` contains no document paths or text; `PrivacyTests` is extended to scan it. | Unit |
| SET-28 | Nothing is written to package data outside `LocalState\private` by this feature (scan of the data folder after a full settings session; `ApplicationData.LocalSettings` is empty). | Integration (packaged) |
| SET-29 | High contrast and 225% text scale: Settings and About remain usable (screenshot compare at both). | UI journey (visual) |
| SET-30 | Hostile policy data (100,000-entry MULTI_SZ, 10 MB string, binary where DWORD expected) does not slow startup by more than 50 ms or allocate beyond the caps. | Integration |
