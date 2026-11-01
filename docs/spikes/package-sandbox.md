# Package sandbox: can the ADR-0004 sandbox run the engine from an installed MSIX?

- Status: **Done on real installs (GitHub-hosted x64 and ARM64 runners). Not run at medium integrity, and not run with the Store-signed package.**
- Date: 2026-11-02 (revised after review the same day: the first version rejected direct launch on evidence that did not support it)
- Author: Sonnet (implementation role). Reviewer: Opus.
- Feeds: ADR-0004 amendment A2, `Quire.App` (`EngineHost`, `AppPaths`), `Quire.Broker.PrivateDirectory`.
- Evidence: [`docs/test-reports/package-sandbox/`](../test-reports/package-sandbox/) (raw `results.md`, ACL dumps, probe output, install logs of four CI runs).
- Code: `tests/Quire.PackageProbe` (runs inside the package), `build/sandbox-probe/*`, `.github/workflows/package-sandbox-probe.yml`, all on branch `m1/sandbox-probe`. Experimental `SandboxPolicy` options (`NamedCapabilities`, `BreakawayFromPackage`) and the improved probe (`--rogue-only`, the protected-directory and rename rows, `rogue-marker.dll`) exist only there.

## Question
ADR-0004 gives the AppContainer read and execute on "the engine's package directory only". An installed package's directory cannot have ACLs set. Can a custom AppContainer child run the engine from it, with how many capabilities, and what else does a packaged broker change?

## Method
1. **This machine** cannot install an unsigned package that has executables without administrator rights (`Add-AppxPackage -AllowUnsigned` fails with `0x80073D2B`; [docs](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)); elevation and trust-store changes are not allowed here. Local runs use loose registration (Developer Mode), whose ACLs differ from a real install.
2. **CI** (`package-sandbox-probe.yml`) builds a test-only MSIX with the real engine, the sandbox probe, an unsigned marker DLL and a self-test executable with an execution alias; signs it with an ephemeral self-signed certificate that exists only on the throwaway hosted runner (the script refuses to run anywhere else); installs it under `C:\Program Files\WindowsApps`; and runs the self-test with real package identity. Per launch mode: Hello, AttachSlotRing, OpenDocument and RenderTile on a real PDF through `SandboxedProcessLauncher`; the full negative-access probe in the same container; and the **unsigned-DLL row in its own sandboxed process**, so a process death there is recorded without losing other rows. A control run of each check unsandboxed shows it could have succeeded.
3. **The unsigned-DLL proof.** `rogue-marker.dll` is unsigned; its `DllMain` calls the probe's exported `quire_probe_emit` to print `ROGUE-CODE-RAN`. Denied means: the marker did not appear in the sandboxed run, while the unsandboxed control showed the marker (so the mechanism works), and the loader's refusal is on record (Code Integrity event 3033, or load error 577). A death by exit code alone is not accepted as evidence.

Runs: [1](https://github.com/mjtpena/QuirePDF/actions/runs/41348083248), [2](https://github.com/mjtpena/QuirePDF/actions/runs/41348813493), [3](https://github.com/mjtpena/QuirePDF/actions/runs/41349278651), **[4 (x64 and ARM64, the deciding run)](https://github.com/mjtpena/QuirePDF/actions/runs/41356102185)**. Runners: Windows 10.0.26100 (x64) and 10.0.26200 (ARM64), user `runneradmin`, administrator at **high integrity**.

## What the first version got wrong
- Mode a's probe printed 18 rows and then the process was ended by the loader at the unsigned-DLL row, so three rows never ran. That is a harness gap (the row sat before the positive controls), not an escape. The proof above replaces the inference.
- The first A2 said that under direct launch "the engine could write its own directory". The results said the opposite: `fs.write.engine-dir` was **denied** in mode a.

## Results, run 4 (x64 and ARM64 gave identical results)
Package directory ACL on the real install: Users read/execute, TrustedInstaller/SYSTEM, the `packageContents` capability, and **one ACE for the package's own SID in capability form (`S-1-15-3-<package>`) with read and execute**; no `ALL APPLICATION PACKAGES` entry.

| Mode | Launched, real tile rendered | Rows run (all denied except the two package-data rows) | Other package's file | Engine dir write | Engine dir **rename** (an unused twin / sibling, so the running exe's lock does not decide it) | Protected private file read | Protected private dir rename | Unsigned DLL alone in a process |
|---|---|---|---|---|---|---|---|---|
| **a** direct from `WindowsApps` | yes, yes | 23, yes | denied | denied | **denied** | denied | denied | **denied: code did not run**, process ended by the loader (exit `0x60C201E`), event 3033 names the DLL, control shows the marker |
| **k** copied to `LocalState`, protected DACL, AppContainer SID read+execute | yes, yes | 23, yes | denied | denied | **allowed** | denied | denied | denied: load error 577, process survived, code did not run |

The two package-data rows (`fs.read.parent-package-data`, `fs.write.parent-package-data`) are **allowed in both modes**, for the reason in the next section. In a, the engine-directory write is also denied to the unsandboxed user (WindowsApps is read-only for everyone), so that row's control cannot discriminate there; the denial that matters is the package's own ACL.

Earlier runs (details in the report folder): mode b (`packageContents`) does not launch on a real install (`CreateProcess` 15612); copy-out with inherited ACEs (c) lets the engine write its own directory; `%LOCALAPPDATA%` (h) is redirected by MSIX and the engine cannot find `pdfium.dll`; breakaway (d, e) and free-standing container SIDs (i, j) do not launch.

## Findings
1. **Direct launch works and passes everything** that the copy-out passes. The loader refuses unsigned code in the package directory more strictly than elsewhere: it ends the process (Code Integrity event 3033) instead of returning error 577. For the engine, which loads no such DLL, that does not matter; it is also what we want.
2. **A packaged broker's sandbox child is not zero-capability.** `CreateAppContainerProfile` from a packaged process creates a child container of the package; the OS gives it exactly one capability, the package's own SID. That SID has full control of the package's data folders (`LocalState`, `LocalCache`, ...), so **the engine can read and write the app's private data there**. It cannot read user documents, other packages, the network or the registry (all rows denied). It also explains why direct launch works.
3. **A protected directory keeps data from the engine.** `LocalState\private-probe`, protected DACL with the user and SYSTEM only: the engine cannot read a file in it and cannot rename the directory. Likely reason (inferred, not isolated): opening a directory in order to rename it needs at least attribute-read access, which only an ACE can give, and the engine's token matches none. A deployment directory that must give the AppContainer read and execute (k) *does* have such an ACE, and the engine could rename it (row allowed in k; the parent's `FILE_DELETE_CHILD` on `LocalState` is the likely route). That is a persistence route into the engine that opens the next document, and a reason against copy-out.
4. **Uninstall removes the protected directory.** After `Remove-AppxPackage` the package's whole data folder, including `LocalState\private-probe` and its file, was gone on both architectures (SYSTEM has full control on it).
5. **Package data in use.** After a run the only file Quire's code writes there is `LocalState\private\documents.json`. The OS adds `Settings\settings.dat`, `Settings\roaming.lock` and the `SystemAppData\Helium` registry hive; Quire does not use `ApplicationData.LocalSettings`.

## Why not copy-out (reviewer's points, now evidence or risk)
- Copy-out puts unsigned executables in a user-writable folder, outside any package signature. Smart App Control blocks unsigned code that its cloud intelligence does not predict to be safe, and a Store build whose engine is such a copy could fail to open any document on those machines. I found nothing official on how SAC treats code copied out of a Store package; this is a **risk, not a fact**, and not tested (no SAC machine). Direct launch removes the question.
- It adds a race between the hash check and the launch, and the stager code and manifest to maintain.
- The engine can move the deployment directory (finding 3).

## Decision
Launch the engine **in place from the package directory** (mode a). `EngineStager`, `engine.manifest.json`, the `PreLaunch` hook and their tests are removed. The broker's own files go in `LocalState\private` with a protected DACL (user and SYSTEM only), re-applied at every start (`PrivateDirectory.Ensure`).

## Not verified
- **Medium integrity.** The runner is a high-integrity administrator. Two attempts to start the packaged probe through `runas /trustlevel:0x20000` (directly and through `cmd /c`) produced no results on either runner, so a filtered-token run was not achieved. Access rows are ACL-based on the user and AppContainer SIDs and should not depend on the broker's integrity, but that is an inference.
- The Store-signed package (the Store re-signs; the installed ACL pattern was the same for Windows Terminal's package, so expected).
- Smart App Control behaviour for either mode.
- The `15612` launch failures of rejected modes were not explained.
