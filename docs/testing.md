# Testing

`./build.ps1 -Test` runs every headless suite. The UI journeys (`tests/MikePDF.UI.Tests`, ADR-0009) drive the real app and are not part of it.

## UI journeys in CI

The journeys run on GitHub-hosted Windows runners in [`.github/workflows/ui-journeys.yml`](../.github/workflows/ui-journeys.yml): on every pull request, on every push to `main`, and by hand. **App-shell changes are UI-verified here, not on a developer's desktop.** Nobody runs `build/run-ui-tests.ps1` locally for that any more: it takes over the mouse and keyboard.

What the job does, on x64 (`windows-2025-vs2026`) and ARM64 (`windows-11-vs2026-arm`) in parallel:

1. Prepares the desktop (below).
2. Runs `build/run-ui-tests.ps1 -Arch <arch> -Ci`: it builds the engine and the app, registers a **copy** of the package whose manifest has the Explorer thumbnail handler, the preview handler and the `windows.comServer` extension removed (it throws if any remain, before or after registering), and runs the journeys through UI Automation. Nothing is registered with the shell, on the runner or anywhere else.
3. Uploads `ui-journeys-trx-<arch>` (the TRX report) and `ui-journeys-shots-<arch>` (every screenshot a journey took, `timeout-*.png` for each wait that expired, and `desktop-after-failure.png`).

`-Ci` adds three things to the script, and only when `GITHUB_ACTIONS` is `true` and `RUNNER_ENVIRONMENT` is `github-hosted` (it throws otherwise): Developer Mode (`AppModelUnlock\AllowDevelopmentWithoutDevLicense`, which is what lets `Add-AppxPackage -Register` take an unsigned layout), the Windows App SDK framework package from the pinned NuGet package when the runner has none at or above the pin, and the TRX report. The journeys use the same manifest copy and the same checks as a local run. Developer Mode is a machine setting, so `-Ci` refuses to run on a developer machine.

### What a hosted runner gives a UI test

Measured on both runners (the first step of the job prints it every run):

| | x64 `windows-2025-vs2026` | ARM64 `windows-11-vs2026-arm` |
|---|---|---|
| Interactive desktop | Yes: session 2, user `runneradmin`, Explorer running | Yes: the same |
| Screen | 1024x768 at start; the Hyper-V video adapter offers up to 1920x1080 | The same |
| Clipboard | Works (STA thread) | Works |
| Framework package | Not installed: the script installs `Microsoft.WindowsAppRuntime.2` | Newer one installed (2.6.4): the script keeps it |
| First-sign-in page | None | "Choose privacy settings for your device" is up, full screen and topmost, over everything |
| Other windows | A Windows Terminal with the runner agent | A console of the runner agent, a `wsl.exe` console asking to install WSL, and the Start menu left open after the first-sign-in page |

The first runs of the job failed for these reasons, and each is why a step exists:

- **Screen size.** At 1024x768 the command bar folds `FullScreenButton`, `PageColourButton` and `PropertiesButton` into its overflow menu, so they are not in the UIA tree, and a window cannot be smaller than the monitor, so the full-screen journeys wait for ever. The "Prepare the desktop" step sets 1920x1080 (`ChangeDisplaySettings`) and fails the job if it cannot.
- **The ARM64 first-sign-in page** covers the app (it is a WebView in an Explorer-hosted window, class `Shell_OOBEProxy`, with no UIA tree, and closing, hiding or ending its web processes does nothing). The step clicks its button until the window is gone: Next, then Accept, at 1920x1080 coordinates. 86 of 97 journeys failed before this.
- **The Start menu and the `wsl.exe` console** take the keyboard from the app on ARM64 after that. The console ("Press any key to install Windows Subsystem for Linux", then in a Windows Terminal) comes back in the middle of the run, so the step sends Escape to close the Start menu and starts a hidden watcher that ends `wsl`, `wslhost` and a terminal titled for `wsl.exe` every two seconds for the rest of the job.

### Order-dependent state (fixed)

`ReaderGapsJourneyTests` and `ViewModesJourneyTests` did not reset the app's settings the way the other classes do (`AppData.ResetSettings()`), so the page colour the dark-pages journeys leave behind decided whether `C5_C6_C7_PageColoursChange…` saw normal paper. They passed or failed with the test order. Both classes now reset settings before and after each journey.

### Waiting on state, not on luck

Every journey waits on state (`app.WaitFor(...)`), not a fixed sleep. A hosted runner also loses an occasional key or flyout, so the page object has three helpers that repeat the action only while its effect is missing: `app.OpenMenu(button, item)` (a flyout closed by a focus change is opened again, up to three times), `app.StartPresentation()` (Ctrl+L again if no presentation started), and `MikePDFApp.Launch`, which makes the window the foreground window and waits for the keyboard focus to be inside MikePDF before a journey sends anything. `PRINT21` waits for focus to land after Tab, and `TAB09` activates the window and drags again if it did not move. When a wait expires or a control is missing, the journey saves `timeout-*.png` / `missing-*.png` and a `*.foreground.txt` next to it naming the window that had the keyboard; that note is what showed the `wsl.exe` console on ARM64.

### Flakes: one rerun, by name

Even so, a few journeys fail once in a while on a hosted runner and pass when run again (the cause is a lost key or flyout, not an app defect that a person would hit). With `-Ci`, `build/run-ui-tests.ps1` reruns **only the journeys that failed**, once, each with a fresh app (`ui-journeys-<arch>-rerun.trx`). One that passes the second time is a **flake**: it is named in the job summary and in `flaky-<arch>.txt` (both in the artifacts) and the job is green. One that fails twice fails the job. More than ten failures in a run is a broken run, not flakiness, and gets no rerun. A journey that shows up as a flake twice gets a roadmap row and a fix, or a quarantine.

### Reliability evidence

Five runs in a row, on both runners, green, on the final harness (the five differ only by merges of `main` and empty commits). `ui-journeys-x64` / `ui-journeys-arm64`:

| Run | x64 | ARM64 |
|---|---|---|
| [41757634126](https://github.com/mjtpena/MikePDF/actions/runs/41757634126) | 109 passed | 108 passed |
| [41759844608](https://github.com/mjtpena/MikePDF/actions/runs/41759844608) | 109 passed | 108 passed |
| [41762734967](https://github.com/mjtpena/MikePDF/actions/runs/41762734967) | flake: `PV23_APseudoRtlWindow…`, passed on rerun | flake: `CMD05_AReaderShortcutDoesNotRunWhileATextBoxHasFocus…`, passed on rerun |
| [41765394106](https://github.com/mjtpena/MikePDF/actions/runs/41765394106) | 109 passed | 108 passed |
| [41767430470](https://github.com/mjtpena/MikePDF/actions/runs/41767430470) | flake: `TAB11_ClosingAWindowWithThreeUnsavedTabs…`, passed on rerun | flake: `PV18_ARightToLeftDocument…`, passed on rerun |

Of the 10 job runs, 6 had no failure at all and 4 had exactly one flake. The x64 job runs about 15 minutes, ARM64 about 20 to 25. Counts: 111 journeys, 2 skipped on x64 (the sample-writer and the wheel quarantine) and 3 on ARM64 (those and TAB10).

The two journeys the 2026-11-02 local run saw fail: `C5_C6_C7_PageColoursChange…` passed in all five runs (it failed on x64 before the settings reset, and the reset fixed it); `TAB10_ClosingATabWithUnsavedEdits…` passes on x64 in all five runs and fails on ARM64 every time, so it is quarantined there (below).

### Quarantine

A journey that fails on a hosted runner and passes on a person's desktop is skipped there with `[UiFact(ciQuarantine: "reason", ciArch: "arm64")]` (`ciArch` is optional). The attribute skips only when `-Ci` is set (`MIKEPDF_UI_CI=1`), so a local run still runs it, and the skip shows in the TRX with its reason. Every quarantine has a row in the [roadmap](milestones/production-roadmap.md); remove the attribute when the row is closed.

| Journey | Where | Reason |
|---|---|---|
| `ViewModesJourneyTests.PV08_PV20_InThePagedFlowTheKeysTurnPagesAndTheWheelNeedsOneNotchMore` | x64 and ARM64 | The mouse-wheel notch (`Mouse.Scroll`) did not turn the page on the hosted runners in 4 of 5 runs, with the pointer over the page and the app idle (the screenshot shows page 1, no dialog). Keys, clicks and drags work. Cause not found; a retry with a pause did not help. |
| `TabsWindowsJourneyTests.TAB10_ClosingATabWithUnsavedEditsAsksAndCancelKeepsItAndDontSaveClosesIt` | ARM64 | After Cancel on the close question, the second Ctrl+W shows no question: the screenshot shows keyboard focus on the first tab header, not the page. It failed in 3 of 3 ARM64 runs, even with the `wsl.exe` console and the Start menu kept away, and passed in every x64 run. It may be a real focus-restoration problem in the app after the dialog closes; see the roadmap row. |

### Reading a failure

Download `ui-journeys-trx-<arch>` for the failing journey and its message, then `ui-journeys-shots-<arch>`: `timeout-<what it waited for>.png` is the app at the moment the wait expired, and `desktop-after-failure.png` is the whole desktop. Every journey waits on state (`app.WaitFor(...)`) rather than a fixed sleep; a new journey should too.

### Running it more than once

`ui-journeys.yml` can be started by hand (Actions, "UI journeys", Run workflow); a manual run has a group of its own, so several can run side by side. A push to a pull request cancels the older run of that branch.
