# Quire status

- **Current milestone:** M1 foundations, under a [scoped M0 waiver](milestones/M0-waiver.md). **M0 is still not passed: it waits on user actions** (see the [M0 report](milestones/M0-report.md))
- **Last updated:** 2026-10-28
- **Branches:** `m0/spikes` (M0), `m1/foundations` (M1, based on it). Neither is pushed, so CI has never run

## M1 foundations (waiver scope)
| WP | Content | Status | Evidence |
|---|---|---|---|
| WP1 | Repo skeleton, `build.ps1`, CMake + dotnet, pinned PDFium, CI workflow | Done | commit 26eabe3 |
| WP2 | IPC schema (v2) + generator; strict C#/C++ codecs; golden vectors; fuzz harnesses | Done, reviewed (R1 applied) | Protocol line coverage 98.8% |
| WP3 | `Quire.PdfWriter` core: strict parser, incremental writer, validator | Done, reviewed | 364 tests, 97.1% line coverage, PDFium differential |
| WP4 | C++ engine host, broker launcher/pipe/slots/client/supervisor, sandbox probe | Done, reviewed (R1 no handle inheritance, R2 watchdog defaults) | Every negative probe denied in the sandbox |
| WP4b | `Quire.WriterHost` in its own sandbox (`ValidateStructure`) | Done, reviewed | Integration tests incl. negative probe under the WriterHost profile |
| WP5 | Opus review, STATUS | Done | ADR-0004 amendment A1, R-37–R-39 |

`./build.ps1 -Test` on DEV (x64), run by Opus: **native 70/70, managed 870/870** (111 integration tests against the real sandboxed processes), zero compiler warnings. Not yet verified: ARM64 (no toolchain here), the CI workflow (not pushed), packaged MSIX launch, the >4 GiB document test (ADR-0005), and real fuzzing runs (the harnesses build; only in-test randomised passes have run).

**Still open in the full M1 gate (§17), outside the waiver:** the `quire-render` CLI, corpus manifest + smoke tier (ADR-0010), visual-diff and perf harnesses, and 24 h of fuzzing including the IPC decoders on both sides.

Key documents: [master prompt](MASTER-PROMPT.md) · [M0 report](milestones/M0-report.md) · [M0 spike plan](spikes/M0-spike-plan.md) · [ADRs](adr/) · [risk register](risks.md) · [threat model](threat-model/README.md)

## M0 exit gate (§17)
| Criterion | Status | Evidence |
|---|---|---|
| S1–S8 spike reports approved by Opus | 7 provisional go, S5 incomplete | `docs/spikes/` |
| ADR-0001 to ADR-0010 written | All 10 written. 0001 and 0003 await the user; 0007 awaits hardware | `docs/adr/` |
| Architecture (§5) confirmed or amended | Confirmed with amendments (WriterHost, Win32k deferred, no CPU tile tier) | ADR-0004, 0006, 0008 |
| Pinned versions recorded | ADR-0001 + A1, PDFium SHA-256 pins. `build/pdfium.lock` is M1 | ADR-0001 |

## Spikes
| Id | Verdict | Remaining |
|---|---|---|
| P0 | Done: PDFium chromium/8125 pinned, fixtures fetched | n/a |
| S1 | Provisional go | From-source build (needs SDK 10.0.28000 + ARM64 tools, admin) |
| S2 | Provisional go | R1/R2/R3 runs. Dense-vector 4-core stress fails "no blank" on DEV |
| S3 (+S3b) | Provisional go | Packaged-launcher rerun, R1/R2 |
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
| 0003 Licence policy | Proposed, interim policy in force. **Awaits user licence choice** |
| 0004 Process model and sandbox | Accepted (Win32k lockdown deferred, D-1) |
| 0005 IPC transport | Accepted |
| 0006 Rendering surface and tiles | Accepted (numbers provisional) |
| 0007 Ink stack | Proposed (needs S5 measurement) |
| 0008 Save pipeline | Accepted (provisional on manual Acrobat pass) |
| 0009 UI automation | Accepted (FlaUI) |
| 0010 Corpus | Proposed (licence verification of sources is M1) |

## Open risks (top five; full list in [risks.md](risks.md))
1. R-17: reference hardware not available
2. R-18: code-signing identity eligibility
3. R-32: prebuilt PDFium may garble wide JBIG2 scans
4. R-27: scope breadth against holding the gates
5. R-23: single human reviewer

## Blocked / waiting on the user
| Item | Unblocks |
|---|---|
| Confirm the 24H2 minimum OS (ADR-0001 D1) | ADR-0001 acceptance |
| Choose the Quire licence (ADR-0003) | ADR-0003 acceptance, dependency policy |
| Manual passes: Acrobat/Edge (`docs/test-reports/M0/S4-acrobat-edge-checklist.md`, `docs/test-reports/M0/S8-acrobat-checklist.md`), Narrator (`spikes/S7/narrator-script.md`). Or confirm the Acrobat EULA permits automation (R-15) | S4, S7, S8 approval |
| Hardware: R1, R2, R3 (4K), Surface Pro with pen, 240 fps camera | S2, S3, S5, S6 approval; every §11 gate |
| Admin installs: Windows SDK 10.0.28000.x, VS C++ ARM64 tools + ATL/MFC. Optional: Performance Log Users membership (PresentMon) | S1 from-source, arm64 AOT, real frame capture |
| Code-signing route (R-18) and budget for the build/fuzz machine (R-19) | M1 CI design |

The checklists refer to output files under `spikes/S4/out/` and `spikes/S8/out/`. They exist on DEV now, and `spikes/S4/run.ps1` / `spikes/S8/run-all.ps1` regenerate them elsewhere.

## Next three actions
1. **User:** push `m1/foundations` (or say so) so CI runs on x64 and ARM64. Work through the "Blocked" table.
2. **Sonnet:** the remaining M1-gate items (`quire-render`, corpus smoke tier, visual diff, perf harness, fuzzing runs), if the waiver is extended to them.
3. **Opus:** re-assess M0 as evidence arrives, and accept ADR-0001/0003/0007 when their inputs land.

## Change log
- 2026-10-27: M0 kickoff (A.1). Master prompt, spike plan, ADR-0001, risk register.
- 2026-10-28: user granted a scoped M0 waiver for M1 foundations. WP1–WP4b implemented on `m1/foundations` by Sonnet agents and reviewed by Opus.
- 2026-10-27/28: P0 and spikes S1–S8 (plus S3b) run on DEV by Sonnet agents and reviewed by Opus. ADRs 0002–0010 written. ADR-0001 amendment A1. Threat-model skeleton. Risk register re-scored (R-30–R-36 added). M0 gate assessed: **not passed**.
