# TEST ONLY: tries everything the sandbox must forbid. Not installed, not packaged, not part of any shipping target.
add_executable(mikepdf_sandbox_probe WIN32 probe.cpp)
set_target_properties(mikepdf_sandbox_probe PROPERTIES OUTPUT_NAME "MikePDF.Engine.SandboxProbe")
target_link_libraries(mikepdf_sandbox_probe PRIVATE mikepdf_runtime_policy mikepdf_log_pipe ws2_32 user32 advapi32)
mikepdf_harden(mikepdf_sandbox_probe)
target_link_options(mikepdf_sandbox_probe PRIVATE /DEPENDENTLOADFLAG:0xA00)
# An unsigned DLL whose DllMain says "ROGUE-CODE-RAN" through the probe's exported emit function: the proof that code did (not) run.
add_library(mikepdf_rogue_marker SHARED rogue_marker.cpp)
set_target_properties(mikepdf_rogue_marker PROPERTIES OUTPUT_NAME "rogue-marker")
# A copy of the (unsigned) PDFium DLL under another name: the unsigned image the signature policy must refuse.
add_custom_command(TARGET mikepdf_sandbox_probe POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:mikepdf_sandbox_probe>/rogue-copy.dll)
