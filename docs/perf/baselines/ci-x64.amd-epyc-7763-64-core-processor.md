# MikePDF performance results

- Runner: `ci-x64`, **not reference hardware** (CI-hosted)
- CPU: AMD EPYC 7763 64-Core Processor, 4 logical cores
- RAM: 16 GB
- GPU: Microsoft Hyper-V Video (driver 10.0.26100.1150)
- OS: Microsoft Windows 10.0.26100 (x64); .NET 10.0.14; power plan: High performance
- Machine busy with other work before the run: 6%
- Profile: ci (warm-up 3, 15 iterations, 5 for the heavy scenarios)
- Date: 2026-11-02 09:17 UTC
- Engine: MikePDF.Engine.exe `2eaac615c2d2`, pdfium.dll `4b0dec92555d`

## Metrics

| Metric | Unit | n | Median | p95 | CV | §11 target | Baseline | Change | Result |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| `open.warm.first_tile.text20` | ms | 15 | 32.8 | 43.1 | 8% | ≤ 250 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.cold_engine.first_tile.text1` | ms | 15 | 28.2 | 33.4 | 5% | ≤ 800 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.large.first_tile.text2000` | ms | 15 | 36.0 | 41.3 | 5% | ≤ 1000 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.engine_warm.first_tile.text20` | ms | 15 | 7.58 | 8.13 | 3% | - | - | - | ok |
| `open.engine_warm.first_tile.text2000` | ms | 15 | 10.4 | 15.3 | 12% | - | - | - | ok |
| `search.first_result.text500` | ms | 5 | 11.0 | 11.4 | 3% | ≤ 300 (R1) | - | - | within target here (not reference hardware) |
| `search.full.text500` | ms | 5 | 851 | 875 | 1% | ≤ 3000 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_peak_working_set.text500` | MB | 5 | 18.2 | 18.4 | 1% | ≤ 600 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_private_bytes.text500` | MB | 5 | 6.17 | 6.50 | 3% | - | - | - | ok |
| `recovery.crash_to_first_tile` | ms | 15 | 20.6 | 24.7 | 10% | ≤ 2000 (R1) | - | - | within target here (not reference hardware) |
| `recovery.supervisor_restore` | ms | 15 | 14.0 | 17.6 | 10% | - | - | - | ok |
| `render.tile_latency.text20` | ms | 15 | 0.90 | 0.91 | 1% | - | - | - | ok |
| `render.tile_throughput.text20` | tiles/s | 15 | 1015 | 1020 | 3% | - | - | - | ok |
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
- Waited 0.5 s (at most 20 s) for the machine to be under 15% busy before measuring; it read 6% when the run began.
- Ran on a CI-hosted runner: a shared virtual machine. Useful for catching regressions against its own baseline, never for judging a budget.
- Skipped package.install_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
- Skipped package.msix_file_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
