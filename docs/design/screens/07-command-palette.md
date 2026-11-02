# 07 Command palette (Ctrl+Shift+P)

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 6 (a screen-reader user reaches every command), 9 (policy-disabled commands are explained). It's also how every scenario's keyboard-only script (journey 9) is possible, because **every command is in the palette** (principle P3).
- Spec: master prompt §2.2 (P1), §7.3 (the palette searches all commands), §10.2 (`Ctrl+Shift+P`). The roadmap puts it in Wave 2.
- `Ctrl+Shift+P` is also the Windows Terminal palette shortcut **[guess: from familiarity]**, so Windows users may already know it.

## 1. Purpose

One keyboard-first place that finds and runs anything: a command, a page, a bookmark, an open tab, a recent file, a setting. It also teaches shortcuts (each result shows its shortcut) and lets users change them.

## 2. Layout

### 2.1 Palette (overlay, top centre of the window)

```text
                      +--------------------------------------------------------------+
                      | [search] Type a command, page, bookmark or file...        [x] |  <- TextBox, 40 epx
                      +--------------------------------------------------------------+
                      | Commands                                                      |  section header (Caption)
                      |  [hl]  Highlight selection                        Ctrl+H      |  <- selected row, 40 epx
                      |  [ul]  Underline selection                        Ctrl+U      |
                      |  [..]  Highlight with colour...                               |
                      | Pages and bookmarks                                           |
                      |  [bk]  Findings > Contingent liabilities          Page 41     |
                      | Settings                                                      |
                      |  [gr]  Settings: Pen writes ink by default                    |
                      +--------------------------------------------------------------+
                      | 6 results  -  Up/Down to choose  -  Enter to run  -  Esc closes|  footer (Caption)
                      +--------------------------------------------------------------+
                         560 epx wide (max 640), up to 9 rows then scroll, OverlayCornerRadius 8
```

### 2.2 Empty query: shows what the user is most likely to want

```text
                      | [search] Type a command, page, bookmark or file...            |
                      | Recent commands                                               |
                      |  Fit width                                         Ctrl+2     |
                      |  Highlight selection                               Ctrl+H     |
                      | Tips                                                          |
                      |  Type a number to go to a page.  Type @ for open tabs.        |
```

### 2.3 Disabled command with its reason

```text
                      |  [pr]  Print...                           (dimmed)  Ctrl+P    |
                      |        The document's owner doesn't allow printing.           |  Caption, secondary
                      |  [hl]  Highlight selection                (dimmed)  Ctrl+H    |
                      |        Select some text first.                                |
```

### 2.4 Row actions (focus on a row, Shift+F10 or the Menu key)

```text
                      |  Change shortcut...        |   opens a small dialog: "Press the new keys" with Reset and conflicts
                      |  Reset shortcut            |
                      |  Show in Settings          |
```

## 3. Behaviour

1. **Open:** `Ctrl+Shift+P` (anywhere in the window, including while a dialog is closed), the **Commands** button in the command bar, or **Commands** in the overflow. The palette appears at the top centre of the window, over the page well and panes, with `MotionBase` fade (instant under reduced motion). Focus goes to its text box. The page behind does not scroll or change.
2. **What it searches** (sections appear only when they have results):

| Section | Contents | Run |
|---|---|---|
| Commands | Every command in the registry ([commands.md](../commands.md)), including ones with no shortcut and ones with no button | Runs it |
| Pages and bookmarks | A typed number (`12`, or `:12`) shows "Go to page 12" first. Outline entries match by title | Goes there |
| Open tabs | Titles of open documents, in every window | Switches to the tab |
| Recent files | The recents list ([02](02-empty-state.md)) | Opens the file |
| Settings | Each setting by name ("Dark page colours", "Pen writes ink by default") | Opens Settings at that item, or flips a simple on/off setting in place with a confirmation line in the footer |

3. **Shorthand prefixes** are optional: `@` limits to open tabs, `#` to bookmarks, `:` to page numbers, `/` to find text in the document (the rest of the line is the query: Enter starts a search, handing over to the Find bar), `?` to the shortcut help. A prefix is never required for anything.
4. **Matching and ranking.** A typed query is matched against the command's name and its alternative names (for example "Fit page" also has "Zoom to fit"). Rank by, in order: **exact or prefix match**, **match at a word start** ("fw" matches "Fit width"), **subsequence match** anywhere. Within a rank, by use: commands run in the last 30 days (a local count) and recency. Never rank by anything that depends on the document's text. The usage counts stay on the device, are stored with the settings, and are cleared by **Clear usage history** in Settings, Privacy.
5. **Enabled first.** Commands that can run now are listed before those that can't. The ones that can't are dimmed, **stay in the list** (so a user can find out why a command isn't available), and show a one-line reason ([README G-SR5](../README.md#66-screen-reader-g-sr)): "Open a document first", "Select some text first", "The document's owner doesn't allow printing", "Turned off by your organization" (policy; see [13](13-settings-and-policies.md)), "Nothing to undo". `Enter` on a disabled row does nothing but reads the reason aloud.
6. **Run:** `Enter`. The palette closes, then the command runs, so that any dialog or focus change the command makes isn't fighting the palette. If the command needs a value the user hasn't given ("Go to page", "Zoom to...", "Highlight with colour..."), it asks inline: the palette stays open and the footer changes to a prompt ("Page number, 1 to 612"); `Enter` completes.
7. **Navigation:** `Down` and `Up` move (wrapping), `PageDown` and `PageUp` by a screen, `Home` and `End` in the **text box** move the caret, `Ctrl+Home` and `Ctrl+End` jump in the list. `Esc` clears the query if there is one, then closes. `Tab` moves from the text box to the list and back. Clicking a result runs it, clicking outside closes.
8. **Shortcut display** shows the **current** binding (after any remapping) in the user's key naming, e.g. "Ctrl+Shift+S". A command with two bindings shows the first. A command with no binding shows nothing.
9. **Change shortcut.** `Shift+F10` or the Menu key on a row, then **Change shortcut...** opens a `ContentDialog`: "Press the keys you want to use for {command}". It records the chord, shows a conflict if the chord is already used ("Ctrl+K is used by {other}. Replace it?"), with **Assign**, **Remove shortcut**, **Reset to default**, **Cancel**. The dialog explains that plain letter keys are off by default because they interfere with typing and screen readers (G-KB4). The same editing lives in Settings, Keyboard.
10. **Context.** The palette knows the context: with a selection it lists markup commands first; in the organiser it lists page commands first; with a form it lists form commands first. This is a tie-break, not a filter.
11. **History.** Reopening within 3 s restores the last query (so a mistaken Esc doesn't cost the query); otherwise it opens empty.
12. **Nothing else.** The palette doesn't run web searches, doesn't show ads or suggestions, and doesn't call any online service. AI (M7) can add a section later, subject to the AI principles in §9.1; it isn't in v1.0.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Instant for commands (an in-memory registry). Bookmarks of a large outline load lazily: while the outline isn't ready the Pages section shows "Reading bookmarks..." as a final row, not a spinner, and the other sections stay usable. A document that's still opening shows only commands that don't need it |
| **Empty** | No results: "No commands match '{query}'." with a second line "Try a shorter word, or a number to go to a page." **[design]**. An empty query is wireframe 2.2 |
| **Error** | If the registry can't build a row (a bug), skip it and write to local diagnostics; the palette doesn't show errors for single rows. If the usage history file is corrupt, start with none. A failed command shows its own error where the command would (the palette does not wrap errors) |
| **Keyboard focus** | Focus is always in the text box or the list, never lost. The selected row is the one whose action `Enter` runs; with a screen reader the focus stays in the box and the selection follows the "active descendant" (see section 5). Two-ring focus around the text box; the selected row has an accent backplate and a 3 epx accent bar at its leading edge |
| **High contrast** | Overlay has `Window` background and a 1 epx `WindowText` edge. Selected row: `Highlight` and `HighlightText`. Dimmed commands use `GrayText` and also show their reason text, so dimming alone is not the cue. Section headers are `WindowText` |
| **225% text scale** | Row height grows with text, so the list shows fewer rows (min 3). The reason lines wrap. The shortcut text moves below the name when the row is narrower than the name plus the shortcut. The footer wraps to two lines; the palette grows to at most 80% of the window height, then scrolls |
| **Narrow window** | At S and XS the palette is full width minus 16 epx, positioned under the title bar. Row text wraps. Section headers stay |
| **Touch** | The palette opens from the **Commands** button (and the XS **More** menu). Rows are 48 epx. The on-screen keyboard raises the palette so the list stays visible. Long-press on a row opens the row actions. Swipe is not used |

## 5. Accessibility

The palette is an **editable combobox with a list popup**, the UIA pattern Narrator and NVDA know best for "type to filter and arrow to choose".

| Element | Type | Name | AutomationId |
|---|---|---|---|
| Palette | Group | "Command palette" | `CommandPalette` |
| Text box | Edit (with ExpandCollapse) | "Search commands, pages, bookmarks and files" | `PaletteBox` |
| List | List | "Results" | `PaletteList` |
| Section header | Header | "Commands" etc. | `PaletteSection_{name}` |
| Row | ListItem | "{command name}, {shortcut}" (for example "Highlight selection, Control Shift H"). Dimmed: "{name}, unavailable. {reason}" | `PaletteItem_{commandId}` |
| Status (footer) | Text (live, polite) | "6 results" | `PaletteStatus` |

Narrator, intended:

| Action | Announcement |
|---|---|
| Open | "Command palette. Search commands, pages, bookmarks and files, edit." |
| Typing, after a 250 ms pause | "6 results" (polite; only when the count changes) |
| `Down` | "Highlight selection, Control H, 1 of 6" (selection-changed event on the list item, since focus stays in the box; `AutomationProperties.ActiveDescendant` is not available in WinUI, so the list item raises a `SelectionItem` selected event and the text box reports the "active item" in its `HelpText` **[guess: needs a Narrator test; fallback is moving focus into the list]**) |
| Disabled row | "Print, unavailable. The document's owner doesn't allow printing." |
| Run | "Highlight selection" (the command announces its own result) |
| Close | Focus returns to where it was before the palette opened |

**Fallback if the active-descendant pattern doesn't work in Narrator:** `Down` from the text box moves real focus into the list (the list gets the keyboard), `Up` from the first row returns to the box. This is a defined fallback and costs nothing to implement. The owner of the screen-reader pass decides which one ships (S7).

## 6. Keyboard path

| Task | Keys |
|---|---|
| Open | `Ctrl+Shift+P` |
| Choose and run | Type, `Down`, `Enter` |
| Go to page 12 | `Ctrl+Shift+P`, `1`, `2`, `Enter` (or just `Ctrl+G`) |
| Switch to another tab | `Ctrl+Shift+P`, `@`, name, `Enter` |
| Change a shortcut | `Ctrl+Shift+P`, name, `Shift+F10`, Change shortcut |
| Close | `Esc` (twice if there is a query) |

## 7. Engine and protocol implications

None. The registry is a UI-process structure. Outline entries come from the existing `GetOutline`, requested when the palette is first opened in a document (not at open time). Recent files come from the recents store.

## 8. OPEN QUESTIONS

1. Prefix characters (`@`, `#`, `:`, `/`, `?`) are this spec's choices, not borrowed wholesale from Sumatra, whose prefixes differ **[src: [Sumatra palette](https://www.sumatrapdfreader.org/docs/Keyboard-shortcuts)]**. Fine unless users of Sumatra are a target audience worth matching.
2. Is the usage-based ranking worth its small privacy cost (a local count of commands)? It never leaves the device and can be cleared. Document it in `privacy.md`.
3. Should the palette be allowed in reading mode and presentation mode? Reading: yes. Presentation: no (it's navigation-only; `Esc` first).
4. The active-descendant approach versus focus in the list (section 5) needs a Narrator and NVDA test.
5. P1 in §2.2, but the roadmap puts it in M3. The design assumes M3.
