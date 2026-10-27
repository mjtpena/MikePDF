cmake_minimum_required(VERSION 3.28)
project(Quire LANGUAGES CXX)

# Native half of Quire: the sandboxed engine host and its tests (ADR-0002, ADR-0004).
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

list(APPEND CMAKE_MODULE_PATH "${CMAKE_SOURCE_DIR}/cmake")
include(QuireHardening)
include(Pdfium)

option(QUIRE_BUILD_TESTS "Build native tests" ON)

# Generated protocol code (tools/Quire.ProtocolGen) lands here.
set(QUIRE_PROTOCOL_CPP_DIR "${CMAKE_SOURCE_DIR}/src/Quire.Protocol/generated/cpp")

add_subdirectory(src/Quire.Protocol)
add_subdirectory(src/Quire.Engine.Pdfium)
add_subdirectory(src/Quire.Engine)

if(QUIRE_BUILD_TESTS)
  include(FetchContent)
  FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.18.0.tar.gz
    URL_HASH SHA256=6e3191c1455468b3fc35a417fb565c1c5071aee1b7e7f85e30cf48a98d37d8b5
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  set(gtest_force_shared_crt OFF CACHE BOOL "" FORCE)
  set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
  FetchContent_MakeAvailable(googletest)
  enable_testing()
  add_subdirectory(tests/Quire.Engine.Tests)
endif()
