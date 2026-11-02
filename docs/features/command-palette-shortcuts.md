# Feature spec: the command palette and remappable, exportable shortcuts

- Status: Draft for Opus review
- Milestone: M3 (full reader). The master prompt places the palette in P1 (§2.2) and M6 (§17); the user asked for it in M3 together with remappable shortcuts. **Scope decision for the user:** OPEN QUESTION OQ-CMD-1
- Master prompt: §1.2 principle 6 (keyboard-complete), §7.3 (every action is a command), §10.2 (keyboard table; "all shortcuts are remappable and exportable"), §2.2
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: [settings-policies](settings-policies.md) (storage, export, policy)
- **Everything else depends on this one**: every feature spec names its commands by id here, and the README sequences the command registry first.

Conventions are in [README.md](README.md).

## 1. Where the code is today

Shortcuts are static `KeyboardAccelerator`s in `MainWindow.xaml` (Ctrl+O, Ctrl+G, Ctrl+F, F3, Shift+F3, F4, Ctrl+0/1/2, Ctrl+plus/minus including OEM keys set in code behind with the US-layout virtual-key numbers 187 and 189, rotate). Each calls a handler directly. There is no command abstraction, no enablement predicate, no localised command name, no palette. Buttons carry their own `x:Uid` text and `AutomationId`. The W1-reader work package will add more accelerators for the §10.2 rows that a reader needs; this feature replaces all of them with one registry (section 3.8 covers the migration).

## 2. User stories

1. As a keyboard user, I press Ctrl+Shift+P, type "rot", and run "Rotate page right" without finding it in a menu.
2. As a new user, the palette shows me the shortcut next to each command, so I learn them.
3. As a power user who comes from another reader, I rebind Highlight to a key I already know, and I'm warned if it clashes.
4. As a user with two PCs, I export my shortcuts on one and import them on the other.
5. As a screen reader user, I hear the shortcut of every button, and the palette reads each result and why a command is unavailable.
6. As an IT admin, commands that policy turns off (Print, for example) show as unavailable with the reason.

## 3. Detailed behaviour

### 3.1 The command

A command is an object registered at startup:

| Field | Meaning |
|---|---|
| `Id` | Stable string, dotted, lower camel case (for example `view.zoomIn`). Never reused, never renamed (users' bindings refer to it). |
| `NameKey` | Resource key for the localised name shown everywhere ("Zoom in"). |
| `KeywordsKey` | Optional resource key with synonyms for the palette ("magnify enlarge"). |
| `Category` | One of: File, Edit, View, Navigate, Tabs and windows, Markup, Pen, Forms, Pages, Tools, Settings, Help. |
| `DefaultChords` | Zero, one or two default shortcuts. |
| `Scope` | Where the shortcut works: `Global`, `Reader`, `Editing`, `Presentation` (3.4). |
| `Icon` | A Segoe Fluent Icons glyph or none. |
| `CanExecute(context)` | Pure function of a `CommandContext` snapshot: has a document, has a selection, permissions, view mode, policy, busy flags. Returns enabled, or disabled with a **reason key**. |
| `Repeatable` | True for commands that make sense on key auto-repeat (zoom, scroll, next page). |
| `Execute(context)` | Async. Exceptions are caught by the dispatcher. |

Rules: every user-visible action is a command (§7.3). Buttons, menus, context menus, accelerators and the palette are all views of the same registry; none holds its own handler. A command's name, shortcut and enabled state come from one place, so they cannot disagree.

`CommandContext` is an immutable snapshot built by the window (active tab, selection kind, permissions bits, view mode, whether a modal dialog is open, whether a text input has focus, policy flags). `CanExecute` must not touch XAML or the engine, so every predicate is unit-testable.

### 3.2 Chords

A chord is zero or more of Ctrl, Alt, Shift plus one key. Text form: `Ctrl+Shift+P`, modifiers in the fixed order Ctrl, Alt, Shift, then the key name. Key names are WinUI `VirtualKey` names with a few friendly aliases (`Plus` for the OEM plus key, `Minus`, `Comma`, `Period`). `ChordParser` accepts upper or lower case and any modifier order and produces the canonical text; invalid text is an error with a position.

The Windows key is never part of a chord.

**Reserved chords** that can't be bound: Alt+F4, Alt+Tab, Alt+Esc, Alt+Space, Ctrl+Esc, Ctrl+Alt+Delete, Print Screen, F10 and Shift+F10 (menu activation), Alt alone, Tab and Shift+Tab (focus movement), Esc alone (cancel and close), and anything with the Windows key.

**Unmodified printable keys** (letters, digits, punctuation, Space, Enter) may only be bound in the `Reader` scope, and are never delivered while a text input has focus (3.4). WCAG 2.1.4 (Character Key Shortcuts) is met by this plus remappability: the user can turn any single-key shortcut off. No single-key defaults are proposed in this spec for tools (OPEN QUESTION OQ-CMD-3), because they would be the first thing a Narrator or NVDA user trips on.

### 3.3 Layout handling

The existing code assigns the zoom keys by US virtual-key numbers (187 and 189). Chords are stored as virtual keys, so `Plus` and `Minus` mean "the OEM plus and minus keys" on any layout that has them unshifted (US, UK, German) and the nearest equivalent on others (the display text always shows what the user's layout calls the key, via `MapVirtualKey`). Layouts where "Ctrl+=" needs Shift (some national layouts) are covered by also accepting the numpad Add and Subtract keys as second default chords. OPEN QUESTION OQ-CMD-2: bind by virtual key (assumed), or by produced character. The test set covers en-US, en-GB, de-DE, fr-FR and ja-JP (106 keyboard) with a layout table, not by switching layouts at test time.

**AltGr.** On layouts where Right Alt acts as AltGr, Windows reports it as Ctrl+Alt. A chord that includes both Ctrl and Alt is ignored while AltGr is active, so typing a character that needs AltGr can't fire a command. Defaults avoid Ctrl+Alt where possible; page-view modes use Ctrl+Alt+digit (see [page-view-modes](page-view-modes.md)), which will be reviewed under this rule.

### 3.4 Dispatching

One `ChordDispatcher` handles `PreviewKeyDown` at the window root (the tunnelling key event on `UIElement`; its availability in Windows App SDK 2.5.1 is **UNVERIFIED** and is the first thing the first WP confirms; fallback is accelerators generated from the registry).

1. Build the chord from the key and the modifier state.
2. If a text input has focus (`TextBox`, `PasswordBox`, `RichEditBox`, an editable `ComboBox`, the palette box, an annotation text editor, a form-field editor) and the chord is a **text-editing chord** (any chord without Ctrl or Alt that produces text; Ctrl+C, X, V, A, Z, Y; arrows, Home, End, Delete, Backspace, Enter, and those with Shift or Ctrl), let the control have it. Commands in scope `Editing` are the exception (a command may declare it works inside the annotation editor, such as `edit.commitText`).
3. Find commands whose scope matches the current scope and whose effective chords include this chord. If one: run it when enabled, otherwise announce its disabled reason (3.6) and mark handled. If none: do nothing, don't mark handled.
4. Key auto-repeat: non-`Repeatable` commands run once per physical press.
5. Handled keys are marked handled so the focused control doesn't also act.

**Scopes.** `Global`: any focus. `Reader`: the page surface or no input has focus. `Editing`: an annotation or form text editor is active. `Presentation`: presentation mode only; in that mode only `Presentation` and a short list of `Global` commands (exit, quit, tabs) run. A chord conflict exists only between commands whose scopes overlap, where `Global` overlaps everything except `Presentation`.

### 3.5 The palette

**Open:** Ctrl+Shift+P (command `app.commandPalette`), or the command bar's search button. Pressing the shortcut again, or Esc, closes it and returns focus to where it was.

**Layout:** a floating panel centred near the top of the window, width the smaller of 640 DIP and 90% of the window, height at most 60% of the window. Content: a search box ("Type a command") and a list of results. Appearance is in the design spec.

**Results.** Sources:

| Source | Items | Limit |
|---|---|---|
| Commands | every registered command, enabled or not | all, virtualised |
| Open tabs | "Switch to tab: {title}" | 20 |
| Recent files | "Open recent: {file name}" with the folder as detail | 20 |
| Settings pages | "Settings: {section}" | all |
| Go to page | when the query is an integer within the page range: "Go to page {n}" as the first result | 1 |

Sources are `IPaletteSource` implementations behind the same scoring. A source that throws is skipped and logged.

**Search.** Text is normalised (NFKC, case-insensitive, diacritic-insensitive, width-insensitive) and split on spaces. Every token must match, in the name, the keywords or the category. Match quality, best first: exact name, name prefix, word-start match (also acronyms: "zi" matches "Zoom in"), contiguous substring, subsequence. Score is the sum over tokens, plus a bonus for recently used commands (last 20 used, kept in a file in `LocalState\private` and removed by Clear history), plus a bonus when the command is enabled. Ties break on the localised name. A 500-command registry must rank in under 5 ms per keystroke (proposed target; benchmark in tests).

With an empty box: the 8 most recently used commands, then categories' first few commands. No result: "No matching commands".

**Row content:** name (matched characters emphasised, visual only), category, shortcut text, and a second line with the **disabled reason** when disabled. A disabled row can be selected but not run; Enter announces the reason.

**Keyboard:** typing filters; Up/Down move the selection while focus stays in the box; Home/End jump; Enter runs; Esc closes; Ctrl+Shift+P closes. Running a command closes the palette first, restores focus, then executes (so commands that open dialogs get a clean focus owner).

**Not available** in presentation mode and while a modal dialog is open; the command is then disabled with a reason.

### 3.6 Disabled reasons

Each `CanExecute` returns a resource key such as `Reason_NoDocument` ("Open a document first"), `Reason_NoSelection`, `Reason_OwnerRestriction` ("The document's owner doesn't allow this"), `Reason_Policy` ("Turned off by your organisation"), `Reason_Busy`, `Reason_ReadOnly`, `Reason_Presentation`. The reason is shown in the palette, set as the control's `HelpText`, and spoken (once) when a disabled shortcut is pressed (a polite announcement, not a sound).

### 3.7 Remapping

Settings > Shortcuts (visuals in the design spec):

- A list of commands grouped by category, with a filter box. Each row shows the name, its effective chords, and "Reset" when changed.
- **Change** opens a capture dialog: "Press the keys you want to use." The dialog shows the chord live. Esc cancels, a Clear button removes the shortcut. For assistive technology, which may intercept keys, the same dialog has a text box where the chord can be **typed** ("Ctrl+Alt+K"), parsed by `ChordParser`.
- At most **2 chords per command**.
- **Conflicts:** if the chord is used by another command in an overlapping scope, the dialog says so ("Ctrl+H is used by Find and replace.") with Reassign (removes it from the other command) or Cancel. A reserved chord is an inline error.
- **Unbinding** is allowed for every command; the palette and the menus stay as a way to run anything.
- "Reset all shortcuts" asks for confirmation.
- **Storage:** only **overrides** are stored, in `settings.json` under `keybindings`: `{ "command": "view.zoomIn", "chords": ["Ctrl+Plus", "Ctrl+Add"] }`; an empty `chords` array means unbound. Defaults live in code, so a newer version can change the default of a command the user never touched. If an update adds a default chord that clashes with one the user chose, the user's choice wins and the new default is dropped, with the clash listed under the shortcuts page ("Shortcut conflicts after update").
- **Policy** cannot remap shortcuts in v1.

### 3.8 Export, import and cheat sheet

- **Export shortcuts** writes `MikePDF-shortcuts.json` (`format: "mikepdf-shortcuts"`, `schemaVersion: 1`, the override list). **Import shortcuts** reads one with the same validation and preview as settings import ([settings-policies](settings-policies.md) section 3.7): size limit 256 KiB, at most 1,000 entries, unknown commands reported and skipped, invalid chords reported and skipped, conflicts reported, nothing applied until confirmed. Both are also part of the whole-settings export. Policy `SettingsImportExportEnabled=0` hides them.
- **Cheat sheet:** "Copy shortcut list" copies the effective bindings as a plain-text table. OPEN QUESTION OQ-CMD-4: whether to also offer a printable page. Not required by §10.2.

### 3.9 Migration of the existing accelerators

The static accelerators in `MainWindow.xaml` are removed in the same change that introduces the registry and dispatcher, and the existing UI journeys (open, find, go to page, zoom, rotate) must pass unchanged. **Order with W1-reader:** if W1-reader merges first with more static accelerators, the registry WP converts them all; if the registry merges first, W1-reader registers commands instead. The README sequences the registry first for that reason.

### 3.10 Initial command catalogue

Command ids used by the specs in this folder, with defaults from §10.2 where it gives one. "Owner" is the spec that defines behaviour. Reader commands from §10.2 that belong to the m2 work (find, history, zoom, rotate, go to page, outline) are listed so their ids are fixed; the m2 spec owns their behaviour.

| Id | Default chord | Scope | Owner |
|---|---|---|---|
| `file.open` | Ctrl+O | Global | m2 |
| `file.save` | Ctrl+S | Global | save pipeline |
| `file.saveAs` | Ctrl+Shift+S | Global | save pipeline |
| `file.saveFlattenedCopy` | none | Global | save pipeline |
| `file.revert` | none | Global | save pipeline |
| `file.print` | Ctrl+P | Global | print |
| `file.printCurrentPage` | none | Global | print |
| `file.clearRecent` | none | Global | file association |
| `edit.undo` | Ctrl+Z | Reader | annotation core |
| `edit.redo` | Ctrl+Y, Ctrl+Shift+Z | Reader | annotation core |
| `edit.delete` | Delete | Reader | annotation core |
| `edit.selectAll` | Ctrl+A | Reader | m2 |
| `edit.copy` | Ctrl+C | Reader | m2 |
| `find.open` / `find.next` / `find.previous` | Ctrl+F / F3 / Shift+F3 | Global / Reader | m2 |
| `view.zoomIn` / `view.zoomOut` / `view.zoomReset` | Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Reader | m2 |
| `view.fitWidth` / `view.fitPage` | Ctrl+2 / Ctrl+1 | Reader | m2 |
| `view.rotateRight` / `view.rotateLeft` | Ctrl+Shift+Plus / Ctrl+Shift+Minus | Reader | m2 |
| `view.single` / `view.facing` / `view.facingCover` / `view.toggleFlow` | Ctrl+Alt+1 / 2 / 3 / 0 (proposed) | Reader | page modes |
| `view.presentation` | Ctrl+L | Global | page modes |
| `view.fullScreen` | F11 | Global | m2 |
| `nav.goToPage` | Ctrl+G | Reader | m2 |
| `nav.nextPage` / `nav.previousPage` | Page Down, Right / Page Up, Left | Reader | m2 |
| `nav.firstPage` / `nav.lastPage` | Home / End | Reader | m2 |
| `nav.back` / `nav.forward` | Alt+Left / Alt+Right | Reader | m2 |
| `tabs.next` / `tabs.previous` | Ctrl+Tab / Ctrl+Shift+Tab | Global | tabs |
| `tabs.close` / `tabs.reopen` | Ctrl+W / Ctrl+Shift+T | Global | tabs |
| `tabs.new` / `window.new` | Ctrl+T / Ctrl+N | Global | tabs |
| `tabs.moveLeft` / `tabs.moveRight` | Ctrl+Shift+PageUp / Ctrl+Shift+PageDown | Global | tabs |
| `tabs.moveToNewWindow` | none | Global | tabs |
| `tabs.goto1` ... `tabs.goto8`, `tabs.gotoLast` | see OQ-TAB-5 (conflict with fit page and fit width) | Global | tabs |
| `markup.highlightSelection` | Ctrl+H (configurable) | Reader | text markup |
| `markup.underlineSelection`, `markup.strikeoutSelection`, `markup.squigglySelection` | none | Reader | text markup |
| `markup.addNoteToSelection` | none | Reader | sticky notes |
| `tool.select`, `tool.note`, `tool.freeText`, `tool.pen`, `tool.highlighter`, `tool.eraser`, `tool.rectangle`, `tool.ellipse`, `tool.line`, `tool.arrow`, `tool.polygon`, `tool.polyline`, `tool.stamp` | none (OQ-CMD-3) | Reader | annotation specs |
| `annot.next` / `annot.previous` | Tab / Shift+Tab (annotation navigation mode only) | Reader | annotation core |
| `annot.edit` / `annot.properties` | Enter / Alt+Enter | Reader | annotation core |
| `annot.reply` | none | Reader | sticky notes |
| `annot.panel` | none | Global | annotation panel |
| `forms.highlightFields` | none | Reader | forms |
| `forms.clearForm` / `forms.resetField` | none | Reader | forms |
| `pages.organise` | none | Global | page organiser |
| `pages.insertBlank` / `pages.insertFromFile` / `pages.duplicate` / `pages.delete` | none | Reader | page organiser |
| `pages.rotateRight` / `pages.rotateLeft` | none | Reader | page organiser |
| `pages.extract` / `pages.moveTo` | none | Reader | page organiser |
| `file.merge` / `file.split` | none | Global | page organiser |
| `app.commandPalette` | Ctrl+Shift+P | Global | this |
| `app.settings` | Ctrl+, | Global | settings |
| `app.about`, `app.openDefaultAppsSettings` | none | Global | settings, file association |
| `settings.export` / `settings.import` / `settings.reset` / `settings.clearHistory` | none | Global | settings |
| `shortcuts.export` / `shortcuts.import` / `shortcuts.resetAll` | none | Global | this |

## 4. Keyboard, pen and touch

The palette is keyboard-first. Touch: a command bar button opens it; tapping a result runs it; the soft keyboard shows. Pen: acts as touch. The remapping dialog requires a keyboard; its typed-chord box exists for assistive technology and for devices without physical keys cannot bind a chord, which is acceptable (the palette and menus still run everything).

## 5. Accessibility

- **Palette semantics.** The panel is a UIA `Window`-like container named "Command palette". The search box is an `Edit` named "Type a command". The result list is a `List` of `ListItem`s with `SelectionItem`. Because focus stays in the search box while the selection moves, each selection change must raise a UIA event that Narrator speaks: use the same mechanism as `AutoSuggestBox` suggestions, or raise `SelectionItem_ElementSelected` and set `ControlledBy`. The spoken text of a row is "{name}, {shortcut or no shortcut}, {category}" and, when disabled, "unavailable, {reason}".
- A polite live region announces the result count after a 300 ms pause in typing ("12 results", "No matching commands").
- Focus: opening moves focus to the box; closing returns it to the previous element (verified by UIA focus tracking).
- Every button, menu item and toolbar control sets `AutomationProperties.AcceleratorKey` to its current shortcut text and updates it when remapped, so Narrator reads the shortcut. Access keys (`AccessKey`) are separate and unchanged.
- Remapping dialog: the live chord is exposed as text with a polite update; conflicts and errors are read in a live region.
- High contrast and 225% text scale apply to the palette; the panel grows in height, never truncates names without a tooltip.
- Reduced motion: no slide-in animation.
- WCAG 2.1.4 and 2.1.1: all functionality by keyboard; character-key shortcuts can be turned off.

## 6. Error states

| Situation | Behaviour |
|---|---|
| A command throws | The dispatcher catches it, shows a banner "That didn't work. {Name} couldn't finish.", logs the exception type (no document data), and the app continues. |
| A palette source throws | Skipped; other results show. |
| Bad chord text in `settings.json` | That chord is dropped and listed under Shortcuts as "Couldn't read a shortcut". |
| Import with conflicts or reserved chords | Reported in the preview; those entries are skipped. |
| Dispatcher cannot attach (API missing) | Fallback accelerators from the registry are installed; if that also fails, the menus and palette remain. |
| History file unreadable | Palette starts with no recents. |

## 7. Edge cases

- **Rotated pages, CropBox, encryption:** commands that need them check permissions in `CanExecute`; the palette shows the reason.
- **Permissions:** `Reason_OwnerRestriction` for print, copy, annotate, assemble and form fill commands according to the permission bits ([print](print.md) section 3.7 for bit numbers).
- **Huge registry or history:** history is capped at 20 entries; registry size is fixed at build time.
- **Key repeat and very fast input:** non-repeatable commands run once; a command that is already running and not re-entrant ignores a second invocation.
- **IME composition:** while an IME composition is active in a text input, no chord is dispatched.
- **Dead keys and compose sequences:** dead keys are not chords; they pass through.
- **Numpad and NumLock:** numpad digits count as the digit keys for the same command only where a catalogue entry says so (`NumberPad0` for zoom reset already exists today).
- **Multiple windows:** each window has its own focus and context; the registry is shared. Palette history is shared.
- **Hostile input:** a shortcut file with 10 million entries or 4 GB: rejected by the size check before parsing. Command names from resources are not executed. Palette query length capped at 256 characters.
- **Bidirectional text and RTL:** the palette mirrors; shortcut text stays left-to-right.
- **Language changes:** names change language; ids and stored chords don't.

## 8. Undo and redo

The palette and remapping have no undo. Remapping has per-row Reset. Commands that edit documents define their own undo; `edit.undo` and `edit.redo` are commands in the catalogue and operate on the active document's edit stack ([annotation-core](annotation-core.md), [save-pipeline](save-pipeline.md)).

## 9. Journal entries

None. Keybindings are settings.

## 10. Save behaviour

`keybindings` are saved atomically with `settings.json`. Palette history is a small separate file in `LocalState\private`.

## 11. Engine and protocol additions

None.

## 12. Localisation strings

Every command has `Cmd_<id>_Name` (and optionally `Cmd_<id>_Keywords`); those are generated from the catalogue and are not repeated here. Fixed strings:

| Key | en-US |
|---|---|
| `Palette_Name` | Command palette |
| `Palette_Box_Name` | Type a command |
| `Palette_NoResults` | No matching commands |
| `Palette_Count_One` | 1 result |
| `Palette_Count_Other` | {0} results |
| `Palette_Row_Automation` | {0}, {1}, {2} |
| `Palette_Row_Unavailable` | unavailable, {0} |
| `Palette_NoShortcut` | no shortcut |
| `Palette_SwitchTab` | Switch to tab: {0} |
| `Palette_OpenRecent` | Open recent: {0} |
| `Palette_GoToPage` | Go to page {0} |
| `Palette_Settings` | Settings: {0} |
| `Reason_NoDocument` | Open a document first |
| `Reason_NoSelection` | Select something first |
| `Reason_OwnerRestriction` | The document's owner doesn't allow this |
| `Reason_Policy` | Turned off by your organisation |
| `Reason_Busy` | MikePDF is busy with another task |
| `Reason_ReadOnly` | This document can't be changed |
| `Reason_Presentation` | Not available in presentation mode |
| `Shortcuts_Title` | Shortcuts |
| `Shortcuts_Change` | Change shortcut |
| `Shortcuts_Capture_Prompt` | Press the keys you want to use |
| `Shortcuts_Capture_Typed` | Or type it, for example Ctrl+Alt+K |
| `Shortcuts_Conflict` | {0} is used by {1}. |
| `Shortcuts_Reassign` | Reassign |
| `Shortcuts_Reserved` | {0} is reserved by Windows or by MikePDF and can't be used. |
| `Shortcuts_Max` | A command can have up to two shortcuts. |
| `Shortcuts_Reset` | Reset |
| `Shortcuts_ResetAll` | Reset all shortcuts |
| `Shortcuts_ResetAll_Confirm` | Restore every shortcut to its default? |
| `Shortcuts_Export` / `_Import` | Export shortcuts / Import shortcuts |
| `Shortcuts_Copy` | Copy shortcut list |
| `Shortcuts_UpdateConflicts` | Shortcut conflicts after update |
| `Command_Failed` | That didn't work. {0} couldn't finish. |

Telemetry: none. Palette history stays on the device.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-CMD-1 | The palette is P1/M6 in the master prompt; this spec puts it in M3 on the user's instruction. Confirm, and confirm that the registry (needed by everything) comes first regardless. | User |
| OQ-CMD-2 | Bind by virtual key (assumed) or by produced character. | Opus |
| OQ-CMD-3 | Default single-key shortcuts for tools (for example H for highlight)? Proposed: none. | User (product), Design |
| OQ-CMD-4 | Printable cheat sheet. | Design |
| OQ-CMD-5 | Does `UIElement.PreviewKeyDown` exist and behave as needed in Windows App SDK 2.5.1? (First task.) | Sonnet spike |
| OQ-CMD-6 | Chord sequences such as Ctrl+K, Ctrl+H. Not in §10.2; excluded from v1. | User |
| OQ-CMD-7 | Palette entries for bookmarks and annotations ("go to bookmark ...")? Not in scope; would be a later source. | User |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| CMD-01 | Registry integrity: ids unique and match `^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$`; every command has a name resource in en-US, a category, a scope; no id is removed or changed relative to a committed `command-ids.txt` snapshot (CI fails on a removal). | Unit (`MikePDF.Core.Tests`) |
| CMD-02 | No two default chords conflict in overlapping scopes; no default is a reserved chord. | Unit |
| CMD-03 | `ChordParser`: canonical round-trip for all defaults; accepts any case and modifier order; rejects empty, unknown key, two keys, Win key, duplicated modifier; error carries a position. | Unit |
| CMD-04 | Reserved chord list is enforced on bind and import. | Unit |
| CMD-05 | Dispatcher: a chord in `Reader` scope runs the command when the page surface has focus, and does not run while a `TextBox` has focus; Ctrl+C in a text box copies text and not the page selection. | Unit (dispatcher with fake focus) + UI journey |
| CMD-06 | AltGr: with AltGr active, Ctrl+Alt chords do not fire. | Unit |
| CMD-07 | Auto-repeat: a non-repeatable command fires once for 30 repeat events; a repeatable one fires 30 times. | Unit |
| CMD-08 | `CanExecute` truth table for each command class (no document, no selection, owner restriction, policy, presentation, busy) returns the right reason key. | Unit |
| CMD-09 | Palette search: exact, prefix, word-start, acronym, substring and subsequence ranking order on a fixed registry; diacritic and width insensitivity; multi-token AND; recents and enabled bonuses; stable tie-break. Golden file of 40 queries. | Unit |
| CMD-10 | Palette performance: ranking 500 commands for any single keystroke takes < 5 ms on the CI runner (proposed target; benchmark must run and the number is recorded). | Unit (benchmark) |
| CMD-11 | Dynamic sources: tabs, recent files, settings pages and "Go to page N" appear for matching queries and respect their limits; a throwing source is skipped. | Unit |
| CMD-12 | Palette flow: Ctrl+Shift+P opens with focus in the box; typing "zoom in" and Enter runs `view.zoomIn` and returns focus to the surface; Esc closes and restores focus; the shortcut again closes. | UI journey |
| CMD-13 | Disabled command: Enter on it does nothing and the reason is announced once. | UI journey (a11y) |
| CMD-14 | Narrator-facing text: each selection change raises exactly one UIA event whose text equals the specified row text; result count live region fires once after 300 ms. | UI journey (a11y, raw `IUIAutomation`) |
| CMD-15 | Accelerator text: every toolbar button with a command shows `AcceleratorKey` equal to the effective chord and updates after a remap. | UI journey (a11y) |
| CMD-16 | Remap flow: change `view.zoomIn` to a new chord via the capture dialog and via the typed-chord box; the old chord stops working; the new works; restart keeps it. | UI journey |
| CMD-17 | Conflict flow: binding a chord used by another command offers Reassign or Cancel; Reassign leaves exactly one owner; unbinding leaves the command runnable from the palette. | Unit + UI journey |
| CMD-18 | Max two chords per command enforced. | Unit |
| CMD-19 | Only overrides are stored; an untouched command follows a changed default after an update; a clash with a user chord keeps the user's and lists the conflict. | Unit |
| CMD-20 | Export/import: round-trip equality; import preview counts are right; unknown command, invalid chord, reserved chord and conflict entries are skipped and reported; > 256 KiB, > 1,000 entries and wrong `format` are rejected. | Unit |
| CMD-21 | With `SettingsImportExportEnabled=0`, shortcuts export and import are hidden and unavailable in the palette. | Unit |
| CMD-22 | Layout table tests: en-US, en-GB, de-DE, fr-FR, ja-JP produce the expected display text for `Plus`, `Minus`, `Comma` and the numpad alternatives; zoom in and out are reachable on each. | Unit |
| CMD-23 | A command that throws leaves the app responsive and shows the banner once; the exception is logged without document data. | Unit + Integration |
| CMD-24 | Migration: after the registry replaces `KeyboardAccelerator`s, `MainWindow.xaml` contains none, and the existing reader journeys pass unchanged. | UI journey |
| CMD-25 | Keyboard-only journey 9 (§12.5): open, search, navigate, annotate (M4), save, with a palette for at least two of the steps and no mouse input. | UI journey |
| CMD-26 | IME composition active in a text input: no chord fires. | UI journey |
| CMD-27 | Pseudo-RTL: the palette mirrors and shortcut text keeps its order. | UI journey (visual) |
| CMD-28 | Hostile shortcut file (4 GB claim, 10 million entries, depth bomb) is rejected before parsing without allocating more than 1 MiB. | Unit |
| CMD-29 | Presentation scope: in presentation mode only presentation and listed global commands run; the palette is disabled with `Reason_Presentation`. | UI journey |
