add_executable(mikepdf_shellext_tests
  client_tests.cpp
  fake_shellhost.cpp
  fuzz_seed_tests.cpp
  layout_tests.cpp
  policy_tests.cpp
  section_tests.cpp
  thumbnail_tests.cpp
  validation_tests.cpp)
target_link_libraries(mikepdf_shellext_tests PRIVATE mikepdf_shellext_core GTest::gtest_main)
# The seed corpus of the handler-reply fuzz target is replayed on every build (tests/fuzz/shell_reply_fuzz.h).
target_include_directories(mikepdf_shellext_tests PRIVATE ${CMAKE_SOURCE_DIR}/tests/fuzz)
target_compile_definitions(mikepdf_shellext_tests PRIVATE MIKEPDF_FUZZ_SEED_DIR="${CMAKE_SOURCE_DIR}/tests/fuzz/seeds")
mikepdf_harden(mikepdf_shellext_tests)
include(GoogleTest)
gtest_discover_tests(mikepdf_shellext_tests DISCOVERY_MODE PRE_TEST)

# A host that loads the real DLL the way Explorer's hosts do (LoadLibrary, DllGetClassObject, CreateInstance, Initialize with a stream) and is driven by the
# managed integration tests (tests/MikePDF.Integration.Tests/ShellHandlerTests.cs). Nothing is registered: no COM class, no registry value, no file association.
add_executable(mikepdf_shellext_testhost testhost.cpp)
target_link_libraries(mikepdf_shellext_testhost PRIVATE mikepdf_shellext_core)
mikepdf_harden(mikepdf_shellext_testhost)
add_dependencies(mikepdf_shellext_testhost mikepdf_shellext)
add_custom_command(TARGET mikepdf_shellext_testhost POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:mikepdf_shellext> $<TARGET_FILE_DIR:mikepdf_shellext_testhost>)

# CI only (.github/workflows/shell-integration.yml): the probe that drives the real shell on a hosted runner with the real package installed, and the competing classic
# thumbnail handler it registers there (only there: the probe refuses to write the registry anywhere that is not a GitHub-hosted runner). Built with the tests, never packaged.
add_executable(mikepdf_shell_probe probe/shell_probe.cpp)
target_link_libraries(mikepdf_shell_probe PRIVATE mikepdf_shellext_core)
mikepdf_harden(mikepdf_shell_probe)
add_library(mikepdf_shell_competitor SHARED probe/competitor.cpp probe/competitor.def)
mikepdf_harden(mikepdf_shell_competitor)
