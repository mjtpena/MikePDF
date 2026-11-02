# S9: annotation display strategy

- Status: **Done on DEV only (x64, offscreen). Numbers are provisional: R1, R2 and R3 were not available, and the real `CanvasVirtualControl` presenter was not used (see Unverified).**
- Date: 2026-11-02
- Author: Sonnet. Reviewer: Opus.
- Target: resolves OQ-AC-1 in [annotation-core](../features/annotation-core.md) 3.3 and OQ-INK-6 in [ink-pen](../features/ink-pen.md) 3.10. Input to ADR-0006 (presenter layers) and the annotation WPs of M4.
- Code: `spikes/S9/` (throwaway; `run.ps1` runs everything). Raw results: `spikes/S9/out/` (gitignored; the text results and ten small PNG pairs are force-added to the branch). Every number below comes from those files.
- Branch `m4/spikes-display-print`. `src/` is untouched.

## Questions

1. **Overlay cost.** What does it cost per frame to draw N annotations as Win2D vector overlays above the tiles (N = 10, 100, 1,000, 5,000 ink strokes; highlights with Multiply over the page) at 1x, 4x and 16x zoom?
2. **Hidden-flag mirror.** Can the engine hide an original annotation by setting its Hidden flag on the in-memory copy, so the tile no longer shows what the user moved or deleted? Is the re-render correct and cheap? Does the flag leak into a save?
3. **Rebase.** After a save, does reloading the saved file into the engine and dropping the overlay give the same pixels? How long does the reload take?
4. **Highlight appearance.** Does Multiply over the tile match how PDFium renders a highlight's appearance stream?

## Method

### Environment (DEV)

| Item | Value |
|---|---|
| CPU / RAM | Intel Core Ultra 9 275HX (24 cores), 64 GB |
| Adapters used | NVIDIA GeForce RTX 5080 Laptop (DXGI adapter 0, "dGPU"), Intel Graphics (adapter 1, "iGPU"), and the Microsoft Basic Render Driver (WARP, software). Win2D devices were created on each adapter explicitly (`D3D11CreateDevice` on the DXGI adapter, then `CanvasDevice.CreateFromDirect3D11Device`). No registry or per-app GPU preference was touched |
| OS / .NET | Windows 11 build 26300 (`10.0.26300.0`), .NET SDK 10.0.300, not admin |
| Libraries | Windows App SDK 2.5.1, Win2D 1.4.0, PDFium `chromium/8125` (158.0.8125.0), `pdfium.dll` x64 through P/Invoke, in process (no sandbox) |
| Build | `dotnet build -c Release`, JIT |

The iGPU is a current Intel part, not the "integrated graphics" low-end the §11 R1 profile means. **No R1 number exists.** The WARP rows are only a software lower bound.

### How each question was measured

- **No window is opened.** Everything draws into `CanvasRenderTarget`s. No mouse, keyboard or UI-test input was used.
- **Q1 frame cost.** Viewport 1600 x 900 device pixels. The page tiles are real PDFium renders (512 px tiles with text, colour blocks and an image-free vector page), uploaded as `CanvasBitmap`s. A frame is: create a drawing session on the target, `Clear`, `DrawImage` each visible tile, draw the overlay, dispose the session, then **flush the GPU with a 1 x 1 pixel read-back of the target** (`GetPixelBytes(0,0,1,1)`). The frame time is the whole interval, tile draws included. The empty-frame-plus-flush floor is reported below. The view origin changes every frame (pan of up to +-128 px), so no frame repeats. 20 warm-up frames, then up to 380 timed frames (a cell stops at 4 s, or 2.5 s on WARP, but never before 40 frames); p50, p95 and max are reported. 1x = 100% (1.333 px per point), 4x and 16x = 400% and 1600%.
- Ink: one annotation per stroke; each stroke is 48 points with a variable width (0.5 to 3 pt, pressure-like), turned into a **filled outline polygon with round caps (about 106 vertices)**, the same geometry the appearance stream carries (ink-pen spec: outlines, not `/BS /W`). Three colours. Positions are random over the page, so at 1x all N strokes are on screen and at 16x about 1% of them are.
- Strategies compared for ink: **A** rebuild each stroke's geometry every frame (visible strokes only); **B** cached `CanvasGeometry` per stroke with bounds culling; **C** the same without culling; **D** one retained `CanvasCommandList` replayed under a transform; **E** one grouped geometry per colour; **F** `CanvasCachedGeometry` (a realised tessellation, tolerance 0.25 device px) with culling; **G** a raster cache: the overlay is rasterised once into a viewport-sized layer and each frame is a blit. The one-time cost of G is the "settle" cost (about one frame of F).
- Highlights: N rectangles (150 x 12 pt average). **H0** plain source-over with alpha (the wrong look, a cost baseline); **H1** tiles into an intermediate render target, rectangles into a transparent one, then `BlendEffect(Multiply)` into the target; **H3** like H1 but the rectangles are a retained command list drawn through `Transform2DEffect`. A "mixed" row has N highlights plus N ink strokes.
- **Q2 hidden flag.** Annotations of 11 PDFs from PDFium's own test resources (third-party, 36 annotations: Highlight, Underline, Squiggly, StrikeOut, Square, Ink, Stamp, FreeText, Line, Polygon, Link, Popup, Widget; some with an `/AP`, some without). For each annotation: a fresh document, `FPDFAnnot_SetFlags(flags | Hidden)`, render the page (1.5x, `FPDF_ANNOT`, the same flag the engine uses); a second fresh document with `FPDFPage_RemoveAnnot` for the same index. The renders are compared by SHA-256 of the pixels. Then persistence across `ClosePage`/`LoadPage`, `FPDF_SaveAsCopy` (full and incremental) while hidden and after restoring, tile cost versus annotation count (generated pages with 0 to 5,000 ink annotations with appearance streams), and a **sprite** experiment for a moved foreign annotation (below).
- **Q3 rebase.** A generated base file plus an **incremental update** that appends annotations with generated appearance streams (what the writer host does, ADR-0008). For each kind, the "screen" is the PDFium tile of the *base* file plus the Win2D overlay of the same model; the "reference" is a fresh PDFium render of the saved file. Both are compared by SSIM over the annotation's bounding box (+3 px) at 1x, 2x and 4x, and over the whole page for all ten kinds at once at `/Rotate` 0, 90, 180 and 270. The overlay and the appearance stream are generated from **the same geometry code** (the "shared geometry" idea of annotation-core 3.3). Reopen cost: base files of 100, 1,000 and 5,000 pages, with 20 or 500 ink annotations appended to page 1, opened with `FPDF_LoadDocument` from disk, first page loaded and one 512 x 512 tile rendered.
- **Q4 highlight appearance.** 62 samples: 5 backgrounds (white paper under black text, a salmon block, a light green block, mid grey under white text, dark grey under black text) x 5 colours x opacity 1.0 and 0.5 at 1x, plus a subset at 4x. Four renderings are compared to the reference (PDFium rendering our appearance stream: `/BM /Multiply`, `/CA`, `/ca`): Win2D Multiply, Win2D Multiply with PDFium's rectangle pixel snapping (below), a source-over fallback at 0.4 x opacity (the OQ-INK-6 fallback), and PDFium's *own generated* appearance for the same annotation without an `/AP`.
- **SSIM.** Wang et al. 2004, 11 x 11 Gaussian window, sigma 1.5, K1 0.01, K2 0.03, L 255, on BT.601 luma, mean over the valid region. Written for the spike (`Imaging.cs`), not cross-checked against a second implementation (UNVERIFIED as an implementation; identical images give exactly 1.0000 and the "no overlay" controls give plausible low values).

## Results

### 1. Overlay cost

Frame p50 / p95 in ms: tiles plus overlay, GPU flushed. All numbers are for **ink strokes**, N on the page. Floors (tiles only): dGPU 0.07 to 0.17 ms; iGPU 0.8 to 1.1 ms; WARP 0.5 ms. An empty frame plus flush: dGPU 0.05 to 0.13 ms, iGPU 0.4 to 0.6 ms, WARP 0.3 ms.

**iGPU (Intel Graphics)**

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| A rebuild every frame | 1,000 | 46.5 / 56.6 | 12.8 / 17.8 | 2.6 / 3.6 |
| B cached geometry + cull | 100 | 4.4 / 5.7 | 2.3 / 3.1 | 1.1 / 1.6 |
| B cached geometry + cull | 1,000 | 26.9 / 36.4 | 9.4 / 12.5 | 2.4 / 3.2 |
| B cached geometry + cull | 5,000 | 131 / 157 | 32.5 / 37.0 | 4.8 / 6.0 |
| F cached realisation + cull | 100 | 1.3 / 1.7 | 1.4 / 1.8 | 0.8 / 1.0 |
| F cached realisation + cull | 1,000 | 7.3 / 9.8 | 2.8 / 3.4 | 1.2 / 1.6 |
| F cached realisation + cull | 5,000 | 28.5 / 33.8 | 8.0 / 9.5 | 2.3 / 3.1 |
| G raster cache (blit) | 100 | 1.0 / 1.1 | 1.4 / 2.0 | 0.9 / 1.2 |
| G raster cache (blit) | 5,000 | 1.2 / 1.7 | 1.2 / 1.8 | 1.3 / 1.6 |

**dGPU (RTX 5080 Laptop)**

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| A rebuild every frame | 5,000 | 181 / 202 | 38.0 / 44.5 | 5.6 / 9.8 |
| B cached geometry + cull | 1,000 | 20.5 / 24.9 | 7.1 / 11.6 | 1.5 / 1.9 |
| B cached geometry + cull | 5,000 | 115 / 125 | 30.2 / 34.9 | 4.3 / 5.5 |
| D command list | 5,000 | 110 / 117 | 38.4 / 53.4 | 13.1 / 18.0 |
| E grouped per colour | 5,000 | **436 / 522** | 51.1 / 69.9 | 6.5 / 10.7 |
| F cached realisation + cull | 1,000 | 5.0 / 6.7 | 1.3 / 2.1 | 0.4 / 0.7 |
| F cached realisation + cull | 5,000 | 26.7 / 46.2 | 6.2 / 8.6 | 1.1 / 1.5 |
| G raster cache (blit) | 5,000 | 0.15 / 0.53 | 0.13 / 0.40 | 0.13 / 0.47 |

Full tables for every strategy, N, zoom and device (including WARP and the mixed rows) are in `spikes/S9/out/s9-overlay-pivots.md`; raw rows in `s9-overlay.csv`.

What the ink numbers say:

- **Drawing strokes as geometry each frame is CPU-bound and does not scale.** It is almost all CPU submit time (for B at N = 1,000 and 1x on the dGPU, 17.9 ms of the 19.6 ms was the CPU side in an early run; the full run has 20.5 ms p50), i.e. Direct2D tessellating about 106-vertex outlines on one thread. At 1x, 1,000 strokes cost 20 to 27 ms (B) on the two GPUs; 5,000 cost 115 to 131 ms. Culling helps only when the view shows a small part of the page (16x). Grouping into one geometry (E) is **worse** at 5,000 (436 ms).
- **A realised (cached) tessellation (F) is 4 to 5 times cheaper**: 1,000 strokes in 5 to 7 ms at 1x, 5,000 in 27 to 29 ms. Building the realisations costs about 36 to 41 ms for 1,000 strokes (one-time per zoom level, because the tolerance depends on scale).
- **A raster cache (G) makes the overlay free per frame**: 0.1 to 0.2 ms on the dGPU and 1.0 to 1.3 ms on the iGPU (that is the floor) for any N tested. The cost moves to a re-rasterise when the zoom settles (about one frame of F at that zoom, for example 7 ms at N = 1,000 and 1x) and when an annotation changes.
- Pinning the process to four E-cores (affinity mask 0xF00, N = 10 and 1,000 only) changed little: B at N = 1,000 and 1x was 22.1 ms (dGPU) and 22.8 ms (iGPU), F 4.6 and 6.1 ms, G 0.13 and 1.0 ms. The work is single-threaded, so the **per-core speed** matters, not the core count. **R1 single-thread speed was not measured; expect these CPU-bound numbers to be worse there.**
- WARP (software) was *faster* than the GPUs for strategies A to E (for B at N = 5,000 and 1x: 33 ms against 115 to 131 ms) and much *slower* for F (185 ms). The rasteriser differences do not carry to hardware; they are listed in the CSV and not interpreted further.

**Highlights with Multiply** (frame p50 / p95 in ms):

| device | strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|---|
| dGPU | H0 source-over alpha (baseline, wrong look) | 1,000 | 1.2 / 1.6 | 1.2 / 1.7 | 2.2 / 2.8 |
| dGPU | H1 Multiply, two render targets | 1,000 | 1.4 / 1.8 | 1.4 / 1.9 | 2.4 / 3.5 |
| dGPU | H3 Multiply, retained list | 5,000 | 3.7 / 4.4 | 5.2 / 7.9 | 7.9 / 11.5 |
| iGPU | H1 Multiply, two render targets | 1,000 | 2.9 / 3.8 | 3.0 / 4.0 | 3.3 / 4.4 |
| iGPU | H3 Multiply, retained list | 5,000 | 6.2 / 8.2 | 6.3 / 7.6 | 7.6 / 9.2 |
| WARP | H3 Multiply, retained list | 5,000 | 12.5 / 15.2 | 8.1 / 9.3 | 6.2 / 7.7 |

- **Multiply is cheap once the tiles are composed into an intermediate target.** The fixed cost of the extra viewport-sized passes is 0.2 ms (dGPU), 0.6 ms (iGPU), 3 ms (WARP); the rectangles cost about 1 to 2 microseconds each. Even 5,000 highlight rectangles stay under 8 ms p50 on the iGPU.
- **It needs a change in how the presenter composes**: tiles must be drawn into a target (or command list) that the `BlendEffect` can read, not straight into the `CanvasVirtualControl` region as `Win2DTilePresenter` does now. OQ-INK-6 is answered: Multiply over the tile surface is possible, in the same drawing session, with `BlendEffect`; `CanvasBlend` has no Multiply value.
- A frame with 1,000 highlights and 1,000 ink strokes (B) costs 25 ms (dGPU, 1x), dominated by the ink.
- Document scale: building 50,000 stroke geometries took 1.4 to 2.0 s and added 135 to 181 MB of private bytes (iGPU and dGPU). **Realise geometry lazily for the pages near the viewport; do not hold it for a whole document.**

### 2. Hidden-flag mirror

**Correctness: 36 of 36 identical.** Setting `/F` Hidden on the in-memory annotation and re-rendering gives **exactly** the same pixels as deleting the annotation (SHA-256 equal, SSIM 1.00000, mean abs difference 0.0000), for every annotation in the set. 17 of the 36 change the page pixels when hidden (Highlight, Underline, Squiggly, StrikeOut, Square, Ink with an appearance, Stamp, FreeText). The other 19 draw nothing in either case: Link, Popup and Widget (not drawn by the `FPDF_ANNOT` pass), and Ink, Line and Polygon annotations that have no `/AP` (PDFium cannot generate one for those kinds, as S4 found). Full list: `spikes/S9/out/s9-hidden-flag.txt`.

**Persistence.** The flag is stored in the document's annotation dictionary, so it survives `FPDF_ClosePage` and `FPDF_LoadPage`. It lives until it is reset or the document is closed.

**It leaks into a save (AC-14 is a real risk).** With annotation #0 hidden (`/F` 4 became 6), `FPDF_SaveAsCopy` wrote `/F 6` in a **full** rewrite and in an **incremental** save. After restoring `/F` to 4, both saves wrote `/F 4` again. An incremental save still rewrites the touched object, so it is not byte-identical to the source. The invariant in annotation-core 4a (restore before any save) is necessary. It matters only for engine-side saves: MikePDF's save pipeline builds files in the writer host (ADR-0008) and does not use `FPDF_SaveAsCopy`, but page operations (S11) might.

**Cost.** Tile render (512 x 512, `FPDF_ANNOT`), p50 / p95 ms, page with N ink annotations that have appearance streams:

| N on the page | file size | tile at 1.33x | same, annotations flag off | tile at 5.33x |
|---|---|---|---|---|
| 0 | 5.9 KB | 3.4 / 4.9 | 3.5 | 6.6 / 8.3 |
| 10 | 40 KB | 4.6 / 7.6 | 4.1 | 6.9 / 14.2 |
| 100 | 345 KB | 9.0 / 17.0 | 3.3 | 8.8 / 9.9 |
| 1,000 | 3.4 MB | 42.5 / 51.5 | 2.2 | 34.6 / 39.3 |
| 5,000 | 17 MB | 172.9 / 193.2 | 1.9 | 145.3 / 173.9 |

- **PDFium redraws every annotation appearance for every tile**, wherever the tile is (a clip does not skip them): about 34 microseconds per annotation. This is **independent of the display strategy** and it exists today for any file that already carries annotations. S2's tile numbers (0.3 to 25 ms) were not, as far as its report shows, measured on annotated pages. A page with 1,000 annotations makes a tile cost about 12 times more. The first render after load is slower still (513 ms at 5,000, against 172 ms steady).
- Changing a flag is cheap: the call sequence `FPDFPage_GetAnnot` + `GetFlags` + `SetFlags` + `CloseAnnot` took (p50) 0.7 microseconds at 10 annotations, 1.2 at 100, 2.9 at 1,000 and 4.2 at 5,000 (column "GetAnnot+SetFlags+Close us" in `s9-hidden-flag.txt`). Hiding does **not** make later tiles cheaper (the annotation list is still built and the hidden entries skipped): a follow-up with 31 renders per step (`s9-hidden-cost.txt`) gave, at N = 5,000: untouched 177 ms, after hiding one annotation 185 ms, after unhiding 190 ms, after `ClosePage`+`LoadPage` 200 ms (+5 to +13%, p95 up to 239 ms). An earlier single pass reported 296 to 324 ms for "after hiding"; that was **not reproduced** and is treated as noise from a loaded machine.
- **Sprite of one annotation (needed to draw a moved foreign annotation).** With the page reloaded, every other annotation hidden, and all page objects removed in memory (`FPDFPage_RemoveObject`, no `GenerateContent`), `FPDF_RenderPageBitmap` onto a transparent bitmap gives the annotation alone. It took 3 to 15 ms per annotation (median about 5 ms). Compositing that sprite over the hidden-annotation render reproduced the original render with **SSIM 1.0000** for Ink, Square and Stamp annotations when blended source-over, and for Highlights **only when blended with Multiply** (source-over gave 0.998 over the whole page; the visible problem is local: opaque yellow over the text). So the broker must know each sprite's blend mode, and the engine cannot report it through the public API. The page rendered identically afterwards for all 10 annotations tried (the removal is not persistent). One Stamp reached only 0.996 with Multiply, so the choice matters in both directions.

### 3. Rebase

**Overlay versus a fresh PDFium render of the saved file** (ROI SSIM around each annotation; whole-page SSIM in brackets). All ten kinds at once on one page, 100%:

| kind | 0 deg | 90 deg | 180 deg | 270 deg |
|---|---|---|---|---|
| Highlight yellow, CA 1 | 0.9906 | 0.9899 | 0.9906 | 0.9894 |
| Highlight green, CA 0.5, over a colour block | 0.9990 | 0.9987 | 0.9990 | 0.9987 |
| Underline | 0.9995 | 0.9996 | 0.9995 | 0.9995 |
| StrikeOut | 0.9997 | 0.9994 | 0.9998 | 0.9998 |
| Square | 0.9999 | 0.9999 | 0.9999 | 0.9999 |
| Circle | 0.9993 | 0.9992 | 0.9993 | 0.9993 |
| Line | 0.9995 | 0.9995 | 0.9995 | 0.9995 |
| Ink, variable width, 3 strokes | 0.9998 | 0.9998 | 0.9998 | 0.9998 |
| Ink highlighter, Multiply, CA 0.5 | 0.9998 | 0.9995 | 0.9998 | 0.9994 |
| **FreeText 12 pt (Helvetica in the file, Arial in the overlay)** | **0.9558** | **0.9306** | **0.9550** | **0.9552** |
| whole page, all kinds | 0.9988 | 0.9984 | 0.9988 | 0.9988 |

- Control: with **no overlay at all** the same boxes score 0.66 to 0.99 (for example 0.94 for the yellow highlight, 0.66 for the text box), so the SSIM figures above are a real measurement of agreement, not an artefact of small boxes.
- **At 2x and 4x** the figures hold: every shape and ink kind stays at 0.9991 to 0.9999. Two things move. The yellow highlight drops to 0.9842 (2x) and 0.9783 (4x) and the green one to 0.9845 at 2x; FreeText rises to 0.9961 and 0.9943 (larger text, less hinting difference).
- **Cause of the highlight gap, and the fix.** PDFium fills an axis-aligned, fill-only rectangle **on the pixel grid without anti-aliasing** (`CFX_RenderDevice::DrawPathWithBlend`: outer integer rectangle, then trimmed to the ceiling of the width and height). Direct2D anti-aliases the same rectangle, so the edge rows differ by up to 60% coverage (max channel difference 159 to 170). Reproducing PDFium's rule in the overlay (device-space rectangle, same rounding) makes the result **exact**: ROI SSIM **1.0000**, max channel difference **1 of 255**, for both highlights at 1x, 2x and 4x and at all four rotations (24 of 24 cells). This belongs in the shared geometry.
- **Text kinds are the exception.** FreeText cannot meet an SSIM threshold of 0.98 at 100% with a system font in the overlay and the standard-14 Helvetica in the file. It reaches 0.994 to 0.996 at 200% and above. Fixing it needs the overlay to draw the same font file the appearance embeds, and even then different rasterisers will differ at small sizes (not tested).
- Not covered by the SSIM sweep: Squiggly, Polygon, PolyLine, Stamp artwork, Note icons, arrowheads.
- **Swap without a gap or a double draw.** The overlay and the new tile must be exchanged in the same frame. Leaving the overlay on top of the rebased tile (which already contains the annotation) darkens Multiply content: the yellow highlight 0.9974 (mean abs 3.6, because yellow x yellow is yellow), the half-opaque green over salmon 0.9778 (mean abs 23.8), the half-opaque pink highlighter ink 0.9528 (mean abs 22.5). Non-Multiply kinds would only be drawn twice at the same place and look unchanged.

**Cost of reopening** (disk file, `FPDF_LoadDocument`, first page, one 512 px tile at 1.33x; p50 of 8 runs; synthetic text pages):

| pages | annotations appended to page 1 | update build ms | open ms | first tile ms | reopen to first tile (p50 / max) |
|---|---|---|---|---|---|
| 100 | 20 | 1.5 | 0.7 | 7.5 | 8.0 / 9.0 |
| 100 | 500 | 40.3 | 7.9 | 69.5 | 73.9 / 81.3 |
| 1,000 | 20 | 1.8 | 0.9 | 7.3 | 8.4 / 8.9 |
| 1,000 | 500 | 47.4 | 10.3 | 77.8 | 87.9 / 112.1 |
| 5,000 | 20 | 2.5 | 4.0 | 7.4 | 11.5 / 12.9 |
| 5,000 | 500 | 40.6 | 12.7 | 82.4 | 94.5 / 101.1 |

- Reopening is cheap on these files (8 to 12 ms with 20 annotations). With 500 annotations it is about 70 to 95 ms, almost all of it the O(N) annotation drawing of the first tile (section 2), not the open. The "update build" is the generator for 500 ink appearance streams (about 40 to 47 ms) and **excludes** starting the writer host process (S3: about 55 ms for a .NET stub).
- **These files are synthetic and tiny (0.04 to 1.7 MB).** S2 measured 60 to 75 ms for the first page of a real 2,000-page document through the engine. A snapshot reload per edit (option 3) therefore costs, on real files, roughly 100 to 200 ms plus a writer-host start, **not measured here**.

### 4. Highlight appearance: Multiply over the tile versus PDFium

62 samples (5 backgrounds, 5 colours, opacity 1.0 and 0.5, 1x and a 4x subset). ROI SSIM against PDFium rendering our appearance stream:

| rendering | min | mean |
|---|---|---|
| Win2D `BlendEffect` Multiply | 0.9705 | 0.9945 |
| **Win2D Multiply + PDFium rectangle pixel snapping** | **0.9996** | **1.0000** (max channel difference over all 62 samples: **1**) |
| source-over alpha at 0.4 x opacity (the OQ-INK-6 fallback) | 0.3744 | 0.8452 |
| PDFium's own generated appearance (no `/AP`) versus ours | 1.0000 | 1.0000 |
| no overlay (control) | 0.6969 | 0.9080 |

- **Multiply over the tile matches PDFium.** The colour maths agree: for a pixel of the modified colour `(1-CA) x B + CA x B x C`, the reference, the overlay and the formula gave identical values in the logged samples (for example 115/92/126 on mid grey with pink at 50%). The only difference is the rectangle edge treatment above.
- **The fallback is not acceptable.** Source-over at a lower opacity gives SSIM 0.37 at worst: on dark backgrounds it hides the text (the reason to keep Multiply).
- PDFium's generated highlight appearance (when a file has `/QuadPoints` but no `/AP`) is pixel-equal to ours in all 62 samples, so for Highlight, MikePDF's and PDFium's own looks coincide.
- Overlapping highlighter ink strokes in the file: each stroke is a separate filled path in the appearance, so overlaps multiply twice; the overlay does the same (each stroke is drawn separately). Consistent, and a known fidelity question for other viewers, not measured here.

## Surprises

1. **Per-frame geometry drawing is far slower than expected**: about 20 microseconds per stroke at 1x on a GPU, CPU-bound in Direct2D, and grouping geometries made it worse. Retained realisations or a raster cache are not optional above about 100 strokes (1,000 strokes = 20 to 27 ms).
2. **Hiding works exactly, but PDFium's per-tile annotation cost is the bigger problem**: 34 microseconds per annotation per tile, applied to every tile of a page, whatever is hidden. Heavily annotated foreign files will cost tiles 10 times more than S2 assumed.
3. **`FPDF_SaveAsCopy` really writes the Hidden flag** (full and incremental). The invariant is needed, or never save from the display document.
4. **PDFium snaps fill-only rectangles to the pixel grid.** One line of geometry removes the highlight drift entirely.
5. Win2D's `CanvasPathBuilder` defaults to **alternate** fill; PDF `f` is non-winding-zero. The spike sets `Winding` explicitly; a shared-geometry library must too.
6. `CanvasBlend` has no Multiply; the blend needs `BlendEffect`.
7. The single-annotation sprite works with page-object removal and costs 3 to 15 ms, but the blend mode of the original is invisible to the broker.

## Recommendation

**Adopt the hybrid (option 4), as annotation-core 3.3 hypothesised, with these specifics.**

1. **Pending owned annotations are drawn by the overlay, from a retained representation, never rebuilt per frame.**
   - Per annotation: a `CanvasGeometry` built once and a `CanvasCachedGeometry` realisation per zoom level (built when the zoom settles, culled by bounds). Measured: 1,000 strokes at 1x cost 5 to 7 ms; 100 strokes about 1 ms.
   - Above about 300 pending strokes in view (an estimate: where F reaches 2 to 3 ms on the iGPU), draw the overlay from a **raster cache layer** (G): re-rasterised on zoom settle and after an edit (about 7 to 30 ms, off the scroll path), blitted per frame at about 0.1 to 1.3 ms.
   - The live pen stroke stays a separate small layer (ink-pen 3.x); nothing here changes that.
2. **Highlights and the highlighter pen use `BlendEffect(Multiply)` over the composed tiles**, with PDFium's rectangle snapping in the shared geometry. Cost: +0.2 to 0.6 ms fixed, rectangles about 1 to 2 microseconds each (5,000 under 8 ms on the iGPU). The presenter must compose tiles into an intermediate target when a Multiply item is on screen (a change to `Win2DTilePresenter` for the annotation WP).
3. **Loaded annotations that the user deletes, restyles or moves are hidden in the engine** with `SetAnnotationHidden` (exact: 36 of 36 identical to removal). Keep invariant 4a, and add a test that the flag never reaches a saved file; the writer host must never be fed from an engine save.
4. **A moved foreign annotation is drawn from an engine-made sprite** (`RenderAnnotationSprite`, 3 to 15 ms, cached per page and zoom). Needs a blend-mode rule: the spike shows it matters (Multiply highlights, 0.998 versus 1.0). Until an engine-side rule exists, restrict moving foreign Highlight and Ink-highlighter annotations (they are text-anchored or Multiply) and move only source-over kinds.
5. **Rebase after a successful save**, as the spec says, with an atomic swap of tiles and overlay in one frame (the double draw is measurable). Reopen cost is 8 to 12 ms on synthetic files with 20 annotations.
6. Put PDFium's rectangle rule, the winding rule and the glyph-font rule in `MikePDF.Markup`'s tests (AC-13), not only the point lists.

### Conditions under which the strategy changes

| Condition | Switch to |
|---|---|
| Reopen plus first tile after a save stays under about 50 ms p95 on R1 for the corpus (measure on real files; the synthetic number is 8 to 12 ms, and 70 to 95 ms when 500 annotations are on the page) | **Option 3 for committed edits** (reload after each commit), with the overlay only for the live gesture. The hybrid's overlay then shrinks to one gesture and the "pending" model disappears. Not recommended before real-file data: a writer-host start is about 55 ms by itself |
| A pending page has more than about 300 strokes in view, or overlay p95 exceeds 4 ms on R1 | Raster-cache layer (G) instead of per-frame drawing |
| Loaded annotations on a page exceed about 100 (tile cost 9 ms) to 500 (about 20 ms) | Tell the tile scheduler the page is "annotation heavy" (longer deadlines, 256 px tiles do not help: the cost is per annotation, not per pixel), and consider an engine annotation tile layer; **not** a fix for blend modes (Multiply needs the backdrop) |
| Option 2 (mirror in PDFium) | Not recommended: it cannot create Line, Polygon or PolyLine (S4), its appearance differs from ours, and `FPDF_SaveAsCopy` would write our edits through PDFium |
| Forms | Widgets are not drawn by the `FPDF_ANNOT` pass, so the Hidden flag on a widget is a separate question (not tested): see Open questions |

## Go / No-go

- **Go** for the hybrid as the M4 working strategy, with the retained overlay, Multiply through `BlendEffect`, PDFium rectangle snapping and engine-side hiding.
- **Go** for AC-12's SSIM >= 0.98 for shapes, ink, underline, strike and highlights (after snapping). **No-go as written for text kinds** (FreeText, and any note text): propose AC-12 as 0.98 for non-text kinds and a separate, lower threshold (0.93 at 100% measured, 0.99 from 200%) until the font question is settled.
- **No-go** on the source-over fallback for highlights.
- **Open (not decided here):** option 3 as the default (needs real-file reopen data).

## Unverified

- **R1 (low-end integrated graphics, 4 cores), R2 (ARM64) and R3, 4K and 150%/200% scaling**: not run. The iGPU is a current Intel part; the CPU-bound overlay cost on a slower core is unmeasured (the four-E-core pin did not change the numbers much but is not an R1).
- **The real presenter.** Frames were drawn into offscreen `CanvasRenderTarget`s. The `CanvasVirtualControl` path, the swap-chain present and the compositor are not included; a frame in the app can only be slower.
- **Re-rasterising the raster-cache layer on a background thread** was not tried; the settle cost is a UI-thread draw in these numbers.
- **Real files**: reopen cost on large or scanned documents, a 100 MB file, a file with 100,000 annotations; only synthetic 100 to 5,000-page text files were used. The 50,000-annotation case measured geometry build only.
- **Forms**: whether `FPDF_FFLDraw` honours the Hidden flag on a widget; whether hiding a widget with a value changes anything. Not tested (no form-fill environment in the spike).
- **Other viewers** (Acrobat Reader, Edge, pdf.js): every SSIM here is against PDFium. The appearance streams were generated for the spike, not by `MikePDF.PdfWriter`.
- **Kinds not in the SSIM sweep**: Squiggly, Polygon, PolyLine, Stamp artwork, Note icons, line endings. **Rounded or custom fonts** in FreeText.
- **My SSIM implementation** was not cross-checked against a reference implementation.
- **The blend-mode rule for sprites** (how the engine or broker knows an annotation multiplies): no rule was designed or tested.
- **Popups**: a Popup annotation hidden or not changed no pixels in the fixtures; its own visibility is not examined.

## Open questions for Opus

1. OQ-AC-1: confirm the hybrid with the retained-overlay refinement above, and decide whether the per-edit reload (option 3) is worth measuring on the real corpus before M4 starts.
2. AC-12: accept a separate, lower threshold for text kinds, or require the overlay to draw text with the same font file as the appearance (cost: shared font metrics in `MikePDF.Markup`).
3. How is a sprite's blend mode known? Options: the engine renders the sprite over white and over black and infers it (untested); the writer host exposes the page's ExtGState for the annotation; or foreign annotations that multiply are not movable.
4. The 34 microseconds per annotation per tile (section 2) is a viewing-performance risk independent of M4: should the perf harness (W1-perf) add a heavily-annotated page, and should `RenderTile` get an "annotations off" flag so the broker can draw foreign annotations itself for pages above a threshold?
5. ADR-0006 presenter change: tiles must be composed into a target for Multiply. Is that a presenter-wide change (always compose) or only when a Multiply item is visible?
