# ADR-0018: Shell handler transport
- Status: Accepted
- Date: 2026-11-03
- Deciders: Opus (approver), Sonnet (S6b evidence)

## Context
Explorer and prevhost load a thumbnail provider and a preview handler into processes MikePDF does not control and does not sandbox. Those processes must never parse PDF bytes (master prompt 8.11). The sandbox launcher is C# (`MikePDF.Broker`). Evidence, measurements and the full message list: [S6b](../spikes/S6b-shell-transport.md).

## Options considered
1. **A per-user named pipe to a packaged ShellHost started on demand.** Measured end to end, packaged, through the shell on x64 and ARM64: warm p50 29 to 46 ms, cold 207 to 474 ms (JIT) and 110 ms p50 (Native AOT, DEV).
2. **A packaged COM `ExeServer`.** `makeappx` rejects a `desktop2:ThumbnailHandler` whose class is not a `com:SurrogateServer`, so it can only be a helper behind a DLL; its marshaled stream read is about twice as slow as a local copy.
3. **The handler starts the sandboxed engine itself.** About 1,300 lines of security code ported to C++ inside an unsandboxed host, with no global engine cap; `CreateAppContainerProfile` is refused at Low integrity.

## Decision
Option 1, as set out in the spike's proposed ADR:
1. The handler is a native C++ DLL registered through the package manifest (`desktop2:ThumbnailHandler` and `desktop2:DesktopPreviewHandler` under the `.pdf` association, with `com:SurrogateServer` classes; the preview class uses `SystemSurrogate="PreviewHost"`). ShellHost is a Native AOT executable in the package.
2. Stream initialisation only (`IInitializeWithStream`).
3. The handler copies the stream into an unnamed section (cap 32 MiB) and ShellHost duplicates the handle from the client process found from the pipe. Pixels come back in a section the handler created; the handler validates every reply value before it builds a bitmap.
4. The pipe is per user, with a DACL for the user and SYSTEM, a Low mandatory label, remote clients rejected and `FILE_FLAG_FIRST_PIPE_INSTANCE`. The handler checks the server's package family before it sends anything.
5. One document per engine process, a fresh engine per file, one spare started ahead, at most 3 live (policy `shell.maxEngines`). An engine whose client disconnects is killed at once.
6. The handler starts ShellHost under a launch lock. ShellHost exits after 60 s idle.
7. Budgets: thumbnail 2 s (1.7 s given to ShellHost); preview 1 s target to the first page, 5 s limit. These are MikePDF's budgets; Microsoft documents no Explorer timeout.

Decided with this ADR:
- **No section source in `OpenDocument` for v1.0** (OQ-SHELL-13). ShellHost stages the bytes to a temp file in `LocalState\private`. That costs 5 ms at 5 MB and 51 ms at 30 MB, well inside the budget, and keeps the engine's attack surface as it is. Revisit only if the reference-hardware runs show staging matters.
- **The shell messages** (`ProcessKind.ShellHandler`, `EngineKind::Shell`, `ShellOpen`, `ShellRenderPage`, `ShellClose`) are added by the shell-extensions work package, which owns the protocol schema in its wave and takes the next schema version. They aren't part of the engine work package's v5.
- **Registry experiments on hosted runners are allowed.** GitHub-hosted runners are disposable, so the S6b workflow's `HKLM\Software\Classes` writes, guarded by `GITHUB_ACTIONS` and `RUNNER_ENVIRONMENT=github-hosted`, are acceptable. Nothing is ever registered on a developer machine.

## Consequences
- No PDF parsing in any shell host, one copy of the sandbox code, and the handler path is testable without registering anything.
- A second package executable and a C++ client codec to maintain, and a cold start of about 0.1 to 0.3 s after an idle minute.
- **A packaged handler applies whenever the package is installed,** not only while MikePDF is the default PDF app, and it won over classic registrations in the spike. What the "off" switches do is a product decision (OQ-SHELL-14). Until the product owner decides, the default is: thumbnails and preview on, with a setting and a policy to turn them off. The shell work package first tests whether the shell falls through to the next handler when ours declines; if it doesn't, the settings text says that "off" shows the generic icon.
- **A handler host at Low integrity can't start ShellHost** (OQ-SHELL-10). The runners only showed High hosts, so the standard-user case needs a real Explorer run.

## Verification
SHELL-02, -03, -05 to -13, -15 to -17, -25 and -26 of the shell-extensions spec; the spike's hosted-runner workflow kept as an integration job; an Explorer run on a standard-user session on R1 and R2 (SHELL-23), which only the user can do.

## Amendments made while building it (W2-shell, 2026-11-03)
These refine the decision; none reverses it.
1. **Two pipes, one ShellHost.** The thumbnail provider connects to `MikePDF.Shell.<user SID>` and the preview handler to `MikePDF.Shell.<user SID>.Preview`. Same DACL, same label, same process, same engine budget. The reason: `ShellOpen` has no field that says which feature asks, and SHELL-16 needs "thumbnails off, preview on" and the reverse to be answered in 100 ms before any work is done. A field would change schema 5, which nobody owns this wave; a second pipe name costs nothing. The pipe also decides the fit rule (a thumbnail is never scaled above 2x its natural size; a preview fits the pane's width).
2. **The negative cache is keyed by content.** With stream initialisation ShellHost has no file ID or last-write time. The key is the size and the SHA-256 of the bytes already in the section (about 1 to 2 ms for a typical file, about 20 ms at the 32 MiB cap).
3. **Form fields are not drawn yet.** The spec asks for form fields drawn without highlight. The engine answers `Unsupported` to the form-fields bit of `RenderTile` until forms ship (no form-fill environment), so a request that set it would fail every thumbnail. ShellHost asks for annotations only.
4. **The spare engine counts toward `shell.maxEngines`**, so "at most 3 live" is about processes, not about requests in flight. A spare that failed to start or died while it waited is skipped, not used to fail the next request.
5. **Test overrides.** A ShellHost or handler with no package identity (only the tests run so) reads `MIKEPDF_SHELL_TEST_*` environment variables for the pipe name, the engine directory, the AppContainer profile, the data folder and the idle time. A packaged one never reads them.
6. **The Low-integrity launch limit is unchanged and still UNVERIFIED** on a standard-user machine (OQ-SHELL-10).
