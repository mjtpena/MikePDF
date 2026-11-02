# 02 Empty state: recents grid, pinned files, drop target

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 1 (reopen the annual report), 7 (a hostile file restores as an error tab, not a crash), 8 (works offline), 10 (large file in recents)
- Replaces: the text list of recents in `EmptyState` in [MainWindow.xaml](../../../src/MikePDF.App/MainWindow.xaml) **[repo]**

## 1. Purpose

What the user sees in a tab that has no document: a new tab (`Ctrl+T`), a launch with no file, or after closing the last document. It must get the user back to a document in one action, and say nothing the user did not ask for.

## 2. Layout

### 2.1 Wide (L), with pinned and recent files

```text
+--------------------------------------------------------------------------------------------+
|[M] New tab  x |  +                                                         [_]  [[]]  [X]  |
+--------------------------------------------------------------------------------------------+
|[=]  ...  (document commands disabled: Open, Commands and Details only)                     |
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|     Open a PDF                                              [ Open a PDF ]   Merge PDFs... |   Title 28/36
|     Drop a PDF on this window, press Ctrl+O, or choose one below.                          |   Body large
|                                                                                            |
|     Pinned                                                                                 |   Subtitle
|     +--------+  +--------+  +--------+                                                     |
|     | thumb  |  | thumb  |  | thumb  |         card: 3:4 thumbnail 144 x 192 epx            |
|     |   P    |  |   P    |  |   P    |         pin glyph shown on the card                  |
|     +--------+  +--------+  +--------+                                                     |
|     Contract v3  Handbook    Tax 2026                                                      |   Body strong, 1 line
|     Documents    Work        Finance                                                       |   Caption, folder
|                                                                                            |
|     Recent                                                          Clear recent files...  |   Subtitle + link
|     +--------+  +--------+  +--------+  +--------+  +--------+  +--------+                 |
|     | thumb  |  | thumb  |  | thumb  |  | thumb  |  | thumb  |  | thumb  |                 |
|     |========|  |==      |  |        |  |        |  |        |  |        |  progress bar    |
|     +--------+  +--------+  +--------+  +--------+  +--------+  +--------+  (last page read)|
|     Annual rep  Receipts    Lecture 4   Lease       Scan 0042   Report Q3                   |
|     Today       Yesterday   Mon         12 Oct      3 Oct       1 Oct                       |
|                                                                                            |
|     [ Make MikePDF your default PDF app? ... ]    <- only the second time this view is seen |
|     No account. No tracking. Your files stay on this device.        <- one quiet line       |
+--------------------------------------------------------------------------------------------+
```

### 2.2 Whole-window drop state

```text
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|   +- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -+   |
|   :                                                                                    :   |
|   :                          [ file glyph, 48 epx ]                                    :   |
|   :                           Open in MikePDF                                          :   |
|   :                                                                                    :   |
|   +- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -+   |
+--------------------------------------------------------------------------------------------+
   Dashed 2 epx accent border, 8 epx inset, scrim at 60% over the grid. Same string as today (Drop_Caption)
```

### 2.3 No recents yet (first launch with no file)

```text
|     Open a PDF                                                                             |
|     Drop a PDF on this window, press Ctrl+O, or choose one below.                          |
|     [ Open a PDF ]                                                                         |
|                                                                                            |
|     Files you open will be listed here. The list stays on this device.                     |
|     No account. No tracking.                                                               |
```

### 2.4 Narrow (XS), list form

```text
+----------------------------------+
| Open a PDF                       |
| [ Open a PDF ]                   |
| Recent                           |
| +----+ Annual report             |
| | th | Documents - Today         |
| +----+                           |
| +----+ Receipts                  |
| | th | Downloads - Yesterday     |
| +----+                           |
+----------------------------------+
```

At 480 epx and below the grid becomes a one-column list of 64 epx rows with a 48 x 64 epx thumbnail.

## 3. Behaviour

1. **Cards.** A card shows: first-page thumbnail (3:4 box, page letterboxed inside it), file name without the `.pdf` extension (Body strong, one line, ellipsis), folder name (Caption, secondary text), last-opened time (Caption, relative: "Today", "Yesterday", weekday, then a date in the user's regional format), and a 3 epx progress line along the thumbnail's bottom edge for the last page read (if the document is more than one page and not at page 1).
2. **Open a card:** click, `Enter` or `Space`. A double-click is not needed. The document opens **in this tab**. If the file is already open elsewhere, switch to it.
3. **Pinned:** user-chosen, shown first, no limit, ordered by drag or by the **Move earlier/later** menu items. **Recent:** the last 30 distinct files by last-opened time. Pinned files don't appear twice.
4. **Card menu** (right-click, long-press, Menu key, `Shift+F10`): Open, Open in new tab, Open in new window, Pin or Unpin, Show in folder, Copy path, Remove from recent. Removing is immediate with an undo bar.
5. **Hover or focus** shows a pin button (24 x 24) on the card's corner and a remove button (24 x 24) on the opposite corner. Both are also in the menu, so neither is hover-only.
6. **Delete** on a focused card removes it from the list (not the file). It never deletes a file.
7. **Missing files.** The list does not check the disk for every card at startup (network shares can stall, and cloud-only placeholders must not be hydrated just to paint a card). Availability is checked lazily when a card is opened. A missing file shows the in-tab error "can't be found" with **Locate...** and **Remove from list**. A card for a file known to be missing is dimmed after one failed open.
8. **Thumbnails are generated at open time** by the pool engine from the page-1 render, stored as small images in the app's local data, keyed by a hash of path and modification time. They are **document content on disk**, so: Settings has **Show previews of recent files** (default on), turning it off deletes the stored previews, and **Clear recent files** removes both the list and the previews. The privacy note in [docs/user/privacy.md](../../user/privacy.md) must state this before the build ships (**[repo: check]**).
9. **Drop.** Dropping files anywhere in the window opens them: one file replaces this tab, several open in tabs. More than 10 asks first (Q12). Non-PDF files show an `InfoBar` "That isn't a PDF" naming the file. Dropping a folder does nothing visible except a banner "Drop PDF files, not folders" **[design]**.
10. **Actions at the top:** **Open a PDF** (`file.open`, accent button) and **Merge PDFs...** (`file.merge`, standard button). No "create", "convert" or "sign" shortcuts, because they would imply features that do not exist (§1.3).
11. **Default-app line** ([15](15-first-run.md)) appears at most once per session and only on this view.
12. **Pinned files and the Jump List** are the same list: pinning in the app pins in the Jump List and vice versa (§7.6).
13. No news, tips carousel, templates, ads, recommendations or "what's new". The only text beyond the controls is the one-line privacy reassurance.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | On launch the heading and buttons draw first. The grid fills as the recents list loads from disk (a few ms). Thumbnails missing from cache appear as a page-shaped placeholder and are generated lazily from visible cards only (never all 30 at once), cancelling when scrolled away. Static placeholder (no shimmer) under reduced motion |
| **Empty** | Wireframe 2.3: no grid; "Files you open will be listed here. The list stays on this device." (existing string `NoRecent.Text` **[repo]**) |
| **Error** | A thumbnail that can't be made shows a generic page glyph with the file name. No error is shown for that. A failed open shows the in-tab error page ([14](14-errors-and-recovery.md)). If the recents list file is unreadable or corrupt, start empty and keep the unreadable file aside; show nothing to the user, and record it in diagnostics (local) |
| **Keyboard focus** | Tab order: Open a PDF, Merge PDFs, Pinned (one group), Recent (one group), Clear recent files, default-app prompt. Inside a group the arrow keys move through cards in reading order, `Home` and `End` jump to first and last, and `Enter` opens. Focus ring on the whole card |
| **High contrast** | Cards have a 1 epx `WindowText` border; focus and selection use `Highlight`; the progress line uses `WindowText`; the dashed drop border is a solid 2 epx `Highlight` outline; thumbnails keep their own content (pages are white paper) |
| **225% text scale** | Card width stays 144 epx for the thumbnail but the caption area is the card's width and grows in height: name wraps to two lines, folder and time wrap, full text in the tooltip and accessible name. The heading wraps. The grid reflows to fewer columns. Buttons keep full text |
| **Narrow window** | Wireframe 2.4: list form; **Open** and **Merge** stack; **Clear recent files** moves into the Recent heading's overflow |
| **Touch** | Cards 48 epx min row height in list form; the pin and remove buttons are always visible (not hover-only) and 40 x 40; long-press shows the menu; a drop of files from another app works through the system drag |

## 5. Accessibility

| Element | Control type | Accessible name | AutomationId |
|---|---|---|---|
| Empty-state region | Group | "Open a PDF" | `EmptyState` **[repo]** |
| Heading | Text (heading) | "Open a PDF" | `EmptyTitle` **[repo]** |
| Open button | Button | "Open a PDF" | `EmptyOpenButton` **[repo]** |
| Merge button | Button | "Merge PDFs" | `EmptyMergeButton` |
| Pinned list | List | "Pinned files" | `PinnedList` |
| Recent list | List | "Recent files" **[repo]** | `RecentList` **[repo]** |
| Card | ListItem | "{name}, in {folder}, {time}, page {n} of {total}" (existing form `Recent_Automation`: "{0}, in {1}" **[repo]**; this adds time and place) | `Recent_{n}`, `Pinned_{n}` |
| Pin button (on card) | Button | "Pin {name}" / "Unpin {name}" | `RecentPin_{n}` |
| Remove button | Button | "Remove {name} from recent files" | `RecentRemove_{n}` |
| Clear link | Hyperlink Button | "Clear recent files" | `ClearRecentButton` |
| Drop overlay | Group | "Open in MikePDF" | `DropOverlay` |

Narrator, intended: on launch with recents "Open a PDF, heading level 1. Open a PDF, button." Entering the grid: "Recent files, list, 6 items. Annual report, in Documents, today, page 12 of 612, list item, 1 of 6." Pin: "Pinned Annual report" (polite). Remove: "Removed Annual report from recent files. Undo available." (polite, the undo bar is reachable by `Ctrl+Z`).

## 6. Keyboard path

| Task | Keys |
|---|---|
| Open the file picker | `Ctrl+O` (works anywhere) |
| Open the most recent file | Focus lands on **Open a PDF**; `Tab` once to the first card, `Enter` |
| Open the 3rd recent | `Tab` to the grid, `Right` twice, `Enter` |
| Pin a card | `Shift+F10`, choose Pin (or `Tab` to its pin button) |
| Remove a card | `Delete` |
| Merge | No default shortcut (see [commands.md](../commands.md)). `Tab` to **Merge PDFs** and `Enter`, or use the palette |
| Clear list | `Tab` to **Clear recent files**, `Enter`, confirm |

## 7. Engine and protocol implications

- **E15:** `RenderThumbnail` at 256 px on the **pool** engine, for recents. The pool engine already exists for the page thumbnails (ADR-0004, ADR-0006), but there is no document open yet at that moment: either the pool engine opens the file only for this purpose, or the thumbnail is generated once, at first open, while the document is already loaded. **This spec chooses the second**: no file is parsed just to paint the start page. Files that fall out of date (modification time changed) get a new thumbnail when next opened.
- Startup budget: the empty state must not wait for the engine. Don't touch `EngineHost.Create` before the window is interactive beyond what the app already does **[repo: `_host = Task.Run(EngineHost.Create)`]**.

## 8. OPEN QUESTIONS

1. Is **Merge PDFs...** appropriate on the empty state, or does it belong only in the File area of the palette? This spec puts it here because scenario 5 starts with no document open.
2. Should recents be shown across devices (OneDrive)? No: out of scope for v1.0 and contradicts "stays on this device".
3. Recents count and pin limit: 30 and unlimited are guesses.
4. Do thumbnails of recent files need to be encrypted at rest (DPAPI)? They show page 1 of documents that may be confidential. This spec says the default is plain files in app-private storage, plus the toggle. The threat model (§13.1, "local data exposure") should decide.
