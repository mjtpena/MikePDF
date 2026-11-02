# MikePDF.Protocol fuzz harness (SharpFuzz and libFuzzer)

Feeds arbitrary bytes to `ProtocolCodec` (request and response, as whole frames and as bodies) and checks that every
accepted message re-encodes to a fixed point. `build.ps1` builds this project but does not run it. The M1 gate
(ADR-0005) is 24 hours with no crash; the release gate (master prompt 12.6) is 72 hours.

`./tests/fuzz/run-fuzz.ps1 -Target protocol-cs -Seconds 900` builds the harness, builds the vendored libFuzzer driver
(`tests/fuzz/libfuzzer-dotnet`, MIT, needs the "C++ AddressSanitizer" Visual Studio component), restores the pinned SharpFuzz.CommandLine
local tool (`tests/fuzz/.config/dotnet-tools.json`), instruments `MikePDF.Protocol.dll` into a separate `-cov` folder and runs libFuzzer
on it, coverage-guided (ADR-0017). By hand:

```powershell
dotnet build tests/fuzz/MikePDF.Protocol.Fuzz -c Release -o build/out/fuzz-bin/protocol
Copy-Item build/out/fuzz-bin/protocol build/out/fuzz-bin/protocol-cov -Recurse
cd tests/fuzz; dotnet tool restore; dotnet sharpfuzz ../../build/out/fuzz-bin/protocol-cov/MikePDF.Protocol.dll; cd ../..   # the assembly under test only

build/out/fuzz-driver/driver/Release/libfuzzer_dotnet.exe `
    '"--target_path=\"build/out/fuzz-bin/protocol-cov/MikePDF.Protocol.Fuzz.exe\" --libfuzzer"' `
    -max_len=65540 -timeout=20 -dict=tests/fuzz/dictionaries/protocol.dict -artifact_prefix=build/out/fuzz-findings/ build/out/fuzz-corpus tests/MikePDF.Protocol.Tests/golden
```

The mutational fuzzer (`MikePDF.Protocol.Fuzz.exe --mutate 900 ...`, `run-fuzz.ps1 -CSharpMode mutational`) is the fallback: no coverage
feedback, no C++ toolchain.

`-max_len=65540` is the largest legal frame (64 KiB body plus the 4-byte prefix). Crashing inputs are written as
`crash-*` files. Replay one against the plain build, without a fuzzer (the harness runs the files it is given instead of fuzzing):

```powershell
dotnet run --project tests/fuzz/MikePDF.Protocol.Fuzz -c Release -- crash-0123abcd
```

The C++ decoder has its own libFuzzer target in `tests/fuzz/protocol_fuzz.cpp`.
