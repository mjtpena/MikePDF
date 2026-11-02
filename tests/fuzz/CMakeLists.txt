# Native libFuzzer targets (master prompt 12.6, ADR-0005 rule 7). Built only with -DMIKEPDF_BUILD_FUZZERS=ON.
#
# MSVC's libFuzzer and AddressSanitizer ship with the "C++ AddressSanitizer" Visual Studio component. They are x64 only, need the
# DLL runtime (/MD) and a Release-style configuration (no /RTC), so these targets are built from source with their own flags
# instead of linking the hardened static libraries the product uses: coverage instrumentation has to be compiled into every
# translation unit whose code the fuzzer should be steered by.
#
#   ./tests/fuzz/build-native.ps1        configures, builds and prints where the executables are
if(NOT MSVC OR CMAKE_GENERATOR_PLATFORM MATCHES "ARM64")
  message(FATAL_ERROR "MIKEPDF_BUILD_FUZZERS needs MSVC on x64 (the libFuzzer and ASan runtimes ship for x64 only)")
endif()

# The ASan runtime DLL that the instrumented executables load. It lives next to cl.exe; copy it beside each target so the
# executables run from a plain shell.
get_filename_component(_fuzz_cl_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
set(_fuzz_asan_dll "${_fuzz_cl_dir}/clang_rt.asan_dynamic-x86_64.dll")
if(NOT EXISTS "${_fuzz_asan_dll}")
  message(FATAL_ERROR "clang_rt.asan_dynamic-x86_64.dll not found next to cl.exe: install the 'C++ AddressSanitizer' Visual Studio component")
endif()

function(mikepdf_fuzz_target name)
  add_executable(${name} ${ARGN})
  target_compile_features(${name} PRIVATE cxx_std_20)
  set_target_properties(${name} PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreadedDLL"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/fuzz")
  target_compile_options(${name} PRIVATE
    /W4 /WX /permissive- /utf-8 /Zc:__cplusplus /Zi
    /fsanitize=address /fsanitize=fuzzer)
  target_compile_definitions(${name} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
  target_link_options(${name} PRIVATE /DEBUG /INCREMENTAL:NO)
  add_custom_command(TARGET ${name} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_fuzz_asan_dll}" $<TARGET_FILE_DIR:${name}>)
endfunction()

# The protocol sources are compiled into each target (not linked as a library) so that they carry the coverage counters.
set(MIKEPDF_FUZZ_PROTOCOL_SOURCES
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Protocol/cpp/mikepdf/protocol/msgpack.cpp
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Protocol/cpp/mikepdf/protocol/codec.cpp
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Protocol/generated/cpp/mikepdf/protocol/messages.cpp)
set(MIKEPDF_FUZZ_PROTOCOL_INCLUDES
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Protocol/cpp
  ${MIKEPDF_PROTOCOL_CPP_DIR})

# The C++ IPC decoders, engine side: every request and response decoder on arbitrary bytes, plus the encode round-trip invariants.
mikepdf_fuzz_target(fuzz_protocol protocol_fuzz.cpp ${MIKEPDF_FUZZ_PROTOCOL_SOURCES})
target_include_directories(fuzz_protocol PRIVATE ${MIKEPDF_FUZZ_PROTOCOL_INCLUDES})

# The engine's document-open and render path, through the PDFium wrapper. PDFium itself stays the uninstrumented prebuilt DLL.
mikepdf_fuzz_target(fuzz_engine_document engine_document_fuzz.cpp
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Engine.Pdfium/src/document.cpp
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Engine.Pdfium/src/library.cpp
  ${MIKEPDF_FUZZ_PROTOCOL_SOURCES})
target_include_directories(fuzz_engine_document PRIVATE
  ${CMAKE_SOURCE_DIR}/src/MikePDF.Engine.Pdfium/include
  ${MIKEPDF_FUZZ_PROTOCOL_INCLUDES})
target_compile_definitions(fuzz_engine_document PRIVATE MIKEPDF_PDFIUM_VERSION="fuzz")
target_link_libraries(fuzz_engine_document PRIVATE pdfium)
add_custom_command(TARGET fuzz_engine_document POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:fuzz_engine_document>)
