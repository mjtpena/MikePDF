# Competitive teardown

- Date: 2026-11-02 (all web sources accessed on this date)
- Author: Sonnet (W1-design)
- Reviewed by: not yet reviewed
- Purpose: find the specific places where a Windows-native reader can be clearly better, and turn each into a measurable target (last section)

## How to read this document

**This is desk research, not a hands-on evaluation.** No competitor was installed or run for this document (other agents share the machine, and the work package is docs-only). Every claim carries one of these tags (same legend as the [design README](README.md#0-how-to-read-this-document)):

| Tag | Meaning |
|---|---|
| **[src]** | Paraphrased from a cited page. The URL is in the source list at the end |
| **[user report]** | From a forum or community post. A person said it. Not a vendor statement, and not verified |
| **[unverified]** | General knowledge of the product that this document did not re-check |
| **[guess]** | An estimate. Replace with a measurement |
| **not measured** | A number nobody has taken. The protocol to take it is in [ux-metrics.md](ux-metrics.md#4-competitor-measurement-protocol) |

Competitor products change often. Versions or dates that were visible are given. Where the information is old, it says so. Where a search snippet was the only evidence, it is called weak.

**Versions seen:**

| Product | Version or date seen | Source quality |
|---|---|---|
| SumatraPDF | 3.7 pre-release notes (a build dated 2026-08-21 is quoted); 3.6 is the previous release | Official docs **[src]** |
| Microsoft Edge PDF viewer | Docs page updated 2026-06-15; feature table lists versions from Edge 79 to 91 | Official docs **[src]** |
| Adobe Acrobat Reader (Windows) | No version confirmed. Evidence is community posts and an enterprise announcement | Weak |
| Foxit PDF Reader | 2026.1.1.36485 dated 2026-04-27, per Wikipedia | Weak (secondary) |
| PDF-XChange Editor | "11.0.1.0" from a search snippet | Weak |
| Okular | Home page read; no version | Official **[src]** but thin |
| macOS Preview | Apple guide index only | Official **[src]** but thin |

---

## 1. Comparison at a glance

| Dimension | Acrobat Reader | Edge | SumatraPDF | Foxit Reader | PDF-XChange Editor | Okular (ref.) | Preview (ref.) |
|---|---|---|---|---|---|---|---|
| Native Windows look | Custom UI (Adobe's own, with a "New Acrobat" mode) [user report] | Edge's own toolbar, Fluent-ish | Custom Win32, themes | Custom, ribbon-style [unverified] | Custom, classic toolbars [unverified] | KDE (Qt) | macOS native |
| Tabs | Yes [unverified] | Browser tabs (a PDF is a web tab) | Yes, with a smart-switch list [src] | Yes [unverified] | Yes [unverified] | Yes | Windows (no tabs by default) [unverified] |
| Annotation: select, then mark | Needs the Comment tool, or a context menu [user report] | Right-click, Highlights, colour; or toolbar Highlight [src] | One key: `a` (highlight), `u` (underline) [src] | [unverified] | [unverified] | Review toolbar [src: thin] | Markup toolbar [src: thin] |
| Pen and ink | Draw tools [unverified] | Draw button enters ink mode [src] | Ink in the 3.7 Edit PDF mode [src] | [unverified] | [unverified] | Freehand line [unverified] | Sketch/draw [unverified] |
| Forms | Full (also XFA, scripts) [unverified] | Basic. JavaScript forms and XFA not supported [src] | Fillable fields highlighted pale blue; Sign Document [src] | [unverified] | Plus edition needed to create fillable forms [src weak] | [not stated on the home page] | Fill forms [src] |
| Page organiser | Paid tier [unverified] | Not stated in the page read | Not documented [guess: none] | Paid tier [unverified] | Feature set largely free [guess] | n/a | Sidebar thumbnails reorder [unverified] |
| Command palette | No [unverified] | No | **Yes, Ctrl+K, with prefix modes** [src] | No [unverified] | No [unverified] | No | No |
| Accessibility | Strong history [unverified] | Keyboard, high contrast follows OS, screen reader support [src] | UI Automation provider is **experimental** [src] | [unverified] | [unverified] | Qt a11y [unverified] | VoiceOver [unverified] |
| Upsell and friction | Sign-in prompts; first-run tour; paid AI add-on [user report] | None for PDF [unverified] | None [unverified] | Upgrade prompts [src weak] | Trial watermark on some features [src] | None | None |
| Price | Free Reader; paid tiers | Free | Free | Free Reader; paid editor | Free tier (>70% of features, per vendor) [src]; paid licence | Free | Included with macOS |

---

## 2. Competitor by competitor

### 2.1 Adobe Acrobat Reader (Windows)

**Evidence quality: weak.** Adobe's help pages returned errors to the fetch tool. What follows is from search snippets and community posts.

| Dimension | Finding |
|---|---|
| First run and open | A "Welcome to Acrobat Reader" screen with a **Start Tour** button appears on first launch, and administrators ask how to suppress it even after applying policies **[user report: [community thread](https://community.adobe.com/t5/acrobat-reader-discussions/disable-the-quot-welcome-to-acrobat-reader-quot-quot-start-tour-quot-on-first-launch/m-p/13887045)]**. Users report repeated **sign-in prompts** when opening PDFs **[user report: [community thread](https://community.adobe.com/t5/acrobat-reader-discussions/sign-in-prompt-when-opening-pdf-file/m-p/14771620/highlight/true)]**. If the user also has paid Acrobat installed, PDFs open in Acrobat even when Reader is set as the default **[user report, same family of threads]** |
| Chrome footprint | Not measured. See [section 3](#3-chrome-footprint). Reader has a "New Acrobat" interface that users are being moved to, and a way to turn it off (View, Enable/Disable New Acrobat) **[user report: [thread](https://community.adobe.com/t5/acrobat-reader-discussions/change-menu-bar-and-toolbar-view-on-acrobat-reader-dc/m-p/14675827)]**. A right-hand tools pane is part of the classic layout **[unverified]** |
| Keyboard coverage | Single-key accelerators for tools exist but are **off by default**: Edit, Preferences, General, "Use single-key accelerators to access tools". With them on, `U` selects the highlighting tool and `Shift+U` cycles Highlight, Underline and Strikethrough **[src: [Adobe keyboard shortcuts](https://helpx.adobe.com/acrobat/using/keyboard-shortcuts.html), via search snippet; the page itself returned 403]**. A direct highlight-the-selection shortcut is not confirmed. Community answers describe the route "Menu key, then H" **[user report: [thread](https://community.adobe.com/t5/acrobat-reader-discussions/highlight-text-in-acrobat-reader-as-a-keyboard-only-user/m-p/14496531)]** |
| Accessibility | Long-standing accessibility features **[unverified]**. Not assessed here |
| Annotation flow | The free Reader has comment tools: comments, highlights, form text and signatures. Editing existing text and images needs a paid plan **[user report and third-party summaries; weak]** |
| Forms | The most complete form support among the set, including scripts and XFA **[unverified]** |
| Page organiser | Organise pages is a paid-tier feature **[unverified]** |
| Search | Find toolbar and advanced search **[unverified]** |
| Speed perception | **Not measured.** Commonly perceived as heavy at startup **[guess]** |
| Upsells and annoyances | The AI Assistant is described as an add-on subscription for Reader and Acrobat customers **[src: [Business Wire, 2024-05-08](https://www.businesswire.com/news/home/20240508717446/en/adobe-revolutionizes-workplace-productivity-with-launch-of-acrobat-ai-assistant-for-the-enterprise/)]**. Users report AI tools tied to the new interface, which appears or disappears with the interface toggle **[user report, 2026]**. (A Reader changelog on APKMirror that mentions a paid, "free for a limited time" AI feature is for the **Android** app, so it is not evidence about Windows.) |

**What to learn:** the free Reader's comment tool set is the baseline users expect (highlight, note, draw, stamp, sign). **What to avoid:** the first-run tour, sign-in on open, and the feeling of a sales funnel.

### 2.2 Microsoft Edge PDF viewer

**Evidence quality: good for features, old for interaction detail.**

| Dimension | Finding |
|---|---|
| First run and open | PDFs open in a browser tab. There's no separate app. Open local, online and embedded PDFs **[src: [Edge PDF docs](https://learn.microsoft.com/deployedge/microsoft-edge-pdf)]**. The user is already inside a browser with its tab strip and address bar |
| Chrome footprint | Browser tab strip, plus address and toolbar row, plus the PDF toolbar (a pin-able toolbar at the top of the PDF content **[src]**). Not measured. See [section 3](#3-chrome-footprint) |
| Keyboard coverage | **Caret browsing**: F7 asks to turn it on, then a blinking cursor moves through the PDF and Shift selects text, which lets users create highlights and follow links and form fields by keyboard **[src]**. Highlights can be made with a pen or the keyboard **[src]** |
| Accessibility | Keyboard accessibility, high-contrast mode (uses OS settings to render PDF content), and screen-reader support on Windows and macOS **[src]**. Microsoft points to its accessibility conformance reports for unsupported scenarios **[src]** |
| Annotation flow | **Ink:** click Draw, then use pen, touch or mouse; colour and thickness are customisable; there's an Erase tool **[src]**. **Highlight:** select text, right-click, Highlights, choose a colour (three actions after selecting); also reachable from the toolbar **[src]**. **Text notes:** select text, right-click, Add Comment, type, tick **[src]**. A user reported that ink colour and thickness **revert for each new document** **[user report, [community post](https://techcommunity.microsoft.com/t5/articles/microsoft-edge-extends-tools-for-the-pdf-reader/m-p/1303243)]**. A Notes pane that lists highlights, text notes, ink notes and comments in page order was announced in the Microsoft 365 message centre; its date and rollout are not confirmed **[src weak: [message RM577542](https://mc.merill.net/message/RM577542)]** |
| Forms | Basic form filling. JavaScript forms are not supported. XFA is not supported **[src]** |
| Page organiser | Not mentioned in the Microsoft page read. Treat as absent until checked **[unverified]** |
| Search | Ctrl+F in the browser **[unverified]** |
| Table of contents, views | Table of contents pane since Edge 86; a two-page view is documented as available only in Dev and Canary channels in the doc version read (updated 2026-06-15, but that sentence may be stale) **[src]** |
| Other | Read aloud (since Edge 84), certificate-based signature viewing and validation, Microsoft Purview and IRM-protected PDFs **[src]**. Process isolation between PDFs from different sites **[src]** |
| Speed perception | **Not measured.** If Edge is already running, a PDF opens fast **[guess]** |
| Upsells and annoyances | No sales prompts for PDF **[unverified]**. The annoyance is structural: a PDF is a web tab, with no tear-out workspace for documents, no annotation list in the version read, and no page tools |

**What to learn:** text note on a selection and the pen-or-keyboard highlight. **What to avoid:** a Draw mode that must be entered before a pen can write, and per-document tool resets.

### 2.3 SumatraPDF

**Evidence quality: good** (official docs read).

| Dimension | Finding |
|---|---|
| First run and open | No account or wizard is described **[unverified]**. Open from Explorer, Ctrl+O, or drag a file **[src, shortcuts page]** |
| Chrome footprint | Toolbar, tab strip and the caption can be switched off: **F8** toolbar, **Shift+F8** caption (3.7+), **F9** menu **[src]**. Default sizes not measured |
| Keyboard coverage | **The broadest in the set.** Single-key navigation (`n`, `p`, `j`, `k`, `g`, `z`, `c`, `f`, `i`), view modes (`Ctrl+6` single, `Ctrl+7` facing, `Ctrl+8` book), fit modes (`Ctrl+0` to `Ctrl+3`), `Ctrl+Tab` with a smart tab switch, `Alt+1` to `Alt+9` for tabs, back and forward (`Alt+Left`, `Alt+Right`), presentation (`F5` or `Ctrl+L`), and a **command palette (`Ctrl+K`)** with prefix modes for commands, open tabs, history, favourites, table of contents, thumbnails, annotations and settings **[src: [shortcuts](https://www.sumatrapdfreader.org/docs/Keyboard-shortcuts), [version history](https://www.sumatrapdfreader.org/docs/Version-history)]**. `F7` starts keyboard text selection with a caret (3.7+) **[src]**. Shortcuts can be customised **[src]** |
| Accessibility | An **experimental** UI Automation provider is enabled for PDF, XPS and DjVu in release builds, exposing text by character, word, line or page to screen readers **[src, version history]** |
| Annotation flow | Since 3.3, `a` highlights the selection and `u` underlines it, with one key and no mode. `Delete` removes the annotation under the cursor. Copy, cut and paste annotations. `Ctrl+Z` and `Ctrl+Shift+Z` undo and redo (3.7) **[src]**. The 3.7 pre-release adds an **Edit PDF mode** with an annotation toolbar (highlight, ink, redaction, stamps), a highlighter mode, an ink thickness slider, a Find Annotation window, and signature-image stamps **[src]**. Saving annotations is `Ctrl+Shift+S` and `Ctrl+S` is Save As **[src]**, which is easy to get wrong |
| Forms | Empty fillable fields are highlighted in pale blue by default; signing offers approval or certifying signatures with a validity status **[src, 3.7 notes]** |
| Page organiser | Not documented in the pages read **[guess: none]** |
| Search | `Ctrl+F` or `/`, `F3`, `Shift+F3` **[src]** |
| Dark mode | Many built-in dark themes; `Shift+I` inverts page colours; document colours can follow the theme **[src]** |
| Speed perception | **Not measured.** The reference for "fast" in this segment **[guess]** |
| Upsells and annoyances | None known **[unverified]**. The annoyance is that its look is not a Windows 11 app's look, the shortcut set is large and unlabelled in the UI, and several features are in pre-release |

**What to learn:** one-key marking, the prefix-mode palette, keyboard text selection, and the **speed**, which is the bar. **What to avoid:** `Ctrl+S` meaning something unexpected, and an accessibility provider labelled experimental.

### 2.4 Foxit PDF Reader

**Evidence quality: weak** (Wikipedia and aggregator pages; Foxit's own pages were not reachable).

| Dimension | Finding |
|---|---|
| First run and open | **[unverified]** |
| Chrome footprint | A ribbon-style toolbar with tabs for tasks **[unverified: the search found no source confirming a ribbon]**. Not measured |
| Keyboard coverage | **[unverified]** |
| Accessibility | **[unverified]** |
| Annotation, forms, page organiser, search | **[unverified]** |
| Release | 2026.1.1.36485 on 2026-04-27 per Wikipedia **[src weak]**. The built-in print-to-PDF printer was dropped in May 2020 **[src weak]** |
| Upsells and annoyances | An aggregator page says the free version lacks OCR and has frequent upgrade prompts, and another article says the product has grown complex **[src weak: [Cuspera](https://www.cuspera.com/products/foxit-reader-x-9224/news)]**. Both are low reliability |

Nothing here is strong enough to design against. The table in section 4 therefore has fewer Foxit rows, and each carries a "verify first" condition.

### 2.5 PDF-XChange Editor

**Evidence quality: medium on licensing, weak elsewhere.**

| Dimension | Finding |
|---|---|
| Model | The free version is free for private and commercial use. More than 70% of features need no licence. The rest are marked and **add a "trial" watermark on output** when used without a licence, on save, export or optimise **[src: [PDF-XChange demo limits](https://www.pdf-xchange.com/product/pdf-xchange-editor/demo_limits) via search snippet; the page redirected during fetch]**. PCWorld's review lists the watermarks as a drawback **[src weak]** |
| Forms | A paid "Plus" edition includes creating and editing fillable forms **[src weak]** |
| Other dimensions | **[unverified]**: tabbed, many toolbars, a dense interface, customisable shortcuts |

**What to learn:** being generous with features sets the bar for a free tool. **What to avoid:** any feature that quietly watermarks output.

### 2.6 Okular (design reference only)

**[src: [okular.kde.org](https://okular.kde.org/), accessed 2026-11-02]** The home page lists annotations (inline and pop-up notes, highlight and underline, user text), digital signature viewing, verification and signing, and "multi-platform". It doesn't mention forms, presentation mode, or accessibility. A fuller page (the manual) returned 404. Design lesson, **[unverified]**: a review toolbar with a separate "pop-up note" and "inline note" is a model for MikePDF's note and free-text split.

### 2.7 macOS Preview (design reference only)

**[src: [Apple support, Preview guide index](https://support.apple.com/guide/preview/annotate-a-pdf-prvw35730/mac), accessed 2026-11-02]** Preview can highlight, underline and strike out text, add notes and speech bubbles, fill PDF forms and add a signature. The index page doesn't describe the toolbar. Design lesson, **[unverified]**: a markup toolbar that sits in the same row as the other commands, with tools that act on a text selection straight away, is what users on a Mac expect. MikePDF's contextual command bar follows the same idea.

---

## 3. Chrome footprint

"Chrome" is everything that isn't the page well: title bar, tabs, toolbars and ribbons, and docked panes. Vertical chrome is what hurts on a 768-px-tall laptop.

**Competitor figures below are guesses. They exist so the target has a reference. They must be replaced with measurements (protocol in [ux-metrics.md](ux-metrics.md#4-competitor-measurement-protocol)) before they are quoted anywhere outside this repository.**

Rows are in **physical pixels at 100% scale** for the first column, so the numbers are comparable across products. The next two columns convert MikePDF's epx to physical pixels at two common scale factors. 1366x768 laptops usually run at 125% or 150% **[guess]**.

| Product | Vertical chrome at 100% scale (px) | Share of 768 px | Notes |
|---|---|---|---|
| **MikePDF today** | About 80 (title row plus command row, read from [store screenshots](../store/screenshots/01-reading.png): 1570 px wide capture, scale unknown). The XAML sets a 40 epx title row plus a `CommandBar` at its default height **[repo]** | About 10% | Sidebar is closed by default |
| **MikePDF target, standard** | **80** | **10.4%** | 40 epx tab strip plus 40 epx command bar. Fixed: contextual tools replace the centre of the row |
| **MikePDF target, reading mode** | **0** | 0% | |
| Edge | About 125 to 130 **[guess]** | About 17% | Tab strip, address and toolbar row, PDF toolbar |
| Acrobat Reader | About 100 to 130 plus a right-hand pane of about 300 px width when open **[guess]** | 13 to 17% | Depends on classic vs new interface |
| SumatraPDF | About 60 to 90 **[guess]**; can be turned off with F8 and Shift+F8 **[src]** | 8 to 12% | |
| Foxit Reader | About 120 to 160 **[guess]** | 16 to 21% | Ribbon style is not confirmed |
| PDF-XChange Editor | About 100 to 140 **[guess]** | 13 to 18% | |

**MikePDF target in physical pixels:**

| Display | Scale | Standard chrome (80 epx) | Share of screen height |
|---|---|---|---|
| 1366x768 | 100% | 80 px | 10.4% |
| 1366x768 | 125% | 100 px | 13.0% (page well: 768 / 1.25 - 80 = **534 epx** tall) |
| 1366x768 | 150% | 120 px | 15.6% (page well **432 epx**) |
| 3840x2160 (4K) | 150% | 120 px | 5.6% (page well **1360 epx**) |
| 3840x2160 (4K) | 200% | 160 px | 7.4% (page well **1000 epx**) |

The share goes up as the scale does because the chrome is sized in epx. That is deliberate: the targets must stay 40 epx for touch and for people who set a high scale because they need large controls. Reading mode (0 chrome) and the F4 pane toggle are for people who need the room.

---

## 4. Specific, measurable ways MikePDF will beat each competitor

Each row is a target, with the way it is checked. "Competitor today" is what the evidence above supports, with its tag. A row is only claimed once the competitor value has been measured on the same hardware. Until then the **status is "target"**.

Definitions: R1 is the reference low-end machine (§11). "Median of 10" means 10 runs after one discarded warm-up run, cold or warm as stated. Scenario numbers are §18 scenarios.

### 4.1 Against Adobe Acrobat Reader

| # | Dimension | Competitor today | MikePDF target | How measured |
|---|---|---|---|---|
| A1 | Cold open of a 1-page PDF from Explorer to first rendered pixel | Not measured | **at most 800 ms** (§11), and at most **50% of Reader's median of 10** on the same R1 | ETW/PresentMon trace, [ux-metrics section 4](ux-metrics.md#4-competitor-measurement-protocol) |
| A2 | Interruptions on first launch of a clean profile (dialogs, tours, sign-in, offers) | A welcome screen with a tour, and sign-in prompts, are reported **[user report]** | **Zero.** The first launch with a file shows the file | Scripted first-launch on a clean VM; count blocking windows |
| A3 | Modal or in-flow upsell dialogs in the whole app | AI add-on and Acrobat Pro offers exist **[user report; src weak]** | **Zero.** There is no purchasable feature to upsell. Checked by a string and dialog audit on every release | `ContentDialog` and string audit in CI; manual pass |
| A4 | Actions to highlight a selected passage in a chosen colour | Several clicks through the Comment tool or context menu; keyboard-only: Menu key then H **[user report]** | **1 click** on the selection toolbar (colour included), or **1 chord** (`Ctrl+H`, last colour). Selection excluded in both | Click and keystroke count in [ux-metrics section 2](ux-metrics.md#2-task-scripts-click-counts-and-timings) |
| A5 | Vertical chrome at 1366x768 | Not measured | At most **80 epx**, constant while marking | Pixel measurement from screenshots |
| A6 | Command coverage by keyboard | Single-key tool shortcuts off by default **[src]** | **100%** of commands are in the palette and remappable. Zero default character-key shortcuts that cannot be turned off | Command inventory vs registry test |
| A7 | Idle memory, one 20-page document | Not measured | UI process at most **200 MB** (§11); engine separate | Perf harness (W1-perf) |
| A8 | Install size | Not measured | At most **60 MB** per architecture (§11) | Package size |
| A9 | A damaged or hostile file does not take the window down | Not measured | The tab shows an error. Other tabs keep working. Engine back within **2 s** (§11) | Journey 7 |
| A10 | Reading by screen reader without a mouse | Not assessed | §18 scenario 6 completes with NVDA and keyboard only | Manual pass (product owner) |

### 4.2 Against the Microsoft Edge PDF viewer

| # | Dimension | Competitor today | MikePDF target | How measured |
|---|---|---|---|---|
| E1 | UI actions between "pen in hand, document open" and "first stroke is on the page" | Click Draw first **[src]** | **0 UI actions.** The pen inks on first touch when pen-first is on (default on where a pen digitizer is present). No tool to pick | Count on a Surface. S5 hardware needed |
| E2 | Ink colour and thickness persist to the next document | Reported to revert per document **[user report]** | **Persist** across documents and sessions, per tool | Open two documents; compare |
| E3 | Annotation list with filter, sort and jump-to in v1.0 | A Notes pane was announced, not confirmed shipped **[src weak]** | **Present in v1.0**, with filter by type, author, status; jump-to; 1 click to the thread | Feature acceptance test |
| E4 | Page organiser, merge and split | Not mentioned in the Microsoft page read | **All three**, undoable, in the app | §18 scenario 5, clicks counted |
| E5 | Forms: scripts and XFA | JavaScript forms and XFA are not supported **[src]** | JavaScript is off by default; the UI **says** when formatting scripts did not run and when a form is XFA, and still fills the AcroForm fallback | Fixture forms |
| E6 | Two-page and cover modes | Documented as Dev/Canary only in the version read **[src, possibly stale]** | **In v1.0**, including cover and R2L | Feature test |
| E7 | Documents in a workspace | Tabs share the web browser's | Tear-out, merge, session restore of positions and panes, Jump List | Journey 1 |
| E8 | Window chrome | PDF viewer sits under the browser chrome **[unverified]** | 80 epx, 0 in reading mode | Screenshot |
| E9 | Number of actions to add a note with a reply | Select, right-click, Add Comment, type, tick **[src]**; replies **[unverified]** | Select, `Ctrl+M`, type; reply with `Ctrl+Enter`; at most **4 steps** for a note plus a reply | Click count |
| E10 | Commands reachable in a command palette | None **[unverified]** | **100%** | Registry test |

### 4.3 Against SumatraPDF

SumatraPDF is the speed and keyboard benchmark. It's the hardest to beat and the most important to **match**.

| # | Dimension | Competitor today | MikePDF target | How measured |
|---|---|---|---|---|
| S1 | Cold open to first pixel | Not measured. Expected to be fast **[guess]** | **Within 1.25 times** Sumatra's median of 10, or at most 800 ms, whichever is larger. Honest note: we may not match it | ETW trace; failing this is **reported, not hidden** |
| S2 | Keyboard marking | 1 key (`a`, `u`) after selecting **[src]** | Match: 1 chord (`Ctrl+H`, `Ctrl+U`). Beat: **colour choice in the same single action** (selection toolbar) and a visible result in the annotation list | Count |
| S3 | Windows 11 fit | Custom Win32 UI, own themes **[src]** | Mica Alt, system accent, snap layouts, high contrast, 225% text. **Preference in a 5-user test: at least 4 of 5 users prefer the look and ease of MikePDF** | [ux-metrics section 6](ux-metrics.md#6-five-user-usability-test-plan), needs the product owner |
| S4 | Screen-reader support | UIA provider is experimental **[src]** | Text pattern with structure-tree order, annotations and fields as UIA children; **zero Accessibility Insights FastPass failures**; scenario 6 completes | FastPass; manual NVDA pass |
| S5 | Annotation save in other viewers | Not measured here | **100% of P0 annotation types render in Acrobat Reader, Edge, Chrome and Firefox** after saving (§0.3) | Fidelity harness; manual Acrobat pass |
| S6 | Pen | Ink exists in 3.7 pre-release **[src]**; pressure and latency unknown | Pressure-sensitive variable-width ink, palm rejection, pen-to-pixel latency **at most 25 ms** p95 (§11) | S5 camera measurement |
| S7 | Palette | Ctrl+K with prefix modes **[src]** | Match the coverage. Beat: **shows shortcut and the reason a command is disabled**, ranks by use, edits shortcuts from the row | Feature test |
| S8 | Forms | Highlighted fields **[src]** | Highlight, required-field and validation display, tab order check, **never blocking a save** | Scenario 3 |
| S9 | Page organiser, merge, split | Not documented | All three | Scenario 5 |
| S10 | Discoverability | Large shortcut set, not shown in the UI **[unverified]** | Every tooltip shows its shortcut from the registry; `Ctrl+/` shows them all | Review |

### 4.4 Against Foxit PDF Reader (verify first)

All rows depend on evidence not yet collected. Collect the competitor value, then confirm or drop the row.

| # | Dimension | Competitor today | MikePDF target | How measured |
|---|---|---|---|---|
| F1 | Upgrade prompts | Reported frequent **[src weak]** | **Zero** | Audit |
| F2 | OCR in the free version | Reported absent **[src weak]** | OCR is P1 (not v1.0). Don't claim until shipped | n/a |
| F3 | Visible commands in the default view | Not measured | At most **13** visible buttons at L width (12, plus Save when there are edits), everything else in the palette and menus | Count |
| F4 | Vertical chrome | Not measured | At most **80 epx** | Screenshot |

### 4.5 Against PDF-XChange Editor

| # | Dimension | Competitor today | MikePDF target | How measured |
|---|---|---|---|---|
| X1 | Watermarks added to output without a licence | Yes, for marked features **[src]** | **Never.** Every output is clean. There are no licence-gated features | Save and inspect every output type |
| X2 | Form creation | Needs a paid edition **[src weak]** | Form creation is **not in v1.0** (§1.3 non-goal). Don't claim | n/a |
| X3 | Look | Classic toolbars **[unverified]** | Native Fluent, Mica Alt | 5-user preference test |
| X4 | Number of toolbars and rows open by default | Not measured | **2 rows** | Count |

### 4.6 Against the references (Okular, Preview)

These are not competitors on Windows. The targets are **design parity** checks, reviewed by Opus: markup tools sit in the main command row (as Preview does **[unverified]**), and notes distinguish a pop-up from an inline text box (as Okular does **[unverified]**).

### 4.7 Wins that apply to every competitor

| # | Dimension | Target | How measured |
|---|---|---|---|
| ALL1 | A crash in one file never loses user work | Journal restores edits in **2 s** after an engine crash (§11, journey 7); after a UI crash, the next launch offers to restore | Journeys 7, 8 |
| ALL2 | No network needed at any point (non-cloud features) | **100%** of v1.0 features work with the network off | Scenario 8, Defender firewall test |
| ALL3 | Enterprise controls and visibility | Policies enforced and **listed in About** | Scenario 9 |
| ALL4 | Keyboard-only completion of all ten scenarios | **10 of 10** scripts pass without the mouse | [ux-metrics section 2](ux-metrics.md#2-task-scripts-click-counts-and-timings) |
| ALL5 | Text scale, high contrast, narrow window | No clipped or overlapping text at 225%; UI usable at 320 epx and in all four contrast themes | Checklist in [ux-metrics section 5](ux-metrics.md#5-accessibility-checks) |

---

## 5. Design opportunities taken from this teardown

| # | Finding | Decision | Where |
|---|---|---|---|
| T1 | Sumatra marks text with one key; Edge needs three actions; Reader needs a tool | Selection toolbar with colour, plus `Ctrl+H` | [screens/08](screens/08-markup-and-ink.md) |
| T2 | Edge requires Draw mode; settings reset per document | Pen-first ink; tool settings persist | [screens/08](screens/08-markup-and-ink.md) |
| T3 | Sumatra's palette has prefix modes | One palette with sections (commands, pages, bookmarks, tabs, recent, settings). A prefix is optional shorthand | [screens/07](screens/07-command-palette.md) |
| T4 | Reader needs sign-in, shows a tour | No first-run flow. One non-blocking default-app prompt, on the empty state only | [screens/15](screens/15-first-run.md) |
| T5 | Edge's Notes pane was announced late | Annotations pane in v1.0 | [screens/06](screens/06-side-panes.md) |
| T6 | Edge lacks a page organiser; Reader gates it | Organiser in v1.0, merge as "pick files, get a grid" | [screens/10](screens/10-page-organiser.md) |
| T7 | Edge has caret browsing; Sumatra `F7` | Keyboard text selection with a caret (palette: Toggle caret selection, `F7`) | [commands.md](commands.md) |
| T8 | Sumatra's accessibility is experimental | The page surface is a first-class UIA Text provider | [screens/01](screens/01-main-window.md) |
| T9 | PDF-XChange watermarks | No gated feature, no watermark | [ux-metrics.md](ux-metrics.md) |
| T10 | Chrome from 100 to 160 px | 80 epx constant; contextual swap in place | [README section 3](README.md#3-window-anatomy-and-layout-system) |

---

## 6. Sources

All accessed 2026-11-02. Pages that returned an error to the fetch tool are marked.

| Ref | URL | Used for | Note |
|---|---|---|---|
| 1 | https://www.sumatrapdfreader.org/docs/Keyboard-shortcuts | Sumatra shortcuts, annotations, palette | Read |
| 2 | https://www.sumatrapdfreader.org/docs/Version-history | Sumatra 3.7 pre-release features | Read (first 100,000 of 146,532 characters) |
| 3 | https://learn.microsoft.com/deployedge/microsoft-edge-pdf | Edge PDF features, accessibility, constraints | Read; updated 2026-06-15 |
| 4 | https://mc.merill.net/message/RM577542 | Edge Notes pane announcement | Search snippet only |
| 5 | https://techcommunity.microsoft.com/t5/articles/microsoft-edge-extends-tools-for-the-pdf-reader/m-p/1303243 | Edge ink settings reset per document | Search snippet; user post |
| 6 | https://helpx.adobe.com/acrobat/using/keyboard-shortcuts.html | Adobe single-key accelerators | **403 on fetch**; via search snippet |
| 7 | https://community.adobe.com/ (threads linked above) | Sign-in prompts, welcome tour, new UI toggle, keyboard-only highlight | Search snippets; user posts |
| 8 | https://www.businesswire.com/news/home/20240508717446/en/ | Acrobat AI Assistant add-on | Search snippet |
| 9 | https://en.wikipedia.org/wiki/Foxit_PDF_Reader | Foxit release | Search snippet |
| 10 | https://www.cuspera.com/products/foxit-reader-x-9224/news | Foxit upgrade prompts and OCR | Search snippet; low reliability |
| 11 | https://www.pdf-xchange.com/product/pdf-xchange-editor/demo_limits | PDF-XChange watermark model | Search snippet; fetch redirected |
| 12 | https://okular.kde.org/ | Okular features | Read |
| 13 | https://support.apple.com/guide/preview/annotate-a-pdf-prvw35730/mac | Preview features | Read (index only) |
| 14 | https://learn.microsoft.com/en-us/windows/apps/design/ (typography, geometry, mica, titlebar, accessibility, targeting pages) | Windows design guidance used by the [design README](README.md) | Read |
