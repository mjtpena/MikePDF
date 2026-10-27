# Quire.PdfWriter fuzz harness

SharpFuzz targets for `Quire.PdfWriter`, the parser that reads untrusted PDF bytes inside the sandboxed `Quire.WriterHost`
(ADR-0008). A fuzz-found parser differential between PDFium and `Quire.PdfWriter` counts as a High security bug (ADR-0008,
risk R-31).

| Target | What it does | What counts as a finding |
|---|---|---|
| `parse` | strict open, lenient open, `StructureValidator`, object and stream access on arbitrary bytes | any exception that escapes, a hang, a limit exceeded, strict-accepts-but-lenient-refuses, a refused file reported valid |
| `append` | strict open, then an incremental update that adds an object and re-writes up to 3 parsed objects, then re-opens the output | any exception, the writer's own output self-check failing on a strictly valid base, the input not being an exact prefix of the output |

Both targets live in `FuzzTargets.cs`. They catch nothing: an escaped exception **is** the crash. The same checks run as a fixed-seed
mutation pass in `tests/Quire.PdfWriter.Tests/MutationTests.cs` on every test run.

This project is **not** in `Quire.slnx` yet (another change is editing the solution). To add it:
`<Project Path="tests/fuzz/Quire.PdfWriter.Fuzz/Quire.PdfWriter.Fuzz.csproj" />` under `/tests/`.

## Build

```powershell
dotnet build tests/fuzz/Quire.PdfWriter.Fuzz/Quire.PdfWriter.Fuzz.csproj -c Release
```

## Reproduce a crash (no fuzzer needed)

```powershell
dotnet run --project tests/fuzz/Quire.PdfWriter.Fuzz -c Release -- parse --replay path\to\crash-input
dotnet run --project tests/fuzz/Quire.PdfWriter.Fuzz -c Release -- append --replay path\to\crash-input
```

Prints `ok` and exits 0 when the input is handled; an unhandled exception otherwise.

## Fuzz with libFuzzer (Windows x64)

One-time: `dotnet tool install --global SharpFuzz.CommandLine`, and download `libfuzzer-dotnet-windows.exe` from
<https://github.com/Metalnem/libfuzzer-dotnet/releases> (needs the LLVM libFuzzer runtime that the release bundles).

```powershell
dotnet publish tests/fuzz/Quire.PdfWriter.Fuzz -c Release -o build/out/fuzz
sharpfuzz build/out/fuzz/Quire.PdfWriter.dll          # instrument the assembly under test (not the harness)

New-Item -ItemType Directory -Force build/out/fuzz-corpus, build/out/fuzz-findings | Out-Null
Copy-Item tests/Quire.PdfWriter.Tests/Fixtures/pdfium/*.pdf build/out/fuzz-corpus/   # seed corpus

libfuzzer-dotnet-windows.exe --target_path=build/out/fuzz/Quire.PdfWriter.Fuzz.exe --target_arg=parse-libfuzzer `
    -max_len=65536 -timeout=10 -artifact_prefix=build/out/fuzz-findings/ build/out/fuzz-corpus
# second target:
libfuzzer-dotnet-windows.exe --target_path=build/out/fuzz/Quire.PdfWriter.Fuzz.exe --target_arg=append-libfuzzer `
    -max_len=65536 -timeout=10 -artifact_prefix=build/out/fuzz-findings/ build/out/fuzz-corpus
```

## Fuzz with AFL++ (Linux or WSL)

`afl-fuzz -i corpus -o findings -t 5000 -- dotnet build/out/fuzz/Quire.PdfWriter.Fuzz.dll parse` (add `-m none`; .NET maps a lot of
address space). Run `sharpfuzz` on `Quire.PdfWriter.dll` first, as above.

## Notes

* Instrument `Quire.PdfWriter.dll` only. The limits in `FuzzTargets.Limits` are deliberately small so runs stay fast and the limit
  paths are reached.
* Keep any crash input and add it to the mutation test's seeds (or a regression test) when fixing the bug.
* The corpus (`Fixtures/pdfium/*.pdf`) is BSD-3-Clause PDFium test data; findings may contain parts of it. Do not publish raw findings.
