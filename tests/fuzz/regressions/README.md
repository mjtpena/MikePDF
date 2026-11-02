# Fuzz regressions

Every input here was found by a fuzzer, then minimised, and each is replayed against its target on every test run
(`tests/fuzz/MikePDF.Fuzz.Tests`, `RegressionTests`; the native ones by `tests/fuzz/regression_tests.cpp` when it exists).

| Folder | Replayed against |
|---|---|
| `protocol/` | the C# and C++ IPC decoders (`ProtocolCodec.Decode*`, `decode_*`) |
| `pdfwriter/` | `MikePDF.PdfWriter` parse and validate, and the incremental append round trip |
| `engine-document/` | the engine's document open and render path (native test only) |

Name each file `<what-it-was>.bin|pdf` and add a line below saying which finding it is, what was wrong, and which commit fixed it.
A file that crashes PDFium rather than MikePDF code does not belong here: write it up in `docs/fuzzing.md` instead (the
sandbox, not a regression test, is what contains those).

## Findings fixed

None yet.
