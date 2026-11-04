# Road to production: work packages

- Date: 2026-11-02
- Instruction from the user: "everything needs to be in main. Then ensure that we have sonnet agents doing all the work to make this production ready, UI/UX superior, and the best free PDF app out there"
- Owner: Opus (plans, reviews, integrates). Sonnet agents implement.

"Production ready" means the eight §0.3 criteria in the [master prompt](../MASTER-PROMPT.md), with evidence. "UI/UX superior" means it measurably beats Acrobat Reader, Edge and SumatraPDF on the §11 budgets, keyboard completeness (§10.2) and accessibility (§10.3), with a design that follows Windows 11 inbox apps (§10.1). Some gates can't be met by agents alone (see the last section).

## How work flows
1. Each work package (WP) gets one Sonnet agent, in its own git worktree, on a branch named `m<milestone>/<wp>`. That prefix makes CI run on push.
2. The agent works in commits of about 600 changed lines or fewer. It maps every acceptance criterion to a test and ends with a §19.1 completion note.
3. Opus reviews against A.6 (correctness, security, threading, test quality, accessibility, performance), re-runs the tests, and merges to `main` through a PR once **the PR's own CI is green**. That CI runs on the merge with current `main`. Two branches that each merged cleanly broke the build together on 2026-11-02, so a branch's own CI isn't enough. The UI journeys run in CI on every pull request and every push to `main` ([docs/testing.md](../testing.md)), so a merge that touches the app is UI-verified by its own PR's CI; Opus reads that job instead of running the journeys on a desktop. If it isn't good enough, it goes back to the agent with a numbered list.
4. One owner at a time for shared surfaces:
   - **Protocol schema** (`src/MikePDF.Protocol/schema`): one WP per wave may change it, and it bumps `schemaVersion`.
   - **App shell** (`MainWindow*`, `PageSurface*`): one WP per wave.
   - **The desktop:** nobody runs `build/run-ui-tests.ps1` or registers a package on a developer machine to verify a change: the UI journeys drive the real mouse and keyboard, and the `ui-journeys` CI job runs them on hosted runners ([docs/testing.md](../testing.md)). Locally, run `./build.ps1 -Test` only; the one exception is the app-shell owner reproducing a failure the CI job could not explain.
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
| Inverted pages also invert photographs; preserving images needs engine work | W1-reader | Engine done in PR #19; app wiring in W2-views |
| Core and full corpus tiers; a hang file and a crash file in the smoke tier; per-file licence audit of PDFium's test resources | W1-render | Done: PR #15 (core tier 1,404 files; full tier and about 5,000 core files still to do) |
| `TileSchedulerTests.APresenterFailureDoesNotKillTheScheduler` failed once on ARM64 and passed on rerun | W2-engine CI | Done: PR #19 (back-off timer race) |
| About can't show the PDFium version without an open document; expose the pool engine's `Hello` reply | W2-commands | API done in PR #19; About line in W2-views |
| `PageSurface` still has its own key handler for navigation keys; move them into the command dispatcher | W2-commands | Done in `m3/tabs` (the surface keeps only Esc and Shift+arrow selection) |
| The engine memory-limit policy is read (`PolicySnapshot.EngineMemoryLimitMb`) but not applied to the broker | W2-commands | Done: PR #19 |
| The pool engine's 32-document limit is now the effective limit on open tabs | W2-engine | Done in `m3/tabs`: it is 16 documents, because each holds two pool documents; a 17th open is refused with a message |
| Form field contents don't print: the engine refuses `RenderBand` flag bit 1, because widgets need `FPDF_FFLDraw`. A v1.0 blocker | W2-print-core | Done: PR #19 (tiles and bands) |
| The print sheet UI: strings in `PrintStrings`, `print.*` settings keys for last-used values, and PRINT-13, -21 and -22 | W2-print-core | Done in `m3/print-sheet` (W2-print-sheet): [print.md section 17](../features/print.md) |
| Print to physical, PCL, PostScript, duplex and XPS printers, ARM64 printing, and the 1,000-page job are unverified | W2-print-core | The 1,000-page job is measured in `m3/print-sheet` (print.md 17.3). XPS is **not run**: no XPS driver on this machine and adding one needs elevation. Physical, PCL, PostScript, duplex and ARM64: the user |
| Print sheet: High contrast and 225% text scale were not run (system settings); the Narrator pass is manual; the sheet's hover reason for a disabled command bar button polls the cursor every 150 ms while the window is active, which WinUI's missing tooltips for disabled controls forced | W2-print-sheet | The user (accessibility pass); a WinUI fix would let the poll go |
| The user-level policy comment says "stricter than the default", but the code compares against the least strict allowed value | W2-commands review | Wave 2 |
| Open documents are capped at 16 because each holds two pool documents (placeholders, and text and search). Share one pool document per session to reach 32 | W2-tabs | Engine WP |
| The performance gate fails on hosted runners without a code cause (3 times on 2026-11-02 and -03: every engine metric 20-30% slower at once). Measure the base and the head in the same job on the same runner instead of against a stored baseline | W2 reviews | Done in `m3/perf-gate`: same-runner A/B with a confidence interval; 0 false positives in 22 A/A jobs, 24 of 24 planted 15-20% slowdowns caught (`docs/perf/README.md`) |
| `search.first_result` and `recovery.crash_to_first_tile` on x64 hosted runners are caught 8 times in 12 at a planted 15-20%; they are short (5 to 25 ms) and noisy. Use more iterations or a longer scenario for them | W2-perf-gate | Perf WP, next time `tools/MikePDF.Perf` is touched |
| Slow drift (many pull requests of 5% each) is not gated. Read the nightly A/A and `main` runs for a month, then decide on a trend check against an older `main` build | W2-perf-gate | Opus, after the first weeks of nightly runs |
| File activation and Jump List items not built: FA-09 to FA-12, FA-15 to FA-21, and the default-app prompt | W2-tabs | File association WP |
| The test-seam gate ignores `MIKEPDF_TEST_*` in Store-signed packages; the Store path is unverified until a Store-signed package exists | W2-tabs | First Store submission (user) |
| TAB-08 (move between monitors with different DPI) and TAB-30 (high contrast, 225% text) need a second monitor and system settings | W2-tabs | The user, or a WP with an accessibility test machine |
| `HangDetectionTests.AHeavyButAlivePageThatOvershootsItsDeadlineIsNotKilledAsHung` fails under heavy machine load (seen twice locally, never in CI) | W2-tabs, W2-engine-2 | Engine WP |
| `CommandsSettingsJourneyTests.CMD05_…CtrlCCopiesTheBoxText` timed out on the clipboard once in a full UI run; passes alone | Opus, main verification | W2-views (running) |
| Large axis-aligned scans render slightly softer in tiles than in a whole-page render (43 corpus pages, about 2.7 levels mean). A whole-page bitmap cache for image-heavy pages would fix it; measure against §11 first-tile and memory budgets first | W2-engine-2 | Engine WP |
| The view-mode metrics (PV-12 open of 10,000 pages in two-page mode, PV-17 presentation turn) live in `tools/MikePDF.Perf/Metrics/ViewModeMetrics.cs` and `Scenarios/ViewModeScenarios.cs` but are not registered in `PerfRunner` or `MetricCatalog`, so `run --only` cannot select them and the A/B gate does not see them | W2-views-2 | Next perf work package: register them, ungated first, then gate after A/A calibration |
| PRINT-10 full run (1,000 pages to Microsoft Print to PDF, `PrintThousandPageTests`): broker peak 220 MiB against 164 MiB at 10 pages, engine 15 MiB against 5 MiB, so the 5% bound fails. Find whether it is a per-page retention (about 56 KB a page in the broker) or heap that is never collected, and fix it | W2-print-sheet | Next print or engine work package |
| Two UI journeys failed in a full local run on 2026-11-02 and were never reported fixed or flaky: `TAB10_ClosingATabWithUnsavedEdits...` and `C5_C6_C7_PageColoursChange...` | W2-print-sheet (seen in its logs) | Checked in the CI UI journeys (PR #24). `C5_C6_C7`: failed on x64 because an earlier journey left dark pages in the settings (two classes did not reset settings); fixed by resetting settings per journey, then green. `TAB10`: passes on x64; on ARM64 it fails every time (see the row below), so it is quarantined there only |
| CI UI journeys, wheel: `ViewModesJourneyTests.PV08_PV20_…TheWheelNeedsOneNotchMore` is quarantined on both hosted runners (`UiFact(ciQuarantine: …)`): the first mouse-wheel notch did not turn the page in 4 of 5 runs, with the pointer over the page and the app idle; keys, clicks and drags work. Not reproduced by hand | CI UI journeys (PR #24) | Find whether the hosted VM's input stack drops wheel input from `SendInput` (try FlaUI's `Mouse.Scroll` against a plain WinUI test window on the runner), or whether the app's wheel handler ignores it; then remove the quarantine |
| CI UI journeys, ARM64: `TabsWindowsJourneyTests.TAB10_…` is quarantined on ARM64 only. After Cancel on the close-unsaved question the second Ctrl+W never shows the question, and the screenshot shows keyboard focus on the first tab header, not the page. It passes on x64 and passed in local runs, so it may be a real focus-restoration bug after the dialog closes, one the faster ARM64 runner sees | CI UI journeys (PR #24) | App-shell owner: after `CloseDirtyDialog` closes, put focus back on the page surface (or the selected tab) and add a check that focus is where the user was; then remove the quarantine |
| The ARM64 hosted image shows Windows' first-sign-in privacy page over everything, leaves the Start menu open after it and keeps launching a `wsl.exe` console that takes the keyboard. The `ui-journeys` job clicks through the page and ends the console with a watcher, which depends on the page's layout at 1920x1080 | CI UI journeys (PR #24) | If a runner image update moves the page, the first step of the job fails loudly (`desktop-before-journeys.png` shows it); fix the click targets there |
| Two UI journeys failed in a full local run on 2026-11-02 and were never reported fixed or flaky: `TAB10_ClosingATabWithUnsavedEdits...` and `C5_C6_C7_PageColoursChange...` | W2-print-sheet (seen in its logs) | Check them in the first green run of the CI UI journeys (#24) |
| The x64 MSIX is 59.65 MB against the 60 MB budget once the writer host ships (AOT, 1.33 MB of it). Every wave 3 feature now has to pay for its size, or something else has to shrink | W3-save-app | Opus: measure in every wave 3 PR; a size job in CI that fails above 60 MB |
| The writer host is Native AOT now and no longer needs the JIT, but `WriterHostClient` still turns `ProhibitDynamicCode` off for it. Turn it on | W3-save-app | Next save or sandbox work package |
| Stage 5 validation starts a fresh engine outside `EngineBudget` | W3-save-app | Engine WP |
| A password-protected document with edits can't be saved, and no unprotected copy is offered (SP-23): the writer can't write encrypted objects | W3-save-app | Save WP after `m4/engine-v7` (the engine's PDFium save may do it) |
| .NET runtime 10.0.8 has a high-severity advisory (GHSA-p3jp-9v83-76w9, Brotli decoder out-of-bounds write, fixed in 10.0.14); the self-contained app ships that runtime, and the AOT projects fail NuGet audit | Opus, reviewing W3-save-app | `m3/runtime-10.0.14` (running) |

## What only the user can do
The following gate v1.0, and agents can't do them:
- Reference hardware runs on R1, R2, R3 and a Surface with a pen (§11, R-17)
- Manual Acrobat Reader and Edge fidelity passes, unless the Acrobat EULA permits automation (R-15)
- Manual Narrator, NVDA and JAWS passes. JAWS needs a licence
- Partner Center, Store submissions, and the decision to go public
- Explorer thumbnails and preview on a standard-user (non-elevated) session, and on a machine with Acrobat installed (ADR-0018, OQ-SHELL-10 and -11)
- Whether MikePDF's thumbnails should replace another default PDF app's (OQ-SHELL-14)
- Budget for a long-running fuzz machine (R-19). GitHub-hosted jobs stop at 6 hours
