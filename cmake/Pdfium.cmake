# Imported target for the PDFium build pinned in build/pdfium.lock.
# MIKEPDF_PDFIUM_DIR is set by build.ps1 (output of build/fetch-pdfium.ps1).
if(NOT MIKEPDF_PDFIUM_DIR)
  message(FATAL_ERROR "MIKEPDF_PDFIUM_DIR not set; run build.ps1 or build/fetch-pdfium.ps1")
endif()
add_library(pdfium SHARED IMPORTED GLOBAL)
set_target_properties(pdfium PROPERTIES
  IMPORTED_LOCATION "${MIKEPDF_PDFIUM_DIR}/bin/pdfium.dll"
  IMPORTED_IMPLIB "${MIKEPDF_PDFIUM_DIR}/lib/pdfium.dll.lib"
  INTERFACE_INCLUDE_DIRECTORIES "${MIKEPDF_PDFIUM_DIR}/include")
