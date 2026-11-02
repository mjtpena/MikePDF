# Command inventory

- Part of the [MikePDF design spec](README.md)
- Spec: master prompt §7.3 (every user action is a command with an ID, a localised name, a default shortcut, an enablement predicate and an icon; the palette searches all commands; undo is per document), §10.2 (the minimum keyboard set, all remappable and exportable)
- Date: 2026-11-02. Status: design. No command registry exists in the app yet; today's accelerators are written in `MainWindow.xaml` **[repo]**.

## 1. How the registry works

One registry is the **single source of truth** for command name, shortcut, icon, tooltip, enablement and disabled reason. Every surface reads it:

| Surface | Reads |
|---|---|
| Command bar and menus | Name, icon, enablement. Tooltip text "{name} ({shortcut})" is built from the **current** binding |
| Command palette ([07](screens/07-command-palette.md)) | Everything, including commands that have no button |
| Keyboard shortcuts page ([13](screens/13-settings-and-policies.md)) | Every command and its bindings |
| Context menus | A subset by context |
| XAML `KeyboardAccelerator`s | Generated from the registry so a remapped key takes effect everywhere |

### 1.1 Rules

1. **Every command is in the palette.** There is a test (journey 9 support): enumerate all IDs in the registry and in the UI (every button, menu item and accelerator), and fail if a UI element has no registry ID or a registry ID is missing from the palette. Only commands marked **Hidden** (none planned) may be absent.
2. **IDs are stable** (`area.verb`, lower camel case), never reused, and appear in the journal, the keyboard export and the usage history.
3. **Names** are sentence case, start with a verb where possible, end with "..." when the command opens a dialog or needs more input, and live in `.resw` (`Command_{id}_Name`, plus `Command_{id}_Alt` for alternative search names).
4. **Enablement** is a predicate over the app state (document, selection, permissions, policy). A disabled command **has a reason string** (`Command_{id}_Disabled_{cause}`): see the reason list in 1.4.
5. **Undo** is stated for each edit command: **Undoable** means one entry in the document's undo stack and the journal; **Navigation** (not an edit, no undo); **New file** (creates a file, nothing to undo in this document).
6. **Remapping:** every command can have up to **two** bindings. A binding is a chord (modifiers plus one key), or a **plain character key only when "Single-key tool shortcuts" is on**. Reserved system chords can't be assigned (`Alt+F4`, `Alt+Tab`, `Ctrl+Alt+Del`, `Win+...`). Export and import is a JSON of **changed** bindings only (§10.2). A remapping conflict asks the user to replace.
7. **Contexts.** A binding has a scope. When two commands use the same chord, the most specific scope that has focus wins: Dialog, then text field (Find box, note box, form editor), then Organiser grid, then Page well, then Window. The table's **Scope** column says where a default binding is active. Scope is not user-editable in v1.0.
8. **Policy** can disable a command (for example JavaScript-related ones, AI, external-link opening). A policy-disabled command shows "Turned off by your organization".
9. **Layouts.** Defaults are written for a US keyboard. On other layouts, `=`, `-`, `,`, `[`, `]` and `/` are matched by the character they produce where the layout allows, falling back to the physical key, so that `Ctrl+=` zooms in for German, French and UK users. (The app already maps the OEM keys **[repo: `ZoomInOemAccelerator`, virtual key 187/189]**.) **Conflicts with AltGr:** chords with `Ctrl+Alt` are never defaults, because `AltGr` is `Ctrl+Alt` on many layouts.

### 1.2 Columns

| Column | Meaning |
|---|---|
| ID | Registry ID |
| Name | Palette and menu text (en-US) |
| Shortcut | Default binding(s). "-" is none |
| Where | Surfaces other than the palette (every command is in the palette). **Bar** command bar, **Over** command bar overflow, **View** View menu, **Tab** tab context menu, **Ctx** page context menu, **Sel** selection toolbar, **Pane** a side pane, **List** a list row's menu, **Org** the organiser, **Form** the form bar, **Dlg** only inside a dialog, **Empty** the empty state, **Find** the find bar |
| Enabled when | The predicate. **Doc** a document is open and readable. **Sel** text is selected. **Edits** the document has unsaved edits |
| Undo | Undoable, Navigation, New file, or n/a |

### 1.3 Mouse, touch and pen gestures that map to commands

These aren't remappable in v1.0 but are listed so they are not forgotten. Each maps to a registry command.

| Gesture | Command |
|---|---|
| Wheel | `nav.scrollDown` / `nav.scrollUp` |
| `Ctrl` + wheel, pinch, double-tap with two fingers | `view.zoomIn` / `view.zoomOut` (continuous, centred on the pointer) |
| `Shift` + wheel | horizontal scroll |
| Middle-click on a tab | `file.close` |
| Mouse back and forward buttons | `nav.back` / `nav.forward` |
| Double-click on a word / triple-click / four clicks | select word / line / paragraph (§7.2) |
| `Alt` + drag | column selection (§7.2) |
| Drag a tab out | `tab.moveToNewWindow` |
| Marquee zoom (armed by `view.zoomRegion`) drag | zoom to the region |
| Pen contact | ink (when pen-first is on) |
| Pen eraser end, barrel button | `markup.eraser`, `markup.select` (defaults; configurable) |
| Two-finger drag | scroll; never ink |
| Long-press on text (touch) | start text selection |

### 1.4 Reasons a command is disabled (strings, not codes)

| Cause key | Message (en-US) |
|---|---|
| `NoDocument` | Open a document first. |
| `NoSelection` | Select some text first. |
| `NothingToUndo` / `NothingToRedo` | Nothing to undo. / Nothing to redo. |
| `OwnerNoPrint` | The document's owner doesn't allow printing. |
| `OwnerNoCopy` | The document's owner doesn't allow copying. |
| `OwnerNoAnnotate` | The document's owner doesn't allow adding comments. |
| `OwnerNoAssemble` | The document's owner doesn't allow changing pages. |
| `OwnerNoForm` | The document's owner doesn't allow filling forms. |
| `Policy` | Turned off by your organization. |
| `NoForm` | This document has no form fields. |
| `NoOutline` | This document has no bookmarks. |
| `NoAttachments` | This document has no attachments. |
| `NoEdits` | There are no unsaved changes. |
| `OnePage` | This document has one page. |
| `Busy` | Wait for the document to finish opening. |
| `ReadOnlyCopy` | This is a repaired copy that isn't saved. Save it first. |
| `NotSupported` | MikePDF can't do this for this document. |

---

## 2. File

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `file.open` | Open... | `Ctrl+O` | Bar, Empty, Tab (+) | Always | Navigation |
| `file.openRecent` | Open recent file | - (palette section) | Empty, Jump List | Recents exist | Navigation |
| `file.newTab` | New tab | `Ctrl+T` | Tab (+) | Always | n/a |
| `file.newWindow` | New window | `Ctrl+N` | Over | Always | n/a |
| `file.save` | Save | `Ctrl+S` | Bar (only with edits) | Doc and Edits and not read-only copy | n/a (the undo stack stays) |
| `file.saveAs` | Save as... | `Ctrl+Shift+S` | Over | Doc | n/a |
| `file.saveFlattenedCopy` | Save a flattened copy... | - | Over, Form | Doc | New file |
| `file.print` | Print... | `Ctrl+P` | Bar | Doc and not `OwnerNoPrint` | n/a |
| `file.properties` | Document properties | `Ctrl+D` | Over | Doc | n/a |
| `file.showInFolder` | Show in folder | - | Tab, Over, Pane | Doc has a path | n/a |
| `file.copyPath` | Copy file path | - | Tab | Doc has a path | n/a |
| `file.reload` | Reload from disk | `Ctrl+R` | Banner, Error page | Doc has a path. Confirms if Edits | Navigation (edits are dropped after confirmation) |
| `file.close` | Close tab | `Ctrl+W` | Tab (x), middle-click | Always. Asks if Edits | n/a |
| `file.closeOthers` | Close other tabs | - | Tab | More than one tab | n/a |
| `file.closeToRight` | Close tabs to the right | - | Tab | A tab to the right | n/a |
| `file.reopenClosed` | Reopen closed tab | `Ctrl+Shift+T` | Tab | A closed tab is remembered | n/a |
| `file.closeWindow` | Close window | `Alt+F4` (system) | Caption button | Always | n/a |
| `file.merge` | Merge PDFs... | - | Empty, Over, Org | Always | New file |
| `file.split` | Split document... | - | Over, Org | Doc with two pages or more | New file |
| `file.share` (P1) | Share... | - | Over | Doc has a path. Needs Q14 | n/a |
| `file.locate` | Locate file... | - | Error page, tab | A restored document is missing | Navigation |

## 3. Tabs and windows

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `tab.next` | Next tab | `Ctrl+Tab` | - | Two tabs or more | Navigation |
| `tab.previous` | Previous tab | `Ctrl+Shift+Tab` | - | Two tabs or more | Navigation |
| `tab.goto1` ... `tab.goto8` | Go to tab 1 to 8 | `Alt+1` ... `Alt+8` | - | That tab exists | Navigation |
| `tab.last` | Go to the last tab | `Alt+9` | - | Always | Navigation |
| `tab.moveLeft` | Move tab left | `Ctrl+Shift+PageUp` | Tab | Not the first tab | n/a |
| `tab.moveRight` | Move tab right | `Ctrl+Shift+PageDown` | Tab | Not the last tab | n/a |
| `tab.moveToNewWindow` | Move tab to a new window | `Ctrl+Shift+N`, tear-out | Tab | Two tabs or more | n/a |
| `tab.duplicate` | Duplicate tab | - | Tab | Doc | n/a |

## 4. Navigation

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `nav.nextPage` | Next page | `Right`, `Page Down` (paged layouts and fit page) | Bar (`>`) | Doc, not the last page | Navigation |
| `nav.previousPage` | Previous page | `Left`, `Page Up` (paged layouts and fit page) | Bar (`<`) | Doc, not the first page | Navigation |
| `nav.scrollScreenDown` | Scroll down one screen | `Page Down`, `Space` (continuous) | - | Doc | Navigation |
| `nav.scrollScreenUp` | Scroll up one screen | `Page Up`, `Shift+Space` (continuous) | - | Doc | Navigation |
| `nav.scrollDown` / `nav.scrollUp` | Scroll down / up | `Down` / `Up` | - | Doc | Navigation |
| `nav.scrollLeft` / `nav.scrollRight` | Scroll left / right | `Left` / `Right` when the page is wider than the view | - | Doc | Navigation |
| `nav.firstPage` | First page | `Home` | - | Doc | Navigation |
| `nav.lastPage` | Last page | `End` | - | Doc | Navigation |
| `nav.goToPage` | Go to page... | `Ctrl+G` | Bar (page box) | Doc | Navigation |
| `nav.back` | Back | `Alt+Left`, mouse back | - | History exists | Navigation |
| `nav.forward` | Forward | `Alt+Right`, mouse forward | - | Forward history exists | Navigation |
| `nav.nextComment` | Next comment | `Alt+Down` | Pane | A comment exists | Navigation |
| `nav.previousComment` | Previous comment | `Alt+Up` | Pane | A comment exists | Navigation |
| `nav.goToBookmark` | Go to bookmark... | - (palette `#`) | Pane | Bookmarks exist | Navigation |
| `nav.nextLandmark` | Next area | `F6` | - | Always | Navigation |
| `nav.previousLandmark` | Previous area | `Shift+F6` | - | Always | Navigation |
| `link.follow` | Follow link | `Enter` on a focused link | Ctx | A link is focused. External links show their address only (STATUS) | Navigation |
| `link.copyAddress` | Copy link address | - | Ctx | A link is focused | n/a |

**Q7 (README):** §10.2 lists `Page Down` as "next page". Here `Page Down` is *Next page* only in paged layouts and fit-page zoom, where one screen is one page, and *Scroll one screen* in continuous scrolling, so that no content is skipped. The two commands share the key; the layout decides.

## 5. View

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `view.zoomIn` | Zoom in | `Ctrl+=`, `Ctrl++`, `Ctrl+Numpad+` | Bar (+) | Doc, below 6400% | Navigation |
| `view.zoomOut` | Zoom out | `Ctrl+-`, `Ctrl+Numpad-` | Bar (-) | Doc, above 10% | Navigation |
| `view.zoomActual` | Actual size | `Ctrl+0` | View, Bar (zoom box) | Doc | Navigation |
| `view.fitWidth` | Fit width | `Ctrl+2` | View, Bar (zoom box) | Doc | Navigation |
| `view.fitPage` | Fit page | `Ctrl+1` | View, Bar (zoom box) | Doc | Navigation |
| `view.zoomTo` | Zoom to... | - | Bar (zoom box) | Doc | Navigation |
| `view.zoomRegion` | Zoom to a region | - | View | Doc | Navigation |
| `view.rotateRight` | Rotate view right | `Ctrl+Shift+=` | View | Doc | Navigation (the file is not changed) |
| `view.rotateLeft` | Rotate view left | `Ctrl+Shift+-` | View | Doc | Navigation (the file is not changed) |
| `view.layoutSingle` | Single page | `Ctrl+4` | View | Doc | Navigation |
| `view.layoutTwo` | Two pages | `Ctrl+5` | View | Doc, two pages or more | Navigation |
| `view.layoutTwoCover` | Two pages with cover | `Ctrl+6` | View | Doc, two pages or more | Navigation |
| `view.continuous` | Continuous scroll | `Ctrl+7` | View | Doc | Navigation |
| `view.readingMode` | Reading mode | `F11` | View, HUD | Doc | Navigation |
| `view.presentation` | Presentation | `Ctrl+L` | View | Doc, no dialog open | Navigation |
| `view.coverPage` | Show the first page alone, as a cover | - | View | Two pages or two pages with cover | Navigation |
| `view.directionAuto` / `view.directionLtr` / `view.directionRtl` | Reading direction: automatic / left to right / right to left | - | View | Doc | Navigation |
| `present.next` / `present.previous` | Next / previous page in the presentation | `Enter`, `N` / `Backspace`, `P` | - | Presenting (scope: Presentation) | Navigation |
| `present.black` / `present.white` | Black screen / White screen | `B`, `.` / `W`, `,` | - | Presenting (scope: Presentation) | Navigation |
| `view.pageColors` | Cycle page colours | `Ctrl+Shift+I` | View | Doc | Navigation |
| `view.pageColorsNormal` / `Dark` / `Sepia` / `HighContrast` | Page colours: Normal / Dark / Sepia / High contrast | - | View | Doc. High contrast needs E13 | Navigation |
| `view.caretSelection` | Select text with the keyboard | `F7` | View | Doc | Navigation |
| `view.showScrollMarks` | Show marks on the scroll rail | - | View | Doc | Navigation |

## 6. Panes

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `pane.toggleLeft` | Show or hide the left pane | `F4` | Bar (Panes) | Doc | Navigation |
| `pane.toggleRight` | Show or hide the details pane | `Alt+Shift+C` | Bar (Details) | Doc | Navigation |
| `pane.thumbnails` | Thumbnails | `Alt+Shift+T` | Pane selector | Doc | Navigation |
| `pane.outline` | Bookmarks | `Alt+Shift+B` | Pane selector | Doc (message when `NoOutline`) | Navigation |
| `pane.annotations` | Annotations | `Alt+Shift+A` | Pane selector | Doc | Navigation |
| `pane.attachments` | Attachments | `Alt+Shift+E` | Pane selector | Doc has attachments | Navigation |
| `pane.signatures` (P1) | Signatures | - | Pane selector | Doc has signatures | Navigation |
| `pane.layers` (P2) | Layers | - | Pane selector | Needs an ADR (E17) | Navigation |
| `pane.resizeLeft` | Resize the left pane | `Left` / `Right` on its handle | Handle | Pane open | n/a |

## 7. Find and selection

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `find.open` | Find in document | `Ctrl+F` | Bar | Doc | Navigation |
| `find.next` | Find next | `F3`, `Enter` (in the find box) | Find | A query exists | Navigation |
| `find.previous` | Find previous | `Shift+F3`, `Shift+Enter` | Find | A query exists | Navigation |
| `find.close` | Close find | `Esc` | Find | Find bar open | Navigation |
| `find.matchCase` | Match case | `Alt+C` (find bar) | Find | Find bar open | n/a |
| `find.wholeWord` | Whole word | `Alt+W` (find bar) | Find | Find bar open | n/a |
| `find.regex` | Regular expression | `Alt+R` (find bar) | Find | Find bar open | n/a |
| `find.diacritics` | Ignore accents | `Alt+A` (find bar) | Find | Find bar open | n/a |
| `find.showResults` | Show all results | `Ctrl+Shift+F` | Find | A query exists | Navigation |
| `edit.selectAllPage` | Select all text on the page | `Ctrl+A` (page well) | Ctx | Doc, not `OwnerNoCopy` for copying | Navigation |
| `edit.copy` | Copy | `Ctrl+C` | Ctx, Sel | Sel, not `OwnerNoCopy` | n/a |
| `edit.copyWithLayout` | Copy with layout | `Ctrl+Shift+C` | Ctx | Sel, not `OwnerNoCopy` | n/a |
| `edit.clearSelection` | Clear selection | `Esc` | - | Sel | Navigation |

## 8. Edit (undo and redo)

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `edit.undo` | Undo {action} | `Ctrl+Z` | Bar (markup, organiser), undo bar | Something to undo | n/a |
| `edit.redo` | Redo {action} | `Ctrl+Y`, `Ctrl+Shift+Z` | Bar (markup, organiser) | Something to redo | n/a |

The name includes the last action ("Undo highlight", "Undo delete 3 pages") for the tooltip and the screen reader.

## 9. Markup (M4)

All markup commands need Doc and not `OwnerNoAnnotate`, and a writable document (not `ReadOnlyCopy`). "Sel" means text selected.

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `markup.toggle` | Markup mode | `Ctrl+Shift+M` | Bar | Doc | Navigation (mode only) |
| `markup.select` | Select tool | `V` (single-key) | Markup bar | Markup mode | Navigation |
| `markup.highlight` | Highlight | `Ctrl+H`; `H` (single-key) | Markup bar, Sel | Sel (or arms the tool) | Undoable |
| `markup.highlightColor` | Highlight with colour... | - | Markup bar, Sel | Sel | Undoable |
| `markup.underline` | Underline | `Ctrl+U`; `U` (single-key) | Markup bar, Sel | Sel | Undoable |
| `markup.strikethrough` | Strikethrough | `Ctrl+Shift+X`; `S` (single-key) | Markup bar, Sel | Sel | Undoable |
| `markup.squiggly` | Squiggly underline | - | Sel (more) | Sel | Undoable |
| `markup.removeMarkup` | Remove markup | - | Sel (more), Ctx | Sel inside markup | Undoable |
| `markup.note` | Add a note | `Ctrl+M`; `N` (single-key) | Markup bar, Sel | Doc (a click places it, or Sel anchors it) | Undoable |
| `markup.freeText` | Add a text box | `T` (single-key) | Markup bar | Doc | Undoable |
| `markup.ink` | Pen | `P` (single-key) | Markup bar | Doc | Undoable (per stroke) |
| `markup.highlighterPen` | Highlighter pen | - | Markup bar | Doc | Undoable (per stroke) |
| `markup.eraser` | Eraser | `E` (single-key) | Markup bar | Doc and at least one ink annotation or markup | Undoable (per drag) |
| `markup.penMode` | Pen writes ink | - | Pen options | A pen digitizer is present or the user turns it on | n/a (setting) |
| `markup.shapeRectangle`, `Ellipse`, `Line`, `Arrow`, `Polygon`, `Polyline` | Rectangle, Ellipse, Line, Arrow, Polygon, Polyline | - | Markup bar (Shapes) | Doc | Undoable |
| `markup.stamp` | Stamp | - | Markup bar | Doc | Undoable |
| `markup.stampImage` | Image stamp... | - | Markup bar (Stamp) | Doc | Undoable |
| `markup.pinTool` | Keep the tool selected | `Shift` + choose, or double-click a tool | Markup bar | A tool is active | n/a |
| `markup.cancel` | Cancel the current tool | `Esc` | - | A tool is armed | Navigation |

## 10. Annotations (selected annotation)

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `annot.next` | Next annotation on the page | `Tab` (page focused, no form) | - | Doc has annotations on the page | Navigation |
| `annot.previous` | Previous annotation on the page | `Shift+Tab` | - | as above | Navigation |
| `annot.edit` | Edit annotation | `Enter`, `F2` | Ctx, List | An annotation is selected | Navigation |
| `annot.delete` | Delete annotation | `Delete` | Ctx, List, Mini bar | An annotation is selected, not locked | Undoable |
| `annot.reply` | Reply to comment | `Ctrl+Enter` (in the reply box) | Comment tab, popup | A note is selected | Undoable |
| `annot.setStatus` | Set status... | - | Comment tab, List | A note is selected. Values: None, Accepted, Rejected, Cancelled, Completed | Undoable |
| `annot.properties` | Show annotation properties | `Alt+Enter` | Ctx | An annotation is selected | Navigation |
| `annot.copy`, `annot.cut`, `annot.paste` | Copy, Cut, Paste annotation | `Ctrl+C`, `Ctrl+X`, `Ctrl+V` (an annotation selected) | Ctx | An annotation is selected; paste needs an annotation on the clipboard | Undoable (cut, paste) |
| `annot.nudge` | Move annotation | `Arrow` (1 pt), `Shift+Arrow` (10 pt) | - | An annotation is selected, not locked | Undoable (coalesced) |
| `annot.lock` | Lock annotation | - | Ctx | An annotation is selected | Undoable |
| `annot.bringToFront`, `annot.sendToBack` | Bring to front, Send to back | - | Ctx | A box annotation is selected | Undoable |
| `annot.recolor` | Change colour... | - | Mini bar, Format | An annotation is selected | Undoable |

## 11. Forms (M4)

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `form.next` | Next field | `Tab` (form focused) | Touch bar | Doc has fields | Navigation |
| `form.previous` | Previous field | `Shift+Tab` | Touch bar | Doc has fields | Navigation |
| `form.commit` | Commit field | `Enter` (editor) | - | A field is being edited | Undoable |
| `form.cancelEdit` | Revert field | `Esc` (editor) | - | A field is being edited | Navigation |
| `form.highlight` | Highlight fields | - | Form, View | `NoForm` otherwise | n/a (setting) |
| `form.requiredList` | Required fields... | - | Form | Doc has fields | Navigation |
| `form.nextEmpty` | Go to the next empty required field | - | Required list | A required field is empty | Navigation |
| `form.clear` | Clear form | - | Form | Doc has fields, not `OwnerNoForm` | Undoable (one step) |
| `form.resetField` | Reset field | - | Ctx | A field is focused | Undoable |

## 12. Pages (M4)

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `pages.organise` | Organise pages | `Ctrl+Shift+O` | View, Pane | Doc, not `OwnerNoAssemble` | Navigation |
| `pages.selectAll` | Select all pages | `Ctrl+A` (organiser or thumbnails) | Org | Organiser or thumbnails focused | Navigation |
| `pages.insertBlank` | Insert blank page | - | Org, Pane | Doc | Undoable |
| `pages.insertFromFile` | Insert pages from file... | - | Org, Pane | Doc | Undoable |
| `pages.delete` | Delete pages | `Delete` (organiser or thumbnails) | Org, Pane | Pages selected, not all pages | Undoable |
| `pages.duplicate` | Duplicate pages | - | Org, Pane | Pages selected | Undoable |
| `pages.rotateLeft` | Rotate pages left | `Ctrl+[` (organiser or thumbnails) | Org, Pane | Pages selected | Undoable |
| `pages.rotateRight` | Rotate pages right | `Ctrl+]` | Org, Pane | Pages selected | Undoable |
| `pages.extract` | Extract pages... | - | Org, Pane | Pages selected | New file |
| `pages.moveTo` | Move pages to... | - | Org | Pages selected | Undoable |
| `pages.moveEarlier` | Move pages earlier | `Alt+Shift+Left` | Org | Pages selected, not at the start | Undoable |
| `pages.moveLater` | Move pages later | `Alt+Shift+Right` | Org | Pages selected, not at the end | Undoable |
| `pages.moveToStart` | Move pages to the start | `Alt+Shift+Home` | Org | as above | Undoable |
| `pages.moveToEnd` | Move pages to the end | `Alt+Shift+End` | Org | as above | Undoable |

## 13. Attachments

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `attach.save` | Save attachment as... | - | Pane, List | An attachment exists | New file |
| `attach.saveAll` | Save all attachments... | - | Pane | Attachments exist | New file |

There is **no Open** command for attachments, by design ([06](screens/06-side-panes.md)).

## 14. Application

| ID | Name | Shortcut | Where | Enabled when | Undo |
|---|---|---|---|---|---|
| `app.commandPalette` | Command palette | `Ctrl+Shift+P` | Bar | Always (not in presentation mode) | n/a |
| `app.settings` | Settings | `Ctrl+,` | Over | Always | n/a |
| `app.shortcuts` | Keyboard shortcuts | `Ctrl+/`, `F1` | Over | Always | n/a. Opens a searchable overlay of all bindings |
| `app.about` | About MikePDF | - | Over | Always | n/a |
| `app.policies` | Enterprise policies | - | Settings, About | Always | n/a |
| `app.makeDefault` | Make MikePDF the default PDF app | - | Settings, prompt | Not already the default | n/a |
| `app.clearRecent` | Clear recent files and previews | - | Empty, Settings | Recents exist | n/a (confirms) |
| `app.exportSettings`, `app.importSettings` | Export, Import settings... | - | Settings | Always | n/a |
| `app.exportShortcuts`, `app.importShortcuts` | Export, Import shortcuts... | - | Settings | Always | n/a |
| `app.resetShortcut` | Reset shortcut | - | Palette row, Settings | The binding differs from the default | n/a |
| `app.changeShortcut` | Change shortcut... | - | Palette row, Settings | Any command | n/a |
| `app.clearUsageHistory` | Clear usage history | - | Settings | Always | n/a |
| `app.quit` | Exit MikePDF | `Alt+F4` (system) | Caption | Always | n/a |

## 15. Reserved for later (P1 and P2)

IDs and names are reserved so that the palette's structure is stable. **None of these is built or shown in v1.0.** A reserved command is **not** listed in the palette until its feature ships (principle: no disabled upsells).

| Area | IDs reserved | Spec reference |
|---|---|---|
| Read aloud | `read.aloud` (`Ctrl+Shift+U`), `read.pause`, `read.stop` | §2.2 |
| Signatures | `sign.validate`, `sign.showPanel`, `sign.create`, `sign.fillAndSign` | §8.7 |
| OCR | `ocr.makeSearchable`, `ocr.language` | §8.8 |
| Redaction | `redact.markArea`, `redact.markSearch`, `redact.apply` | §8.6 |
| Compare | `compare.open` | §8.9 |
| Optimise | `file.optimise` | §2.2 |
| AI | `ai.summarise`, `ai.ask`, `ai.explainSelection`, `ai.smartFind` | §9.2 |
| Layers | `pane.layers` | E17 |

---

## 16. Single-key shortcuts (opt-in)

Off by default (G-KB4, WCAG 2.1.4). When on, they are active only when the **page well has focus and no text field is editing**. All remappable. They exist for people who mark up a lot, and are modelled on how tool keys work in other readers.

| Key | Command |
|---|---|
| `V` | `markup.select` |
| `H` | `markup.highlight` |
| `U` | `markup.underline` |
| `S` | `markup.strikethrough` |
| `N` | `markup.note` |
| `T` | `markup.freeText` |
| `P` | `markup.ink` |
| `E` | `markup.eraser` |

## 17. Access keys (key tips)

Press and release `Alt`: key tips appear over the tab strip and command bar. The letter then chooses the item. They don't conflict with `Alt+Shift+...` and `Alt+digit` defaults, because a key tip needs the `Alt` **release** first **[design; verify with WinUI `AccessKey`]**.

| Key tip | Item |
|---|---|
| `T` | Tab strip (then arrows) |
| `O` | Open |
| `P` | Panes |
| `N` | Page box |
| `Z` | Zoom box |
| `V` | View menu |
| `M` | Markup |
| `F` | Find |
| `R` | Print |
| `C` | Command palette |
| `D` | Details pane |

## 18. Inventory counts

Counted by hand from the tables above (a compound row such as `tab.goto1` ... `tab.goto8` counts as the number of IDs it names). **Reserved** commands (section 15) are excluded. Three listed IDs are not v1.0: `file.share` (P1, Q14), `pane.signatures` (P1) and `pane.layers` (P2).

| Area | Commands |
|---|---|
| File | 21 |
| Tabs and windows | 15 |
| Navigation and links | 20 |
| View | 22 |
| Panes | 9 |
| Find and selection (including copy and select) | 13 |
| Edit (undo, redo) | 2 |
| Markup | 24 |
| Annotations | 15 |
| Forms | 9 |
| Pages | 14 |
| Attachments | 2 |
| Application | 15 |
| **Total** | **181** (178 without the three non-v1.0 IDs above) |

The count is by hand and may be off by a few. The registry test (rule 1) is the authority. Reconcile this table with the first implementation.

## 19. OPEN QUESTIONS

1. **Q7:** `Page Down` semantics (section 4).
2. **Q14:** the **Share** command.
3. Whether **`Ctrl+Shift+M`** (markup mode) collides with a system or Store-app chord. It's not a Windows shortcut **[unverified]**.
4. **`Ctrl+7`, `Ctrl+4..6`** for layouts are this design's choices. Confirm with the owner.
5. **`Ctrl+R`** reload could be mistaken for rotate. Rotate has `Ctrl+Shift+=/-`. Keep, but test.
6. **`Alt+Shift+...`** defaults (`pane.*`, `pages.move*`) share their modifiers with the Windows input-language switch hotkey on machines with several keyboard layouts. As far as I know the switch fires only when `Alt` and `Shift` are released **without** another key pressed in between **[unverified]**, and other apps use `Shift+Alt+Arrow` shortcuts, so this is probably fine. **Test on a machine with two layouts and the hotkey enabled before M3 closes.** If it misbehaves, the fallback for panes is `Ctrl+Shift+digit` (free, since `Ctrl+digit` is zoom and layout and `Alt+digit` is tabs).
7. Should `Ctrl+Z` also work as undo for **navigation** (undo a page jump)? No: `Alt+Left` is for that.
