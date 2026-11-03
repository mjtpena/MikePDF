# 11 Print

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenario 2 (print pages 3 to 7 double-sided) and the print test matrix of M3
- Spec: master prompt §2.1 (print with the Windows print dialog, ranges, scaling, duplex honoured), §8.10. ADR-0011 (printing approach) **has not been written**; this design depends on it
- Milestone: M3. Nothing here exists in the app yet **[repo: STATUS "No ... printing"]**.

## 1. Purpose

Print the right pages, at the right size, on the right side of the paper, in as few steps as possible, with a preview that is honest about what comes out.

## 2. The decision this design needs (Q1)

§8.10 allows two routes, to be decided in ADR-0011:

| Route | What the user sees | For | Against |
|---|---|---|---|
| **A. System dialog only** | The Windows print dialog, possibly with a few app-added options | Looks and behaves like every other Windows app; printer preferences and paper handling are the system's | App options can't express page ranges beyond what the system offers, odd and even, annotations on or off, or "print as image". Little control over the preview |
| **B. MikePDF print sheet, with a link to the system's printer preferences** | One sheet with preview and every option the design needs. **Printer preferences...** opens the driver's own dialog for tray, duplex defaults, colour and quality | Preview we control; every §8.10 option present; fewer steps | We own more UI; a printer-specific option exists in the driver's dialog only |

**This spec recommends B**, and describes it below. If ADR-0011 chooses A, section 3's option list still applies: it becomes the list of options to add to the system dialog (where the system dialog can host them) and the rest move to a **Print options...** dialog opened from the app before the system dialog. The checks in section 6 are the same either way.

## 3. Layout

### 3.1 Print sheet (a large `ContentDialog`, 960 x 640 epx max, scrolls inside)

```text
+--------------------------------------------------------------------------------------------+
| Print                                                                                  [x] |
+-------------------------------------------------+------------------------------------------+
|                                                 |  Printer                                  |
|           +--------------------------+          |  [ Microsoft Print to PDF            v ]  |
|           |                          |          |  [Printer preferences...]                 |
|           |                          |          |                                           |
|           |      page 3 preview      |          |  Copies [ 1 ]   [x] Collate               |
|           |                          |          |                                           |
|           |                          |          |  Pages                                    |
|           +--------------------------+          |   (o) All pages (40)                      |
|              [<]  Page 1 of 5  [>]              |   ( ) Current page (12)                   |
|              Printing 5 pages on 5 sheets,       |   ( ) Selected pages (none)               |
|              both sides: 3 sheets                |   ( ) Pages [ 3-7           ]             |
|                                                 |       Print: [ All pages in range v ]     |
|                                                 |                                           |
|                                                 |  Sides  [ Two-sided, long edge        v ]  |
|                                                 |  Layout [ Auto orientation            v ]  |
|                                                 |  Scale  (o) Fit  ( ) Actual size ( ) [100]%|
|                                                 |  Paper  [ A4                          v ]  |
|                                                 |  Colour [ Colour                      v ]  |
|                                                 |                                           |
|                                                 |  Include                                  |
|                                                 |   [x] Comments and drawings               |
|                                                 |   [x] Form field contents                 |
|                                                 |   [ ] Print as image (for problem printers)|
|                                                 |                                           |
+-------------------------------------------------+------------------------------------------+
|                                                          [ Print ]        [ Cancel ]         |
+--------------------------------------------------------------------------------------------+
```

The right column is a **single scrolling form** with 4 epx between rows and `Space16` between groups. `Print` is the accent button and the default. The preview shows the page as the printer will draw it: margins, scaling, annotations on or off, orientation.

### 3.2 While printing

```text
    +--------------------------------------------------------+
    | (i) Printing "Contract v3" to Microsoft Print to PDF   |  InfoBar in the tab (overlay)
    |     Page 3 of 5   [==========-------]        [Cancel]  |
    +--------------------------------------------------------+
```

### 3.3 Narrow (XS and S): the preview hides behind a toggle

```text
+-----------------------------------+
| Print                        [x]  |
| [ Show preview ]                  |
| Printer [ ...              v ]    |
| Pages ...                         |
| ...                               |
| [ Print ]            [ Cancel ]   |
+-----------------------------------+
```

## 4. Behaviour

1. **Open:** `Ctrl+P` or the **Print** button. Disabled with the reason when the document's owner doesn't allow printing (§6.4 `PermissionDenied`) or no document is open. Opens within 300 ms (the printer list loads in the background).
2. **Initial values.**
   - Printer: the last used printer; if it's gone, the system default; **Microsoft Print to PDF** is always available on Windows 11 and is the fallback.
   - Pages: **Current page** if the user got to the dialog with a selection of thumbnails ("Selected pages"), else **All pages**. Typing in the range box selects the range option.
   - **Document hints** are honoured: `/ViewerPreferences /PrintScaling /None` preselects **Actual size**, `/Duplex` preselects the matching sides option, `/NumCopies` sets copies (up to a cap of 99), `/PrintPageRange` preselects the range **[design: each hint verified in the print feature spec]**. The user can change all of them.
   - Scale: **Fit to printable area** unless hinted.
3. **Pages.** Ranges accept `3-7`, `1,3,5-8`, `12-` and `-4`. Out of range pages are flagged inline ("The document has 40 pages."). **Print:** All pages in range, **Odd pages**, **Even pages**. A summary under the preview says what will happen: "Printing 5 pages on 3 sheets, both sides."
4. **Sides.** One-sided, Two-sided long edge, Two-sided short edge. Only the options the printer supports are enabled (read from the driver's capabilities). If the printer does not support duplex, the option shows "This printer can't print on both sides" (disabled, with that text). It is **honoured** (§2.1): the chosen value reaches the print job.
5. **Layout (orientation):** Auto (rotate to fit each page, per page), Portrait, Landscape.
6. **Scale:** Fit, Actual size, Custom (10 to 400%). Preview updates.
7. **Paper and colour** are read from the printer's capabilities. **Printer preferences...** opens the driver's dialog for everything else (tray, quality, finishing). Values changed there are read back into this sheet.
8. **Include:** comments and drawings (default on), form field contents (default on), **Print as image** (default off). Print as image renders each page to a bitmap at the printer's resolution and sends the image, for printers that mishandle fonts or transparency. When the document allows only **low-resolution printing** (a permission bit), the sheet says "The document's owner only allows low-quality printing." and forces **Print as image** at 150 dpi with the box disabled.
9. **Preview.** Rendered by the engine (`RenderTile` at preview resolution). It updates 250 ms after an option change (never blocking the controls), shows the first page of the selection, and lets the user step through the selected pages with `Left` and `Right` buttons or `PageUp` and `PageDown`. A slow page shows its low-resolution render first. The preview never uses the printer driver.
10. **Print.** `Enter` on the sheet or **Print**. The sheet closes. An `InfoBar` (3.2) shows progress with **Cancel**. Rendering happens **at printer resolution, in bands**, so memory stays capped (§8.10); each band is rendered by the engine and handed to the print path. The UI never decodes page content.
11. **Cancel** stops rendering and deletes the print job. Pages already sent may print; the InfoBar says "Stopped after page 3."
12. **Done.** The InfoBar says "Sent to {printer}." for 4 s, then goes. The print dialog's last settings (printer, scale, include options) are remembered per user, not per document.
13. **Security and privacy.** The document's content is rendered in the engine and handed to the broker as bitmaps or vector data through the **broker-owned print path**; the engine has no spooler access and no network. MikePDF doesn't add headers, footers, watermarks or "printed by" text to the page.
14. **Print to PDF** is a normal printer here. (A "Save as PDF" command that saves a copy is `file.saveAs`.)
15. The first Print starts the printer discovery. **No print dialog opens at app start.**

## 5. States

| State | Behaviour |
|---|---|
| **Loading** | The sheet opens at once; the printer ComboBox shows "Looking for printers..." with a ring until the list arrives (usually under a second; a network printer can take longer: it fills in as it answers). The preview shows a page-shaped placeholder and a small ring until the first render; **Print** is enabled as soon as a printer is selected, so a user who knows what they want can print without waiting for the preview |
| **Empty** | No printers other than "Microsoft Print to PDF": it is the only entry. No printers at all (the print service is off, or the policy removed them): "No printers were found. You can add one in Windows Settings." with an **Open printer settings** button (`ms-settings:printers`) **[guess: verify the URI]**. A document of one blank page: prints normally |
| **Error** | Printer offline or not accepting the job: after Print, an `InfoBar` (Error) says "The printer didn't accept the job. Check that it is on and connected, then try again." with **Try again** and **Choose another printer**. A page that can't be rendered: the job continues with that page blank and the final message says "Page 7 couldn't be printed" (the user can then reprint it); the sheet suggests **Print as image** after a failure. A permission refusal: the sheet doesn't open, with the reason. Unwritable PDF output: the system's own save dialog reports it |
| **Keyboard focus** | Initial focus: the **Printer** drop-down. Tab order: Printer, Printer preferences, Copies, Collate, Pages options (arrow keys within), range box, Print (subset), Sides, Layout, Scale, Paper, Colour, Include, Print as image, preview pager, **Print**, Cancel. `Enter` activates Print from anywhere except inside a multi-line field or an open drop-down; `Esc` closes. Access keys: `Alt+P` printer, `Alt+C` copies, `Alt+G` pages **[design]** |
| **High contrast** | System colours; the preview keeps the page's own colours on a `Window` surround with a 1 epx `WindowText` edge; the margin guides use `WindowText` dashes; selected radio uses `Highlight` |
| **225% text scale** | The right column grows; the sheet scrolls vertically (buttons pinned). The preview column narrows to a minimum of 240 epx then moves **above** the options. Labels wrap |
| **Narrow window** | Wireframe 3.3: a single column; the preview is behind **Show preview** |
| **Touch** | Controls 40 x 40 or larger (drop-downs 44 epx high); the range box raises the on-screen keyboard with a numeric layout where it can; the preview pager buttons are 44 x 44; swipe on the preview turns pages |

## 6. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Print sheet | Dialog | "Print" | `PrintDialog` |
| Printer | ComboBox | "Printer" | `PrintPrinter` |
| Printer preferences | Button | "Printer preferences" | `PrintPreferences` |
| Copies, Collate | Spinner (NumberBox), CheckBox | "Copies", "Collate" | `PrintCopies`, `PrintCollate` |
| Pages options | RadioButton group | "Pages: All pages", "Current page", "Selected pages", "Custom range" | `PrintPagesAll`, `PrintPagesCurrent`, `PrintPagesSelected`, `PrintPagesRange` |
| Range box | Edit | "Page range, for example 3-7" | `PrintRange` |
| Odd or even | ComboBox | "Print" | `PrintSubset` |
| Sides, Layout, Scale, Paper, Colour | ComboBox, RadioButton, NumberBox | "Sides", "Orientation", "Scale", "Paper size", "Colour" | `PrintSides`, `PrintOrientation`, `PrintScale`, `PrintPaper`, `PrintColour` |
| Include | CheckBox | "Include comments and drawings", "Include form field contents", "Print as image" | `PrintInclude*` |
| Preview | Image (with a description) | "Preview of page 3. Portrait, A4, scaled to fit." | `PrintPreview` |
| Pager | Button | "Previous page in preview", "Next page in preview" | `PrintPrev`, `PrintNext` |
| Summary | Text (live, polite) | "Printing 5 pages on 3 sheets, both sides" | `PrintSummary` |
| Progress | InfoBar (live, polite) | "Printing {name}, page 3 of 5" | `PrintProgress` |

Narrator, intended:

| Action | Announcement |
|---|---|
| Open | "Print dialog. Printer, Microsoft Print to PDF, combo box." |
| Range typed | "Printing 5 pages on 3 sheets, both sides." (polite, after a 500 ms pause) |
| Invalid range | Assertive: "That range isn't valid. The document has 40 pages." |
| Print | "Printing Contract v3, page 3 of 5" at most every 2 pages; "Sent to Microsoft Print to PDF" |
| Error | Assertive: "The printer didn't accept the job." |

The preview has a text description so that a screen-reader user knows scale and orientation without seeing it.

## 7. Keyboard path (scenario 2: pages 3 to 7, double-sided)

| Step | Keys | Count |
|---|---|---|
| Open | `Ctrl+P` | 1 |
| Enter the range | `Tab` to the range box (or click it), type `3-7` | 2 |
| Sides | `Tab` to Sides, `Down` to "Two-sided, long edge" | 2 |
| Print | `Enter` | 1 |

About **6 actions** and no mouse needed. See [ux-metrics](../ux-metrics.md#2-task-scripts-click-counts-and-timings).

## 8. Engine and protocol implications

| # | Need |
|---|---|
| E10 | `RenderBand`: page, DPI (up to the printer's), band rectangle, flags (annotations, form fields, grayscale, image mode), cancellable, with the broker providing a bounded buffer. Memory: band height chosen so a band stays under a cap (for example 64 MB) |
| | **Vector or raster output:** the print path may send vector content to the spooler (best quality and size) or bitmaps (simple and safe). Which one is ADR-0011's decision. The engine currently only returns bitmaps (`RenderTile`) |
| | **Printer capabilities** (duplex, paper, colour, resolution) are read by the broker via Windows print APIs. That is not PDF parsing, so it's allowed in the UI process |
| | **`ViewerPreferences`** hints come from `GetDocumentInfo` (E2) |
| | Test matrix (§8.10): Microsoft Print to PDF, an XPS printer, one physical PCL printer and one PostScript printer |

## 9. OPEN QUESTIONS

1. **Q1 (ADR-0011):** route A or B above. The recommendation is B.
2. Is **Pages per sheet** (n-up) in v1.0? `fpdf_ppo` has n-up support, but §2.1 doesn't list it. This spec leaves it out of the sheet. Add if the usability test asks.
3. Should the print dialog **remember per document** (e.g. last range)? This spec says no.
4. Whether **vector** or **raster** goes to the printer matters for output quality at 600 dpi. Out of scope for the design, in scope for the ADR.
5. `ms-settings:printers` is a guess; verify it opens the Printers & scanners page on the target Windows versions.

## 10. As built (W2-print-sheet, `m3/print-sheet`)

The sheet is `src/MikePDF.App/Views/PrintSheetView.xaml` in a `ContentDialog` (`MainWindow.Print.cs`), the rules are in `MikePDF.Core.Printing.PrintSheetModel`, and the preview is `MikePDF.Printing.PrintPreview`. Where the build differs from sections 3 to 7, and why:

| Design | Built | Why |
|---|---|---|
| Scale as radio buttons plus a percent box | A `Scale` drop-down (fit, shrink large pages only, actual size, custom) with the percent box beside it when custom is chosen | The spec (print.md 3.2) has four modes, not three; four radios and a box are tall in a column that already scrolls at 225% text |
| **Selected pages** radio | Not offered | The sidebar's thumbnail list is single-select until the page organiser (M4); no placeholder UI for an unbuilt feature (decisions.md Q16) |
| Low-resolution permission forces **Print as image** at 150 dpi | The notice "The document's owner allows low-quality printing only." and a 150 dpi cap in the pipeline; Print as image stays the person's choice | print.md 3.7 says the cap and a notice; it does not force the image mode. The spec wins |
| Narrator announces progress "at most every 2 pages" | The count is a polite live region that changes once per sheet, and once at the end (print.md 5, PRINT-21) | The spec wins |
| **Pages per sheet** (design open question 2) | 1, 2, 4, 6, 9 and 16, with the page order (across then down, or down then across) | decisions.md, "After the print and shell reviews" |
| Tab order ends with the preview pager | The options come first in the XAML, so the order is Printer, preferences, copies, collate, pages, range, subset, sides, orientation, rotate, scale, pages per sheet, order, paper, colour, include, the pager, then Print and Cancel. In the stacked layout (narrow window or 225% text) the preview is visually above the options but its pager still comes after them in the tab order | A XAML tree's tab order is its document order |
| Access keys `Alt+P`, `Alt+C`, `Alt+G` | Set on the printer, copies and pages controls (`AccessKey`) | as designed |
| Preview "updates 250 ms after an option change" | 250 ms debounce for the picture and the notices; the summary and a range error follow 500 ms after the last key, because they are live regions | design 6 (Narrator table) |
| Progress as an `InfoBar` in the tab | An `InfoBar` over the document area of the window (not tied to one tab), with a `ProgressBar` named "Printing {document}", a live status line and **Cancel** (`Esc` while it has focus). One job at a time per window; closing the window mid-job asks first | A job outlives the tab it was started from |

Open question 5 (`ms-settings:printers`): the button starts the address through the shell; whether it lands on Printers & scanners on every Windows version is not verified here.
