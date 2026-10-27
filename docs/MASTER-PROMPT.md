# Fable Master Build Prompt: Quire, a Windows-Native PDF Reader

> **Working name:** Quire (rename freely)
> **Target:** Production-quality, Windows-native PDF reader and light editor, built on WinUI 3 and PDFium
> **Build team:** Fable orchestrating Claude Opus (architecture, review, release authority) and Claude Sonnet (implementation, tests, docs)
> **Document version:** 1.0, 27 October 2026

---

## 0. Operating instruction

You are the autonomous principal engineering team responsible for designing, implementing, testing, securing, documenting, packaging, and shipping **Quire**, a production-quality PDF application for Windows 11.

You are **not** building a prototype, a demo, or a proof of concept. You are building software that a business could deploy to 10,000 managed devices and that a demanding power user would pick over Adobe Acrobat Reader for daily use.

### 0.1 Model responsibilities

| Responsibility | Owner |
|---|---|
| Architecture, process model, IPC protocol, threading model | **Opus** |
| Security design, threat model, sandboxing, fuzzing strategy | **Opus** |
| PDF spec interpretation (ISO 32000-2), edge-case rulings | **Opus** |
| Test strategy, corpus design, acceptance thresholds | **Opus** |
| Code review of every PR touching engine, IPC, save, signing, redaction | **Opus** |
| Milestone exit-gate assessments and the final release decision | **Opus** |
| Feature implementation, refactoring, UI, view models | **Sonnet** |
| Unit, integration, UI, and visual regression test authoring | **Sonnet** |
| Documentation, release notes, localisation resource files | **Sonnet** |
| Routine debugging, CI fixes, dependency bumps | **Sonnet** |

Opus is the technical decision authority. Every significant decision is written as an ADR in `docs/adr/` **before** implementation starts. Sonnet may propose ADRs; Opus approves or rejects them.

### 0.2 Non-negotiable working rules

1. **Do not claim done without evidence.** Every task closes with: tests added, tests passing, CI link or log excerpt, and a short note of what was verified manually.
2. **Do not guess APIs.** Before using any PDFium, Windows App SDK, WinUI, Win2D, or Windows AI API, confirm the signature against the headers or official docs in the repo's pinned version. If something can't be verified, write a spike, not production code.
3. **Do not stop at the happy path.** Every feature includes error handling, cancellation, malformed-input behaviour, accessibility, and localisation hooks.
4. **Do not silently reduce scope.** If a requirement proves infeasible, write an ADR explaining why, propose the nearest viable alternative, and flag it in the milestone report.
5. **Never write user data destructively.** Saves are atomic. Original files are never truncated in place. Crash recovery exists before any write feature ships.
6. **Security beats convenience.** Untrusted PDF parsing never happens in the UI process.
7. **Small, reviewable PRs.** Target under 600 changed lines excluding generated code and test fixtures.
8. **Keep a running `docs/STATUS.md`** with current milestone, open risks, blocked items, and the next three actions.

### 0.3 Definition of "production quality" for this project

Quire is production quality only when **all** of these are true:

- Opens and renders at least **99.5%** of the regression corpus without crash, hang, or blank page (see §12).
- Visual parity with the PDFium reference renderer (`pdfium_test` or Chromium) at an SSIM of **≥ 0.98** on at least 98% of corpus pages.
- **Zero** known crashes in the UI process from malformed input. Engine-process crashes are contained, reported, and recovered without losing user work.
- Annotations, form values, and ink saved by Quire open correctly in Adobe Acrobat Reader, Microsoft Edge, Chrome, and Firefox.
- Passes accessibility review: Narrator, keyboard-only use, high contrast, text scaling to 225%, and Accessibility Insights FastPass with zero failures.
- Meets every performance budget in §11 on the reference hardware.
- 72-hour fuzzing run with no new unique crashes in Quire-owned code.
- Signed MSIX, auto-update tested, clean uninstall, enterprise deployment documented.

---

## 1. Product vision

### 1.1 Positioning

Quire is the **fastest, most Windows-native way to read, mark up, fill, and understand PDFs**. It competes with:

- **Adobe Acrobat Reader**: on speed, footprint, no upsell, no account wall
- **Microsoft Edge PDF viewer**: on depth (tabs, workspaces, pen, multi-document AI, power tools)
- **SumatraPDF**: on modern UI, annotation, forms, pen, and accessibility
- **Foxit / PDF-XChange**: on polish, native Fluent design, and AI features

### 1.2 Design principles

1. **Instant.** First pixel faster than the user can notice. Scrolling never stutters.
2. **Native.** Looks and behaves like a first-party Windows 11 app: Mica, Fluent, snap layouts, pen, touch, Narrator, dark mode, and accent colours.
3. **Honest saves.** What you see is what Acrobat sees after saving.
4. **Private by default.** No telemetry without opt-in. AI runs locally first. Documents never leave the device unless the user explicitly chooses a cloud model.
5. **Pen-first markup.** On a Surface, Quire should feel like paper.
6. **Keyboard-complete.** Every action is reachable from the keyboard and the command palette.

### 1.3 Explicit non-goals for v1

These are **out of scope** for v1. Don't build them, and don't partially build them:

- Editing existing body text in place (reflowing paragraphs)
- Creating PDFs from Office documents (print-to-PDF via Windows already covers this)
- Full XFA dynamic form authoring
- PDF/UA remediation and tagging editor
- Preflight, colour separations, and print production tooling
- macOS, Linux, or mobile builds
- Cloud document storage service

---

## 2. Scope by priority

### 2.1 P0: required for v1.0

**Viewing**
- Open PDF via file picker, drag-and-drop, command line, file association, Jump List, and Recent
- Continuous scroll, single page, two-page (facing, with/without cover), and presentation mode
- Zoom: fit width, fit page, actual size, 10% to 6400%, pinch, Ctrl+wheel, and marquee zoom
- Rotate view (doesn't modify the file) and rotate pages (modifies the file)
- Tabs, with tear-out to new window, and session restore
- Thumbnails sidebar with virtualisation
- Outline/bookmarks panel
- Link navigation (internal, external with confirmation, named destinations)
- Back/forward navigation history (Alt+Left/Right and mouse buttons)
- Text selection, copy, and select-all on a page
- Find in document with match highlighting, match count, case and whole-word options, and regex
- Password-protected PDFs (user password; owner-restricted permissions respected)
- Dark mode for the UI plus optional page colour inversion and sepia reading modes
- Print using the Windows print dialog, with page ranges, scaling, and duplex settings honoured
- Document properties dialog (metadata, PDF version, producer, fonts, security)
- Reopen at last position per document

**Markup and forms**
- Highlight, underline, strikethrough, and squiggly (text markup annotations)
- Sticky notes with replies
- Free text (typewriter) annotation
- Ink annotation with pen pressure, eraser, and palm rejection
- Shapes: rectangle, ellipse, line, arrow, polygon, and polyline
- Stamps (built-in set plus image stamps)
- Annotation list panel with filter, sort, and jump-to
- AcroForm filling: text, checkbox, radio, combo, list, and push buttons
- Form tab order, field validation display, and flattening on export
- Save (incremental), Save As, and Save a flattened copy

**Page operations**
- Insert pages from another PDF, delete, reorder (drag in thumbnails), rotate, and extract to a new PDF
- Merge multiple PDFs
- Split by page range, every N pages, or by bookmark

**Platform**
- MSIX packaging, x64 and ARM64 native builds
- File association for `.pdf`, and a prompt pointing the user to Windows Settings to set Quire as the default
- Explorer thumbnail provider and preview handler
- Crash recovery for unsaved annotations
- English (en-US, en-AU, en-GB) at launch, with full resource externalisation for later locales

### 2.2 P1: v1.x

- OCR to make scanned PDFs searchable (invisible text layer)
- Digital signature **validation** with a trust-status UI
- Digital signature **creation** with Windows certificate store and smart cards (PAdES B-B and B-T)
- Redaction that truly removes content, with verification (see §8.6)
- Compare two PDFs (text diff and visual diff)
- Compress/optimise (image downsampling, object streams, deduplicating fonts)
- Local AI: summarise, ask questions about a document, and explain a selection
- Multi-document workspace with AI Q&A across a folder
- Fill and Sign (simple handwritten-style signature images, clearly distinct from cryptographic signing)
- Command palette (Ctrl+Shift+P) and customisable keyboard shortcuts
- Read aloud using Windows speech synthesis

### 2.3 P2: future

- Form autofill from a user profile vault (Windows Hello–protected)
- Attachments panel and portfolio support
- Layers (optional content groups) panel
- PDF/A conversion with veraPDF validation
- Long-term validation signatures (PAdES B-LT and B-LTA)
- Static XFA rendering (subject to the engine spike in §4.3)
- Plugin model
- Microsoft Store and winget distribution with enterprise policy templates (ADMX)

---

## 3. Target platform and baseline technology

Before writing any code, confirm each version below is still current and record the pinned versions in `docs/adr/0001-platform-baseline.md`.

| Area | Choice | Notes |
|---|---|---|
| OS | Windows 11 23H2 and later | Windows 10 is out of scope |
| Architectures | x64 and ARM64 native | No x86 build. ARM64 must be truly native, not emulated |
| UI framework | WinUI 3 via Windows App SDK **2.x stable channel** | At the time of writing, 2.5.1 (16 Sept 2026) is the latest stable release. Never ship on Preview or Experimental channels |
| App language | C# on **.NET 10 (LTS)** | Evaluate Native AOT for startup; fall back to ReadyToRun if blockers appear |
| Engine host | C++20 executable using **PDFium** | Separate, sandboxed process (§5) |
| PDFium distribution | `bblanchon/pdfium-binaries` for early milestones; a self-built PDFium from source for release | The prebuilt project ships weekly builds, including Windows x64 and ARM64 packages and NuGet packages |
| 2D rendering in UI | Win2D (WinUI) `CanvasVirtualControl` or Composition surfaces | Validate Win2D's compatibility with Windows App SDK 2.x in a spike. The latest Win2D-WinUI changelog entry references Windows App SDK 1.8 |
| Ink | WinUI ink APIs or a custom ink pipeline on Win2D | Decided in spike S5 |
| OCR | Windows AI `TextRecognizer` on NPU devices; fall back to `Windows.Media.Ocr` | The Windows AI OCR APIs run only on devices with an NPU |
| Local LLM | Abstraction over Windows AI `LanguageModel`, Foundry Local, and optional cloud | See §9.4 on the Phi Silica transition |
| Packaging | MSIX, signed, with App Installer auto-update; Store later | |
| CI | GitHub Actions on Windows runners, plus a self-hosted ARM64 runner | |

### 3.1 Why not build the PDF engine from scratch

Record this as ADR-0002. Summary for the team:

- PDF parsing correctness depends on decades of malformed real-world files. PDFium is the engine Chrome uses, so it absorbs that long tail.
- PDFium is BSD-licensed, which permits closed-source commercial distribution.
- MuPDF is faster and has a cleaner API but is AGPL. It's only viable if Quire is open-sourced under AGPL-compatible terms or a commercial licence is bought from Artifex.
- `Windows.Data.Pdf` only renders. It has no text extraction, annotations, or forms.
- A from-scratch engine would consume most of the project's effort and still lose on compatibility.

Quire's differentiation is **everything around the engine**: rendering pipeline, UX, pen, save fidelity, accessibility, AI, and security architecture.

---

## 4. Mandatory feasibility spikes (Milestone 0)

Run these spikes **before** committing to the architecture. Each produces a short report in `docs/spikes/` with findings, measurements, a recommendation, and a go/no-go. Opus reviews each one.

### S1: PDFium build and API surface
- Build PDFium from source for x64 and ARM64 with: no V8 (default), V8 enabled (separate config), and XFA enabled (separate config).
- Compare with the `bblanchon/pdfium-binaries` prebuilt packages: size, exported APIs, and feature flags.
- Confirm availability of every API listed in §6.2.
- Output: build scripts, binary sizes, exported symbol diff, and a recommendation on which build ships.

### S2: Rendering pipeline throughput
- Render tiles of 256 px and 512 px at 100%, 200%, and 400% using PDFium into BGRA buffers. Transfer them to the UI process via shared memory and present them with Win2D or Composition.
- Measure: time to first tile, tiles per second, GPU upload cost, and memory per cached tile.
- Test on a low-end x64 laptop, a Snapdragon X ARM64 device, and a high-DPI 4K display.
- Output: a recommended tile size, cache policy, and presentation surface.

### S3: Process isolation and IPC
- Prototype the broker/engine split (§5) with a named-pipe control channel and a shared-memory bitmap channel.
- Measure round-trip latency for a `RenderTile` request and the cost of an engine process restart.
- Confirm that the engine runs in an AppContainer or at low integrity with no network or filesystem access beyond handles passed from the broker.

### S4: Save fidelity
- Use PDFium to add each P0 annotation type, fill each form field type, and save incrementally and as a full copy.
- Open the output in Acrobat Reader, Edge, Chrome, and Firefox. Record discrepancies.
- Confirm that the appearance streams generated satisfy viewers that don't regenerate appearances.
- Output: a fidelity matrix and a list of gaps that Quire must fill with its own appearance-stream generator.

### S5: Ink latency
- Compare the WinUI ink options with a custom Win2D ink pipeline for pen-to-pixel latency, pressure fidelity, and palm rejection on a Surface Pro.
- Output: the chosen ink stack and how ink gets converted to PDF `/Ink` annotations with appearance streams.

### S6: Windows App SDK 2.x compatibility
- Confirm Win2D, Native AOT, MSIX packaging, and the Windows AI APIs work together on the pinned Windows App SDK version.
- Output: the dependency matrix, plus any blockers and workarounds.

### S7: Accessibility of a custom-rendered surface
- Prototype a UI Automation peer for a rendered page that exposes the Text pattern with text ranges mapped to PDFium character indices.
- Validate with Narrator and Accessibility Insights.

### S8: Signature handling
- Enumerate signatures in sample signed PDFs via PDFium's signature APIs. Verify CMS signatures with .NET `SignedCms` and chain trust with the Windows certificate store.
- Prototype an incremental-update writer that adds a signature field with a `/Contents` placeholder, computes the digest over `/ByteRange`, and embeds a CMS signature.
- Output: a feasibility verdict for P1 signing.

**Milestone 0 exit gate:** all eight spike reports are approved by Opus, ADRs 0001–0010 are written, and the architecture in §5 is either confirmed or amended.

---

## 5. System architecture

### 5.1 Process model

Quire uses a multi-process architecture, modelled on how browsers isolate untrusted content.

```text
┌──────────────────────────────────────────────────────────────┐
│ Quire.App (UI process, medium integrity, .NET 10 + WinUI 3)  │
│  • Windows, tabs, panels, input, ink capture                 │
│  • View models, command system, settings, session state      │
│  • Tile compositor (Win2D / Composition)                      │
│  • Document broker: owns file handles and the save pipeline   │
│  • NEVER parses PDF bytes                                     │
└───────────────┬──────────────────────────────┬───────────────┘
                │ named pipe (control, msgpack) │ shared memory (tiles)
┌───────────────▼──────────────┐  ┌────────────▼──────────────────┐
│ Quire.Engine (per document   │  │ Quire.Engine (pool member)    │
│ or pooled; AppContainer/LowIL)│  │ thumbnails, search, OCR prep  │
│  • PDFium (single-threaded)   │  │                               │
│  • Render, text, annots, forms│  │                               │
│  • No network, no FS access   │  │                               │
└───────────────────────────────┘  └───────────────────────────────┘

Optional helpers:
  Quire.ShellExt   in-proc COM thumbnail/preview handlers → spawn a sandboxed engine
  Quire.AI         local model host process (isolated, cancellable)
  Quire.Updater    App Installer / Store update coordination
```

### 5.2 Key architectural rules

1. **PDFium is not thread-safe.** Treat each engine process as a single-threaded actor. Every PDFium call for a process runs on one dedicated engine thread with a work queue. Parallelism comes from **multiple engine processes**, not threads.
2. **The broker owns file I/O.** The UI process opens the file and passes a read-only duplicated handle, or streamed bytes, to the engine. The engine never opens paths.
3. **Engine crashes are survivable.** If an engine dies, the UI shows a non-blocking banner, respawns the engine, reloads the document from the broker, replays unsaved edits from the edit journal (§7.4), and restores scroll position.
4. **Hangs are killable.** Every engine request has a deadline. Progressive rendering uses PDFium's pausable render API so a stuck page can be cancelled. A watchdog kills an engine that misses its heartbeat for more than 5 seconds.
5. **Memory is capped.** Engine processes run inside a Job Object with a memory limit (default 2 GB per engine, configurable via policy).
6. **Edits are commands.** All document mutations are serialisable commands (`AddAnnotation`, `SetFieldValue`, `MovePage`, and so on). This gives undo/redo, crash replay, and test determinism.

### 5.3 Engine sandbox requirements

- Launch engine processes in an **AppContainer** with no capabilities. If AppContainer blocks a required feature, document it in an ADR and fall back to a low-integrity restricted token.
- Apply process mitigation policies: CFG, CET shadow stack where supported, disallow child process creation, block non-Microsoft-signed DLL loading (except Quire's own signed DLLs), disable Win32k system calls if rendering stays fully in software, and prohibit dynamic code if the V8 build isn't loaded.
- Fonts: provide system font access through a broker-mediated font provider if the sandbox blocks direct access. Validate with S1/S3.
- JavaScript (V8) is **off by default**. If enabled by policy, it runs in a separate engine flavour with the same sandbox, and the UI shows a document-level "This document contains scripts" banner.

### 5.4 IPC protocol

- Transport: named pipes with a per-session random name, ACL restricted to the current user and AppContainer SID.
- Serialisation: MessagePack with a versioned schema in `src/Quire.Protocol` (shared C# definitions plus generated C++ structs).
- Every message has `requestId`, `documentId`, `deadlineMs`, and `cancellationToken`.
- Large payloads (tiles, thumbnails, page images for OCR) go through a shared-memory ring buffer with fixed-size slots. Control messages only carry slot indices.
- All engine responses are **validated by the UI process** as untrusted: bounds-check sizes, counts, string lengths, and coordinates. A compromised engine must not be able to corrupt the UI process.
- Fuzz the protocol decoder on both sides (§12.6).

Minimum message set:

| Category | Messages |
|---|---|
| Lifecycle | `Hello`, `OpenDocument`, `CloseDocument`, `Ping`, `Shutdown` |
| Metadata | `GetDocumentInfo`, `GetPageSizes`, `GetOutline`, `GetPermissions`, `GetSignatures` |
| Rendering | `RenderTile`, `RenderThumbnail`, `CancelRender` |
| Text | `GetPageText`, `GetCharBoxes`, `HitTestChar`, `Search`, `GetStructTree` |
| Links | `GetLinks`, `ResolveDestination` |
| Annotations | `ListAnnotations`, `ApplyEditCommand`, `GetAnnotationAppearance` |
| Forms | `GetFormFields`, `FormHitTest`, `FormKeyEvent`, `FormMouseEvent`, `FormFocus` |
| Save | `SaveIncremental`, `SaveFull`, `SaveFlattened` (writes to a broker-provided handle) |
| Pages | `InsertPages`, `DeletePages`, `MovePages`, `RotatePages`, `ExtractPages` |

### 5.5 Solution structure

```text
quire/
├─ src/
│  ├─ Quire.App/                  WinUI 3 app (C#)
│  ├─ Quire.Core/                 Domain model, commands, undo/redo, journal (C#)
│  ├─ Quire.Rendering/            Tile scheduler, cache, compositor (C#)
│  ├─ Quire.Protocol/             IPC schema, generated code (C# + C++)
│  ├─ Quire.Broker/               Engine pool, sandbox launch, watchdog (C#)
│  ├─ Quire.Engine/               Engine host executable (C++20)
│  ├─ Quire.Engine.Pdfium/        Thin C++ wrapper over PDFium
│  ├─ Quire.PdfWriter/            Incremental update + signature writer (C#)
│  ├─ Quire.Signing/              CMS, PAdES, trust evaluation (C#)
│  ├─ Quire.Ink/                  Ink capture, smoothing, PDF ink conversion
│  ├─ Quire.Accessibility/        UIA peers, text pattern providers
│  ├─ Quire.AI/                   Model abstraction, RAG index, prompts
│  ├─ Quire.ShellExt/             Thumbnail + preview handlers (C++)
│  └─ Quire.Package/              MSIX packaging project
├─ tests/
│  ├─ Quire.Core.Tests/
│  ├─ Quire.Engine.Tests/         (GoogleTest)
│  ├─ Quire.Integration.Tests/
│  ├─ Quire.UI.Tests/             (WinAppDriver successor or FlaUI)
│  ├─ Quire.Visual.Tests/         (golden image + SSIM)
│  ├─ Quire.Fidelity.Tests/       (round-trip vs other viewers)
│  ├─ Quire.Perf.Tests/
│  └─ fuzz/
├─ corpus/                        (manifest only; files fetched by script)
├─ docs/
│  ├─ adr/  spikes/  threat-model/  user/  admin/
│  └─ STATUS.md
├─ build/                         (PDFium build scripts, signing, CI helpers)
└─ .github/workflows/
```

---

## 6. Engine layer (Quire.Engine)

### 6.1 Responsibilities

- Load documents from broker-provided bytes or handles using PDFium's custom file access so the engine never touches paths.
- Render page tiles at a requested scale, rotation, and clip rect, with optional annotation and form overlays.
- Extract text, character boxes, and the structure tree.
- Perform search with result rectangles.
- Execute edit commands and produce saved bytes.
- Report precise, typed errors. Never crash on malformed input (crashes are bugs; all are fuzz targets).

### 6.2 PDFium API areas to wrap

Opus must confirm each against the pinned PDFium headers in S1. Treat names as areas, not guarantees.

| Area | PDFium headers (verify) |
|---|---|
| Library init, custom file access, document load | `fpdfview.h` |
| Rendering, progressive and pausable rendering | `fpdfview.h`, `fpdf_progressive.h` |
| Text extraction, char boxes, search | `fpdf_text.h` |
| Links, destinations, bookmarks | `fpdf_doc.h` |
| Annotations | `fpdf_annot.h` |
| Form fill environment, form events | `fpdf_formfill.h` |
| Page objects, content editing | `fpdf_edit.h` |
| Saving, incremental save | `fpdf_save.h` |
| Page import, merge, n-up | `fpdf_ppo.h` |
| Flattening | `fpdf_flatten.h` |
| Structure tree (tagged PDF) | `fpdf_structtree.h` |
| Signatures (read only) | `fpdf_signature.h` |
| Thumbnails, attachments, metadata | `fpdf_thumbnail.h`, `fpdf_attachment.h`, `fpdf_doc.h` |

### 6.3 Coordinate systems

Define these in `Quire.Core/Geometry` with exhaustive unit tests. Coordinate bugs are the most common class of PDF viewer defect.

- **PDF user space:** points, origin bottom-left, honour `/MediaBox`, `/CropBox`, and `/Rotate`
- **Page space:** rotation-normalised, origin top-left, in points
- **View space:** device-independent pixels at the current zoom
- **Device space:** physical pixels (DPI-aware, per-monitor v2)

Every conversion has a unit test for each of the four page rotations, non-zero CropBox origins, and per-monitor DPI changes while dragging a window across monitors.

### 6.4 Error model

Typed results only. No exceptions cross the IPC boundary.

| Code | Meaning | UI behaviour |
|---|---|---|
| `PasswordRequired` | User password needed | Password dialog |
| `PasswordIncorrect` | Wrong password | Retry, with limit |
| `UnsupportedEncryption` | Unknown security handler | Explain, offer to open in default browser |
| `Damaged` | Unrecoverable structure | Explain, offer repair attempt |
| `PartiallyRendered` | Some page content failed | Render what we can, small warning chip |
| `Timeout` | Exceeded deadline | Retry button, auto-retry at lower quality |
| `EngineCrashed` | Engine died | Respawn and restore (§5.2) |
| `PermissionDenied` | Owner restrictions | Disable affected commands, explain why |

---

## 7. Application layer (Quire.App and Quire.Core)

### 7.1 Rendering pipeline

1. The viewport computes visible page regions plus a prefetch margin (1 screen up and down; more when scrolling quickly, based on velocity).
2. The tile scheduler requests tiles in priority order: visible at current zoom, visible at a lower-resolution fallback, prefetch, thumbnails.
3. **Never show blank space.** While high-resolution tiles load, show a scaled lower-resolution tile or the thumbnail.
4. Requests made obsolete by scrolling or zooming are cancelled immediately.
5. Tile cache: two-level LRU (GPU surfaces plus a compressed CPU cache), budgeted by available memory with a default ceiling. Evict when the window is minimised or the system signals memory pressure.
6. Zoom gestures scale existing tiles on the GPU first, then re-rasterise when the gesture settles (with a debounce of about 120 ms; tune in perf tests).
7. Annotations and form widgets render in a **separate layer** from page content so editing an annotation never re-renders page content.
8. Selection, search highlights, and hover effects are drawn by the UI as vector overlays, not baked into tiles.

### 7.2 Text selection

- Character-level hit testing via `HitTestChar` with tolerance.
- Selection follows reading order where a structure tree exists; otherwise use PDFium's text order with a column-detection heuristic (document the heuristic and test it against multi-column fixtures).
- Rectangular (column) selection with Alt+drag.
- Copy as plain text; copy with layout preserved as an option.
- Double-click selects a word, triple-click a line, quadruple-click a paragraph.

### 7.3 Command system and undo/redo

- Every user action is a command with an ID, a localised name, a default shortcut, an enablement predicate, and an icon.
- The command palette searches all commands.
- Undo/redo is per-document, unlimited within a session, and survives engine restarts via the journal.

### 7.4 Edit journal and crash recovery

- Every mutating command is appended to `%LOCALAPPDATA%\Packages\<pkg>\LocalState\journals\<docHash>.jsonl` and flushed before the command is acknowledged.
- On launch after a crash, Quire offers to restore unsaved changes per document.
- The journal is deleted after a successful save or an explicit discard.
- The journal stores no document content beyond what the user added (for example, annotation text). Document that in the privacy notes.

### 7.5 Save pipeline

1. The engine writes output to a broker-provided **temporary file handle** in the same directory as the target file (or the app's temp folder if the directory isn't writable).
2. The broker validates the output: it reopens it in a fresh engine and confirms page count, annotation count, and form values match expectations.
3. The broker atomically replaces the original using `ReplaceFileW`, preserving ACLs and alternate data streams where appropriate.
4. On failure, the original is untouched and the user gets a clear error with a Save As option.
5. Default is **incremental save** (preserves existing signatures). If an operation invalidates signatures, warn the user before saving.
6. Handle files locked by OneDrive, SharePoint sync, or other processes gracefully. Detect cloud-only placeholders and hydrate them before opening.

### 7.6 Windowing and shell

- Multiple top-level windows, each with tabs. Tabs can be torn out and merged.
- Session restore: windows, tabs, scroll positions, zoom, and panel state.
- Single-instance routing via Windows App SDK app instancing: opening a PDF from Explorer adds a tab to the most recent window (configurable).
- Jump List: recent and pinned documents.
- Snap layouts, Mica backdrop, title bar customisation with tabs in the title bar.
- Full support for per-monitor DPI v2 and moving windows between monitors with different DPI.

### 7.7 Settings

- Stored in packaged app local settings, mirrored to a JSON export for backup.
- Enterprise policy overrides via registry (`HKLM\Software\Policies\Quire`). Document each policy and ship ADMX/ADML templates in P2.
- Policy-controllable at minimum: JavaScript enablement, external link behaviour, AI features (on/off, local-only, allowed cloud endpoints), telemetry, update channel, default save behaviour, and engine memory limit.

---

## 8. Feature specifications

Every feature section below must be expanded by Sonnet into a feature spec in `docs/features/` with acceptance criteria before implementation. Opus approves.

### 8.1 Find

- Incremental search with results streamed as pages are processed; current match highlighted distinctly.
- Options: match case, whole word, regex, diacritic-insensitive.
- Results panel with page snippets.
- Searches large documents (2,000+ pages) without blocking the UI. Results appear progressively.
- Ligatures, hyphenation at line ends, and Unicode normalisation (NFC/NFKC) handled. Add fixtures for each.

### 8.2 Annotations

For each P0 annotation type:
- Create, select, move, resize, restyle (colour, opacity, thickness, font), and delete
- Author name defaults to the Windows account display name (configurable)
- Created and modified timestamps
- **Appearance streams always generated** by Quire so other viewers render them identically
- Replies and review state (Accepted, Rejected, Cancelled, Completed) for comments
- Keyboard-accessible: Tab cycles annotations on the page; Enter edits; Delete removes
- Round-trip fidelity tests against other viewers (§12.4)

### 8.3 Ink and pen

- Pressure-sensitive ink with smoothing; latency target in §11.
- Pen barrel button and eraser end mapped to configurable tools.
- Palm rejection while inking.
- Highlighter pen with multiply blend.
- "Pen mode": pen always inks, finger always scrolls.
- Convert to PDF `/Ink` annotations with generated appearance streams. Long sessions are batched into reasonable annotation objects, not one annotation per stroke.

### 8.4 Forms (AcroForm)

- Render and interact via PDFium's form fill environment.
- Field formatting (number, date, percentage) where it can be done without JavaScript; with JavaScript disabled, show raw values and flag formatting scripts in a document banner.
- Tab order honours document order or structure.
- Highlight fillable fields (toggle).
- "Clear form" and "Reset field".
- Flatten on export.
- XFA: detect it, show a clear message, and render any AcroForm fallback if present. Static XFA rendering is P2, pending S1.

### 8.5 Page organiser

- Grid view of thumbnails with multi-select, drag to reorder, and drag in pages from Explorer or another Quire tab.
- Insert blank page, insert from file, duplicate, rotate, delete, and extract.
- Every operation is undoable and journaled.
- Bookmarks and links pointing to moved pages stay correct; test it.

### 8.6 Redaction (P1, high-risk)

Redaction is a security feature. A redaction that leaves recoverable text is worse than no redaction.

- Mark areas or search-and-mark (including regex for patterns such as email addresses, phone numbers, and tax file numbers).
- Apply step **removes**: text objects (splitting partially covered text runs), vector paths, and image pixels (rewriting image streams with the region painted over) within the marked areas.
- Also remove or sanitise: metadata, hidden layers, annotations under the region, form field values, embedded files, JavaScript, and earlier incremental revisions (apply forces a full rewrite, never incremental).
- **Verification step is mandatory:** after applying, re-open the output in a fresh engine and assert that no extracted text intersects redacted regions, that the regex patterns used no longer match, and that the document has no incremental revisions. If verification fails, the save is blocked.
- Test with adversarial fixtures: text under images, white-on-white text, text in form fields, text in annotations, text in metadata, text in previous revisions, and text drawn as vector glyph outlines.

### 8.7 Digital signatures (P1)

**Validation**
- Enumerate signature fields and signature objects.
- Verify: byte-range coverage of the whole file, digest integrity, CMS signature validity, certificate chain to the Windows trust store (or an optional AATL/EUTL trust list, documented), revocation via OCSP/CRL when online, and timestamp validity.
- Detect modifications after signing by comparing revisions. Classify them as allowed (form fill, annotations per DocMDP permissions) or disallowed.
- Signature panel shows a clear, accessible status: Valid, Valid with warnings, Invalid, Unknown.

**Creation**
- Choose a certificate from the Windows certificate store, including smart cards and Windows Hello–backed keys via CNG.
- Write an incremental update with a signature field, a `/Contents` placeholder, and `/ByteRange`. Compute the digest and embed a detached CMS (PAdES B-B). Optionally add an RFC 3161 timestamp (B-T) from a configured TSA.
- Visible signature appearance with name, date, reason, and optional image.
- Validate the output with Quire's validator and record cross-checks against Acrobat Reader in the fidelity suite.

### 8.8 OCR (P1)

- Detect image-only pages (no extractable text and dominant image coverage).
- Use Windows AI `TextRecognizer` where available (NPU devices). Fall back to `Windows.Media.Ocr` on other devices. The Windows AI OCR APIs provide word and line bounding polygons and confidence scores.
- Write an invisible text layer (render mode 3) aligned to recognised word boxes.
- Background, cancellable, progress per page, never blocks reading.
- Language selection based on installed OCR languages.
- Accuracy test set with ground truth (§12.3).

### 8.9 Compare (P1)

- Text diff by page with aligned paragraphs; visual overlay diff with a difference heat map.
- Synchronised scrolling of both documents.

### 8.10 Printing

- Windows print dialog via the WinUI print integration or the Windows print APIs (decide in an ADR).
- Render at printer DPI through the engine, banded to cap memory.
- Options: page ranges, odd/even, scaling (fit, actual, custom), auto-rotate, annotations on/off, form fields on/off, and print as image fallback for problem printers.
- Test with Microsoft Print to PDF, an XPS printer, and at least one physical PCL printer and one PostScript printer.

### 8.11 Explorer integration

- Thumbnail provider and preview handler in `Quire.ShellExt`, registered via the MSIX manifest (verify the exact extension schema during S6).
- Shell extensions must **not** host PDFium in-process inside Explorer. They spawn a sandboxed engine and render through IPC with a strict timeout (thumbnail: 2 s, preview first page: 1 s target).

---

## 9. AI features (P1)

### 9.1 Principles

- **Local first.** The default provider is on-device. Cloud providers are opt-in per user, and can be disabled by policy.
- **Grounded answers only.** Every answer cites page numbers, and clicking a citation jumps to the highlighted source passage.
- **No hidden data movement.** Before any cloud call, the UI shows which provider is used and what will be sent. Policy can force local-only.
- **Documents are untrusted input.** Treat all document text as potentially containing prompt injection (§9.5).

### 9.2 Features

| Feature | Description |
|---|---|
| Summarise | Whole document or page range, with length options and citations |
| Ask this document | Chat panel grounded in the open document |
| Explain selection | Right-click a selection for an explanation, translation, or definition |
| Ask across a workspace | Q&A across a user-chosen folder of PDFs (P1.x) |
| Smart find | Natural-language search that returns ranked passages |
| Form assist (P2) | Suggest field values from the user's profile vault, with explicit confirmation per field |

### 9.3 Retrieval pipeline

1. Extract text per page (with OCR text where available), keeping page and char-box references.
2. Chunk by structure (headings and paragraphs from the structure tree where tagged; layout heuristics otherwise). Target 300–800 tokens with overlap.
3. Embed with a local embedding model. Store in a per-user local index (SQLite with a vector extension, or a file-based index; decide in an ADR).
4. Retrieve top-k with hybrid scoring (BM25 plus vector), re-rank, and assemble a prompt that includes chunk IDs.
5. The model must answer with chunk IDs. The UI maps them back to page and rectangle highlights.
6. If retrieval confidence is low, the answer says so instead of guessing.
7. Evaluate Windows App SDK **App Content Search** for the index. It reached the stable channel in Windows App SDK 2.5.1 as a Limited Access Feature that requires a token. Record the decision in an ADR.

### 9.4 Model provider abstraction

Define `IChatModel`, `IEmbeddingModel`, and `IModelCatalog` with these providers:

- **Windows AI `LanguageModel`** (on-device). Microsoft has announced that Phi Silica is being replaced by a new on-device model, Aion Instruct, which begins rolling out to Insider devices in November 2026 and retail devices in January 2027, and won't need Limited Access tokens. **Do not hard-code Phi Silica behaviour.** Feature-detect the model at runtime, keep prompts model-agnostic, and run the AI evaluation suite (§12.8) against whichever model is active.
- **Foundry Local** or another local runtime for devices without a supported NPU/GPU (verify current availability in an ADR).
- **Azure OpenAI / Azure AI Foundry** (opt-in, enterprise endpoint configured by policy).
- **Any OpenAI-compatible endpoint** (opt-in, advanced setting).

Every provider supports streaming, cancellation, token budgeting, and a capability descriptor (context length, supports JSON output, and so on).

### 9.5 Prompt-injection defences

- Document text is placed in clearly delimited data blocks, and the system prompt states that instructions inside documents must be ignored.
- The AI layer has **no tools that act**. It cannot save, delete, send, browse, or open links. It only returns text and chunk IDs.
- Output is rendered as plain text plus Quire-controlled citation chips. No HTML, and no auto-opened links.
- A red-team fixture set of PDFs with embedded injection attempts is part of the AI evaluation suite (§12.8).

---

## 10. UX, accessibility, and localisation

### 10.1 Visual design

- Fluent 2 design language, WinUI 3 controls, Mica/Mica Alt, Segoe UI Variable, and Segoe Fluent Icons.
- Light, dark, and high contrast themes. Follow system theme by default.
- Layout: title-bar tabs; a compact command bar; a collapsible left pane (thumbnails, outline, annotations, attachments, signatures, layers); a right pane for comments, AI, and properties.
- Reading mode: hides all chrome (F11 or a button), with auto-hiding controls.
- Empty state: recent files grid with thumbnails, pinned files, and drag-and-drop target.
- Ship a design spec in `docs/design/` with screen-by-screen wireframes before implementing the UI. Opus reviews it for consistency with Windows 11 inbox apps.

### 10.2 Keyboard

Match common reader conventions so users can switch without relearning. Minimum set:

| Action | Shortcut |
|---|---|
| Open / Save / Save As / Print | Ctrl+O / Ctrl+S / Ctrl+Shift+S / Ctrl+P |
| Find / next / previous | Ctrl+F / F3 or Enter / Shift+F3 |
| Zoom in / out / reset | Ctrl+= / Ctrl+- / Ctrl+0 |
| Fit width / fit page | Ctrl+2 / Ctrl+1 (configurable) |
| Go to page | Ctrl+G |
| Next / previous page | Page Down, Right / Page Up, Left |
| First / last page | Home / End |
| Back / forward | Alt+Left / Alt+Right |
| Rotate view | Ctrl+Shift+= / Ctrl+Shift+- |
| Command palette | Ctrl+Shift+P |
| Highlight selection | Ctrl+H (configurable) |
| Close tab / reopen closed tab | Ctrl+W / Ctrl+Shift+T |
| Full screen / presentation | F11 / Ctrl+L |

All shortcuts are remappable and exportable.

### 10.3 Accessibility requirements

- UI Automation for every control, with names, roles, states, and keyboard focus visuals.
- The page surface exposes the **Text pattern** so Narrator, JAWS, and NVDA can read, navigate by line/word/character, and announce selection.
- Reading order uses the structure tree when the PDF is tagged. Otherwise use the reading-order heuristic and announce "This document is not tagged" once.
- Annotations, links, and form fields are exposed as UIA elements in logical order with their accessible names.
- Respect text scaling, reduced motion, and high contrast (UI chrome adapts; an optional page "high contrast" render mode remaps colours).
- Target compliance with **WCAG 2.2 AA** for all UI chrome.
- Automated checks with Accessibility Insights for Windows in CI where possible, plus a manual screen-reader script per milestone (§12.7).

### 10.4 Localisation

- All strings in `.resw` resources, with no concatenated sentences and full pluralisation support.
- Pseudo-localisation build in CI to catch truncation and hard-coded strings.
- Right-to-left layout support tested with a pseudo-RTL build, even if no RTL language ships in v1.
- Dates, numbers, and units follow the user's regional settings.

---

## 11. Performance budgets

Reference hardware (record exact models in an ADR):
- **R1 low-end:** x64, 4 cores, 8 GB RAM, integrated graphics, SSD
- **R2 ARM64:** Snapdragon X–class Copilot+ PC, 16 GB RAM
- **R3 high-end:** x64, 8+ cores, 32 GB RAM, discrete GPU, 4K display

Budgets are measured on R1 unless stated. Opus may revise numbers after spike S2, with an ADR explaining why.

| Metric | Budget |
|---|---|
| Cold start to interactive empty window | ≤ 600 ms (R1), ≤ 350 ms (R3) |
| Cold open of a 1-page PDF from Explorer to first rendered pixel | ≤ 800 ms |
| Warm open (app running) to first rendered pixel, typical 20-page PDF | ≤ 250 ms |
| 2,000-page PDF: first page visible | ≤ 1.0 s |
| Scroll frame rate on typical text PDF | 60 fps sustained on R1; display refresh rate on R3; no frame over 50 ms during a 10 s fling |
| Blank area visible while scrolling | None; low-res placeholder always present |
| Zoom gesture response | Next frame (GPU scaling); sharp tiles within 300 ms after the gesture settles |
| Search, 500-page text PDF, full results | ≤ 3 s; first result ≤ 300 ms |
| Ink pen-to-pixel latency | ≤ 25 ms on Surface Pro–class hardware (validate in S5) |
| UI process memory, idle with one 20-page document | ≤ 200 MB |
| Engine process memory, 500-page document after full scroll | ≤ 600 MB (cache-bounded) |
| Install size (MSIX, single architecture) | ≤ 60 MB excluding optional AI models |
| Engine crash to restored document | ≤ 2 s |

Performance tests run in CI on every PR against R1-class runners (with variance tolerance) and nightly on real reference hardware. A regression of more than 10% on any budget metric fails the build.

---

## 12. Testing strategy

Testing is the main thing that turns AI-generated code into production software. Treat the test infrastructure as a first-class product. Build it in Milestone 1, not at the end.

### 12.1 Test pyramid

| Layer | Tooling | Scope |
|---|---|---|
| Unit (C#) | xUnit or MSTest, FluentAssertions, coverage via coverlet | Core, geometry, commands, journal, protocol, signing, AI orchestration |
| Unit (C++) | GoogleTest | Engine wrapper, IPC codec, writer helpers |
| Integration | xUnit with real engine processes | Open/render/edit/save flows through the real IPC boundary |
| UI automation | FlaUI or Appium with the current Windows driver (decide in ADR) | End-to-end user journeys |
| Visual regression | Golden images plus SSIM and per-pixel diff | Rendering, annotations, themes |
| Fidelity | Cross-viewer round-trip harness | Saved output in other viewers |
| Performance | BenchmarkDotNet, ETW traces, custom frame timing | Budgets in §11 |
| Fuzzing | libFuzzer (engine and C++ codec), SharpFuzz (C# codec and writer) | Malformed input |
| Accessibility | Accessibility Insights, scripted Narrator checks | §10.3 |
| AI evaluation | Custom eval harness | §12.8 |

Coverage targets: **≥ 85% line coverage** for `Quire.Core`, `Quire.Protocol`, `Quire.PdfWriter`, and `Quire.Signing`; **≥ 70%** elsewhere. Coverage is a floor, not the goal. Opus reviews whether tests assert meaningful behaviour.

### 12.2 PDF corpus

Build `corpus/manifest.json` listing source, licence, hash, and tags for every file. Never commit corpus files whose licences forbid redistribution. Fetch them in CI from a private storage account instead.

Sources to evaluate (verify licences before use):
- PDFium's own test corpus and pdf.js test files
- veraPDF and Isartor test suites (PDF/A conformance)
- PDF Association sample files and the Arlington PDF Model for structural validation
- Public-domain government documents (for example, the GovDocs1 corpus)
- SafeDocs / Common Crawl PDF datasets for real-world breadth
- A curated **"nasty" set**: broken xref tables, linearised files, huge pages, 10,000-page files, JBIG2 and JPX images, Type 3 fonts, CJK and Arabic text, vertical writing, transparency groups, shading types 1–7, encrypted files (RC4, AES-128, AES-256), and incremental updates with dozens of revisions
- A **business set**: invoices, contracts, bank statements, tax forms (including Australian Taxation Office forms), government forms with AcroForm fields, scanned documents, and slide decks exported from PowerPoint

Tiers:
- **Smoke** (≈ 200 files): every PR
- **Core** (≈ 5,000 files): nightly
- **Full** (50,000+ files): weekly and before every release

### 12.3 Rendering and OCR correctness

- Render every page of the corpus at 1x and 2x with Quire and with the reference renderer.
- Compute SSIM and the percentage of pixels changed. Thresholds per §0.3.
- Any page below threshold opens an auto-triaged issue with a side-by-side diff image.
- OCR: a ground-truth set of at least 300 scanned pages across fonts, qualities, and languages. Track word error rate and alignment of boxes to text. Fail on a regression of more than 2 percentage points.

### 12.4 Save fidelity harness

For every annotation type, form field type, page operation, and signature operation:
1. Apply the operation in Quire via the command layer.
2. Save incrementally and as a full copy.
3. Validate structure with `qpdf --check` and an Arlington-model-based checker.
4. Render the result in PDFium (fresh process), pdf.js (headless), and, on a dedicated Windows test VM, Adobe Acrobat Reader and Microsoft Edge via UI-automated screenshots.
5. Compare to the expected golden output.
6. Re-open in Quire and assert the model matches exactly (annotation properties, field values, page order).

### 12.5 UI journeys (automated)

At minimum:
1. First launch → open a PDF from Explorer → read → close → relaunch → reopens at last position
2. Open a password-protected PDF → wrong password → correct password
3. Highlight text → add a note → save → re-open → annotations intact
4. Fill a multi-page form → save → open in the fidelity harness → values intact
5. Reorder, rotate, and delete pages → undo all → redo all → save
6. Merge three PDFs → split by bookmark → verify outputs
7. Kill the engine process mid-scroll → document restores within budget
8. Kill the UI process with unsaved ink → relaunch → restore prompt → ink restored
9. Keyboard-only: open, search, navigate, annotate, save, without the mouse
10. Narrator reads a tagged PDF in reading order
11. Print to Microsoft Print to PDF → output matches the source visually
12. Ask a question in the AI panel → answer contains citations → clicking a citation jumps to the passage

### 12.6 Fuzzing

- libFuzzer targets for: document load, render page, text extraction, annotation enumeration, form interaction, save round-trip, and the IPC decoder.
- SharpFuzz targets for the C# protocol decoder, the incremental writer, and the signature parser.
- Seed from the corpus. Run continuously on a dedicated machine, with crashes de-duplicated and triaged automatically.
- Crashes inside PDFium itself are reported upstream following PDFium's security process, and Quire pins a patched build.
- Release gate: 72 hours with no new unique crashes in Quire-owned code, and every PDFium crash contained by the sandbox.

### 12.7 Manual and exploratory testing

Each milestone includes a scripted manual pass recorded in `docs/test-reports/`:
- Screen readers: Narrator, NVDA, and JAWS (if licensed)
- Pen and touch on a Surface device
- Multi-monitor with mixed DPI
- OneDrive Files On-Demand and SharePoint-synced folders
- Low-memory conditions (constrain with a Job Object)
- Network-off operation (everything except cloud AI must work)

### 12.8 AI evaluation

- A golden Q&A set of at least 200 questions over 30 documents, each with expected answer facts and source pages.
- Metrics: answer correctness (LLM-graded with a rubric, spot-checked by a human), citation precision, citation recall, refusal correctness on unanswerable questions, and latency.
- Prompt-injection red-team set: at least 50 adversarial documents. The pass condition is zero instruction-following from document content.
- Run against every supported provider and model, and record results per model. Re-run when the on-device model changes (see §9.4).

---

## 13. Security

### 13.1 Threat model

Opus writes `docs/threat-model/` using STRIDE, covering at least:

| Threat | Mitigation |
|---|---|
| Malicious PDF exploits a parser bug | Sandboxed engine, mitigation policies, fuzzing, prompt PDFium updates |
| Compromised engine attacks the UI process | Validate all IPC input, least-privilege pipe ACLs, no shared writable memory except tile slots |
| Malicious links (phishing, `file://`, UNC paths for NTLM leakage) | Confirm external links, block `file://` and UNC by default, show full URL |
| Launch actions and embedded executables | Never execute; show attachments as files to save only, with Mark-of-the-Web applied |
| JavaScript abuse | Off by default; separate engine flavour when enabled; policy-controlled |
| Redaction leakage | §8.6 verification gate |
| Signature spoofing (shadow attacks, incremental-save attacks) | Revision-aware validation, classify post-signature changes, test against known attack fixtures |
| Prompt injection via AI | §9.5 |
| Update tampering | Signed MSIX, App Installer HTTPS, signature verification |
| DLL planting | Packaged app, signed binaries, safe DLL search order |
| Local data exposure (journals, AI index) | Stored in per-user app data; AI index encryptable with DPAPI; clear-data command |

### 13.2 Secure development practices

- Compiler hardening: `/guard:cf`, `/CETCOMPAT`, `/Qspectre` where appropriate, `/sdl`, and ASLR/high-entropy VA.
- AddressSanitizer builds of the engine in CI for fuzzing and integration tests.
- Static analysis: MSVC code analysis, clang-tidy for C++, Roslyn analysers with warnings as errors for C#.
- Dependency scanning and an SBOM (SPDX or CycloneDX) generated for every build.
- PDFium updated at least monthly, and within 72 hours of any upstream security fix relevant to the shipped feature set.
- Secrets never in the repo. Code-signing keys live in a cloud HSM-backed signing service, never on developer machines.

### 13.3 Security review gates

Opus performs a written security review before merging any change to: the IPC protocol, the sandbox launch code, the save pipeline, redaction, signing, link handling, attachment handling, JavaScript enablement, and AI provider calls.

---

## 14. Privacy and telemetry

- No telemetry by default. Opt-in diagnostics only, with a plain-language explanation.
- If enabled: crash dumps (minidumps with document content excluded), anonymous performance counters, and feature usage counts. Never file names, paths, document text, or AI prompts.
- Crash reporting uses Windows Error Reporting or a self-hosted endpoint (decide in ADR). Symbols published privately.
- A privacy statement in `docs/user/privacy.md` that's accurate to the code. Opus verifies it against implementation before release.

---

## 15. Build, CI/CD, packaging, and distribution

### 15.1 CI pipeline

On every PR:
1. Restore with locked dependencies (central package management, `packages.lock.json`)
2. Build x64 and ARM64, Debug and Release
3. Run analysers with warnings as errors
4. Unit tests, C# and C++
5. Integration tests with real engines
6. Smoke corpus render and visual diff
7. Performance smoke against budgets
8. Pseudo-localisation build
9. Package an unsigned MSIX and run an install/launch/uninstall smoke test

Nightly: core corpus, fidelity harness, full UI journeys, ASan engine run, and performance on reference hardware.

Weekly and pre-release: full corpus, 72-hour fuzzing, AI evaluation suite, and the manual test pass.

### 15.2 PDFium supply chain

- Pin the PDFium revision in `build/pdfium.lock` with the Chromium branch number and commit hash.
- For release builds, build PDFium from source in CI with Quire's chosen GN args (recorded in the repo), the same compiler hardening flags, and reproducible build settings.
- Generate third-party notices automatically from PDFium's bundled dependencies.
- A weekly job builds the latest PDFium branch, runs the core corpus, and opens a PR if everything passes.

### 15.3 Packaging

- MSIX with a stable package identity, signed with an EV or trusted code-signing certificate.
- Separate x64 and ARM64 packages bundled in an MSIX bundle.
- Distribution channels: App Installer file over HTTPS with auto-update (v1.0), Microsoft Store and winget (P2), and an enterprise offline package with deployment documentation for Intune and Configuration Manager.
- File type association for `.pdf` with proper icons; protocol activation `quire://` for internal use only (validated input).
- Clean uninstall: no leftover files outside package data, except user-created documents.

### 15.4 Release process

- Semantic versioning. Channels: Canary (internal), Beta, Stable.
- Release checklist in `docs/release-checklist.md` covering all of §0.3.
- Staged rollout for auto-updates, with the ability to halt.
- Every release has release notes, an SBOM, a third-party notices file, and symbol upload.

---

## 16. Licensing and compliance

- Quire's own code licence: decide in ADR-0003 (proprietary or open source). This decision constrains dependencies.
- **Do not take AGPL or GPL dependencies** unless the licence ADR explicitly allows it. This excludes MuPDF, iText, and Ghostscript in a proprietary build.
- PDFium is BSD-3-Clause, and its third-party dependencies (for example FreeType, libjpeg-turbo, OpenJPEG, Little CMS, zlib, Abseil) carry their own licences. All must be listed in notices.
- Corpus files: only redistribute files whose licences allow it.
- Fonts: bundle only fonts with redistribution rights.
- Trademarks: don't use "Adobe", "Acrobat", or other brand names in product UI or marketing except factual compatibility statements reviewed for accuracy.
- Export compliance for cryptography in signing features: document it in the release checklist.

---

## 17. Milestones and exit gates

Each milestone ends with a **milestone report** (§19) and an Opus exit-gate review. Don't start the next milestone until the gate passes. If a gate fails, the report lists what failed and the remediation plan.

### M0: Feasibility (spikes S1–S8)
**Exit gate:** spike reports approved; ADRs 0001–0010 written; architecture confirmed or amended; pinned versions recorded.

### M1: Foundations and test infrastructure
- Repo skeleton, CI, analysers, code signing for dev builds
- PDFium build pipeline and lock file
- Engine host with sandbox, IPC, watchdog, and crash respawn
- Corpus manifest, smoke tier, visual-diff harness, perf harness, fuzz targets running
**Exit gate:** a headless CLI (`quire-render`) renders the smoke corpus through the sandboxed engine with visual diffs reported; fuzzers have run 24 hours; IPC decoder fuzzed on both sides.

### M2: Vertical slice: the fast reader
- WinUI shell, single window, one tab, continuous scroll, zoom, tile pipeline, thumbnails, outline, links, text selection, find, password documents, dark mode, recent files
**Exit gate:** all §11 viewing budgets met on R1 and R2; smoke and core corpus at ≥ 99% open success; engine-kill journey passes; Narrator can read a tagged page.

### M3: Full reader
- Tabs, multiple windows, tear-out, session restore, presentation mode, two-page modes, print, properties, file association, Jump List, shell thumbnail and preview handlers, settings, policies
**Exit gate:** UI journeys 1, 2, 7, 9, 10, 11 pass; print test matrix passes; accessibility FastPass clean; shell extensions meet their timeouts.

### M4: Markup, forms, and page organiser
- All P0 annotation types, ink and pen, annotation panel, forms, save pipeline, journal and crash recovery, page operations, merge and split
**Exit gate:** fidelity harness green for all P0 operations across PDFium, pdf.js, Acrobat Reader, and Edge; UI journeys 3–6 and 8 pass; ink latency budget met.

### M5: Hardening and v1.0 release candidate
- Full corpus, 72-hour fuzz, performance tuning, accessibility manual pass with NVDA and JAWS, pseudo-localisation fixes, threat model review, privacy review, packaging, auto-update, enterprise docs
**Exit gate:** every item in §0.3 satisfied with evidence; Opus signs the release decision record `docs/release/v1.0-decision.md`.

### M6: v1.x professional features
- OCR, signature validation then creation, redaction, compare, compress, command palette, read aloud
**Exit gate:** per-feature acceptance criteria; redaction verification suite at 100%; signature validation agrees with Acrobat Reader on the signature fixture set (all disagreements explained in writing).

### M7: v1.x AI
- Model abstraction, local index, summarise, ask, explain, cross-document workspace
**Exit gate:** AI evaluation thresholds met (set in the M7 plan by Opus, with at least: citation precision ≥ 90%, zero injection successes, correct refusal ≥ 90% on unanswerable questions).

---

## 18. Real-world acceptance scenarios

Before v1.0 ships, these must work end-to-end on R1, R2, and a Surface device, observed and recorded:

1. **Accountant:** opens a 600-page annual report from a SharePoint-synced folder, searches for "contingent liabilities", highlights three passages, adds comments, saves, and emails it to a colleague who opens it in Acrobat Reader and sees every comment.
2. **Contract review:** opens a 40-page contract, reads it in two-page mode on a 4K monitor, adds sticky notes with replies, prints pages 3–7 double-sided.
3. **Government form:** fills an AcroForm with 120 fields, tabbing through every field in order, saves, reopens, and every value is intact; the file opens correctly in Edge.
4. **Pen user:** on a Surface, annotates a 30-slide lecture PDF with pressure-sensitive ink and a highlighter in pen mode, scrolls with a finger, and undoes and redoes strokes, without lag.
5. **Paperwork assembly:** merges a cover letter, a signed scan, and three receipts; reorders and rotates pages; deletes a blank page; exports one PDF.
6. **Screen reader user:** using only NVDA and the keyboard, opens a tagged PDF, navigates by headings, reads a table, follows a link, and adds a comment.
7. **Hostile file:** opens a corpus file known to crash an older PDFium build. Quire shows an error for that document, other tabs remain usable, and nothing is lost.
8. **Offline traveller:** with networking disabled, every non-cloud feature works, including local AI where the hardware supports it.
9. **Enterprise:** IT deploys Quire via Intune with JavaScript disabled, AI set to local-only, and telemetry off. Policies are enforced and visible in the About dialog.
10. **Big file:** opens a 5,000-page, 1.2 GB scanned PDF. The first page appears within budget, memory stays within caps, and scrolling remains smooth.

---

## 19. Reporting format

### 19.1 Per-task completion note

```markdown
### Task: <id> <title>
- Changed: <files/modules>
- Tests added: <list>
- Evidence: <CI run link or pasted summary of test results>
- Manual verification: <what was checked by hand, on what hardware>
- Known limitations: <honest list, or "none">
- Follow-ups: <issue ids>
```

### 19.2 Milestone report

```markdown
# Milestone <n> Report
## Outcome
<pass/fail against the exit gate, one paragraph>
## Exit-gate evidence
| Criterion | Result | Evidence |
## Metrics
<performance budgets, corpus pass rate, coverage, fuzz hours, open bugs by severity>
## Scope changes
<ADR links for anything descoped or changed>
## Risks
<top 5, with owner and mitigation>
## Next milestone plan
```

### 19.3 Honesty rules

- If a number wasn't measured, write "not measured", never an estimate presented as fact.
- If a test was skipped, say so and why.
- If a feature works only on some hardware, state which.
- Never mark a gate as passed based on a partial run.

---

## 20. Risk register (initial)

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Win2D or another dependency lags the Windows App SDK 2.x baseline | Medium | High | Spike S6; fall back to Composition APIs with a custom swap chain |
| AppContainer blocks font access or rendering in the engine | Medium | High | Broker-mediated font provider; low-integrity token fallback with ADR |
| Appearance streams render differently in Acrobat | High | Medium | Quire-owned appearance generator; fidelity harness |
| Signature creation interop failures | Medium | High | Validate with multiple validators; ship validation before creation |
| Redaction misses hidden content | Medium | Critical | Full-rewrite apply, mandatory verification gate, adversarial fixtures |
| On-device model change (Phi Silica to its successor) alters AI quality | High | Medium | Provider abstraction; eval suite re-run on model change |
| PDFium upstream security fixes require fast rebuilds | High | High | Automated weekly build pipeline, 72-hour patch SLA |
| AI-generated code passes tests that assert too little | High | High | Opus test-quality reviews, mutation testing on core modules (Stryker.NET) |
| Performance regressions creep in | Medium | Medium | Perf tests on every PR with a 10% regression gate |

---

## Appendix A: Agent prompt pack

Use these prompts with Fable. Each one is self-contained. Always attach this master document as context.

### A.1 Kickoff prompt (Opus)

```text
You are the principal architect for Quire, defined in the attached master build prompt.
Read the entire document before acting.

Your first deliverables, in order:
1. docs/STATUS.md initialised with Milestone 0 tasks.
2. A plan for spikes S1–S8: for each, the questions to answer, the measurements to take,
   the hardware needed, the pass/fail criteria, and which tasks Sonnet will implement.
3. Draft ADR-0001 (platform baseline) with versions you have VERIFIED against official
   sources today. For anything you cannot verify, mark it "UNVERIFIED" and add a spike task.
4. A risk review of §20, adding anything missing.

Do not write production code yet. Do not claim anything is verified unless you checked it.
```

### A.2 Spike execution prompt (Sonnet, one per spike)

```text
Implement spike <S#> from the Quire master build prompt §4.
Constraints:
- Spike code lives in /spikes/<S#>/ and is never imported by production code.
- Measure exactly what §4 asks for. Report raw numbers, hardware, build config, and method.
- If an API does not exist or behaves differently from the docs, record the evidence
  (header excerpt, error, or log) rather than working around it silently.
Deliver docs/spikes/<S#>.md with: Questions, Method, Results (tables), Surprises,
Recommendation, Go/No-go, Open questions for Opus.
```

### A.3 Spike review prompt (Opus)

```text
Review docs/spikes/<S#>.md and its code.
1. Are the measurements valid (method, sample size, hardware, variance)?
2. Does the evidence support the recommendation?
3. What would make this approach fail in production?
Then write or update the relevant ADR, and either approve the spike or list the
additional experiments required.
```

### A.4 Feature spec prompt (Sonnet, reviewed by Opus)

```text
Write docs/features/<feature>.md for the Quire feature "<name>" (master prompt §<n>).
Include: user stories, detailed behaviour, keyboard and pen interactions, accessibility
behaviour (UIA roles, names, Narrator announcements), error states, edge cases (rotated
pages, CropBox offsets, encrypted docs, permissions, huge docs), undo/redo semantics,
journal entries, save behaviour, telemetry events (if opted in), localisation strings,
and numbered acceptance criteria that can each be mapped to an automated test.
Flag anything ambiguous as OPEN QUESTION instead of deciding it yourself.
```

### A.5 Implementation prompt (Sonnet)

```text
Implement <task id> from docs/features/<feature>.md for Quire.
Rules:
- Follow the architecture in master prompt §5–§7. No PDFium calls outside Quire.Engine.
- Every acceptance criterion maps to at least one automated test; list the mapping in the PR.
- Handle cancellation, malformed input, and engine crash for every async path.
- No new dependencies without an approved ADR. No GPL/AGPL code.
- Keep the PR under ~600 changed lines; split if necessary.
- Finish with the §19.1 completion note. If anything is not done, say exactly what.
```

### A.6 Code review prompt (Opus)

```text
Review PR <n> for Quire. Check, in order:
1. Correctness against the feature spec and acceptance criteria.
2. Security: untrusted input handling, IPC validation, file handling, link handling.
3. Threading: PDFium calls only on the engine thread; UI-thread affinity respected;
   no sync-over-async; cancellation honoured.
4. Test quality: would these tests fail if the feature were broken? Name at least one
   mutation that the tests would NOT catch, if any exists.
5. Accessibility and localisation.
6. Performance impact relative to §11 budgets.
Output: Approve / Request changes, with a numbered list of required changes and
separately a list of optional suggestions.
```

### A.7 Bug triage prompt (Sonnet)

```text
Triage issue <n> for Quire. Reproduce it using the minimal corpus file or steps.
Classify: crash / hang / rendering / fidelity / UX / accessibility / performance / security.
Determine whether the root cause is in Quire code or in PDFium. If PDFium, produce a
minimal repro with pdfium_test and draft an upstream bug report (do not file security
bugs publicly). Add a regression test before fixing. Report root cause in one paragraph.
```

### A.8 Fidelity investigation prompt (Opus)

```text
The fidelity harness shows Quire output differs in <viewer> for <operation>.
Compare the saved PDF objects (annotation dictionaries, appearance streams, /DA strings,
font resources, /NeedAppearances, xref and trailer of the incremental section) against
the same operation saved by the reference viewer. Identify the exact spec clause
(ISO 32000-2) and the viewer behaviour that causes the difference. Propose the fix,
the test that proves it, and any risk to other viewers.
```

### A.9 Security review prompt (Opus)

```text
Perform a security review of <component/PR> for Quire using the threat model in
docs/threat-model/. Enumerate attacker-controlled inputs, trust boundaries crossed,
and failure modes. Check for: integer overflow in size calculations, unchecked counts
from IPC, path or URL handling issues, TOCTOU in the save pipeline, privilege leaks
across the sandbox boundary, and secrets in logs. Produce findings with severity,
exploitability, and a required fix for each High or Critical.
```

### A.10 Milestone gate prompt (Opus)

```text
Assess Quire Milestone <n> against its exit gate in master prompt §17 and the
production criteria in §0.3 where applicable. Use only evidence present in the repo,
CI logs, and test reports. For each criterion: Pass / Fail / Not measured, with the
evidence link. A milestone with any Fail or Not measured on a gate criterion does not
pass. Write docs/milestones/M<n>-report.md using the §19.2 template and update STATUS.md.
```

### A.11 Anti-stall prompt (use when progress stalls)

```text
Progress on Quire has stalled. Read docs/STATUS.md and the last 20 commits.
1. State plainly what is blocked and why.
2. List what has been tried.
3. Propose three concrete options to unblock, with trade-offs.
4. Pick one, write the ADR if it changes scope or architecture, and continue.
Do not restart completed work. Do not lower the quality bar to make progress.
```

---

## Appendix B: ADR template

See [`adr/TEMPLATE.md`](adr/TEMPLATE.md).

---

## Appendix C: Initial ADR list

| ADR | Title |
|---|---|
| 0001 | Platform baseline and pinned versions |
| 0002 | PDF engine choice (PDFium) and build strategy |
| 0003 | Quire licence and dependency licence policy |
| 0004 | Process model and sandbox |
| 0005 | IPC transport and serialisation |
| 0006 | Rendering surface (Win2D vs Composition) and tile strategy |
| 0007 | Ink stack |
| 0008 | Save pipeline and atomic replace |
| 0009 | UI automation framework for tests |
| 0010 | Corpus sourcing and storage |
| 0011 | Printing approach |
| 0012 | Signature validation trust lists |
| 0013 | AI provider abstraction and local index store |
| 0014 | Crash reporting and telemetry |
| 0015 | Distribution channels and update mechanism |

---

## Final instruction to Fable

Start with Appendix A.1. Work milestone by milestone. Hold every milestone to its exit gate.
When something is hard, such as save fidelity, redaction, signatures, or accessibility of the
page surface, that's where the product earns its place, so don't simplify it away.
Report honestly, verify everything, and ship something you would trust with your own documents.
