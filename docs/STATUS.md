# MikePDF status

- **Current work:** a Store preview of the reader ([scope](milestones/store-readiness.md)), alongside M1 foundations under a [scoped M0 waiver](milestones/M0-waiver.md). **M0 is still not passed, and this isn't v1.0**: performance and fidelity gates wait on user actions (see the [M0 report](milestones/M0-report.md))
- **Last updated:** 2026-11-02
- **Name:** the project was Quire until 2026-11-02 ([ADR-0016](adr/0016-rename-to-mikepdf.md)). The old name stays in `spikes/`, `docs/spikes/`, `docs/test-reports/` and the master prompt, which are records of what was run and asked for
- **Repository:** public, Apache-2.0 (the licence is detected once merged to `main`). `m1/foundations` holds this work

## Store preview
| Item | Status | Evidence |
|---|---|---|
| Reader: open, password, recents, scroll, zoom, rotate, go to page, crash recovery | Done | UI journeys |
| Reader: thumbnails, bookmarks, links, text selection and copy, find, UIA text | Done | UI journeys; protocol schema 3 |
| External links | Show the address and offer to copy it. Nothing is opened | `AnExternalLinkShowsItsAddressAndOpensNothing`, `PrivacyTests` |
| MSIX, bundle, assets, identity file | Done, with **placeholder identity** until Partner Center gives the real values | `build/package.ps1`, `build/store/identity.json` |
| Sandbox on a real install | Runs in place, x64 and ARM64; the test build's self-test checks the probe rows on each install test | ADR-0004 A2, `SelfTest.Probe.cs` |
| Release workflow | Package, install test, bundle and certification kit in CI; publish is off until the user sets it up | `.github/workflows/store-release.yml` |
| Listing, certification notes, privacy policy, screenshots | Done | `docs/store/`, `docs/user/privacy.md` |

Checked on DEV (x64) on 2026-11-02, under the new name: 1620 tests passed and 0 failed, with the 29 UI tests skipped by that run; native 95/95; UI journeys 28 passed and 1 skipped by design, on both the development build and the shipped package layout.

**Not verified:**
- The Store-signed package, a standard-user install, and medium integrity (R-41)
- Smart App Control (R-43)
- A manual Narrator or NVDA pass (S7)
- Light and high-contrast themes, which are implemented but have only run in dark
- ARM64 outside CI runners

**Known limits:**
- Above 4800% zoom at 100% display scale, tiles are stretched rather than re-rendered
- A second launch replaces the document in the one window
- No annotations, forms, printing or save

## Waiting on the user
| Item | Unblocks |
|---|---|
| Merge the PR to `main` | Privacy policy URL, licence detection |
| Partner Center account (Individual or Company), reserve the name, put the identity values in `build/store/identity.json` | Uploadable package |
| First submission to a private audience, then the tests in step 5 of the [runbook](store/README.md) | Deciding whether to go public |
| Confirm the 24H2 minimum OS (ADR-0001 D1). The package already requires it | ADR-0001 acceptance |
| Manual passes: Acrobat/Edge (`docs/test-reports/M0/S4-acrobat-edge-checklist.md`, `docs/test-reports/M0/S8-acrobat-checklist.md`), Narrator (`spikes/S7/narrator-script.md`). Or confirm the Acrobat EULA permits automation (R-15) | S4, S7, S8 approval |
| Hardware: R1, R2, R3 (4K), Surface Pro with pen, 240 fps camera | S2, S3, S5, S6 approval; every §11 gate |
| Admin installs: Windows SDK 10.0.28000.x, VS C++ ARM64 tools + ATL/MFC. Optional: Performance Log Users membership (PresentMon) | S1 from-source, arm64 AOT, real frame capture |
| Budget for the build/fuzz machine (R-19) | Long fuzzing runs |

The checklists refer to output files under `spikes/S4/out/` and `spikes/S8/out/`. They exist on DEV, and `spikes/S4/run.ps1` / `spikes/S8/run-all.ps1` regenerate them elsewhere.

## M1 foundations (waiver scope)
| WP | Content | Status | Evidence |
|---|---|---|---|
| WP1 | Repo skeleton, `build.ps1`, CMake + dotnet, pinned PDFium, CI workflow | Done | CI on x64 and ARM64 |
| WP2 | IPC schema + generator; strict C#/C++ codecs; golden vectors; fuzz harnesses | Done, reviewed | Schema now v3 |
| WP3 | `MikePDF.PdfWriter` core: strict parser, incremental writer, validator | Done, reviewed | 97.1% line coverage, PDFium differential |
| WP4 | C++ engine host, broker launcher/pipe/slots/client/supervisor, sandbox probe | Done, reviewed | Every negative probe denied in the sandbox |
| WP4b | `MikePDF.WriterHost` in its own sandbox (`ValidateStructure`). Not shipped in the preview | Done, reviewed | Integration tests |
| WP5 | Opus review, STATUS | Done | ADR-0004 A1, R-37–R-39 |

**Still open in the full M1 gate (§17), outside the waiver:** the `mikepdf-render` CLI, corpus manifest + smoke tier (ADR-0010), visual-diff and perf harnesses, the >4 GiB document test, and 24 h of fuzzing including the IPC decoders on both sides.

Key documents: [master prompt](MASTER-PROMPT.md) · [M0 report](milestones/M0-report.md) · [M0 spike plan](spikes/M0-spike-plan.md) · [ADRs](adr/) · [risk register](risks.md) · [threat model](threat-model/README.md) · [Store runbook](store/README.md)

## M0 exit gate (§17)
| Criterion | Status | Evidence |
|---|---|---|
| S1–S8 spike reports approved by Opus | 7 provisional go, S5 incomplete | `docs/spikes/` |
| ADR-0001 to ADR-0010 written | All 10 written. 0001 awaits the user; 0007 awaits hardware | `docs/adr/` |
| Architecture (§5) confirmed or amended | Confirmed with amendments (WriterHost, Win32k deferred, no CPU tile tier) | ADR-0004, 0006, 0008 |
| Pinned versions recorded | ADR-0001 + A1, `build/pdfium.lock` | ADR-0001 |

## Spikes
| Id | Verdict | Remaining |
|---|---|---|
| P0 | Done: PDFium chromium/8125 pinned, fixtures fetched | n/a |
| S1 | Provisional go | From-source build (needs SDK 10.0.28000 + ARM64 tools, admin) |
| S2 | Provisional go | R1/R2/R3 runs. Dense-vector 4-core stress fails "no blank" on DEV |
| S3 (+S3b) | Provisional go | R1/R2. The packaged rerun is done (ADR-0004 A2) |
| S4 | Provisional go | Manual Acrobat/Edge pass |
| S5 | Incomplete (inventory only) | Surface + pen + 240 fps camera |
| S6 | Provisional go (x64) | arm64 run (R2), arm64 AOT, Copilot+ device |
| S7 | Provisional go | Manual Narrator/NVDA/Accessibility Insights |
| S8 | Provisional go | Manual Acrobat pass |

## ADRs
| ADR | Status |
|---|---|
| 0001 Platform baseline | Proposed + amendment A1. **Awaits user OK on the 24H2 minimum (D1)** |
| 0002 PDF engine and build | Accepted (PDFium). Release build Proposed until S1 builds from source |
| 0003 Licence policy | See the ADR: Apache-2.0, repository public |
| 0004 Process model and sandbox | Accepted, with amendments A1 and A2 (Win32k lockdown deferred, D-1) |
| 0005 IPC transport | Accepted |
| 0006 Rendering surface and tiles | Accepted (numbers provisional) |
| 0007 Ink stack | Proposed (needs S5 measurement) |
| 0008 Save pipeline | Accepted (provisional on manual Acrobat pass) |
| 0009 UI automation | Accepted (FlaUI) |
| 0010 Corpus | Proposed (licence verification of sources is M1) |
| 0015 Store-first distribution | See the ADR |
| 0016 Rename to MikePDF | Accepted |

## Open risks (top five; full list in [risks.md](risks.md))
1. R-43: Smart App Control hasn't been tested against the Store-signed package
2. R-41: the package hasn't run Store-signed or as a standard user
3. R-17: reference hardware not available
4. R-32: prebuilt PDFium may garble wide JBIG2 scans
5. R-23: single human reviewer

## Next three actions
1. **User:** merge the PR, then follow the [runbook](store/README.md) from step 2.
2. **Opus:** rebuild the upload once the identity values are committed, and act on what the private-audience tests find.
3. **Sonnet:** the remaining M1-gate items, if the waiver is extended to them.

## Change log
- 2026-10-27: M0 kickoff (A.1). Master prompt, spike plan, ADR-0001, risk register.
- 2026-10-27/28: P0 and spikes S1–S8 (plus S3b) run on DEV by Sonnet agents and reviewed by Opus. ADRs 0002–0010 written. ADR-0001 amendment A1. Threat-model skeleton. Risk register re-scored (R-30–R-36 added). M0 gate assessed: **not passed**.
- 2026-10-28: user granted a scoped M0 waiver for M1 foundations. WP1–WP4b implemented on `m1/foundations` by Sonnet agents and reviewed by Opus.
- 2026-11-02: repository made public under Apache-2.0, CI fixed, Store preview built (ADR-0015, ADR-0004 A2). Renamed from Quire to MikePDF (ADR-0016).
