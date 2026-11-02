# M3 commands and settings: what was built (work package W2-commands, branch `m3/commands-settings`)

- Status: built, with the gaps listed at the end. Specs: [command-palette-shortcuts](command-palette-shortcuts.md) and [settings-policies](settings-policies.md); screens 07 and 13 of the [design](../design/README.md).
- Date: 2026-11-02. Author: Sonnet. Reviewer: Opus.

## What exists now

| Part | Where |
|---|---|
| Chords, the parser, reserved chords, key layouts | `MikePDF.Core/Commands/Chord.cs`, `ChordRules.cs` |
| Command model, the catalogue (54 commands), the shortcut registry, the dispatcher | `CommandModel.cs`, `CommandCatalog.cs`, `CommandRegistry.cs`, `ChordDispatcher.cs` |
| Palette search, its sources, command history | `PaletteSearch.cs`, `PaletteSources.cs`, `CommandHistory.cs` |
| Settings registry and store (`settings.json`, atomic, debounced), migration | `MikePDF.Core/Settings/SettingsRegistry.cs`, `SettingsStore.cs`, `LegacyMigration.cs` |
| Policy provider, resolution, report | `PolicyCatalog.cs`, `PolicySources.cs`, `PolicyProvider.cs`, `PolicyReport.cs`; the admin table is [docs/admin/policies.md](../admin/policies.md) |
| Export, import with a preview, undo; shortcut export and import | `SettingsTransfer.cs` |
| The window's command host and binder | `MikePDF.App/MainWindow.Commands.cs`, `MainWindow.Settings.cs` |
| Palette, Settings page, shortcut dialog, About | `MikePDF.App/Views/CommandPaletteView.*`, `SettingsView*.cs`, `ShortcutDialog.cs`, `AboutContent.cs` |

Every accelerator and toolbar button is now a view of a command. `MainWindow.xaml` has no `KeyboardAccelerator`; a test fails if one comes back, if a toolbar button or menu item has no command bound to it, if a command has no action in the window, or if a string the code asks for is missing.

## Decisions taken on the open questions (all can be overridden)

| Question | What was done |
|---|---|
| Command ids: the feature spec and `commands.md` differ (`tabs.next` and `tab.next`, `find.open` and `find.open`) | The ids of [commands.md](../design/commands.md), as asked. Where the inventory has no id, a new one was added: `view.zoomPhysical` (Ctrl+Alt+0, "Actual size"; `view.zoomActual` is Ctrl+0, 100%), `app.clearHistory`, `app.resetSettings`, `app.undoImportSettings`, `app.resetShortcuts`, `app.copyShortcuts`. `view.readingMode` is the id for F11, named "Full screen" until reading mode's auto-hiding controls exist |
| OQ-CMD-5: does `PreviewKeyDown` exist in Windows App SDK 2.x | Yes. The root `Grid.PreviewKeyDown` receives every key from any focus. The dispatcher is built on it |
| OQ-CMD-2: bind by virtual key or by character | By virtual key (assumed). Display text comes from `MapVirtualKey` for the layout in use; the layout table in `ChordRules.cs` is test data |
| OQ-SET-1: default for external links | Show the address (today's behaviour). Block is the other choice. "Ask first, then open" opens nothing in this build (no network or launcher code is allowed), so it behaves as show-address and says so |
| OQ-SET-2: read HKCU too | Yes, stricter only: a user-level value counts only if it is at least as strict as the machine value (or the default when there is none). Policies marked "machine only" ignore it |
| OQ-SET-3: A2 over section 7.7 | Settings are in `LocalState\private\settings.json`, not `ApplicationData.LocalSettings` |
| Esc in the palette | The design says clear the query first, then close. The feature spec says close. The design was followed |
| Policy refresh | A 2-second timer calls `PolicyProvider.Refresh()` (a size query per known name). Not `RegNotifyChangeKeyValue`: that cannot watch a key that does not exist yet. A change is effective within 5 seconds, as the spec requires |
| BOM in `settings.json` | Treated as damage (SET-05 lists it). The app never writes one |

## Things that were not obvious

- **A standard user cannot write `HKCU\Software\Policies`.** Windows protects it. The policy journeys therefore name a throwaway key under `HKCU\Software\MikePDF.Tests` in the environment variable `MIKEPDF_TEST_POLICY_KEY`, and the app reads it as additional user-level policy, only if it starts with that prefix. User-level policy can only tighten, so the seam cannot weaken anything. The tests never write HKLM.
- **The page surface handles its own navigation keys.** `PageSurface` is not ours to change in this wave, so the commands that match its keys (`nav.nextPage` on Right and so on) are flagged `SurfaceNative`. The dispatcher runs them from the registry when the surface has focus; if the person unbinds one, the dispatcher swallows the key so the surface does not still act on it. Arrow keys on the sidebar lists and the like are never taken, because a plain key needs the surface itself to have focus.
- **Space and Page Down are now one command** (`nav.scrollScreenDown`, behaviour of the old Page Down). In a layout where the page fits the window, Space now goes to the next page instead of scrolling 90% of a screen. This is the Q7 rule of the design.
- **`Ctrl+Shift+0` is reserved** (Windows takes it before the window sees it), as is `Ctrl+Shift+Esc` and `Ctrl+Space`.

## Test map (criterion to test)

Core tests are in `tests/MikePDF.Core.Tests`; journeys in `tests/MikePDF.UI.Tests/CommandsSettingsJourneyTests.cs`.

| Criterion | Test |
|---|---|
| CMD-01 | `CommandTests.CMD01_*`, `AppWiringTests.CMD01_EveryCommandInTheCatalogueHasAnActionInTheWindow`, `command-ids.txt` |
| CMD-02, CMD-03, CMD-04 | `CommandTests.CMD02_*`, `CMD03_*`, `CMD04_*` |
| CMD-05 | `DispatcherTests.CMD05_*`; journey `CMD05_AReaderShortcutDoesNotRunWhileATextBoxHasFocus...` |
| CMD-06, CMD-07, CMD-26 (unit part) | `DispatcherTests.CMD06_*`, `CMD07_*`, `CMD26_*` |
| CMD-08 | `CommandTests.CMD08_*` |
| CMD-09, CMD-10, CMD-11 | `PaletteTests.CMD09_*` (40-query table), `CMD10_*` (500-command benchmark), `CMD11_*` |
| CMD-12 | journey `CMD12_ThePaletteOpensWithFocusInItsBox...` |
| CMD-13, CMD-14 | journey `CMD13_CMD14_...` (raw UIA notification events), `PaletteTests.CMD14_*` |
| CMD-15 | journey `CMD15_EveryButtonShowsItsShortcut...` |
| CMD-16 | journeys `CMD16_RemappingAShortcutByTypingIt...` (typed, and a restart) and `CMD16_RemappingByPressingTheKeys...` (captured) |
| CMD-17, CMD-18, CMD-19 | `CommandTests.CMD17_*`, `CMD18_*`, `CMD19_*`; journey `CMD17_ABindingThatClashes...` |
| CMD-20, CMD-21, CMD-28 | `TransferTests.CMD20_*`, `SET18_*`, `CMD28_*` |
| CMD-22 | `CommandTests.CMD22_*` |
| CMD-24 | `AppWiringTests.CMD24_*` plus the whole existing reader journey suite, unchanged |
| CMD-25 | journey `CMD25_AKeyboardOnlyJourney...` |
| CMD-29 (unit part) | `DispatcherTests.CMD29_*` |
| SET-01 to SET-03 | `SettingsTests.SET01_*`, `SET02_*`, `SET03_*` |
| SET-04 to SET-06 | `SettingsTests.SET04_*`, `SET05_*`, `SET06_*` |
| SET-07, SET-30 (part) | `PolicyTests.SET07_*` (including a real-registry test under a throwaway HKCU key) |
| SET-10 | `PolicyTests.SET10_*` |
| SET-11 | journey `SET11_ScenarioNineAboutShowsTheThreePolicies...` (user-level, see above) |
| SET-12, SET-13 | journey `SET12_ALockedSettingIsDisabled...` (locks, then unlocks, while the app runs) |
| SET-14 to SET-18 | `TransferTests.SET14_*` to `SET18_*` |
| SET-19, SET-27 | `SettingsTests.SET19_*`, `SET27_*` |
| SET-25 | `PolicyTests.SET25_*` |
| SET-26 | journey `SET26_ChangingTheThemeApplies...` |

## Not done, and why

| Item | Why |
|---|---|
| SET-08, SET-09, SET-13 against `HKLM` in the installed package | Needs elevation, and the rules forbid writing HKLM. The reader, the resolution and the live refresh are tested against HKCU keys instead |
| SET-09 (`EngineMemoryLimitMB` applied to the Job Object) | The broker belongs to the engine work package. `PolicySnapshot.EngineMemoryLimitMb` is ready to be read |
| SET-20 (clear history, integration), SET-21 (external links under each policy value, end to end), SET-22, SET-23, SET-24 | The clear commands and the Block behaviour exist and the settings resolve (unit tests), but there is no journey for them; print, save and comments do not exist yet |
| SET-15 UI half, SET-16 UI half | File pickers are system dialogs, which the journeys must not drive. The preview, the counts and Undo are unit tested |
| SET-28 (nothing written outside `LocalState\private`) | Not scanned |
| SET-29, CMD-27 (high contrast, 225% text, RTL), CMD-26 (IME) UI halves | Not run |
| CMD-23 (a command that throws, logged) | The banner exists and the app continues; only the exception type goes to the debug output, because there is no logging yet |
| Palette prefixes `@ # / ?`, row actions (Shift+F10), "type a value" prompts, outline entries in the palette | Not built. `:` and a plain number go to a page |
| Settings search box, per-section Reset, language, Open folder | Not built (no language resources; the folder opener would launch a program) |
| Presentation mode | Does not exist; the dispatcher and the commands carry the rule, tested with a flag |
| The engine version in About | The app has no way to ask the engine for it without a document. About says the engine is PDFium in a separate process. The engine work package could expose the `Hello` reply |
