# Feature spec: Explorer thumbnail provider and preview handler

- Status: Accepted for implementation. The transport is decided ([ADR-0018](../adr/0018-shell-handler-transport.md), evidence in [S6b](../spikes/S6b-shell-transport.md)); corrected for the S6b findings on 2026-11-03 (work package W2-shell)
- Milestone: M3 (full reader)
- Master prompt: §8.11, §5.1 (Quire.ShellExt), §13.1 and the B3 boundary in `docs/threat-model/README.md`, §11 (no hard budget; §8.11 sets 2 s and 1 s), risk R-16
- Author: Sonnet (W1-specs). Reviewer: Opus
- Date: 2026-11-02
- Depends on: `MikePDF.Broker` (launcher, supervisor, client), ADR-0004 (sandbox profile), ADR-0015 decision 6 (handlers were excluded from the Store preview and return here), S6 (manifest findings), ADR-0018 and S6b (transport)
- Related: [settings-policies](settings-policies.md) (policy keys), [file-association-jumplist](file-association-jumplist.md)

Conventions are in [README.md](README.md).

## 1. Constraints

1. **No PDFium inside Explorer or any shell host process** (§8.11). Handlers spawn a sandboxed engine and exchange pixels through IPC.
2. **The sandbox launch code is C#** (`MikePDF.Broker`). The handler must be a native COM DLL, because managed in-process shell extensions are not supported by Microsoft (S6 recorded this; the current wording was not re-verified).
3. S6 found that the manifest registrations for a thumbnail handler (`desktop2:ThumbnailHandler`) and a preview handler (`desktop2:DesktopPreviewHandler`) validate, but **that the handlers do not work yet**: S6's COM classes pointed at a placeholder. The COM surrogate server entry must be at Application level. The surrogate (`dllhost.exe`) is **not** sandboxed.
4. **Handlers apply whenever the package is installed; the default-app prompt does not control them.** Measured in S6b on x64 and ARM64: with Edge as the default `.pdf` app, MikePDF's packaged thumbnail handler still served `.pdf`, and it won over a classic handler registered at the extension and at the default app's ProgID. Installing the package therefore takes over every PDF's thumbnail. Consequence for the "off" switches: when a handler declines, whether the shell falls through to the next registered handler or shows the generic icon was not known. W2-shell tests it (section 3.5, OQ-SHELL-14) and records the result; until a result exists, the setting's text says that "off" shows the generic icon. Not shown by S6b and still UNVERIFIED: a competing *packaged* app, another OS build, a machine with Acrobat's real registration.
5. Timeouts from §8.11: thumbnail **2 s**, preview first page **1 s target**. These are MikePDF's own budgets: Microsoft documents no Explorer timeout for either kind of handler (S6b), and Explorer's behaviour when a handler is slow is UNVERIFIED.

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
| `MikePDF.ShellExt.dll` | C++20 | In-process COM server, hosted by `dllhost.exe` (thumbnails) and `prevhost.exe` (previews) | Implements the shell interfaces (`IInitializeWithStream` only). **Parses nothing.** Copies the stream into a section, asks ShellHost over a per-user pipe, validates the pixels it gets back and returns them to the shell. |
| `MikePDF.ShellHost.exe` | C# Native AOT, packaged, full trust | Trusted | A small broker for shell requests. Reuses `MikePDF.Broker` (`EngineSupervisor`, `EngineClient` and `ReplyChecks`, so every engine reply is validated as untrusted). Starts at most `shell.maxEngines` shell engines, enforces deadlines, caches failures. |
| `MikePDF.Engine.exe` (`--kind shell`) | C++20 | Sandboxed (ADR-0004, zero capabilities) | Opens one document, renders pages of it. |

`EngineKind::Shell` (one document; an allow-list of messages, everything else answers `Unsupported`) sits beside `Document` and `Pool`. Package layout: `ShellExt\MikePDF.ShellExt.dll`, `ShellHost\MikePDF.ShellHost.exe` and `Engine\MikePDF.Engine.exe`.

### 3.2 Transport (decided: ADR-0018)

A per-user named pipe to ShellHost, which the handler starts on demand. S6b measured it end to end, packaged, through the real shell on x64 and ARM64 (warm p50 29 to 46 ms, cold 207 to 474 ms with a JIT ShellHost, 110 ms with Native AOT on DEV). The alternatives lost on evidence:

- **A packaged COM `ExeServer` cannot be the handler.** `makeappx` rejects a `desktop2:ThumbnailHandler` whose `Clsid` is not a `com:SurrogateServer` class, so an `ExeServer` could only be a helper behind the DLL, and its marshaled stream read is about twice as slow as a local copy.
- **The handler starting the sandbox itself** needs about 1,300 lines of security-critical launcher code ported to C++ inside an unsandboxed host, has no global engine cap, and `CreateAppContainerProfile` is refused at Low integrity.

How it works:

- **Launch.** If the pipe is absent the handler starts `MikePDF.ShellHost.exe` from the package directory (`CreateProcess`, with breakaway from the host's job) **under a launch lock** (a named mutex), so a folder of 16 cold hosts starts one ShellHost, not 16 (measured: 14 extra ShellHosts without the lock), and waits for the pipe. ShellHost exits **60 seconds** after its last request, and is a single instance per user session (a named mutex).
- **Integrity limit.** A handler host at **Low** integrity cannot start ShellHost (S6b measured: the process starts and no pipe appears). The runners only showed High hosts; on a standard-user machine the hosts are expected at Medium, which works. This is OQ-SHELL-10 and is UNVERIFIED until a real Explorer run (SHELL-23). A Low host can still *use* a running ShellHost (the pipe carries a Low mandatory label).
- **Pipe.** Per user; DACL for the user and SYSTEM only; Low label; remote clients rejected; `FILE_FLAG_FIRST_PIPE_INSTANCE`; the handler checks the server's package family before it sends anything.
- **Stream hand-over.** The handler copies the `IStream` into an unnamed section (cap 32 MiB) and ShellHost duplicates the handle from the client process found from the pipe (section 11).

### 3.3 Thumbnail provider

Interfaces: `IThumbnailProvider` and **`IInitializeWithStream` only**. Microsoft documents it as the only initialisation interface used when a thumbnail provider is loaded out of process (S6b answered OQ-SHELL-3), so there is no `IInitializeWithItem`, no file path and no staging in the handler. The handler copies the stream into a section and the copy is capped at **32 MiB**; above that it fails and Explorer shows the generic icon. ShellHost stages the bytes to a temp file in its private folder for the engine (ADR-0018: 5 ms at 5 MB, 51 ms at 30 MB).

`GetThumbnail(cx)`:

1. Clamp `cx` to 16 to 1,024.
2. Ask ShellHost to open the document and render page 1 to fit `cx` x `cx` (aspect preserved, never upscaled beyond 2x its natural size).
3. Return a 32-bit premultiplied BGRA `HBITMAP` with `WTSAT_ARGB`, opaque white page, no border or badge.
4. Total time limit **2 s** from entry to return, of which the handler gives ShellHost **1.7 s**. On expiry: cancel the request (ShellHost kills the engine), return a failure `HRESULT`; Explorer falls back to the generic icon. Never block longer; never leave an engine behind.

Rules: `/Rotate` and CropBox are applied (the same displayed box as the viewer). Annotations are drawn; form fields are drawn without highlight. Password-protected files fail (no password prompt from a shell). Zero-byte, non-PDF, damaged and unsupported files fail. A file larger than 32 MiB fails (generic icon).

**Concurrency.** Explorer asks for many thumbnails at once. ShellHost runs at most `shell.maxEngines` (default **3**, range 1 to 6, set by the policy `ShellMaxEngines`) engines, and the spare engine started ahead counts toward that number. Requests queue newest first; a request whose deadline has passed while queued is dropped without work. The queue holds at most 64 requests; the oldest is dropped beyond that.

**Negative cache.** A file that failed (damaged, password-protected, the engine crashed or timed out) is not tried again for 10 minutes, so a crash-inducing file cannot become a crash loop each time Explorer repaints. With stream initialisation ShellHost has no file ID or last-write time, so the key is the size and the SHA-256 of the bytes it already has in the section; it is checked before an engine is started. The cache lives in ShellHost's memory only, holds at most 1,024 entries (oldest dropped), and is not used for a failure that is not the file's fault (policy off, queue full, ShellHost shutting down).

### 3.4 Preview handler

Interfaces: `IPreviewHandler`, `IOleWindow`, `IObjectWithSite` (for `IPreviewHandlerFrame`), `IPreviewHandlerVisuals`, and the initialisation interface as above. The handler creates a child window inside the preview pane's rectangle.

- `DoPreview`: shows "Loading..." at 300 ms if page 1 is not ready, and page 1 when it is. Target **1 s** to page 1; hard limit 5 s, after which the pane shows "Preview isn't available for this file" (limits proposed). `prevhost.exe` starts a new process for each activation (30 to 50 ms in S6b), so a cold ShellHost leaves about 900 ms of the 1 s target for the launch and the render; S6b measured warm first pixel at 26 to 52 ms and cold at 207 to 292 ms, inside it.
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

Turning thumbnails off in MikePDF does **not** delete thumbnails Windows already cached; the Settings text says so. Registration is part of the package, so these switches cannot unregister the handlers; they make them decline. **What a declined request shows** depends on whether the shell falls through to the next registered handler (the one that would serve `.pdf` without us) or shows the generic icon; W2-shell tests this on a hosted runner and records the result in the work package's report. Until a result says the shell falls through, the Settings text says that "off" shows the generic icon (OQ-SHELL-14). The `ShellMaxEngines` policy (DWORD 1 to 6, default 3, the lower value is stricter) sets `shell.maxEngines`.

### 3.6 Failure behaviour

Every failure returns a failure `HRESULT` quickly and leaves no engine behind: the job object around each shell engine kills it, ShellHost kills any engine that outlives its request deadline, and when ShellHost itself dies the handler's next call starts a new one. The shell's own behaviour on failure (generic icon, retry later) is outside our control. The handler never shows a dialog and never writes to disk. ShellHost's staged copy of the bytes lives in its private folder only while a document is open, and ShellHost deletes leftovers of an earlier run when it starts.

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
- **Network and cloud files:** with stream initialisation the shell opens the file and hands the handler a stream; the handler never sees a path or the file's attributes, so **it cannot check the recall attributes** (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `FILE_ATTRIBUTE_RECALL_ON_OPEN`) as the first draft of this spec planned. Whether the shell hydrates an online-only file before it calls a handler is the shell's behaviour and is UNVERIFIED (SHELL-14). OPEN QUESTION OQ-SHELL-7: should the preview pane hydrate on explicit user selection? (Windows itself does not for most types.)
- **Mark of the Web:** thumbnails of Internet-zone files are produced and the handler has no special path (it never reads `Zone.Identifier`; every file is untrusted and goes to the sandbox). Explorer, not the handler, blocks the preview pane for downloaded files since the October 2025 updates (KB5070960); whether that applies to a third-party handler is UNVERIFIED, and the user documentation says that an empty pane for a downloaded file is Windows's behaviour (Properties, Unblock).
- **Thousands of files at once** (a folder with 10,000 PDFs): bounded engines, bounded queue, newest-first; memory stays flat (criterion SHELL-12).
- **Hostile input:** all PDF content is parsed only in the sandboxed engine; ShellHost validates dimensions, strides and section sizes in every reply (`ReplyChecks`); the handler validates again before creating a bitmap (width and height 1 to 4,096; `stride == 4 * width`; section size >= `stride * height`; no integer overflow in the multiplication, checked in 64-bit). A wrong reply fails the request and ends the session.
- **Pipe security:** the pipe's DACL names the current user and SYSTEM, it carries a Low mandatory label (without it a Low host gets `ERROR_ACCESS_DENIED`), remote clients are rejected, and ShellHost creates the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so a squatter that is already there makes it fail rather than serve. The handler checks that the server process belongs to this package (`GetNamedPipeServerProcessId`, then the package family of that process) before it sends a file handle; ShellHost reads the client's PID from the pipe, never from a message, and logs its image and package family. Another process of the same user can use ShellHost as a "PDF to bitmap" service; it can already read the user's files, so that is not a new capability. A client can hand ShellHost a section whose contents change while the engine reads: that affects only that client's thumbnail.
- **ARM64:** `MikePDF.ShellExt.dll` is built natively for ARM64; an emulated build would not load into the native ARM64 Explorer.
- **Uninstall:** the registration is manifest-only; removing the package removes the `CLSID` and `PackagedCom` keys. S6b found one empty key left behind for an extension the system had no entry for (`HKCR\.s6bpdf\OpenWithProgids`); for `.pdf` the system already has the key, so nothing of ours is expected to stay, and SHELL-26 checks the CLSIDs, not every key.
- **Windows Search:** content indexing (an `IFilter` or property handler) is not part of this spec or of v1.

## 8. Undo and redo, 9. Journal entries, 10. Save behaviour

Not applicable. These handlers are read-only and never modify, create or lock user files (the handle is opened with full sharing so the shell never blocks another writer).

## 11. Engine and protocol additions (schema v5; ADR-0018 and S6b are the authority, this section follows them)

The text below was corrected in `m3/engine` after S6b: the handle direction, the field names and limits and the session in the envelope.

New `ProcessKind` value `ShellHandler = 3`, sent by the handler in `Hello`; ShellHost accepts no other kind and an engine refuses this one. The framing is the engine's (4-byte length, MessagePack, ADR-0005 limits), one pipe connection per handler instance, and the connection is the session. Ids from 2048 never collide with engine (16 to 63) or writer-host (1024 and up) ids.

**`ShellOpen` (2048, no document).** Request: `section` (u64, min 1), a section handle valid in the **handler** (the sending process) with read access, which ShellHost duplicates from the process at the other end of the pipe (`GetNamedPipeClientProcessId`, then `DuplicateHandle`); `bytes` (u32, 1 to 33554432), the file size (the section may be larger). Reply: `documentId` (u32, min 1, the session), `pageCount` (u32, max 10000000), `firstWidth` and `firstHeight` (f32, exclusive min 0, max 14400, points). No password: a shell never prompts.

**`ShellRenderPage` (2049, document required).** Request: `page` (u32, max 9999999), `maxWidth` and `maxHeight` (u32, 16 to 1024), `flags` (u32, max 1; bit 0 draws annotations), `pixels` (u64, min 1), a section handle valid in the handler with write access, and `pixelBytes` (u32, 4 to 4194304, at least `maxWidth x maxHeight x 4`). Reply: `width` and `height` (u32, 1 to 1024, at most the request's maxima), `stride` (u32, 4 to 4096, equal to `4 x width`); the pixels are in the caller's section. The handler checks the last three before it builds a bitmap; the schema cannot.

**`ShellClose` (2050, document required).** Empty request and reply. Optional: closing the pipe ends the session.

`OpenDocument` has no section source (ADR-0018 decided against it for v1.0).

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
| OQ-SHELL-1 | Transport A, B or C. **Answered: A** (ADR-0018). | Done |
| OQ-SHELL-2 | Do handlers apply only while MikePDF is the default `.pdf` app? **Answered: no**, they apply whenever the package is installed (section 1, item 4). | Done |
| OQ-SHELL-3 | Item or stream initialisation? **Answered: stream only** (section 3.3). | Done |
| OQ-SHELL-4 | Memory limit and time limit for shell engines. S6b measured an engine peak working set of 11 to 24 MB on its fixtures; **keep 256 MB and the 1.7 s deadline**. Neither limit was reached, so a run with a hostile file is still owed. | Opus after a hostile-file run |
| OQ-SHELL-5 | Preview: scrollable (assumed) or first page only. | User (product) |
| OQ-SHELL-6 | How much accessibility the preview must provide in v1 (name and position only, as specified, or text). | User and Opus |
| OQ-SHELL-7 | Hydrate online-only files on explicit preview selection? | User (product) |
| OQ-SHELL-8 | Does the shell enforce Mark-of-the-Web restrictions on our preview handler? S6b: thumbnails are produced for Internet-zone files; Explorer blocks the preview pane itself (KB5070960); for a third-party handler UNVERIFIED. | Real Explorer run |
| OQ-SHELL-9 | ADR-0015 decision 6 removed handlers from the Store preview. Re-adding them changes the Store submission (a shell extension in the package); who re-runs certification? | User |
| OQ-SHELL-10 | At what integrity level does Explorer's activation run the handler on a standard-user machine? A Low host cannot start ShellHost (section 3.2). | User and Opus (R1, R2) |
| OQ-SHELL-11 | Handler precedence on a machine with Acrobat's real registration, and against another packaged app. | User |
| OQ-SHELL-12 | Does Explorer's block on previewing downloaded files apply before a third-party handler is created? | Real Explorer run |
| OQ-SHELL-13 | A section source in `OpenDocument`. **Answered: not for v1.0** (ADR-0018); revisit if reference-hardware runs show the staging matters. | Done |
| OQ-SHELL-14 | MikePDF's thumbnails replace the user's other PDF app's for every PDF once the package is installed. Is that wanted, and what do the "off" switches show (the shell's fall-through is tested in W2-shell)? Default until decided: on, with a setting and a policy to turn them off. | User (product) |

## 14. Acceptance criteria

| Id | Criterion | Layer |
|---|---|---|
| SHELL-01 | The manifest declares the thumbnail handler, preview handler and the Application-level surrogate with real CLSIDs (not the S6 placeholders); a build check validates the XML and that each `Clsid` resolves. | Unit (build check) |
| SHELL-02 | Handler registration works on an installed package: `CoCreateInstance` of each CLSID from a test host returns a working object on x64 and ARM64. | Integration (packaged) |
| SHELL-03 | `IThumbnailProvider.GetThumbnail` returns a correctly sized premultiplied BGRA bitmap for a text fixture at `cx` 32, 96, 256 and 1024; the longer side equals `cx`; aspect ratio within 1 pixel. | Integration |
| SHELL-04 | Thumbnail content matches the viewer render of page 1 scaled down: SSIM >= 0.95 on 20 fixtures including rotated pages and a CropBox with a non-zero origin (threshold proposed, not measured). | Integration (visual) |
| SHELL-05 | Latency: over 200 corpus files, thumbnail p95 <= 2.0 s cold ShellHost and a warm p95 no worse than the S6b baseline until R1 and R2 runs set a number (S6b, through the shell on the hosted runners: warm p95 at most 56.5 ms, cold at most 474 ms with a JIT ShellHost; DEV Native AOT cold p95 139 ms); every call that exceeds 2 s fails rather than returning late. | Integration (perf) |
| SHELL-06 | Preview page 1 is shown within 1.0 s for the typical 20-page fixture on R1 (target; recorded as measured or not measured; S6b first pixel: warm 26 to 52 ms, cold 207 to 292 ms, plus 30 to 50 ms for the new `prevhost.exe`). | Integration (perf) |
| SHELL-07 | A hanging engine (fixture that never finishes rendering) makes the thumbnail fail at 2 s, the engine process is gone within 1 s after that, and the next request works. | Integration |
| SHELL-08 | A crashing engine (fixture that crashes it) fails the thumbnail and a repeat request of the same bytes within 10 minutes returns failure without starting a new engine (negative cache, keyed by size and SHA-256); after the cache expires (fake clock) it tries again. | Integration + Unit |
| SHELL-09 | Killing ShellHost mid-request fails that request; the next request starts a new ShellHost and succeeds. | Integration |
| SHELL-10 | No orphan processes: 60 s after the last request ShellHost has exited; no `MikePDF.Engine.exe` remains from shell requests. | Integration |
| SHELL-11 | Handler-side validation: replies with width 0, width 5000, stride not 4 x width, section smaller than needed, and overflow-sized values are all rejected without allocating. | Unit (C++) |
| SHELL-12 | Flood: 500 concurrent thumbnail requests keep live shell engines <= `shell.maxEngines`, all requests finish within their deadline or fail, and ShellHost private bytes do not grow by more than a bounded amount (set after the first measurement). | Integration (perf) |
| SHELL-13 | Password-protected, zero-byte, non-PDF, damaged and truncated fixtures fail cleanly; no dialog; no crash; no engine left. | Integration |
| SHELL-14 | Online-only placeholders are not hydrated by a thumbnail request. The handler cannot check recall attributes (stream initialisation, section 7), so this is a property of the shell, not of the handler. | Manual record (OneDrive); UNVERIFIED until run |
| SHELL-15 | Policy off: with `ShellThumbnailsEnabled=0`, ShellHost is never started and `GetThumbnail` fails; with `ShellPreviewEnabled=0` the preview shows the policy message. | Integration (needs admin registry write in CI) |
| SHELL-16 | User setting off: ShellHost answers thumbnail and preview requests with failure within 100 ms. | Integration |
| SHELL-17 | Pipe security: a process in another package, and a process running as another user, cannot connect (DACL test); a client that is not this package's is closed by ShellHost; a server that is not this package's is refused by the handler. | Integration |
| SHELL-18 | Fuzz: the handler's reply decoder and ShellHost's request decoder have libFuzzer and SharpFuzz targets in the fuzz workflow; seed corpus includes the SHELL-11 cases. | Unit (fuzz harness build) |
| SHELL-19 | Preview: scrolling through a 200-page fixture renders pages on demand, keeps at most 4 pages in memory, and `Unload` closes the session within 200 ms. | Integration |
| SHELL-20 | Preview keyboard and focus: arrows, Page Up/Down, Home, End scroll; Tab returns focus to the host frame (test through a stub `IPreviewHandlerFrame`). | Integration |
| SHELL-21 | Preview resize and DPI change re-render visible pages at the new size without blank frames longer than 100 ms. | Integration (visual) |
| SHELL-22 | Accessibility: the preview window exposes name "PDF preview" and value "Page n of N" through UIA, updated on scroll; checked with raw `IUIAutomation`. | UI journey (a11y) |
| SHELL-23 | End to end in Explorer on a machine where MikePDF is the default app: a folder of 50 PDFs shows thumbnails and Alt+P shows a preview. Recorded as a manual checklist; marked **not run** until it is done on R1 and R2. | Manual record |
| SHELL-24 | With another app as the default, the handlers are still used by Explorer (measured on the hosted runners in S6b with Edge as the default; this criterion records it on a real machine, including one with Acrobat installed: OQ-SHELL-11). | Manual record |
| SHELL-25 | ARM64: the DLL is a native ARM64 image (PE machine type check) and SHELL-02 passes on the ARM64 runner. | Integration |
| SHELL-26 | Uninstall leaves no handler registration: after removal the CLSIDs are gone from `CLSID` and `PackagedCom`, and `CoCreateInstance` of each fails with `REGDB_E_CLASSNOTREG`. | Integration |
