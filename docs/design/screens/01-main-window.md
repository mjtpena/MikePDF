# 01 Main window: title-bar tabs, command bar, panes

- Part of the [MikePDF design spec](../README.md). Tokens, breakpoints and control rules are defined there and not repeated here.
- Serves §18 scenarios: 1, 2, 3, 4, 5, 6, 7, 9, 10 (every scenario starts here)
- Replaces: the single-document title bar and the 14-button `CommandBar` in [MainWindow.xaml](../../../src/MikePDF.App/MainWindow.xaml) **[repo]**

## 1. Purpose

The window a document lives in. It must show the page first, keep every command one keystroke away, and make a second document, a pane or a mode cost no more chrome than the first one.

## 2. Layout

### 2.1 Wide window (L, 1280 x 800 epx), one document open, left pane open

```text
+--------------------------------------------------------------------------------------------+
|[M] Annual report.pdf  x | Contract v3.pdf  | Receipts      +                    [_] [[]] [X] | 40  Mica Alt
+--------------------------------------------------------------------------------------------+
|[=]|[<] 12 /612 [>]|[-] 125% v [+]|[pg v]|[Mark]| [find] |            |[prt]|[cmd]|[i]|      | 40  commanding layer
+---------------+-----------------------------------------------------------------+----------+
| [thm][out][ann]|                                                                 |          |
| [att][srch]    |                                                                 |          |
| ---------------|                                                                 |  right   |
|  Thumbnails    |                  +-----------------------+                      |  pane    |
|                |                  |                       |                      | (closed  |
|  +--------+    |                  |   Page 12             |                      |  by      |
|  |  p11   |    |                  |                       |                      |  default)|
|  +--------+    |                  |                       |                      |          |
|  +========+    |                  |                       |                      |          |
|  # p12    #    |                  |                       |                  |||  |          |
|  +========+    |                  +-----------------------+                  |||  |          |
|  +--------+    |                              12 epx gap                      |||  |          |
|  |  p13   |    |                  +-----------------------+                  |||  |          |
|  +--------+    |                  |   Page 13 (partly)    |                  ^ scroll rail   |
+---------------+------------------+-----------------------+------------------+----+----------+
   296 epx                                  page well (opaque)                      320 epx
```

Key to the command bar, left to right (each `|` is a group divider; AutomationIds in section 5):

| Group | Contents | Notes |
|---|---|---|
| Panes | `[=]` Left pane toggle (`pane.toggleLeft`, F4) | Name "Panes" |
| Page | `[<]` previous, **page box** "12" "/ 612", `[>]` next | Inline edit; replaces the `PageIndicator` button. Shows a page label such as "iv" when the document has page labels (E1) |
| Zoom | `[-]`, **zoom box** "125%" with drop-down, `[+]` | Drop-down: Fit width, Fit page, Actual size, 50, 75, 100, 125, 150, 200, 400, **Zoom to...** |
| View | `[pg v]` drop-down button; icon shows the current layout | Layout, scrolling, rotate, page colours, reading mode, highlight form fields |
| Markup | `[Mark]` toggle button | Swaps the centre of this row for the markup tools ([08](08-markup-and-ink.md)) |
| Find | `[find]` | `find.open`, `Ctrl+F` |
| (flexible space) | | |
| File | `[prt]` Print | Save appears here only when the document has edits (`file.save`), left of Print |
| Commands | `[cmd]` Command palette | `app.commandPalette` |
| Details | `[i]` Right-pane toggle | `pane.toggleRight`; badge dot when the open document has comments |

That is 11 buttons (12 when Save is shown) plus the page box and zoom box. Today there are 10 icon buttons plus the page-indicator button **[repo]**, and 7 of those are zoom, fit and rotate controls. Here those 7 become the zoom group and the View menu, so the **number of things on screen does not grow**, while Markup, Print, Commands and Details are added.

### 2.2 Standard laptop (M, 911 x 512 epx = 1366 x 768 at 150%), panes closed

```text
+----------------------------------------------------------------------------+
|[M] Annual report.pdf x | Contract...  +                        [_] [[]] [X] |40
+----------------------------------------------------------------------------+
|[=]|[<] 12/612 [>]|[Zoom 125% v]|[pg v]|[find]|[Mark]|          |[...]|        |40
+----------------------------------------------------------------------------+
|                                                                            |
|                         +--------------------------+                       |
|                         |   page well 432 epx tall |                       |
|                         |                          |                       |
|                         +--------------------------+                       |
+----------------------------------------------------------------------------+
```

At M the zoom group collapses to one **Zoom** drop-down (the `-` and `+` move into its menu and stay on the keyboard), Print, Commands and Details go to the `[...]` overflow.

### 2.3 XS (a window snapped to a quarter screen, 480 epx wide)

```text
+----------------------------------+
|[M] Annual report.pdf x  +  [_][X]|40
+----------------------------------+
|[=] [<] 12 / 612 [>]        [...] |40
+----------------------------------+
|                                  |
|       page, fit width            |
|                                  |
+----------------------------------+
   Tapping [=] slides the left pane over the page (overlay, 280 epx, dim scrim 40%)
```

### 2.4 Tab detail

```text
 +--------------------------------+   modified:   +-----------------------------+
 | [pdf] Annual report.pdf     x  |     --->      | [pdf] Annual report.pdf  .  |  . becomes x on hover/focus
 +--------------------------------+               +-----------------------------+
   16 epx glyph  title (Caption)  close 24x24         tooltip: full path, "Page 12 of 612", "modified"
```

Tab states: normal, selected (accent underline 3 epx), modified (dot), loading (a 16 epx `ProgressRing` replaces the glyph), error (warning glyph), locked (password-protected and not yet unlocked: lock glyph), restored-but-missing (dimmed glyph, "can't be found").

## 3. Behaviour

### 3.1 Tabs and windows (§7.6)

1. A tab is a document session. All windows live in **one broker process**, so a tab can move between windows without reloading the document or restarting an engine session. Only the UI parent changes. (The broker owns file handles and sessions, §5.1.)
2. **Open** a PDF from Explorer, Jump List, drop or `Ctrl+O`: a new tab in the most recent window (setting; Q13 in README). If the file is already open, **activate its tab** and show a brief highlight on the tab rather than opening a duplicate.
3. **`+`** (and `Ctrl+T`) opens a tab showing the empty state ([02](02-empty-state.md)). Opening a file from that tab **replaces** it.
4. **Close** a tab: `Ctrl+W`, the close button, or middle-click. If edits are unsaved, ask ([README 9.3](../README.md#93-undo-save-and-dirty-state)). Closing the last tab leaves an empty-state tab (it does not close the window; `Alt+F4` and the caption button do).
5. **Reopen closed tab**: `Ctrl+Shift+T`. The last 10 closed tabs are remembered with page and zoom, not with unsaved edits.
6. **Reorder** by dragging. **Tear out** by dragging a tab away from the strip: it becomes a new window under the pointer (`TabView.CanTearOutTabs`). **Merge** by dropping a tab on another window's strip. Keyboard alternative: `Ctrl+Shift+N` (`tab.moveToNewWindow`), and **Move tab left / right** in the tab menu (`Ctrl+Shift+PageUp/PageDown`, matching Windows Terminal and browsers **[guess]**).
7. **Many tabs** scroll in the strip with chevron buttons. Min tab width 96 epx, max 240 epx.
8. **Session restore** reopens windows, tabs, pages, zoom, layout and pane state on launch. A missing file restores as a dimmed tab that shows "{name} can't be found" and a **Locate** button ([14](14-errors-and-recovery.md)).
9. **Title:** the window title is "{document name} - MikePDF" (existing string `Window_Title` **[repo]**).

### 3.2 Command bar

- Buttons are icon-only with tooltips ("Zoom out (Ctrl+-)"), as today **[repo]**. Tooltips are generated from the command registry, so the shortcut text is always the current binding.
- Arrow keys move along the bar; `Tab` leaves it. `Enter` and `Space` activate.
- Disabled commands explain themselves: no document ("Open a document first"), owner restriction ("The document's owner doesn't allow printing") ([README G-SR5](../README.md#66-screen-reader-g-sr)).
- **Contextual centre.** In Markup mode ([08](08-markup-and-ink.md)), Organise ([10](10-page-organiser.md)) and Form mode ([09](09-forms.md)) the centre groups (View, Find, Markup) cross-fade (`MotionBase`) to that mode's tools, and a **Done** button appears at the trailing edge. `Esc` or **Done** returns. The row height and the page well do not change. Left (Panes, Page, Zoom) and trailing (Commands, Details) groups are constant.

### 3.3 Panes ([06](06-side-panes.md))

- **Left pane:** `SelectorBar` of icons: Thumbnails, Bookmarks, Annotations, Attachments (only if the document has any), Search results (only after a search), Signatures and Layers (reserved, P1/P2). The header shows the selected pane's name. Closed by default on first open of a document; remembered per document afterwards, with a global default in Settings.
- **Right pane:** tabs **Comment** (thread of the selected annotation), **Format** (properties of the selected annotation or tool defaults). **Ask** (AI) is reserved for M7 and not shown before then (Q16).
- Panes are resizable with a drag handle (4 epx wide visible, 12 epx hit, keyboard: focus the handle and use Left and Right, 16 epx per press). Width is remembered.
- At XS and S the panes overlay and close on outside click or `Esc`.

### 3.4 Page well

- Hosts `PageSurface` (ADR-0006). Overlays (banners, find bar, selection toolbar, undo bar) sit above it and never move the page.
- **Scroll rail:** a 12 epx rail on the right edge, widening to 16 epx on hover. It shows the viewport thumb and, when relevant, ticks for find hits, annotations and the current page. Ticks are visual only (UIA raw view) because the same information is in the panes.
- Click focus: clicking the page well moves keyboard focus to the surface so that shortcuts work.

### 3.5 Never blank

While tiles load the surface shows the low-resolution fallback or a page-shaped placeholder at the correct aspect ratio (ADR-0006). A page never shows an empty rectangle.

### 3.6 Document lifecycle UI

| Event | UI |
|---|---|
| Opening | Tab shows a ring; page well shows a 48 epx `ProgressRing` after 300 ms (not before, to avoid flicker on fast opens) with `Busy` named "Opening" **[repo]** |
| Opened | Tab title and glyph set; polite announcement "{name}, {n} pages" (single page: "{name}, 1 page"); page well shows page 1 or the remembered position |
| Edited | Modified dot on the tab; **Save** appears in the command bar |
| Saving | See [README 9.3](../README.md#93-undo-save-and-dirty-state) |
| Closing with edits | "Save changes to {name}?" **Save / Don't save / Cancel** |
| Engine problem | Banner "Restarting the viewer" ([14](14-errors-and-recovery.md)) |

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Cold start: window and chrome appear first (budget 600 ms, §11), the empty state follows. Opening: tab ring, then page 1 within budget; low-res placeholders while tiles render; pane content shows its own skeleton ([06](06-side-panes.md)). Command bar buttons that need a document are disabled with their reason until the document opens (`IsEnabled=false` today **[repo]**) |
| **Empty** | No tab content but the empty-state page ([02](02-empty-state.md)). The command bar shows Open, Commands and Details only; document commands are disabled |
| **Error** | A document that fails to open leaves its tab with an in-tab error page and keeps every other tab usable ([14](14-errors-and-recovery.md)). The window never shows an error dialog for a single document, except the password prompt |
| **Keyboard focus** | Visible two-ring focus on every control (README 4.8). Landmark order: tab strip, command bar, left pane, page well, right pane (`F6`). Focus on opening a document goes to the page well. After closing a tab, focus goes to the next tab. After closing a pane, focus returns to the button that opened it |
| **High contrast** | No Mica; tab strip and command bar use `Window` with a 1 epx `WindowText` bottom edge. The selected tab uses `Highlight` with `HighlightText` and a 3 epx underline. Modified dot, error glyph and lock glyph use `WindowText`. Scroll rail uses `WindowText` ticks |
| **225% text scale** | Tab titles scale; the strip grows (title-bar guidance **[src]**) to about 52 epx; tabs hit-target stays 32 epx minimum and tab width is `MinWidth` 96 so titles truncate with an ellipsis; tooltip shows the full title. The command bar is icon-only so it does not grow, but the page box and zoom box grow in width. Menus and flyout items scale |
| **Narrow window** | See breakpoints in the [README](../README.md#32-breakpoints) and wireframes 2.2 and 2.3. Panes overlay. The tab strip shows the selected tab plus a chevron list |
| **Touch** | Tab hit height 40 epx (48 in Touch density); close button 32 x 32; command bar buttons 48 x 48 in Touch density; scroll rail drag handle 24 epx; pane resize handle becomes a 24 epx grab area; a long-press on a tab shows its menu |

## 5. Accessibility

### 5.1 UIA tree

| Element | Control type | Accessible name | AutomationId | Patterns, notes |
|---|---|---|---|---|
| Window | Window | "{document name} - MikePDF" | | |
| Tab strip | Tab | "Open documents" | `DocumentTabs` | Selection. Arrow keys move, `Enter` or `Space` selects |
| Tab | TabItem | "{title}" plus ", modified", ", loading", ", password needed", or ", can't be found" as applicable | `Tab_{n}` | SelectionItem, Invoke on close via the `x` child |
| Tab close | Button | "Close {title}" | `TabClose_{n}` | |
| New tab | Button | "New tab" | `NewTabButton` | |
| Command bar | ToolBar | "Document commands" | `Commands` **[repo]** | |
| Panes toggle | Button (toggle) | "Panes" | `SidebarButton` **[repo]** | Toggle pattern. Tooltip "Show or hide the panes (F4)" |
| Previous page / next page | Button | "Previous page" / "Next page" | `PagePreviousButton`, `PageNextButton` | |
| Page box | Edit | "Page, 12 of 612" (with a label: "Page iv, 4 of 612") | `PageIndicator` **[repo, now an Edit]** | Value pattern. Enter goes. Arrow Up and Down step. HelpText "Type a page number and press Enter" |
| Zoom out / in | Button | "Zoom out" / "Zoom in" | `ZoomOutButton`, `ZoomInButton` **[repo]** | |
| Zoom box | ComboBox (editable) | "Zoom, 125 percent" | `ZoomButton` **[repo, now a ComboBox]** | ExpandCollapse, Value |
| View menu | Button with a menu | "View" | `ViewButton` | ExpandCollapse. Items are radio or toggle menu items with names such as "Two pages with cover" |
| Markup | Button (toggle) | "Markup" | `MarkupButton` | Toggle |
| Find | Button | "Find" | `FindButton` **[repo]** | |
| Print, Commands, Details | Button | "Print", "Command palette", "Details" | `PrintButton`, `PaletteButton`, `DetailsButton` | |
| Left pane | Pane (Group) | "Panes" | `SidebarPane` **[repo]** | |
| Pane selector | Tab | "Pane" | `PaneSelector` | Items "Thumbnails", "Bookmarks", "Annotations", "Attachments", "Search results" |
| Page surface | Document | "Document pages" **[repo]** | `PageSurface` **[repo]** | **Text pattern** (S7). Children: links, annotations, form fields in reading order |
| Scroll rail | ScrollBar | "Vertical scroll" | `PageScroll` | RangeValue. Ticks are raw (hidden) |
| Banner | InfoBar (Group) | Severity and title | `Banner` **[repo]** | Live region (`Polite`; `Assertive` for errors that stop the current action) |
| Resize handle | Separator (focusable) | "Resize left pane" | `LeftPaneResizer` | RangeValue |

### 5.2 What Narrator should announce (intended; not yet tested with Narrator)

| Action | Announcement |
|---|---|
| Document opens | "Annual report, 612 pages." Then focus lands on the page surface: "Document pages, page 1 of 612" |
| Tab focus | "Annual report.pdf, modified, tab, 1 of 3, selected" |
| Page box focus | "Page, 12 of 612, edit, type a page number and press Enter" |
| Change page | "Page 13 of 612" (polite; coalesced while scrolling) |
| Untagged document | Once per document: "This document is not tagged. Reading order is estimated." |
| Save succeeded | "Saved" (polite) |
| Pane opens | "Thumbnails pane" then the focused item |
| Engine restarts | Assertive: "Restarting the viewer. Your place is kept." Then polite: "Back to normal." (strings exist **[repo: Banner_EngineRestarting_*]**) |

### 5.3 Focus order

Tab strip (arrow keys inside), `+`, command bar (arrow keys inside), left pane (selector, then content), page well, right pane (selector, then content). `F6` jumps between these six landmarks; `Shift+F6` goes back.

## 6. Keyboard path

| Task | Keys |
|---|---|
| Open | `Ctrl+O` |
| Go to page 12 | `Ctrl+G`, `1`, `2`, `Enter` (focus goes to the page box with its text selected) |
| Next tab / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Tab 3 | `Alt+3` |
| Show or hide panes | `F4` |
| Choose a pane | Focus the selector and arrow keys, or `Alt+Shift+T` (thumbnails), `Alt+Shift+B` (bookmarks), `Alt+Shift+A` (annotations), `Alt+Shift+E` (attachments) |
| Command bar | `F6` to the bar, arrows, `Enter`; or press `Alt` to show key tips |
| Close, reopen | `Ctrl+W`, `Ctrl+Shift+T` |
| Tear out | `Ctrl+Shift+N` |
| Any command | `Ctrl+Shift+P` |

## 7. Engine and protocol implications

- **Same-process tab moves** need no engine message. The broker re-parents a `DocumentSession`.
- E1 page labels (`GetPageLabels`), E3 richer `GetDocumentInfo` (tagged flag for the Narrator announcement).
- Session restore stores per-document view state in the broker's settings, not in the engine.
- Drag of a tab between windows must not tear down the engine. Confirm in the multi-window design that **one engine session survives a window change** (the document broker is not per-window, §5.1).

## 8. OPEN QUESTIONS

1. **Q2:** merge the command bar into the title-bar row when one tab is open, saving 40 epx? This spec says no. The layout would jump when a second tab opens, and tabs and tools would compete for width at M.
2. **Q15:** how a screen reader navigates by headings on a custom surface (scenario 6). Needs S7 follow-up. Candidate: expose structure-tree headings as UIA children with a `Header` control type and heading level in the `StyleId` text attribute. Not verified with NVDA.
3. Should closing the last tab close the window? This spec says no (an empty-state tab stays) so that `Ctrl+W` pressed twice cannot quit the app by accident.
4. Does `Ctrl+Shift+PageUp/PageDown` conflict with anything in the pinned Windows App SDK `TabView` defaults? Verify.
