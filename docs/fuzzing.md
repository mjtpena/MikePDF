# Fuzzing

- Status: targets run locally and in CI; the C# targets are coverage-guided since ADR-0017 was accepted. The M1 "fuzzers have run 24 hours" gate is **not yet met**: the plan to meet it is below
- Owner: W1-fuzz (Sonnet), reviewed by Opus
- Spec: master prompt §12.6 and §13, [ADR-0005](adr/0005-ipc-transport.md) rule 7, [ADR-0008](adr/0008-save-pipeline.md), [R-19](risks.md), [threat model](threat-model/README.md) B1 and B2. Toolchain: [ADR-0017](adr/0017-fuzzing-toolchain.md) (Accepted)

## What is fuzzed, and how well

| Target | Code under test | Engine | Guided by | What counts as a finding |
|---|---|---|---|---|
| `protocol-cpp` | C++ IPC decoders, engine side (`msgpack.cpp`, `codec.cpp`, generated `messages.cpp`): all four request and response decoders, whole frame and bare body | libFuzzer + AddressSanitizer (MSVC) | **coverage** (`ft:` features) and value profile | any sanitizer report, abort, hang over 30 s; a decoded message that cannot be re-encoded or whose encoding is not a fixed point |
| `engine-document` | `mikepdf::pdfium::Document`: open (with and without a password), info, page sizes, outline, page boxes, links, link addresses, page text, search, `render_tile` over scales 0.01 to 64, rotations, tile origins at the schema edges, and a too-small buffer | libFuzzer + AddressSanitizer (MSVC) | coverage of **MikePDF's wrapper only** | any sanitizer report, abort or hang; a status the protocol does not allow for that call; a reply that fails its own schema `validate()`; a too-small render buffer accepted |
| `protocol-cs` | C# IPC decoders, broker side (`ProtocolCodec`): the same four decoders and the same invariants | libFuzzer + SharpFuzz (MSVC libFuzzer, vendored libfuzzer-dotnet driver); `MutationalFuzzer` is the fallback | **edge coverage of `MikePDF.Protocol.dll`** (`ft:`); the fallback: outcome signature only | any exception that escapes, a hang over the timeout, a process crash (stack overflow, out of memory), a broken invariant |
| `pdfwriter-parse` | `MikePDF.PdfWriter`: strict open, lenient open, `StructureValidator`, object and stream access | libFuzzer + SharpFuzz; mutational fallback | **edge coverage of `MikePDF.PdfWriter.dll`** | as above, plus strict-accepts-but-lenient-refuses, a refused file reported valid, a limit exceeded |
| `pdfwriter-append` | `MikePDF.PdfWriter` incremental writer: parse, rewrite up to three objects, append, reopen | libFuzzer + SharpFuzz; mutational fallback | **edge coverage of `MikePDF.PdfWriter.dll`** | as above, plus the writer's own self-check failing on a strictly valid base, or the input not being an exact prefix of the output |
| `shell-reply-cpp` | The Explorer handler's view of a ShellHost reply (`MikePDF.ShellExt`: the generated response decoders of `ShellOpen`, `ShellRenderPage` and `ShellClose`, and `check_render_reply` / `check_open_reply`, the checks it makes before it builds a bitmap), on an 8-byte context (the request's box, the pixel section's size) plus a body; the whole input is also tried as a frame | libFuzzer + AddressSanitizer (MSVC) | coverage of the handler's validation and the protocol codec (compiled in) | any sanitizer report, abort or hang; an accepted reply whose width or height is 0 or above the request, whose stride is not 4 x width, or whose stride x height (64-bit) is outside the section (SHELL-18, SHELL-11) |
| `shell-request-cs` | ShellHost's view of a handler's request (`ProtocolCodec` request decoders and `ShellRequestRules`: the allow-list after Hello, the Hello check, the budget clamp, the pixel-section rule), whole frame and bare body | libFuzzer + SharpFuzz; mutational fallback | **edge coverage of `MikePDF.Protocol.dll` and `MikePDF.ShellHost.dll`** | as `protocol-cs`, plus a rule that disagrees with an independent 128-bit computation, a budget outside 1 ms to 5 s, an allow-list that changed (SHELL-18) |

Read these limits before reading any result:

1. **The C# targets are coverage-guided only inside `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll`** ([ADR-0017](adr/0017-fuzzing-toolchain.md)). `MessagePack.dll` and the framework are not instrumented, and SharpFuzz reports edges only (no compare tracing, no value profile). The fallback, `-CSharpMode mutational`, is `tests/fuzz/Shared/MutationalFuzzer.cs`: a havoc mutator whose only novelty signal is the outcome signature, so it plateaus earlier (ADR-0017 has the 10-minute comparison). It is never used silently: the script exits 2 if the coverage-guided pipeline cannot be set up.
2. **`engine-document` does not fuzz PDFium.** The pinned PDFium is a prebuilt, uninstrumented DLL (ADR-0002). libFuzzer gets no coverage feedback from inside it, and AddressSanitizer cannot see its heap. The target tests MikePDF's wrapper around it and the limits the wrapper enforces. Fuzzing PDFium proper needs a PDFium built from source with ASan and coverage, which is the self-hosted build machine in R-19. Until then the protection against PDFium bugs is the sandbox (ADR-0004), not this fuzzer.
3. **The engine's request path is not fuzzed.** `EngineCore::execute` (handles, slot ring, the document table) is reached by the integration tests, not by a fuzzer. The decoders that feed it are fuzzed.
4. **x64 only.** MSVC's libFuzzer and ASan runtimes ship for x64. ARM64 builds are covered by the unit and integration tests, not by fuzzing.
5. **The two shell targets are new** (`shell-reply-cpp`, `shell-request-cs`, SHELL-18). They have the 60 s smoke on every push and their seeds are replayed on every build (`fuzz_seed_tests.cpp`, `CorpusReplayTests`), with every truncation and byte flip of every seed; they have no fuzz hours yet, and no hour of theirs counts toward the 24-hour or 72-hour plans below until a nightly slice has run them. They test the validation and the decoders, not the handler's COM code or ShellHost's engine handling.
5. **The seeds are small.** The PDF seeds are the 21 PDFium and integration fixtures (about 160 KB). There is no corpus of large real-world files yet (ADR-0010 is Proposed).

## Running them

Needs Windows 11 x64, the .NET SDK from `global.json`, and Visual Studio or Build Tools with the C++ workload **and the "C++ AddressSanitizer" component** (it carries `clang_rt.fuzzer_*` and `clang_rt.asan_dynamic-x86_64.dll`). CMake is found the same way `build.ps1` finds it.

```powershell
./tests/fuzz/run-fuzz.ps1 -Target all -Seconds 60                    # the CI smoke: all seven targets, 60 s each
./tests/fuzz/run-fuzz.ps1 -Target protocol-cpp -Seconds 900          # one target for 15 minutes
./tests/fuzz/run-fuzz.ps1 -Target engine-document -Seconds 3600 -Jobs 4   # four processes sharing one corpus
./tests/fuzz/run-fuzz.ps1 -Target pdfwriter-parse -Seconds 900 -CSharpMode mutational   # the C# fallback, no C++ toolchain needed
./tests/fuzz/run-fuzz.ps1 -SelfTest                                  # prove the coverage-guided C# pipeline finds a planted throw, hang and stack overflow
```

For the C# targets the script also builds the libFuzzer driver (`tests/fuzz/libfuzzer-dotnet`, vendored from Metalnem/libfuzzer-dotnet, MSVC libFuzzer), restores the local tool `SharpFuzz.CommandLine` (`tests/fuzz/.config/dotnet-tools.json`, SHA-512 checked) and instruments **copies** of `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll` under `build/out/fuzz-bin/*-cov`. The script builds what it needs, collects the seeds (`tests/MikePDF.Protocol.Tests/golden` as frames and as bare bodies; the PDF fixtures), starts each target with its dictionary (`tests/fuzz/dictionaries`), and writes `build/out/fuzz/summary.md` and `summary.json`. Its exit code is 0 for no finding, 1 for a finding, 2 when the run could not be set up. Reproducers land in `build/out/fuzz/findings/<target>/`, the corpus in `build/out/fuzz/corpus/<target>/` (pass `-CorpusRoot` to keep it elsewhere; it is the fuzzer's memory, so keep it between runs).

Each target by hand:

| Target | Build | Run, replay and minimise |
|---|---|---|
| `protocol-cpp`, `engine-document` | `cmake -S . -B build/out/fuzz-x64 -A x64 -DMIKEPDF_PDFIUM_DIR=<dir> -DMIKEPDF_BUILD_TESTS=OFF -DMIKEPDF_BUILD_FUZZERS=ON` then `cmake --build build/out/fuzz-x64 --config Release --target fuzz_protocol fuzz_engine_document` | `fuzz_protocol.exe -max_len=65540 -dict=tests/fuzz/dictionaries/protocol.dict <corpus-dir> <seed-dir>`. Replay: `fuzz_protocol.exe <file>`. Minimise: `fuzz_protocol.exe -minimize_crash=1 -runs=100000 <file>`. `MIKEPDF_FUZZ_TRACE=1` makes `fuzz_engine_document` print where its time went |
| `protocol-cs` (guided) | `dotnet build tests/fuzz/MikePDF.Protocol.Fuzz -c Release -o build/out/fuzz-bin/protocol`, copy to `protocol-cov`, `dotnet sharpfuzz` (from `tests/fuzz`) on `protocol-cov/MikePDF.Protocol.dll`, and the driver (`cmake -S tests/fuzz/libfuzzer-dotnet -B build/out/fuzz-driver -A x64`, then `cmake --build build/out/fuzz-driver --config Release`; the full commands are in `tests/fuzz/MikePDF.Protocol.Fuzz/README.md`) | `libfuzzer_dotnet.exe "--target_path=\"<protocol-cov>\MikePDF.Protocol.Fuzz.exe\" --libfuzzer" -max_len=65540 -timeout=20 -dict=... <corpus> <seeds>`. Replay against the **plain** build: `MikePDF.Protocol.Fuzz.exe <file>`. Minimise: `MikePDF.Protocol.Fuzz.exe --minimize <file>` |
| `protocol-cs` (fallback) | `dotnet build tests/fuzz/MikePDF.Protocol.Fuzz -c Release` | `MikePDF.Protocol.Fuzz.exe --mutate 900 --corpus <dir> --seeds <dir> --dict tests/fuzz/dictionaries/protocol.dict --findings <dir>`. Replay: `MikePDF.Protocol.Fuzz.exe <file>`. Minimise: `... --minimize <file>` (writes `<file>.min`) |
| `pdfwriter-parse`, `pdfwriter-append` | as `protocol-cs`, with `MikePDF.PdfWriter.Fuzz` and `MikePDF.PdfWriter.dll` (`tests/fuzz/MikePDF.PdfWriter.Fuzz/README.md`); the guided harness arguments are `parse --libfuzzer` or `append --libfuzzer` | Fallback: `MikePDF.PdfWriter.Fuzz.exe parse --mutate 900 ...` (same flags). Replay: `... parse --replay <file>`. Minimise: `... parse --minimize <file>` |

Native crashes print an AddressSanitizer report and write `crash-<sha1>`, `timeout-<sha1>` or `oom-<sha1>`. A guided C# finding is a libFuzzer artifact with the same names (an escaped exception or stack overflow in the harness makes the driver abort; the managed stack is in the log). The fallback's supervisor writes `crash-<hash>` or `timeout-<hash>` plus a `.txt` with the exception. In the fallback a stack overflow or out-of-memory exit (which no `catch` can see) is recorded the same way from the input it mirrors into a memory-mapped file before each run.

**Proof that the harness can find things.** For the coverage-guided pipeline: `run-fuzz.ps1 -SelfTest` (also the first fuzz step of every CI push) starts the driver on a target planted to throw, hang and overflow the stack when an input begins with `FZZ`, seeded with `FZZ`, and requires all three to end in a libFuzzer artifact. It proves detection and reporting, not search (the planted code is not instrumented). For the fallback: `MikePDF.Fuzz.Tests.HarnessSelfTests` starts the real harness against a target planted to throw, to hang and to overflow the stack once an input begins with `FZZ`, and asserts that each is found, exits with 1 and leaves a reproducer. `MutatorTests` covers the mutator, the dictionary reader (and checks that both shipped dictionaries parse completely and stay under libFuzzer's 128-byte limit) and the minimiser.

## How coverage was measured

- **Native targets.** MSVC's libFuzzer prints `ft:` (features: each instrumented edge at each hit-count bucket, plus value-profile features) but no `cov:` line, because MSVC emits no PC table, so there is **no covered-edge percentage** for `protocol-cpp` and `engine-document`. The summary reports `ft` and the number of instrumented edges (`inline 8-bit counters`). `ft` growing is the signal that the fuzzer is still finding new behaviour; a plateau is the signal to stop trusting more hours on that corpus. No line coverage is measured for the C++ code.
- **C# targets, guided.** libFuzzer prints `ft:` (instrumented edges of `MikePDF.Protocol.dll` / `MikePDF.PdfWriter.dll` at each hit-count bucket). To compare any corpus (guided or fallback) on that one yardstick, replay it through the instrumented harness: `libfuzzer_dotnet.exe <same --target_path> -runs=0 <corpus> <seeds>` and read `INITED ft:`. As for the native targets there is no covered-edge percentage.
- **C# targets, lines.** Line coverage of the replayed inputs, using the code-coverage extension already referenced by every test project:

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

### Coverage-guided C# runs (2026-11-02, branch `m1/sharpfuzz`, ADR-0017)

Same machine, 960 seconds (16 minutes) of wall-clock per target, two libFuzzer processes sharing one corpus (`-Jobs 2`), the corpus starting empty, the three targets running at the same time. `ft` is libFuzzer's feature count over `MikePDF.Protocol.dll` or `MikePDF.PdfWriter.dll` (edges times hit-count buckets); the largest of the two processes.

| Target | Executions | `ft` | Corpus files | Findings |
|---|---|---|---|---|
| `protocol-cs` | 46,658,561 | 4,237 | 907 | 0 |
| `pdfwriter-parse` | 3,614,607 | 12,084 | 5,597 | 0 |
| `pdfwriter-append` | 6,973,095 | 11,364 | 4,377 | 0 |

No crash, hang or broken invariant in MikePDF code. That is **weak evidence**, for the reasons in limit 1: 16 minutes, and only the two MikePDF assemblies give feedback. The 10-minute comparison against the mutational fuzzer, with its `ft` and line-coverage numbers per target, is in ADR-0017 (summary: more `ft` on all three targets, and `MikePDF.PdfWriter` line coverage of the replayed corpus 90.8% against 85.9% for the mutational corpus and 76.9% for the seeds alone; `MikePDF.Protocol` is at 99.2% for all three). The first attempt at this run hung on both PdfWriter targets: libFuzzer's `-jobs` workers were started with the harness word lost (the driver's argument is re-joined without quoting), so they died at start and were restarted for ever. `run-fuzz.ps1` now passes the word through `--target_arg`, and kills a fuzzer that is still alive 10 minutes after its time is up. That bug would have hung the nightly job (3 processes) until the CI limit.

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

- **Every push and pull request:** the `fuzz-smoke` job in `.github/workflows/ci.yml` runs `run-fuzz.ps1 -SelfTest` and then `run-fuzz.ps1 -Target all -Seconds 60` on the x64 runner. It proves the targets build, start, accept their seeds and find nothing in a minute. It is not fuzzing in any useful sense. A finding fails the job and the reproducers are uploaded.
  Evidence that it works on the hosted image: on the first push of this branch (CI run 41417004801) the job built the native targets with the runner's MSVC and ran all five for 60 seconds: `protocol-cpp` 963,686 executions, `engine-document` 4,254, `protocol-cs` 7,957,541, `pdfwriter-parse` 705,493, `pdfwriter-append` 3,565,205, no findings, about six minutes in all. So the `windows-2025-vs2026` image does carry the AddressSanitizer component and libFuzzer.
- **Every night (02:17 UTC):** `.github/workflows/fuzz.yml` runs one 300-minute slice per target, as a five-job matrix, three processes each. The corpus of the previous night is restored from the Actions cache (newest key for the target), extended, and saved again; it is also uploaded as an artifact for 14 days, because cache entries are evicted after seven days without use. A finding fails the job and uploads the reproducers and logs for 90 days. The step summary shows the table above.
- **By hand:** `workflow_dispatch` with `minutes` (at most 300), `targets` (a list or `all`) and `processes` (at most 8).
- **The tests** (`MikePDF.Fuzz.Tests`) run in the normal `dotnet test` of both architectures: the harness self-tests, the mutator tests, and the replay of every regression and every seed.

### Verifying the scheduled workflow

On 2026-11-02 `fuzz.yml` with the coverage-guided C# targets was exercised on `m1/sharpfuzz` by the method described below (temporary push trigger, 2-minute slice, removed again; run 41437898455): all five target jobs green, the C# jobs ran 3 libFuzzer processes with the SharpFuzz driver on the hosted image and restored the previous night's mutational corpora as seeds. The `schedule` trigger and the 300-minute slice remain unproven.

Scheduled workflows run only from the default branch, and a workflow cannot be started by hand until its file exists there, so `fuzz.yml` cannot be proven by a scheduled run before the merge. What was done instead, on 2026-11-02: the file was exercised on the branch `m1/fuzzing` with a temporary `push` trigger and a 2-minute slice (commits "TEMPORARY: run fuzz.yml on pushes..." and its removal). Two runs, 41418922800 and 41419555138, each had a green `plan` job and five green target jobs; the second run's "Restore corpus" step reported `Cache restored from key: fuzz-corpus-<target>-41418922800` for all five targets, and the corpus file counts grew from the first run to the second (for example `protocol-cpp` 8,017 to 10,352). That proves the matrix, the cache carry-over, the step summary and the artifact uploads. It did **not** prove the `schedule` trigger, the 300-minute slice (the longest slice run was 2 minutes), or the upload of findings (no finding occurred; the `if: failure()` path is unexercised). After the merge:

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
- **The C# targets are coverage-guided on every plan** (ADR-0017, Accepted), but only inside the two MikePDF assemblies, and a 72-hour run of the child-process driver has not been tried.

Recommendation. Merge, then let Plan A run: it meets the M1 "24 hours" gate on the cumulative reading after five nights, at no cost, with no decision needed except that reading. Accept ADR-0017 before the M5 run. For the 72-hour release gate, rent or borrow a machine for Plan B on the frozen release candidate, and take Plan C only if the user wants PDFium itself fuzzed (R-19's budget decision).

## Not done

- No fuzz target for `EngineCore::execute` (the request path past the decoders), for the broker's reply validators (`ReplyChecks`), or for the signature parser (it does not exist yet).
- No structure-aware mutation of MessagePack or PDF; the byte-level mutators with dictionaries do the work.
- No corpus minimisation job (`-merge=1`); the nightly corpus only grows. Add it when the cache approaches its size limit.
- ARM64, and any fuzzing of PDFium's own code.
