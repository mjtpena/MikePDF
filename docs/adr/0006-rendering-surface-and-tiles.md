# ADR-0006: Rendering surface and tile strategy
- Status: Accepted (architecture). Numbers provisional until R1/R2/R3 runs
- Date: 2026-10-28
- Deciders: Opus (approver), Sonnet (S2, S6, S7 evidence)

## Context
§7.1 sets the pipeline rules: never show blank space, cancel obsolete work, keep a two-level LRU cache, scale on the GPU during a gesture and re-rasterise after it, keep overlays separate. The evidence:
- **[S6](../spikes/S6.md):** Win2D 1.4.0 works on WinAppSDK 2.5.1 on x64.
- **[S7](../spikes/S7.md):** the page surface must be a Quire-owned XAML `Control` that owns the UIA Text peer, with the presenter hidden from UIA.
- **[S2](../spikes/S2.md)** (DEV only, iGPU and dGPU, 100% scale), driven end to end through the C++ AppContainer engine and shared-memory slots:

| Area | Measurement |
|---|---|
| Tile render, 512 px text | 0.3–0.6 ms p50 |
| Tile render, dense vector | 5.5 ms p50 |
| Tile render, transparency | 11–25 ms p50 |
| Page parse (`FPDF_LoadPage`, not interruptible) | 0.4–4 ms text, 17–22 ms dense vector, 116–150 ms for a GeoPDF |
| First sharp tile, typical text page (end to end) | ready p95 ≈ 9 ms; composed p95 ≈ 40 ms (≈ 48 ms in a fresh app) |
| First page of a 2,000-page document | 60–75 ms |
| Fling (108 runs, 64,812 frames) | no frame over 50 ms, worst 31 ms |
| Blank frames, text and transparency documents | zero |
| Blank frames, dense-vector stress pinned to 4 E-cores | 35–49 of 600 |
| Upload of a 512 px tile | call p95 ≤ 0.54 ms; ≤ 1.53 ms with a forced GPU sync |
| Memory per 512 px tile, Win2D | 548–629 KB GPU **plus** 560–650 KB private CPU; freed on dispose |
| Memory per 512 px tile, Composition | 744–816 KB GPU, 45–97 KB private; **GPU memory not freed after `Dispose`** within about 1 s |

Composition also has threading hazards: dispose must happen on the compositor thread, and `CreateDrawingSession` fails transiently from background threads.

The UI process working set is already **156–204 MB** with one 20-page document. The §11 budget is ≤ 200 MB.

## Options considered
1. **Win2D `CanvasVirtualControl`** inside a Quire `PageSurface` control. **Chosen.**
2. **Composition visuals**, one `SpriteVisual` per tile, as the fallback. Lower UI-thread and CPU cost, but has the hazards above. Compositor-driven scroll (independent of the UI thread) is its untested advantage.
3. **A custom DXGI swap chain** in a `SwapChainPanel`. Most control, most code. Kept in reserve.

## Decision
1. **Surface.** `PageSurface : Control` owns the UIA peer (ADR via S7) and hosts the presenter behind an `ITilePresenter` interface. Win2D (option 1) is the default; Composition (option 2) is a second implementation kept compiling and tested in CI, so it can be switched by configuration. Annotations, selection, search hits and ink live on separate layers above the tiles (§7.1 rules 7–8).
2. **Tiles.** 512 px device-pixel tiles by default. If the first tiles of a page take more than 8 ms to render, that page switches to 256 px tiles (an untested proposal; it gets verified in M2).
3. **Never blank.** Thumbnails (128 px wide) for the viewport's pages and the pages ahead come from the **pool engine** (ADR-0004), so a slow page parse in a document's main engine never blocks the low-resolution fallback. Thumbnails are cached for every page within ±8 screens.
4. **Scheduling.**
   - Priority order: visible thumbnails, then visible tiles from the viewport centre outward, then thumbnails ahead (max(1 screen, 0.6 s of velocity), capped at 8 screens), then tile prefetch (1 screen ahead, half a screen behind; none above 2.5 screens/s).
   - **1–2 requests in flight** per engine.
   - Each request carries a generation number. `CancelBefore(gen)` goes over the I/O lane (ADR-0005).
   - Progressive rendering uses a pause callback that reads the cancel flag.
   - Keep the parsed page for visible pages only.
   - Lay pages out lazily, without fetching every page size before the first paint.
   - Upload tiles off the UI thread.
5. **Cache.** This deviates from §7.1 rule 5 (D-1 below).
   - A single GPU-tier LRU, with a nominal budget of **clamp(2% of physical RAM, 96 MB, 256 MB)**.
   - Accounting counts true cost (nominal size × 2.0 for Win2D, × 1.5 for Composition).
   - Visible tiles are never evicted. Evict to 90% of the budget.
   - On minimise or a memory-pressure signal, trim to the visible set.
6. **Zoom.** Scale the existing tiles on the GPU during the gesture, then re-rasterise 120 ms after it settles. S2 measured about 150 ms to sharp tiles against the 300 ms budget.

## Deviations
- **D-1: no compressed CPU cache tier in v1.0.** §7.1 rule 5 asks for one. S2 shows re-rendering a text tile (≤ 1 ms) is cheaper than decompressing one, and the tier adds memory to a UI process already at its budget. Revisit in M2 as "keep compressed copies only of tiles that took more than 5 ms to render", if R1 shows heavy pages re-rendering during scroll-back.
- **D-2 (flag for §11): the 200 MB idle UI-process budget is at risk** before any tile cache exists. M2 must break down the baseline (WinAppSDK/XAML vs Quire). Then either cut it, move the tile CPU copies out (Composition, or a swap-chain presenter), or revise the budget by ADR with evidence. Tracked as R-33.

## Consequences
- Two presenter implementations to keep compiling.
- The pool engine becomes part of the never-blank guarantee, not an optimisation.

## Verification
- On R1, R2, and R3 at 150%/200% scale: the S2 fling matrix (`spikes/S2/fling/run-matrix.ps1`, about 35 minutes per GPU), time to first tile, and memory. With PresentMon if the user grants Performance Log Users membership; otherwise the tick-based figures, labelled as such.
- If Win2D shows UI-thread stalls under real app load in M2, switch the default to Composition, using the abstraction above.
