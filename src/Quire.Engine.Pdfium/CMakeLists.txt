# Thin C++ wrapper over PDFium. The only code allowed to call PDFium (master prompt A.5).
add_library(quire_pdfium STATIC
  library.cpp)
target_include_directories(quire_pdfium PUBLIC include)
target_link_libraries(quire_pdfium PUBLIC pdfium)
quire_harden(quire_pdfium)
