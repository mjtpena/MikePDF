# MikePDF.PdfWriter fuzz harness

Coverage-guided (SharpFuzz and libFuzzer) fuzz targets for `MikePDF.PdfWriter`, the parser that reads untrusted PDF bytes inside the sandboxed `MikePDF.WriterHost`
(ADR-0008). A fuzz-found parser differential between PDFium and `MikePDF.PdfWriter` counts as a High security bug (ADR-0008,
risk R-31).

| Target | What it does | What counts as a finding |
|---|---|---|
| `parse` | strict open, lenient open, `StructureValidator`, object and stream access on arbitrary bytes | any exception that escapes, a hang, a limit exceeded, strict-accepts-but-lenient-refuses, a refused file reported valid |
| `append` | strict open, then an incremental update that adds an object and re-writes up to 3 parsed objects, then re-opens the output | any exception, the writer's own output self-check failing on a strictly valid base, the input not being an exact prefix of the output |

Three more targets belong to the save pipeline (save-pipeline 3.3, 3.4 and 11.3), in `SaveFuzzTargets.cs`; they need the `MikePDF.Core` and `MikePDF.Protocol` references this project now has:

| Target | What it does | What counts as a finding |
|---|---|---|
| `edit` | strict open, the page tree walk, `AnnotationAppender` with an add, an add on the last page and an update and a delete aimed at what the file really has, and `DocumentRewriter` with each flatten flag | any exception, an output that does not reopen strictly or does not keep the input as a prefix, a rewrite that changes the page count |
| `assemble` | the page assembler: the input as a source next to a built-in partner document (outline, names, labels, a form, links), four plans (first pages; reversed with a duplicate; a merge with outline wrappers; every other page), each output reopened and validated, and the first plan twice | any exception, a file the strict parser refuses that is taken (a refusal for a parser reason or a limit is what it must get), an output that does not reopen strictly, fails the validator (a dangling reference counts), or has the wrong page count, `OutputInvalid` or any refusal of a plan that was valid, two different outputs for one plan |
| `journal` | `JournalCodec.Parse` on arbitrary bytes, then the round trip of what it returned and the recovery code that consumes it (`RecoveryService.Summarise`, `SaveRecovery.IsPipelineFile`) | any exception, a hang, sequence numbers that are not 1, 2, 3, a record that does not survive being written and read again, a recorded path accepted from outside the target's folder |
| `editblob` | `EditBlobCodec.TryDecode` on arbitrary bytes; an accepted blob must re-encode to the same bytes and is then applied to a built-in two page document by the appender | any exception, a blob that is not a fixed point, an output that does not reopen strictly with the input as a prefix |
| `tail` | `TailNormalizer.Normalize` (NormalizeTail, schema 7) with the input as the engine's save and its first `%%EOF` as the end of the original, then with the first half as the original | any exception; an output that does not keep the original as its prefix or does not open strictly |

`MikePDF.PdfWriter.Fuzz <journal|editblob|tail> --seeds-out <dir>` writes the seeds (committed under `tests/fuzz/seeds/journal`, `tests/fuzz/seeds/editblob` and `tests/fuzz/seeds/tail`). The `edit` and `assemble` targets take the PDF seeds (the assembler fixtures A, B and C among them).

Both of the first two targets live in `FuzzTargets.cs`. They catch nothing: an escaped exception **is** the crash. The same checks run as a fixed-seed
mutation pass in `tests/MikePDF.PdfWriter.Tests/MutationTests.cs` on every test run.

This project is **not** in `MikePDF.slnx` yet (another change is editing the solution). To add it:
`<Project Path="tests/fuzz/MikePDF.PdfWriter.Fuzz/MikePDF.PdfWriter.Fuzz.csproj" />` under `/tests/`.

## Build

```powershell
dotnet build tests/fuzz/MikePDF.PdfWriter.Fuzz/MikePDF.PdfWriter.Fuzz.csproj -c Release
```

## Reproduce a crash (no fuzzer needed)

```powershell
dotnet run --project tests/fuzz/MikePDF.PdfWriter.Fuzz -c Release -- parse --replay path\to\crash-input
dotnet run --project tests/fuzz/MikePDF.PdfWriter.Fuzz -c Release -- append --replay path\to\crash-input
```

Prints `ok` and exits 0 when the input is handled; an unhandled exception otherwise.

## Fuzz with libFuzzer and SharpFuzz (Windows x64, coverage-guided; ADR-0017)

`./tests/fuzz/run-fuzz.ps1 -Target pdfwriter-parse -Seconds 900` does all of the following: builds the harness, builds the vendored
libFuzzer driver (`tests/fuzz/libfuzzer-dotnet`, needs the "C++ AddressSanitizer" Visual Studio component), restores the pinned
SharpFuzz.CommandLine local tool, instruments `MikePDF.PdfWriter.dll` into a separate `-cov` folder, and runs libFuzzer. By hand:

```powershell
dotnet build tests/fuzz/MikePDF.PdfWriter.Fuzz -c Release -o build/out/fuzz-bin/pdfwriter
Copy-Item build/out/fuzz-bin/pdfwriter build/out/fuzz-bin/pdfwriter-cov -Recurse
cd tests/fuzz; dotnet tool restore; dotnet sharpfuzz ../../build/out/fuzz-bin/pdfwriter-cov/MikePDF.PdfWriter.dll; cd ../..   # instrument the assembly under test only

build/out/fuzz-driver/driver/Release/libfuzzer_dotnet.exe `
    '"--target_path=\"build/out/fuzz-bin/pdfwriter-cov/MikePDF.PdfWriter.Fuzz.exe\" parse --libfuzzer"' `
    -max_len=131072 -timeout=20 -dict=tests/fuzz/dictionaries/pdf.dict -artifact_prefix=build/out/fuzz-findings/ build/out/fuzz-corpus
```

Use `append` for the second target. A crash lands as `crash-*` (`timeout-*` for a hang), and the managed stack is in the log. Reproduce it against the
**plain** build with `--replay`, never against the instrumented one.

## The mutational fallback

`MikePDF.PdfWriter.Fuzz.exe parse --mutate 900 --corpus c --seeds s --dict d --findings f` runs the dependency-free mutational fuzzer
(`../Shared/MutationalFuzzer.cs`). It has no coverage feedback and needs no C++ toolchain: `run-fuzz.ps1 -CSharpMode mutational` uses it.
`--minimize <file>` shrinks a crashing input for either mode (writes `<file>.min`).

## Notes

* Instrument `MikePDF.PdfWriter.dll` only (not the harness, not a shipped copy). The limits in `FuzzTargets.Limits` are deliberately small so runs stay fast and the limit
  paths are reached.
* Keep any crash input and add it to the mutation test's seeds (or a regression test) when fixing the bug.
* The corpus (`Fixtures/pdfium/*.pdf`) is BSD-3-Clause PDFium test data; findings may contain parts of it. Do not publish raw findings.
