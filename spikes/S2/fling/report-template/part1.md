# S2: Rendering pipeline throughput

- Status: **Done on DEV only (x64, Core Ultra 9 275HX, 64 GB, RTX 5080 Laptop + Intel iGPU, 3440x1440 at 100% scale). Every verdict below is provisional: R1, R2 and R3 were not available, so the §11 budgets are not proven or disproven.**
- Date: 2026-10-28
- Author: Sonnet (implementation role). Reviewer: Opus.
- Target: ADR-0006 (rendering surface and tile strategy). Reuses the S3 transport and the S6 Win2D verdict.
- Code: `spikes/S2/` (frozen for evidence). Raw outputs: `spikes/S2/out/results/` (gitignored; regenerate with the scripts named in Method). Fixture sources and hashes: `spikes/S2/fixtures.md`.
- Repo state: branch `m0/spikes`, HEAD `3d26d99`; `spikes/S2/` is untracked, **nothing committed** (so "commit hash" means HEAD plus that working tree).

## Questions

1. Which tile size, 256 or 512 px, gives the best time to first tile and the best scroll smoothness at 100%, 200% and 400%? (plan Q1)
2. What does a tile upload cost with Win2D (`CanvasBitmap.CreateFromBytes` drawn in a `CanvasVirtualControl`) versus Composition (`CompositionDrawingSurface` + one `SpriteVisual` per tile)? (Q2)
3. How much GPU and CPU memory does one cached tile cost, measured? Is a compressed CPU tier worth it with codecs that need no new dependency? (Q3)
4. Can the pipeline hold 60 fps, no frame over 50 ms and no blank area during a 10 s fling? (Q4) On DEV-class hardware only; R1 was not available.
5. Extra questions the brief added: engine-only render cost per tile (p50/p95/max, n >= 30), progressive rendering cost and cancel latency, transport cost through the S3 AppContainer engine, behaviour on both GPUs, zoom-gesture response.

## Method

### Environment (DEV)

| Item | Value |
|---|---|
| CPU / RAM | Intel Core Ultra 9 275HX, 24 logical processors (8 P-cores at logical CPUs 0, 1, 10-13, 22, 23 with efficiency class 1; 16 E-cores, class 0; read with `bench.exe cpuinfo`), 63.4 GB |
| GPUs | Intel Graphics (driver 32.0.101.8331; DXGI: 128 MB dedicated, 48,573 MB shared) and NVIDIA GeForce RTX 5080 Laptop (32.0.16.1742, 16,003 MB). Also present and unused: DisplayLink, virtual display drivers |
| Display | Primary 3440x1440, app `XamlRoot.RasterizationScale` = **1.0**. The OS (`EnumDisplaySettings`, WMI) reports **50 Hz** for it, but `CompositionTarget.Rendering` ticks at a median of **16.67 ms (60.0 Hz)** in every run. I did not resolve this conflict (`DwmGetCompositionTimingInfo` failed with 0x88980090 for the window handle, so I have no DWM-side refresh figure). Frame metrics below use the observed tick, not the OS-reported refresh |
| OS | Windows 11 Home 26H2, build 26300 (`Environment.OSVersion` 10.0.26300.0). Developer Mode on, not admin. Power plan **Balanced** (I did not change system settings; the plan asks for "Best performance"). Plugged in |
| Other load | The machine is the user's desktop. OBS, NVIDIA Broadcast, browsers and others were running. System CPU before each run is logged in `driver.log` (typically 5 to 20%). Not an isolated lab machine |
| Toolchain | .NET SDK 10.0.300, runtime 10.0.8; Windows App SDK 2.5.1 (WinUI 2.3.9); Win2D 1.4.0; MSVC 19.44 (VS 2022 Build Tools); PDFium bblanchon `chromium/8125` (158.0.8125.0), x64, no V8/XFA |
| Build | Release. Native: `cl /std:c++20 /O2 /MT /guard:cf /sdl`, `/CETCOMPAT`. Managed: `dotnet build -c Release -p:Platform=x64`, framework-dependent JIT, `TieredPGO` off, unpackaged (`WindowsPackageType=None`), self-contained Windows App SDK not used (the installed 2.5.1 framework package is used, as in S6) |
| Sandbox | The engine ran in the S3 zero-capability AppContainer (profile `Quire.S2.Probe`, created and deleted by the app; engine directory ACL granted read+execute to that SID) with a kill-on-close job. The S3 mitigation set (CET, dynamic-code prohibition, signature lock at runtime, child-process policy) was **not** applied in S2 |
| PresentMon | **Not usable.** v2.6.0 (MIT; Intel-signed; SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`, in `spikes/_shared/tools/presentmon/`) refuses to start a trace session without admin or the "Performance Log Users" group: `error: failed to start trace session: access denied`. Fallback used: `CompositionTarget.Rendering` intervals on the UI thread. A composition-only app has no swap chain to observe either way |

### What was built (task mapping)

| Task | Where | Notes |
|---|---|---|
| S2.1 engine bench | `engine-bench/bench.cpp`, `build.cmd`, `run-engine-bench.ps1` | PDFium via `FPDF_LoadCustomDocument` on a file `HANDLE` (the S3 access pattern), tiles rendered with `FPDF_RenderPageBitmap` into a caller-owned BGRA buffer, flags 0 (no annotations, no LCD text). Modes `all`, `cold`, `dump`, `cpuinfo` |
| S2.2 transport | `transport/engine-cpp/engine.cpp`, `transport/Quire.S2.Transport/` | New C++ engine derived from the S3b one: same pipe framing, `RenderTile` straight into a 48 x 1 MiB shared-section slot ring, plus a generation number with `CancelBefore(gen)` (drops queued work and aborts the in-flight progressive render), a page cache (LRU of `FPDF_PAGE`), `GetPageSizes`. Broker side copied and trimmed from S3 `Sandbox.cs`, with an overlapped pipe so a reader thread and several writers can share it |
| S2.3 presenter A | `viewer/Quire.S2.Viewer/Presenters.cs` (`Win2DPresenter`) | `CanvasVirtualControl` sized to the viewport (not to the document), `CanvasBitmap.CreateFromBytes` per tile, redraw of the visible region each frame |
| S2.4 presenter B | same file (`CompPresenter`) | `CanvasComposition.CreateCompositionGraphicsDevice` + one `CompositionDrawingSurface` and `SpriteVisual` per tile; scroll and zoom gesture are transforms on one container visual |
| S2.5 fling | `Modes.cs`, `Modes2.cs`, `Flow.cs`, `fling/run-matrix.ps1`, `fling/run-all.ps1`, `fling/summarize.py` | Scripted fling, zoom gesture, TTFT, upload bench; the page surface is a `UserControl` hosting the presenter (S7 pattern) |
| Extras | `tier/TierBench`, `fixtures/*` | CPU-tier codec bench; fixture generators |

New dependencies: none beyond S3/S6 (CsWin32 0.3.x MIT, build-time only; Win2D 1.4.0; Windows App SDK 2.5.1). `System.IO.Compression` (Deflate, Brotli) is part of .NET. Python 3.14 with Pillow 12.2 (HPND) was used only to build two fixtures.

### Definitions

- **Zoom 100%** = 96 dpi (1.333 device px per PDF point) at device scale 1.0. The DEV display ran at 100% scale, so **R3's 4K at 150% and 200% scale is only approximated** by zoom 200% and 400% here (same tile counts and pixel work per page area, not the same monitor).
- **Tile** = square of N x N device pixels (edge tiles clipped). **Cold** in this report means a fresh process (fresh app and fresh engine, PDFium caches empty) with a **warm OS file cache**. True cold (reboot or standby flush) is **not measured**: no reboot, no RAMMap, not admin. **Warm** = the same document opened again in a running process, after 3 untimed warm-up opens.
- **Time to first tile (TTFT).** Engine only: from `FPDF_LoadCustomDocument` to the end of the first `FPDF_RenderPageBitmap`. End to end (viewer): from the start of the open RPC to (a) **ready** = the tile's GPU resource exists in the UI process (`ReadyAt`, exact), (b) **submitted** = start of the first UI tick at or after ready (the tick that attaches or draws it), (c) **composed** = start of the next tick (the compositor consumed that frame). Actual scan-out is up to one more refresh later. **"Pixels on screen" was never observed directly** (no PresentMon, no display capture).
- **Frame time** = interval between consecutive `CompositionTarget.Rendering` callbacks on the UI thread, QPC-timestamped. Counts over 16.7 ms are dominated by jitter around the 16.67 ms tick (27 to 38% of the frames in every run), so I also report counts over 25 ms (1.5 ticks), over 33.4 ms (2 ticks) and over 50 ms.
- **Blank-area probe.** Every frame, the visible page area is split into tile cells. A cell is *sharp* if its tile at the current zoom is in the GPU cache, *low-res only* if the page thumbnail (128 px wide, a thumbnail-scale render) covers it, and *blank* if neither exists. A frame with any blank cell is a **blank frame**. This is computed from the draw list the presenter was given, **not** from screen pixels; whether the compositor actually showed it that frame is unobserved. The page-white rectangle drawn behind a cell counts as blank (strict reading of §7.1 rule 3).
- **Fling.** Scroll offset is a pure function of time: 15,000 px/s for 1.5 s, then exponential decay with tau 2.5 s, 10 s total (about 50,000 to 59,000 px; 31 to 56 pages of Letter at 100%). Preceded by a 4 s warm-up fling elsewhere in the document (JIT, driver), then all tiles dropped and statistics reset, then the settled first screen, then the measured fling (about 600 frames). Zoom 100%, viewport 1280 x 1000 px, tile 512 unless stated.
- **GPU choice.** Windows per-app graphics preference (`HKCU\Software\Microsoft\DirectX\UserGpuPreferences\<S2Viewer.exe path>`: `GpuPreference=1;` power saving = Intel, `=2;` high performance = NVIDIA), written by the driver script and removed again in a `finally` block. The adapter that actually ran is taken from DXGI `QueryVideoMemoryInfo` per process: the adapter whose usage grew. The unset default is the Intel GPU (the display is attached to it), so **"default" and "igpu" are the same adapter**.
- **Memory.** GPU: per-process `IDXGIAdapter3::QueryVideoMemoryInfo` current usage, local and non-local segment, sampled every 250 ms. CPU: working set and `PrivateUsage` of the UI process. "Per tile" figures come from a fresh-process test that uploads 200 tiles and reads the counters before and after.
- **Scheduling policy under test** (viewer `Pipeline.cs`): planner order = thumbnails of visible pages, visible tiles (nearest the viewport centre first), thumbnails ahead (up to max(1 screen, 0.6 s of velocity), capped at 8 screens), tile prefetch of 1 screen in the scroll direction and 0.5 behind (none while scrolling faster than 2.5 screens/s). At most 3 requests in flight; when an in-flight request is no longer wanted, `CancelBefore(gen)` is sent (at most once per 6 ms) and the still-wanted tiles are re-requested. GPU cache: LRU by last-used frame, 256 MB nominal budget (evict to 90%), a separate uploader thread by default.
