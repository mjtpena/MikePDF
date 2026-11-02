# W2-engine-2: what was measured about rendering

- Date: 2026-11-03
- Author: Sonnet implementer (W2-engine-2), for Opus's review
- Everything here was measured on x64 with the pinned PDFium (chromium/8125, 158.0.8125.0). Throwaway harnesses (a P/Invoke program over `pdfium.dll`) were used and are not in the repository; the numbers can be reproduced with `tools/MikePDF.Render` and the engine tests named below.

## 1. Dark pages: FPDF_COLORSCHEME against the matrix, and what was built

Question: "inverted pages must not invert photographs". Candidates, measured on the 100-file smoke tier (103 pages rendered, page 1 and 2 of each file at about 600 px wide):

- **A, today**: draw normally, then the app's `PageColourMatrix.Inverted` over every pixel (on the GPU).
- **B, `FPDF_RenderPageBitmapWithColorScheme_Start`** with `FPDF_COLORSCHEME` (paper dark, path and text colours set). Two variants of the path fill (paper colour, mid grey), `FPDF_CONVERT_FILL_TO_STROKE` on.
- **C, built**: draw normally, then the same matrix in the engine over every pixel except the rectangles of colour photographs.

| Measure (pages that have it) | A | B | C |
|---|---|---|---|
| Mean absolute change of photograph pixels, 9 pages with photographs (0 = untouched) | 0.671 | 0.257 | 0.000 |
| Share of coloured vector and text pixels that stay coloured, 32 pages | 1.00 | 0.16 (27 of 32 pages: none at all) | 1.00 |

B is rejected. PDFium's forced-colour mode replaces **every** path fill, path stroke, text fill and text stroke with one colour each, so blue headings, links, charts, table shading and page-number boxes all turn the text colour (`bug_650.pdf` page 1: the blue heading, the blue link and the red page-number box turn white or are outlined). It is a monochrome high-contrast mode, not a dark mode. It also altered three of the nine photograph pages (`image_clipped`, `matte`, `rotated_image`: images drawn through masks or clips), and it needs a different paper colour for every call. It does leave ordinary images alone.

A keeps hue, but inverts photographs (and, as the app does it, on the GPU, where nothing can tell a photograph from a scan).

C is what was built: render flag **bit 2 of `RenderTile`** ("dark pages"), no change to the schema's layout. The schema's `flags` limit went from 3 to 7 for `RenderTile`; `schemaVersion` stays 5 (no field, message or enum changed, and a peer of the same build always agrees). What counts as a photograph is decided in the engine from the image's own pixels, not its metadata, because metadata cannot tell a CAD drawing stored as 24-bit RGB (the diagram on `bug_650.pdf`, which must be recoloured with the page: a white box on a dark page is worse than an inverted drawing) from a photograph:

- a picture of at least 16 x 16 pixels, more than one bit a pixel (stencil masks are text);
- covering less than 90% of the page (a full-page picture is a scan, so it is recoloured as text);
- fewer than 55% near-white (all channels 235 or more) in a 48 x 48 sample of its decoded pixels.

The analysis decodes each image once per page (at most 32 images; the result for the last page asked about is kept), so a page's first dark tile costs about what drawing its images costs. Every later tile of the page reuses the rectangles.

Known limits, on purpose: a photograph on a white studio background is more than 55% white and is recoloured like a document; a rotated or skewed photograph keeps its axis-aligned bounding rectangle, so a corner of the paper around it is left white; a photograph that carries printed text keeps the text bright on its own background. Where an image is clipped (a circular portrait) the whole rectangle is left alone.

The app must not draw dark tiles through the inverted matrix as well. See the completion note for what the app-shell owner calls: `EngineTileRenderer.DarkPages`, an empty tile cache on change, and `Presenter.ColourMode = Normal` while it is on. `Sepia` stays on the GPU.

Tests: `tests/MikePDF.Engine.Tests/dark_page_tests.cpp` (matrix numbers, photograph untouched, line art recoloured, tiles of a dark page add up to the whole across a photograph, rotation, scan and tiny image, interrupt), `tests/MikePDF.Core.Tests/DarkPageContractTests.cs` (the same numbers against the C# matrix), `tests/MikePDF.Integration.Tests/DarkPageTests.cs` (through the sandbox).

## 2. Tiles that do not match the whole page (the 51 known-low corpus pages)

The corpus PR found 51 pages under SSIM 0.98 against the PDFium reference, all scans from Wikimedia Commons. They are **two different causes**; one is fixed here.

### 2.1 Skewed scans: fixed (8 of the 51 pages; SSIM 0.73 to 0.97 become 0.99999 or better)

A crooked scan is drawn under a matrix with a shear (`[828 1.62 -2.11 1080 ...]` on the Observer, 0.1 degrees). PDFium draws such an image with its image transformer, and the transformer takes the shear from the corner of the **clip rectangle**, not from the corner of the image. A tile or band that does not start at the page's corner therefore shows the image shifted, by an amount that grows with the distance from the corner: measured on `Newspaper_The_Observer...1967-02-20.pdf` page 8 at 2x, tile (0,0) is identical to the whole-page render, and the best alignment of the other tiles is 1 to 5 pixels off in both directions (mean luminance difference 12 to 18 levels on 27 to 33% of the pixels). Plain `FPDF_RenderPageBitmap` with a negative start offset reproduces it, so it is PDFium's, not the tile matrix. The same applies to the print path: the bands of a skewed scan did not line up with the whole page.

Fix (`Document::render_window`): for a tile or band that does not start at the page's corner, on a page that draws a skewed image (found once per page by walking the page objects), the engine draws from the corner through the far edge of the window plus 8 pixels into a scratch bitmap and copies the window out. Measured: pixel-identical to the whole-page render (mean absolute difference 0.00 to 0.01). Budget: a scratch bitmap is at most 144 MiB (a whole A4 page at 600 dpi is 133 MiB); a window that needs more is drawn the plain way (and may show the shift). The cost is that a far tile of a skewed page is drawn at the size of everything before it; only pages with a skewed image pay it.

Tests: `tests/MikePDF.Engine.Tests/tile_consistency_tests.cpp` (exact equality of tiles and bands with the whole page for a 0.3 degree skew at 2x; different skew and scales with at most one differing pixel in 100,000; unskewed and quarter-turned images unaffected; over-budget window still drawn; interrupt leaves the destination untouched). With the budget forced to zero the first test fails, as it should.

SSIM before (the x64 core baseline on `origin/m3/corpus`) and after (`tools/MikePDF.Render` on the same 12 files, the same pages and scales, with this engine). 51 pages were under 0.98; 8 changed and are now 0.99999 or 1.0000, the other 43 are unchanged to the sixth decimal:

| Page (scale 2x) | Before | After |
|---|---|---|
| The Observer 1967-02-20, page 8 | 0.7308 | 0.99999 |
| The Observer, page 11 | 0.7440 | 1.0000 |
| The Observer, page 2 | 0.7639 | 1.0000 |
| The Observer, page 5 | 0.7954 | 0.99999 |
| Persian memoir in the Vahid magazine (`commons/196813432-...`), pages 11, 10, 5 | 0.9588, 0.9719, 0.9750 | 1.0000 |
| Persian party history (`118736058`), page 53 | 0.9695 | 1.0000 |

Mean over the 51 pages: 0.928 before, 0.949 after; weakest 0.731 before, 0.910 after.

### 2.2 Large scans drawn per tile: not fixable from the tile matrix (the other 43 pages)

These are unchanged by the fix above (their SSIM is identical to the baseline). They are not skewed (`f01`: one axis-aligned 4096 x 6144 JPEG drawn at 3072 x 4608 pt, matrix `[3072 0 0 4608 0 0]`) and their SSIM is already low at 1x, where the page is 15 tiles of 1024. Evidence:

- Whole-page render (what the reference does) against the same page drawn in 1024 tiles, **on one loaded page**: identical. On a **freshly loaded page per tile**, which is what the engine does for each request: not identical, even for the tile at (0,0): mean luminance difference 1.7 to 4.6 levels, 2 to 14% of pixels more than 8 off.
- The difference depends on the size of the bitmap that is drawn into: tile sizes 512, 1024, 2048 and 3072 give, for the tile at the far corner, 6.65, 4.32, 0.99 and 0.00 levels. A tile as wide as the page equals the whole-page render exactly.
- The reference process, my own whole-page render and the engine's output: the reference and the whole-page render are bit-identical (0.000); the engine's tile output differs from both by 2.67 levels on average, 9% of pixels more than 8, maximum 75.

So PDFium's decoding or resampling of a large image depends on the size of the bitmap it draws into (as far as I could tell from its behaviour: PDFium passes the device size to the image loader as a hint, and for JPEG 2000 that makes it skip resolution levels; `f01` is a JPEG, where I did not find the mechanism, only the dependence on tile size above), and a tile of at most 1024 px tells it that less is needed than the whole page would. The result is a softer picture in the tiles than in a whole-page render, which is visible at 100% on these scans and is what the SSIM measures. It cannot be corrected by a different matrix or interpolation flags: the same flags give identical pixels on a warm page.

Options, in order of cost:

1. Accept and record it: these 43 pages stay known-low in the baseline (as the corpus PR did). The tiles are consistent with each other, only softer than a whole-page render.
2. **Whole-page bitmap cache in the engine** for pages whose images are larger than their drawn size: draw the whole page once per (page, scale, rotation) into a cached bitmap when it fits a budget (3072 x 4608 is 56 MB; 2x of it is 200 MB and does not), and cut tiles from it. Pixels equal the reference exactly, and it is also faster, because today every tile decodes the whole image again (15 decodes of a 4096 x 6144 JPEG for one 1x page). Costs: the first tile of a page waits for the whole page, a cancel is honoured only at PDFium's poll points, and the cache is up to 144 MiB of engine memory.
3. Bigger tiles for image-heavy pages (a protocol change: `tileWidth` and `tileHeight` are limited to 1024 and the slots to 4 MiB).

I did not build option 2: it changes first-tile latency and engine memory, which are the Opus's and the perf harness's budgets to decide. The measurements above say how much it would gain.
