# ADR-0009: UI automation framework for tests
- Status: Accepted
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S6.6 and S7 evidence)

## Context
§12.1 and §12.5 need end-to-end UI journeys. ADR-0001 recorded the following, and [S6](../spikes/S6.md) confirmed it:
- **WinAppDriver** is no longer actively developed.
- The **Appium Windows driver** is deprecated and proxies WinAppDriver.
- **FlaUI.UIA3 5.0.0** (MIT, last release 2025-02-25) drove a WinUI 3 app on Windows App SDK 2.5.1 under JIT, Native AOT and packaged MSIX.

[S7](../spikes/S7.md) drove its accessibility checks through a separate-process UIA client using the native `IUIAutomation` COM API, with 251 checks.

## Options considered
1. **FlaUI.UIA3.** An in-process library, no server to install, works today. Maintenance has slowed.
2. Appium plus the Windows driver. Rests on unmaintained WinAppDriver. **Rejected.**
3. Raw `IUIAutomation` everywhere. Works with no dependency, but is verbose.
4. Newer Appium drivers (appium-wincore-driver, NovaWindows). Untested on WinUI 3.

## Decision
- **FlaUI.UIA3 5.0.0 for UI journeys**, behind a Quire page-object layer (`tests/Quire.UI.Tests/Pages/*`) so the driver can be swapped without rewriting the journeys.
- **Raw `IUIAutomation` for accessibility assertions** (TextPattern walking, events), reusing S7's `UiaNative` approach, so a11y tests don't depend on how FlaUI wraps those patterns.
- UI tests need an interactive desktop session. Locally they run in Developer Mode against an unsigned MSIX. In CI they run on a self-hosted runner with auto-logon (hosted runners: whether the desktop is available is to be verified in M1).

## Consequences
- If FlaUI stops working on a future Windows App SDK, only the page-object layer is rewritten (to option 3 or 4).

## Verification
- All §12.5 journeys automated by M3/M4 gates, with a flake rate under 1% over 50 nightly runs.
