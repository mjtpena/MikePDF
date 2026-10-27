# Milestone 0 spike plan (S1–S8)

- Status: Draft for execution
- Date: 2026-10-27
- Owner: Opus (plan, review, ADRs). Sonnet (spike implementation and reports)
- Source: [master prompt §4](../MASTER-PROMPT.md#4-mandatory-feasibility-spikes-milestone-0), with execution rules from Appendix A.2/A.3

This plan says, for each spike, what we need to learn, how we measure it, what hardware it needs, what counts as pass or fail, and the Sonnet tasks that produce the evidence. Version pins come from [ADR-0001](../adr/0001-platform-baseline.md). A spike must not start on a version that ADR-0001 marks UNVERIFIED without first recording the actual version it used.

---

## Common rules for every spike

**Where code lives.** All spike code goes under `/spikes/<S#>/`. Production code never references it. When a spike finishes, its code is frozen and kept for evidence, not deleted.

**Report.** Each spike writes `docs/spikes/<S#>.md` with these sections, in this order: Questions, Method, Results (tables), Surprises, Recommendation, Go/No-go, Open questions for Opus.

**Measurement standard.** These apply unless a spike section says otherwise:
- Release builds only. Record the commit hash, the build flags, and the PDFium revision.
- At least 30 samples for each timing metric. Report p50, p95 and max. If there are fewer samples, say so.
- "Cold" means the first run after a reboot or after flushing the standby list (`RAMMap -Et`). "Warm" means the second and later runs. Label every number as one or the other.
- Machines are plugged in, on the "Best performance" power mode, with background updates paused. Record CPU, RAM, GPU, display resolution and scale, OS build, and driver versions.
- Timing comes from `QueryPerformanceCounter` or ETW (WPR/WPA). Frame timing comes from PresentMon. Stopwatch numbers from the UI thread alone are not accepted.
- Any number that wasn't measured is written as **not measured** (§19.3).

**New dependencies.** A spike may try a package that has no ADR yet, for example CsWin32, MessagePack-CSharp, or System.Security.Cryptography.Pkcs. The report must list each one with its licence. The dependency gets into production only through an approved ADR.

**Review.** Opus reviews each report using A.3: is the measurement valid, does the evidence support the recommendation, and what would make it fail in production. Opus then writes or updates the target ADR.

---

## Dependency graph and order

```text
                 ┌────────────► S2 (render throughput) ◄──── S3 (IPC/sandbox)
 P0: pin prebuilt│
 PDFium binaries ├────────────► S4 (save fidelity)
 (bblanchon)     ├────────────► S8 (signatures)
                 ├────────────► S7 (UIA text pattern) ◄──┐
                 └────────────► S3                       │
 S1 (build from source) ── independent; feeds ADR-0002   │
 S6 (WinAppSDK 2.x matrix) ──────────────────────────────┴──► S2 presenter, S5 ink
```

- **Task P0** (Sonnet, small) pins one `bblanchon/pdfium-binaries` release into `spikes/_shared/pdfium/` using a fetch script that checks the SHA-256. Every spike except S1's from-source work and S5 can start as soon as P0 is done.
- **Wave 1, in parallel:** S1, S3, S6, S4, S8.
- **Wave 2:** S2 (needs S3's transport and S6's Win2D verdict), S7 (needs S6), S5 (needs S6 and a Surface device).
- **Critical path:** S6 → S2 → ADR-0006. S6 is the earliest point where the WinUI/Win2D/AOT stack could fail.

## Hardware matrix

| Id | Spec (§11) | Needed by | Availability |
|---|---|---|---|
| DEV | x64 dev box, 16+ GB RAM, 150 GB free SSD | S1, S3, S4, S6, S7, S8 | Assumed available (OPEN: confirm) |
| R1 | x64, 4 cores, 8 GB, iGPU, SSD | S2, S3, S6 | **OPEN** |
| R2 | Snapdragon X Copilot+ PC, 16 GB | S2, S3, S6 (native ARM64 + Windows AI APIs) | **OPEN** |
| R3 | x64 8+ cores, 32 GB, dGPU, 4K display | S2 | **OPEN** |
| SURF | Surface Pro with a pressure pen | S5 | **OPEN** |
| VM-V | Windows VM with Acrobat Reader, Edge, Chrome, Firefox | S4, S8 | Can build. Acrobat EULA check is OPEN (see risks R-15) |
| CAM | Phone or camera that records at 240 fps or faster | S5 | **OPEN** |

If a spike runs without its target hardware, its report says so and its go/no-go is **provisional**. The gate in §17 can't pass on provisional performance numbers.

---

## S1: PDFium build and API surface

**Target ADR:** ADR-0002 (engine and build strategy). It also feeds `build/pdfium.lock`.

**Questions**
1. Can we build PDFium from source on a Windows x64 host for `target_cpu="x64"` and `"arm64"` in three configurations: (a) default with no V8 and no XFA, (b) V8 on, (c) XFA on (which also needs V8)?
2. Which toolchain does PDFium require? Is it the bundled clang-cl? Which Windows SDK? Is Visual Studio needed at all?
3. Can we apply the §13.2 hardening flags (`/guard:cf`, `/CETCOMPAT`, ASLR/high-entropy VA) through GN? Do the resulting binaries show those flags?
4. Component (DLL) or static linking: which do we ship, and what does each mean for symbol export?
5. Does the prebuilt bblanchon DLL export every function we need? How does it differ from our own build?
6. Are any of the §6.2 functions marked experimental (`// Experimental API.`) in the headers? Experimental APIs can change between revisions, so these become a pinning risk.
7. Can we produce an ASan build and a libFuzzer build of the engine wrapper against our PDFium?

**API presence checklist.** Confirm each against the headers at the pinned revision. Names are candidates until checked (§0.2 rule 2).

| Area | Candidate functions |
|---|---|
| Load | `FPDF_InitLibraryWithConfig`, `FPDF_LoadCustomDocument`, `FPDF_GetLastError`, `FPDF_GetPageCount`, `FPDF_GetPageSizeByIndexF` |
| Render | `FPDF_RenderPageBitmapWithMatrix`, `FPDF_RenderPageBitmap_Start`, `FPDF_RenderPage_Continue`, `FPDF_RenderPage_Close`, `FPDFBitmap_CreateEx`, `FPDF_FFLDraw` |
| Text | `FPDFText_LoadPage`, `FPDFText_CountChars`, `FPDFText_GetUnicode`, `FPDFText_GetCharBox`, `FPDFText_GetLooseCharBox`, `FPDFText_GetCharIndexAtPos`, `FPDFText_FindStart`/`FindNext`, `FPDFText_GetRect` |
| Doc | `FPDFBookmark_GetFirstChild`/`GetNextSibling`/`GetDest`, `FPDFLink_GetLinkAtPoint`, `FPDFLink_Enumerate`, `FPDF_GetNamedDestByName`, `FPDFDest_GetLocationInPage`, `FPDF_GetMetaText` |
| Annot | `FPDFPage_CreateAnnot`, `FPDFAnnot_SetAP`, `FPDFAnnot_AppendAttachmentPoints`, `FPDFAnnot_AddInkStroke`, `FPDFAnnot_SetColor`, `FPDFAnnot_SetStringValue`, `FPDFAnnot_GetSubtype` |
| Forms | `FPDFDOC_InitFormFillEnvironment`, `FORM_OnAfterLoadPage`, `FORM_OnLButtonDown`/`Up`, `FORM_OnChar`, `FORM_OnKeyDown`, `FORM_ForceToKillFocus`, `FPDFAnnot_GetFormFieldType` |
| Edit/Save | `FPDF_SaveAsCopy` with `FPDF_INCREMENTAL` and `FPDF_NO_INCREMENTAL`, `FPDF_SaveWithVersion`, `FPDFPage_SetRotation`, `FPDFPage_Delete`, `FPDF_MovePages` |
| PPO | `FPDF_ImportPagesByIndex`, `FPDF_ImportPages`, `FPDF_ImportNPagesToOne` |
| Flatten | `FPDFPage_Flatten` |
| Struct | `FPDF_StructTree_GetForPage`, `FPDF_StructElement_GetType`, `FPDF_StructElement_GetMarkedContentID` |
| Signature | `FPDF_GetSignatureCount`, `FPDF_GetSignatureObject`, `FPDFSignatureObj_GetContents`/`GetByteRange`/`GetSubFilter`/`GetTime`/`GetDocMDPPermission` |
| Misc | `FPDFPage_GetThumbnailAsBitmap`, `FPDFDoc_GetAttachmentCount`, `FPDFAttachment_GetFile` |

**Measurements:** full build time per config and architecture; `pdfium.dll` size per config and architecture (raw and compressed inside the MSIX); exported symbol count; diff of exported symbols against the prebuilt build; `dumpbin /headers /loadconfig` output showing the machine type and the CFG and CET flags.

**Pass / fail**
- Pass: the no-V8 build succeeds natively for both x64 and arm64 (`dumpbin` machine `AA64` on arm64). Every function in the checklist exists in the pinned headers, or each missing one has a written workaround. The hardening flags show up in the binary.
- Pass: no-V8 `pdfium.dll` is **≤ 15 MB** per architecture. That is a working budget so the whole MSIX stays within 60 MB, and Opus may revise it.
- Fail: the arm64 build needs emulation, or a P0 area has no API and no workaround.
- V8 and XFA builds: report their size and build cost only. They don't block the gate.

**Hardware:** DEV.

**Sonnet tasks**
| # | Task | Output |
|---|---|---|
| S1.1 | depot_tools and PDFium checkout bootstrap, pinned to a branch | `spikes/S1/fetch.ps1` |
| S1.2 | Three GN arg sets plus a build script for both architectures | `spikes/S1/args/*.gn`, `build.ps1` |
| S1.3 | Export diff tool: `dumpbin /exports` on our build and on the prebuilt, then a sorted diff | `spikes/S1/symdiff.ps1` |
| S1.4 | API checklist checker: parse the public headers, flag missing and experimental APIs | `spikes/S1/apicheck.py` + CSV |
| S1.5 | ASan/libFuzzer feasibility: one trivial fuzz target that links against our build | `spikes/S1/fuzz/` |
| S1.6 | Report | `docs/spikes/S1.md` |

---

## S2: Rendering pipeline throughput

**Target ADR:** ADR-0006 (rendering surface and tile strategy). It may also revise §11 budgets through an ADR.

**Questions**
1. Which tile size, 256 or 512 px, gives the best time to first tile and the best scroll smoothness at 100%, 200% and 400%?
2. What does it cost to upload a tile to the GPU with Win2D (`CanvasBitmap` from a shared buffer, drawn in a `CanvasVirtualControl`) compared with Composition (`CompositionDrawingSurface` through the interop interface)?
3. How much memory does each cached tile use on the GPU and the CPU? Is a compressed CPU tier worth it? No new codec dependency without an ADR. Measure an uncompressed tier and one candidate.
4. Can the pipeline hold the 60 fps / no-frame-over-50 ms / no-blank-area budgets on R1 while doing a fling?

**Fixtures (10 files):** text-heavy book page, two-column paper, dense vector map, JBIG2 scan, JPX photo, transparency groups, huge page (200×200 in), Type 3 fonts, CJK text, 2,000-page text document. Each comes from the corpus manifest with its licence recorded.

**Measurements**, each per tile size × zoom × fixture × device:
- Time to first tile: render request → pixels on screen. p50 and p95.
- Render throughput in tiles per second, inside the engine (PDFium only) and end to end.
- Shared-memory transfer plus GPU upload time per tile.
- Memory: GPU surface bytes per tile (measured, not computed) and process working set after a full scroll.
- A scripted 10 s fling, captured with PresentMon: frame-time p95 and max, plus a blank-area count from a placeholder-coverage probe.

**Pass / fail**
- Pass: on R1, warm time to first tile for a typical text page at 100% is **≤ 150 ms** p95. That leaves room inside the 250 ms warm-open budget.
- Pass: during the fling on R1, no frame goes over 50 ms and no blank area appears, with the low-resolution fallback on.
- Pass: GPU upload of a 512 px tile is **≤ 2 ms** p95 on R1.
- Fail: neither surface meets the fling budget on R1. That triggers an ADR to revise the budget or the architecture.

**Hardware:** R1, R2, R3 (4K at 150% and 200% scale).

**Sonnet tasks**
| # | Task | Output |
|---|---|---|
| S2.1 | Native bench: PDFium → BGRA tile, all sizes and zooms, CSV output | `spikes/S2/engine-bench/` |
| S2.2 | Shared-memory slot ring (reusing S3's section code) plus a C# reader | `spikes/S2/transport/` |
| S2.3 | Presenter A: Win2D `CanvasVirtualControl` with a tile cache | `spikes/S2/presenter-win2d/` |
| S2.4 | Presenter B: Composition surfaces with a tile cache | `spikes/S2/presenter-comp/` |
| S2.5 | Scripted fling driver plus PresentMon capture and parse script | `spikes/S2/fling/` |
| S2.6 | Report with a recommendation on tile size, cache policy and surface | `docs/spikes/S2.md` |

---

## S3: Process isolation and IPC

**Target ADRs:** ADR-0004 (process model and sandbox) and ADR-0005 (IPC transport and serialisation).

**Questions**
1. Can a packaged full-trust MSIX process create an AppContainer profile and launch the engine inside it with **zero capabilities**? This means `CreateAppContainerProfile` plus `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`. Verify both against the current Win32 docs.
2. Can the engine inside the AppContainer connect to a named pipe that has a DACL granting only the current user and the AppContainer SID? Can it map a section handle passed through `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`?
3. Inside the AppContainer, can PDFium render text in non-embedded fonts using its default Windows font path (GDI)? If not, how much work is a broker-provided font provider (`FPDF_SYSFONTINFO`)?
4. Which process mitigation policies can the engine run under without breaking PDFium? Check in particular Win32k lockdown, dynamic-code prohibition, the signature policy on DLL loading, child-process prohibition, and the CET shadow stack.
5. Does a Job Object memory limit stop a runaway engine cleanly, and can the broker detect that it happened?
6. If AppContainer fails, how does the fallback compare: a low-integrity restricted token?

**Measurements**
- Round-trip time for `Ping`, p50/p99/max, n ≥ 10,000.
- `RenderTile` RTT excluding render time, for a 512 px tile through the shared-memory slot.
- Cold spawn of the engine up to `Hello`.
- Kill the engine, respawn it, and reopen a 20-page document. The §11 budget is ≤ 2 s from crash to restored document, so measure the engine's share of that.
- MessagePack encode and decode cost for the largest control message (`GetCharBoxes` on a dense page).

**Negative-access tests.** Each must fail from inside the engine. Record the exact error codes:
- open `%USERPROFILE%\Documents\*`, `%TEMP%`, the Quire package directory for write, and a UNC path
- connect a TCP socket
- `CreateProcess`
- enumerate other processes' handles
- read the clipboard
- load an unsigned DLL

**Pass / fail**
- Pass: every negative test is denied. Ping RTT p99 is **≤ 1 ms**. On R1, spawn plus a warm 20-page open takes **≤ 500 ms**. Fonts render correctly, natively or through a provider. Every mitigation that can't be applied is recorded with the reason.
- Fail: a zero-capability AppContainer can't run PDFium, and the low-IL fallback also fails a negative test. That calls for an architecture amendment.

**Hardware:** DEV, R1, R2.

**Sonnet tasks**
| # | Task | Output |
|---|---|---|
| S3.1 | C# sandbox launcher: AppContainer profile, capabilities, handle list, Job Object, mitigation flags | `spikes/S3/launcher/` |
| S3.2 | C++20 engine stub: pipe client, section mapper, single engine thread with a work queue, PDFium render | `spikes/S3/engine/` |
| S3.3 | Negative-access suite that runs inside the engine and reports to the broker | `spikes/S3/negative/` |
| S3.4 | Latency and restart benchmark | `spikes/S3/bench/` |
| S3.5 | Font test: render fixtures that use non-embedded fonts, compare with an unsandboxed render (SSIM) | `spikes/S3/fonts/` |
| S3.6 | Low-IL restricted-token fallback with the same suites | `spikes/S3/lowil/` |
| S3.7 | Report | `docs/spikes/S3.md` |

---

## S4: Save fidelity

**Target ADR:** ADR-0008 (save pipeline). It also defines the scope of Quire's own appearance-stream generator.

**Questions**
1. For each P0 annotation type, does PDFium write an `/AP` stream when it creates the annotation and saves? Is that appearance correct in viewers that don't regenerate appearances?
2. For each AcroForm field type, after setting the value through the form-fill environment and saving, is `/V` correct, is the widget `/AP` regenerated, and is `/NeedAppearances` set?
3. Is the incremental save output valid (xref, trailer `/Prev`) and does it preserve existing signatures byte for byte?
4. What do Acrobat Reader, Edge, Chrome and Firefox (pdf.js) each show?

**Operation matrix** (rows), each saved incrementally and as a full copy:
- Annotations: Highlight, Underline, StrikeOut, Squiggly, Text (sticky note) with Popup and an `/IRT` reply, FreeText, Ink with pressure-varying width, Square, Circle, Line with arrow line endings, Polygon, PolyLine, Stamp (standard name), Stamp (image).
- Fields: text (single-line, multiline, comb), checkbox, radio group, combo (editable and not), list (multi-select), push button.

**Columns:** `qpdf --check` result · `/AP` present · renders correctly in Acrobat, Edge, Chrome and Firefox (yes/partial/no plus a screenshot) · properties still there after reopening in PDFium · signature preserved (incremental only, on a pre-signed fixture).

**Pass / fail:** this spike gathers facts, and the full matrix is the deliverable. It is a **go** if every gap can be closed by Quire writing its own appearance streams and dictionaries through the PDFium edit APIs or `Quire.PdfWriter`. It is a **no-go** if PDFium's incremental save corrupts existing signatures or produces structurally invalid output that we can't post-process.

**Hardware:** DEV and VM-V.

**Sonnet tasks**
| # | Task | Output |
|---|---|---|
| S4.1 | Fixture generator CLI: applies every operation through PDFium and saves both ways | `spikes/S4/gen/` |
| S4.2 | Structural checks: `qpdf --check` plus a `qpdf --json` object dump per output | `spikes/S4/check.ps1` |
| S4.3 | Viewer screenshot capture on VM-V (manual capture is fine for the spike, and the report says so) | `spikes/S4/shots/` |
| S4.4 | Matrix report with object-level diffs for every failing cell | `docs/spikes/S4.md` |

---

## S5: Ink latency

**Target ADR:** ADR-0007 (ink stack).

**Questions**
1. Which ink APIs exist in WinUI 3 on the pinned Windows App SDK? WinUI 3 has historically had no `InkCanvas`, so verify what 2.x ships.
2. Candidates: (a) whatever WinUI or App SDK offers natively; (b) a custom pipeline using pointer input (with `GetIntermediatePoints`) drawn into a Win2D swap chain; (c) option (b) plus DirectComposition delegated ink trails (`IDCompositionInkTrailDevice`) for low-latency prediction. Check that (c) works from WinUI 3.
3. How faithful is pressure (levels and curve), and do tilt and the barrel button come through?
4. Does palm rejection work while the pen is in range?
5. How do strokes become `/Ink` annotations: smoothing, point decimation, batching strokes into annotations, and generating `/AP` with variable width?

**Measurements:** pen-to-pixel latency from 240 fps video (the frame where the pen tip moves to the frame where ink appears), n ≥ 30 strokes for each candidate, p50/p95. Pressure: the ratio of distinct levels to hardware levels. Palm: count of stray strokes in a scripted 5-minute writing session.

**Pass / fail:** pass if one candidate reaches **≤ 25 ms p95** on SURF with no stray palm strokes and monotonic pressure mapping. Otherwise the budget is revised through an ADR with the measured best result.

**Hardware:** SURF and CAM. **Blocked until those are available.**

**Sonnet tasks:** S5.1 inventory the API surface (docs plus a compile probe) · S5.2 build candidates a/b/c · S5.3 capture protocol and frame-counting script · S5.4 prototype the `/Ink` converter (output checked against S4's matrix) · S5.5 report.

---

## S6: Windows App SDK 2.x compatibility

**Target:** ADR-0001 amendments and the dependency matrix. It is an input to ADR-0006 and ADR-0009.

**Questions.** On the pinned Windows App SDK and .NET 10, for x64 and arm64:
1. Does the pinned Win2D-WinUI build load and draw?
2. Does Native AOT publish a WinUI app that runs without trimming warnings we can't fix? If not, compare ReadyToRun.
3. Does single-project MSIX packaging work with an AppContainer child engine and an arm64-native C++ engine binary?
4. Do the Windows AI APIs (`TextRecognizer`, `LanguageModel`) feature-detect correctly, working on R2 and failing cleanly on R1 and DEV?
5. App instancing and redirection, the file type association, and the manifest schema for thumbnail and preview handlers (§8.11).
6. Does App Content Search exist in the pinned SDK, and what does its Limited Access Feature token need?
7. Which UI automation driver can drive WinUI 3 today? This feeds ADR-0009.

**Measurements:** cold start to an interactive empty window for JIT, R2R and AOT on R1 and R3 (§11: ≤ 600 ms on R1, ≤ 350 ms on R3); MSIX size per architecture; process architecture on R2 checked through `IsWow64Process2` and Task Manager.

**Pass / fail:** pass if there is a working matrix of {WinAppSDK, Win2D, AOT or R2R, MSIX, Windows AI} on both architectures, and cold start fits the budget with at least one deployment mode. A component that fails gets a documented workaround, or ADR-0006 switches to the Composition fallback (§20).

**Hardware:** DEV, R1, R2, R3.

**Sonnet tasks:** S6.1 hello-world WinUI app with a Win2D canvas · S6.2 AOT, R2R and JIT publish profiles plus a startup bench · S6.3 MSIX packaging with the engine stub from S3 · S6.4 Windows AI feature-detect probe · S6.5 manifest probes for file types, shell handlers and instancing · S6.6 UI-driver smoke test with FlaUI and the current Appium Windows driver · S6.7 report.

---

## S7: Accessibility of a custom-rendered surface

**Target ADR:** ADR-0006 (surface choice must support UIA). It also feeds the design of `Quire.Accessibility`.

**Questions**
1. Does WinUI 3 expose `ITextProvider` and `ITextRangeProvider` (in `Microsoft.UI.Xaml.Automation.Provider`) so a custom `FrameworkElementAutomationPeer` can implement them? Verify against the pinned SDK metadata.
2. Can text ranges map onto PDFium character indices, with `Move` and `ExpandToEnclosingUnit` for Character, Word, Line, Paragraph and Page, and with `GetBoundingRectangles` built from character boxes, correct on all four rotations?
3. Do selection-changed and text-changed events reach Narrator and NVDA?
4. Reading order: does the structure tree drive it for tagged files, and does the heuristic work for untagged two-column files?

**Fixtures:** a tagged single-column document; an untagged two-column paper; a page with `/Rotate 90` and a non-zero CropBox origin; a page with ligatures (fi, fl) and hyphenation.

**Pass / fail:** pass if Narrator reads each fixture by character, word and line in the expected order (a scripted manual pass with a transcript in the report), bounding rectangles land on the glyphs for all four rotations (screenshot overlay), and Accessibility Insights FastPass shows zero failures on the spike window. Fail if WinUI 3 doesn't let us implement the Text pattern. That forces an ADR on a hosting approach, for example a Win32 child HWND with a raw UIA provider.

**Hardware:** DEV. NVDA (free) installed.

**Sonnet tasks:** S7.1 page control plus automation peer · S7.2 text range provider over PDFium character data (pulled from the engine stub) · S7.3 overlay debug view of the bounding rectangles · S7.4 Narrator/NVDA script and transcripts · S7.5 report.

---

## S8: Signature handling

**Target ADR:** ADR-0012 (trust lists), plus a feasibility verdict for P1 signing. It also informs the scope of `Quire.PdfWriter`.

**Questions**
1. Does the PDFium signature API give us enough to validate (byte range, contents, subfilter, time, DocMDP), or do we need our own parsing in `Quire.PdfWriter`?
2. Can .NET `SignedCms` (System.Security.Cryptography.Pkcs) verify `adbe.pkcs7.detached` and `ETSI.CAdES.detached` signatures, and does `X509Chain` against the Windows store give trust status, including offline revocation behaviour?
3. Can we write an incremental update that adds a signature field and widget plus a `/V` dictionary with a `/Contents` placeholder (16 KB to start) and a `/ByteRange` placeholder, then patch both in place after signing, without upsetting the xref?
4. Can CNG keys from the store (a test cert, plus a smart card or Windows Hello key if available) sign through `CmsSigner`?
5. Can we detect changes made after signing (comparing revisions) on known attack fixtures?

**Fixtures:** valid signed PDFs (RSA and ECDSA); a tampered byte; an incremental annotation added after signing (allowed by DocMDP); a page-content change after signing (not allowed); a shadow-attack sample and an incremental-save-attack sample from published PDF security research (check the licence before redistributing).

**Measurements:** a classification table per fixture (Quire's verdict, Acrobat Reader's verdict, and agree or disagree); output size; signing time.

**Pass / fail:** pass if every fixture is classified correctly and the prototype's output is shown as cryptographically valid in Acrobat Reader (an untrusted-identity warning is acceptable for a self-signed test cert). Fail on any false "Valid" for a tampered or attack fixture. A failure there blocks P1 signing until it is resolved.

**Hardware:** DEV and VM-V. Smart card or Windows Hello is optional, and the report says so if it wasn't tested.

**Sonnet tasks:** S8.1 enumeration tool through PDFium · S8.2 SignedCms/X509Chain validator · S8.3 incremental signature writer prototype · S8.4 fixture set plus manifest with licences · S8.5 report.

---

## Exit criteria for M0 (from §17)

- [ ] S1–S8 reports exist and are approved by Opus
- [ ] ADR-0001 through ADR-0010 are written and Accepted
- [ ] Architecture in §5 is confirmed or amended by ADR
- [ ] Pinned versions are recorded (ADR-0001 plus `build/pdfium.lock`)
