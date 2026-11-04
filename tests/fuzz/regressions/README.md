# Fuzz regressions

Every input here was found by a fuzzer, then minimised, and each is replayed against its target on every test run
(`tests/fuzz/MikePDF.Fuzz.Tests`, `RegressionTests`; the native ones by `tests/fuzz/regression_tests.cpp` when it exists).

| Folder | Replayed against |
|---|---|
| `protocol/` | the C# and C++ IPC decoders (`ProtocolCodec.Decode*`, `decode_*`) |
| `pdfwriter/` | `MikePDF.PdfWriter` parse and validate, the incremental append round trip, and the page assembler (`pdfwriter-assemble`) |
| `engine-document/` | the engine's document open and render path (native test only) |

Name each file `<what-it-was>.bin|pdf` and add a line below saying which finding it is, what was wrong, and which commit fixed it.
A file that crashes PDFium rather than MikePDF code does not belong here: write it up in `docs/fuzzing.md` instead (the
sandbox, not a regression test, is what contains those).

## Findings fixed

| File | Finding | Fixed in |
|---|---|---|
| `pdfwriter/assemble-duplicate-source-status.pdf` | `pdfwriter-assemble`, CI fuzz-smoke run 41751360466, 4,580 executions: the invariant "a source went missing from the status list" fired. Reading the catalog for the new `ScriptsDropped` warning (`SourceDocument.DocumentScriptKinds`) threw `AssemblyAbort` for a `/Names` entry the strict parser refuses, after `AssemblySourceSet.Create` had already added the source's usable status; the `catch` then added a second, refusal status for the same source. Not a crash of the assembler: a list that no longer matched the sources. The fix decides the status and the warnings from facts read beforehand, so nothing that can throw sits between the two; the job's reads of the catalog's `/Names`, `/PageLabels` and `/Outlines` no longer refuse the assembly but warn | the commit after `daa9402` on `m4/page-assembler` |
