# 03 Reading mode (F11) and presentation mode (Ctrl+L)

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 2 (reading a contract on a 4K monitor), 6 (a screen-reader user in reading mode), 10 (a big file)
- Related: [04 two-page and cover](04-two-page-and-cover.md) for the page layouts both modes use

## 1. Purpose

Two modes that give the page the whole screen.

| | Reading mode | Presentation mode |
|---|---|---|
| Key | `F11` | `Ctrl+L` |
| For | Reading, studying, reviewing | Showing slides to a room |
| Window | True full screen (Q6) | True full screen |
| Layout | The user's current layout, zoom and scrolling (continuous or paged) | Fit page, paged, one page or one spread; black surround |
| Controls | Auto-hiding HUD; the full chrome on request | None; a small page hint on pointer move |
| Input | Everything works, including find, copy, markup | Navigation only. No editing. Markup tools are not available |
| Exit | `F11` or `Esc` | `Esc` (or `F11`, or the `Exit` button on the pointer-move bar) |

## 2. Layout

### 2.1 Reading mode, controls hidden

```text
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|                                                                                            |
|                          +-----------------------------+                                   |
|                          |                             |                                   |
|                          |                             |                                   |
|                          |           page              |                                   |
|                          |                             |                                   |
|                          +-----------------------------+                                   |
|                                                                                            |
|                                                                                            |
+--------------------------------------------------------------------------------------------+
   No chrome at all. A 2 epx accent line along the bottom edge shows reading progress, off by default.
```

### 2.2 Reading mode with the HUD (pointer moved, or any key)

```text
+--------------------------------------------------------------------------------------------+
|                                                                                            |
|                          +-----------------------------+                                   |
|                          |           page              |                                   |
|                          |                             |                                   |
|                          +-----------------------------+                                   |
|                                                                                            |
|        +------------------------------------------------------------------------+          |
|        | [<] 12 / 612 [>] | [-] 125% [+] | [find] | [pg v] | [Exit reading mode] |   HUD    |
|        +------------------------------------------------------------------------+          |
+--------------------------------------------------------------------------------------------+
   HUD: floating pill, bottom centre, 8 epx above the edge, overlay layer, 40 epx high.
   Hides after 2.5 s without pointer movement and without keyboard focus inside it.
```

### 2.3 Reading mode with the full chrome revealed (pointer at the top edge, `Alt` or `F10`)

```text
+--------------------------------------------------------------------------------------------+
|[M] Annual report.pdf x | Contract v3.pdf |  +                                             |  overlay,
|[=]|[<] 12 / 612 [>]|[-] 125% v [+]|[pg v]|[Mark]|[find]|                 |[prt]|[cmd]|[i]|  80 epx
+--------------------------------------------------------------------------------------------+
|                          +-----------------------------+                                   |
|                          |           page (does not move)                                  |
```

The chrome slides over the page (`MotionBase`) and hides when the pointer leaves it for 1 s or on `Esc`. The page does not move. No caption buttons (true full screen); `Esc` and the HUD **Exit** button are the ways out.

### 2.4 Presentation mode

```text
+--------------------------------------------------------------------------------------------+
| (black surround)                                                                           |
|                            +------------------------+                                      |
|                            |                        |                                      |
|                            |        page 12         |                                      |
|                            |   fit to screen height |                                      |
|                            |                        |                                      |
|                            +------------------------+                                      |
|                                                          [ 12 / 612 | Exit ]               |
+--------------------------------------------------------------------------------------------+
   The page-hint bar appears on pointer move or any key for 2 s: page number and an Exit button.
   Cursor hides after 2 s idle. Two-page spreads use the same rule (fit the spread).
```

## 3. Behaviour

### 3.1 Reading mode

1. **Enter** with `F11`, the View menu item **Reading mode**, or the HUD. The window enters true full screen (`AppWindowPresenterKind.FullScreen`). The tab strip and command bar slide out (`MotionBase`; instant under reduced motion). The page well does not reflow: the page well grows to the window and **tiles re-render only if the zoom mode is fit-width or fit-page**, the same as any resize.
2. **Reveal the HUD** on pointer movement over the page, a touch on the page's middle third, `Alt`, or any navigation key. It shows page navigation, zoom, find, the View menu, and **Exit reading mode**. Pointer movement over the HUD, or keyboard focus inside it, keeps it visible.
3. **Reveal the full chrome** by moving the pointer to the top edge (it must stay within 4 epx of the edge for 250 ms, to avoid accidental reveals), pressing `Alt` or `F10`, or a downward touch swipe from the top 24 epx **[design; the system also uses edge swipes, so verify that a swipe does not clash]**. This is how to switch tabs and reach every command without leaving the mode. `Ctrl+Tab` also works without revealing anything, and shows the new tab's name for 1.5 s at the top centre.
4. **Exit:** `F11`, `Esc` (when no menu or flyout is open), the HUD button, or the chrome. The window returns to its previous state (maximised or restored, same size and position).
5. **Everything still works.** Find shows its bar over the page. Selecting text shows the selection toolbar. Notes popups open. The only thing that does not appear is the permanent chrome.
6. **Progress line** (optional, Settings, Reading): a 2 epx line at the bottom edge showing `(current page) / (page count)`. Off by default. It is decoration (hidden from UIA).
7. **Remembered:** the mode is **not** persisted across launches. A restored session reopens windowed.
8. **Multiple monitors:** the mode fills the monitor the window is on.
9. Touch: tap the left or right 20% of the page well to turn pages **in paged layouts only** (in continuous scroll those taps do nothing, so a tap never moves content unexpectedly).

### 3.2 Presentation mode

1. **Enter** with `Ctrl+L`, or **View, Presentation**. The window goes true full screen with a black surround. The layout is **fit page** and **paged**, one page, or a spread if the user has two-page mode on. Zoom, find, selection and markup are off.
2. **Keys:** `Right`, `Down`, `PageDown`, `Space`, `Enter`, left click: next. `Left`, `Up`, `PageUp`, `Backspace`, right click: previous. `Home` and `End`: first and last. **Digits then `Enter`**: go to that page. `B` or `.`: toggle a **black screen**. `W` or `,`: toggle a **white screen**. Any other navigation key leaves the blank screen. `Esc`: exit. Mouse wheel: next and previous, with a 250 ms debounce **[design]**. Touch: swipe left for next and right for previous; two-finger pinch is ignored.
3. **Keep the display awake** with a display request (`Windows.System.Display.DisplayRequest`) while the mode is active, and release it on exit. Don't touch system notification settings.
4. **No annotations are drawn or saved in this mode** (`Ctrl+L` is for showing, not for editing). A laser pointer and temporary pen are P2 ideas, unscheduled.
5. **Page transitions:** none. Page changes show the next page's tiles immediately. A low-res placeholder is allowed to show for at most one frame, **but never a black flash**, so presentation mode pre-renders the next page and the previous page at fit-page resolution (needs a prefetch rule; see section 7).
6. **Exit** restores the previous layout and zoom.
7. **Annotations on pages are drawn** (they are part of the document). Form fields are drawn but not interactive.

## 4. States

| State | Reading mode | Presentation mode |
|---|---|---|
| **Loading** | The HUD shows a 16 epx `ProgressRing` next to the page number while the document opens; pages show placeholders as usual | A black screen with a centred 32 epx `ProgressRing` until the first page's fit-page tile is ready, never a white flash. If rendering a page takes more than 200 ms, show its thumbnail upscaled with a small ring in the corner |
| **Empty** | Not available without a document: the command is disabled with "Open a document first" (palette reason). On an empty-state tab, `F11` still toggles full screen, to show the empty state **[design: harmless]** | Not available: same disabled reason |
| **Error** | A page that fails shows the same in-page error chip as usual. A banner about the engine appears over the page for 6 s. If the document is closed by an error, the mode exits to the tab's error page | A page that fails shows a centred message on black ("This page can't be shown") with the page number, and navigation keeps working. The mode never exits on its own |
| **Keyboard focus** | Focus rests on the page surface. When the HUD or chrome appears through the keyboard (`Alt`), focus goes to the first control; `Esc` returns focus to the page. The visible focus ring is drawn as usual | Focus stays on the surface; there are no focusable controls except the **Exit** button in the hint bar, reachable by `Tab` for 2 s after the bar appears (it stays while focused) |
| **High contrast** | HUD and revealed chrome use `Window`, `WindowText` and a 1 epx edge. The page keeps its content. The progress line uses `Highlight` | Surround uses `Window` (not black) when a high-contrast theme is on, since the user chose it; hint bar as above |
| **225% text scale** | The HUD's text (page number, "Exit reading mode") scales, so the pill grows to two rows at 225%. Buttons remain 40 x 40. No fixed width | The hint bar grows with the text; the page number wraps; nothing overlaps the page |
| **Narrow window** | A full screen is never narrow. If the window is snapped, the mode refuses and shows "Reading mode needs the whole screen" for 3 s **[design]**; the user can unsnap | Same |
| **Touch** | HUD buttons 48 x 48 and the pill is 56 epx high. Taps on page edges turn pages in paged layouts. Swipe from the top reveals chrome. The touch keyboard is not needed | Swipe to turn pages. A tap shows the hint bar; there are no other touch controls |

## 5. Accessibility

### 5.1 Reading mode

| Element | Type | Name | AutomationId |
|---|---|---|---|
| HUD | ToolBar | "Reading controls" | `ReadingHud` |
| Previous / next page, zoom out / in, find, view, exit | Button | "Previous page", "Next page", "Zoom out", "Zoom in", "Find", "View", "Exit reading mode" | `HudPrevious`, `HudNext`, `HudZoomOut`, `HudZoomIn`, `HudFind`, `HudView`, `HudExit` |
| Page indicator | Text | "Page 12 of 612" | `HudPage` |

Narrator, intended: **entering** "Reading mode. Press F11 or Escape to exit." (polite, once). **Exiting** "Reading mode off." The HUD is a normal toolbar when focused: "Reading controls, toolbar. Previous page, button." The page surface keeps its full UIA tree, so a screen-reader user reading by line keeps working inside the mode.

### 5.2 Presentation mode

| Element | Type | Name | AutomationId |
|---|---|---|---|
| Surface | Document | "Presentation, page 12 of 612" | `PresentationSurface` |
| Hint bar | Group | "Presentation controls" | `PresentationHint` |
| Exit | Button | "Exit presentation" | `PresentationExit` |

Narrator, intended: entering "Presentation mode. Press Escape to exit. Page 1 of 30." Turning a page: "Page 13 of 30" (polite). Blank screen: "Screen black." / "Screen white." / "Screen restored."

Presentation mode is a **visual** display mode. A screen-reader user who wants to read a presentation should use reading mode; this is stated in the mode's tooltip and in the help text.

### 5.3 Focus order

Reading mode: page surface, then HUD (when shown): Previous, page, Next, Zoom out, zoom box, Zoom in, Find, View, Exit. Revealed chrome: tabs, command bar, as in [01](01-main-window.md).

## 6. Keyboard path

| Task | Keys |
|---|---|
| Reading mode on or off | `F11` |
| Presentation on or off | `Ctrl+L` (exit: `Esc`) |
| Turn pages in reading mode | `Page Down` / `Page Up`, arrows, `Space`, `Home`, `End`, `Ctrl+G` |
| Find in reading mode | `Ctrl+F` (the bar appears over the page) |
| Reveal chrome by keyboard | `Alt` or `F10` |
| Switch tabs | `Ctrl+Tab`, `Alt+1` to `Alt+9` |
| Black or white screen in presentation | `B` or `.`; `W` or `,` |
| Jump to a page in presentation | digits, `Enter` |

## 7. Engine and protocol implications

- **Presentation prefetch rule:** the tile scheduler (ADR-0006 rule 4) gets a mode flag. In presentation, keep the next and previous pages' fit-page tiles rendered at priority just under the visible page, regardless of velocity.
- **No new engine messages.** Page colour (invert, sepia) applies through the GPU colour matrix (E13) and stays on in both modes.
- **`DisplayRequest`** and full screen are UI-process concerns. No sandbox impact.

## 8. OPEN QUESTIONS

1. **Q6:** F11 true full screen versus chrome-hidden-in-a-window. This spec chooses true full screen, because §10.2 pairs F11 with "full screen / presentation". A windowed "hide chrome" can be a View-menu option later.
2. Should presentation mode read speaker notes or show a timer? §2 doesn't ask for it. Out of scope.
3. Can **Esc** safely mean "exit reading mode" when it also closes the find bar? This spec orders it: first close the transient layer, only then exit the mode (G-KB2). Confirm in user tests.
4. Is the top-edge swipe safe on touch devices? Windows uses edge gestures; verify.
