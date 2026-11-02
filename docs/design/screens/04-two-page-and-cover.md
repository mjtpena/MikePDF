# 04 Two-page and cover modes (and the paged and continuous layouts)

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 2 (contract in two-page mode on a 4K monitor), 10 (big file: layout must not need every page size up front)
- Replaces: the current single-column layout. Layout choice is new; today there is only continuous single pages **[repo: STATUS "Known limits"]**

## 1. Purpose

Let the user read the way the document was designed: facing pages like a book, with or without a standalone cover, scrolling or turning. Two independent choices:

| Choice | Options | Default |
|---|---|---|
| **Scrolling** | Continuous scroll, or One page (or spread) at a time | Continuous |
| **Pages** | Single, Two pages, Two pages with cover | Single |

That is six layouts. They are chosen in the **View menu** of the command bar (and the palette, and the shortcuts below), never in a dialog.

## 2. Layout

### 2.1 View menu

```text
 [pg v]
 +-----------------------------------------+
 | Pages                                   |
 |   ( ) Single page            Ctrl+4     |
 |   (o) Two pages              Ctrl+5     |
 |   ( ) Two pages with cover   Ctrl+6     |
 | Scrolling                               |
 |   [x] Continuous scroll      Ctrl+7     |
 | ---------------------------------------- |
 | Zoom to fit                             |
 |   Fit width                  Ctrl+2     |
 |   Fit page                   Ctrl+1     |
 |   Actual size                Ctrl+0     |
 | Rotate view left   Ctrl+Shift+-         |
 | Rotate view right  Ctrl+Shift+=         |
 | ---------------------------------------- |
 | Page colours                         >  |
 |   (o) Normal  ( ) Dark  ( ) Sepia  ...   |
 | [ ] Highlight form fields               |
 | Reading mode                  F11       |
 | Presentation                  Ctrl+L    |
 +-----------------------------------------+
```

The button icon shows the current layout (one page, two pages, two pages with cover), so the user can see it without opening the menu.

### 2.2 Two pages with cover, continuous (a 40-page contract, 3840 x 2160 at 150%, 2560 x 1440 epx)

```text
+-------------------------------------------------------------------------------------------+
| [command bar]                                                                             |
+-------------------------------------------------------------------------------------------+
|                                                                                           |
|                         +-------------------+                                             |
|                         |                   |                                             |
|                         |      cover        |       spread 1: the cover is alone,         |
|                         |      page 1       |       on the RIGHT, as in a book            |
|                         +-------------------+                                             |
|                                                                                           |
|            +-------------------+-------------------+                                      |
|            |                   |                   |                                      |
|            |      page 2       |      page 3       |       spread 2: 2 epx gutter between |
|            |                   |                   |       the pages, 16 epx to the next  |
|            +-------------------+-------------------+       spread                         |
|                                                                                           |
|            +-------------------+-------------------+                                      |
|            |      page 4       |      page 5       |                                      |
+-------------------------------------------------------------------------------------------+
```

### 2.3 Two pages, paged, fit page (no cover): spread 1 is pages 1 and 2

```text
+---------------------------------------------------------------+
| [command bar]                                                 |
+---------------------------------------------------------------+
|        +--------------------+--------------------+            |
|        |                    |                    |            |
|        |       page 1       |       page 2       |            |
|        |                    |                    |            |
|        +--------------------+--------------------+            |
|                   [<] spread 1 of 20 [>]  (page box shows "1-2 / 40")           |
+---------------------------------------------------------------+
```

### 2.4 Pairing rules, by example

For a 7-page document:

| Mode | Spreads |
|---|---|
| Single | 1 / 2 / 3 / 4 / 5 / 6 / 7 |
| Two pages | 1-2 / 3-4 / 5-6 / 7 (the last page sits alone on the **left**) |
| Two pages with cover | 1 / 2-3 / 4-5 / 6-7 |
| Right-to-left binding (`/Direction R2L`), two pages with cover | 1 / **3-2** / **5-4** / **7-6** (pages are mirrored within the spread) |

## 3. Behaviour

1. **Spread** means one or two pages shown side by side with a 2 epx gutter. A spread is the unit of paging and of the page box in paged scrolling.
2. **Mixed page sizes.** The two pages of a spread are **centred vertically** on each other. If one page is landscape and the other portrait, they are still paired (the pairing is by index, not by shape). **OPEN QUESTION** below.
3. **Rotation.** The view rotation (`Ctrl+Shift+=`) applies to the whole spread. A page whose `/Rotate` differs is rotated for itself first.
4. **Cover.** In "with cover", page 1 is the lone right-hand page of the first spread. The cover mode has no effect on a one-page document.
5. **Last page.** If the page count leaves one page over, it is shown alone on the left (right in R2L).
6. **Zoom.** Fit width means the **spread's** width fits the well. Fit page fits the whole spread. Actual size is 100% of each page.
7. **Paged scrolling.** `Page Down`, `Space`, `Right`, the mouse wheel past the end of the visible spread (with a 250 ms resistance so that one wheel flick turns one spread), and swipes turn the spread. When the spread is taller than the well (zoomed in), the same keys first scroll to the end of the spread, then turn.
8. **Continuous.** Spreads stack with 16 epx between them. Scrolling is smooth and tiles follow ADR-0006. The page box shows the number of the spread's **first** visible page and the label, e.g. "12-13 / 40" when both are meaningfully visible, and "12 / 40" when only one is.
9. **Remembered** per document (layout, scrolling, zoom mode) and as a default in Settings, Reading. A change made through the menu is applied immediately and the **current page stays in view** (the view anchors on the first visible page's top edge and keeps it).
10. **Windows size.** At XS and S widths, two-page layouts are **allowed but offered cautiously**: the menu items stay enabled, but fit-width in two-page mode at those widths makes pages very small. The page box and zoom reflect that. The app does not silently switch layout when the window narrows. **[design]**
11. **Auto-suggest is off.** The app doesn't detect a wide window and suggest two-page mode with a popup (P2 / "no interruption"). Settings has **Use two pages automatically on wide windows** (off by default; threshold 1800 epx; only portrait pages) for users who want it.
12. **Performance.** Layout is **lazy** (ADR-0006 rule 4, "lay pages out lazily without fetching every page size before first paint"). Pairing needs only indices. Heights of unmeasured pages use the first page's size as an estimate and correct as sizes arrive, **keeping the viewport anchored** (no jump). In a 5,000-page file the scroll range grows as sizes arrive, and the scroll thumb may move slightly. That's unavoidable and acceptable.
13. **Annotations and form fields** on facing pages: hit testing and focus use the page that was hit. A tab-order walk follows page order, left then right.
14. **Text selection across a spread:** a drag from the left page to the right page selects through the end of the left page and from the start of the right page, in page order. Column selection (`Alt+drag`) is per page.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Placeholders are drawn at the estimated spread size; the first spread is rendered first and the layout does not shift when the second page of a spread arrives (its slot is reserved at the same height as its partner until its size is known) |
| **Empty** | View menu layout items are disabled with "Open a document first". The page box reads "No page" (existing string `PageIndicator_None` **[repo]**) |
| **Error** | If one page of a spread fails, its slot shows the page error chip (see [14](14-errors-and-recovery.md)) and the partner page renders normally. A document with zero readable page sizes falls back to Single and shows a banner "This document's pages couldn't be measured. Showing one page at a time" **[design]** |
| **Keyboard focus** | Focus is on the surface; the focus ring marks the focused **page** (not the whole spread). Left and Right Arrow move between pages of a spread in paged mode when the page fits the width, otherwise they scroll |
| **High contrast** | A 2 epx `WindowText` gutter between pages (a visible seam), no shadow, 1 epx page outline |
| **225% text scale** | The layout is unaffected (document content). The View menu items scale and the menu may become scrollable. The page box grows |
| **Narrow window** | See section 3, item 10. The View menu stays the same and the first two-page pairing at a narrow width fits to width, which is small by nature |
| **Touch** | Pinch zoom anchors on the pinch centre. A horizontal swipe in paged mode turns the spread **only** when the spread is at fit-page or the user is at the edge of a zoomed spread; otherwise it pans |

## 5. Accessibility

| Element | Type | Name | AutomationId |
|---|---|---|---|
| View button | Button with menu | "View" (the layout is in the HelpText: "Layout: two pages with cover, continuous") | `ViewButton` |
| Menu items | MenuItem (radio) | "Single page", "Two pages", "Two pages with cover" | `LayoutSingle`, `LayoutTwo`, `LayoutTwoCover` |
| Continuous toggle | MenuItem (toggle) | "Continuous scroll" | `LayoutContinuous` |
| Page box | Edit | "Pages 12 to 13 of 40" in a spread, "Page 12 of 40" alone | `PageIndicator` |
| Surface | Document | "Document pages" | `PageSurface` |
| Spread (UIA group) | Group | "Spread 6, pages 12 and 13" | `Spread_{n}` |

Narrator, intended: choosing a layout announces the new state: "Two pages with cover, continuous. Page 12 of 40." A spread turn in paged mode: "Pages 12 and 13 of 40". The Text pattern **keeps reading order by page**: reading by line on a spread reads the left page fully, then the right page. Spreads are exposed as groups only in the UIA tree, so Narrator's "next item" goes page by page.

Focus order inside a spread: the left page's links and annotations in reading order, then the right page's (R2L reverses the pages).

## 6. Keyboard path

| Task | Keys |
|---|---|
| Single page, two pages, with cover | `Ctrl+4`, `Ctrl+5`, `Ctrl+6` |
| Toggle continuous scroll | `Ctrl+7` |
| Fit page / fit width | `Ctrl+1` / `Ctrl+2` |
| Next / previous spread (paged) | `Page Down` / `Page Up`, or `Space` / `Shift+Space` |
| First / last page | `Home` / `End` |
| Go to page | `Ctrl+G` |
| Choose by menu | `F6` to the command bar, to **View**, `Enter`, arrows |
| Choose by palette | `Ctrl+Shift+P`, "two pages" |

The order (single, facing, book) matches Sumatra's `Ctrl+6/7/8` **[src: [Sumatra shortcuts](https://www.sumatrapdfreader.org/docs/Keyboard-shortcuts)]**, moved to `Ctrl+4/5/6` so that the digits stay together with `Ctrl+1/2` (fit) and `Ctrl+0` (actual size) from §10.2. All are remappable.

## 7. Engine and protocol implications

- **E2:** the document's viewer preferences (reading direction, `/PageLayout`, `/PageMode`) come back in `GetDocumentInfo`. `/PageLayout` is only a **hint**: it sets the default layout the first time a document is opened (e.g. `TwoPageRight` selects Two pages with cover) and the user's choice wins afterwards. **[design]**
- Layout needs page sizes by index for the visible range and a prefetch window only, not all sizes (existing `GetPageSizes` ranges, ADR-0006 rule 4 **[repo]**).
- No new tile semantics: a spread is two page surfaces in one layout.

## 8. OPEN QUESTIONS

1. **Mixed orientations:** when a landscape page sits next to a portrait page, should the spread break into two single pages? Acrobat-style readers pair by index **[unverified]**. This spec pairs by index and keeps it predictable. Revisit after the usability test.
2. Should `/PageLayout` and `/PageMode` set the default layout on first open? This spec says yes, once.
3. Does the user want **Two pages** to default to cover for documents with an odd page count? No: keep it explicit.
4. The shortcut numbers `Ctrl+4` to `Ctrl+7` have no precedent outside Sumatra's layout. Confirm with the owner, since §10.2 doesn't list them.
