# Thin C++ wrapper over PDFium. The only code allowed to call PDFium (master prompt A.5).
add_library(quire_pdfium STATIC
  src/library.cpp
  src/document.cpp)
target_include_directories(quire_pdfium PUBLIC include)
target_link_libraries(quire_pdfium PUBLIC pdfium quire_protocol)
quire_harden(quire_pdfium)

# The version string Hello reports. PDFium has no runtime API for it, so take it from the package that was linked.
file(STRINGS "${QUIRE_PDFIUM_DIR}/VERSION" _pdfium_version_lines)
foreach(_line IN LISTS _pdfium_version_lines)
  if(_line MATCHES "^(MAJOR|MINOR|BUILD|PATCH)=([0-9]+)$")
    set(_pdfium_${CMAKE_MATCH_1} ${CMAKE_MATCH_2})
  endif()
endforeach()
target_compile_definitions(quire_pdfium PRIVATE
  QUIRE_PDFIUM_VERSION="${_pdfium_MAJOR}.${_pdfium_MINOR}.${_pdfium_BUILD}.${_pdfium_PATCH}")
