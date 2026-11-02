# ADR-0017: Fuzzing toolchain for the .NET code
- Status: Proposed
- Date: 2026-11-02
- Deciders: Opus (approver), Sonnet (proposer, WP W1-fuzz)

## Context
Master prompt §12.6 and ADR-0005 rule 7 call for SharpFuzz targets on the C# protocol decoder and on `MikePDF.PdfWriter`, and libFuzzer on the C++ side. The C++ side needed nothing new: MSVC ships libFuzzer and AddressSanitizer in the "C++ AddressSanitizer" Visual Studio component, and the native targets are built from source with them (`tests/fuzz/CMakeLists.txt`).

For the .NET side, the `SharpFuzz` library (MIT) is already a central package version (ADR-0001, ADR-0005 and ADR-0008 name it). The library alone does not fuzz anything. A coverage-guided run also needs two things that no accepted ADR covers:
1. **An instrumenter.** The `SharpFuzz.CommandLine` .NET tool (MIT, built on Mono.Cecil, MIT) rewrites an assembly so that it reports edge coverage.
2. **A driver.** `libfuzzer-dotnet` (MIT) is a native libFuzzer front end for an instrumented .NET process. Its releases are prebuilt binaries on GitHub; building it needs clang. Alternatively AFL++ (Apache-2.0) drives `SharpFuzz.OutOfProcess` on Linux, but `MikePDF.PdfWriter` and `MikePDF.Protocol` target `net10.0-windows`, so they do not run there.

The work package's rules forbid adding either without an ADR, and forbid running an unvetted prebuilt binary. So the .NET targets run today under a dependency-free mutational fuzzer (`tests/fuzz/Shared/MutationalFuzzer.cs`). It has **no coverage feedback**: an input joins the corpus when it yields a decoder verdict or typed error not seen before. That finds shallow and moderately deep bugs (it found two instances of one PdfWriter bug in its first 15 minutes, and its self-tests show it finds a planted exception, hang and stack overflow), but it will plateau earlier than a coverage-guided fuzzer on the same code.

## Options considered
1. **Stay with the mutational fuzzer.** No new dependency, nothing to vet. Weaker search; the 72-hour gate would be argued on outcome diversity and line coverage of the replayed corpus, not on edge coverage.
2. **`SharpFuzz.CommandLine` as a pinned local .NET tool, plus `libfuzzer-dotnet` built from a pinned source commit with clang-cl in CI.** Coverage-guided, MIT throughout, one more tool in the build. Needs the VS "C++ Clang tools for Windows" component and a source archive with a recorded SHA-256, so no unvetted binary runs.
3. **`SharpFuzz.CommandLine` plus our own small in-process driver.** An instrumented assembly records edge hits in a shared buffer owned by the SharpFuzz runtime; if our own driver can supply and read that buffer (not yet verified, so a half-day spike comes first), `MutationalFuzzer` could use it instead of outcome signatures. Only the instrumenter is new, and no native code is built. The risk is that we maintain a coverage-guided fuzzer ourselves, and it must stay in step with SharpFuzz's internals.

## Decision
Proposed: **option 3**, then option 2 only if option 3's search proves too weak. Reasons: it adds one MIT tool (a local tool manifest entry, version and package hash pinned), runs the same on a developer machine and on a hosted runner, and keeps one fuzzer for both .NET targets. Until the ADR is accepted, option 1 stays in force, and `docs/fuzzing.md` states the limit wherever it reports C# results.

## Consequences
- Accepting option 3 means one tool-manifest entry (`dotnet-tools.json`) and a build step that instruments `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll` into a separate folder (never the shipped ones).
- The SharpFuzz instrumentation changes IL, so crash reproducers are always replayed against the plain build (`--replay`), which the harnesses already do.
- Opus checks the licence of `SharpFuzz.CommandLine` and Mono.Cecil, and the NuGet package hash, before accepting (ADR-0003).

## Verification
- The coverage-guided run reaches strictly more `MikePDF.Protocol` and `MikePDF.PdfWriter` lines than the mutational run over the same time, measured with the code-coverage extension on a corpus replay (`CorpusReplayTests`).
- The planted-failure self-tests (`HarnessSelfTests`) still find all three failures with the new driver.
