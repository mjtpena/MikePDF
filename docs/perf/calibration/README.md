# Calibration runs of the A/B gate

The numbers in the gate (a 10% limit, a 3% margin, a 95% interval, the `AbFloor` of each metric) were set from these runs. They are the `perf-ab.json` files of the Performance workflow, run on GitHub-hosted runners from the branch `m3/perf-gate`; `docs/perf/README.md`, "Where the numbers come from", reads them.

| Folder | What it is | Runs per architecture |
|---|---|---:|
| `aa/` | The head against a second copy of itself (`gh workflow run perf.yml -f base=self`). Every `regressed` here would be a false positive: there are none | 11 |
| `planted15/`, `planted18/`, `planted20/` | The same, with every time in the head lengthened by 15%, 18% or 20% (`-f plant=15`). Memory and `recovery.supervisor_restore` are not times the harness stretches and must stay `ok` | 6, 1, 5 |

* `index.md` lists every run with a link, its CPU and the commit measured. `summary.md` is the output of `MikePDF.Perf calibrate` on each folder.
* The files are the report of one run as JSON, written compact. `ratios` are the head over the base of each round, the input to the interval; `change`, `changeLower` and `changeUpper` are the median change and its 95% interval (positive is worse); `verdict` is what the harness decided at the time.
* The first eight A/A runs (commit `3dd485f`) used the floors of the stored-baseline comparison, which no A/A change came near. The first eight planted runs (commit `0716a92`) had a 1.2 ms floor on `search.first_result`, now 0.75 ms; that is why ARM64 missed it four times.
* Do not add runs made on a development machine, and never one with another planted fraction without naming the folder for it.

To add the nightly A/A runs, download the `perf-results-x64` and `perf-results-arm64` artifacts of several nights into one folder and run `MikePDF.Perf calibrate --dir <folder>`.
