# S6b: how shell handlers reach the sandbox

- Status: **Done. Prototype run through the real sandboxed engine on DEV (x64), and the packaged registration, activation and handler code run on GitHub-hosted runners (x64 and ARM64). The real Explorer, a Medium-integrity user session and a machine with a competing PDF app were not available: see "Not verified".**
- Date: 2026-11-02
- Author: Sonnet (S6b). Reviewer: Opus.
- Target: [shell-extensions](../features/shell-extensions.md) 3.2 and 11 (OQ-SHELL-1, -2, -3, -4, -8), risk R-16, master prompt 5.3 and 8.11. Output: a proposed [ADR-0018](#proposed-adr-0018-shell-handler-transport), the message list and schema v5 contents below.
- Branch `m3/spike-shell`; `src/` is untouched. Code: `spikes/S6b/` (throwaway). Raw output: `spikes/S6b/out/` (the folder is gitignored; CSV, text and log results are force-added). Every number below comes from those files. CI: `.github/workflows/s6b-shell-transport.yml`.
- Nothing was registered on the developer machine: no COM class, shell extension, package, file association, `HKLM` or `HKCU\Software\Classes` value. The handler DLL is loaded by a test host with `LoadLibrary` and `DllGetClassObject`. Packaged registration, the real shell and the registry experiments ran only on disposable hosted runners.

## Recommendation

**Transport A: a per-user named pipe to a packaged `ShellHost` that the handler starts on demand.** The handler is a thin native DLL (stream initialisation only). It copies the `IStream` into an unnamed section, and `ShellHost` pulls that handle from the handler's process, starts **one single-use sandboxed engine per file** (with one spare started ahead), and writes the rendered pixels into a section the handler created. The handler validates the reply before it builds a bitmap.

Why, in order of weight:
1. **It works, packaged, on both architectures.** Installed on x64 and ARM64 runners, the real shell activated the packaged thumbnail handler in its own `dllhost.exe` and the preview handler in `prevhost.exe`; the handler started the packaged `ShellHost`, which started the real AppContainer engine from the package directory; a correct thumbnail came back (21% non-white, 198 x 256, from a 612 x 792 page). Runs 1 to 3, both architectures: 33 of 33 thumbnails per run (3 cold, 30 warm), no failure.
2. **It is fast enough with a large margin.** Warm: p50 29 to 46 ms and p95 32 to 56 ms through the real shell on the runners (p50 37 ms, p95 48 ms on DEV). Cold (ShellHost and engine started on demand): 207 to 474 ms through the shell with a self-contained .NET ShellHost; 110 ms p50 / 139 ms p95 with a Native AOT ShellHost on DEV. The budget is 2 s.
3. **It is testable without registering anything**, which the other two are not: this whole spike's local half ran against a pipe. The framing, limits and fuzz harnesses of ADR-0005 carry over.
4. **The other two lose on evidence.** B (`com:ExeServer`) **cannot be the handler**: the manifest schema rejects a `desktop2:ThumbnailHandler` whose `Clsid` is not a `com:SurrogateServer` class. As a helper it adds a marshaling layer and its stream read is about twice as slow as a local copy (30 MB: 39 to 44 ms against 20 to 25 ms), and a custom interface needs a proxy/stub that this spike did not register. C (the handler starts the sandbox) needs about 1,300 lines of security-critical launcher code ported to C++, runs inside an unsandboxed host, has no global engine cap or negative cache, and `CreateAppContainerProfile` is **refused at Low integrity** (0x80070005).

Decisions inside A that the evidence supports (details in the ADR):
- **Stream initialisation only.** The documentation says `IInitializeWithStream` is the only initialisation interface used when a thumbnail provider is loaded out of process. This answers OQ-SHELL-3: drop the item and file paths and their staging.
- **Server pulls the handle** (`GetNamedPipeClientProcessId`, then `DuplicateHandle` from that process). It worked from a Low-integrity client; the spec's "the handler duplicates into ShellHost" was not tested from Low, and a Low process cannot open a Medium one for duplication under the integrity rules (inferred, not measured).
- **A pipe with a Low mandatory label** (`S:(ML;;NW;;;LW)`) so a Low-integrity host can connect. Without the label a Low client gets `ERROR_ACCESS_DENIED` (5); with it, thumbnails and preview worked from Low.
- **Native AOT ShellHost.** Cold start 110 ms p50 against 274 ms, working set 29 MB against 62 MB.
- **A single-flight launch lock.** 16 hosts that all found no ShellHost started 14 extra ShellHost processes, which exited at once; that is wasted work, and it grows with the Explorer folder size.
- **One engine per file, one spare ahead.** p50 28 ms against 37 ms for none and 8 ms for a shared engine; a shared engine gives up the one-process-per-document isolation for 20 ms.

What this spike could not settle, and the product must not assume: **the integrity level of a packaged handler under a real Explorer.** On the runners every activated host (`DllHost.exe`, `prevhost.exe`, ShellHost, the ExeServer) logged `integrity=high`, **whatever the caller's level**: a driver started at Medium got High hosts, because DCOM starts a server with the user's session token and the runner's session token is High (an elevated administrator, no UAC filtering). No host was ever Low, although the documentation says prevhost runs handlers at Low by default. On a standard-user machine the session token is Medium, so the hosts are *expected* at Medium, where on-demand launch works; that expectation is **untested**. If a host were Low, a handler could not start ShellHost (measured), and a Low *caller* is refused COM activation of our classes (`0x80070005`, measured). See OQ-SHELL-10 and "Not verified".

## Questions

1. Which transport carries a shell handler's request to the sandbox: (a) a pipe to a long-running broker started on demand, (b) a packaged COM `ExeServer`, (c) a small C++ launcher in the handler? Bytes from the `IStream`, the security of every hop, latency to a 256 px thumbnail.
2. Which manifest extensions does a packaged thumbnail and preview handler need, and do packaged handlers apply only when MikePDF is the default PDF app?
3. Mark-of-the-Web: any special path to keep or skip?
4. Prototype the best option to a correct thumbnail through the real sandboxed engine; p50 and p95, cold and warm; memory.

## Method

- **DEV:** Windows 11 build 26300 (`10.0.26300.0`), Intel Core Ultra 9 275HX (24 logical cores), 64 GB, .NET SDK 10.0.300, VS Build Tools 2022 (MSVC 14.44), PDFium `chromium/8125`. **The machine is shared with other agents: background CPU was 43% before the first full run, 40% before the second and 14% before an earlier run whose files were overwritten.** All DEV latency is "under whatever load the machine had", not a clean R1 or R2 figure. DEV is not reference hardware.
- **CI:** `windows-2025-vs2026` (Windows 10.0.26100, x64) and `windows-11-vs2026-arm` (10.0.26200, ARM64). The user is an administrator whose token is **High integrity and elevated** (the driver logs `integrity RID 0x3000, elevated token=1`); the default `.pdf` app is Microsoft Edge (`UserChoice ProgId = MSEdgePDF`). A throwaway self-signed certificate signs the test package on the runner only (the same guard as `build/store/install-test.ps1`).
- **Prototype** (`spikes/S6b/`):
  - `native/s6b_handler.cpp`: a C++20 DLL with an `IThumbnailProvider` and an `IPreviewHandler` (plus `IInitializeWithStream`, `IOleWindow`, `IObjectWithSite`, `IPreviewHandlerVisuals`). It parses nothing. It copies the stream into an unnamed section (cap 32 MiB), connects to the pipe (starting ShellHost if the pipe is absent), checks that the server is the expected image or package family, sends two requests, validates the reply (size, stride, 64-bit arithmetic) and builds a DIB section.
  - `ShellHost/` (C#, `MikePDF.Broker` unchanged): the pipe, the handle pull, the engine pool in three modes (`fresh`, `spare`, `reuse`), a cap of 3 live engines, a kill of the engine when its client goes away, an idle exit after 60 s.
  - `native/s6b_testhost.cpp`: calls the DLL the way Explorer does (`DllGetClassObject`, `CreateInstance`, `Initialize` with a file stream from `SHCreateStreamOnFileEx`, `GetThumbnail`; `SetWindow` and `DoPreview` on a hidden window that is never shown), runs it cold or warm, and can re-run itself with a Low-integrity token.
  - `ci/`: the package manifest, a driver that exercises the real shell (`IShellItemImageFactory`, `AssocQueryString`, a `Zone.Identifier` file, `IPreviewHandler` through COM), and a COM `ExeServer` probe.
- **Wire format of the prototype** is its own fixed 72-byte request and reply (`native/s6b_proto.h`). It is not the product protocol; the product messages are in "Protocol" below.
- **Timings.** `QueryPerformanceCounter` in the handler (`GetThumbnail` entry to bitmap returned) and inside ShellHost. "Cold" means ShellHost is stopped before the call, so the call starts it; the OS file cache was warm (no reboot or standby flush was possible). The Close request (which ends the one-shot engine) is measured after the bitmap is returned and is not in `total_ms`.

## What Microsoft documents

| Topic | What the page says | Source |
|---|---|---|
| Hosting of thumbnail handlers | Handlers "run by default in a separate process"; `DisableProcessIsolation` opts out | [Thumbnail Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/thumbnail-providers) |
| Initialisation | `IInitializeWithStream` "is the only initialization interface used when IThumbnailProvider instances are loaded out-of-proc (for isolation purposes). This is the primary code path for Windows for all IThumbnailCache code paths." Item and file initialisation only for third parties that bypass `IThumbnailCache` | [IThumbnailProvider](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nn-thumbcache-ithumbnailprovider), [Building Thumbnail Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/building-thumbnail-providers) |
| Preview handlers | "Preview handlers always run out of process"; an in-process DLL run by `prevhost.exe` is "the preferred method"; a local server is "not recommended". "By default, preview handlers run in a low integrity level (IL) process"; stream initialisation gives that benefit | [Preview Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/preview-handlers) |
| Managed code | Managed in-process extensions are unsupported; "It is acceptable to use managed code ... to implement out-of-process extensions", and preview handlers are listed as out of process. Thumbnail handlers "are required to release all resources immediately when their reference count drops to zero" | [Guidance for Implementing In-Process Extensions](https://learn.microsoft.com/en-us/windows/win32/shell/shell-and-managed-code) |
| Manifest: thumbnail | `desktop2:ThumbnailHandler Clsid`; only under a `FileTypeAssociation` of an `Application` with `EntryPoint="windows.FullTrustApplication"`; Windows 10 1703 or later | [desktop2:ThumbnailHandler](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop2-thumbnailhandler) |
| Manifest: preview | `desktop2:DesktopPreviewHandler Clsid` "must match the ID attribute under the Class element in the SurrogateServer" | [desktop2:DesktopPreviewHandler](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop2-desktoppreviewhandler) |
| Manifest: COM | `com:SurrogateServer` with `SystemSurrogate="PreviewHost"` or `CustomSurrogateExecutable`; `com:ExeServer` with `Executable`, `Arguments`, `LaunchAndActivationPermission` | [com:SurrogateServer](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-com-surrogateserver), [com:ExeServer](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-com-exeserver) |
| Registration | After changing a handler's registration, call `SHChangeNotify(SHCNE_ASSOCCHANGED)` or "the change might not be recognized until the system is rebooted" | [Creating Shell Extension Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/handlers) |
| Mark-of-the-Web | Since the updates of 14 October 2025, "File Explorer automatically disables the preview feature for files downloaded from the internet" (files with MotW, and files on an Internet-zone share); unblock through Properties. The article does not mention thumbnails or third-party handlers | [KB5070960](https://support.microsoft.com/help/5070960) |

**Timeouts: Microsoft documents no number** for thumbnail extraction or for preview handlers. The pages above, the thumbnail and preview handler pages, and a web search found none. The 2 s and 1 s limits in master prompt 8.11 are MikePDF's own budgets, not Explorer's; Explorer's behaviour when a handler is slow is **UNVERIFIED** (see below). Do not quote an Explorer timeout in the spec.

## Results

### 1. Manifest and activation (CI, x64 and ARM64)

- **What validates** (found with `makeappx pack`, which runs the schema, no install needed): `ThreadingModel` is one of `Both`, `STA`, `MTA`, `MainSTA`, `Neutral` (not `Apartment`); inside `com:ComServer`, `com:ExeServer` must come before `com:SurrogateServer`; **a `desktop2:ThumbnailHandler` `Clsid` must resolve to a `com:SurrogateServer` class** (`The keyref ... does not resolve to a key for the Identity Constraint ComServer_SurrogateServer_Class_IdKey`). So an `ExeServer` class cannot be a thumbnail handler. The `com:Extension` goes in the Application-level `<Extensions>` (S6 found the same).
- **The package that works** (`spikes/S6b/ci/make-package.ps1`): an `Application` with `EntryPoint="Windows.FullTrustApplication"` carrying `uap:FileTypeAssociation` elements for `.s6bpdf` and `.pdf`, each with `desktop2:ThumbnailHandler Clsid="...0001"` and `desktop2:DesktopPreviewHandler Clsid="...0002"`, plus one `com:Extension Category="windows.comServer"` with a `com:SurrogateServer` for the thumbnail class (`ThreadingModel="Both"`, own `AppId`), a `com:SurrogateServer SystemSurrogate="PreviewHost"` for the preview class (`ThreadingModel="STA"`, own `AppId`), and a `com:ExeServer`. I used neither `desktop3:` nor `desktop10:`: the package works without them (`desktop10:DisplayName` is an optional attribute of the preview element).
- **Install:** `Add-AppxPackage` accepted it on both architectures (`SignatureKind=Developer`). Registry evidence (`out/ci-run1/.../reg-*.txt`): each class appears under `HKCR\PackagedCom\Package\<full name>\Class\{CLSID}` with a `ServerId`, a relative `DllPath` and a threading value; the file type's ProgID `AppX<hash>` is listed under `HKCR\.s6bpdf\OpenWithProgids` and under `HKCR\.pdf\OpenWithProgids`.
- **The shell uses it.** `IShellItemImageFactory::GetImage(SIIGBF_THUMBNAILONLY)` on a `.s6bpdf` file succeeded 33 of 33 times on each architecture in each run, and each call logged one `GetThumbnail` in the handler. The handler's host was `C:\Windows\system32\DllHost.exe` with the package identity (`package=MikePDF.S6bProbe_...`), started as `DllHost.exe /Processid:{<our thumbnail AppId>}` (its parent was a system process, pid 492 in the listing, not identified here): **a dedicated surrogate for our AppId, not a shared host**. The preview class was hosted by `C:\Windows\system32\prevhost.exe`, again with the package identity, and **a new prevhost was started for each `CoCreateInstance`** (four runs, four process ids; 31 to 50 ms each).
- **`AssocQueryString(ASSOCSTR_SHELLEXTENSION)` is not a usable probe** for packaged handlers: for `.s6bpdf` it returns `0x80070483` (not found) for both handler kinds although the shell then used the handler. For `.pdf` it returned only the preview handler `{3A84F9C2-...}` that is already registered at extension level on the runner (not ours).
- **Uninstall:** after `Remove-AppxPackage` the `CLSID` and `PackagedCom` keys were gone. **One empty key was left: `HKCR\.s6bpdf\OpenWithProgids`.** Spec 7 says "no leftover registry keys": amend it.

### 2. Do packaged handlers apply only when MikePDF is the default PDF app? (OQ-SHELL-2)

- **Measured: no, not in this configuration.** With `UserChoice` pointing at Edge (`MSEdgePDF`) and MikePDF's package registered for `.pdf`, `GetImage` on a real `.pdf` was served by MikePDF's packaged handler (our log gained one `GetThumbnail`, the bitmap was our render). Run 1, 2 and 3, x64 and ARM64.
- **Why is not established.** On the runner, Edge registers no thumbnail handler at extension level (`HKCR\.pdf\ShellEx` holds only a preview handler), so our ProgID in `OpenWithProgids` may simply be the only candidate. That is a hypothesis.
- [[RUN4-PRECEDENCE]]
- **For the preview handler** the shell resolves `.pdf` to the existing extension-level class (`AssocQueryString` above), not to ours. This was not driven through Explorer's pane. UNVERIFIED.
- Consequence for the spec: "handlers take effect only while MikePDF is the default" (spec 1, item 4) is **not what was measured**; the default-app prompt is not what makes the handler work, but whether it **wins against Acrobat or Edge** is still open.

### 3. Mark-of-the-Web (OQ-SHELL-8)

- **No special path in the handler.** The handler never reads `Zone.Identifier`: a stream's bytes are the file's main data stream only (handler log: `stream size=3564` for the plain and the marked copy of the same file). Every file is treated as untrusted and goes to the sandbox, so there is nothing to keep and nothing to skip.
- **Thumbnails of an Internet-zone file are produced.** A copy with `Zone.Identifier` `ZoneId=3` got a thumbnail through the shell, handler invoked once, 21% non-white, same as the unmarked file (x64 and ARM64, runs 1 to 3).
- **The preview pane is blocked by Explorer, not by the handler**, for files with MotW since the October 2025 updates (KB5070960). Whether that block also applies to a third-party handler, and whether it happens before the handler is created, is not stated and was not tested (it needs Explorer). Treat "no preview for downloaded files until unblocked" as a documented platform behaviour, and make the empty-pane case readable in the user documentation. UNVERIFIED for our handler.
- Writing `Zone.Identifier` by hand is discouraged by Microsoft-affiliated sources because of a cross-process zone cache; the test used a fresh file name for each case.

### 4. Correctness of the prototype

- `rectangles.pdf` (PDFium test fixture, 200 x 300 pt, black, blue, green and red corner squares) through the handler DLL and the sandboxed engine at `cx` 256: 171 x 256, `WTSAT_ARGB`, all five pixel checks exact (`spikes/S6b/verify_rect.py`; the output is in the run log of 2026-11-02 and the thumbnail of `text1.pdf` is `spikes/S6b/out/smoke.png`). The same page through the real shell on both runners: 198 x 256 with 21% non-white pixels each time.
- **Hostile inputs fail cleanly:** an encrypted file, a zero-byte file, a non-PDF, a truncated PDF and a 40 MiB file (over the 32 MiB cap) all failed in 0.1 to 25 ms with no engine left. A page with a 14000 x 14000 pixel image (2.3 MB file) rendered in about 200 ms; **the engine's peak working set was 24 MB**, and a memory limit of 16 MB did not stop it (the job limit is a commit limit, not working set; not investigated).
- **A handler that gives up:** with a budget of 120 ms per call and a render that takes about 200 ms, the call failed at 160 ms with `ERROR_TIMEOUT`; ShellHost logged "client gone mid-request; killing its engine" within the same tenth of a second; the engine was gone at the first poll; the next request worked (`spikes/S6b/out/faults.txt`). Without the watcher the engine would have run to its own 5 s deadline.

### 5. Latency and memory

x64 DEV, `text1.pdf` (3,564 bytes), `cx` 256, handler DLL in the test host, ShellHost framework-dependent unless stated. `total` is `GetThumbnail` entry to bitmap returned. Two full runs at about 40% background CPU; the second ("r2") is shown where it differs. Raw CSV: `spikes/S6b/out/m-*.csv`; tables: `summary-local.md`.

| Scenario | n | p50 ms | p95 ms | Notes |
|---|---|---|---|---|
| Warm, a fresh engine per request | 200 | 37.1 | 48.0 | r2: 40.0 / 53.2. Spawn p50 20 ms, open 3.5, render 1.5, pixels out 0.2, handler copy 0.07 |
| Warm, a spare engine started ahead | 200 | 28.3 | 35.8 | r2: 32.5 / 56.5 |
| Warm, one shared engine (no isolation between files) | 200 | 8.3 | 10.6 | r2: 12.1 / 15.6 |
| Cold: ShellHost started on demand, fresh engine | 30 | 273.6 | 331.1 | r2: 311.1 / 601.3. Of this, connect (launch and start) p50 136 ms |
| Cold with a spare engine | 30 | 252.7 | 363.5 | r2: 348.1 / 431.0 |
| **Cold, Native AOT ShellHost** | 30 | **110.1** | **139.2** | connect p50 68 ms; exe 3 MB |
| Warm, Native AOT ShellHost | 100 | 38.8 | 69.5 | noisy: same as JIT within the load |
| `cx` 32 / 96 / 1024, warm fresh | 100 each | 33.6 / 35.5 / 44.1 | 42.7 / 40.5 / 52.3 | 1024 px: pixels out 3 ms, bitmap 3 ms |
| Preview first pixel (600 x 800 pane), warm | 30 | 51.9 | 72.3 | r2: 64.0 / 82.4 |
| Preview first pixel, cold | 15 | 291.6 | 371.6 | r2: 321.9 / 378.1 |

Through the real shell, packaged, on the hosted runners (`GetImage` on unique `.s6bpdf` files; includes the shell's own work and the COM call into the surrogate; ShellHost is a self-contained .NET build, JIT):

| Runner | Cold (3 calls) | Warm p50 / p95 (30) | Preview: `CoCreateInstance` | Preview first pixel warm / cold |
|---|---|---|---|---|
| x64 run 1 | 428, 270, 260 ms | 39.6 / 42.6 ms | 42 to 50 ms (new prevhost each) | 32 / 261 ms |
| x64 run 2 | 454, 306, 298 ms | 42.5 / 51.3 ms | 44 to 54 ms | 36 / 285 ms |
| x64 run 3, unpackaged driver, High | 230, 246, 207 ms | 29.4 / 32.4 ms | 31 to 34 ms | 26 / 207 ms |
| ARM64 run 1 | 474, 254, 253 ms | 45.6 / 56.5 ms | 44 to 71 ms | 35 / 247 ms |
| ARM64 run 2 | 435, 267, 258 ms | 46.0 / 47.3 ms | 44 to 66 ms | 35 / 245 ms |

- The shell adds about 15 to 20 ms to the handler's own time (handler `total` about 22 ms against `GetImage` 29 to 46 ms).
- **The source size matters only through the copy.** IStream to section: 3.5 KB 0.06 ms, 65 KB 0.1 ms, 5 MB 4.0 ms, 30 MB 22.8 ms (p50). Smaller reads cost more: 30 MB with 4 KiB reads took 58.6 ms and with 64 KiB reads 25.8 ms (the shell's own stream sets the real chunk size; not observed). **The unchanged engine wants a disk file**, so ShellHost wrote the section to a temp file first: 0.5 ms for the small file, 4.7 ms at 5 MB, 50.6 ms at 30 MB (p50, `srv_stage_us`). Giving the engine the section would remove that (see the schema).
- **Pixel return:** a section (`srv_out_us` 0.2 ms at 256 px, 2.9 ms at 1024) against pixels inline over the pipe (0.2 to 1.8 ms at 256 px, 3.9 ms at 1024): no meaningful difference up to 1024 px. The section is kept because it needs no frame limit (a pipe frame is 64 KiB in ADR-0005) and keeps the reply a few bytes.
- **Memory.** Engine peak working set: 10.7 MB (256 px, 20-page text file, max 11.1) and 13.7 MB (1024 px, max 14.1), 24 MB for the 14000 x 14000 image page. ShellHost after 100 calls: working set 61.8 MB, private 28.4 MB (framework-dependent JIT); **Native AOT 28.9 MB and 16.4 MB**. Handler host after 100 calls: working set 11.7 MB, private 1.9 MB (no growth seen). Section pages for the pixels and the stream copy are freed when the call ends. The measured engine peak is far below the 256 MB proposed in OQ-SHELL-4, which no fixture came near (the limit was not exercised: UNVERIFIED for a hostile file). These runs set `S6B_PEAK=1`, which adds about 10 to 15 ms per call to the render round trip (reading the engine's peak working set): do not read the render time from the `mem-*` files.
- **Flood (SHELL-12 shape).** 16 handler hosts at once, 10 calls each, ShellHost capped at 3 engines: 160 of 160 calls succeeded; **at most 3 engines were live**; p50 274 ms and p95 622 ms to bitmap (queueing behind the cap); ShellHost 68 MB working set after. No engine process was left (`spikes/S6b/out/flood-16x10-max3.txt`).
- **Idle exit.** In run 2 ShellHost, the ExeServer and every engine were gone after the 75 s idle wait (`processes-after-idle.txt`); ShellHost's own idle limit is 60 s.

### 6. Integrity levels and on-demand launch

- **A Low-integrity client works if the pipe carries a Low label.** The test host re-run with a Low token (`DuplicateTokenEx` and `SetTokenInformation`): thumbnails 27 to 44 ms, preview first pixel 39 to 48 ms, `ShellHost` pulling the handles from the Low process with `PROCESS_DUP_HANDLE` (`openForDup=ok`). Without the label: `ERROR_ACCESS_DENIED` (5) on connect (`spikes/S6b/out/lowil.txt`).
- **A Low-integrity process cannot start a working ShellHost.** With ShellHost not running, the Low client's `CreateProcess` succeeded and **no pipe appeared in 15 s and no ShellHost process remained** (`lowil.txt`, case 3). On-demand launch therefore works only when the handler's host is at Medium or higher. The reason inside ShellHost was not isolated.
- **Option C from Low:** the broker's `AppContainerProfile.CreateOrOpen` fails at Low with `CreateAppContainerProfile failed (HRESULT 0x80070005)` even though the profile exists. With the SID derived by reflection, the next step could not be reached because my synthetic Low token could not see the engine files (a harness limit, `lowil-launch.txt`): `CreateProcess` into an AppContainer from Low is **UNVERIFIED**.
- **A Low-integrity COM client is refused activation** of every packaged class: `CoCreateInstance` of the preview class and the ExeServer, and `GetImage` itself, returned `0x80070005` (run 2 and 3, both architectures; the thumbnail handler was not invoked). This is about the *caller's* integrity; a Low *handler host* was not tested (it needs Explorer or a Low prevhost).
- **A Medium-integrity caller works the same, and does not make the hosts Medium.** In run 4 the driver was started from an image carrying a Medium mandatory label (`icacls /setintegritylevel M`; `runas /trustlevel` gave a High process here and `SetTokenInformation` cannot raise Low to Medium, so the label was the one mechanism that worked): it logged `integrity RID 0x2000 (medium)` and got thumbnails (x64: cold 340, 290, 284 ms; warm p50 41.2 ms, p95 47.9 ms; ARM64: 333, 271, 275 ms; warm 49.2 / 55.4 ms), the `.pdf` served by our handler, and previews (first pixel 35 to 45 ms warm, 282 to 338 ms cold). The `prevhost.exe`, `DllHost.exe` and ShellHost that served it logged **`integrity=high`** (and `thread-token=no impersonation` in run 5: the handler's threads do not run under a lower token). So the level of an activated host is the user's session token, not the caller's. This is a measured fact of the runner; it is not a measurement of a standard-user session.

### 7. The three options, measured and composed

| | (a) pipe and on-demand launch | (b) packaged COM `ExeServer` | (c) C++ launcher in the handler |
|---|---|---|---|
| Can it be the handler? | The DLL is the handler | **No.** `ThumbnailHandler Clsid` must be a SurrogateServer class (makeappx). Only a helper behind a DLL | The DLL is the handler |
| Bytes from the stream to the sandbox | Section copy (0.06 ms, 4 ms, 23 ms for 3.5 KB, 5 MB, 30 MB), handle pulled by ShellHost, then to the engine | Marshaled `IStream`, read over RPC in 1 MiB calls: **7 to 9 ms at 5 MB, 39 to 44 ms at 30 MB** (measured with a C++ server); or a section handle passed in an `HRESULT` call, which needs a proxy | Section copy, duplicated into the engine by the handler's own launcher |
| Latency, 256 px, warm | **Measured:** 29 to 46 ms p50 through the shell on the runners | **Composed, not measured end to end:** (a) plus a warm `CoCreateInstance` of 1.2 to 4 ms and a 0.05 to 0.12 ms call; a stream read that is 2x slower for large files | **Composed:** the engine part alone, about 22 to 29 ms (ShellHost's server time for open plus render), no pipe, no ShellHost |
| Latency, cold | **Measured:** 207 to 474 ms (JIT ShellHost, through the shell); 110 / 139 ms (AOT, DEV) | **Measured part:** 23 to 34 ms to start a *native* ExeServer cold. A C# server adds its runtime start (68 to 136 ms in this spike's ShellHost) | About the same as warm: nothing to start but the engine (spawn p50 17 to 20 ms) |
| Who starts the server | The handler, by `CreateProcess` (worked from the surrogates at High; fails from Low; a stampede without a lock) | COM, with identity and `LaunchAndActivationPermission`; a handshake (the class is registered when the server is ready) | The handler |
| Security of the engine launch | One copy of the sandbox code (C#, in ShellHost), already reviewed | The same | **A second copy in C++** (about 1,300 lines: launcher 197, process 191, job 188, profile 86, pipe server 102, policy 42, slot ring 203, plus the client side of the protocol), inside a process that is not sandboxed; the two copies must stay in step |
| Cap on engines, negative cache | In ShellHost: measured, 3 live engines in a 16-way flood | In the server: same | **None across processes** (each surrogate acts alone): needs a named semaphore and a shared cache |
| Works from a Low host | Connect and data path: yes (label). Launch: no | Activation from a Low caller was refused (0x80070005) | `CreateAppContainerProfile` refused; the rest UNVERIFIED |
| Testable without registration | **Yes** (this spike's local half) | No: needs the package | Partly |
| Marshaling and registration extras | None | A custom interface needs `com:Interface` and `com:ProxyStub` or a typelib (not tried); an AOT-safe .NET COM server needs source-generated COM (S5 found `[ComImport]` fails under Native AOT; not tried here) | None |

**Security of every hop in A** (what an attacker who controls the PDF can reach):
1. *File to the shell to the handler (`dllhost` or `prevhost`).* The handler reads bytes and parses nothing. A hostile file cannot reach a parser here. The host is not sandboxed and has the user's rights; that is the reason nothing is parsed in it.
2. *Handler to a section.* An unnamed, pagefile-backed section with the default DACL. The handler caps it at 32 MiB.
3. *The pipe.* Per-user name; DACL `D:P(A;;GA;;;<user>)(A;;GA;;;SY)`; Low label so a Low host can use it; `PIPE_REJECT_REMOTE_CLIENTS`. ShellHost creates the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so a squatter that is already there makes it fail rather than serve. The handler checks the server before it sends anything (**measured in CI**: server image path and package family both matched: `familyMatches=1`). ShellHost reads the client's PID from the pipe, never from a message, and logs the image and the package family. Another process of the same user can use ShellHost as a "PDF to bitmap" service; it can already read the user's files.
4. *Handle pull.* ShellHost duplicates a handle value the client names, from the client's own process, and maps at most the declared number of bytes after checking the region size (`VirtualQuery`); a handle the client does not own, or one that is not a section, fails. A client can hand ShellHost a section whose contents change while the engine reads: that affects only that client's thumbnail.
5. *ShellHost to the engine.* ADR-0004 and ADR-0005 unchanged: AppContainer, job, mitigations, handles by duplication, pipe DACL. In a package the engine runs in place from the package directory (A2); CI confirmed it on both architectures.
6. *Engine to ShellHost.* Untrusted. The existing `ReplyChecks` validate dimensions and strides; the pixels are copied once.
7. *ShellHost to the handler.* A section the handler created; ShellHost writes `stride x height` bytes into it. The handler validates width, height and stride (1 to the requested size, `stride == 4 x width`, 64-bit product at most the section size) before it creates a bitmap, and forces the alpha byte to 255. No pointer crosses.

### 8. Handlers and the shell as observed

- A thumbnail provider is created, initialised and asked; the shell may keep the instance and ask again (a documented behaviour found in sources only). The prototype does no work in `Initialize`.
- **Concurrency.** The documents say nothing about how many thumbnails the shell requests at once, and this spike drove the shell one call at a time. The flood used 16 test hosts, not the shell. UNVERIFIED for Explorer.
- **A thumbnail through the shell costs 15 to 20 ms more** than the handler's own time (see above).

## Protocol: messages and schema v5

ShellHost speaks the engine framing (ADR-0005: 4-byte length, MessagePack, the limits and the strict decoders) with one pipe connection per handler instance. **The connection is the session: closing it ends the session, kills the engine and frees everything**, so a crashed handler needs no cleanup protocol (measured: the "client gone" kill above).

**What the engine agent adds to schema v5, exactly:**

1. **`ProcessKind.ShellHandler = 3`.** Sent in `Hello.processKind` by the handler DLL to ShellHost. ShellHost replies with a normal `HelloReply` (`schemaVersion`, `pdfiumVersion` = empty), and its own `Hello` to the engine still says `Broker`. The engine must **refuse** a `Hello` whose `processKind` is `ShellHandler`, and ShellHost must refuse any `Hello` that is not `ShellHandler`. It is not a supervised peer, so `EngineSupervisorOptions.Peer` does not use it.
2. **`EngineKind::Shell`** (C#, `--kind shell` on the engine command line, `EngineKind` in `engine_core.h`): at most **one** document; an **allow-list** of messages (`Hello`, `AttachSlotRing`, `Ping`, `Shutdown`, `OpenDocument`, `CloseDocument`, `GetDocumentInfo`, `GetPageSizes`, `RenderTile`, `CancelBefore`); everything else (outline, links, boxes, text, search, details) answers `Unsupported`. The memory limit and the slot sizes are ShellHost's policy, not the engine's.
3. **Three messages for the handler pipe**, ids from 2048:

   `ShellOpen` (2048, `documentId` none). Request: `section` u64, min 1, *a section handle valid in the sending (handler) process with read access; ShellHost duplicates it from the process on the other end of the pipe*; `bytes` u32, 1 to 33554432 (32 MiB), the file size (the section may be larger). Reply `ShellOpenReply`: `documentId` u32 min 1 (the session), `pageCount` u32 max 10000000, `firstWidth` f32 and `firstHeight` f32, exclusive min 0 and max 14400, in points. No password: a shell never prompts.

   `ShellRenderPage` (2049, `documentId` required). Request: `page` u32 max 9999999; `maxWidth` u32 and `maxHeight` u32, 16 to 1024 (the tile limit; raise to 4096 when the band message exists); `flags` u32 max 1 (bit 0 annotations); `pixels` u64 min 1, *a section handle valid in the sending process with write access*; `pixelBytes` u32 min 4 max 4194304 (at least `maxWidth x maxHeight x 4`). Reply `ShellRenderPageReply`: `width` u32 and `height` u32, 1 to 1024 and at most the request's maxima; `stride` u32 4 to 4096 (must equal `4 x width`). The pixels are in the caller's section.

   `ShellClose` (2050, `documentId` required). Empty request and reply. Optional, since closing the pipe does the same.

   `Hello` (1) and `Ping` (2) are used as they are. No `Shutdown` is exposed to a handler (ShellHost leaves by its own idle timer). The envelope's `deadlineMs` carries the handler's remaining budget; ShellHost clamps it to 1 ms to 1700 ms.
4. **`OpenDocument` takes a section** (optional, but it removes the temp file): add `source` (enum `DocumentSource`: `File` = 0, `Section` = 1; default `File`, key 2) and `size` u64 (key 3, required when `Section`). For a section the engine maps it read-only (`FILE_MAP_READ`), loads it with `FPDF_LoadMemDocument64`, checks that the handle is a section and that the mapped region is at least `size`; the `FILE_TYPE_DISK` check applies to `File` only. Measured value: 50.6 ms p50 at 30 MB (4.7 ms at 5 MB) of staging that disappears. If the engine agent does not take this, ShellHost keeps writing a temp file in `LocalState\private` and the system works unchanged.
5. **The generator** emits C++ *client* encoders and decoders for `Hello`, `Ping`, `ShellOpen`, `ShellRenderPage`, `ShellClose` and decoders for their replies (the handler side), and server-side decoders for the same requests (C# `ShellHost`), with the same `maxCount`, range and `sameLengthAs` rules, plus fuzz harnesses for the handler's reply decoder and ShellHost's request decoder (SHELL-18).

Statuses are the existing ones: `PasswordRequired`, `Damaged`, `Unsupported`, `Timeout`, `EngineCrashed`, `PermissionDenied` (policy off), `InvalidArgument`. A request dropped from the queue answers `Timeout`; no new status is needed.

## Proposed ADR-0018: shell handler transport

- Status: **Proposed**
- Date: 2026-11-02
- Deciders: Opus (approver), Sonnet (S6b evidence)

**Context.** Explorer and prevhost load a thumbnail provider and a preview handler into processes MikePDF does not control and does not sandbox. Those processes must never parse PDF bytes (master prompt 8.11). The sandbox launcher is C# (`MikePDF.Broker`). Evidence: [S6b](S6b-shell-transport.md).

**Options.** (a) a per-user named pipe to a packaged ShellHost started on demand; (b) a packaged COM `ExeServer`; (c) the handler starts the sandboxed engine itself.

**Decision.**
1. **A.** The handler is a native C++ DLL registered through the package manifest, `desktop2:ThumbnailHandler` and `desktop2:DesktopPreviewHandler` under the `.pdf` file type association, with `com:SurrogateServer` classes (the thumbnail class in its own AppId, the preview class with `SystemSurrogate="PreviewHost"`). ShellHost is a Native AOT executable in the package.
2. **Stream initialisation only**: `IInitializeWithStream`. No `IInitializeWithItem` or `IInitializeWithFile`.
3. **Hand-over.** The handler copies the stream into an unnamed section (cap 32 MiB; larger files fail and Explorer shows the generic icon), sends `ShellOpen` with the section handle value, and ShellHost duplicates it from the client process found from the pipe. Pixels return in a section the handler created and passed in `ShellRenderPage`. The handler validates every reply value before it builds a bitmap.
4. **Pipe.** Name per user; DACL: current user and SYSTEM; mandatory label Low; remote clients rejected; ShellHost creates the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE`; the handler verifies the server's package family before it sends anything; ShellHost checks the client's PID and logs its image and package family.
5. **Engines.** One document per engine process, a fresh engine for every file, one spare started ahead, at most 3 live (policy `shell.maxEngines`), requests queued newest first with the deadline carried in the envelope. An engine whose client disconnects is killed at once.
6. **Launch.** The handler starts ShellHost with `CreateProcess` under a launch lock, and waits for the pipe. ShellHost exits after 60 s idle.
7. **Budgets.** Thumbnail: the handler gives ShellHost 1.7 s of its 2 s. Preview: 1 s target to the first page, 5 s limit. These are MikePDF's budgets; Microsoft documents no Explorer timeout.

**Consequences.** Easier: no PDF parsing in any host, one copy of the sandbox code, testing without registration, the existing fuzz and validation machinery. Harder: a second package executable and a C++ client codec to maintain; a cold start of about 0.1 to 0.3 s after an idle minute; **a handler host at Low integrity cannot start ShellHost** (OQ-SHELL-10). Not chosen: B, because the handler cannot be an ExeServer class and a custom interface needs a proxy; C, because it copies security-critical code into an unsandboxed host and has no global cap.

**Verification.** SHELL-02, -03, -05 to -13, -15 to -17, -25 and -26 of the spec; the hosted-runner workflow of this spike kept as an integration job; an Explorer run on R1 and R2 (SHELL-23).

## Corrections the spec needs

1. **Spec 1 item 4 and OQ-SHELL-2:** measured on the runner, a packaged handler was used for `.pdf` while Edge was the default. Rewrite as a documented behaviour with its conditions once the precedence result is in (section 2).
2. **Spec 3.3, OQ-SHELL-3:** drop `IInitializeWithItem` and the file-in-a-private-area staging. Stream only (Microsoft documents it as the only path out of process).
3. **Spec 11, the handle direction:** "a handle value valid in ShellHost, duplicated by the handler" becomes "a handle value valid in the handler, duplicated by ShellHost". The first needs the handler to open ShellHost for duplication, which a Low host cannot do (inferred from the integrity rules, not measured); the second was measured from a Low client.
4. **Spec 11, schema version:** v5, not v4 (v4 is already taken by the reader gaps).
5. **Spec 3.2 candidate B:** an `ExeServer` class cannot be the handler. Remove the wording that suggests it can.
6. **Spec 7, "Uninstall: no leftover registry keys":** one empty `OpenWithProgids` key for a new extension stayed behind.
7. **Spec 3.1 and 3.2, launch:** add the launch lock (16 concurrent cold hosts started 14 extra ShellHosts) and the Low-integrity limit.
8. **Spec 3.4, preview:** `prevhost.exe` starts a new process for each activation (30 to 50 ms), so the "Loading..." at 300 ms and a 1 s target leave about 900 ms for ShellHost plus rendering after a cold start. Warm preview first pixel was 26 to 52 ms; cold 207 to 292 ms.
9. **SHELL-05, SHELL-06:** warm baselines now exist: through the shell, p95 at most 56.5 ms; cold at most 474 ms (JIT) on the runners; DEV AOT cold p95 139 ms. Set the criterion after R1 and R2 runs.
10. **OQ-SHELL-4:** engine peak working set 11 to 24 MB on the fixtures; keep 256 MB and the 1.7 s deadline; neither limit was reached, so a hostile-file run is still owed.

## Open questions this spike raises

| Id | Question | Who |
|---|---|---|
| OQ-SHELL-10 | At what integrity level does Explorer's activation run our packaged handler on a standard-user machine? The runners showed the user's session token, never Low (so Medium is expected there), but the documentation says prevhost uses Low by default. If Low, the handler cannot start ShellHost, and a launch path across integrity levels is needed (a ShellHost started by the app or by a startup task; a Low caller is refused COM activation of our classes). Needs a real Explorer run on a non-elevated session | User and Opus (R1, R2) |
| OQ-SHELL-11 | Handler precedence on a machine with Acrobat or Edge's own thumbnail handler, with MikePDF not the default | User (a machine with Acrobat) |
| OQ-SHELL-12 | Does Explorer's block on previewing downloaded files apply before a third-party handler is created, and should MikePDF say so in its settings text? | Opus with a test on a real Explorer |
| OQ-SHELL-13 | Should the engine accept a section in `OpenDocument` (saves up to 50 ms at 30 MB)? | Opus (schema v5 owner) |

## Not verified

- **The real Explorer.** No `explorer.exe` thumbnail request, no preview pane, no folder of files. The shell was driven by `IShellItemImageFactory` and COM activation from a test process. How many thumbnails Explorer asks for at once, in how many `dllhost` instances, how long it waits for a handler, what it does when one is slow or fails, and whether it caches a failure: all UNVERIFIED.
- **A standard-user session.** The runner's session token is High and elevated, so every COM-activated host was High. A Medium *caller* was tested (label mechanism), but the hosts it activated were High. What level the hosts have when the session token is Medium (a normal user) was not observed.
- **Handler precedence against a competing registration, when the competing app is the user's default.** [[RUN4-NV]]
- **The handler at Low integrity inside a real prevhost.**
- **The Store-signed package, Smart App Control, SmartScreen, Defender.** The test package is signed by a runner-only certificate; whether any of them delays or blocks starting ShellHost is untested.
- **Windows' own timeout for a slow handler**, which Microsoft does not document.
- **A scrollable multi-page preview, keyboard and focus (`TranslateAccelerator`), DPI changes, UIA.** The preview prototype paints page 1 only.
- **OneDrive placeholders, network shares, files over 32 MiB** (the stream path is capped, and a larger file only fails here).
- **A genuinely hung engine.** A render that outlasts the handler's deadline was used, not an infinite loop; the engine watchdog is ADR-0004's.
- **A hostile file against the 256 MB limit.** The limit is a commit limit; the fixtures never approached it.
- **A C# COM server under Native AOT**, and a custom interface's proxy/stub through `com:Interface`. Option B was measured with a native server only.
- **ARM64 on DEV.** ARM64 ran only on the hosted runner. A cold start from a rebooted machine (nothing in the OS cache) was not measured.
- **DEV numbers** are under 40 to 43% background CPU from other agents; the p95 and the cold figures move by tens of per cent between the two runs.

## How to reproduce

- Local (nothing is registered): `spikes/S6b/native/build.ps1 -Arch x64`; `dotnet build spikes/S6b/ShellHost/S6bShellHost.csproj -c Release`; `python spikes/S6b/gen_pdfs.py spikes/S6b/out/pdfs`; `spikes/S6b/run-measure.ps1` (latency, sizes, memory, inputs), `run-lowil.ps1`, `run-faults.ps1`, `run-flood.ps1`, `run-lowil-launch.ps1`. The engine comes from `./build.ps1 -SkipManaged`.
- Hosted runner: push to `m3/spike-shell` (or run the workflow by hand). **Never run `spikes/S6b/ci/*` on a developer machine**: it installs a package with a self-signed certificate and writes `HKCU\Software\Classes`.

## Evidence index

| What | Where |
|---|---|
| Local latency CSVs and tables | `spikes/S6b/out/m-*.csv`, `summary-local.md`, `summary-local-r2.md`, `environment.txt`, `commands.txt` |
| Native AOT ShellHost | `m-cold-aot-256.csv`, `m-warm-aot-256.csv`, `m-mem-aot-256.csv` |
| Low-integrity client | `lowil.txt`, `lowil-launch.txt` |
| Faults, timeout, kill | `faults.txt` |
| Flood | `flood-16x10-max3.txt` |
| Hosted runs | `ci-run1/`, `ci-run2/`, `ci-run3/`, `ci-run4/` (per architecture: `driver/driver.log`, `driver-*.log`, `experiment.log`, `handler.log`, `shellhost.log`, `comserver.log`, `reg-*.txt`, `processes-*.txt`, `events-*.txt`, `install.log`) |
| Thumbnail of the test PDF | `smoke.png` |
