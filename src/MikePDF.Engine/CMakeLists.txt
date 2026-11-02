# Sandboxed engine host (ADR-0004, ADR-0005). Never opens paths; works on broker-provided handles.

# The runtime mitigation the engine applies to itself. Separate so the sandbox probe can use the very same code.
add_library(mikepdf_runtime_policy STATIC runtime_policy.cpp)
target_include_directories(mikepdf_runtime_policy PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
mikepdf_harden(mikepdf_runtime_policy)

# The diagnostics pipe to the broker (no handle is inherited, so there is no stderr). Shared with the sandbox probe.
add_library(mikepdf_log_pipe STATIC log_pipe.cpp)
target_include_directories(mikepdf_log_pipe PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
mikepdf_harden(mikepdf_log_pipe)

# Everything except main(), so the unit tests can drive it.
add_library(mikepdf_engine_core STATIC
  command_line.cpp
  engine_core.cpp
  engine_host.cpp
  handle_guard.cpp
  mapped_file.cpp
  pipe_client.cpp
  slot_section.cpp)
target_include_directories(mikepdf_engine_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(mikepdf_engine_core PUBLIC mikepdf_pdfium mikepdf_protocol mikepdf_runtime_policy mikepdf_log_pipe)
mikepdf_harden(mikepdf_engine_core)

add_executable(mikepdf_engine WIN32 main.cpp)
set_target_properties(mikepdf_engine PROPERTIES OUTPUT_NAME "MikePDF.Engine")
target_link_libraries(mikepdf_engine PRIVATE mikepdf_engine_core)
mikepdf_harden(mikepdf_engine)
# Imports resolve from System32 and the application directory only (0x800 | 0x200).
target_link_options(mikepdf_engine PRIVATE /DEPENDENTLOADFLAG:0xA00)
add_custom_command(TARGET mikepdf_engine POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:mikepdf_engine>)
