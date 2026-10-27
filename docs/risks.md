# Risk register

- Status: Living document. Reviewed at every milestone gate
- Last review: 2026-10-27 (M0 kickoff, Opus)
- Source: master prompt §20 (R-01 to R-09), plus additions from the ADR-0001 verification pass (R-10 onwards)

Likelihood and impact use Low / Medium / High / Critical. **Owner** is who drives the mitigation: Opus (architecture and review), Sonnet (implementation), or **User** (decisions, money, hardware, legal).

## Top five right now

1. **R-17**: reference hardware is not confirmed. That blocks S2, S5 and the M2 performance gate.
2. **R-18**: a code-signing identity may not be obtainable as an individual in Australia.
3. **R-01**: Win2D has no stated support for Windows App SDK 2.x.
4. **R-27**: the scope is a multi-year programme, and breadth could crowd out depth.
5. **R-23**: one human reviewer for a large amount of AI-written code.

## Original register (§20), re-scored

| Id | Risk | L | I | Change since §20 | Mitigation | Owner |
|---|---|---|---|---|---|---|
| R-01 | Win2D or another dependency lags the WinAppSDK 2.x baseline | **High** ↑ | High | Evidence: Win2D 1.4.0 targets WinAppSDK 1.8, mentions nothing about 2.x, and 1.8 left servicing 2026-09-24 (ADR-0001 D2) | S6 proves Win2D on 2.5.1 first. S2 builds a Composition presenter alongside, so the fallback has measurements behind it, not a guess | Opus |
| R-02 | AppContainer blocks font access or rendering in the engine | Medium | High | Detail: PDFium's Windows font path uses GDI, which **conflicts with the Win32k-lockdown mitigation** in §5.3 | S3.5 font test. Design a broker font provider (`FPDF_SYSFONTINFO`) up front. Win32k lockdown may be dropped, recorded in ADR-0004 | Opus |
| R-03 | Appearance streams render differently in Acrobat | High | Medium | n/a | S4 matrix. Quire's own appearance generator. Fidelity harness from M1 | Opus / Sonnet |
| R-04 | Signature creation interop failures | Medium | High | Confirmed: `fpdf_signature.h` is **read-only and Experimental**, so all signing writer code is Quire's | S8 prototype. Ship validation before creation. Cross-validate with Acrobat plus one independent validator | Opus |
| R-05 | Redaction misses hidden content | Medium | Critical | n/a | Full-rewrite apply, mandatory verification gate, adversarial fixtures (§8.6) | Opus |
| R-06 | On-device model change alters AI quality | High | Medium | **Dates confirmed**: Aion Instruct on Insider Nov 2026, retail Jan 2027, when Phi Silica is removed | Build and evaluate M7 against Aion Instruct (sideload package available now). Provider abstraction. Re-run the evaluation on any model change | Opus |
| R-07 | PDFium security fixes need fast rebuilds | High | High | Detail: prebuilt binaries are **trunk snapshots**, not stable branches (ADR-0001 D3) | Release builds track a Chromium stable branch. Weekly build job. 72-hour patch SLA. Self-hosted build machine (R-19) | Opus |
| R-08 | AI-written code passes tests that assert too little | High | High | n/a | Opus reviews test quality (A.6 item 4). Stryker.NET mutation testing on Core/Protocol/PdfWriter/Signing from M1 | Opus |
| R-09 | Performance regressions creep in | Medium | Medium | Detail: hosted runners are noisy (2 vCPU, shared), so a **10% per-PR gate on hosted runners will be flaky** | Per-PR: an instruction-count or allocation proxy on hosted runners. Wall-clock gates only on self-hosted reference hardware, nightly | Opus / Sonnet |

## Added risks

| Id | Risk | L | I | Mitigation | Owner |
|---|---|---|---|---|---|
| R-10 | OS baseline in §3 (23H2) is out of servicing before v1 | High | Medium | **Resolved by ADR-0001 D1** (24H2 minimum, LTSC included). Re-check yearly | Opus |
| R-11 | Native AOT for WinUI on 2.x has no current docs, and AOT/MSIX/ARM64 bugs are open. Startup budget at risk | Medium | Medium | S6 compares JIT/R2R/AOT. The budget must be met by at least one mode. Keep code reflection-free (source generators for MessagePack) so AOT stays possible | Opus |
| R-12 | PDFium Experimental APIs (all of `fpdf_annot.h` and `fpdf_signature.h`, most of `fpdf_structtree.h`) change between revisions | High | Medium | Thin wrapper in `Quire.Engine.Pdfium` is the only consumer. A CI header-diff check on every PDFium bump. Engine tests cover every wrapped call | Sonnet |
| R-13 | XFA can't be built without V8 (prebuilt), which ties P2 static XFA to a JavaScript engine | Medium | Low | Defer to P2. If pursued, build from source and check whether XFA can run without executing scripts | Opus |
| R-14 | Launching a zero-capability AppContainer child from a packaged full-trust app (profile creation, handle lists, pipe DACLs) is harder than it looks | Medium | High | S3 with a negative-access suite. Low-IL restricted token fallback measured in the same spike | Opus |
| R-15 | **Automating Acrobat Reader on a CI VM may break its EULA**, but the M4 fidelity gate and §0.3 depend on Acrobat | Medium | High | User/legal checks the EULA. Otherwise fall back to a manually run Acrobat pass per release (documented), with automation limited to pdf.js, PDFium, Edge and Chrome | User |
| R-16 | MSIX thumbnail and preview handler registration (surrogate hosting, `prevhost.exe`) and spawning a sandboxed engine from Explorer's context, within 1–2 s timeouts | Medium | Medium | S6.5 manifest probe. A dedicated M3 spike if the probe shows complexity. An engine pool for shell extensions | Opus |
| R-17 | **Reference hardware (R1, R2, R3, Surface + 240 fps camera) is not confirmed** | High | High | User confirms the inventory. Without it, S2 and S5 stay provisional and M2's gate can't pass (§19.3) | User |
| R-18 | **Code-signing identity.** Artifact Signing Public Trust: individuals must be in the US or Canada. AU organisations are listed on one Microsoft page but not another. EV/OV certificates cost money and need HSM storage (§13.2) | High | High | User decides: register an organisation and apply to Artifact Signing (check the portal's country list), buy an OV/EV cert on a cloud HSM, or go Store-first (Store signs the package). Decide before M1 CI signing work | User |
| R-19 | GitHub-hosted private runners (2 vCPU / 8 GB) can't build PDFium from source in reasonable time, and perf numbers there are noisy | High | Medium | Self-hosted x64 build machine for PDFium and the fuzzing box. Hosted runners only for Quire code using a cached, hashed PDFium artifact | User (budget) / Sonnet |
| R-20 | UI automation tooling is weak: WinAppDriver unmaintained, Appium's Windows driver proxies it, FlaUI last released Feb 2025 | High | Medium | ADR-0009 after S6.6. Prefer FlaUI (direct UIA3, no server) and isolate behind a page-object layer so the driver can be swapped | Opus |
| R-21 | Windows AI coverage is fragmented: OCR NPU-only, `LanguageModel` LAF-gated until Jan 2027, GPU path Insider-only, not available in China | High | Medium | Feature-detect with `GetReadyState`. `Windows.Media.Ocr` and Foundry Local fallbacks. Request the LAF token early if Phi Silica testing is still needed before Jan 2027 | Opus / User (token request) |
| R-22 | From Oct 2026, some Windows AI text APIs add an **invisible watermark** to output. Users who copy AI summaries into documents carry it along | Medium | Low | Document it in `docs/user/privacy.md` and the AI panel's help text. Never strip it silently. Assess legal labelling duties before M7 | Opus / User |
| R-23 | One human reviewer for a large AI-written codebase leads to review fatigue and rubber-stamping | High | High | PRs ≤ 600 lines. Opus does first-line review. The user's own review is reserved for the §13.3 security gates and milestone gates. Mutation testing (R-08) | User / Opus |
| R-24 | Mixed toolchains: PDFium with clang-cl, Quire's C++ with MSVC 14.50. Mismatches in CRT, hardening flags or debug info | Medium | Medium | PDFium is consumed only through its C API (no C++ ABI across the boundary). S1 checks CFG/CET flags on both. Consider building the engine with clang-cl too (ADR-0002) | Opus |
| R-25 | Corpus licensing: pdf.js and PDFium test files, SafeDocs and others carry mixed licences. Storage cost of a private 50k-file bucket | Medium | Medium | ADR-0010. The manifest records the licence of every file. Private blob storage, fetched by hash | Opus / User (storage) |
| R-26 | The licence decision (ADR-0003) is still open, and it controls which dependencies and test tools are allowed | Medium | Medium | User decides proprietary vs open source before M1. Until then, treat everything as proprietary: no GPL/AGPL linked code. External GPL test tools only by ADR | User |
| R-27 | The master prompt is a multi-year programme. Breadth could crowd out depth, or gates could get waived under schedule pressure | High | High | Hold every exit gate (§17). The M2 vertical slice proves the architecture before broadening. Anti-stall prompt A.11 instead of lowering the bar | Opus |
| R-28 | WinAppSDK 2.x moves quickly: the 2.0 line's servicing ends 2027-04-29, and minors bring deprecations | Medium | Medium | A quarterly SDK bump task. Ban deprecated APIs with Roslyn analyzers. ADR-0001 update on every bump | Sonnet |
| R-29 | JAWS licence for the M5 manual pass (§12.7 "if licensed") | Low | Low | NVDA and Narrator are the required gate. JAWS only if the user licenses it, and the report says so | User |

## Closed / resolved
- R-10: resolved by ADR-0001 D1, pending Opus acceptance of ADR-0001.
