# Feature spec: reader gaps (WP W1-reader, branch `m2/reader-gaps`)

- Status: implemented on `m2/reader-gaps`, awaiting Opus review. The unit, protocol, engine and integration tests have run; the UI journeys are written but see the table below for whether they have run
- Master prompt: §2.1 (Viewing), §8.1 (Find), §10.2 (Keyboard), §10.3 (Accessibility), §5.4 (IPC), ADR-0004 A2 (broker-private data), ADR-0005 A1 (bounded windows)
- Template: A.4. Anything that was ambiguous is marked **OPEN QUESTION** and what was done meanwhile is stated.
- Out of scope (next wave, M3): tabs, extra windows, the command palette, the settings dialog, presentation mode, two-page layouts, remappable shortcuts. Every shortcut below is a default that the later shortcut map takes over.

Seven items: (1) back and forward history, (2) named destinations, (3) zoom and navigation keys with true actual size, (4) find options, (5) page colour modes, (6) document properties, (7) full screen.

## Shared rules
- **Strings** are `Strings/en-US/Resources.resw` keys. A sentence is one resource with `{0}` placeholders, never joined from pieces.
- **Every control has a UIA name** and an AutomationId (the journeys find controls by id). Nothing is mouse-only.
- **Engine data is untrusted.** Every new reply is limited by the schema (counts, lengths, ranges), checked again against the request by the broker, and cleaned with `DisplayText.Clean` before it is shown. A bad reply ends the session like any other protocol violation; a missing or failed reply shows a plain message and never blocks reading.
- **No network, no process start, no external link.** `PrivacyTests` stay green.
- **Cancellation and engine crash:** every async path takes a `CancellationToken`, closing the document cancels it, and an engine restart is retried by the existing supervisor.

---

## 1. Back and forward history

### Stories
- I follow a link to a footnote and press Alt+Left to return to the line I was reading.
- I search, hop to a match three chapters away, and return with one press.
- I jump with a thumbnail or a bookmark and can go back with the mouse's back button.

### Behaviour
- **What is recorded:** the position the view was at (page, and how far down it, of the page at the top of the window) just before a *jump*. Jumps are: following an internal link, Go to page (dialog), choosing a bookmark, choosing a thumbnail, and moving to a find match (F3, Enter, the buttons).
- **What is not:** scrolling, the wheel, Page Up/Down, the arrow keys and Home/End (they are reading, not jumping), zooming, rotating.
- **A jump that does not move the view** (the destination is already where the window is, within 2 px) records nothing.
- **Find coalescing:** moving between the matches of one find records one entry, the position before the first jump. A new search, or any other kind of jump, starts a new entry.
- **Back** (Alt+Left, the mouse back button, the toolbar button) goes to the most recent entry and puts where the person was onto the forward list. **Forward** (Alt+Right, the mouse forward button, the toolbar button) is the reverse. A new jump clears the forward list.
- **Bounded:** 64 entries per direction; the oldest is dropped. **Per document:** each document has its own history, which ends when the document is closed. It is not saved.
- **Toolbar:** "Back" and "Forward" buttons after Open, enabled only when there is somewhere to go.
- **Input:** Alt+Left and Alt+Right work wherever the keyboard focus is (the page surface, the sidebar, the find box). The mouse's X1 and X2 buttons work over the whole window.
- **Edge cases:** a document of one page keeps a history within the page; an entry whose page no longer exists (cannot happen today, as pages do not change) is skipped; opening another document starts an empty history.

### Accessibility
- Buttons: AutomationIds `BackButton`, `ForwardButton`, names "Back" and "Forward", tooltips name the keys. `IsEnabled` follows the history, so Narrator announces them as dimmed.
- After Back/Forward the page indicator's name changes ("Page 3 of 12..."), which is the only announcement needed.

### Acceptance criteria
- **H1** `NavigationHistory` records an entry for a jump, and Back returns it, Forward returns where Back left. (unit)
- **H2** A new jump after a Back clears Forward. (unit)
- **H3** History holds at most 64 entries per direction and drops the oldest. (unit)
- **H4** A repeat of the same location (same page, within 1% of a page) is not recorded twice in a row. (unit)
- **H5** Find jumps of one search coalesce to one entry; a different jump key or `ResetKey` starts a new one. (unit)
- **H6** Back and Forward on an empty history return nothing and change nothing. (unit)
- **H7** Following a contents link then Alt+Left returns to the contents page, Alt+Right returns to the chapter. (UI journey)
- **H8** Go to page, a thumbnail and a bookmark each record a jump; scrolling with Page Down and the arrow keys records nothing. (UI journey)
- **H9** The toolbar buttons are disabled with no history and enabled after a jump, and clicking them navigates. (UI journey)
- **H10** The mouse back and forward buttons navigate. (UI journey, real `SendInput` X buttons)
- **H11** Moving to several find matches then Back returns to where the search began. (UI journey)

---

## 2. Named destinations

### Behaviour
A *named destination* is a name that the file maps to a place: in the catalog's `/Names /Dests` name tree (PDF 1.2) or the catalog's `/Dests` dictionary (PDF 1.1). A link or bookmark can point at the name instead of the place:
- a link's `/Dest` that is a string or a name;
- a link or bookmark `/A << /S /GoTo /D (name) >>`;
- a bookmark's `/Dest` that is a string or a name.

PDFium resolves the name while describing the destination, so the engine reports the *resolved* destination (kind 1 to 3 with page and point) and the broker never sees the name. The work here is to prove that every form resolves, and to fix any that does not.
- **Resolved:** the link/bookmark behaves exactly like one with an explicit destination (goes to the page, to the point, recorded in history).
- **Unresolvable** (the name is not in the tree, or its value is not a destination): the destination is kind 0 (*None*): the link is not clickable (no hand cursor) and the bookmark does nothing.
- **What `DestinationKind.Other` (5) covers:** an action MikePDF deliberately does not follow: remote go-to (`GoToR`) and embedded go-to (`GoToE`), launch, named actions (`NextPage`, `Print`...), JavaScript, submit-form, import-data, movie, sound, and any other unsupported action type. It never means "a name that could not be resolved". Clicking such a link shows "Link not followed".
- A name tree with `/Kids` and `/Limits`, a value that is a dictionary with `/D`, and a hostile deep or cyclic tree are PDFium's to bound; the engine does not loop (PDFium has depth limits) and the tests include a cycle.

### OPEN QUESTION
- *Named actions* (`/S /Named /N /NextPage`) are reported as `Other` because PDFium exposes no way to read the action's name. They could be supported later only with an engine change that parses the dictionary itself. Not done.

### Acceptance criteria
- **N1** A link with `/Dest (name)` resolved through the `/Names /Dests` tree reports the destination's page and point. (engine unit, integration)
- **N2** A link with `/Dest /Name` resolved through the catalog `/Dests` dictionary resolves. (engine unit)
- **N3** A `GoTo` action with `/D (name)` resolves, for a link and for a bookmark. (engine unit, integration)
- **N4** A bookmark with `/Dest (name)` resolves. (engine unit, integration)
- **N5** A name-tree value that is a dictionary with `/D` resolves; a tree with `/Kids` and `/Limits` resolves. (engine unit)
- **N6** An unknown name, a name whose value is not a destination, and a tree that points back to itself give kind 0 and never hang. (engine unit)
- **N7** A `GoToR`, `Launch` and `Named` action stay kind 5 and are never resolved or followed. (engine unit)
- **N8** Clicking a link with a named destination, and choosing a bookmark with one, scrolls to the destination's page. (UI journey)

---

## 3. Zoom and navigation keys, and actual size

### Behaviour
Already present (verified, and now covered by journeys): Ctrl+= / Ctrl+- zoom in/out by the usual steps, Ctrl+0 reset to 100%, Ctrl+1 fit page, Ctrl+2 fit width, Ctrl+Shift+= / Ctrl+Shift+- rotate the view, Right/Left next/previous page, Home/End first/last page, F3 / Shift+F3.

Added or changed:
- **Page Down / Page Up** follow §10.2 (next / previous page) when the page in view fits whole in the window, and scroll one screen less a little overlap when it does not (the page is taller than the window). Space and Shift+Space keep scrolling a screen.
- **Actual size** (Ctrl+Alt+0, and "Actual size" in the zoom menu): the zoom at which one inch of the page is one inch on this monitor. It uses the monitor's physical DPI (`GetDpiForMonitor`, raw DPI) and the display scale: `zoom = rawDpi / (96 x displayScale)`. When Windows does not know the monitor's size (a virtual display, a projector: raw DPI missing or outside 72 to 400) it falls back to 100%. The monitor is the one the window is on at that moment, so it is right after moving the window between monitors.
- **Ctrl+0 stays "100%"** (96 CSS-style pixels per inch, as Edge and Acrobat), the reset in §10.2. **OPEN QUESTION:** the master prompt lists both "reset" and "actual size". They are two commands here. If Opus prefers one, Ctrl+0 can be pointed at actual size by changing one handler.
- The zoom menu lists "Actual size" first. Its tooltip names the key.

### Acceptance criteria
- **Z1** `ZoomMath.ActualSizeZoom(rawDpi, scale)` returns `rawDpi / (96 x scale)`, clamps to 10%..6400%, and returns 1.0 when the DPI is missing or implausible. (unit)
- **Z2** `ViewportController.PageDown/PageUp` go to the next/previous page when the current page fits the window, scroll a screen when it does not, and stop at the first and last page. (unit)
- **Z3** Ctrl+= and Ctrl+- change the zoom by one step each way; Ctrl+0 gives 100%; Ctrl+1 and Ctrl+2 give the fit modes. (UI journey)
- **Z4** Page Down / Page Up move a whole page in fit-page mode and scroll within a tall page. (UI journey)
- **Z5** Ctrl+Alt+0 and the menu item "Actual size" set the zoom the physical-size formula gives for the window's monitor (the test reads the monitor's raw DPI itself). (UI journey)
- **Z6** Ctrl+Shift+= and Ctrl+Shift+- rotate the view by quarter turns and the page indicator keeps the page. (UI journey)
- **Z7** Home/End/Right/Left behave as before. (UI journey, existing)

---

## 4. Find options

### Behaviour
- The find bar gets two toggles beside "Match case": **Whole word** and **Regular expression**. All three are on-state toggle buttons with names and tooltips. Changing any restarts the search.
- **Whole word** (not regex): a match must not be directly preceded or followed by a letter or digit. The engine does this (PDFium whole-word flag).
- **Regular expression:** the pattern is a .NET regular expression compiled with `RegexOptions.NonBacktracking | CultureInvariant` (plus `IgnoreCase` unless Match case is on) and a match timeout of 2 s per page. It runs in the broker over the characters of the page that were already read (the same numbering as the engine's matches), on a worker thread, never the UI thread.
  - **Hostile patterns:** `NonBacktracking` guarantees linear matching, so catastrophic patterns (`(a+)+$`) finish. Constructing a regex has no timeout, so a pattern is first checked: at most 128 characters (the box limit), counted repeats `{n,m}` with no bound above 1000, and the product of all the repeat bounds at most 1000 (measured: the matcher builds its states as it goes; `(a{30}){30}` takes 0.3 s on a 5000-character page, `(a{50}){50}` 1.2 s, `(a{70}){70}` over 2 s and .NET refuses `(a{100}){100}`). Anything else is refused as "too complex" without being compiled. A pattern that compiles but exceeds the 2 s timeout on a page marks that page as not searched and is not retried; after three such pages in a row the rest of the document is given up on without trying.
  - **Unsupported syntax** (backreferences, lookarounds, atomic groups, conditionals: `NonBacktracking` throws `NotSupportedException`) is reported as "unsupported", not as a crash.
  - Empty matches are ignored; at most 2048 matches per page (the engine's limit), with the "truncated" flag.
  - Whole word is switched off while regex is on (a pattern writes `\b` itself).
  - Line breaks PDFium made up are `\r\n` in the text, so `\s` crosses lines; a hyphen at the end of a line appears as `-`.
- **Messages in the live region** (`FindStatus`, a polite live region updated when a search ends or the match changes): "3 of 17", "No matches", "Searching, 40%", "That pattern is not valid", "That pattern is too complex", "That pattern uses features that are not supported", and the new "{0} of {1}. Some pages could not be searched." when pages failed.
- **F3 / Shift+F3** go to the next/previous match from anywhere (the page, the sidebar), and open the find bar when it is closed. Enter / Shift+Enter in the box do the same.
- **Diacritic-insensitive** search (§8.1) is not part of this work package. The results panel (§8.1) is M3.

### Accessibility
- Toggles: `FindWholeWordToggle` "Whole word", `FindRegexToggle` "Regular expression", `FindMatchCaseToggle` "Match case", with Toggle state exposed.
- The count is announced through the live region; while a search runs the region is off so it does not chatter.

### Acceptance criteria
- **F1** The engine's whole-word flag excludes a match inside a longer word ("margin" does not match "margins") and keeps a standalone one. (integration)
- **F2** `RegexQuery.TryCompile` accepts a valid pattern and reports `Invalid` for a bad one and `Unsupported` for a lookaround or backreference. (unit)
- **F3** Patterns over 128 characters, a bound above 1000, or a product of bounds above 1000 are refused as `TooComplex` without compiling. (unit)
- **F4** A catastrophic-backtracking pattern over a 200000-character page completes in under 5 s and finds the right matches. (unit)
- **F5** A match timeout produces a failed page (`Unsupported`), not an exception, and is not retried; three in a row give up on the rest of the document. (unit)
- **F6** Regex matches carry the right start and length in the engine's character numbering, skip empty matches, honour Match case, and stop at 2048 with the truncated flag. (unit)
- **F7** A `FindSession` over a regex source finds matches in page order starting at the current page, counts pages that failed, and reports them in its progress. (unit)
- **F8** Whole word and regex toggles change the count shown in the live region ("margin" with Whole word has fewer matches; `marg.n` finds some; an invalid pattern shows the message and a hostile one does not hang the window). (UI journey)
- **F9** F3 and Shift+F3 move between matches with focus on the pages, and open the bar when closed. (UI journey)
- **F10** The new controls have the UIA names and ids listed above and expose their toggle state. (UI journey)

---

## 5. Page colour modes

### Behaviour
- Three modes: **Normal**, **Dark pages** (inverted, for night reading) and **Sepia**. A command-bar button "Page colour" opens a menu of three radio items; the current one is checked.
- Done on the GPU in the broker's Win2D presenter: each tile is drawn through a colour matrix effect. No engine change, no re-render, and switching is instant (the next frame).
- **Dark pages** inverts lightness and keeps hue (the matrix of CSS `invert(1) hue-rotate(180deg)`, then eased so that black text on white becomes about 95% white on about 7% grey instead of pure white on pure black). A red stays reddish, a blue stays bluish. The paper behind the tiles is mapped through the same matrix, so a page that has not loaded yet is dark as well.
- **Sepia** multiplies by a warm paper tint (white becomes #F4ECD8) after a 20% desaturation. Black text stays dark.
- **Images:** the page is a flat bitmap by the time the GPU gets it, so a photograph is inverted along with the page. Its hue is kept, its brightness is not. Keeping photographs as they are would need the engine to draw images and text separately. **Not done; recorded as a follow-up.** This is a known limit of the "no engine change" route.
- **Persistence:** the choice is saved in the broker-private store (`LocalState\private\documents.json`, ADR-0004 A2: only the user and SYSTEM can read it; the sandboxed engine cannot). It applies to every document and survives a restart. A damaged value falls back to Normal and never stops the app.
- High contrast: the page colour modes are ignored while a high-contrast theme is on (the system colours win); the menu says so.

### Accessibility
- `PageColourButton` "Page colour"; menu items `PageColourNormal`, `PageColourInverted`, `PageColourSepia` with checked state. Mode changes are announced through the menu's own checked state; no extra announcement.

### Acceptance criteria
- **C1** The inverted matrix maps white to a dark grey and black to a light grey, keeps the hue of a saturated colour, and is deterministic. (unit)
- **C2** The sepia matrix maps white to #F4ECD8 within 1/255 and keeps black dark. (unit)
- **C3** The normal matrix is the identity. (unit)
- **C4** The page colour is stored in and read back from the document store; a missing or unknown value reads as Normal; a store written by the previous version (no value) loads. (unit)
- **C5** Choosing Dark pages makes the page area of the window dark, Sepia makes it warm, Normal restores it (measured on a screenshot of the page surface). (UI journey)
- **C6** The choice persists across a restart. (UI journey)
- **C7** The menu items have names and checked state. (UI journey)

---

## 6. Document properties dialog

### Behaviour
- Opened with Ctrl+D or the "Properties" button; a dialog with a Close button; Esc closes it.
- Sections and rows:
  - **Description:** Title, Author, Subject, Keywords.
  - **Document:** File name, File size, Pages, Page size (of the first page, in points and in inches or millimetres by region, with the paper name when it matches A3, A4, A5, Letter, Legal or Tabloid; "pages differ in size" when all sizes are known and differ), PDF version, Tagged PDF (yes/no), Created, Modified (in the user's locale and time zone, `f` format; a date that cannot be parsed is shown as the file wrote it, cleaned), Creator (application), Producer (PDF library), Contains scripts, Signatures (count; validity is M6), Form type (none, AcroForm, XFA).
  - **Security:** Encryption (none, or the standard handler's revision with its strength: R2 RC4 40-bit, R3 RC4 128-bit, R4 128-bit RC4 or AES, R5/R6 AES 256-bit), and each permission as Allowed / Not allowed: Print, Print in high quality, Copy text, Modify the document, Add or change annotations, Fill in forms, Assemble pages. (Extraction for accessibility is not listed: PDF 2.0 removed the bit, and screen readers are always served.) A document opened with its owner password shows "Opened with the owner password: no restrictions apply".
  - **Fonts:** names of the fonts the document draws with, whether each is embedded and whether it is a subset, read from the first 100 pages within 1.5 s. When that did not cover the whole document, the row says "Fonts on the first N pages".
- **Owner restrictions are respected** (P0): when the effective permissions do not allow copying (bit 5 of /P, after the owner password if one was used), Ctrl+C does nothing but show a banner "The document's owner has not allowed copying text", the selection can still be made and the text is still offered to screen readers (accessibility extraction is always allowed). Printing, editing and form restrictions have nothing to disable yet; M3/M4 read the same flags.
- **Engine data:** two engine messages, both from the pool engine so drawing is never held up. `GetDocumentInfo` (already there) gives the text fields and flags; the new `GetDocumentDetails` (schema 4) gives dates, security revision, user permissions, form type and fonts. All strings are cut at the schema's byte limit and passed through `DisplayText.Clean`; dates are parsed by `PdfDate`, which never throws; font names are limited to printable ASCII by the engine.
- **Failure:** if the engine cannot answer (crash, timeout) the dialog shows what it has and "Some details could not be read" for the rest.
- **File size** comes from the file system in the broker, not from the engine.

### Accessibility
- The dialog (`PropertiesDialog`) has a title; each row is a `StackPanel` named "Label: value" with id `Prop_<Name>` (`Prop_Title`, `Prop_Author`, `Prop_Pages`, `Prop_PageSize`, `Prop_Version`, `Prop_Tagged`, `Prop_Created`, `Prop_Encryption`, `Prop_Perm_Copy`, `Prop_Fonts`...), so Narrator reads one thing per row. Values are selectable text. Focus starts on the dialog's first row; Tab reaches Close.

### Acceptance criteria
- **P1** `GetDocumentDetails` has a golden request and reply, malformed-frame vectors for every field limit (string too long, array too long, parallel arrays unequal, value out of range), and both the C# and C++ decoders accept and reject the same frames. (protocol, C++ and C# tests)
- **P2** The engine reports creation and modification dates, security revision, user permissions, form type and a font list for a test document, within the schema limits. (engine unit)
- **P3** The engine truncates over-long metadata and font names, replaces non-printable font-name bytes, and reports fonts as incomplete when the budget or page cap stops the scan. (engine unit)
- **P4** Through the sandbox, `GetDocumentDetails` returns the same through the broker, a restart in the middle changes nothing for the caller, and a reply with unequal font arrays is rejected as a protocol violation. (integration)
- **P5** `PdfDate.TryParse` handles `D:YYYY`, partial forms, `Z`, `+hh'mm'`, `+hh`, no zone, junk, and out-of-range fields without throwing. (unit)
- **P6** `PdfPermissions` reads each bit, treats an unencrypted document as everything allowed, and a revision-2 document as having only the four defined bits. (unit)
- **P7** `DocumentReader.GetPropertiesAsync` combines info and details, cleans text, rejects hostile windows (unequal arrays), and degrades to partial properties when details fail. (unit)
- **P8** Paper names match within a point and not otherwise. (unit)
- **P9** Ctrl+D opens a dialog whose rows show the document's title, author, page count, page size, version, creation date in the local format, producer, encryption and fonts; Close and Esc dismiss it. (UI journey)
- **P10** A document that does not allow copying (PDFium's `encrypted.pdf`, opened with its password) shows "Not allowed" for Copy text, and Ctrl+A, Ctrl+C leaves the clipboard unchanged and shows the banner. (UI journey)
- **P11** The row names and ids exist. (UI journey)

---

## 7. Full screen

### Behaviour
- **F11** toggles full screen (`AppWindow` full-screen presenter). The title bar, the command bar and the sidebar are hidden; the pages take the whole monitor. The find bar and banners are overlays and still work (Ctrl+F in full screen opens the find bar).
- **Esc** leaves full screen. If the find bar is open, Esc closes it first; a second Esc leaves.
- On entering, a banner announces "Full screen. Press Esc or F11 to leave." (a polite live region, closes itself after 4 s). The sidebar and find state are restored on leaving.
- The mouse is not hidden and the chrome does not auto-reveal (the auto-hiding controls of §10.1 come with the M3 shell redesign). The window state is not saved.
- Opening a document, an error dialog or the password dialog while in full screen works as before.

### Acceptance criteria
- **S1** F11 makes the window cover the monitor and removes the command bar from the UIA tree; F11 again restores both. (UI journey)
- **S2** Esc leaves full screen. (UI journey)
- **S3** With the find bar open, the first Esc closes it and the window stays full screen. (UI journey)
- **S4** The announcement banner appears on entering. (UI journey)

---

## Localisation strings added
`Nav_Back`, `Nav_Forward` (tooltips), `Zoom_ActualSize`, `PageColour*`, `Find_WholeWord*`, `Find_Regex*`, `Find_RegexInvalid`, `Find_RegexTooComplex`, `Find_RegexUnsupported`, `Find_CountFailed`, `Find_NoneFailed`, `Find_MatchesFailed`, `Properties_*` (title, labels, values, permissions, errors), `Banner_CopyBlocked_*`, `Banner_FullScreen_*`. The exact list is `Resources.resw`.

## Telemetry, journal, save, undo
None. No telemetry exists. Nothing here writes to the document, so there is no undo, no journal entry and no save behaviour. The only persisted state is the page colour (private store) and nothing about history.

## Risks and limits (honest)
- Photographs are inverted with the page in dark mode (see 5).
- Named actions stay unfollowed (see 2).
- Actual size depends on Windows knowing the monitor's physical size; a wrong EDID gives a wrong inch.
- Fonts are listed for the first 100 pages only, within 1.5 s; PDFium has no document-level font list.
- Regex runs on the text as PDFium orders it, so a pattern spanning columns follows PDFium's order, not reading order.

## Test map

| Criterion | Test (file) | Ran |
|---|---|---|
| H1 to H6 | `NavigationHistoryTests` (`tests/MikePDF.Core.Tests/ReaderGapsTests.cs`) | yes |
| H7 to H9 | `ReaderGapsJourneyTests.H7_H8_H9_...`, `H8_GoToPageAThumbnailAndABookmarkAreJumpsToo` | no (UI) |
| H10, H11 | `ReaderGapsJourneyTests.H10_...`, `H11_...` | no (UI) |
| N1, N2, N3, N4, N5, N6, N7 | `NamedDestinationTest.*`, `NamedDestinationHostile.*` (`tests/MikePDF.Engine.Tests/reader_gaps_tests.cpp`) | yes |
| N1, N3, N4 (through the sandbox) | `ReaderGapsServiceTests.N1_N3_N4_...` | yes |
| N8 | `ReaderGapsJourneyTests.N8_...` | no (UI) |
| Z1, Z2 | `ZoomKeyTests` | yes |
| Z3, Z4, Z5 | `ReaderGapsJourneyTests.Z3_Z4_...`, `Z5_...` | no (UI) |
| Z6, Z7 | existing `JourneyTests.RotatingTheViewTurnsThePagesWithoutChangingThePage`, `ThePageIndicatorFollowsKeyboardNavigation` | not re-run after this change |
| F1 | `ReaderGapsServiceTests.F1_...` | yes |
| F2 to F7 | `RegexFindTests` | yes |
| F8, F9, F10 | `ReaderGapsJourneyTests.F8_F10_...`, `F8_APatternThatIsNotValid...`, `F9_...` | no (UI) |
| C1 to C4 | `PageColourTests` | yes |
| C5 to C7 | `ReaderGapsJourneyTests.C5_C6_C7_...` | no (UI) |
| P1 | `GoldenTests`, `MalformedFrameTests` (C#), `ProtocolGolden.*` (C++) with the new vectors | yes |
| P2, P3 | `DocumentDetails.*` (`reader_gaps_tests.cpp`) | yes |
| P4 | `ReaderGapsServiceTests.P4_*`, `EngineClientValidationTests` (six new forged-reply scenarios) | yes |
| P5 to P8 | `PropertiesTests` | yes |
| P9 to P11 | `ReaderGapsJourneyTests.P9_P11_...`, `P10_...` | no (UI) |
| S1 to S4 | `ReaderGapsJourneyTests.S1_S4_...` | no (UI) |
