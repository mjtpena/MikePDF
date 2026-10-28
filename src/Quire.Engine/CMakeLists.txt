# Sandboxed engine host (ADR-0004, ADR-0005). Never opens paths; works on broker-provided handles.

# The runtime mitigation the engine applies to itself. Separate so the sandbox probe can use the very same code.
add_library(quire_runtime_policy STATIC runtime_policy.cpp)
target_include_directories(quire_runtime_policy PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
quire_harden(quire_runtime_policy)

# The diagnostics pipe to the broker (no handle is inherited, so there is no stderr). Shared with the sandbox probe.
add_library(quire_log_pipe STATIC log_pipe.cpp)
target_include_directories(quire_log_pipe PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
quire_harden(quire_log_pipe)

# Everything except main(), so the unit tests can drive it.
add_library(quire_engine_core STATIC
  command_line.cpp
  engine_core.cpp
  engine_host.cpp
  handle_guard.cpp
  mapped_file.cpp
  pipe_client.cpp
  slot_section.cpp)
target_include_directories(quire_engine_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(quire_engine_core PUBLIC quire_pdfium quire_protocol quire_runtime_policy quire_log_pipe)
quire_harden(quire_engine_core)

add_executable(quire_engine WIN32 main.cpp)
set_target_properties(quire_engine PROPERTIES OUTPUT_NAME "Quire.Engine")
target_link_libraries(quire_engine PRIVATE quire_engine_core)
quire_harden(quire_engine)
# Imports resolve from System32 and the application directory only (0x800 | 0x200).
target_link_options(quire_engine PRIVATE /DEPENDENTLOADFLAG:0xA00)
add_custom_command(TARGET quire_engine POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:quire_engine>)
