# MikePDF.Protocol fuzz harness (SharpFuzz)

Feeds arbitrary bytes to `ProtocolCodec` (request and response, as whole frames and as bodies) and checks that every
accepted message re-encodes to a fixed point. `build.ps1` builds this project but does not run it. The M1 gate
(ADR-0005) is 24 hours with no crash; the release gate (master prompt 12.6) is 72 hours.

Running it needs tools that are not part of the repository:

- the .NET tool `SharpFuzz.CommandLine`, which instruments an assembly
- `libfuzzer-dotnet` (https://github.com/Metalnem/libfuzzer-dotnet), the libFuzzer driver, a native binary built with clang

```powershell
# once
dotnet tool install --global SharpFuzz.CommandLine

# build, then instrument the assembly under test (not the harness, not MessagePack)
dotnet build tests/fuzz/MikePDF.Protocol.Fuzz -c Release
$out = "tests/fuzz/MikePDF.Protocol.Fuzz/bin/Release/net10.0-windows10.0.26100.0"
sharpfuzz "$out/MikePDF.Protocol.dll"

# fuzz, seeded with the golden frames
libfuzzer-dotnet --target_path="$out/MikePDF.Protocol.Fuzz.exe" -max_len=65540 tests/MikePDF.Protocol.Tests/golden
```

`-max_len=65540` is the largest legal frame (64 KiB body plus the 4-byte prefix). Crashing inputs are written as
`crash-*` files. Replay one without a fuzzer (the harness runs the files it is given instead of fuzzing):

```powershell
dotnet run --project tests/fuzz/MikePDF.Protocol.Fuzz -c Release -- crash-0123abcd
```

The C++ decoder has its own libFuzzer target in `tests/fuzz/protocol_fuzz.cpp`.
