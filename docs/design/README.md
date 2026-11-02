# MikePDF design system and UI specification

- Status: **Draft for Opus review** (master prompt §10.1 asks for a design spec with screen-by-screen wireframes before the UI is implemented)
- Date: 2026-11-02
- Author: Sonnet (work package W1-design, branch `m3/design`)
- Scope: the v1.0 UI (M3 full reader, M4 markup, forms and page organiser), with hooks for the v1.x features that change layout (signatures, AI)
- This is a design document. Nothing here has been built or measured unless it says so. See [How to read this](#0-how-to-read-this-document).

## Contents

| Document | What it holds |
|---|---|
| This file | Principles, materials, layout system, tokens, control rules, shared patterns, protocol implications |
| [competitive-teardown.md](competitive-teardown.md) | Seven competitors and references, and a table of measurable ways MikePDF will beat each one |
| [commands.md](commands.md) | The full command inventory: IDs, shortcuts, placement, enablement, undo |
| [ux-metrics.md](ux-metrics.md) | How "superior" is measured: task scripts, click counts, budgets, accessibility checks, a 5-user test plan |
| [screens/](screens/) | One file per screen area, listed in [section 11](#11-screen-index-and-the-screen-template) |

---

## 0. How to read this document

Evidence tags, used throughout all design files:

| Tag | Meaning |
|---|---|
| **[src]** | Taken from a cited source (URL and access date given). Not copied; paraphrased |
| **[repo]** | Read from this repository (file named) |
| **[design]** | A decision made by this spec. Not a fact about the world |
| **[guess]** | A guess or recollection that wasn't verified. Replace with a measurement before relying on it |
| **OPEN QUESTION** | Needs the product owner, or an ADR, before implementation |

Rules from master prompt §19.3 apply: a number that was not measured says "not measured". Every Narrator announcement written here is the **intended** announcement. No Narrator pass has been run (STATUS: S7 manual pass pending).

Units: **epx** is a WinUI effective pixel (1 epx = 1 physical pixel at 100% display scale). Physical pixels = epx x display scale.

The names MikePDF uses for things. "Page well" is the scrolling area that shows pages. "Chrome" is everything that is not the page well, a pane, or an overlay. "Command bar" is the single row of tools under the tabs. "Pane" means the left or right side panel.

---

## 1. Principles

These restate master prompt §1.2 as rules a reviewer can check against a wireframe or a build.

| # | Principle | Testable rule |
|---|---|---|
| P1 | **The page is the product.** Chrome serves the page and gets out of the way | Default chrome is at most **80 epx** tall. No row, banner or pane appears on its own over a document, except a user-requested one, an error, or a restricted-document notice. Content never shifts when a banner appears (banners overlay the page well) |
| P2 | **Instant, and no interruptions** | No sign-in, tour, upsell, rating prompt, or modal at launch. Cold open of a 1-page PDF to first pixel is at most 800 ms (§11). A dialog appears only for a decision the user must make before work can continue |
| P3 | **One way to do each thing, and every way is in the palette** | Every command has an ID, and every ID is in the command palette and the remap list ([commands.md](commands.md)). Toolbar buttons are shortcuts to commands, never the only path |
| P4 | **Direct manipulation over modes** | Select text, then one click to mark it. The pen inks when the pen touches. Tools return to Select after one use unless pinned (double-click the tool, or hold Shift) |
| P5 | **Honest state** | The UI always shows what is saved, what is pending, and what failed, in plain words that say what the user can do next. No error codes in the main text (details are one click away, and never contain document content) |
| P6 | **Undoable, always** | Every edit is a command (§5.2 rule 6) and reaches Ctrl+Z. Destructive confirmations are for things that cannot be undone (closing with unsaved changes, overwriting a file) |
| P7 | **Keyboard- and screen-reader-complete from the first build** | Each screen file lists a keyboard path and UIA names. A feature without them is not done (§10.3) |
| P8 | **A first-party Windows 11 app** | WinUI 3 controls, Mica Alt, Segoe UI Variable, Segoe Fluent Icons, system accent, system theme, snap layouts, per-monitor DPI. A custom control must justify itself against [section 7](#7-which-control-to-use) |
| P9 | **Private and offline by default** | No account. No network at startup. Nothing leaves the device without a visible, named action (links show their address and never open themselves: STATUS "External links") |

Windows 11 inbox apps used as the consistency reference [guess: from general familiarity, not re-verified for this document]: **Notepad** and **File Explorer** (tabs in the title bar, one command row), **Windows Terminal** (command palette on Ctrl+Shift+P), **Settings** (NavigationView), **Photos** (viewer with minimal chrome). Opus reviews for consistency with these, as §10.1 asks.

---

## 2. Platform and materials

### 2.1 Stack

| Layer | Choice | Source |
|---|---|---|
| Design language | Fluent 2 as applied to Windows apps | §10.1, [Windows typography page](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) (updated 2026-07-14, accessed 2026-11-02) **[src]** |
| UI framework | WinUI 3 on Windows App SDK 2.x | ADR-0001 **[repo]** |
| Backdrop | **Mica Alt** (see 2.2) | [Mica page](https://learn.microsoft.com/en-us/windows/apps/design/style/mica) (2026-07-22, accessed 2026-11-02) **[src]** |
| UI font | Segoe UI Variable (WinUI default for supported languages) | typography page **[src]** |
| Icons | Segoe Fluent Icons (font glyphs, `FontIcon`) | typography page **[src]** |
| Page rendering | Win2D `CanvasVirtualControl` presenter behind `ITilePresenter`; annotations, selection, find hits and ink on separate layers | ADR-0006 **[repo]** |

### 2.2 Mica, Mica Alt and the layer stack

The current app uses `MicaBackdrop` with the default kind **[repo: MainWindow.xaml.cs]**. MikePDF moves to `MicaKind.BaseAlt`. Reason: Microsoft recommends Mica Alt for apps with a tabbed title bar, because it gives more contrast between the tab strip and the commanding area **[src: Mica page]**.

Layers, from back to front. This is the "base, commanding, content" system the Mica page recommends **[src]**:

| Layer | Used for | Brush (WinUI theme resource) |
|---|---|---|
| Base | Window background, title bar and tab strip | Mica Alt (`SystemBackdrop`) |
| Commanding | Command bar row | `LayerOnMicaBaseAltFillColorDefaultBrush` |
| Content | Left and right panes, dialogs' bodies | `LayerFillColorDefaultBrush` |
| Page well | Behind the pages | `PageWellBrush`, a MikePDF brush: an opaque `SolidBackgroundFillColorSecondaryBrush` **[design]**. Opaque for two reasons: pages and page borders need a stable contrast, and the compositor does not blend under every tile (impact **[guess]**; measure in S2 follow-up) |
| Overlay | Find bar, selection toolbar, palette, notes popups, undo bar | `SolidBackgroundFillColorBaseBrush`, 1 epx `ControlStrokeColorDefaultBrush`, `OverlayCornerRadius`, shadow depth 32 |
| System flyouts | Menus, ComboBox drop-downs, tooltips | Whatever WinUI draws (Acrylic). Don't restyle |

Rules:
- Apply the backdrop **once**, on the window. Set the intermediate layers' own background to transparent where Mica should show through **[src]**.
- Mica falls back to a solid colour when transparency is off, Battery Saver is on, the window is inactive, or the hardware is low-end. In high contrast the user's own colours replace it **[src]**. Nothing in MikePDF may depend on the Mica tint for contrast. Every foreground is checked on the solid fallback (`SolidBackgroundFillColorBase`, `SolidBackgroundFillColorBaseAlt`).
- The inactive window dims tab and command-bar content to the standard inactive state (Windows draws caption buttons; MikePDF must dim its own).

### 2.3 Fonts and icons

- One UI font. Do not set `FontFamily` anywhere except the document-text areas of free-text annotation editing (which use the annotation's own font, see [screens/08](screens/08-markup-and-ink.md)).
- Weights: Regular and Semibold only. No bold, no italic in UI text (the Windows type ramp excludes both) **[src]**.
- Sentence case everywhere, including titles **[src]**. Truncate with an ellipsis. For minimum sizes: 12 epx Regular, 14 epx Semibold **[src]**.
- Icon glyph codes are chosen at implementation from the Segoe Fluent Icons table. This spec names icons by meaning ([section 8](#8-iconography-and-content-rules)), because a wrong hex code is worse than none. Codes already in the repo (chevron up `E70E`, chevron down `E70D`, close `E711`, hamburger `E700`, fit width `E9A6`, page `E7C3`) stay.

---

## 3. Window anatomy and layout system

### 3.1 Regions

```text
 y=0   +-----------------------------------------------------------------------------+
       | [icon] [Tab 1 v] [Tab 2  ] [+]            drag region              [_][[]][X]| TITLE / TAB STRIP  40 epx
 y=40  +-----------------------------------------------------------------------------+
       | [pane] [<] 3 / 7 [>]  [-] 125% v [+]  [view v]  [find]   ...   [print][cmds][i]| COMMAND BAR        40 epx
 y=80  +------------+------------------------------------------------------+---------+
       | LEFT PANE  |                    PAGE WELL                         | RIGHT   |
       | 296 epx    |   (banners, find bar, selection toolbar overlay)     | PANE    |
       | resizable  |                                                      | 320 epx |
       +------------+------------------------------------------------------+---------+
```

| Region | Standard | Touch density | Reading mode |
|---|---|---|---|
| Title bar and tab strip | 40 epx | 48 epx | 0 (revealed on top-edge hover as an overlay) |
| Command bar | 40 epx | 48 epx | 0 |
| **Chrome total** | **80 epx** | **96 epx** | **0** |
| Left pane | 296 epx default, 200 to 480 epx, closed by default on first open | same | closed |
| Right pane | 320 epx default, 280 to 480 epx, closed by default | same | closed |

The title bar uses the 40 epx height the app already has **[repo]**. Windows' standard is 32 epx and 48 epx when it holds a search box or account control **[src: [title bar page](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design), 2024-07-31, accessed 2026-11-02]**. 40 epx is a **[design]** compromise so that tabs have a 32 epx hit height with 8 epx of breathing room. The caption-button inset comes from `AppWindow.TitleBar.RightInset`, as today **[repo]**.

**Contextual command bar.** When the user enters Markup or Organise pages, the **centre of the same 40 epx row changes in place**. The row never grows and the page well never moves. This is how the spec gets many more commands than a fixed toolbar without any extra chrome.

**Left pane and right pane.** Both overlay at narrow widths ([3.2](#32-breakpoints)). They never sit side by side with the page at less than the minimum page-well width of 360 epx.

### 3.2 Breakpoints

Window width in epx **[design]**. At 1366x768 and a typical 150% scale, the window is about 911x512 epx **[guess: depends on the device]**, so the **M** breakpoint is the one laptops see most.

| Name | Width | Command bar | Panes | Page layout |
|---|---|---|---|---|
| XS | < 520 | Pane toggle, page box, **More** menu | Overlay (slides over the page well), one at a time | Single column, fit width |
| S | 520 to 799 | Pane toggle, page box, zoom (collapsed to one **Zoom** button), Find, **More** | Overlay | Single column |
| M | 800 to 1199 | Full left group, **View** menu, Find, tools in overflow by priority | Left inline when open, right overlay | Two-page modes allowed |
| L | 1200 to 1799 | Everything visible | Both inline | All |
| XL | >= 1800 | Everything visible | Both inline, pane width capped | All; centred page well |

Minimum window size: 480x360 epx **[design]**. Below it the window clips rather than reflows.

Overflow order (what leaves the command bar first), set with `CommandBar.DynamicOverflowOrder`. Lowest number leaves last:

| Order | Item |
|---|---|
| 1 | Panes toggle, page box |
| 2 | Open |
| 3 | Find |
| 4 | Zoom group (collapses to a single **Zoom** button before it leaves) |
| 5 | **Markup** button |
| 6 | View menu |
| 7 | Print |
| 8 | Details pane toggle |
| 9 | Commands (palette button) |
| 10 | Document properties (always in overflow) |

### 3.3 Window and shell behaviour

- Snap layouts and the maximise glyph work through the system caption buttons; do not draw custom ones **[src: title bar page]**. A right-click on empty title-bar space shows the system menu, and a double-click toggles maximise **[src]**.
- Tabs live in the title bar (`TabView`, see [screens/01](screens/01-main-window.md)). The caption inset is respected.
- Per-monitor DPI v2. Moving a window between monitors re-rasterises tiles after the move settles (ADR-0006), and the chrome re-lays out with no flash.
- Window position, size, tabs, scroll, zoom and pane state are restored (§7.6). A document that has moved or been deleted restores as an inactive tab with a "can't be found" state, not as an error dialog.

---

## 4. Tokens

Names are MikePDF resource keys. Values reference WinUI resources wherever one exists, so that theme, accent and high-contrast changes apply for free. Hex values appear only for colours that are MikePDF's own.

### 4.1 Spacing

A 4 epx grid with a short ramp **[design]**. WinUI controls bring their own padding. These tokens are for MikePDF-owned layouts.

| Token | epx | Use |
|---|---|---|
| `Space2` | 2 | Hairline gaps, focus ring offset |
| `Space4` | 4 | Between icon and label; command-bar button gap |
| `Space8` | 8 | Control padding; gap in toolbars; page-well to overlay margin |
| `Space12` | 12 | Gap between pages in the page well; list-row padding |
| `Space16` | 16 | Gutter in panes and dialogs; page-well side margin; between sections |
| `Space24` | 24 | Dialog body padding; empty-state side margins |
| `Space32` | 32 | Between major groups on the empty state |
| `Space48` | 48 | Large empty-state vertical rhythm |

Page-well layout:
- Gap between pages in continuous mode: `Space12` (12 epx), scaled with zoom only below 50% (never less than 4 epx).
- Gap between the two pages of a spread: 2 epx. Between spreads: `Space16`.
- Side margin of the page well when a page is wider than the well: 0 (horizontal scroll). Otherwise pages centre.

### 4.2 Type ramp

Exactly the Windows 11 ramp **[src: typography page]**. No other sizes in the UI.

| Style | WinUI style key | Weight | Size / line height (epx) | Use in MikePDF |
|---|---|---|---|---|
| Caption | `CaptionTextBlockStyle` | Regular | 12 / 16 | Tab titles, secondary text under list rows, field hints, status text, window title |
| Body | `BodyTextBlockStyle` | Regular | 14 / 20 | Default text, buttons, list rows, inputs |
| Body strong | `BodyStrongTextBlockStyle` | Semibold | 14 / 20 | Row primary text, dialog button text |
| Body large | `BodyLargeTextBlockStyle` | Regular | 18 / 24 | Empty-state hints |
| Subtitle | `SubtitleTextBlockStyle` | Semibold | 20 / 28 | Section headings in panes, settings sections |
| Title | `TitleTextBlockStyle` | Semibold | 28 / 36 | Empty-state heading, error-page heading |
| Title large, Display | not used in chrome | | | |

Text scaling to 225%: every text container flows. No fixed heights on anything that contains text. Command-bar buttons are icon-only, so they do not scale. Tab titles, list rows, dialogs, InfoBars and settings do ([G-TS rules](#6-global-accessibility-rules)). Keep 50 to 60 characters per line in prose **[src: typography page]**: empty-state and dialog text columns are capped at 60 characters, roughly 480 epx.

### 4.3 Corner radius

From the Windows geometry guidance **[src: [geometry page](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry), updated 2026-02-19, accessed 2026-11-02]**:

| Token | epx | Used for |
|---|---|---|
| `ControlCornerRadius` | 4 | Buttons, text boxes, list rows, tabs, thumbnails' selection backplate, scroll marks |
| `OverlayCornerRadius` | 8 | Dialogs, flyouts, the find bar, the palette, notes popups, the selection toolbar, the undo bar |
| 0 | 0 | Where two controls touch (split buttons) and where a pane edge meets the window edge. Maximised and snapped windows are not rounded by Windows |
| Pages | 0 | **Page corners are square.** They are paper, not UI |

Don't override the two global resources in `App.xaml`.

### 4.4 Elevation

Two-layer rule: base plus content, then transient overlays above **[src: Mica page, layering guidance]**. Shadows use `ThemeShadow` with a z translation. These z values follow the WinUI defaults as best recalled **[guess: verify against the WinUI generic resources before use]**.

| Token | z translation | Used for |
|---|---|---|
| `ElevationNone` | 0 | Panes, command bar, page well |
| `ElevationPage` | 8 | Pages in the page well (with a 1 epx `ControlStrokeColorDefaultBrush` hairline so a white page stays visible on a light well). No shadow in high contrast, hairline only |
| `ElevationOverlay` | 32 | Find bar, selection toolbar, palette, notes popups, undo bar |
| `ElevationDialog` | 128 | ContentDialog (system drawn) |

Shadows are decoration. Nothing relies on a shadow to be seen. Every overlay also has the 1 epx stroke, which is what remains in high contrast.

### 4.5 Motion

The product is a reader. Motion is allowed only to explain a change of place or state. **[design]** tokens, with Fluent 2 curve shapes as best recalled **[guess: verify against WinUI `ControlFastAnimationDuration` and related resources]**:

| Token | Duration | Curve | Used for |
|---|---|---|---|
| `MotionInstant` | 0 ms | none | Anything under reduced motion |
| `MotionFast` | 83 ms | decelerate `(0,0,0,1)` | Hover and press feedback, selection toolbar fade-in |
| `MotionBase` | 167 ms | decelerate on enter, accelerate `(1,0,1,1)` on exit | Overlays in and out, pane open and close, command-bar centre swap (cross-fade), reading-mode chrome hide |
| `MotionSlow` | 250 ms | decelerate | Page-organiser reorder settle, scroll-to-page animation (distance under one screen) |

Rules:
1. **Reduced motion.** When Windows "Animation effects" is off (`UISettings.AnimationsEnabled` is false) every token resolves to `MotionInstant`. No parallax, no smooth scroll to a destination (jump instead, but keep a 2 epx "arrival" outline for 1 s so the user sees where they landed), and no looping shimmer on skeletons (show a static skeleton). Indeterminate progress stays, since it is functional, and a text status line accompanies it.
2. **Direct manipulation is not animation.** Pinch, Ctrl+wheel zoom and drag follow the input one-to-one, scaled on the GPU, then sharpen after 120 ms (ADR-0006). That is allowed in reduced motion.
3. Nothing flashes more than 3 times in a second (WCAG 2.3.1). Find-hit "pulse" on arrival is one outline fade (83 ms), never a loop.
4. Never animate layout of the page well when chrome shows or hides: chrome overlays, the page does not move.

### 4.6 Colour roles

Roles map to WinUI theme resources so that light, dark and high contrast come from the system. "HC" means any of the four Windows contrast themes. In HC every role resolves to a **system colour** (`SystemColorWindowColor` and friends, which WinUI's high-contrast dictionaries already map). **MikePDF never hard-codes a chrome colour** [design].

| Role | Light / Dark | High contrast |
|---|---|---|
| `Surface.Base` | Mica Alt | `SystemColorWindowColor` |
| `Surface.Command` | `LayerOnMicaBaseAltFillColorDefaultBrush` | Window |
| `Surface.Pane` | `LayerFillColorDefaultBrush` | Window, with a 1 epx `SystemColorWindowTextColor` edge |
| `Surface.PageWell` | `SolidBackgroundFillColorSecondaryBrush` | Window |
| `Surface.Overlay` | `SolidBackgroundFillColorBaseBrush` | Window |
| `Text.Primary` | `TextFillColorPrimaryBrush` | WindowText |
| `Text.Secondary` | `TextFillColorSecondaryBrush` | WindowText (no dimming in HC) |
| `Text.Disabled` | `TextFillColorDisabledBrush` | `SystemColorGrayTextColor` |
| `Border.Default` | `ControlStrokeColorDefaultBrush` | WindowText |
| `Accent` | `AccentFillColorDefaultBrush` (system accent). Text on accent uses `TextOnAccentFillColorPrimaryBrush` | Highlight / HighlightText |
| `Focus.Primary`, `Focus.Secondary` | `FocusVisualPrimaryBrush`, `FocusVisualSecondaryBrush` | Same keys; WinUI maps them |
| `Status.Critical`, `.Caution`, `.Success`, `.Info` | `SystemFillColorCriticalBrush` and the matching `Caution`, `Success`, `Attention` brushes. Each status also has a **shape** (glyph) and a **word** | WindowText with the glyph |
| `Selection.Text` | Accent at 35% alpha over the page | `Highlight` outline plus 20% fill |
| `Find.Hit` | `#FFD54F` at 55%, multiply blend **[design]** | 2 epx `Highlight` outline |
| `Find.Current` | `#FF9100` at 70%, multiply, **plus** a 2 epx outline `#B34700` **[design]** | 3 epx `Highlight` outline plus fill |
| `Form.FieldHint` | `#7FB3E6` at 25% over the field **[design]**; plus a 1 epx dotted outline at hover/focus only | 1 epx dotted `WindowText` outline |
| `Ink.Default` | Defaults in [4.7](#47-annotation-palette-document-content) | n/a (document content) |

Contrast requirements: text 4.5:1 and large text 3:1 (WCAG 1.4.3). Non-text UI and focus indicators 3:1 (1.4.11) **[src: [accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist) 4.5:1 text target, 2026-07-22, accessed 2026-11-02]**. The contrast check runs on **both** the Mica result and the solid fallback, and on the page well in both themes. The find colours above are **not yet checked** for contrast against typical page colours; they are intended to be adjusted at implementation, with the outline as the non-colour cue.

### 4.7 Annotation palette (document content)

Annotation colours belong to the document, so they don't change with theme. Six named colours, each with a name (read by Narrator, shown in tooltips, never colour-only) **[design]**:

| Name | sRGB (guess; tune against the fidelity harness) | Blend |
|---|---|---|
| Yellow | `#FFEB3B` | Multiply (highlight), Normal (others) |
| Green | `#8BE28B` | same |
| Blue | `#7FD1F5` | same |
| Pink | `#FF9ECF` | same |
| Orange | `#FFB74D` | same |
| Purple | `#CBA6F7` | same |
| Plus: **Ink colours** | Black `#000000`, Red `#D13438`, Blue `#0078D4`, Green `#107C10`, plus a custom colour | Normal |

The last used colour of each tool is remembered across documents (Edge resets ink colour and thickness per document **[src: [forum post](https://techcommunity.microsoft.com/t5/articles/microsoft-edge-extends-tools-for-the-pdf-reader/m-p/1303243), accessed 2026-11-02; user report, not a Microsoft statement]**).

### 4.8 Focus visuals

- Standard WinUI controls keep the default focus visual (a 2 epx outer `FocusVisualPrimaryBrush` ring with a 1 epx inner `FocusVisualSecondaryBrush` ring). Don't suppress it, and don't set `UseSystemFocusVisuals=false` **[design]**.
- **Custom surfaces draw the same two-ring focus visual.** This covers the focused page, annotation, form field, link, thumbnail, organiser cell, and the find-hit. Ring offset `Space2`.
- Focus is never hidden under an overlay (WCAG 2.4.11 Focus Not Obscured). When focus moves to an element, the page well scrolls it into view with a 64 epx top margin and 16 epx margins on the other sides (so the reading-mode HUD and find bar do not cover it). The selection toolbar moves, not the focus.
- Focus order follows reading order: tab strip, command bar, left pane (if open), page well, right pane (if open). **F6 and Shift+F6** cycle these landmarks. This is the Windows convention for pane cycling **[guess: widely used, not re-verified]**.
- Keyboard focus is **not** the same as the page selection, and a click on the page well gives it focus (so Ctrl+key shortcuts work after a click).

---

## 5. Density, touch and pen

### 5.1 Target sizes

| Surface | Hit target | Visual | Basis |
|---|---|---|---|
| Command bar buttons | 40 wide x 40 tall (the full row height) | Glyph 16 epx in a 32 tall backplate | MS guidance: 7.5 mm square, 40x40 px at 135 PPI at 1.0x **[src: [targeting page](https://learn.microsoft.com/en-us/windows/apps/design/input/guidelines-for-targeting) 2026-09-27, accessed 2026-11-02]**; WCAG 2.5.8 minimum 24x24 |
| Tab | 32 tall x at least 96 wide; close button 24x24 | | |
| List rows in panes | 40 tall minimum (48 with a second line) | | |
| Selection toolbar and popup buttons | 40 x 40 | 44 x 44 when the input was touch or pen | |
| Page-level targets (annotation icons, field edges, text-selection handles) | Visual size plus **12 epx hit slop**; minimum 24 x 24 | | |
| Thumbnails and organiser cells | The whole cell | | |
| Scroll rail | 12 epx wide, widens to 16 on hover | | |

Spacing between adjacent targets is at least 4 epx.

### 5.2 Density modes

| Setting | Row heights | Notes |
|---|---|---|
| **Standard** (default) | Tab strip 40, command bar 40 | |
| **Touch** | 48 and 48 | Settings: **Auto / Always / Never**. Auto switches transient UI (menus, flyouts, the selection toolbar, list rows) when the last input was touch or pen, and switches the tab strip and command bar only when the Windows tablet posture is detected **[guess: detection method is unverified; WinUI 3 desktop apps have no `UserInteractionMode` equivalent confirmed here]** |
| **Compact** (opt-in) | Tab strip 36, command bar 36, list rows 32 | Uses WinUI's compact density dictionary if available in the pinned Windows App SDK **[guess: verify]**. Not available together with Touch |

### 5.3 Pen and touch behaviours (summary; detail in screens 03 and 08)

1. **The pen inks, the finger scrolls** when pen mode applies ([screens/08](screens/08-markup-and-ink.md)). While the pen is in range, touch contacts never ink.
2. A short pen contact (under 150 ms, under 2 epx of travel) is a **tap**, not a stroke. It activates links and fields and selects annotations. This is a **[design]** choice to prevent accidental dots.
3. Pinch zooms; two-finger drag scrolls. These work in every mode and are never taken by a tool.
4. Touch text selection uses handles and the selection toolbar; a long-press starts it. Handles are 44 x 44.
5. All hover-only affordances have a touch or keyboard equivalent (WCAG 1.4.13, 2.5.1).
6. Pen hover (where the digitizer reports it) previews the current tool's tip size (a 1 epx ring). Hover never triggers an action.

---

## 6. Global accessibility rules

Every screen file lists the screen's own UIA names and states. These global rules apply to all of them. The checks that prove them are in [ux-metrics.md](ux-metrics.md).

### 6.1 High contrast (G-HC)

1. G-HC1: All colours come from roles in [4.6](#46-colour-roles). No hard-coded chrome hex values.
2. G-HC2: No Mica, no shadows beyond the 1 epx stroke, no translucency. Overlays have a `WindowText` edge.
3. G-HC3: State is never only colour. Selected, current, error, required and modified states each have a glyph, an outline, or text.
4. G-HC4: Find hits, selection and focus on the page use outlines in the system `Highlight` colour (4.6).
5. G-HC5: The page itself is **document content**. An optional **page high-contrast render mode** (Settings, Reading) remaps page colours to `Window` and `WindowText`. The tile cache is keyed by colour mode. This mode needs an engine render option (see [section 10](#10-protocol-and-engine-implications)); until then only invert and sepia exist (GPU colour matrix).
6. G-HC6: Annotation handles and outlines (chrome drawn over the page) use `Highlight`.

### 6.2 Text scale to 225% (G-TS)

1. G-TS1: No fixed height on any element that holds text. Use `MinHeight`.
2. G-TS2: The title bar and tab strip may grow taller with text scale. The caption buttons stay anchored right **[src: title bar page says the title bar may need to grow]**.
3. G-TS3: Command-bar buttons are icon-only and do not grow. Their tooltips and the overflow menu do.
4. G-TS4: Dialogs scroll their body vertically, never horizontally. Their buttons stay pinned to the bottom edge of the dialog and visible.
5. G-TS5: At 225% the list rows in panes wrap to three lines, then truncate with an ellipsis, with the full text in the tooltip and the accessible name.
6. G-TS6: Page content (rendered tiles) is not affected by the text-size setting. That is the document's text.

### 6.3 Narrow window (G-NW)

See [3.2](#32-breakpoints). Panes become overlays. Dialogs become full-width sheets with an 8 epx margin. Nothing requires horizontal scrolling at 320 epx width apart from the page itself (WCAG 1.4.10 reflow, applied to chrome). Controls never overlap.

### 6.4 Touch (G-TC)

See [section 5](#5-density-touch-and-pen). Every pointer action that depends on a path (drag to reorder, draw) has a single-pointer alternative where the result can be reached without one (WCAG 2.5.7): move-to-position dialogs for page reordering, and the Format pane for annotation position and size.

### 6.5 Keyboard (G-KB)

1. G-KB1: Everything is reachable by keyboard. Tab and Shift+Tab move through controls; arrow keys move within a group (command bar, tab strip, lists, trees, grids).
2. G-KB2: Esc closes the topmost transient thing (menu, flyout, overlay), and only then exits a mode.
3. G-KB3: No keyboard traps. Inside a form, Tab means "next field", and on a page with annotations Tab means "next annotation" (WCAG 2.1.2). So the page well is left with **F6** (next landmark) or **Shift+F6**, which always work. On a page with nothing focusable, Tab also leaves. Ctrl+Tab always switches tabs. **[design]**
4. G-KB4: Character-key shortcuts are **off by default** and can be remapped or turned off (WCAG 2.1.4). The opt-in single-key set is listed in commands.md.
5. G-KB5: Every icon-only control has an accessible name and a tooltip. Tooltips show the command's shortcut from the command registry, so labels and shortcuts can't drift apart.
6. G-KB6: **Access keys.** Pressing and releasing Alt shows key tips on the tab strip and command bar (WinUI `AccessKey`). Table in commands.md.

### 6.6 Screen reader (G-SR)

1. G-SR1: Each control has `AutomationProperties.Name`, a correct control type, and the patterns its type implies. Names come from `.resw` (the `x:Uid` convention the app uses today **[repo: Resources.resw]**).
2. G-SR2: Status changes use live regions: `Polite` for progress, saved, search count, "engine restored". `Assertive` only for errors that block the current action.
3. G-SR3: The page surface exposes the **Text pattern** (S7), with the structure tree used for reading order when the PDF is tagged. When it is not tagged, Narrator is told once per document: "This document is not tagged. Reading order is estimated." Announce only when a UIA client is listening, not as a banner.
4. G-SR4: Annotations, links and form fields appear as UIA children of the page surface in reading order, with names (for annotations: type, author and a text excerpt).
5. G-SR5: Disabled commands that have a reason stay discoverable. Their reason is in the tooltip and in `AutomationProperties.HelpText`. Keyboard focus on a disabled button needs `AllowFocusWhenDisabled` or equivalent **[guess: verify the property in WinUI 3 before relying on it; the fallback is that the command palette shows the reason]**.
6. G-SR6: Decorative things (page-well background, scroll-rail ticks, thumbnails' image part) are hidden from UIA (`AccessibilityView=Raw`, as the current thumbnails already are **[repo]**).

---

## 7. Which control to use

A decision table so that the implementation does not drift. "Not" entries are as important as the "use" entries.

| Need | Use | Not | Notes |
|---|---|---|---|
| Documents in the title bar | `TabView` in the title bar (`CanTearOutTabs`, `TabDroppedOutside`, `IsAddTabButtonVisible`) | A custom tab strip | Tab strip footer is the drag region. Tab close, reorder and tear-out are built in |
| Primary commands | `CommandBar`, icons only, dynamic overflow, `DefaultLabelPosition=Collapsed` (as today **[repo]**) | `MenuBar` (costs 32 epx), ribbon, Pivot | |
| Tool with a choice (highlight colour, ink pen) | `SplitButton` or `ToggleSplitButton` (button applies the last choice, arrow opens the choices) | Two buttons | |
| A menu of view options | `DropDownButton` with `MenuFlyout` and radio items (`RadioMenuFlyoutItem`) and toggles (`ToggleMenuFlyoutItem`) | | |
| Selection mini-toolbar | `CommandBarFlyout` (`ShowMode=Transient`, placement above the selection) | A custom popup | System text-selection pattern |
| Context menus | `MenuFlyout` / `CommandBarFlyout` on right-click and the Menu key | | |
| Pane chooser | `SelectorBar`, icons only, name in tooltip and the pane header | Pivot, a tab row of labelled toggles (as today **[repo]**) | Fits 225% text |
| Outline | `TreeView` | | |
| Thumbnails, annotations list, search results | `ListView` (virtualised) | | |
| Recents grid, organiser grid | `GridView` (virtualised; `CanReorderItems`, `CanDragItems`, `AllowDrop` in the organiser) or `ItemsView` with a `UniformGridLayout` | Hand-built wrap panels (not virtualised) | |
| Text entry | `TextBox` (find, notes); `PasswordBox` (`PasswordRevealMode=Peek`) for passwords | | |
| Page number and zoom entry | An inline composite: `TextBox` plus a "/ N" `TextBlock`, with one UIA name (page box). Zoom is an editable `ComboBox` | `NumberBox` (spin buttons add clutter) | |
| Colour | A swatch grid in a `Flyout`, then **More colours** opens `ColorPicker` in a dialog | | |
| Size and opacity | `Slider` with a value readout and a `TextBox` | Slider alone (not keyboard-precise) | |
| Setting on or off, takes effect now | `ToggleSwitch` | `CheckBox` | |
| Several independent options in a dialog | `CheckBox` | | |
| One of two to four options | `RadioButtons` | `ComboBox` | |
| One of five or more options | `ComboBox` | | |
| Tool on or off | `ToggleButton` | | |
| Non-blocking message tied to a document | `InfoBar` in the page well (overlay, top centre, max 720 epx). One at a time. Persistent until fixed or dismissed | A dialog | Severity: Error, Warning, Informational, Success |
| Confirmation of an undoable action | The **undo bar**: a custom overlay (bottom centre, 8 s, live region) with an **Undo** button | A toast, a dialog | The only custom control in the set. No Windows notification |
| Teach a feature | `TeachingTip` | | Only in response to a user action, never at launch, at most once per feature per device, never in the first session before a document has been read, and never with a purchase or sign-in action |
| A decision the user must make before continuing | `ContentDialog` (password, unsaved changes, split, print, properties, merge confirmation) | InfoBar | One primary button. Destructive buttons are not the default |
| Progress, known | `ProgressBar` determinate (search %, save, split) | | |
| Progress, unknown | `ProgressRing` indeterminate, plus a text status | | |
| Settings | `NavigationView` (left) in a Settings **tab** | A modal dialog | |
| Command palette | A custom overlay: `TextBox` + `ListView` with the **combobox** UIA pattern (edit with a list popup) | `AutoSuggestBox` (can't host sectioned, icon-and-shortcut rows) | |
| Focus the user on one page | `Flyout` anchored to an annotation | Dialog | |

Don't use: hamburger-only navigation, `Pivot`, `MenuBar`, dialogs for information, notifications for in-app events, tooltips as the only place an error is explained, and a splash screen.

---

## 8. Iconography and content rules

### 8.1 Icon intents

Each is a Segoe Fluent Icons glyph, chosen at implementation. Where Windows has a standard glyph for the meaning, use that one.

| Intent | Used for |
|---|---|
| Open folder / file | Open |
| Panes (sidebar) | Left pane toggle |
| Details (right pane) | Right pane toggle |
| Chevron left / right | Previous / next page |
| Zoom out, zoom in, magnifier | Zoom group, Find |
| Fit width, fit page | View menu items and palette |
| Rotate left / right | Rotate view |
| Layout (pages) | View menu button, reflecting the current layout |
| Markup (pen on page) | Markup toggle |
| Highlighter, underline, strikethrough, squiggly, note, text box, ink pen, eraser, shapes, stamp | Markup tools |
| Undo, redo | Edit |
| Print, save | File |
| Search (command line) | Command palette button |
| Info (i) | Document properties |
| Warning, error, success, info | Status |

### 8.2 Copy

- Sentence case. Plain words. Say what happened, then what the user can do. No exclamation marks. No "oops". Match the tone of the strings already written **[repo: Resources.resw has `Banner_EngineRestarting_*`, `Error_*`]**.
- Don't put the product name in a sentence unless it is the actor ("MikePDF doesn't open web addresses"). The existing strings set this tone.
- No concatenated sentences, pluralisation through resource variants, dates and numbers through the user's regional settings (§10.4).
- Never use the words "Adobe", "Acrobat" or other brand names in UI. A factual compatibility line is allowed in the Store listing (§16), not in the app.
- All text is in `.resw` with `x:Uid`. Pseudo-localisation must show no truncated or clipped text at +40% expansion.

---

## 9. Shared interaction patterns

### 9.1 Banners (document-level, non-blocking)

One `InfoBar` at a time at the top centre of the page well. Queue by severity, then arrival. They overlay the page, so the page never moves.

| Banner | Severity | Action | Dismissal |
|---|---|---|---|
| Engine restarting / restored / failing | Warning / Success / Error | Open again | Auto after 6 s (restored); manual otherwise |
| Part of a page could not be drawn | Warning | Details | Manual; remembered per page for this session |
| Fillable form detected | Informational | Highlight fields, Clear form | Manual; remembered per document |
| Document scripts not run | Informational | Details | Manual; remembered per document |
| Restricted by the document's owner | Informational | Details | Manual; remembered per document |
| Save failed | Error | Save as... | Manual |
| Signatures will be invalidated (before save) | A dialog, not a banner | | |
| Unsaved changes were found (after a crash) | A dialog at launch ([screens/14](screens/14-errors-and-recovery.md)) | | |

### 9.2 Dialogs

`ContentDialog`. Title in Subtitle style, body in Body, default button is the safe one. Esc cancels. Enter activates the default. A dialog never opens at launch, apart from the crash-restore question and a password prompt for a file the user has just asked to open.

### 9.3 Undo, save and dirty state

- A tab shows a **modified dot** when the document has unsaved edits, replaced by the close button on hover or focus. The tab tooltip and its accessible name add "modified".
- `Ctrl+S` saves incrementally. When it takes more than 400 ms, an `InfoBar` shows "Saving..." with a determinate bar if known. On success the dot disappears and a polite live region says "Saved". No toast.
- Closing a tab or window with unsaved edits asks "Save changes to {name}?" with **Save**, **Don't save** and **Cancel**. The body says plainly that the changes will be discarded. The edit journal makes crash loss recoverable ([screens/14](screens/14-errors-and-recovery.md)); a deliberate close is a deliberate discard.
- Undo and redo are per document, unlimited, shared by annotation, form and page-organiser edits, and survive engine restarts (§7.3).

### 9.4 Drag and drop

The window is a drop target for PDF files (shows a "Open in MikePDF" overlay, as the app does today **[repo: `Drop_Caption`]**). Dropping on the tab strip adds tabs. Dropping on the page organiser inserts pages. More than 10 files at once asks "Open 14 files?" first **[design; OPEN QUESTION on the threshold]**.

### 9.5 Link safety

Unchanged from the current build **[repo: STATUS]**: an external link shows its address and offers to copy it; nothing opens. The design here does not change that.

---

## 10. Protocol and engine implications

The design needs the following messages or fields beyond the minimum set in §5.4. All go through the schema owner (one work package per wave changes `mikepdf.protocol.json` and bumps `schemaVersion` **[repo: production-roadmap.md]**). "New" means not in the §5.4 minimum list. "Verify" means a PDFium API or behaviour was assumed and has not been checked against the pinned headers (§0.2 rule 2).

| # | Need | Screens | Implication |
|---|---|---|---|
| E1 | Page labels (roman numerals, "A-3") | 01, 03, 05 | New `GetPageLabels` (or a field of `GetPageSizes`). PDFium has `FPDF_GetPageLabel` (verify) |
| E2 | Viewer preferences: direction (R2L), page layout, display title | 04, 01 | New fields on `GetDocumentInfo` |
| E3 | Richer `GetDocumentInfo`: tagged, linearised, revision count, JavaScript present, XFA present, attachment count, permissions flags, encryption method | 12, 14, 01 | Extend. Several flags need verification (revision count and linearisation are structural facts the engine must derive) |
| E4 | Font list | 12 | New `GetFontList`. **PDFium has no document-level font enumeration** (verify). The engine must scan page objects. Make it progressive and cancellable, with a "scanned pages 1 to N" status |
| E5 | `ListAnnotations` with geometry (rects, quad points, ink lists), author, dates, reply links (`/IRT`), review state, popup text | 06, 08 | Extend. The annotations pane needs it for large documents, paged by page range |
| E6 | `HitTestAnnotation`, `GetAnnotationBounds` | 08 | New. Selection, resize handles and hit slop need these on the UI side without a round trip per pointer move |
| E7 | Edit commands for `ApplyEditCommand`: text markup, note, reply, set review state, free text, ink stroke append, shapes, stamps, set field value, move/delete/insert/rotate/extract pages | 08, 09, 10 | Journal entry types, one per command (§7.4) |
| E8 | Search options and results: whole word, regex, diacritic fold; streamed hits with a **context snippet** and hit rectangles; a `Cancel` that acts within one page | 05 | Extend `Search`. First hit within 300 ms (§11) |
| E9 | Attachments | 06 | New `GetAttachments`, `SaveAttachment` (writes to a broker handle with Mark of the Web applied). The engine must never open or run them (§13.1) |
| E10 | Print rendering | 11 | New `RenderBand` (page, DPI, band rectangle, flags: annotations, form fields, grayscale), cancellable, memory capped. Preview uses `RenderTile` |
| E11 | New blank document and cross-document page import | 10 | `InsertPages` needs a **source** document in the same engine process (`fpdf_ppo` imports between two open documents, verify). Merge and cross-tab drag therefore load the sources into the destination's engine as read-only handles |
| E12 | Form field list: tab order and `/Tabs`, required, read-only, max length, comb, format script kind, tooltip (`/TU`) as the label | 09 | Extend `GetFormFields`. Whether PDFium's form environment advances focus on Tab is **not verified**. If it doesn't, the broker implements tab order using the field list and `FormFocus` |
| E13 | Page colour mode | 03, 04 | Invert and sepia: GPU colour matrix only, no engine change. **High-contrast page remap** needs a render option (verify what PDFium supports) |
| E14 | Repair: save a recovered copy of a damaged document | 14 | `SaveFull` on a document the engine opened through its recovery path. The engine must report that it recovered (`Damaged` with a recoverable flag) |
| E15 | Thumbnails in the recents grid | 02 | `RenderThumbnail` (exists) on the **pool** engine at 256 px. Cache on disk (privacy: see [screens/02](screens/02-empty-state.md)) |
| E16 | Structure tree for headings | 06, 03 | `GetStructTree` (in §5.4). Scenario 6 needs headings navigable. How a screen reader finds headings in a custom surface is **open** (S7) |
| E17 | Layers (optional content) | 06 | P2. **PDFium has no public API to list optional content groups** (verify). Don't promise a Layers pane before an ADR |

---

## 11. Screen index and the screen template

### 11.1 Index and scenario map

§18 scenarios: 1 Accountant, 2 Contract review, 3 Government form, 4 Pen user, 5 Paperwork assembly, 6 Screen reader user, 7 Hostile file, 8 Offline traveller, 9 Enterprise, 10 Big file.

| File | Area | §18 scenarios served |
|---|---|---|
| [screens/01-main-window.md](screens/01-main-window.md) | Title-bar tabs, command bar, left and right panes | 1, 2, 3, 4, 5, 6, 7, 9, 10 |
| [screens/02-empty-state.md](screens/02-empty-state.md) | Recents grid, pinned files, drop target | 1, 7, 8, 10 |
| [screens/03-reading-and-presentation.md](screens/03-reading-and-presentation.md) | Reading mode (F11), presentation (Ctrl+L) | 2, 6, 10 |
| [screens/04-two-page-and-cover.md](screens/04-two-page-and-cover.md) | Two-page, cover, paged modes | 2, 10 |
| [screens/05-find.md](screens/05-find.md) | Find bar and results | 1, 6, 10 |
| [screens/06-side-panes.md](screens/06-side-panes.md) | Thumbnails, outline, annotations, attachments | 1, 2, 5, 6, 10 |
| [screens/07-command-palette.md](screens/07-command-palette.md) | Command palette | 6, 9 (all through P3) |
| [screens/08-markup-and-ink.md](screens/08-markup-and-ink.md) | Markup toolbar, notes with replies, ink and pen mode | 1, 2, 4, 6 |
| [screens/09-forms.md](screens/09-forms.md) | Form filling, tab order, validation | 3, 6 |
| [screens/10-page-organiser.md](screens/10-page-organiser.md) | Organiser, merge, split | 5 |
| [screens/11-print.md](screens/11-print.md) | Print | 2 |
| [screens/12-document-properties.md](screens/12-document-properties.md) | Document properties | 9 |
| [screens/13-settings-and-policies.md](screens/13-settings-and-policies.md) | Settings, enterprise policies, About | 8, 9 |
| [screens/14-errors-and-recovery.md](screens/14-errors-and-recovery.md) | Errors, damaged file, engine recovery, crash restore | 7, 10 |
| [screens/15-first-run.md](screens/15-first-run.md) | First run and the file-association prompt | 8, 9 |

### 11.2 The template every screen file follows

1. **Purpose and scenarios** it serves.
2. **Layout**: ASCII wireframes at the breakpoints that matter.
3. **Behaviour**: what happens, in order.
4. **States**: loading, empty, error, keyboard focus, high contrast, 225% text scale, narrow window, touch.
5. **Accessibility**: UIA names, control types and AutomationIds, what Narrator should announce (intended), and the focus order.
6. **Keyboard path**: how to do the main tasks without a pointer.
7. **Engine and protocol implications**.
8. **OPEN QUESTIONS** for that screen.

AutomationIds use the existing PascalCase convention (`PageIndicator`, `FindBox`, `ThumbnailList`) **[repo]**. Existing IDs that the UI tests use stay where the control survives: `OpenButton`, `SidebarButton`, `FindButton`, `ZoomButton`, `ZoomInButton`, `ZoomOutButton`, `FitWidthButton`, `FitPageButton`, `RotateLeftButton`, `RotateRightButton`, `PageIndicator`, `ThumbnailList`, `OutlineTree`, `FindBox`, `FindBar`, `Banner`. Where this design removes a control, the screen file says so, and the implementing work package updates the UI tests in the same change.

---

## 12. What this design changes in the current app

Read from [MainWindow.xaml](../../src/MikePDF.App/MainWindow.xaml) **[repo]**:

| Today | Design |
|---|---|
| One document per window; title text only | Title-bar `TabView` ([01](screens/01-main-window.md)) |
| `CommandBar` of 10 icon buttons plus a page-indicator button, 7 of them zoom, fit and rotate | Page box, zoom group, one **View** menu, Find, Markup, Print, palette, Details (12 buttons plus two boxes at L width) |
| Sidebar toggled with two labelled toggle buttons | `SelectorBar` of icons with up to six panes |
| Page indicator is a button that opens a dialog | An inline editable page box |
| Find box has Match case only | Find bar with case, whole word, regex, diacritics, results list, scroll-rail ticks |
| Empty state is a text list of recents | Thumbnail grid, pinned files, drop target |
| Mica Base | Mica Alt |
| Title bar 40 epx plus an app-bar default height of about 48 epx. About 80 px of chrome in the Store screenshots | 80 epx total, fixed; contextual swaps in place |

---

## 13. OPEN QUESTIONS (roll-up)

Each is also stated in the screen file where it matters. Numbers refer to the files.

| # | Question | File |
|---|---|---|
| Q1 | Print: build MikePDF's own print sheet, or use the system print dialog? Needs ADR-0011. This spec recommends **a MikePDF print sheet plus a link to printer preferences** | [11](screens/11-print.md) |
| Q2 | Should the single-tab case merge the command bar into the title-bar row (40 epx total chrome)? The spec says no by default, to avoid a layout jump when a second tab opens | [01](screens/01-main-window.md) |
| Q3 | Policy registry path: §7.7 says `HKLM\Software\Policies\Quire`. After the rename (ADR-0016) it should be `...\MikePDF`. Confirm | [13](screens/13-settings-and-policies.md) |
| Q4 | Selection toolbar on by default? This spec says yes (one click to mark). Some users find popups intrusive; a setting turns it off | [08](screens/08-markup-and-ink.md) |
| Q5 | "Pen writes ink by default" when a pen digitizer is present: on or off at first run? This spec says **on**, with a one-time, non-blocking explanation the first time the pen inks | [08](screens/08-markup-and-ink.md) |
| Q6 | F11: does it also hide the title bar (true full screen)? This spec says yes | [03](screens/03-reading-and-presentation.md) |
| Q7 | §10.2 says Page Down is "next page". In continuous scroll that would skip content. This spec: Page Down scrolls one screen, and in single-page or fit-page layouts a screen is a page. Confirm | [commands.md](commands.md) |
| Q8 | Are document properties editable in v1.0 (title, author)? This spec says read-only in v1.0 | [12](screens/12-document-properties.md) |
| Q9 | Ask once about opt-in diagnostics at first run? This spec says **no prompt**; the choice lives in Settings | [15](screens/15-first-run.md) |
| Q10 | When to show the default-app prompt, and whether to show it at all. This spec: once, on the empty state only, after the second time the user sees it | [15](screens/15-first-run.md) |
| Q11 | The comment author defaults to the Windows account display name (§8.2). That is personal data sent to everyone who gets the file. This spec asks the user once, inline, the first time they add a comment | [08](screens/08-markup-and-ink.md) |
| Q12 | The threshold of "Open N files?" confirmation (10) | §9.4 |
| Q13 | Multi-window and Explorer launches: "add a tab to the most recent window" is the §7.6 default. Confirm the setting's default and its wording | [13](screens/13-settings-and-policies.md) |
| Q14 | A **Share** command (Windows share sheet) is in the inventory as P1. Confirm it is wanted, since sharing sends the file elsewhere | [commands.md](commands.md) |
| Q15 | How a screen reader finds **headings** in the custom page surface (scenario 6) | [01](screens/01-main-window.md), [06](screens/06-side-panes.md) |
| Q16 | AI: the **Ask** tab in the right pane is reserved but hidden until M7. Confirm it should not be shown, even disabled, before then (this spec: not shown) | [01](screens/01-main-window.md) |
