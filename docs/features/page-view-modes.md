# Feature spec: single-page, two-page and presentation modes

- Status: Draft for Opus review
- Milestone: M3 (full reader)
- Master prompt: §2.1 (continuous scroll, single page, two-page facing with and without cover, presentation mode), §7.1, §10.2 (Ctrl+L), §11 (scroll and zoom budgets), §10.3
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: `MikePDF.Core.Layout` (`DocumentLayout`, `ViewportController`, `ZoomMath`, `PageProjection`), [command-palette-shortcuts](command-palette-shortcuts.md)
- Not in this spec: full screen (F11) and page colour modes belong to `m2-reader-gaps.md`. Presentation mode here **builds on** full screen and assumes that spec provides "hide all chrome and cover the monitor".

Conventions are in [README.md](README.md).

## 1. Where the code is today

`DocumentLayout` stacks pages in **one column** (continuous scroll). `FitMode` is `None`, `Width` or `Page`. `DocumentPosition` stores page, page fraction, zoom, fit and view rotation, but no view mode. Everything that maps between screen and page (selection, find highlights, links, the UIA text peer) goes through `PageProjection` over `DocumentLayout`. Introducing spreads means generalising the layout, and every consumer of it must keep working. That is most of the cost of this feature.

## 2. User stories

1. As a contract reviewer on a 4K monitor, I read two facing pages at once, with the cover alone on the right as in a printed book (§18 scenario 2).
2. As someone reading a scanned book, I turn pages one spread at a time with Page Down or the arrow keys, rather than scrolling.
3. As a presenter, I show a slide-deck PDF full screen on a projector with black around the slide, advance with a clicker, and nothing on screen but the slide.
4. As a reader of a right-to-left document (Arabic or Hebrew book, or a manga PDF), facing pages appear in the right order.
5. As a Narrator user, I know which mode I am in and which page I am on after each turn.
6. As any user, my choice of mode is remembered for each document and I can set a default.

## 3. Detailed behaviour

### 3.1 The mode model

A view mode is two independent choices.

| Choice | Values |
|---|---|
| **Arrangement** | `Single`, `Facing` (pages 1-2, 3-4 and so on), `FacingWithCover` (page 1 alone, then 2-3, 4-5) |
| **Flow** | `Continuous` (spreads stacked and scrolled), `Paged` (one spread at a time) |

`Single` + `Continuous` is the existing behaviour and the default. **Presentation** is a third kind of state, `Paged` + `Single` + `FitMode.Page` + chrome hidden, entered from any mode and returning to it.

OPEN QUESTION OQ-PV-1: §2.1 lists "two-page (facing, with/without cover)" and does not say whether two-page **continuous** is required. This spec includes both flows because the layout work is the same, and because Edge and Acrobat offer them. If Opus wants less, drop `Facing`+`Continuous` first; nothing else changes.

### 3.2 Spreads

A **spread** is one or two pages.

- `Single`: every page is its own spread.
- `Facing`: spreads are [1,2], [3,4], and so on. If the page count is odd, the last page is alone, on the left (LTR) or right (RTL).
- `FacingWithCover`: [1] alone on the right side of the spread slot, then [2,3], [4,5]. A document with one page is a single spread.
- Right-to-left binding (`/ViewerPreferences /Direction /R2L`): the lower-numbered page of a spread is on the **right**. Cover is on the left. See 3.7.
- Page sizes can differ inside a spread. Pages are aligned on their vertical centres (the design spec may override). Spread width is the sum of the two page widths plus the page gap; height is the larger page height. A lone page in a facing arrangement keeps its half-slot: the empty half is blank, so a cover does not jump to the middle.
- A page with a view rotation (`Ctrl+Shift+=`) is rotated individually; spreads are not rotated as a unit. Page `/Rotate` and CropBox are already applied by `PageGeometry` before layout, so spread layout uses displayed sizes. A spread containing a landscape and a portrait page is legal.

### 3.3 Layout and zoom

- `DocumentLayout` becomes a layout over spreads. It keeps the existing public surface where possible (`Top(page)`, `Width(page)`, `PageCount`), adds `SpreadCount`, `SpreadOf(page)`, `PagesOf(spread)`, `Left(page)`, and an `Arrangement`/`Flow` pair. The existing continuous single-column layout must produce **bit-identical** results to today's, which the existing `LayoutTests` and `ProjectionTests` already assert and must keep passing unchanged.
- **Fit width** fits the widest spread's width to the viewport. **Fit page** fits the current spread (both dimensions). In `Paged` flow, fit page re-evaluates when the spread changes, because the next spread may be a different size. In `Continuous` flow, fit page uses the current spread at the moment it is applied and then holds that zoom (as today).
- Zoom limits and the 120 ms re-raster settle (§7.1 rule 6) are unchanged. The 4800%-at-100% stretching limit in `STATUS.md` still applies.
- Between spreads in `Continuous` flow the gap equals the existing page gap; between the two pages of a spread it is a smaller gap (design spec).

### 3.4 Navigation

| Input | `Continuous` | `Paged` |
|---|---|---|
| Page Down, Space | scroll one viewport (existing) | If the spread extends below the view, scroll down by a viewport minus a 10% overlap; at the bottom, go to the next spread. |
| Right arrow, Down arrow | existing | next spread if the whole spread fits the view; otherwise scroll (Down) or pan (Right) |
| Page Up, Shift+Space | existing | mirror of Page Down |
| Home, End | first or last page | first or last spread |
| Go to page N (Ctrl+G) | scroll so page N is at the top | show the spread containing N |
| Mouse wheel | scroll | scroll within the spread; **one further notch** after reaching the end turns the page (prevents accidental turns) |
| Touch swipe horizontal | pan | next or previous spread when the spread fits the view, else pan |
| Back/forward history (m2 spec) | unchanged | targets the page; the mode shows its spread |

The "current page" for the page indicator, thumbnail highlight and `DocumentPosition` is the **first page of the spread that holds the most visible area**. The thumbnail sidebar highlights **both** pages of the current spread.

### 3.5 Everything that works on a page still works

Each of these must work unchanged on both pages of a spread, because they are all `PageProjection` consumers. The acceptance criteria test them in a facing mode explicitly.

- Text selection, including a selection that starts on the left page and ends on the right page. Selection order is page-index order, in both LTR and RTL.
- Find highlights and "current match" navigation.
- Link hit-testing and activation.
- The UIA text pattern: the page surface still exposes the pages in page order; the visible-ranges call returns ranges from both pages of the spread.
- Marquee zoom, pinch zoom, Ctrl+wheel zoom: zoom about the cursor over a spread.
- Annotations and form fields (M4): hit-testing, handles and keyboard focus per page.

### 3.6 Presentation mode

Command `view.presentation` (Ctrl+L). Enters full screen on the monitor that holds the centre of the window, using the full screen implementation from the m2 spec, then:

- Layout: `Paged`, `Single`, fit page, centred, background black, no chrome, no scroll bars, no page gap.
- **Advance:** Right, Down, Page Down, Space, Enter, N, left click (not on a link), mouse wheel down, a clicker's "next" (sent as Page Down or Right by most clickers). **Back:** Left, Up, Page Up, Shift+Space, Backspace, P, right click, mouse wheel up. **Home / End:** first / last page. Typing digits then Enter goes to that page (an on-screen number appears while typing, then disappears 1.5 s after Enter or Esc).
- **Black and white screen:** B or full stop toggles black, W or comma toggles white, any other navigation key restores the page. This is the PowerPoint convention that presenters expect. Proposed, not in §10.2.
- **Cursor:** hidden after 3 seconds of no movement, shown on movement. Hidden immediately when navigating by key.
- **Links:** a left click on an internal link follows it. An external link is **not** followed; it shows the same address-and-copy prompt as normal reading, because the app's policy never opens external addresses silently (see `STATUS.md`, external links). OPEN QUESTION OQ-PV-4: whether the prompt should be suppressed in presentation mode with the link just ignored.
- **Annotations** are drawn (tile flag) but annotation tools, popups and editing are off. Form fields are drawn but not interactive.
- **Exit:** Esc, F11, or Ctrl+L. Exiting returns to the previous mode, page and zoom.
- **Power:** while presenting, the display and system are kept awake (`SetThreadExecutionState` with `ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED | ES_CONTINUOUS`, cleared on exit and on process end). Notifications are Windows' business (Focus assist); the app does not change system settings.
- **Pen and touch:** tap in the right two-thirds advances, tap in the left third goes back; swipe left or right moves one page. The pen barrel button is unassigned by default. OPEN QUESTION OQ-PV-2: a laser pointer (a red dot that follows the pointer while a key or the barrel button is held). It is cheap to build as an overlay. It is **not** in §2.1, so it is listed here as a candidate, not scope.
- **Not in scope:** presenter view with notes and a second screen, slide transitions (`/Trans`), and auto-advance timing (`/Dur`) from the PDF. OPEN QUESTION OQ-PV-3: whether `/Dur` auto-advance should be honoured in presentation mode (Acrobat does in full-screen mode).
- If the monitor is removed during a presentation, presentation mode exits to the previous window.
- Prefetch: the next two pages and the previous one are rendered at presentation scale ahead of time.

### 3.7 Right-to-left binding

The document's `/Direction` selects the arrangement order. It comes from a new small engine message, `GetViewerPreferences` (defined in [print](print.md), since printing also needs it). A user override `view.direction` (`Auto`, `LeftToRight`, `RightToLeft`) in the View menu wins over the document. In `Continuous`+`Single` the setting has no visible effect. `Home` is still page 1; "Right arrow" in `Paged` RTL means the previous spread, matching the visual direction. This is **not** mirrored for presentation mode (slides advance forward regardless).

### 3.8 Remembering the mode

`DocumentPosition` gains `ViewMode` (arrangement and flow) and `Direction` override. Old records without them load as `Single`+`Continuous`. The setting `view.defaultMode` ([settings-policies](settings-policies.md)) is used for documents with no record. The document's `/PageLayout` (for example `TwoPageRight`) could pick the initial mode. PDFium may not expose it, which is **UNVERIFIED**; OPEN QUESTION OQ-PV-5: honour `/PageLayout` if the engine can supply it, or ignore it.

## 4. Keyboard, pen and touch

| Command | Default | Id |
|---|---|---|
| Single page | Ctrl+Alt+1 | `view.single` |
| Two pages (facing) | Ctrl+Alt+2 | `view.facing` |
| Two pages with cover | Ctrl+Alt+3 | `view.facingCover` |
| Toggle continuous / paged | Ctrl+Alt+0 | `view.toggleFlow` |
| Presentation | Ctrl+L | `view.presentation` |
| Next / previous spread (paged) | Page Down / Page Up, Right / Left when the spread fits | `nav.nextPage`, `nav.previousPage` |

The Ctrl+Alt+digit defaults are proposals; §10.2 gives only Ctrl+L. Pen and touch are in 3.4 and 3.6. Pinch and marquee zoom behave as in the continuous mode.

## 5. Accessibility

- The mode group is a radio group in the View menu ("Single page", "Two pages", "Two pages with cover" and "Continuous scrolling" as a check item). Each has an accessible name and exposes its state.
- After changing mode, a polite live-region announcement: "Two pages with cover, continuous. Pages 4 and 5 of 40." After each page turn in `Paged` flow: "Pages 4 and 5 of 40" or "Page 4 of 40".
- The page surface's UIA peer still exposes the document text in page order. In a facing spread, `GetVisibleRanges` returns ranges from both pages. Narrator's reading-by-page works unchanged.
- Presentation mode: on entry, "Presentation mode. Page 1 of 30. Press Escape to exit." On each turn, "Page {n} of {total}" (polite). On exit, "Presentation ended. Page {n} of {total}." The black and white screen announce "Screen black" and "Screen restored".
- The Esc hint is shown on screen for 3 seconds at entry (design spec), as text, not only as an icon. It respects reduced motion (no animation).
- No state is conveyed by colour alone. High contrast: the page surface is unchanged, but presentation mode keeps the page's own colours on black; the m2 page-colour modes apply if the user turned one on.
- Text scaling to 225% affects the chrome only; the page is scaled by zoom.

## 6. Error states

| Situation | Behaviour |
|---|---|
| A page size lookup fails or a page is damaged | The page occupies its default slot (letter size); the existing partial-page chip appears; spread layout continues. |
| Page count 0 or 1 | Facing modes are available but identical to `Single` (a lone cover). No error. |
| Pages with huge size differences (A0 next to a business card) | Allowed; the smaller is centred. Fit page uses the larger. No scaling to equalise (that would change the document). |
| Presentation mode while a dialog is open | Dialogs are modal and hidden-chrome rules still apply; presentation is not entered until the dialog closes (the command is disabled with the reason in the palette). |
| Full screen API fails | Presentation falls back to a maximised borderless window and shows a notice. |

## 7. Edge cases

- **Rotated pages / CropBox offsets:** layout uses displayed sizes from `PageGeometry`; the unit tests include a spread of a 90-degree page and a non-zero-origin CropBox page and assert the page rectangles and the projection round-trip.
- **Encrypted documents:** no effect, but the mode is applied after the password dialog succeeds, not before.
- **Huge documents:** a 10,000-page document in `Facing`+`Continuous` must not fetch all page sizes before the first paint (existing lazy sizes). Spread positions are computed lazily from the sizes known so far, with the same placeholder-size rule as today, and corrected as sizes arrive without moving the current spread (anchor preserved).
- **Mixed page sizes arriving late:** when sizes arrive after the first paint, the anchor spread must not jump. The existing `LayoutAnchor` is generalised to `(page, fractionX, fractionY)` within the spread.
- **Mode change preserves position:** the first visible page stays in view after any mode change, including `Single` to `FacingWithCover` where the page moves into a different spread.
- **Odd page counts and covers:** a 3-page document in `FacingWithCover` is [1], [2,3]; in `Facing` it is [1,2], [3].
- **Very small windows:** fit width is clamped to the minimum zoom (10%); a facing spread still renders.
- **High-DPI and mixed-DPI monitors:** tile scale follows the existing per-monitor DPI path.
- **Hostile input:** a `/Direction` value that is not `L2R` or `R2L` is `Auto`. Page sizes of zero or beyond the 14,400 pt schema limit are rejected by the existing reply checks. Page counts up to 10,000,000 must not make spread arrays quadratic.

## 8. Undo and redo

View modes are view state, not document edits. They are not on the undo stack and do not mark the document dirty.

## 9. Journal entries

None. `DocumentPosition` and `session.json` ([tabs-windows-session](tabs-windows-session.md)) carry the mode.

## 10. Save behaviour

None. The view mode is never written into the PDF (no `/PageLayout` write-back).

## 11. Engine and protocol additions

| Message | Id | Notes |
|---|---|---|
| `GetViewerPreferences` | 26 | Defined in [print](print.md) section 11; this feature reads `direction`. |

No other change. Rendering already supports any tile at any page.

## 12. Localisation strings

| Key | en-US |
|---|---|
| `View_Single` | Single page |
| `View_Facing` | Two pages |
| `View_FacingCover` | Two pages with cover |
| `View_Continuous` | Continuous scrolling |
| `View_Direction_Auto` | Reading direction: automatic |
| `View_Direction_LTR` | Left to right |
| `View_Direction_RTL` | Right to left |
| `View_Announce_Mode` | {0}. {1} |
| `View_Announce_Pages_One` | Page {0} of {1} |
| `View_Announce_Pages_Two` | Pages {0} and {1} of {2} |
| `Present_Enter_Announce` | Presentation mode. Page {0} of {1}. Press Escape to exit. |
| `Present_Turn_Announce` | Page {0} of {1} |
| `Present_Exit_Announce` | Presentation ended. Page {0} of {1}. |
| `Present_Hint` | Press Esc to exit |
| `Present_Black_Announce` | Screen black |
| `Present_Restored_Announce` | Screen restored |
| `Present_GoTo` | Go to page {0} |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-PV-1 | Is two-page **continuous** in scope for v1? | User (product) |
| OQ-PV-2 | Laser pointer in presentation mode: in or out for v1? | User (product) |
| OQ-PV-3 | Honour `/Dur` auto-advance and `/Trans` in presentation mode? | User (product) |
| OQ-PV-4 | Suppress the external-link prompt in presentation mode? | Opus (security) |
| OQ-PV-5 | Honour `/PageLayout` if PDFium can read it (unverified)? | Opus |
| OQ-PV-6 | Should the default facing arrangement for wide spreads use equal-width slots or each page's own width? Design spec decides the look; this spec assumes the pages' own widths. | Design |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| PV-01 | Spread arithmetic: for page counts 0, 1, 2, 3, 10, 11 and each arrangement, `SpreadOf`/`PagesOf` give the lists in 3.2, including RTL. | Unit (`MikePDF.Core.Tests`) |
| PV-02 | Regression: the existing `LayoutTests`, `ProjectionTests`, `ViewportController` tests pass unmodified and `Single`+`Continuous` rectangles are identical to the old layout for 1,000 random page-size sets. | Unit |
| PV-03 | Spread geometry for mixed sizes: heights, centring, lone-page half-slot, and cover on the correct side in LTR and RTL. | Unit |
| PV-04 | Rotated pages (all four `/Rotate` values and all four view turns) and a non-zero CropBox origin give consistent rectangles; `PageProjection` round-trips a page point through screen and back within 0.01 pt on both pages of a spread. | Unit |
| PV-05 | Fit width and fit page on a spread equal the specified formulas; in `Paged` flow fit page recomputes on spread change. | Unit |
| PV-06 | Mode change keeps the first visible page in view for every pair of modes (36 combinations) on a 40-page fixture. | Unit |
| PV-07 | `DocumentPosition` round-trips `ViewMode`; a record without it loads as `Single`+`Continuous`. | Unit |
| PV-08 | Paged navigation: Page Down scrolls then turns; the extra wheel notch is required to turn; Home/End go to first/last spread. | Unit (input model) |
| PV-09 | Text selection from the left page to the right page of a spread returns text in page-index order, LTR and RTL. | Unit + Integration |
| PV-10 | Find: a match on the right page of a facing spread is highlighted at the right place and "next match" crosses the spread boundary correctly. | Integration |
| PV-11 | A link on the right page of a facing spread activates (hit test through the projection). | Integration |
| PV-12 | In `Facing`+`Continuous`, opening a 10,000-page fixture paints the first spread within the §11 budget for large files (1.0 s) and no frame has blank coverage while scrolling (frame composer log). | Integration (perf) |
| PV-13 | Late-arriving page sizes do not move the anchor spread by more than 1 px in layout space. | Unit |
| PV-14 | Presentation enter/exit: restores the previous mode, page, zoom, and the system execution state is cleared on exit and on abnormal exit (verified by querying the state). | Integration + UI journey |
| PV-15 | Presentation key map (3.6): every listed key and click advances or goes back; digits+Enter goes to page; B/W toggle black and white; a link click follows an internal link. | UI journey |
| PV-16 | Presentation is chrome-free: no scroll bars, title bar, command bar or sidebar in the UIA tree except the surface and the live region. | UI journey (a11y) |
| PV-17 | Page turn in presentation mode: a placeholder frame is composed on the first frame after the key press; sharp tiles within 150 ms at 1920x1080 on a typical text deck (**proposed budget, not measured**). | Integration (perf) |
| PV-18 | `GetViewerPreferences` direction `R2L` produces RTL arrangement; `view.direction` override beats the document. | Integration |
| PV-19 | Narrator-facing strings in section 12 are raised once per mode change and once per turn (counted from UIA events). | UI journey (a11y) |
| PV-20 | Keyboard-only: change modes, turn pages, enter and leave presentation with the keyboard. | UI journey |
| PV-21 | Monitor removal during presentation exits presentation without losing the document. | Integration (simulated display change) |
| PV-22 | Hostile: `/Direction` junk values, 0x0 and 15,000 pt pages, 10,000,000-page counts never throw and never allocate more than O(pages) memory in the layout. | Unit + fuzz-style cases |
| PV-23 | Pseudo-RTL build: the mode menu and the presentation hint mirror correctly. | UI journey (visual) |
