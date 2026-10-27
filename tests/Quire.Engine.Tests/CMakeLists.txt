add_executable(quire_engine_tests
  library_tests.cpp)
target_link_libraries(quire_engine_tests PRIVATE quire_pdfium GTest::gtest_main)
quire_harden(quire_engine_tests)
add_custom_command(TARGET quire_engine_tests POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:quire_engine_tests>)
include(GoogleTest)
gtest_discover_tests(quire_engine_tests DISCOVERY_MODE PRE_TEST)
