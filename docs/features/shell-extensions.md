# Feature spec: Explorer thumbnail provider and preview handler

- Status: Draft for Opus review. **Needs a transport spike (S6b) before implementation** (section 3.2)
- Milestone: M3 (full reader)
- Master prompt: §8.11, §5.1 (Quire.ShellExt), §13.1 and the B3 boundary in `docs/threat-model/README.md`, §11 (no hard budget; §8.11 sets 2 s and 1 s), risk R-16
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: `MikePDF.Broker` (launcher, supervisor, client), ADR-0004 (sandbox profile), ADR-0015 decision 6 (handlers were excluded from the Store preview and return here), S6 (manifest findings)
- Related: [settings-policies](settings-policies.md) (policy keys), [file-association-jumplist](file-association-jumplist.md)

Conventions are in [README.md](README.md).

## 1. Constraints

1. **No PDFium inside Explorer or any shell host process** (§8.11). Handlers spawn a sandboxed engine and exchange pixels through IPC.
2. **The sandbox launch code is C#** (`MikePDF.Broker`). The handler must be a native COM DLL, because managed in-process shell extensions are not supported by Microsoft (S6 recorded this; the current wording was not re-verified).
3. S6 found that the manifest registrations for a thumbnail handler (`desktop2:ThumbnailHandler`) and a preview handler (`desktop2:DesktopPreviewHandler`) validate, but **that the handlers do not work yet**: S6's COM classes pointed at a placeholder. The COM surrogate server entry must be at Application level. The surrogate (`dllhost.exe`) is **not** sandboxed.
4. **Handlers only take effect when MikePDF is the handler Windows associates with `.pdf`.** Shell handlers hang off the file type's ProgID. If the user's default is Edge or Acrobat, Explorer uses their handlers. This is Windows behaviour for packaged apps as far as is known, but it is **UNVERIFIED** for MikePDF; the spike confirms it. It makes the default-app prompt ([file-association-jumplist](file-association-jumplist.md)) part of the feature's value.
5. Timeouts from §8.11: thumbnail **2 s**, preview first page **1 s target**. Neither is measured yet.

## 2. User stories

1. As an Explorer user, I see the first page of each PDF as its icon, quickly, and Explorer never freezes because of one bad file.
2. As an Explorer user, I press Alt+P and see the document in the preview pane and scroll through it.
3. As an IT admin, I can switch both handlers off by policy and the choice is visible in About.
4. As a security-minded user, opening a folder full of hostile PDFs cannot crash Explorer or run code with my privileges.
5. As a user with a sensitive document, I can turn thumbnails off in MikePDF and know that Windows' own thumbnail cache is a separate thing.

## 3. Detailed behaviour

### 3.1 Components

| Component | Language | Trust | Job |
|---|---|---|---|
| `MikePDF.ShellExt.dll` | C++20 | In-process COM server, hosted by `dllhost.exe` (thumbnails) and the preview host (previews) | Implements the shell interfaces. **Parses nothing.** Passes a file handle to ShellHost, receives pixels, validates them, returns them to the shell. |
| `MikePDF.ShellHost.exe` | C#, packaged, full trust | Trusted | A small broker for shell requests. Reuses `MikePDF.Broker` (`EngineSupervisor`, `EngineClient` and `ReplyChecks`, so every engine reply is validated as untrusted). Starts at most N shell engines, enforces deadlines, caches failures. |
| `MikePDF.Engine.exe` (shell kind) | C++20 | Sandboxed (ADR-0004, zero capabilities) | Opens one document, renders one page. |

A new `EngineKind::Shell` (one document, small slot ring, lower memory limit) is added beside `Document` and `Pool`.

### 3.2 Transport: three candidates, none verified

The handler has to reach ShellHost, and ShellHost may not be running. OPEN QUESTION OQ-SHELL-1 asks Opus to pick after **spike S6b** (continues R-16). The spike must answer, on a real package install on x64 and ARM64:

| Candidate | What the handler does | What must be proven |
|---|---|---|
| A. Named pipe and on-demand launch | Connects to a per-user pipe; if absent, starts `MikePDF.ShellHost.exe` from the package directory, then retries | A process inside the package's COM surrogate can start a sibling package executable with package identity; the pipe's DACL lets the handler (same user and package) connect, and a different package or user cannot; startup fits the budget |
| B. COM out-of-process server | `CoCreateInstance` of an `ExeServer` class declared in the manifest; the shell service is a COM interface | A proxy/stub or type library can be registered in a package; marshalling overhead; AOT-safe COM (S5: classic `[ComImport]` fails under Native AOT) |
| C. Sandbox launch inside the handler | The handler itself creates the AppContainer engine | Requires a C++ port of the launcher (about 1,000 lines of security-critical code) and runs it inside a non-sandboxed surrogate; **not recommended** |

The rest of this spec describes candidate A's messages because they are concrete and fuzzable; if B wins, the same contract is expressed as a COM interface and section 11 changes shape.

ShellHost starts on demand and exits **60 seconds** after its last request (proposed) so it never sits in memory for users who rarely open a folder of PDFs. It is a single instance per user session (a named mutex).

### 3.3 Thumbnail provider

Interfaces: `IThumbnailProvider`, `IInitializeWithItem` (preferred: gives a path, so the handler opens a read-only handle with full sharing and passes it on), and `IInitializeWithStream` as a fallback. The stream path copies bytes to a file in a private area and is capped at **32 MiB**; above that it fails and Explorer shows the generic icon (OPEN QUESTION OQ-SHELL-3: register for item only, which would drop the stream path entirely).

`GetThumbnail(cx)`:

1. Clamp `cx` to 16 to 1,024.
2. Ask ShellHost to open the document and render page 1 to fit `cx` x `cx` (aspect preserved, never upscaled beyond 2x its natural size).
3. Return a 32-bit premultiplied BGRA `HBITMAP` with `WTSAT_ARGB`, opaque white page, no border or badge.
4. Total time limit **2 s** from entry to return, of which the handler gives ShellHost **1.7 s**. On expiry: cancel the request (ShellHost kills the engine), return a failure `HRESULT`; Explorer falls back to the generic icon. Never block longer; never leave an engine behind.

Rules: `/Rotate` and CropBox are applied (the same displayed box as the viewer). Annotations are drawn; form fields are drawn without highlight. Password-protected files fail (no password prompt from a shell). Zero-byte, non-PDF, damaged and unsupported files fail. A file larger than 4 GiB uses the same path as the viewer (mapped open); I/O is bounded by the deadline.

**Concurrency.** Explorer asks for many thumbnails at once. ShellHost runs at most `shell.maxEngines` (default **3**, proposed) engines. Requests queue newest first; a request whose deadline has passed while queued is dropped without work. The queue holds at most 64 requests; the oldest is dropped beyond that.

**Negative cache.** A file (file ID, size, last-write time) that failed or timed out is not tried again for 10 minutes, so a crash-inducing file cannot become a crash loop each time Explorer repaints. The cache lives in ShellHost's memory only.

### 3.4 Preview handler

Interfaces: `IPreviewHandler`, `IOleWindow`, `IObjectWithSite` (for `IPreviewHandlerFrame`), `IPreviewHandlerVisuals`, and the initialisation interface as above. The handler creates a child window inside the preview pane's rectangle.

- `DoPreview`: shows "Loading..." at 300 ms if page 1 is not ready, and page 1 when it is. Target **1 s** to page 1; hard limit 5 s, after which the pane shows "Preview isn't available for this file" (limits proposed).
- The preview is a **vertical continuous view at fit-width**, one page after another, with the system scroll bar and mouse wheel. Pages render on demand through ShellHost over a session that stays open while the preview is alive. OPEN QUESTION OQ-SHELL-5: scrollable (assumed) or first page only. Scrollable is the common expectation, and costs the session protocol of section 11.
- Resizing the pane re-renders visible pages at the new width after 100 ms of quiet.
- Keyboard: arrows, Page Up/Down, Home, End scroll; Tab hands focus back to Explorer through `TranslateAccelerator`/`IPreviewHandlerFrame`. No zoom, no text selection, no search, no links, no JavaScript, no form interaction (read-only preview).
- Visuals: background and text colours come from `IPreviewHandlerVisuals::SetBackgroundColor` and `SetTextColor`; the page itself stays white. Per-monitor DPI: the handler reads `GetDpiForWindow` and re-renders on `WM_DPICHANGED`.
- `Unload` closes the ShellHost session, destroys the window, and releases everything within 200 ms.
- A preview of a modified file: the preview pane shows the file on disk. It does not show unsaved edits (they are not in the file).

### 3.5 Settings and policy

| Key | Where | Effect |
|---|---|---|
| `shell.thumbnails` (user, default on) | `settings.json` | Off: ShellHost answers every thumbnail request with failure. |
| `shell.preview` (user, default on) | `settings.json` | Same for previews. |
| `ShellThumbnailsEnabled`, `ShellPreviewEnabled` (policy, DWORD) | `HKLM\Software\Policies\MikePDF` | 0 forces off and the user setting is locked ([settings-policies](settings-policies.md)). The **handler reads the policy itself** from HKLM (a cheap read), so a policy-off machine never launches ShellHost. |

Turning thumbnails off in MikePDF does **not** delete thumbnails Windows already cached; the Settings text says so. Registration is part of the package, so these switches cannot unregister the handlers; they make them decline.

### 3.6 Failure behaviour

Every failure returns a failure `HRESULT` quickly and leaves no engine behind: the job object around each shell engine kills it, ShellHost kills any engine that outlives its request deadline, and when ShellHost itself dies the handler's next call starts a new one. The shell's own behaviour on failure (generic icon, retry later) is outside our control. The handler never shows a dialog and never writes to disk (except the stream-fallback temp file, which is deleted when the call returns).

## 4. Keyboard, pen and touch

Preview handler: arrows, Page Up/Down, Home, End, mouse wheel, touch pan and touch fling (via `WM_VSCROLL`/`WM_MOUSEWHEEL`/`WM_POINTER*` or gesture messages; the child window opts into pan gestures). Thumbnail provider has no interaction. Pen acts as mouse.

## 5. Accessibility

- The preview child window is a real `HWND` with a default UIA provider. It is named "PDF preview" and its value is "Page {n} of {total}", updated by scrolling (a minimal `IRawElementProviderSimple` over `WM_GETOBJECT`). OPEN QUESTION OQ-SHELL-6: this gives a screen reader the page position but **not** the document text. A text-exposing preview would need a text layer from the engine and a UIA text provider in a native DLL. It is a known limitation, to be listed in the accessibility statement, and not claimed as accessible content.
- The system scroll bar is used so scrolling is operable by keyboard and AT without custom code.
- High contrast: the pane background follows `IPreviewHandlerVisuals`; the page stays as the document draws it. The placeholder messages use the system text colour.
- Thumbnails have no text; Windows supplies the item name.

## 6. Error states

| Situation | Thumbnail | Preview |
|---|---|---|
| Timeout | failure `HRESULT`, generic icon | "Preview isn't available for this file" |
| Password-protected | failure | "This file is protected by a password. Open it in MikePDF to read it." |
| Damaged or unsupported | failure | "Preview isn't available for this file" |
| Policy or setting off | failure | "Preview is turned off" (policy: "Preview is turned off by your organisation") |
| ShellHost cannot start | failure | "Preview isn't available" |
| Engine crash | failure; negative cache | message above |
| File too large for the stream fallback | failure | message above |

No message includes a file path or document content.

## 7. Edge cases

- **Rotated pages, CropBox offsets:** applied by the engine's page transform as in the viewer; fixtures include `/Rotate 90`, a non-zero-origin CropBox and a page with a CropBox outside the MediaBox.
- **Encrypted with an empty user password:** opens normally.
- **Permissions:** a document that forbids copying or printing still previews and thumbnails (they are viewing, not copying); a policy for "no preview of protected files" is not planned.
- **Huge documents:** a 5,000-page, 1.2 GB scan: first page within the deadline or a failure; no page-size scan of the whole file.
- **Network and cloud files:** an online-only OneDrive file is **not** hydrated by a thumbnail request (opening the handle would trigger a download). The handler checks the recall attributes (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` and `FILE_ATTRIBUTE_RECALL_ON_OPEN`) and fails without opening, so browsing a folder does not download every file. The preview does the same. OPEN QUESTION OQ-SHELL-7: should the preview pane hydrate on explicit user selection? (Windows itself does not for most types.)
- **Mark of the Web:** how the shell treats files with a Zone.Identifier stream for third-party preview handlers is **UNVERIFIED**; the test matrix includes a downloaded-file fixture.
- **Thousands of files at once** (a folder with 10,000 PDFs): bounded engines, bounded queue, newest-first; memory stays flat (criterion SHELL-12).
- **Hostile input:** all PDF content is parsed only in the sandboxed engine; ShellHost validates dimensions, strides and section sizes in every reply (`ReplyChecks`); the handler validates again before creating a bitmap (width and height 1 to 4,096; `stride == 4 * width`; section size >= `stride * height`; no integer overflow in the multiplication, checked in 64-bit). A wrong reply fails the request and ends the session.
- **Pipe security:** the pipe has a DACL for the current user and this package only; the handler checks that the server process belongs to this package (`GetNamedPipeServerProcessId`, then the package family of that process) before it sends a file handle; ShellHost checks the client's PID and package the same way. An unrelated process cannot feed ShellHost or impersonate it.
- **ARM64:** `MikePDF.ShellExt.dll` is built natively for ARM64; an emulated build would not load into the native ARM64 Explorer.
- **Uninstall:** the registration is manifest-only; removing the package removes it. No leftover registry keys.
- **Windows Search:** content indexing (an `IFilter` or property handler) is not part of this spec or of v1.

## 8. Undo and redo, 9. Journal entries, 10. Save behaviour

Not applicable. These handlers are read-only and never modify, create or lock user files (the handle is opened with full sharing so the shell never blocks another writer).

## 11. Engine and protocol additions (schema v5, in the schema as the shape of candidate A; see ADR-0005 amendment A2)

New `ProcessKind` value `ShellHandler = 3`. ShellHost speaks the same framing as the engine protocol (4-byte length, MessagePack, limits as in ADR-0005). Candidate A's messages, ids from 2048 so they never collide with engine (16 to 63) or writer-host (1024 and up) ids:

**`ShellOpen` (2048).**

| Request field | Type | Limits |
|---|---|---|
| `fileHandle` | u64 | min 1; a handle value valid in ShellHost, duplicated by the handler with `DuplicateHandle` |
| `fileSize` | u64 | max 2^40; informational, checked against the handle |

Reply: `sessionId` (u32, min 1), `pageCount` (u32, max 10000000), `firstWidthPt` and `firstHeightPt` (f32, exclusive min 0, max 14400).

**`ShellRenderPage` (2049).**

| Request field | Type | Limits |
|---|---|---|
| `sessionId` | u32 | min 1 |
| `page` | u32 | max 9999999 |
| `maxWidth`, `maxHeight` | u32 | 1 to 4096 |
| `flags` | u32 | max 1 (bit 0: draw annotations); other bits must be 0 |
| `section` | u64 | min 1; a section handle valid in ShellHost, created by the handler |
| `sectionBytes` | u32 | `maxWidth x maxHeight x 4` at most; max 67108864 |

Reply: `width`, `height` (u32, 1 to 4096, at most the request's max), `stride` (u32, equal to `4 x width`). The pixels are in the shared section, written by ShellHost after it copies them out of the engine's slot.

**`ShellClose` (2050).** Request: `sessionId`. Reply: empty.

Statuses reuse the existing enum (`PasswordRequired`, `Damaged`, `Timeout`, `PermissionDenied` for policy-off, and so on). `deadlineMs` in the envelope carries the handler's remaining budget. The generator must emit C++ **client** encoders and decoders for these three messages (the engine side only needed server-side ones so far).

## 12. Localisation strings

Strings that the handler shows live in the DLL's resources (`MikePDF.ShellExt.rc` with the same en-US text as the app's `.resw`; one source of truth generated at build). Languages follow the UI language of the user.

| Key | en-US |
|---|---|
| `Shell_Preview_Name` | PDF preview |
| `Shell_Preview_Value` | Page {0} of {1} |
| `Shell_Preview_Loading` | Loading... |
| `Shell_Preview_Unavailable` | Preview isn't available for this file |
| `Shell_Preview_Password` | This file is protected by a password. Open it in MikePDF to read it. |
| `Shell_Preview_Off` | Preview is turned off |
| `Shell_Preview_OffPolicy` | Preview is turned off by your organisation |
| `Settings_Shell_Thumbnails` | Show page thumbnails in File Explorer |
| `Settings_Shell_Preview` | Show previews in the File Explorer preview pane |
| `Settings_Shell_CacheNote` | Windows keeps its own cache of thumbnails. Turning this off doesn't delete thumbnails Windows already saved. |

Telemetry: none.

## 13. Open questions

| Id | Question | Who answers |
|---|---|---|
| OQ-SHELL-1 | Transport A, B or C (spike S6b). | Opus |
| OQ-SHELL-2 | Do handlers apply only while MikePDF is the default `.pdf` app (spike)? | Sonnet spike |
| OQ-SHELL-3 | Register for `IInitializeWithItem` only, dropping the stream fallback? | Opus |
| OQ-SHELL-4 | Memory limit and time limit for shell engines (256 MB proposed, unmeasured). | Opus after measurement |
| OQ-SHELL-5 | Preview: scrollable (assumed) or first page only. | User (product) |
| OQ-SHELL-6 | How much accessibility the preview must provide in v1 (name and position only, as specified, or text). | User and Opus |
| OQ-SHELL-7 | Hydrate online-only files on explicit preview selection? | User (product) |
| OQ-SHELL-8 | Does the shell enforce Mark-of-the-Web restrictions on our preview handler? | Sonnet spike |
| OQ-SHELL-9 | ADR-0015 decision 6 removed handlers from the Store preview. Re-adding them changes the Store submission (a shell extension in the package); who re-runs certification? | User |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| SHELL-01 | The manifest declares the thumbnail handler, preview handler and the Application-level surrogate with real CLSIDs (not the S6 placeholders); a build check validates the XML and that each `Clsid` resolves. | Unit (build check) |
| SHELL-02 | Handler registration works on an installed package: `CoCreateInstance` of each CLSID from a test host returns a working object on x64 and ARM64. | Integration (packaged) |
| SHELL-03 | `IThumbnailProvider.GetThumbnail` returns a correctly sized premultiplied BGRA bitmap for a text fixture at `cx` 32, 96, 256 and 1024; the longer side equals `cx`; aspect ratio within 1 pixel. | Integration |
| SHELL-04 | Thumbnail content matches the viewer render of page 1 scaled down: SSIM >= 0.95 on 20 fixtures including rotated pages and a CropBox with a non-zero origin (threshold proposed, not measured). | Integration (visual) |
| SHELL-05 | Latency: over 200 corpus files, thumbnail p95 <= 2.0 s cold ShellHost and p95 <= the warm figure measured in the first run (no number is promised before measurement); every call that exceeds 2 s fails rather than returning late. | Integration (perf) |
| SHELL-06 | Preview page 1 is shown within 1.0 s for the typical 20-page fixture on R1 (target; recorded as measured or not measured). | Integration (perf) |
| SHELL-07 | A hanging engine (fixture that never finishes rendering) makes the thumbnail fail at 2 s, the engine process is gone within 1 s after that, and the next request works. | Integration |
| SHELL-08 | A crashing engine (fixture that crashes it) fails the thumbnail and a repeat request within 10 minutes returns failure without starting a new engine (negative cache); after the cache expires (fake clock) it tries again. | Integration + Unit |
| SHELL-09 | Killing ShellHost mid-request fails that request; the next request starts a new ShellHost and succeeds. | Integration |
| SHELL-10 | No orphan processes: 60 s after the last request ShellHost has exited; no `MikePDF.Engine.exe` remains from shell requests. | Integration |
| SHELL-11 | Handler-side validation: replies with width 0, width 5000, stride not 4 x width, section smaller than needed, and overflow-sized values are all rejected without allocating. | Unit (C++) |
| SHELL-12 | Flood: 500 concurrent thumbnail requests keep live shell engines <= `shell.maxEngines`, all requests finish within their deadline or fail, and ShellHost private bytes do not grow by more than a bounded amount (set after the first measurement). | Integration (perf) |
| SHELL-13 | Password-protected, zero-byte, non-PDF, damaged and truncated fixtures fail cleanly; no dialog; no crash; no engine left. | Integration |
| SHELL-14 | Online-only placeholders are not hydrated: the file's recall attribute is unchanged after a thumbnail request. | Integration (needs placeholder fixture; manual-gated on OneDrive) |
| SHELL-15 | Policy off: with `ShellThumbnailsEnabled=0`, ShellHost is never started and `GetThumbnail` fails; with `ShellPreviewEnabled=0` the preview shows the policy message. | Integration (needs admin registry write in CI) |
| SHELL-16 | User setting off: ShellHost answers thumbnail and preview requests with failure within 100 ms. | Integration |
| SHELL-17 | Pipe security: a process in another package, and a process running as another user, cannot connect (DACL test); a client that is not this package's is closed by ShellHost; a server that is not this package's is refused by the handler. | Integration |
| SHELL-18 | Fuzz: the handler's reply decoder and ShellHost's request decoder have libFuzzer and SharpFuzz targets in the fuzz workflow; seed corpus includes the SHELL-11 cases. | Unit (fuzz harness build) |
| SHELL-19 | Preview: scrolling through a 200-page fixture renders pages on demand, keeps at most 4 pages in memory, and `Unload` closes the session within 200 ms. | Integration |
| SHELL-20 | Preview keyboard and focus: arrows, Page Up/Down, Home, End scroll; Tab returns focus to the host frame (test through a stub `IPreviewHandlerFrame`). | Integration |
| SHELL-21 | Preview resize and DPI change re-render visible pages at the new size without blank frames longer than 100 ms. | Integration (visual) |
| SHELL-22 | Accessibility: the preview window exposes name "PDF preview" and value "Page n of N" through UIA, updated on scroll; checked with raw `IUIAutomation`. | UI journey (a11y) |
| SHELL-23 | End to end in Explorer on a machine where MikePDF is the default app: a folder of 50 PDFs shows thumbnails and Alt+P shows a preview. Recorded as a manual checklist; marked **not run** until it is done on R1 and R2. | Manual record |
| SHELL-24 | With another app as the default, the handlers are not used by Explorer (OQ-SHELL-2 result is recorded as a documented behaviour, not an assumption). | Manual record |
| SHELL-25 | ARM64: the DLL is a native ARM64 image (PE machine type check) and SHELL-02 passes on the ARM64 runner. | Integration |
| SHELL-26 | Uninstall leaves no handler registration (query the shell for the CLSID after removal). | Integration |
