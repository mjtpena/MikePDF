# Road to production: work packages

- Date: 2026-11-02
- Instruction from the user: "everything needs to be in main. Then ensure that we have sonnet agents doing all the work to make this production ready, UI/UX superior, and the best free PDF app out there"
- Owner: Opus (plans, reviews, integrates). Sonnet agents implement.

"Production ready" means the eight §0.3 criteria in the [master prompt](../MASTER-PROMPT.md), with evidence. "UI/UX superior" means it measurably beats Acrobat Reader, Edge and SumatraPDF on the §11 budgets, keyboard completeness (§10.2) and accessibility (§10.3), with a design that follows Windows 11 inbox apps (§10.1). Some gates can't be met by agents alone (see the last section).

## How work flows
1. Each work package (WP) gets one Sonnet agent, in its own git worktree, on a branch named `m<milestone>/<wp>`. That prefix makes CI run on push.
2. The agent works in commits of about 600 changed lines or fewer. It maps every acceptance criterion to a test and ends with a §19.1 completion note.
3. Opus reviews against A.6 (correctness, security, threading, test quality, accessibility, performance), re-runs the tests, and merges to `main` through a PR once CI is green. If it isn't good enough, it goes back to the agent with a numbered list.
4. One owner at a time for shared surfaces:
   - **Protocol schema** (`src/MikePDF.Protocol/schema`): one WP per wave may change it, and it bumps `schemaVersion`.
   - **App shell** (`MainWindow*`, `PageSurface*`): one WP per wave.
   - **The desktop:** only the app-shell owner may run `build/run-ui-tests.ps1` or register a package, because the UI journeys drive the real mouse and keyboard. Everyone else runs `./build.ps1 -Test` only.
5. Agents never push to `main`, never create accounts, never submit to the Store, never change system settings or trust stores, and never add a dependency without an ADR. No GPL or AGPL code.

## Waves
### Wave 1 (now): finish the M1 gate, the M2 gaps, and the design work for M3 and M4
| WP | Branch | Scope | Owns |
|---|---|---|---|
| W1-render | `m1/render-cli` | `mikepdf-render` headless CLI through the sandboxed engine; corpus manifest and smoke tier (ADR-0010); visual-diff harness (SSIM against a PDFium reference); CI job | `tools/MikePDF.Render`, `tests/corpus` |
| W1-fuzz | `m1/fuzzing` | Fuzz targets running for real: IPC decoders on both sides (C# and C++), PdfWriter parser, engine document load; a scheduled workflow that runs in time slices and keeps the corpus; crash triage notes; the plan for the 24-hour and 72-hour runs | `tests/fuzz`, fuzz workflow |
| W1-perf | `m1/perf` | Perf harness for every §11 metric that can be measured headlessly or with ETW: cold start, first pixel, 2,000-page open, search, memory, engine-crash restore, install size; a CI job with variance tolerance; a regression check over 10% | `tools/MikePDF.Perf`, startup trace points |
| W1-reader | `m2/reader-gaps` | M2 and P0 viewing gaps: back and forward history, actual size, named destinations, find whole-word and regex, inverted and sepia page modes, document properties dialog, keyboard map completion (§10.2 rows that apply to a reader) | App shell, protocol schema |
| W1-design | `m3/design` | `docs/design/`: screen-by-screen wireframes and interaction specs for the v1.0 UI (title-bar tabs, command bar, panes, reading mode, empty state, command palette, markup tools, forms, page organiser, settings). Competitive teardown of Acrobat Reader, Edge, SumatraPDF and Foxit with specific ways to beat each | `docs/design` |
| W1-specs | `m3/feature-specs` | A.4 feature specs with numbered acceptance criteria for every M3 and M4 feature | `docs/features` |

### Wave 2: M3 full reader
This wave starts after the W1-design and W1-specs reviews.
- Tabs, multiple windows, tear-out, session restore, and Jump List
- Presentation mode, two-page modes, and single-page mode
- Printing, with the print test matrix
- Settings, enterprise policies, and the About dialog showing policies
- Explorer thumbnail provider and preview handler, sandboxed, with timeouts
- Mica, title-bar tabs, the command palette, and remappable shortcuts
- Light and high-contrast themes verified, text scaling to 225%, and Accessibility Insights FastPass in CI

### Wave 3: M4 markup, forms and page organiser
- Text markup, notes with replies, free text, ink with pressure, shapes, stamps, and the annotations panel
- AcroForm filling, tab order, validation, and flattening
- The save pipeline on PdfWriter: incremental save, Save As, and flattened copy, with the edit journal and crash recovery
- Page operations: insert, delete, reorder, rotate, extract, merge and split
- The fidelity harness across PDFium, pdf.js, Acrobat Reader and Edge. The Acrobat and Edge parts need the user (R-15)

### Wave 4: M5 hardening and the v1.0 release candidate
- Full corpus, the 72-hour fuzz run, and performance tuning on reference hardware
- Manual screen-reader passes, pseudo-localisation, and threat-model and privacy reviews
- Auto-update and enterprise deployment docs
- The release decision record, `docs/release/v1.0-decision.md`

### Later: M6 professional features and M7 AI (v1.x)
These come after v1.0 ships.

## What only the user can do
The following gate v1.0, and agents can't do them:
- Reference hardware runs on R1, R2, R3 and a Surface with a pen (§11, R-17)
- Manual Acrobat Reader and Edge fidelity passes, unless the Acrobat EULA permits automation (R-15)
- Manual Narrator, NVDA and JAWS passes. JAWS needs a licence
- Partner Center, Store submissions, and the decision to go public
- Budget for a long-running fuzz machine (R-19). GitHub-hosted jobs stop at 6 hours
