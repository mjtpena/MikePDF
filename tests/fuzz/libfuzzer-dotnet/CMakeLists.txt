# The libFuzzer driver for SharpFuzz-instrumented .NET code (ADR-0017). Standalone on purpose: it needs MSVC's libFuzzer and nothing
# else, so the C# fuzz targets can be built without fetching PDFium.
#
# libfuzzer-dotnet-windows.cc is vendored unchanged from https://github.com/Metalnem/libfuzzer-dotnet (MIT, commit
# bd39d4e88d715ab460a929943645be2a186cde52, see LICENSE next to it). It starts the instrumented .NET harness as a child process
# and passes each libFuzzer input to it through shared memory and two pipes. The harness fills a 64 KiB edge-hit map in the same
# shared memory, which this driver copies into libFuzzer's "extra counters" after every input. libFuzzer then steers by it.
#
#   cmake -S tests/fuzz/libfuzzer-dotnet -B build/out/fuzz-driver -A x64
#   cmake --build build/out/fuzz-driver --config Release
cmake_minimum_required(VERSION 3.25)
project(mikepdf_libfuzzer_dotnet CXX)

if(NOT MSVC OR CMAKE_GENERATOR_PLATFORM MATCHES "ARM64")
  message(FATAL_ERROR "the libFuzzer driver needs MSVC on x64 (MSVC's libFuzzer runtime ships for x64 only)")
endif()

add_executable(libfuzzer_dotnet libfuzzer-dotnet-windows.cc)
set_target_properties(libfuzzer_dotnet PROPERTIES
  MSVC_RUNTIME_LIBRARY "MultiThreadedDLL"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/driver")
# No /WX and a relaxed warning level: the file is third-party and unchanged.
target_compile_options(libfuzzer_dotnet PRIVATE /W3 /utf-8 /Zi /fsanitize=fuzzer)
target_compile_definitions(libfuzzer_dotnet PRIVATE _CRT_SECURE_NO_WARNINGS)
target_link_options(libfuzzer_dotnet PRIVATE /DEBUG /INCREMENTAL:NO)
