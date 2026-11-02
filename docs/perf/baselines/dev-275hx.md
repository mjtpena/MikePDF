# MikePDF performance results

- Runner: `dev-275hx`, **not reference hardware**
- CPU: Intel(R) Core(TM) Ultra 9 275HX, 24 logical cores
- RAM: 63.4 GB
- GPU: Intel(R) Graphics (driver 32.0.101.8331); NVIDIA GeForce RTX 5080 Laptop GPU (driver 32.0.16.1742); DisplayLink USB Device (driver 11.2.3146.0); DisplayLink USB Device (driver 11.2.3146.0); Honor Virtual Display Device (driver 1.0.23.3); Virtual Display Driver (driver 11.30.4.434)
- OS: Microsoft Windows 10.0.26300 (x64); .NET 10.0.8; power plan: Balanced
- Machine busy with other work before the run: 32%
- Profile: full (warm-up 5, 30 iterations, 9 for the heavy scenarios)
- Date: 2026-11-02 09:18 UTC
- Engine: MikePDF.Engine.exe `fd81d55a8bfb`, pdfium.dll `4b0dec92555d`

## Metrics

| Metric | Unit | n | Median | p95 | CV | §11 target | Baseline | Change | Result |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| `open.warm.first_tile.text20` | ms | 30 | 50.4 | 71.6 | 15% | ≤ 250 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.cold_engine.first_tile.text1` | ms | 30 | 60.0 | 83.0 | 29% | ≤ 800 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.large.first_tile.text2000` | ms | 30 | 57.8 | 76.0 | 13% | ≤ 1000 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.engine_warm.first_tile.text20` | ms | 30 | 6.54 | 7.34 | 6% | - | - | - | ok |
| `open.engine_warm.first_tile.text2000` | ms | 30 | 8.78 | 10.8 | 8% | - | - | - | ok |
| `search.first_result.text500` | ms | 9 | 10.1 | 14.9 | 18% | ≤ 300 (R1) | - | - | within target here (not reference hardware) |
| `search.full.text500` | ms | 9 | 833 | 1016 | 8% | ≤ 3000 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_peak_working_set.text500` | MB | 9 | 19.3 | 20.4 | 2% | ≤ 600 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_private_bytes.text500` | MB | 9 | 6.27 | 6.47 | 2% | - | - | - | ok |
| `recovery.crash_to_first_tile` | ms | 30 | 30.2 | 48.1 | 21% | ≤ 2000 (R1) | - | - | within target here (not reference hardware) |
| `recovery.supervisor_restore` | ms | 30 | 20.8 | 34.4 | 23% | - | - | - | ok |
| `render.tile_latency.text20` | ms | 30 | 0.75 | 1.29 | 27% | - | - | - | ok |
| `render.tile_throughput.text20` | tiles/s | 30 | 1230 | 1566 | 26% | - | - | - | ok |
| `package.install_size` | MB | - | - | - | - | - | - | - | skipped |
| `package.msix_file_size` | MB | - | - | - | - | - | - | - | skipped |

## Generated documents

| Id | Pages | Bytes | SHA-256 |
|---|---:|---:|---|
| text1 | 1 | 4,706 | `38bfd6dee3ec09b0` |
| text20 | 20 | 85,193 | `0733a3d9c5c96947` |
| text500 | 500 | 2,134,447 | `e81e19cdf8074177` |
| text2000 | 2000 | 8,545,548 | `3ad49c8f94b696eb` |

## Notes

- Headless: the broker and the real sandboxed engine, no window. The app's process start, XAML, GPU upload and compositor are not in these numbers; they are measured by the app's trace markers (tools/MikePDF.Perf/measure-app.ps1), which have not been run end to end.
- Not reference hardware (R1, R2 and R3 are not declared for this run): every comparison with a §11 target is indicative only and proves nothing about the budget.
- Waited 40.8 s (at most 40 s) for the machine to be under 15% busy before measuring; it read 32% when the run began.
- The machine was 32% busy with other work before the run began (all programs, all logical processors): every timing here is inflated by that. Rerun on an idle machine before trusting a number.
- Skipped package.install_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
- Skipped package.msix_file_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
