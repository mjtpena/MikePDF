# Fuzzing

- Status: targets run locally and in CI. The M1 "fuzzers have run 24 hours" gate is **not yet met**: the plan to meet it is below
- Owner: W1-fuzz (Sonnet), reviewed by Opus
- Spec: master prompt §12.6 and §13, [ADR-0005](adr/0005-ipc-transport.md) rule 7, [ADR-0008](adr/0008-save-pipeline.md), [R-19](risks.md), [threat model](threat-model/README.md) B1 and B2. Toolchain proposal: [ADR-0017](adr/0017-fuzzing-toolchain.md)

## What is fuzzed, and how well

| Target | Code under test | Engine | Guided by | What counts as a finding |
|---|---|---|---|---|
| `protocol-cpp` | C++ IPC decoders, engine side (`msgpack.cpp`, `codec.cpp`, generated `messages.cpp`): all four request and response decoders, whole frame and bare body | libFuzzer + AddressSanitizer (MSVC) | **coverage** (`ft:` features) and value profile | any sanitizer report, abort, hang over 30 s; a decoded message that cannot be re-encoded or whose encoding is not a fixed point |
| `engine-document` | `mikepdf::pdfium::Document`: open (with and without a password), info, page sizes, outline, page boxes, links, link addresses, page text, search, `render_tile` over scales 0.01 to 64, rotations, tile origins at the schema edges, and a too-small buffer | libFuzzer + AddressSanitizer (MSVC) | coverage of **MikePDF's wrapper only** | any sanitizer report, abort or hang; a status the protocol does not allow for that call; a reply that fails its own schema `validate()`; a too-small render buffer accepted |
| `protocol-cs` | C# IPC decoders, broker side (`ProtocolCodec`): the same four decoders and the same invariants | mutational (`MutationalFuzzer`) | **outcome signature only, no coverage** | any exception that escapes, a hang over the timeout, a process crash (stack overflow, out of memory), a broken invariant |
| `pdfwriter-parse` | `MikePDF.PdfWriter`: strict open, lenient open, `StructureValidator`, object and stream access | mutational | outcome signature only | as above, plus strict-accepts-but-lenient-refuses, a refused file reported valid, a limit exceeded |
| `pdfwriter-append` | `MikePDF.PdfWriter` incremental writer: parse, rewrite up to three objects, append, reopen | mutational | outcome signature only | as above, plus the writer's own self-check failing on a strictly valid base, or the input not being an exact prefix of the output |

Read these limits before reading any result:

1. **The two C# target families are not coverage-guided.** The SharpFuzz instrumenter and a libFuzzer driver need an accepted ADR first ([ADR-0017](adr/0017-fuzzing-toolchain.md), Proposed). Until then they run under `tests/fuzz/Shared/MutationalFuzzer.cs`: a havoc mutator with a persisted corpus, a dictionary, a watchdog and a restarting supervisor. An input is kept when it produces a decoder verdict or typed error not seen before. That is weaker than coverage guidance, and it will plateau earlier. The SharpFuzz entry points are still in the harnesses for when the ADR is accepted.
2. **`engine-document` does not fuzz PDFium.** The pinned PDFium is a prebuilt, uninstrumented DLL (ADR-0002). libFuzzer gets no coverage feedback from inside it, and AddressSanitizer cannot see its heap. The target tests MikePDF's wrapper around it and the limits the wrapper enforces. Fuzzing PDFium proper needs a PDFium built from source with ASan and coverage, which is the self-hosted build machine in R-19. Until then the protection against PDFium bugs is the sandbox (ADR-0004), not this fuzzer.
3. **The engine's request path is not fuzzed.** `EngineCore::execute` (handles, slot ring, the document table) is reached by the integration tests, not by a fuzzer. The decoders that feed it are fuzzed.
4. **x64 only.** MSVC's libFuzzer and ASan runtimes ship for x64. ARM64 builds are covered by the unit and integration tests, not by fuzzing.
5. **The seeds are small.** The PDF seeds are the 21 PDFium and integration fixtures (about 160 KB). There is no corpus of large real-world files yet (ADR-0010 is Proposed).

## Running them

Needs Windows 11 x64, the .NET SDK from `global.json`, and Visual Studio or Build Tools with the C++ workload **and the "C++ AddressSanitizer" component** (it carries `clang_rt.fuzzer_*` and `clang_rt.asan_dynamic-x86_64.dll`). CMake is found the same way `build.ps1` finds it.

```powershell
./tests/fuzz/run-fuzz.ps1 -Target all -Seconds 60                    # the CI smoke: all five targets, 60 s each
./tests/fuzz/run-fuzz.ps1 -Target protocol-cpp -Seconds 900          # one target for 15 minutes
./tests/fuzz/run-fuzz.ps1 -Target engine-document -Seconds 3600 -Jobs 4   # four processes sharing one corpus
```

The script builds what it needs, collects the seeds (`tests/MikePDF.Protocol.Tests/golden` as frames and as bare bodies; the PDF fixtures), starts each target with its dictionary (`tests/fuzz/dictionaries`), and writes `build/out/fuzz/summary.md` and `summary.json`. Its exit code is 0 for no finding, 1 for a finding, 2 when the run could not be set up. Reproducers land in `build/out/fuzz/findings/<target>/`, the corpus in `build/out/fuzz/corpus/<target>/` (pass `-CorpusRoot` to keep it elsewhere; it is the fuzzer's memory, so keep it between runs).

Each target by hand:

| Target | Build | Run, replay and minimise |
|---|---|---|
| `protocol-cpp`, `engine-document` | `cmake -S . -B build/out/fuzz-x64 -A x64 -DMIKEPDF_PDFIUM_DIR=<dir> -DMIKEPDF_BUILD_TESTS=OFF -DMIKEPDF_BUILD_FUZZERS=ON` then `cmake --build build/out/fuzz-x64 --config Release --target fuzz_protocol fuzz_engine_document` | `fuzz_protocol.exe -max_len=65540 -dict=tests/fuzz/dictionaries/protocol.dict <corpus-dir> <seed-dir>`. Replay: `fuzz_protocol.exe <file>`. Minimise: `fuzz_protocol.exe -minimize_crash=1 -runs=100000 <file>`. `MIKEPDF_FUZZ_TRACE=1` makes `fuzz_engine_document` print where its time went |
| `protocol-cs` | `dotnet build tests/fuzz/MikePDF.Protocol.Fuzz -c Release` | `MikePDF.Protocol.Fuzz.exe --mutate 900 --corpus <dir> --seeds <dir> --dict tests/fuzz/dictionaries/protocol.dict --findings <dir>`. Replay: `MikePDF.Protocol.Fuzz.exe <file>`. Minimise: `... --minimize <file>` (writes `<file>.min`) |
| `pdfwriter-parse`, `pdfwriter-append` | `dotnet build tests/fuzz/MikePDF.PdfWriter.Fuzz -c Release` | `MikePDF.PdfWriter.Fuzz.exe parse --mutate 900 ...` (same flags). Replay: `... parse --replay <file>`. Minimise: `... parse --minimize <file>` |

Native crashes print an AddressSanitizer report and write `crash-<sha1>`, `timeout-<sha1>` or `oom-<sha1>`. The C# supervisor writes `crash-<hash>` or `timeout-<hash>` plus a `.txt` with the exception, and a stack overflow or out-of-memory exit (which no `catch` can see) is recorded the same way from the input it mirrors into a memory-mapped file before each run.

**Proof that the harness can find things.** `MikePDF.Fuzz.Tests.HarnessSelfTests` starts the real harness against a target planted to throw, to hang and to overflow the stack once an input begins with `FZZ`, and asserts that each is found, exits with 1 and leaves a reproducer. `MutatorTests` covers the mutator, the dictionary reader (and checks that both shipped dictionaries parse completely and stay under libFuzzer's 128-byte limit) and the minimiser.

## How coverage was measured

- **Native targets.** MSVC's libFuzzer prints `ft:` (features: each instrumented edge at each hit-count bucket, plus value-profile features) but no `cov:` line, because MSVC emits no PC table, so there is **no covered-edge percentage** for `protocol-cpp` and `engine-document`. The summary reports `ft` and the number of instrumented edges (`inline 8-bit counters`). `ft` growing is the signal that the fuzzer is still finding new behaviour; a plateau is the signal to stop trusting more hours on that corpus. No line coverage is measured for the C++ code.
- **C# targets.** Line coverage of the replayed inputs, using the code-coverage extension already referenced by every test project:

  ```powershell
  $env:MIKEPDF_FUZZ_CORPUS_ROOT = "<CorpusRoot>"      # holds protocol-cs, pdfwriter-parse, pdfwriter-append; omit it for the seeds alone
  dotnet test --project tests/fuzz/MikePDF.Fuzz.Tests -c Release --filter-class "*CorpusReplayTests" -- --coverage --coverage-output-format cobertura --coverage-output seeds.xml
  ```

  The percentage is `lines with hits > 0 / lines` over the `MikePDF.Protocol` and `MikePDF.PdfWriter` packages of the report.

## Results of the local runs (2026-11-02)

DEV machine (Windows 11 x64, 24 logical cores shared with other agents' builds), the five targets running at the same time, 15 minutes of wall-clock each, the corpus starting empty. "Executions" sums all processes of the target.

| Target | Processes | Executions | Native `ft` (instrumented edges) | Corpus files kept | Findings |
|---|---|---|---|---|---|
| `protocol-cpp` | 2 | 13,609,136 | 13,603 (7,352) | 11,131 | 0 |
| `engine-document` | 4 | 115,320 | 9,597 (9,536) | 1,943 | 0 |
| `protocol-cs` | 2 | 192,531,819 | n/a | 4,930 | 0 |
| `pdfwriter-parse` | 2 | 11,699,831 | n/a | 3,098 | 0 |
| `pdfwriter-append` | 2 | 20,478,331 | n/a | 41 | 1 bug, found twice in the first minutes (below) |
| `pdfwriter-append`, rerun for 15 minutes after the fix | 2 | 137,453,820 | n/a | 41 | 0 |

The first `pdfwriter-append` run is short of executions because the worker was restarted after each of its two hits of the bug; the rerun is the clean 15 minutes. Its 41 corpus files show that the outcome signature the target returned was nearly uninformative (most mutations are rejected by the strict parser, and a valid one was told apart only by how many objects it rewrote). After that run the signature was enriched with the size of the file, of its xref and the xref form; two minutes of that version kept 74 files. It was **not** re-run for 15 minutes.

The reported `ft` is the largest of the processes, and with `-jobs` the processes reload each other's finds, so it is not the sum. `engine-document` runs at about 130 executions a second across four processes; most of that time is PDFium parsing and loading (`MIKEPDF_FUZZ_TRACE=1` on a 30-second run put about half of it in open, a quarter in info, page sizes and the outline, and under a tenth in rendering), so the target is slow because PDFium is, not because of the wrapper.

Line coverage of the .NET code under test (the replay described above):

| Package | Seeds alone | Seeds plus the corpus above |
|---|---|---|
| `MikePDF.Protocol` | 1,115 / 1,124 lines (99.2%) | 1,115 / 1,124 (99.2%) |
| `MikePDF.PdfWriter` | 1,377 / 1,789 lines (77.0%) | 1,538 / 1,789 (86.0%) |

The golden vectors already reach all but nine lines of the protocol package (default branches in the generated dispatch, enums and text helpers), so the fuzzer had nothing new to reach there; that is a statement about the decoders being well covered by the golden vectors, not evidence of more depth. In the writer the corpus reached 161 more lines than the fixtures.

### Findings

| # | Target | What | Owner | State |
|---|---|---|---|---|
| F-1 | `pdfwriter-append` | `PdfSerializer.FormatReal` cut reals to 15 fractional digits, so any object holding e.g. `841.8897637795277` or `1e-19` was rewritten with a different number, and the writer's own read-back self-check then refused the whole update (so Save would have failed on a document with such a page box). Fail-safe: it refused, it did not write a wrong file. | MikePDF (`MikePDF.PdfWriter`) | **Fixed** in the same branch. Exact round-trip formatting; reproducer `tests/fuzz/regressions/pdfwriter/tiny-real-append-selfcheck.pdf`; unit tests in `SerializerTests` and `IncrementalUpdateTests` that fail without the change |

Observations that are not findings:

- PDFium opens a document whose page tree has no pages, and the wrapper reports `pageCount` 0. The schema allows it, and `DocumentLayout` and `ViewportController` already handle zero pages. The `engine-document` target accepts it.
- The engine refuses `RenderTile` with the form-fields flag as `Unsupported` ("no form-fill environment until forms arrive"). The target asks for it now and then to keep that refusal covered.
- No crash has been seen inside `pdfium.dll` in these runs. Given limit 2, that is weak evidence.

## Triage

1. **A finding fails the job.** Download the artifact `fuzz-findings-<target>-<run>` (kept 90 days), or take the files from `build/out/fuzz/findings/<target>/`. Do not paste an input into a public issue before step 4: it may contain parts of the fixtures, or trigger a PDFium bug that has not been reported.
2. **Reproduce.** Native: `fuzz_<target>.exe <file>`. C#: `--replay <file>` (always against a plain build, never an instrumented one). If it does not reproduce, it is a flake or a state-dependent bug: rerun with `-runs=` for the native targets, and read the `.txt` and the log for what the process had done before.
3. **Minimise.** `-minimize_crash=1` (native) or `--minimize` (C#). Keep the original as well.
4. **Classify by the top frames.**
   - **MikePDF code** (frames in `src/` or `tests/fuzz/` code, or in our `.cpp` wrappers): this is ours. Fix it, with the minimised input committed to `tests/fuzz/regressions/<folder>/` and a test that fails without the fix, and add a row to the table above and to `regressions/README.md`. Severity: memory-safety in code that parses untrusted bytes in the UI process is **Critical**; in the engine, **High** (the sandbox contains it); an exception, a refusal or a failed invariant is **Medium** or **Low** depending on what the user loses.
   - **Inside `pdfium.dll`** (an access violation or a stack in the DLL, with a MikePDF frame only as the caller): this is PDFium's. Do not "fix" it by catching the fault or by adding a guard that hides it. Record the PDFium version (`build/pdfium.lock`), the input's SHA-256 and the stack in the PDFium findings log below; confirm whether the sandboxed engine contains it (the engine process dies, the supervisor restarts it, `EngineCrashed` reaches the broker: `Integration.Tests/RecoveryTests.cs` exercises that path); check whether a newer PDFium build already fixes it; and report it upstream under the Chromium security process, keeping the input private until it is fixed. If the crash is reachable from a release feature and there is no fix, it blocks release until Opus decides otherwise.
   - **In a library MikePDF uses** (MessagePack, the CRT): report upstream, and note the pinned version.
5. **De-duplicate.** Two findings are one when the exception type and the top three frames match (C#: the supervisor does this per process), or the AddressSanitizer summary line and top frames match (native). "No new unique crash" for the release gate means no finding with a signature not already triaged.
6. **Close.** The finding is closed when the regression test is in `main` and the next nightly run has not reproduced it.

### PDFium findings log

None yet. (None have been seen; the target cannot see inside PDFium. See limit 2.)

## CI

- **Every push and pull request:** the `fuzz-smoke` job in `.github/workflows/ci.yml` runs `run-fuzz.ps1 -Target all -Seconds 60` on the x64 runner. It proves the targets build, start, accept their seeds and find nothing in a minute. It is not fuzzing in any useful sense. A finding fails the job and the reproducers are uploaded.
  Evidence that it works on the hosted image: on the first push of this branch (CI run 41417004801) the job built the native targets with the runner's MSVC and ran all five for 60 seconds: `protocol-cpp` 963,686 executions, `engine-document` 4,254, `protocol-cs` 7,957,541, `pdfwriter-parse` 705,493, `pdfwriter-append` 3,565,205, no findings, about six minutes in all. So the `windows-2025-vs2026` image does carry the AddressSanitizer component and libFuzzer.
- **Every night (02:17 UTC):** `.github/workflows/fuzz.yml` runs one 300-minute slice per target, as a five-job matrix, three processes each. The corpus of the previous night is restored from the Actions cache (newest key for the target), extended, and saved again; it is also uploaded as an artifact for 14 days, because cache entries are evicted after seven days without use. A finding fails the job and uploads the reproducers and logs for 90 days. The step summary shows the table above.
- **By hand:** `workflow_dispatch` with `minutes` (at most 300), `targets` (a list or `all`) and `processes` (at most 8).
- **The tests** (`MikePDF.Fuzz.Tests`) run in the normal `dotnet test` of both architectures: the harness self-tests, the mutator tests, and the replay of every regression and every seed.

### Verifying the scheduled workflow

Scheduled workflows run only from the default branch, and a workflow cannot be started by hand until its file exists there, so `fuzz.yml` cannot be proven by a scheduled run before the merge. What was done instead on the branch is in the completion note (the file was exercised on the branch with a temporary push trigger and a 2-minute slice, then the trigger was removed). After the merge:

1. `gh workflow run fuzz.yml -f minutes=5 -f targets=all -f processes=2` and watch it with `gh run watch`. Expect five green jobs, a table in each job summary, and five `fuzz-corpus-*` artifacts.
2. Run it a second time and check the log of the "Restore corpus" step: it should say it restored `fuzz-corpus-<target>-<previous run id>`. That proves the corpus carries over. `gh cache list --key fuzz-corpus` lists the entries.
3. The next morning, `gh run list --workflow fuzz.yml --event schedule` must show a run that started near 02:17 UTC. GitHub disables scheduled workflows in a repository with no activity for 60 days, and delays them under load, so a missed night is not necessarily a bug, but two in a row is.
4. If the Visual Studio image lacks the AddressSanitizer component, the native jobs fail at the build step with `clang_rt.asan_dynamic-x86_64.dll not found`; the C# jobs still run.

## Plan for the 24-hour and 72-hour runs

Terms. A **fuzz hour** is one hour of one fuzzer process on one target. A **wall-clock hour** is an hour of calendar time with a target running (any number of processes). The gates read: M1 (§17) "fuzzers have run 24 hours; IPC decoder fuzzed on both sides"; release (§0.3, §12.6) "72-hour run with no new unique crashes in MikePDF-owned code, every PDFium crash contained by the sandbox". Neither says whether hours may be added up across runs, whether they are per target, or whether processes count. **That needs a decision from Opus or the user**; the plan gives both readings.

| Plan | What it is | Wall-clock per target | Fuzz hours per target | Calendar time | Cost |
|---|---|---|---|---|---|
| **A. Nightly slices on hosted runners** | `fuzz.yml` as merged: 5 jobs a night, 300 minutes, 3 processes, the corpus carried over | 5 h a night | 15 a night | 5 nights for 25 wall-clock hours (the 24-hour gate); 15 nights for 75 (the 72-hour gate) | **No charge while the repository is public and the runner label is a standard hosted runner** (hosted minutes are free there; confirm the `windows-2025-vs2026` label is not a larger, billed runner). Private, or on a larger runner, it is 25 job-hours of Windows minutes a night: check GitHub's current rate |
| **B. One continuous run on a dedicated machine** | one Windows x64 machine with 16 vCPU and 32 GB, the five targets running together, about three processes each | 24 h or 72 h, unbroken | 72 or 216 | 1 or 3 days | The machine for 24 or 72 hours. **Assumption, not a quote:** an on-demand 16 vCPU Windows VM costs roughly US$1 to 2 an hour, so about US$25 to 50 for 24 h and US$75 to 150 for 72 h. Cheaper if preemptible, but a preemption breaks "unbroken". A machine the user already owns costs nothing and is busy for the duration |
| **C. B, plus a PDFium built with ASan and coverage** | the R-19 self-hosted build machine builds PDFium from source instrumented; `engine-document` then fuzzes PDFium itself | as B | as B | one-off build, then as B | The one-off build is hours on 16 vCPU (not measured). It is the only plan that fuzzes PDFium's own code coverage-guided |

What each can and cannot prove:

- **Plan A proves** that after N nights of accumulated corpus nothing new was found in code that changes nightly. It does **not** prove a continuous 72-hour run: the process restarts every night (so slow leaks, memory growth and long-run instability are not tested), and the code under test moves between nights unless the branch is frozen. For the release gate, freeze the release-candidate commit and dispatch the workflow against it for 15 consecutive nights, and say in the release decision that the 72 hours are cumulative.
- **Plan B proves** the literal reading of the gate, and is what the release should use unless Opus accepts cumulative hours. It needs the machine only for three days.
- **Hosted runners also can't prove:** anything about ARM64; anything inside PDFium (limit 2); behaviour on the reference hardware; and anything beyond the GitHub cache's guarantee about the corpus (hence the artifact copy). Nightly times also vary with the shared host, so executions per night are not a stable number to gate on; gate on hours run and findings.
- **No plan makes the C# targets coverage-guided.** That needs ADR-0017.

Recommendation. Merge, then let Plan A run: it meets the M1 "24 hours" gate on the cumulative reading after five nights, at no cost, with no decision needed except that reading. Accept ADR-0017 before the M5 run. For the 72-hour release gate, rent or borrow a machine for Plan B on the frozen release candidate, and take Plan C only if the user wants PDFium itself fuzzed (R-19's budget decision).

## Not done

- No fuzz target for `EngineCore::execute` (the request path past the decoders), for the broker's reply validators (`ReplyChecks`), or for the signature parser (it does not exist yet).
- No structure-aware mutation of MessagePack or PDF; the byte-level mutators with dictionaries do the work.
- No corpus minimisation job (`-merge=1`); the nightly corpus only grows. Add it when the cache approaches its size limit.
- ARM64, and any fuzzing of PDFium's own code.
