# ADR-0001: Platform baseline and pinned versions
- Status: Proposed
- Date: 2026-10-27
- Deciders: Opus (approver), Opus (proposer)

## Context
Master prompt §3 asks for every baseline version to be confirmed as current, and pinned here, before any code is written. Rule 2 in §0.2 forbids guessing APIs or versions. This ADR records what was checked on 2026-10-27, where each fact came from, and what still needs a spike.

**How each row was verified.** Two research passes read official or primary sources (learn.microsoft.com, dotnet.microsoft.com, nuget.org, GitHub release pages and READMEs, chromiumdash, pdfium.googlesource.com, docs.github.com). Opus then re-checked three load-bearing claims directly: Windows 11 servicing dates, the Phi Silica → Aion Instruct transition, and the Win2D changelog. The Status column uses these labels:

- **VERIFIED (Opus)**: Opus re-read the official source today.
- **VERIFIED**: a research pass read the official source today and gave a URL. Opus did not re-read it.
- **UNVERIFIED → S#**: no official source confirms it. Spike S# must produce the evidence.
- **CONTRADICTED**: the official source disagrees with the master prompt. The deviation is noted below.

Every version here is a point in time. Re-check before M1 starts and at every milestone gate.

## Pinned baseline

### Operating system and architectures
| Item | Pin | Status | Source |
|---|---|---|---|
| Minimum OS | **Windows 11 24H2 (build 26100)**. Manifest `TargetDeviceFamily MinVersion="10.0.26100.0"` | CONTRADICTED vs §3 ("23H2 and later"). See Deviation D1 | [Windows 11 release information](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information) |
| Max version tested | Windows 11 26H2 (build 26300), `MaxVersionTested="10.0.26300.0"` | VERIFIED (Opus): 26H2 GA 2026-09-29 | same |
| OS test matrix | 24H2 Enterprise, 24H2 LTSC, 25H2 Pro, 26H2 Pro | VERIFIED (Opus): all in servicing today | same |
| 23H2 servicing | Home/Pro: ended. Enterprise/Education: ends **2026-11-10** | VERIFIED (Opus) | same |
| 24H2 servicing | Home/Pro: ended. Enterprise/Education: 2027-10-12. LTSC: 2029-10-09 (mainstream) | VERIFIED (Opus) | same |
| 25H2 servicing | Home/Pro 2027-10-12. Ent/Edu 2028-10-10 | VERIFIED (Opus) | same |
| 26H2 servicing | Home/Pro 2028-10-10. Ent/Edu 2029-10-09 | VERIFIED (Opus) | same |
| Architectures | x64 and ARM64, both native. No x86 | Policy (§3) | n/a |

### UI framework and runtime
| Item | Pin | Status | Source |
|---|---|---|---|
| Windows App SDK | **2.5.1** (`Microsoft.WindowsAppSDK` 2.5.1, stable, 2026-09-16) | VERIFIED. Matches §3 | [Release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels), [NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) |
| WinAppSDK channel policy | Stable only. 2.5.4-experimental (2026-09-29) exists and is **not** allowed in shipping builds | VERIFIED | same |
| WinAppSDK 2.x notable changes | SemVer from 2.0. The package family name tracks the major version. ML moved to `Microsoft.Windows.AI.MachineLearning`. `Window.Current`, `DependencyObject.Dispatcher` and `FocusManager.GetFocusedElement` are deprecated | VERIFIED (partial list. No consolidated breaking-change doc exists) | [2.0 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0) |
| .NET | **.NET 10 LTS**, runtime 10.0.14 (security, 2026-10-25), SDK **10.0.403**. End of support 2028-11-14. `global.json` pins the SDK with `rollForward: latestPatch` | VERIFIED (Amendment A2: the 10.0.14 date, 2026-10-25, is confirmed by the release metadata) | [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [10.0 releases.json](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [10.0.14 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md) |
| C#/WinRT | Whatever 2.5.1 brings in transitively. Latest stable CsWinRT is 2.3.1. **CsWinRT 3.0 previews are excluded** | VERIFIED (version). Interplay with AOT UNVERIFIED → S6 | [CsWinRT releases](https://github.com/microsoft/CsWinRT/releases) |
| Deployment mode | Target **Native AOT**. Fall back to ReadyToRun | UNVERIFIED → **S6**. Official AOT guidance for WinUI stops at the WinAppSDK 1.6 notes, and AOT/MSIX/ARM64 bugs are still open | [1.6 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6) |
| Win2D | **Microsoft.Graphics.Win2D 1.4.0** (2026-03-10 per the changelog, 2026-03-16 on NuGet) | VERIFIED (Opus): built against WinAppSDK 1.8.260204000, with **no mention of 2.x**. Compatibility with 2.5.1 is UNVERIFIED → **S6** | [Win2D CHANGELOG](https://github.com/microsoft/Win2D/blob/winappsdk/main/CHANGELOG.md) |
| Rendering fallback | Composition (`CompositionDrawingSurface` + D2D interop) if Win2D fails S6 | Decision deferred to ADR-0006 | n/a |

### Windows AI and OCR
| Item | Pin / finding | Status | Source |
|---|---|---|---|
| `TextRecognizer` (Windows AI OCR) | Runs **only on NPU** (Copilot+ PCs). Stable since 1.7.1 | VERIFIED. Matches §3/§8.8 | [Text recognition](https://learn.microsoft.com/en-us/windows/ai/apis/text-recognition) |
| `Windows.Media.Ocr` fallback | Supported. Needs package identity, which MSIX provides. Microsoft docs call it "legacy" | VERIFIED | [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr) |
| `LanguageModel` (Phi Silica) | Current model. **Limited Access Feature: needs a token**. Runs on NPU, and on a GPU only with Developer Mode, an Insider build and an experimental SDK. Not available in China | VERIFIED (Opus) | [Phi Silica](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica) |
| Aion Instruct transition | Insider rollout **Nov 2026** (side by side, controlled by CFR). Retail **Jan 2027**, when Phi Silica is removed. **No LAF token needed**. Same API surface | VERIFIED (Opus). Matches §9.4 | same |
| AI text watermarking | From Oct 2026, output from some Windows AI text APIs carries an invisible watermark | VERIFIED (Opus). New finding, see risk R-22 | same |
| Windows AI retail OS minimum | Not stated on the get-started page, which still cites an old Insider build | UNVERIFIED → **S6**. Gate on `GetReadyState()` at runtime and never on the OS version | [Get started](https://learn.microsoft.com/en-us/windows/ai/apis/get-started) |
| App Content Search | Stable in 2.5.1 (`Microsoft.Windows.Search.AppContentIndex`). **LAF token required** | VERIFIED. Matches §9.3 | Release channels page |
| Foundry Local | GA 2026-04-09. SDK v2.2.1 (2026-10-23). CLI still preview (0.12.1). Release notes mention Windows ARM64 | VERIFIED (ARM64 only through release notes) | [GA post](https://devblogs.microsoft.com/foundry/foundry-local-ga/), [releases](https://github.com/microsoft/Foundry-Local/releases) |

### PDF engine
| Item | Pin | Status | Source |
|---|---|---|---|
| Prebuilt PDFium (spikes, M1) | **bblanchon/pdfium-binaries `chromium/8125`** (PDFium 158.0.8125.0, 2026-10-26). Assets `pdfium-win-x64.tgz` and `pdfium-win-arm64.tgz`, pinned by SHA-256 in `spikes/_shared/pdfium/` | VERIFIED | [releases](https://github.com/bblanchon/pdfium-binaries/releases) |
| Prebuilt NuGet | `bblanchon.PDFium.Win32` 158.0.8125. **Not used**: whether it contains the arm64 runtime is UNVERIFIED. Prefer the tgz assets with hashes | VERIFIED (id/version) | [NuGet](https://www.nuget.org/packages/bblanchon.PDFium.Win32) |
| Prebuilt licence | Repo says MIT, NuGet says Apache-2.0. Record both plus PDFium's BSD-3-Clause and its third-party notices | VERIFIED (the two differ) | repo + NuGet |
| XFA in prebuilt | **No separate XFA build.** `pdf_enable_xfa` follows `ENABLE_V8`, so XFA always comes with V8 | VERIFIED. New finding, see D4 | [configure step](https://github.com/bblanchon/pdfium-binaries/blob/master/steps/05-configure.sh) |
| Release PDFium | Built from source on a **Chromium stable branch**. Candidate: `chromium/8103` (M157, full stable 2026-11-03). Final pin goes in `build/pdfium.lock` through ADR-0002 after S1 | Proposed. See D3 | [chromiumdash](https://chromiumdash.appspot.com/fetch_milestones?only_branched=true&num=4) |
| PDFium toolchain | depot_tools + GN + Ninja. **Bundled clang (clang-cl) is the supported compiler. PDFium accepts no MSVC patches** | VERIFIED | [PDFium README](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/README.md) |
| ARM64 cross-compile | `target_cpu="arm64"` from an x64 host. It works in practice (pdfium-binaries does it) but no official docs cover it | UNVERIFIED (official docs) → **S1** | pdfium-binaries workflow |
| Public headers (§6.2) | All 14 exist. **All of `fpdf_annot.h` and `fpdf_signature.h` are Experimental**, and 21 of 33 functions in `fpdf_structtree.h` are. `fpdf_signature.h` is read-only | VERIFIED (header names). Per-function checks → **S1** | [public/](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/) |

### Native toolchain
| Item | Pin | Status | Source |
|---|---|---|---|
| Visual Studio | **VS 2026 (18.x)**, currently 18.11.2 (2026-10-25). Required by Chromium/PDFium builds | VERIFIED | [VS 2026 release notes](https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes), [Chromium Windows build](https://chromium.googlesource.com/chromium/src/+/main/docs/windows_build_instructions.md) |
| MSVC for MikePDF-owned C++ | **MSVC 14.50 (LTS, supported to Nov 2028)**, `/std:c++20`. The current default 14.51 is supported only to Feb 2027 | VERIFIED (lifecycle). Which toolset 18.11 installs by default is UNVERIFIED | [Compiler versions](https://learn.microsoft.com/en-us/cpp/overview/compiler-versions) |
| Windows SDK | **10.0.28000.2957** (Chromium needs ≥ 10.0.28000.2270) | VERIFIED | [Windows SDK](https://learn.microsoft.com/en-us/windows/apps/windows-sdk/downloads) |
| Engine fuzzing compiler | **clang-cl `-fsanitize=fuzzer,address`**, matching PDFium. MSVC `/fsanitize=fuzzer` is still "experimental" | VERIFIED | [/fsanitize](https://learn.microsoft.com/en-us/cpp/build/reference/fsanitize) |
| C# fuzzing | SharpFuzz **2.3.0** (NuGet. GitHub releases lag at 2.2.0) | VERIFIED | [NuGet](https://www.nuget.org/packages/SharpFuzz) |
| qpdf (test tool) | **12.4.2** (2026-09-27) | VERIFIED | [releases](https://github.com/qpdf/qpdf/releases) |

### CI, packaging, signing, test drivers
| Item | Pin | Status | Source |
|---|---|---|---|
| x64 CI runner | **`windows-2025-vs2026`**, named explicitly. Never `windows-latest`, because sources disagree on which VS that image has | VERIFIED (label exists). Image contents conflict | [GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners), [runner-images#14017](https://github.com/actions/runner-images/issues/14017) |
| ARM64 CI runner | Hosted **`windows-11-vs2026-arm`** for build and unit tests (GA for private repos 2026-01-29). Self-hosted R2 for perf and Windows AI | VERIFIED | [changelog](https://github.blog/changelog/2026-01-29-arm64-standard-runners-are-now-available-in-private-repositories/) |
| Hosted runner size | Private repos get 2 vCPU / 8 GB. **Too small for a PDFium from-source build per PR.** See risk R-19 | VERIFIED | GitHub-hosted runners |
| MSIX auto-update | App Installer file, **2021 schema** (`ShowPrompt`, `UpdateBlocksActivation`) | VERIFIED | [App Installer auto-update](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview) |
| Release signing | **Azure Artifact Signing** (formerly Trusted Signing) if eligible. It signs MSIX. Public Trust: organisations in AU are listed on the quickstart, but **individuals must be in the US or Canada**, and another MS page lists fewer countries | **OPEN (user decision)**. See risk R-18 | [quickstart](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart), [FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq) |
| Dev signing | Self-signed test certificate, generated per machine and never committed | Policy (§13.2) | n/a |
| UI automation | Decision belongs to ADR-0009. Facts: WinAppDriver is "no longer under active development". `appium-windows-driver` 6.3.1 **proxies WinAppDriver**. FlaUI 5.0.0, last released 2025-02-25 | VERIFIED. All options are weak, see risk R-20 | [Testing docs](https://learn.microsoft.com/en-us/windows/apps/develop/testing/), [appium-windows-driver](https://github.com/appium/appium-windows-driver), [FlaUI.UIA3](https://www.nuget.org/packages/FlaUI.UIA3) |

## Deviations from master prompt §3

- **D1: Minimum OS raised from 23H2 to 24H2.** 23H2 Home/Pro is already out of servicing, and 23H2 Enterprise/Education ends 2026-11-10, well before v1.0. 24H2 is the oldest release still in servicing for Enterprise (to 2027-10) and is the current LTSC base (to 2029). For the "10,000 managed devices" goal in §0, LTSC matters. Consumer Home/Pro 24H2 is also out of servicing, so the realistic consumer baseline is 25H2+. We still choose 24H2 as the minimum so LTSC fleets are covered.
- **D2: Win2D compatibility with WinAppSDK 2.x has no official support statement.** The research pass also reported that the 1.8 line Win2D targets left servicing on 2026-09-24 (VERIFIED by research pass, not re-checked by Opus). S6 must prove Win2D 1.4.0 works on 2.5.1 before ADR-0006 chooses it. §20's Composition fallback becomes a real option rather than a contingency.
- **D3: Prebuilt PDFium is a trunk snapshot, not a stable branch.** pdfium-binaries tags (8125, PDFium 158) are ahead of Chromium stable (M156 branch 8078. M157 branch 8103 goes stable on 2026-11-03). Spikes and M1 can use the prebuilt. Release builds should follow a Chromium stable branch so security fixes match Chrome's (§15.2). This is ADR-0002's decision.
- **D4: XFA requires V8 in the prebuilt binaries.** §4 S1's "XFA enabled (separate config)" can only be built from source. In practice XFA always brings in a JavaScript engine, which matters for the P2 static-XFA decision and the sandbox flavour in §5.3.
- **D5: Engine C++ fuzzing uses clang-cl, not MSVC libFuzzer.** This matches PDFium's only supported compiler. MSVC libFuzzer support is experimental.

## Options considered
1. **Accept §3 as written (23H2 minimum, Win2D assumed).** Rejected. The OS minimum would be out of servicing before v1, and Win2D on 2.x has no evidence.
2. **Pin to the newest consumer OS only (25H2+).** Rejected. It excludes 24H2 LTSC enterprise fleets.
3. **This ADR: 24H2 minimum, pinned versions with explicit spike gates.** Chosen.

## Decision
Adopt the pins above for Milestone 0 and M1. Rows marked UNVERIFIED must not be relied on in production code until their spike report is approved. If WinAppSDK, .NET, Win2D or PDFium moves during M0, update this ADR rather than drifting silently.

## Consequences
- S6 becomes the critical path. Win2D, AOT and Windows AI on 2.5.1 are all unproven.
- `fpdf_annot.h` and `fpdf_signature.h` are Experimental across the board. Every PDFium bump needs an API-diff check in CI, added to the weekly PDFium job in §15.2.
- Signing: the release pipeline can't be designed until the user decides how to get a code-signing identity (R-18).
- AI: on today's retail Copilot+ devices, `LanguageModel` needs a LAF token until Aion Instruct ships in Jan 2027. Plan the M7 evaluation on Aion Instruct.

## Verification
- S6 report: the dependency matrix is green on x64 and ARM64, or ADR-0006 picks the fallback.
- S1 report: per-function API check, and an arm64 build that is native (`dumpbin` machine AA64).
- Re-run this verification at the M0 exit gate and update every row's date.

## Follow-up tasks
- [ ] S6.x: Win2D 1.4.0 on WinAppSDK 2.5.1, both architectures
- [ ] S6.x: AOT vs R2R startup, plus the AOT/MSIX/ARM64 issue check
- [ ] S6.x: Windows AI retail OS minimum, and how `GetReadyState` behaves on R1/DEV
- [ ] S1.x: arm64 cross-build and the per-function API check
- [ ] User: code-signing route (R-18)
- [ ] Re-check the .NET 10.0.14 release date and the VS 18.11 default MSVC toolset

## Amendment A1 (2026-10-28): spike evidence
Results from the M0 spikes on DEV. DEV is an x64 Core Ultra 9 275HX with 64 GB and an RTX 5080, with a non-Copilot+ Intel NPU and Windows 11 26H2. Rows above are updated as follows:

| Row | New status | Evidence |
|---|---|---|
| Win2D 1.4.0 on WinAppSDK 2.5.1 | **VERIFIED on x64** (packaged and unpackaged, JIT and AOT). arm64 builds (ARM64 PE) but has **not run** (needs R2) | [S6](../spikes/S6.md) |
| Deployment mode | Native AOT **VERIFIED on x64** (zero IL warnings; needs `link.exe` and `vswhere.exe` on PATH). arm64 AOT **blocked** here (no ARM64 C++ libs). R2R builds on both architectures. **Keep R2R as the fallback**; choose the default after R1/R3 cold-start runs | S6 |
| Startup (indicative only) | Warm p50 to first frame on loaded DEV: AOT about 430 ms, R2R about 550 ms, JIT 530–670 ms, packaged JIT 879 ms. **Not a budget verdict** (machine at 100% CPU, not R1) | S6 |
| MSIX | `dotnet build` single-project MSIX works. Unsigned loose registration works in Developer Mode. **Tooling forces `MaxVersionTested=10.0.26100.0`; a post-build patch is needed** to declare 26300 | S6 |
| Windows AI | `GetReadyState()` returns `NotSupportedOnCurrentSystem` (LanguageModel and TextRecognizer) on a non-Copilot+ NPU. Without the `systemAIModels` capability it returns `CapabilityMissing`. `Windows.Media.Ocr` OCR worked (22–35 ms per page) | S6 |
| App Content Search | Types present. Without a LAF token: `UnauthorizedAccessException` (0x80070005), "Limited Access Feature is not available" | S6 |
| Text pattern on a custom surface | `ITextProvider`/`ITextProvider2`/`ITextRangeProvider` exist in 2.5.1 and work from C# | [S7](../spikes/S7.md) |
| PDFium prebuilt | All 65 checklist functions exported on x64 and arm64. Sizes 7.59 / 6.90 MB. Hardening: ASLR, high-entropy VA, DEP, CFG, /GS. CETCOMPAT x64 only. **No PDBs, unsigned** | [S1](../spikes/S1.md) |
| `FPDF_LoadCustomDocument` | **4 GiB limit** (`m_FileLen` is 32-bit on Windows). Use `FPDF_LoadMemDocument64` over a mapped view (ADR-0005) | [S3](../spikes/S3.md) |
| Local toolchain (DEV) | VS 2022 Build Tools, MSVC **14.44** x64 only (ADR pins 14.50 LTS), no clang-cl, Windows SDK **10.0.26100** only. PDFium's pinned `build/vs_toolchain.py` requires **SDK 10.0.28000.0**, so a from-source PDFium build is blocked here | S1, Opus check of `vs_toolchain.py` at the pinned build revision |
| .NET on DEV | SDK 10.0.300 / runtime 10.0.8 vs pin 10.0.403 / 10.0.14. M1 adds a `global.json` that enforces the pin. **Superseded by Amendment A2**: the repo now builds and ships with 10.0.14 | S1, S6 |
| COM interop | **Use source-generated COM** (`[GeneratedComInterface]`), not `[ComImport]`, for anything that must work under AOT | S6 |
| Ink | WinUI 3 has no `InkCanvas`, and Win2D 1.4.0 has no `DrawInk`. Pen pressure, tilt, barrel, eraser, `GetIntermediatePoints` and `PointerPredictor` are available | [S5](../spikes/S5.md) |

The OS-minimum deviation D1 is unchanged and still awaits the user's confirmation.

## Amendment A2 (2026-11-04): runtime 10.0.14 for GHSA-p3jp-9v83-76w9
The repo had been building with SDK 10.0.300 and shipping runtime 10.0.8, which is below the pin and below the fix for the .NET Brotli decoder out-of-bounds write (CVE-2026-111497, GHSA-p3jp-9v83-76w9, high). The shipped app is self-contained, so the runtime pack ships to users, and the Native AOT toolchain (`Microsoft.DotNet.ILCompiler`, `Microsoft.NETCore.App.Runtime.NativeAOT.win-*`) is covered by the same advisory.

| Row | New status | Evidence |
|---|---|---|
| .NET servicing release | **10.0.14**, released 2026-10-25, `security: true`. Its `cve-list` includes CVE-2026-111497, CVE-2026-70197, CVE-2026-102870 and CVE-2026-114148. Fixed in 10.0.14 | [releases.json](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [10.0.14 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md) |
| SDK that carries it | **10.0.403** (feature band 4xx) and 10.0.114 (band 1xx), each with runtime 10.0.14. The 10.0.14 entry lists no 3xx SDK, so the old 10.0.300 band is not an option. The repo takes **10.0.403**, as this ADR already pinned | same |
| `global.json` | `version` **10.0.403**, `rollForward` **latestPatch** (as the .NET row above says). A 10.0.300 or 10.0.402 SDK is now refused | n/a |
| CI | Every `actions/setup-dotnet` step installs the exact `10.0.403` (it was the floating `10.0.4xx`). On the x64 runner `windows-2025-vs2026` image 20261030.294.1 lists SDK 10.0.403 and runtime 10.0.14 preinstalled. The arm64 runner `windows-11-vs2026-arm` has no image manifest that could be found (the partner arm-windows-11 manifest is stale: it lists SDK 10.0.101), so its SDK comes from `setup-dotnet` alone, which is why the exact version is pinned | [Windows2025-VS2026-Readme](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-VS2026-Readme.md) |
| Lock files | No `packages.lock.json` pins a runtime or ILCompiler pack: those packs come from the SDK's bundled versions at publish time, and ShellHost has no lock file (its only packages are the SDK's AOT toolchain). Nothing in the lock files needed regenerating | the repo |
| NuGet audit | Stays **on**. There is no NU1903 suppression in the repo; the fix is the SDK, not a warning exception | n/a |
