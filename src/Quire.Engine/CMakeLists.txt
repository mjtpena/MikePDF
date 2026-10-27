# Sandboxed engine host executable (ADR-0004). Never opens paths; works on broker-provided handles.
add_executable(quire_engine WIN32
  main.cpp)
set_target_properties(quire_engine PROPERTIES OUTPUT_NAME "Quire.Engine")
target_link_libraries(quire_engine PRIVATE quire_pdfium)
quire_harden(quire_engine)
add_custom_command(TARGET quire_engine POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:pdfium> $<TARGET_FILE_DIR:quire_engine>)
