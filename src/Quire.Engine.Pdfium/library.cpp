#include "quire/pdfium/library.h"

#include <atomic>
#include <stdexcept>

#include <fpdfview.h>

namespace quire::pdfium {
namespace {
std::atomic<bool> g_initialised{false};
}

Library::Library() {
    if (g_initialised.exchange(true)) {
        throw std::logic_error("PDFium library already initialised");
    }
    FPDF_LIBRARY_CONFIG config{};
    config.version = 2;  // No V8 isolate, no Skia; matches the no-V8 build (ADR-0002).
    FPDF_InitLibraryWithConfig(&config);
}

Library::~Library() {
    FPDF_DestroyLibrary();
    g_initialised = false;
}

}  // namespace quire::pdfium
