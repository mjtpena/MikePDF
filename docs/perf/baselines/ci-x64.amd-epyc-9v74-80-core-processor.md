# MikePDF performance results

- Runner: `ci-x64`, **not reference hardware** (CI-hosted)
- CPU: AMD EPYC 9V74 80-Core Processor, 4 logical cores
- RAM: 16 GB
- GPU: Microsoft Hyper-V Video (driver 10.0.26100.1150)
- OS: Microsoft Windows 10.0.26100 (x64); .NET 10.0.14; power plan: High performance
- Machine busy with other work before the run: 7%
- Profile: ci (warm-up 3, 15 iterations, 5 for the heavy scenarios)
- Date: 2026-11-02 10:03 UTC
- Engine: MikePDF.Engine.exe `42e5bdb2a04e`, pdfium.dll `4b0dec92555d`

## Metrics

| Metric | Unit | n | Median | p95 | CV | §11 target | Baseline | Change | Result |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| `open.warm.first_tile.text20` | ms | 15 | 27.5 | 47.0 | 20% | ≤ 250 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.cold_engine.first_tile.text1` | ms | 15 | 24.1 | 28.2 | 6% | ≤ 800 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.large.first_tile.text2000` | ms | 15 | 31.1 | 41.5 | 10% | ≤ 1000 (R1) | - | - | engine share only: under target here (not reference hardware), not a pass |
| `open.engine_warm.first_tile.text20` | ms | 15 | 5.97 | 6.39 | 3% | - | - | - | ok |
| `open.engine_warm.first_tile.text2000` | ms | 15 | 8.14 | 8.76 | 3% | - | - | - | ok |
| `search.first_result.text500` | ms | 5 | 9.50 | 11.6 | 10% | ≤ 300 (R1) | - | - | within target here (not reference hardware) |
| `search.full.text500` | ms | 5 | 694 | 723 | 2% | ≤ 3000 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_peak_working_set.text500` | MB | 5 | 18.2 | 18.4 | 0% | ≤ 600 (R1) | - | - | within target here (not reference hardware) |
| `memory.engine_private_bytes.text500` | MB | 5 | 6.17 | 6.36 | 2% | - | - | - | ok |
| `recovery.crash_to_first_tile` | ms | 15 | 16.2 | 18.7 | 7% | ≤ 2000 (R1) | - | - | within target here (not reference hardware) |
| `recovery.supervisor_restore` | ms | 15 | 10.8 | 12.1 | 6% | - | - | - | ok |
| `render.tile_latency.text20` | ms | 15 | 0.68 | 0.70 | 2% | - | - | - | ok |
| `render.tile_throughput.text20` | tiles/s | 15 | 1281 | 1297 | 3% | - | - | - | ok |
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
- Waited 0.5 s (at most 20 s) for the machine to be under 15% busy before measuring; it read 7% when the run began.
- Ran on a CI-hosted runner: a shared virtual machine. Useful for catching regressions against its own baseline, never for judging a budget.
- Skipped package.install_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
- Skipped package.msix_file_size: No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.
