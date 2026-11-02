
### Honesty notes on the data set

- **Two passes.** A first complete pass with an earlier build was discarded and everything was re-run with the final build (archived in `spikes/S2/out/results/old-pass1/`, not used below). Reasons: the TTFT driver let the scheduler spin against a closed document between iterations (thousands of `status -10` errors, optimistic first-tile numbers), the thumbnail-then-tile pattern parsed a dense page twice (`NOCACHE` thumbnails), and the comp upload bench crashed on a cross-thread `Dispose`. The first pass also had one 41 ms frame in `x-4ecores-text2000-win2d`; it did not reproduce in the second pass.
- **Samples.** Engine bench: 24 to 60 tiles per cell (single-page fixtures `huge-page` and `real-usgs-topo` have 24, **below the plan's 30**; the CSVs hold every tile). Warm TTFT n = 40 (engine) and 20 to 30 (viewer; 20 for the GeoPDF). Cold TTFT n = 30 process launches (engine) and 12 (viewer). Cancel-latency n is only counted when the cancel arrived before the render ended, so n is 11 to 60 (see the table). Fling: one run is about 600 frames; every core configuration ran twice (passes f1 and f2).
- **Display.** One 100%-scale monitor; high-DPI is emulated by zoom. No second machine, no R1/R2/R3.
- **Content under test.** Fixture categories 4 (JBIG2) and 5 (JPX) are partly synthetic (see `fixtures.md`); category 2 real file is three-column.

## Results

### 1. Engine only (S2.1): PDFium page to BGRA tile, single thread

Tiles are 256 or 512 px, page already loaded, non-blank tiles only (tiles that are blank margins are in `tiles.csv` and make sparse text pages look up to 30 times faster at p50). Zoom 100% = 96 dpi at device scale 1.0. Flags 0. `engine-default` = default scheduling on DEV; the same matrix pinned to one E-core is summarised after the table.

{{engine_tiles}}

Pinned to one E-core (logical CPU 4, efficiency class 0) the same bench is between 7% faster and 25% slower, not 2 to 3 times slower:

{{ecore}}

**Page load is the expensive part, not the tile.** `FPDF_LoadPage` parses the content stream; it is not progressive and cannot be interrupted. Per sampled page (p50 / p95 over the sampled pages; the single-page fixtures have one sample):

{{engine_first}}

**Time to first tile inside the engine** (open document to the end of the first tile; "viewport complete" = the rest of a 1200 x 1000 px viewport rendered sequentially after it). Warm = same process, 3 warm-up opens, n = 40.

{{engine_ttft}}

**Process-cold** (fresh process: `FPDF_InitLibrary` then open, page, first tile; OS file cache warm; n = 30 launches per cell; this is *not* a reboot-cold number, **true cold is not measured**):

{{cold}}

**Progressive rendering** (`FPDF_RenderPageBitmap_Start/Continue`, the pause callback returns true when a cancel flag is set or the time slice is over). Heaviest 512 tile of the sampled page, n = 30 per row. "Pause overshoot" is how long `Continue` ran past its slice deadline, i.e. how coarse the pause points are. **Cancel latency** = time from setting the flag (from a second thread, at a random point inside the render) to the engine being free again.

{{prog}}

Reading it:

- **The cost of pausing is not measurable**: progressive and plain times differ by less than the run-to-run noise (in either direction, up to 25%), for slices of 0.5 to 4 ms (full numbers in `progressive.csv`).
- **Pause points are between page objects.** Text, huge pages and images pause within 0 to 2.5 ms. Dense vector pages pause within 4 to 7 ms. **Transparency groups are atomic**: overshoot of 12 to 18 ms.
- **Cancel latency on the heaviest page (the transparency fixture at 400%, 512 tile): p50 7.1 ms, p95 14.3 ms, max 18.4 ms.** So the "under 16 ms" target holds at p95 and misses at the maximum by 2.4 ms, on one fixture. At 100% zoom the same page is p95 12.4 ms, max 12.6 ms. Everything else is below 7 ms.
- Not covered: a request cannot be cancelled while `FPDF_LoadPage` runs (116 to 150 ms for the USGS GeoPDF page), nor while a JPX/JBIG2 image decode runs inside a single page object (JPX first tile 53 to 56 ms, the pause points never fired: continues = 1).

### 2. Transport (S2.2): C++ engine in the S3 AppContainer, shared-memory slots

Every number comes from QPC stamps taken in the broker and in the engine (QPC is system wide). One tile = request frame on the pipe, engine renders straight into a slot of the shared section, response frame, UI-side copy of the slot into a managed array (needed because `CanvasBitmap.CreateFromBytes` takes a `byte[]`), GPU upload. Fling runs at 100%, tile 512, pass f1, three requests in flight.

{{transport}}

- **Transport overhead per tile is about 0.1 ms** (request transit p50 0.02 to 0.03 ms, response transit p50 0.07 to 0.10 ms, slot copy 0.05 ms) and **does not depend on the sandbox**: `x-nosandbox-text2000-win2d` (engine as a normal process) has tile request-to-ready p50/p95 of 1.46/2.25 ms against 1.44/2.22 ms in the AppContainer (iGPU, `fling-text2000-win2d-t512`). Engine queueing (p95 about 1.2 ms for text, up to 150 ms behind a dense GeoPDF page) and page load dominate the tails, not the pipe.
- **Zero-copy:** the engine renders into the shared slot, so there is no copy on the engine side. The only copy is slot to managed array (about 0.03 to 0.05 ms for 1 MiB).
- **Engine request to GPU-ready p50 / p95 for a text tile: 1.1 to 1.6 ms / 1.7 to 2.5 ms** (512) and 0.6 to 0.9 ms / 1.1 to 1.6 ms (256). Heavy content: 19 / 31 ms (transparency), 19 / 35 ms (dense vector), because the engine renders them one at a time.
- Engine queue depth: with 3 requests in flight the queue wait for a *tile* grows to 19 ms p50 behind dense pages (`x-inflight6-vec60-comp`: 23 ms p50 / 63 ms p95; `x-inflight1-vec60-comp`: 7.0 / 23 ms). Frame times and blank frames were identical (see section 4), so depth beyond 1 to 2 only adds latency.

### 3. Upload cost and memory per tile (S2.3, S2.4)

Fresh process, 30 untimed warm-up tiles, then 200 tiles rendered by the real engine and uploaded one by one. "Upload call" is the CPU time of the upload (Win2D: `CanvasBitmap.CreateFromBytes`; Composition: temp bitmap + `CreateDrawingSurface` + `DrawImage` into the surface + brush + sprite). "Upload + GPU sync" adds a forced wait until the GPU has executed the work (a 1x1 draw plus read-back on the same device). GPU memory: `QueryVideoMemoryInfo` current usage before and after the 200 tiles; for Composition the sprites were attached to the tree and given 1.5 s so the surfaces are realised. "Nominal" = width x height x 4 of the tiles actually uploaded (edge tiles are smaller than 512 x 512, so the nominal average is 577 KB for "512" and 175 KB for "256"). "Freed" = usage still held 0.8 s after disposing all 200 tiles (+ `Trim` + GPU sync).

{{upload}}

- **512 px upload: call p50/p95 0.13/0.41 ms (Win2D, iGPU), 0.21/0.37 ms (Win2D, dGPU), 0.21/0.54 ms (Composition, iGPU), 0.18/0.44 ms (Composition, dGPU).** Including the forced GPU sync the p95 is 0.61 to 1.53 ms and the worst single tile 3.4 ms (Composition, iGPU). All below the 2 ms p95 threshold.
- **Win2D keeps a full CPU-side copy per tile**: process private bytes grow by 560 to 650 KB per 512 tile (about 0.95 to 1.15 times the pixel bytes) on both GPUs, on top of the GPU bytes. Composition grows private bytes by only 45 to 97 KB per tile. GPU bytes per tile: Win2D 548 to 629 KB (0.95 to 1.09x nominal); Composition 744 to 816 KB (1.29 to 1.41x nominal). Totals per 512 tile (GPU + private): Win2D about 1.9 to 2.2x the pixel bytes, Composition about 1.4 to 1.5x.
- **The Composition surfaces were not returned after `Dispose`**: 138 to 142 MB of GPU usage were still held 0.8 s after freeing 200 tiles (about 90% of what they had used), against 6 MB (iGPU) or none (dGPU) for Win2D. Observed longer-term effect: in a fling after a warm-up that had already freed its tiles, the 2,000-page Composition runs started from 468 to 478 MB of GPU usage (Win2D 135 to 236 MB). Whether the compositor trims later is **not measured** (I only looked at about 1 s). This matters for any plan that relies on memory-pressure eviction.
- The per-tile figures are the delta of a process-level counter over 200 tiles, so tens of KB of noise per tile are possible. The 256 rows are less reliable (Win2D dGPU: 60 KB/tile measured against 175 KB nominal, which looks like sub-allocation or late commit rather than compression). I did not investigate.

In-situ memory during the 10 s fling (peaks of the 250 ms sampler). With one 20-page document open and one screen of tiles (`x-idle-*`) the UI process has 117 to 131 MB private and a 156 MB (dGPU) to 192 to 204 MB (iGPU) working set:

{{memory}}

### 4. Fling (S2.5)

Per run: about 600 frames, all intervals in ms. Passes f1 and f2 are the same configuration run twice. `blank frames` is the model-based probe (see Definitions), `low-res-only frames` are frames where at least one visible cell showed only the thumbnail and none was blank. `sharp after fling end` is the time from the end of the scripted motion to every visible cell being sharp (0.01 to 0.04 ms means the tiles had already caught up at the end of the fling; the check runs once per frame).

{{core}}

Summary over **all 108 viewer runs with frame data (64,812 frames): 4 frames over 25 ms (the worst 31.0 ms), none over 33.4 ms, none over 50 ms.** Blank frames occurred only in the deliberately hostile runs below. For the 32 text-document runs (`text-2000` and the real 585-page book; 19,205 frames): zero blank frames, one frame over 25 ms, none over 50 ms; 32.5% of the frames are over 16.7 ms, which is jitter around the 16.67 ms tick (p95 is 16.8 ms in every run), not missed frames.

Presenter UI-thread cost per frame (p50 / p95 over the same runs): Win2D redraw handler **0.08 / 0.10 ms** (tile 512, about 7 visible tiles) and 0.09 / 0.11 ms (tile 256, about 20 visible tiles; one 14 ms outlier call); Composition update **0.010 / 0.035 ms** (512) and 0.021 / 0.045 ms (256; one 3.4 ms outlier). GC during the fling: 0 to 4 collections, up to 11 ms total pause.

Extra configurations (all on one GPU each, one run each; same columns plus GPU local peak MB and private MB). `hidpi-zNNN` = zoom NNN% (stand-in for R3's 4K at 200% scale); `x-4ecores` and `r1like` = process and engine pinned to 4 E-cores (logical CPUs 2 to 5); `x-nolowres` = no thumbnail fallback; `x-inflight K` = K requests in flight; `x-prefetch N` = N screens of tile prefetch; `x-noprogressive` = plain `FPDF_RenderPageBitmap`; `x-uiupload` = GPU uploads on the UI thread with a 3 ms per-frame budget; `x-v6000` = 6,000 px/s fling; `x-nosandbox` = engine not in the AppContainer.

{{extras}}

What the extra runs say:

- **The low-res fallback is what keeps the screen from going blank**: without it, 66 to 71 of 600 frames of the dense-vector stress document show a cell with nothing (up to 0.88 to 0.95M of 1.28M px blank); with it, 0, on both GPUs and both presenters.
- **It does not hold on a weak CPU with the heaviest content**: with the process and engine pinned to 4 E-cores, the dense-vector stress document (60 pages of 17 x 22 in, about 110k path segments each, 15,000 px/s) had **35 to 49 blank frames of 600 (6 to 8%)**, a worst frame with 1.27M px (the whole viewport) blank, on both presenters and both GPUs. Cause: thumbnails of such pages take 57 ms p50 / 110 ms p95 on the E-cores (about 45 ms p50 on the default scheduling; the page parse alone is 17 to 22 ms there) and the fling needs about 7 new pages per second. The transparency stress document (`r1like-z100-tr60`) and all text runs, including zoom 400% on 4 E-cores, stayed at zero blank frames.
- **Frame time was never the problem.** No run has a frame over 50 ms; the worst is 31 ms. The UI thread does about 0.01 to 0.1 ms of work per frame in this design; the cost shows up as latency and blank area, not as frame time.
- **Engine queue depth**: `x-inflight1` had the lowest tile latency on heavy pages (p50 7 ms against 19 ms at depth 3 and 23 ms at depth 6) with no change in frames or blanks.
- **Prefetch** (0, 1 or 2 screens, dense-vector stress): no change in frame times or blank frames; low-res-only frames 255 to 256 with no prefetch against 223 to 224 with 1 or 2 screens. Small effect, in the expected direction.
- **UI-thread uploads with a 3 ms budget** worked as well as the uploader thread for frame time, but tile request-to-ready became frame-paced (17 ms p50) because completions are drained once per frame.
- **Progressive off** changed nothing measurable in this fling (the blocking render would only matter where a cancel arrives mid-render, which the stress fling here rarely needs).

### 5. Zoom gesture (S2.5)

Text book (real 585-page document), 512 tiles, 24 transitions per run (100 to 200, 200 to 400, 400 to 200, 200 to 100, 6 cycles, 1 warm-up cycle discarded). The gesture scales the *existing* tiles for 12 frames (the first scaled frame is the very next tick), then holds for the 120 ms debounce, then the target-zoom tiles are requested; the displayed layout switches when every visible cell is sharp. All tile caches are dropped before every transition, so this is a full re-rasterisation.

{{zoom}}

- **Response to the gesture is the next tick**: the scale is applied in the same UI tick and the gesture frames run at the normal 16.67 ms interval (p95 16.8 ms, max 18 ms) on both presenters.
- **Sharp tiles after the gesture settles: about 150 ms in total, i.e. 120 ms debounce plus one UI tick.** The re-rasterised tiles of the whole viewport were ready within the first tick after the debounce expired (the check runs once per frame, so 16.7 ms is the resolution, not a measurement of the engine). §11 asks for sharp tiles within 300 ms after the gesture settles. **Pass on DEV; text pages only.** Zoom with dense or transparent pages was not measured and would take as long as the tile renders (6 to 25 ms per 512 tile plus a page parse).

### 6. Time to first tile, end to end (viewer)

Time zero is the start of the open RPC. ready / composed are defined above. "warm" = n 30 opens in a running app (20 for the GeoPDF), "cold process" = 12 fresh launches. `text-20` = the plan's typical 20-page text document, page index 5; `book` = the real 585-page text, page 100; `text2000` = 2,000 pages, page 0 (which includes enumerating all 2,000 page sizes, 27 ms); `topo` = the USGS GeoPDF (one dense page).

{{ttft}}

- **Typical text page, 100%: first sharp tile ready at p50 7.7 ms, composed p95 40 ms** (iGPU and dGPU alike), 19 ms ready (p50) and 48 ms composed (p95) in a cold process (fresh app and engine). The composed figure is two UI ticks (one to attach the tile, one for the compositor to consume it) and is dominated by tick quantisation, not by the engine (engine only: 2.2 to 3.7 ms warm, 4 to 6 ms process-cold).
- **2,000-page document: first page visible** at 60 ms (warm) and 70 to 75 ms (cold process), against §11's 1.0 s. 27 ms of the warm figure is `GetPageSizes` over all 2,000 pages (a flat page tree), which would grow with page count: layout must not need every page size up front.
- **4 E-cores + iGPU (R1-like CPU)**: text-20 first sharp tile composed p95 45 ms (both presenters). So the CPU is not the limit for a typical page either.
- **The dense GeoPDF page is the outlier**: first tile ready at 165 ms p50 (page parse 116 to 150 ms), composed p95 198 ms. Not a text page; no remedy exists without interruptible parsing, but showing the thumbnail first does not help because the thumbnail needs the same parse. The engine now keeps the parsed page for visible pages so the sharp tiles reuse it (the first pass parsed it twice: 292 ms).
- The numbers are **warm OS cache, DEV hardware, a synthetic or small text file for `text-20`**. Real cold start (disk read of a large file, first-run page-cache misses) is not included.

### 7. Compressed CPU tier (Q3)

Codecs that ship in .NET (no new dependency). Real rendered 512 x 512 BGRA tiles (page 150 of the real book at 100%, a synthetic text page, a 3-column Federal Register page, dense vector, GeoPDF, transparency, Type 3, JPX photo, JBIG2 scan), 40 repetitions, compress and decompress in memory, DEV CPU, one thread. `delta` = per-channel left delta before Deflate.

{{tier}}

To compare with rebuilding the tile from the engine (page loaded): text 0.3 to 1.8 ms, Type 3 2.4 ms, dense vector 5.5 ms, GeoPDF 2.7 ms (p95 10 ms), transparency 11 ms (p95 21 ms), JPX 0.3 ms after a one-time 50 ms decode. So:

- **Text-like tiles** (compress 12 to 100x with Deflate fastest): decompressing takes 0.3 to 0.7 ms, which is the same as re-rendering and then still needs the upload. **No benefit.**
- **Heavy tiles** (vector, transparency, GeoPDF: ratio 3 to 5x with fast Deflate, 1.7 to 2.3 ms to compress, 0.8 to 1.4 ms to decompress): a decompressed tile costs a fifth to a tenth of a re-render. **Worth it only for pages whose measured tile render time is above a few ms**, and the saving is 2 to 5x memory, not the 12 to 100x of text.
- An *uncompressed* CPU tier costs 1 MiB per tile (and Win2D already keeps one, see section 3).

## Surprises

1. **PDFium is not the bottleneck on DEV**: a 512 text tile renders in 0.3 to 0.6 ms (p50), the whole 2,000-page fling is 600 frames of 16.67 ms. What costs time is `FPDF_LoadPage` (content parse): 0.4 to 4 ms for text, 17 to 22 ms for a dense vector page, 116 to 150 ms for the USGS GeoPDF, none of it interruptible.
2. **The default adapter is the Intel iGPU.** The display is attached to it, so with no preference set both Win2D and the compositor use it. The NVIDIA GPU runs only with the per-app graphics preference, which works without admin (a per-user registry value, confirmed through DXGI per-process usage: Intel 0 MB, NVIDIA 150 to 770 MB). R1's integrated graphics is therefore the *default* case on hybrid laptops.
3. **The display reports 50 Hz but the UI ticks at 60 Hz.** `EnumDisplaySettings` and WMI say 3440 x 1440 at 50 Hz, `CompositionTarget.Rendering` has a median of 16.67 ms in 108 runs. `DwmGetCompositionTimingInfo` returned 0x88980090. Unresolved; frame results are therefore stated against the observed tick.
4. **PresentMon needs admin or the Performance Log Users group**; neither is available. There is no frame-presented measurement in this report.
5. **Composition presenter hazards** (all absent with Win2D): (a) `Dispose()` of a composition object must run on the thread that owns the compositor, otherwise `COMException 0x8001010E`; (b) `CanvasComposition.CreateDrawingSession(surface)` called from a background thread throws a transient `InvalidOperationException` (0x80131509): the upload bench crashed in 5 of 12 attempts before a retry loop was added; with it, every upload-bench process needed 1 to 2 retries in its first calls, and none of the other 65 Composition runs (flings, TTFT, zoom, idle) needed any, probably because their first uploads are paced by arrivals instead of back to back (not verified); (c) Win2D uses `Windows.Graphics.DirectX` enums while `CompositionGraphicsDevice.CreateDrawingSurface` takes `Microsoft.Graphics.DirectX` enums; (d) surfaces stay allocated after `Dispose` (section 3).
6. **Win2D `CanvasBitmap` memory is double-booked**: GPU bytes plus about the same again in process private bytes, on both GPUs (measured in a fresh process; I did not find where the second copy lives).
7. **`GetPageSizes` over 2,000 pages is 27 ms** (flat page tree) and scales with page count.
8. **The prebuilt PDFium decodes JBIG2 MMR (T.6) data incorrectly for pages 2,038 px wide or more**, but correctly at 1,701 px. The same libtiff-produced bitstream round-trips through Pillow's decoder. I did not isolate the cause (a PDFium MMR run-length table issue is a guess, nothing was checked in the PDFium source) so the JBIG2 fixture uses 200 dpi. A real 300 dpi JBIG2 scan could fail or garble in the shipped engine: needs a check against real scanner output.
9. **Tick quantisation hides engine speed.** TTFT composed 34 ms for a 2.7 ms engine TTFT.
10. **Pause points are coarse for transparency** (12 to 18 ms), so "cancel under one frame" cannot be promised for such pages.
11. Tooling traps: PowerShell `-File` does not bind comma lists to `[string[]]` parameters; `Get-Counter` failed once with 0x800007D6 mid-matrix; `python -I` heredocs through this shell collapse `\\` (cost me a fixture path and a CSV regex).

## Recommendation

All of this is **provisional: DEV hardware, display at 100% scale, no R1/R2/R3**, and the S2 conclusions must be re-run on R1 (4 cores, 8 GB, integrated graphics) before ADR-0006 hard-codes numbers.

**Tile size: 512 px as the default.**
- Frame time and blank-area results are the same for 256 and 512 (section 4, both presenters, both GPUs).
- 512 needs 2.8 times fewer tiles: about 7 visible and 390 cached against about 20 visible and 1,080 cached for the same bytes, so fewer requests, draw calls, sprites and upload calls (draw handler 0.08 ms vs 0.09 ms; Composition update 0.010 vs 0.021 ms).
- 256 has the lower per-tile latency (0.6 to 0.9 ms vs 1.1 to 1.6 ms p50) and smaller edge waste, and a transparency 512 tile costs 11 to 25 ms against 3.7 to 7 ms for 256, which is what bounds cancel latency (p95 14 ms at 400%).
- So: **512 default; fall back to 256 per page when the first tiles of that page take more than a few ms** (transparency, dense vector). The adaptive switch is a proposal, not measured.

**Cache policy.**
- Two tiers is not justified for text. GPU tier: LRU keyed on last frame the tile was visible or wanted, evict to 90% of budget, never evict visible tiles. Keep thumbnails (128 px wide, up to about 85 KB each) for the pages around the viewport; they cost nothing and are what prevents blank areas.
- **Budget the GPU tier against the process total, not the nominal bytes**: nominal x 2.0 for Win2D, x 1.5 for Composition (section 3). The 10 s fling at 100% touched about 390 entries of 512 (129 to 246 MB nominal) and evicted nothing at the 256 MB budget except in the dense-vector stress (about 140 evictions); the working set that matters is the viewport plus one screen plus thumbnails, about 30 to 60 MB nominal. A starting point to confirm on R1: **nominal budget = clamp(2% of physical RAM, 96 MB, 256 MB)**, which is 164 MB on an 8 GB machine. Not measured: eviction on minimise or memory pressure.
- **The UI process is already at the §11 ceiling before the tile cache grows**: 156 MB (dGPU) to 192 to 204 MB (iGPU) working set with one 20-page document and one screen of tiles, against the 200 MB budget. The budget for the tile cache and for Win2D's CPU copies has to come from somewhere else, or the 200 MB figure needs an ADR (the bulk is the Windows App SDK/XAML baseline, not measured here).
- **No CPU compressed tier at M1.** Revisit as "keep a Deflate-fastest copy only of tiles whose render took more than about 5 ms" (3 to 5x smaller, about 1 ms to bring back); no new dependency needed.

**Presenter for ADR-0006: Win2D `CanvasVirtualControl` (A) as primary, Composition (B) as the documented fallback.**
- Evidence for A: identical frame, blank and upload results on both GPUs; lower GPU memory per tile and memory comes back on dispose; none of B's four hazards (cross-thread dispose, transient `CreateDrawingSession` failure, enum split, retained surfaces); it is the surface S6 already proved on 2.5.1.
- Evidence for B: UI-thread cost per frame is 8 times lower (0.010 vs 0.08 ms), CPU private memory per tile is 6 to 14 times lower (45 to 97 KB vs 560 to 650 KB), upload call is similar. None of that mattered at the load measured here.
- **Not measured, and the reason B might still be needed**: compositor-driven scroll and zoom (here both presenters are driven from the UI thread each tick, so a UI-thread stall stalls both), a document-sized virtual surface (here `CanvasVirtualControl` is viewport-sized and redrawn every frame, which is not its intended use), and any UI thread load beyond the presenter. A decision to drop B should wait for R1.
- Either way the page surface needs its own UIA peer (S6, S7).

**Scheduling and prefetch policy** (all from section 4):
- Request order: thumbnails of visible pages, visible tiles nearest the viewport centre first, thumbnails ahead (max(1 screen, 0.6 s of velocity), up to 8 screens), tile prefetch of one screen in the scroll direction and half a screen behind, no tile prefetch above about 2.5 screens/s.
- Keep **1 to 2 requests in flight** against the single-threaded engine (depth 1 had the best latency, depth 6 the worst; no frame difference), and use a generation number plus `CancelBefore(gen)` so queued and in-flight obsolete work is dropped (the engine handled it on the I/O thread; 0 calls in the text documents, at most 2 per fling in the stress runs).
- Render with `FPDF_RenderPageBitmap_Start/Continue` and a pause callback that reads the cancel flag: no measurable cost.
- Keep the parsed page for visible pages across thumbnail and tile requests (saved 125 ms on the GeoPDF); do not cache prefetch pages.
- Lay out lazily: do not fetch every page size before the first page is shown.
- Uploads on a separate thread, not the UI thread: equal frame times, 17 ms lower request-to-ready.

## Go/No-go (against the plan's thresholds)

| Plan threshold | Measured on DEV | Verdict |
|---|---|---|
| Warm time to first tile, typical text page at 100%, **<= 150 ms p95** | engine only 2.2 to 3.7 ms p50 (4 to 6 ms in a fresh process); end to end, first sharp tile ready p95 8.6 to 9.5 ms and composed p95 39 to 41 ms (iGPU and dGPU), about 48 ms composed p95 in a fresh app; 44.6 ms with 4 E-cores. Heavy outlier: the dense USGS GeoPDF page 198 to 199 ms composed p95 (engine only 160 ms p50) | **Pass for typical text on DEV, provisional.** Fail for the GeoPDF outlier. "Pixels on screen" never observed (no PresentMon): add up to one refresh |
| Fling: **no frame over 50 ms** | none in 64,812 frames over 108 runs; worst 31.0 ms; 4 frames over 25 ms | **Pass on DEV, provisional** (UI thread idle apart from the presenter) |
| Fling: **no blank area** with the low-res fallback on | 0 blank frames in all text-document, 2,000-page, transparency-stress and zoom 200/400% runs, both GPUs, both presenters. **35 to 49 of 600 frames blank** in the dense-vector stress document pinned to 4 E-cores | **Pass for text and transparency, fail for the dense-vector stress on a 4-core CPU stand-in.** Provisional; R1 needed |
| GPU upload of a 512 tile **<= 2 ms p95** | call p95 0.37 to 0.54 ms; with a forced GPU sync p95 0.61 to 1.53 ms (max 3.4 ms, Composition iGPU) | **Pass, provisional** |
| Fail condition: neither surface meets the fling budget on R1 | R1 not available; both surfaces met it on DEV | **Not evaluated** |
| §11 zoom: sharp tiles within 300 ms after the gesture settles | about 150 ms (120 ms debounce plus one tick) for text | **Pass on DEV for text, provisional** |
| Extra: cancel latency under 16 ms on the heaviest page | p95 14.3 ms, **max 18.4 ms** (transparency, 400%); everything else under 7 ms | **Pass at p95, miss at max** |

Overall: **provisional GO for the architecture** (C++ engine in the AppContainer, shared-memory slots, tile scheduler with low-res fallback, Win2D surface) on DEV-class hardware. Per §19.3 no gate is marked passed: R1 (and R2, R3) numbers are required.

## Open questions for Opus

1. **R1/R2/R3 are the remaining evidence.** A 4-core E-core pin is not an R1: no memory, clock or iGPU class match. Can the R1 and R3 (4K at 150%/200%) runs be scheduled, and should the 50 Hz vs 60 Hz question be settled on those panels? The driver is `fling/run-matrix.ps1` and takes about 35 minutes per GPU.
2. **PresentMon or ETW.** Admin or Performance Log Users membership would let me capture real present times and dropped frames. Is that required for the §11 verdict, or is the tick-based figure enough? Without it "composed" is two ticks, not scan-out.
3. **The UI process ceiling (200 MB idle)**: the working set is 156 to 204 MB with one document before the tile cache grows. Is the budget to be revised, or should Win2D's per-tile CPU copy (about 1 MiB per tile at 512) count against the engine/UI split?
4. **Composition surfaces retained after `Dispose`.** I only watched about 1 s. Do you want a longer test (minutes, with `Trim`, minimise and memory-pressure events) before ADR-0006 keeps B as the fallback?
5. **The JBIG2 MMR width limit.** Should S1 check the PDFium source/build for the 2,038 px problem, and should the corpus (ADR-0010) include real 300 dpi JBIG2 scanner output? If the production engine really garbles those, it is an S4/M1 risk, not a rendering-throughput one.
6. **Dense-page parse is not interruptible** (116 to 150 ms for one GeoPDF page, 18 to 22 ms for dense vector, 12 to 18 ms atomic transparency groups). Acceptable for M1, or do you want a second engine process (thumbnail/prefetch worker) so a parse never blocks the visible tile?
7. **Win32k lockdown and the signature policy (S3b) were not applied to this engine**. The throughput numbers are for the AppContainer without the full mitigation set; the CET, dynamic-code and runtime signature policies are cheap by construction but I did not measure them here.
8. **Display scale.** This display ran at 100%; the 150% and 200% device-scale cases (R3) are only approximated by zoom 200% and 400%. Is that approximation acceptable for the ADR, or must it wait for the 4K panel?
