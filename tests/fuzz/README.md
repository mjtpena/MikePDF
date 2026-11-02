# Fuzz targets

How to run them, how coverage is measured, how findings are triaged and the 24-hour and 72-hour plans are in
[docs/fuzzing.md](../../docs/fuzzing.md). This folder holds the code.

| Path | What it is |
|---|---|
| `run-fuzz.ps1` | Builds and runs the targets: `./tests/fuzz/run-fuzz.ps1 -Target all -Seconds 60` (the CI smoke) |
| `protocol_fuzz.cpp` | libFuzzer target: the C++ IPC decoders (engine side), with round-trip invariants |
| `engine_document_fuzz.cpp` | libFuzzer target: the engine's document open and render path through the PDFium wrapper |
| `CMakeLists.txt` | Builds those two with MSVC `/fsanitize=fuzzer /fsanitize=address` (`-DMIKEPDF_BUILD_FUZZERS=ON`, x64 only) |
| `MikePDF.Protocol.Fuzz/` | The C# IPC decoders, broker side |
| `MikePDF.PdfWriter.Fuzz/` | `MikePDF.PdfWriter`: parse and validate (`parse`), incremental writer round trip (`append`) |
| `libfuzzer-dotnet/` | The libFuzzer driver for the SharpFuzz-instrumented C# harnesses: Metalnem's `libfuzzer-dotnet-windows.cc` vendored unchanged (MIT), with a standalone CMake project that builds it against MSVC's libFuzzer (ADR-0017) |
| `.config/dotnet-tools.json` | Local tool manifest: `SharpFuzz.CommandLine` 2.3.0, which instruments `MikePDF.Protocol.dll` and `MikePDF.PdfWriter.dll` |
| `Shared/LibFuzzerEntry.cs` | The harnesses' `--libfuzzer` child mode (what the driver starts) |
| `Shared/MutationalFuzzer.cs` | The dependency-free mutational fuzzer, the fallback for the C# harnesses (`run-fuzz.ps1 -CSharpMode mutational`; no coverage feedback) |
| `MikePDF.Fuzz.Tests/` | Tests: the harness finds a planted crash, hang and stack overflow; the mutator; every regression and seed replayed |
| `dictionaries/` | `protocol.dict` and `pdf.dict` (libFuzzer and AFL format, used by both the native and the C# fuzzers) |
| `regressions/` | Minimised reproducers of fixed findings; replayed on every test run |

Seeds are not duplicated here: the protocol golden vectors (`tests/MikePDF.Protocol.Tests/golden`) and the PDF fixtures
(`tests/MikePDF.PdfWriter.Tests/Fixtures/pdfium`, `tests/MikePDF.Integration.Tests/Fixtures`) are collected by `run-fuzz.ps1`.
