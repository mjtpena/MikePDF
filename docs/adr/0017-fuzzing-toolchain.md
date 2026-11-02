# ADR-0017: Fuzzing toolchain for the .NET code
- Status: Accepted (2026-11-02, after the spike below)
- Date: 2026-11-02
- Deciders: Opus (approver), Sonnet (proposer, WP W1-fuzz; spike and implementation by WP `m1/sharpfuzz`)

## Context
Master prompt §12.6 and ADR-0005 rule 7 call for SharpFuzz targets on the C# protocol decoder and on `MikePDF.PdfWriter`, and libFuzzer on the C++ side. The C++ side needed nothing new: MSVC ships libFuzzer and AddressSanitizer in the "C++ AddressSanitizer" Visual Studio component, and the native targets are built from source with them (`tests/fuzz/CMakeLists.txt`).

For the .NET side, the `SharpFuzz` library (MIT) is already a central package version. The library alone does not fuzz anything. A coverage-guided run also needs:
1. **An instrumenter.** The `SharpFuzz.CommandLine` .NET tool rewrites an assembly so that it records edge coverage. (The first draft of this ADR said it is built on Mono.Cecil. It is built on **dnlib**, MIT, which is also a transitive dependency of the `SharpFuzz` package that the harnesses already reference and is in their lock files.)
2. **A driver.** `libfuzzer-dotnet` (MIT) is a native libFuzzer front end for an instrumented .NET process. It has a Windows variant, `libfuzzer-dotnet-windows.cc`, written for MSVC's libFuzzer.

Until now the .NET targets ran under a dependency-free mutational fuzzer (`tests/fuzz/Shared/MutationalFuzzer.cs`) with **no coverage feedback**: an input joined the corpus when it yielded a decoder verdict or typed error not seen before.

## Options considered
1. **Stay with the mutational fuzzer.** Nothing new to vet; weaker search.
2. **`SharpFuzz.CommandLine` as a pinned local tool, plus `libfuzzer-dotnet-windows.cc` built with MSVC.** Coverage-guided, MIT throughout.
3. **`SharpFuzz.CommandLine` plus our own in-process driver** that reads SharpFuzz's edge map itself. Not tried: the spike showed option 2 works without maintaining a fuzzer of our own.

## Spike (2026-11-02): which driver, and does coverage guidance work
**Which driver.** The upstream driver's Windows variant works with MSVC's libFuzzer as it is; no clang, clang-cl or other LLVM install is needed. It is one 300-line C++ file (`libfuzzer-dotnet-windows.cc`, Metalnem/libfuzzer-dotnet commit `bd39d4e88d715ab460a929943645be2a186cde52`, last touched 2025-05-02, MIT). The file is **vendored unchanged** in `tests/fuzz/libfuzzer-dotnet/` with its licence and a standalone CMake project that builds it with `cl /fsanitize=fuzzer` (the Visual Studio component already required by the native targets). No prebuilt binary from upstream's releases is run: the source was read (it starts the harness as a child process, passes inputs through shared memory and two pipes, copies SharpFuzz's 64 KiB edge map into libFuzzer's "extra counters" after each input, and calls `abort()` when the child dies or reports a failure), and built here. The hand-off is: libFuzzer (in the driver) -> shared memory -> `SharpFuzz.Fuzzer.LibFuzzer.Run` in the harness's child process -> instrumented `MikePDF.Protocol.dll` / `MikePDF.PdfWriter.dll` -> edge map back.

**What is instrumented.** `dotnet sharpfuzz` is run on a **copy** of the harness folder (`build/out/fuzz-bin/<x>-cov`) and only on `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll`; never on the shipped copies, the harness or MessagePack. Reproducers are replayed against the plain build.

**Planted failures.** `run-fuzz.ps1 -SelfTest` (also run by the `fuzz-smoke` job) starts the driver on a target that throws, hangs and overflows the stack when an input begins with `FZZ`, seeded with `FZZ`. All three are found, each leaves a reproducer (`crash-*`, `timeout-*`, `crash-*`), and the managed exception text is in the log. The unmodified Windows driver reports a managed stack overflow as "SharpFuzz process exited with code 0xc00000fd, aborting". Note what this proves and what it does not: the planted code is in the harness, which is not instrumented, so libFuzzer has no coverage to find `FZZ` from an empty corpus. The self-test proves the **detection and reporting** path, not the search (the comparison below is about the search).

**Coverage guidance versus the mutational fuzzer, 10 minutes per target, one process, empty corpus, same seeds and dictionaries, the six runs at the same time on the same machine (24 logical cores shared with other work), `-Rng 12345`.** Both final corpora were then replayed, with the seeds, through the **instrumented** build with `libfuzzer_dotnet -runs=0`, so that one yardstick measures both. `ft` is libFuzzer's feature count: instrumented edges at each hit-count bucket.

| Target | Executions: guided / mutational | `ft` of the seeds alone | `ft` after guided | `ft` after mutational | Corpus files at 60 s, 5 min, 10 min: guided | mutational |
|---|---|---|---|---|---|---|
| `protocol-cs` | 9.9 M / 84.4 M | 3,117 | **3,902** (+785) | 3,595 (+478) | 258, 307, 321 | 1,689, 2,322, 2,478 |
| `pdfwriter-parse` | 1.96 M / 4.97 M | 5,063 | **11,620** (+6,557) | 9,198 (+4,135) | 986, 1,320, 1,439 | 584, 1,133, 1,441 |
| `pdfwriter-append` | 3.93 M / 17.7 M | 4,662 | **10,994** (+6,332) | 6,842 (+2,180) | 714, 967, 1,049 | 42, 118, 158 |

Corpus file counts are not comparable between the two (the guided one keeps an input only for new coverage; the mutational one keeps any new outcome signature, which for `protocol-cs` is thousands of inputs that reach the same code), which is why `ft` is the measure. The guided runs made 2.5 to 8.5 times fewer executions (the pipe round trip per input, and the instrumentation itself; not separated) and still found 1.6 to 2.9 times as many new features: 1.6 times on `protocol-cs`, 1.6 on `pdfwriter-parse`, 2.9 on `pdfwriter-append`.

Line coverage of the same corpora, the measure this ADR's first draft named: `MikePDF.Fuzz.Tests` `CorpusReplayTests` under the code-coverage extension (percentage of lines with a hit; the numerator and denominator are the cobertura report's line elements for the two packages):

| Package | Seeds alone | Seeds + mutational corpus | Seeds + guided corpus |
|---|---|---|---|
| `MikePDF.PdfWriter` | 76.9% (2,754 / 3,580) | 85.9% (3,074) | **90.8%** (3,250) |
| `MikePDF.Protocol` | 99.2% (2,230 / 2,248) | 99.2% | 99.2% |

So the first verification criterion ("strictly more lines") is **met for `MikePDF.PdfWriter` and not met for `MikePDF.Protocol`**, where the golden vectors already reach 99.2% and neither fuzzer can add a line; there the evidence is the `ft` difference only (edges and hit-count buckets inside the covered lines). That is what the data supports. One caveat on all of it: one 10-minute run per cell, no repeats, so the size of the gaps is indicative, but the ordering held in all three targets and in both measures.

## Decision
**Accepted: option 2.** The C# targets run coverage-guided by default: `SharpFuzz.CommandLine` 2.3.0 as a local tool (`tests/fuzz/.config/dotnet-tools.json`; the manifest pins the version, and `run-fuzz.ps1` also checks the package's SHA-512 against a pinned value after restoring), the vendored `libfuzzer-dotnet-windows.cc` built with MSVC, and the harnesses' `--libfuzzer` child mode. The mutational fuzzer stays as the explicit fallback (`run-fuzz.ps1 -CSharpMode mutational`, and the `csharp_mode` input of `fuzz.yml`), for machines without the Visual Studio C++ AddressSanitizer component. The script never falls back silently: if the coverage-guided pipeline cannot be set up it exits with code 2.

Limits that remain, and are stated in `docs/fuzzing.md`:
- Only `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll` give feedback. `MessagePack.dll` and the framework are not instrumented, so a parse path inside MessagePack is steered by what the MikePDF code does with it, not by its own edges.
- No compare tracing or value profile for .NET: SharpFuzz reports edges only, so a 4-byte magic number that is compared and not branched on is found by the dictionary and by luck, as before.
- Windows x64 only (MSVC's libFuzzer).
- The harness runs in a child process and every input crosses two pipes. The harness crashes are therefore reported by the driver (`abort()` after a broken pipe), not by a signal handler in the harness; the stack is in the log, the input is the libFuzzer artifact.

## Licences (checked 2026-11-02)
All test-only; nothing here ships in the product package, and none of it is linked into a shipped binary.

| Component | Licence | How it is used |
|---|---|---|
| `SharpFuzz` 2.3.0 library | MIT (package metadata and repository) | already referenced by the two harnesses; in their lock files |
| `SharpFuzz.Common` 2.2.0 | MIT | transitive, same |
| `SharpFuzz.CommandLine` 2.3.0 | MIT (package `license` expression `MIT`) | local tool, instruments a copy of two assemblies; package SHA-512 `G/7VoIh0eqKYdGM9GZJqM+twCotc9T3WFl6QMaulYhzKNGwS90zdXx43JC3jYrwz/BTGVDv4Nu1VK5F+f12kzg==` |
| `dnlib` 4.4.0 | MIT | inside the instrumenter; transitive of `SharpFuzz` |
| `libfuzzer-dotnet-windows.cc` (Metalnem/libfuzzer-dotnet, commit `bd39d4e8`) | MIT, copyright 2019 Nemanja Mijailovic | vendored in `tests/fuzz/libfuzzer-dotnet/` with its `LICENSE`; built from source, test-only |
| MSVC libFuzzer runtime (`clang_rt.fuzzer_*`) | Part of the Visual Studio "C++ AddressSanitizer" component; LLVM code under Apache-2.0 with LLVM exceptions | linked into the test-only driver executable; the Visual Studio licence terms apply to the build machine; not redistributed |

`THIRD-PARTY-NOTICES.md` lists the first five under "Used only to build or test".

## Consequences
- Every C# fuzz job needs MSVC's libFuzzer. The hosted `windows-2025-vs2026` image has it (the native targets already build there).
- A tool-manifest restore needs nuget.org access in CI. The restore is not covered by the NuGet lock files; the SHA-512 check in `run-fuzz.ps1` stands in for it.
- Nightly corpora from before this change (mutational outcome-signature corpora) are valid libFuzzer seeds and are loaded as such.
- SharpFuzz changes IL, so crash reproducers are always replayed against the plain build (`--replay` or a file argument), which the harnesses already do.

## Verification
- The coverage-guided run reaches more `MikePDF.PdfWriter` lines and more libFuzzer features on all three targets than the mutational run over the same time: **shown above** (lines: PdfWriter only; features: all three).
- The planted-failure self-test finds all three failures with the new driver: `run-fuzz.ps1 -SelfTest`, run on every push by `fuzz-smoke`. (`HarnessSelfTests` in `MikePDF.Fuzz.Tests` keeps proving the same for the mutational fallback.)
