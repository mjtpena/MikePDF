# UX metrics: how "superior" will be measured

- Part of the [MikePDF design spec](README.md)
- Date: 2026-11-02. Status: **a plan.** No number in this file under "MikePDF" has been measured on a build, and no competitor number has been measured at all, unless a row says so.
- Honesty rules: master prompt §19.3. A row marked **designed** was counted from the wireframes. A row marked **target** is a number we intend to hit. A row marked **not measured** has no number. **Do not publish a comparison that has an unmeasured side.**
- Related: [competitive-teardown.md](competitive-teardown.md) (what to beat), [commands.md](commands.md) (shortcuts used in the scripts), the [screens](screens/) (the step lists).

## 1. What "superior" means

The roadmap's words are: "UI/UX superior means it measurably beats Acrobat Reader, Edge and SumatraPDF on the §11 budgets, keyboard completeness (§10.2) and accessibility (§10.3), with a design that follows Windows 11 inbox apps (§10.1)" **[repo: production-roadmap.md]**. This file makes that testable.

**MikePDF may be called superior to a given competitor only when all five gates below hold for that competitor.** Gates 1 to 4 are mechanical. Gate 5 needs people.

| Gate | Condition | Evidence |
|---|---|---|
| **G1 Budgets** | Every §11 budget is met on R1, and on R2 and R3 where §11 says so (section 3) | Perf harness reports (W1-perf), R1/R2/R3 runs. **User action:** hardware (R-17) |
| **G2 Effort** | In each of the ten §18 scenario scripts, MikePDF's action count is **at most** the competitor's measured count, and **strictly lower in at least 7 of 10** (section 2). No scenario where MikePDF needs more than 120% of the competitor's actions | Script runs on both apps, counted by a second person from the recording |
| **G3 Speed** | Cold open to first pixel (1-page, 20-page, 2,000-page) is at most the competitor's median of 10 runs on the same R1 in every case, or within the explicitly stated margin (Sumatra: 1.25x, [teardown S1](competitive-teardown.md#43-against-sumatrapdf)) | Section 4 protocol |
| **G4 Accessibility** | Accessibility Insights FastPass has zero failures on each screen in section 5.1. The WCAG 2.2 AA checklist (5.3) has no open **Fail**. Scenario 6 completes with NVDA and no mouse | Section 5. **User action:** manual Narrator, NVDA passes (STATUS "Waiting on the user") |
| **G5 People** | A summative study (section 6.5) has a mean SUS of at least 80, unassisted task completion of at least 90% across the core tasks, and more participants preferring MikePDF's look and ease than the competitor's | Section 6. **User action:** recruit and run |

A competitor that can't be measured (EULA, availability) is simply not claimed against. **Check each EULA's terms on benchmarking and publication before publishing anything (R-15 in the risk register is about Acrobat automation; the same question applies to benchmarking) [unverified].**

---

## 2. Task scripts, click counts and timings

### 2.1 Counting rules

An **action** is one discrete user input: a click, a tap, a key chord (`Ctrl+F` is one), a drag, or one continuous run of typed text (typing "contingent liabilities" is one action; the characters aren't counted). Choosing a file in the system picker by typing its path counts as one action. **Not counted:** thinking, reading, scrolling to look (unless the scenario needs a scroll to a destination), and waiting. Count the **shortest path a user who knows the product would take**, and separately the path a first-time user took in the study.

Two paths are given where they differ: **K** (keyboard) and **M** (pointer). A script passes if either path meets the gate; the study records which path each participant used.

Each count below is **designed** from the wireframes: [01](screens/01-main-window.md) to [15](screens/15-first-run.md). Competitor cells hold a number only where a cited document gives the steps; otherwise "not measured".

### 2.2 The ten scenarios

#### 1. Accountant: find, highlight three passages, comment, save

| Step | MikePDF K | MikePDF M | Edge | Sumatra | Reader |
|---|---|---|---|---|---|
| Open the 600-page file | 2 (`Ctrl+O`, pick) | 2 | not measured | not measured | not measured |
| Search "contingent liabilities" | 2 (`Ctrl+F`, type) | 2 (Find button, type) | not measured | 2 (`Ctrl+F`, type) **[src]** | not measured |
| Highlight passage 1 | 1 (`Ctrl+H` on the current hit) | 2 (select, click swatch) | 4 (select, right-click, Highlights, colour) **[src]** | 2 (select, `a`) **[src]** | not measured |
| Highlight passages 2 and 3 | 4 (`Enter`, `Ctrl+H`, twice) | 4 | 8 **[src]** | 4 **[src]** | not measured |
| Comment on each passage | 9 (`Ctrl+M`, type, `Esc`, x3) | 9 (click Note on the toolbar, type, click away, x3) | 15 (select, right-click, Add Comment, type, tick, x3) **[src]** | not measured (note support not confirmed in the pages read) | not measured |
| Save | 1 (`Ctrl+S`) | 1 | not measured | not measured | not measured |
| **In-app total** | **19** | **20** | **27 or more** (the Edge cells above) | not complete | not measured |

Out of the app: attaching the file in the mail client. Finding the file for it: `Ctrl+Shift+P`, "show in folder", `Enter` (3 actions).

| Measure | Target | Source of the target |
|---|---|---|
| First page visible, 600-page file in a SharePoint-synced folder | at most 1.0 s (the 2,000-page budget) | §11 |
| First search hit | at most 300 ms; all hits in a 500-page text PDF at most 3 s | §11 |
| Every comment visible to a colleague in Acrobat Reader | 100% | §0.3, fidelity harness; manual Acrobat pass (**user**) |
| Highlight visible after a click | at most 100 ms | design target, **not measured** |

#### 2. Contract review: two-page, notes with replies, print 3 to 7 double-sided

| Step | MikePDF K | MikePDF M | Edge | Sumatra | Reader |
|---|---|---|---|---|---|
| Open the 40-page file | 2 | 2 | not measured | not measured | not measured |
| Two-page mode | 1 (`Ctrl+5`) | 2 (View, Two pages) | not measured (documented in Dev/Canary only at last read **[src, may be stale]**) | 1 (`Ctrl+7`) **[src]** | not measured |
| Two notes, each with one reply | 14: per note 4 (select text, `Ctrl+M`, type, `Esc`); per reply 3 (open, type, `Ctrl+Enter`) | 14 | not measured (replies **[unverified]**) | not measured | not measured |
| Print pages 3 to 7, two-sided | 6 (`Ctrl+P`, range, sides, `Enter`; see [11](screens/11-print.md)) | 7 | not measured | not measured | not measured |
| Save | 1 | 1 | not measured | not measured | not measured |
| **Total** | **24** | **26** | not measured | not measured | not measured |

Measures: two-page mode on a 4K monitor holds 60 fps scrolling (§11); the print job produces pages 3 to 7 on both sides on Microsoft Print to PDF and a physical printer (the §8.10 test matrix).

#### 3. Government form: 120 fields

| Measure | MikePDF | Competitor |
|---|---|---|
| **Pointer actions required** to fill every field and save | **0** (open, `Tab` through all 120 fields in document order, `Ctrl+S`) | not measured |
| Mouse-or-keyboard actions beyond typing and `Tab`: checking the required list | 2 (open the list, go to the first empty required field) | not measured |
| Values intact after reopen in MikePDF and in Edge | 120 of 120 | **User action** for the Edge check, §18.3 |
| Key-to-glyph latency in a field | at most 50 ms p95 (**target**, no engine round trip per key) | not measured |
| Tab order correct | Matches the file's `/Tabs` or annotation order on 100% of fixture forms | Fixture test |

#### 4. Pen user: 30 slides

| Step | MikePDF | Edge | Sumatra |
|---|---|---|---|
| Actions before the first stroke is on the page (document open, pen in hand) | **0** | 1 (click Draw) **[src]** | not measured |
| Switch to the highlighter | 1 (tap the highlighter pen) | not measured | not measured |
| Scroll by finger without disturbing ink | 0 (finger never inks; palm ignored while the pen is in range) | not measured | not measured |
| Undo a stroke | 1 (`Ctrl+Z` or the Undo button) | not measured | not measured |
| Pen-to-pixel latency | at most 25 ms p95 (§11) | not measured | not measured |

The latency, pressure fidelity and palm-rejection numbers need the **Surface Pro, the pen and a 240 fps camera** (S5, **user action**). Until then, G1 can't pass for this scenario.

#### 5. Paperwork assembly: merge, reorder, rotate, delete a blank, export

| Step | MikePDF | Others |
|---|---|---|
| Merge five files | 3 (`file.merge`, select all in the picker, Open) | Edge, Sumatra: not documented as a feature in the pages read **[unverified]**; Reader: a paid-tier tool **[unverified]** |
| Rotate one page | 2 (select, `Ctrl+]`) | n/a |
| Delete a blank page | 2 (select, `Delete`) | n/a |
| Save as one PDF | 3 (`Ctrl+S`, name, `Enter`) | n/a |
| **Total** | **10** | "needs another tool" until a competitor is measured doing it |

Time target: from "Merge" to a saved file for five small PDFs, at most 30 s for a practised user (**target, not measured**).

#### 6. Screen-reader user: NVDA and keyboard only

| Step | Path | Risk |
|---|---|---|
| Open a tagged PDF | `Ctrl+O`, pick | None |
| Navigate by headings | Bookmarks pane (`Alt+Shift+B`) and arrows; or a screen-reader headings command if the page surface exposes headings | **Q15 is open**: exposing headings in the custom surface is not designed to work yet |
| Read a table | Text pattern by line, and (if exposed) table navigation | Table navigation needs the structure tree exposed as UIA table elements. **Not promised for v1.0.** If not, the scenario fails and must be reported as failed |
| Follow a link | `Tab` to the link, `Enter` (`link.follow`) | Internal links only; external links show their address |
| Add a comment | `F7`, `Shift+Arrows`, `Ctrl+M`, type, `Ctrl+Enter`, `Esc` | |

Pass criteria: the script completes with **no mouse, no sighted help, and no dead end**. Measure: completion (yes or no), time, and the count of "I don't know where I am" moments the observer notes. This scenario is the most likely to fail G4, and is the one the design can't yet promise.

#### 7. Hostile file

| Measure | Target | Source |
|---|---|---|
| Time from the engine dying to the error or recovery shown | at most 5 s (watchdog), recovery at most 2 s | §5.2, §11 |
| Windows or dialogs shown | 0 (an in-tab error page) | [14](screens/14-errors-and-recovery.md) |
| Other tabs usable (responsive to input within 100 ms) | 100% | Journey 7 |
| User work lost | 0 | Journey 7, 8 |

#### 8. Offline traveller

| Measure | Target | Method |
|---|---|---|
| Outbound network connections made by MikePDF processes during scripts 1 to 7 and 9 | **0** | Windows Firewall block rule plus a network trace (ETW), run with the network disabled |
| Features that fail offline | 0 (non-cloud) | Run every script offline |

#### 9. Enterprise

| Step | MikePDF | Measure |
|---|---|---|
| Policies set (JavaScript off, AI local-only, telemetry off) | Enforced | Test each §7.7 policy: the setting is locked and the behaviour matches |
| Find the policies in the app | 4 (`Ctrl+Shift+P`, "about", `Enter`, **Show**) | Count; at most 4 |
| Policy typo | App starts; the bad value is listed as "Not applied" | [13](screens/13-settings-and-policies.md) |

#### 10. Big file: 5,000 pages, 1.2 GB

| Measure | Target | Source |
|---|---|---|
| First page visible | at most 1.0 s | §11 |
| Blank area while scrolling | none | §11 |
| Engine memory after a full scroll | within the cap (600 MB for 500 pages; the cap for 5,000 pages isn't stated in §11 **[gap]**) | §11, §5.2 |
| Frame rate during a 10 s fling | 60 fps R1, no frame over 50 ms | §11 |
| First-use error rate | no banner except as designed | [14](screens/14-errors-and-recovery.md) |

---

## 3. §11 budgets, harness and status

All budgets are measured on R1 unless noted. "Evidence so far" quotes what is in the repository. "Not measured" means no run on the reference hardware exists.

| §11 metric | Budget | Harness | Evidence so far |
|---|---|---|---|
| Cold start to an interactive empty window | at most 600 ms (R1), 350 ms (R3) | W1-perf (`tools/MikePDF.Perf`), ETW | Not measured on R1 |
| Cold open of a 1-page PDF from Explorer to first pixel | at most 800 ms | W1-perf | Not measured on R1. S2 on DEV: "first sharp tile, typical text page (end to end), ready p95 about 9 ms, composed p95 about 40 ms (about 48 ms in a fresh app)" **[repo: ADR-0006]**, which excludes process start |
| Warm open of a 20-page PDF to first pixel | at most 250 ms | W1-perf | Not measured |
| 2,000-page PDF: first page visible | at most 1.0 s | W1-perf | S2 on DEV: 60 to 75 ms **[repo: ADR-0006]**, DEV only |
| Scroll frame rate on a typical text PDF | 60 fps R1; display rate R3; no frame over 50 ms in a 10 s fling | S2 harness, PresentMon (optional, needs a group membership **[repo: STATUS]**) | S2 on DEV: 108 runs, 64,812 frames, none over 50 ms, worst 31 ms **[repo]** |
| Blank area while scrolling | none | S2 harness | Zero on text and transparency documents. **35 to 49 of 600 frames blank** on the dense-vector stress pinned to 4 E-cores **[repo]** |
| Zoom gesture | next frame; sharp tiles within 300 ms | S2 | About 150 ms to sharp tiles **[repo]** |
| Search, 500-page text PDF | full results at most 3 s; first result at most 300 ms | W1-perf | Not measured |
| Ink pen-to-pixel | at most 25 ms | S5 camera | Not measured (S5 incomplete) |
| UI process memory, idle, one 20-page document | at most 200 MB | W1-perf | **156 to 204 MB** already, before any tile cache (D-2, R-33) **[repo]** |
| Engine process memory, 500 pages after a full scroll | at most 600 MB | W1-perf | Not measured |
| Install size, single architecture | at most 60 MB | package size | Not measured in this document |
| Engine crash to a restored document | at most 2 s | Journey 7 | Not measured as a budget |

### 3.1 Interaction budgets this design adds

These are **design targets** for the chrome. They aren't in §11. Propose them to Opus for an ADR if kept. All are **not measured**.

| Interaction | Target (p95, R1) | Why |
|---|---|---|
| Key press to palette visible | at most 100 ms | The palette is the answer to "everything is a command" |
| Keystroke to updated palette results | at most 50 ms | Typing must not lag |
| Text selection finished to selection toolbar visible | at most 100 ms | One-click marking |
| Click on a colour to the highlight drawn | at most 100 ms (optimistic UI; the engine confirms later) | |
| Undo or redo to visible change | at most 100 ms | |
| Left pane open or close | at most 150 ms (`MotionBase` 167 ms; instant in reduced motion) | |
| Tab switch to a loaded document's page visible | at most 150 ms | |
| Note icon click to popup | at most 100 ms | |
| Mode swap (Markup, Organise) | at most 167 ms, with no layout shift of the page well (**0 epx**) | |
| Page box `Enter` to page visible | at most 100 ms plus tile time | |
| Form field click to editor ready | at most 100 ms | |
| Command bar overflow reflow on resize | within one frame (16.7 ms) of the end of the resize step | |

Layout shift of the page well when any banner, bar or overlay appears or disappears: **0 epx** (principle P1). Automated check: record the page rectangle before and after each state in the UI tests (FlaUI, ADR-0009).

---

## 4. Competitor measurement protocol

Goal: numbers a reviewer can reproduce. Nothing here has been run.

### 4.1 Setup

| Item | Rule |
|---|---|
| Hardware | R1, R2 and R3 (§11), the same unit for every product. Record exact model, CPU, RAM, GPU, storage, display and scale factor, power plan (plugged in, Balanced) |
| OS | The same Windows 11 build for all products; record the build. Windows Update paused; Defender real-time protection **on** (as users have it), with the same exclusions for all |
| Installation | A clean profile (new local user, or a reset VM snapshot) per product. Install the **current stable** release from the vendor. Record the exact version. No sign-in. Decline optional offers (record each offer shown: that is a metric) |
| Documents | The same set, on the local SSD. 1-page text PDF. 20-page typical text PDF. 600-page report. 2,000-page text PDF. 40-page contract. 120-field form. A scanned file. From the corpus manifest (ADR-0010), with a licence that allows use |
| Runs | One discarded warm-up, then **10** measured runs. Report **median and p95**, plus the min and max. For cold runs, reboot or clear the file cache (**RAMMap** "empty standby list" needs admin; **[unverified]**: or use a fresh boot) between runs |
| Cold vs warm | **Cold:** the product is not running and has not run since boot. **Warm:** running, window open, no document |

### 4.2 Measures and tools

| Measure | Tool | Notes |
|---|---|---|
| Cold open to first rendered page pixel from Explorer | A 240 fps camera pointed at the screen (4.2 ms resolution) for **competitors**, because their internals can't be instrumented; ETW and PresentMon traces for MikePDF to cross-check the camera on the same runs | Start of measure: the frame after the double-click's release. End: the first frame with page text legible |
| Cold start to an interactive window | Same | |
| Search time | Stopwatch from the Enter key (key logger timestamp) to the first highlight, and to the "complete" count | Same document |
| Scroll smoothness | PresentMon or the 240 fps camera on a scripted fling | Group membership needed for PresentMon **[repo: STATUS]** |
| Memory | **Private working set summed over every process of the product** (Edge: all `msedge.exe` of the PDF tab plus shared processes, **noted**) after opening, and after a full scroll | Task Manager details or `Get-Process` sums; same method for all |
| Chrome footprint | Screenshots at 1366x768 100%, and 3840x2160 at the machine's default scale, with the product's **default** layout and one document open. Measure the vertical pixels above the page area and the horizontal pixels taken by default panes, with an image ruler. Record the scale | Replaces the guesses in [teardown section 3](competitive-teardown.md#3-chrome-footprint) |
| Action counts | Run each script by a person who knows the product, **screen-recorded**; a second person counts actions from the recording | Section 2.1 |
| Interruptions and offers | First launch on a clean profile: count every dialog, tour step, sign-in prompt and offer before the document is readable | |
| Install size | Size on disk of the install folder or package | |

### 4.3 Reporting

For each product and measure: the value (median, p95), the sample size, the version, the hardware, the date, and the recording or trace file name. **"Not measured" stays in the table until it is.**

---

## 5. Accessibility checks

### 5.1 Accessibility Insights for Windows: FastPass

FastPass has two parts: **automated checks** ("dozens of accessibility rules") and a manual **tab stops** test (keyboard access: missing tab stops, keyboard traps, incorrect tab order) **[src: [FastPass docs](https://accessibilityinsights.io/docs/windows/getstarted/fastpass/), accessed 2026-11-02]**. The docs give no formal pass threshold. This plan sets one: **zero automated failures, and zero tab-stop problems** (unreachable elements, traps, missing focus indicator, inconsistent order, unexpected focus movement).

Run, per screen and state in the table below, with **Entire app** selected: automated checks (`Shift+F8` in the tool), then record tab stops (`Shift+F7`) **[src]**.

| Screen / state | Automated | Tab stops | Status |
|---|---|---|---|
| Main window, empty ([02](screens/02-empty-state.md)) | yes | yes | not run |
| Main window with a document, panes closed ([01](screens/01-main-window.md)) | yes | yes | not run |
| Left pane: each of Thumbnails, Bookmarks, Annotations, Attachments, Search ([06](screens/06-side-panes.md), [05](screens/05-find.md)) | yes | yes | not run |
| Find bar open | yes | yes | not run |
| Command palette open ([07](screens/07-command-palette.md)) | yes | yes | not run |
| Markup mode, selection toolbar, note popup, Format tab ([08](screens/08-markup-and-ink.md)) | yes | yes | not run |
| Form with the Form bar, Required list ([09](screens/09-forms.md)) | yes | yes | not run |
| Organiser view and Split dialog ([10](screens/10-page-organiser.md)) | yes | yes | not run |
| Print sheet ([11](screens/11-print.md)) | yes | yes | not run |
| Document properties ([12](screens/12-document-properties.md)) | yes | yes | not run |
| Settings (each section), Keyboard shortcuts dialog, About ([13](screens/13-settings-and-policies.md)) | yes | yes | not run |
| Error page, banners, password dialog, restore dialog ([14](screens/14-errors-and-recovery.md)) | yes | yes | not run |
| Reading mode HUD ([03](screens/03-reading-and-presentation.md)) | yes | yes | not run |
| Default-app prompt ([15](screens/15-first-run.md)) | yes | yes | not run |

**The page surface:** FastPass tests the UIA tree; it can't judge the document content. The page surface's Text pattern is tested separately with Narrator and NVDA (5.2) and S7's prototype, **not** by FastPass.

**Automation in CI:** §10.3 asks for Accessibility Insights checks in CI "where possible". The Windows tool's command-line automation is **not verified here**. Plan: a UI test (FlaUI, ADR-0009) that walks the UIA tree of each screen and checks the **rules we can state**: every interactive element has a non-empty `Name`, a control type, an `AutomationId`, and a `BoundingRectangle` of at least 24 x 24 epx where it's on screen; every list item is reachable; no two siblings share the same name and role without a distinguishing description. This is **not** a replacement for FastPass.

### 5.2 Screen-reader scripts

| Script | Reader | Status |
|---|---|---|
| Narrator: the main window, find, palette, a note, a form, the organiser, a dialog | Narrator | Written as the "Narrator, intended" tables in each screen file. **Not run.** The existing script is `spikes/S7/narrator-script.md` **[repo: STATUS]** |
| NVDA: scenario 6 | NVDA | Not written. **User action:** manual pass |
| JAWS: scenario 6 | JAWS | Needs a licence (**user action**, roadmap) |

For each: the expected announcement (the intended text in each screen file) versus what was heard, and a verdict (match, acceptable difference, problem).

### 5.3 WCAG 2.2 Level A and AA checklist for the chrome

WCAG is written for the web. For software the W3C Note **WCAG2ICT 2.2** (December 2025) explains how Level A and AA criteria apply to non-web software, taking the whole program as the unit and relying on the platform's accessibility services for programmatic information **[src: [WCAG2ICT 2.2](https://www.w3.org/TR/wcag2ict-22/), accessed 2026-11-02]**. That Note is informative. Some regulations exclude four criteria (2.4.1, 2.4.5, 3.2.3, 3.2.4) for non-web software, and EN 301 549 also excludes 2.4.2 and 3.1.2 **[src, same page]**. MikePDF meets the excluded ones anyway where they cost little, and marks them so.

"Chrome" here means the app's UI: tab strip, command bar, panes, overlays, dialogs, settings. **The document content (PDF pages) is out of scope for this checklist** (its accessibility depends on the file; the app's job is to expose it, §10.3).

Status for every row today: **not tested**. "Design" states how the spec meets it. The test column is how to check.

| SC | Name | Lvl | Design (how the chrome meets it) | Test |
|---|---|---|---|---|
| 1.1.1 | Non-text content | A | Every icon-only control has an accessible name and a tooltip; thumbnails' images are hidden (`Raw`) with the item named "Page n"; decorative glyphs hidden | FastPass names; Narrator |
| 1.2.x | Time-based media | A, AA | Not applicable: no media in the chrome | n/a |
| 1.3.1 | Info and relationships | A | UIA control types, `LabeledBy`, groups, `HeadingLevel` for section headings, `List`/`Tree`/`Grid` patterns | FastPass; Narrator |
| 1.3.2 | Meaningful sequence | A | Focus order = reading order (tab strip, command bar, left pane, page, right pane) | Tab stops |
| 1.3.3 | Sensory characteristics | A | Instructions don't depend on colour, position or shape alone | Copy review |
| 1.3.4 | Orientation | AA | Works in portrait and landscape (tablet) | Rotate a device |
| 1.3.5 | Identify input purpose | AA | Mostly N/A (the app has few personal-data fields; the author name and password fields are labelled). UIA has no autocomplete-token property | Review |
| 1.4.1 | Use of colour | A | State uses glyphs, outlines or text as well as colour (find current hit, invalid field, selected cell, required) | Grayscale and high-contrast review |
| 1.4.3 | Contrast (minimum) | AA | 4.5:1 text, 3:1 large; checked on the Mica and the solid fallback, light and dark | Contrast tool at each screen. **The find-hit colours in README 4.6 haven't been checked** |
| 1.4.4 | Resize text | AA | Windows text size to 225% (above the 200% in WCAG); no fixed text heights | Set 225%, walk every screen |
| 1.4.5 | Images of text | AA | None in the chrome | Review |
| 1.4.10 | Reflow | AA | The chrome works at 320 epx wide, without two-dimensional scrolling (apart from the page) | Narrow window test |
| 1.4.11 | Non-text contrast | AA | 3:1 for controls' boundaries and states, focus rings | Contrast tool |
| 1.4.12 | Text spacing | AA | WinUI has no user text-spacing override that the app can honour **[unverified]**; layouts flow and tolerate extra spacing (no fixed heights). Treat as "meets by flow", test with taller line heights | Manual |
| 1.4.13 | Content on hover or focus | AA | Tooltips: dismissible (`Esc`), hoverable, persistent. The selection toolbar and the HUD are not hover-only and can be reached by keyboard | Manual |
| 2.1.1 | Keyboard | A | Every command reachable: registry test ([commands.md rule 1](commands.md#11-rules)); pointer-only drawing is the stated exception (ink; see 08) | Script (journey 9) |
| 2.1.2 | No keyboard trap | A | `F6` leaves the page; dialogs close with `Esc` | Tab stops |
| 2.1.4 | Character key shortcuts | A | Off by default; remappable; only active with page focus | Settings test |
| 2.2.1 | Timing adjustable | A | No time limits on user input. Auto-hiding items (HUD, undo bar) don't remove capability: undo is `Ctrl+Z`, the HUD's commands are in the palette and chrome | Review |
| 2.2.2 | Pause, stop, hide | A | No auto-moving content over 5 s except progress indicators with a text status; reduced motion honoured | Review |
| 2.3.1 | Three flashes | A | No flashing | Review |
| 2.4.1 | Bypass blocks | A | `F6` landmarks. (Excluded by some regulations for software.) | Tab stops |
| 2.4.2 | Page titled | A | Window title "{document} - MikePDF"; dialog titles | Narrator |
| 2.4.3 | Focus order | A | See 1.3.2; focus return rules in each screen file | Tab stops |
| 2.4.4 | Link purpose | A | Link names say where they go; document links announce their address, not "click here" | Narrator |
| 2.4.5 | Multiple ways | AA | Palette, menus, buttons, shortcuts, panes. (Excluded by some regulations.) | Review |
| 2.4.6 | Headings and labels | AA | Descriptive labels, sentence-case headings | Review |
| 2.4.7 | Focus visible | AA | Two-ring focus on every control and on custom surfaces | Tab stops, visual |
| 2.4.11 | Focus not obscured (minimum) | AA (2.2) | Scroll margins keep the focused item clear of the find bar, HUD, popups | Manual with overlays open |
| 2.5.1 | Pointer gestures | A | Pinch, swipe and drag have button, keyboard or menu alternatives | Review |
| 2.5.2 | Pointer cancellation | A | Actions fire on release; drags cancel with `Esc` | Manual |
| 2.5.3 | Label in name | A | The accessible name contains the visible text. **Known gap in today's build:** the Match-case toggle shows "Aa" but is named "Match case" **[repo: `FindMatchCaseToggle`]**. Fix by naming it "Aa, match case" or by changing the visible label to an icon | FastPass; Narrator |
| 2.5.4 | Motion actuation | A | No shake or tilt input | Review |
| 2.5.7 | Dragging movements | AA (2.2) | Reorder pages (Move to..., `Alt+Shift+Arrows`), resize panes (keyboard on handle), move annotations (arrows, Format fields) | Script |
| 2.5.8 | Target size (minimum) | AA (2.2) | Command bar 40 x 40; page-level targets at least 24 x 24 with slop; panes' rows 40+ | Bounds check in the UIA walk |
| 3.1.1 | Language of page | A | Resources in en-US/en-AU/en-GB; the app language declared in the manifest | Review |
| 3.1.2 | Language of parts | AA | Document text language isn't exposed to UIA in v1.0 (**gap**; the PDF `/Lang` could set the Text pattern's culture). EN 301 549 excludes this for software | Note as a gap |
| 3.2.1 | On focus | A | Focus never changes context | Review |
| 3.2.2 | On input | A | Settings apply immediately by design (a ToggleSwitch is expected to); the palette doesn't run on typing; the page box doesn't navigate until `Enter` | Review |
| 3.2.3 | Consistent navigation | AA | The command bar keeps its order in every mode (centre changes only) | Review |
| 3.2.4 | Consistent identification | AA | One registry gives one name and icon per command | Registry test |
| 3.2.6 | Consistent help | A (2.2) | Help (`F1`, `Ctrl+/`) and the palette are in the same place in every state | Review |
| 3.3.1 | Error identification | A | Errors in text, with a glyph; field errors name the field | Narrator |
| 3.3.2 | Labels or instructions | A | Every input is labelled; ranges show an example ("1-3, 4-10") | Review |
| 3.3.3 | Error suggestion | AA | Messages say how to fix ("Use ranges like 1-3, 4-10") | Review |
| 3.3.4 | Error prevention (legal, financial, data) | AA | Undo for edits; confirmation for discarding unsaved changes and clearing data; no financial transactions | Review |
| 3.3.7 | Redundant entry | A (2.2) | Author name asked once; passwords not re-requested within the document's session | Review |
| 3.3.8 | Accessible authentication (minimum) | AA (2.2) | The password dialog allows paste and reveal; no puzzle or transcription | Manual |
| 4.1.2 | Name, role, value | A | UIA names, roles, states; custom controls have peers | FastPass; Narrator |
| 4.1.3 | Status messages | AA | Live regions for saved, find count, engine restore, undo bar | Narrator |

Count of gaps already identified: **2.5.3** (Match case name), **3.1.2** (document language), and **1.4.3** (find-colours unchecked). These are design findings, not test results.

### 5.4 Other mechanical checks

| Check | How |
|---|---|
| High contrast | Switch to each of the four Windows contrast themes; walk every screen; no information lost; no text on a mismatched background |
| 225% text | Windows, Accessibility, Text size 225%; walk every screen; no clipping or overlap; every dialog's buttons reachable |
| Narrow | Resize to 320 epx; walk every screen |
| Reduced motion | Windows animation off; no non-essential motion; nothing depends on an animation finishing |
| Touch and pen | Surface; every control reachable with a finger; pen rules ([08](screens/08-markup-and-ink.md)) |
| Mixed DPI | Move the window between 100% and 200% monitors during a drag; chrome and tiles re-layout without a flash |
| Pseudo-localisation | CI build (§10.4): +40% text, no truncation in chrome, no hard-coded strings |
| RTL | Pseudo-RTL build: chrome mirrors, icons that imply direction flip (chevrons), page content does not |

---

## 6. Five-user usability test plan

**The product owner would have to run this.** Sonnet and Opus can prepare the materials and analyse recordings. They can't recruit people or sit with them.

### 6.1 Why five, and what five can and can't show

Nielsen's argument: about 85% of usability problems are found by 5 users, if the average user finds about 31% of them (problems found = N(1 - (1 - 0.31)^n)). The advice is to run several small rounds with fixes between, not one big one. **Caveats from the same source: it assumes one user group (for distinct groups, 3 to 4 per group); and quantitative studies, which give timings and rates, need about 20 users [src: [NN/g, "Why you only need to test with 5 users", 2000](https://www.nngroup.com/articles/why-you-only-need-to-test-with-5-users/), accessed 2026-11-02; the article is dated, and links to a newer analysis I haven't read]**. So:

| Round | Purpose | Participants | Comparisons | When |
|---|---|---|---|---|
| **Round A** formative | Find the biggest problems in M3 flows | 5 general users | None (MikePDF only) | After the M3 build exists |
| **Round B** formative | Check fixes; M4 flows (markup, forms, organiser) | 5 general users (new people) | None | After the M4 build exists |
| **Round AT** assistive technology | Scenario 6 and the keyboard flows | 3 to 4 screen-reader users (NVDA), plus 2 to 3 keyboard-only users | None | After M3 and again after M4 |
| **Round P** pen | Scenario 4 | 3 to 5 pen users on a Surface | None | After M4 |
| **Round S** summative, comparative | The numbers that G5 and the comparisons need | **at least 20** | MikePDF vs one competitor per participant, counterbalanced | Before the v1.0 release decision |

The **5-user rounds don't prove "superior"**. They find what to fix. G5 needs Round S.

### 6.2 Participants

| Group | Who | Screener |
|---|---|---|
| General | Office workers who open PDFs weekly; mixed Windows experience | Not a designer or developer; has used at least one PDF reader this month; not a Sumatra power user |
| Accountant and reviewer profiles | People who mark up long documents | Marks up PDFs monthly or more |
| Forms | People who fill forms | Has filled a PDF form in the last 6 months |
| Pen | Surface or similar pen users | Uses a pen weekly |
| AT | NVDA users | Uses NVDA daily; comfortable with PDFs |

Recruit by a neutral route (not friends of the team). Record age range, Windows familiarity and the competitor each person normally uses. No incentives that depend on liking the product.

### 6.3 Session script (60 minutes, one participant, one moderator, one note-taker)

| Min | Part |
|---|---|
| 0 to 5 | Welcome, consent (recording and data), "we are testing the software, not you", think-aloud practice on a trivial task |
| 5 to 10 | Pre-task questions: which reader do you use; what do you find annoying about it |
| 10 to 50 | Tasks T1 to T6 (below), about 6 minutes each, in a **fixed order that alternates easy and hard**. After each: the **Single Ease Question** (7-point: "Overall, how easy or difficult was this task?") |
| 50 to 58 | **System Usability Scale** (10 items), then an open question: "What would you change first?" |
| 58 to 60 | Thanks, debrief |

Moderator rules: read the task text exactly as written; don't explain the UI; don't answer "how do I...?" (reply "what would you try?"); after 2 minutes stuck, log "stuck", then give one nudge at a time; after 4 minutes, end the task as failed with assistance. Never name a command or a shortcut.

### 6.4 Tasks (neutral wording; the participant never hears the product's terms)

Files are synthetic and non-confidential, from the corpus. The tasks map to §18 scenarios.

| # | Task text for the participant | Scenario | Success criterion | Cap |
|---|---|---|---|---|
| T1 | "Here is a long annual report. Find where it talks about *contingent liabilities*, and mark the three most relevant passages so you can find them again. Add a short comment to each." | 1 | 3 distinct passages marked, 3 comments, saved | 8 min |
| T2 | "You are reviewing this contract with a colleague on a big screen. Read it so you can see two pages at once. Leave a note for your colleague on one clause, and reply to the note already there. Then print pages 3 to 7, printed on both sides, to the PDF printer." | 2 | Two-page view; note added; reply added; print job for 3 to 7 duplex | 8 min |
| T3 | "Fill in this application form (30 fields). When you finish, save it." Moderator watches whether the participant uses `Tab` or the mouse. | 3 | All 30 values correct after reopening | 8 min |
| T4 (Round P and Round B) | "Here are lecture slides. Use the pen to underline an important phrase on page 5, circle a figure on page 8, and highlight a paragraph on page 12. Scroll with your finger. Undo the circle." Surface with pen | 4 | Three marks; the circle undone; no stray ink from touch | 8 min |
| T5 | "Combine these five files into one, in this order: ... One page is upside down. One page is blank. Fix those and save as one file." | 5 | Correct order, rotated, blank removed, saved | 8 min |
| T6 | "Something is wrong with this file. Tell me what is happening and what you would do next." (a damaged file, then a file that crashes the engine, with another document open in a second tab) | 7 | Participant states what is wrong and that the other tab is fine; finds the next step | 4 min |
| T7 | "You want to change the keyboard key that does *X*." and "Find out whether your company has set any rules for this app." | 9 | Shortcut changed; policies found | 5 min |
| T8 | (Observed only) first launch with no file: "You've just installed this. Open a PDF." Then, later, the default-app prompt appears. "What do you make of this message?" | 15 | Opens a file unaided; understands the prompt | 3 min |

Per task, record: **completed unaided** (yes or no), **completed with a nudge**, **failed**; **time on task** (from the recording); **errors** (wrong control, undo, backtracking); **path used** (keyboard or pointer, which shortcut); **first-click** location; **SEQ**; verbatim remarks.

### 6.5 Summative round (Round S)

At least 20 participants, each doing T1, T2, T3, T5 on MikePDF and one competitor (assigned so that each competitor gets an equal number; order counterbalanced by a Latin square; different but equivalent documents so there's no learning effect). Report **means with 95% confidence intervals** for time and SEQ, **completion rates with intervals**, and **SUS per product**.

SUS scoring: for odd items, score minus 1; for even items, 5 minus score; sum the ten values and multiply by 2.5 (0 to 100). A commonly quoted average SUS is about 68 **[unverified recollection; check the source before using as a benchmark]**. The gate G5 asks for a mean of at least **80** for MikePDF **[design target]**.

### 6.6 Analysis and pass criteria

| Item | Rule |
|---|---|
| Issue log | Every observed problem gets: task, what happened, severity (0 none, 1 cosmetic, 2 minor, 3 major, 4 blocker), how many of the participants hit it |
| Fix threshold (Round A, Round B) | Any severity-3 or 4 issue hit by **2 or more** of 5 participants must be fixed or consciously accepted by the owner (written, with a reason) before the next round |
| Round pass (Round A, Round B) | Each task completed unaided by at least 4 of 5, no task with zero unaided completions, no severity-4 issue open |
| AT round pass | **All** participants complete scenario 6 without moderator help, or every failure is a documented bug with a fix |
| Pen round pass | No palm or touch ink in the recordings; latency subjectively "no lag" for all, plus the camera measurement (S5) |
| Round S pass | G5 numbers |

Bias and limits to state in the report: small sample; participants may know the team; moderator effects; the product is new to participants while competitors are familiar; synthetic documents; a lab isn't real use.

### 6.7 Data handling

- Consent in writing. Recordings (screen, audio, face optional) are kept in a private location, shared only with the team, **deleted within 6 months** (or earlier on request).
- Participants use **only synthetic documents**, never their own.
- Reports use "P1, P2..." and no names. Quotes are anonymised.
- MikePDF has **no telemetry**, so there is no field data on how people really use it. These studies and the opt-in diagnostics, if a user turns them on, are the only usage evidence.

### 6.8 What the product owner has to do

| Step | Who |
|---|---|
| Decide the rounds and the schedule | Owner |
| Recruit participants (screener in 6.2) | Owner (or a recruiter) |
| Provide hardware: R1 laptop, 4K display, Surface with pen, a quiet room; screen-recording software | Owner |
| Moderate sessions or appoint a moderator, and a note-taker | Owner |
| Provide NVDA users; JAWS needs a licence | Owner |
| Provide competitor installs and check their EULAs | Owner |
| Prepare tasks, test files, the SUS and SEQ forms, the recording consent text, the analysis sheet | **Sonnet** (can do) |
| Analyse recordings and issue logs, draft the report | **Sonnet**, reviewed by Opus |

---

## 7. Reporting

One report per milestone in `docs/test-reports/`, with: the scorecard (G1 to G5 per competitor), tables from section 2 with measured numbers replacing designed ones, the FastPass status per screen, the WCAG checklist with **Pass / Fail / Not tested** per row, the study findings, and the open issues. Anything not run says "not run". A gate that has any "not measured" doesn't pass (§19.3).

## 8. OPEN QUESTIONS

1. Are the §1 gate thresholds (7 of 10 strictly better, SUS 80, 90% completion) the right bar? They are this document's proposal.
2. Should the **interaction budgets** in 3.1 become ADR-backed budgets next to §11?
3. §11 has no memory cap for 5,000-page documents (scenario 10). Add one by ADR.
4. Is there a way to run Accessibility Insights checks from the command line in CI? Not verified. The UIA-walk test in 5.1 is the fallback.
5. May competitor benchmark results be published? **Check the EULAs.**
6. The comparative study needs competitors installed on test machines: licences for paid ones (e.g. a Foxit or PDF-XChange paid feature) aren't needed for the free feature set, but the Reader's paid features will be out of reach, which limits which scenarios can be compared.
