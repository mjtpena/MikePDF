# S7 manual screen-reader script (Narrator, NVDA, Accessibility Insights)

Status of every result in this file: **not measured (manual pass pending)**. Nothing below has been run against a screen reader. The
automated UIA client test (`A11yTest uia-tests`) exercises the same Text pattern through the real UIA core, but it is not a
screen reader. See `docs/spikes/S7.md`.

Key names below are from memory of the Narrator and NVDA documentation and have **not been verified on this build**. Before the
pass, press **Narrator+1** (Input Learning) and press each key to hear what Windows says it does. Where a key differs, write the
real key in the "Actual key" column and keep going. Narrator key = Caps Lock or Insert.

## Setup

1. Build and generate fixtures:
   `dotnet build spikes/S7/PageA11y -c Release -p:Platform=x64` and `dotnet build spikes/S7/A11yTest -c Release -p:Platform=x64`,
   then `A11yTest.exe gen` (fixtures land in `spikes/S7/fixtures/`).
2. Start the app on one fixture, e.g.
   `spikes\S7\out\bin\PageA11y\x64\Release\net10.0-windows10.0.26100.0\win-x64\PageA11y.exe --fixture spikes\S7\fixtures\a-tagged-single-column.pdf --overlay lines`
   Use `--rot 1` (or the Rotate button) for the rotated passes. `--overlay lines` shows the rectangles UIA reports.
3. Start Narrator (Win+Ctrl+Enter). In Narrator settings turn on the visual highlight of Narrator's cursor (it draws the
   rectangle Narrator believes it is reading, which is the bounding-rectangle check done by eye).
4. Record speech: Narrator+Ctrl+X copies the last phrase to the clipboard; Speech recap (believed Narrator+Alt+X) lists recent
   phrases. Paste the transcript into the "Heard" column.
5. Put focus on the page: Tab until Narrator says "PDF page 1, document" (name and role come from the peer:
   Name "PDF page 1", control type Document).

## Pass A: tagged single column (`a-tagged-single-column.pdf`)

Expected reading order (the structure tree, not the content stream order): heading, paragraph 1, paragraph 2, then the footer.

| # | Action | Believed key | Actual key | Expected speech | Heard | Result |
|---|---|---|---|---|---|---|
| A1 | Focus the page | Tab | | "PDF page 1, document" | | not measured |
| A2 | Read all from start | Narrator+Ctrl+... (read document) | | "Quarterly Findings. The committee reviewed three proposals during the spring session and approved two of them. Funding was released in May. Delivery is expected before the end of the year. Page 1 of 1" | | not measured |
| A3 | Next word, x3 from the start | Narrator+L | | "Quarterly", "Findings", "The" | | not measured |
| A4 | Previous word | Narrator+J | | "Findings" | | not measured |
| A5 | Next line, x2 | Narrator+O | | "The committee reviewed three proposals during", "the spring session and approved two of them." | | not measured |
| A6 | Next character, x3 | Narrator+Period | | "t", "h", "e" | | not measured |
| A7 | Next paragraph | Narrator+Ctrl+O | | "Funding was released in May. Delivery is expected before the end of the year." | | not measured |
| A8 | Select a word with the mouse (double click is not implemented; drag across a word) | mouse | | Narrator announces the selected text (this tests the TextSelectionChanged event) | | not measured |
| A9 | Hover the mouse over a word | mouse | | Narrator reads that word (RangeFromPoint) | | not measured |

## Pass B: untagged two column (`b-untagged-two-column.pdf`)

Expected order: title, then the whole left column, then the whole right column. If Narrator reads the first left line and then
the first right line, the order is wrong (that is the content-stream order).

| # | Action | Expected speech | Heard | Result |
|---|---|---|---|---|
| B1 | Read all | "A Study of Reading Order in Two Column Pages. Reading order is easy when a page has one column, ..." and only after "...together." the right column: "The heuristic used here cuts the page into regions ..." | | not measured |
| B2 | Next line, x10 from the title | title, then left column lines 1..9 | | not measured |
| B3 | Next paragraph, x3 | left paragraph 1, left paragraph 2, right paragraph 1 | | not measured |
| B4 | Is "This document is not tagged" announced? | **Not implemented in the spike.** Master prompt 10.3 requires it once per document. Record as a gap. | | not measured |

## Pass C: rotated page with offset CropBox (`c-rotate90-cropbox.pdf`)

Run once at `--rot 0` and once each at 1, 2, 3 (or press Rotate). Reading must not change; only the rectangles move.

| # | Action | Expected | Heard | Result |
|---|---|---|---|---|
| C1 | Read all at each rotation | "TOP LEFT. Rotated page with an offset crop box. The page dictionary carries Rotate 90 and a CropBox whose origin is not at zero. Reading it correctly needs the transform. BOTTOM RIGHT" and never "Hidden line outside the crop box." | | not measured |
| C2 | Narrator cursor highlight on "TOP LEFT" at each rotation | Highlight sits on the glyphs at the visual top-left (rotation 0), top-right (90), bottom-right (180), bottom-left (270) | | not measured |
| C3 | Next word x5 | Words in the order above | | not measured |

## Pass D: ligatures and hyphenation (`d-ligatures-hyphenation.pdf`)

| # | Action | Expected | Heard | Result |
|---|---|---|---|---|
| D1 | Read all | "The official offline workflow is a well-known case of finding flaws in the data. This sentence is a hyphenated example of line breaking, and so is a second one that wraps again." Words "official", "offline", "workflow", "finding", "flaws" are spoken whole, not as "of fi cial" | | not measured |
| D2 | Next line x3 at the second paragraph | "This sentence is a hyphenated exam", "ple of line breaking, and so is a sec", "ond one that wraps again." (the hyphen is deliberately not in the text, so a line ends mid-word) | | not measured |
| D3 | Next word across the line break | "example" read as one word, highlight covers "exam-" on line 1 and "ple" on line 2 | | not measured |
| D4 | Next character through "official" | "o", "f", "f", "i", "c", "i", "a", "l" (ligature expanded to f, i) | | not measured |

## NVDA (free, not installed here)

NVDA reads UIA Text pattern content through its UIA text-range support. Repeat Passes A to D with NVDA (browse mode is not
involved: use NVDA+Down arrow "say all", Ctrl+Left/Right for words, Up/Down for lines, Left/Right for characters). Also check that
NVDA's review cursor (numpad) follows. Record in the same tables, columns "NVDA heard".

Result: **not measured (manual pass pending)**.

## Accessibility Insights for Windows FastPass (not installed here)

1. Install Accessibility Insights for Windows (free), point it at the running `PageA11y` window, run FastPass (automated checks
   plus tab stops).
2. Spike pass criterion (spike plan S7): zero failures on the spike window. Expected weak points to look at first: the toolbar controls
   use the base WinUI styles because `XamlControlsResources` could not be loaded in this code-only app (contrast and focus visuals may
   differ from the real app), and the page element has no children (by design).
3. In "Live inspect", select the page element and check the Patterns list shows Text (and TextPattern2), and that clicking
   "Text range" shows `GetText` / bounding rectangles that match the overlay.

Result: **not measured (manual pass pending)**.

## What to report back

Fill the "Heard" and "Result" columns, note any key that differed, and say for each pass whether the Narrator cursor highlight sat
on the glyphs. Anything that differs from "Expected" is a finding for ADR-0006 and for the `Quire.Accessibility` design.
