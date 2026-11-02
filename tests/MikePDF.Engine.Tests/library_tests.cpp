#include <gtest/gtest.h>

#include <stdexcept>

#include "mikepdf/pdfium/library.h"

TEST(PdfiumLibrary, SecondInstanceIsRejected) {
    mikepdf::pdfium::Library library;
    EXPECT_THROW(mikepdf::pdfium::Library second, std::logic_error);
}

TEST(PdfiumLibrary, CanReinitialiseAfterDestruction) {
    { mikepdf::pdfium::Library first; }
    EXPECT_NO_THROW(mikepdf::pdfium::Library second);
}
