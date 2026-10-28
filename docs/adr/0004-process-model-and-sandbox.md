# ADR-0004: Process model and sandbox
- Status: Accepted, with one deviation from §5.3 (Win32k lockdown deferred, D-1 below). Timing gates provisional until R1/R2
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S3 and S3b evidence)

## Context
§5 sets out the multi-process model:
- a UI/broker process that never parses PDF bytes
- single-threaded PDFium engine processes in a zero-capability AppContainer with mitigation policies
- a Job Object memory cap
- a watchdog
- crash recovery

[S3](../spikes/S3.md) tested this with a .NET stub (S3) and a C++20 stub built with MSVC 14.44, `/guard:cf /CETCOMPAT /sdl` (S3b), on DEV.

**Negative access tests.** Under the hardened profile every one was denied: user files, temp, the repo, UNC paths, TCP to localhost and to the internet, `CreateProcess`, `OpenProcess` on the broker, the clipboard, HKCU writes, and loading an unsigned DLL. Each denial was checked against an unsandboxed control run. A plain AppContainer still allowed `cmd.exe` and the unsigned DLL. The child-process policy plus a runtime signature policy close both.

**Low-integrity fallback.** It still reads Documents, reaches the network, reads the clipboard and opens 49 other processes. It fails the bar.

**Mitigation results with the C++ stub:**
| Mitigation | Result |
|---|---|
| DEP, ASLR, strict handle checks, extension-point disable, CFG, CET (compat), image-load restrictions, font-disable, child-process restriction | Survive |
| Prohibit dynamic code | Survives |
| Microsoft-signed-only, applied **at creation** | Fails: pdfium.dll refused with 577 |
| Microsoft-signed-only, applied **at runtime after load** | Works |
| Win32k lockdown | **Fails with stock pdfium.dll.** Static gdi32/user32 imports kill the loader (`0xC0000142`). Delay-loaded, PDFium init returns 1359. Fails the same way outside AppContainer, so it's PDFium, not the sandbox |

**Fonts.** PDFium's GDI font path works inside the AppContainer, pixel-identical to outside it. A broker-fed `FPDF_SYSFONTINFO` provider works too, and renders CJK that GDI misses on a machine without those fonts.

**Timings (C++ stub, DEV, 18–29% background CPU):**
| Metric | p50 | p95 | Notes |
|---|---|---|---|
| Spawn to Hello | ~10 ms | 10.6 ms plain, 15.6 ms hardened | |
| Crash to restored tile | ~18 ms | ~130 ms hardened | About 4 in 30 hardened restarts take +110 ms; not root-caused. Budget is ≤ 2 s |
| Ping | | p99 24–34 µs | |

**Job memory limit.** When the limit is hit the OS does not kill the engine. Allocation fails (error 1455), and the IOCP notification is the only signal.

## Decision
1. **Processes.**
   - `Quire.App`: UI + broker, packaged full trust, medium IL. Never parses PDF or CMS bytes.
   - `Quire.Engine` (C++20 + PDFium), one per open document plus one shared pool engine for thumbnails and search, capped at 6 engines by default (policy-configurable).
   - `Quire.WriterHost` (self-contained .NET, ADR-0008), one per save, validation or signing job.
   - Later: shell-extension engines (§8.11) and `Quire.AI` (§5.1).
2. **Sandbox profile, applied to every untrusted-content process:**
   - AppContainer with **zero capabilities**. One profile per package (`Quire.Engine`), derived from the package family.
   - Creation attributes: `SECURITY_CAPABILITIES`, `HANDLE_LIST` (document handle, slot section, log pipe only), `JOB_LIST`, `CHILD_PROCESS_POLICY=RESTRICTED`.
   - Mitigation flags at creation: DEP, force-relocate/bottom-up/high-entropy ASLR, strict handle checks, extension-point disable, CFG, CET shadow stack (compat; strict mode to be evaluated in M5), image-load (no remote, no low-label), non-system-font disable. For the C++ engine, **prohibit dynamic code** is also set at creation. `Quire.WriterHost` runs without it, because the .NET JIT needs dynamic code; a Native AOT WriterHost is the M5 target that lets us turn it on.
   - **Signature policy is applied by the process itself at runtime**, after loading its fixed DLL set and before receiving any document. That covers the window an attacker could use, because no document bytes have arrived yet. Whether Store/MSIX signing allows the creation-time policy is a M1 check (S6.3).
   - Launched detached (no console).
   - ACLs: the AppContainer SID gets read+execute on the engine's package directory only.
3. **Job Object.** Kill-on-close, active-process limit 1, UI restrictions, and a per-process memory limit (default 2 GB, set by policy, §5.2 rule 5). The broker listens on the job's IOCP. A `JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT` message means: terminate, report `EngineCrashed`, respawn.
4. **Watchdog.** I/O-lane ping every 1 s (ADR-0005). After 5 s with no reply, `TerminateProcess`, then respawn, reload from the broker's handle, replay the journal, and restore position (§5.2 rule 3).
5. **No silent fallback.** If AppContainer creation fails, Quire shows an error and does not open the document. The low-IL token is **not** an acceptable fallback (S3 evidence). An enterprise policy may allow it explicitly, with a visible warning.
6. **Fonts.** v1 uses PDFium's GDI font path inside the AppContainer. The broker font provider (`FPDF_SYSFONTINFO`, prototype in `spikes/S3/engine/FontProvider.cs`) moves to the M5 hardening track, together with D-1.

## Deviation from §5.3
- **D-1: Win32k lockdown is not applied in v1.0.** §5.3 makes it conditional ("if rendering stays fully in software"). S3b shows stock PDFium can't initialise without win32k. Enabling it needs a **patched PDFium build**: delay-loaded or no gdi32/user32, with the Windows font backend replaced by the broker font provider. That's tracked as risk R-30, with a target of M5 and our own PDFium build (ADR-0002). All other §5.3 mitigations are applied.

## Consequences
- A third process type and a font provider to maintain later.
- Restarts are cheap: the crash-to-restore budget of ≤ 2 s has about 15× headroom at p95 on DEV.

## Verification
- Re-run the negative suite and mitigations from a **packaged MSIX** launcher on R1 and R2 (S3 open item). The suite becomes an integration test in M1.
- Find the cause of the hardened-restart outliers in M1.
- M1 gate: the headless `quire-render` CLI runs the smoke corpus through this exact sandbox profile.

## Amendment A1 (2026-10-28): M1 implementation evidence
- **No handle inheritance at all**, replacing the `HANDLE_LIST` approach in decision 2.
  - Sandboxed processes are created `CREATE_SUSPENDED` with `bInheritHandles=FALSE`, joined to their job atomically through `JOB_LIST`.
  - The broker `DuplicateHandle`s the slot section and documents into the child and announces them over the pipe (`AttachSlotRing`, `OpenDocument`). Only then is the process resumed.
  - Reason: any other `CreateProcess(bInheritHandles=TRUE)` in the UI process, such as .NET `Process.Start` with redirected streams, could otherwise leak an inheritable section handle (Opus review R1 of WP4).
  - Enforced by an integration test that scans the broker's handles.
- **Diagnostics** go over a second named pipe with the same DACL and PID check, because there is no inherited stderr.
- **`Quire.WriterHost` signature policy:** Microsoft-signed-only is applied at runtime **after a warm-up that loads every assembly the host needs**. Applied earlier, it fails: `Quire.PdfWriter.dll` gave `FileLoadException` 0x80070241, because our own assemblies are unsigned and CoreCLR maps them as images. Consequence: any code path that lazily loads an unsigned assembly after warm-up kills the host. That fails closed, and it's caught by tests. Revisit once our own assemblies are Authenticode-signed (release pipeline), when a creation-time policy that allows our signer may be possible.
- **Engine environment** must include `LOCALAPPDATA`, `USERPROFILE` and `APPDATA`. Without them, `CreateProcess` fails with error 203.
- The **watchdog defaults** (1 s ping, 5 s kill) are asserted by unit tests.
