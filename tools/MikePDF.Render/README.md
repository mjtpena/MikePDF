# mikepdf-render

A headless command line that renders PDFs through the **sandboxed engine** and reports how they compare with PDFium running outside the sandbox. It is the M1 exit gate of the [master prompt](../../docs/MASTER-PROMPT.md) (§17, §12.3): "a headless CLI renders the smoke corpus through the sandboxed engine with visual diffs reported".

```powershell
./build.ps1                                   # builds the native engine (needed once, and after engine changes)
dotnet build tools/MikePDF.Render -c Release  # builds the tool and the reference renderer

# render some files, or a folder
dotnet run --project tools/MikePDF.Render -c Release --no-build -- docs\sample.pdf --scale 1,2 --png --out render-out

# the smoke corpus: fetch and verify it once, then run the gate
dotnet run --project tools/MikePDF.Render -c Release --no-build -- corpus fetch --tier smoke
dotnet run --project tools/MikePDF.Render -c Release --no-build -- --tier smoke --scale 1,2 --out render-out
```

`--help` lists every option. Exit code: **0** the gate passed, **1** it failed, **2** the run could not start (bad arguments, no engine, the sandbox could not be created: there is no weaker fallback, ADR-0004).

## What it does

- **Renders through the real sandbox.** It deploys `MikePDF.Engine.exe` and `pdfium.dll` to a private folder, grants the AppContainer read access to it (`EngineDeployment.EnsureAppContainerAccess`, as the integration tests do), and drives the engine with `MikePDF.Broker`'s `EngineSupervisor`. This process never loads PDFium. Each page is asked for tile by tile (1024 px, the largest the protocol allows), as the viewer does, and the tiles are stitched into one image.
- **Compares each page with a reference.** `mikepdf-refrender` (`tools/MikePDF.Render.Reference`, a separate process, never shipped) loads the very `pdfium.dll` the engine links, calls it directly with the engine's call sequence, and writes raw pixels. The page's **SSIM** is computed on luminance with a uniform 8 x 8 window (`Imaging/Ssim.cs`: exact integer window sums, no dependency), along with the share of pixels that changed by more than 8 levels. A page under the bar gets a PNG with the reference, the MikePDF render and the difference side by side in `diff/`.
- **Survives hostile files.** An engine that crashes, hangs or runs out of memory is replaced by the supervisor; the page is tried again (`--retries`, default 1); after three failed pages in a row a file is abandoned; a file that outlives `--file-timeout-s` is cut off; a supervisor that gives up is replaced by a fresh one; a reference renderer that crashes or hangs is killed. A request that goes unanswered for `--hang-grace-ms` (default 2 s) past its deadline counts as a hang and the watchdog kills the engine: PDFium looks at its deadline only about every 100 drawing operations, so a page of very heavy operations can overshoot it on a slow machine. Every such event is in the report, with the file and page it happened in, and the next file renders.
- **Reports** in the error model of §6.4 (`status` strings are the protocol's `Status` names): `report.json` (machine-readable, schema `mikepdf-render-report/1`) and `report.md`. Per file: open status and time, page count, expected outcome, engine events (crash, restore, give-up, with exit code and restore time). Per page and scale: status, time, size, tiles, retries, SSIM, changed pixels, image paths.

## The gate

A run fails (exit code 1) on any of:

| Failure | Meaning |
|---|---|
| `EngineCrash`, `EngineHang`, `EngineFailed` | An engine process ended, was killed by the watchdog or the job's memory limit, or the supervisor gave up. Even if the run recovered. Tolerated only for a file whose manifest entry says `allowsEngineCrash` or an `engineEvent` (`EngineFailed`, a supervisor that gave up, is never tolerated) |
| `ExpectedEngineEventMissing`, `EngineNotRestored` | A file whose entry says `expect.engineEvent` is `crash` or `hang` did not take the engine down in that way, or the run did not see the engine replaced afterwards. Such a file is in the smoke tier to exercise recovery on every pull request; one that stopped doing so tests nothing |
| `ReferenceCrash`, `ReferenceHang` | The same for the reference renderer (PDFium outside the sandbox) |
| `UnexpectedOpenStatus`, `PageCountMismatch` | A file opened differently from its `expect` entry |
| `RenderFailed`, `Timeout`, `PartiallyRendered` | A page did not render `Ok` |
| `SsimBelowThreshold` | A page scored under `--ssim-threshold` (default 0.98, §0.3) and was not already that low in the baseline |
| `SsimRegression` | A page scored more than 0.002 under its baseline entry, even if above the bar |
| `SsimPassRateBelowTarget` | Fewer than `--ssim-target` percent (default 98, §0.3) of compared pages reached the bar |
| `OpenSuccessBelowTarget` | Fewer than 99.5% of the files that should open opened (§0.3). The files that count are those expected to open plus real files marked `expect.knownIssue` (a PDFium limitation, not a file broken on purpose); the report also says whether the M2 bar of 99% is met |
| `OpenSuccessRegression` | The open rate fell below the one recorded in the baseline, even if it is still above 99.5% |
| `NothingCompared` | The reference is on and no page could be compared |

The **baseline** (`tests/corpus/baselines/<tier>-<arch>.json`, picked up automatically for a tier run, or `--baseline`) records each page's SSIM at the last accepted run, per architecture. Write one with `--write-baseline <file>` after a run you have reviewed, and commit it in the same change as whatever moved the scores.

A manifest entry can also say `allowsDamagedPages` (the document opens, but pages broken on purpose cannot be loaded: those end `Damaged` and are warnings, nothing else is excused), `knownIssue` (a real file that should open and does not: it counts against the open rate) and, under `render`, `pages` (which pages to render), `tileDeadlineMs` and `reference: false`. The nightly workflow (`.github/workflows/nightly-corpus.yml`) runs `--tier core` on x64 and ARM64 with this gate and the per-architecture baseline.

The report (`report.md`) also gives the open rate against both bars, the SSIM distribution (minimum, 1st and 5th percentile, median, mean, and how many pages fall in each band) and a table per tag (scanned, tagged, form, cjk, rtl, encrypted ...) with how many files of each opened and how many pages rendered. Engine events are filed under the file that caused them: a hang is reported by the supervisor a moment after the request it hit has been answered, so at the end of a file the run waits (up to 20 s when the file is meant to take the engine down or a page ended `EngineCrashed`, 3 s when a page only timed out) for that event before it moves on.

## The corpus

`corpus check | fetch | verify | generate`, driven by `tests/corpus/manifest.json` ([ADR-0010](../../docs/adr/0010-corpus-sourcing.md), [tests/corpus/README.md](../../tests/corpus/README.md)). Files are never committed: third-party files are downloaded from a pinned URL and generated files are made by `Corpus/CorpusGenerators.cs`; both are checked against the manifest's SHA-256 before use, and a run refuses a corpus that does not match.

## Tests

`tests/MikePDF.Render.Tests` covers SSIM against the definition, the PNG codec against hand-filtered data, the gate rule by rule, the manifest checks, fetching with a fake server, and, against the real sandboxed engine, rendering with and without the reference, engine kills, deadlines, file time limits, hostile files and the command line's exit codes.
