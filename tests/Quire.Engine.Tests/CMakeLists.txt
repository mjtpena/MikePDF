add_executable(quire_engine_tests
  document_tests.cpp
  engine_core_tests.cpp
  library_tests.cpp
  protocol_tests.cpp)
target_link_libraries(quire_engine_tests PRIVATE quire_engine_core GTest::gtest_main)
# Golden frames written by the C# tests; the C++ decoder must accept and reject exactly the same ones.
target_compile_definitions(quire_engine_tests PRIVATE
  QUIRE_GOLDEN_DIR="${CMAKE_SOURCE_DIR}/tests/Quire.Protocol.Tests/golden"
  QUIRE_FIXTURE_DIR="${CMAKE_SOURCE_DIR}/tests/Quire.Integration.Tests/Fixtures")
quire_harden(quire_engine_tests)
add_custom_command(TARGET quire_engine_tests POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:quire_engine_tests>)
include(GoogleTest)
gtest_discover_tests(quire_engine_tests DISCOVERY_MODE PRE_TEST)
