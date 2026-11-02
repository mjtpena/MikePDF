# The Explorer thumbnail provider and preview handler (ADR-0018, docs/features/shell-extensions.md). A COM DLL that runs inside dllhost.exe and prevhost.exe,
# unsandboxed, and therefore parses nothing: it hands the stream to MikePDF.ShellHost over a pipe and validates what comes back.

# Everything except the DLL's entry points, so the unit tests and the test host can drive it with their own environments.
add_library(mikepdf_shellext_core STATIC
  bitmap.cpp
  class_factory.cpp
  page_layout.cpp
  pipe_channel.cpp
  policy_gate.cpp
  preview_handler.cpp
  preview_uia.cpp
  section.cpp
  shell_client.cpp
  thumbnail_provider.cpp
  validation.cpp)
target_include_directories(mikepdf_shellext_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(mikepdf_shellext_core PUBLIC mikepdf_protocol ole32 oleaut32 shlwapi shell32 gdi32 user32 advapi32 uuid uiautomationcore)
mikepdf_harden(mikepdf_shellext_core)

add_library(mikepdf_shellext SHARED dllmain.cpp MikePDF.ShellExt.def MikePDF.ShellExt.rc)
set_target_properties(mikepdf_shellext PROPERTIES OUTPUT_NAME "MikePDF.ShellExt")
target_link_libraries(mikepdf_shellext PRIVATE mikepdf_shellext_core)
mikepdf_harden(mikepdf_shellext)
# Imports resolve from System32 and the application directory only (0x800 | 0x200).
target_link_options(mikepdf_shellext PRIVATE /DEPENDENTLOADFLAG:0xA00)
