# ADR-0002: PDF engine choice (PDFium) and build strategy
- Status: **Accepted (engine choice)**. **Proposed (release build)**, pending the S1 from-source half
- Date: 2026-10-27
- Deciders: Opus (approver), Sonnet (S1 evidence)

## Context
Master prompt §3.1 lays out why Quire uses PDFium instead of writing its own engine:
- Chrome's engine has absorbed the long tail of malformed real-world files.
- It is BSD-licensed.
- MuPDF is AGPL, which conflicts with ADR-0003.
- `Windows.Data.Pdf` can only render: no text, annotations or forms.

[S1](../spikes/S1.md) characterised the pinned prebuilt (chromium/8125, PDFium 158.0.8125.0):
- All **65 checklist functions are present and exported on x64 and arm64**. The x64 and arm64 export tables are identical (471 each).
- **23 of the 65 are Experimental.** Across all headers, 238 of 478 functions are, including all of the annotation and signature APIs.
- The x64 runtime smoke test passed: custom file access from bytes, render, text, bookmarks, signature enumeration, and saving both ways into memory. Garbage input returned typed error 3 rather than crashing.
- Sizes: x64 7.59 MB, arm64 6.90 MB, V8 build 32.3 MB. The deflated x64 build is 3.73 MB.
- No CRT DLL dependency. It imports 47 gdi32 and 3 user32 functions, which matters for the sandbox (ADR-0004).
- The prebuilt has ASLR, high-entropy VA, DEP, CFG and /GS. CETCOMPAT is on x64 only. There are no PDBs and no Authenticode signature.
- It is a **patched** upstream build. pdfium-binaries patches `BUILD.gn` to `shared_library` and changes `FPDF_EXPORT`, so a vanilla non-component build would export nothing.
- Building from source was **blocked**: this machine has no C++ toolchain.

## Options considered
1. **PDFium** (chosen). BSD, Chrome-grade compatibility, has every API area we need. Downsides: half the API is Experimental, and Windows ARM64 builds aren't documented.
2. **MuPDF.** Faster, with a cleaner API. AGPL, so excluded by ADR-0003 unless the licence changes.
3. **Windows.Data.Pdf.** Render-only. Rejected.
4. **Own engine.** Rejected (§3.1).

For the shipped binary:
- a. Ship the bblanchon prebuilt. It's unsigned, has no PDBs, is a trunk snapshot, and our hardening flags aren't set explicitly. **Rejected for release, accepted for M0/M1 development.**
- b. **Build from a Chromium stable branch ourselves** with the same shared-library export patch. Chosen for release.

## Decision
1. **Engine: PDFium.** All PDFium calls are confined to `Quire.Engine` / `Quire.Engine.Pdfium` (§0.2, A.5).
2. **Development builds (M0–M1):** the prebuilt pinned by SHA-256 in `spikes/_shared/pdfium/fetch.ps1`. It moves to `build/pdfium.lock` in M1.
3. **Release builds (from M2):** build from source on a **Chromium stable branch**, x64 and arm64.
   - Configuration: `pdf_enable_v8=false`, `pdf_enable_xfa=false`, `is_component_build=false`, a shared-library patch identical in effect to pdfium-binaries', and the AGG renderer.
   - Hardening flags: CFG, CETCOMPAT on x64, high-entropy ASLR, and /guard:ehcont if the toolchain accepts it.
   - Our own Authenticode signature and private PDB upload (§14).
4. **V8/XFA:** not built by default. If a policy ever enables JavaScript (§5.3), it's a separate engine flavour, treated as one switch because XFA needs V8.
5. **Experimental-API policy:** every Experimental function we wrap is listed in `src/Quire.Engine.Pdfium/EXPERIMENTAL.md`. The weekly PDFium job diffs exports and header signatures against `functions.csv` (the baseline from S1) and fails on any change to a function we wrap. Where a stable equivalent exists, use it.
6. **Hardening read-back in CI:** `symdiff.py`'s PE-flag check runs on every PDFium artefact, so a toolchain change can't silently drop a flag.

## Consequences
- M1 needs a machine with VS 2026 and the C++ workload (plus ATL/MFC and ARM64 tools), Windows SDK 10.0.28000.x and depot_tools. That is a user action (STATUS "Blocked") and also unblocks the C++ engine host.
- We carry a small downstream patch to PDFium's build files and must rebase it on every branch bump.
- An incremental save of an unmodified document is not a no-op (S1 Surprise 7; S4 investigates what gets appended). The save pipeline (ADR-0008) must not call incremental save when nothing changed.

## Verification
- S1 from-source rerun: both architectures build, the arm64 output is AA64, the exported set matches the prebuilt, and the hardening flags are present. **Until then, item 3 stays Proposed.**
- ARM64 control-flow hardening (PAC/BTI) has no CETCOMPAT equivalent in the PE flags read so far. Treat it as an M1 research item.

## Note (2026-10-28)
- DEV turned out to have VS 2022 Build Tools (MSVC 14.44, x64 only). That's enough to build Quire's C++ engine on x64 (S3b), but **not PDFium**: the pinned `build/vs_toolchain.py` requires Windows SDK `10.0.28000.0`, and DEV has only 26100. ARM64 C++ libraries are also missing. Unblocking needs an admin install of SDK 10.0.28000.x plus the VS ARM64 C++ tools.
- S3b: stock pdfium.dll fails to initialise under Win32k lockdown (ADR-0004 D-1). The from-source build should evaluate delay-loading gdi32/user32 and a GDI-free font path.
