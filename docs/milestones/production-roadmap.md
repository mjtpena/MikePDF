# Road to production: work packages

- Date: 2026-11-02
- Instruction from the user: "everything needs to be in main. Then ensure that we have sonnet agents doing all the work to make this production ready, UI/UX superior, and the best free PDF app out there"
- Owner: Opus (plans, reviews, integrates). Sonnet agents implement.

"Production ready" means the eight §0.3 criteria in the [master prompt](../MASTER-PROMPT.md), with evidence. "UI/UX superior" means it measurably beats Acrobat Reader, Edge and SumatraPDF on the §11 budgets, keyboard completeness (§10.2) and accessibility (§10.3), with a design that follows Windows 11 inbox apps (§10.1). Some gates can't be met by agents alone (see the last section).

## How work flows
1. Each work package (WP) gets one Sonnet agent, in its own git worktree, on a branch named `m<milestone>/<wp>`. That prefix makes CI run on push.
2. The agent works in commits of about 600 changed lines or fewer. It maps every acceptance criterion to a test and ends with a §19.1 completion note.
3. Opus reviews against A.6 (correctness, security, threading, test quality, accessibility, performance), re-runs the tests, and merges to `main` through a PR once **the PR's own CI is green**. That CI runs on the merge with current `main`. Two branches that each merged cleanly broke the build together on 2026-11-02, so a branch's own CI isn't enough. After a merge that touches the app, Opus runs the UI journeys on `main`. If it isn't good enough, it goes back to the agent with a numbered list.
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

## Backlog from reviews
Found while reviewing wave 1. Each gets a work package or joins one.
| Item | Source | Goes to |
|---|---|---|
| Engine budget: each document starts its own pool engine (`DocumentSession.cs`), but ADR-0004 says one shared pool and a cap. This must be fixed before tabs | W1-specs finding 1 | Done: PR #12 (ADR-0004 A3); the cap is read from policy and pinning is in the UI in `m3/tabs` |
| The watchdog can kill an engine on a heavy page as "hung", because PDFium checks its deadline only about every 100 drawing operations. The viewer has the same defaults. Base hang detection on a heartbeat, not on the render deadline | W1-render risk 2 | Done: PR #12, progress-based hang detection (10 s stall, 90 s hard limit) |
| `EngineProtocolTests.RenderTileIsRefusedUntilTheRingIsAttachedAndTheRingAttachesOnce` is flaky on ARM64 | W1-render risk 3 | Mitigated in PR #12 (errors logged, launch retried); root cause not proven |
| Starting an engine failed 5 times out of 5 ("Hello failed ... Pipe is broken") when every core was busy; it never failed on an idle machine in 3,000 cycles. Likely a startup timeout or race in the broker or engine | W1-perf | Mitigated in PR #12 (start retried, exit code logged); not reproduced |
| Run `tools/MikePDF.Perf/measure-app.ps1` against the installed app to verify the app-level markers end to end | W1-perf | Opus, with the next UI run |
| UI tests kill every `MikePDF.Engine` on the machine | W1-render risk 1 | Done: PR #8 |
| A test helper listens on all interfaces, so a firewall prompt appears | W1-reader | Done: PR #8 |
| Integration tests in parallel worktrees share the `MikePDF.IntegrationTests` AppContainer profile and can fail each other | W1-reader | Done: PR #12, profiles named per run |
| C# fuzz targets aren't coverage-guided. Run the SharpFuzz spike in ADR-0017 | W1-fuzz | Done: PR #9 (ADR-0017) |
| Fuzzing gate: count nightly 5-hour slices per target, with the corpus carried over, toward 24 h (M1) and 72 h (M5) | W1-fuzz | Decided by Opus 2026-11-02 |
| Inverted pages also invert photographs; preserving images needs engine work | W1-reader | W2-engine-2 (running) |
| Core and full corpus tiers; a hang file and a crash file in the smoke tier; per-file licence audit of PDFium's test resources | W1-render | Done: PR #15 (core tier 1,404 files; full tier and about 5,000 core files still to do) |
| `TileSchedulerTests.APresenterFailureDoesNotKillTheScheduler` failed once on ARM64 and passed on rerun | W2-engine CI | W2-engine-2 (running) |
| About can't show the PDFium version without an open document; expose the pool engine's `Hello` reply | W2-commands | W2-engine-2 (running) |
| `PageSurface` still has its own key handler for navigation keys; move them into the command dispatcher | W2-commands | Done in `m3/tabs` (the surface keeps only Esc and Shift+arrow selection) |
| The engine memory-limit policy is read (`PolicySnapshot.EngineMemoryLimitMb`) but not applied to the broker | W2-commands | W2-engine-2 (running) |
| The pool engine's 32-document limit is now the effective limit on open tabs | W2-engine | Done in `m3/tabs`: it is 16 documents, because each holds two pool documents; a 17th open is refused with a message |
| Form field contents don't print: the engine refuses `RenderBand` flag bit 1, because widgets need `FPDF_FFLDraw`. A v1.0 blocker | W2-print-core | Next engine WP, before the print sheet |
| The print sheet UI: strings in `PrintStrings`, `print.*` settings keys for last-used values, and PRINT-13, -21 and -22 | W2-print-core | Print sheet WP, after the tabs WP |
| Print to physical, PCL, PostScript, duplex and XPS printers, ARM64 printing, and the 1,000-page job are unverified | W2-print-core | Print sheet WP (XPS, 1,000 pages); the user (physical printers) |
| The user-level policy comment says "stricter than the default", but the code compares against the least strict allowed value | W2-commands review | Wave 2 |
| Open documents are capped at 16 because each holds two pool documents (placeholders, and text and search). Share one pool document per session to reach 32 | W2-tabs | Engine WP |
| The performance gate fails on hosted runners without a code cause (3 times on 2026-11-02 and -03: every engine metric 20-30% slower at once). Measure the base and the head in the same job on the same runner instead of against a stored baseline | W2 reviews | Done in `m3/perf-gate`: same-runner A/B with a confidence interval; 0 false positives in 22 A/A jobs, 24 of 24 planted 15-20% slowdowns caught (`docs/perf/README.md`) |
| `search.first_result` and `recovery.crash_to_first_tile` on x64 hosted runners are caught 8 times in 12 at a planted 15-20%; they are short (5 to 25 ms) and noisy. Use more iterations or a longer scenario for them | W2-perf-gate | Perf WP, next time `tools/MikePDF.Perf` is touched |
| Slow drift (many pull requests of 5% each) is not gated. Read the nightly A/A and `main` runs for a month, then decide on a trend check against an older `main` build | W2-perf-gate | Opus, after the first weeks of nightly runs |
| File activation and Jump List items not built: FA-09 to FA-12, FA-15 to FA-21, and the default-app prompt | W2-tabs | File association WP |
| The test-seam gate ignores `MIKEPDF_TEST_*` in Store-signed packages; the Store path is unverified until a Store-signed package exists | W2-tabs | First Store submission (user) |
| TAB-08 (move between monitors with different DPI) and TAB-30 (high contrast, 225% text) need a second monitor and system settings | W2-tabs | The user, or a WP with an accessibility test machine |

## What only the user can do
The following gate v1.0, and agents can't do them:
- Reference hardware runs on R1, R2, R3 and a Surface with a pen (§11, R-17)
- Manual Acrobat Reader and Edge fidelity passes, unless the Acrobat EULA permits automation (R-15)
- Manual Narrator, NVDA and JAWS passes. JAWS needs a licence
- Partner Center, Store submissions, and the decision to go public
- Explorer thumbnails and preview on a standard-user (non-elevated) session, and on a machine with Acrobat installed (ADR-0018, OQ-SHELL-10 and -11)
- Whether MikePDF's thumbnails should replace another default PDF app's (OQ-SHELL-14)
- Budget for a long-running fuzz machine (R-19). GitHub-hosted jobs stop at 6 hours
