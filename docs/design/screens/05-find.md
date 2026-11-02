# 05 Find

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 1 (search "contingent liabilities" in a 600-page report), 6 (screen-reader user searches), 10 (5,000-page file)
- Spec: master prompt §8.1. Budgets: first result at most 300 ms, full results on a 500-page text PDF at most 3 s (§11)
- Replaces: the floating box in `FindBar` with one option (match case) in [MainWindow.xaml](../../../src/MikePDF.App/MainWindow.xaml) **[repo]**, and builds on `MainWindow.Find.cs` behaviour

## 1. Purpose

Find the thing, see where it is, and move through every occurrence without losing the place or blocking the UI. Two parts: a **Find bar** over the page, and a **Results** list in the left pane.

## 2. Layout

### 2.1 Find bar (overlay at the top right of the page well)

```text
                                              +----------------------------------------------------+
                                              | [ contingent liabilities        x ] 3 of 47+   [^][v]|
                                              | [Aa] [Ab|] [.*] [a-a] [Results]               [ x ] |
                                              +----------------------------------------------------+
                                                 case  whole regex  accents  list in pane       close
```

Two rows at L and M widths, 8 epx padding, 360 epx wide (max 480; at S and XS it's full width minus 16 epx):

| Row | Controls |
|---|---|
| 1 | Find box (placeholder "Find in document"), clear `x`, match count, previous, next |
| 2 | Toggle buttons: **Match case** `Aa`, **Whole word**, **Regular expression** `.*`, **Ignore accents** (diacritic-insensitive), **Show all results** (opens the list in the left pane), and **Close** |

The current bar is one row with only Match case **[repo]**. The design adds the options §8.1 requires in a second row, which the bar needs only while it's open.

### 2.2 Results in the left pane

```text
 +--------------------------------+
 | [thm][out][ann][att][SRCH]     |   Search results (this tab appears after a search)
 | -------------------------------|
 |  Search results                |   Subtitle
 |  "contingent liabilities"      |
 |  47 matches on 19 pages        |   Caption, polite live region
 |  [========------] Searching 62%|   determinate ProgressBar until done
 |                                |
 |  Page 41                       |   group header (sticky)
 |   ...note 12 on **contingent   |   snippet: 80 characters of context, match in semibold
 |   liabilities** is disclosed...|
 |   ...no **contingent           |
 |   liabilities** beyond those...|
 |  Page 44                       |
 |   ...**Contingent liabilities**|
 |   Note 18 ...                  |
 +--------------------------------+
```

### 2.3 The page, with hits and the scroll rail

```text
+------------------------------------------------------------------+---+
|   ... the company's [contingent liabilities] are described in  ...|   | rail ticks
|                      ^^^^^^^^^^^^^^^^^^^^^^^ current: orange fill |-- |  <- tick for page 41
|   and outline; other hits: yellow, multiply                       |   |
+------------------------------------------------------------------+---+
```

## 3. Behaviour

1. **Open:** `Ctrl+F` or the **Find** button. If text is selected on the page, the box is **pre-filled** with it (single line, up to 128 characters, matching the `MaxLength` of the current box **[repo]**). Otherwise, if the bar was used earlier in this tab, it shows the last query selected, so typing replaces it. Focus goes to the box.
2. **Incremental search.** Typing starts a search after a 120 ms pause, starting from the **current page** and wrapping at the end. The **first hit** is found and scrolled to without waiting for the rest (budget 300 ms). Results stream in as pages are processed, in page order from the start page. A new keystroke cancels the previous search within one page.
3. **Count.** "3 of 47+" while searching ("so far" strings exist: `Find_CountMore` **[repo]**), "3 of 47" when complete, "No matches" when none. The count is a **polite live region** that is updated at most once a second while streaming, so Narrator does not talk constantly.
4. **Move.** `Enter` or `F3` next, `Shift+Enter` or `Shift+F3` previous. Both wrap, with a one-time "Reached the end, continuing from the start" hint in the status text, not a dialog. `F3` and `Shift+F3` keep working after the bar is closed (they re-show the highlights for 4 s and jump).
5. **Hits.** All hits on visible pages are marked. The current hit has a distinct fill **and** an outline ([README 4.6](../README.md#46-colour-roles)); colour is never the only cue.
6. **Options** apply immediately and re-run the search:
   - **Match case** (`Alt+C`)
   - **Whole word** (`Alt+W`)
   - **Regular expression** (`Alt+R`): syntax is .NET-style **[design: the exact flavour is a protocol decision; PDFium text search has no regex, so the engine or broker implements it. OPEN QUESTION]**. An invalid pattern shows the box with a red 2 epx outline, an error glyph, and the text "That isn't a valid pattern" in the status line. It does not run and does not clear the previous hits. The message is `Find_Refused` today ("That can't be searched for" **[repo]**), reworded for regex.
   - **Ignore accents** (`Alt+A`): diacritic-insensitive (§8.1).
   - Normalisation, ligatures and line-end hyphenation are handled by the engine (§8.1; fixtures to add).
   - Options persist per user, not per tab.
7. **Results list.** **Show all results** (`Ctrl+Shift+F`, or the button) opens the **Search results** pane in the left pane. The list groups hits by page with an 80-character snippet with the match in semibold. Selecting a row scrolls the page to that hit and makes it the current hit **without moving keyboard focus out of the list** (so the arrow keys can walk the list). `Enter` moves focus to the page at that hit. The pane persists after `Esc`, until the user starts a new search or closes the pane.
8. **Scroll rail ticks.** One tick per page with hits, in `Find.Hit` colour, plus a longer tick for the current one. Ticks are visual only (UIA raw).
9. **Cancel and close.** `Esc` closes the bar, **cancels any running search**, clears the page hits, and returns focus to the page at the current hit's position (so the reader keeps their place). The results list remains if it was opened.
10. **Large documents.** A 2,000-page document searches in the background while the user reads or scrolls (budget in §11 is for 500 pages: 3 s). The progress bar in the results pane and the count's "+" show that the search is still going. The user can navigate hits found so far.
11. **Scanned pages.** A page with no text is skipped. When a search finishes and some pages had no text, the status line says "12 pages have no searchable text" with a Details link that lists them. (OCR is P1 and, when it exists, offers a **Make searchable** action there.)
12. **Tabs.** Each tab keeps its own query and hits. Switching tabs hides the bar and restores it on return. (Search across all tabs is P2.)
13. **Security.** The query is never logged and never leaves the process. Regex input is length-capped (128 characters), compiled with a timeout in whatever process runs it, to avoid catastrophic patterns **[design; the engine or broker implementation must be fuzzed]**.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Searching: the count shows "so far", the results pane shows a determinate `ProgressBar` and the percent ("Searching, 62%", string `Find_SearchingPercent` **[repo]**). Before the first hit arrives, the status says "Searching..." |
| **Empty** | The box is empty: no count, **Next** and **Previous** disabled, no results pane content ("Type to search this document."). No matches: "No matches" with a hint line, "Try fewer words, or turn off Whole word" when whole word, case or regex is on |
| **Error** | Invalid regex (above). **Refused** query (too long, only whitespace): "That can't be searched for" **[repo]**. Search engine problem: the bar shows "Search stopped. Try again" with a **Retry** button, and the engine banner appears if the engine restarted. Hits already found stay |
| **Keyboard focus** | Focus order in the bar: find box, clear, previous, next, then the four toggles, **Show all results**, **Close**. Arrow keys move among the toggles. A visible focus ring everywhere. The find box has the standard text-box focus visual. After `Esc`, focus goes to the page |
| **High contrast** | The bar uses `Window` and a 1 epx `WindowText` edge. Toggled-on options use `Highlight`. Hits on the page: 2 epx `Highlight` outline; current hit: 3 epx outline plus fill |
| **225% text scale** | The bar becomes **three rows** of controls if needed; the find box keeps a minimum width of 200 epx; the count wraps below the box. Toggles are icon-only, so they stay 40 x 40. The results pane rows wrap snippets onto up to 4 lines |
| **Narrow window** | At S and XS the bar spans the page well's width minus 16 epx and takes a third row. Results are an overlay pane. The bar never covers the focused hit: if the current hit would sit under the bar, the page well scrolls it into view with the bar's height as margin |
| **Touch** | Bar buttons are 44 x 44. The count is read with the **Next** button's tooltip. The on-screen keyboard raises the bar with it (the bar sits above the input pane) |

## 5. Accessibility

| Element | Type | Name | AutomationId |
|---|---|---|---|
| Find bar | Group | "Find" | `FindBar` **[repo]** |
| Find box | Edit | "Find in document" **[repo]** | `FindBox` **[repo]** |
| Count | Text (live, polite) | "3 of 47 matches", or "Searching, 62 percent, 12 matches so far" | `FindStatus` **[repo]** |
| Previous / next | Button | "Previous match" / "Next match" **[repo]** | `FindPreviousButton`, `FindNextButton` **[repo]** |
| Match case | Button (toggle) | "Match case" **[repo]** | `FindMatchCaseToggle` **[repo]** |
| Whole word | Button (toggle) | "Whole word" | `FindWholeWordToggle` |
| Regular expression | Button (toggle) | "Regular expression" | `FindRegexToggle` |
| Ignore accents | Button (toggle) | "Ignore accents" | `FindAccentsToggle` |
| Show all results | Button | "Show all results" | `FindResultsButton` |
| Close | Button | "Close find" **[repo]** | `FindCloseButton` **[repo]** |
| Results pane | List | "Search results" | `SearchResultsList` |
| Group header | Header | "Page 41, 3 matches" | `SearchGroup_{page}` |
| Result row | ListItem | "Page 41. {snippet with the match marked}: 'note 12 on contingent liabilities is disclosed'" | `SearchHit_{n}` |

Narrator, intended:

| Action | Announcement |
|---|---|
| `Ctrl+F` | "Find in document, edit" (with the pre-filled text: "contingent liabilities, selected") |
| Typing (after the pause) | "Searching" once; then "3 of 47 matches" when the first hit is shown (polite) |
| `Enter` | "4 of 47. Page 41." and the hit's line is read from the page (the Text pattern's selection moves to the hit so Narrator reads the sentence) |
| Complete | "47 matches on 19 pages" (polite, once) |
| Wrap | "Reached the end of the document, continuing from the start" |
| No match | "No matches" |
| Invalid regex | Assertive: "That isn't a valid pattern" |

**Hit selection and the Text pattern.** The current hit is also the Text pattern's **selection** (a text range), so screen readers read the hit in its context. This depends on S7's mapping between ranges and character indices.

## 6. Keyboard path

| Task | Keys |
|---|---|
| Find | `Ctrl+F` |
| Next / previous | `Enter` or `F3` / `Shift+Enter` or `Shift+F3` |
| Toggle case, whole word, regex, accents | `Alt+C`, `Alt+W`, `Alt+R`, `Alt+A` (when the bar has focus) |
| List all results in the pane | `Ctrl+Shift+F` |
| Walk the list | Up and Down; `Enter` jumps to the page; `Esc` returns to the find box |
| Close | `Esc` |
| The accountant's flow (scenario 1) | `Ctrl+F`, type "contingent liabilities", `Enter` to step, `Ctrl+H` to highlight the hit |

The scenario 1 flow shows the intended **find, then mark** link: when a hit is current, `Ctrl+H` highlights it (the hit acts as the selection). That needs the hit to also be the selection (see section 5).

## 7. Engine and protocol implications

- **E8:** `Search` must support: match case, whole word, **regex**, ignore accents; stream hits per page with rectangles (as quads for multi-line hits) and a **context snippet** (about 80 characters before and after, with the match offset); and cancel within one page.
- PDFium's text search (`fpdf_text.h`) has no regex and no whole-word-with-diacritics mode that matches this spec **[unverified: check the pinned header]**. Regex and diacritic folding probably need to be done over the text the engine already extracts for each page (page text plus char boxes). Doing that in the engine keeps parsing out of the UI process; the broker must **not** run regexes over text that came from a compromised engine without limits (use a timeout and length limits).
- The broker validates the hits (counts, rectangle bounds) as untrusted, as with every response (§5.4).

## 8. OPEN QUESTIONS

1. **Regex flavour and where it runs** (engine or broker). Needs an ADR note.
2. Should **Esc** keep the page highlights visible? This spec clears them. Some users want them to stay until they click. Test.
3. Should Find auto-fill from the selection? This spec says yes.
4. Search across all open documents is P2 in the spec (§2.3 workspace). The bar has no room for a scope selector in v1.0 by design.
5. Is a 128-character limit right for regexes? It is today's `MaxLength` **[repo]**.
