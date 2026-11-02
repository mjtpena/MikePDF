# Milestone 0 Report

- Date: 2026-10-28
- Assessor: Opus (A.10), using only evidence in this repo
- Branch: `m0/spikes`

## Outcome
**M0 does not pass its exit gate yet.**
- All eight spike reports exist. Seven are approved as **provisional**; S5 is incomplete.
- Nine of ten required ADRs are Accepted or partly Accepted. ADR-0001 and ADR-0003 wait on user decisions, and ADR-0007 waits on S5 hardware.
- The §5 architecture is **confirmed, with three amendments**:
  1. A third sandboxed process, `MikePDF.WriterHost`, because MikePDF must own its save writer and that writer parses untrusted bytes.
  2. Win32k lockdown deferred until we have our own PDFium build.
  3. No compressed CPU tile tier.
- The remaining failures are **not** design problems. They are hardware that isn't available (R1, R2, R3, Surface, camera), toolchain installs that need admin rights, manual passes in Acrobat, Edge and Narrator, and two user decisions.

Per §19.3 and A.10, a "Not measured" on a gate criterion means the gate does not pass.

## Exit-gate evidence
| Criterion (§17 M0) | Result | Evidence |
|---|---|---|
| S1–S8 spike reports approved by Opus | **Fail**: 7 provisional, 1 incomplete (see table below) | `docs/spikes/S1.md`…`S8.md` |
| ADRs 0001–0010 written | **Pass (written)**. Acceptance is partial: 0001 and 0003 are Proposed (user), 0007 is Proposed (hardware), 0002 is Accepted for the engine and Proposed for the release build | `docs/adr/` |
| Architecture (§5) confirmed or amended | **Pass**: confirmed, with amendments ADR-0004 D-1, ADR-0006 D-1/D-2, ADR-0008 (WriterHost) | ADR-0004, 0005, 0006, 0008 |
| Pinned versions recorded | **Partial**: ADR-0001 plus amendment A1; PDFium prebuilt pinned by SHA-256 in `spikes/_shared/pdfium/fetch.ps1`. `build/pdfium.lock` is an M1 deliverable | ADR-0001 |

### Spike review (A.3)
| Spike | Verdict | What is still missing before approval |
|---|---|---|
| S1 PDFium API surface | **Provisional go** | From-source x64 and arm64 build, hardening flags on our own build, ASan/libFuzzer. Blocked: Windows SDK 10.0.28000 and the VS ARM64 C++ tools need an admin install |
| S2 Rendering throughput | **Provisional go** | R1, R2, R3 (4K at 150/200%) runs; PresentMon (needs Performance Log Users); fails on DEV for the dense-vector 4-core stress (blank frames) and the GeoPDF time to first tile |
| S3 Sandbox and IPC | **Provisional go** | Rerun from a packaged MSIX launcher; R1/R2 timings; hardened-restart outliers |
| S4 Save fidelity | **Provisional go** | Manual Acrobat and Edge pass: `docs/test-reports/M0/S4-acrobat-edge-checklist.md` |
| S5 Ink latency | **Incomplete** | Only the API inventory is done. Latency, pressure and palm tests need a Surface Pro, pen and 240 fps camera |
| S6 WinAppSDK compatibility | **Provisional go (x64)** | arm64 runtime on R2; arm64 AOT (needs ARM64 C++ tools); Windows AI `Ready` on a Copilot+ device; cold start on R1/R3 |
| S7 UIA Text pattern | **Provisional go** | Manual Narrator pass (`spikes/S7/narrator-script.md`); NVDA; Accessibility Insights FastPass; a non-100% display scale |
| S8 Signatures | **Provisional go** | Manual Acrobat pass (`docs/test-reports/M0/S8-acrobat-checklist.md`); smart card / Windows Hello; a real CA chain |

## Metrics (DEV only; not budget verdicts)
| Metric | Value | Source |
|---|---|---|
| PDFium checklist APIs present (x64/arm64) | 65/65 (23 Experimental) | S1 |
| Sandbox negative tests denied (hardened AppContainer) | All | S3 |
| C++ engine spawn to Hello / crash to restored tile | p50 10 ms / p50 18 ms, p95 130 ms hardened | S3b |
| Ping RTT p99 | 24–122 µs | S3, S3b |
| First sharp tile, typical text page (composed, p95) | ≈ 40 ms | S2 |
| Fling: frames > 50 ms | 0 of 64,812 | S2 |
| 512 px tile upload p95 | ≤ 1.53 ms with GPU sync | S2 |
| Warm startup p50 (loaded machine) | AOT ≈ 430 ms, R2R ≈ 550 ms | S6 |
| Save matrix | 257 pass, 27 fail, 72 not measured, 15 n/a | S4 |
| Signature fixtures classified correctly / false Valid | 46/46, 0 | S8 |
| UIA Text pattern checks | 251/251 across 4 fixtures × 4 rotations | S7 |
| Coverage, fuzz hours | Not applicable to M0 (no production code); fuzzing not run | n/a |
| Open bugs by severity | No production code yet. Upstream candidate: JBIG2 MMR wide-page decode (R-32) | n/a |

## Scope changes
- ADR-0001 D1: minimum OS raised to Windows 11 24H2 (**awaiting user confirmation**).
- ADR-0004 D-1: Win32k lockdown deferred to our own PDFium build (M5).
- ADR-0006 D-1: no compressed CPU tile tier in v1.0. D-2: the 200 MB UI memory budget is flagged for M2 review.
- ADR-0008: MikePDF owns annotation writing. A new `MikePDF.WriterHost` sandboxed process. Signing splits into digest-in-sandbox and sign-in-broker.
- ADR-0003: interim licence policy (no GPL/AGPL in shipped code) until the user decides.

## Risks (top five)
| Risk | Owner | Mitigation |
|---|---|---|
| R-17 Reference hardware not available | User | Confirm or obtain R1, R2, R3, Surface and camera. Every performance gate depends on it |
| R-18 Code-signing identity (individual, outside the US or Canada) | User | Decide: organisation + Artifact Signing, OV/EV on a cloud HSM, or Store-first |
| R-32 JBIG2 MMR wide-page decode | Sonnet / Opus | Reproduce with real scans in M1, then report upstream |
| R-27 Scope breadth vs gates | Opus | Hold gates. M2 vertical slice before broadening |
| R-23 Single human reviewer | User / Opus | Small PRs. User review only on §13.3 security gates and milestone gates |

## What is needed to pass M0
**User actions** (none can be done by an agent on this machine):
1. Confirm ADR-0001 D1 (24H2 minimum) and choose the licence (ADR-0003).
2. Run the manual passes from these checklists:
   - S4 and S8: Acrobat and Edge
   - S7: Narrator (plus NVDA if available)
   Or confirm the Acrobat EULA allows automating it (R-15).
3. Provide or confirm the hardware: R1, R2, R3 (4K), Surface Pro with pen, 240 fps camera.
4. Admin installs, on DEV or a build machine:
   - Windows SDK 10.0.28000.x with Debugging Tools
   - VS C++ ARM64 build tools with ATL/MFC
   - optionally, Performance Log Users membership for PresentMon

**Then (agents):**
- S1 from-source build
- the S2, S3 and S6 matrices on R1, R2 and R3
- the S5 measurement run
- update the ADRs, and re-assess this gate

## Next milestone plan (M1, after the gate)
- Repo skeleton per §5.5, CI on `windows-2025-vs2026` and `windows-11-vs2026-arm`, analysers, and `global.json` pinning .NET 10.0.403.
- `build/pdfium.lock` plus the PDFium build pipeline (ADR-0002) on a self-hosted build machine (R-19).
- Engine host (C++20) with the ADR-0004 sandbox profile, ADR-0005 IPC (schema generator, both decoders fuzzed), watchdog and respawn.
- `MikePDF.PdfWriter` core, from the S8/S4 prototypes, inside `MikePDF.WriterHost`.
- Corpus manifest plus the smoke tier (ADR-0010), visual-diff harness, perf harness, fuzz targets.
- **M1 exit gate:** `mikepdf-render` renders the smoke corpus through the sandboxed engine with visual diffs; 24 h of fuzzing; the IPC decoder fuzzed on both sides.
