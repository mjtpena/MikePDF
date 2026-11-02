add_executable(mikepdf_engine_tests
  document_tests.cpp
  reading_tests.cpp
  reader_gaps_tests.cpp
  schema5_tests.cpp
  forms_tests.cpp
  tile_consistency_tests.cpp
  dark_page_tests.cpp
  engine_core_tests.cpp
  library_tests.cpp
  protocol_tests.cpp)
target_link_libraries(mikepdf_engine_tests PRIVATE mikepdf_engine_core GTest::gtest_main)
# Golden frames written by the C# tests; the C++ decoder must accept and reject exactly the same ones.
target_compile_definitions(mikepdf_engine_tests PRIVATE
  MIKEPDF_GOLDEN_DIR="${CMAKE_SOURCE_DIR}/tests/MikePDF.Protocol.Tests/golden"
  MIKEPDF_FIXTURE_DIR="${CMAKE_SOURCE_DIR}/tests/MikePDF.Integration.Tests/Fixtures")
mikepdf_harden(mikepdf_engine_tests)
add_custom_command(TARGET mikepdf_engine_tests POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:mikepdf_engine_tests>)
include(GoogleTest)
gtest_discover_tests(mikepdf_engine_tests DISCOVERY_MODE PRE_TEST)
