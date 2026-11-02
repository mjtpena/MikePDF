# Feature specs for M3 and M4

- Status: Draft for Opus review. Written by Sonnet as work package W1-specs on branch `m3/feature-specs`
- Date: 2026-11-02
- Scope: A.4 feature specs for every M3 and M4 feature in the [master prompt](../MASTER-PROMPT.md) §17 (where it says "Quire", read "MikePDF", ADR-0016). Nothing outside `docs/features/` changed
- Not here: history, named destinations, find options, page colour modes, document properties and full screen (the `m2-reader-gaps.md` spec), and all visual design (`docs/design/`)

## 1. The specs

| Spec | Milestone | What it covers | Acceptance criteria |
|---|---|---|---|
| [tabs-windows-session](tabs-windows-session.md) | M3 | Tabs, windows, tear-out, session restore, tab suspension, engine budget | 31 |
| [page-view-modes](page-view-modes.md) | M3 | Single, facing and facing-with-cover modes, continuous or paged, presentation mode | 23 |
| [print](print.md) | M3 | Print options, banded rendering, permissions, the §8.10 test matrix | 25 |
| [file-association-jumplist](file-association-jumplist.md) | M3 | Activation and path validation, default-app prompt, Jump List | 21 |
| [shell-extensions](shell-extensions.md) | M3 | Explorer thumbnail provider and preview handler, ShellHost, timeouts | 26 |
| [settings-policies](settings-policies.md) | M3 | Settings store, HKLM policies, About, export and import | 30 |
| [command-palette-shortcuts](command-palette-shortcuts.md) | M3 | Command registry, palette, remappable and exportable shortcuts | 29 |
| [annotation-core](annotation-core.md) | M4 | **Extra file.** Shared model, display strategy, rules and read-side protocol for all annotation specs | 25 |
| [text-markup](text-markup.md) | M4 | Highlight, underline, strikethrough, squiggly | 24 |
| [sticky-notes](sticky-notes.md) | M4 | Notes, replies, review state, popups | 23 |
| [free-text](free-text.md) | M4 | Text box and typewriter, shared text layout | 19 |
| [ink-pen](ink-pen.md) | M4 | Pen, highlighter, eraser, pressure, palm rejection, latency, batching | 28 |
| [shapes](shapes.md) | M4 | Rectangle, ellipse, line, arrow, polygon, polyline | 19 |
| [stamps](stamps.md) | M4 | 14 built-in stamps and image stamps, sandboxed image import | 21 |
| [annotation-panel](annotation-panel.md) | M4 | List with filter, sort, jump-to, 100,000-item scale | 21 |
| [forms](forms.md) | M4 | AcroForm fields, tab order, validation display, flattening | 29 |
| [save-pipeline](save-pipeline.md) | M4 | Save, Save As, flattened copy, journal, crash recovery, writer-host messages | 37 |
| [page-organiser](page-organiser.md) | M4 | Insert, delete, reorder, rotate, extract, merge, split | 32 |

`annotation-core.md` was not on the list. Nine specs share one model, one display strategy and one set of rules, and repeating them would have let them drift apart.

## 2. How to read the specs

**Template.** Each spec follows A.4 in this order: user stories; detailed behaviour; keyboard, pen and touch; accessibility (UIA roles, names, announcements); error states; edge cases (rotated pages, CropBox offsets, encrypted documents and permissions, huge documents, hostile input); undo and redo; journal entries; save behaviour; engine and protocol additions with validation limits; localisation strings; open questions; numbered acceptance criteria. Where a section does not apply it says so and why. Section numbers differ slightly between specs.

**Telemetry.** The product has no telemetry (§14, `docs/user/privacy.md`). Every spec says "Telemetry: none". If opt-in diagnostics are added later (ADR-0014), counts only, never names or paths.

**Acceptance criteria and tests.** Each criterion has an id (`TAB-07`, `SP-16`), a statement, and a **test layer**:

| Layer | Meaning | Where it lives |
|---|---|---|
| Unit | Pure code, no processes | `tests/MikePDF.Core.Tests`, `MikePDF.PdfWriter.Tests`, `MikePDF.Protocol.Tests`; C++ in `tests/MikePDF.Engine.Tests`; new `MikePDF.Markup.Tests` and `MikePDF.Ink.Tests` |
| Integration | Real engine, writer host and broker processes, real files | `tests/MikePDF.Integration.Tests` |
| Fidelity | The §12.4 harness: save, then `qpdf --check`, fresh PDFium, pdf.js, and Acrobat Reader and Edge | new `tests/MikePDF.Fidelity.Tests` (listed in §5.5 of the master prompt; not in the repo yet) |
| UI journey | FlaUI journeys (ADR-0009) and raw `IUIAutomation` accessibility checks | `tests/MikePDF.UI.Tests` |

Qualifiers: "(a11y)" is a raw UIA assertion; "(visual)" is a screenshot compare at a theme or scale; "(perf)" records a number; "(manual-gated)" cannot run unattended; "Manual record" is a checklist in `docs/test-reports/`.

**Honesty rules (§19.3).**
- A threshold marked *proposed*, *target* or *untested* is a starting value, not a measurement.
- Where a number depends on hardware that does not exist yet (the Surface Pen run, the reference PCs), the criterion says **not measured** and stays that way until the run happens.
- Acrobat Reader and Edge cells in the fidelity harness cannot be automated until the user settles R-15. Until then they are recorded as pass, fail or **not measured**, never omitted. The M4 exit gate (§17) cannot pass without that pass (OQ-SP-10).
- Anything described as **UNVERIFIED** is an API name, behaviour or platform feature that was not checked here. The first task in the relevant work package confirms it (rule: do not guess APIs).

**Traceability (recommendation).** Each test carries its criterion id (a `[Trait("Spec", "TAB-07")]` attribute in C#, a GoogleTest name prefix in C++), and a CI script lists criteria with no test, except those marked manual. A.5 asks every PR to list the mapping; this makes it checkable. This is a suggestion only; no tooling was added.

**Resource keys.** `.resw` has no plural support (§10.4 asks for pluralisation). The specs use paired keys, `Name_One` and `Name_Other`, and ask for one convention (OQ-GLOBAL-1).

## 3. What reading the code and ADRs turned up

These affect planning more than any one feature does.

1. **There is no engine cap, and the pool engine is not shared.** ADR-0004 says one shared pool engine and at most 6 engines. `DocumentSession` starts its own document engine **and** its own pool engine, and nothing counts them. Ten tabs would be 20 sandboxed processes with up to 2 GB each. Tabs need an engine budget and suspended tabs first ([tabs-windows-session](tabs-windows-session.md) 3.8).
2. **`ReplaceFileW` (§7.5) collides with the deny-write open handle (ADR-0005 decision 6) and the engine's mapped view.** A handle without `FILE_SHARE_DELETE` blocks the replace. Whether the current flags already break Save has not been tested. Spike S10 decides; it may amend ADR-0005 and ADR-0008 ([save-pipeline](save-pipeline.md) 3.6).
3. **Storage paths in the master prompt conflict with ADR-0004 amendment A2.** §7.4 puts journals in `LocalState\journals` and §7.7 puts settings in local settings. A2 says the engine holds the package's capability and can read and write package data, so anything the broker must keep from it goes in `LocalState\private`. The specs follow A2 (OQ-SP-7, OQ-SET-3).
4. **Annotations a user creates are not in the engine's document, so the tile won't show them.** How the screen shows them, and hides originals the user deletes or moves, is a design question no ADR answers. [annotation-core](annotation-core.md) 3.3 compares four options and recommends a hybrid, conditional on spike S9. Print and save depend on the answer.
5. **PDFium's page import and page-tree save are untested.** Outlines, links to excluded pages, form fields and tags may not survive `FPDF_ImportPages`, PDFium cannot write an outline, and an incremental save of page changes has not been tried. §8.5 demands correct bookmarks and links. Spike S11 decides between PDFium operations and a page assembler in `MikePDF.PdfWriter` ([page-organiser](page-organiser.md) 1).
6. **Encrypted documents cannot be saved into.** `MikePDF.PdfWriter` has `UnsupportedEncryption`, and PDFium's behaviour on a full save of an encrypted file is not measured. Annotating and form filling an encrypted PDF would be possible in memory but not saveable. The specs block Save and offer an unprotected copy (OQ-AC-6, OQ-SP-8).
7. **Free text can only draw WinAnsi characters** without font embedding. That excludes Cyrillic, Greek, CJK, Arabic and Hebrew ([free-text](free-text.md) 3.4, OQ-FT-1). It is the largest functional gap in M4 as specified.
8. **Image stamps need an image decoder, and the UI process may not run one** (§0.2 rule 6). The specs decode in the writer-host sandbox with WIC; whether WIC works under the signature-policy mitigation there is unverified ([stamps](stamps.md) 3.3).
9. **Shell handlers need a transport nobody has proved.** The handler is a native DLL in an unsandboxed surrogate; the sandbox code is C#. S6b must show how the handler reaches a broker, and Explorer probably uses MikePDF's handlers only while MikePDF is the default PDF app ([shell-extensions](shell-extensions.md)).
10. **Ctrl+1 and Ctrl+2 are fit page and fit width in §10.2**, which blocks the browser-style "go to tab" shortcuts (OQ-TAB-5). The default for `Ctrl+Alt+digit` view-mode shortcuts is also unreviewed against AltGr.
11. **Coordination with the other W1 packages.** The command registry replaces the static accelerators that W1-reader is still adding; the registry should merge first, or W1-reader should register commands. Presentation mode builds on W1-reader's full-screen; the viewer-preference message (`GetViewerPreferences`) is shared with print; page labels are used by print, panel and organiser if W1-reader adds them.
12. **The palette is P1/M6 in the master prompt** (§2.2, §17). It is specified for M3 here on the instruction, and OQ-CMD-1 asks the user to confirm.

## 4. Spikes and ADRs the specs need

| Id | What | Needed by |
|---|---|---|
| S13 | UI API verification: `TabView` tear-out events, `UIElement.PreviewKeyDown`, Jump List API, default-app probe, `SHAddToRecentDocs` for packaged apps | tabs, palette, file association |
| S6b | Shell handler transport (pipe + on-demand launch, COM `ExeServer`, or C++ launcher); handler activation only when default; Mark-of-the-Web; R-16 follow-up | shell extensions |
| S12 and ADR-0011 | Printing candidates measured against the criteria in [print](print.md) 3.1 | print |
| S9 | Annotation display strategy: overlay cost, hidden-flag mirror, rebase, Multiply over tiles | annotation core, ink, print |
| S10 | Replace semantics: sharing flags, `ReplaceFileW` vs handle rename, ACL and Zone.Identifier preservation, OneDrive, SMB | save pipeline |
| S11 | PDFium page operations, import fidelity and incremental save of page changes; assembler cost | page organiser |
| ADR-0005 amendment | A second format for the edit blob (OQ-SP-3), the two-document limit, `ProcessKind.ShellHandler` | save pipeline, page organiser, shell |
| ADR-0008 amendment | Page operations needing full rewrite (OQ-PO-7); share-delete handles (OQ-SP-4) | save pipeline |
| Device S5 run | Surface Pro, pen, 240 fps camera | ink and the M4 exit gate |

## 5. Dependency order

The order that lets the most work run in parallel without rework. A step may start when everything above it in its column is done. "Schema" steps are the single schema owner per wave (`production-roadmap.md`).

**Before M3 work (can start now, in parallel)**
- Spikes S13, S6b, S12 (with ADR-0011).
- Spikes S9, S10, S11 can run in the same period; they gate M4, not M3.

**M3, in this order**
1. **Command registry and chord dispatcher** (palette spec section 3.1 to 3.4, plus migrating the accelerators). Everything references command ids.
2. **Settings store and policy provider** (settings spec 3.1 to 3.4). Tabs, print and shell read settings and policy.
3. **Schema v4** (one WP): `GetViewerPreferences`, `RenderBand`, `ProcessKind.ShellHandler`, shell messages, C++ client codecs.
4. Then in parallel, as separate tracks: tabs-windows-session (needs 1, 2); page-view-modes (needs the layout generalisation, then v4); print (needs 2, 3 and ADR-0011); shell-extensions (needs 3 and S6b); palette UI and shortcut remapping (needs 1, 2); policy report and About (needs 2).
5. file-association-jumplist after tabs (activations land in tabs) and S13.

**M4, in this order**
1. **Schema v5** (one WP): annotation read side, `GetTextQuads`, forms messages, `SaveDocument`, `RenderTile` flags, writer-host messages 1025 to 1027, the edit blob schema.
2. **`MikePDF.Markup` base, edit model, commands, undo, and the journal with recovery.** No write feature may ship before the journal (§0.2 rule 5).
3. **Save for annotations** (writer host `AppendAnnotations`, validation, replace) driven by the smallest annotation kind, text markup, because S4 verified its appearance shape. Needs S10.
4. **Annotation foundation UI**: overlay layer, selection, hit-testing, keyboard navigation, UIA peers. Needs S9.
5. **Annotation kinds**, mostly parallel after 3 and 4: text markup, then sticky notes and the annotation panel, shapes, free text, stamps (needs the image-import host), ink (needs the `MikePDF.Ink` library, which can start early, and the device run, which cannot).
6. **Forms**, parallel to step 5 once step 3 exists (needs `NormalizeTail` and the author-artwork rule).
7. **Print snapshot** for modified documents (after step 3).
8. **Schema v6** and the **page organiser** last, with merge and split; needs S11.
9. Fidelity harness cells for every kind are added as each kind lands; the Acrobat and Edge pass (R-15) is the user's.

## 6. Size estimates

Unit: one work package (WP) is about 600 changed lines of production and test code, excluding generated code and fixtures (§0.2 rule 7). These counts are **judgement, not measurement**; the method was to break each spec into pieces comparable to existing modules (for example `MikePDF.PdfWriter` is about 3,800 lines for parser, writer and validator, and `DocumentSession` about 1,000) and round. Treat each as plus or minus 40%. Spike and ADR packages are counted where marked.

| Spec | WPs | Breakdown |
|---|---|---|
| command-palette-shortcuts | 8 | registry, chords and dispatcher with migration (3); palette UI and search (2); remap UI, storage, export (2); a11y and journeys (1) |
| settings-policies | 7 | store and registry (2); policy provider and report (2); settings UI, export and import (2); docs lint and tests (1) |
| tabs-windows-session | 12 | spike S13 (1); tab model (2); strip, tear-out, session move (3); session store and restore (2); engine budget, suspension, shared pool (3); activation queue (1) |
| page-view-modes | 10 | spread layout generalisation (3); paged navigation (2); consumers and regression tests (2); presentation (2); viewer preferences (1) |
| print | 12 | spike S12 and ADR (1); layout, ranges, bands (2); `RenderBand` (2); print pipeline and dialog (3); progress and errors (1); snapshot (1); matrix setup and tests (2) |
| file-association-jumplist | 5 | validator and activation (1); probe and prompt (2); Jump List (1); tests (1) |
| shell-extensions | 13 | spike S6b (1); ShellHost (3); thumbnail DLL (2); preview handler (3); shell engine kind, protocol, C++ client codecs (2); manifest, policy, tests (2) |
| **M3 subtotal** | **67** | |
| annotation-core | 17 | spike S9 (1); `MikePDF.Markup` base (2); model, commands, undo (3); read-side messages (2); overlay, hit-test, selection (4); keyboard and UIA (3); mirror and rebase (2) |
| save-pipeline | 25 | spike S10 (1); journal (3); dirty state and prompts (2); `SaveDocument` and planner (2); `NormalizeTail` (3); `AppendAnnotations` and edit blob (4); validation (2); replace and recovery UI (3); special documents (2); fault-injection tests (3) |
| text-markup | 7 | `GetTextQuads` (2); geometry and appearance (2); creation UI (2); tests (1) |
| sticky-notes | 7 | icons and appearance (1); structure and threads (2); thread UI (3); tests (1) |
| free-text | 6 | metrics and layout (2); appearance and save (1); editor (2); tests (1) |
| ink-pen | 12 | `MikePDF.Ink` core (3); capture and layer (3); palm, barrel, eraser (2); batching and appearance (2); journal (1); device runs (1) |
| shapes | 7 | geometry (1); tools and editing (3); appearance (1); vertex keyboard (1); tests (1) |
| stamps | 8 | artwork and appearance (2); import host (2); library and gallery (2); placement (1); tests (1) |
| annotation-panel | 5 | model, filter, sort (1); loader (1); UI (2); a11y and tests (1) |
| forms | 12 | messages (3); overlay editors (3); tab order (1); banners and required (1); highlight, clear, reset (1); flatten (1); tests (2) |
| page-organiser | 17 | spike S11 (1); engine operations (3); grid (3); insert and extract (2); merge (2); split (2); undo and journal (2); tests (2). Add 3 to 5 if the assembler route is needed |
| **M4 subtotal** | **123** | |
| **Total** | **190** | about 114,000 lines at 600 each |

The largest single risks to these counts are the save pipeline (every other M4 spec waits on it), the page assembler decision, shell transport, and anything that needs hardware.

## 7. Every new protocol message

Existing ids: 1 to 4 lifecycle, 16 to 25 document and reading, 32 and 33 rendering, 1024 `ValidateStructure`. The sequence below keeps one schema change per wave. All engine messages require `documentId` and use windows so a reply fits one 64 KiB frame (ADR-0005 A1). Limits are in the owning spec. Every message needs golden vectors, malformed-frame tests, `ReplyChecks` rules and a fuzz harness in both languages where it has a C++ side.

| Id | Message | Process | Schema | Owner spec |
|---|---|---|---|---|
| 26 | `GetViewerPreferences` | Engine | v4 | print (used by page-view-modes) |
| 34 | `RenderBand` | Engine | v4 | print |
| 2048 | `ShellOpen` | ShellHost | v4 | shell-extensions |
| 2049 | `ShellRenderPage` | ShellHost | v4 | shell-extensions |
| 2050 | `ShellClose` | ShellHost | v4 | shell-extensions |
| n/a | `ProcessKind.ShellHandler = 3`; C++ **client** encoders and decoders in the generator | all | v4 | shell-extensions |
| 27 | `GetTextQuads` | Engine | v5 | text-markup |
| 40 | `ListAnnotations` | Engine | v5 | annotation-core |
| 41 | `GetAnnotation` | Engine | v5 | annotation-core |
| 42 | `GetInkStroke` | Engine | v5 | annotation-core (ink) |
| 43 | `SetAnnotationHidden` | Engine | v5 | annotation-core |
| 44 | `CountAnnotations` | Engine | v5 | annotation-core (panel) |
| 45 | `GetFormFields` | Engine | v5 | forms |
| 46 | `GetFormFieldDetail` | Engine | v5 | forms |
| 47 | `SetFormFieldValue` | Engine | v5 | forms |
| 48 | `FormAction` | Engine | v5 | forms |
| 62 | `SaveDocument` | Engine | v5 | save-pipeline |
| n/a | `RenderTile.flags` maximum 3 to 7 (bit 2: field highlight) | Engine | v5 | forms |
| 1025 | `AppendAnnotations` | WriterHost | v5 | save-pipeline |
| 1026 | `NormalizeTail` | WriterHost | v5 | save-pipeline |
| 1027 | `ImportStampImage` | WriterHost | v5 | stamps |
| n/a | New schema file `mikepdf.edits.json` (edit blob, not a frame) | WriterHost | v5 | save-pipeline |
| 56 | `InsertPages` | Engine | v6 | page-organiser |
| 57 | `InsertBlankPage` | Engine | v6 | page-organiser |
| 58 | `DeletePages` | Engine | v6 | page-organiser |
| 59 | `MovePages` | Engine | v6 | page-organiser |
| 60 | `RotatePages` | Engine | v6 | page-organiser |
| 61 | `CreateDocument` | Engine (pool or assembly kind) | v6 | page-organiser |
| 1028 | `AssemblePages` | WriterHost | v6, only if S11 picks the assembler | page-organiser |

Engine behaviour changes that are not messages: `EngineKind::Shell`; the document kind's limit from 1 to 2 open documents (or an assembly kind); the invariant that `SaveDocument` first restores display-mirror annotation flags.

New projects (no new third-party dependency): `MikePDF.Markup`, `MikePDF.Ink`, `MikePDF.ShellHost`, `MikePDF.ShellExt` (C++), and the test projects named in section 2. OQ-AC-7 asks whether they need an ADR.

## 8. Open questions by who must answer

Ids are in the spec that raises them. Items marked **blocks** stop implementation until answered.

### User (product, hardware, legal, money)

| Id | Question |
|---|---|
| OQ-CMD-1 | Confirm the palette moves from M6 to M3 (**blocks** palette UI scope) |
| OQ-INK-1 | The Surface Pro, pen and 240 fps camera for S5 (**blocks** the M4 ink gate) |
| OQ-SP-10 | The Acrobat Reader and Edge fidelity pass, or confirmation that automation is allowed (R-15) (**blocks** the M4 gate) |
| OQ-PRINT-7, 8, 9 | Which XPS printer stands in; who installs printers; physical PCL and PostScript printers (**block** the physical print cells) |
| OQ-SHELL-9 | Re-running Store certification once handlers return |
| OQ-TAB-1, 3, 5 | Session restore default; virtual desktops; Ctrl+1/2 conflict |
| OQ-SET-1 | Default for external links |
| OQ-AC-3, 4, 5 | Redraw foreign annotations; author-name notice; copy and paste of annotations |
| OQ-FT-1 | Free text outside WinAnsi (**blocks** claiming non-Latin support) |
| OQ-FT-6, OQ-SH-1, OQ-SH-2 | Callouts; cloudy borders; z-order |
| OQ-ST-1, 3, 4, 5 | Stamp info line; JPEG re-encode; stamp text language; free rotation |
| OQ-INK-4, 5, 8 | Barrel default; touch inking; lasso and recognition |
| OQ-PV-1, 2, 3 | Two-page continuous; laser pointer; auto-advance and transitions |
| OQ-PRINT-4, 5 | Form print option; page labels |
| OQ-FA-2, 3, 6 | Items without paths; Print verb; prompt thresholds |
| OQ-FM-3, 6, 8 | XFA fallback; flatten fields only; button GoTo |
| OQ-PO-3, 4, 5, 9, 10 | Dangling bookmarks; tab drag semantics; `+` joiner; tags; page labels |
| OQ-PNL-3, 4 | Full-text comment search; comment summary export |
| OQ-SHELL-5, 6, 7 | Preview scrolls; preview accessibility depth; hydration |
| OQ-SN-5, OQ-SP-6, OQ-CMD-6, OQ-CMD-7 | State history growth; journal retention; chord sequences; bookmark entries in the palette |
| OQ-TM-2 | Marking text when copying is forbidden (with Opus) |
| OQ-AC-2, OQ-FM-5, OQ-SP-2, OQ-SP-8 | DocMDP blocking; DPAPI for journals; encrypted-document Save (with Opus) |

### Opus (architecture, security, test)

| Id | Question |
|---|---|
| OQ-AC-1, OQ-INK-6 | Display strategy and Multiply on screen (**blocks** M4 UI) |
| OQ-SP-3 | Edit blob as a second format (**blocks** save) |
| OQ-SP-4, OQ-SP-7 | Replace mechanism after S10; journal path (**block** save) |
| OQ-SP-1 | Journal write failure: strict or degraded |
| OQ-INK-7 | Ink journal flush within 50 ms |
| OQ-PO-1, 2, 6, 7, 8 | Route A or B; document limit; journal blobs; page ops needing full rewrite; same-name fields (**block** organiser) |
| OQ-PRINT-1, 2, 3, 6 | Meaning of the image fallback; ADR-0011; snapshot; low quality |
| OQ-SHELL-1, 3, 4 | Transport; item-only registration; shell engine limits |
| OQ-TAB-2, 4, 6 | One UI thread; password in memory on suspend; live-tab cap |
| OQ-SET-2, 3, 4, 6, 7 | HKCU policy; A2 vs §7.7; reserved policies; en variants; freezing the contract |
| OQ-FM-1, 2, 4 | Structure tab order; native formatting; author artwork |
| OQ-FT-2, 3, 4 | AcroForm DR fonts; custom editor; rotated appearance |
| OQ-ST-2 | Where images are decoded |
| OQ-INK-2, 3 | Delegated ink; private width samples |
| OQ-AC-6, 7, 8 | Encrypted save; new projects ADR; annotation marker |
| OQ-PV-4, 5 | Presentation link prompt; `/PageLayout` |
| OQ-CMD-2 | Chord binding by virtual key or character |
| OQ-FA-5 | Register `mikepdf://` |
| OQ-SN-2, 3, 4 | Reply appearance; NoRotate placement; draft journaling |
| OQ-TM-1, OQ-SH-3, OQ-ST-7 | Highlight height; transparency group; non-standard stamp name (after the Acrobat pass) |
| OQ-SP-9 | Arlington checker licence |
| OQ-GLOBAL-1 | Plural resource convention |

### Design (`docs/design/`)

OQ-TM-3, TM-4, SH-4, ST-6, SN-1, PV-6, PNL-1, PNL-2, SET-5, CMD-4, FM-7, FM-9, FT-5, SP-5. These are choices of default, icon, layout or look; they don't change behaviour.

### Sonnet spikes that must produce data

S13 (UI APIs), S6b (shell transport; OQ-SHELL-2, OQ-SHELL-8), S9, S10, S11, S12, and the probes behind OQ-FA-1, OQ-FA-4, OQ-CMD-5, OQ-TAB-7 and OQ-FM-1.

### Cross-cutting

| Id | Question | Who |
|---|---|---|
| OQ-GLOBAL-1 | How plurals work in `.resw` (paired `_One` and `_Other` keys assumed) | Opus, localisation |
| OQ-GLOBAL-2 | Merge order with W1-reader: command registry first, or W1-reader registers commands | Opus |
| OQ-GLOBAL-3 | SSIM and other thresholds written as targets (0.95 to 0.98) get fixed after the first harness runs | Opus |

## 9. Riskiest items

1. **The save pipeline and file replacement.** Every M4 feature ends in it; `ReplaceFileW` may conflict with the open-file handles; Acrobat and Edge acceptance of MikePDF-written files is unmeasured; the strict parser refuses some real files (R-39). A bug here loses user work.
2. **Annotation display strategy.** The hybrid overlay-plus-hidden-flag design is a hypothesis. If the overlay and the saved appearance drift, the product shows one thing and saves another, which is the opposite of "honest saves". The mirror flag leaking into a saved file would be a silent corruption.
3. **Ink latency and palm rejection.** No measurement exists; WinUI has no ink control; delegated ink cannot be attached; highlighter Multiply on screen is unproven. The M4 gate depends on a device the project does not yet have.
4. **Page operations through PDFium.** Outlines, links, forms and incremental save of page changes are unverified; the fallback is a large new component in the most security-sensitive parser.
5. **Free text without embedded fonts, and stamps with image decoding.** One excludes most of the world's scripts; the other needs a codec inside a sandbox that restricts loading code.
6. **Tabs without an engine budget.** As written today they would multiply 2 GB engines. This is easy to fix but must be fixed first.
7. **Encrypted documents.** A common case (password-protected contracts) where markup cannot be saved.
8. **Shell handlers.** Unproven transport, only active when MikePDF is default, in an unsandboxed host, with hard time limits.
9. **Acrobat and Edge fidelity can't be automated.** The M4 gate depends on a manual pass that nobody has scheduled.
10. **Scale of the work.** About 190 work packages by this estimate, with one human reviewer (R-23). Order and parallelism in section 5 matter more than any single feature.

## 10. Consolidated v1 non-goals in these specs

Callout text boxes; cloudy borders; z-order commands; free rotation of stamps and shapes; lasso, partial stroke selection, shape recognition, handwriting to text; copy and paste of annotations; native number/date formatting in forms (Phase B, pending a ruling); XFA filling; presenter view, slide transitions, auto-advance, N-up, booklet and poster printing; content search in Explorer (IFilter); chord sequences in shortcuts; HKCU policy (pending); tagged-PDF preservation through merge and split; Fill and Sign, signing and redaction (P1).
