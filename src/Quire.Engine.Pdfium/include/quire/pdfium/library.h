#pragma once

#include <string_view>

namespace quire::pdfium {

// Owns PDFium's process-wide initialisation. Exactly one instance may exist,
// and it must live on the engine thread: PDFium is not thread-safe (§5.2 rule 1).
class Library {
public:
    Library();
    ~Library();

    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
};

// Version of the PDFium package this binary was built against (build/pdfium.lock), e.g. "158.0.8125.0".
[[nodiscard]] std::string_view library_version() noexcept;

}  // namespace quire::pdfium
