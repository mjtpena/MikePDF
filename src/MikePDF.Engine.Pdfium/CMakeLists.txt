# Thin C++ wrapper over PDFium. The only code allowed to call PDFium (master prompt A.5).
add_library(mikepdf_pdfium STATIC
  src/library.cpp
  src/document.cpp)
target_include_directories(mikepdf_pdfium PUBLIC include)
target_link_libraries(mikepdf_pdfium PUBLIC pdfium mikepdf_protocol)
mikepdf_harden(mikepdf_pdfium)

# The version string Hello reports. PDFium has no runtime API for it, so take it from the package that was linked.
file(STRINGS "${MIKEPDF_PDFIUM_DIR}/VERSION" _pdfium_version_lines)
foreach(_line IN LISTS _pdfium_version_lines)
  if(_line MATCHES "^(MAJOR|MINOR|BUILD|PATCH)=([0-9]+)$")
    set(_pdfium_${CMAKE_MATCH_1} ${CMAKE_MATCH_2})
  endif()
endforeach()
target_compile_definitions(mikepdf_pdfium PRIVATE
  MIKEPDF_PDFIUM_VERSION="${_pdfium_MAJOR}.${_pdfium_MINOR}.${_pdfium_BUILD}.${_pdfium_PATCH}")
