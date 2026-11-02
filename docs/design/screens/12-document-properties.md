# 12 Document properties

- Part of the [MikePDF design spec](../README.md)
- Serves §18 scenarios: 9 (an administrator checks what a document and the app are doing: security, scripts), 6 (a screen-reader user finds out whether the file is tagged)
- Spec: master prompt §2.1 ("document properties dialog: metadata, PDF version, producer, fonts, security"). W1-reader (`m2/reader-gaps`) implements a first version of this dialog; this design is what it should converge to.
- Opens with `Ctrl+D` or the **Document properties** command (palette, command bar overflow, File area).

## 1. Purpose

Answer "what is this file?" without opening a technical tool: who made it, how big it is, whether it is protected or signed, what it contains that could matter (scripts, attachments, forms), and which fonts it needs.

## 2. Layout

### 2.1 Dialog (a `ContentDialog`, 560 x 560 epx max, `Close` button only)

```text
+--------------------------------------------------------------------+
| Document properties                                                |
|   [ Description ]  Security   Fonts   Details         <- SelectorBar  |
| ------------------------------------------------------------------ |
|  Title          Annual report 2025                                  |
|  Author         Finance team                                        |
|  Subject        Group results                                        |
|  Keywords       annual, finance                                      |
|                                                                    |
|  File name      Annual report.pdf              [Show in folder]     |
|  Location       C:\Users\mike\Documents        [Copy path]          |
|  File size      18.4 MB (19,298,114 bytes)                         |
|  Created        12 Mar 2026, 09:02                                  |
|  Modified       14 Mar 2026, 17:45                                  |
|  Created with   Microsoft Word                                       |
|  PDF producer   Microsoft: Print To PDF                              |
|  PDF version    1.7                                                  |
|  Pages          612        Page size  A4 (210 x 297 mm)  [see all]   |
|  Tagged PDF     Yes                                                  |
|  Fast web view  No                                                   |
|                                                          [ Close ]  |
+--------------------------------------------------------------------+
```

### 2.2 Security tab

```text
|  [ Description ]  [Security]   Fonts   Details                     |
|  Password to open    Yes (AES 256-bit)                             |
|  Signed              No   (or: Yes, 2 signatures  [Show signatures])|
|                                                                    |
|  What this document allows                                          |
|   Printing                 Allowed (high quality)                  |
|   Copying text and images  Not allowed                             |
|   Adding comments          Allowed                                 |
|   Filling forms            Allowed                                 |
|   Changing pages           Not allowed                             |
|   Reading with assistive technology   Allowed                      |
|  These are set by the document's owner. MikePDF respects them.     |
```

### 2.3 Fonts tab

```text
|  [ Description ]  Security  [Fonts]  Details                       |
|  Scanning pages... 80 of 612   [==========------]   [Stop]          |
|  Name                     Type          Embedded    Encoding        |
|  Calibri                  TrueType      Subset      WinAnsi         |
|  Cambria Math             Type 0        Subset      Identity-H      |
|  Helvetica                Type 1        No          WinAnsi         |
|  (Not embedded fonts may look different on other computers.)        |
```

### 2.4 Details tab

```text
|  [ Description ]  Security  Fonts  [Details]                       |
|  Revisions            3 (the file was saved 2 times after the first)|
|  Scripts              None   (or: "Has scripts. MikePDF doesn't run them.")|
|  Attachments          2                          [Show attachments] |
|  Forms                120 fields (AcroForm)   (or: XFA form)         |
|  Page labels          Yes (i, ii, iii, 1, 2, ...)                    |
|  Bookmarks            12                                             |
|  Comments             24                                             |
|  Reading direction    Left to right                                  |
|  Page layout hint     Two pages, with cover                          |
```

## 3. Behaviour

1. **Opening is instant** (the main fields are in `GetDocumentInfo` and the file system). Slower facts fill in as they arrive (fonts, revision count). A `ContentDialog` is modal to its window, so it blocks that window's tabs until it is closed. That is acceptable because it closes quickly, and other windows are unaffected.
2. **Read-only in v1.0** (Q8). Title, author, subject and keywords are shown, selectable and copyable (each row has a **Copy** option on right-click), but not editable. Editing metadata is a write feature; if the owner wants it, it joins the save pipeline ([README Q8](../README.md#13-open-questions-roll-up)).
3. **Rows with nothing to show** read "Not set" (not blank, not a dash), so a screen reader says something.
4. **Dates** use the user's regional format. Sizes use the user's units (KB, MB) with exact bytes in the tooltip.
5. **Page size** shows the first page's size, with **see all** when pages differ, which lists sizes with counts ("A4: 590 pages, A3: 22 pages") **[design]**. Sizes honour `/CropBox`.
6. **Security tab** shows the **permissions** the engine reports (`GetPermissions`): the permission bits as plain sentences. The line "These are set by the document's owner. MikePDF respects them." states policy plainly. **MikePDF never offers to remove or bypass them.**
7. **Fonts tab** scans pages for fonts in the background: a determinate progress bar, **Stop**, and a "partial" label if stopped. **PDFium has no document-level font list** (README E4), so this tab is a scan of page objects: the first 50 pages first, then the rest, capped at 2,000 pages scanned unless the user presses **Scan all** **[design]**. It is the slowest part and is optional. The scan runs in the engine at idle priority and yields to rendering.
8. **Details tab:** revisions, scripts, attachments, forms, XFA, page labels, bookmarks, comments, reading direction, page layout hint. **Show attachments**, **Show signatures** and **Show comments** close the dialog and open that pane.
9. **Show in folder** and **Copy path** (the broker uses the shell; path text is the user's own data). Both are absent for a document opened from a stream with no path.
10. **Copy all:** a **Copy** button (or `Ctrl+C` with no text selected) puts the whole properties list on the clipboard as text. No hidden fields are included.
11. **The title bar shows the file name** and the dialog's own title shows the document name for a screen reader ("Document properties, Annual report").
12. **Cloud-only files** (OneDrive placeholders) aren't hydrated by this dialog: the file size and dates shown come from the file system record, and a note says "Available online only" when the placeholder bit is set.

## 4. States

| State | Behaviour |
|---|---|
| **Loading** | Rows show their values as they arrive; unknown ones show "Reading..." in secondary text and are live regions (polite, announced once complete). The fonts scan shows its progress bar |
| **Empty** | A document with no metadata: "Not set" rows. A document with no fonts (images only): the Fonts tab says "This document doesn't use any fonts." Not applicable without a document: the command is disabled |
| **Error** | A property that can't be read shows "Couldn't be read" with no other action; the dialog keeps working. A font scan that fails: "The font list couldn't be read." with **Try again**. If the engine restarts while the dialog is open, it shows "MikePDF restarted. Reading again..." and refills |
| **Keyboard focus** | Opens with focus on the tab selector. `Left` and `Right` change the tab; `Tab` moves into the rows (each row is a focusable text with its label); `Esc` or **Close** closes and returns focus to where it was. Focus rings as usual |
| **High contrast** | System colours; tab selector uses `Highlight`; the permission rows use text ("Allowed", "Not allowed") with no colour dependency; progress bar uses `Highlight` |
| **225% text scale** | The label and value columns stack: the label on top of the value. The dialog scrolls vertically; **Close** stays pinned. Long paths wrap anywhere (a path has no spaces). The tab selector shows icons only (names in tooltips) |
| **Narrow window** | The dialog is a full-width sheet with an 8 epx margin. Two-column rows stack as in 225% |
| **Touch** | Rows are at least 40 epx high; **Copy** appears as a visible button on each row on touch (the context menu works too); tabs are 44 epx high |

## 5. Accessibility

| Element | Type | Accessible name | AutomationId |
|---|---|---|---|
| Dialog | Dialog | "Document properties, {name}" | `PropertiesDialog` |
| Tab selector | Tab | "Properties section" | `PropertiesTabs` |
| Tab | TabItem | "Description", "Security", "Fonts", "Details" | `PropDescription`, `PropSecurity`, `PropFonts`, `PropDetails` |
| Row | Group (with label and value) | "{Label}: {value}" | `Prop_{key}` |
| Show in folder, Copy path | Button | "Show {name} in folder", "Copy path" | `PropShowInFolder`, `PropCopyPath` |
| Permissions list | List | "What this document allows" | `PropPermissions` |
| Fonts list | List (columns) | "Fonts" | `PropFontList` |
| Font row | ListItem | "{name}, {type}, {embedded}, {encoding}" | `PropFont_{n}` |
| Font progress | ProgressBar | "Scanning fonts, 80 of 612 pages" | `PropFontProgress` |

Narrator, intended: opening: "Document properties, Annual report. Description, tab, 1 of 4, selected." Arrowing into the rows reads "Title: Annual report 2025". The permission list reads "Printing: Allowed, high quality." The tagged row reads "Tagged PDF: Yes" which tells a screen-reader user whether reading order is dependable.

## 6. Keyboard path

| Task | Keys |
|---|---|
| Open | `Ctrl+D` |
| Switch tab | `Left` / `Right` on the tab selector, or `Ctrl+Tab` inside the dialog |
| Read rows | `Tab` / arrows |
| Copy all | `Ctrl+C` |
| Show in folder | `Tab` to the button, `Enter` |
| Close | `Esc` |

## 7. Engine and protocol implications

| # | Need |
|---|---|
| E3 | `GetDocumentInfo` extended: title, author, subject, keywords, creator, producer, creation and modification dates, PDF version, page count, tagged, linearised, revision count, JavaScript present, XFA present, attachment count, outline count, annotation count, encryption method, **permission flags** |
| E1 | Page labels present? |
| E2 | Reading direction and page layout hints |
| E4 | `GetFontList`: progressive and cancellable. PDFium has no document font list, so the engine scans page objects, bounded by page range and time slice |
| | Page sizes: from `GetPageSizes` (existing) with a summary computed by the broker |
| | The revision count is a structural fact from the cross-reference chain. PDFium doesn't expose it directly **[unverified]**; if the engine can't give it, drop the row rather than make it up |
| | All values are untrusted strings: the UI **bounds-checks lengths** (a title of 10 MB must not be displayed) and strips control characters and bidirectional override characters before showing them |

## 8. OPEN QUESTIONS

1. **Q8:** editable metadata in v1.0? This spec: no.
2. **Description tab position:** first. Some readers lead with the Security tab; for a reader, users ask "what is it?" first.
3. Should the dialog be a **non-modal side sheet** instead (so it can stay open while the user reads)? §2.1 says dialog. This spec follows §2.1.
4. Revision count and linearisation need an engine capability that has not been checked.
5. The per-permission wording ("Printing: Allowed, high quality") needs a content review.
