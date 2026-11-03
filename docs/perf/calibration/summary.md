# Calibration summary

Made by `MikePDF.Perf calibrate --dir docs/perf/calibration/<kind>` from the files in this folder (see README.md). A change is the median of the per-round changes of one run, positive meaning worse; the lower end is the lower end of the 95% interval of the same. Verdict counts are the ones the harness gave at the time. The first eight A/A runs ran with the floors of the stored-baseline comparison, which no A/A change came near; the first eight planted runs ran with a 1.2 ms floor on the search first result. docs/perf/README.md says which planted misses the 1.2 ms floor caused.

# aa

## `ci-arm64`: 11 run(s), no slowdown planted

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **0 of 11**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 11 | -2.8% | -0.9% | 1.8% | -1.6% | 0 | 0 | 11 |
| `open.cold_engine.first_tile.text1` | 11 | -2.9% | -0.4% | 1.1% | -3.1% | 0 | 0 | 11 |
| `open.large.first_tile.text2000` | 11 | -1.8% | -0.7% | 1.2% | -1.6% | 0 | 0 | 11 |
| `open.engine_warm.first_tile.text20` | 11 | -0.9% | -0.4% | 1% | -0.3% | 0 | 0 | 11 |
| `open.engine_warm.first_tile.text2000` | 11 | -0.9% | -0.3% | 0.2% | -0.9% | 0 | 0 | 11 |
| `search.first_result.text500` | 11 | -0.8% | -0.2% | 0.7% | -0.4% | 0 | 0 | 11 |
| `search.full.text500` | 11 | -1% | 0% | 0.2% | -0.6% | 0 | 0 | 11 |
| `memory.engine_peak_working_set.text500` | 11 | -0.1% | 0% | 0.2% | -0.1% | 0 | 0 | 11 |
| `memory.engine_private_bytes.text500` | 11 | -0.2% | -0.1% | 0.2% | 0.1% | 0 | 0 | 11 |
| `recovery.crash_to_first_tile` | 11 | -2% | -0.2% | 1.2% | -1.4% | 0 | 0 | 11 |
| `recovery.supervisor_restore` | 11 | -2.3% | -0.3% | 1.2% | -1.7% | 0 | 0 | 11 |
| `render.tile_latency.text20` | 11 | -0.5% | 0.1% | 0.6% | -1.1% | 0 | 0 | 11 |
| `render.tile_throughput.text20` | 11 | -0.7% | 0% | 0.7% | -0.3% | 0 | 0 | 11 |

## `ci-x64`: 11 run(s), no slowdown planted

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **0 of 11**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 11 | -1.9% | -0.2% | 3.3% | -3% | 0 | 0 | 11 |
| `open.cold_engine.first_tile.text1` | 11 | -2.6% | -0.2% | 2.6% | -1.7% | 0 | 0 | 11 |
| `open.large.first_tile.text2000` | 11 | -3.7% | -0.2% | 2.8% | -4.4% | 0 | 0 | 11 |
| `open.engine_warm.first_tile.text20` | 11 | -0.8% | 0.4% | 1.6% | -1.2% | 0 | 0 | 11 |
| `open.engine_warm.first_tile.text2000` | 11 | -4.2% | 0.7% | 2.6% | -2.1% | 0 | 0 | 11 |
| `search.first_result.text500` | 11 | -8.9% | -0.4% | 4.6% | -8.3% | 0 | 0 | 11 |
| `search.full.text500` | 11 | -2.2% | 0% | 1.6% | -1.2% | 0 | 0 | 11 |
| `memory.engine_peak_working_set.text500` | 11 | -0.4% | -0.2% | 0.4% | -0.4% | 0 | 0 | 11 |
| `memory.engine_private_bytes.text500` | 11 | -0.5% | 0.1% | 1.1% | -0.6% | 0 | 0 | 11 |
| `recovery.crash_to_first_tile` | 11 | -3.8% | 0.5% | 2.6% | -3.9% | 0 | 0 | 11 |
| `recovery.supervisor_restore` | 11 | -3% | 0.4% | 3.3% | -1.8% | 0 | 0 | 11 |
| `render.tile_latency.text20` | 11 | -3% | 0% | 1.3% | -0.9% | 0 | 0 | 11 |
| `render.tile_throughput.text20` | 11 | -2% | -0.2% | 0.9% | -0.6% | 0 | 0 | 11 |


# planted15

## `ci-arm64`: 6 run(s), planted slowdown 15%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **6 of 6**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 6 | 13.3% | 15.1% | 15.7% | 14.8% | 6 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 6 | 11.6% | 14.6% | 15.5% | 13.2% | 6 | 0 | 0 |
| `open.large.first_tile.text2000` | 6 | 12.6% | 13.8% | 16.2% | 13.4% | 6 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 6 | 12.2% | 12.8% | 13.4% | 12.5% | 6 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 6 | 12.4% | 13% | 14.6% | 13.4% | 6 | 0 | 0 |
| `search.first_result.text500` | 6 | 15.2% | 15.7% | 16.4% | 14.7% | 2 | 0 | 4 |
| `search.full.text500` | 6 | 15.2% | 15.7% | 15.8% | 15.3% | 6 | 0 | 0 |
| `memory.engine_peak_working_set.text500` | 6 | 0% | 0% | 0% | -0.1% | 0 | 0 | 6 |
| `memory.engine_private_bytes.text500` | 6 | -0.2% | 0% | 0.2% | -0.4% | 0 | 0 | 6 |
| `recovery.crash_to_first_tile` | 6 | 13.5% | 14.5% | 15.2% | 13.3% | 6 | 0 | 0 |
| `recovery.supervisor_restore` | 6 | -2% | -1.2% | -1% | -4.2% | 0 | 0 | 6 |
| `render.tile_latency.text20` | 6 | 14.6% | 15.1% | 15.6% | 14.1% | 6 | 0 | 0 |
| `render.tile_throughput.text20` | 6 | 12.7% | 13% | 13.3% | 12.7% | 6 | 0 | 0 |

## `ci-x64`: 6 run(s), planted slowdown 15%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **6 of 6**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 6 | 12.4% | 15.7% | 18.9% | 17.1% | 6 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 6 | 11.5% | 16.1% | 19.5% | 14.4% | 6 | 0 | 0 |
| `open.large.first_tile.text2000` | 6 | 14% | 15.7% | 21.3% | 18.1% | 6 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 6 | 11.3% | 14.2% | 15.1% | 12.5% | 6 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 6 | 14% | 15.8% | 18% | 13.2% | 6 | 0 | 0 |
| `search.first_result.text500` | 6 | 10.1% | 14.5% | 21.7% | 16% | 4 | 0 | 2 |
| `search.full.text500` | 6 | 11.8% | 15% | 15.2% | 13.1% | 5 | 1 | 0 |
| `memory.engine_peak_working_set.text500` | 6 | -0.3% | 0.1% | 0.2% | -0.9% | 0 | 0 | 6 |
| `memory.engine_private_bytes.text500` | 6 | -0.6% | 0% | 0.4% | -2.3% | 0 | 0 | 6 |
| `recovery.crash_to_first_tile` | 6 | 9.2% | 11.1% | 13.1% | 10.2% | 3 | 1 | 2 |
| `recovery.supervisor_restore` | 6 | -5.7% | -3.3% | 1.6% | -5.2% | 0 | 0 | 6 |
| `render.tile_latency.text20` | 6 | 13.9% | 14.8% | 17.4% | 15% | 6 | 0 | 0 |
| `render.tile_throughput.text20` | 6 | 11.6% | 12.8% | 13.4% | 12.6% | 6 | 0 | 0 |


# planted18

## `ci-arm64`: 1 run(s), planted slowdown 18%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **1 of 1**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 1 | 16% | 16% | 16% | 8% | 1 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 1 | 15.4% | 15.4% | 15.4% | 6.6% | 1 | 0 | 0 |
| `open.large.first_tile.text2000` | 1 | 13.7% | 13.7% | 13.7% | 9.2% | 1 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 1 | 15.2% | 15.2% | 15.2% | 12.6% | 1 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 1 | 16.5% | 16.5% | 16.5% | 14.3% | 1 | 0 | 0 |
| `search.first_result.text500` | 1 | 17.9% | 17.9% | 17.9% | 16.5% | 1 | 0 | 0 |
| `search.full.text500` | 1 | 18.6% | 18.6% | 18.6% | 17.3% | 1 | 0 | 0 |
| `memory.engine_peak_working_set.text500` | 1 | -0.1% | -0.1% | -0.1% | -0.5% | 0 | 0 | 1 |
| `memory.engine_private_bytes.text500` | 1 | 0% | 0% | 0% | -0.4% | 0 | 0 | 1 |
| `recovery.crash_to_first_tile` | 1 | 17.1% | 17.1% | 17.1% | 15% | 1 | 0 | 0 |
| `recovery.supervisor_restore` | 1 | 0.8% | 0.8% | 0.8% | -8.4% | 0 | 0 | 1 |
| `render.tile_latency.text20` | 1 | 20.2% | 20.2% | 20.2% | 18.6% | 1 | 0 | 0 |
| `render.tile_throughput.text20` | 1 | 15.8% | 15.8% | 15.8% | 15.1% | 1 | 0 | 0 |

## `ci-x64`: 1 run(s), planted slowdown 18%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **1 of 1**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 1 | 17.5% | 17.5% | 17.5% | 15.1% | 1 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 1 | 17.6% | 17.6% | 17.6% | 12.2% | 1 | 0 | 0 |
| `open.large.first_tile.text2000` | 1 | 18% | 18% | 18% | 13.6% | 1 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 1 | 15.4% | 15.4% | 15.4% | 10.2% | 1 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 1 | 15.6% | 15.6% | 15.6% | 10.8% | 1 | 0 | 0 |
| `search.first_result.text500` | 1 | 6.9% | 6.9% | 6.9% | -8.6% | 0 | 0 | 1 |
| `search.full.text500` | 1 | 15.7% | 15.7% | 15.7% | 12.3% | 1 | 0 | 0 |
| `memory.engine_peak_working_set.text500` | 1 | 0.4% | 0.4% | 0.4% | -1% | 0 | 0 | 1 |
| `memory.engine_private_bytes.text500` | 1 | 1.7% | 1.7% | 1.7% | -3.4% | 0 | 0 | 1 |
| `recovery.crash_to_first_tile` | 1 | 18.3% | 18.3% | 18.3% | 14.4% | 1 | 0 | 0 |
| `recovery.supervisor_restore` | 1 | -1.9% | -1.9% | -1.9% | -10.6% | 0 | 0 | 1 |
| `render.tile_latency.text20` | 1 | 17.6% | 17.6% | 17.6% | 15.6% | 1 | 0 | 0 |
| `render.tile_throughput.text20` | 1 | 15.6% | 15.6% | 15.6% | 13.8% | 1 | 0 | 0 |


# planted20

## `ci-arm64`: 5 run(s), planted slowdown 20%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **5 of 5**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 5 | 19.4% | 20.3% | 22.4% | 20.3% | 5 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 5 | 18.3% | 18.6% | 19% | 16.5% | 5 | 0 | 0 |
| `open.large.first_tile.text2000` | 5 | 16.9% | 18.8% | 19.1% | 16.2% | 5 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 5 | 17.7% | 18% | 19.2% | 17.6% | 5 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 5 | 17.6% | 18.8% | 19% | 17.3% | 5 | 0 | 0 |
| `search.first_result.text500` | 5 | 19.5% | 19.8% | 20.7% | 18.8% | 5 | 0 | 0 |
| `search.full.text500` | 5 | 20.3% | 20.9% | 21.1% | 20.2% | 5 | 0 | 0 |
| `memory.engine_peak_working_set.text500` | 5 | -0.1% | 0% | 0% | -0.3% | 0 | 0 | 5 |
| `memory.engine_private_bytes.text500` | 5 | -0.2% | 0.2% | 0.2% | -0.3% | 0 | 0 | 5 |
| `recovery.crash_to_first_tile` | 5 | 18.4% | 20.3% | 21.7% | 17.6% | 5 | 0 | 0 |
| `recovery.supervisor_restore` | 5 | -1.5% | -0.5% | 0.8% | -2.2% | 0 | 0 | 5 |
| `render.tile_latency.text20` | 5 | 19.2% | 20.1% | 21.1% | 20% | 5 | 0 | 0 |
| `render.tile_throughput.text20` | 5 | 16.4% | 16.6% | 17% | 16.5% | 5 | 0 | 0 |

## `ci-x64`: 5 run(s), planted slowdown 20%

Rule: change over 10%, lower end of the 95% interval over 3%, and the metric's floor. Runs with at least one regressed metric: **5 of 5**.

| Metric | Runs | Change: min | median | max | Lower end: max | Regressed | Unconfirmed | Ok or better |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `open.warm.first_tile.text20` | 5 | 17.6% | 21.7% | 24.5% | 21.1% | 5 | 0 | 0 |
| `open.cold_engine.first_tile.text1` | 5 | 20.2% | 22.4% | 29.9% | 22.7% | 5 | 0 | 0 |
| `open.large.first_tile.text2000` | 5 | 22.9% | 23.3% | 25% | 21% | 5 | 0 | 0 |
| `open.engine_warm.first_tile.text20` | 5 | 17.9% | 18.6% | 19.6% | 17% | 5 | 0 | 0 |
| `open.engine_warm.first_tile.text2000` | 5 | 17.7% | 19.7% | 22% | 18.9% | 5 | 0 | 0 |
| `search.first_result.text500` | 5 | 19% | 22.5% | 24.5% | 20.4% | 4 | 0 | 1 |
| `search.full.text500` | 5 | 20% | 20.3% | 20.7% | 19.8% | 5 | 0 | 0 |
| `memory.engine_peak_working_set.text500` | 5 | -0.4% | -0.2% | 0.1% | -0.3% | 0 | 0 | 5 |
| `memory.engine_private_bytes.text500` | 5 | -1.4% | -0.3% | 0% | -0.3% | 0 | 0 | 5 |
| `recovery.crash_to_first_tile` | 5 | 7.9% | 15.2% | 19.9% | 15.2% | 4 | 0 | 1 |
| `recovery.supervisor_restore` | 5 | -5.2% | -1.7% | 1.7% | -3.2% | 0 | 0 | 5 |
| `render.tile_latency.text20` | 5 | 18.8% | 19.4% | 20.2% | 18.9% | 5 | 0 | 0 |
| `render.tile_throughput.text20` | 5 | 16.4% | 16.7% | 16.9% | 16.3% | 5 | 0 | 0 |


