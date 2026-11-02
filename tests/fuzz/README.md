# C++ protocol fuzz target

`protocol_fuzz.cpp` is a libFuzzer target for the C++ decoder in `src/MikePDF.Protocol`. **It is not built** by CMake or
`build.ps1`, and it has never been run: the DEV machine has no clang, and libFuzzer needs it. It is written so that
it can be added to the build as soon as clang is available (a `MIKEPDF_BUILD_FUZZERS` CMake option, off by default).
The in-process randomised test in `tests/MikePDF.Engine.Tests/protocol_tests.cpp` (100,000 frames, fixed seed) is what
exercises the decoder today.

With clang-cl and the ASan and libFuzzer runtimes installed (from the repository root):

```powershell
clang-cl /std:c++20 /Zi /fsanitize=fuzzer,address `
  -I src/MikePDF.Protocol/cpp -I src/MikePDF.Protocol/generated/cpp `
  tests/fuzz/protocol_fuzz.cpp `
  src/MikePDF.Protocol/cpp/mikepdf/protocol/msgpack.cpp `
  src/MikePDF.Protocol/cpp/mikepdf/protocol/codec.cpp `
  src/MikePDF.Protocol/generated/cpp/mikepdf/protocol/messages.cpp `
  /Fe:build/out/protocol_fuzz.exe

# seeded with the golden frames written by the C# tests; 65540 is the largest legal frame
mkdir build/out/protocol-corpus
build/out/protocol_fuzz.exe -max_len=65540 build/out/protocol-corpus tests/MikePDF.Protocol.Tests/golden
```

The target aborts if a message the decoder accepted cannot be re-encoded, or if re-encoding is not a fixed point, so
those invariants are checked as well as memory safety. The M1 gate (ADR-0005) is 24 hours with no crash.
