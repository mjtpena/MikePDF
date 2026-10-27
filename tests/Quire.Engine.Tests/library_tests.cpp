#include <gtest/gtest.h>

#include <stdexcept>

#include "quire/pdfium/library.h"

TEST(PdfiumLibrary, SecondInstanceIsRejected) {
    quire::pdfium::Library library;
    EXPECT_THROW(quire::pdfium::Library second, std::logic_error);
}

TEST(PdfiumLibrary, CanReinitialiseAfterDestruction) {
    { quire::pdfium::Library first; }
    EXPECT_NO_THROW(quire::pdfium::Library second);
}
