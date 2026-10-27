# Quire status

- **Current milestone:** M0, Feasibility (spikes S1–S8)
- **Last updated:** 2026-10-27
- **Gate status:** not started. 0 of 8 spike reports, 0 of 10 ADRs accepted (ADR-0001 proposed)

Key documents: [master prompt](MASTER-PROMPT.md) · [M0 spike plan](spikes/M0-spike-plan.md) · [ADR-0001 platform baseline](adr/0001-platform-baseline.md) · [risk register](risks.md)

## M0 exit gate (§17)

| Criterion | Status | Evidence |
|---|---|---|
| S1–S8 spike reports approved by Opus | Not started | n/a |
| ADR-0001 to ADR-0010 written | 1 of 10 drafted (0001 Proposed) | `docs/adr/` |
| Architecture (§5) confirmed or amended | Not started | Depends on S2, S3, S6, S7 |
| Pinned versions recorded | Partial: ADR-0001 drafted. `build/pdfium.lock` not created | ADR-0001 |

## M0 tasks

### Spikes
| Id | Task | Owner | Depends on | Hardware | Status |
|---|---|---|---|---|---|
| P0 | Pin prebuilt PDFium `chromium/8125` (x64 + arm64 tgz, SHA-256) into `spikes/_shared/pdfium/` | Sonnet | n/a | DEV | Not started |
| S1 | PDFium build from source and API surface | Sonnet → Opus review | n/a | DEV | Not started |
| S2 | Rendering pipeline throughput (Win2D and Composition presenters) | Sonnet → Opus review | P0, S3, S6 | R1, R2, R3 | Not started (blocked on hardware) |
| S3 | Process isolation (AppContainer) and IPC | Sonnet → Opus review | P0 | DEV, R1, R2 | Not started |
| S4 | Save fidelity matrix | Sonnet → Opus review | P0 | DEV, VM-V | Not started |
| S5 | Ink latency | Sonnet → Opus review | S6 | SURF, CAM | **Blocked on hardware** |
| S6 | WinAppSDK 2.5.1 compatibility matrix (Win2D, AOT, MSIX, Windows AI) | Sonnet → Opus review | n/a | DEV, R1, R2, R3 | Not started. **Critical path** |
| S7 | UIA Text pattern on a custom-rendered page | Sonnet → Opus review | P0, S6 | DEV | Not started |
| S8 | Signature validation and incremental signing prototype | Sonnet → Opus review | P0 | DEV, VM-V | Not started |

### ADRs (Appendix C, 0001–0010 required for M0)
| ADR | Title | Fed by | Status |
|---|---|---|---|
| 0001 | Platform baseline and pinned versions | Verification pass 2026-10-27, S1, S6 | **Proposed** |
| 0002 | PDF engine choice (PDFium) and build strategy | S1 | Not started |
| 0003 | Quire licence and dependency licence policy | **User decision** | Not started (blocked) |
| 0004 | Process model and sandbox | S3 | Not started |
| 0005 | IPC transport and serialisation | S3 | Not started |
| 0006 | Rendering surface and tile strategy | S2, S6, S7 | Not started |
| 0007 | Ink stack | S5 | Not started |
| 0008 | Save pipeline and atomic replace | S4 | Not started |
| 0009 | UI automation framework for tests | S6.6 | Not started |
| 0010 | Corpus sourcing and storage | Licence review | Not started |

### Recommended in M0 (not part of the gate)
- Threat-model skeleton in `docs/threat-model/` (STRIDE, §13.1). Started early so S3's sandbox decisions are recorded against it.
- `build/pdfium.lock` format, defined in S1.

## Open risks (top five; full list in [risks.md](risks.md))
1. R-17: reference hardware not confirmed
2. R-18: code-signing identity eligibility (individual in Australia)
3. R-01: Win2D has no stated WinAppSDK 2.x support
4. R-27: scope breadth against holding the gates
5. R-23: single human reviewer

## Blocked / waiting on the user
| Item | Why it matters | Risk |
|---|---|---|
| Which reference devices you own or can get (R1 low-end x64, R2 Snapdragon X Copilot+, R3 4K dGPU, Surface Pro + pen, 240 fps camera) | S2/S5 results and the M2 performance gate are provisional without them | R-17 |
| Quire licence: proprietary or open source (ADR-0003) | Decides whether MuPDF, GPL test tools and some corpora are usable | R-26 |
| Code-signing route: organisation + Artifact Signing, OV/EV cert on a cloud HSM, or Store-first | Release pipeline design, SmartScreen reputation | R-18 |
| Budget for a self-hosted x64 build/fuzz machine and an ARM64 perf runner | PDFium from-source builds and the 72-hour fuzz gate don't fit hosted runners | R-19 |
| Acrobat Reader EULA check for automated fidelity testing | The M4 fidelity gate design | R-15 |
| Windows AI LAF token request (Phi Silica / App Content Search), if needed before Aion Instruct ships | M7 timing and index decision | R-21 |

## Next three actions
1. **Opus:** accept or amend ADR-0001 after the user confirms the OS-minimum change (D1: 23H2 → 24H2).
2. **Sonnet:** P0, then S6 and S1 in parallel (Wave 1), following [the spike plan](spikes/M0-spike-plan.md) and A.2.
3. **User:** answer the "Blocked" table, hardware and licence first.

## Change log
- 2026-10-27: M0 kickoff (A.1). Master prompt saved, spike plan, ADR-0001 (Proposed), risk register, this file.
