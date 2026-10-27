# Imported target for the PDFium build pinned in build/pdfium.lock.
# QUIRE_PDFIUM_DIR is set by build.ps1 (output of build/fetch-pdfium.ps1).
if(NOT QUIRE_PDFIUM_DIR)
  message(FATAL_ERROR "QUIRE_PDFIUM_DIR not set; run build.ps1 or build/fetch-pdfium.ps1")
endif()
add_library(pdfium SHARED IMPORTED GLOBAL)
set_target_properties(pdfium PROPERTIES
  IMPORTED_LOCATION "${QUIRE_PDFIUM_DIR}/bin/pdfium.dll"
  IMPORTED_IMPLIB "${QUIRE_PDFIUM_DIR}/lib/pdfium.dll.lib"
  INTERFACE_INCLUDE_DIRECTORIES "${QUIRE_PDFIUM_DIR}/include")
